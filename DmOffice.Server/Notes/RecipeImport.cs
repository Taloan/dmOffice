using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;

namespace DmOffice.Notes;

/// <summary>
/// Rezept per Link (Coach Stufe 2): die Seite holen, das schema.org-Rezept (JSON-LD, wie es Chefkoch, Lecker, Kitchen
/// Stories und fast alle Rezeptseiten mitliefern) lesen und eine Notiz in der Gestalt der notizi-Rezeptvorlage anlegen -
/// Bild, Eckdaten, „Zutaten“ als Kästchen je Abschnitt, „Zubereitung“ nummeriert, „Rezept von …“, Tag „recipe“. So erkennt
/// der Essensplan sie (Recipes.IsRecipe) und „Zutaten auf die Einkaufsliste“ funktioniert wie bei Rezepten aus notizi.
/// </summary>
public static class RecipeImport
{
    public sealed record Section(string Name, List<string> Items);
    public sealed record Recipe(string Name, string Description, string Yield, string PrepTime, string CookTime, string TotalTime,
        List<Section> Ingredients, List<Section> Steps, string? Image, string Source, string Tip = "", string Calories = "");

    private const long MaxPage = 5 * 1024 * 1024, MaxImage = 8 * 1024 * 1024;

    // Nur Adressen im Internet: Rezepte liegen nie im eigenen Netz - so kann der Link nichts auf dem Server oder im LAN abfragen.
    // Geprüft beim Verbinden selbst (auch nach Weiterleitungen und gegen DNS-Tricks), wie bei den Kalender-Abos.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true, MaxAutomaticRedirections = 5, AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addrs.FirstOrDefault(IsPublic) ?? throw new HttpRequestException("Diese Adresse ist nicht erlaubt.");
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    }) { Timeout = TimeSpan.FromSeconds(20) };

    static RecipeImport()
    {
        // Manche Seiten liefern Programmen ohne Browserkennung nur eine Fehlerseite
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; dmOffice-Rezept/1.0)");
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE,de;q=0.9,en;q=0.5");
    }

    internal static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any))
            return false;
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
        return (b[0] & 0xFE) != 0xFC;   // fc00::/7 (Unique Local)
    }

    /// <summary>Seite holen und das Rezept lesen; Fehlertext, wenn es keines gibt.</summary>
    public static async Task<Recipe> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Bitte einen Link mit http:// oder https:// angeben.");
        using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Die Seite antwortet mit {(int)res.StatusCode}.");
        var html = await ReadLimited(res, MaxPage, ct);
        return FromHtml(Encoding.UTF8.GetString(html), res.RequestMessage?.RequestUri ?? uri)
            ?? throw new InvalidOperationException("Auf dieser Seite steht kein Rezept in maschinenlesbarer Form (schema.org).");
    }

    /// <summary>Eine Seite als Text (nur Internet-Adressen, höchstens 5 MB) - für die Rezeptsuche.</summary>
    internal static async Task<string> PageAsync(Uri uri, CancellationToken ct)
    {
        using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Die Seite antwortet mit {(int)res.StatusCode}.");
        return Encoding.UTF8.GetString(await ReadLimited(res, MaxPage, ct));
    }

    /// <summary>Rezept aus einer HTML-Seite: der erste JSON-LD-Block mit @type Recipe (auch in @graph oder einer Liste).</summary>
    public static Recipe? FromHtml(string html, Uri page)
    {
        foreach (Match m in Regex.Matches(html, @"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(m.Groups[1].Value.Trim(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException) { continue; }
            using (doc)
                if (FindRecipe(doc.RootElement) is { } r) return Read(r, page);
        }
        return null;
    }

    private static JsonElement? FindRecipe(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) if (FindRecipe(x) is { } r) return r;
            return null;
        }
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("@type", out var t) && (t.ValueKind == JsonValueKind.String ? t.GetString() == "Recipe"
            : t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "Recipe")))
            return e;
        if (e.TryGetProperty("@graph", out var g)) return FindRecipe(g);
        if (e.TryGetProperty("mainEntity", out var me)) return FindRecipe(me);
        return null;
    }

    private static Recipe Read(JsonElement r, Uri page)
    {
        var ingredients = Strings(r, "recipeIngredient");
        if (ingredients.Count == 0) ingredients = Strings(r, "ingredients");
        var steps = new List<Section>();
        if (r.TryGetProperty("recipeInstructions", out var ins)) ReadSteps(ins, steps, "");
        var image = r.TryGetProperty("image", out var img) ? ImageUrl(img) : null;
        if (image != null && Uri.TryCreate(page, image, out var abs)) image = abs.ToString();
        return new Recipe(Text(r, "name"), Text(r, "description"), Yield(r), Duration(Text(r, "prepTime")), Duration(Text(r, "cookTime")),
            Duration(Text(r, "totalTime")), ingredients.Count > 0 ? [new Section("", ingredients)] : [], steps, image, page.ToString(), Calories: Calories(r));
    }

    /// <summary>Kalorien je Portion aus nutrition.calories („520 kcal“, „520 calories“, 520); kJ umgerechnet; sonst leer.</summary>
    private static string Calories(JsonElement r)
    {
        if (!r.TryGetProperty("nutrition", out var n) || n.ValueKind != JsonValueKind.Object || !n.TryGetProperty("calories", out var c)) return "";
        var text = c.ValueKind == JsonValueKind.Number ? c.GetRawText() : c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        var m = Regex.Match(text, @"(\d+(?:[.,]\d+)?)\s*(kj)?", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return "";
        if (m.Groups[2].Success) v /= 4.184;
        return v is > 0 and <= 5000 ? Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    }

    /// <summary>Anleitung: Text, Liste von Texten, HowToStep (text) oder HowToSection (name + itemListElement).</summary>
    private static void ReadSteps(JsonElement e, List<Section> into, string section)
    {
        Section Current()
        {
            if (into.Count == 0 || into[^1].Name != section) into.Add(new Section(section, []));
            return into[^1];
        }
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                // Ein Block Text: an Zeilenumbrüchen teilen
                foreach (var line in Plain(e.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    Current().Items.Add(line);
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) ReadSteps(x, into, section);
                break;
            case JsonValueKind.Object:
                var type = e.TryGetProperty("@type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (type == "HowToSection" || (type == null && e.TryGetProperty("itemListElement", out _)))
                {
                    var name = Text(e, "name");
                    if (e.TryGetProperty("itemListElement", out var items)) ReadSteps(items, into, name);
                }
                else
                {
                    var text = Text(e, "text");
                    if (text.Length == 0) text = Text(e, "name");
                    if (text.Length > 0) Current().Items.Add(text);
                }
                break;
        }
    }

    private static string? ImageUrl(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Array => e.EnumerateArray().Select(ImageUrl).FirstOrDefault(u => !string.IsNullOrEmpty(u)),
        JsonValueKind.Object => e.TryGetProperty("url", out var u) ? ImageUrl(u) : e.TryGetProperty("contentUrl", out var c) ? ImageUrl(c) : null,
        _ => null,
    };

    private static string Yield(JsonElement r)
    {
        if (!r.TryGetProperty("recipeYield", out var y)) return "";
        var v = y.ValueKind switch
        {
            JsonValueKind.Number => y.GetRawText(),
            JsonValueKind.String => Plain(y.GetString() ?? ""),
            // Oft ["4", "4 Portionen"] - die ausführlichere Angabe nehmen
            JsonValueKind.Array => y.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetRawText() : Plain(x.GetString() ?? ""))
                .OrderByDescending(s => s.Length).FirstOrDefault() ?? "",
            _ => "",
        };
        return Regex.IsMatch(v, @"^\d+$") ? v + " Portionen" : v;
    }

    /// <summary>ISO-8601-Dauer („PT1H30M“, „P0DT0H45M“) lesbar: „1 Std. 30 Min.“; Unlesbares bleibt, wie es ist.</summary>
    public static string Duration(string iso)
    {
        var m = Regex.Match(iso, @"^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:\d+S)?)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return iso;
        int d = Num(m.Groups[1]), h = Num(m.Groups[2]), min = Num(m.Groups[3]);
        h += d * 24; h += min / 60; min %= 60;
        if (h == 0 && min == 0) return "";
        return h > 0 && min > 0 ? $"{h} Std. {min} Min." : h > 0 ? $"{h} Std." : $"{min} Min.";
        static int Num(Group g) => g.Success ? int.Parse(g.Value) : 0;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Plain(v.GetString() ?? "") : "";

    private static List<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return [];
        if (v.ValueKind == JsonValueKind.String) return Plain(v.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Plain(x.GetString() ?? "")).Where(s => s.Length > 0).ToList();
    }

    /// <summary>HTML-Reste und Entitäten aus den Texten der Seite (manche liefern „&amp;lt;p&amp;gt;…“).</summary>
    private static string Plain(string s)
    {
        s = WebUtility.HtmlDecode(WebUtility.HtmlDecode(s));
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</li>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = Regex.Replace(s, @"[ \t ]+", " ");
        return string.Join('\n', s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)).Trim();
    }

    /// <summary>Notiz in der Gestalt der notizi-Vorlage (XHTML wie im Editor) und ihr Klartext.</summary>
    public static (string Html, string Plain) ToNote(Recipe r, string? imageBlob)
    {
        var h = new StringBuilder("<div data-notizi=\"1\">");
        var p = new StringBuilder();
        void Para(string text, string? tag = null)
        {
            h.Append(tag == null ? "<p>" : $"<{tag}>").Append(Esc(text)).Append(tag == null ? "</p>" : $"</{tag}>");
            p.Append(text).Append('\n');
        }
        Para(r.Name.Length > 0 ? r.Name : "Rezept");
        if (imageBlob != null) h.Append("<p><img src=\"blob:").Append(imageBlob).Append("\" /></p>");
        var facts = new List<string>();
        if (r.Yield.Length > 0) facts.Add(r.Yield);
        // Kalorien je Portion: daran misst der Essensplan, wie das Rezept in den Tag passt
        if (Recipes.Kcal(r.Calories.Trim() + " kcal je Portion") is int kcal) facts.Add(kcal + " kcal je Portion");
        if (r.PrepTime.Length > 0) facts.Add("Arbeitszeit " + r.PrepTime);
        if (r.CookTime.Length > 0) facts.Add("Koch-/Backzeit " + r.CookTime);
        if (r.TotalTime.Length > 0 && r.TotalTime != r.PrepTime) facts.Add("Gesamt " + r.TotalTime);
        if (facts.Count > 0) Para(string.Join("  ·  ", facts));
        if (r.Description.Length > 0) { h.Append("<p><i>").Append(Esc(r.Description)).Append("</i></p>"); p.Append(r.Description).Append('\n'); }
        if (r.Ingredients.Count > 0)
        {
            Para("Zutaten", "h3");
            foreach (var g in r.Ingredients)
            {
                if (g.Name.Length > 0) Para(g.Name, "h4");
                h.Append("<ul>");
                foreach (var i in g.Items) { h.Append("<li>☐ ").Append(Esc(i)).Append("</li>"); p.Append("☐ ").Append(i).Append('\n'); }
                h.Append("</ul>");
            }
        }
        if (r.Steps.Count > 0)
        {
            Para("Zubereitung", "h3");
            foreach (var s in r.Steps)
            {
                if (s.Name.Length > 0) Para(s.Name, "h4");
                h.Append("<ol>");
                foreach (var step in s.Items) { h.Append("<li>").Append(Esc(step)).Append("</li>"); p.Append(step).Append('\n'); }
                h.Append("</ol>");
            }
        }
        if (r.Tip.Length > 0)
        {
            Para("Tipp", "h3");
            foreach (var line in r.Tip.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Para(line);
        }
        if (Uri.TryCreate(r.Source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
        {
            var host = u.Host.Replace("www.", "");
            h.Append("<p>Rezept von <a href=\"").Append(Esc(r.Source)).Append("\">").Append(Esc(host)).Append("</a></p>");
            p.Append("Rezept von ").Append(host);
        }
        // Rezept-Scan: Buch und Seite als Text, ohne Angabe keine Zeile
        else if (r.Source.Trim().Length > 0) Para("Rezept von " + r.Source.Trim());
        h.Append("</div>");
        return (h.ToString(), p.ToString().Replace("\n", "\r\n"));
    }

    // Nur was im XHTML stören würde - Umlaute bleiben Umlaute
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>Bild laden (nur echte Bilder bis 8 MB); ohne Bild geht es auch.</summary>
    public static async Task<byte[]?> ImageAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
            using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true) return null;
            return await ReadLimited(res, MaxImage, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) { return null; }
    }

    private static async Task<byte[]> ReadLimited(HttpResponseMessage res, long max, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength > max) throw new InvalidOperationException("Die Seite ist zu groß.");
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > max) throw new InvalidOperationException("Die Seite ist zu groß.");
        }
        return ms.ToArray();
    }

    /// <summary>Alles zusammen: holen, Bild in den Notizspeicher, Notiz anlegen (Tag „recipe“). Gibt die Nummer der Notiz zurück.</summary>
    public static async Task<long> ImportAsync(UserSpace space, NotesDb db, string url, string? folder, CancellationToken ct)
    {
        var recipe = await FetchAsync(url, ct);
        string? blob = null;
        if (recipe.Image != null && await ImageAsync(recipe.Image, ct) is { Length: > 0 } data)
        {
            blob = await NotesSync.BlobNameAsync(space, data, ct);
            db.PutBlob(blob, data);
        }
        var (html, plain) = ToNote(recipe, blob);
        var id = db.CreateNote(folder);
        db.SaveNote(id, html, plain);
        db.SetTags(id, [Recipes.Tag]);
        return id;
    }
}
