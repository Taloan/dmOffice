using System.Collections.Concurrent;
using System.Security.Cryptography;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;

namespace DmOffice.Gallery;

/// <summary>
/// Doppelte Fotos in der Zeitleiste stapeln (Schalter galdupes): dasselbe Foto in der Ablage und in einem Galerie-Ordner, etwa
/// vom Handy hochgeladen und später mit Lightroom importiert. Lightroom ergänzt beim Import Metadaten im Dateikopf - die
/// Prüfsumme der ganzen Datei unterscheidet sich dann, die Bilddaten nicht.
/// Zwei Stufen, damit nicht jede Datei gelesen werden muss:
/// 1. Kandidaten aus dem Metadaten-Index (Aufnahmezeit, Kamera, Bildgröße, Belichtung, Blende, ISO) - ohne Dateizugriff.
/// 2. Nur Kandidaten mit gleichem Schlüssel werden einmal gelesen: Prüfsumme der Bilddaten ohne die Metadaten-Segmente (APPn, COM).
///    Das geschieht im Hintergrund; das Ergebnis bleibt je Datei (Größe und Änderungszeit) gespeichert. Danach baut die Zeitleiste neu.
/// Vorerst nur JPEG: bei HEIC und RAW liegen die Bilddaten anders.
/// </summary>
public static class GalleryDupes
{
    private static readonly HashSet<string> Jpeg = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg" };

    /// <summary>Schlüssel aus dem Metadaten-Index; null, wenn die Angaben für einen sicheren Vergleich nicht reichen.</summary>
    public static string? CandidateKey(GalleryTimeline.Entry e)
    {
        if (!Jpeg.Contains(e.Ext) || e.Meta is not { } m) return null;
        if (m.Taken is not long taken || string.IsNullOrEmpty(m.Camera) || m.Width is not int w || m.Height is not int h) return null;
        return string.Join('|', taken, m.Camera, w + "x" + h, m.Exposure, m.Aperture, m.Iso);
    }

    /// <summary>Stand einer Datei: ändert sich Größe oder Zeit, wird neu gelesen.</summary>
    private static string Stamp(GalleryTimeline.Entry e) => e.Size.ToString("x") + "-" + e.Modified.ToString("x");

    /// <summary>Reihenfolge im Stapel: eigene Ablage oben, dann geteilte, dann Galerie-Ordner.</summary>
    public static int Rank(GalleryTimeline.Entry e) => e.Id.StartsWith("ext:", StringComparison.Ordinal) ? 2 : e.Owner != null ? 1 : 0;

    // ---------------- Prüfsumme der Bilddaten ----------------

    /// <summary>
    /// SHA-256 über alle JPEG-Segmente außer APP0-APP15 und COM, ab dem Bildbeginn (SOS) über den Rest der Datei.
    /// Null, wenn es kein JPEG ist.
    /// </summary>
    public static async Task<string?> ImageHashAsync(Stream s, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var head = new byte[4];
        if (!await ReadExactAsync(s, head.AsMemory(0, 2), ct) || head[0] != 0xFF || head[1] != 0xD8) return null;
        while (true)
        {
            if (!await ReadExactAsync(s, head.AsMemory(0, 2), ct) || head[0] != 0xFF) return null;
            var marker = head[1];
            if (marker == 0xFF) { continue; } // Füllbyte
            if (!await ReadExactAsync(s, head.AsMemory(2, 2), ct)) return null;
            var len = (head[2] << 8) | head[3];
            if (len < 2) return null;
            var body = new byte[len - 2];
            if (!await ReadExactAsync(s, body, ct)) return null;
            var meta = marker is >= 0xE0 and <= 0xEF or 0xFE;
            if (!meta) { hash.AppendData(head); hash.AppendData(body); }
            if (marker == 0xDA) break;
        }
        var buf = new byte[1 << 20];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0) hash.AppendData(buf, 0, n);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<bool> ReadExactAsync(Stream s, Memory<byte> buf, CancellationToken ct) =>
        await s.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct) == buf.Length;

    // ---------------- Gespeicherte Prüfsummen ----------------

    private static readonly ConcurrentDictionary<UserDb, bool> Ready = new();

    private static void Ensure(UserDb db)
    {
        if (Ready.ContainsKey(db)) return;
        db.Exec("CREATE TABLE IF NOT EXISTS gallery_imagehash(id TEXT PRIMARY KEY, stamp TEXT NOT NULL, sha TEXT NULL)");
        Ready[db] = true;
    }

    /// <summary>
    /// Prüfsummen der Kandidaten (Id → Prüfsumme), soweit schon bekannt. Fehlende liest ein Lauf im Hintergrund nach; danach wird die
    /// Zeitleiste des Benutzers neu gebaut. Leer, solange keine zwei Fotos denselben Kandidaten-Schlüssel haben.
    /// </summary>
    public static Dictionary<string, string> Known(HttpContext http, ServerConfig config, IReadOnlyList<GalleryTimeline.Entry> items)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidates = items.Select(e => (e, k: CandidateKey(e))).Where(x => x.k != null).GroupBy(x => x.k!)
            .Where(g => g.Count() > 1).SelectMany(g => g.Select(x => x.e)).ToList();
        if (candidates.Count == 0) return result;
        var space = http.Space();
        Ensure(space.Db);
        var stored = space.Db.Query("SELECT id, stamp, sha FROM gallery_imagehash", r => (Id: r.GetString(0), Stamp: r.GetString(1), Sha: r.IsDBNull(2) ? null : r.GetString(2)))
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
        var missing = new List<GalleryTimeline.Entry>();
        foreach (var e in candidates)
        {
            // Geänderte Datei (etwa Sterne oder Farbe ins XMP geschrieben): die bisherige Prüfsumme gilt weiter, bis die neue berechnet ist -
            // sonst fiele das Foto bis dahin aus dem Stapel. Metadaten ändern die Bilddaten ohnehin nicht.
            if (stored.TryGetValue(e.Id, out var s) && s.Sha != null) result[e.Id] = s.Sha;
            if (s.Stamp != Stamp(e)) missing.Add(e);
        }
        if (missing.Count > 0) Start(http, config, space, missing);
        return result;
    }

    private static readonly ConcurrentDictionary<string, bool> Running = new();

    private static void Start(HttpContext http, ServerConfig config, UserSpace space, List<GalleryTimeline.Entry> missing)
    {
        if (!Running.TryAdd(space.User.Id, true)) return;
        // Wege zu den Dateien jetzt auflösen (geteilte Ordner brauchen die Anfrage), gelesen wird danach ohne sie
        var jobs = new List<(GalleryTimeline.Entry Entry, Func<CancellationToken, Task<(Stream, IDisposable?)>> Open)>();
        foreach (var e in missing)
        {
            try
            {
                if (e.Id.StartsWith("ext:", StringComparison.Ordinal))
                {
                    var (src, path) = GalleryFolders.Resolve(space, config, e.Id[4..]);
                    jobs.Add((e, async ct =>
                    {
                        var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
                        try { return (await driver.OpenReadAsync(path, ct), driver); }
                        catch { driver.Dispose(); throw; }
                    }));
                }
                else if (SharedFiles.ById(http, e.FileId) is { } hit)
                {
                    var (db, path) = (hit.Loc.Db, hit.File.Path);
                    jobs.Add((e, _ => Task.FromResult<(Stream, IDisposable?)>((FileStore.OpenRead(db, path), null))));
                }
            }
            catch (KeyNotFoundException) { }
        }
        _ = Task.Run(async () =>
        {
            var changed = 0;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                foreach (var (e, open) in jobs)
                {
                    string? sha;
                    try
                    {
                        var (s, owner) = await open(cts.Token);
                        try { await using (s) sha = await ImageHashAsync(s, cts.Token); }
                        finally { owner?.Dispose(); }
                    }
                    // Nicht erreichbar oder gerade weg: beim nächsten Aufbau der Zeitleiste noch einmal
                    catch (Exception x) when (x is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException) { continue; }
                    // Kein JPEG: als erledigt merken (sha null), sonst würde es immer wieder gelesen
                    var before = space.Db.Scalar("SELECT sha FROM gallery_imagehash WHERE id = $i", ("$i", e.Id)) as string;
                    space.Db.Exec("INSERT OR REPLACE INTO gallery_imagehash(id, stamp, sha) VALUES($i, $s, $h)", ("$i", e.Id), ("$s", Stamp(e)), ("$h", sha));
                    if (before != sha) changed++;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                Running.TryRemove(space.User.Id, out _);
                // Nur neu aufbauen, wenn sich eine Prüfsumme geändert hat (nach Sternen oder Farbe bleibt sie gleich)
                if (changed > 0) GalleryTimeline.Forget(space.User.Id);
            }
        });
    }

    /// <summary>Für Tests: warten, bis der Lauf eines Benutzers fertig ist.</summary>
    public static async Task WaitAsync(string userId, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (Running.ContainsKey(userId) && DateTime.UtcNow < until) await Task.Delay(20);
    }

    // ---------------- Stapeln ----------------

    /// <summary>
    /// Fotos mit gleichem Kandidaten-Schlüssel und gleicher Prüfsumme der Bilddaten zu einem Stapel: oben das Foto aus der eigenen
    /// Ablage, die übrigen (samt ihrer RAW-Stapel) darunter, als Doppelte gekennzeichnet - Aktionen wie Löschen nehmen sie nicht mit.
    /// </summary>
    public static List<GalleryTimeline.Entry> Stack(List<GalleryTimeline.Entry> list, IReadOnlyDictionary<string, string> hashes)
    {
        if (hashes.Count < 2) return list;
        string? KeyOf(GalleryTimeline.Entry e) => hashes.TryGetValue(e.Id, out var h) && CandidateKey(e) is { } k ? k + "|" + h : null;
        var groups = list.Select(e => (e, k: KeyOf(e))).Where(x => x.k != null).GroupBy(x => x.k!)
            .Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.Select(x => x.e).OrderBy(Rank).ThenBy(e => e.Id, StringComparer.Ordinal).ToList());
        if (groups.Count == 0) return list;
        var outList = new List<GalleryTimeline.Entry>(list.Count);
        foreach (var e in list)
        {
            if (KeyOf(e) is not { } k || !groups.TryGetValue(k, out var g)) { outList.Add(e); continue; }
            if (!ReferenceEquals(g[0], e)) continue;
            var stack = new List<GalleryTimeline.Entry>(e.Stack ?? []);
            foreach (var d in g.Skip(1))
            {
                stack.Add(d with { Stack = null, Dupe = true });
                if (d.Stack != null) stack.AddRange(d.Stack.Select(x => x with { Dupe = true }));
            }
            outList.Add(e with { Stack = stack });
        }
        return outList;
    }
}
