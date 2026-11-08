using System.Collections.Concurrent;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;

namespace DmOffice.Gallery;

/// <summary>Ein Ordner aus einem externen Speicher, dessen Fotos und Videos in der Galerie erscheinen.</summary>
public sealed class GallerySource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>Kennung des externen Ordners (ExternalFolders: Serverordner oder eigener WebDAV-Ordner).</summary>
    public string Mount { get; set; } = "";
    /// <summary>Pfad darin, mit „/“ getrennt; leer = der ganze externe Ordner.</summary>
    public string Path { get; set; } = "";
    /// <summary>Überwachen: regelmäßig neu einlesen und Offenes (Bildangaben, Vorschauen) laufend abarbeiten. Aus: nur beim
    /// Hinzufügen und auf Knopfdruck - dann auch das Abarbeiten nur bis zum Ende dieses einen Durchgangs (siehe <see cref="Draining"/>).</summary>
    public bool Watch { get; set; } = true;
    /// <summary>Nicht überwacht, aber eingelesen (Knopf oder Hinzufügen): Offenes noch abarbeiten, bis nichts mehr offen ist -
    /// gespeichert, damit ein Neustart des Servers mittendrin nicht stehen bleibt. Danach ruht der Ordner (kein Abruf beim Anbieter).</summary>
    public bool Draining { get; set; }
    /// <summary>XMP-Sidecar-Dateien neben den Fotos lesen (Titel, Beschreibung, Stichwörter, Bewertung).</summary>
    public bool Sidecars { get; set; } = true;
    /// <summary>Große Vorschauen (2048 px) im Hintergrund vorab erzeugen - Fotos öffnen dann sofort, auch über einen langsamen Tunnel.</summary>
    public bool Proxies { get; set; }
    public DateTime? LastScan { get; set; }
    public string? Error { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Galerie-Ordner (Version 2) wie Immichs externe Bibliotheken: Fotos und Videos in externen Ordnern werden eingelesen - Pfad,
/// Größe, Änderungszeit, Aufnahmedaten, kleines Vorschaubild - und erscheinen in der Galerie. Die Originale bleiben, wo sie
/// sind; nichts wird in die eigene Ablage kopiert. Index je Benutzer in der Tabelle gallery_ext.
/// </summary>
public static class GalleryFolders
{
    public const string Key = "gallery.sources";
    /// <summary>Überwachte Ordner so oft neu einlesen.</summary>
    public static readonly TimeSpan WatchEvery = TimeSpan.FromMinutes(15);
    private const int MaxFiles = 200_000;
    private const long MaxImageBytes = 150L * 1024 * 1024;
    /// <summary>
    /// Große Vorschauen aus Originalen höchstens zwei zugleich (wie <see cref="Thumbs"/> für die eigene Ablage): jede liest das
    /// ganze Original (RAW bis 50 MB) und rechnet ein Bild mit bis zu 40 MP herunter. Ohne Grenze liefen nach einer Unterbrechung
    /// der Freigabe alle vom Browser wiederholten Vorschauen auf einmal los und füllten den Speicher des Servers (29.10.2026).
    /// </summary>
    private static readonly SemaphoreSlim ProxyGate = new(2);

    public static List<GallerySource> Of(UserDb db) => db.GetJson<List<GallerySource>>(Key);

    /// <summary>Ablage der großen Vorschauen (beim Start gesetzt; in Tests ohne).</summary>
    public static GalleryProxies? Proxies { get; set; }

    /// <summary>Gemeinsamer Speicher für Bildangaben und kleine Vorschaubilder der Server-Ordner (beim Start gesetzt).</summary>
    public static GalleryShared? Shared { get; set; }

    /// <summary>Belegter Platz der großen Vorschauen eines Galerie-Ordners (gemeinsam: die des ganzen Server-Ordners).</summary>
    public static (int Count, long Bytes) ProxyUsage(UserSpace space, ServerConfig config, GallerySource src) =>
        Proxies == null ? (0, 0) : ProxyScope(space, config, src) is var sc ? Proxies.Usage(sc.A, sc.B) : (0, 0);

    /// <summary>
    /// Große Vorschauen genau dieses Galerie-Ordners: wie viele und wie viel Platz - auch die, die ohne „vorab“ beim Öffnen entstanden.
    /// Die Vorschauen liegen nach Pfad (gemeinsam: je Server-Ordner), gezählt wird über die Fotos im Index.
    /// </summary>
    public static (int Count, long Bytes) FolderProxyUsage(UserSpace space, ServerConfig config, GallerySource src)
    {
        if (Proxies == null) return (0, 0);
        Ensure(space.Db);
        var scope = ProxyScope(space, config, src);
        int n = 0; long bytes = 0;
        foreach (var p in space.Db.Query("SELECT path FROM gallery_ext WHERE source = $s AND mime LIKE 'image/%'", r => r.GetString(0), ("$s", src.Id)))
            if (Proxies.SizeOf(scope.A, scope.B, p) is > 0 and var b) { n++; bytes += b; }
        return (n, bytes);
    }

    /// <summary>Lässt ein anderes Konto für einen überlappenden Ordner desselben Server-Ordners große Vorschauen vorab erzeugen?</summary>
    public static bool ProxiesWantedElsewhere(IEnumerable<UserSpace> all, UserSpace space, ServerConfig config, GallerySource src)
    {
        if (!SharedMount(config, src.Mount)) return false;
        static bool Overlap(string a, string b) => a.Length == 0 || b.Length == 0 || a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);
        return all.Where(o => o.User.Id != space.User.Id).Any(o => Of(o.Db).Any(x => x.Proxies && x.Mount == src.Mount && Overlap(x.Path, src.Path)));
    }

    /// <summary>Große Vorschauen dieses Galerie-Ordners löschen (vorab erzeugte und beim Öffnen entstandene); gibt die Anzahl zurück.</summary>
    public static int DeleteFolderProxies(UserSpace space, ServerConfig config, GallerySource src)
    {
        if (Proxies == null) return 0;
        Ensure(space.Db);
        var scope = ProxyScope(space, config, src);
        var n = 0;
        foreach (var p in space.Db.Query("SELECT path FROM gallery_ext WHERE source = $s", r => r.GetString(0), ("$s", src.Id)))
            if (Proxies.SizeOf(scope.A, scope.B, p) > 0) { Proxies.Remove(scope.A, scope.B, p); n++; }
        return n;
    }

    /// <summary>Große Vorschauen eines Galerie-Ordners abgeschaltet: nur eigene löschen - gemeinsame braucht vielleicht noch jemand.</summary>
    public static void DropProxies(UserSpace space, ServerConfig config, GallerySource src)
    {
        if (!SharedMount(config, src.Mount)) Proxies?.RemoveSource(space.User.Id, src.Id);
    }

    /// <summary>Eine Datei eines Server-Ordners gibt es nicht mehr: Bildangaben, Vorschaubild und große Vorschau für alle weg.</summary>
    private static void ForgetShared(string mount, string path)
    {
        Shared?.Remove(mount, path);
        Proxies?.Remove(GalleryProxies.SharedScope, mount, path);
        ExternalFolders.Thumbs?.Remove(GalleryProxies.SharedScope, mount, path);
    }

    /// <summary>
    /// Gemeinsamen Bestand von Server-Ordnern löschen, die der Admin nicht mehr freigibt. Sonst wächst er mit: Vorschauen und
    /// Bildangaben bleiben, auch wenn jemand einen Ordner aus seiner Galerie nimmt - ein anderer braucht sie vielleicht.
    /// </summary>
    public static void PruneShared(ServerConfig config)
    {
        var freed = config.Current.ExternalFolders.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (Shared != null) foreach (var m in Shared.Mounts().Where(m => !freed.Contains(m))) Shared.RemoveMount(m);
        if (Proxies != null) foreach (var m in Proxies.SharedMounts().Where(m => !freed.Contains(m))) Proxies.RemoveShared(m);
        if (ExternalFolders.Thumbs != null) foreach (var m in ExternalFolders.Thumbs.SharedMounts().Where(m => !freed.Contains(m))) ExternalFolders.Thumbs.RemoveShared(m);
    }

    /// <summary>Server-Ordner (vom Admin freigegeben): für alle dieselben Dateien - Vorschauen und Bildangaben gemeinsam.</summary>
    internal static bool SharedMount(ServerConfig config, string mount) => config.Current.ExternalFolders.Any(x => x.Id == mount);

    /// <summary>Kennung des Server-Ordners, wenn Vorschauen und Bildangaben gemeinsam sind - sonst null (eigener WebDAV-Ordner).</summary>
    private static string? SharedOf(ServerConfig config, GallerySource src) => Shared != null && SharedMount(config, src.Mount) ? src.Mount : null;

    /// <summary>Wo die großen Vorschauen eines Galerie-Ordners liegen: gemeinsam je Server-Ordner oder je Benutzer und Ordner.</summary>
    private static (string A, string B) ProxyScope(UserSpace space, ServerConfig config, GallerySource src) =>
        SharedMount(config, src.Mount) ? (GalleryProxies.SharedScope, src.Mount) : (space.User.Id, src.Id);

    /// <summary>Leerer Wert in gallery_ext.thumb: das Vorschaubild liegt im gemeinsamen Speicher.</summary>
    private static readonly byte[] InShared = [];

    /// <summary>Stand der Vorschaubilder im Index: steigt er, werden RAW-Fotos neu eingelesen (2: Drehung wie in der Dateiablage).</summary>
    private const int ThumbsVersion = 2;
    private const string ThumbsKey = "gallery.thumbs";

    /// <summary>RAW-Vorschaubilder aus älterem Stand verwerfen; true, wenn danach neu eingelesen werden muss.</summary>
    public static bool Upgrade(UserDb db)
    {
        if (int.TryParse(db.Get(ThumbsKey), out var v) && v >= ThumbsVersion) return false;
        Ensure(db);
        var raws = db.Query("SELECT source, path FROM gallery_ext", r => (r.GetString(0), r.GetString(1))).Where(x => Thumbs.IsRaw(x.Item2)).ToList();
        foreach (var (s, p) in raws)
            db.Exec("UPDATE gallery_ext SET size = -1, thumb = NULL, big = NULL WHERE source = $s AND path = $p", ("$s", s), ("$p", p));
        db.Set(ThumbsKey, ThumbsVersion.ToString());
        return raws.Count > 0;
    }
    public static void Save(UserDb db, List<GallerySource> list) => db.SetJson(Key, list);

    /// <summary>Foto (auch RAW) oder Video - nach der Endung, wie die Dateiablage.</summary>
    public static string? KindOf(string name)
    {
        if (Thumbs.IsRaw(name)) return "image";
        var k = FileKinds.Of(new FileEntry(name, false, 0, "", null, DateTime.MinValue, DateTime.MinValue));
        return k is "image" or "video" ? k : null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<UserDb, bool> Ready = new();

    /// <summary>Tabelle anlegen; description und sidecar (Änderungszeit der gelesenen Sidecar-Datei, 0 = keine) kamen später dazu.</summary>
    public static void Ensure(UserDb db)
    {
        if (Ready.ContainsKey(db)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS gallery_ext(
        source TEXT NOT NULL, path TEXT NOT NULL, size INTEGER NOT NULL, modified INTEGER NOT NULL, mime TEXT,
        taken INTEGER, camera TEXT, lens TEXT, focal REAL, aperture REAL, exposure REAL, iso INTEGER, width INTEGER, height INTEGER,
        duration REAL, rating INTEGER, title TEXT, keywords TEXT, thumb BLOB, big BLOB, PRIMARY KEY(source, path))");
        var cols = db.Query("PRAGMA table_info(gallery_ext)", r => r.GetString(1)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!cols.Contains("description")) db.Exec("ALTER TABLE gallery_ext ADD COLUMN description TEXT");
        if (!cols.Contains("sidecar")) db.Exec("ALTER TABLE gallery_ext ADD COLUMN sidecar INTEGER NOT NULL DEFAULT 0");
        // Stufe des Einlesens: 0 nur aufgelistet (Platzhalter), 1 Bildangaben aus dem Kopf der Datei, 2 fertig (mit Vorschaubild).
        // Ältere Einträge sind fertig.
        if (!cols.Contains("stage")) db.Exec("ALTER TABLE gallery_ext ADD COLUMN stage INTEGER NOT NULL DEFAULT 2");
        // Farbmarkierung kam später dazu: Index und Trigger mit der neuen Spalte neu anlegen, Sidecar-Dateien einmal neu lesen
        if (!cols.Contains("label"))
        {
            db.Exec("ALTER TABLE gallery_ext ADD COLUMN label TEXT");
            db.Exec("DROP INDEX IF EXISTS gallery_ext_list");
            db.Exec("DROP TRIGGER IF EXISTS gallery_ext_ver_upd");
            db.Exec("UPDATE gallery_ext SET sidecar = -1 WHERE sidecar <> 0");
        }
        // Markierung wie Lightroom (xmpDM:pick) kam später dazu: wie bei der Farbmarkierung Index und Trigger neu, Sidecar-Dateien neu lesen
        if (!cols.Contains("pick"))
        {
            db.Exec("ALTER TABLE gallery_ext ADD COLUMN pick INTEGER");
            db.Exec("DROP INDEX IF EXISTS gallery_ext_list");
            db.Exec("DROP TRIGGER IF EXISTS gallery_ext_ver_upd");
            db.Exec("UPDATE gallery_ext SET sidecar = -1 WHERE sidecar <> 0");
        }
        // Ort aus Foto oder Sidecar - eigene Tabelle (die Liste mit ihrem Index bleibt, wie sie ist); ohne Zeile: noch nicht gelesen,
        // lat NULL: gelesen, ohne Ort. Beim Einlesen des Kopfs gefüllt, nach Geotagging gesetzt, bei geänderter Datei verworfen.
        db.Exec(@"CREATE TABLE IF NOT EXISTS gallery_ext_gps(source TEXT NOT NULL, path TEXT NOT NULL, lat REAL, lon REAL, PRIMARY KEY(source, path)) WITHOUT ROWID;
            CREATE TRIGGER IF NOT EXISTS gallery_ext_gps_del AFTER DELETE ON gallery_ext BEGIN
                DELETE FROM gallery_ext_gps WHERE source = old.source AND path = old.path;
            END;");
        db.Exec("CREATE INDEX IF NOT EXISTS gallery_ext_stage ON gallery_ext(source, stage)");
        // Stufen 2 und 3: neueste zuerst (gerade hinzugekommene Fotos bekommen ihr Vorschaubild vor dem Altbestand)
        db.Exec("CREATE INDEX IF NOT EXISTS gallery_ext_stage_new ON gallery_ext(source, stage, modified)");
        // RAW-Fotos ohne Aufnahmezeit einmal neu lesen (früher das falsche Unterverzeichnis bei NEF, ARW …); das Vorschaubild bleibt
        if (db.Get("gallery_ext.raw1") != "1")
        {
            db.Exec($"UPDATE gallery_ext SET stage = 0 WHERE taken IS NULL AND ({FileMeta.RawPaths("path")})");
            db.Set("gallery_ext.raw1", "1");
        }
        // Fotos ohne jede Bildangabe einmal neu lesen: im Foto-Editor gespeicherte kamen über den gemeinsamen Bestand ohne Aufnahmezeit
        // an (GalleryShared, Version 1) und standen beim Speicherdatum statt beim Aufnahmetag
        if (db.Get("gallery_ext.meta2") != "1")
        {
            db.Exec("UPDATE gallery_ext SET stage = 0 WHERE taken IS NULL AND camera IS NULL AND width IS NULL AND mime LIKE 'image/%'");
            db.Set("gallery_ext.meta2", "1");
        }
        // Fotos ganz ohne Vorschau einmal neu lesen: fiel der Speicher beim Lesen aus, kam eine halbe Datei an und der Eintrag galt
        // als fertig ohne Vorschaubild
        if (db.Get("gallery_ext.short1") != "1")
        {
            db.Exec("UPDATE gallery_ext SET stage = 0 WHERE stage = 2 AND thumb IS NULL AND mime LIKE 'image/%'");
            db.Set("gallery_ext.short1", "1");
        }
        // Die Liste aus einem Index mit allen ihren Spalten lesen: in der Tabelle stehen die Vorschaubilder (thumb, big) mitten in
        // der Zeile, description dahinter - ohne Index liest jede Abfrage bei zehntausenden Fotos über alle Bilddaten hinweg
        db.Exec(@"CREATE INDEX IF NOT EXISTS gallery_ext_list ON gallery_ext(source, path, size, modified, mime, taken, camera, lens, focal, aperture,
            exposure, iso, width, height, duration, rating, title, keywords, description, label, pick)");
        // Änderungszähler für den ETag der Liste: jede Änderung an dem, was die Liste zeigt, zählt hoch (Vorschaubilder nicht)
        db.Exec("CREATE TABLE IF NOT EXISTS gallery_ext_ver(id INTEGER PRIMARY KEY CHECK(id = 1), v INTEGER NOT NULL)");
        db.Exec("INSERT OR IGNORE INTO gallery_ext_ver(id, v) VALUES(1, 0)");
        db.Exec("CREATE TRIGGER IF NOT EXISTS gallery_ext_ver_ins AFTER INSERT ON gallery_ext BEGIN UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1; END");
        db.Exec("CREATE TRIGGER IF NOT EXISTS gallery_ext_ver_del AFTER DELETE ON gallery_ext BEGIN UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1; END");
        // Vorschaubild fertig (Stufe 2 erreicht): zählt auch - sonst bliebe auf der offenen Seite das Symbol stehen
        db.Exec("CREATE TRIGGER IF NOT EXISTS gallery_ext_ver_stage AFTER UPDATE OF stage ON gallery_ext WHEN (OLD.stage < 2) <> (NEW.stage < 2) BEGIN UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1; END");
        db.Exec(@"CREATE TRIGGER IF NOT EXISTS gallery_ext_ver_upd AFTER UPDATE OF size, modified, mime, taken, camera, lens, focal, aperture, exposure, iso,
            width, height, duration, rating, title, keywords, description, label, pick ON gallery_ext BEGIN UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1; END");
        // Der Ort zählt mit (Atlas, Detailbereich)
        db.Exec(@"CREATE TRIGGER IF NOT EXISTS gallery_ext_gps_ver_ins AFTER INSERT ON gallery_ext_gps BEGIN UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1; END;
            CREATE TRIGGER IF NOT EXISTS gallery_ext_gps_ver_del AFTER DELETE ON gallery_ext_gps BEGIN UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1; END;
            CREATE TRIGGER IF NOT EXISTS gallery_ext_gps_ver_upd AFTER UPDATE ON gallery_ext_gps BEGIN UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1; END;");
        // Der Ort aus Sidecar-Dateien kam später dazu: sie einmal neu lesen (klein; der Kopf der Fotos wird dafür nicht neu gelesen)
        if (db.Get("gallery_ext.gps1") != "1")
        {
            db.Exec("UPDATE gallery_ext SET sidecar = -1 WHERE sidecar <> 0");
            db.Set("gallery_ext.gps1", "1");
        }
        Ready[db] = true;
    }

    /// <param name="Pending">Bildangaben oder Vorschaubild stehen noch aus (Stufe 0 oder 1) - Teil des ETags der Kachel, damit die Seite
    /// das Vorschaubild holt, sobald es da ist.</param>
    public sealed record Entry(string Source, string Path, long Size, long Modified, string? Mime, FileMeta.Row? Meta, string? Description = null, bool Pending = false);

    /// <summary>
    /// Stand der Liste als ETag: Änderungszähler der Tabelle (Trigger) und welche Ordner sichtbar sind - sofort, ohne die
    /// Tabelle zu lesen. „f2“: Form der Antwort (ändert sie sich, bekommen alle eine neue Liste).
    /// </summary>
    public static string Version(UserSpace space, ServerConfig config) => VersionOf(space, config).Tag;

    /// <summary>ETag wie <see cref="Version"/> und dazu, welche Ordner der Benutzer sehen darf (Kennungen, sortiert).</summary>
    public static (string Tag, string Sources) VersionOf(UserSpace space, ServerConfig config)
    {
        Ensure(space.Db);
        var mounts = ExternalFolders.MountsOf(space, config).Select(m => m.Id).ToHashSet();
        var ok = string.Join(",", Of(space.Db).Where(s => mounts.Contains(s.Mount)).Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal));
        var sums = "f2-" + Convert.ToInt64(space.Db.Scalar("SELECT v FROM gallery_ext_ver WHERE id = 1") ?? 0L);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sums + "|" + ok)))[..20].ToLowerInvariant();
        return ("\"" + hash + "\"", ok);
    }

    /// <summary>Alle eingelesenen Fotos und Videos der Galerie-Ordner, die der Benutzer noch sehen darf.</summary>
    public static List<Entry> List(UserSpace space, ServerConfig config)
    {
        Ensure(space.Db);
        var mounts = ExternalFolders.MountsOf(space, config).Select(m => m.Id).ToHashSet();
        var ok = Of(space.Db).Where(s => mounts.Contains(s.Mount)).Select(s => s.Id).ToHashSet();
        // Ort aus der eigenen Tabelle dazu (aus dem Foto oder der Sidecar-Datei, nach dem Verorten)
        PhotoMarks.Ensure(space.Db);
        // Noch offen: über den Index der Stufen (wenige), nicht aus jeder Zeile
        var pending = space.Db.Query("SELECT source, path FROM gallery_ext INDEXED BY gallery_ext_stage WHERE stage < 2", r => r.GetString(0) + "/" + r.GetString(1)).ToHashSet(StringComparer.Ordinal);
        return space.Db.Query(EntrySql, EntryFrom)
            .Where(e => ok.Contains(e.Source)).Select(e => pending.Contains(e.Source + "/" + e.Path) ? e with { Pending = true } : e).ToList();
    }

    /// <summary>Einträge mit Ort; Angaben nur in der Datenbank (PhotoMarks) gehen vor denen aus der Datei.</summary>
    private static readonly string EntrySql = $@"SELECT e.source, e.path, e.size, e.modified, e.mime, e.taken, e.camera, e.lens, e.focal, e.aperture, e.exposure, e.iso, e.width, e.height,
                e.duration, {PhotoMarks.Col("e", "rating")}, {PhotoMarks.Col("e", "title")}, {PhotoMarks.Col("e", "keywords")}, {PhotoMarks.Col("e", "description")}, {PhotoMarks.Col("e", "label")}, g.lat, g.lon, {PhotoMarks.Col("e", "pick")}
                FROM gallery_ext e INDEXED BY gallery_ext_list LEFT JOIN gallery_ext_gps g ON g.source = e.source AND g.path = e.path
                LEFT JOIN photo_marks pm ON pm.key = 'ext:' || e.source || '/' || e.path";

    private static Entry EntryFrom(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4),
        new FileMeta.Row(r.GetString(1), N<long>(r, 5), S(r, 6), S(r, 7), N<double>(r, 8), N<double>(r, 9), N<double>(r, 10), N<int>(r, 11),
            N<double>(r, 20), N<double>(r, 21), N<int>(r, 12), N<int>(r, 13), N<double>(r, 14), N<int>(r, 15), S(r, 16), S(r, 17), S(r, 19), Pick: N<int>(r, 22)), S(r, 18));

    /// <summary>Etag einer Kachel: Stand der Datei, „-o“ solange Bildangaben oder Vorschaubild ausstehen, Fingerabdruck der Bearbeitung.</summary>
    public static string TileTag(Entry e, IReadOnlyDictionary<string, string> edits) =>
        PhotoEdits.Tag(e.Size.ToString("x") + "-" + e.Modified.ToString("x") + (e.Pending ? "-o" : ""), edits.GetValueOrDefault(PhotoEdits.KeyOfExt(e.Source + "/" + e.Path)));

    private static string? S(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static T? N<T>(Microsoft.Data.Sqlite.SqliteDataReader r, int i) where T : struct =>
        r.IsDBNull(i) ? null : (T)Convert.ChangeType(r.GetValue(i), typeof(T), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Einen Galerie-Ordner einlesen, Stufe 1: nur auflisten - neue und geänderte Dateien kommen als Platzhalter (Name, Größe,
    /// Datum) sofort in die Galerie, Verschwundenes geht raus, Sidecars werden gelesen (klein). Bildangaben und Vorschaubilder
    /// holen danach <see cref="HeadsAsync"/> (Kopf der Datei) und <see cref="FullAsync"/> (ganze Datei, nur wo nötig).
    /// Gibt die Zahl der Fotos und Videos zurück.
    /// </summary>
    public static async Task<int> ScanAsync(UserSpace space, ServerConfig config, GallerySource src, CancellationToken ct, Activity.Job? job = null)
    {
        var db = space.Db;
        Ensure(db);
        var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
        using (driver)
        {
            var known = db.Query("SELECT path, size, modified, sidecar FROM gallery_ext WHERE source = $s", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)), ("$s", src.Id))
                .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3, x.Item4), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var dirs = new Stack<string>();
            var fresh = 0;
            dirs.Push(StoragePath.Clean(src.Path));
            while (dirs.Count > 0 && seen.Count < MaxFiles)
            {
                ct.ThrowIfCancellationRequested();
                var dir = dirs.Pop();
                var entries = await driver.ListAsync(dir, ct);
                var sidecars = SidecarsIn(src, entries);
                var sidecarNames = sidecars.ToDictionary(x => x.Key, x => x.Value.Name);
                foreach (var e in entries)
                {
                    // Versteckte Ordner und Systemordner (Synology @eaDir, Papierkörbe) überspringen
                    if (e.Name.StartsWith('.') || e.Name.StartsWith('@') || e.Name.Equals("#recycle", StringComparison.OrdinalIgnoreCase)) continue;
                    // Konfliktkopien von Syncthing sind keine eigenen Fotos
                    if (e.Name.Contains(".sync-conflict-", StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = StoragePath.Join(dir, e.Name);
                    if (e.IsDir) { dirs.Push(rel); continue; }
                    if (KindOf(e.Name) is not { } kind) continue;
                    // Während des Einlesens aus der Galerie genommen: aufhören (sonst liest er weiter, und der neue Ordner wartet)
                    if (seen.Count % 50 == 0 && !Of(db).Any(s => s.Id == src.Id)) throw new SourceGone();
                    seen.Add(rel);
                    if (seen.Count % 200 == 0) job?.Progress(seen.Count);
                    if (!await SyncAsync(db, driver, src.Id, dir, rel, e, sidecars, sidecarNames, known.TryGetValue(rel, out var k) ? k : null, ct)) continue;
                    job?.Progress(seen.Count, null, $"{dir}: {++fresh:N0} neu gefunden");
                }
            }
            // Kein einziger Eintrag, aber Bestand da: der Speicher antwortet nicht richtig (ausgehängte Freigabe zeigt einen leeren
            // Ordner) - „antwortet nicht“ ist nicht „gelöscht“: nichts entfernen, als Fehler melden. Gelöscht wird nur, was ein
            // vollständiges Auflisten nicht mehr findet; ganz weg ist ein Ordner erst, wenn ihn die Person aus der Liste nimmt.
            if (seen.Count == 0 && known.Count > 0)
                throw new IOException("Der Ordner meldet keine einzige Datei – vermutlich gerade nicht erreichbar. Der Bestand bleibt.");
            // Was nicht mehr da ist, verschwindet aus der Galerie (nur nach einem vollständigen Durchgang)
            if (seen.Count < MaxFiles)
                // Im Foto-Editor gespeichert und noch auf dem Weg zum Speicher: bleibt
                foreach (var gone in known.Keys.Where(p => !seen.Contains(p) && !DmOffice.Dav.PhotoWork.IsPending(src.Id, p)))
                {
                    db.Exec("DELETE FROM gallery_ext WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", gone));
                    if (!SharedMount(config, src.Mount)) Proxies?.Remove(space.User.Id, src.Id, gone);
                    // Gemeinsamer Bestand: nur weg, wenn die Datei wirklich nicht mehr da ist (nachgefragt - ein kurzer Aussetzer
                    // des Speichers soll ihn nicht leeren); die Freigabe-Auswahl des Admins gilt für alle gleich
                    else if (await driver.StatAsync(gone, ct) == null) ForgetShared(src.Mount, gone);
                }
            return seen.Count;
        }
    }

    /// <summary>Sidecar-Dateien eines Ordners: Schlüssel = Name ohne „.xmp“, klein.</summary>
    private static Dictionary<string, DriverEntry> SidecarsIn(GallerySource src, IEnumerable<DriverEntry> entries) => src.Sidecars
        ? entries.Where(x => !x.IsDir && Sidecar.Is(x.Name) && x.Size <= Sidecar.MaxBytes).GroupBy(x => Sidecar.Key(x.Name)).ToDictionary(g => g.Key, g => g.First())
        : [];

    /// <summary>Einen Eintrag mit dem Stand im Ordner abgleichen; true, wenn etwas neu war (Datei oder Sidecar-Datei).</summary>
    private static async Task<bool> SyncAsync(UserDb db, IStorageDriver driver, string source, string dir, string rel, DriverEntry e, Dictionary<string, DriverEntry> sidecars,
        Dictionary<string, string> sidecarNames, (long Size, long Modified, long Sidecar)? known, CancellationToken ct)
    {
        var modified = new DateTimeOffset(DateTime.SpecifyKind(e.Modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var sc = Sidecar.For(e.Name, sidecarNames) is { } scName ? sidecars[Sidecar.Key(scName)] : null;
        var stamp = sc == null ? 0 : SidecarStamp(sc);
        var same = known is { } k && k.Size == e.Size && k.Modified == modified;
        if (same && known!.Value.Sidecar == stamp) return false;
        // Foto neu oder geändert, oder Sidecar weg bzw. abgeschaltet: als Platzhalter (Stufe 0) - die Angaben kommen wieder aus dem Foto
        if (!same || sc == null) Placeholder(db, source, rel, e.Size, modified, !same);
        if (sc != null) await ApplySidecarAsync(db, driver, source, rel, StoragePath.Join(dir, sc.Name), stamp, ct);
        return true;
    }

    /// <summary>
    /// Neu oder geändert: Eintrag mit Name, Größe, Datum (Stufe 0). Geänderte Datei: Vorschaubilder verwerfen. Angaben aus der
    /// Sidecar-Datei zurücksetzen - gibt es sie noch, liest der Durchgang sie gleich danach wieder ein.
    /// </summary>
    private static void Placeholder(UserDb db, string source, string rel, long size, long modified, bool changed)
    {
        db.Exec("DELETE FROM gallery_ext_gps WHERE source = $s AND path = $p", ("$s", source), ("$p", rel));
        db.Exec(@"INSERT INTO gallery_ext(source, path, size, modified, mime, stage) VALUES($s, $p, $z, $m, $mi, 0)
                  ON CONFLICT(source, path) DO UPDATE SET size = excluded.size, modified = excluded.modified, mime = excluded.mime, stage = 0, sidecar = 0, description = NULL,
                  thumb = CASE WHEN $c THEN NULL ELSE gallery_ext.thumb END, big = CASE WHEN $c THEN NULL ELSE gallery_ext.big END",
            ("$s", source), ("$p", rel), ("$z", size), ("$m", modified), ("$mi", DavServer.Mime(rel)), ("$c", changed ? 1 : 0));
    }

    /// <summary>So viel vom Anfang einer Datei: bei JPEG/HEIC stehen die Bildangaben vorn, bei RAW auch das eingebettete Vorschau-JPEG.</summary>
    private const int HeadBytes = 512 * 1024, RawHeadBytes = 6 * 1024 * 1024;

    /// <summary>Wie viele Einträge eines Ordners in welcher Stufe sind: (alle, ohne Bildangaben, ohne Vorschaubild).</summary>
    public static (int Total, int Listed, int NoThumb) Stages(UserDb db, string source)
    {
        Ensure(db);
        var by = db.Query("SELECT stage, COUNT(*) FROM gallery_ext INDEXED BY gallery_ext_stage WHERE source = $s GROUP BY stage", r => (r.GetInt32(0), r.GetInt32(1)), ("$s", source))
            .ToDictionary(x => x.Item1, x => x.Item2);
        return (by.Values.Sum(), by.GetValueOrDefault(0), by.GetValueOrDefault(1));
    }

    /// <summary>
    /// Stufe 2: aus dem Kopf jeder Datei (½ MB, RAW 6 MB) Bildangaben und - bei RAW und kleinen Dateien - das Vorschaubild.
    /// Arbeitet bis <paramref name="until"/>; Rest beim nächsten Durchgang.
    /// </summary>
    // Nicht lesbare Dateien kommen später wieder dran, damit die übrigen nicht hinter ihnen warten: Die Abfrage beginnt immer bei den neuesten.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> Later = new();
    public static TimeSpan RetryAfter { get; set; } = TimeSpan.FromHours(1);

    private static void TryLater(string source, string rel) => Later.GetOrAdd(source, _ => new())[rel] = DateTime.UtcNow + RetryAfter;

    /// <summary>Dateien dieses Ordners, die gerade auf den nächsten Versuch warten.</summary>
    public static HashSet<string> RetryLater(string source)
    {
        if (!Later.TryGetValue(source, out var map)) return [];
        var now = DateTime.UtcNow;
        foreach (var (rel, at) in map) if (at <= now) map.TryRemove(rel, out _);
        return map.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Auf Knopfdruck: auch die wartenden gleich noch einmal versuchen.</summary>
    public static void RetryNow(string source) => Later.TryRemove(source, out _);

    public static async Task<int> HeadsAsync(UserSpace space, ServerConfig config, GallerySource src, DateTime until, Activity.Job? job, CancellationToken ct)
    {
        var db = space.Db;
        var done = 0;
        var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
        using (driver)
            while (DateTime.UtcNow < until)
            {
                var later = RetryLater(src.Id);
                var rows = db.Query($"SELECT path, size, thumb IS NOT NULL, modified FROM gallery_ext INDEXED BY gallery_ext_stage_new WHERE source = $s AND stage = 0 ORDER BY modified DESC LIMIT {200 + later.Count}",
                    r => (r.GetString(0), r.GetInt64(1), r.GetBoolean(2), r.GetInt64(3)), ("$s", src.Id)).Where(x => !later.Contains(x.Item1)).ToList();
                if (rows.Count == 0) break;
                foreach (var (rel, size, hasThumb, modified) in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (done % 50 == 0 && !Of(db).Any(s => s.Id == src.Id)) throw new SourceGone();
                    if (!await ReadHeadOfAsync(space, config, driver, src, rel, size, modified, hasThumb, null, ct)) { TryLater(src.Id, rel); continue; }
                    job?.Progress(++done, null, $"{src.Path}: Bildangaben {done:N0}");
                }
            }
        return done;
    }

    /// <summary>
    /// Eine Datei aus dem Kopf einlesen: Bildangaben und - bei RAW, kleinen Dateien oder eingebetteter Vorschau - das Vorschaubild.
    /// <paramref name="again"/>: bewusst erneut lesen (Stufe des Eintrags), auch wenn der gemeinsame Speicher den Stand kennt. Einen fertigen
    /// Eintrag lässt das bei unlesbarem Kopf unverändert und schickt ihn nicht noch einmal durch das Lesen der ganzen Datei.
    /// False: nicht lesbar (weg oder später noch einmal).
    /// </summary>
    private static async Task<bool> ReadHeadOfAsync(UserSpace space, ServerConfig config, IStorageDriver driver, GallerySource src, string rel, long size, long modified, bool hasThumb, int? again, CancellationToken ct)
    {
        var db = space.Db;
        var shared = SharedOf(config, src);
        var kind = KindOf(rel);
        var fileStamp = GalleryProxies.Stamp(size, modified);
        // Schon gelesen (von jemand anderem oder früher): nichts von der Platte holen
        if (again == null && shared != null && Shared!.Find(shared, rel, fileStamp) is { } hit && Usable(rel, hit))
        {
            SaveMeta(db, src.Id, rel, hit.Meta, hit.HasThumb ? InShared : null, hit.Stage >= 2 ? 2 : 1);
            return true;
        }
        FileMeta.Row? meta = null;
        byte[]? thumb = null;
        var stage = 2;
        var whole = false;
        try
        {
            if (kind == "image" && size <= MaxImageBytes)
            {
                var raw = Thumbs.IsRaw(rel);
                var head = await ReadHeadAsync(driver, rel, raw ? RawHeadBytes : HeadBytes, ct);
                // Kürzer als die Datei und als der gewünschte Kopf: abgebrochen gelesen - später noch einmal
                if (head.Length < Math.Min(size, raw ? RawHeadBytes : HeadBytes)) throw new IOException("Datei nur teilweise gelesen.");
                try { meta = FileMeta.Read(rel, new MemoryStream(head), space.Zone); } catch (Exception e) when (e is not OperationCanceledException) { }
                whole = head.Length >= size;
                if (!hasThumb && (raw || whole))
                    try { thumb = raw ? Thumbs.FromRaw(head, Thumbs.Small) : Thumbs.FromBytes(head, Thumbs.Small); } catch (Exception e) when (e is not OperationCanceledException) { }
                // Großes JPEG mit eingebetteter Vorschau (MPF): nur diesen Ausschnitt holen statt der ganzen Datei
                if (!hasThumb && thumb == null && !raw && !whole && Thumbs.MpfPreview(head) is { } mp && mp.Offset + mp.Length <= size)
                    try { thumb = Thumbs.FromPreview(await driver.ReadRangeAsync(rel, mp.Offset, mp.Length, ct), head, Thumbs.Small); }
                    catch (Exception e) when (e is IOException or HttpRequestException or KeyNotFoundException or UnauthorizedAccessException) { }
                // Ohne Vorschaubild (JPEG ganz lesen) oder ohne Bildangaben (stehen weiter hinten): Stufe 3
                if (!hasThumb && thumb == null || meta == null && !whole) stage = 1;
            }
            else if (kind == "video")
            {
                // Videos nur aus einem Ordner auf dem Server auswerten (springen im Inhalt) - das Standbild macht der Browser
                await using var s = await driver.OpenReadAsync(rel, ct);
                if (s.CanSeek) meta = FileMeta.Read(rel, s, space.Zone);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException) { await GoneAsync(space, config, driver, src, rel, ct); return false; }   // weg oder später noch einmal
        if (again == 2)
        {
            stage = 2;
            // Im Kopf steht nichts Brauchbares: was früher aus der ganzen Datei kam, bleibt
            if (meta == null || !whole && meta is { Taken: null, Camera: null, Width: null, Duration: null })
            {
                if (thumb == null) return true;
                if (shared != null) { Shared!.Put(shared, rel, fileStamp, 2, Shared.Find(shared, rel, fileStamp)?.Meta, thumb); thumb = InShared; }
                db.Exec("UPDATE gallery_ext SET thumb = $t WHERE source = $s AND path = $p", ("$t", thumb), ("$s", src.Id), ("$p", rel));
                return true;
            }
        }
        if (shared != null)
        {
            Shared!.Put(shared, rel, fileStamp, stage, meta, thumb);
            SaveMeta(db, src.Id, rel, meta, thumb != null ? InShared : null, stage);
        }
        else SaveMeta(db, src.Id, rel, meta, thumb, stage);
        return true;
    }

    /// <summary>
    /// Stufe 3: die ganze Datei, nur wo der Kopf nicht reichte (JPEG-Vorschaubild, Bildangaben weiter hinten) - dabei gleich
    /// die große Vorschau, wenn sie eingeschaltet ist. Arbeitet bis <paramref name="until"/>.
    /// </summary>
    public static async Task<int> FullAsync(UserSpace space, ServerConfig config, GallerySource src, DateTime until, Activity.Job? job, CancellationToken ct)
    {
        var db = space.Db;
        var done = 0;
        var shared = SharedOf(config, src);
        var scope = ProxyScope(space, config, src);
        var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
        using (driver)
            while (DateTime.UtcNow < until)
            {
                var later = RetryLater(src.Id);
                var rows = db.Query($"SELECT path, size, modified, taken IS NULL AND camera IS NULL AND width IS NULL, thumb IS NOT NULL FROM gallery_ext INDEXED BY gallery_ext_stage_new WHERE source = $s AND stage = 1 ORDER BY modified DESC LIMIT {100 + later.Count}",
                    r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetBoolean(3), r.GetBoolean(4)), ("$s", src.Id)).Where(x => !later.Contains(x.Item1)).ToList();
                if (rows.Count == 0) break;
                foreach (var (rel, size, modified, noMeta, hasThumb) in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (done % 25 == 0 && !Of(db).Any(s => s.Id == src.Id)) throw new SourceGone();
                    var fileStamp = GalleryProxies.Stamp(size, modified);
                    if (shared != null && Shared!.Find(shared, rel, fileStamp) is { Stage: >= 2 } hit && Usable(rel, hit))
                    {
                        if (hit.Meta != null) SaveMeta(db, src.Id, rel, hit.Meta, hit.HasThumb ? InShared : null, 2);
                        else db.Exec("UPDATE gallery_ext SET thumb = COALESCE($t, thumb), stage = 2 WHERE source = $s AND path = $p", ("$t", hit.HasThumb ? InShared : null), ("$s", src.Id), ("$p", rel));
                        job?.Progress(++done, null, $"{src.Path}: Vorschaubilder {done:N0}");
                        continue;
                    }
                    byte[] bytes;
                    try { bytes = await ReadAllAsync(driver, rel, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException) { if (!await GoneAsync(space, config, driver, src, rel, ct)) TryLater(src.Id, rel); continue; }
                    var raw = Thumbs.IsRaw(rel);
                    FileMeta.Row? meta = null;
                    byte[]? thumb = null;
                    // Gemeinsam: alles aus der Datei, damit es für alle reicht
                    if (noMeta || shared != null) try { meta = FileMeta.Read(rel, new MemoryStream(bytes), space.Zone); } catch (Exception e) when (e is not OperationCanceledException) { }
                    if (!hasThumb || shared != null) try { thumb = raw ? Thumbs.FromRaw(bytes, Thumbs.Small) : Thumbs.FromBytes(bytes, Thumbs.Small); } catch (Exception e) when (e is not OperationCanceledException) { }
                    if (src.Proxies && Proxies != null)
                        try { if ((raw ? Thumbs.FromRaw(bytes, GalleryProxies.Size) : Thumbs.FromBytes(bytes, GalleryProxies.Size)) is { } big) Proxies.Put(scope.A, scope.B, rel, fileStamp, big); }
                        catch (Exception e) when (e is not OperationCanceledException) { }
                    if (shared != null)
                    {
                        Shared!.Put(shared, rel, fileStamp, 2, meta, thumb);
                        if (thumb != null) thumb = InShared;
                    }
                    // Fertig, auch wenn sich nichts lesen ließ (kaputte Datei) - sonst käme sie jedes Mal wieder
                    if (meta != null) SaveMeta(db, src.Id, rel, meta, thumb, 2);
                    else db.Exec("UPDATE gallery_ext SET thumb = COALESCE($t, thumb), stage = 2 WHERE source = $s AND path = $p", ("$t", thumb), ("$s", src.Id), ("$p", rel));
                    job?.Progress(++done, null, $"{src.Path}: Vorschaubilder {done:N0}");
                }
            }
        return done;
    }

    /// <summary>Bildangaben eintragen - Titel, Stichwörter und Bewertung aus einer Sidecar-Datei bleiben (sie gehen vor).</summary>
    private static void SaveMeta(UserDb db, string source, string rel, FileMeta.Row? meta, byte[]? thumb, int stage)
    {
        db.Exec(@"UPDATE gallery_ext SET taken = $t, camera = $c, lens = $l, focal = $f, aperture = $a, exposure = $x, iso = $i, width = $w, height = $h, duration = $d,
                  rating = CASE WHEN sidecar <> 0 THEN rating ELSE $r END, title = CASE WHEN sidecar <> 0 THEN title ELSE $ti END,
                  keywords = CASE WHEN sidecar <> 0 THEN keywords ELSE $k END, label = CASE WHEN sidecar <> 0 THEN label ELSE $lb END,
                  pick = CASE WHEN sidecar <> 0 THEN pick ELSE $pk END,
                  description = CASE WHEN sidecar <> 0 THEN description ELSE $de END,
                  thumb = COALESCE($th, thumb), stage = $st
                  WHERE source = $s AND path = $p AND (stage <> $st OR $th IS NOT NULL OR taken IS NOT $t OR camera IS NOT $c OR lens IS NOT $l OR focal IS NOT $f
                      OR aperture IS NOT $a OR exposure IS NOT $x OR iso IS NOT $i OR width IS NOT $w OR height IS NOT $h OR duration IS NOT $d
                      OR (sidecar = 0 AND (rating IS NOT $r OR title IS NOT $ti OR keywords IS NOT $k OR label IS NOT $lb OR pick IS NOT $pk OR description IS NOT $de)))",
            ("$t", meta?.Taken), ("$c", meta?.Camera), ("$l", meta?.Lens), ("$f", meta?.Focal), ("$a", meta?.Aperture), ("$x", meta?.Exposure), ("$i", meta?.Iso),
            ("$w", meta?.Width), ("$h", meta?.Height), ("$d", meta?.Duration), ("$r", meta?.Rating), ("$ti", meta?.Title), ("$k", meta?.Keywords), ("$th", thumb),
            ("$lb", meta?.Label), ("$de", meta?.Description), ("$pk", meta?.Pick), ("$st", stage), ("$s", source), ("$p", rel));
        // Ort aus dem Foto merken - nur einen vorhandenen: Angaben aus dem gemeinsamen Bestand kommen ohne Ort, „ohne Ort“ stellt erst das
        // Verorten selbst fest (liest dafür den Kopf der Datei)
        if (meta?.Lat != null && Gps(db, source, rel) is var g && (!g.Known || g.Lat != meta.Lat || g.Lon != meta.Lon)) SetGps(db, source, rel, meta.Lat, meta.Lon);
    }

    /// <summary>Titel, Beschreibung, Bewertung und Stichwörter nach dem Schreiben ins JPEG eintragen (null = bleibt, leer bzw. 0 = entfernt).</summary>
    public static void SetInfo(UserDb db, string source, string rel, PhotoMeta.Fields f)
    {
        Ensure(db);
        var (title, description, rating) = (f.Title, f.Description, f.Rating);
        string? keywords = null;
        if (f.HasKeywords)
        {
            var old = db.Scalar("SELECT keywords FROM gallery_ext WHERE source = $s AND path = $p", ("$s", source), ("$p", rel)) as string;
            var list = f.KeywordsFrom((old ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
            keywords = list.Count > 0 ? string.Join("\n", list) : null;
        }
        db.Exec(@"UPDATE gallery_ext SET title = CASE WHEN $st THEN $t ELSE title END, description = CASE WHEN $sd THEN $d ELSE description END,
                  rating = CASE WHEN $sr THEN $r ELSE rating END, keywords = CASE WHEN $sk THEN $k ELSE keywords END,
                  pick = CASE WHEN $sp THEN $pk ELSE pick END, label = CASE WHEN $sl THEN $lb ELSE label END WHERE source = $s AND path = $p",
            ("$st", title != null), ("$t", string.IsNullOrWhiteSpace(title) ? null : title.Trim()),
            ("$sd", description != null), ("$d", string.IsNullOrWhiteSpace(description) ? null : description.Trim()),
            ("$sr", rating != null), ("$r", rating is > 0 ? rating : null), ("$sk", f.HasKeywords), ("$k", keywords),
            ("$sp", f.Pick != null), ("$pk", f.Pick is int pv && pv != 0 ? Math.Sign(pv) : null),
            ("$sl", f.Label != null), ("$lb", string.IsNullOrEmpty(f.Label) ? null : f.Label), ("$s", source), ("$p", rel));
    }

    /// <summary>Ort eines Fotos im Galerie-Ordner merken (lat/lon null: gelesen, ohne Ort).</summary>
    public static void SetGps(UserDb db, string source, string rel, double? lat, double? lon)
    {
        Ensure(db);
        if (lat == null || lon == null) lat = lon = null;
        db.Exec("INSERT OR REPLACE INTO gallery_ext_gps(source, path, lat, lon) VALUES($s, $p, $la, $lo)", ("$s", source), ("$p", rel), ("$la", lat), ("$lo", lon));
    }

    /// <summary>Bekannter Ort: (true, lat, lon) - lat null heißt „gelesen, ohne Ort“; (false, …), wenn noch nicht gelesen.</summary>
    public static (bool Known, double? Lat, double? Lon) Gps(UserDb db, string source, string rel)
    {
        Ensure(db);
        return db.Query("SELECT lat, lon FROM gallery_ext_gps WHERE source = $s AND path = $p",
            r => (true, r.IsDBNull(0) ? (double?)null : r.GetDouble(0), r.IsDBNull(1) ? (double?)null : r.GetDouble(1)), ("$s", source), ("$p", rel)).FirstOrDefault();
    }

    /// <summary>Der Galerie-Ordner ist auf dem Speicher da und nicht leer (sonst ist eher der Speicher weg als die Datei).</summary>
    private static async Task<bool> RootPresentAsync(IStorageDriver driver, GallerySource src, CancellationToken ct)
    {
        var root = StoragePath.Clean(src.Path);
        if (root.Length > 0 && (await driver.StatAsync(root, ct))?.IsDir != true) return false;
        return (await driver.ListAsync(root, ct)).Count > 0;
    }

    /// <summary>
    /// Lesen schlug fehl: ist die Datei inzwischen weg (verschoben, gelöscht), gleich aus der Galerie nehmen - sonst stünde sie bis zum
    /// nächsten Einlesen ohne Vorschaubild da und würde bei jedem Durchgang wieder versucht. Sonst (Aussetzer) beim nächsten Mal noch einmal.
    /// </summary>
    private static async Task<bool> GoneAsync(UserSpace space, ServerConfig config, IStorageDriver driver, GallerySource src, string rel, CancellationToken ct)
    {
        try
        {
            if (await driver.StatAsync(rel, ct) != null) return false;
            // Nicht gefunden - aber ist der Ordner überhaupt da? Eine ausgehängte Freigabe meldet jede Datei als fehlend.
            if (!await RootPresentAsync(driver, src, ct)) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or KeyNotFoundException) { return false; }
        space.Db.Exec("DELETE FROM gallery_ext WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", rel));
        if (!SharedMount(config, src.Mount)) Proxies?.Remove(space.User.Id, src.Id, rel);
        else ForgetShared(src.Mount, rel);
        return true;
    }

    private static async Task<byte[]> ReadHeadAsync(IStorageDriver driver, string rel, int max, CancellationToken ct)
    {
        await using var s = await driver.OpenReadAsync(rel, ct);
        var buf = new byte[max];
        int n = 0, r;
        while (n < max && (r = await s.ReadAsync(buf.AsMemory(n, max - n), ct)) > 0) n += r;
        return n == max ? buf : buf[..n];
    }

    /// <summary>Stand einer Sidecar-Datei (Zeit und Größe): ändert er sich, liest das Einlesen sie neu.</summary>
    public static long SidecarStamp(DriverEntry sc) => Math.Max(1, new DateTimeOffset(DateTime.SpecifyKind(sc.Modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds() ^ sc.Size);

    /// <summary>Titel, Beschreibung, Stichwörter und Bewertung aus der Sidecar-Datei - sie geht den Angaben im Foto vor.</summary>
    private static async Task ApplySidecarAsync(UserDb db, IStorageDriver driver, string source, string rel, string sidecar, long stamp, CancellationToken ct)
    {
        Sidecar.Data? d = null;
        try { d = Sidecar.Read(await ReadAllAsync(driver, sidecar, ct)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException) { return; }
        ApplySidecar(db, source, rel, d, stamp);
    }

    /// <summary>Gelesene Sidecar-Datei eintragen (auch gleich nach dem Schreiben aus der Galerie, mit ihrem neuen Stand).</summary>
    public static void ApplySidecar(UserDb db, string source, string rel, Sidecar.Data? d, long stamp)
    {
        Ensure(db);
        if (d?.Lat != null) SetGps(db, source, rel, d.Lat, d.Lon);
        // Nur ein Ort in der Sidecar-Datei (etwa von hier verortet): Titel und Bewertung bleiben die aus dem Foto
        if (d == null || d is { Title: null, Description: null, Rating: null, Label: null, Pick: null, Keywords.Count: 0 })
            db.Exec("UPDATE gallery_ext SET sidecar = $st WHERE source = $s AND path = $p", ("$st", stamp), ("$s", source), ("$p", rel));
        else
            db.Exec("UPDATE gallery_ext SET title = $t, description = $d, keywords = $k, rating = $r, label = $lb, pick = $pk, sidecar = $st WHERE source = $s AND path = $p",
                ("$t", d.Title), ("$d", d.Description), ("$k", d.Keywords.Count > 0 ? string.Join("\n", d.Keywords) : null), ("$r", d.Rating), ("$lb", d.Label), ("$pk", d.Pick),
                ("$st", stamp), ("$s", source), ("$p", rel));
    }

    // ---------------- Abgleich beim Ansehen: das gezeigte Foto und die sichtbaren Kacheln ----------------

    /// <summary>Ergebnis des Abgleichs eines Eintrags. Changed: etwas, das die Galerie zeigt, ist anders; FileChanged: Größe oder Zeit der Datei.</summary>
    public sealed record Seen(string Id, bool Reachable, bool Changed, bool FileChanged, bool Gone, Entry? Entry, string? Etag, bool HasThumb);

    /// <summary>Items: was sich geändert hat. Open: Kennungen, die in diesem Aufruf nicht drankamen (Obergrenze, Zeit, schon ein Aufruf in Arbeit).</summary>
    public sealed record VisibleResult(List<Seen> Items, List<string> Open, bool Reachable);

    public const int VisibleMax = 40, VisibleReads = 8;
    public static TimeSpan VisibleBudget { get; set; } = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RefreshBudget = TimeSpan.FromSeconds(30), RefreshWait = TimeSpan.FromSeconds(5);

    /// <summary>Je Benutzer ein Abgleich zugleich: Anbieter wie pCloud begrenzen die Abrufe.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SeenGates = new();
    private static SemaphoreSlim SeenGate(string userId) => SeenGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    private readonly record struct State(Entry Entry, int Stage, bool HasThumb, long Sidecar);

    private static State? StateOf(UserDb db, string source, string rel)
    {
        PhotoMarks.Ensure(db);
        var row = db.Query("SELECT stage, thumb IS NOT NULL, sidecar FROM gallery_ext WHERE source = $s AND path = $p",
            r => (Stage: r.GetInt32(0), Thumb: r.GetBoolean(1), Sidecar: r.GetInt64(2)), ("$s", source), ("$p", rel));
        if (row.Count == 0) return null;
        var entry = db.Query(EntrySql + " WHERE e.source = $s AND e.path = $p", EntryFrom, ("$s", source), ("$p", rel)).First();
        return new State(entry with { Pending = row[0].Stage < 2 }, row[0].Stage, row[0].Thumb, row[0].Sidecar);
    }

    private static Seen SeenOf(UserSpace space, string id, State before, State? after)
    {
        if (after is not { } now) return new Seen(id, true, true, true, true, null, null, false);
        var file = before.Entry.Size != now.Entry.Size || before.Entry.Modified != now.Entry.Modified;
        return new Seen(id, true, file || before.Entry != now.Entry || before.HasThumb != now.HasThumb, file, false, now.Entry, TileTag(now.Entry, PhotoEdits.Fingerprints(space.Db)), now.HasThumb);
    }

    /// <summary>Datei geändert: die große Vorschau gehört zum alten Stand.</summary>
    private static void DropProxy(UserSpace space, ServerConfig config, GallerySource src, string rel)
    {
        var scope = ProxyScope(space, config, src);
        Proxies?.Remove(scope.A, scope.B, rel);
    }

    /// <summary>
    /// Ein gezeigtes Foto abgleichen: Stand im Ordner vergleichen und den Kopf der Datei neu lesen, auch bei gleicher Größe und Zeit
    /// (manche Programme ändern Markierungen, ohne dass sich das zeigt). Nicht erreichbar oder Lesefehler: Eintrag bleibt, wie er ist.
    /// </summary>
    public static async Task<Seen> RefreshAsync(UserSpace space, ServerConfig config, string id, CancellationToken ct)
    {
        var (src, rel) = Resolve(space, config, id);
        var db = space.Db;
        Ensure(db);
        if (StateOf(db, src.Id, rel) is not { } before) throw new KeyNotFoundException("Nicht gefunden.");
        var same = SeenOf(space, id, before, before);
        // Im Foto-Editor gespeichert und noch auf dem Weg zum Speicher: dort steht noch der alte Stand
        if (PhotoWork.IsPending(src.Id, rel)) return same;
        if (!await ExternalFolders.ReachableAsync(space, config, src.Mount)) return same with { Reachable = false };
        var gate = SeenGate(space.User.Id);
        try { if (!await gate.WaitAsync(RefreshWait, ct)) return same; }
        catch (OperationCanceledException) { return same; }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(RefreshBudget);
            var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
            using (driver)
            {
                // Die Auflistung wie beim Einlesen: dieselbe Zeit wie im Index, dazu die Sidecar-Datei
                var dir = StoragePath.Parent(rel);
                var entries = await driver.ListAsync(dir, limit.Token);
                var name = rel[(rel.LastIndexOf('/') + 1)..];
                if (entries.FirstOrDefault(x => !x.IsDir && x.Name == name) is not { } e)
                    await GoneAsync(space, config, driver, src, rel, limit.Token);
                else
                {
                    var sidecars = SidecarsIn(src, entries);
                    await SyncAsync(db, driver, src.Id, dir, rel, e, sidecars, sidecars.ToDictionary(x => x.Key, x => x.Value.Name), (before.Entry.Size, before.Entry.Modified, before.Sidecar), limit.Token);
                    if (StateOf(db, src.Id, rel) is { } now)
                    {
                        if (now.Entry.Size != before.Entry.Size || now.Entry.Modified != before.Entry.Modified) DropProxy(space, config, src, rel);
                        await ReadHeadOfAsync(space, config, driver, src, rel, now.Entry.Size, now.Entry.Modified, now.HasThumb, now.Stage, limit.Token);
                    }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }   // Speicher antwortet nicht: bleibt still, nächstes Mal wieder
        finally { gate.Release(); }
        return SeenOf(space, id, before, StateOf(db, src.Id, rel));
    }

    /// <summary>
    /// Sichtbare Kacheln abgleichen: je Ordner einmal auflisten, Geändertes zurücksetzen wie beim Einlesen, dazu von noch nicht
    /// eingelesenen Einträgen höchstens <see cref="VisibleReads"/> den Kopf der Datei lesen. Fehlt eine Datei in der Auflistung,
    /// entscheidet das nächste vollständige Einlesen. Fremde Kennungen fallen heraus.
    /// </summary>
    public static async Task<VisibleResult> VisibleAsync(UserSpace space, ServerConfig config, IEnumerable<string> ids, CancellationToken ct)
    {
        var db = space.Db;
        Ensure(db);
        var mounts = ExternalFolders.MountsOf(space, config).Select(m => m.Id).ToHashSet();
        var sources = Of(db).Where(s => mounts.Contains(s.Mount)).ToDictionary(s => s.Id);
        var rows = new List<(string Id, GallerySource Src, string Rel, State Before)>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(VisibleMax))
        {
            var slash = id.IndexOf('/');
            if (slash <= 0 || !sources.TryGetValue(id[..slash], out var src)) continue;
            var rel = StoragePath.Clean(id[(slash + 1)..]);
            if (!PhotoWork.IsPending(src.Id, rel) && StateOf(db, src.Id, rel) is { } st) rows.Add((id, src, rel, st));
        }
        if (rows.Count == 0) return new VisibleResult([], [], true);
        var gate = SeenGate(space.User.Id);
        if (!gate.Wait(0)) return new VisibleResult([], rows.Select(r => r.Id).ToList(), true);
        var reachable = true;
        var tried = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(VisibleBudget);
            foreach (var group in rows.GroupBy(r => r.Src.Id))
            {
                var src = group.First().Src;
                if (!await ExternalFolders.ReachableAsync(space, config, src.Mount)) { reachable = false; continue; }
                var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
                using (driver)
                {
                    foreach (var byDir in group.GroupBy(r => StoragePath.Parent(r.Rel)))
                    {
                        var entries = await driver.ListAsync(byDir.Key, limit.Token);
                        var sidecars = SidecarsIn(src, entries);
                        var sidecarNames = sidecars.ToDictionary(x => x.Key, x => x.Value.Name);
                        foreach (var r in byDir)
                        {
                            var name = r.Rel[(r.Rel.LastIndexOf('/') + 1)..];
                            if (entries.FirstOrDefault(x => !x.IsDir && x.Name == name) is not { } e) continue;
                            await SyncAsync(db, driver, src.Id, byDir.Key, r.Rel, e, sidecars, sidecarNames, (r.Before.Entry.Size, r.Before.Entry.Modified, r.Before.Sidecar), limit.Token);
                            if (StateOf(db, src.Id, r.Rel) is { } now && (now.Entry.Size != r.Before.Entry.Size || now.Entry.Modified != r.Before.Entry.Modified)) DropProxy(space, config, src, r.Rel);
                        }
                    }
                    foreach (var r in group)
                    {
                        if (tried.Count >= VisibleReads) break;
                        if (StateOf(db, src.Id, r.Rel) is not { Stage: 0 } now) continue;
                        tried.Add(r.Id);
                        await ReadHeadOfAsync(space, config, driver, src, r.Rel, now.Entry.Size, now.Entry.Modified, now.HasThumb, null, limit.Token);
                    }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }   // Speicher antwortet nicht oder Zeit um: was fertig ist, zählt
        finally { gate.Release(); }
        var items = new List<Seen>();
        var open = new List<string>();
        foreach (var r in rows)
        {
            var now = StateOf(db, r.Src.Id, r.Rel);
            if (now is { Stage: 0 } && !tried.Contains(r.Id) && reachable) open.Add(r.Id);
            var seen = SeenOf(space, r.Id, r.Before, now);
            if (seen.Changed) items.Add(seen);
        }
        return new VisibleResult(items, open, reachable);
    }

    /// <summary>Höchstens so große Fotos ganz einlesen (große RAW-Dateien haben um 100 MB) - darüber keine Vorschau.</summary>
    private const long MaxRead = 200L * 1024 * 1024;

    private static async Task<byte[]> ReadAllAsync(IStorageDriver driver, string rel, CancellationToken ct)
    {
        await using var s = await driver.OpenReadAsync(rel, ct);
        if (s.CanSeek && s.Length > MaxRead) throw new IOException("Zu groß für eine Vorschau.");
        // Bekannte Länge: gleich in passender Größe lesen - ein wachsender Puffer mit Kopie am Ende brauchte bei einem RAW
        // von 50 MB kurz rund das Dreifache
        if (s.CanSeek && s.Length is > 0 and < int.MaxValue)
        {
            var buf = new byte[s.Length];
            var n = await s.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct);
            // Weniger als angekündigt: der Speicher hat mittendrin aufgehört (NAS weg) - später noch einmal statt einer
            // „fertigen“ Datei ohne Vorschau, die nie wieder drankäme
            if (n < buf.Length) throw new IOException("Datei nur teilweise gelesen.");
            // Datei ist inzwischen gewachsen: den Rest anhängen
            var more = new MemoryStream();
            await s.CopyToAsync(more, ct);
            return more.Length == 0 ? buf : [.. buf, .. more.ToArray()];
        }
        // Unbekannte Länge: in Stücken, mit derselben Obergrenze
        var m = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await s.ReadAsync(chunk, ct)) > 0)
        {
            if (m.Length + read > MaxRead) throw new IOException("Zu groß für eine Vorschau.");
            m.Write(chunk, 0, read);
        }
        return m.ToArray();
    }

    internal static byte[]? EmbeddedJpeg(byte[] raw) => Thumbs.EmbeddedJpeg(raw);

    // ---------------- Vorschaubilder und Dateien ----------------

    /// <summary>Galerie-Ordner und Pfad aus der Kennung „&lt;Quelle&gt;/&lt;Pfad&gt;“ - nur, wenn der Benutzer ihn sehen darf.</summary>
    /// <summary>Alle Angaben einer Datei aus einem Galerie-Ordner - aus dem Kopf (bei RAW 6 MB), reicht das nicht, aus der ganzen Datei.</summary>
    public static async Task<List<(string Group, string Name, string Value)>> AllTagsAsync(UserSpace space, ServerConfig config, string id, CancellationToken ct)
    {
        var (src, path) = Resolve(space, config, id);
        var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
        using (driver)
        {
            var head = await ReadHeadAsync(driver, path, Thumbs.IsRaw(path) ? RawHeadBytes : HeadBytes * 4, ct);
            var tags = FileMeta.AllTags(new MemoryStream(head));
            if (tags.Count == 0) tags = FileMeta.AllTags(new MemoryStream(await ReadAllAsync(driver, path, ct)));
            return tags;
        }
    }

    /// <summary>
    /// Fotos und Videos aus Galerie-Ordnern löschen (Kennung „Quelle/Pfad“) - im externen Ordner selbst, samt XMP-Sidecar.
    /// Nur in beschreibbar eingebundenen Ordnern (sonst Fehlertext, nichts gelöscht). „foto.xmp“ geht nur mit, wenn kein
    /// anderes Foto gleichen Namens bleibt (RAW und JPG teilen sich oft eins), „foto.arw.xmp“ immer.
    /// </summary>
    public static async Task<string?> DeleteAsync(UserSpace space, ServerConfig config, IEnumerable<string> ids, CancellationToken ct)
    {
        Ensure(space.Db);
        var items = ids.Select(id => Resolve(space, config, id)).ToList();
        foreach (var mount in items.Select(x => x.Source.Mount).Distinct())
        {
            var (d, ro) = ExternalFolders.Open(space, config, mount);
            d.Dispose();
            if (ro) return "Dieser Ordner ist nur zum Lesen eingebunden.";
        }
        foreach (var bySource in items.GroupBy(x => x.Source.Id))
        {
            var src = bySource.First().Source;
            var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
            using (driver)
                foreach (var byDir in bySource.Select(x => x.Path).Distinct().GroupBy(StoragePath.Parent))
                {
                    var dir = byDir.Key;
                    var names = (await driver.ListAsync(dir, ct)).Where(e => !e.IsDir).Select(e => e.Name).ToList();
                    var gone = byDir.Select(p => p[(p.LastIndexOf('/') + 1)..].ToLowerInvariant()).ToHashSet();
                    // Was nach dem Löschen bleibt: Fotos gleichen Namens behalten „foto.xmp“
                    var stays = names.Where(n => !Sidecar.Is(n) && !gone.Contains(n.ToLowerInvariant())).Select(n => Path.GetFileNameWithoutExtension(n).ToLowerInvariant()).ToHashSet();
                    foreach (var path in byDir)
                    {
                        var known = space.Db.Scalar("SELECT 1 FROM gallery_ext WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", path)) != null;
                        if (!known) continue;
                        var name = path[(path.LastIndexOf('/') + 1)..];
                        var baseName = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                        var doomed = names.Where(n => n.Equals(name, StringComparison.OrdinalIgnoreCase) || n.Equals(name + ".xmp", StringComparison.OrdinalIgnoreCase)
                            || (!stays.Contains(baseName) && n.Equals(baseName + ".xmp", StringComparison.OrdinalIgnoreCase))).ToList();
                        foreach (var n in doomed) await driver.DeleteAsync(StoragePath.Join(dir, n), ct);
                        names.RemoveAll(doomed.Contains);
                        space.Db.Exec("DELETE FROM gallery_ext WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", path));
                        if (SharedMount(config, src.Mount)) ForgetShared(src.Mount, path); else Proxies?.Remove(space.User.Id, src.Id, path);
                        ExternalFolders.Changed(space, src.Mount, path);
                    }
                }
        }
        GalleryList.Forget(space.User.Id);
        return null;
    }

    public static (GallerySource Source, string Path) Resolve(UserSpace space, ServerConfig config, string id)
    {
        var slash = id.IndexOf('/');
        if (slash <= 0) throw new KeyNotFoundException("Nicht gefunden.");
        var src = Of(space.Db).FirstOrDefault(s => s.Id == id[..slash]) ?? throw new KeyNotFoundException("Nicht gefunden.");
        if (!ExternalFolders.MountsOf(space, config).Any(m => m.Id == src.Mount)) throw new KeyNotFoundException("Nicht gefunden.");
        return (src, StoragePath.Clean(id[(slash + 1)..]));
    }

    /// <summary>
    /// Kleines Vorschaubild aus dem Index. Groß: aus der Ablage der großen Vorschauen (2048 px, vorab erzeugt oder beim ersten
    /// Öffnen gemerkt), sonst eine ältere aus dem Index (1600 px), sonst jetzt aus dem Original.
    /// </summary>
    public static async Task<byte[]?> ThumbAsync(UserSpace space, ServerConfig config, string id, int size, CancellationToken ct, bool original = false)
    {
        // Bearbeitet (PhotoEdits): die bearbeitete Fassung - aus der großen Vorschau des Originals, RAW mit der Engine aus dem Original
        if (!original && KindOf(id) == "image" && PhotoEdits.Get(space.Db, PhotoEdits.KeyOfExt(id)) is { } edit)
        {
            var (s0, p0) = Resolve(space, config, id);
            Ensure(space.Db);
            var row0 = space.Db.Query("SELECT size, modified FROM gallery_ext WHERE source = $s AND path = $p", r => (r.GetInt64(0), r.GetInt64(1)), ("$s", s0.Id), ("$p", p0)).FirstOrDefault();
            return await PhotoEdits.ThumbAsync(space.User.Id, PhotoEdits.KeyOfExt(id), edit, row0.Item1.ToString("x") + "-" + row0.Item2.ToString("x"), size, Thumbs.IsRaw(p0),
                () => ThumbAsync(space, config, id, Thumbs.Large, ct, original: true), () => PhotoWork.GetAsync(space, config, "ext:" + id, ct), null, ct);
        }
        var (src, path) = Resolve(space, config, id);
        Ensure(space.Db);
        var row = space.Db.Query("SELECT size, modified, thumb FROM gallery_ext WHERE source = $s AND path = $p",
            r => (Size: r.GetInt64(0), Modified: r.GetInt64(1), Thumb: r.IsDBNull(2) ? null : (byte[])r.GetValue(2)), ("$s", src.Id), ("$p", path)).FirstOrDefault();
        var stamp = GalleryProxies.Stamp(row.Size, row.Modified);
        if (size <= Thumbs.Small)
            return row.Thumb is { Length: > 0 } own ? own : SharedOf(config, src) is { } m ? Shared!.Thumb(m, path, stamp) : null;
        if (KindOf(path) != "image") return null;
        var scope = ProxyScope(space, config, src);
        if (Proxies?.Get(scope.A, scope.B, path, stamp) is { } proxy) return proxy;
        // Noch nicht umgezogen (früher je Benutzer)
        if (scope.A == GalleryProxies.SharedScope && Proxies?.Get(space.User.Id, src.Id, path, stamp) is { } old1) return old1;
        var old = space.Db.Query("SELECT big FROM gallery_ext WHERE source = $s AND path = $p", r => r.IsDBNull(0) ? null : (byte[])r.GetValue(0), ("$s", src.Id), ("$p", path)).FirstOrDefault();
        if (old != null) return old;
        return await MakeProxyAsync(space, config, src, path, stamp, ct);
    }

    /// <summary>
    /// Fertige große Vorschau (2048 px, sonst die ältere mit 1600 px aus dem Index) eines Fotos aus einem Galerie-Ordner, nur wenn schon
    /// vorhanden - mit ihrer Kantenlänge, samt Aufnahmezeit und Ort
    /// aus dem Index (die Vorschau hat keine Bildangaben). Null: keine Vorschau da, kein Bild oder bearbeitet (dann das Original).
    /// So muss etwa das Tagebuch nicht jedes Original über die Freigabe holen, nur um es wieder kleinzurechnen.
    /// </summary>
    public static (byte[] Image, int Size, DateTime? Taken, double? Lat, double? Lon)? ReadyLarge(UserSpace space, ServerConfig config, string id)
    {
        if (KindOf(id) != "image" || PhotoEdits.Get(space.Db, PhotoEdits.KeyOfExt(id)) != null) return null;
        var (src, path) = Resolve(space, config, id);
        Ensure(space.Db);
        var row = space.Db.Query("SELECT size, modified, taken FROM gallery_ext WHERE source = $s AND path = $p",
            r => (Size: r.GetInt64(0), Modified: r.GetInt64(1), Taken: r.IsDBNull(2) ? (long?)null : r.GetInt64(2)), ("$s", src.Id), ("$p", path)).FirstOrDefault();
        if (row.Size == 0) return null;
        var stamp = GalleryProxies.Stamp(row.Size, row.Modified);
        var scope = ProxyScope(space, config, src);
        var big = Proxies?.Get(scope.A, scope.B, path, stamp)
            ?? (scope.A == GalleryProxies.SharedScope ? Proxies?.Get(space.User.Id, src.Id, path, stamp) : null);
        var size = GalleryProxies.Size;
        if (big == null)
        {
            big = space.Db.Query("SELECT big FROM gallery_ext WHERE source = $s AND path = $p", r => r.IsDBNull(0) ? null : (byte[])r.GetValue(0), ("$s", src.Id), ("$p", path)).FirstOrDefault();
            size = Thumbs.LegacyLarge;
        }
        if (big == null) return null;
        var gps = space.Db.Query("SELECT lat, lon FROM gallery_ext_gps WHERE source = $s AND path = $p",
            r => (Lat: r.IsDBNull(0) ? (double?)null : r.GetDouble(0), Lon: r.IsDBNull(1) ? (double?)null : r.GetDouble(1)), ("$s", src.Id), ("$p", path)).FirstOrDefault();
        return (big, size, row.Taken is long t ? DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime : null, gps.Lat, gps.Lon);
    }

    /// <summary>Große Vorschau aus dem Original machen und ablegen (ohne Ablage: wie früher im Index).</summary>
    private static async Task<byte[]?> MakeProxyAsync(UserSpace space, ServerConfig config, GallerySource src, string path, long stamp, CancellationToken ct, IStorageDriver? open = null)
    {
        // Warten, bis eine der zwei Stellen frei ist; hat der Browser die Anfrage inzwischen aufgegeben (weitergescrollt), bricht ct ab
        await ProxyGate.WaitAsync(ct);
        IStorageDriver? driver = null;
        try
        {
            var scope = ProxyScope(space, config, src);
            // Während des Wartens von einer anderen Anfrage gemacht (dasselbe Foto mehrfach angefragt)
            if (Proxies?.Get(scope.A, scope.B, path, stamp) is { } ready) return ready;
            driver = open ?? ExternalFolders.Open(space, config, src.Mount).Driver;
            var bytes = await ReadAllAsync(driver, path, ct);
            var big = Thumbs.IsRaw(path) ? Thumbs.FromRaw(bytes, GalleryProxies.Size) : Thumbs.FromBytes(bytes, GalleryProxies.Size);
            if (big == null) return null;
            if (Proxies != null) Proxies.Put(scope.A, scope.B, path, stamp, big);
            else space.Db.Exec("UPDATE gallery_ext SET big = $b WHERE source = $s AND path = $p", ("$b", big), ("$s", src.Id), ("$p", path));
            return big;
        }
        finally
        {
            if (open == null) driver?.Dispose();
            ProxyGate.Release();
        }
    }

    /// <summary>Fotos eines Galerie-Ordners, deren große Vorschau fehlt oder veraltet ist.</summary>
    public static List<(string Path, long Stamp, long Size)> MissingProxies(UserSpace space, ServerConfig config, GallerySource src)
    {
        Ensure(space.Db);
        if (Proxies == null) return [];
        var scope = ProxyScope(space, config, src);
        return space.Db.Query("SELECT path, size, modified FROM gallery_ext WHERE source = $s AND stage = 2", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("$s", src.Id))
            .Where(x => KindOf(x.Item1) == "image" && x.Item2 > 0 && x.Item2 <= MaxImageBytes)
            .Select(x => (x.Item1, GalleryProxies.Stamp(x.Item2, x.Item3), x.Item2))
            .Where(x => !Proxies.Has(scope.A, scope.B, x.Item1, x.Item2)).ToList();
    }

    /// <summary>
    /// Große Vorschauen eines Galerie-Ordners vorab erzeugen, eine nach der anderen. Hört auf, wenn der Schalter aus ist oder
    /// der Ordner weg; Fehler bei einzelnen Fotos überspringt er (nächster Durchgang versucht es wieder).
    /// </summary>
    public static async Task<int> MakeProxiesAsync(UserSpace space, ServerConfig config, GallerySource src, Activity.Job job, CancellationToken ct)
    {
        if (SharedMount(config, src.Mount)) Proxies?.Adopt(space.User.Id, src.Id, src.Mount);
        var missing = MissingProxies(space, config, src);
        if (missing.Count == 0) return 0;
        var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
        using (driver)
        {
            int done = 0, made = 0;
            job.Progress(0, missing.Count, src.Path);
            foreach (var (path, stamp, _) in missing)
            {
                ct.ThrowIfCancellationRequested();
                if (done % 25 == 0 && Of(space.Db).FirstOrDefault(s => s.Id == src.Id) is not { Proxies: true }) break;
                try { if (await MakeProxyAsync(space, config, src, path, stamp, ct, driver) != null) made++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or ArgumentException or InvalidOperationException) { }
                job.Progress(++done, missing.Count, src.Path);
            }
            return made;
        }
    }

    /// <summary>
    /// Im Foto-Editor gespeichert: gleich in die Galerie, ohne auf das Einlesen des ganzen Ordners zu warten. Die Kopie übernimmt
    /// die Angaben des Originals (Aufnahmezeit, Kamera, Ort - die Datei hat dieselben Metadaten), Größe und Vorschaubild kommen aus
    /// dem Ergebnis. Mit Größe und Änderungszeit der Datei auf dem Speicher, damit das nächste Einlesen sie als bekannt überspringt.
    /// </summary>
    public static void Edited(UserSpace space, ServerConfig config, GallerySource src, string from, string to, byte[] image, long size, DateTime modified)
    {
        var db = space.Db;
        Ensure(db);
        var stamp = new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var (w, h) = Dimensions(image);
        if (from != to)
            db.Exec(@"INSERT INTO gallery_ext(source, path, size, modified, mime, taken, camera, lens, focal, aperture, exposure, iso, width, height, rating, title, keywords, description, label, pick, stage)
                      SELECT source, $to, $z, $m, mime, taken, camera, lens, focal, aperture, exposure, iso, $w, $h, rating, title, keywords, description, label, pick, 1
                      FROM gallery_ext WHERE source = $s AND path = $from
                      ON CONFLICT(source, path) DO NOTHING",
                ("$s", src.Id), ("$from", from), ("$to", to), ("$z", size), ("$m", stamp), ("$w", w), ("$h", h));
        else
        {
            db.Exec("UPDATE gallery_ext SET size = $z, modified = $m, width = $w, height = $h, thumb = NULL, big = NULL, stage = 1 WHERE source = $s AND path = $to",
                ("$s", src.Id), ("$to", to), ("$z", size), ("$m", stamp), ("$w", w), ("$h", h));
            // Überschrieben: die große Vorschau verwerfen - sie wird beim nächsten Öffnen aus dem neuen Foto gemacht
            var scope = ProxyScope(space, config, src);
            Proxies?.Remove(scope.A, scope.B, to);
        }
        if (Thumbs.FromBytes(image, Thumbs.Small) is not { } small) return;
        // Gemeinsamer Bestand: mit den Bildangaben aus dem Ergebnis (wer den Ordner auch eingebunden hat, übernimmt sie von hier)
        if (SharedOf(config, src) is { } m) { Shared!.Put(m, to, GalleryProxies.Stamp(size, stamp), 2, MetaOf(space, to, image), small); small = InShared; }
        db.Exec("UPDATE gallery_ext SET thumb = $t, stage = 2 WHERE source = $s AND path = $p", ("$t", small), ("$s", src.Id), ("$p", to));
        // Große Vorschau gleich aus dem Ergebnis - das Vollbild braucht das Original nicht (es wird vielleicht erst noch geschrieben)
        if (Proxies != null && Thumbs.FromBytes(image, GalleryProxies.Size) is { } big) { var sc = ProxyScope(space, config, src); Proxies.Put(sc.A, sc.B, to, GalleryProxies.Stamp(size, stamp), big); }
    }

    /// <summary>
    /// Ein im Hintergrund geschriebenes Foto liegt jetzt auf dem Speicher: Größe und Zeit von dort übernehmen (damit das Einlesen es als
    /// bekannt überspringt), Vorschaubilder auf den neuen Stand umschreiben.
    /// </summary>
    public static void Written(UserSpace space, ServerConfig config, GallerySource src, string path, byte[] image, DateTime modified)
    {
        var stamp = new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        space.Db.Exec("UPDATE gallery_ext SET size = $z, modified = $m WHERE source = $s AND path = $p",
            ("$z", (long)image.Length), ("$m", stamp), ("$s", src.Id), ("$p", path));
        if (SharedOf(config, src) is { } m && Thumbs.FromBytes(image, Thumbs.Small) is { } small)
            Shared!.Put(m, path, GalleryProxies.Stamp(image.Length, stamp), 2, MetaOf(space, path, image), small);
        if (Proxies != null && Thumbs.FromBytes(image, GalleryProxies.Size) is { } big) { var sc = ProxyScope(space, config, src); Proxies.Put(sc.A, sc.B, path, GalleryProxies.Stamp(image.Length, stamp), big); }
    }

    /// <summary>
    /// Von einem Importprofil gerade in diesen Galerie-Ordner gelegt, Inhalt noch zur Hand: Eintrag samt Bildangaben und Vorschaubild
    /// gleich aus diesen Bytes - wie <see cref="FullAsync"/>, nur ohne die Datei noch einmal vom Speicher zu lesen. Größe und Zeit sind
    /// die des Speichers, damit das nächste Einlesen sie als bekannt überspringt (eine Sidecar-Datei liest es dann nur noch dazu).
    /// </summary>
    public static void Placed(UserSpace space, ServerConfig config, GallerySource src, string rel, byte[] bytes, long size, DateTime modified)
    {
        if (KindOf(rel) is null) return;
        var db = space.Db;
        Ensure(db);
        var stamp = new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        Placeholder(db, src.Id, rel, size, stamp, changed: true);
        var raw = Thumbs.IsRaw(rel);
        var meta = MetaOf(space, rel, bytes);
        byte[]? thumb = null;
        try { thumb = raw ? Thumbs.FromRaw(bytes, Thumbs.Small) : Thumbs.FromBytes(bytes, Thumbs.Small); } catch (Exception e) when (e is not OperationCanceledException) { }
        var fileStamp = GalleryProxies.Stamp(size, stamp);
        if (src.Proxies && Proxies != null)
            try { if ((raw ? Thumbs.FromRaw(bytes, GalleryProxies.Size) : Thumbs.FromBytes(bytes, GalleryProxies.Size)) is { } big) { var sc = ProxyScope(space, config, src); Proxies.Put(sc.A, sc.B, rel, fileStamp, big); } }
            catch (Exception e) when (e is not OperationCanceledException) { }
        if (SharedOf(config, src) is { } shared)
        {
            Shared!.Put(shared, rel, fileStamp, 2, meta, thumb);
            if (thumb != null) thumb = InShared;
        }
        if (meta != null) SaveMeta(db, src.Id, rel, meta, thumb, 2);
        else db.Exec("UPDATE gallery_ext SET thumb = COALESCE($t, thumb), stage = 2 WHERE source = $s AND path = $p", ("$t", thumb), ("$s", src.Id), ("$p", rel));
    }

    /// <summary>Bildangaben eines gerade gespeicherten Fotos (null, wenn nicht lesbar).</summary>
    private static FileMeta.Row? MetaOf(UserSpace space, string path, byte[] image)
    {
        try { return FileMeta.Read(path, new MemoryStream(image), space.Zone); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    /// <summary>Breite und Höhe aufrecht (Drehangabe berücksichtigt); null, wenn das Bild nicht lesbar ist.</summary>
    private static (int? W, int? H) Dimensions(byte[] image)
    {
        using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(image));
        if (codec == null) return (null, null);
        var swap = codec.EncodedOrigin is SkiaSharp.SKEncodedOrigin.LeftTop or SkiaSharp.SKEncodedOrigin.RightTop or SkiaSharp.SKEncodedOrigin.RightBottom or SkiaSharp.SKEncodedOrigin.LeftBottom;
        return swap ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>Standbild eines Videos, im Browser gemacht (wie in der Dateiablage).</summary>
    public static void StoreThumb(UserSpace space, ServerConfig config, string id, byte[] image)
    {
        var (src, path) = Resolve(space, config, id);
        if (KindOf(path) != "video" || Thumbs.FromBytes(image, Thumbs.Small) is not { } small) return;
        if (SharedOf(config, src) is { } m && space.Db.Query("SELECT size, modified FROM gallery_ext WHERE source = $s AND path = $p", r => (r.GetInt64(0), r.GetInt64(1)), ("$s", src.Id), ("$p", path)).FirstOrDefault() is { Item1: > 0 } row)
        {
            Shared!.SetThumb(m, path, GalleryProxies.Stamp(row.Item1, row.Item2), small);
            small = InShared;
        }
        space.Db.Exec("UPDATE gallery_ext SET thumb = $t WHERE source = $s AND path = $p", ("$t", small), ("$s", src.Id), ("$p", path));
    }

    /// <summary>
    /// Der gemeinsame Speicher fehlt oder ist unvollständig (er liegt im Cache und gehört nicht zum Core-Backup - nach einer
    /// Wiederherstellung ist er leer): Einträge, deren Vorschaubild dort liegen sollte, gehen zurück auf Stufe 1 (Kopf lesen),
    /// der Job holt sie neu. Solange der Speicher mindestens so viele Einträge hat wie Verweise, kostet das nur zwei Zählungen.
    /// Gibt zurück, wie viele zurückgesetzt wurden.
    /// </summary>
    public static int RepairShared(UserSpace space, ServerConfig config, GallerySource src)
    {
        if (SharedOf(config, src) is not { } mount) return 0;
        Ensure(space.Db);
        const string refsSql = "FROM gallery_ext WHERE source = $s AND thumb IS NOT NULL AND length(thumb) = 0";
        var refs = Convert.ToInt64(space.Db.Scalar("SELECT COUNT(*) " + refsSql, ("$s", src.Id)) ?? 0L);
        if (refs == 0 || Shared!.Count(mount) >= refs) return 0;
        var there = Shared.WithThumbs(mount);
        var lost = space.Db.Query("SELECT path, size, modified " + refsSql, r => (Path: r.GetString(0), Stamp: GalleryProxies.Stamp(r.GetInt64(1), r.GetInt64(2))), ("$s", src.Id))
            .Where(x => !there.Contains((x.Path, x.Stamp))).Select(x => x.Path).ToList();
        if (lost.Count == 0) return 0;
        using var conn = space.Db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var p in lost) UserDb.Exec(conn, "UPDATE gallery_ext SET thumb = NULL, stage = 0 WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", p));
        tx.Commit();
        return lost.Count;
    }

    /// <summary>
    /// Taugt ein gemeinsamer Eintrag? RAW ohne Aufnahmezeit nicht - die stammt womöglich noch aus dem früheren Fehler
    /// (falsches Unterverzeichnis bei NEF, ARW …) und wird lieber neu gelesen.
    /// </summary>
    private static bool Usable(string rel, GalleryShared.Hit hit) => !(Thumbs.IsRaw(rel) && hit.Meta?.Taken == null);

    /// <summary>
    /// Bestand in den gemeinsamen Speicher übernehmen (einmal je Galerie-Ordner, in Portionen): Bildangaben und Vorschaubild
    /// jeder schon gelesenen Datei; in gallery_ext bleibt danach nur der Verweis (leeres Vorschaubild). Angaben aus
    /// Sidecar-Dateien gehen nicht mit (sie gelten je Benutzer). Gibt zurück, ob noch etwas offen ist.
    /// </summary>
    public static bool SeedShared(UserSpace space, ServerConfig config, GallerySource src, int max = 2000)
    {
        if (SharedOf(config, src) is not { } mount) return false;
        var db = space.Db;
        Ensure(db);
        var key = "gallery.seed." + src.Id;
        if (db.Get(key) == "done") return false;
        var rows = db.Query(@"SELECT path, size, modified, stage, sidecar, taken, camera, lens, focal, aperture, exposure, iso, width, height, duration, rating, title, keywords, label, thumb, pick
                FROM gallery_ext WHERE source = $s AND stage >= 1 AND (thumb IS NULL OR length(thumb) > 0) AND path > $after ORDER BY path LIMIT $n",
            r => (Path: r.GetString(0), Size: r.GetInt64(1), Modified: r.GetInt64(2), Stage: r.GetInt32(3), Sidecar: r.GetInt64(4) != 0,
                Meta: new FileMeta.Row(r.GetString(0), N<long>(r, 5), S(r, 6), S(r, 7), N<double>(r, 8), N<double>(r, 9), N<double>(r, 10), N<int>(r, 11), null, null,
                    N<int>(r, 12), N<int>(r, 13), N<double>(r, 14), N<int>(r, 15), S(r, 16), S(r, 17), S(r, 18), Pick: N<int>(r, 20)),
                Thumb: r.IsDBNull(19) ? null : (byte[])r.GetValue(19)),
            ("$s", src.Id), ("$after", db.Get(key) ?? ""), ("$n", max)).ToList();
        if (rows.Count == 0) { db.Set(key, "done"); return false; }
        foreach (var x in rows)
        {
            if (x.Size <= 0 || Thumbs.IsRaw(x.Path) && x.Meta.Taken == null) continue;
            // Sidecar-Angaben gehören dem Benutzer, nicht der Datei
            var meta = x.Sidecar ? x.Meta with { Rating = null, Title = null, Keywords = null, Label = null, Pick = null } : x.Meta;
            Shared!.Put(mount, x.Path, GalleryProxies.Stamp(x.Size, x.Modified), x.Stage, meta, x.Thumb);
            if (x.Thumb is { Length: > 0 }) db.Exec("UPDATE gallery_ext SET thumb = $t WHERE source = $s AND path = $p", ("$t", InShared), ("$s", src.Id), ("$p", x.Path));
        }
        db.Set(key, rows[^1].Path);
        return true;
    }

    /// <summary>Der Galerie-Ordner wurde während des Einlesens entfernt.</summary>
    public sealed class SourceGone : Exception;

    /// <summary>Einträge von Ordnern, die es nicht mehr gibt (etwa während des Einlesens entfernt), aus dem Index nehmen.</summary>
    public static int Prune(UserDb db)
    {
        Ensure(db);
        var ids = Of(db).Select(s => s.Id).ToHashSet();
        var stale = db.Query("SELECT DISTINCT source FROM gallery_ext INDEXED BY gallery_ext_list", r => r.GetString(0)).Where(s => !ids.Contains(s)).ToList();
        foreach (var s in stale) db.Exec("DELETE FROM gallery_ext WHERE source = $s", ("$s", s));
        return stale.Count;
    }

    public static void Forget(UserDb db, string sourceId, string? user = null)
    {
        Ensure(db);
        db.Exec("DELETE FROM gallery_ext WHERE source = $s", ("$s", sourceId));
        db.Set("gallery.seed." + sourceId, null);
        // Je Benutzer abgelegte Vorschauen weg; gemeinsame eines Server-Ordners bleiben (andere brauchen sie vielleicht)
        if (user != null) Proxies?.RemoveSource(user, sourceId);
    }
}

/// <summary>
/// Erzeugt im Hintergrund die großen Vorschauen der Galerie-Ordner mit eingeschaltetem „Große Vorschau vorab“ - nach dem Einlesen,
/// eine nach der anderen (bei zehntausenden Fotos über einen Tunnel auch Tage). Läuft neben dem Einlesen, nicht gleichzeitig im selben Ordner.
/// </summary>
public sealed class GalleryProxyJob(Spaces spaces, ServerConfig config, ILogger<GalleryProxyJob> log) : BackgroundService
{
    private static readonly SemaphoreSlim Kick = new(0);
    /// <summary>Gleich anfangen (Schalter eingeschaltet, Einlesen fertig).</summary>
    public static void Request() => Kick.Release();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(90), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Große Vorschauen: Durchgang fehlgeschlagen"); }
            try { await Kick.WaitAsync(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (GalleryFolders.Proxies == null || !Features.On(Features.Gallery)) return;
        foreach (var space in spaces.All())
            foreach (var src in GalleryFolders.Of(space.Db).Where(s => s.Proxies && s.LastScan != null))
            {
                if (GalleryScanJob.IsRunning(space.User.Id, src.Id)) continue;
                using var job = Activity.Start(space.User.Id, Activity.GalleryProxies, src.Path);
                try
                {
                    var made = await GalleryFolders.MakeProxiesAsync(space, config, src, job, ct);
                    if (made > 0) log.LogInformation("Große Vorschauen {User}/{Source}: {Count} erzeugt", space.User.Login, src.Id, made);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or ArgumentException)
                {
                    job.Fail($"{src.Path}: {e.Message}");
                }
            }
    }
}

/// <summary>Liest die Galerie-Ordner ein: neue sofort, überwachte alle 15 Minuten, andere auf Knopfdruck.</summary>
public sealed class GalleryScanJob(Spaces spaces, ServerConfig config, ILogger<GalleryScanJob> log) : BackgroundService
{
    private static readonly ConcurrentDictionary<string, bool> Wanted = new();
    private static readonly ConcurrentDictionary<string, bool> Running = new();
    private static readonly SemaphoreSlim Kick = new(0);

    /// <summary>Gleich einlesen (neu hinzugefügt oder „Jetzt neu einlesen“).</summary>
    public static void Request(string userId, string sourceId) { Wanted[userId + "/" + sourceId] = true; Kick.Release(); }
    public static bool IsRunning(string userId, string sourceId) => Running.ContainsKey(userId + "/" + sourceId) || Wanted.ContainsKey(userId + "/" + sourceId);

    /// <summary>Woran das Einlesen gerade arbeitet (Person, Ordner, Schritt) - für die Aktivität: wer wartet, sieht, worauf.</summary>
    public static (string User, string Path, string Step)? Current { get; private set; }
    internal static void Doing(string user, string path, string step) => Current = (user, path, step);
    internal static void Idle() => Current = null;

    /// <summary>Angefordert, aber noch nicht dran: Pfade der wartenden Ordner dieser Person.</summary>
    public static List<string> Waiting(string userId, IEnumerable<GallerySource> sources) =>
        sources.Where(s => Wanted.ContainsKey(userId + "/" + s.Id) && !Running.ContainsKey(userId + "/" + s.Id)).Select(s => s.Path).ToList();

    /// <summary>Je Durchgang und Ordner höchstens so lange je Stufe - so kommen alle Ordner voran und das Auflisten wartet nicht.</summary>
    public static TimeSpan StageBudget { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Nichts mehr offen: ein nicht überwachter Ordner ruht wieder bis zum nächsten „Jetzt neu einlesen“.</summary>
    private static void Drained(UserSpace space, GallerySource src)
    {
        if (!src.Draining) return;
        src.Draining = false;
        var now = GalleryFolders.Of(space.Db);
        if (now.FirstOrDefault(x => x.Id == src.Id) is { Draining: true } cur) { cur.Draining = false; GalleryFolders.Save(space.Db, now); }
    }

    /// <summary>Stufen 2 und 3 für die offenen Einträge eines Ordners, mit Fortschritt (fertig von allen) in der Aktivität.</summary>
    private async Task StagesAsync(UserSpace space, GallerySource src, string key, CancellationToken ct)
    {
        // Bestand eines Server-Ordners in den gemeinsamen Speicher (in Portionen, dann gleich weiter)
        try { if (GalleryFolders.SeedShared(space, config, src)) Kick.Release(); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException) { log.LogWarning("Galerie-Ordner: Übernahme in den gemeinsamen Speicher: {Error}", e.Message); }
        // Gemeinsamer Speicher nach einer Wiederherstellung leer: verlorene Vorschaubilder neu holen
        try { if (GalleryFolders.RepairShared(space, config, src) is var lost and > 0) log.LogInformation("Galerie-Ordner {Source}: {Count} Vorschaubilder fehlen im gemeinsamen Speicher, werden neu erzeugt", src.Id, lost); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException) { log.LogWarning("Galerie-Ordner: Prüfung des gemeinsamen Speichers: {Error}", e.Message); }
        var (total, listed, noThumb) = GalleryFolders.Stages(space.Db, src.Id);
        if (listed + noThumb == 0) { Drained(space, src); return; }
        Running[key] = true;
        Doing(space.User.Id, src.Path, "Vorschauen");
        using var job = Activity.Start(space.User.Id, Activity.GalleryScan, src.Path, total);
        try
        {
            job.Progress(total - listed - noThumb, total, $"{src.Path}: {listed + noThumb:N0} offen");
            if (listed > 0) await GalleryFolders.HeadsAsync(space, config, src, DateTime.UtcNow + StageBudget, null, ct);
            var s = GalleryFolders.Stages(space.Db, src.Id);
            job.Progress(s.Total - s.Listed - s.NoThumb, s.Total, $"{src.Path}: {s.Listed + s.NoThumb:N0} offen");
            if (s.NoThumb > 0) await GalleryFolders.FullAsync(space, config, src, DateTime.UtcNow + StageBudget, null, ct);
            s = GalleryFolders.Stages(space.Db, src.Id);
            job.Progress(s.Total - s.Listed - s.NoThumb, s.Total, s.Listed + s.NoThumb > 0 ? $"{src.Path}: {s.Listed + s.NoThumb:N0} offen" : src.Path);
            var waiting = GalleryFolders.RetryLater(src.Id).Count;
            if (waiting > 0) log.LogWarning("Galerie-Ordner {Source}: {Count} Dateien ließen sich gerade nicht lesen, nächster Versuch später", src.Id, waiting);
            // Noch offen: gleich weitermachen statt eine Minute zu warten
            if (s.Listed + s.NoThumb > waiting) Kick.Release();
            else Drained(space, src);
        }
        catch (GalleryFolders.SourceGone) { GalleryFolders.Forget(space.Db, src.Id, space.User.Id); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or ArgumentException)
        {
            job.Fail($"{src.Path}: {e.Message}");
        }
        finally { Running.TryRemove(key, out _); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(40), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Galerie-Ordner: Durchgang fehlgeschlagen"); }
            finally { Idle(); }
            try { await Kick.WaitAsync(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        // Modul Galerie ausgeschaltet (Verwaltung): ruht, Gewünschtes bleibt vorgemerkt
        if (!Features.On(Features.Gallery)) return;
        // Gemeinsamer Bestand nicht mehr freigegebener Server-Ordner
        try { GalleryFolders.PruneShared(config); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException) { log.LogWarning("Galerie-Ordner: Aufräumen des gemeinsamen Bestands: {Error}", e.Message); }
        // Angeforderte Ordner (Knopf „Jetzt neu einlesen“, gerade übertragene Fotos) zuerst - sonst warteten sie, bis alle anderen
        // Ordner aller Personen ihre offenen Vorschauen abgearbeitet haben (große Archive brauchen dafür Stunden)
        var all = spaces.All().ToList();
        foreach (var space in all.OrderByDescending(sp => Wanted.Keys.Any(k => k.StartsWith(sp.User.Id + "/", StringComparison.Ordinal))))
        {
            // Ordner, die es nicht mehr gibt: Galerie, Audiothek, Importprofile, DMS und Klartext-Sicherung abschalten
            try { if (MountDetach.Orphans(space, config) is var off and > 0) log.LogInformation("Externe Ordner: {Count} Verwendungen entfernter Ordner abgeschaltet", off); }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException) { log.LogWarning("Externe Ordner: Abschalten entfernter Ordner: {Error}", e.Message); }
            var list = GalleryFolders.Of(space.Db);
            var upgraded = list.Count > 0 && GalleryFolders.Upgrade(space.Db);
            GalleryFolders.Prune(space.Db);
            var visible = ExternalFolders.MountsOf(space, config).Select(m => m.Id).ToHashSet();
            foreach (var src in list.OrderByDescending(x => Wanted.ContainsKey(space.User.Id + "/" + x.Id)))
            {
                var key = space.User.Id + "/" + src.Id;
                // Externer Ordner nicht (mehr) eingebunden oder nicht sichtbar: nichts einlesen (wie die Audiothek)
                if (!visible.Contains(src.Mount)) continue;
                var wanted = Wanted.ContainsKey(key);
                // Überwachen aus: nur auf Knopfdruck (oder beim ersten Mal) - auch kein Nachholen von Bildangaben und kein
                // Neueinlesen nach einem Update, sonst wird ein abgeschalteter Speicher (etwa ein ausgeschaltetes NAS) weiter angefragt
                if (!src.Watch && !wanted && src.LastScan != null) continue;
                // Gerade nicht erreichbar: später, ohne Fehler (wie die Audiothek)
                if (!wanted && !await ExternalFolders.ReachableAsync(space, config, src.Mount)) continue;
                var due = upgraded || wanted || src.LastScan == null || (src.Watch && DateTime.UtcNow - src.LastScan > GalleryFolders.WatchEvery);
                if (!due)
                {
                    // Nicht überwacht und nicht angestoßen: ruhen - sonst holte der Server im Hintergrund ständig Originale (etwa aus pCloud)
                    if (src.Watch || src.Draining) await StagesAsync(space, src, key, ct);
                    continue;
                }
                Running[key] = true;
                if (Wanted.TryRemove(key, out _)) GalleryFolders.RetryNow(src.Id);
                Doing(space.User.Id, src.Path, "Einlesen");
                using var job = Activity.Start(space.User.Id, Activity.GalleryScan, src.Path);
                try
                {
                    src.Count = await GalleryFolders.ScanAsync(space, config, src, ct, job);
                    src.Error = null;
                }
                catch (OperationCanceledException) { throw; }
                catch (GalleryFolders.SourceGone)
                {
                    // Entfernt: was dieser Durchgang schon eingetragen hat, wieder weg - und gleich mit dem nächsten Ordner weiter
                    Running.TryRemove(key, out _);
                    GalleryFolders.Forget(space.Db, src.Id, space.User.Id);
                    continue;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or ArgumentException)
                {
                    src.Error = e.Message;
                    job.Fail($"{src.Path}: {e.Message}");
                    log.LogWarning("Galerie-Ordner {User}/{Source}: {Error}", space.User.Login, src.Id, e.Message);
                }
                finally { Running.TryRemove(key, out _); }
                src.LastScan = DateTime.UtcNow;
                // Zwischendurch kann der Benutzer etwas geändert haben: nur diesen Eintrag zurückschreiben
                var now = GalleryFolders.Of(space.Db);
                if (now.FirstOrDefault(x => x.Id == src.Id) is { } cur)
                {
                    cur.LastScan = src.LastScan; cur.Count = src.Count; cur.Error = src.Error;
                    if (!cur.Watch && src.Error == null) cur.Draining = src.Draining = true;
                    GalleryFolders.Save(space.Db, now);
                }
                // Dann Bildangaben und Vorschaubilder, danach die großen Vorschauen für Neues
                await StagesAsync(space, src, key, ct);
                if (src.Proxies) GalleryProxyJob.Request();
            }
        }
    }
}
