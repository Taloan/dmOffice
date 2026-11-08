using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DmOffice.Core;

namespace DmOffice.Chat;

/// <summary>
/// Pukkis Morgenüberblick (Version 2), Zusatz zu Terminen und Aufgaben: Wetter für den eingestellten Ort (Open-Meteo, ohne
/// Anmeldung) und die wichtigsten Schlagzeilen aus RSS-Feeds - ohne KI, nur Überschrift und Link. Eigene Einstellungen
/// ("pukki.briefing"), nicht in den Chat-Einstellungen: ältere Apps speichern diese als Ganzes und würden neue Felder löschen.
/// </summary>
public static class PukkiBriefing
{
    public const string Key = "pukki.briefing";

    public sealed class Settings
    {
        public bool News { get; set; }
        /// <summary>Kennungen der Voreinstellungen (<see cref="Presets"/>) oder eigene Feed-Adressen (http/https).</summary>
        public List<string> Sources { get; set; } = ["tagesschau"];
        public int Count { get; set; } = 5;
        /// <summary>Stichwörter (mit Komma getrennt): Schlagzeilen damit kommen zuerst bzw. fallen weg.</summary>
        public string? Prefer { get; set; }
        public string? Exclude { get; set; }
        public bool Weather { get; set; }
        public string? Place { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }
    }

    public sealed record Preset(string Id, string Name, string Url);

    /// <summary>Voreinstellungen - alle geprüft, liefern RSS bzw. Atom.</summary>
    public static readonly Preset[] Presets =
    [
        new("tagesschau", "tagesschau", "https://www.tagesschau.de/index~rss2.xml"),
        new("spiegel", "Spiegel", "https://www.spiegel.de/schlagzeilen/index.rss"),
        new("zeit", "Zeit", "https://newsfeed.zeit.de/index"),
        new("welt", "Welt", "https://www.welt.de/feeds/latest.rss"),
        new("ntv", "n-tv", "https://www.n-tv.de/rss"),
        new("heise", "heise", "https://www.heise.de/rss/heise-atom.xml"),
        new("google", "Google News", "https://news.google.com/rss?hl=de&gl=DE&ceid=DE:de"),
    ];

    public static Settings Of(UserDb db) => db.GetJson<Settings>(Key);

    public static void Save(UserDb db, Settings s)
    {
        s.Count = Math.Clamp(s.Count, 1, 15);
        s.Sources = s.Sources.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().Take(10).ToList();
        foreach (var x in s.Sources)
            if (Presets.All(p => p.Id != x) && !(Uri.TryCreate(x, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
                throw new ArgumentException($"„{x}“ ist keine Feed-Adresse (http:// oder https://).");
        if (s.Weather && (s.Lat == null || s.Lon == null)) throw new ArgumentException("Für das Wetter erst einen Ort wählen.");
        db.SetJson(Key, s);
    }

    public sealed record Headline(string Title, string Link, DateTime? At, string Source);

    private static readonly ConcurrentDictionary<string, (DateTime At, List<Headline> Items)> Cache = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Feeds frei eingetragener Adressen: nur öffentliche Ziele (kein Heimnetz, nicht der Server selbst, keine Dienste nebenan) -
    /// geprüft bei jeder Verbindung, auch nach Weiterleitungen und gegen DNS-Rebinding; höchstens 2 MB, 20 s.
    /// </summary>
    private static readonly HttpClient Feeds = new(new System.Net.Http.SocketsHttpHandler
    {
        AllowAutoRedirect = true, MaxAutomaticRedirections = 3, AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addrs.FirstOrDefault(a => !DmOffice.Storage.ExternalFolders.Private(a)) ?? throw new HttpRequestException("Diese Adresse ist nicht erlaubt.");
            var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct); return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    }) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };

    /// <summary>Ein Feed (RSS oder Atom), 20 Minuten zwischengespeichert. Leere Liste, wenn er nicht geht.</summary>
    public static async Task<List<Headline>> FeedAsync(HttpClient _, string url, string name, CancellationToken ct)
    {
        var http = Feeds;
        if (Cache.TryGetValue(url, out var c) && DateTime.UtcNow - c.At < CacheFor) return c.Items;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("dmOffice/2.0 (Pukki-Morgenueberblick)");
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return c.Items ?? [];
            var items = Parse(await res.Content.ReadAsStringAsync(ct), name);
            Cache[url] = (DateTime.UtcNow, items);
            return items;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Xml.XmlException or InvalidOperationException) { return c.Items ?? []; }
    }

    /// <summary>Überschriften und Links aus RSS (item) oder Atom (entry).</summary>
    internal static List<Headline> Parse(string xml, string source)
    {
        var doc = XDocument.Parse(xml);
        string? Text(XElement e, string local) => e.Elements().FirstOrDefault(x => x.Name.LocalName == local)?.Value;
        DateTime? When(string? s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d
            : DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o) ? o.UtcDateTime : null;
        var list = new List<Headline>();
        foreach (var e in doc.Descendants().Where(x => x.Name.LocalName is "item" or "entry"))
        {
            var title = Clean(Text(e, "title"));
            var link = e.Name.LocalName == "item" ? Text(e, "link")?.Trim()
                : (e.Elements().Where(x => x.Name.LocalName == "link").FirstOrDefault(x => (string?)x.Attribute("rel") is null or "alternate") ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "link"))?.Attribute("href")?.Value;
            if (string.IsNullOrEmpty(title) || link == null || !link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new Headline(title, link, When(Text(e, "pubDate") ?? Text(e, "updated") ?? Text(e, "published") ?? Text(e, "date")), source));
        }
        return list;
    }

    private static string Clean(string? s) =>
        s == null ? "" : Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", "")), @"\s+", " ").Trim();

    private static string[] Words(string? s) => (s ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length > 1).ToArray();

    /// <summary>
    /// Die Schlagzeilen für den Überblick: aus jedem Feed abwechselnd die neuesten, Stichwörter zum Vorziehen zuerst, Stichwörter
    /// zum Weglassen raus, fast gleiche Überschriften nur einmal.
    /// </summary>
    internal static List<Headline> Pick(List<List<Headline>> feeds, Settings s)
    {
        var exclude = Words(s.Exclude);
        var prefer = Words(s.Prefer);
        bool Has(Headline h, string[] words) => words.Any(w => h.Title.Contains(w, StringComparison.OrdinalIgnoreCase));
        var lists = feeds.Select(f => f.Where(h => !Has(h, exclude)).OrderByDescending(h => h.At ?? DateTime.MinValue).ToList()).ToList();
        var mixed = new List<Headline>();
        for (int i = 0; lists.Any(l => i < l.Count); i++)
            foreach (var l in lists) if (i < l.Count) mixed.Add(l[i]);
        var ordered = mixed.Where(h => Has(h, prefer)).Concat(mixed.Where(h => !Has(h, prefer)));
        var seen = new HashSet<string>();
        var picked = new List<Headline>();
        foreach (var h in ordered)
        {
            var key = new string(h.Title.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(50).ToArray());
            if (!seen.Add(key)) continue;
            picked.Add(h);
            if (picked.Count >= s.Count) break;
        }
        return picked;
    }

    /// <summary>Wetter und Schlagzeilen für heute als Text für den Chat - null, wenn nichts eingeschaltet ist oder nichts kam.</summary>
    public static async Task<string?> TextAsync(HttpClient http, UserSpace space, DateOnly today, CancellationToken ct)
    {
        var s = Of(space.Db);
        var parts = new List<string>();
        if (s.Weather && s.Lat is double lat && s.Lon is double lon && await Journal.JournalLookup.Weather(http, lat, lon, today, ct) is { } w)
            parts.Add($"🌤 Wetter{(string.IsNullOrWhiteSpace(s.Place) ? "" : " in " + s.Place)}: {w}");
        if (s.News && s.Sources.Count > 0)
        {
            var feeds = await Task.WhenAll(s.Sources.Select(x => Presets.FirstOrDefault(p => p.Id == x) is { } p
                ? FeedAsync(http, p.Url, p.Name, ct) : FeedAsync(http, x, new Uri(x).Host.Replace("www.", ""), ct)));
            var picked = Pick(feeds.ToList(), s);
            // Als Link mit Überschrift ([Titel](Adresse) - Web und App zeigen nur den Titel); Google News nennt die Quelle schon im Titel
            if (picked.Count > 0)
                parts.Add("📰 Schlagzeilen:\n" + string.Join("\n", picked.Select(h =>
                    $"• [{h.Title.Replace("[", "(").Replace("]", ")")}]({h.Link.Replace(")", "%29")})" + (h.Source == "Google News" ? "" : $" – {h.Source}"))));
        }
        return parts.Count > 0 ? string.Join("\n\n", parts) : null;
    }

    /// <summary>Für den Morgenüberblick aus dem Hintergrund (synchron, höchstens 20 Sekunden).</summary>
    public static string? Text(IServiceProvider services, UserSpace space, DateOnly today)
    {
        var s = Of(space.Db);
        if (!s.News && !s.Weather) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
            return TextAsync(http, space, today, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { return null; }
    }

    public record PlaceHit(string Name, string? Region, string? Country, double Lat, double Lon);

    /// <summary>Ort suchen (Open-Meteo Geocoding, ohne Anmeldung) - für die Einstellung des Wetters.</summary>
    public static async Task<List<PlaceHit>> PlacesAsync(HttpClient http, string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return [];
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=8&language=de&name=" + Uri.EscapeDataString(q.Trim()), ct));
            if (!doc.RootElement.TryGetProperty("results", out var r)) return [];
            string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return r.EnumerateArray().Select(e => new PlaceHit(S(e, "name") ?? "", S(e, "admin1"), S(e, "country"), e.GetProperty("latitude").GetDouble(), e.GetProperty("longitude").GetDouble())).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException) { return []; }
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/briefing", (HttpContext http) => new { settings = Of(http.Space().Db), presets = Presets.Select(p => new { p.Id, p.Name }) });
        api.MapPut("/briefing", (Settings body, HttpContext http) => { Save(http.Space().Db, body); return Results.Ok(Of(http.Space().Db)); });
        api.MapGet("/briefing/places", async (string q, IHttpClientFactory f, CancellationToken ct) => await PlacesAsync(f.CreateClient(), q, ct));
        // Vorschau: so sähe der Zusatz heute aus
        api.MapGet("/briefing/preview", async (HttpContext http, IHttpClientFactory f, CancellationToken ct) =>
        {
            var space = http.Space();
            return new { text = await TextAsync(f.CreateClient(), space, DateOnly.FromDateTime(Pukki.Now(space)), ct) };
        });
    }
}
