using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Gallery;

/// <summary>Ein Profil der Bibliothek (Index). Id: Prüfsumme der Datei (16 Hex), bei Dateien mit mehreren Profilen „-n“ dahinter.</summary>
public sealed record LensLibEntry(string Id, string? Maker, string? Make, string? Model, string? Camera, string? Lens, string? LensName, string? LensId,
    bool? Raw, double? Crop, double? FocalMin, double? FocalMax, double? ApertureMin, double? ApertureMax, string Source, bool Vignetting, bool Tca)
{
    /// <summary>Anzeigename: Objektiv (schön), sonst wie in den Aufnahmedaten.</summary>
    public string Name => LensName ?? Lens ?? Path.GetFileNameWithoutExtension(Source);
    /// <summary>Für Auswahllisten: Name · Kamera · RAW/JPEG (wie libLabel in web/src/settings/LensLibrary.tsx).</summary>
    public string Label => string.Join(" · ", new[] { Name, Camera ?? Model, Raw == true ? "RAW" : Raw == false ? "JPEG" : null }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>
/// Profilbibliothek der Objektivkorrektur (Kennung lenslib): viele Profile als ZIP hochgeladen,
/// im Hintergrund gelesen (Photo-Engine /lens/library) und unter &lt;Daten&gt;/lens-library abgelegt: index.json und je Profil
/// profiles/&lt;id&gt;.json. Mitgeliefert wird nichts. Hat ein Foto keine passende Regel, sucht <see cref="Find"/> hier ein Profil
/// nach dem Objektivnamen aus den Aufnahmedaten - Regeln gehen immer vor (LensProfiles.Block).
/// </summary>
public static class LensLibrary
{
    public const string Prefix = "lib:";
    /// <summary>Grenzen des ZIP-Imports: Größe des Uploads, Einträge, entpackte Größe je Datei und insgesamt, Verdichtung je Datei.</summary>
    public const long MaxUpload = 2L * 1024 * 1024 * 1024, MaxTotal = 4L * 1024 * 1024 * 1024, MaxFile = 32L * 1024 * 1024;
    public const int MaxRatio = 200, MaxChunk = 16 * 1024 * 1024;
    /// <summary>Einträge der ZIP-Datei höchstens (in Tests kleiner).</summary>
    internal static int MaxEntries { get; set; } = 100_000;
    private static readonly string[] Extensions = [".lcp", ".xml", ".json"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lock Gate = new();
    private static string? _dir;
    private static Dictionary<string, LensLibEntry> _entries = [];
    private static long _epoch;
    // Suchschlüssel (normalisierter Objektivname) → Profile; aus dem Index berechnet
    private static Dictionary<string, List<LensLibEntry>> _byLens = [];
    // Kamera (normalisiert) → Sensorformat, aus den Profilen der Bibliothek
    private static Dictionary<string, double> _crops = [];
    private static readonly ConcurrentDictionary<string, Hit?> FindCache = new();
    private static readonly ConcurrentDictionary<string, JsonObject> ProfileCache = new();

    private sealed class Index
    {
        public long Epoch { get; set; }
        public List<LensLibEntry> Entries { get; set; } = [];
    }

    public static void Init(string root)
    {
        var dir = Path.Combine(root, "lens-library");
        Directory.CreateDirectory(Path.Combine(dir, "profiles"));
        lock (Gate) { _dir = dir; Load(); }
        // Reste abgebrochener Uploads
        foreach (var f in Directory.EnumerateFiles(dir, "upload-*.zip")) try { File.Delete(f); } catch (IOException) { }
    }

    /// <summary>Nur für Tests: eigener Ordner.</summary>
    internal static void UseDir(string dir) => Init(dir);

    private static void Load()
    {
        var file = Path.Combine(_dir!, "index.json");
        Index index;
        try { index = File.Exists(file) ? JsonSerializer.Deserialize<Index>(File.ReadAllText(file), Json) ?? new() : new(); }
        catch (JsonException) { index = new(); }
        _entries = index.Entries.ToDictionary(e => e.Id);
        _epoch = index.Epoch;
        Rebuild();
    }

    private static void Save()
    {
        if (_dir == null) return;
        var file = Path.Combine(_dir, "index.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new Index { Epoch = _epoch, Entries = [.. _entries.Values.OrderBy(e => e.Source, StringComparer.Ordinal)] }, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    private static void Rebuild()
    {
        var by = new Dictionary<string, List<LensLibEntry>>();
        var crops = new Dictionary<string, double>();
        foreach (var e in _entries.Values)
        {
            foreach (var k in new[] { e.Lens, e.LensName }.Select(Norm).Where(k => k.Length > 0).Distinct())
            {
                if (!by.TryGetValue(k, out var l)) by[k] = l = [];
                l.Add(e);
            }
            if (e.Crop is { } c && e.Model is { } m && Norm(m) is { Length: > 0 } km) crops.TryAdd(km, c);
        }
        _byLens = by;
        _crops = crops;
        FindCache.Clear();
        ProfileCache.Clear();
    }

    /// <summary>Ändert sich mit jedem Import und beim Leeren (im Stand der Objektivprofile, damit Vorschaubilder neu entstehen).</summary>
    public static long Epoch { get { lock (Gate) return _epoch; } }
    public static int Count { get { lock (Gate) return _entries.Count; } }
    public static bool IsLib(string? id) => id != null && id.StartsWith(Prefix, StringComparison.Ordinal);
    public static LensLibEntry? Entry(string id) { lock (Gate) return IsLib(id) && _entries.TryGetValue(id[Prefix.Length..], out var e) ? e : null; }
    public static bool Exists(string id) => Entry(id) != null;
    public static List<LensLibEntry> All() { lock (Gate) return [.. _entries.Values]; }

    /// <summary>Profil im Format der Engine (mit Name); null, wenn es das nicht gibt.</summary>
    public static JsonObject? Profile(string id)
    {
        if (Entry(id) is not { } e || _dir == null) return null;
        if (!ProfileCache.TryGetValue(e.Id, out var p))
        {
            try { p = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "profiles", e.Id + ".json"))) as JsonObject; }
            catch (Exception x) when (x is IOException or JsonException) { p = null; }
            if (p == null) return null;
            p["name"] = e.Name;
            if (ProfileCache.Count > 200) ProfileCache.Clear();
            ProfileCache[e.Id] = p;
        }
        var copy = (JsonObject)p.DeepClone();
        copy["id"] = id;
        return copy;
    }

    // ---------------- Zuordnung ----------------

    /// <summary>Schlüssel für Namen: klein, „f/2.8“ = „f2.8“, nur Buchstaben, Ziffern und Punkt.</summary>
    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant()) if (char.IsAsciiLetterOrDigit(c) || c == '.') sb.Append(c);
        return sb.ToString();
    }

    private static string FirstWord(string? s) => Norm(s?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());

    /// <summary>Warum ein Profil gewählt wurde: Kamera (genau diese), Format (gleicher Hersteller und Sensorformat), Hersteller, Objektiv (nur der Name passt).</summary>
    public sealed record Hit(LensLibEntry Entry, string Why);

    /// <summary>
    /// Bestes Profil für ein Foto nach Objektivname (genau, sonst enthalten), Kamera und Art (RAW/JPEG): bevorzugt die Art des Fotos
    /// (für JPEG ein JPEG-Profil, falls vorhanden), dann genau diese Kamera, dann gleicher Hersteller mit gleichem Sensorformat,
    /// dann gleicher Hersteller, sonst irgendeins. null: kein Objektivname oder nichts gefunden.
    /// </summary>
    public static Hit? Find(string? camera, string? lens, bool raw)
    {
        var key = Norm(lens);
        if (key.Length < 3) return null;
        var cacheKey = key + "|" + Norm(camera) + "|" + raw;
        if (FindCache.TryGetValue(cacheKey, out var cached)) return cached;
        Hit? hit;
        lock (Gate) hit = FindLocked(camera, key, raw);
        if (FindCache.Count > 5000) FindCache.Clear();
        FindCache[cacheKey] = hit;
        return hit;
    }

    private static Hit? FindLocked(string? camera, string key, bool raw)
    {
        var exact = true;
        if (!_byLens.TryGetValue(key, out var list))
        {
            // Nicht genau: der längste Name, der den gesuchten enthält oder in ihm steckt (mindestens 6 Zeichen)
            exact = false;
            list = [.. _byLens.Where(x => x.Key.Length >= 6 && key.Length >= 6 && (x.Key.EndsWith(key, StringComparison.Ordinal) || key.EndsWith(x.Key, StringComparison.Ordinal)))
                .OrderByDescending(x => Math.Min(x.Key.Length, key.Length)).Take(1).SelectMany(x => x.Value)];
            if (list.Count == 0) return null;
        }
        var cam = Norm(camera);
        var make = FirstWord(camera);
        double? crop = null;
        foreach (var (m, c) in _crops) if (cam.Length > 0 && (cam == m || cam.EndsWith(m, StringComparison.Ordinal))) { crop = c; break; }
        (int Score, string Why) Rate(LensLibEntry e)
        {
            var kind = e.Raw == null || e.Raw == raw ? 100 : 0;
            var model = Norm(e.Model);
            var makeE = FirstWord(e.Make);
            var sameMake = make.Length > 0 && makeE.Length > 0 && (make == makeE || make.StartsWith(makeE, StringComparison.Ordinal) || makeE.StartsWith(make, StringComparison.Ordinal));
            if (model.Length > 0 && cam.Length > 0 && (cam == model || cam.EndsWith(model, StringComparison.Ordinal) || cam == Norm(e.Make) + model))
                return (kind + 30, "camera");
            var sameCrop = crop != null && e.Crop != null && Math.Abs(crop.Value - e.Crop.Value) < 0.05;
            if (sameMake && sameCrop) return (kind + 20, "format");
            if (sameMake) return (kind + 15, "make");
            if (sameCrop) return (kind + 10, "format");
            return (kind, "lens");
        }
        var best = list.Select(e => (e, r: Rate(e))).OrderByDescending(x => x.r.Score).ThenBy(x => x.e.Source, StringComparer.Ordinal).First();
        return new Hit(best.e, exact ? best.r.Why : best.r.Why == "camera" ? "camera" : "similar");
    }

    /// <summary>Für das Foto: RAW nach Dateiname (Aufnahmedaten aus der Galerie) oder nach dem Inhalt (LensProfiles.MetaOf setzt dann „.dng“).</summary>
    public static Hit? Find(FileMeta.Row? meta) =>
        meta == null || !Features.On(Features.LensLib) ? null : Find(meta.Camera, meta.Lens, Thumbs.IsRaw(meta.Path));

    /// <summary>Sieht der Inhalt nach RAW aus (nicht JPEG, PNG, GIF, WebP, HEIC/AVIF)?</summary>
    public static bool LooksRaw(byte[] d)
    {
        if (d.Length < 12) return false;
        if (d[0] == 0xFF && d[1] == 0xD8) return false;
        if (d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return false;
        if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return false;
        if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F') return false;
        if (d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p')
        {
            var brand = Encoding.ASCII.GetString(d, 8, 4);
            return brand.StartsWith("crx", StringComparison.Ordinal); // CR3; heic, mif1, avif …: kein RAW
        }
        return true;
    }

    // ---------------- Suche für die Oberfläche ----------------

    public static (int Total, List<LensLibEntry> Items) Search(string? q, string? maker, string? kind, int take)
    {
        var words = (q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Norm).Where(w => w.Length > 0).ToArray();
        List<LensLibEntry> all;
        lock (Gate) all = [.. _entries.Values];
        var hits = all.Where(e =>
            (string.IsNullOrEmpty(maker) || string.Equals(e.Maker, maker, StringComparison.OrdinalIgnoreCase))
            && (kind is not ("raw" or "jpeg") || e.Raw == (kind == "raw"))
            && words.All(w => Haystack(e).Contains(w, StringComparison.Ordinal)))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Source, StringComparer.Ordinal).ToList();
        return (hits.Count, [.. hits.Take(Math.Clamp(take, 1, 5000))]);
    }

    /// <summary>Suchtext: jedes Feld für sich normalisiert, damit ein Wort nicht über zwei Felder hinweg trifft.</summary>
    private static string Haystack(LensLibEntry e) => string.Join(' ', new[] { e.Name, e.Lens, e.Camera, e.Make, e.Model, e.Maker, e.LensId, e.Source }.Select(Norm));

    public static List<(string Maker, int Count)> Makers()
    {
        lock (Gate) return [.. _entries.Values.GroupBy(e => e.Maker ?? "").OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Select(g => (g.Key, g.Count()))];
    }

    // ---------------- Leeren ----------------

    /// <summary>Alle Profile der Bibliothek entfernen. Rückgabe: Zahl der entfernten Profile.</summary>
    public static int Clear()
    {
        lock (Gate)
        {
            var n = _entries.Count;
            if (_dir != null)
            {
                var profiles = Path.Combine(_dir, "profiles");
                if (Directory.Exists(profiles)) Directory.Delete(profiles, recursive: true);
                Directory.CreateDirectory(profiles);
            }
            _entries = [];
            _epoch = Math.Max(_epoch + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Save();
            Rebuild();
            return n;
        }
    }

    /// <summary>Einzelne Profile entfernen (Verwaltung). Rückgabe: Zahl der entfernten Profile.</summary>
    public static int Remove(IEnumerable<string> ids)
    {
        lock (Gate)
        {
            var n = 0;
            foreach (var id in ids.Where(IsLib).Select(i => i[Prefix.Length..]).Distinct())
            {
                if (!_entries.Remove(id)) continue;
                n++;
                if (_dir != null) try { File.Delete(Path.Combine(_dir, "profiles", id + ".json")); } catch (IOException) { }
            }
            if (n == 0) return 0;
            _epoch = Math.Max(_epoch + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Save();
            Rebuild();
            return n;
        }
    }

    // ---------------- Import ----------------

    /// <summary>Ergebnis eines Imports: neu aufgenommen, schon vorhanden, nicht lesbar (mit Grund), nicht unterstützt (Fischauge).</summary>
    public sealed record Result(int Files, int Added, int Known, int Failed, int Unsupported, bool Stopped, List<string> Errors);

    /// <summary>Weg in der ZIP-Datei bereinigt: Schrägstriche, keine „..“/„.“, ohne Laufwerk; ohne vorangestelltes „LensProfiles/1.0/“.</summary>
    public static string CleanPath(string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not ("." or "..") && !p.EndsWith(':')).ToList();
        // Ordner selbst gezippt: „1.0/…“, „LensProfiles/1.0/…“, „CameraRaw/LensProfiles/1.0/…“
        var cut = parts.FindLastIndex(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^\d+(\.\d+)*$"));
        if (cut >= 0 && cut < parts.Count - 1 && parts.Take(cut).All(p => p.Equals("LensProfiles", StringComparison.OrdinalIgnoreCase)
            || p.Equals("CameraRaw", StringComparison.OrdinalIgnoreCase) || p.Equals("Adobe", StringComparison.OrdinalIgnoreCase)))
            parts = parts[(cut + 1)..];
        else if (parts.Count > 1 && parts[0].Equals("LensProfiles", StringComparison.OrdinalIgnoreCase)) parts = parts[1..];
        return string.Join('/', parts.Select(p => p.Length > 120 ? p[..120] : p));
    }

    private static bool Wanted(ZipArchiveEntry e)
    {
        if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) return false;
        var name = e.FullName.Replace('\\', '/');
        if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || Path.GetFileName(name).StartsWith("._", StringComparison.Ordinal)) return false;
        return Extensions.Contains(Path.GetExtension(name).ToLowerInvariant());
    }

    /// <summary>Wie viele Profildateien die ZIP-Datei enthält; wirft bei kaputter Datei oder zu vielen Einträgen.</summary>
    public static int CountFiles(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > MaxEntries) throw new ArgumentException($"Die ZIP-Datei enthält mehr als {MaxEntries:N0} Einträge.");
        return archive.Entries.Count(Wanted);
    }

    /// <summary>
    /// ZIP lesen: nichts wird entpackt auf die Platte geschrieben, jede Profildatei nur in den Speicher (Grenzen siehe oben).
    /// <paramref name="read"/>: Datei → Einträge {"profile", "meta"} (Engine /lens/library); wirft <see cref="InvalidDataException"/>
    /// bei nicht lesbarer Datei (Meldung „unsupported:…“ für nicht unterstützte Profile), andere Fehler brechen ab.
    /// </summary>
    public static async Task<Result> ImportAsync(string zip, Func<byte[], CancellationToken, Task<JsonArray>> read, Action<int, int, int>? progress,
        Func<CancellationToken, Task>? check, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > MaxEntries) throw new ArgumentException($"Die ZIP-Datei enthält mehr als {MaxEntries:N0} Einträge.");
        var files = archive.Entries.Where(Wanted).ToList();
        int added = 0, known = 0, failed = 0, unsupported = 0, done = 0;
        long total = 0;
        var errors = new List<string>();
        var stopped = false;
        void Error(string path, string why) { failed++; if (errors.Count < 200) errors.Add(path + ": " + why); }

        // Lesen nacheinander (ZipArchive ist nicht threadsicher), Auswerten in der Engine zu viert
        var pending = new List<Task>();
        var write = new Lock();
        var sinceSave = 0;
        try
        {
            foreach (var entry in files)
            {
                ct.ThrowIfCancellationRequested();
                if (check != null) await check(ct);
                var path = CleanPath(entry.FullName);
                byte[]? data = null;
                if (entry.Length > MaxFile) Error(path, "zu groß");
                else if (entry.CompressedLength > 0 && entry.Length > 1024 * 1024 && entry.Length / entry.CompressedLength > MaxRatio) Error(path, "ungewöhnlich stark gepackt");
                else
                {
                    data = ReadLimited(entry, out var tooBig);
                    if (tooBig) { Error(path, "zu groß"); data = null; }
                    else if ((total += data.Length) > MaxTotal) throw new ArgumentException("Die ZIP-Datei ist entpackt zu groß.");
                }
                if (data == null)
                {
                    progress?.Invoke(++done, files.Count, failed);
                    continue;
                }
                var hash = Convert.ToHexStringLower(SHA256.HashData(data))[..16];
                bool have;
                lock (Gate) have = _entries.ContainsKey(hash) || _entries.ContainsKey(hash + "-1");
                if (have)
                {
                    lock (write) { known++; done++; }
                    UpdateSource(hash, path);
                    progress?.Invoke(done, files.Count, failed);
                    continue;
                }
                var bytes = data;
                pending.Add(Task.Run(async () =>
                {
                    try
                    {
                        var items = await read(bytes, ct);
                        var n = Store(hash, path, items);
                        lock (write) { if (n > 0) added++; else Error(path, "keine Korrekturwerte"); }
                    }
                    catch (InvalidDataException e)
                    {
                        lock (write) { if (e.Message.StartsWith("unsupported:", StringComparison.Ordinal)) unsupported++; else Error(path, e.Message); }
                    }
                    lock (write) { done++; progress?.Invoke(done, files.Count, failed); }
                }, ct));
                if (pending.Count >= 4) { await Task.WhenAny(pending); foreach (var t in pending.Where(t => t.IsCompleted).ToList()) { await t; pending.Remove(t); } }
                if (++sinceSave >= 500) { sinceSave = 0; lock (Gate) Save(); }
            }
            await Task.WhenAll(pending);
        }
        catch (JobControl.Stopped) { stopped = true; await Task.WhenAll(pending.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default))); }
        finally
        {
            lock (Gate)
            {
                if (added > 0) _epoch = Math.Max(_epoch + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Save();
                Rebuild();
            }
        }
        return new Result(files.Count, added, known, failed, unsupported, stopped, errors);
    }

    private static byte[] ReadLimited(ZipArchiveEntry entry, out bool tooBig)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxFile) { tooBig = true; return []; }
        }
        tooBig = false;
        return ms.ToArray();
    }

    private static void UpdateSource(string hash, string path)
    {
        lock (Gate)
            foreach (var id in _entries.Keys.Where(k => k == hash || k.StartsWith(hash + "-", StringComparison.Ordinal)).ToList())
                if (_entries[id].Source != path) _entries[id] = _entries[id] with { Source = path, Maker = MakerOf(path) ?? _entries[id].Maker };
    }

    private static string? MakerOf(string path)
    {
        var i = path.IndexOf('/');
        return i > 0 ? path[..i] : null;
    }

    private static string? Str(JsonNode? n, int max = 200) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? (s.Trim().Length > max ? s.Trim()[..max] : s.Trim()) : null;
    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>Profile einer Datei ablegen (Index noch nicht gespeichert). Rückgabe: Zahl der abgelegten Profile.</summary>
    internal static int Store(string hash, string path, JsonArray items)
    {
        var n = 0;
        var list = items.OfType<JsonObject>().Where(i => i["profile"] is JsonObject p
            && new[] { "distortion", "vignetting", "tca" }.Any(k => (p[k] as JsonArray)?.Count > 0)).Take(50).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var id = list.Count == 1 ? hash : hash + "-" + (i + 1);
            var p = (JsonObject)list[i]["profile"]!.DeepClone();
            var m = list[i]["meta"] as JsonObject ?? [];
            p.Remove("id");
            var e = new LensLibEntry(id, MakerOf(path), Str(m["make"]), Str(m["model"]), Str(m["camera"]), Str(m["lens"]), Str(m["lensName"]) ?? Str(p["name"]),
                Str(m["lensId"], 40), m["raw"] is JsonValue r && r.TryGetValue<bool>(out var raw) ? raw : null, Num(m["crop"]),
                Num(m["focalMin"]), Num(m["focalMax"]), Num(m["apertureMin"]), Num(m["apertureMax"]), path,
                (p["vignetting"] as JsonArray)?.Count > 0, (p["tca"] as JsonArray)?.Count > 0);
            if (_dir != null)
            {
                var file = Path.Combine(_dir, "profiles", id + ".json");
                File.WriteAllText(file + ".tmp", p.ToJsonString());
                File.Move(file + ".tmp", file, overwrite: true);
            }
            lock (Gate) _entries[id] = e;
            n++;
        }
        return n;
    }

    // ---------------- API ----------------

    private static readonly ConcurrentDictionary<string, (string User, string File, long Size)> Uploads = new();
    private static int _running;
    private static ILogger? _log;

    public record StartBody(long Size);
    public record DoneBody(string? Lang);

    /// <summary>Läuft gerade ein Einlesen?</summary>
    public static bool Running => Volatile.Read(ref _running) == 1;

    /// <summary>Hochladen einer ZIP-Datei beginnen (nur Admins). Rückgabe: Kennung des Uploads.</summary>
    public static string StartUpload(string user, bool isAdmin, long size)
    {
        if (!isAdmin) throw new UnauthorizedAccessException("Profile vom Admin kann nur ein Admin ändern.");
        if (size is <= 0 or > MaxUpload) throw new ArgumentException("Die ZIP-Datei darf höchstens 2 GB groß sein.");
        var id = Guid.NewGuid().ToString("N");
        var file = Path.Combine(_dir!, "upload-" + id + ".zip");
        File.WriteAllBytes(file, []);
        foreach (var old in Uploads.Where(u => u.Value.User == user).ToList())
        {
            Uploads.TryRemove(old.Key, out _);
            try { File.Delete(old.Value.File); } catch (IOException) { }
        }
        Uploads[id] = (user, file, size);
        return id;
    }

    private static void Admin(HttpContext http)
    {
        if (!http.Space().User.IsAdmin) throw new UnauthorizedAccessException("Profile vom Admin kann nur ein Admin ändern.");
    }

    private static object Row(LensLibEntry e) => new
    {
        id = Prefix + e.Id, name = e.Name, e.Maker, e.Make, e.Model, e.Camera, e.Lens, e.LensId, e.Raw, e.Crop, e.FocalMin, e.FocalMax, e.ApertureMin, e.ApertureMax,
        e.Source, e.Vignetting, e.Tca,
    };

    public static string Summary(Result r, string? lang)
    {
        var en = lang == "en";
        var c = System.Globalization.CultureInfo.GetCultureInfo(en ? "en-US" : "de-DE");
        string N(int n) => n.ToString("N0", c);
        var head = en ? (r.Stopped ? "Cancelled: " : "") + $"{N(r.Added)} lens profiles added" : (r.Stopped ? "Abgebrochen: " : "") + $"{N(r.Added)} Objektivprofile aufgenommen";
        var parts = new List<string>();
        if (r.Known > 0) parts.Add(en ? $"{N(r.Known)} already there" : $"{N(r.Known)} schon vorhanden");
        if (r.Unsupported > 0) parts.Add(en ? $"{N(r.Unsupported)} fisheye profiles skipped" : $"{N(r.Unsupported)} Fischaugen-Profile übersprungen");
        if (r.Failed > 0) parts.Add(en ? $"{N(r.Failed)} not readable" : $"{N(r.Failed)} nicht lesbar");
        return parts.Count == 0 ? head : head + ", " + string.Join(", ", parts);
    }

    public static void Map(RouteGroupBuilder api, WebApplication app, string root)
    {
        Init(root);
        _log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.Gallery.LensLibrary");
        var lib = api.MapGroup("/lib");

        lib.MapGet("/", (string? q, string? maker, string? kind, int? take) =>
        {
            var (total, items) = Search(q, maker, kind, take ?? 2000);
            return Results.Ok(new
            {
                count = Count, total, items = items.Select(Row), makers = Makers().Select(m => new { name = m.Maker, n = m.Count }),
                running = Volatile.Read(ref _running) == 1,
            });
        });

        lib.MapGet("/item", (string id) => Entry(id) is { } e ? Results.Ok(Row(e)) : throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht mehr."));

        // Hochladen in Stücken (wie große Dateien sonst auch): anfangen, Stücke anhängen, fertig → Einlesen im Hintergrund
        lib.MapPost("/upload", (StartBody body, HttpContext http) =>
        {
            var id = StartUpload(http.Space().User.Id, http.Space().User.IsAdmin, body.Size);
            return Results.Ok(new { id, chunk = MaxChunk });
        });
        lib.MapPut("/upload/{id}", async (string id, long offset, HttpContext http, CancellationToken ct) =>
        {
            Admin(http);
            if (!Uploads.TryGetValue(id, out var up) || up.User != http.Space().User.Id) throw new KeyNotFoundException("Dieser Upload ist nicht mehr da - bitte noch einmal hochladen.");
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxChunk + 1024;
            await using var fs = new FileStream(up.File, FileMode.Open, FileAccess.Write, FileShare.None);
            if (offset != fs.Length) return Results.Conflict(new { error = "Stück an falscher Stelle.", offset = fs.Length });
            fs.Seek(0, SeekOrigin.End);
            var buf = new byte[81920];
            long n = 0;
            int r;
            while ((r = await http.Request.Body.ReadAsync(buf, ct)) > 0)
            {
                n += r;
                if (n > MaxChunk || fs.Length + r > up.Size) throw new ArgumentException("Das Stück ist zu groß.");
                await fs.WriteAsync(buf.AsMemory(0, r), ct);
            }
            return Results.Ok(new { offset = fs.Length });
        });
        lib.MapPost("/upload/{id}/done", (string id, DoneBody body, HttpContext http) =>
        {
            Admin(http);
            if (!Uploads.TryRemove(id, out var up) || up.User != http.Space().User.Id) throw new KeyNotFoundException("Dieser Upload ist nicht mehr da - bitte noch einmal hochladen.");
            var user = http.Space().User;
            int count;
            try
            {
                if (new FileInfo(up.File).Length != up.Size) throw new ArgumentException("Der Upload ist unvollständig - bitte noch einmal hochladen.");
                try { count = CountFiles(up.File); }
                catch (InvalidDataException) { throw new ArgumentException("Das ist keine lesbare ZIP-Datei."); }
                if (count == 0) throw new ArgumentException("In der ZIP-Datei sind keine Objektivprofile (.lcp, .xml, .json).");
                if (!PhotoEngine.Configured) return Results.Json(new { error = "Zum Einlesen wird die Photo-Engine gebraucht - sie ist ausgeschaltet." }, statusCode: 503);
                if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Es werden gerade schon Objektivprofile eingelesen.");
            }
            catch
            {
                try { File.Delete(up.File); } catch (IOException) { }
                throw;
            }
            _ = Task.Run(async () =>
            {
                using var control = JobControl.Run(user.Id, Activity.LensLib);
                using var job = Activity.Start(user.Id, Activity.LensLib, null, count);
                var en = body.Lang == "en";
                try
                {
                    var result = await ImportAsync(up.File, ReadAsync, (done, total, failed) =>
                        job.Progress(done, total, failed > 0 ? (en ? $"{failed:N0} not readable" : $"{failed:N0} nicht lesbar") : null),
                        ct => JobControl.CheckAsync(user.Id, Activity.LensLib, ct), CancellationToken.None);
                    job.Finish(Summary(result, body.Lang), result.Errors.Count > 0 ? result.Errors : null);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _log?.LogWarning("Bibliothek der Objektivprofile einlesen: {Error}", e.Message);
                    job.Fail(e.Message);
                }
                finally
                {
                    Volatile.Write(ref _running, 0);
                    try { File.Delete(up.File); } catch (IOException) { }
                }
            });
            return Results.Accepted(value: new { started = true, count });
        });

        lib.MapDelete("/", (HttpContext http) =>
        {
            Admin(http);
            if (Volatile.Read(ref _running) == 1) throw new InvalidOperationException("Es werden gerade Objektivprofile eingelesen.");
            var rules = LensProfiles.DropLibraryRules();
            return Results.Ok(new { removed = Clear(), rules });
        });
    }

    /// <summary>Eine Datei in der Engine lesen.</summary>
    private static async Task<JsonArray> ReadAsync(byte[] data, CancellationToken ct)
    {
        var (status, json) = await PhotoEngine.CallAsync("/lens/library", data, "application/octet-stream", 120, ct);
        if (status == 400)
        {
            var error = json?["error"]?.GetValue<string>() ?? "nicht lesbar";
            throw new InvalidDataException(error.Contains("FisheyeModel", StringComparison.Ordinal) ? "unsupported:" + error : error);
        }
        if (status != 200 || json?["items"] is not JsonArray items) throw new InvalidOperationException("Die Photo-Engine antwortet gerade nicht.");
        return items;
    }
}
