using System.Security.Cryptography;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Audio;

/// <summary>
/// Gemeinsamer Zwischenspeicher der Audiothek-Ordner auf Server-Ordnern (vom Admin freigegeben), wie GalleryShared für die Galerie:
/// die Dateien sind für alle dieselben, also auch Tags, Dauer und Cover - einmal je Datei (Ordner-Kennung + Pfad, Stand aus Größe
/// und Änderungszeit), nicht je Benutzer. Wer einen solchen Ordner in seine Audiothek aufnimmt, bekommt schon Gelesenes sofort;
/// je Benutzer bleiben nur, welche Titel in seiner Audiothek sind (audio_track), und sein Hörverlauf. Eigene WebDAV-Ordner bleiben getrennt.
/// Verschlüsselte Datenbank unter data/cache/audio/_shared.db - nicht in der Sicherung, lässt sich jederzeit neu aufbauen.
/// </summary>
public sealed class AudioShared
{
    private readonly string _connectionString;
    private readonly object _write = new();

    static AudioShared() => SQLitePCL.Batteries_V2.Init();

    public AudioShared(DataPaths paths, Vault vault)
        : this(Path.Combine(paths.Root, "cache", "audio", "_shared.db"), HKDF.DeriveKey(HashAlgorithmName.SHA256, vault.Key, 32, info: "dmoffice audio shared v1"u8.ToArray())) { }

    internal AudioShared(string path, byte[] key)
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
        UserDb.Exec(conn, @"CREATE TABLE IF NOT EXISTS track (mount TEXT NOT NULL, path TEXT NOT NULL, stamp INTEGER NOT NULL, ok INTEGER NOT NULL,
                title TEXT, artist TEXT, album_artist TEXT, album TEXT, track INTEGER, disc INTEGER, year INTEGER, genre TEXT, duration REAL, cover TEXT,
                PRIMARY KEY(mount, path));
            CREATE TABLE IF NOT EXISTS cover (id TEXT PRIMARY KEY, data BLOB NOT NULL) WITHOUT ROWID;");
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

    /// <summary>Stand einer Datei aus Größe und Änderungszeit (ms).</summary>
    public static long Stamp(long size, long modified) => size * 31 + modified;

    /// <summary>Gelesener Stand: Ok = Tags ließen sich lesen (sonst nur Cover aus dem Ordner); Cover = Kennung in <see cref="Cover"/>.</summary>
    public sealed record Hit(bool Ok, AudioFolders.Tags Tags, string? Cover);

    public Hit? Find(string mount, string path, long stamp)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT ok, title, artist, album_artist, album, track, disc, year, genre, duration, cover FROM track WHERE mount = $m AND path = $p AND stamp = $s",
            r => new Hit(r.GetBoolean(0), new AudioFolders.Tags(S(r, 1), S(r, 2), S(r, 3), S(r, 4), I(r, 5), I(r, 6), I(r, 7), S(r, 8), r.IsDBNull(9) ? null : r.GetDouble(9), null), S(r, 10)),
            ("$m", mount), ("$p", path), ("$s", stamp)).FirstOrDefault();
    }

    /// <summary>Verkleinertes Cover (wie in audio_cover), null wenn es fehlt.</summary>
    public byte[]? Cover(string id)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT data FROM cover WHERE id = $i", r => (byte[])r.GetValue(0), ("$i", id)).FirstOrDefault();
    }

    /// <summary>Gelesenes ablegen; das Cover (schon verkleinert) einmal je Kennung.</summary>
    public void Put(string mount, string path, long stamp, AudioFolders.Tags? t, string? coverId, byte[]? coverData)
    {
        lock (_write)
        {
            using var conn = Open();
            if (coverId != null && coverData != null)
                UserDb.Exec(conn, "INSERT OR IGNORE INTO cover(id, data) VALUES($i, $d)", ("$i", coverId), ("$d", coverData));
            UserDb.Exec(conn, @"INSERT OR REPLACE INTO track(mount, path, stamp, ok, title, artist, album_artist, album, track, disc, year, genre, duration, cover)
                VALUES($m, $p, $s, $ok, $ti, $ar, $aa, $al, $no, $di, $y, $g, $du, $c)",
                ("$m", mount), ("$p", path), ("$s", stamp), ("$ok", t != null), ("$ti", t?.Title), ("$ar", t?.Artist), ("$aa", t?.AlbumArtist), ("$al", t?.Album),
                ("$no", t?.No), ("$di", t?.Disc), ("$y", t?.Year), ("$g", t?.Genre), ("$du", t?.Duration), ("$c", coverId));
        }
    }

    /// <summary>Server-Ordner, die der Admin nicht mehr freigibt: alles dazu weg, dann verwaiste Cover.</summary>
    public void Prune(IEnumerable<string> keep)
    {
        var set = keep.ToHashSet();
        lock (_write)
        {
            using var conn = Open();
            foreach (var m in UserDb.Query(conn, "SELECT DISTINCT mount FROM track", r => r.GetString(0)).Where(m => !set.Contains(m)).ToList())
                UserDb.Exec(conn, "DELETE FROM track WHERE mount = $m", ("$m", m));
            UserDb.Exec(conn, "DELETE FROM cover WHERE id NOT IN (SELECT cover FROM track WHERE cover IS NOT NULL)");
        }
    }

    public long Count(string mount)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT COUNT(*) FROM track WHERE mount = $m", r => r.GetInt64(0), ("$m", mount)).First();
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? I(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
}
