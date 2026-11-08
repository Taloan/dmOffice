using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace DmOffice.Gallery;

/// <summary>
/// Personen in der Galerie: die Photo-Engine findet Gesichter (SFace, /faces) und liefert je Gesicht einen Fingerabdruck (128 Zahlen);
/// dmOffice ordnet sie benannten Personen zu und fasst unbenannte zu Gruppen zusammen. Gesichtsdaten sind biometrisch (Art. 9 DSGVO)
/// und liegen deshalb im Tresor (docs/tresor-konzept.md): Gesichter, Personen und ausgeblendete Gruppen in der Tresor-Datenbank;
/// in der Benutzerdatenbank bleibt nur, welches Foto eingelesen ist und wie viele Gesichter es hat (face_items). Eingelesen wird
/// auch bei geschlossenem Tresor - neue Gesichter legt der Server über den öffentlichen Schlüssel ab (Art „faces“), beim Öffnen werden
/// sie übernommen und zugeordnet. Ansehen, Benennen und die Suche nach Namen gehen nur bei offenem Tresor; ohne Tresor keine Personen.
/// Nur, wenn der Benutzer es eingeschaltet hat (UserSettings.Faces) und die Engine es anbietet; Ausschalten löscht alle Gesichtsdaten.
/// Eingelesen wird die große Vorschau des Originals im Hintergrund (Aktivität „faces“), angestoßen beim Öffnen der Galerie.
/// Schlüssel je Foto: „f:&lt;file_id&gt;“ eigene Ablage, „o:&lt;Besitzer&gt;:&lt;file_id&gt;“ geteilte Ordner, „x:&lt;Quelle&gt;/&lt;Pfad&gt;“ Galerie-Ordner.
/// </summary>
public static class GalleryFaces
{
    /// <summary>Ab dieser Ähnlichkeit (Skalarprodukt der normierten Fingerabdrücke) dieselbe Person (SFace-Empfehlung).</summary>
    public const double Same = 0.363;
    /// <summary>Darunter bis hierher: als Vorschlag zeigen.</summary>
    public const double Maybe = 0.30;
    /// <summary>Unbenannte Gruppen etwas enger, damit sich keine Fremden mischen.</summary>
    private const double GroupSame = 0.40;
    /// <summary>Kleine oder unsichere Gesichter bilden keine Gruppen (zugeordnet werden sie trotzdem).</summary>
    private const int GroupMinPx = 50;
    private const double GroupMinScore = 0.85;

    /// <summary>Modell der Engine (null: keine Gesichtererkennung) und das Erkennen selbst - Tests setzen eigene ein.</summary>
    public static Func<CancellationToken, Task<string?>> Model { get; set; } = PhotoEngine.FacesModelAsync;
    public static Func<byte[], ILogger?, CancellationToken, Task<(string Model, List<PhotoEngine.Face> Faces)?>> Detect { get; set; } = PhotoEngine.FacesAsync;
    /// <summary>So oft höchstens nach neuen Fotos schauen (Tests: 0).</summary>
    public static TimeSpan KickEvery { get; set; } = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<UserDb, bool> Ready = new();

    public static void Ensure(UserDb db)
    {
        if (Ready.ContainsKey(db)) return;
        db.Exec("CREATE TABLE IF NOT EXISTS face_items (key TEXT PRIMARY KEY, stamp TEXT NOT NULL, model TEXT NOT NULL, n INTEGER NOT NULL)");
        Ready[db] = true;
    }

    /// <summary>Tabellen der Gesichter in der Tresor-Datenbank (Schritt 3 von <see cref="TresorDb"/>).</summary>
    public const string TresorSchema =
        "CREATE TABLE faces (id INTEGER PRIMARY KEY, key TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL, w REAL NOT NULL, h REAL NOT NULL, " +
        "score REAL NOT NULL, size INTEGER NOT NULL, emb BLOB NOT NULL, person INTEGER, rejected TEXT);" +
        "CREATE INDEX faces_key ON faces(key); CREATE INDEX faces_person ON faces(person);" +
        "CREATE TABLE persons (id INTEGER PRIMARY KEY, name TEXT NOT NULL, cover INTEGER, hidden INTEGER NOT NULL DEFAULT 0);" +
        // Ausgeblendete Gruppen ("spielt keine Rolle"): Gesicht -> Kennung der Gruppe beim Ausblenden (fuer Rueckgaengig)
        "CREATE TABLE face_ignored (face INTEGER PRIMARY KEY, grp TEXT NOT NULL);" +
        "CREATE TABLE faces_ver (id INTEGER PRIMARY KEY CHECK(id = 1), v INTEGER NOT NULL); INSERT INTO faces_ver(id, v) VALUES(1, 0);";

    /// <summary>Tresor-Datenbank: die Tabellen legt ihre Versionierung an.</summary>
    private static void Ensure(TresorDb db) { }

    /// <summary>Beim Start: Übernahme neuer Gesichter beim Öffnen des Tresors und Umzug alter Gesichtsdaten aus der Benutzerdatenbank.</summary>
    public static void Register(Func<string, UserDb> userDb)
    {
        Tresor.Absorbers[DropFaces] = AbsorbFaces;
        Tresor.Absorbers[DropForget] = (db, payload) =>
        {
            var keys = JsonSerializer.Deserialize<List<string>>(payload) ?? [];
            using var conn = db.Open();
            using var tx = conn.BeginTransaction();
            foreach (var k in keys) UserDb.Exec(conn, "DELETE FROM faces WHERE key = $k", ("$k", k));
            Bump(conn);
            tx.Commit();
        };
        Tresor.Opened += (user, db) => MoveIntoTresor(userDb(user), db);
    }

    private const string DropFaces = "faces", DropForget = "faces-forget";
    private sealed record DroppedFace(double X, double Y, double W, double H, double S, int Z, string E);
    private sealed record Dropped(string Key, List<DroppedFace> Faces);

    /// <summary>Personen-Mittelwerte für die Übernahme, gemerkt bis sich Zuordnungen ändern (beim Öffnen kommen viele Fotos auf einmal).</summary>
    private static readonly ConditionalWeakTable<TresorDb, Tuple<string, Dictionary<long, float[]>>> AbsorbCentroids = new();

    /// <summary>Neue Gesichter eines Fotos übernehmen und gleich bekannten Personen zuordnen.</summary>
    private static void AbsorbFaces(TresorDb db, byte[] payload)
    {
        var d = JsonSerializer.Deserialize<Dropped>(payload);
        if (d == null) return;
        Dictionary<long, float[]> centroids;
        // Stand der Zuordnungen: Anzahl und Summe zugeordneter Gesichter samt Personen - ändert sich beim Benennen, Ablehnen, Auflösen
        var stamp = string.Join(":", db.Query("SELECT COUNT(*), COALESCE(SUM(id * person), 0), (SELECT COUNT(*) FROM persons) FROM faces WHERE person IS NOT NULL",
            r => new[] { r.GetInt64(0), r.GetInt64(1), r.GetInt64(2) })[0]);
        if (AbsorbCentroids.TryGetValue(db, out var c) && c.Item1 == stamp) centroids = c.Item2;
        else { centroids = Centroids(Load(db)); AbsorbCentroids.AddOrUpdate(db, Tuple.Create(stamp, centroids)); }
        var rows = new List<FaceRow>();
        long before;
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            before = Convert.ToInt64(UserDb.Query(conn, "SELECT v FROM faces_ver WHERE id = 1", r => r.GetInt64(0)).FirstOrDefault());
            UserDb.Exec(conn, "DELETE FROM faces WHERE key = $k", ("$k", d.Key));
            foreach (var f in d.Faces)
            {
                var emb = Floats(Convert.FromBase64String(f.E));
                var person = Assign(emb, centroids, []);
                var id = UserDb.Query(conn, "INSERT INTO faces(key, x, y, w, h, score, size, emb, person) VALUES($k,$x,$y,$w,$h,$s,$z,$e,$p) RETURNING id", r => r.GetInt64(0),
                    ("$k", d.Key), ("$x", f.X), ("$y", f.Y), ("$w", f.W), ("$h", f.H), ("$s", f.S), ("$z", f.Z), ("$e", Bytes(emb)), ("$p", person)).First();
                rows.Add(new FaceRow(id, d.Key, f.X, f.Y, f.W, f.H, f.S, f.Z, emb, person, []));
            }
            Bump(conn);
            tx.Commit();
        }
        // Stand im Speicher fortschreiben statt neu laden: sonst lädt jedes neue Foto alle Gesichter samt Merkmalen neu (bei
        // zehntausenden quadratisch langsam - Benennen und die Übersicht warteten darauf). Die Gruppen bleiben bis zum Ende des
        // Einlesens wie sie sind (ScanAsync verwirft den Stand danach). Die Zentren bleiben: die eigenen Zuordnungen verschieben sie kaum.
        if (Data.TryGetValue(db, out var snap) && snap.Version == before)
        {
            var faces = snap.Faces.Where(f => f.Key != d.Key).Concat(rows).ToList();
            Data[db] = new Snapshot(before + 1, faces, snap.Persons, snap.Ignored) { Groups = snap.Groups };
        }
        var after = string.Join(":", db.Query("SELECT COUNT(*), COALESCE(SUM(id * person), 0), (SELECT COUNT(*) FROM persons) FROM faces WHERE person IS NOT NULL",
            r => new[] { r.GetInt64(0), r.GetInt64(1), r.GetInt64(2) })[0]);
        AbsorbCentroids.AddOrUpdate(db, Tuple.Create(after, centroids));
    }

    private static bool HasTable(UserDb db, string name) =>
        Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $n", ("$n", name)) ?? 0L) > 0;

    /// <summary>
    /// Gesichtsdaten von vor dem Tresor (Benutzerdatenbank) in den Tresor umziehen - beim ersten Öffnen. Danach in der Benutzerdatenbank
    /// gelöscht und überschrieben (secure_delete), nicht nur als frei markiert.
    /// </summary>
    internal static void MoveIntoTresor(UserDb user, TresorDb tresor)
    {
        if (!HasTable(user, "faces")) return;
        var faces = user.Query("SELECT id, key, x, y, w, h, score, size, emb, person, rejected FROM faces", r => (Id: r.GetInt64(0), Key: r.GetString(1),
            X: r.GetDouble(2), Y: r.GetDouble(3), W: r.GetDouble(4), H: r.GetDouble(5), S: r.GetDouble(6), Z: r.GetInt64(7), E: (byte[])r[8],
            P: r.IsDBNull(9) ? (long?)null : r.GetInt64(9), R: r.IsDBNull(10) ? null : r.GetString(10)));
        var persons = HasTable(user, "persons") ? user.Query("SELECT id, name, cover, hidden FROM persons",
            r => (Id: r.GetInt64(0), Name: r.GetString(1), Cover: r.IsDBNull(2) ? (long?)null : r.GetInt64(2), Hidden: r.GetInt64(3))) : [];
        var ignored = HasTable(user, "face_ignored") ? user.Query("SELECT face, grp FROM face_ignored", r => (Face: r.GetInt64(0), Grp: r.GetString(1))) : [];
        using (var conn = tresor.Open())
        using (var tx = conn.BeginTransaction())
        {
            foreach (var p in persons)
                UserDb.Exec(conn, "INSERT OR IGNORE INTO persons(id, name, cover, hidden) VALUES($i,$n,$c,$h)", ("$i", p.Id), ("$n", p.Name), ("$c", p.Cover), ("$h", p.Hidden));
            foreach (var f in faces)
                UserDb.Exec(conn, "INSERT OR IGNORE INTO faces(id, key, x, y, w, h, score, size, emb, person, rejected) VALUES($i,$k,$x,$y,$w,$h,$s,$z,$e,$p,$r)",
                    ("$i", f.Id), ("$k", f.Key), ("$x", f.X), ("$y", f.Y), ("$w", f.W), ("$h", f.H), ("$s", f.S), ("$z", f.Z), ("$e", f.E), ("$p", f.P), ("$r", f.R));
            foreach (var g in ignored) UserDb.Exec(conn, "INSERT OR IGNORE INTO face_ignored(face, grp) VALUES($f,$g)", ("$f", g.Face), ("$g", g.Grp));
            Bump(conn);
            tx.Commit();
        }
        using (var conn = user.Open())
        {
            UserDb.Exec(conn, "PRAGMA secure_delete = ON");
            foreach (var t in new[] { "faces", "persons", "face_ignored", "faces_ver" })
                if (HasTable(user, t)) { UserDb.Exec(conn, $"DELETE FROM {t}"); UserDb.Exec(conn, $"DROP TABLE {t}"); }
        }
    }

    /// <summary>Namen der Personen je Foto (Schlüssel wie <see cref="KeyOf"/>) - für die Suche in der Galerie; aus: leer.</summary>
    public static Dictionary<string, List<string>> NamesByKey(UserSpace space, TresorDb? tresor)
    {
        if (!space.Settings.Faces || tresor == null) return [];
        return tresor.Query("SELECT DISTINCT f.key, p.name FROM faces f JOIN persons p ON p.id = f.person", r => (Key: r.GetString(0), Name: r.GetString(1)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToList());
    }

    private static long Version(TresorDb db) { Ensure(db); return Convert.ToInt64(db.Scalar("SELECT v FROM faces_ver WHERE id = 1") ?? 0L); }
    private static void Bump(SqliteConnection conn) => UserDb.Exec(conn, "UPDATE faces_ver SET v = v + 1 WHERE id = 1");

    /// <summary>Alle Gesichtsdaten weg (Ausschalten): im Tresor (falls offen) und der Stand des Einlesens.</summary>
    public static void Clear(UserDb db, TresorDb? tresor)
    {
        Ensure(db);
        db.Exec("DELETE FROM face_items");
        if (tresor == null) return;
        using var conn = tresor.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "DELETE FROM faces"); UserDb.Exec(conn, "DELETE FROM persons"); UserDb.Exec(conn, "DELETE FROM face_ignored");
        Bump(conn);
        tx.Commit();
        Data.TryRemove(tresor, out _);
    }

    // ---------------- Fotos der Galerie ----------------

    public static string KeyOf(GalleryTimeline.Entry e) =>
        e.Id.StartsWith("ext:", StringComparison.Ordinal) ? "x:" + e.Id[4..] : e.Owner == null ? "f:" + e.FileId : "o:" + e.Owner + ":" + e.FileId;

    // Ohne „-o“ (Vorschaubild steht noch aus): dieselbe Datei, die Gesichter bleiben
    private static string StampOf(GalleryTimeline.Entry e) => e.Etag.Split("~e")[0].Replace("-o", "");

    private static readonly string[] Jpegs = [".jpg", ".jpeg", ".heic", ".heif"];
    private static readonly HashSet<string> Videos = [".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".3gp", ".mts", ".m2ts", ".wmv"];

    /// <summary>Fotos, in denen Gesichter gesucht werden: keine Videos, RAW nur ohne gleichnamiges JPG daneben.</summary>
    private static IEnumerable<GalleryTimeline.Entry> Photos(IReadOnlyList<GalleryTimeline.Entry> items)
    {
        var stems = items.Where(e => Jpegs.Contains(e.Ext)).Select(e => e.Id[..^e.Ext.Length].ToLowerInvariant()).ToHashSet();
        return items.Where(e => e.Mime?.StartsWith("video/", StringComparison.Ordinal) != true && !Videos.Contains(e.Ext)
            && !(Thumbs.IsRaw(e.Name) && stems.Contains(e.Id[..^e.Ext.Length].ToLowerInvariant())));
    }

    private static readonly ConditionalWeakTable<IReadOnlyList<GalleryTimeline.Entry>, Dictionary<string, GalleryTimeline.Entry>> VisibleCache = new();

    /// <summary>Was der Benutzer gerade sehen darf: Schlüssel → Foto (Freigaben können enden - dann zeigt auch die Personenansicht es nicht mehr).</summary>
    public static async Task<Dictionary<string, GalleryTimeline.Entry>> VisibleAsync(HttpContext http, ServerConfig config)
    {
        var items = await GalleryTimeline.AllAsync(http, config);
        return VisibleCache.GetValue(items, list =>
        {
            var map = new Dictionary<string, GalleryTimeline.Entry>();
            foreach (var e in Photos(list)) map.TryAdd(KeyOf(e), e);
            return map;
        });
    }

    /// <summary>Holt die große Vorschau des Originals (unbearbeitet, damit die Rahmen zum Bild passen) - ohne die Anfrage.</summary>
    private static Func<CancellationToken, Task<byte[]?>> Fetcher(HttpContext http, ServerConfig config, GalleryTimeline.Entry e)
    {
        var space = http.Space();
        if (e.Id.StartsWith("ext:", StringComparison.Ordinal))
        {
            var id = e.Id[4..];
            return ct => GalleryFolders.ThumbAsync(space, config, id, Thumbs.Large, ct, original: true);
        }
        var db = e.Owner == null ? space.Db : SharedFiles.Resolve(http, e.Id).Db;
        var fileId = e.FileId;
        return async ct => FileStore.ById(db, fileId) is { } f ? await Thumbs.Get(db, f, Thumbs.Large, ct, original: true) : null;
    }

    // ---------------- Einlesen ----------------

    private static readonly ConcurrentDictionary<string, bool> Running = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastKick = new();

    public static bool IsRunning(string userId) => Running.ContainsKey(userId);

    /// <summary>In einem Galerie-Ordner ist noch etwas ohne Bildangaben oder Vorschau - die Personenerkennung wartet darauf.</summary>
    public static bool WaitingForGallery(UserSpace space) =>
        GalleryFolders.Of(space.Db).Any(s => GalleryFolders.Stages(space.Db, s.Id) is var st && st.Listed + st.NoThumb > 0);

    /// <summary>Neue und geänderte Fotos im Hintergrund einlesen, verschwundene vergessen. Billig, wenn kürzlich schon geschaut.</summary>
    public static async Task KickAsync(HttpContext http, ServerConfig config, ILogger log, bool force = false)
    {
        var space = http.Space();
        var user = space.User.Id;
        if (!space.Settings.Faces || Running.ContainsKey(user) || !Tresor.Exists(user)) return;
        if (!force && DateTime.UtcNow - LastKick.GetValueOrDefault(user) < KickEvery) return;
        LastKick[user] = DateTime.UtcNow;
        // Nachgelagert: erst der ganze Bestand mit Bildangaben und Vorschauen, dann Gesichter. Solange in einem Galerie-Ordner
        // noch etwas offen ist, wartet die Erkennung (WaitingForGallery) - sie holte sonst dieselben Originale noch einmal und
        // nähme dem Einlesen die Leitung
        if (WaitingForGallery(space)) return;
        var model = await Model(http.RequestAborted);
        if (model == null) return;
        var db = space.Db;
        Ensure(db);
        var done = db.Query("SELECT key, stamp, model FROM face_items", r => (r.GetString(0), r.GetString(1), r.GetString(2))).ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));
        var visible = await VisibleAsync(http, config);
        var todo = new List<(string Key, string Stamp, Func<CancellationToken, Task<byte[]?>> Fetch)>();
        foreach (var (key, e) in visible)
        {
            // Foto aus einem Galerie-Ordner, das noch auf Bildangaben oder Vorschau wartet (ruhender Ordner): später
            if (e.Etag.Contains("-o", StringComparison.Ordinal)) continue;
            var stamp = StampOf(e);
            if (done.TryGetValue(key, out var d) && d.Item1 == stamp && d.Item2 == model) continue;
            todo.Add((key, stamp, Fetcher(http, config, e)));
        }
        var gone = visible.Count == 0 ? [] : done.Keys.Where(k => !visible.ContainsKey(k)).ToList();
        if (gone.Count > 0) Forget(user, db, gone);
        if (todo.Count == 0) return;
        if (!Running.TryAdd(user, true)) return;
        // Neueste zuerst - die sieht man in der Galerie oben
        var order = visible.ToDictionary(x => x.Key, x => x.Value.Taken);
        todo.Sort((a, b) => order[b.Key].CompareTo(order[a.Key]));
        _ = Task.Run(async () =>
        {
            try { await ScanAsync(user, db, todo, log); }
            catch (Exception ex) { log.LogWarning(ex, "Gesichter einlesen"); }
            finally
            {
                Running.TryRemove(user, out _);
                // Während des Einlesens blieben die Gruppen stehen (AbsorbFaces) - jetzt einmal neu bilden
                if (Tresor.DbOf(user) is { } t) Data.TryRemove(t, out _);
            }
        });
    }

    /// <summary>Verschwundene Fotos vergessen: Stand hier, Gesichter im Tresor (über den öffentlichen Schlüssel).</summary>
    private static void Forget(string user, UserDb db, List<string> keys)
    {
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            foreach (var k in keys) UserDb.Exec(conn, "DELETE FROM face_items WHERE key = $k", ("$k", k));
            tx.Commit();
        }
        Tresor.Drop(user, DropForget, JsonSerializer.SerializeToUtf8Bytes(keys));
    }

    private static async Task ScanAsync(string user, UserDb db, List<(string Key, string Stamp, Func<CancellationToken, Task<byte[]?>> Fetch)> todo, ILogger log)
    {
        using var job = Activity.Start(user, Activity.Faces, null, todo.Count);
        int done = 0, found = 0, skipped = 0;
        foreach (var (key, stamp, fetch) in todo)
        {
            if (!Active(db)) return;
            byte[]? jpeg = null;
            try
            {
                if (await fetch(CancellationToken.None) is { } webp)
                {
                    using var bmp = SKBitmap.Decode(webp);
                    if (bmp != null) jpeg = PhotoAdjust.Jpeg(bmp, 90);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or ArgumentException or HttpRequestException)
            {
                log.LogDebug("Gesichter: {Key} nicht lesbar: {Error}", key, e.Message);
            }
            if (jpeg == null) { skipped++; job.Progress(++done, todo.Count); continue; }   // nächstes Mal wieder versuchen
            var res = await Detect(jpeg, log, CancellationToken.None);
            if (res == null) { job.Fail("Die Photo-Engine antwortet nicht - später geht es weiter."); return; }
            // Ausgeschaltet, während es lief: nichts mehr schreiben
            if (!Active(db)) return;
            // Gesichter in den Tresor (öffentlicher Schlüssel - geht auch geschlossen; offen wird gleich übernommen und zugeordnet)
            Tresor.Drop(user, DropFaces, JsonSerializer.SerializeToUtf8Bytes(new Dropped(key,
                res.Value.Faces.Select(f => new DroppedFace(f.X, f.Y, f.W, f.H, f.Score, f.SizePx, Convert.ToBase64String(Bytes(f.Embedding)))).ToList())));
            db.Exec("INSERT INTO face_items(key, stamp, model, n) VALUES($k,$s,$m,$n) ON CONFLICT(key) DO UPDATE SET stamp=$s, model=$m, n=$n",
                ("$k", key), ("$s", stamp), ("$m", res.Value.Model), ("$n", res.Value.Faces.Count));
            found += res.Value.Faces.Count;
            job.Progress(++done, todo.Count, $"{found} Gesichter");
        }
        if (skipped > 0) log.LogInformation("Gesichter: {Skipped} Fotos ohne Vorschau übersprungen", skipped);
    }

    /// <summary>Hat der Besitzer der Datenbank die Personen noch eingeschaltet? (Zum Ausschalten leert Clear die Tabellen und vergisst den Stand.)</summary>
    private static bool Active(UserDb db) => !Cleared.ContainsKey(db);
    private static readonly ConcurrentDictionary<UserDb, bool> Cleared = new();

    /// <summary>Beim Ausschalten: laufendes Einlesen beenden, Daten löschen.</summary>
    public static void Disable(UserDb db, TresorDb? tresor)
    {
        Cleared[db] = true;
        Clear(db, tresor);
    }

    public static void Enable(UserDb db) => Cleared.TryRemove(db, out _);

    // ---------------- Daten im Speicher ----------------

    internal sealed record FaceRow(long Id, string Key, double X, double Y, double W, double H, double Score, int Size, float[] Emb, long? Person, HashSet<long> Rejected);
    internal sealed record PersonRow(long Id, string Name, long? Cover, bool Hidden);
    internal sealed class Snapshot(long version, List<FaceRow> faces, List<PersonRow> persons, Dictionary<long, string>? ignored = null)
    {
        public long Version { get; } = version;
        public List<FaceRow> Faces { get; } = faces;
        public List<PersonRow> Persons { get; } = persons;
        /// <summary>Ausgeblendete Gesichter -> Kennung ihrer Gruppe beim Ausblenden</summary>
        public Dictionary<long, string> Ignored { get; } = ignored ?? [];
        public Dictionary<string, List<long>>? Groups;
    }

    private static readonly ConcurrentDictionary<TresorDb, Snapshot> Data = new();

    internal static Snapshot Load(TresorDb db)
    {
        var v = Version(db);
        if (Data.TryGetValue(db, out var s) && s.Version == v) return s;
        var faces = db.Query("SELECT id, key, x, y, w, h, score, size, emb, person, rejected FROM faces", r => new FaceRow(r.GetInt64(0), r.GetString(1), r.GetDouble(2), r.GetDouble(3),
            r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetInt32(7), Floats((byte[])r[8]), r.IsDBNull(9) ? null : r.GetInt64(9),
            r.IsDBNull(10) ? [] : r.GetString(10).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToHashSet()));
        var persons = db.Query("SELECT id, name, cover, hidden FROM persons", r => new PersonRow(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetInt64(3) != 0));
        var ignored = db.Query("SELECT face, grp FROM face_ignored", r => (r.GetInt64(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
        s = new Snapshot(v, faces, persons, ignored);
        Data[db] = s;
        return s;
    }

    private static byte[] Bytes(float[] v) { var b = new byte[v.Length * 4]; Buffer.BlockCopy(v, 0, b, 0, b.Length); return b; }
    private static float[] Floats(byte[] b) { var v = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, v, 0, v.Length * 4); return v; }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static float[] Normalize(float[] v)
    {
        var n = Math.Sqrt(v.Sum(x => (double)x * x));
        return n < 1e-9 ? v : v.Select(x => (float)(x / n)).ToArray();
    }

    /// <summary>Je Person der Mittelwert ihrer Gesichter (normiert).</summary>
    internal static Dictionary<long, float[]> Centroids(Snapshot s)
    {
        var sums = new Dictionary<long, float[]>();
        foreach (var f in s.Faces)
        {
            if (f.Person is not long p) continue;
            if (!sums.TryGetValue(p, out var acc)) sums[p] = acc = new float[f.Emb.Length];
            for (var i = 0; i < acc.Length && i < f.Emb.Length; i++) acc[i] += f.Emb[i];
        }
        return sums.ToDictionary(x => x.Key, x => Normalize(x.Value));
    }

    private static long? Assign(float[] emb, Dictionary<long, float[]> centroids, HashSet<long> rejected)
    {
        long? best = null;
        var bestSim = Same;
        foreach (var (p, c) in centroids)
        {
            if (rejected.Contains(p)) continue;
            var sim = Dot(emb, c);
            if (sim >= bestSim) { bestSim = sim; best = p; }
        }
        return best;
    }

    /// <summary>Normierter Mittelwert von Gesichtern.</summary>
    private static float[]? Mean(IEnumerable<FaceRow> faces)
    {
        float[]? acc = null;
        foreach (var f in faces)
        {
            acc ??= new float[f.Emb.Length];
            for (var i = 0; i < acc.Length && i < f.Emb.Length; i++) acc[i] += f.Emb[i];
        }
        return acc == null ? null : Normalize(acc);
    }

    /// <summary>Unbenannte Gruppen: noch keiner Person zugeordnete, deutliche Gesichter, die sich ähneln (größte zuerst).
    /// Ohne ausgeblendete - und ohne Gesichter, die einer ausgeblendeten Gruppe gleichen (neue Fotos derselben Person).</summary>
    internal static Dictionary<string, List<long>> Groups(Snapshot s)
    {
        if (s.Groups != null) return s.Groups;
        var byId = s.Faces.ToDictionary(f => f.Id);
        var ignoredMeans = s.Ignored.GroupBy(x => x.Value)
            .Select(g => Mean(g.Where(x => byId.ContainsKey(x.Key)).Select(x => byId[x.Key]))).OfType<float[]>().ToList();
        var leaders = new List<(float[] Sum, List<FaceRow> Members)>();
        foreach (var f in s.Faces.Where(f => f.Person == null && f.Size >= GroupMinPx && f.Score >= GroupMinScore && !s.Ignored.ContainsKey(f.Id)
            && !ignoredMeans.Any(m => Dot(f.Emb, m) >= Same)).OrderByDescending(f => f.Size))
        {
            var best = -1;
            var bestSim = GroupSame;
            for (var i = 0; i < leaders.Count; i++)
            {
                var sim = Dot(f.Emb, leaders[i].Sum) / Math.Sqrt(Math.Max(1e-9, Dot(leaders[i].Sum, leaders[i].Sum)));
                if (sim >= bestSim) { bestSim = sim; best = i; }
            }
            if (best < 0) { leaders.Add(((float[])f.Emb.Clone(), [f])); continue; }
            var sum = leaders[best].Sum;
            for (var i = 0; i < sum.Length && i < f.Emb.Length; i++) sum[i] += f.Emb[i];
            leaders[best].Members.Add(f);
        }
        s.Groups = leaders.Where(l => l.Members.Select(m => m.Key).Distinct().Count() >= 2)
            .ToDictionary(l => "g" + l.Members.Min(m => m.Id), l => l.Members.Select(m => m.Id).ToList());
        return s.Groups;
    }

    // ---------------- Personen benennen und ändern ----------------

    /// <summary>Gesichter einer Person geben (neu oder vorhanden, gleicher Name zählt als dieselbe) und Ähnliche gleich mit zuordnen.</summary>
    public static long Name(TresorDb db, IEnumerable<long> faceIds, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("Bitte einen Namen angeben (höchstens 80 Zeichen).");
        Ensure(db);
        long person;
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            person = UserDb.Query(conn, "SELECT id FROM persons WHERE name = $n COLLATE NOCASE", r => r.GetInt64(0), ("$n", name)).FirstOrDefault();
            if (person == 0) person = UserDb.Query(conn, "INSERT INTO persons(name) VALUES($n) RETURNING id", r => r.GetInt64(0), ("$n", name)).First();
            foreach (var id in faceIds) UserDb.Exec(conn, "UPDATE faces SET person = $p WHERE id = $i", ("$p", person), ("$i", id));
            Bump(conn);
            tx.Commit();
        }
        Attach(db, person);
        return person;
    }

    /// <summary>Freie Gesichter, die der Person nun deutlich ähneln, ihr zuordnen (außer abgelehnte).</summary>
    private static void Attach(TresorDb db, long person)
    {
        var s = Load(db);
        if (!Centroids(s).TryGetValue(person, out var c)) return;
        var add = s.Faces.Where(f => f.Person == null && !f.Rejected.Contains(person) && Dot(f.Emb, c) >= Same).Select(f => f.Id).ToList();
        if (add.Count == 0) return;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in add) UserDb.Exec(conn, "UPDATE faces SET person = $p WHERE id = $i AND person IS NULL", ("$p", person), ("$i", id));
        Bump(conn);
        tx.Commit();
    }

    /// <summary>Gesichter der Person geben (Vorschlag bestätigt).</summary>
    public static void Confirm(TresorDb db, long person, IEnumerable<long> faceIds)
    {
        Ensure(db);
        if (Load(db).Persons.All(p => p.Id != person)) throw new KeyNotFoundException("Diese Person gibt es nicht.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in faceIds) UserDb.Exec(conn, "UPDATE faces SET person = $p WHERE id = $i", ("$p", person), ("$i", id));
        Bump(conn);
        tx.Commit();
    }

    /// <summary>„Ist nicht diese Person“: lösen und nicht wieder vorschlagen oder zuordnen.</summary>
    public static void Reject(TresorDb db, long person, IEnumerable<long> faceIds)
    {
        Ensure(db);
        var s = Load(db);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in faceIds)
        {
            if (s.Faces.FirstOrDefault(f => f.Id == id) is not { } f) continue;
            var rejected = new HashSet<long>(f.Rejected) { person };
            UserDb.Exec(conn, "UPDATE faces SET person = CASE WHEN person = $p THEN NULL ELSE person END, rejected = $r WHERE id = $i",
                ("$p", person), ("$r", string.Join(',', rejected)), ("$i", id));
        }
        UserDb.Exec(conn, "UPDATE persons SET cover = NULL WHERE id = $p AND cover IN (SELECT id FROM faces WHERE person IS NOT $p)", ("$p", person));
        Bump(conn);
        tx.Commit();
    }

    /// <summary>Umbenennen (auf einen vorhandenen Namen: zusammenführen), Titelbild wählen, ausblenden.</summary>
    public static long Change(TresorDb db, long person, string? name, long? cover, bool? hidden)
    {
        Ensure(db);
        var s = Load(db);
        if (s.Persons.All(p => p.Id != person)) throw new KeyNotFoundException("Diese Person gibt es nicht.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var result = person;
        if (name != null)
        {
            name = name.Trim();
            if (name.Length is 0 or > 80) throw new ArgumentException("Bitte einen Namen angeben (höchstens 80 Zeichen).");
            var other = UserDb.Query(conn, "SELECT id FROM persons WHERE name = $n COLLATE NOCASE AND id <> $p", r => r.GetInt64(0), ("$n", name), ("$p", person)).FirstOrDefault();
            if (other != 0)
            {
                UserDb.Exec(conn, "UPDATE faces SET person = $o WHERE person = $p", ("$o", other), ("$p", person));
                UserDb.Exec(conn, "DELETE FROM persons WHERE id = $p", ("$p", person));
                result = other;
            }
            else UserDb.Exec(conn, "UPDATE persons SET name = $n WHERE id = $p", ("$n", name), ("$p", person));
        }
        if (cover != null && s.Faces.Any(f => f.Id == cover && f.Person == result)) UserDb.Exec(conn, "UPDATE persons SET cover = $c WHERE id = $p", ("$c", cover), ("$p", result));
        if (hidden != null) UserDb.Exec(conn, "UPDATE persons SET hidden = $h WHERE id = $p", ("$h", hidden.Value ? 1 : 0), ("$p", result));
        Bump(conn);
        tx.Commit();
        return result;
    }

    /// <summary>Gruppe ausblenden („spielt keine Rolle“): taucht unter „Noch ohne Namen“ nicht mehr auf, ähnliche neue Gesichter auch nicht.</summary>
    public static void Ignore(TresorDb db, string group)
    {
        if (!Groups(Load(db)).TryGetValue(group, out var ids)) throw new KeyNotFoundException("Diese Gruppe gibt es nicht mehr - die Ansicht wird neu geladen.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in ids) UserDb.Exec(conn, "INSERT OR REPLACE INTO face_ignored(face, grp) VALUES($f, $g)", ("$f", id), ("$g", group));
        Bump(conn);
        tx.Commit();
    }

    /// <summary>Ausgeblendete wieder zeigen: eine Gruppe (Rückgängig) oder alle.</summary>
    public static void Unignore(TresorDb db, string? group)
    {
        Ensure(db);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (group == null) UserDb.Exec(conn, "DELETE FROM face_ignored");
        else UserDb.Exec(conn, "DELETE FROM face_ignored WHERE grp = $g", ("$g", group));
        Bump(conn);
        tx.Commit();
    }

    /// <summary>Vorschlag ablehnen: die Gesichter der Gruppe sind nicht diese Person.</summary>
    public static void RejectGroup(TresorDb db, string group, long person)
    {
        if (!Groups(Load(db)).TryGetValue(group, out var ids)) throw new KeyNotFoundException("Diese Gruppe gibt es nicht mehr - die Ansicht wird neu geladen.");
        Reject(db, person, ids);
    }

    /// <summary>Welcher benannten Person die Gruppe am ehesten gleicht (ab „vielleicht“), außer abgelehnten und ausgeblendeten.</summary>
    private static (PersonRow Person, double Sim)? SuggestFor(Snapshot s, List<FaceRow> members, Dictionary<long, float[]> centroids)
    {
        if (Mean(members) is not { } mean) return null;
        var rejected = members.SelectMany(m => m.Rejected).ToHashSet();
        (PersonRow, double)? best = null;
        foreach (var p in s.Persons)
        {
            if (p.Hidden || rejected.Contains(p.Id) || !centroids.TryGetValue(p.Id, out var c)) continue;
            var sim = Dot(mean, c);
            if (sim >= Maybe && (best == null || sim > best.Value.Item2)) best = (p, sim);
        }
        return best;
    }

    /// <summary>Person auflösen: ihre Gesichter sind wieder frei.</summary>
    public static void Remove(TresorDb db, long person)
    {
        Ensure(db);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "UPDATE faces SET person = NULL WHERE person = $p", ("$p", person));
        UserDb.Exec(conn, "DELETE FROM persons WHERE id = $p", ("$p", person));
        Bump(conn);
        tx.Commit();
    }

    // ---------------- Für die Ansicht ----------------

    /// <summary>Bestes Gesicht als Bild: das gewählte, sonst das größte deutliche.</summary>
    private static FaceRow? CoverOf(IEnumerable<FaceRow> faces, long? cover) =>
        faces.FirstOrDefault(f => f.Id == cover) ?? faces.OrderByDescending(f => f.Score >= GroupMinScore).ThenByDescending(f => f.Size).FirstOrDefault();

    /// <summary>Personen und unbenannte Gruppen mit Anzahl Fotos (nur sichtbare Fotos).</summary>
    public static object Overview(TresorDb db, long scanned, Dictionary<string, GalleryTimeline.Entry> visible)
    {
        Ensure(db);
        var s = Load(db);
        var shown = s.Faces.Where(f => visible.ContainsKey(f.Key)).ToList();
        var persons = s.Persons.Select(p =>
        {
            var mine = shown.Where(f => f.Person == p.Id).ToList();
            return new { id = p.Id, name = p.Name, hidden = p.Hidden, count = mine.Select(f => f.Key).Distinct().Count(), face = CoverOf(mine, p.Cover)?.Id };
        }).Where(p => p.count > 0).OrderByDescending(p => p.count).ToList();
        var byId = shown.ToDictionary(f => f.Id);
        var centroids = Centroids(s);
        var groups = Groups(s).Select(g =>
        {
            var mine = g.Value.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            return (Id: g.Key, Mine: mine, Count: mine.Select(f => f.Key).Distinct().Count());
        }).Where(g => g.Count >= 2).OrderByDescending(g => g.Count).Take(200).Select(g =>
        {
            var sug = SuggestFor(s, g.Mine, centroids);
            return new { id = g.Id, count = g.Count, face = CoverOf(g.Mine, null)?.Id,
                suggest = sug is { } x ? new { id = x.Person.Id, name = x.Person.Name } : null };
        }).ToList();
        return new { persons, groups, scanned, faces = shown.Count, ignored = s.Ignored.Values.Distinct().Count() };
    }

    /// <summary>Fotos einer Person oder Gruppe, neueste zuerst, je Foto das Gesicht; bei Personen dazu Vorschläge.</summary>
    public static byte[] Photos(TresorDb db, Dictionary<string, GalleryTimeline.Entry> visible, long? person, string? group)
    {
        Ensure(db);
        var s = Load(db);
        List<FaceRow> faces;
        if (person != null) faces = s.Faces.Where(f => f.Person == person).ToList();
        else if (group != null && Groups(s).TryGetValue(group, out var ids)) { var set = ids.ToHashSet(); faces = s.Faces.Where(f => set.Contains(f.Id)).ToList(); }
        else throw new KeyNotFoundException("Diese Gruppe gibt es nicht mehr - die Ansicht wird neu geladen.");
        faces = faces.Where(f => visible.ContainsKey(f.Key)).ToList();
        var byKey = faces.GroupBy(f => f.Key).Select(g => (Entry: visible[g.Key], Faces: g.Select(f => f.Id).ToList())).OrderByDescending(x => x.Entry.Taken).ToList();
        // Vorschläge: freie Gesichter knapp unter der Schwelle, nicht abgelehnt
        var suggest = new List<(FaceRow Face, double Sim)>();
        if (person is long p && Centroids(s).TryGetValue(p, out var c))
            suggest = s.Faces.Where(f => f.Person == null && !f.Rejected.Contains(p) && visible.ContainsKey(f.Key))
                .Select(f => (f, Dot(f.Emb, c))).Where(x => x.Item2 >= Maybe).OrderByDescending(x => x.Item2).Take(60).ToList();
        return GalleryTimeline.Json(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items");
            foreach (var (e, _) in byKey) GalleryTimeline.Write(w, e);
            w.WriteEndArray();
            w.WriteStartObject("faces");
            foreach (var (e, ids) in byKey) { w.WriteStartArray(e.Id); foreach (var id in ids) w.WriteNumberValue(id); w.WriteEndArray(); }
            w.WriteEndObject();
            w.WriteStartArray("suggestions");
            foreach (var (f, sim) in suggest)
            {
                w.WriteStartObject();
                w.WriteNumber("face", f.Id);
                w.WriteString("path", visible[f.Key].Id);
                w.WriteNumber("sim", Math.Round(sim, 3));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static readonly SemaphoreSlim CropGate = new(3);

    /// <summary>Ausschnitt eines Gesichts (quadratisch, etwas Rand), WebP; null, wenn das Foto nicht (mehr) sichtbar ist.</summary>
    public static async Task<(byte[] Data, string Stamp)?> CropAsync(HttpContext http, ServerConfig config, long faceId, int size, CancellationToken ct)
    {
        var f = Load(Tresor.Db(http)).Faces.FirstOrDefault(x => x.Id == faceId);
        if (f == null) return null;
        var visible = await VisibleAsync(http, config);
        if (!visible.TryGetValue(f.Key, out var e)) return null;
        // Höchstens drei zugleich: die Übersicht fragt dutzende Ausschnitte auf einmal an, jeder braucht die große Vorschau
        // (2048 px, entpackt rund 16 MB) - ohne Grenze wartete alles und der Speicher lief voll
        await CropGate.WaitAsync(ct);
        try
        {
            var webp = await Fetcher(http, config, e)(ct);
            if (webp == null) return null;
            using var bmp = SKBitmap.Decode(webp);
            if (bmp == null) return null;
            double cx = (f.X + f.W / 2) * bmp.Width, cy = (f.Y + f.H / 2) * bmp.Height;
            var side = Math.Min(Math.Min(bmp.Width, bmp.Height), Math.Max(f.W * bmp.Width, f.H * bmp.Height) * 1.6);
            var left = Math.Clamp(cx - side / 2, 0, bmp.Width - side);
            var top = Math.Clamp(cy - side / 2, 0, bmp.Height - side);
            size = Math.Clamp(size, 32, 512);
            using var outBmp = new SKBitmap(size, size);
            using (var canvas = new SKCanvas(outBmp))
            using (var img = SKImage.FromBitmap(bmp))
                canvas.DrawImage(img, new SKRect((float)left, (float)top, (float)(left + side), (float)(top + side)), new SKRect(0, 0, size, size),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var data = SKImage.FromBitmap(outBmp).Encode(SKEncodedImageFormat.Webp, 85);
            return (data.ToArray(), StampOf(e) + ":" + faceId);
        }
        finally { CropGate.Release(); }
    }

    /// <summary>Anzahl erkannter Gesichter je Foto (für die Statistik der Ansicht).</summary>
    public static long Scanned(UserDb db) { Ensure(db); return Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM face_items") ?? 0L); }
}
