using System.Globalization;
using System.Security.Cryptography;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Dav;

public sealed record FileEntry(string Path, bool IsDir, long Size, string Etag, string? Mime, DateTime Created, DateTime Modified, long FileId = 0)
{
    public string Name => Path.Length == 0 ? "" : Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>Eine Loeschung im Papierkorb: Datei oder Ordner samt Inhalt, mit dem Ort, an dem es lag.</summary>
public sealed record TrashEntry(long Id, string Path, bool IsDir, long Size, DateTime Deleted, string? Mime)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>Eine fruehere Version einer Datei (vor dem Ueberschreiben gesichert).</summary>
public sealed record FileVersion(long Id, long Size, string Etag, DateTime Modified, DateTime Saved);

public sealed class DavConflictException : Exception { public DavConflictException(string m) : base(m) { } }

/// <summary>Nicht erlaubt (403), etwa Verschieben auf sich selbst.</summary>
public sealed class DavForbiddenException : Exception { public DavForbiddenException(string m) : base(m) { } }

/// <summary>Speicher voll - DAV antwortet mit 507 (RFC 4918 11.5).</summary>
public sealed class QuotaExceededException : Exception
{
    public QuotaExceededException(long quota) : base($"Speicher voll – das Kontingent von {quota / 1024 / 1024} MB ist ausgeschöpft") { }
}

/// <summary>
/// Die Dateien eines Benutzers (WebDAV) - die Metadaten in seiner verschluesselten Datenbank, die Inhalte als einzeln
/// verschluesselte Dateien daneben (UserDb.Blobs, Spalte blob). Gelesen und geschrieben wird als Stream.
/// Alte Inhalte aus der Zeit davor (Spalte data) lagert <see cref="MigrateBlobs"/> aus; bis dahin wird auch daraus gelesen.
/// Pfade ohne fuehrenden und abschliessenden Schraegstrich, "" ist die Wurzel.
/// Geloeschtes kommt in den Papierkorb, Ueberschriebenes als fruehere Version zur Seite - ausser im
/// Abgleichordner von notizi: der bleibt ein geschuetzter Bereich ohne Papierkorb, Versionen und Kopien.
/// Papierkorb und Versionen zaehlen zum Speicher; wird es eng, weichen zuerst die aeltesten Versionen,
/// dann die aeltesten Eintraege im Papierkorb (wie bei Nextcloud).
/// </summary>
public static class FileStore
{
    /// <summary>Grenze je Benutzer in Bytes (0 = unbegrenzt) - in Program.cs gesetzt.</summary>
    public static Func<string, long>? QuotaOf;

    public static long Quota(UserDb db) => QuotaOf?.Invoke(db.UserId) ?? 0;

    /// <summary>Dateiablage fuer diesen Benutzer abgeschaltet - in Program.cs gesetzt.</summary>
    public static Func<string, bool>? FilesOffOf;

    public static bool FilesOff(UserDb db) => FilesOffOf?.Invoke(db.UserId) ?? false;

    /// <summary>Vorgabe: Papierkorb nach so vielen Tagen leeren (je Benutzer einstellbar, 0 = nie).</summary>
    public const int DefaultTrashDays = 30;

    /// <summary>Liegt der Pfad im Abgleichordner von notizi (oder ist er es)?</summary>
    public static bool InRoot(string path, string root)
    {
        root = Normalize(root);
        return root.Length > 0 && (path == root || path.StartsWith(root + "/", StringComparison.Ordinal));
    }

    /// <summary>Liegt der geschuetzte Ordner unter diesem Ordner (echter Vorfahr, auch die Wurzel)?</summary>
    public static bool Contains(string path, string root)
    {
        root = Normalize(root);
        return root.Length > 0 && (path.Length == 0 || root.StartsWith(path + "/", StringComparison.Ordinal));
    }

    /// <summary>Der Abgleichordner von notizi dieses Benutzers ("" = keiner) - aus seinen Einstellungen.</summary>
    public static string ProtectedRoot(UserDb db)
    {
        try { return Normalize(db.GetJson<UserSettings>("settings").Notizi?.Root ?? ""); }
        catch (ArgumentException) { return ""; }
    }

    /// <summary>Wirft, wenn nach dieser Aenderung mehr Dateien belegt waeren als erlaubt. Verkleinern geht immer.
    /// Papierkorb und Versionen zaehlen hier nicht - sie weichen danach in <see cref="MakeRoom"/>.</summary>
    private static void CheckQuota(UserDb db, SqliteConnection conn, long added)
    {
        if (added <= 0) return;
        var quota = Quota(db);
        if (quota <= 0) return;
        // Das Kontingent gilt fuer alles, was der Benutzer belegt: Dateien und die Datenbank (Mails, Kalender, Kontakte, Notizen)
        var used = Convert.ToInt64(UserDb.Query(conn, "SELECT COALESCE(SUM(size),0) FROM files", r => r.GetInt64(0)).First()) + UserStorage.Database(db);
        if (used + added > quota) throw new QuotaExceededException(quota);
    }

    /// <summary>Ueber der Grenze: aelteste Versionen, dann aelteste Papierkorb-Eintraege endgueltig loeschen.</summary>
    private static void MakeRoom(UserDb db, SqliteConnection conn)
    {
        var quota = Quota(db);
        if (quota <= 0) return;
        long Sum(string sql) => Convert.ToInt64(UserDb.Query(conn, sql, r => r.GetInt64(0)).First());
        var over = UserStorage.Database(db) + Sum("SELECT COALESCE(SUM(size),0) FROM files") + Sum("SELECT COALESCE(SUM(size),0) FROM file_trash")
                   + Sum("SELECT COALESCE(SUM(size),0) FROM file_versions") - quota;
        if (over <= 0) return;
        foreach (var (id, size) in UserDb.Query(conn, "SELECT id, size FROM file_versions ORDER BY saved, id", r => (r.GetInt64(0), r.GetInt64(1))))
        {
            if (over <= 0) break;
            UserDb.Exec(conn, "DELETE FROM file_versions WHERE id=$i", ("$i", id));
            over -= size;
        }
        foreach (var (id, size) in UserDb.Query(conn, "SELECT id, size FROM file_trash ORDER BY deleted, id", r => (r.GetInt64(0), r.GetInt64(1))))
        {
            if (over <= 0) break;
            UserDb.Exec(conn, "DELETE FROM file_trash WHERE id=$i", ("$i", id));
            over -= size;
        }
        Orphans(conn);
    }

    /// <summary>Was hoechstens noch geschrieben werden darf, bevor die Grenze erreicht ist (die ersetzte Datei zaehlt mit).</summary>
    private static long Room(UserDb db, long replaced)
    {
        var quota = Quota(db);
        return quota <= 0 ? long.MaxValue : Math.Max(0, quota - Usage(db) - UserStorage.Database(db) + replaced);
    }

    public static string Normalize(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
            if (p is "." or ".." || p.Any(char.IsControl)) throw new ArgumentException("Ungültiger Pfad.");
        return string.Join("/", parts);
    }

    public static string? Parent(string path) => path.Length == 0 ? null : path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private const string Columns = "path,is_dir,size,etag,mime,created,modified,COALESCE(fileid,0)";
    private const string ColumnsF = "f.path,f.is_dir,f.size,f.etag,f.mime,f.created,f.modified,COALESCE(f.fileid,0)";

    private static DateTime Time(string s) => DateTime.Parse(s, null, DateTimeStyles.RoundtripKind);

    private static FileEntry Read(SqliteDataReader r) => new(r.GetString(0), r.GetInt64(1) != 0, r.GetInt64(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), Time(r.GetString(5)), Time(r.GetString(6)), r.GetInt64(7));

    public static FileEntry? Get(UserDb db, string path) =>
        db.Query($"SELECT {Columns} FROM files WHERE path=$p", Read, ("$p", path)).FirstOrDefault();

    public static FileEntry? ById(UserDb db, long fileId) =>
        db.Query($"SELECT {Columns} FROM files WHERE fileid=$f", Read, ("$f", fileId)).FirstOrDefault();

    public static List<FileEntry> Children(UserDb db, string path) =>
        db.Query($"SELECT {Columns} FROM files WHERE parent=$p ORDER BY is_dir DESC, path", Read, ("$p", path));

    /// <summary>Alles darunter, fuer Depth: infinity.</summary>
    public static List<FileEntry> Descendants(UserDb db, string path) => path.Length == 0
        ? db.Query($"SELECT {Columns} FROM files WHERE path<>'' ORDER BY path", Read)
        : db.Query($"SELECT {Columns} FROM files WHERE path GLOB $p ORDER BY path", Read, ("$p", Glob(path) + "/*"));

    /// <summary>Groesse eines Ordners samt allem darunter (oc:size), einer Datei ihre Groesse.</summary>
    public static long TreeSize(UserDb db, FileEntry f) => !f.IsDir ? f.Size : f.Path.Length == 0 ? Usage(db)
        : Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(size),0) FROM files WHERE path GLOB $p", ("$p", Glob(f.Path) + "/*")) ?? 0L);

    /// <summary>Ganzer Inhalt im Speicher - nur fuer Kleines (notizi-Abgleich, Office-Vorlagen, Tests). Sonst <see cref="OpenRead"/>.</summary>
    public static byte[]? Data(UserDb db, string path) =>
        OpenContent(db, "files", "path=$p AND is_dir=0", ("$p", path)) is { } s ? ReadAll(s) : null;

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        {
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return data;
        }
    }

    /// <summary>Inhalt als Stream mit Laenge und Suchen - fuer grosse Dateien, Bereiche (Range) und ZIP, ohne die ganze
    /// Datei in den Speicher zu laden. Entschluesselt blockweise aus dem BlobStore; noch nicht ausgelagerte Inhalte
    /// kommen direkt aus der Datenbank (SQLite-Blob, der Stream haelt dann eine eigene Verbindung bis zum Schliessen).</summary>
    public static Stream OpenRead(UserDb db, string path) =>
        OpenContent(db, "files", "path=$p AND is_dir=0", ("$p", path)) ?? throw new KeyNotFoundException("Datei nicht gefunden.");

    /// <summary>Inhalt einer Zeile (Datei, Version): Blob, alter Inhalt in data oder leer; null, wenn es die Zeile nicht gibt.</summary>
    private static Stream? OpenContent(UserDb db, string table, string where, params (string, object?)[] args)
    {
        // Zweiter Versuch: zwischen Abfrage und Oeffnen kann die Datei ersetzt und ihr alter Blob geloescht worden sein
        for (int attempt = 0; ; attempt++)
        {
            var conn = db.Open();
            try
            {
                var row = UserDb.Query(conn, $"SELECT rowid, blob, data IS NULL OR length(data)=0 FROM {table} WHERE {where}",
                    r => (Id: r.GetInt64(0), Blob: r.IsDBNull(1) ? null : r.GetString(1), Empty: r.GetInt64(2) != 0), args);
                if (row.Count == 0) { conn.Dispose(); return null; }
                if (row[0].Blob is { } blob)
                {
                    conn.Dispose();
                    try { return db.Blobs.OpenRead(blob); }
                    catch (Exception ex) when (attempt == 0 && ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
                }
                if (row[0].Empty) { conn.Dispose(); return new MemoryStream(Array.Empty<byte>(), false); }
                return new OwnedStream(new SqliteBlob(conn, table, "data", row[0].Id, readOnly: true), conn);
            }
            catch { conn.Dispose(); throw; }
        }
    }

    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); owner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Pfad als GLOB-Muster ohne Platzhalter (fuer "liegt unter": Glob(pfad) + "/*"). GLOB statt LIKE: LIKE
    /// vergleicht in SQLite ohne Ruecksicht auf Gross-/Kleinschreibung - unter "Fotos" waeren sonst auch die Dateien
    /// eines privaten Ordners "fotos" gefunden worden (ZIP eines Links, Kopieren aus einer Freigabe, Loeschen).</summary>
    private static string Glob(string path) => path.Replace("[", "[[]").Replace("*", "[*]").Replace("?", "[?]");

    private static string Now() => DateTime.UtcNow.ToString("o");

    private static string Etag(byte[] sha256) => "\"" + Convert.ToHexString(sha256)[..24].ToLowerInvariant() + "\"";

    /// <summary>Pruefsumme als Text, wie in der Spalte sha256: 64 Zeichen hex, klein.</summary>
    public static string Hex(byte[] sha256) => Convert.ToHexString(sha256).ToLowerInvariant();

    private static bool Exists(SqliteConnection conn, string path) => UserDb.Query(conn, "SELECT 1 FROM files WHERE path=$p", r => 1, ("$p", path)).Any();

    /// <summary>Schreibt eine Datei. Der Ordner darueber muss existieren (sonst 409, RFC 4918 9.7.1).
    /// Ersetzt sie eine andere, wird deren Inhalt als fruehere Version gesichert (nicht im notizi-Ordner).</summary>
    /// <param name="mtime">Aenderungszeit vom Programm (X-OC-Mtime der Nextcloud-Clients), sonst jetzt.</param>
    public static (string Etag, bool Created) Put(UserDb db, string path, byte[] data, string? mime, string? ifMatch = null, string? ifNoneMatch = null, DateTime? mtime = null)
    {
        var replaced = Precheck(db, path, ifMatch, ifNoneMatch);
        BlobInfo blob;
        using (var w = db.Blobs.Create(Room(db, replaced), () => new QuotaExceededException(Quota(db))))
        {
            w.Write(data);
            blob = w.Complete();
        }
        return Commit(db, path, blob, mime, ifMatch, ifNoneMatch, mtime);
    }

    /// <summary>Wie <see cref="Put(UserDb,string,byte[],string?,string?,string?,DateTime?)"/>, aber als Stream: der Inhalt geht
    /// blockweise verschluesselt auf die Platte, nie ganz in den Speicher. Wird es zu viel fuer die Grenze, bricht es ab.</summary>
    public static async Task<(string Etag, bool Created)> PutAsync(UserDb db, string path, Stream data, string? mime, string? ifMatch = null,
        string? ifNoneMatch = null, DateTime? mtime = null, CancellationToken ct = default)
    {
        // Vorab pruefen, was sich ohne Inhalt pruefen laesst - sonst kaeme ein 409 erst nach dem ganzen Upload
        var replaced = Precheck(db, path, ifMatch, ifNoneMatch);
        var blob = await db.Blobs.WriteAsync(data, Room(db, replaced), () => new QuotaExceededException(Quota(db)), ct);
        return Commit(db, path, blob, mime, ifMatch, ifNoneMatch, mtime);
    }

    /// <summary>Einen schon geschriebenen Blob (UserDb.Blobs) als Inhalt eintragen - etwa erst herunterladen, dann den Ort waehlen.
    /// Scheitert es, wird der Blob geloescht.</summary>
    public static (string Etag, bool Created) PutBlob(UserDb db, string path, BlobInfo blob, string? mime) =>
        Commit(db, path, blob, mime, null, null, null);

    /// <summary>Neue Datei unter freiem Namen (Hochladen ueber Freigabelink): ist der Name inzwischen belegt, kommt eine Nummer dran.
    /// Gibt den Pfad zurueck, unter dem sie liegt.</summary>
    public static async Task<string> PutNewAsync(UserDb db, string path, Stream data, string? mime, CancellationToken ct = default)
    {
        path = FreeName(db, path);
        Precheck(db, path, null, "*");
        var blob = await db.Blobs.WriteAsync(data, Room(db, 0), () => new QuotaExceededException(Quota(db)), ct);
        for (int i = 0; ; i++)
        {
            try { Commit(db, path, blob, mime, null, "*", null, keepBlobOnConflict: i < 5); return path; }
            catch (Pim.PreconditionFailedException) when (i < 5) { path = FreeName(db, path); }
        }
    }

    /// <summary>Was sich vor dem Schreiben pruefen laesst; gibt die Groesse der ersetzten Datei zurueck (0: neu).</summary>
    private static long Precheck(UserDb db, string path, string? ifMatch, string? ifNoneMatch)
    {
        if (path.Length == 0) throw new ArgumentException("Die Wurzel ist ein Ordner.");
        if (Get(db, Parent(path)!) is not { IsDir: true }) throw new DavConflictException("Übergeordneter Ordner fehlt.");
        var existing = Get(db, path);
        if (existing is { IsDir: true }) throw new DavConflictException("Dort liegt ein Ordner.");
        if (ifNoneMatch == "*" && existing != null) throw new Pim.PreconditionFailedException();
        if (ifMatch != null && (existing == null || (ifMatch != "*" && ifMatch != existing.Etag))) throw new Pim.PreconditionFailedException();
        return existing?.Size ?? 0;
    }

    /// <summary>Den geschriebenen Blob als Inhalt eintragen. Scheitert es, wird der Blob wieder geloescht.</summary>
    private static (string Etag, bool Created) Commit(UserDb db, string path, BlobInfo blob, string? mime, string? ifMatch, string? ifNoneMatch,
        DateTime? mtime, bool keepBlobOnConflict = false)
    {
        (string Etag, bool Created) result;
        try { result = Commit(db, path, blob.Id, blob.Size, Etag(blob.Sha256), Hex(blob.Sha256), mime, ifMatch, ifNoneMatch, mtime); }
        catch (Pim.PreconditionFailedException) when (keepBlobOnConflict) { throw; }
        catch
        {
            if (blob.Id != null) db.Blobs.Delete(blob.Id);
            throw;
        }
        CollectBlobs(db);
        return result;
    }

    /// <param name="sha256">Pruefsumme des Inhalts (hex), beim Schreiben mitgerechnet - null, wenn unbekannt (Nachrechnen: FileHashes).</param>
    private static (string Etag, bool Created) Commit(UserDb db, string path, string? blob, long size, string etag, string? sha256, string? mime,
        string? ifMatch, string? ifNoneMatch, DateTime? mtime)
    {
        if (path.Length == 0) throw new ArgumentException("Die Wurzel ist ein Ordner.");
        var root = ProtectedRoot(db);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var parent = Parent(path)!;
        var p = UserDb.Query(conn, "SELECT is_dir FROM files WHERE path=$p", r => r.GetInt64(0), ("$p", parent)).FirstOrDefault(-1);
        if (p != 1) throw new DavConflictException("Übergeordneter Ordner fehlt.");
        var existing = UserDb.Query(conn, "SELECT is_dir, etag, COALESCE(size,0), COALESCE(fileid,0) FROM files WHERE path=$p",
            r => (Dir: r.GetInt64(0), Etag: r.GetString(1), Size: r.GetInt64(2), FileId: r.GetInt64(3)), ("$p", path)).FirstOrDefault((Dir: -1L, Etag: "", Size: 0L, FileId: 0L));
        if (existing.Dir == 1) throw new DavConflictException("Dort liegt ein Ordner.");
        bool exists = existing.Dir == 0;
        if (ifNoneMatch == "*" && exists) throw new Pim.PreconditionFailedException();
        if (ifMatch != null && (!exists || (ifMatch != "*" && ifMatch != existing.Etag))) throw new Pim.PreconditionFailedException();
        CheckQuota(db, conn, size - (exists ? existing.Size : 0));

        var modified = mtime?.ToUniversalTime().ToString("o") ?? Now();
        // Fruehere Version sichern: nur echter Inhalt (Windows legt erst eine leere Datei an), nur wenn er sich aendert
        // Wie viele, bestimmt die Aufbewahrung des Benutzers (FileRetention). Die Version uebernimmt den Blob - keine Kopie.
        var (keepVersion, maxVersions) = FileRetention.OnSave(FileRetention.Load(db));
        if (keepVersion && exists && existing.Size > 0 && existing.Etag != etag && existing.FileId > 0 && !InRoot(path, root))
        {
            UserDb.Exec(conn, "INSERT INTO file_versions(fileid,size,etag,modified,saved,data,blob,sha256) SELECT fileid,size,etag,modified,$n,data,blob,sha256 FROM files WHERE path=$p",
                ("$p", path), ("$n", Now()));
            UserDb.Exec(conn, "DELETE FROM file_versions WHERE fileid=$f AND id NOT IN (SELECT id FROM file_versions WHERE fileid=$f ORDER BY id DESC LIMIT $m)",
                ("$f", existing.FileId), ("$m", maxVersions));
        }
        UserDb.Exec(conn, @"INSERT INTO files(path,parent,is_dir,data,blob,size,etag,mime,created,modified,sha256) VALUES($p,$par,0,NULL,$b,$s,$e,$m,$c,$n,$h)
            ON CONFLICT(path) DO UPDATE SET data=NULL, blob=$b, size=$s, etag=$e, mime=COALESCE($m,mime), modified=$n, sha256=$h",
            ("$p", path), ("$par", parent), ("$b", blob), ("$s", size), ("$e", etag), ("$m", mime), ("$c", Now()), ("$n", modified), ("$h", sha256));
        Touch(conn, parent);
        MakeRoom(db, conn);
        tx.Commit();
        return (etag, !exists);
    }

    public static void Mkcol(UserDb db, string path)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var parent = Parent(path) ?? throw new DavConflictException("Gibt es schon.");
        if (UserDb.Query(conn, "SELECT is_dir FROM files WHERE path=$p", r => r.GetInt64(0), ("$p", parent)).FirstOrDefault(-1) != 1)
            throw new DavConflictException("Übergeordneter Ordner fehlt.");
        if (Exists(conn, path)) throw new InvalidOperationException("Gibt es schon.");
        InsertDir(conn, path, parent);
        Touch(conn, parent);
        tx.Commit();
    }

    private static void InsertDir(SqliteConnection conn, string path, string parent)
    {
        var now = Now();
        UserDb.Exec(conn, "INSERT INTO files(path,parent,is_dir,etag,created,modified) VALUES($p,$par,1,$e,$n,$n)",
            ("$p", path), ("$par", parent), ("$e", "\"" + Guid.NewGuid().ToString("N")[..16] + "\""), ("$n", now));
    }

    /// <summary>Legt alle fehlenden Ordner bis einschliesslich path an.</summary>
    public static void EnsureDir(UserDb db, string path)
    {
        var current = "";
        foreach (var part in Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? part : current + "/" + part;
            if (Get(db, current) is { } e)
            {
                if (!e.IsDir) throw new DavConflictException($"Ist eine Datei, kein Ordner: {current}");
                continue;
            }
            Mkcol(db, current);
        }
    }

    /// <summary>Freier Name neben dem gewuenschten: "Bericht.pdf" → "Bericht (2).pdf", "Ordner" → "Ordner (2)".</summary>
    public static string FreeName(UserDb db, string path)
    {
        using var conn = db.Open();
        return FreeName(conn, path);
    }

    private static string FreeName(SqliteConnection conn, string path)
    {
        if (!Exists(conn, path)) return path;
        var dir = Parent(path)!;
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        // Schon eine Nummer dran: weiterzaehlen statt "x (2) (2)"
        var m = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*) \((\d+)\)$");
        int n = 2;
        if (m.Success) { stem = m.Groups[1].Value; n = int.Parse(m.Groups[2].Value) + 1; }
        for (; ; n++)
        {
            var candidate = (dir.Length == 0 ? "" : dir + "/") + $"{stem} ({n}){ext}";
            if (!Exists(conn, candidate)) return candidate;
        }
    }

    public static bool Delete(UserDb db, string path) => Delete(db, path, out _);

    /// <summary>Loescht in den Papierkorb - im notizi-Ordner endgueltig.</summary>
    /// <param name="trashId">Eintrag im Papierkorb, 0 wenn endgueltig geloescht oder nicht gefunden.</param>
    public static bool Delete(UserDb db, string path, out long trashId)
    {
        if (path.Length == 0) throw new ArgumentException("Die Wurzel bleibt.");
        var root = ProtectedRoot(db);
        trashId = 0;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (!Exists(conn, path)) return false;
        if (!InRoot(path, root)) trashId = ToTrash(conn, path, root);
        UserDb.Exec(conn, "DELETE FROM files WHERE path=$p OR path GLOB $l", ("$p", path), ("$l", Glob(path) + "/*"));
        Touch(conn, Parent(path)!);
        tx.Commit();
        CollectBlobs(db);
        return true;
    }

    /// <summary>Kopiert den Eintrag samt Inhalt in den Papierkorb (ohne den notizi-Ordner darunter). Geloescht wird danach.</summary>
    private static long ToTrash(SqliteConnection conn, string path, string root)
    {
        var skip = Contains(path, root) ? " AND NOT (path=$r OR path GLOB $rl)" : "";
        var args = new (string, object?)[] { ("$p", path), ("$l", Glob(path) + "/*"), ("$r", root), ("$rl", Glob(root) + "/*"), ("$n", Now()) };
        var where = "(path=$p OR path GLOB $l)" + skip;
        UserDb.Exec(conn, $@"INSERT INTO file_trash(path,is_dir,size,deleted)
            SELECT $p, (SELECT is_dir FROM files WHERE path=$p), (SELECT COALESCE(SUM(size),0) FROM files WHERE {where}), $n", args);
        var id = UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First();
        UserDb.Exec(conn, $@"INSERT INTO file_trash_items(trash_id,rel,is_dir,size,etag,mime,created,modified,fileid,data,blob,sha256)
            SELECT $id, CASE WHEN path=$p THEN '' ELSE substr(path, length($p)+2) END, is_dir, size, etag, mime, created, modified, fileid, data, blob, sha256
            FROM files WHERE {where}", args.Append(("$id", id)).ToArray());
        return id;
    }

    // ---------------- Papierkorb ----------------

    public static List<TrashEntry> Trash(UserDb db) =>
        db.Query(@"SELECT t.id, t.path, t.is_dir, t.size, t.deleted, i.mime FROM file_trash t
            LEFT JOIN file_trash_items i ON i.trash_id=t.id AND i.rel='' ORDER BY t.deleted DESC, t.id DESC",
            r => new TrashEntry(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, r.GetInt64(3), Time(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5)));

    /// <summary>Zurueck an den alten Ort; fehlende Ordner entstehen neu, ist der Name belegt, bekommt er eine Nummer.
    /// Gibt den Pfad zurueck, unter dem es jetzt liegt.</summary>
    public static string Restore(UserDb db, long id)
    {
        var root = ProtectedRoot(db);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var e = UserDb.Query(conn, "SELECT path, size FROM file_trash WHERE id=$i", r => (Path: r.GetString(0), Size: r.GetInt64(1)), ("$i", id))
            .FirstOrDefault() ;
        if (e.Path == null) throw new KeyNotFoundException("Nicht im Papierkorb.");
        if (InRoot(e.Path, root)) throw new InvalidOperationException("Der Ort liegt im geschützten notizi-Ordner.");
        CheckQuota(db, conn, e.Size);
        // Fehlende Ordner auf dem Weg anlegen (auch wenn dort inzwischen eine Datei liegt: dann daneben)
        var parent = Parent(e.Path)!;
        var current = "";
        foreach (var part in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Length == 0 ? part : current + "/" + part;
            var kind = UserDb.Query(conn, "SELECT is_dir FROM files WHERE path=$p", r => r.GetInt64(0), ("$p", next)).FirstOrDefault(-1);
            if (kind == 0) { parent = current; break; }
            if (kind == -1) InsertDir(conn, next, current);
            current = next;
        }
        var target = FreeName(conn, (parent.Length == 0 ? "" : parent + "/") + e.Path[(e.Path.LastIndexOf('/') + 1)..]);
        var rels = UserDb.Query(conn, "SELECT rel FROM file_trash_items WHERE trash_id=$i ORDER BY length(rel), rel", r => r.GetString(0), ("$i", id));
        foreach (var rel in rels)
        {
            var path = rel.Length == 0 ? target : target + "/" + rel;
            // fileid behalten (Freigaben, Favoriten, Versionen haengen daran) - ausser sie ist inzwischen vergeben
            UserDb.Exec(conn, @"INSERT INTO files(path,parent,is_dir,data,blob,size,etag,mime,created,modified,sha256,fileid)
                SELECT $p,$par,is_dir,data,blob,size,etag,mime,created,modified,sha256,
                    CASE WHEN fileid IN (SELECT fileid FROM files WHERE fileid IS NOT NULL) THEN NULL ELSE fileid END
                FROM file_trash_items WHERE trash_id=$i AND rel=$r",
                ("$p", path), ("$par", Parent(path)), ("$i", id), ("$r", rel));
        }
        UserDb.Exec(conn, "DELETE FROM file_trash WHERE id=$i", ("$i", id));
        Touch(conn, Parent(target)!);
        tx.Commit();
        return target;
    }

    /// <summary>Endgueltig loeschen: diese Eintraege, oder alle (ids = null).</summary>
    public static int PurgeTrash(UserDb db, IEnumerable<long>? ids)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        int n = 0;
        if (ids == null) n = UserDb.Exec(conn, "DELETE FROM file_trash");
        else foreach (var id in ids) n += UserDb.Exec(conn, "DELETE FROM file_trash WHERE id=$i", ("$i", id));
        Orphans(conn);
        tx.Commit();
        CollectBlobs(db);
        return n;
    }

    /// <summary>Papierkorb nach Tagen leeren und Verwaistes wegraeumen (Hintergrunddienst; die Versionen regelt FileRetention).</summary>
    public static int Expire(UserDb db, int trashDays, DateTime now)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        int n = 0;
        if (trashDays > 0)
            n = UserDb.Exec(conn, "DELETE FROM file_trash WHERE deleted < $c", ("$c", now.AddDays(-trashDays).ToUniversalTime().ToString("o")));
        Orphans(conn);
        tx.Commit();
        CollectBlobs(db);
        // Einmal am Tag: Blobs ohne Verweis (Absturz zwischen Schreiben und Eintragen) und halbfertige Uploads
        if (!DateTime.TryParse(db.Get("blobs_swept"), null, DateTimeStyles.RoundtripKind, out var swept) || swept < now.ToUniversalTime().AddDays(-1))
        {
            SweepBlobs(db, TimeSpan.FromDays(1));
            db.Set("blobs_swept", now.ToUniversalTime().ToString("o"));
        }
        return n;
    }

    // ---------------- Blobs: Aufraeumen und Auslagern ----------------

    private static bool Referenced(SqliteConnection conn, string blob) =>
        UserDb.Query(conn, string.Join(" UNION ALL ", UserDb.BlobTables.Select(t => $"SELECT 1 FROM {t} WHERE blob=$b")) + " LIMIT 1",
            r => 1, ("$b", blob)).Count > 0;

    /// <summary>Blobs loeschen, auf die nichts mehr zeigt: vorgemerkt in blob_gone (Trigger beim Loeschen oder Ersetzen
    /// eines Verweises). Versionen, Papierkorb und Kopien teilen sich Blobs - weg ist er erst mit dem letzten Verweis.
    /// Gibt die Zahl der geloeschten Blobs zurueck.</summary>
    public static int CollectBlobs(UserDb db)
    {
        int n = 0;
        using var conn = db.Open();
        while (true)
        {
            // Schreibsperre: zwischen Pruefen und Loeschen kommt kein neuer Verweis dazu
            using var tx = conn.BeginTransaction();
            var ids = UserDb.Query(conn, "SELECT id FROM blob_gone LIMIT 500", r => r.GetString(0));
            if (ids.Count == 0) return n;
            foreach (var id in ids)
            {
                if (BlobStore.ValidId(id) && !Referenced(conn, id)) { db.Blobs.Delete(id); n++; }
                UserDb.Exec(conn, "DELETE FROM blob_gone WHERE id=$i", ("$i", id));
            }
            tx.Commit();
        }
    }

    /// <summary>Blob-Dateien ohne Verweis in der Datenbank loeschen, wenn sie aelter sind als <paramref name="grace"/>
    /// (juengere koennten gerade eingetragen werden), dazu halbfertige Uploads. Gibt die Zahl der geloeschten zurueck.</summary>
    public static int SweepBlobs(UserDb db, TimeSpan grace)
    {
        int n = db.Blobs.CleanTemp(grace);
        var cut = DateTime.UtcNow - grace;
        using var conn = db.Open();
        foreach (var (id, modified) in db.Blobs.List().ToList())
        {
            if (modified > cut) continue;
            using var tx = conn.BeginTransaction();
            if (!Referenced(conn, id)) { db.Blobs.Delete(id); n++; }
            tx.Commit();
        }
        return n;
    }

    /// <summary>Wie viele Inhalte noch in der Datenbank liegen (Spalte data) statt im BlobStore.</summary>
    public static long PendingBlobs(UserDb db) =>
        UserDb.BlobTables.Sum(t => Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM {t} WHERE {Pending(t)}") ?? 0L));

    /// <summary>Wie viele Bytes davon noch in der Datenbank liegen (so viel Platz braucht die Auslagerung im BlobStore).</summary>
    public static long PendingBlobBytes(UserDb db) =>
        UserDb.BlobTables.Sum(t => Convert.ToInt64(db.Scalar($"SELECT COALESCE(SUM(length(data)),0) FROM {t} WHERE {Pending(t)}") ?? 0L));

    /// <summary>Zeile mit Inhalt noch in der Datenbank. file_thumbs.data ist NOT NULL - dort steht danach ein leerer Wert
    /// (leer ohne Blob heisst wie bisher: kein Vorschaubild moeglich).</summary>
    private static string Pending(string table) => table == "file_thumbs" ? "length(data)>0" : "data IS NOT NULL";

    /// <summary>
    /// Alte Inhalte aus der Datenbank in den BlobStore auslagern, Zeile fuer Zeile: als Stream aus dem SQLite-Blob lesen,
    /// verschluesselt schreiben, dann in einer kurzen Transaktion blob setzen und data leeren. Jede Zeile fuer sich -
    /// abbrechen und spaeter weitermachen geht jederzeit (Neustart, <paramref name="ct"/>). Hat jemand die Zeile inzwischen
    /// neu geschrieben, bleibt dessen Inhalt und der eben geschriebene Blob wird verworfen.
    /// Den frei gewordenen Platz gibt erst ein Verdichten (VACUUM) an die Platte zurueck - das bleibt Sache des Benutzers.
    /// </summary>
    /// <param name="max">Hoechstens so viele Zeilen in diesem Aufruf (0: alle).</param>
    /// <returns>Ausgelagerte Zeilen.</returns>
    public static int MigrateBlobs(UserDb db, CancellationToken ct = default, int max = 0)
    {
        int n = 0;
        foreach (var table in UserDb.BlobTables)
        {
            // Der Reihe nach ueber rowid - ein Durchgang liest die Tabelle einmal, nicht fuer jede Zeile von vorn
            long last = 0;
            while (max == 0 || n < max)
            {
                ct.ThrowIfCancellationRequested();
                var next = db.Query($"SELECT rowid, length(data) FROM {table} WHERE rowid>$last AND {Pending(table)} ORDER BY rowid LIMIT 1",
                    r => (Id: r.GetInt64(0), Length: r.GetInt64(1)), ("$last", last));
                if (next.Count == 0) break;
                var (rowid, length) = next[0];
                last = rowid;
                string? blob = null, sha256 = null;
                if (length > 0)
                {
                    using var conn = db.Open();
                    using var source = new SqliteBlob(conn, table, "data", rowid, readOnly: true);
                    using var w = db.Blobs.Create();
                    source.CopyTo(w, BlobStore.ChunkSize);
                    var info = w.Complete();
                    (blob, sha256) = (info.Id, Hex(info.Sha256));
                }
                using (var conn = db.Open())
                {
                    var empty = table == "file_thumbs" ? "X''" : "NULL";
                    // Nur, wenn die Zeile noch denselben alten Inhalt hat - sonst hat sie inzwischen jemand neu geschrieben
                    // Die Pruefsumme faellt beim Auslagern mit ab (der Inhalt wird ohnehin gelesen)
                    var hash = UserDb.HashTables.Contains(table) ? ", sha256=COALESCE(sha256,$h)" : "";
                    var changed = UserDb.Exec(conn, $"UPDATE {table} SET blob=$b, data={empty}{hash} WHERE rowid=$r AND blob IS NULL AND {Pending(table)} AND length(data)=$l",
                        ("$b", blob), ("$r", rowid), ("$l", length), ("$h", sha256));
                    if (changed == 0 && blob != null) db.Blobs.Delete(blob);
                    if (changed > 0) n++;
                }
            }
        }
        return n;
    }

    /// <summary>Was an endgueltig verschwundenen Dateien hing: Versionen, Vorschaubilder, Favoriten, Verlauf, Links.</summary>
    private static void Orphans(SqliteConnection conn)
    {
        const string gone = "NOT IN (SELECT fileid FROM files WHERE fileid IS NOT NULL) AND fileid NOT IN (SELECT fileid FROM file_trash_items WHERE fileid IS NOT NULL)";
        foreach (var table in new[] { "file_versions", "file_thumbs", "file_favorites", "file_recent", "file_links" })
            UserDb.Exec(conn, $"DELETE FROM {table} WHERE fileid {gone}");
    }

    // ---------------- Fruehere Versionen ----------------

    public static List<FileVersion> Versions(UserDb db, long fileId) =>
        db.Query("SELECT id, size, etag, modified, saved FROM file_versions WHERE fileid=$f ORDER BY id DESC",
            r => new FileVersion(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Time(r.GetString(3)), Time(r.GetString(4))), ("$f", fileId));

    /// <summary>Inhalt einer Version als Stream, null wenn es sie nicht gibt.</summary>
    public static Stream? OpenVersion(UserDb db, long fileId, long id) =>
        OpenContent(db, "file_versions", "fileid=$f AND id=$i", ("$f", fileId), ("$i", id));

    public static byte[]? VersionData(UserDb db, long fileId, long id) => OpenVersion(db, fileId, id) is { } s ? ReadAll(s) : null;

    /// <summary>Version wiederherstellen: wird wieder der aktuelle Inhalt, der bisherige kommt als Version dazu.
    /// Der Blob der Version wird mitbenutzt, nicht kopiert.</summary>
    public static string RestoreVersion(UserDb db, string path, long id)
    {
        var f = Get(db, path) ?? throw new KeyNotFoundException("Datei nicht gefunden.");
        var v = db.Query("SELECT blob, size, etag, sha256 FROM file_versions WHERE fileid=$f AND id=$i",
            r => (Blob: r.IsDBNull(0) ? null : r.GetString(0), Size: r.GetInt64(1), Etag: r.GetString(2), Sha256: r.IsDBNull(3) ? null : r.GetString(3)),
            ("$f", f.FileId), ("$i", id));
        if (v.Count == 0) throw new KeyNotFoundException("Version nicht gefunden.");
        if (v[0].Blob is { } blob)
        {
            var etag = Commit(db, path, blob, v[0].Size, v[0].Etag, v[0].Sha256, f.Mime, null, null, null).Etag;
            CollectBlobs(db);
            return etag;
        }
        // Noch nicht ausgelagert: ueber einen neuen Blob
        using var stream = OpenVersion(db, f.FileId, id)!;
        return PutAsync(db, path, stream, f.Mime).GetAwaiter().GetResult().Etag;
    }

    // ---------------- Verschieben, Kopieren ----------------

    /// <summary>Verschieben oder kopieren, Ordner samt Inhalt. Gibt zurueck, ob das Ziel neu ist.
    /// Ein ersetztes Ziel kommt in den Papierkorb. Kopien eines Ordners ueber dem notizi-Ordner lassen diesen aus.</summary>
    public static bool MoveOrCopy(UserDb db, string from, string to, bool overwrite, bool copy) => MoveOrCopy(db, from, to, overwrite, copy, out _);

    public static bool MoveOrCopy(UserDb db, string from, string to, bool overwrite, bool copy, out long replacedTrashId)
    {
        replacedTrashId = 0;
        if (from.Length == 0 || to.Length == 0) throw new ArgumentException("Die Wurzel bleibt.");
        if (to == from) throw new DavForbiddenException("Quelle und Ziel sind gleich.");
        if (to.StartsWith(from + "/")) throw new ArgumentException("Ziel liegt in der Quelle.");
        // Ziel ueber der Quelle: Ueberschreiben loeschte die Quelle gleich mit
        if (from.StartsWith(to + "/")) throw new DavConflictException("Ziel ist ein Ordner über der Quelle.");
        var root = ProtectedRoot(db);
        // Kopie eines Ordners, in dem der notizi-Ordner liegt: ihn auslassen - sonst laege eine sichtbare Kopie daneben
        var skip = copy && Contains(from, root) ? root : null;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (!Exists(conn, from)) throw new KeyNotFoundException();
        var parent = Parent(to)!;
        if (UserDb.Query(conn, "SELECT is_dir FROM files WHERE path=$p", r => r.GetInt64(0), ("$p", parent)).FirstOrDefault(-1) != 1)
            throw new DavConflictException("Zielordner fehlt.");
        bool exists = Exists(conn, to);
        if (exists && !overwrite) throw new Pim.PreconditionFailedException();
        var rows = UserDb.Query(conn, "SELECT path, size FROM files WHERE path=$p OR path GLOB $l ORDER BY length(path)",
            r => (Path: r.GetString(0), Size: r.GetInt64(1)), ("$p", from), ("$l", Glob(from) + "/*"))
            .Where(r => skip == null || !InRoot(r.Path, skip)).ToList();
        if (copy)
        {
            var replaced = exists ? Convert.ToInt64(UserDb.Query(conn, "SELECT COALESCE(SUM(size),0) FROM files WHERE path=$p OR path GLOB $l",
                r => r.GetInt64(0), ("$p", to), ("$l", Glob(to) + "/*")).First()) : 0;
            CheckQuota(db, conn, rows.Sum(r => r.Size) - replaced);
        }
        if (exists)
        {
            if (!InRoot(to, root)) replacedTrashId = ToTrash(conn, to, root);
            UserDb.Exec(conn, "DELETE FROM files WHERE path=$p OR path GLOB $l", ("$p", to), ("$l", Glob(to) + "/*"));
        }

        var now = Now();
        foreach (var (src, _) in rows)
        {
            var dst = to + src[from.Length..];
            var dstParent = Parent(dst)!;
            if (copy)
                // Die Kopie teilt sich den Blob mit dem Original (kein Duplikat); geloescht wird er erst mit dem letzten Verweis
                UserDb.Exec(conn, @"INSERT INTO files(path,parent,is_dir,data,blob,size,etag,mime,created,modified,sha256)
                    SELECT $d,$dp,is_dir,data,blob,size,etag,mime,$n,$n,sha256 FROM files WHERE path=$s", ("$d", dst), ("$dp", dstParent), ("$n", now), ("$s", src));
            else
                UserDb.Exec(conn, "UPDATE files SET path=$d, parent=$dp, modified=CASE WHEN path=$root THEN $n ELSE modified END WHERE path=$s",
                    ("$d", dst), ("$dp", dstParent), ("$n", now), ("$s", src), ("$root", from));
        }
        Touch(conn, parent);
        if (!copy) Touch(conn, Parent(from)!);
        MakeRoom(db, conn);
        tx.Commit();
        CollectBlobs(db);
        return !exists;
    }

    /// <summary>
    /// Ordner aendert ETag und Zeit, wenn sich darin etwas aendert - und ebenso jeder Ordner darueber
    /// bis zur Wurzel. Nextcloud-Clients steigen nur in Ordner mit neuem ETag hinab und finden Neues so.
    /// </summary>
    private static void Touch(SqliteConnection conn, string dir)
    {
        var now = Now();
        for (string? d = dir; d != null; d = Parent(d))
            UserDb.Exec(conn, "UPDATE files SET etag=$e, modified=$n WHERE path=$p AND is_dir=1",
                ("$e", "\"" + Guid.NewGuid().ToString("N")[..16] + "\""), ("$n", now), ("$p", d));
    }

    /// <summary>Belegt von den Dateien selbst (ohne Papierkorb und Versionen).</summary>
    public static long Usage(UserDb db) => Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(size),0) FROM files") ?? 0L);

    public static long TrashUsage(UserDb db) => Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(size),0) FROM file_trash") ?? 0L);

    public static long VersionUsage(UserDb db) => Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(size),0) FROM file_versions") ?? 0L);

    /// <summary>Alles zusammen, wie es zum Speicher zaehlt.</summary>
    public static long UsageTotal(UserDb db) => Usage(db) + TrashUsage(db) + VersionUsage(db);

    // ---------------- Suchen, zuletzt verwendet, Favoriten ----------------

    /// <summary>Dateien und Ordner, deren Name den Begriff enthaelt (gross/klein und Akzente egal), unter einem Ordner.</summary>
    /// <param name="keep">weitere Bedingung - gilt vor der Grenze, sonst fallen Treffer hinter den ersten <paramref name="limit"/> Eintraegen weg</param>
    public static List<FileEntry> Search(UserDb db, string under, string term, int limit = 500, Func<FileEntry, bool>? keep = null)
    {
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        term = term.Trim();
        // Neueste zuerst: stoesst die Suche an die Grenze, fehlen die alten Dateien, nicht die neuen
        return db.Query($"SELECT {Columns} FROM files WHERE path<>'' AND ($u='' OR path GLOB $l) ORDER BY modified DESC", Read, ("$u", under), ("$l", Glob(under) + "/*"))
            .Where(f => term.Length == 0 || compare.IndexOf(f.Name, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            .Where(f => keep == null || keep(f))
            .Take(limit).ToList();
    }

    /// <summary>Geoeffnet oder heruntergeladen (Weboberflaeche) - fuer "Zuletzt verwendet".</summary>
    public static void Used(UserDb db, long fileId)
    {
        if (fileId <= 0) return;
        db.Exec("INSERT INTO file_recent(fileid,used) VALUES($f,$n) ON CONFLICT(fileid) DO UPDATE SET used=$n", ("$f", fileId), ("$n", Now()));
        db.Exec("DELETE FROM file_recent WHERE fileid NOT IN (SELECT fileid FROM file_recent ORDER BY used DESC LIMIT 200)");
    }

    /// <summary>Zuletzt geoeffnet oder geaendert, neueste zuerst - mit dem Zeitpunkt.</summary>
    public static List<(FileEntry File, DateTime Used)> Recent(UserDb db, int limit = 60)
    {
        var opened = db.Query($"SELECT {ColumnsF}, r.used FROM file_recent r JOIN files f ON f.fileid=r.fileid WHERE f.is_dir=0 ORDER BY r.used DESC LIMIT $n",
            r => (Read(r), Time(r.GetString(8))), ("$n", limit));
        var changed = db.Query($"SELECT {Columns} FROM files WHERE is_dir=0 ORDER BY modified DESC LIMIT $n", Read, ("$n", limit))
            .Select(f => (f, f.Modified));
        return opened.Concat(changed).GroupBy(x => x.Item1.Path).Select(g => g.MaxBy(x => x.Item2))
            .OrderByDescending(x => x.Item2).Take(limit).Select(x => (x.Item1, x.Item2)).ToList();
    }

    public static void Favorite(UserDb db, long fileId, bool on)
    {
        if (on) db.Exec("INSERT OR IGNORE INTO file_favorites(fileid,added) VALUES($f,$n)", ("$f", fileId), ("$n", Now()));
        else db.Exec("DELETE FROM file_favorites WHERE fileid=$f", ("$f", fileId));
    }

    public static List<FileEntry> Favorites(UserDb db) =>
        db.Query($"SELECT {ColumnsF} FROM file_favorites v JOIN files f ON f.fileid=v.fileid ORDER BY f.is_dir DESC, v.added", Read);

    public static HashSet<long> FavoriteIds(UserDb db) => db.Query("SELECT fileid FROM file_favorites", r => r.GetInt64(0)).ToHashSet();

    public static HashSet<long> SharedIds(UserDb db) => db.Query("SELECT DISTINCT fileid FROM file_links", r => r.GetInt64(0)).ToHashSet();
}
