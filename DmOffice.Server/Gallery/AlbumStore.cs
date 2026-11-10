using System.Security.Cryptography;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Gallery;

/// <summary>Ein Album: fest (Einträge von Hand) oder smart (gespeicherte Filterleiste über die Fotos des Besitzers).</summary>
public sealed record Album(long Id, string Owner, string Name, string Kind, string? Rule, string Created, List<string> Members)
{
    public const string Fixed = "fixed", Smart = "smart";
    public bool Sees(string user) => user == Owner || Members.Contains(user);
}

/// <summary>
/// Öffentlicher Link auf ein Album („extern zeigen“): live, nur ansehen; wahlweise mit Ablauf, Passwort und Erlaubnis zum
/// Herunterladen der Originale. Das Token steht hier (die Datenbank ist verschlüsselt), gesucht wird über seinen Hash.
/// </summary>
public sealed record AlbumLink(string Id, long Album, string Token, DateTime? Expires, bool HasPassword, bool Download, DateTime Created, long Views)
{
    public bool Expired(DateTime now) => Expires is { } e && e <= now;
}

/// <summary>Ein Eintrag eines festen Albums: Foto eines Benutzers - "f:&lt;fileid&gt;" (eigene Ablage) oder "x:&lt;quelle&gt;/&lt;pfad&gt;" (Galerie-Ordner).</summary>
public sealed record AlbumItem(string Owner, string Ref, string AddedBy, string Added);

/// <summary>
/// Alben (Version 2) in einer eigenen, verschlüsselten Datenbank data/albums.db - zentral, weil geteilte Alben Fotos mehrerer
/// Benutzer enthalten (wer mitmacht, legt eigene dazu). Schlüssel wie beim Chat: zufällig, mit dem Hauptschlüssel eingepackt.
/// Fotos werden über ihre feste Dateinummer gemerkt - Verschieben und Umbenennen ändern nichts.
/// </summary>
public sealed class AlbumStore
{
    private readonly string _connectionString;
    private readonly object _write = new();

    static AlbumStore() => SQLitePCL.Batteries_V2.Init();

    public AlbumStore(DataPaths paths, Vault vault) : this(Path.Combine(paths.Root, "albums.db"), LoadKey(paths, vault)) { }

    public AlbumStore(string path, byte[] key)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Password = "x'" + Convert.ToHexString(key) + "'", DefaultTimeout = 30 }.ToString();
        using var conn = Open();
        UserDb.Exec(conn, @"CREATE TABLE IF NOT EXISTS albums (id INTEGER PRIMARY KEY AUTOINCREMENT, owner TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL,
                rule TEXT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS album_members (album INTEGER NOT NULL REFERENCES albums(id) ON DELETE CASCADE, user TEXT NOT NULL, PRIMARY KEY(album, user));
            CREATE TABLE IF NOT EXISTS album_items (album INTEGER NOT NULL REFERENCES albums(id) ON DELETE CASCADE, owner TEXT NOT NULL, ref TEXT NOT NULL,
                added_by TEXT NOT NULL, added TEXT NOT NULL, PRIMARY KEY(album, owner, ref));
            CREATE INDEX IF NOT EXISTS album_members_user ON album_members(user);
            CREATE INDEX IF NOT EXISTS album_items_owner ON album_items(owner, ref);
            CREATE TABLE IF NOT EXISTS album_links (id TEXT PRIMARY KEY, album INTEGER NOT NULL REFERENCES albums(id) ON DELETE CASCADE, token TEXT NOT NULL,
                token_hash TEXT NOT NULL UNIQUE, password TEXT NULL, download INTEGER NOT NULL, expires TEXT NULL, created TEXT NOT NULL, views INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS album_links_album ON album_links(album);");
    }

    private static byte[] LoadKey(DataPaths paths, Vault vault)
    {
        Directory.CreateDirectory(paths.Keys);
        var file = Path.Combine(paths.Keys, "albums.key");
        if (!File.Exists(file))
        {
            var fresh = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(file, vault.Wrap(fresh));
            return fresh;
        }
        var wrapped = File.ReadAllText(file).Trim();
        try { return vault.Unwrap(wrapped); }
        catch (CryptographicException) when (vault.Previous != null)
        {
            var key = Vault.Unwrap(vault.Previous, wrapped);
            File.WriteAllText(file, vault.Wrap(key));
            return key;
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Konsistente, gleich verschlüsselte Kopie (Core-Backup).</summary>
    public void SnapshotTo(string file) => S3Backup.Vacuum(_connectionString, file);
    internal string ConnectionString => _connectionString;

    private static Album Read(SqliteConnection conn, SqliteDataReader r)
    {
        var id = r.GetInt64(0);
        var members = UserDb.Query(conn, "SELECT user FROM album_members WHERE album=$a ORDER BY user", x => x.GetString(0), ("$a", id)).ToList();
        return new Album(id, r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), members);
    }

    public Album? Get(long id)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT id, owner, name, kind, rule, created FROM albums WHERE id=$i", r => Read(conn, r), ("$i", id)).FirstOrDefault();
    }

    /// <summary>Eigene und mit mir geteilte Alben, nach Name.</summary>
    public List<Album> ForUser(string user)
    {
        using var conn = Open();
        return UserDb.Query(conn, @"SELECT id, owner, name, kind, rule, created FROM albums
                WHERE owner=$u OR id IN (SELECT album FROM album_members WHERE user=$u) ORDER BY name COLLATE NOCASE",
            r => Read(conn, r), ("$u", user)).ToList();
    }

    public long Create(string owner, string name, string kind, string? rule)
    {
        lock (_write)
        {
            using var conn = Open();
            UserDb.Exec(conn, "INSERT INTO albums(owner, name, kind, rule, created) VALUES($o,$n,$k,$r,$c)",
                ("$o", owner), ("$n", name), ("$k", kind), ("$r", rule), ("$c", DateTime.UtcNow.ToString("o")));
            return UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
        }
    }

    public void Update(long id, string? name, string? rule)
    {
        lock (_write)
        {
            using var conn = Open();
            if (name != null) UserDb.Exec(conn, "UPDATE albums SET name=$n WHERE id=$i", ("$n", name), ("$i", id));
            if (rule != null) UserDb.Exec(conn, "UPDATE albums SET rule=$r WHERE id=$i", ("$r", rule), ("$i", id));
        }
    }

    public void SetMembers(long id, IEnumerable<string> members)
    {
        lock (_write)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            UserDb.Exec(conn, "DELETE FROM album_members WHERE album=$i", ("$i", id));
            foreach (var m in members.Distinct()) UserDb.Exec(conn, "INSERT INTO album_members(album, user) VALUES($i,$u)", ("$i", id), ("$u", m));
            tx.Commit();
        }
    }

    public void Delete(long id) { lock (_write) { using var conn = Open(); UserDb.Exec(conn, "DELETE FROM albums WHERE id=$i", ("$i", id)); } }

    /// <summary>Ein Mitglied steigt aus: seine Fotos gehen mit.</summary>
    public void Leave(long id, string user)
    {
        lock (_write)
        {
            using var conn = Open();
            UserDb.Exec(conn, "DELETE FROM album_members WHERE album=$i AND user=$u", ("$i", id), ("$u", user));
            UserDb.Exec(conn, "DELETE FROM album_items WHERE album=$i AND owner=$u", ("$i", id), ("$u", user));
        }
    }

    public List<AlbumItem> Items(long id)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT owner, ref, added_by, added FROM album_items WHERE album=$i ORDER BY added DESC",
            r => new AlbumItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ("$i", id)).ToList();
    }

    public bool Contains(long id, string owner, string reference)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT 1 FROM album_items WHERE album=$i AND owner=$o AND ref=$r", r => 1, ("$i", id), ("$o", owner), ("$r", reference)).Any();
    }

    public int Add(long id, string owner, IEnumerable<string> refs, string addedBy)
    {
        lock (_write)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var n = 0;
            foreach (var r in refs.Distinct())
                n += UserDb.Exec(conn, "INSERT OR IGNORE INTO album_items(album, owner, ref, added_by, added) VALUES($i,$o,$r,$b,$t)",
                    ("$i", id), ("$o", owner), ("$r", r), ("$b", addedBy), ("$t", DateTime.UtcNow.ToString("o")));
            tx.Commit();
            return n;
        }
    }

    public void Remove(long id, IEnumerable<(string Owner, string Ref)> items)
    {
        lock (_write)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (o, r) in items) UserDb.Exec(conn, "DELETE FROM album_items WHERE album=$i AND owner=$o AND ref=$r", ("$i", id), ("$o", o), ("$r", r));
            tx.Commit();
        }
    }

    /// <summary>Benutzer gelöscht: seine Alben, Mitgliedschaften und Fotos in fremden Alben.</summary>
    // ---------------- Öffentliche Links ----------------

    private static AlbumLink ReadLink(SqliteDataReader r) => new(r.GetString(0), r.GetInt64(1), r.GetString(2),
        r.IsDBNull(3) ? null : DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind), !r.IsDBNull(4), r.GetInt64(5) != 0,
        DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetInt64(7));
    private const string LinkCols = "id, album, token, expires, password, download, created, views";

    public List<AlbumLink> Links(long album)
    {
        using var conn = Open();
        return UserDb.Query(conn, $"SELECT {LinkCols} FROM album_links WHERE album=$a ORDER BY created", ReadLink, ("$a", album)).ToList();
    }

    public AlbumLink CreateLink(long album, DateTime? expires, string? password, bool download)
    {
        lock (_write)
        {
            using var conn = Open();
            var id = Guid.NewGuid().ToString("N")[..12];
            var token = Dav.Links.NewToken();
            UserDb.Exec(conn, "INSERT INTO album_links(id, album, token, token_hash, password, download, expires, created) VALUES($i,$a,$t,$h,$p,$d,$e,$c)",
                ("$i", id), ("$a", album), ("$t", token), ("$h", Dav.LinkIndex.Hash(token)), ("$p", string.IsNullOrEmpty(password) ? null : Passwords.Hash(password)),
                ("$d", download ? 1 : 0), ("$e", expires?.ToUniversalTime().ToString("o")), ("$c", DateTime.UtcNow.ToString("o")));
            return UserDb.Query(conn, $"SELECT {LinkCols} FROM album_links WHERE id=$i", ReadLink, ("$i", id)).First();
        }
    }

    public bool DeleteLink(long album, string id)
    {
        lock (_write) { using var conn = Open(); return UserDb.Exec(conn, "DELETE FROM album_links WHERE album=$a AND id=$i", ("$a", album), ("$i", id)) > 0; }
    }

    /// <summary>Link zu einem Token (über den Hash gesucht) samt Passwort-Hash.</summary>
    public (AlbumLink Link, string? Password)? LinkByToken(string token)
    {
        using var conn = Open();
        return UserDb.Query(conn, $"SELECT {LinkCols} FROM album_links WHERE token_hash=$h", r => (ReadLink(r), r.IsDBNull(4) ? null : r.GetString(4)),
            ("$h", Dav.LinkIndex.Hash(token))).Select(x => ((AlbumLink, string?)?)x).FirstOrDefault();
    }

    public void CountView(string id)
    {
        lock (_write) { using var conn = Open(); UserDb.Exec(conn, "UPDATE album_links SET views = views + 1 WHERE id=$i", ("$i", id)); }
    }

    public void Forget(string user)
    {
        lock (_write)
        {
            using var conn = Open();
            UserDb.Exec(conn, "DELETE FROM albums WHERE owner=$u", ("$u", user));
            UserDb.Exec(conn, "DELETE FROM album_members WHERE user=$u", ("$u", user));
            UserDb.Exec(conn, "DELETE FROM album_items WHERE owner=$u", ("$u", user));
        }
    }
}
