using System.Collections.Concurrent;
using DmOffice.Core;
using DmOffice.Dav;
using Notizi.Sync;

namespace DmOffice.Notes;

/// <summary>
/// Der notizi-Abgleichordner im eigenen WebDAV - ohne Umweg ueber HTTP,
/// direkt auf den Dateien in der Benutzerdatenbank. notizi auf PC und Handy
/// schreiben per WebDAV in dieselben Dateien.
/// </summary>
public sealed class DbTransport : ISyncTransport, ISyncBrowser
{
    private readonly UserDb _db;
    private readonly string _root;

    public DbTransport(UserDb db, string root)
    {
        _db = db;
        _root = FileStore.Normalize(root);
    }

    private string At(string path) => FileStore.Normalize(_root.Length == 0 ? path : _root + "/" + path);

    public Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
    {
        var p = At(path);
        return Task.FromResult(FileStore.Get(_db, p) is { IsDir: false } ? FileStore.Data(_db, p) : null);
    }

    public Task<string> WriteAsync(string path, byte[] data, string? expectedTag = null, CancellationToken ct = default)
    {
        var p = At(path);
        try
        {
            var (etag, _) = FileStore.Put(_db, p, data, "application/octet-stream",
                ifMatch: expectedTag is { Length: > 0 } ? expectedTag : null, ifNoneMatch: expectedTag == "" ? "*" : null);
            return Task.FromResult(etag);
        }
        catch (Pim.PreconditionFailedException) { throw new SyncPreconditionException(path); }
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        FileStore.Delete(_db, At(path));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string folder, CancellationToken ct = default)
    {
        var p = At(folder);
        IReadOnlyList<RemoteFile> list = FileStore.Get(_db, p) is { IsDir: true }
            ? FileStore.Children(_db, p).Where(f => !f.IsDir && !f.Name.EndsWith(".tmp")).Select(f => new RemoteFile(f.Name, f.Etag)).ToList()
            : Array.Empty<RemoteFile>();
        return Task.FromResult(list);
    }

    public Task EnsureFolderAsync(string folder, CancellationToken ct = default)
    {
        FileStore.EnsureDir(_db, At(folder));
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<string> Folders, bool IsSyncFolder)> BrowseAsync(string folder, CancellationToken ct = default)
    {
        var p = At(folder);
        if (FileStore.Get(_db, p) is not { IsDir: true }) return Task.FromResult<(IReadOnlyList<string>, bool)>((Array.Empty<string>(), false));
        var children = FileStore.Children(_db, p);
        IReadOnlyList<string> dirs = children.Where(c => c.IsDir).Select(c => c.Name).ToList();
        return Task.FromResult((dirs, children.Any(c => !c.IsDir && c.Name == SyncStore.InfoFile)));
    }

    public void Dispose() { }
}

/// <summary>Gleicht die Notizen jedes Benutzers mit seinem notizi-Ordner ab - jede Minute und auf Anstoss.</summary>
public sealed class NotesSync : BackgroundService
{
    public sealed class State
    {
        public DateTime? LastRun { get; set; }
        public string? Report { get; set; }
        public string? Error { get; set; }
        public bool Running { get; set; }
        internal SyncStore? Store;
        internal string? StoreKey;
        /// <summary>Ein Durchgang je Benutzer - Hintergrund und Knopf nicht gleichzeitig.</summary>
        internal readonly SemaphoreSlim Gate = new(1, 1);
    }

    private static readonly ConcurrentDictionary<string, State> States = new();
    private readonly Spaces _spaces;
    private readonly ILogger<NotesSync> _log;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<string, DateTime> _due = new();

    public NotesSync(Spaces spaces, ILogger<NotesSync> log)
    {
        _spaces = spaces;
        _log = log;
    }

    public static State StateOf(UserSpace s) => States.GetOrAdd(s.User.Id, _ => new State());

    public static object Status(UserSpace s)
    {
        var st = StateOf(s);
        return new { st.LastRun, st.Report, st.Error, st.Running, pending = NotesDb.Of(s).PendingCount() };
    }

    /// <summary>Bald abgleichen - nach dem Speichern einige Sekunden gesammelt.</summary>
    public void Kick(string userId, TimeSpan? delay = null)
    {
        _due[userId] = DateTime.UtcNow + (delay ?? TimeSpan.Zero);
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stop);
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in _spaces.All())
            {
                var n = space.Settings.Notizi;
                if (!n.Enabled || string.IsNullOrEmpty(n.E2eePassword) || string.IsNullOrEmpty(n.DbFolder)) continue;
                var st = StateOf(space);
                bool due = st.LastRun == null || DateTime.UtcNow - st.LastRun > TimeSpan.FromMinutes(1)
                           || (_due.TryGetValue(space.User.Id, out var at) && at <= DateTime.UtcNow);
                if (!due || st.Running) continue;
                _due.TryRemove(space.User.Id, out _);
                await RunAsync(space, stop);
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(5), stop); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunAsync(UserSpace space, CancellationToken ct)
    {
        var st = StateOf(space);
        await st.Gate.WaitAsync(ct);
        st.Running = true;
        try
        {
            var n = space.Settings.Notizi;
            var store = await OpenAsync(space, create: true, ct);
            if (store == null) { st.Error = "Das E2EE-Passwort passt nicht zu diesem Ordner."; return; }
            var db = NotesDb.Of(space);

            // Neuer Schluessel seit dem letzten Mal: Bilder und Anhaenge auf seine Namen bringen
            var keyMark = store.Crypto.BlobName(Array.Empty<byte>());
            if (n.BlobKeyMark != keyMark)
            {
                db.RekeyBlobs(store.Crypto.BlobName);
                space.UpdateSettings(s => s.Notizi.BlobKeyMark = keyMark);
            }
            // Kennung: eine andere Datenbank im Ordner als beim letzten Mal - hier neu anfangen.
            // Offene Aenderungen nicht wegwerfen: dann nur den Abgleich vergessen, der erste
            // Durchgang fuehrt gleiche Notizen zusammen (wie beim Ortswechsel in den Einstellungen)
            var id = await store.EnsureIdAsync(n.DbId ?? Guid.NewGuid().ToString("N"), ct);
            if (id != n.DbId)
            {
                if (n.DbId != null)
                {
                    if (db.PendingCount() > 0)
                    {
                        db.ForgetSync();
                        _log.LogWarning("Notizen-Abgleich {User}: andere Datenbank im Ordner, offene Aenderungen werden zusammengefuehrt", space.User.Login);
                    }
                    else db.Clear();
                }
                space.UpdateSettings(s => s.Notizi.DbId = id);
            }

            var report = await new SyncEngine(store, db, "dmOffice").RunAsync(ct);
            st.Report = report.ToString();
            st.Error = report.Problems.Count > 0 ? string.Join("; ", report.Problems.Take(3)) : null;
        }
        catch (Exception ex)
        {
            st.Error = ex.Message;
            st.Store = null;
            _log.LogWarning("Notizen-Abgleich {User}: {Error}", space.User.Login, ex.Message);
        }
        finally
        {
            st.LastRun = DateTime.UtcNow;
            st.Running = false;
            st.Gate.Release();
        }
    }

    /// <summary>Den Abgleichordner oeffnen (Schluessel ableiten kostet eine halbe Sekunde - gemerkt).
    /// Ist er leer, wird er eingerichtet - so laesst sich auch hier anfangen.</summary>
    public static async Task<SyncStore?> OpenAsync(UserSpace space, bool create, CancellationToken ct)
    {
        var n = space.Settings.Notizi;
        var st = StateOf(space);
        var key = $"{n.Root}|{n.DbFolder}|{n.E2eePassword}";
        if (st.Store != null && st.StoreKey == key) return st.Store;

        var transport = new DbTransport(space.Db, n.Root + "/" + n.DbFolder);
        SyncStore? store;
        if (await SyncStore.IsSetUpAsync(transport, ct)) store = await Task.Run(() => SyncStore.OpenAsync(transport, n.E2eePassword!, ct), ct);
        else if (create)
        {
            store = await Task.Run(() => SyncStore.CreateAsync(transport, n.E2eePassword!, ct: ct), ct);
            await store.WriteNameAsync(n.DbFolder, ct);
        }
        else return null;
        st.Store = store;
        st.StoreKey = key;
        return store;
    }

    /// <summary>
    /// Name fuer einen Anhang oder ein Bild: mit eingerichtetem Abgleich wie im Abgleichordner (HMAC),
    /// sonst vorlaeufig der SHA-256. Wird der Abgleich spaeter eingeschaltet, benennt RekeyBlobs um.
    /// </summary>
    public static async Task<string> BlobNameAsync(UserSpace space, byte[] data, CancellationToken ct)
    {
        var n = space.Settings.Notizi;
        if (n.Enabled && !string.IsNullOrEmpty(n.E2eePassword) && !string.IsNullOrEmpty(n.DbFolder))
        {
            try { if (await OpenAsync(space, create: false, ct) is { } store) return store.Crypto.BlobName(data); }
            catch { /* dann vorlaeufig */ }
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
    }

    public static async Task<object> ScanAsync(UserSpace space, CancellationToken ct)
    {
        var n = space.Settings.Notizi;
        var browser = new DbTransport(space.Db, n.Root);
        if (FileStore.Get(space.Db, FileStore.Normalize(n.Root)) == null) return Array.Empty<object>();
        var list = await SyncCatalog.ScanAsync(browser, folder => new DbTransport(space.Db, n.Root + "/" + folder), n.E2eePassword ?? "", ct);
        return list.Select(e => new { e.Folder, e.Name, e.PasswordFits, e.Title });
    }
}
