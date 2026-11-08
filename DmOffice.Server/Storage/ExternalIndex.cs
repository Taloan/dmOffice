using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using DmOffice.Core;
using DmOffice.Search;
using Microsoft.Data.Sqlite;

namespace DmOffice.Storage;

/// <summary>
/// Suche in Server-Mounts, je Mount in drei Stufen (<see cref="ExternalFolderDef.Level"/>): nur bereitstellen, Dateien und Ordner,
/// auch Inhalte gewählter Dateigruppen. Ein zentraler Index für alle (data/extindex.db, verschlüsselt, nicht im Backup - er lässt sich
/// jederzeit neu aufbauen): Sehen mehrere Benutzer denselben Mount, wird er nur einmal gelesen; die Treffer bekommt, wer ihn sehen darf.
/// Weil sich dort Dateien ohne dmOffice ändern, prüft <see cref="ExternalIndexer"/> nach Zeitplan (Änderungsdatum und Größe) und wertet
/// Neues und Geändertes aus. Verknüpfungen (Symlinks) werden nicht verfolgt. Schreibzugriffe bleiben kurz (je Ordner eine Transaktion,
/// das Auflisten läuft außerhalb), damit Prüfung, Auswerten und Verwaltung sich nicht gegenseitig sperren.
/// </summary>
public sealed class ExternalIndex
{
    private const int SchemaVersion = 2;
    private const int Chunk = 1000;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, (DateTime At, State State)> _states = new();

    static ExternalIndex() => SQLitePCL.Batteries_V2.Init();

    public ExternalIndex(DataPaths paths, Vault vault) : this(Path.Combine(paths.Root, "extindex.db"), LoadKey(paths, vault)) { }

    internal ExternalIndex(string path, byte[] key)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Password = "x'" + Convert.ToHexString(key) + "'", DefaultTimeout = 30 }.ToString();
        using var conn = Open();
        // Ältere Form: wegwerfen, der Index lässt sich neu aufbauen
        if (UserDb.Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First() < SchemaVersion)
            UserDb.Exec(conn, "DROP TRIGGER IF EXISTS ext_files_ad; DROP TABLE IF EXISTS ext_fts; DROP TABLE IF EXISTS ext_files; DROP TABLE IF EXISTS ext_mounts;");
        UserDb.Exec(conn, @"
CREATE TABLE IF NOT EXISTS ext_mounts (
    mount        TEXT PRIMARY KEY,
    root         TEXT NOT NULL,
    include      TEXT NOT NULL,
    content_key  TEXT NOT NULL DEFAULT '',
    last_scan    INTEGER NULL,
    scan_stamp   INTEGER NULL,              -- Prüfung läuft oder wurde unterbrochen: geht damit weiter
    root_listed  INTEGER NOT NULL DEFAULT 0,
    scan_seconds INTEGER NOT NULL DEFAULT 0,
    last_seconds INTEGER NOT NULL DEFAULT 0,
    read_bytes   INTEGER NOT NULL DEFAULT 0,
    read_seconds REAL    NOT NULL DEFAULT 0,
    reset        INTEGER NOT NULL DEFAULT 0,
    scan_note    TEXT NULL
);
CREATE TABLE IF NOT EXISTS ext_files (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    mount    TEXT    NOT NULL,
    path     TEXT    NOT NULL,              -- relativ zum Mount, mit /
    parent   TEXT    NOT NULL,
    top      TEXT    NOT NULL,              -- Ordner der obersten Ebene; '' bei Dateien dort
    name     TEXT    NOT NULL,
    is_dir   INTEGER NOT NULL,
    grp      TEXT    NOT NULL DEFAULT '',   -- text | docs | images | '' (keine Inhalte möglich)
    size     INTEGER NOT NULL,
    modified INTEGER NOT NULL,              -- Unix-Sekunden
    status   TEXT    NOT NULL,              -- dir | named | pending | ok | empty | skipped | error
    method   TEXT    NOT NULL DEFAULT 'none',
    note     TEXT    NULL,
    want_ocr INTEGER NOT NULL DEFAULT 0,
    tries    INTEGER NOT NULL DEFAULT 0,
    listed   INTEGER NOT NULL DEFAULT 0,
    UNIQUE (mount, path)
);
CREATE INDEX IF NOT EXISTS ext_files_parent ON ext_files(mount, parent);
CREATE INDEX IF NOT EXISTS ext_files_pending ON ext_files(mount, status, grp, top);
CREATE VIRTUAL TABLE IF NOT EXISTS ext_fts USING fts5(name, body, tokenize='unicode61 remove_diacritics 2');
CREATE TRIGGER IF NOT EXISTS ext_files_ad AFTER DELETE ON ext_files BEGIN
    DELETE FROM ext_fts WHERE rowid = old.id;
END;");
        UserDb.Exec(conn, $"PRAGMA user_version = {SchemaVersion}");
    }

    private static byte[] LoadKey(DataPaths paths, Vault vault)
    {
        Directory.CreateDirectory(paths.Keys);
        var file = Path.Combine(paths.Keys, "extindex.key");
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
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static long Unix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Höchstens so viele Einträge je Mount - eine riesige Freigabe soll den Server nicht überfordern.</summary>
    public const int MaxEntries = 1_000_000;

    /// <summary>Ordner und Dateien, die niemand sucht (Papierkörbe, Vorschau-Ordner von NAS-Systemen, halbe Uploads).</summary>
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
        { "@eaDir", "#recycle", "#snapshot", ".Trash", ".Trashes", "$RECYCLE.BIN", "System Volume Information", ".git", "node_modules", "lost+found", ".DS_Store", "Thumbs.db", "desktop.ini" };

    private static bool Skip(string name) => SkipNames.Contains(name) || name.StartsWith(".Trash-", StringComparison.Ordinal)
        || name.EndsWith(".dmoffice-part", StringComparison.Ordinal) || name.StartsWith("._", StringComparison.Ordinal);

    private static readonly EnumerationOptions ListOptions = new() { RecurseSubdirectories = false, IgnoreInaccessible = true, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };

    private const string DoneStatus = "('ok','empty','skipped','error')";

    /// <summary>Bedingung: Inhalt dieser Datei soll ausgewertet werden. Nur Kennungen aus der festen Liste kommen in den Text.</summary>
    private static (string Sql, (string, object?)[] Args) Wanted(ExternalFolderDef f)
    {
        if (f.Level != ExternalFolderDef.LevelContents) return ("0", []);
        var groups = string.Join(',', f.Groups.Where(ExternalFolderDef.AllGroups.Contains).Select(g => "'" + g + "'"));
        if (groups.Length == 0) return ("0", []);
        var args = new List<(string, object?)> { ("$max", f.MaxBytes) };
        var sql = $"grp IN ({groups}) AND size <= $max";
        if (f.ContentFolders.Count > 0)
        {
            sql += " AND top IN (" + string.Join(',', f.ContentFolders.Select((_, i) => "$t" + i)) + ")";
            args.AddRange(f.ContentFolders.Select((t, i) => ("$t" + i, (object?)t)));
        }
        return (sql, args.ToArray());
    }

    // ---------------- Abgleich mit der Konfiguration ----------------

    /// <summary>Mounts ohne Suche, entfernte oder mit geändertem Pfad oder geänderter Auswahl: Index weg. Auswahl der Inhalte geändert:
    /// nicht mehr Gewolltes vergessen. Gibt die Mounts zurück, in denen gesucht werden soll.</summary>
    public List<ExternalFolderDef> Sync(IEnumerable<ExternalFolderDef> folders)
    {
        var wanted = folders.Where(f => f.Level != ExternalFolderDef.LevelNone).ToList();
        using var conn = Open();
        var known = UserDb.Query(conn, "SELECT mount, root, include, content_key FROM ext_mounts",
            r => (Mount: r.GetString(0), Root: r.GetString(1), Include: r.GetString(2), Key: r.GetString(3)));
        foreach (var k in known)
        {
            var f = wanted.FirstOrDefault(w => w.Id == k.Mount);
            if (f == null || f.Path != k.Root || IncludeKey(f) != k.Include) { Drop(conn, k.Mount); _states.TryRemove(k.Mount, out _); }
            else if (ContentKey(f) != k.Key)
            {
                ForgetUnwanted(conn, f);
                UserDb.Exec(conn, "UPDATE ext_mounts SET content_key = $k WHERE mount = $m", ("$k", ContentKey(f)), ("$m", f.Id));
                _states.TryRemove(f.Id, out _);
            }
        }
        foreach (var f in wanted)
            UserDb.Exec(conn, "INSERT OR IGNORE INTO ext_mounts(mount, root, include, content_key) VALUES ($m, $r, $i, $k)",
                ("$m", f.Id), ("$r", f.Path), ("$i", IncludeKey(f)), ("$k", ContentKey(f)));
        return wanted;
    }

    private static string IncludeKey(ExternalFolderDef f) => string.Join('\n', f.Include.OrderBy(x => x, StringComparer.Ordinal));
    private static string ContentKey(ExternalFolderDef f) =>
        f.Level + "|" + string.Join(',', f.Groups.OrderBy(x => x, StringComparer.Ordinal)) + "|" + string.Join('\n', f.ContentFolders.OrderBy(x => x, StringComparer.Ordinal)) + "|" + f.MaxBytes;

    private static void Drop(SqliteConnection conn, string mount)
    {
        // In Stücken: Tausende Zeilen samt Volltext sollen die Datenbank nicht lange sperren
        while (UserDb.Exec(conn, "DELETE FROM ext_files WHERE id IN (SELECT id FROM ext_files WHERE mount = $m LIMIT $l)", ("$m", mount), ("$l", Chunk)) > 0) { }
        UserDb.Exec(conn, "DELETE FROM ext_mounts WHERE mount = $m", ("$m", mount));
    }

    /// <summary>Texte, die nach der neuen Auswahl nicht mehr gewollt sind, aus dem Index nehmen; Ausgelassenes neu vormerken.</summary>
    private static void ForgetUnwanted(SqliteConnection conn, ExternalFolderDef f)
    {
        var (sql, args) = Wanted(f);
        var withMount = args.Append(("$m", (object?)f.Id)).ToArray();
        var all = withMount.Append(("$l", (object?)Chunk)).ToArray();
        while (true)
        {
            var rows = UserDb.Query(conn, $"SELECT id, name FROM ext_files WHERE mount = $m AND status IN {DoneStatus} AND NOT ({sql}) LIMIT $l",
                r => (Id: r.GetInt64(0), Name: r.GetString(1)), all);
            if (rows.Count == 0) break;
            using var tx = conn.BeginTransaction();
            foreach (var row in rows)
            {
                UserDb.Exec(conn, "UPDATE ext_files SET status = 'pending', method = 'none', note = NULL, want_ocr = 0, tries = 0 WHERE id = $id", ("$id", row.Id));
                UserDb.Exec(conn, "DELETE FROM ext_fts WHERE rowid = $id", ("$id", row.Id));
                UserDb.Exec(conn, "INSERT INTO ext_fts(rowid, name, body) VALUES ($id, $n, '')", ("$id", row.Id), ("$n", row.Name));
            }
            tx.Commit();
        }
        // Größe erlaubt jetzt, Texterkennung jetzt an: noch einmal
        UserDb.Exec(conn, $"UPDATE ext_files SET status = 'pending', tries = 0 WHERE mount = $m AND status = 'skipped' AND note LIKE 'größer als%' AND {sql}", withMount);
        if (f.Groups.Contains(ExternalFolderDef.GroupImages))
            UserDb.Exec(conn, $"UPDATE ext_files SET status = 'pending', tries = 0 WHERE mount = $m AND want_ocr = 1 AND {sql}", withMount);
    }

    /// <summary>„Alles neu auswerten“: vormerken, das Auswerten erledigt es in kleinen Schritten.</summary>
    public void RequestReset(string mount)
    {
        using var conn = Open();
        UserDb.Exec(conn, "UPDATE ext_mounts SET reset = 1 WHERE mount = $m", ("$m", mount));
    }

    /// <summary>Vorgemerktes neu auswerten: alle gewollten Dateien wieder offen, die alten Texte bleiben bis dahin auffindbar.</summary>
    internal void RunReset(ExternalFolderDef f)
    {
        using var conn = Open();
        if (!UserDb.Query(conn, "SELECT 1 FROM ext_mounts WHERE mount = $m AND reset = 1", r => 1, ("$m", f.Id)).Any()) return;
        var (sql, args) = Wanted(f);
        var all = args.Append(("$m", (object?)f.Id)).Append(("$l", (object?)Chunk)).ToArray();
        while (UserDb.Exec(conn, $"UPDATE ext_files SET status = 'pending', tries = 0 WHERE id IN (SELECT id FROM ext_files WHERE mount = $m AND status IN {DoneStatus} AND {sql} LIMIT $l)", all) > 0) { }
        UserDb.Exec(conn, "UPDATE ext_mounts SET reset = 0 WHERE mount = $m", ("$m", f.Id));
        _states.TryRemove(f.Id, out _);
    }

    public DateTimeOffset? LastScan(string mount)
    {
        using var conn = Open();
        var v = UserDb.Query(conn, "SELECT last_scan FROM ext_mounts WHERE mount = $m", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$m", mount)).FirstOrDefault();
        return v is long s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
    }

    /// <summary>Wurde eine Prüfung unterbrochen (Neustart)? Dann geht sie weiter, ohne Fertiges noch einmal aufzulisten.</summary>
    public bool ScanOpen(string mount)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT scan_stamp IS NOT NULL FROM ext_mounts WHERE mount = $m", r => r.GetInt64(0) != 0, ("$m", mount)).FirstOrDefault();
    }

    // ---------------- Prüfen ----------------

    /// <summary>
    /// Den Mount durchgehen: Neues und Geändertes (Größe oder Änderungsdatum) eintragen, Verschwundenes entfernen. Nur die ausgewählten
    /// Ordner der obersten Ebene, wenn eine Auswahl besteht. Das Auflisten läuft ohne Sperre, je Ordner eine kurze Transaktion.
    /// Abgebrochen (Neustart): der nächste Aufruf geht weiter. Gibt die Zahl der Einträge zurück.
    /// </summary>
    public long Scan(ExternalFolderDef f, CancellationToken ct = default, Action<long>? progress = null)
    {
        var root = Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar);
        using var conn = Open();
        if (!Directory.Exists(root))
        {
            UserDb.Exec(conn, "UPDATE ext_mounts SET scan_note = $n WHERE mount = $m", ("$n", "Den Ordner gibt es auf dem Server gerade nicht."), ("$m", f.Id));
            return 0;
        }
        var open = UserDb.Query(conn, "SELECT scan_stamp, root_listed FROM ext_mounts WHERE mount = $m",
            r => (Stamp: r.IsDBNull(0) ? (long?)null : r.GetInt64(0), RootListed: r.GetInt64(1)), ("$m", f.Id)).First();
        var stamp = open.Stamp ?? Unix();
        if (open.Stamp == null) UserDb.Exec(conn, "UPDATE ext_mounts SET scan_stamp = $s, scan_seconds = 0, scan_note = NULL WHERE mount = $m", ("$s", stamp), ("$m", f.Id));

        var include = f.Include.Count > 0 ? new HashSet<string>(f.Include, StringComparer.Ordinal) : null;
        var pending = new Stack<string>();
        pending.Push("");
        long count = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            while (pending.Count > 0 && count < MaxEntries)
            {
                ct.ThrowIfCancellationRequested();
                var rel = pending.Pop();
                var done = rel.Length == 0 ? open.RootListed == stamp
                    : UserDb.Query(conn, "SELECT listed = $s FROM ext_files WHERE mount = $m AND path = $p", r => r.GetInt64(0) != 0, ("$s", stamp), ("$m", f.Id), ("$p", rel)).FirstOrDefault();
                if (done)
                {
                    // Schon erledigt: nur die Unterordner weiterverfolgen
                    foreach (var dir in UserDb.Query(conn, "SELECT path FROM ext_files WHERE mount = $m AND parent = $p AND is_dir = 1", r => r.GetString(0), ("$m", f.Id), ("$p", rel))) pending.Push(dir);
                    count += UserDb.Query(conn, "SELECT COUNT(*) FROM ext_files WHERE mount = $m AND parent = $p", r => r.GetInt64(0), ("$m", f.Id), ("$p", rel)).First();
                }
                else
                {
                    var listed = ApplyDir(conn, f, root, rel, include, stamp);
                    if (listed == null) continue;
                    foreach (var dir in listed.Value.Dirs) pending.Push(dir);
                    count += listed.Value.Count;
                }
                progress?.Invoke(count);
            }
            UserDb.Exec(conn, "UPDATE ext_mounts SET last_scan = $t, scan_stamp = NULL, last_seconds = scan_seconds + $e, scan_seconds = 0, scan_note = $n WHERE mount = $m",
                ("$t", Unix()), ("$e", (long)watch.Elapsed.TotalSeconds), ("$n", count >= MaxEntries ? "Mehr als eine Million Einträge – nur ein Teil ist durchsuchbar." : null), ("$m", f.Id));
            _states.TryRemove(f.Id, out _);
            return count;
        }
        catch (OperationCanceledException)
        {
            UserDb.Exec(conn, "UPDATE ext_mounts SET scan_seconds = scan_seconds + $e WHERE mount = $m", ("$e", (long)watch.Elapsed.TotalSeconds), ("$m", f.Id));
            throw;
        }
    }

    /// <summary>Einen Ordner auflisten und eintragen. Gibt Unterordner und Zahl der Einträge zurück; null, wenn er sich nicht lesen lässt.</summary>
    private (List<string> Dirs, long Count, List<string> NewDirs)? ApplyDir(SqliteConnection conn, ExternalFolderDef f, string root, string rel, HashSet<string>? include, long stamp)
    {
        var path = rel.Length == 0 ? root : Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        List<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", ListOptions).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        var known = UserDb.Query(conn, "SELECT path, size, modified, is_dir FROM ext_files WHERE mount = $m AND parent = $p",
            r => (Path: r.GetString(0), Size: r.GetInt64(1), Modified: r.GetInt64(2), Dir: r.GetInt64(3) != 0), ("$m", f.Id), ("$p", rel))
            .ToDictionary(x => x.Path, StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var dirs = new List<string>();
        var newDirs = new List<string>();
        long count = 0;
        using var tx = conn.BeginTransaction();
        foreach (var e in entries)
        {
            if (Skip(e.Name)) continue;
            var isDir = e is DirectoryInfo;
            // Oben nur die ausgewählten Ordner
            if (rel.Length == 0 && include != null && !(isDir && include.Contains(e.Name))) continue;
            var child = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
            present.Add(child);
            count++;
            long size, modified;
            try
            {
                size = isDir ? 0 : ((FileInfo)e).Length;
                modified = new DateTimeOffset(e.LastWriteTimeUtc).ToUnixTimeSeconds();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (isDir) dirs.Add(child);
            var isKnown = known.TryGetValue(child, out var k);
            if (isKnown && k.Dir == isDir)
            {
                if (isDir && k.Modified != modified) UserDb.Exec(conn, "UPDATE ext_files SET modified = $mod WHERE mount = $m AND path = $p", ("$mod", modified), ("$m", f.Id), ("$p", child));
                if (isDir || (k.Size == size && k.Modified == modified)) continue;
            }
            if (isKnown) DeleteTree(conn, f.Id, child);
            if (isDir) newDirs.Add(child);
            var grp = isDir ? "" : TextExtract.GroupOf(e.Name);
            var id = UserDb.Query(conn, @"INSERT INTO ext_files(mount, path, parent, top, name, is_dir, grp, size, modified, status)
                    VALUES ($m, $p, $par, $t, $n, $d, $g, $sz, $mod, $st) RETURNING id", r => r.GetInt64(0),
                ("$m", f.Id), ("$p", child), ("$par", rel), ("$t", rel.Length == 0 ? (isDir ? e.Name : "") : rel.Split('/')[0]), ("$n", e.Name), ("$d", isDir ? 1 : 0), ("$g", grp),
                ("$sz", size), ("$mod", modified), ("$st", isDir ? "dir" : grp.Length == 0 ? "named" : "pending")).First();
            // Der Name ist sofort auffindbar, der Inhalt kommt beim Auswerten dazu
            UserDb.Exec(conn, "INSERT INTO ext_fts(rowid, name, body) VALUES ($id, $n, '')", ("$id", id), ("$n", e.Name));
        }
        // Was nicht mehr da ist, mit allem darunter
        foreach (var gone in known.Keys.Where(p => !present.Contains(p))) DeleteTree(conn, f.Id, gone);
        if (rel.Length == 0) UserDb.Exec(conn, "UPDATE ext_mounts SET root_listed = $s WHERE mount = $m", ("$s", stamp), ("$m", f.Id));
        else UserDb.Exec(conn, "UPDATE ext_files SET listed = $s WHERE mount = $m AND path = $p", ("$s", stamp), ("$m", f.Id), ("$p", rel));
        tx.Commit();
        return (dirs, count, newDirs);
    }

    private static void DeleteTree(SqliteConnection conn, string mount, string path) =>
        UserDb.Exec(conn, "DELETE FROM ext_files WHERE mount = $m AND (path = $p OR (path >= $from AND path < $to))",
            ("$m", mount), ("$p", path), ("$from", path + "/"), ("$to", path + "0"));

    /// <summary>Ein Ordner hat sich über dmOffice geändert oder wurde geöffnet: gleich nachziehen, neue Unterordner mit allem darin.</summary>
    public void RefreshDir(ExternalFolderDef f, string rel, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar);
        var include = f.Include.Count > 0 ? new HashSet<string>(f.Include, StringComparer.Ordinal) : null;
        if (rel.Length > 0 && include != null && !include.Contains(rel.Split('/')[0])) return;
        using var conn = Open();
        var todo = new Stack<string>();
        todo.Push(rel);
        while (todo.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = todo.Pop();
            var full = dir.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar)));
            if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (!Directory.Exists(full)) continue;
            var listed = ApplyDir(conn, f, root, dir, include, 0);
            if (listed != null) foreach (var d in listed.Value.NewDirs) todo.Push(d);
        }
        _states.TryRemove(f.Id, out _);
    }

    // ---------------- Auswerten ----------------

    public sealed record Pending(long Id, string Mount, string Path, string Name, long Size, int Tries);

    /// <summary>Nächste gewollte Dateien: erst Textdateien, dann Dokumente, zuletzt Bilder und Scans (das Aufwendigste).</summary>
    public List<Pending> NextPending(ExternalFolderDef f, int limit)
    {
        var (sql, args) = Wanted(f);
        if (sql == "0") return [];
        using var conn = Open();
        var list = new List<Pending>();
        foreach (var g in ExternalFolderDef.AllGroups)
        {
            if (list.Count >= limit) break;
            list.AddRange(UserDb.Query(conn, $"SELECT id, mount, path, name, size, tries FROM ext_files WHERE mount = $m AND status = 'pending' AND grp = $g AND {sql} LIMIT $l",
                r => new Pending(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt32(5)),
                args.Append(("$m", (object?)f.Id)).Append(("$g", (object?)g)).Append(("$l", (object?)(limit - list.Count))).ToArray()));
        }
        return list;
    }

    /// <summary>Eine Datei lesen und ihren Text eintragen. Den Versuch vorher zählen: bringt eine Datei den Prozess zum Absturz,
    /// kommt sie nach drei Versuchen nicht wieder.</summary>
    public void Process(ExternalFolderDef f, Pending p, ExtractLimits? limits = null, CancellationToken ct = default)
    {
        using (var conn = Open()) UserDb.Exec(conn, "UPDATE ext_files SET tries = tries + 1 WHERE id = $id", ("$id", p.Id));
        limits ??= new ExtractLimits { MaxBytes = f.MaxBytes };
        var watch = Stopwatch.StartNew();
        Extracted result;
        if (p.Tries >= 3) result = new Extracted("", "none", "error", "Abgebrochen nach mehreren Versuchen.");
        else if (!TextExtract.Supported(p.Name, null)) result = Extracted.Skip("Dateityp ohne Text");
        else if (p.Size > limits.MaxBytes) result = Extracted.Skip($"größer als {limits.MaxBytes / 1024 / 1024} MB");
        else
        {
            var root = Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, p.Path.Replace('/', Path.DirectorySeparatorChar)));
            try
            {
                // Nie außerhalb des Mounts lesen (auch nicht über einen Verweis, der inzwischen dorthin zeigt)
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || new FileInfo(full).LinkTarget != null)
                    result = Extracted.Skip("Verknüpfung");
                else result = TextExtract.Extract(File.ReadAllBytes(full), p.Name, null, f.Groups.Contains(ExternalFolderDef.GroupImages) && Ocr.Available, limits, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (FileNotFoundException) { using var c = Open(); UserDb.Exec(c, "DELETE FROM ext_files WHERE id = $id", ("$id", p.Id)); return; }
            catch (Exception e) { result = new Extracted("", "none", "error", e.Message); }
        }
        using var conn2 = Open();
        using var tx = conn2.BeginTransaction();
        // Inzwischen geändert (neu vorgemerkt) oder weg: nichts schreiben
        if (!UserDb.Query(conn2, "SELECT 1 FROM ext_files WHERE id = $id AND status = 'pending'", r => 1, ("$id", p.Id)).Any()) return;
        UserDb.Exec(conn2, "UPDATE ext_files SET status = $s, method = $me, note = $n, want_ocr = $w WHERE id = $id",
            ("$s", result.Status), ("$me", result.Method), ("$n", result.Note), ("$w", result.WantsOcr ? 1 : 0), ("$id", p.Id));
        UserDb.Exec(conn2, "DELETE FROM ext_fts WHERE rowid = $id", ("$id", p.Id));
        UserDb.Exec(conn2, "INSERT INTO ext_fts(rowid, name, body) VALUES ($id, $n, $b)", ("$id", p.Id), ("$n", p.Name), ("$b", result.Text));
        UserDb.Exec(conn2, "UPDATE ext_mounts SET read_bytes = read_bytes + $b, read_seconds = read_seconds + $t WHERE mount = $m", ("$b", p.Size), ("$t", watch.Elapsed.TotalSeconds), ("$m", f.Id));
        tx.Commit();
    }

    // ---------------- Suche ----------------

    public sealed record Hit(string Mount, string Path, string Name, bool IsDir, long Size, DateTime Modified, string? Snippet);

    private static readonly string SnippetSql = $"snippet(ext_fts, 1, '{TextIndex.MarkStart}', '{TextIndex.MarkEnd}', '…', 14)";

    /// <summary>Namen und Inhalte in diesen Mounts (alle Wörter, Präfixsuche), auf Wunsch nur unterhalb eines Pfads.
    /// Snippet nur, wenn der Inhalt passt.</summary>
    public List<Hit> Search(IEnumerable<string> mounts, string query, string? under = null, int limit = 100)
    {
        var list = mounts.ToList();
        var match = TextIndex.FtsQuery(query);
        if (list.Count == 0 || match.Length == 0) return [];
        under = (under ?? "").Trim('/');
        using var conn = Open();
        var ids = string.Join(',', list.Select((_, i) => "$m" + i));
        return UserDb.Query(conn, $@"SELECT e.mount, e.path, e.name, e.is_dir, e.size, e.modified, {SnippetSql}
                FROM ext_fts JOIN ext_files e ON e.id = ext_fts.rowid
                WHERE ext_fts MATCH $q AND e.mount IN ({ids}) AND ($u = '' OR substr(e.path, 1, length($u) + 1) = $u || '/')
                ORDER BY bm25(ext_fts, 4.0, 1.0) LIMIT $l",
            r =>
            {
                var snippet = r.GetString(6);
                return new Hit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.GetInt64(4),
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)).UtcDateTime, snippet.Contains(TextIndex.MarkStart) ? snippet : null);
            },
            list.Select((m, i) => ("$m" + i, (object?)m)).Append(("$q", match)).Append(("$u", under)).Append(("$l", limit)).ToArray());
    }

    // ---------------- Stand ----------------

    /// <summary>Dateien je Gruppe und oberstem Ordner: Anzahl, Größe, ausgewertet - der Dialog rechnet daraus seine Auswahl.</summary>
    public sealed record Part(string Group, string Top, long Count, long Bytes, long Done, long OpenBytes);

    public sealed record State(string Mount, long Files, long Dirs, long Wanted, long Done, long Errors, DateTimeOffset? LastScan, long LastSeconds,
        double? BytesPerSecond, string? Note, List<Part> Parts);

    /// <summary>Stand eines Mounts. maxAge: so alt darf die Zählung sein - sie geht über alle Dateien.</summary>
    public State StateOf(ExternalFolderDef f, TimeSpan? maxAge = null)
    {
        if (maxAge is { } age && _states.TryGetValue(f.Id, out var cached) && DateTime.UtcNow - cached.At < age) return cached.State;
        using var conn = Open();
        var parts = UserDb.Query(conn, $@"SELECT grp, top, COUNT(*), SUM(size), SUM(status IN {DoneStatus}), SUM(CASE WHEN status = 'pending' THEN size ELSE 0 END)
                FROM ext_files WHERE mount = $m AND is_dir = 0 GROUP BY grp, top",
            r => new Part(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)), ("$m", f.Id));
        var dirs = UserDb.Query(conn, "SELECT COUNT(*) FROM ext_files WHERE mount = $m AND is_dir = 1", r => r.GetInt64(0), ("$m", f.Id)).First();
        var (sql, args) = Wanted(f);
        var counts = UserDb.Query(conn, $"SELECT COUNT(*), COALESCE(SUM(status IN {DoneStatus}), 0), COALESCE(SUM(status = 'error'), 0) FROM ext_files WHERE mount = $m AND is_dir = 0 AND {sql}",
            r => (Wanted: r.GetInt64(0), Done: r.GetInt64(1), Errors: r.GetInt64(2)), args.Append(("$m", (object?)f.Id)).ToArray()).First();
        var m = UserDb.Query(conn, "SELECT last_scan, last_seconds, read_bytes, read_seconds, scan_note FROM ext_mounts WHERE mount = $m",
            r => (Last: r.IsDBNull(0) ? (long?)null : r.GetInt64(0), Seconds: r.GetInt64(1), Bytes: r.GetInt64(2), Read: r.GetDouble(3), Note: r.IsDBNull(4) ? null : r.GetString(4)), ("$m", f.Id)).FirstOrDefault();
        var state = new State(f.Id, parts.Sum(p => p.Count), dirs, counts.Wanted, counts.Done, counts.Errors,
            m.Last is long l ? DateTimeOffset.FromUnixTimeSeconds(l) : null, m.Seconds, m.Read > 1 && m.Bytes > 0 ? m.Bytes / m.Read : null, m.Note, parts);
        _states[f.Id] = (DateTime.UtcNow, state);
        return state;
    }
}

/// <summary>
/// Hält den Index der Server-Mounts aktuell, in drei unabhängigen Abläufen: die Prüfung nach Zeitplan (<see cref="Due"/>), das Auswerten
/// (eine Datei nach der anderen, mit kurzen Pausen, auch während einer Prüfung) und das sofortige Nachziehen einzelner Ordner.
/// </summary>
public sealed class ExternalIndexer(ExternalIndex index, ServerConfig config, ILogger<ExternalIndexer> log) : BackgroundService
{
    /// <summary>Was gerade läuft, für die Aktivität.</summary>
    public sealed record Progress(string Mount, long Done, long? Total);

    private readonly SemaphoreSlim _wakeScan = new(0, 1), _wakeRead = new(0, 1);
    private readonly ConcurrentDictionary<string, byte> _forced = new();
    private readonly ConcurrentDictionary<string, DateTime> _opened = new();
    private readonly Channel<(string Mount, string Path)> _refresh = Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public Progress? Checking { get; private set; }
    public Progress? Reading { get; private set; }

    /// <summary>Wann zuletzt einer dieser Server-Mounts geprüft wurde - für die Aktivität.</summary>
    public DateTime? LastScan(IEnumerable<ExternalFolderDef> folders) =>
        folders.Select(f => index.LastScan(f.Id)).Where(x => x != null).DefaultIfEmpty().Max()?.UtcDateTime;

    private static void Wake(SemaphoreSlim s)
    {
        try { s.Release(); } catch (SemaphoreFullException) { /* schon geweckt */ }
    }

    /// <summary>Einstellungen geändert: gleich abgleichen, statt erst beim nächsten Durchlauf.</summary>
    public void Kick() { Wake(_wakeScan); Wake(_wakeRead); }

    /// <summary>„Jetzt prüfen“.</summary>
    public void RequestScan(string mount) { _forced[mount] = 0; Wake(_wakeScan); }

    /// <summary>Etwas hat sich über dmOffice geändert: den Ordner gleich nachziehen.</summary>
    public void Changed(string mount, string dir) => _refresh.Writer.TryWrite((mount, dir));

    /// <summary>Ein Ordner wurde geöffnet: nachziehen, derselbe Ordner höchstens einmal je Minute.</summary>
    public void Opened(string mount, string dir)
    {
        var key = mount + "|" + dir;
        if (_opened.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
        if (_opened.Count > 5000) _opened.Clear();
        _opened[key] = DateTime.UtcNow;
        _refresh.Writer.TryWrite((mount, dir));
    }

    /// <summary>Ist eine Prüfung fällig? Abstand laut Einstellung; „täglich“ um 03:00 und „wöchentlich“ sonntags um 03:00 Ortszeit.</summary>
    internal static bool Due(ExternalFolderDef f, DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is not { } l) return true;
        switch (f.SearchCheck)
        {
            case ExternalFolderDef.CheckManual: return false;
            case ExternalFolderDef.CheckHourly: return now - l >= TimeSpan.FromHours(1);
            case ExternalFolderDef.CheckSixHours: return now - l >= TimeSpan.FromHours(6);
            case ExternalFolderDef.CheckWeekly:
                var slot = new DateTimeOffset(now.Date.AddDays(-(int)now.DayOfWeek).AddHours(3), now.Offset);
                return l < (now >= slot ? slot : slot.AddDays(-7));
            default:
                var today = new DateTimeOffset(now.Date.AddHours(3), now.Offset);
                return l < (now >= today ? today : today.AddDays(-1));
        }
    }

    /// <summary>Nur nachts auswerten: zwischen 22 und 6 Uhr Ortszeit.</summary>
    internal static bool ReadAllowed(ExternalFolderDef f, DateTimeOffset now) => f.SearchRead != ExternalFolderDef.ReadNight || now.Hour >= 22 || now.Hour < 6;

    /// <summary>Ein Durchgang (für die Tests): prüfen, was fällig ist, dann bis zu <paramref name="max"/> Dateien auswerten.</summary>
    public static async Task<int> RunOnceAsync(ExternalIndex index, IEnumerable<ExternalFolderDef> folders, int max, CancellationToken ct,
        TimeSpan? pause = null, bool forceScan = false)
    {
        var wanted = index.Sync(folders);
        foreach (var f in wanted)
            if (forceScan || index.ScanOpen(f.Id) || Due(f, index.LastScan(f.Id), DateTimeOffset.Now))
                await Task.Run(() => index.Scan(f, ct), ct);
        int done = 0;
        foreach (var f in wanted)
        {
            index.RunReset(f);
            while (done < max && !ct.IsCancellationRequested)
            {
                var next = index.NextPending(f, Math.Min(20, max - done));
                if (next.Count == 0) break;
                foreach (var p in next)
                {
                    index.Process(f, p, ct: ct);
                    done++;
                    if (pause is { } ps && ps > TimeSpan.Zero) await Task.Delay(ps, ct);
                }
            }
        }
        return done;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stop); } catch (OperationCanceledException) { return; }
        await Task.WhenAll(Loop(CheckAsync, _wakeScan, TimeSpan.FromMinutes(1), stop), Loop(ReadAsync, _wakeRead, TimeSpan.FromMinutes(1), stop), RefreshAsync(stop));
    }

    private async Task Loop(Func<CancellationToken, Task<bool>> work, SemaphoreSlim wake, TimeSpan idle, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var again = false;
            try { again = await Task.Run(() => work(stop), stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Suche in Server-Mounts: {Error}", e.Message); }
            if (again) continue;
            try { await wake.WaitAsync(idle, stop); } catch (OperationCanceledException) { return; }
        }
    }

    private Task<bool> CheckAsync(CancellationToken stop)
    {
        var wanted = index.Sync(config.Current.ExternalFolders);
        foreach (var f in wanted)
        {
            var forced = _forced.TryRemove(f.Id, out _);
            if (!forced && !index.ScanOpen(f.Id) && !Due(f, index.LastScan(f.Id), DateTimeOffset.Now)) continue;
            Checking = new Progress(f.Name, 0, null);
            try
            {
                index.Scan(f, stop, n => Checking = new Progress(f.Name, n, null));
                log.LogInformation("Suche in Server-Mounts: „{Mount}“ geprüft", f.Name);
            }
            finally { Checking = null; }
            Wake(_wakeRead);
        }
        return Task.FromResult(false);
    }

    private async Task<bool> ReadAsync(CancellationToken stop)
    {
        var any = false;
        foreach (var f in config.Current.ExternalFolders.Where(f => f.Level == ExternalFolderDef.LevelContents))
        {
            index.RunReset(f);
            if (!ReadAllowed(f, DateTimeOffset.Now)) continue;
            var next = index.NextPending(f, 20);
            if (next.Count == 0) continue;
            any = true;
            var state = index.StateOf(f, TimeSpan.FromMinutes(1));
            Reading = new Progress(f.Name, state.Done, state.Wanted);
            foreach (var p in next)
            {
                index.Process(f, p, ct: stop);
                await Task.Delay(TimeSpan.FromMilliseconds(50), stop);
            }
        }
        if (!any) Reading = null;
        return any;
    }

    private async Task RefreshAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var (mount, dir) in _refresh.Reader.ReadAllAsync(stop))
            {
                var f = config.Current.ExternalFolders.FirstOrDefault(x => x.Id == mount && x.Level != ExternalFolderDef.LevelNone);
                if (f == null || index.LastScan(f.Id) == null) continue;
                try
                {
                    await Task.Run(() => index.RefreshDir(f, dir, stop), stop);
                    Wake(_wakeRead);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Suche in Server-Mounts, Ordner nachziehen: {Error}", e.Message); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
