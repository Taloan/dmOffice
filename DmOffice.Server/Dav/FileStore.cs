using System.Security.Cryptography;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Dav;

public sealed record FileEntry(string Path, bool IsDir, long Size, string Etag, string? Mime, DateTime Created, DateTime Modified)
{
    public string Name => Path.Length == 0 ? "" : Path[(Path.LastIndexOf('/') + 1)..];
}

public sealed class DavConflictException : Exception { public DavConflictException(string m) : base(m) { } }

/// <summary>Nicht erlaubt (403), etwa Verschieben auf sich selbst.</summary>
public sealed class DavForbiddenException : Exception { public DavForbiddenException(string m) : base(m) { } }

/// <summary>
/// Die Dateien eines Benutzers (WebDAV) - in seiner verschluesselten
/// Datenbank, wie alles andere. Pfade ohne fuehrenden und abschliessenden
/// Schraegstrich, "" ist die Wurzel.
/// </summary>
/// <summary>Speicher voll - DAV antwortet mit 507 (RFC 4918 11.5).</summary>
public sealed class QuotaExceededException : Exception
{
    public QuotaExceededException(long quota) : base($"Speicher voll – erlaubt für Dateien: {quota / 1024 / 1024} MB") { }
}

public static class FileStore
{
    /// <summary>Grenze je Benutzer in Bytes (0 = unbegrenzt) - in Program.cs gesetzt.</summary>
    public static Func<string, long>? QuotaOf;

    public static long Quota(UserDb db) => QuotaOf?.Invoke(db.UserId) ?? 0;

    /// <summary>Dateiablage fuer diesen Benutzer abgeschaltet - in Program.cs gesetzt.</summary>
    public static Func<string, bool>? FilesOffOf;

    public static bool FilesOff(UserDb db) => FilesOffOf?.Invoke(db.UserId) ?? false;

    /// <summary>Liegt der Pfad im Abgleichordner von notizi (oder ist er es)?</summary>
    public static bool InRoot(string path, string root)
    {
        root = Normalize(root);
        return root.Length > 0 && (path == root || path.StartsWith(root + "/", StringComparison.Ordinal));
    }

    /// <summary>Wirft, wenn nach dieser Aenderung mehr belegt waere als erlaubt. Verkleinern geht immer.</summary>
    private static void CheckQuota(UserDb db, SqliteConnection conn, long added)
    {
        if (added <= 0) return;
        var quota = Quota(db);
        if (quota <= 0) return;
        var used = Convert.ToInt64(UserDb.Query(conn, "SELECT COALESCE(SUM(size),0) FROM files", r => r.GetInt64(0)).First());
        if (used + added > quota) throw new QuotaExceededException(quota);
    }

    public static string Normalize(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
            if (p is "." or ".." || p.Any(char.IsControl)) throw new ArgumentException("Ungültiger Pfad.");
        return string.Join("/", parts);
    }

    public static string? Parent(string path) => path.Length == 0 ? null : path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private const string Columns = "path,is_dir,size,etag,mime,created,modified";

    private static FileEntry Read(SqliteDataReader r) => new(r.GetString(0), r.GetInt64(1) != 0, r.GetInt64(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind));

    public static FileEntry? Get(UserDb db, string path) =>
        db.Query($"SELECT {Columns} FROM files WHERE path=$p", Read, ("$p", path)).FirstOrDefault();

    public static List<FileEntry> Children(UserDb db, string path) =>
        db.Query($"SELECT {Columns} FROM files WHERE parent=$p ORDER BY is_dir DESC, path", Read, ("$p", path));

    /// <summary>Alles darunter, fuer Depth: infinity.</summary>
    public static List<FileEntry> Descendants(UserDb db, string path) =>
        db.Query($"SELECT {Columns} FROM files WHERE path LIKE $p ESCAPE '\\' ORDER BY path", Read, ("$p", Like(path) + "/%"));

    public static byte[]? Data(UserDb db, string path) => db.Scalar("SELECT data FROM files WHERE path=$p AND is_dir=0", ("$p", path)) as byte[] ?? (Get(db, path) is { IsDir: false } ? Array.Empty<byte>() : null);

    private static string Like(string path) => path.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Now() => DateTime.UtcNow.ToString("o");

    private static string Etag(byte[] data) => "\"" + Convert.ToHexString(SHA256.HashData(data))[..24].ToLowerInvariant() + "\"";

    /// <summary>Schreibt eine Datei. Der Ordner darueber muss existieren (sonst 409, RFC 4918 9.7.1).</summary>
    public static (string Etag, bool Created) Put(UserDb db, string path, byte[] data, string? mime, string? ifMatch = null, string? ifNoneMatch = null)
    {
        if (path.Length == 0) throw new ArgumentException("Die Wurzel ist ein Ordner.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var parent = Parent(path)!;
        var p = UserDb.Query(conn, "SELECT is_dir FROM files WHERE path=$p", r => r.GetInt64(0), ("$p", parent)).FirstOrDefault(-1);
        if (p != 1) throw new DavConflictException("Übergeordneter Ordner fehlt.");
        var existing = UserDb.Query(conn, "SELECT is_dir, etag, COALESCE(size,0) FROM files WHERE path=$p", r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2)), ("$p", path)).FirstOrDefault((-1L, "", 0L));
        if (existing.Item1 == 1) throw new DavConflictException("Dort liegt ein Ordner.");
        bool exists = existing.Item1 == 0;
        if (ifNoneMatch == "*" && exists) throw new Pim.PreconditionFailedException();
        if (ifMatch != null && (!exists || (ifMatch != "*" && ifMatch != existing.Item2))) throw new Pim.PreconditionFailedException();
        CheckQuota(db, conn, data.LongLength - (exists ? existing.Item3 : 0));

        var etag = Etag(data);
        var now = Now();
        UserDb.Exec(conn, @"INSERT INTO files(path,parent,is_dir,data,size,etag,mime,created,modified) VALUES($p,$par,0,$d,$s,$e,$m,$n,$n)
            ON CONFLICT(path) DO UPDATE SET data=$d, size=$s, etag=$e, mime=COALESCE($m,mime), modified=$n",
            ("$p", path), ("$par", parent), ("$d", data), ("$s", data.LongLength), ("$e", etag), ("$m", mime), ("$n", now));
        Touch(conn, parent);
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
        if (UserDb.Query(conn, "SELECT 1 FROM files WHERE path=$p", r => 1, ("$p", path)).Any())
            throw new InvalidOperationException("Gibt es schon.");
        var now = Now();
        UserDb.Exec(conn, "INSERT INTO files(path,parent,is_dir,etag,created,modified) VALUES($p,$par,1,$e,$n,$n)",
            ("$p", path), ("$par", parent), ("$e", "\"" + Guid.NewGuid().ToString("N")[..16] + "\""), ("$n", now));
        Touch(conn, parent);
        tx.Commit();
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

    public static bool Delete(UserDb db, string path)
    {
        if (path.Length == 0) throw new ArgumentException("Die Wurzel bleibt.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var n = UserDb.Exec(conn, "DELETE FROM files WHERE path=$p OR path LIKE $l ESCAPE '\\'", ("$p", path), ("$l", Like(path) + "/%"));
        if (n > 0) Touch(conn, Parent(path)!);
        tx.Commit();
        return n > 0;
    }

    /// <summary>Verschieben oder kopieren, Ordner samt Inhalt. Gibt zurueck, ob das Ziel neu ist.</summary>
    public static bool MoveOrCopy(UserDb db, string from, string to, bool overwrite, bool copy)
    {
        if (from.Length == 0 || to.Length == 0) throw new ArgumentException("Die Wurzel bleibt.");
        if (to == from) throw new DavForbiddenException("Quelle und Ziel sind gleich.");
        if (to.StartsWith(from + "/")) throw new ArgumentException("Ziel liegt in der Quelle.");
        // Ziel ueber der Quelle: Ueberschreiben loeschte die Quelle gleich mit
        if (from.StartsWith(to + "/")) throw new DavConflictException("Ziel ist ein Ordner über der Quelle.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (!UserDb.Query(conn, "SELECT 1 FROM files WHERE path=$p", r => 1, ("$p", from)).Any()) throw new KeyNotFoundException();
        var parent = Parent(to)!;
        if (UserDb.Query(conn, "SELECT is_dir FROM files WHERE path=$p", r => r.GetInt64(0), ("$p", parent)).FirstOrDefault(-1) != 1)
            throw new DavConflictException("Zielordner fehlt.");
        bool exists = UserDb.Query(conn, "SELECT 1 FROM files WHERE path=$p", r => 1, ("$p", to)).Any();
        if (exists && !overwrite) throw new Pim.PreconditionFailedException();
        if (copy)
        {
            var copied = Convert.ToInt64(UserDb.Query(conn, "SELECT COALESCE(SUM(size),0) FROM files WHERE path=$p OR path LIKE $l ESCAPE '\\'",
                r => r.GetInt64(0), ("$p", from), ("$l", Like(from) + "/%")).First());
            var replaced = exists ? Convert.ToInt64(UserDb.Query(conn, "SELECT COALESCE(SUM(size),0) FROM files WHERE path=$p OR path LIKE $l ESCAPE '\\'",
                r => r.GetInt64(0), ("$p", to), ("$l", Like(to) + "/%")).First()) : 0;
            CheckQuota(db, conn, copied - replaced);
        }
        if (exists) UserDb.Exec(conn, "DELETE FROM files WHERE path=$p OR path LIKE $l ESCAPE '\\'", ("$p", to), ("$l", Like(to) + "/%"));

        var now = Now();
        var rows = UserDb.Query(conn, "SELECT path FROM files WHERE path=$p OR path LIKE $l ESCAPE '\\' ORDER BY length(path)",
            r => r.GetString(0), ("$p", from), ("$l", Like(from) + "/%"));
        foreach (var src in rows)
        {
            var dst = to + src[from.Length..];
            var dstParent = Parent(dst)!;
            if (copy)
                UserDb.Exec(conn, @"INSERT INTO files(path,parent,is_dir,data,size,etag,mime,created,modified)
                    SELECT $d,$dp,is_dir,data,size,etag,mime,$n,$n FROM files WHERE path=$s", ("$d", dst), ("$dp", dstParent), ("$n", now), ("$s", src));
            else
                UserDb.Exec(conn, "UPDATE files SET path=$d, parent=$dp, modified=CASE WHEN path=$root THEN $n ELSE modified END WHERE path=$s",
                    ("$d", dst), ("$dp", dstParent), ("$n", now), ("$s", src), ("$root", from));
        }
        Touch(conn, parent);
        if (!copy) Touch(conn, Parent(from)!);
        tx.Commit();
        return !exists;
    }

    /// <summary>Ordner aendert ETag und Zeit, wenn sich darin etwas aendert - Programme erkennen so Neues.</summary>
    private static void Touch(SqliteConnection conn, string dir) =>
        UserDb.Exec(conn, "UPDATE files SET etag=$e, modified=$n WHERE path=$p AND is_dir=1",
            ("$e", "\"" + Guid.NewGuid().ToString("N")[..16] + "\""), ("$n", Now()), ("$p", dir));

    public static long Usage(UserDb db) => Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(size),0) FROM files") ?? 0L);
}
