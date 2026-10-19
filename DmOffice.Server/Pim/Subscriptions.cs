using System.Net;
using System.Net.Sockets;
using DmOffice.Core;

namespace DmOffice.Pim;

/// <summary>Abonnierter Kalender: eine .ics-Adresse, die regelmaessig neu geholt wird (Muellabfuhr, Feiertage, Schulferien ...).</summary>
public sealed class Subscription
{
    public long Collection { get; set; }
    public string Url { get; set; } = "";
    /// <summary>Abstand in Minuten (60 = stuendlich, 1440 = taeglich).</summary>
    public int Interval { get; set; } = 360;
    public DateTime? LastFetch { get; set; }
    public string? LastError { get; set; }
    public string? ETag { get; set; }
}

/// <summary>
/// Kalender abonnieren (webcal/https). Die Termine liegen wie gewohnt in einer Sammlung - schreibgeschuetzt,
/// auch ueber CalDAV -, damit Handy und Thunderbird sie mit abgleichen. Beim Abruf wird ersetzt, was sich
/// geaendert hat, und geloescht, was der Anbieter entfernt hat.
/// </summary>
public static class Subscriptions
{
    private const string Key = "pim.subscriptions";
    private const long MaxBytes = 20 * 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true, MaxAutomaticRedirections = 5, AutomaticDecompression = DecompressionMethods.All,
        // Keine Adressen auf dem Server selbst (localhost, Link-Local wie 169.254.169.254)
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addrs.FirstOrDefault(a => !Blocked(a)) ?? throw new HttpRequestException("Diese Adresse ist nicht erlaubt.");
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    }) { Timeout = TimeSpan.FromSeconds(30) };

    static Subscriptions() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("dmOffice-Kalenderabo/1.0");

    private static bool Blocked(IPAddress a) =>
        IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal ||
        (a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..]) ||
        a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);

    public static List<Subscription> All(UserDb db) => db.GetJson<List<Subscription>>(Key);
    private static void Save(UserDb db, List<Subscription> list) => db.SetJson(Key, list);
    public static Subscription? Of(UserDb db, long collection) => All(db).FirstOrDefault(s => s.Collection == collection);
    public static bool IsSubscribed(UserDb db, long collection) => All(db).Any(s => s.Collection == collection);

    public static void Update(UserDb db, long collection, Action<Subscription> change)
    {
        lock (db)
        {
            var list = All(db);
            if (list.FirstOrDefault(s => s.Collection == collection) is { } s) { change(s); Save(db, list); }
        }
    }

    public static void Add(UserDb db, Subscription s) { lock (db) { var list = All(db); list.RemoveAll(x => x.Collection == s.Collection); list.Add(s); Save(db, list); } }
    public static void Remove(UserDb db, long collection) { lock (db) { var list = All(db); list.RemoveAll(x => x.Collection == collection); Save(db, list); } }

    /// <summary>webcal:// ist https:// (bzw. http://) - wie in Kalender-Apps.</summary>
    public static Uri Normalize(string url)
    {
        url = url.Trim();
        if (url.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[10..];
        else if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new ArgumentException("Bitte eine Adresse mit https://, http:// oder webcal:// angeben.");
        return uri;
    }

    /// <summary>Abholen und abgleichen. Gibt die Zahl der Termine zurueck (oder -1: unveraendert laut ETag).</summary>
    public static async Task<int> FetchAsync(UserSpace space, Subscription sub, CancellationToken ct)
    {
        var db = space.Db;
        var coll = PimStore.Find(db, sub.Collection) ?? throw new KeyNotFoundException("Kalender gibt es nicht mehr.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Normalize(sub.Url));
            if (sub.ETag != null && PimStore.Objects(db, coll.Id).Count > 0) req.Headers.TryAddWithoutValidation("If-None-Match", sub.ETag);
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == HttpStatusCode.NotModified)
            {
                Update(db, coll.Id, s => { s.LastFetch = DateTime.UtcNow; s.LastError = null; });
                return -1;
            }
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Der Server antwortet mit {(int)res.StatusCode} {res.ReasonPhrase}.");
            if (res.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("Der Kalender ist zu groß (über 20 MB).");
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxBytes) throw new InvalidOperationException("Der Kalender ist zu groß (über 20 MB).");
            }
            var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unter der Adresse liegt kein Kalender (kein iCalendar/.ics).");

            // Abgleich nach UID: gleiche UID behaelt ihre Adresse, Neues kommt dazu, Fehlendes geht
            var existing = PimStore.Objects(db, coll.Id).Where(o => o.Uid != null).GroupBy(o => o.Uid!).ToDictionary(g => g.Key, g => g.First());
            var seen = new HashSet<string>();
            int count = 0;
            foreach (var (uri, data) in PimApi.Split(coll, text))
            {
                var uid = Dav.Ics.Parse(data).FirstOrDefault()?.Children.FirstOrDefault(c => c.Name is "VEVENT" or "VTODO")?.Text("UID");
                if (uid == null || !seen.Add(uid)) continue;
                var target = existing.TryGetValue(uid, out var old) ? old.Uri : uri;
                if (old == null || old.Data != data)
                {
                    try { PimStore.Put(db, coll, target, data, zone: space.Zone); }
                    catch (Exception) { continue; } // ein kaputter Eintrag haelt den Rest nicht auf
                }
                count++;
            }
            foreach (var (uid, o) in existing)
                if (!seen.Contains(uid)) PimStore.Delete(db, coll, o.Uri);

            var etag = res.Headers.ETag?.ToString();
            Update(db, coll.Id, s => { s.LastFetch = DateTime.UtcNow; s.LastError = null; s.ETag = etag; });
            return count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var msg = ex is TaskCanceledException ? "Zeitüberschreitung beim Abrufen." : ex.InnerException is SocketException se ? "Keine Verbindung: " + se.Message : ex.Message;
            Update(db, coll.Id, s => { s.LastFetch = DateTime.UtcNow; s.LastError = msg; });
            throw new InvalidOperationException(msg, ex);
        }
    }
}

/// <summary>Holt faellige Kalender-Abos aller angemeldeten Benutzer.</summary>
public sealed class SubscriptionSync : BackgroundService
{
    private readonly Spaces _spaces;
    private readonly ILogger<SubscriptionSync> _log;
    public SubscriptionSync(Spaces spaces, ILogger<SubscriptionSync> log) { _spaces = spaces; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stop);
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in _spaces.All())
            {
                List<Subscription> subs;
                try { subs = Subscriptions.All(space.Db); } catch { continue; }
                foreach (var sub in subs)
                {
                    if (sub.LastFetch is DateTime last && DateTime.UtcNow - last < TimeSpan.FromMinutes(Math.Max(15, sub.Interval))) continue;
                    try { await Subscriptions.FetchAsync(space, sub, stop); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                    catch (Exception ex) { _log.LogInformation("Kalenderabo {Url}: {Error}", sub.Url, ex.Message); }
                }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stop); } catch (OperationCanceledException) { break; }
        }
    }
}
