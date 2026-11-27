using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Gallery;

/// <summary>
/// Eigene Objektivprofile und Regeln einer Person (Kennung lenslib): in ihrer Datenbank unter „lens.own“, Kennungen mit „own:“ vorn.
/// Sie gelten für die eigenen Fotos (bei geteilten: für die der Besitzerin) vor den Profilen und Regeln vom Admin
/// (<see cref="LensProfiles"/>, <see cref="LensLibrary"/>). Eingemessene Profile sind eigene Profile mit Herkunft „calibrated“.
/// </summary>
public static class LensOwn
{
    public const string Prefix = "own:";
    public const int MaxProfiles = 500, MaxRules = 200;
    private const string Key = "lens.own";

    public sealed class Store
    {
        public List<JsonObject> Profiles { get; set; } = [];
        public List<LensRule> Rules { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Func<string, UserDb?> _db = _ => null;
    private static readonly ConcurrentDictionary<string, Store> Cache = new();
    private static readonly Lock Gate = new();

    /// <summary>Datenbank einer Person nach Kennung (Server: über Spaces; Tests: eigene).</summary>
    public static void Use(Func<string, UserDb?> db) { _db = db; Cache.Clear(); }

    public static bool IsOwn(string? id) => id != null && id.StartsWith(Prefix, StringComparison.Ordinal);

    private static Store Load(string? user)
    {
        if (string.IsNullOrEmpty(user) || !Features.On(Features.LensLib)) return new();
        if (Cache.TryGetValue(user, out var hit)) return hit;
        Store store;
        try { store = _db(user) is { } db ? db.GetJson<Store>(Key) : new(); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { store = new(); }
        if (Cache.Count > 500) Cache.Clear();
        Cache[user] = store;
        return store;
    }

    private static void Change(string user, Action<Store> change, bool recompute = true)
    {
        var db = _db(user) ?? throw new InvalidOperationException("Die eigenen Objektivprofile lassen sich gerade nicht speichern.");
        lock (Gate)
        {
            var copy = JsonSerializer.Deserialize<Store>(JsonSerializer.Serialize(Load(user), Json), Json)!;
            change(copy);
            db.SetJson(Key, copy);
            Cache[user] = copy;
        }
        // Vorschaubilder mit Objektivkorrektur entstehen neu (gemeinsamer Stand in LensProfiles)
        if (recompute) LensProfiles.Touch();
    }

    public static List<JsonObject> Profiles(string? user) => [.. Load(user).Profiles.Select(p => (JsonObject)p.DeepClone())];
    public static List<LensRule> Rules(string? user) => [.. Load(user).Rules];

    public static JsonObject? Profile(string? user, string id) =>
        IsOwn(id) ? Load(user).Profiles.FirstOrDefault(p => LensProfiles.IdOf(p) == id)?.DeepClone() as JsonObject : null;

    public static bool Exists(string? user, string id) => IsOwn(id) && Load(user).Profiles.Any(p => LensProfiles.IdOf(p) == id);

    private static string Unique(Store s, string name, string? self)
    {
        var n = name;
        for (var i = 2; s.Profiles.Any(p => LensProfiles.IdOf(p) != self && string.Equals(LensProfiles.NameOf(p), n, StringComparison.OrdinalIgnoreCase)); i++) n = $"{name} ({i})";
        return n;
    }

    /// <summary>Profile aufnehmen (eingelesen oder zum Einmessen angelegt). Rückgabe: neue Kennungen.</summary>
    public static List<string> Add(string user, IEnumerable<JsonObject> profiles)
    {
        var ids = new List<string>();
        Change(user, s =>
        {
            foreach (var p in profiles)
            {
                if (s.Profiles.Count >= MaxProfiles) throw new ArgumentException("Es sind schon zu viele Objektivprofile angelegt.");
                var copy = (JsonObject)p.DeepClone();
                var id = Prefix + Guid.NewGuid().ToString("N")[..12];
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

    public static void Rename(string user, string id, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 120) throw new ArgumentException("Bitte einen Namen angeben.");
        Change(user, s =>
        {
            var p = s.Profiles.FirstOrDefault(x => LensProfiles.IdOf(x) == id) ?? throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht.");
            p["name"] = Unique(s, name, id);
        }, recompute: false);
    }

    /// <summary>Eigenes Profil löschen; eigene Regeln dazu gehen mit. Rückgabe: Zahl der entfernten Regeln.</summary>
    public static int Delete(string user, IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        var removed = 0;
        Change(user, s =>
        {
            if (s.Profiles.RemoveAll(x => set.Contains(LensProfiles.IdOf(x) ?? "")) == 0) throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht.");
            removed = s.Rules.RemoveAll(r => set.Contains(r.Profile));
        });
        return removed;
    }

    /// <summary>Eingemessene Verzeichnung für eine Brennweite eintragen (ersetzt einen Eintrag ohne Blende bei derselben Brennweite).</summary>
    public static void SetDistortion(string user, string id, double focal, double[] k, string? lens = null, string? camera = null)
    {
        if (focal is <= 0 or > 5000 || k.Length is 0 or > 3 || k.Any(x => !double.IsFinite(x) || Math.Abs(x) > 10)) throw new ArgumentException("Ungültige Werte.");
        Change(user, s =>
        {
            var p = s.Profiles.FirstOrDefault(x => LensProfiles.IdOf(x) == id) ?? throw new KeyNotFoundException("Dieses Objektivprofil gibt es nicht.");
            // Objektiv und Kamera aus dem Foto merken: so findet die automatische Zuordnung das Profil auch ohne Regel
            if (p["lens"] == null && !string.IsNullOrWhiteSpace(lens)) p["lens"] = lens.Trim();
            if (p["camera"] == null && !string.IsNullOrWhiteSpace(camera)) p["camera"] = camera.Trim();
            LensProfiles.PutDistortion(p, focal, k);
        });
    }

    /// <param name="exists">Gibt es das Profil (eigenes, vom Admin)?</param>
    public static void SetRules(string user, IEnumerable<LensRule> rules, Func<string, bool> exists)
    {
        var list = rules.Take(MaxRules + 1).ToList();
        if (list.Count > MaxRules) throw new ArgumentException("Zu viele Regeln.");
        var clean = LensProfiles.Clean(list, id => Exists(user, id) || exists(id));
        Change(user, s => s.Rules = clean);
    }

    /// <summary>Erste passende eigene Regel, deren Profil es noch gibt.</summary>
    public static LensRule? Match(string? user, FileMeta.Row? meta, Func<string, bool> exists)
    {
        if (meta == null) return null;
        return Load(user).Rules.FirstOrDefault(r => r.Matches(meta.Camera, meta.Lens, meta.Focal) && (Exists(user, r.Profile) || exists(r.Profile)));
    }

    /// <summary>Eigenes Profil nach dem Objektivnamen (eingemessene wie eingelesene).</summary>
    public static LensProfiles.AutoHit? Find(string? user, FileMeta.Row? meta) =>
        meta == null ? null : LensProfiles.FindByName(Load(user).Profiles, meta, p => LensProfiles.SourceOf(p) == "calibrated" ? "calibrated" : "own");
}
