using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Gallery;

/// <summary>
/// Objektivkorrektur eines Fotos (Kennung lenscorr): an/aus, Profil (null = über die Regeln, „-“ = keins), Stärke der Profilwerte in Prozent,
/// eigene Regler für Verzeichnung (−100 Kissen … +100 Tonne) und Vignettierung (−100 dunkler … +100 heller) samt Mittelpunkt.
/// </summary>
public sealed record LensSettings(bool Off = false, string? Profile = null, double Distortion = 100, double Vignetting = 100, bool Tca = true,
    double ManualDistortion = 0, double ManualVignetting = 0, double Midpoint = 50)
{
    public LensSettings Clamped() => this with
    {
        Profile = Profile is { Length: > 0 and <= 40 } p ? p : null,
        Distortion = Math.Clamp(Distortion, 0, 200), Vignetting = Math.Clamp(Vignetting, 0, 200),
        ManualDistortion = Math.Clamp(ManualDistortion, -100, 100), ManualVignetting = Math.Clamp(ManualVignetting, -100, 100), Midpoint = Math.Clamp(Midpoint, 0, 100),
    };
}

/// <summary>Regel: Kamera enthält …, Objektiv enthält …, optional Brennweitenbereich → Profil. Die erste passende gilt.</summary>
public sealed record LensRule(string Id, string? Camera, string? Lens, double? FocalMin, double? FocalMax, string Profile)
{
    /// <summary>Groß-/Kleinschreibung und mehrfache Leerzeichen zählen nicht („NOKTON  28mm“ = „nokton 28mm“).</summary>
    public bool Matches(string? camera, string? lens, double? focal) =>
        Contains(camera, Camera) && Contains(lens, Lens)
        && (FocalMin == null || focal >= FocalMin - 0.01) && (FocalMax == null || focal <= FocalMax + 0.01);

    private static bool Contains(string? value, string? part) =>
        string.IsNullOrWhiteSpace(part) || value != null && Squeeze(value).Contains(Squeeze(part), StringComparison.OrdinalIgnoreCase);

    /// <summary>Leerraum am Rand weg, innen zu einem Leerzeichen.</summary>
    public static string Squeeze(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Objektivprofile und Regeln für alle (data/lenscorr.json): lesen darf jeder, ändern nur Admins. Ein Profil enthält je Brennweite
/// (optional je Blende) Verzeichnung k1..k3, Vignettierung a1..a3 und Farbsäume - umgerechnet in das Modell der Photo-Engine
/// (photo-engine/engine/lens.py). Die Korrektur rechnet die Engine; ohne Engine bleibt das Foto unkorrigiert.
/// </summary>
public static class LensProfiles
{
    private sealed class Store
    {
        public long Epoch { get; set; }
        public List<JsonObject> Profiles { get; set; } = [];
        public List<LensRule> Rules { get; set; } = [];
        /// <summary>Früher serverweit „Neue Fotos gleich korrigieren“; heute je Person (<see cref="Core.UserSettings.LensAutoNew"/>).
        /// Der gespeicherte Wert bleibt die Vorgabe für alle, die selbst noch nichts gewählt haben (Vorgabe: an).</summary>
        public bool AutoNew { get; set; } = true;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lock Gate = new();
    private static string? _file;
    private static Store _store = new();
    public const int MaxProfiles = 500, MaxRules = 200;

    public static void Init(string root)
    {
        lock (Gate)
        {
            _file = Path.Combine(root, "lenscorr.json");
            _store = Load(_file);
        }
    }

    /// <summary>Nur für Tests: Speicher in einer Datei.</summary>
    internal static void UseFile(string file) { lock (Gate) { _file = file; _store = Load(file); } }

    private static Store Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(file), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Ändert sich mit jedem Profil und jeder Regel - im Fingerabdruck der Einstellungen, damit Vorschaubilder neu entstehen.</summary>
    public static long Epoch
    {
        get
        {
            long epoch;
            lock (Gate) epoch = _store.Epoch;
            if (Features.On(Features.LensLib)) epoch += LensLibrary.Epoch; // [feature:lenslib]
            return epoch;
        }
    }

    public static List<JsonObject> Profiles() { lock (Gate) return [.. _store.Profiles.Select(p => (JsonObject)p.DeepClone())]; }
    public static List<LensRule> Rules() { lock (Gate) return [.. _store.Rules]; }

    /// <param name="user">Person, deren eigene Profile mit gelten</param>
    public static JsonObject? Profile(string id, string? user = null)
    {
        if (LensLibrary.IsLib(id)) return LensLibrary.Profile(id); // [feature:lenslib]
        if (LensOwn.IsOwn(id)) return LensOwn.Profile(user, id); // [feature:lenslib]
        lock (Gate) return _store.Profiles.FirstOrDefault(p => IdOf(p) == id)?.DeepClone() as JsonObject;
    }

    public static string? IdOf(JsonObject p) => p["id"]?.GetValue<string>();
    public static string NameOf(JsonObject p) => p["name"]?.GetValue<string>() ?? "";

    /// <summary>Für die ETags der Galerie-Listen: mit dem Stand der Profile, damit Browser und App die neu berechneten Vorschaubilder holen.</summary>
    public static string ListTag => Features.On(Features.LensCorr) ? "|lens" + Epoch : "";

    /// <summary>Vorgabe für „Neue Fotos gleich korrigieren“, solange eine Person selbst nichts gewählt hat (übernommen aus der früheren
    /// serverweiten Einstellung: war sie aus, bleibt die Vorgabe aus, sonst an).</summary>
    public static bool AutoNewDefault { get { lock (Gate) return _store.AutoNew; } }

    /// <summary>Nur für Tests: frühere serverweite Einstellung nachstellen.</summary>
    internal static void SetAutoNewDefault(bool on) => Change(s => s.AutoNew = on, recompute: false);

    /// <param name="recompute">Ändert die Rechnung (Profil, Regel): neuer Stand, Vorschaubilder entstehen neu. Name oder Einstellung: nicht.</param>
    private static void Change(Action<Store> change, bool recompute = true)
    {
        lock (Gate)
        {
            var copy = JsonSerializer.Deserialize<Store>(JsonSerializer.Serialize(_store, Json), Json)!;
            change(copy);
            copy.Epoch = recompute ? Math.Max(_store.Epoch + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : _store.Epoch;
            if (_file != null)
            {
                var temp = _file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(copy, Json));
                File.Move(temp, _file, overwrite: true);
            }
            _store = copy;
        }
    }

    // [feature:lenslib]
    /// <summary>Neuer Stand ohne sonstige Änderung (eigene Profile oder Regeln einer Person haben sich geändert): Vorschaubilder entstehen neu.</summary>
    public static void Touch() => Change(_ => { });

    /// <summary>Gibt es das Profil vom Admin (serverweit oder aus einer ZIP-Datei)?</summary>
    public static bool AdminExists(string id)
    {
        if (LensLibrary.Exists(id)) return true;
        lock (Gate) return _store.Profiles.Any(p => IdOf(p) == id);
    }

    /// <summary>Herkunft eines Profils: „calibrated“ (eingemessen) oder die Art der eingelesenen Datei.</summary>
    public static string? SourceOf(JsonObject p) => p["source"]?.GetValue<string>();
    // [/feature:lenslib]

    /// <summary>Profile aufnehmen (aus der Engine geprüft). Rückgabe: neue Kennungen.</summary>
    public static List<string> Add(IEnumerable<JsonObject> profiles)
    {
        var ids = new List<string>();
        Change(s =>
        {
            foreach (var p in profiles)
            {
                if (s.Profiles.Count >= MaxProfiles) throw new ArgumentException("Es sind schon zu viele Objektivprofile angelegt.");
                var copy = (JsonObject)p.DeepClone();
                var id = Guid.NewGuid().ToString("N")[..12];
                copy["id"] = id;
                var name = (copy["name"]?.GetValue<string>() ?? copy["lens"]?.GetValue<string>() ?? "").Trim();
                copy["name"] = Unique(s, name.Length == 0 ? "Objektiv" : name[..Math.Min(name.Length, 120)], null);
                foreach (var k in new[] { "distortion", "vignetting", "tca" }) if (copy[k] is not JsonArray) copy[k] = new JsonArray();
                s.Profiles.Add(copy);
                ids.Add(id);
            }
        });
        return ids;
    }

    private static string Unique(Store s, string name, string? self)
    {
        var n = name;
        for (var i = 2; s.Profiles.Any(p => IdOf(p) != self && string.Equals(NameOf(p), n, StringComparison.OrdinalIgnoreCase)); i++) n = $"{name} ({i})";
        return n;
    }

    public static void Rename(string id, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 120) throw new ArgumentException("Bitte einen Namen angeben.");
        Change(s =>
        {
            var p = s.Profiles.FirstOrDefault(x => IdOf(x) == id) ?? throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht.");
            p["name"] = Unique(s, name, id);
        }, recompute: false);
    }

    /// <summary>Profil löschen; Regeln dazu gehen mit. Rückgabe: Zahl der entfernten Regeln.</summary>
    public static int Delete(string id)
    {
        var removed = 0;
        Change(s =>
        {
            if (s.Profiles.RemoveAll(x => IdOf(x) == id) == 0) throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht.");
            removed = s.Rules.RemoveAll(r => r.Profile == id);
        });
        return removed;
    }

    /// <summary>Eingemessene Verzeichnung für eine Brennweite eintragen (ersetzt einen Eintrag ohne Blende bei derselben Brennweite).</summary>
    public static void SetDistortion(string id, double focal, double[] k)
    {
        if (focal is <= 0 or > 5000 || k.Length is 0 or > 3 || k.Any(x => !double.IsFinite(x) || Math.Abs(x) > 10)) throw new ArgumentException("Ungültige Werte.");
        Change(s =>
        {
            var p = s.Profiles.FirstOrDefault(x => IdOf(x) == id) ?? throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht.");
            PutDistortion(p, focal, k);
        });
    }

    /// <summary>Verzeichnung einer Brennweite in ein Profil schreiben (Werte schon geprüft).</summary>
    internal static void PutDistortion(JsonObject p, double focal, double[] k)
    {
        var list = p["distortion"] as JsonArray ?? [];
        p["distortion"] = list;
        foreach (var e in list.OfType<JsonObject>().Where(e => Math.Abs((e["focal"]?.GetValue<double>() ?? -1) - focal) < 0.05 && e["aperture"] == null).ToList())
            list.Remove(e);
        list.Add(new JsonObject { ["focal"] = Math.Round(focal, 2), ["k"] = new JsonArray([.. k.Concat(Enumerable.Repeat(0.0, 3 - k.Length)).Select(x => (JsonNode)JsonValue.Create(x))]) });
        var sorted = list.OfType<JsonObject>().OrderBy(e => e["focal"]?.GetValue<double>() ?? 0).Select(e => e.DeepClone()).ToList();
        list.Clear();
        foreach (var e in sorted) list.Add(e);
    }

    public static void SetRules(IEnumerable<LensRule> rules)
    {
        var list = rules.Take(MaxRules + 1).ToList();
        if (list.Count > MaxRules) throw new ArgumentException("Zu viele Regeln.");
        Change(s =>
        {
            var ids = s.Profiles.Select(IdOf).ToHashSet();
            s.Rules = Clean(list, id => ids.Contains(id)
                || LensLibrary.Exists(id) // [feature:lenslib]
                );
        });
    }

    /// <summary>Regeln prüfen und bereinigen: Profil vorhanden, Kamera oder Objektiv, gültiger Brennweitenbereich.</summary>
    internal static List<LensRule> Clean(List<LensRule> list, Func<string, bool> exists) => [.. list.Select(r =>
    {
        if (!exists(r.Profile)) throw new ArgumentException("Jede Regel braucht ein Objektivprofil.");
        if (string.IsNullOrWhiteSpace(r.Camera) && string.IsNullOrWhiteSpace(r.Lens)) throw new ArgumentException("Jede Regel braucht Kamera oder Objektiv.");
        var (lo, hi) = (r.FocalMin, r.FocalMax);
        if (lo is <= 0 || hi is <= 0 || lo > hi) throw new ArgumentException("Der Brennweitenbereich ist ungültig.");
        return r with
        {
            Id = r.Id is { Length: > 0 and <= 40 } ? r.Id : Guid.NewGuid().ToString("N")[..12],
            Camera = Cut(r.Camera), Lens = Cut(r.Lens),
        };
    })];

    private static string? Cut(string? s) => string.IsNullOrWhiteSpace(s) ? null : LensRule.Squeeze(s)[..Math.Min(LensRule.Squeeze(s).Length, 120)];

    /// <summary>Erste passende Regel für die Aufnahmedaten: eigene Regel der Person vor der Regel vom Admin.</summary>
    public static LensRule? Match(FileMeta.Row? meta, string? user = null)
    {
        if (meta == null) return null;
        if (Features.On(Features.LensLib) && LensOwn.Match(user, meta, AdminExists) is { } own) return own; // [feature:lenslib]
        lock (Gate) return _store.Rules.FirstOrDefault(r => r.Matches(meta.Camera, meta.Lens, meta.Focal) && (_store.Profiles.Any(p => IdOf(p) == r.Profile)
            || LensLibrary.Exists(r.Profile) // [feature:lenslib]
            ));
    }

    // [feature:lenslib]
    /// <summary>Automatisch gewähltes Profil: Kennung, Name, RAW/JPEG, Kamera, Grund (wie <see cref="LensLibrary.Hit"/>), Herkunft (own, calibrated, admin).</summary>
    public sealed record AutoHit(string Profile, string Name, bool? Raw, string? Camera, string Why, string Origin);

    /// <summary>
    /// Profil für ein Foto ohne passende Regel, nach dem Objektivnamen: eigene Profile der Person (eingemessen oder eingelesen), dann die
    /// aus ZIP-Dateien vom Admin, zuletzt die übrigen vom Admin. Regeln gehen immer vor.
    /// </summary>
    public static AutoHit? Library(FileMeta.Row? meta, string? user = null) => Match(meta, user) == null ? AutoProfile(meta, user) : null;

    /// <summary>Wie <see cref="Library"/>, ohne nach Regeln zu fragen.</summary>
    public static AutoHit? AutoProfile(FileMeta.Row? meta, string? user)
    {
        if (meta == null || !Features.On(Features.LensLib)) return null;
        if (LensOwn.Find(user, meta) is { } own) return own;
        if (LensLibrary.Find(meta) is { } hit)
            return new AutoHit(LensLibrary.Prefix + hit.Entry.Id, hit.Entry.Name, hit.Entry.Raw, hit.Entry.Camera ?? hit.Entry.Model, hit.Why, "admin");
        List<JsonObject> admin;
        lock (Gate) admin = _store.Profiles;
        return FindByName(admin, meta, _ => "admin");
    }

    /// <summary>Profil nach dem Objektivnamen (genau, sonst das Ende eines Namens bei mindestens 6 Zeichen); gleiche Kamera bevorzugt.</summary>
    internal static AutoHit? FindByName(IEnumerable<JsonObject> profiles, FileMeta.Row meta, Func<JsonObject, string> origin)
    {
        var key = LensLibrary.Norm(meta.Lens);
        if (key.Length < 3) return null;
        var cam = LensLibrary.Norm(meta.Camera);
        (int Score, JsonObject P, bool Exact)? best = null;
        foreach (var p in profiles)
        {
            var exact = false;
            var near = false;
            foreach (var n in new[] { p["lens"]?.GetValue<string>(), NameOf(p) }.Select(LensLibrary.Norm).Where(n => n.Length > 0))
            {
                if (n == key) exact = true;
                else if (n.Length >= 6 && key.Length >= 6 && (n.EndsWith(key, StringComparison.Ordinal) || key.EndsWith(n, StringComparison.Ordinal))) near = true;
            }
            if (!exact && !near) continue;
            var pc = LensLibrary.Norm(p["camera"]?.GetValue<string>());
            var sameCamera = pc.Length > 0 && cam.Length > 0 && (cam == pc || cam.EndsWith(pc, StringComparison.Ordinal));
            var score = (exact ? 10 : 0) + (sameCamera ? 5 : 0);
            if (best == null || score > best.Value.Score) best = (score, p, exact);
        }
        if (best is not { } b) return null;
        var why = b.Score % 10 >= 5 ? "camera" : b.Exact ? "lens" : "similar";
        return new AutoHit(IdOf(b.P) ?? "", NameOf(b.P), null, b.P["camera"]?.GetValue<string>(), why, origin(b.P));
    }

    /// <summary>Regeln mit einem Profil aus der Bibliothek entfernen (Bibliothek leeren). Rückgabe: Zahl der entfernten Regeln.</summary>
    public static int DropLibraryRules()
    {
        lock (Gate) if (!_store.Rules.Any(r => LensLibrary.IsLib(r.Profile))) return 0;
        var removed = 0;
        Change(s => removed = s.Rules.RemoveAll(r => LensLibrary.IsLib(r.Profile)));
        return removed;
    }
    // [/feature:lenslib]

    /// <summary>Einstellungen, wenn eine Regel passt (der Editor schaltet die Korrektur dann ein); sonst null.</summary>
    public static LensSettings? Auto(FileMeta.Row? meta, string? user = null) => Match(meta, user) != null
        || Library(meta, user) != null // [feature:lenslib]
        ? new LensSettings() : null;

    /// <summary>
    /// Block „lens“ für die Photo-Engine: Profil, Brennweite, Blende, Stärken, eigene Regler. null: nichts zu tun (aus, kein Profil, keine
    /// eigenen Regler).
    /// </summary>
    /// <param name="user">Person, deren eigene Profile und Regeln gelten (Besitzerin des Fotos)</param>
    public static JsonObject? Block(LensSettings? s, FileMeta.Row? meta, string? user = null)
    {
        if (s == null || s.Off) return null;
        s = s.Clamped();
        var profile = s.Profile switch
        {
            "-" => null,
            null => Match(meta, user) is { } rule ? Profile(rule.Profile, user)
                : AutoProfile(meta, user) is { } hit ? Profile(hit.Profile, user) // [feature:lenslib]
                : null,
            var id => Profile(id, user),
        };
        if (profile == null && Math.Abs(s.ManualDistortion) < 0.01 && Math.Abs(s.ManualVignetting) < 0.01) return null;
        profile?.Remove("id");
        return new JsonObject
        {
            ["profile"] = profile,
            ["focal"] = meta?.Focal,
            ["aperture"] = meta?.Aperture,
            ["distortion"] = s.Distortion,
            ["vignetting"] = s.Vignetting,
            ["tca"] = s.Tca,
            ["manual"] = new JsonObject { ["distortion"] = s.ManualDistortion, ["vignetting"] = s.ManualVignetting, ["midpoint"] = s.Midpoint },
        };
    }

    /// <summary>Aufnahmedaten aus den Bytes eines Fotos.</summary>
    public static FileMeta.Row? MetaOf(byte[] data)
    {
        try
        {
            var meta = FileMeta.Read("", new MemoryStream(data, writable: false), TimeZoneInfo.Utc);
            // Manche Programme schreiben die Brennweite nicht als Bruch: dann als Zahl lesen
            if (meta is { Focal: null }) meta = meta with { Focal = FocalNumber(data) };
            // Für die Bibliothek: RAW oder JPEG nach dem Inhalt (der Weg fehlt hier)
            if (meta != null && LensLibrary.LooksRaw(data)) meta = meta with { Path = "photo.dng" }; // [feature:lenslib]
            return meta;
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static double? FocalNumber(byte[] data)
    {
        try
        {
            var exif = MetadataExtractor.ImageMetadataReader.ReadMetadata(new MemoryStream(data, writable: false))
                .OfType<MetadataExtractor.Formats.Exif.ExifSubIfdDirectory>().FirstOrDefault();
            double? f = exif?.GetObject(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagFocalLength) switch
            {
                MetadataExtractor.Rational r when r.Denominator != 0 => r.ToDouble(),
                IConvertible c => c.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
            return f is > 0 and < 5000 ? f : null;
        }
        catch (Exception e) when (e is IOException or MetadataExtractor.ImageProcessingException or FormatException or InvalidCastException) { return null; }
    }

    // Aufnahmedaten für die Vorschau im Editor: je Benutzer und Foto 15 Minuten gemerkt (nicht bei jedem Schieben das Original lesen)
    private static readonly ConcurrentDictionary<string, (DateTime At, FileMeta.Row? Meta)> MetaCache = new();

    public static async Task<FileMeta.Row?> MetaAsync(string key, Func<Task<byte[]>> bytes)
    {
        if (MetaCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(15)) return hit.Meta;
        var meta = MetaOf(await bytes());
        MetaCache[key] = (DateTime.UtcNow, meta);
        foreach (var old in MetaCache.OrderByDescending(x => x.Value.At).Skip(64).Select(x => x.Key).ToList()) MetaCache.TryRemove(old, out _);
        return meta;
    }

    // ---------------- Einmessen: erkannte Fotos kurz merken (Vorher/Nachher) ----------------

    private sealed record Shot(string User, DateTime At, byte[] Small, double? Focal, string Name);
    private static readonly ConcurrentDictionary<string, Shot> Shots = new();

    private static string Remember(string user, byte[] small, double? focal, string name)
    {
        var token = Guid.NewGuid().ToString("N");
        Shots[token] = new Shot(user, DateTime.UtcNow, small, focal, name);
        foreach (var old in Shots.Where(x => DateTime.UtcNow - x.Value.At > TimeSpan.FromMinutes(30)).Select(x => x.Key)
                     .Concat(Shots.Where(x => x.Value.User == user).OrderByDescending(x => x.Value.At).Skip(30).Select(x => x.Key)).ToList())
            Shots.TryRemove(old, out _);
        return token;
    }

    private static Shot? ShotOf(string user, string token) => Shots.TryGetValue(token, out var s) && s.User == user ? s : null;

    // ---------------- API ----------------

    public record ProfileBody(string Name);
    public record RulesBody(List<LensRule> Rules);
    public record AutoBody(bool On);
    public record FitBody(List<JsonObject> Images, int Terms = 2);
    // [feature:lenslib]
    public record SaveFitBody(string Profile, double Focal, double[] K, string? Lens = null, string? Camera = null);
    public record IdsBody(List<string> Ids);
    // [feature:lenslib:else]
    // public record SaveFitBody(string Profile, double Focal, double[] K);
    // [/feature:lenslib]
    public record PreviewBody(string Token, double[] K, double? Focal);

    private static object Summary(JsonObject p)
    {
        double[] Focals(string key) => [.. (p[key] as JsonArray ?? []).OfType<JsonObject>().Select(e => e["focal"]?.GetValue<double>() ?? 0).Distinct().Order()];
        return new
        {
            id = IdOf(p), name = NameOf(p), lens = p["lens"]?.GetValue<string>(), camera = p["camera"]?.GetValue<string>(), source = p["source"]?.GetValue<string>(),
            focals = Focals("distortion"), vignetting = Focals("vignetting").Length > 0, tca = Focals("tca").Length > 0,
        };
    }

    private static void Admin(HttpContext http)
    {
        if (!http.Space().User.IsAdmin) throw new UnauthorizedAccessException("Objektivprofile und Regeln kann nur ein Admin ändern.");
    }

    /// <summary>Einmessen: mit eigenen Profilen jede Person für sich, sonst nur Admins.</summary>
    private static void Calibrator(HttpContext http)
    {
        if (Features.On(Features.LensLib)) return; // [feature:lenslib]
        Admin(http);
    }

    private static async Task<byte[]> BodyAsync(HttpContext http, long max, CancellationToken ct)
    {
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = max;
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, ct);
        if (ms.Length == 0) throw new ArgumentException("Die Datei ist leer.");
        return ms.ToArray();
    }

    private const string NoEngine = "Dafür wird die Photo-Engine gebraucht - sie ist ausgeschaltet oder antwortet gerade nicht.";

    private static async Task<IResult> DetectAsync(HttpContext http, byte[] data, string name, CancellationToken ct)
    {
        var meta = MetaOf(data);
        var (status, json) = await PhotoEngine.CallAsync("/lens/corners?cols=15&rows=8", data, "application/octet-stream", 120, ct);

        if (status == 0) return Results.Json(new { error = NoEngine }, statusCode: 503);
        if (status != 200 || json == null) return Results.BadRequest(new { error = "Das Foto ließ sich nicht lesen." });
        var found = json["found"]?.GetValue<bool>() == true;
        string? token = null;
        if (found)
        {
            // Für Vorher/Nachher eine verkleinerte, aufrechte Fassung merken (RAW: die Engine entwickelt)
            byte[]? small = Thumbs.IsRaw(name) ? await PhotoEngine.DevelopAsync(data, new PhotoAdjust.Values(), 1600, 88, null, ct) : null;
            if (small == null)
            {
                try { using var bmp = PhotoAdjust.Small(data, 1600); small = PhotoAdjust.Jpeg(bmp, 88); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException) { small = null; }
            }
            if (small != null) token = Remember(http.Space().User.Id, small, meta?.Focal, Path.GetFileName(name));
        }
        return Results.Ok(new { found, token, focal = meta?.Focal, camera = meta?.Camera, lens = meta?.Lens, corners = json });
    }

    // [feature:lenslib]
    /// <summary>Anzeigenamen der Profile (eigene, vom Admin, aus ZIP-Dateien) nach Kennung - für die Listen der Regeln.</summary>
    internal static Dictionary<string, string?> Names(string? user, IEnumerable<string> ids) => ids.Distinct().ToDictionary(id => id, id =>
        LensLibrary.IsLib(id) ? LensLibrary.Entry(id)?.Label : Profile(id, user) is { } p ? NameOf(p) : null);

    /// <summary>Eine Zeile der Profilliste: eigene, eingemessene und alle vom Admin (serverweit oder aus einer ZIP-Datei).</summary>
    public sealed record ListRow(string Id, string Name, string? Maker, string? Camera, string? Lens, string? LensId, bool? Raw, double? Crop,
        double? FocalMin, double? FocalMax, double? ApertureMin, double? ApertureMax, string? Source, string? File, bool Vignetting, bool Tca, string Origin);

    private static ListRow RowOf(JsonObject p, string origin)
    {
        double[] Focals(string key) => [.. (p[key] as JsonArray ?? []).OfType<JsonObject>().Select(e => e["focal"]?.GetValue<double>() ?? 0).Distinct().Order()];
        var f = Focals("distortion").Concat(Focals("vignetting")).Concat(Focals("tca")).Distinct().Order().ToArray();
        return new ListRow(IdOf(p) ?? "", NameOf(p), null, p["camera"]?.GetValue<string>(), p["lens"]?.GetValue<string>(), null, null, null,
            f.Length > 0 ? f[0] : null, f.Length > 0 ? f[^1] : null, null, null, SourceOf(p), null, Focals("vignetting").Length > 0, Focals("tca").Length > 0, origin);
    }

    private static ListRow RowOf(LensLibEntry e) => new(LensLibrary.Prefix + e.Id, e.Name, e.Maker, e.Camera ?? e.Model, e.Lens, e.LensId, e.Raw, e.Crop,
        e.FocalMin, e.FocalMax, e.ApertureMin, e.ApertureMax, "zip", e.Source, e.Vignetting, e.Tca, "admin");

    /// <summary>Ergebnis der Suche: alle Profile (Zahl), passende (Zahl), die ersten passenden, Hersteller mit Anzahl.</summary>
    public sealed record ListResult(int Count, int Total, List<ListRow> Items, List<(string Maker, int Count)> Makers);

    /// <summary>
    /// Profile, die für eine Person gelten: eigene (auch eingemessene) und alle vom Admin; <paramref name="adminOnly"/>: nur die vom Admin
    /// (Verwaltung). Suche nach Wörtern, Hersteller, Art (raw/jpeg), Herkunft (own, calibrated, admin).
    /// </summary>
    public static ListResult List(string? user, string? q, string? maker, string? kind, string? origin, int take, bool adminOnly = false)
    {
        var words = (q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(LensLibrary.Norm).Where(w => w.Length > 0).ToArray();
        var local = new List<ListRow>();
        if (!adminOnly) local.AddRange(LensOwn.Profiles(user).Select(p => RowOf(p, SourceOf(p) == "calibrated" ? "calibrated" : "own")));
        local.AddRange(Profiles().Select(p => RowOf(p, "admin")));
        string Hay(ListRow r) => string.Join(' ', new[] { r.Name, r.Lens, r.Camera, r.Maker, r.LensId, r.File }.Select(LensLibrary.Norm));
        var hits = local.Where(r => (string.IsNullOrEmpty(origin) || r.Origin == origin) && string.IsNullOrEmpty(maker)
            && (kind is not ("raw" or "jpeg") || r.Raw == (kind == "raw")) && words.All(w => Hay(r).Contains(w, StringComparison.Ordinal))).ToList();
        var lib = string.IsNullOrEmpty(origin) || origin == "admin" ? LensLibrary.Search(q, maker, kind, take) : (Total: 0, Items: new List<LensLibEntry>());
        var items = hits.Concat(lib.Items.Select(RowOf))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Origin == "admin" ? 1 : 0).ThenBy(r => r.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(take, 1, 5000)).ToList();
        return new ListResult(local.Count + LensLibrary.Count, hits.Count + lib.Total, items, LensLibrary.Makers());
    }

    /// <summary>
    /// Profile löschen: eigene darf jede Person, die vom Admin (serverweit oder aus einer ZIP-Datei) nur ein Admin. Regeln mit diesen
    /// Profilen gehen mit (eigene bei eigenen Profilen, Admin-Regeln bei Profilen vom Admin). Rückgabe: Zahl der entfernten Regeln.
    /// </summary>
    public static int DeleteProfiles(string user, bool isAdmin, IEnumerable<string> ids)
    {
        var list = ids.Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
        if (list.Count == 0) return 0;
        var own = list.Where(LensOwn.IsOwn).ToList();
        var other = list.Where(i => !LensOwn.IsOwn(i)).ToList();
        if (other.Count > 0 && !isAdmin) throw new UnauthorizedAccessException("Profile vom Admin kann nur ein Admin löschen.");
        var rules = 0;
        if (own.Count > 0) rules += LensOwn.Delete(user, own);
        var lib = other.Where(LensLibrary.IsLib).ToList();
        if (lib.Count > 0)
        {
            LensLibrary.Remove(lib);
            var set = lib.ToHashSet();
            lock (Gate) if (_store.Rules.Any(r => set.Contains(r.Profile))) Change(s => rules += s.Rules.RemoveAll(r => set.Contains(r.Profile)));
        }
        foreach (var id in other.Where(i => !LensLibrary.IsLib(i))) rules += Delete(id);
        return rules;
    }

    /// <summary>Umbenennen: eigene Profile jede Person, serverweite nur ein Admin; Profile aus ZIP-Dateien tragen den Namen aus der Datei.</summary>
    public static void RenameProfile(string user, bool isAdmin, string id, string name)
    {
        if (LensOwn.IsOwn(id)) { LensOwn.Rename(user, id, name); return; }
        if (!isAdmin) throw new UnauthorizedAccessException("Profile vom Admin kann nur ein Admin umbenennen.");
        if (LensLibrary.IsLib(id)) throw new ArgumentException("Profile aus einer ZIP-Datei behalten den Namen aus der Datei.");
        Rename(id, name);
    }

    /// <summary>Eingemessene Verzeichnung speichern: im eigenen Profil, in einem serverweiten nur als Admin.</summary>
    public static void SaveDistortion(string user, bool isAdmin, SaveFitBody body)
    {
        if (LensOwn.IsOwn(body.Profile)) { LensOwn.SetDistortion(user, body.Profile, body.Focal, body.K, body.Lens, body.Camera); return; }
        if (!isAdmin) throw new UnauthorizedAccessException("Profile vom Admin kann nur ein Admin ändern.");
        SetDistortion(body.Profile, body.Focal, body.K);
    }

    /// <summary>Profile aus einer Datei, von der Engine gelesen: nur die mit Werten.</summary>
    private static async Task<(IResult? Error, List<JsonObject> Found)> ReadImportAsync(HttpContext http, CancellationToken ct)
    {
        var data = await BodyAsync(http, 8L * 1024 * 1024, ct);
        var (status, json) = await PhotoEngine.CallAsync("/lens/import", data, "application/octet-stream", 60, ct);
        if (status == 0) return (Results.Json(new { error = NoEngine }, statusCode: 503), []);
        if (status == 400) return (Results.BadRequest(new { error = "Die Datei enthält kein lesbares Objektivprofil." }), []);
        if (status != 200 || json?["profiles"] is not JsonArray arr) return (Results.Json(new { error = NoEngine }, statusCode: 503), []);
        var found = arr.OfType<JsonObject>().Where(p => (p["distortion"] as JsonArray)?.Count > 0 || (p["vignetting"] as JsonArray)?.Count > 0 || (p["tca"] as JsonArray)?.Count > 0).ToList();
        return found.Count == 0 ? (Results.BadRequest(new { error = "Die Datei enthält kein lesbares Objektivprofil." }), []) : (null, found);
    }

    /// <summary>Person, deren eigene Profile für ein Foto gelten: die Besitzerin der Bearbeitung (geteilte Fotos: wem sie gehören).</summary>
    private static string LensUser(HttpContext http, ServerConfig config, string path) => FilesApi.EditOwner(http, config, path);

    private static void MapOwn(RouteGroupBuilder api)
    {
        api.MapGet("/list", (string? q, string? maker, string? kind, string? origin, string? scope, int? take, HttpContext http) =>
        {
            var admin = scope == "admin";
            if (admin) Admin(http);
            var r = List(http.Space().User.Id, q, maker, kind, origin, take ?? 2000, admin);
            return Results.Ok(new { count = r.Count, total = r.Total, items = r.Items, makers = r.Makers.Select(m => new { name = m.Maker, n = m.Count }), running = LensLibrary.Running });
        });
        api.MapGet("/item", (string id, HttpContext http) =>
        {
            var user = http.Space().User.Id;
            if (LensLibrary.Entry(id) is { } e) return Results.Ok(RowOf(e));
            if (LensOwn.Profile(user, id) is { } own) return Results.Ok(RowOf(own, SourceOf(own) == "calibrated" ? "calibrated" : "own"));
            if (!LensOwn.IsOwn(id) && Profile(id) is { } p) return Results.Ok(RowOf(p, "admin"));
            throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht.");
        });
        api.MapPost("/admin/delete", (IdsBody body, HttpContext http) =>
        {
            Admin(http);
            if (body.Ids.Any(LensOwn.IsOwn)) throw new ArgumentException("Hier lassen sich nur Profile vom Admin löschen.");
            return Results.Ok(new { rules = DeleteProfiles(http.Space().User.Id, true, body.Ids) });
        });
        api.MapPut("/admin/rules", (RulesBody body, HttpContext http) =>
        {
            Admin(http);
            SetRules(body.Rules ?? []);
            var rules = Rules();
            return Results.Ok(new { rules, names = Names(null, rules.Select(r => r.Profile)) });
        });
        // Einzelne Dateien als Profile vom Admin (Verwaltung)
        api.MapPut("/admin/import", async (HttpContext http, CancellationToken ct) =>
        {
            Admin(http);
            var (error, found) = await ReadImportAsync(http, ct);
            if (error != null) return error;
            var ids = Add(found);
            return Results.Ok(new { ids, names = ids.Select(i => Profile(i) is { } np ? NameOf(np) : "") });
        });
    }
    // [/feature:lenslib]

    public static void Map(WebApplication app, DataPaths paths)
    {
        Init(paths.Root);
        var api = app.MapGroup("/api/gallery/lens").RequireAuthorization();

        // [feature:lenslib]
        var spaces = app.Services.GetRequiredService<Spaces>();
        LensOwn.Use(id =>
        {
            try { return spaces.Get(id).Db; }
            catch (Exception e) when (e is KeyNotFoundException or UpdatePendingException) { return null; }
        });
        api.MapGet("/", (HttpContext http) =>
        {
            var user = http.Space().User.Id;
            if (!Features.On(Features.LensLib)) return Results.Ok(new
            {
                profiles = Profiles().Select(Summary), rules = Rules(), autoNew = LensApply.AutoNewFor(http.Space()), admin = http.Space().User.IsAdmin, engine = PhotoEngine.Configured,
            });
            var own = LensOwn.Profiles(user);
            var rules = LensOwn.Rules(user);
            var adminRules = Rules();
            return Results.Ok(new
            {
                profiles = own.Select(Summary), rules, adminRules,
                names = Names(user, rules.Concat(adminRules).Select(r => r.Profile)),
                count = own.Count + Profiles().Count + LensLibrary.Count,
                autoNew = LensApply.AutoNewFor(http.Space()),
                admin = http.Space().User.IsAdmin,
                engine = PhotoEngine.Configured,
            });
        });
        // [feature:lenslib:else]
        // api.MapGet("/", (HttpContext http) => Results.Ok(new
        // {
        //     profiles = Profiles().Select(Summary),
        //     rules = Rules(),
        //     autoNew = LensApply.AutoNewFor(http.Space()),
        //     admin = http.Space().User.IsAdmin,
        //     engine = PhotoEngine.Configured,
        // }));
        // [/feature:lenslib]

        // [feature:lenslib]
        // Neues Profil (zum Einmessen): eigenes Profil der Person
        api.MapPost("/profiles", (ProfileBody body, HttpContext http) =>
        {
            var name = body.Name?.Trim() ?? "";
            if (name.Length is 0 or > 120) throw new ArgumentException("Bitte einen Namen angeben.");
            var profile = new JsonObject { ["name"] = name, ["source"] = "calibrated" };
            if (!Features.On(Features.LensLib)) { Admin(http); return Results.Ok(new { id = Add([profile])[0] }); }
            return Results.Ok(new { id = LensOwn.Add(http.Space().User.Id, [profile])[0] });
        });
        api.MapPut("/profiles/{id}", (string id, ProfileBody body, HttpContext http) =>
        {
            if (!Features.On(Features.LensLib)) { Admin(http); Rename(id, body.Name ?? ""); return Results.Ok(); }
            RenameProfile(http.Space().User.Id, http.Space().User.IsAdmin, id, body.Name ?? "");
            return Results.Ok();
        });
        api.MapDelete("/profiles/{id}", (string id, HttpContext http) =>
        {
            if (!Features.On(Features.LensLib)) { Admin(http); return Results.Ok(new { rules = Delete(id) }); }
            return Results.Ok(new { rules = DeleteProfiles(http.Space().User.Id, http.Space().User.IsAdmin, [id]) });
        });
        // [feature:lenslib:else]
        // api.MapPost("/profiles", (ProfileBody body, HttpContext http) =>
        // {
        //     Admin(http);
        //     var name = body.Name?.Trim() ?? "";
        //     if (name.Length is 0 or > 120) throw new ArgumentException("Bitte einen Namen angeben.");
        //     var id = Add([new JsonObject { ["name"] = name, ["source"] = "calibrated" }])[0];
        //     return Results.Ok(new { id });
        // });
        // api.MapPut("/profiles/{id}", (string id, ProfileBody body, HttpContext http) => { Admin(http); Rename(id, body.Name ?? ""); return Results.Ok(); });
        // api.MapDelete("/profiles/{id}", (string id, HttpContext http) => { Admin(http); return Results.Ok(new { rules = Delete(id) }); });
        // [/feature:lenslib]

        // Export: ein Profil oder alle, als JSON-Datei (zum Wiedereinlesen hier oder auf einem anderen dmOffice)
        api.MapGet("/export", (string? id, HttpContext http) =>
        {
            // [feature:lenslib]
            // Ohne Kennung: die eigenen Profile; mit Kennung jedes, das für die Person gilt
            var user = http.Space().User.Id;
            var list = id == null ? (Features.On(Features.LensLib) ? LensOwn.Profiles(user) : Profiles())
                : Profile(id, user) is { } p ? [p] : throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht.");
            // [feature:lenslib:else]
            // var list = id == null ? Profiles() : Profile(id) is { } p ? [p] : throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht.");
            // [/feature:lenslib]
            foreach (var item in list) item.Remove("id");
            var name = list.Count == 1 ? NameOf(list[0]) : "objektivprofile";
            var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["profiles"] = new JsonArray([.. list]) }, new JsonSerializerOptions { WriteIndented = true }),
                "application/json", safe + ".json");
        });

        // Import: eigene JSON-Datei, .lcp-Profil oder lensfun-XML - die Engine liest und prüft; eine Datei kann mehrere Profile enthalten
        // [feature:lenslib]
        // Mit eigenen Profilen: in die eigenen Profile der Person
        api.MapPut("/import", async (HttpContext http, CancellationToken ct) =>
        {
            var lib = Features.On(Features.LensLib);
            if (!lib) Admin(http);
            var (error, found) = await ReadImportAsync(http, ct);
            if (error != null) return error;
            var user = http.Space().User.Id;
            var ids = lib ? LensOwn.Add(user, found) : Add(found);
            return Results.Ok(new { ids, names = ids.Select(i => Profile(i, user) is { } np ? NameOf(np) : "") });
        });
        // [feature:lenslib:else]
        // api.MapPut("/import", async (HttpContext http, CancellationToken ct) =>
        // {
        //     Admin(http);
        //     var data = await BodyAsync(http, 8L * 1024 * 1024, ct);
        //     var (status, json) = await PhotoEngine.CallAsync("/lens/import", data, "application/octet-stream", 60, ct);
        //     if (status == 0) return Results.Json(new { error = NoEngine }, statusCode: 503);
        //     if (status == 400) return Results.BadRequest(new { error = "Die Datei enthält kein lesbares Objektivprofil." });
        //     if (status != 200 || json?["profiles"] is not JsonArray arr) return Results.Json(new { error = NoEngine }, statusCode: 503);
        //     var found = arr.OfType<JsonObject>().Where(p => (p["distortion"] as JsonArray)?.Count > 0 || (p["vignetting"] as JsonArray)?.Count > 0 || (p["tca"] as JsonArray)?.Count > 0).ToList();
        //     if (found.Count == 0) return Results.BadRequest(new { error = "Die Datei enthält kein lesbares Objektivprofil." });
        //     var ids = Add(found);
        //     return Results.Ok(new { ids, names = ids.Select(i => Profile(i) is { } np ? NameOf(np) : "") });
        // });
        // [/feature:lenslib]

        // [feature:lenslib]
        // Regeln: mit eigenen Profilen die eigenen Regeln der Person (Admin-Regeln: /admin/rules)
        api.MapPut("/rules", (RulesBody body, HttpContext http) =>
        {
            if (!Features.On(Features.LensLib)) { Admin(http); SetRules(body.Rules ?? []); return Results.Ok(new { rules = Rules() }); }
            var user = http.Space().User.Id;
            LensOwn.SetRules(user, body.Rules ?? [], AdminExists);
            var rules = LensOwn.Rules(user);
            return Results.Ok(new { rules, names = Names(user, rules.Select(r => r.Profile)) });
        });
        // [feature:lenslib:else]
        // api.MapPut("/rules", (RulesBody body, HttpContext http) => { Admin(http); SetRules(body.Rules ?? []); return Results.Ok(new { rules = Rules() }); });
        // [/feature:lenslib]
        // „Neue Fotos gleich korrigieren“: jede Person für ihre eigenen Fotos
        api.MapPut("/auto", (AutoBody body, HttpContext http) => { http.Space().UpdateSettings(s => s.LensAutoNew = body.On); return Results.Ok(); });
        LensApply.Map(api, app);
        LensLibrary.Map(api, app, paths.Root); // [feature:lenslib]
        MapOwn(api); // [feature:lenslib]

        // Editor: Aufnahmedaten, passende Regel, Profile zur Auswahl
        api.MapGet("/for", async (string path, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var meta = await MetaAsync(http.Space().User.Id + "|" + path, () => FilesApi.PhotoBytesAsync(http, config, path, ct));
            // [feature:lenslib]
            // Eigene Profile und Regeln der Besitzerin des Fotos
            var user = Features.On(Features.LensLib) ? LensUser(http, config, path) : null;
            var rule = Match(meta, user);
            var hit = rule == null ? AutoProfile(meta, user) : null;
            var ownRule = rule != null && LensOwn.Rules(user).Any(r => r.Id == rule.Id);
            return Results.Ok(new
            {
                camera = meta?.Camera, lens = meta?.Lens, focal = meta?.Focal, aperture = meta?.Aperture,
                rule = rule == null ? null : new { rule.Id, rule.Profile, name = Profile(rule.Profile, user) is { } rp ? NameOf(rp) : LensLibrary.Entry(rule.Profile)?.Label, own = ownRule },
                library = hit == null ? null : new { profile = hit.Profile, name = hit.Name, raw = hit.Raw, camera = hit.Camera, why = hit.Why, origin = hit.Origin },
                raw = meta != null && Thumbs.IsRaw(meta.Path),
                profiles = LensOwn.Profiles(user).Concat(Profiles()).Select(p => new { id = IdOf(p), name = NameOf(p) }).OrderBy(p => p.name, StringComparer.CurrentCultureIgnoreCase),
                engine = PhotoEngine.Configured,
            });
            // [feature:lenslib:else]
            // var rule = Match(meta);
            // return Results.Ok(new
            // {
            //     camera = meta?.Camera, lens = meta?.Lens, focal = meta?.Focal, aperture = meta?.Aperture,
            //     rule = rule == null ? null : new { rule.Id, rule.Profile, name = Profile(rule.Profile) is { } rp ? NameOf(rp) : null },
            //     profiles = Profiles().Select(p => new { id = IdOf(p), name = NameOf(p) }).OrderBy(p => p.name, StringComparer.CurrentCultureIgnoreCase),
            //     engine = PhotoEngine.Configured,
            // });
            // [/feature:lenslib]
        });

        // Einmessen 1: Schachbrett in einem Foto finden - aus der Galerie (POST ?path=) oder hochgeladen (PUT, Body, ?name=)
        api.MapPost("/calibrate/detect", async (string path, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            Calibrator(http);
            return await DetectAsync(http, await FilesApi.PhotoBytesAsync(http, config, path, ct), path, ct);
        });
        api.MapPut("/calibrate/detect", async (string? name, HttpContext http, CancellationToken ct) =>
        {
            Calibrator(http);
            return await DetectAsync(http, await BodyAsync(http, 200L * 1024 * 1024, ct), name ?? "", ct);
        });
        // Einmessen 2: Verzeichnung aus den Ecken mehrerer Fotos einer Brennweite
        api.MapPost("/calibrate/fit", async (FitBody body, HttpContext http, CancellationToken ct) =>
        {
            Calibrator(http);
            var req = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["images"] = new JsonArray([.. body.Images.Take(20).Select(i => i.DeepClone())]), ["terms"] = Math.Clamp(body.Terms, 1, 3) });
            var (status, json) = await PhotoEngine.CallAsync("/lens/fit", req, "application/json", 120, ct);
            if (status == 0) return Results.Json(new { error = NoEngine }, statusCode: 503);
            if (status != 200 || json == null) return Results.BadRequest(new { error = "Aus diesen Fotos ließ sich die Verzeichnung nicht bestimmen." });
            return Results.Text(json.ToJsonString(), "application/json");
        });
        // Vorher/Nachher eines erkannten Fotos: ohne k das Foto, mit k korrigiert (wie später in der Galerie)
        api.MapPost("/calibrate/preview", async (PreviewBody body, HttpContext http, CancellationToken ct) =>
        {
            Calibrator(http);
            var shot = ShotOf(http.Space().User.Id, body.Token) ?? throw new KeyNotFoundException("Das Foto ist nicht mehr da - bitte noch einmal erkennen lassen.");
            if (body.K is not { Length: > 0 and <= 3 }) return Results.File(shot.Small, "image/jpeg");
            var focal = body.Focal ?? shot.Focal ?? 50;
            var lens = new JsonObject
            {
                ["profile"] = new JsonObject { ["distortion"] = new JsonArray(new JsonObject { ["focal"] = focal, ["k"] = new JsonArray([.. body.K.Select(x => (JsonNode)JsonValue.Create(x))]) }) },
                ["focal"] = focal,
            };
            var dev = await PhotoEngine.DevelopAsync(shot.Small, new PhotoAdjust.Values(), null, 88, null, ct, lens);
            return dev == null ? Results.Json(new { error = NoEngine }, statusCode: 503) : Results.File(dev, "image/jpeg");
        });

        api.MapPost("/calibrate/save", (SaveFitBody body, HttpContext http) =>
        {
            // [feature:lenslib]
            if (Features.On(Features.LensLib)) { SaveDistortion(http.Space().User.Id, http.Space().User.IsAdmin, body); return Results.Ok(); }
            // [/feature:lenslib]
            Admin(http);
            SetDistortion(body.Profile, body.Focal, body.K);
            return Results.Ok();
        });

        // Schachbrett als Bild (zum Abfotografieren am Bildschirm oder Ausdrucken)
        api.MapGet("/checkerboard.svg", () =>
        {
            const int cols = 16, rows = 9, sq = 100;
            var sb = new System.Text.StringBuilder();
            sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {(cols + 2) * sq} {(rows + 2) * sq}' shape-rendering='crispEdges'><rect width='100%' height='100%' fill='#fff'/>");
            for (var y = 0; y < rows; y++) for (var x = 0; x < cols; x++) if ((x + y) % 2 == 0) sb.Append($"<rect x='{(x + 1) * sq}' y='{(y + 1) * sq}' width='{sq}' height='{sq}'/>");
            sb.Append("</svg>");
            return Results.Text(sb.ToString(), "image/svg+xml");
        });
    }
}
