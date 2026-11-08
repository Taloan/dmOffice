using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DmOffice.Core;

/// <summary>
/// Ein Core-Stand zum Lesen in einem eigenen Arbeitsordner: entschlüsselt und ausgepackt, die Datenbanken bleiben wie im Betrieb
/// verschlüsselt. Für das Werkzeug und die Prüfung vor dem Wiederherstellen. Nichts davon berührt den laufenden Server.
/// </summary>
public sealed class StandReader : IDisposable
{
    private readonly string _work;
    public DataPaths Paths { get; }
    public Vault Vault { get; }
    public S3Backup.Manifest Manifest { get; }

    private StandReader(string work, DataPaths paths, Vault vault, S3Backup.Manifest manifest) { _work = work; Paths = paths; Vault = vault; Manifest = manifest; }

    /// <summary>Stand öffnen: Datei „&lt;Zeit&gt;_&lt;Blob&gt;.dmb“, der Name gehört zur Entschlüsselung.</summary>
    public static StandReader Open(string file, string? masterKey)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        if (S3Backup.ParseName(name) == null) throw new ArgumentException("Der Dateiname gehört zu keinem Core-Stand (<Zeit>_<Kennung>.dmb). Der Name darf nicht geändert sein.");
        var work = Path.Combine(Path.GetTempPath(), "dmoffice-stand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var paths = new DataPaths(work);
            // Nur Tests: Schlüssel aus einer Datei statt aus der Umgebung
            if (RestoreTool.KeyFileForTests is { } keyFile) File.Copy(keyFile, Path.Combine(work, "master.key"));
            var vault = new Vault(paths, masterKey);
            var store = new BlobStore(Path.Combine(work, "in"), S3Backup.KeyOf(vault.Key));
            var id = name[(name.IndexOf('_') + 1)..];
            var raw = store.RawTemp();
            File.Copy(file, raw);
            try { store.Adopt(id, raw); }
            catch (Exception e) when (e is CryptographicException or InvalidDataException) { throw new InvalidOperationException("Der Stand lässt sich mit diesem Hauptschlüssel nicht öffnen.", e); }
            using (var s = store.OpenRead(id)) TarFile.ExtractToDirectory(s, work, overwriteFiles: true);
            store.DeleteAll();
            var manifest = JsonSerializer.Deserialize<S3Backup.Manifest>(File.ReadAllText(Path.Combine(work, "backup.json"))) ?? throw new InvalidDataException("Der Stand hat keine Inhaltsangabe.");
            return new StandReader(work, paths, vault, manifest);
        }
        catch { TryDelete(work); throw; }
    }

    /// <summary>Stände vor 2.0 tragen keine Version - das Werkzeug liest sie nicht.</summary>
    public bool SupportedByTool => ToolReads(Manifest.Release);

    internal static bool ToolReads(string? release) =>
        release != null && (release.Length == 0 || !int.TryParse(release.Split('.')[0], out var major) || major >= 2);

    public ServerConfig Config() => new(Paths, Vault);
    public UserStore Users() => new(Paths, Vault);

    /// <summary>Datenbank eines Benutzers auf der Kopie im Arbeitsordner; Inhalte legt sie nur dort ab.</summary>
    public UserDb Db(UserStore users, UserRecord user) => new(Paths.UserDb(user.Id), users.DbKey(user), user.Id, Paths.UserBlobs(user.Id));

    /// <summary>Wo der Stand seine Inhalte findet, in dieser Reihenfolge: Files-Backup, dann Storage Extender (Ziel und Präfix aus seinen Einstellungen).</summary>
    public static List<(IBlobRemote Remote, string? Prefix)> SourcesOf(ServerConfig config)
    {
        var s = config.Current;
        var list = new List<(IBlobRemote, string?)>();
        if (s.FilesBackup && FilesBackup.Configured(config)) list.Add((FilesBackup.RemoteOf(config), FilesBackup.PrefixOf(s)));
        if (S3Mirror.Configured(s, config.S3SecretKey())) list.Add((S3Mirror.RemoteOf(config), s.S3Prefix));
        return list;
    }

    public void Dispose() => TryDelete(_work);

    private static void TryDelete(string dir)
    {
        // Datenbankverbindungen liegen im Pool und halten unter Windows die Dateien
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Prüfung vor dem Wiederherstellen: Inhalte des Stands, die am Ziel seiner Einstellungen fehlen.</summary>
public static class RestoreCheck
{
    public static async Task<(long Total, long Missing)> CountAsync(StandReader reader, CancellationToken ct)
    {
        var config = reader.Config();
        var users = reader.Users();
        long total = 0, missing = 0;
        var sources = StandReader.SourcesOf(config);
        try
        {
            foreach (var user in users.All())
            {
                if (!File.Exists(reader.Paths.UserDb(user.Id))) continue;
                var wanted = S3Mirror.Referenced(reader.Db(users, user));
                total += wanted.Count;
                var there = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (remote, sourcePrefix) in sources)
                {
                    var prefix = S3Mirror.Prefix(sourcePrefix) + user.Id + "/";
                    foreach (var key in await remote.ListAsync(prefix, ct)) if (key.StartsWith(prefix, StringComparison.Ordinal)) there.Add(key[prefix.Length..]);
                }
                missing += wanted.Count(id => !there.Contains(id[..2] + "/" + id));
            }
        }
        finally { foreach (var (remote, _) in sources) remote.Dispose(); }
        return (total, missing);
    }
}

/// <summary>
/// Kommandozeilenwerkzeug: einzelne Dateien aus einem Core-Stand und dem Files-Backup herausholen, ohne einen Server zu starten.
/// Es liest am Ziel nur, startet keine Hintergrunddienste und baut außer zum Ziel keine Verbindung auf. Der Tresor bleibt außen vor.
/// Der Hauptschlüssel kommt aus DMOFFICE_MASTER_KEY, nicht aus den Argumenten. Anleitung: docs/wiederherstellen.de.md
/// </summary>
public static class RestoreTool
{
    public const string Command = "restore-tool";
    internal static string? KeyFileForTests;

    private const string Usage = """
        restore-tool users  --stand <datei.dmb>
        restore-tool ls     --stand <datei.dmb> --user <login> [--path <ordner>] [--trash] [--versions <datei>]
        restore-tool find   --stand <datei.dmb> --user <login> <text>
        restore-tool get    --stand <datei.dmb> --user <login> --out <ordner> [--trash] [--version <nr>] [--files <ordner>] [--prefix <präfix>] <pfad>

        DMOFFICE_MASTER_KEY muss gesetzt sein. Ohne --files gilt das Ziel aus den Einstellungen des Stands.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var (cmd, opts, rest) = Parse(args);
        if (cmd is not ("users" or "ls" or "find" or "get") || !opts.TryGetValue("stand", out var stand)) { await e.WriteLineAsync(Usage); return 2; }
        var key = Environment.GetEnvironmentVariable("DMOFFICE_MASTER_KEY");
        if (string.IsNullOrWhiteSpace(key) && KeyFileForTests == null) { await e.WriteLineAsync("DMOFFICE_MASTER_KEY ist nicht gesetzt."); return 2; }
        if (!File.Exists(stand)) { await e.WriteLineAsync($"Den Stand {stand} gibt es nicht."); return 2; }
        try
        {
            using var reader = StandReader.Open(stand, key);
            if (!reader.SupportedByTool) { await e.WriteLineAsync("Das Werkzeug liest Stände ab Version 2.0. Dieser Stand ist älter."); return 2; }
            var users = reader.Users();
            if (cmd == "users")
            {
                foreach (var u in users.All()) await o.WriteLineAsync(u.Login);
                return 0;
            }
            if (!opts.TryGetValue("user", out var login)) { await e.WriteLineAsync(Usage); return 2; }
            if (users.All().FirstOrDefault(u => string.Equals(u.Login, login, StringComparison.OrdinalIgnoreCase)) is not { } user)
            { await e.WriteLineAsync($"Den Benutzer {login} gibt es in diesem Stand nicht."); return 2; }
            var db = reader.Db(users, user);
            switch (cmd)
            {
                case "ls": await Ls(db, opts, o, e); return 0;
                case "find":
                    if (rest.Count == 0) { await e.WriteLineAsync(Usage); return 2; }
                    await Find(db, rest[0], o);
                    return 0;
                default:
                    if (rest.Count == 0 || !opts.TryGetValue("out", out var outDir)) { await e.WriteLineAsync(Usage); return 2; }
                    return await Get(reader, db, user, rest[0], outDir, opts, o, e, ct);
            }
        }
        catch (Exception x) when (x is InvalidOperationException or InvalidDataException or ArgumentException or IOException or SqliteException or UnauthorizedAccessException)
        {
            await e.WriteLineAsync(x.Message);
            return 1;
        }
    }

    private static (string Cmd, Dictionary<string, string> Opts, List<string> Others) Parse(string[] args)
    {
        var opts = new Dictionary<string, string>();
        var rest = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { rest.Add(args[i]); continue; }
            var name = args[i][2..];
            if (name == "trash") opts[name] = "1";
            else if (i + 1 < args.Length) opts[name] = args[++i];
        }
        return (args.Length > 0 ? args[0] : "", opts, rest);
    }

    private static string Line(bool dir, long size, string when, string path) =>
        $"{(dir ? "<Ordner>" : Size(size)),10}  {when,-19}  {path}";

    private static string Size(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.0} KB" : b < 1024L * 1024 * 1024 ? $"{b / 1024.0 / 1024:0.0} MB" : $"{b / 1024.0 / 1024 / 1024:0.0} GB";

    private static async Task Ls(UserDb db, Dictionary<string, string> opts, TextWriter o, TextWriter e)
    {
        if (opts.TryGetValue("versions", out var file))
        {
            var rows = db.Query("SELECT v.id, v.size, v.modified FROM file_versions v JOIN files f ON f.fileid = v.fileid WHERE f.path = $p ORDER BY v.id DESC",
                r => (Id: r.GetInt64(0), Size: r.GetInt64(1), When: r.GetString(2)), ("$p", Norm(file)));
            foreach (var v in rows) await o.WriteLineAsync($"{v.Id,6}  {Size(v.Size),10}  {v.When}");
            if (rows.Count == 0) await e.WriteLineAsync("Keine früheren Versionen.");
            return;
        }
        if (opts.ContainsKey("trash"))
        {
            foreach (var t in db.Query("SELECT path, is_dir, size, deleted FROM file_trash ORDER BY deleted DESC", r => (Path: r.GetString(0), Dir: r.GetInt64(1) != 0, Size: r.GetInt64(2), When: r.GetString(3))))
                await o.WriteLineAsync(Line(t.Dir, t.Size, t.When, t.Path));
            return;
        }
        opts.TryGetValue("path", out var folder);
        foreach (var f in db.Query("SELECT path, is_dir, size, modified FROM files WHERE parent = $p ORDER BY is_dir DESC, path", r => (Path: r.GetString(0), Dir: r.GetInt64(1) != 0, Size: r.GetInt64(2), When: r.GetString(3)), ("$p", Norm(folder ?? ""))))
            await o.WriteLineAsync(Line(f.Dir, f.Size, f.When, f.Path));
    }

    private static async Task Find(UserDb db, string text, TextWriter o)
    {
        var like = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        foreach (var f in db.Query("SELECT path, is_dir, size, modified FROM files WHERE path <> '' AND path LIKE $l ESCAPE '\\' ORDER BY path", r => (Path: r.GetString(0), Dir: r.GetInt64(1) != 0, Size: r.GetInt64(2), When: r.GetString(3)), ("$l", like)))
            await o.WriteLineAsync(Line(f.Dir, f.Size, f.When, f.Path));
        foreach (var t in db.Query("SELECT path, is_dir, size, deleted FROM file_trash WHERE path LIKE $l ESCAPE '\\' ORDER BY deleted DESC", r => (Path: r.GetString(0), Dir: r.GetInt64(1) != 0, Size: r.GetInt64(2), When: r.GetString(3)), ("$l", like)))
            await o.WriteLineAsync(Line(t.Dir, t.Size, t.When, t.Path + "  (Papierkorb)"));
    }

    private sealed record Item(string Rel, string? Blob, byte[]? Data, bool Dir);

    private static async Task<int> Get(StandReader reader, UserDb db, UserRecord user, string path, string outDir, Dictionary<string, string> opts, TextWriter o, TextWriter e, CancellationToken ct)
    {
        path = Norm(path);
        var items = opts.TryGetValue("version", out var version) ? Version(db, path, version)
            : opts.ContainsKey("trash") ? Trash(db, path)
            : Current(db, path);
        if (items.Count == 0) { await e.WriteLineAsync($"{path} gibt es in diesem Stand nicht."); return 1; }

        var config = reader.Config();
        var sources = new List<(IBlobRemote Remote, string? Prefix)>();
        try
        {
            if (opts.TryGetValue("files", out var folder)) sources.Add((new Storage.DriverRemote(new Storage.FolderDriver(Path.GetFullPath(folder))), StandPrefix(config)));
            else sources = StandReader.SourcesOf(config);
            if (opts.TryGetValue("prefix", out var p) && sources.Count > 0) sources[0] = (sources[0].Remote, p);
            var root = Path.GetFullPath(outDir);
            int written = 0, missing = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var to = Path.GetFullPath(Path.Combine(root, item.Rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!to.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && to != root) { missing++; await e.WriteLineAsync($"Überspringe {item.Rel}: Pfad außerhalb des Zielordners."); continue; }
                if (item.Dir) { Directory.CreateDirectory(to); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (File.Exists(to)) { missing++; await e.WriteLineAsync($"Überspringe {item.Rel}: Datei gibt es im Zielordner schon."); continue; }
                if (item.Blob == null)
                {
                    await File.WriteAllBytesAsync(to, item.Data ?? [], ct);
                    written++;
                    continue;
                }
                IBlobRemote? remote = null;
                var key = "";
                foreach (var source in sources)
                {
                    key = S3Mirror.Prefix(source.Prefix) + user.Id + "/" + item.Blob[..2] + "/" + item.Blob;
                    if (await source.Remote.SizeAsync(key, ct) >= 0) { remote = source.Remote; break; }
                }
                if (remote == null) { missing++; await e.WriteLineAsync($"Inhalt fehlt am Ziel: {item.Rel}"); continue; }
                var raw = db.Blobs.RawTemp();
                await remote.DownloadAsync(key, raw, ct);
                try { db.Blobs.Adopt(item.Blob, raw); }
                catch (Exception x) when (x is CryptographicException or InvalidDataException) { missing++; await e.WriteLineAsync($"Inhalt beschädigt: {item.Rel}"); continue; }
                await using (var s = db.Blobs.OpenRead(item.Blob))
                await using (var f = File.Create(to)) await s.CopyToAsync(f, ct);
                db.Blobs.Delete(item.Blob);
                written++;
                await o.WriteLineAsync(item.Rel);
            }
            await o.WriteLineAsync($"{written} Dateien geschrieben, {missing} nicht.");
            return missing > 0 ? 3 : 0;
        }
        finally { foreach (var source in sources) source.Remote.Dispose(); }
    }

    private static string? StandPrefix(ServerConfig config)
    {
        var s = config.Current;
        return s.FilesBackup ? FilesBackup.PrefixOf(s) : s.S3Prefix;
    }

    private static string Norm(string path) => path.Replace('\\', '/').Trim('/');
    private static string Base(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static List<Item> Current(UserDb db, string path)
    {
        var like = path.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "/%";
        var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        return db.Query("SELECT path, is_dir, blob, data FROM files WHERE path = $p OR path LIKE $l ESCAPE '\\' ORDER BY path",
            r => new Item(parent.Length == 0 ? r.GetString(0) : r.GetString(0)[(parent.Length + 1)..], r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : (byte[])r[3], r.GetInt64(1) != 0),
            ("$p", path), ("$l", like));
    }

    private static List<Item> Trash(UserDb db, string path)
    {
        // Mehrfach gelöscht: der jüngste Eintrag
        if (db.Scalar("SELECT id FROM file_trash WHERE path = $p ORDER BY deleted DESC, id DESC LIMIT 1", ("$p", path)) is not long id) return [];
        var name = Base(path);
        return db.Query("SELECT rel, is_dir, blob, data FROM file_trash_items WHERE trash_id = $i ORDER BY rel",
            r => new Item(r.GetString(0).Length == 0 ? name : name + "/" + r.GetString(0), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : (byte[])r[3], r.GetInt64(1) != 0),
            ("$i", id));
    }

    private static List<Item> Version(UserDb db, string path, string version)
    {
        if (!long.TryParse(version, out var id)) throw new ArgumentException("--version braucht die Nummer aus „ls --versions“.");
        var name = $"{Path.GetFileNameWithoutExtension(Base(path))} (Version {id}){Path.GetExtension(Base(path))}";
        return db.Query("SELECT v.blob, v.data FROM file_versions v JOIN files f ON f.fileid = v.fileid WHERE f.path = $p AND v.id = $i",
            r => new Item(name, r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : (byte[])r[1], false), ("$p", path), ("$i", id));
    }
}
