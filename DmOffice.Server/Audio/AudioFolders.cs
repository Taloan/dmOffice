using System.Collections.Concurrent;
using System.Security.Cryptography;
using DmOffice.Core;
using DmOffice.Storage;
using SkiaSharp;

namespace DmOffice.Audio;

/// <summary>Ein Audiothek-Ordner: ein Ordner in einem externen Ordner, dessen Musik in die Audiothek kommt.</summary>
public sealed class AudioSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>Kennung des externen Ordners (ExternalFolders).</summary>
    public string Mount { get; set; } = "";
    /// <summary>Pfad darin, leer = ganzer externer Ordner.</summary>
    public string Path { get; set; } = "";
    /// <summary>Alle 15 Minuten neu einlesen.</summary>
    public bool Watch { get; set; } = true;
    public DateTime? LastScan { get; set; }
    public string? Error { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Audiothek (docs/audiothek-konzept.md): Musik aus Audiothek-Ordnern einlesen wie die Galerie ihre Fotos - erst auflisten
/// (Titel aus dem Dateinamen), dann Tags, Dauer und Cover aus der Datei (ATL). Je Benutzer in seiner Datenbank: Titel
/// (<c>audio_track</c>), Cover einmal je Bild (<c>audio_cover</c>), Hörverlauf (<c>audio_play</c>, zusammengefasst in
/// <c>audio_stat</c>). Der Hörverlauf hängt an externem Ordner und Pfad, nicht am Audiothek-Ordner - er bleibt, wenn man
/// einen Ordner herausnimmt und wieder aufnimmt.
/// </summary>
public static class AudioFolders
{
    public const string Key = "audio.sources";
    public static readonly TimeSpan WatchEvery = TimeSpan.FromMinutes(15);
    /// <summary>Pause nach jedem Stück (Tests: null).</summary>
    public static TimeSpan TagPause { get; set; } = TimeSpan.FromMilliseconds(150);
    private const int MaxFiles = 200_000;
    /// <summary>Kantenlänge der gespeicherten Cover.</summary>
    public const int CoverSize = 300;
    /// <summary>Cover-Dateien im Ordner, wenn die Datei keins enthält - in dieser Reihenfolge.</summary>
    private static readonly string[] CoverNames = ["cover", "folder", "front", "album", "albumart"];

    /// <summary>Kennung in der Release-Liste (features.json).</summary>
    public const string Feature = "audio";

    /// <summary>
    /// Audiothek freigeschaltet (Release-Liste), eingeschaltet (Verwaltung) und für diesen Benutzer da: nicht
    /// ohne Dateiablage. Aus: Schnittstelle 404, kein Einlesen, Bereich in Web und App ausgeblendet (Anmeldestand user.audio).
    /// </summary>
    public static bool Enabled(UserRecord user) => Features.On(Feature) && !user.FilesOff;

    public static List<AudioSource> Of(UserDb db) => db.GetJson<List<AudioSource>>(Key);
    public static void Save(UserDb db, List<AudioSource> list) => db.SetJson(Key, list);

    /// <summary>MIME-Typ der unterstützten Dateiarten, sonst null. Alle spielen Browser und ExoPlayer ohne Umwandlung.</summary>
    public static string? MimeOf(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".aac" => name.EndsWith(".aac", StringComparison.OrdinalIgnoreCase) ? "audio/aac" : "audio/mp4",
        ".flac" => "audio/flac",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        _ => null,
    };

    private static readonly ConcurrentDictionary<UserDb, bool> Ready = new();

    public static void Ensure(UserDb db)
    {
        if (Ready.ContainsKey(db)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS audio_track(source TEXT NOT NULL, path TEXT NOT NULL, mount TEXT NOT NULL, size INTEGER NOT NULL,
                modified INTEGER NOT NULL, stage INTEGER NOT NULL DEFAULT 0, title TEXT, artist TEXT, album_artist TEXT, album TEXT,
                track INTEGER, disc INTEGER, year INTEGER, genre TEXT, duration REAL, cover TEXT, PRIMARY KEY(source, path));
            CREATE INDEX IF NOT EXISTS audio_track_stage ON audio_track(source, stage);
            CREATE TABLE IF NOT EXISTS audio_cover(id TEXT PRIMARY KEY, data BLOB NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS audio_stat(mount TEXT NOT NULL, path TEXT NOT NULL, plays INTEGER NOT NULL DEFAULT 0,
                skips INTEGER NOT NULL DEFAULT 0, last INTEGER, PRIMARY KEY(mount, path)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS audio_play(id INTEGER PRIMARY KEY, mount TEXT NOT NULL, path TEXT NOT NULL, at INTEGER NOT NULL,
                ms INTEGER NOT NULL, skipped INTEGER NOT NULL, device TEXT);
            -- Änderungszähler für den ETag der Liste (der Hörverlauf zählt nicht - er kommt über /stats)
            CREATE TABLE IF NOT EXISTS audio_ver(id INTEGER PRIMARY KEY CHECK(id = 1), v INTEGER NOT NULL);
            INSERT OR IGNORE INTO audio_ver(id, v) VALUES(1, 0);
            CREATE TRIGGER IF NOT EXISTS audio_track_ver_ins AFTER INSERT ON audio_track BEGIN UPDATE audio_ver SET v = v + 1 WHERE id = 1; END;
            CREATE TRIGGER IF NOT EXISTS audio_track_ver_del AFTER DELETE ON audio_track BEGIN UPDATE audio_ver SET v = v + 1 WHERE id = 1; END;
            CREATE TRIGGER IF NOT EXISTS audio_track_ver_upd AFTER UPDATE OF size, modified, title, artist, album_artist, album, track, disc, year,
                genre, duration, cover ON audio_track BEGIN UPDATE audio_ver SET v = v + 1 WHERE id = 1; END;");
        Ready[db] = true;
    }

    // ---- Liste ----

    public sealed record Track(string Source, string Path, long Size, long Modified, string? Title, string? Artist, string? AlbumArtist,
        string? Album, int? No, int? Disc, int? Year, string? Genre, double? Duration, string? Cover);

    private static HashSet<string> Visible(UserSpace space, ServerConfig config)
    {
        var mounts = ExternalFolders.MountsOf(space, config).Select(m => m.Id).ToHashSet();
        return Of(space.Db).Where(s => mounts.Contains(s.Mount)).Select(s => s.Id).ToHashSet();
    }

    /// <summary>ETag der Liste: Änderungszähler und welche Ordner sichtbar sind.</summary>
    public static string Version(UserSpace space, ServerConfig config)
    {
        Ensure(space.Db);
        var ok = string.Join(",", Visible(space, config).Order(StringComparer.Ordinal));
        var v = Convert.ToInt64(space.Db.Scalar("SELECT v FROM audio_ver WHERE id = 1") ?? 0L);
        return "\"" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("a1-" + v + "|" + ok)))[..20].ToLowerInvariant() + "\"";
    }

    /// <summary>
    /// Alle Titel der Audiothek-Ordner, die der Benutzer sehen darf. Liegt ein Ordner in einem anderen (beide aufgenommen),
    /// kommt jede Datei nur einmal - über den zuerst aufgenommenen Ordner.
    /// </summary>
    public static List<Track> List(UserSpace space, ServerConfig config)
    {
        Ensure(space.Db);
        var ok = Visible(space, config);
        var order = Of(space.Db).Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return space.Db.Query(@"SELECT source, path, size, modified, title, artist, album_artist, album, track, disc, year, genre, duration, cover, mount FROM audio_track",
                r => (Mount: r.GetString(14), T: new Track(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), S(r, 4), S(r, 5), S(r, 6), S(r, 7), I(r, 8), I(r, 9), I(r, 10),
                    S(r, 11), r.IsDBNull(12) ? null : r.GetDouble(12), S(r, 13))))
            .Where(x => ok.Contains(x.T.Source))
            .GroupBy(x => (x.Mount, x.T.Path)).Select(g => g.MinBy(x => order.GetValueOrDefault(x.T.Source, int.MaxValue)).T).ToList();
    }

    private static string? S(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? I(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    /// <summary>Kennung „Quelle/Pfad“ auflösen - KeyNotFound, wenn der Ordner fehlt oder nicht mehr sichtbar ist.</summary>
    public static (AudioSource Source, string Path) Resolve(UserSpace space, ServerConfig config, string id)
    {
        var slash = id.IndexOf('/');
        if (slash <= 0) throw new KeyNotFoundException("Nicht gefunden.");
        var src = Of(space.Db).FirstOrDefault(s => s.Id == id[..slash]) ?? throw new KeyNotFoundException("Nicht gefunden.");
        if (!ExternalFolders.MountsOf(space, config).Any(m => m.Id == src.Mount)) throw new KeyNotFoundException("Nicht gefunden.");
        return (src, StoragePath.Clean(id[(slash + 1)..]));
    }

    public static byte[]? Cover(UserDb db, string id)
    {
        Ensure(db);
        return db.Scalar("SELECT data FROM audio_cover WHERE id = $i", ("$i", id)) as byte[];
    }

    // ---- Einlesen ----

    public sealed class SourceGone : Exception;

    /// <summary>
    /// Stufe 1: auflisten. Neue und geänderte Dateien kommen sofort in die Liste (Titel aus dem Dateinamen, Album aus dem
    /// Ordner), Verschwundenes geht raus. Tags und Cover holt danach <see cref="TagsAsync"/>. Gibt die Zahl der Titel zurück.
    /// </summary>
    public static async Task<int> ScanAsync(UserSpace space, ServerConfig config, AudioSource src, CancellationToken ct, Activity.Job? job = null)
    {
        var db = space.Db;
        Ensure(db);
        var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
        using (driver)
        {
            var known = db.Query("SELECT path, size, modified FROM audio_track WHERE source = $s", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("$s", src.Id))
                .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var dirs = new Stack<string>();
            var fresh = 0;
            dirs.Push(StoragePath.Clean(src.Path));
            while (dirs.Count > 0 && seen.Count < MaxFiles)
            {
                ct.ThrowIfCancellationRequested();
                var dir = dirs.Pop();
                var changed = new List<(string Rel, long Size, long Modified, bool New)>();
                foreach (var e in await driver.ListAsync(dir, ct))
                {
                    // Versteckte Ordner und Systemordner (Synology @eaDir, Papierkörbe) überspringen
                    if (e.Name.StartsWith('.') || e.Name.StartsWith('@') || e.Name.Equals("#recycle", StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = StoragePath.Join(dir, e.Name);
                    if (e.IsDir) { dirs.Push(rel); continue; }
                    if (MimeOf(e.Name) == null) continue;
                    seen.Add(rel);
                    if (seen.Count % 500 == 0) job?.Progress(seen.Count);
                    var modified = new DateTimeOffset(DateTime.SpecifyKind(e.Modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                    var had = known.TryGetValue(rel, out var k);
                    if (had && k.Item1 == e.Size && k.Item2 == modified) continue;
                    changed.Add((rel, e.Size, modified, !had));
                }
                if (changed.Count == 0) continue;
                // Während des Einlesens aus der Audiothek genommen: aufhören
                if (!Of(db).Any(s => s.Id == src.Id)) throw new SourceGone();
                Write(db, src, changed);
                fresh += changed.Count;
                job?.Progress(seen.Count, null, $"{src.Path}: {fresh:N0} neu gefunden");
            }
            // Was nicht mehr da ist, verschwindet (nur nach einem vollständigen Durchgang)
            if (seen.Count < MaxFiles)
            {
                var gone = known.Keys.Where(p => !seen.Contains(p)).ToList();
                if (gone.Count > 0)
                {
                    using var conn = db.Open();
                    using var tx = conn.BeginTransaction();
                    foreach (var p in gone) UserDb.Exec(conn, "DELETE FROM audio_track WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", p));
                    tx.Commit();
                    PruneCovers(db);
                }
            }
            return seen.Count;
        }
    }

    /// <summary>Neue und geänderte Dateien eines Ordners in einem Rutsch: Stufe 0, neue mit Titel aus dem Dateinamen.</summary>
    private static void Write(UserDb db, AudioSource src, List<(string Rel, long Size, long Modified, bool New)> rows)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (rel, size, modified, _) in rows)
        {
            var slash = rel.LastIndexOf('/');
            var name = rel[(slash + 1)..];
            var folder = slash > 0 ? rel[..slash] : "";
            UserDb.Exec(conn, @"INSERT INTO audio_track(source, path, mount, size, modified, stage, title, album) VALUES($s, $p, $m, $z, $t, 0, $ti, $al)
                ON CONFLICT(source, path) DO UPDATE SET size = excluded.size, modified = excluded.modified, stage = 0",
                ("$s", src.Id), ("$p", rel), ("$m", src.Mount), ("$z", size), ("$t", modified),
                ("$ti", System.IO.Path.GetFileNameWithoutExtension(name)), ("$al", folder.Length > 0 ? folder[(folder.LastIndexOf('/') + 1)..] : null));
        }
        tx.Commit();
    }

    /// <summary>Wie viele Titel eines Ordners noch auf ihre Tags warten.</summary>
    public static int Pending(UserDb db, string source)
    {
        Ensure(db);
        return Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM audio_track WHERE source = $s AND stage = 0", ("$s", source)) ?? 0L);
    }

    /// <summary>Gelesene Angaben einer Datei.</summary>
    public sealed record Tags(string? Title, string? Artist, string? AlbumArtist, string? Album, int? No, int? Disc, int? Year, string? Genre,
        double? Duration, byte[]? Picture);

    /// <summary>Tags, Dauer und eingebettetes Cover (Vorderseite bevorzugt) aus einer Datei.</summary>
    public static Tags Read(Stream s, string mime)
    {
        var t = new ATL.Track(s, mime);
        static string? Txt(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        static int? Num(int? v) => v is > 0 ? v : null;
        var pic = t.EmbeddedPictures.FirstOrDefault(p => p.PicType == ATL.PictureInfo.PIC_TYPE.Front) ?? t.EmbeddedPictures.FirstOrDefault();
        return new Tags(Txt(t.Title), Txt(t.Artist), Txt(t.AlbumArtist), Txt(t.Album), Num(t.TrackNumber), Num(t.DiscNumber), Num(t.Year), Txt(t.Genre),
            t.DurationMs > 0 ? Math.Round(t.DurationMs / 1000.0, 1) : null, pic?.PictureData is { Length: > 0 } d ? d : null);
    }

    /// <summary>
    /// Stufe 2: Tags, Dauer und Cover der wartenden Titel eines Ordners lesen, bis zur Frist. Ohne eingebettetes Cover nimmt
    /// sie ein Bild aus dem Ordner (cover.jpg, folder.jpg …). Gibt zurück, wie viele noch warten.
    /// </summary>
    public static async Task<int> TagsAsync(UserSpace space, ServerConfig config, AudioSource src, DateTime until, CancellationToken ct)
    {
        var db = space.Db;
        Ensure(db);
        var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
        using (driver)
        {
            var folderCovers = new Dictionary<string, string?>(StringComparer.Ordinal);
            var failed = 0;
            // Server-Ordner: Tags und Cover einmal für alle (AudioShared) - schon Gelesenes übernehmen statt die Datei zu lesen
            var shared = Shared != null && config.Current.ExternalFolders.Any(f => f.Id == src.Mount) ? src.Mount : null;
            void Apply(string rel, Tags? tags, string? cover)
            {
                if (tags == null)
                    db.Exec("UPDATE audio_track SET stage = 1, cover = $c WHERE source = $s AND path = $p", ("$c", cover), ("$s", src.Id), ("$p", rel));
                else
                    db.Exec(@"UPDATE audio_track SET stage = 1, title = COALESCE($ti, title), artist = $ar, album_artist = $aa, album = COALESCE($al, album),
                            track = $no, disc = $di, year = $y, genre = $g, duration = $du, cover = $c WHERE source = $s AND path = $p",
                        ("$ti", tags.Title), ("$ar", tags.Artist), ("$aa", tags.AlbumArtist), ("$al", tags.Album), ("$no", tags.No), ("$di", tags.Disc),
                        ("$y", tags.Year), ("$g", tags.Genre), ("$du", tags.Duration), ("$c", cover), ("$s", src.Id), ("$p", rel));
            }
            while (DateTime.UtcNow < until)
            {
                var rows = db.Query("SELECT path, size, modified FROM audio_track WHERE source = $s AND stage = 0 LIMIT 200", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("$s", src.Id));
                if (rows.Count == 0) break;
                foreach (var (rel, size, modified) in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow >= until) break;
                    var stamp = AudioShared.Stamp(size, modified);
                    if (shared != null && Shared!.Find(shared, rel, stamp) is { } hit)
                    {
                        Apply(rel, hit.Ok ? hit.Tags : null, hit.Cover != null ? CopyCover(db, hit.Cover) : null);
                        continue;
                    }
                    Tags? tags = null;
                    try
                    {
                        // Auf dem Server eingehängter Ordner: eine geöffnete Datei mit Spulen (vorher öffnete jeder Lesezugriff sie neu -
                        // über den Tunnel zum Unraid viele Umläufe je Stück); sonst (WebDAV) in Blöcken
                        await using var direct = await driver.OpenReadAsync(rel, ct);
                        using var blocks = direct.CanSeek ? null : new DriverStream(driver, rel, size);
                        Stream stream = blocks ?? direct;
                        tags = await Task.Run(() => Read(stream, MimeOf(rel) ?? "audio/mpeg"), ct);
                        if (blocks?.Error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(blocks.Error);
                        failed = 0;
                    }
                    catch (OperationCanceledException) { throw; }
                    // Speicher weg (Netz, Freigabe): nach mehreren Fehlschlägen hintereinander abbrechen, der nächste Durchgang macht
                    // weiter - sonst stünden alle ohne Tags da. Einzelne (Datei gerade gelöscht) zählen wie kaputte Dateien.
                    catch (Exception e) when ((e is HttpRequestException || (e is IOException && e is not (FileNotFoundException or DirectoryNotFoundException or EndOfStreamException)))
                        && ++failed >= 5) { throw; }
                    // Datei weg oder kaputt (ATL wirft dann Verschiedenes): bleibt mit dem Namen in der Liste - eine Datei hält den
                    // Durchgang nicht auf; der nächste Durchgang räumt Verschwundenes weg
                    catch (Exception) { }
                    var cover = tags?.Picture is { } pic ? StoreCover(db, pic) : null;
                    if (cover == null)
                    {
                        var dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
                        if (!folderCovers.TryGetValue(dir, out cover)) folderCovers[dir] = cover = await FolderCoverAsync(db, driver, dir, ct);
                    }
                    Apply(rel, tags, cover);
                    // Nur, was sich wirklich lesen ließ oder kaputt ist - nicht, wenn der Speicher gerade weg war (dann wirft es oben)
                    if (shared != null) Shared!.Put(shared, rel, stamp, tags is null ? null : tags with { Picture = null }, cover, cover != null ? Cover(db, cover) : null);
                    // Kurz Luft lassen: Galerie, Vorschauen und Office holen ihre Dateien über denselben Weg, und die Datenbank braucht
                    // dieselbe Schreibsperre - ohne Pause lag bei zehntausend Stücken über eine Stunde lang alles andere still
                    await Task.Delay(TagPause, ct);
                }
            }
            return Pending(db, src.Id);
        }
    }

    /// <summary>Gemeinsamer Zwischenspeicher für Server-Ordner (in Program.cs gesetzt; Tests: auch null).</summary>
    public static AudioShared? Shared { get; set; }

    /// <summary>Cover aus dem gemeinsamen Zwischenspeicher in die eigene Datenbank (schon verkleinert, gleiche Kennung).</summary>
    private static string? CopyCover(UserDb db, string id)
    {
        if (db.Scalar("SELECT 1 FROM audio_cover WHERE id = $i", ("$i", id)) != null) return id;
        if (Shared?.Cover(id) is not { } data) return null;
        db.Exec("INSERT OR IGNORE INTO audio_cover(id, data) VALUES($i, $d)", ("$i", id), ("$d", data));
        return id;
    }

    /// <summary>Cover aus dem Ordner (cover.jpg, folder.jpg …, auch PNG), höchstens 10 MB.</summary>
    private static async Task<string?> FolderCoverAsync(UserDb db, IStorageDriver driver, string dir, CancellationToken ct)
    {
        List<DriverEntry> entries;
        try { entries = await driver.ListAsync(dir, ct); }
        catch (Exception e) when (e is IOException or HttpRequestException or KeyNotFoundException or UnauthorizedAccessException) { return null; }
        var pick = CoverNames
            .Select(n => entries.FirstOrDefault(e => !e.IsDir && e.Size is > 0 and <= 10 * 1024 * 1024
                && System.IO.Path.GetFileNameWithoutExtension(e.Name).Equals(n, StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetExtension(e.Name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png"))
            .FirstOrDefault(e => e != null);
        if (pick == null) return null;
        try { return StoreCover(db, await driver.ReadRangeAsync(StoragePath.Join(dir, pick.Name), 0, (int)pick.Size, ct)); }
        catch (Exception e) when (e is IOException or HttpRequestException or KeyNotFoundException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Cover verkleinert ablegen, einmal je Bild (Prüfsumme des Originals); null, wenn es kein lesbares Bild ist.</summary>
    public static string? StoreCover(UserDb db, byte[] image)
    {
        var id = Convert.ToHexString(SHA256.HashData(image))[..24].ToLowerInvariant();
        if (db.Scalar("SELECT 1 FROM audio_cover WHERE id = $i", ("$i", id)) != null) return id;
        // Kaputtes oder leeres Bild: SkiaSharp wirft dann (ArgumentNullException „codec“), statt null zu liefern - das brach
        // den ganzen Durchgang ab, und alle folgenden Stücke blieben ohne Tags
        if (image.Length == 0) return null;
        SKBitmap? decoded;
        try { decoded = SKBitmap.Decode(image); }
        catch (ArgumentException) { return null; }
        using var bmp = decoded;
        if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return null;
        var f = Math.Min(1.0, (double)CoverSize / Math.Max(bmp.Width, bmp.Height));
        using var small = f < 1 ? bmp.Resize(new SKImageInfo(Math.Max(1, (int)(bmp.Width * f)), Math.Max(1, (int)(bmp.Height * f))), new SKSamplingOptions(SKCubicResampler.Mitchell)) : null;
        using var data = (small ?? bmp).Encode(SKEncodedImageFormat.Jpeg, 82);
        if (data == null) return null;
        db.Exec("INSERT OR IGNORE INTO audio_cover(id, data) VALUES($i, $d)", ("$i", id), ("$d", data.ToArray()));
        return id;
    }

    private static void PruneCovers(UserDb db) =>
        db.Exec("DELETE FROM audio_cover WHERE id NOT IN (SELECT cover FROM audio_track WHERE cover IS NOT NULL)");

    /// <summary>Ordner herausgenommen: seine Titel weg. Der Hörverlauf bleibt (hängt an externem Ordner und Pfad).</summary>
    public static void Forget(UserDb db, string sourceId)
    {
        Ensure(db);
        db.Exec("DELETE FROM audio_track WHERE source = $s", ("$s", sourceId));
        PruneCovers(db);
    }

    // ---- Hörverlauf ----

    /// <summary>Als gehört zählt: halbe Dauer oder 4 Minuten. Ohne bekannte Dauer: 30 Sekunden.</summary>
    public static bool Counts(long ms, double? duration) => ms >= (duration is > 0 ? Math.Min(duration.Value * 500, 240_000) : 30_000);

    public sealed record Stat(int Plays, int Skips, long? Last);

    /// <summary>Gespielt (ms): als gehört oder übersprungen eintragen. Gibt den neuen Stand des Titels zurück.</summary>
    public static Stat Played(UserSpace space, ServerConfig config, string id, long ms, string? device, DateTime? at = null)
    {
        var (src, path) = Resolve(space, config, id);
        var db = space.Db;
        Ensure(db);
        var duration = db.Scalar("SELECT duration FROM audio_track WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", path)) is double d ? d : (double?)null;
        var played = Counts(ms, duration);
        var when = new DateTimeOffset(at ?? DateTime.UtcNow).ToUnixTimeMilliseconds();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "INSERT INTO audio_play(mount, path, at, ms, skipped, device) VALUES($m, $p, $a, $ms, $sk, $d)",
            ("$m", src.Mount), ("$p", path), ("$a", when), ("$ms", ms), ("$sk", played ? 0 : 1), ("$d", device is { Length: > 0 } ? device[..Math.Min(device.Length, 60)] : null));
        UserDb.Exec(conn, played
                ? "INSERT INTO audio_stat(mount, path, plays, last) VALUES($m, $p, 1, $a) ON CONFLICT(mount, path) DO UPDATE SET plays = plays + 1, last = MAX(COALESCE(last, 0), $a)"
                : "INSERT INTO audio_stat(mount, path, skips) VALUES($m, $p, 1) ON CONFLICT(mount, path) DO UPDATE SET skips = skips + 1",
            ("$m", src.Mount), ("$p", path), ("$a", when));
        var stat = UserDb.Query(conn, "SELECT plays, skips, last FROM audio_stat WHERE mount = $m AND path = $p",
            r => new Stat(r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt64(2)), ("$m", src.Mount), ("$p", path)).First();
        tx.Commit();
        return stat;
    }

    /// <summary>Hörverlauf der sichtbaren Titel: Kennung → Stand.</summary>
    public static Dictionary<string, Stat> Stats(UserSpace space, ServerConfig config)
    {
        Ensure(space.Db);
        var ok = Visible(space, config);
        return space.Db.Query(@"SELECT t.source, t.path, s.plays, s.skips, s.last FROM audio_track t JOIN audio_stat s ON s.mount = t.mount AND s.path = t.path",
                r => (Source: r.GetString(0), Id: r.GetString(0) + "/" + r.GetString(1), Stat: new Stat(r.GetInt32(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt64(4))))
            .Where(x => ok.Contains(x.Source))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Stat);
    }
}

/// <summary>Liest die Audiothek-Ordner ein: neue sofort, überwachte alle 15 Minuten, andere auf Knopfdruck. Nur bei eingeschalteter Audiothek.</summary>
public sealed class AudioScanJob(Spaces spaces, ServerConfig config, ILogger<AudioScanJob> log) : BackgroundService
{
    private static readonly ConcurrentDictionary<string, bool> Wanted = new();
    private static readonly ConcurrentDictionary<string, bool> Running = new();
    private static readonly SemaphoreSlim Kick = new(0);

    /// <summary>Gleich einlesen (neu hinzugefügt oder „Jetzt neu einlesen“).</summary>
    public static void Request(string userId, string sourceId) { Wanted[userId + "/" + sourceId] = true; Kick.Release(); }
    public static bool IsRunning(string userId, string sourceId) => Running.ContainsKey(userId + "/" + sourceId) || Wanted.ContainsKey(userId + "/" + sourceId);

    /// <summary>Je Durchgang und Ordner höchstens so lange Tags lesen - so kommen alle Ordner voran. Danach eine Minute Pause.</summary>
    public static TimeSpan TagBudget { get; set; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Audiothek: Durchgang fehlgeschlagen"); }
            try { await Kick.WaitAsync(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!Features.On(AudioFolders.Feature)) return;
        // Gemeinsamer Bestand nicht mehr freigegebener Server-Ordner
        try { AudioFolders.Shared?.Prune(config.Current.ExternalFolders.Select(f => f.Id)); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException) { log.LogWarning("Audiothek: Aufräumen des gemeinsamen Bestands: {Error}", e.Message); }
        foreach (var space in spaces.All())
        {
            if (!AudioFolders.Enabled(space.User)) continue;
            foreach (var src in AudioFolders.Of(space.Db))
            {
                var key = space.User.Id + "/" + src.Id;
                // Externer Ordner nicht (mehr) sichtbar oder gerade nicht erreichbar: später
                if (!ExternalFolders.MountsOf(space, config).Any(m => m.Id == src.Mount) || !await ExternalFolders.ReachableAsync(space, config, src.Mount)) continue;
                var due = Wanted.ContainsKey(key) || src.LastScan == null || (src.Watch && DateTime.UtcNow - src.LastScan > AudioFolders.WatchEvery);
                if (!due && AudioFolders.Pending(space.Db, src.Id) == 0) continue;
                Running[key] = true;
                Wanted.TryRemove(key, out _);
                using var job = Activity.Start(space.User.Id, Activity.AudioScan, src.Path);
                try
                {
                    if (due)
                    {
                        src.Count = await AudioFolders.ScanAsync(space, config, src, ct, job);
                        src.Error = null;
                        src.LastScan = DateTime.UtcNow;
                        // Zwischendurch kann der Benutzer etwas geändert haben: nur diesen Eintrag zurückschreiben
                        var now = AudioFolders.Of(space.Db);
                        if (now.FirstOrDefault(x => x.Id == src.Id) is { } cur) { cur.LastScan = src.LastScan; cur.Count = src.Count; cur.Error = null; AudioFolders.Save(space.Db, now); }
                    }
                    if (AudioFolders.Pending(space.Db, src.Id) > 0)
                    {
                        job.Progress(0, null, src.Path);
                        // Noch offen: im nächsten Durchgang weiter (nach der Pause von einer Minute - nicht gleich wieder)
                        await AudioFolders.TagsAsync(space, config, src, DateTime.UtcNow + TagBudget, ct);
                        // Tags ließen sich lesen: eine Meldung von einem früheren Durchgang gilt nicht mehr
                        if (src.Error != null)
                        {
                            src.Error = null;
                            var now = AudioFolders.Of(space.Db);
                            if (now.FirstOrDefault(x => x.Id == src.Id) is { } cur) { cur.Error = null; AudioFolders.Save(space.Db, now); }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (AudioFolders.SourceGone) { AudioFolders.Forget(space.Db, src.Id); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or ArgumentException)
                {
                    job.Fail($"{src.Path}: {e.Message}");
                    log.LogWarning("Audiothek-Ordner {User}/{Source}: {Error}", space.User.Login, src.Id, e.Message);
                    var now = AudioFolders.Of(space.Db);
                    if (now.FirstOrDefault(x => x.Id == src.Id) is { } cur) { cur.Error = e.Message; cur.LastScan = DateTime.UtcNow; AudioFolders.Save(space.Db, now); }
                }
                finally { Running.TryRemove(key, out _); }
            }
        }
    }
}
