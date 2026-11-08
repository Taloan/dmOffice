using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;
using Microsoft.Data.Sqlite;
using Notizi.Sync;

namespace DmOffice.Notes;

/// <summary>
/// Abgleich-Ordner der notizi-Datenbanken im Tresor (Tresor Stufe 4, docs/tresor-konzept.md). Der Inhalt der Dateien liegt
/// in der Tresor-Datenbank (Tabelle notizi_files); Namen, Größen und ETags stehen offen in der Benutzerdatenbank
/// (tresor_notizi) - die Dateinamen von notizi sind Kennungen ohne Inhalt. So nimmt dmOffice Hochgeladenes auch bei
/// geschlossenem Tresor an (bedingt auf den ETag, abgelegt über den öffentlichen Schlüssel des Tresors); Lesen und
/// Auflisten gehen nur, solange der Tresor auf einem Gerät der Person offen ist. Welche Datenbank im Tresor liegt, merkt
/// sich dmOffice hier: jeder Ordner der obersten Ebene in tresor_notizi. Pfade sind relativ zur notizi-Wurzel.
/// </summary>
public static class TresorNotizi
{
    public const string Schema = "CREATE TABLE notizi_files (path TEXT PRIMARY KEY, data BLOB NOT NULL)";
    private const string IndexSchema =
        "CREATE TABLE IF NOT EXISTS tresor_notizi (path TEXT PRIMARY KEY, is_dir INTEGER NOT NULL, size INTEGER NOT NULL, etag TEXT NOT NULL, modified TEXT NOT NULL)";
    private const string KindPut = "notizi", KindDelete = "notizi-del";

    public sealed record Entry(string Path, bool IsDir, long Size, string Etag, DateTime Modified)
    {
        public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UserDb, object> Ready = new();
    private static readonly ConcurrentDictionary<string, object> Gates = new();
    private static object Gate(string userId) => Gates.GetOrAdd(userId, _ => new object());

    private static UserDb Index(UserDb db)
    {
        Ready.GetValue(db, d => { d.Exec(IndexSchema); return new object(); });
        return db;
    }

    /// <summary>Beim Start: Übernehmen beim Öffnen, offene Notizen-Datenbanken beim Schließen freigeben.</summary>
    public static void Register()
    {
        Tresor.Absorbers[KindPut] = (db, payload) =>
        {
            var zero = Array.IndexOf(payload, (byte)0);
            if (zero <= 0) return;
            db.Exec("INSERT OR REPLACE INTO notizi_files (path, data) VALUES ($p, $d)",
                ("$p", Encoding.UTF8.GetString(payload, 0, zero)), ("$d", payload[(zero + 1)..]));
        };
        Tresor.Absorbers[KindDelete] = (db, payload) => DeleteContent(db, Encoding.UTF8.GetString(payload));
        Tresor.Closed += TresorNotes.Release;
        Tresor.NotesBackup = TresorNotes.Backup;
    }

    // ---------------- Was im Tresor liegt ----------------

    /// <summary>Die notizi-Datenbanken (Ordner unter der Wurzel), deren Abgleich-Ordner im Tresor liegt.</summary>
    public static List<string> Folders(UserSpace space) =>
        Index(space.Db).Query("SELECT path FROM tresor_notizi WHERE is_dir=1 AND instr(path, '/')=0 ORDER BY path", r => r.GetString(0));

    public static bool IsFolder(UserSpace space, string folder) =>
        Index(space.Db).Scalar("SELECT 1 FROM tresor_notizi WHERE path=$p AND is_dir=1", ("$p", folder)) != null;

    /// <summary>Pfad in der Ablage (z. B. „notizisync/crypt/info.json“) relativ zur notizi-Wurzel, wenn er in einer Datenbank im Tresor liegt.</summary>
    public static string? Rel(UserSpace space, string? filePath)
    {
        var root = FileStore.Normalize(space.Settings.Notizi.Root);
        if (filePath == null || root.Length == 0 || !FileStore.InRoot(filePath, root) || filePath.Length <= root.Length) return null;
        var rel = filePath[(root.Length + 1)..];
        return IsFolder(space, rel.Split('/')[0]) ? rel : null;
    }

    private static Entry ReadEntry(SqliteDataReader r) => new(r.GetString(0), r.GetInt64(1) != 0, r.GetInt64(2), r.GetString(3),
        DateTime.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));

    public static Entry? Get(UserSpace space, string rel) =>
        Index(space.Db).Query("SELECT path, is_dir, size, etag, modified FROM tresor_notizi WHERE path=$p", ReadEntry, ("$p", rel)).FirstOrDefault();

    public static List<Entry> Children(UserSpace space, string rel) =>
        Index(space.Db).Query("SELECT path, is_dir, size, etag, modified FROM tresor_notizi WHERE path GLOB $g AND instr(substr(path, $n), '/')=0 ORDER BY is_dir DESC, path",
            ReadEntry, ("$g", Glob(rel) + "/*"), ("$n", rel.Length + 2));

    private static List<Entry> Tree(UserSpace space, string rel) =>
        Index(space.Db).Query("SELECT path, is_dir, size, etag, modified FROM tresor_notizi WHERE path=$p OR path GLOB $g ORDER BY length(path)",
            ReadEntry, ("$p", rel), ("$g", Glob(rel) + "/*"));

    private static string Glob(string s) => s.Replace("[", "[[]").Replace("*", "[*]").Replace("?", "[?]");
    private static string Parent(string rel) => rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
    private static string NewEtag() => "\"" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + "\"";
    private static string Now() => DateTime.UtcNow.ToString("o");

    /// <summary>Tresor-Datenbank zum Lesen: offen auf irgendeinem Gerät (der notizi-Abgleich hat kein eigenes), sonst 423.</summary>
    private static TresorDb Content(UserSpace space) => Tresor.DbOf(space.User.Id) ?? throw new TresorClosedException();

    // ---------------- Lesen (nur bei offenem Tresor) ----------------

    public static byte[] Read(UserSpace space, string rel)
    {
        var db = Content(space);
        if (Get(space, rel) is not { IsDir: false }) throw new KeyNotFoundException();
        return db.Scalar("SELECT data FROM notizi_files WHERE path=$p", ("$p", rel)) as byte[] ?? throw new KeyNotFoundException();
    }

    /// <summary>Auflisten: wie Lesen nur bei offenem Tresor - auch die Namen gehen sonst nicht hinaus.</summary>
    public static List<Entry> List(UserSpace space, string rel)
    {
        _ = Content(space);
        return Children(space, rel);
    }

    // ---------------- Schreiben (immer) ----------------

    /// <summary>Hochladen, bedingt wie WebDAV (If-Match / If-None-Match: *). Bei geschlossenem Tresor über den öffentlichen Schlüssel.</summary>
    public static (string Etag, bool Created) Put(UserSpace space, string rel, byte[] data, string? ifMatch, string? ifNoneMatch)
    {
        lock (Gate(space.User.Id))
        {
            var e = Get(space, rel);
            if (e is { IsDir: true }) throw new DavConflictException("Dort liegt ein Ordner.");
            if (ifNoneMatch == "*" && e != null) throw new PreconditionFailedException();
            if (ifMatch == "*" ? e == null : ifMatch != null && e?.Etag != ifMatch) throw new PreconditionFailedException();
            if (Parent(rel) is { Length: > 0 } parent && Get(space, parent) is not { IsDir: true }) throw new DavConflictException("Den Ordner darüber gibt es nicht.");
            var etag = NewEtag();
            if (Tresor.DbOf(space.User.Id) is { } db)
                db.Exec("INSERT OR REPLACE INTO notizi_files (path, data) VALUES ($p, $d)", ("$p", rel), ("$d", data));
            else
                Tresor.Drop(space.User.Id, KindPut, [.. Encoding.UTF8.GetBytes(rel), 0, .. data]);
            Index(space.Db).Exec("INSERT OR REPLACE INTO tresor_notizi (path, is_dir, size, etag, modified) VALUES ($p, 0, $s, $e, $m)",
                ("$p", rel), ("$s", data.LongLength), ("$e", etag), ("$m", Now()));
            return (etag, e == null);
        }
    }

    /// <summary>Ordner anlegen; gibt es ihn schon: false (WebDAV 405).</summary>
    public static bool Mkcol(UserSpace space, string rel)
    {
        lock (Gate(space.User.Id))
        {
            if (Get(space, rel) != null) return false;
            if (Parent(rel) is { Length: > 0 } parent && Get(space, parent) is not { IsDir: true }) throw new DavConflictException("Den Ordner darüber gibt es nicht.");
            Index(space.Db).Exec("INSERT INTO tresor_notizi (path, is_dir, size, etag, modified) VALUES ($p, 1, 0, $e, $m)",
                ("$p", rel), ("$e", NewEtag()), ("$m", Now()));
            return true;
        }
    }

    /// <summary>Löschen (Datei oder Ordner samt Inhalt); den Ordner einer Datenbank selbst nur über „Aus dem Tresor holen“.</summary>
    public static bool Delete(UserSpace space, string rel, string? ifMatch)
    {
        lock (Gate(space.User.Id))
        {
            if (!rel.Contains('/')) throw new DavForbiddenException("Die Datenbank selbst kommt nur über „Aus dem Tresor holen“ heraus.");
            var e = Get(space, rel);
            if (e == null) return false;
            if (ifMatch != null && ifMatch != "*" && e.Etag != ifMatch) throw new PreconditionFailedException();
            if (Tresor.DbOf(space.User.Id) is { } db) DeleteContent(db, rel);
            else Tresor.Drop(space.User.Id, KindDelete, Encoding.UTF8.GetBytes(rel));
            space.Db.Exec("DELETE FROM tresor_notizi WHERE path=$p OR path GLOB $g", ("$p", rel), ("$g", Glob(rel) + "/*"));
            return true;
        }
    }

    private static void DeleteContent(TresorDb db, string rel) =>
        db.Exec("DELETE FROM notizi_files WHERE path=$p OR path GLOB $g", ("$p", rel), ("$g", Glob(rel) + "/*"));

    // ---------------- In den Tresor verschieben, aus dem Tresor holen ----------------

    /// <summary>
    /// Den Abgleich-Ordner einer Datenbank in den Tresor ziehen (Tresor offen): Inhalt in die Tresor-Datenbank, ETags
    /// bleiben (die Geräte gleichen ohne Umweg weiter ab), danach die Dateien im offenen Bereich löschen.
    /// </summary>
    public static void MoveIn(UserSpace space, string folder)
    {
        var db = Content(space);
        var root = FileStore.Normalize(space.Settings.Notizi.Root);
        var path = root + "/" + folder;
        if (FileStore.Get(space.Db, path) is not { IsDir: true } top) throw new KeyNotFoundException("Die Datenbank gibt es nicht.");
        lock (Gate(space.User.Id))
        {
            if (IsFolder(space, folder)) throw new InvalidOperationException("Die Datenbank liegt schon im Tresor.");
            var index = Index(space.Db);
            foreach (var f in FileStore.Descendants(space.Db, path).Prepend(top))
            {
                var rel = f.Path[(root.Length + 1)..];
                if (!f.IsDir)
                    db.Exec("INSERT OR REPLACE INTO notizi_files (path, data) VALUES ($p, $d)", ("$p", rel), ("$d", FileStore.Data(space.Db, f.Path) ?? []));
                index.Exec("INSERT OR REPLACE INTO tresor_notizi (path, is_dir, size, etag, modified) VALUES ($p, $d, $s, $e, $m)",
                    ("$p", rel), ("$d", f.IsDir ? 1 : 0), ("$s", f.Size), ("$e", f.Etag), ("$m", f.Modified.ToUniversalTime().ToString("o")));
            }
            // Inhalte, die noch in der Benutzerdatenbank stehen, überschrieben statt nur frei markiert (secure_delete gilt je
            // Verbindung); ausgelagerte räumt die Ablage weg. Im notizi-Ordner löscht sie endgültig (kein Papierkorb, keine Versionen).
            using (var conn = space.Db.Open())
            {
                UserDb.Exec(conn, "PRAGMA secure_delete = ON");
                UserDb.Exec(conn, "UPDATE files SET data=NULL WHERE (path=$p OR path GLOB $g) AND data IS NOT NULL", ("$p", path), ("$g", Glob(path) + "/*"));
            }
            FileStore.Delete(space.Db, path);
        }
    }

    /// <summary>Zurück in den offenen Bereich (Tresor offen): Dateien wieder in die Ablage, dann aus dem Tresor löschen.</summary>
    public static void MoveOut(UserSpace space, string folder)
    {
        var db = Content(space);
        var root = FileStore.Normalize(space.Settings.Notizi.Root);
        lock (Gate(space.User.Id))
        {
            if (!IsFolder(space, folder)) throw new KeyNotFoundException("Die Datenbank liegt nicht im Tresor.");
            if (FileStore.Get(space.Db, root + "/" + folder) != null) throw new InvalidOperationException("Im offenen Bereich gibt es schon einen Ordner mit diesem Namen.");
            foreach (var e in Tree(space, folder))
            {
                var path = root + "/" + e.Path;
                if (e.IsDir) FileStore.EnsureDir(space.Db, path);
                else FileStore.Put(space.Db, path, db.Scalar("SELECT data FROM notizi_files WHERE path=$p", ("$p", e.Path)) as byte[] ?? [], "application/octet-stream");
            }
            DeleteContent(db, folder);
            space.Db.Exec("DELETE FROM tresor_notizi WHERE path=$p OR path GLOB $g", ("$p", folder), ("$g", Glob(folder) + "/*"));
        }
        TresorNotes.Forget(space.User.Id, folder);
    }
}

/// <summary>Der Abgleich-Ordner einer Datenbank im Tresor für den Abgleich auf dem Server (wie <see cref="DbTransport"/>).</summary>
public sealed class TresorNotiziTransport(UserSpace space, string folder) : ISyncTransport, ISyncBrowser
{
    private string At(string path) => path.Length == 0 ? folder : folder + "/" + path.Trim('/');

    public Task<byte[]?> ReadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(TresorNotizi.Get(space, At(path)) is { IsDir: false } ? TresorNotizi.Read(space, At(path)) : null);

    public Task<string> WriteAsync(string path, byte[] data, string? expectedTag = null, CancellationToken ct = default)
    {
        try
        {
            var (etag, _) = TresorNotizi.Put(space, At(path), data,
                ifMatch: expectedTag is { Length: > 0 } ? expectedTag : null, ifNoneMatch: expectedTag == "" ? "*" : null);
            return Task.FromResult(etag);
        }
        catch (PreconditionFailedException) { throw new SyncPreconditionException(path); }
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        TresorNotizi.Delete(space, At(path), null);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RemoteFile>>(TresorNotizi.List(space, At(path))
            .Where(e => !e.IsDir && !e.Name.EndsWith(".tmp")).Select(e => new RemoteFile(e.Name, e.Etag)).ToList());

    public Task EnsureFolderAsync(string path, CancellationToken ct = default)
    {
        var current = folder;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            TresorNotizi.Mkcol(space, current);
        }
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<string> Folders, bool IsSyncFolder)> BrowseAsync(string path, CancellationToken ct = default)
    {
        var list = TresorNotizi.List(space, At(path));
        return Task.FromResult<(IReadOnlyList<string>, bool)>((list.Where(e => e.IsDir).Select(e => e.Name).ToList(),
            list.Any(e => !e.IsDir && e.Name == SyncStore.InfoFile)));
    }

    public void Dispose() { }
}

/// <summary>
/// Die Notizen einer notizi-Datenbank im Tresor, wie dmOffice sie zeigt und bearbeitet: je Datenbank eine eigene
/// SQLCipher-Datei unter users/&lt;id&gt;.tresor.notes, verschlüsselt mit einem Schlüssel aus dem Tresor-Schlüssel. Nur ein
/// Zwischenspeicher - die Notizen selbst stehen im Abgleich-Ordner im Tresor; die Datei lässt sich jederzeit neu füllen.
/// </summary>
public sealed class TresorNotesFile : ISqlStore, IDisposable
{
    private readonly string _connectionString;

    static TresorNotesFile() => SQLitePCL.Batteries_V2.Init();

    public TresorNotesFile(string path, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = UserDb.ConnectionStringOf(path, key);
        using var conn = Open();
        if (Convert.ToInt32(UserDb.Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First()) == 0)
        {
            using var tx = conn.BeginTransaction();
            UserDb.Exec(conn, Schema);
            UserDb.Exec(conn, "PRAGMA user_version = 1");
            tx.Commit();
        }
    }

    /// <summary>Dieselben Tabellen wie in der Benutzerdatenbank (UserDb, Notizen samt Schlagwörtern), ohne Suchindex der Anhänge.</summary>
    private const string Schema = """
CREATE TABLE notes (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    sync_id      TEXT    NOT NULL UNIQUE,
    sync_rev     INTEGER NOT NULL DEFAULT 0,
    folder       TEXT    NULL,
    sort_order   REAL    NULL,
    title        TEXT    NOT NULL DEFAULT '',
    custom_title TEXT    NULL,
    color        TEXT    NOT NULL DEFAULT '#FFF9A8',
    important    INTEGER NOT NULL DEFAULT 0,
    alarm        TEXT    NULL,
    alarm_active INTEGER NOT NULL DEFAULT 1,
    archived     TEXT    NULL,
    deleted      TEXT    NULL,
    created      TEXT    NOT NULL,
    modified     TEXT    NOT NULL,
    html         TEXT    NOT NULL,
    plain        TEXT    NOT NULL DEFAULT '',
    editor       TEXT    NULL,
    images       TEXT    NOT NULL DEFAULT '[]',
    attachments  TEXT    NOT NULL DEFAULT '[]',
    sources      TEXT    NOT NULL DEFAULT '[]',
    tags         TEXT    NULL
);
CREATE TABLE note_folders (
    sync_id    TEXT    PRIMARY KEY,
    sync_rev   INTEGER NOT NULL DEFAULT 0,
    parent     TEXT    NULL,
    name       TEXT    NOT NULL,
    sort_order REAL    NOT NULL DEFAULT 0,
    created    TEXT    NOT NULL,
    modified   TEXT    NOT NULL
);
CREATE TABLE note_blobs (name TEXT PRIMARY KEY, data BLOB NOT NULL);
CREATE TABLE note_sync_state (
    sync_id    TEXT    PRIMARY KEY,
    kind       TEXT    NOT NULL,
    base_rev   INTEGER NOT NULL,
    remote_tag TEXT    NOT NULL,
    base_item  BLOB    NULL
);
CREATE TABLE note_tombstones (sync_id TEXT PRIMARY KEY, kind TEXT NOT NULL);
CREATE VIRTUAL TABLE notes_fts USING fts5(
    title, plain, content='notes', content_rowid='id', tokenize='unicode61 remove_diacritics 2'
);
CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
    INSERT INTO notes_fts(rowid, title, plain) VALUES (new.id, new.title, new.plain);
END;
CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, plain) VALUES ('delete', old.id, old.title, old.plain);
END;
CREATE TRIGGER notes_au AFTER UPDATE OF title, plain ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, plain) VALUES ('delete', old.id, old.title, old.plain);
    INSERT INTO notes_fts(rowid, title, plain) VALUES (new.id, new.title, new.plain);
END;
""";

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL; PRAGMA secure_delete=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        return UserDb.Exec(conn, sql, args);
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        return UserDb.Query(conn, sql, read, args);
    }

    /// <summary>Stimmige Kopie für die Sicherung (VACUUM INTO) - bleibt mit demselben Schlüssel verschlüsselt.</summary>
    public void SnapshotTo(string target) => S3Backup.Vacuum(_connectionString, target);

    public void Dispose() => SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
}

/// <summary>Die offenen Notizen-Datenbanken im Tresor je Person und Datenbank - freigegeben, sobald der Tresor überall zu ist.</summary>
public static class TresorNotes
{
    private static readonly ConcurrentDictionary<(string User, string Folder), (TresorNotesFile File, NotesDb Db)> Open = new();

    private static string FileOf(string userId, string folder) =>
        Path.Combine(Tresor.NotesDir(userId), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..32].ToLowerInvariant() + ".db");

    /// <summary>Notizen einer Datenbank im Tresor; null, wenn sie nicht im Tresor liegt. Tresor nirgends offen: 423.</summary>
    public static NotesDb? Of(UserSpace space, string folder)
    {
        if (!TresorNotizi.IsFolder(space, folder)) return null;
        var userId = space.User.Id;
        if (Open.TryGetValue((userId, folder), out var open) && Tresor.OpenAnywhere(userId)) return open.Db;
        var key = Tresor.SubKey(userId, "tresor-notes") ?? throw new TresorClosedException();
        return Open.GetOrAdd((userId, folder), k =>
        {
            var file = new TresorNotesFile(FileOf(k.User, k.Folder), key);
            return (file, new NotesDb(file));
        }).Db;
    }

    /// <summary>
    /// Sicherung (Tresor.Backup): je Datenbank die Notizen samt noch nicht Abgeglichenem. Offene Dateien als stimmige Kopie,
    /// die anderen so, wie sie auf der Platte liegen (dann schreibt niemand daran). Beides bleibt verschlüsselt.
    /// </summary>
    public static void Backup(string userId, string target)
    {
        var dir = Tresor.NotesDir(userId);
        if (!Directory.Exists(dir)) return;
        Directory.CreateDirectory(target);
        var open = Open.Where(o => o.Key.User == userId).ToDictionary(o => Path.GetFileName(FileOf(userId, o.Key.Folder)), o => o.Value.File);
        foreach (var f in Directory.EnumerateFiles(dir, "*.db"))
        {
            var name = Path.GetFileName(f);
            if (open.TryGetValue(name, out var file)) { file.SnapshotTo(Path.Combine(target, name)); continue; }
            foreach (var side in new[] { "", "-wal" })
                if (File.Exists(f + side)) File.Copy(f + side, Path.Combine(target, name + side), overwrite: true);
        }
    }

    /// <summary>Tresor überall geschlossen: Verbindungen lösen.</summary>
    public static void Release(string userId)
    {
        foreach (var k in Open.Keys.Where(k => k.User == userId).ToList())
            if (Open.TryRemove(k, out var v)) v.File.Dispose();
    }

    /// <summary>Datenbank kommt aus dem Tresor: Zwischenspeicher löschen.</summary>
    public static void Forget(string userId, string folder)
    {
        if (Open.TryRemove((userId, folder), out var v)) v.File.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { "", "-wal", "-shm" }.Select(s => FileOf(userId, folder) + s))
            if (File.Exists(f)) File.Delete(f);
    }
}
