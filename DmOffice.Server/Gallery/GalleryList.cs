using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Gallery;

/// <summary>
/// Die Liste der Galerie-Ordner fertig gepackt im Speicher: bei 50.000 Fotos ist sie viele MB groß, und sie bei jedem Abruf
/// aus der Datenbank zu lesen, zu erzeugen und zu packen kostete Sekunden. Gebaut wird einmal je Stand (ETag aus
/// <see cref="GalleryFolders.Version"/>); während Einlesen und Vorschau-Job laufend Zeilen ändern, höchstens alle
/// <see cref="Grace"/> neu - so lange gilt der vorige Stand weiter. Ändert sich, welche Ordner der Benutzer sehen darf, sofort.
/// </summary>
public static class GalleryList
{
    private sealed record Built(string Tag, string Sources, DateTime At, byte[] Raw, byte[] Br, byte[] Gz);

    private static readonly ConcurrentDictionary<string, Built> Cache = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    public static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Nach einer Änderung durch den Benutzer selbst (Löschen): gleich neu bauen statt nach der Schonfrist.</summary>
    public static void Forget(string userId)
    {
        foreach (var key in Cache.Keys.Where(k => k.StartsWith(userId + "|", StringComparison.Ordinal))) Cache.TryRemove(key, out _);
    }

    /// <summary>Gepackt ausliefern (Brotli, sonst Gzip), mit ETag/304. <paramref name="kind"/> trennt die Formate.</summary>
    public static async Task<IResult> SendAsync(HttpContext http, ServerConfig config, string kind, Func<List<GalleryFolders.Entry>, object> shape)
    {
        var space = http.Space();
        var key = space.User.Id + "|" + kind;
        var (tag, sources) = GalleryFolders.VersionOf(space, config);
        Cache.TryGetValue(key, out var b);
        if (b == null || b.Sources != sources || (b.Tag != tag && DateTime.UtcNow - b.At > Grace))
        {
            var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(http.RequestAborted);
            try
            {
                // Wer gewartet hat, nimmt, was der andere eben gebaut hat
                Cache.TryGetValue(key, out b);
                if (b == null || b.Sources != sources || (b.Tag != tag && DateTime.UtcNow - b.At > Grace))
                {
                    b = await Task.Run(() => Build(tag, sources, JsonSerializer.SerializeToUtf8Bytes(shape(GalleryFolders.List(space, config)), Json)));
                    Cache[key] = b;
                }
            }
            finally { gate.Release(); }
        }
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.ETag = b.Tag;
        http.Response.Headers.Vary = "Accept-Encoding";
        if (http.Request.Headers.IfNoneMatch.ToString() == b.Tag) return Results.StatusCode(304);
        var accept = http.Request.Headers.AcceptEncoding.ToString();
        if (accept.Contains("br")) { http.Response.Headers.ContentEncoding = "br"; return Results.Bytes(b.Br, "application/json"); }
        if (accept.Contains("gzip")) { http.Response.Headers.ContentEncoding = "gzip"; return Results.Bytes(b.Gz, "application/json"); }
        return Results.Bytes(b.Raw, "application/json");
    }

    private static Built Build(string tag, string sources, byte[] raw)
    {
        // Brotli Qualität 5: deutlich kleiner als Gzip und für viele MB in deutlich unter einer Sekunde gepackt
        var br = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        if (!BrotliEncoder.TryCompress(raw, br, out var brLen, 5, 22)) throw new InvalidOperationException("Brotli");
        using var gz = new MemoryStream();
        using (var s = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true)) s.Write(raw);
        return new Built(tag, sources, DateTime.UtcNow, raw, br[..brLen], gz.ToArray());
    }

    /// <summary>Wie üblich (camelCase), leere Angaben weglassen.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Kompakte Form für die Web-Galerie: jeder Ordnerpfad und jeder Text (Kamera, Objektiv, Typ, Titel ...) nur einmal,
    /// je Foto eine Zeile als Feld ohne Feldnamen. Bei 50.000 Fotos gut ein Fünftel der alten Liste.
    /// Zeile: [Ordner, Name, Größe, geändert (ms), Typ, aufgenommen (ms), Kamera, Objektiv, Brennweite, Blende, Belichtung, ISO,
    /// Breite, Höhe, Dauer, Bewertung, Titel, Stichwörter, Beschreibung] - Texte als Nummer in „strs“, leere Enden weggelassen.
    /// </summary>
    public static object Compact(List<GalleryFolders.Entry> entries)
    {
        var dirs = new List<string>(); var dirIx = new Dictionary<string, int>(StringComparer.Ordinal);
        var strs = new List<string>(); var strIx = new Dictionary<string, int>(StringComparer.Ordinal);
        int Dir(string d) { if (!dirIx.TryGetValue(d, out var i)) { i = dirs.Count; dirs.Add(d); dirIx[d] = i; } return i; }
        object? Str(string? s) { if (s == null) return null; if (!strIx.TryGetValue(s, out var i)) { i = strs.Count; strs.Add(s); strIx[s] = i; } return i; }
        var rows = new List<object?[]>(entries.Count);
        foreach (var e in entries)
        {
            var slash = e.Path.LastIndexOf('/');
            var m = e.Meta;
            var row = new object?[]
            {
                Dir(e.Source + "/" + (slash < 0 ? "" : e.Path[..(slash + 1)])), e.Path[(slash + 1)..], e.Size, e.Modified, Str(e.Mime),
                m?.Taken, Str(m?.Camera), Str(m?.Lens), m?.Focal, m?.Aperture, m?.Exposure, m?.Iso,
                m?.Width, m?.Height, m?.Duration, m?.Rating, Str(m?.Title), Str(m?.Keywords), Str(e.Description),
            };
            var n = row.Length;
            while (n > 5 && row[n - 1] == null) n--;
            rows.Add(n == row.Length ? row : row[..n]);
        }
        return new { v = 3, dirs, strs, rows };
    }
}
