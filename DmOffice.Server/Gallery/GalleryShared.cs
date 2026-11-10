using System.Security.Cryptography;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.Data.Sqlite;

namespace DmOffice.Gallery;

/// <summary>
/// Gemeinsamer Zwischenspeicher der Galerie-Ordner auf Server-Ordnern (vom Admin freigegeben): die Dateien sind für alle
/// dieselben, also auch Bildangaben und kleines Vorschaubild - einmal je Datei (Ordner-Kennung + Pfad, Stand aus Größe und
/// Änderungszeit), nicht je Benutzer. Wer einen solchen Ordner in seine Galerie aufnimmt, bekommt schon Gelesenes sofort;
/// je Benutzer bleibt nur, welche Dateien in seiner Galerie sind (gallery_ext). Eigene WebDAV-Ordner bleiben getrennt.
/// Verschlüsselte Datenbank unter data/cache/gallery/_shared.db - nicht in der Sicherung, lässt sich jederzeit neu aufbauen
/// (geht sie kaputt oder ändert sich der Hauptschlüssel, fängt sie leer an). Bildangaben aus der Datei selbst; was aus
/// Sidecar-Dateien kommt, bleibt je Benutzer (der Schalter „XMP-Dateien“ gilt je Galerie-Ordner).
/// </summary>
public sealed class GalleryShared
{
    private readonly string _connectionString;
    private readonly object _write = new();

    static GalleryShared() => SQLitePCL.Batteries_V2.Init();

    public GalleryShared(DataPaths paths, Vault vault)
        : this(Path.Combine(paths.Root, "cache", "gallery", "_shared.db"), HKDF.DeriveKey(HashAlgorithmName.SHA256, vault.Key, 32, info: "dmoffice gallery shared v1"u8.ToArray())) { }

    internal GalleryShared(string path, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Password = "x'" + Convert.ToHexString(key) + "'", DefaultTimeout = 30 }.ToString();
        try { Create(); }
        catch (SqliteException)
        {
            // Nur ein Zwischenspeicher: lässt er sich nicht öffnen (anderer Schlüssel, kaputt), neu anfangen
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
            Create();
        }
    }

    private void Create()
    {
        using var conn = Open();
        UserDb.Exec(conn, @"CREATE TABLE IF NOT EXISTS media (mount TEXT NOT NULL, path TEXT NOT NULL, stamp INTEGER NOT NULL, stage INTEGER NOT NULL,
            taken INTEGER, camera TEXT, lens TEXT, focal REAL, aperture REAL, exposure REAL, iso INTEGER, width INTEGER, height INTEGER, duration REAL,
            rating INTEGER, title TEXT, keywords TEXT, label TEXT, thumb BLOB, PRIMARY KEY(mount, path))");
        // Version 1 (29.10.2026): Einträge nur mit Vorschaubild (früher von SetThumb nach dem Speichern im Foto-Editor als fertig
        // angelegt) gelten als nicht gelesen - andere übernahmen sonst ein Foto ohne Aufnahmezeit und sortierten es nach dem Speicherdatum
        if (Convert.ToInt64(UserDb.Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).FirstOrDefault()) < 1)
        {
            UserDb.Exec(conn, "UPDATE media SET stage = 1 WHERE stage = 2 AND thumb IS NOT NULL AND taken IS NULL AND camera IS NULL AND width IS NULL AND duration IS NULL");
            UserDb.Exec(conn, "PRAGMA user_version = 1");
        }
        // Version 2 (01.11.2026): Markierung wie Lightroom (xmpDM:pick)
        if (!UserDb.Query(conn, "PRAGMA table_info(media)", r => r.GetString(1)).Contains("pick", StringComparer.OrdinalIgnoreCase))
            UserDb.Exec(conn, "ALTER TABLE media ADD COLUMN pick INTEGER");
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Gelesener Stand einer Datei (ohne das Vorschaubild selbst): Stufe (1: Kopf gelesen, 2: fertig), Bildangaben, ob es ein Vorschaubild gibt.</summary>
    public sealed record Hit(int Stage, FileMeta.Row? Meta, bool HasThumb);

    public Hit? Find(string mount, string path, long stamp)
    {
        using var conn = Open();
        return UserDb.Query(conn, @"SELECT stage, taken, camera, lens, focal, aperture, exposure, iso, width, height, duration, rating, title, keywords, label, thumb IS NOT NULL, pick
                FROM media WHERE mount = $m AND path = $p AND stamp = $s",
            r =>
            {
                var meta = r.IsDBNull(1) && r.IsDBNull(2) && r.IsDBNull(8) && r.IsDBNull(10) ? null
                    : new FileMeta.Row(path, N<long>(r, 1), S(r, 2), S(r, 3), N<double>(r, 4), N<double>(r, 5), N<double>(r, 6), N<int>(r, 7), null, null,
                        N<int>(r, 8), N<int>(r, 9), N<double>(r, 10), N<int>(r, 11), S(r, 12), S(r, 13), S(r, 14), Pick: N<int>(r, 16));
                return new Hit(r.GetInt32(0), meta, r.GetBoolean(15));
            }, ("$m", mount), ("$p", path), ("$s", stamp)).FirstOrDefault();
    }

    public byte[]? Thumb(string mount, string path, long stamp)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT thumb FROM media WHERE mount = $m AND path = $p AND stamp = $s AND thumb IS NOT NULL",
            r => (byte[])r.GetValue(0), ("$m", mount), ("$p", path), ("$s", stamp)).FirstOrDefault();
    }

    /// <summary>Gelesenes ablegen. Ohne neues Vorschaubild bleibt ein vorhandenes (gleicher Stand); ein neuer Stand ersetzt alles.</summary>
    public void Put(string mount, string path, long stamp, int stage, FileMeta.Row? m, byte[]? thumb)
    {
        lock (_write)
        {
            using var conn = Open();
            UserDb.Exec(conn, @"INSERT INTO media(mount, path, stamp, stage, taken, camera, lens, focal, aperture, exposure, iso, width, height, duration, rating, title, keywords, label, thumb, pick)
                VALUES($m, $p, $s, $st, $t, $c, $l, $f, $a, $x, $i, $w, $h, $d, $r, $ti, $k, $lb, $th, $pk)
                ON CONFLICT(mount, path) DO UPDATE SET stage = excluded.stage, taken = excluded.taken, camera = excluded.camera, lens = excluded.lens, focal = excluded.focal,
                    aperture = excluded.aperture, exposure = excluded.exposure, iso = excluded.iso, width = excluded.width, height = excluded.height,
                    duration = excluded.duration, rating = excluded.rating, title = excluded.title, keywords = excluded.keywords, label = excluded.label, pick = excluded.pick,
                    thumb = CASE WHEN excluded.thumb IS NOT NULL THEN excluded.thumb WHEN media.stamp = excluded.stamp THEN media.thumb ELSE NULL END, stamp = excluded.stamp",
                ("$m", mount), ("$p", path), ("$s", stamp), ("$st", stage), ("$t", m?.Taken), ("$c", m?.Camera), ("$l", m?.Lens), ("$f", m?.Focal), ("$a", m?.Aperture),
                ("$x", m?.Exposure), ("$i", m?.Iso), ("$w", m?.Width), ("$h", m?.Height), ("$d", m?.Duration), ("$r", m?.Rating), ("$ti", m?.Title), ("$k", m?.Keywords),
                ("$lb", m?.Label), ("$th", thumb), ("$pk", m?.Pick));
        }
    }

    /// <summary>Nur das Vorschaubild (Standbild eines Videos aus dem Browser).</summary>
    public void SetThumb(string mount, string path, long stamp, byte[] thumb)
    {
        lock (_write)
        {
            using var conn = Open();
            // Ohne Eintrag für diesen Stand: nur das Vorschaubild, Stufe 1 - die Bildangaben liest dann jeder selbst (Stufe 2 hieße
            // „fertig“, und andere übernähmen das Foto ohne Aufnahmezeit)
            if (UserDb.Exec(conn, "UPDATE media SET thumb = $t WHERE mount = $m AND path = $p AND stamp = $s", ("$t", thumb), ("$m", mount), ("$p", path), ("$s", stamp)) == 0)
                UserDb.Exec(conn, "INSERT OR REPLACE INTO media(mount, path, stamp, stage, thumb) VALUES($m, $p, $s, 1, $t)", ("$m", mount), ("$p", path), ("$s", stamp), ("$t", thumb));
        }
    }

    /// <summary>Datei verschwunden: auch hier weg.</summary>
    public void Remove(string mount, string path)
    {
        lock (_write) { using var conn = Open(); UserDb.Exec(conn, "DELETE FROM media WHERE mount = $m AND path = $p", ("$m", mount), ("$p", path)); }
    }

    /// <summary>Server-Ordner, zu denen etwas gespeichert ist.</summary>
    public List<string> Mounts()
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT DISTINCT mount FROM media", r => r.GetString(0)).ToList();
    }

    /// <summary>Der Admin hat den Server-Ordner aus der Freigabe genommen: alles dazu weg.</summary>
    public void RemoveMount(string mount)
    {
        lock (_write) { using var conn = Open(); UserDb.Exec(conn, "DELETE FROM media WHERE mount = $m", ("$m", mount)); }
    }

    /// <summary>Was zu einem Server-Ordner mit Vorschaubild da ist (Pfad und Stand).</summary>
    public HashSet<(string Path, long Stamp)> WithThumbs(string mount)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT path, stamp FROM media WHERE mount = $m AND thumb IS NOT NULL", r => (r.GetString(0), r.GetInt64(1)), ("$m", mount)).ToHashSet();
    }

    public long Count(string mount)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT COUNT(*) FROM media WHERE mount = $m", r => r.GetInt64(0), ("$m", mount)).First();
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static T? N<T>(SqliteDataReader r, int i) where T : struct =>
        r.IsDBNull(i) ? null : (T)Convert.ChangeType(r.GetValue(i), typeof(T), System.Globalization.CultureInfo.InvariantCulture);
}
