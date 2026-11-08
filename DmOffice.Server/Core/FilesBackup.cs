using System.Text.Json;

namespace DmOffice.Core;

/// <summary>Regeln rund um Core-Backup und Files-Backup (docs/datei-backup-konzept.md).</summary>
public static class BackupRules
{
    /// <summary>Abstand „monatlich“ in Stunden, wie ihn die Einstellung speichert.</summary>
    public const int Monthly = 720;

    public static int EveryHours(ServerSettings s) => S3Backup.Intervals.Contains(s.S3BackupEveryHours) ? s.S3BackupEveryHours : 24;

    /// <summary>Stunden eines Abstands zum Rechnen: Ein Monat zählt 31 Tage.</summary>
    private static int Span(int every) => every == Monthly ? 31 * 24 : every;

    /// <summary>So viele Stände bleiben am Ziel; frühere Einstellungen in Tagen werden umgerechnet, mindestens 3.</summary>
    public static int KeepCount(ServerSettings s) => s.S3BackupKeepCount > 0 ? s.S3BackupKeepCount
        : Math.Max(3, (int)Math.Ceiling(Math.Max(1, s.S3BackupKeepDays) * 24.0 / Span(EveryHours(s))));

    /// <summary>Mindesthaltedauer des Files-Backups in Tagen: (Anzahl Stände + 1) × Abstand, mindestens 1 Tag.</summary>
    public static int MinHoldDays(ServerSettings s) => Math.Max(1, (int)Math.Ceiling((KeepCount(s) + 1) * Span(EveryHours(s)) / 24.0));

    /// <summary>Das Files-Backup setzt das Core-Backup voraus.</summary>
    public static bool FilesOn(ServerSettings s) => s.S3Backup && s.FilesBackup;

    /// <summary>Wirksame Haltedauer in Tagen; null: unbegrenzt.</summary>
    public static int? HoldDays(ServerSettings s) => s.FilesBackupForever ? null : Math.Max(s.FilesBackupDays, MinHoldDays(s));

    /// <summary>Überfällig: seit dem letzten Lauf ist anderthalbmal der Abstand vergangen.</summary>
    public static bool Overdue(ServerSettings s, DateTime lastRunUtc, DateTime nowUtc) =>
        nowUtc - lastRunUtc > TimeSpan.FromHours(Span(EveryHours(s)) * 1.5);
}

/// <summary>
/// Files-Backup: hält die Dateiinhalte für die Haltedauer an einem Ziel vor. Es kennt die Core-Stände nicht: Verschwindet ein
/// Inhalt vom Server, merkt es sich den Zeitpunkt und löscht ihn am Ziel erst nach der Haltedauer. Jeder Core-Stand, der jünger
/// ist als die Haltedauer, findet dort also alle seine Inhalte, auch wenn er nicht mehr am Ziel liegt.
/// Das Files-Backup ist immer eine eigene Kopie: am eigenen Ziel oder, mit dem Ziel des Storage Extender, im eigenen Ordner
/// {Präfix}files-backup/ neben backups/. Kopiert werden die verschlüsselten Bytes, wie sie sind. Verdrängtes holt der Dienst dafür
/// kurz vom Storage Extender, ohne den Cache zu füllen; im selben Speicher kopiert der Anbieter es direkt, wenn er das kann.
/// Er hat keinen eigenen Zeitplan: Er läuft vor jedem Core-Backup.
/// </summary>
public sealed class FilesBackup(Spaces spaces, ServerConfig config, DataPaths paths, S3Mirror mirror, ILogger<FilesBackup> log) : BackgroundService
{
    public const string RestoreFlag = "restore-files.flag";
    public DataPaths Paths => paths;
    private const string GoneFile = ".files-backup-gone.json";
    /// <summary>Ordner der Kopie, wenn das Files-Backup das Ziel des Storage Extender nutzt.</summary>
    public const string OwnFolder = "files-backup";

    /// <summary>
    /// CompleteSince: Seit wann alle Inhalte am Ziel liegen (erster fehlerfreier Lauf mit diesem Ziel). Stände davor gelten als unvollständig.
    /// </summary>
    public sealed record State(bool Running, DateTime? LastRun, string? LastError, long Uploaded, long Count, long Bytes, long Pending, DateTime? CompleteSince,
        long Done = 0, long Total = 0);
    public sealed record RestoreState(bool Running, long Missing, long Restored, long NotThere, long Failed, string? Error);

    private State _state = new(false, null, null, 0, 0, 0, 0, null);
    public State Status => _state;
    public RestoreState? Restore { get; private set; }

    private readonly SemaphoreSlim _kick = new(0);
    private readonly List<TaskCompletionSource> _waiting = new();
    /// <summary>Nur für Tests: statt des eingestellten Ziels.</summary>
    internal IBlobRemote? RemoteOverride;

    public static bool Configured(ServerConfig config)
    {
        var s = config.Current;
        if (!s.FilesBackupOwnTarget) return s.S3Mirror && S3Mirror.Configured(s, config.S3SecretKey());
        return s.FilesBackupTarget switch
        {
            "webdav" => !string.IsNullOrWhiteSpace(s.FilesBackupWebDavUrl),
            "folder" => !string.IsNullOrWhiteSpace(s.FilesBackupFolder),
            _ => !string.IsNullOrWhiteSpace(s.FilesBackupS3Endpoint) && !string.IsNullOrWhiteSpace(s.FilesBackupS3Bucket) && !string.IsNullOrWhiteSpace(s.FilesBackupS3AccessKey) && !string.IsNullOrEmpty(config.FilesBackupS3SecretKey()),
        };
    }

    /// <summary>Das Ziel des Files-Backups: das eigene oder der Storage Extender.</summary>
    public static IBlobRemote RemoteOf(ServerConfig config)
    {
        var s = config.Current;
        return s.FilesBackupOwnTarget
            ? S3Mirror.RemoteOf(s.FilesBackupTarget, s.FilesBackupS3Endpoint, s.FilesBackupS3Region, s.FilesBackupS3Bucket, s.FilesBackupS3AccessKey, config.FilesBackupS3SecretKey(), s.FilesBackupWebDavUrl, s.FilesBackupWebDavUser, config.FilesBackupWebDavPassword(), s.FilesBackupFolder)
            : S3Mirror.RemoteOf(config);
    }

    public static string? PrefixOf(ServerSettings s) => s.FilesBackupOwnTarget ? s.FilesBackupS3Prefix : S3Mirror.Prefix(s.S3Prefix) + OwnFolder;

    /// <summary>Wo das eigene Ziel liegt - ohne Zugangsdaten. Gleich dem Storage Extender wäre es kein eigenes Ziel.</summary>
    public static string OwnLocationOf(ServerSettings s) =>
        $"{s.FilesBackupTarget ?? "s3"}|{s.FilesBackupS3Endpoint}|{s.FilesBackupS3Bucket}|{s.FilesBackupWebDavUrl}|{s.FilesBackupFolder}|{S3Mirror.Prefix(s.FilesBackupS3Prefix)}";

    private string StateFile => Path.Combine(paths.Root, "files-backup.json");
    private sealed record Saved(DateTime? LastRun, string? LastError, long Uploaded, long Count, long Bytes, long Pending, DateTime? CompleteSince);

    private void Save()
    {
        var x = _state;
        try { File.WriteAllText(StateFile, JsonSerializer.Serialize(new Saved(x.LastRun, x.LastError, x.Uploaded, x.Count, x.Bytes, x.Pending, x.CompleteSince))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Files-Backup: Stand nicht gespeichert: {Error}", e.Message); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(StateFile)) is { } x)
                _state = new State(false, Utc(x.LastRun), x.LastError, x.Uploaded, x.Count, x.Bytes, x.Pending, Utc(x.CompleteSince));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        // Vor der eigenen Kopie stand hier der Stand des Storage Extender - das war keine Sicherung
        if (BackupRules.FilesOn(config.Current) && !config.Current.FilesBackupOwnTarget && !config.Current.FilesBackupCopied)
            _state = new State(false, null, null, 0, 0, 0, 0, null);
    }

    private static DateTime? Utc(DateTime? d) => d is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;

    /// <summary>Ziel oder Schalter geändert: Bis zum nächsten fehlerfreien Lauf gelten die Dateien nicht als vollständig.</summary>
    public void TargetChanged()
    {
        _state = _state with { CompleteSince = null, LastError = null };
        Save();
        config.Update(s => s.FilesBackupCopied = false);
        if (BackupRules.FilesOn(config.Current)) Kick();
    }

    public void Kick() => _kick.Release();

    /// <summary>Vor dem Core-Stand: Abwarten, bis ein ganzer Lauf nach diesem Aufruf fertig ist.</summary>
    public Task BeforeCoreAsync(CancellationToken ct)
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiting) _waiting.Add(t);
        Kick();
        return t.Task.WaitAsync(ct);
    }

    /// <summary>Stand der Dateien eines Core-Stands; null: kein Files-Backup. Unvollständig: älter als die Haltedauer oder vor dem ersten fertigen Lauf.</summary>
    public bool? Complete(DateTime createdUtc, DateTime nowUtc)
    {
        var s = config.Current;
        if (!BackupRules.FilesOn(s)) return null;
        if (_state.CompleteSince is not { } since || createdUtc < since) return false;
        return BackupRules.HoldDays(s) is not int days || nowUtc - createdUtc <= TimeSpan.FromDays(days);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Load();
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }
        var flag = Path.Combine(paths.Root, RestoreFlag);
        if (File.Exists(flag))
        {
            try { if (BackupRules.FilesOn(config.Current) && Configured(config)) { using var remote = RemoteOf(config); await RestoreAsync(remote, ct); } }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { Restore = new RestoreState(false, 0, 0, 0, 0, S3Remote.Describe(e)); log.LogWarning("Aus dem Files-Backup holen: {Error}", S3Remote.Describe(e)); }
            try { File.Delete(flag); } catch (IOException) { }
        }
        // Die erste Kopie wartet nicht auf das nächste Core-Backup: Bis sie vollständig ist, hält der Storage Extender seine Frist
        if (BackupRules.FilesOn(config.Current) && !config.Current.FilesBackupOwnTarget && !config.Current.FilesBackupCopied && Configured(config)) Kick();
        while (!ct.IsCancellationRequested)
        {
            // Hat keinen eigenen Zeitplan: läuft, wenn das Core-Backup wartet oder der Admin Ziel oder Schalter geändert hat
            try { await _kick.WaitAsync(ct); } catch (OperationCanceledException) { return; }
            var s = config.Current;
            if (!BackupRules.FilesOn(s) || !Configured(config))
            {
                if (BackupRules.FilesOn(s) && !s.FilesBackupOwnTarget) { _state = _state with { Running = false, LastRun = DateTime.UtcNow, LastError = "Der Storage Extender ist nicht eingeschaltet." }; Save(); }
                Finish();
                continue;
            }
            try { using var remote = RemoteOverride ?? RemoteOf(config); await PassAsync(remote, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Files-Backup: {Error}", e.Message); _state = _state with { Running = false, LastRun = DateTime.UtcNow, LastError = "Ziel nicht erreichbar: " + S3Remote.Describe(e) }; Save(); }
            Finish();
        }
    }

    private void Finish()
    {
        List<TaskCompletionSource> done;
        lock (_waiting) { done = _waiting.ToList(); _waiting.Clear(); }
        foreach (var w in done) w.TrySetResult();
    }

    /// <summary>Ein Lauf mit eigenem Ziel: Fehlendes übertragen, dann nach der Haltedauer aufräumen.</summary>
    public async Task PassAsync(IBlobRemote remote, CancellationToken ct)
    {
        var s = config.Current;
        var same = !s.FilesBackupOwnTarget;
        var prefix = S3Mirror.Prefix(PrefixOf(s));
        var mirrorPrefix = S3Mirror.Prefix(s.S3Prefix);
        long uploaded = 0, pending = 0, count = 0, bytes = 0, done = 0;
        string? error = null;
        // Vorab zählen, damit die Übersicht den Fortschritt zeigen kann
        long total = 0;
        foreach (var sp in spaces.All()) total += S3Mirror.Referenced(sp.Db).Union(sp.Db.Blobs.List().Select(b => b.Id)).Count();
        _state = _state with { Running = true, Done = 0, Total = total, Uploaded = 0 };
        // Hält die Quelle für Verdrängtes nur offen, wenn sie gebraucht wird
        IBlobRemote? source = null;
        try
        {
            // Im selben Speicher kopiert der Anbieter direkt, falls er das kann; die Größe muss danach stimmen
            async Task<bool> CopyWithin(string from, string key, long size) =>
                same && await remote.TryCopyAsync(from, key, ct) && await remote.SizeAsync(key, ct) == size;
            var other = await S3Mirror.OtherServerAsync(remote, PrefixOf(s), S3Mirror.ServerId(paths), Keep(s), ct);
            if (other != null) log.LogWarning("Files-Backup: am selben Ziel arbeitet auch ein anderer dmOffice-Server (zuletzt {At:u}) - dort wird nichts gelöscht", other);
            foreach (var space in spaces.All())
            {
                ct.ThrowIfCancellationRequested();
                var blobs = space.Db.Blobs;
                var userPrefix = prefix + space.User.Id + "/";
                var sizes = (await remote.ListSizesAsync(userPrefix, ct)).ToDictionary(x => x.Key, x => x.Size, StringComparer.Ordinal);
                var here = blobs.List().Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
                var needed = S3Mirror.Referenced(space.Db);
                var mirrorUser = mirrorPrefix + space.User.Id + "/";
                // Erste Kopie: Auch was der Storage Extender nur noch für seine Frist hält, kommt mit - für ältere Core-Stände
                var held = same && !s.FilesBackupCopied
                    ? (await remote.ListAsync(mirrorUser, ct)).Select(k => k[(k.LastIndexOf('/') + 1)..]).Where(BlobStore.ValidId).ToList()
                    : [];
                foreach (var id in here.Union(needed).Union(held))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!BackupRules.FilesOn(config.Current)) throw new SwitchedOff();
                    var key = userPrefix + id[..2] + "/" + id;
                    var local = blobs.PathOf(id);
                    sizes.TryGetValue(key, out var atTarget);
                    var hasIt = sizes.ContainsKey(key);
                    var from = mirrorUser + id[..2] + "/" + id;
                    try
                    {
                        if (here.Contains(id))
                        {
                            // Ein halb geschriebenes Objekt (Abbruch) zählt nicht
                            var length = new FileInfo(local).Length;
                            if (!hasIt || atTarget != length)
                            {
                                if (!await CopyWithin(from, key, length)) await remote.UploadAsync(local, key, ct);
                                uploaded++;
                                sizes[key] = length;
                            }
                        }
                        else if (!hasIt)
                        {
                            // Verdrängt: die Bytes kurz vom Storage Extender holen, ohne den Cache zu füllen
                            source ??= same ? remote : S3Mirror.Configured(config.Current, config.S3SecretKey()) ? mirror.RemoteOverride ?? S3Mirror.RemoteOf(config) : null;
                            if (source == null) { pending++; error ??= "Dateiinhalte fehlen auf dem Server, und kein Storage Extender ist eingerichtet."; continue; }
                            var size = await source.SizeAsync(from, ct);
                            if (size < 0) { pending++; error ??= "Ein Dateiinhalt fehlt auf dem Server und im Storage Extender."; continue; }
                            if (await CopyWithin(from, key, size)) { uploaded++; sizes[key] = size; done++; _state = _state with { Done = done, Uploaded = uploaded }; continue; }
                            if (mirror.Headroom() < size) { pending++; error ??= "Auf dem lokalen Speichervolumen ist nur noch der frei zu lassende Speicherplatz verfügbar – das Files-Backup setzt beim nächsten Lauf fort."; continue; }
                            var raw = blobs.RawTemp();
                            try { await source.DownloadAsync(from, raw, ct); await remote.UploadAsync(raw, key, ct); uploaded++; sizes[key] = new FileInfo(raw).Length; }
                            finally { if (File.Exists(raw)) File.Delete(raw); }
                        }
                    }
                    // Gerade gelöscht (der Bestand wurde am Anfang gelesen): kein Fehler
                    catch (Exception e) when (e is not OperationCanceledException && here.Contains(id) && !File.Exists(local)) { }
                    catch (Exception e) when (e is not OperationCanceledException) { pending++; error ??= S3Remote.Describe(e); }
                    done++;
                    _state = _state with { Done = done, Uploaded = uploaded };
                }
                var there = sizes.Keys.ToHashSet(StringComparer.Ordinal);
                var sweep = await S3Mirror.SweepAsync(remote, blobs.Dir, GoneFile, there, here, needed, Keep(config.Current), other != null, S3Backup.HoldDeletes(config.Current, paths, DateTime.UtcNow), space.User.Login, log, ct);
                error ??= sweep.Error;
                count += there.Count - sweep.Deleted;
                bytes += sizes.Values.Sum();
            }
        }
        catch (SwitchedOff) { _state = _state with { Running = false }; return; }
        catch (OperationCanceledException) { _state = _state with { Running = false }; throw; }
        catch (Exception e) { error ??= S3Remote.Describe(e); pending++; }
        finally { if (!same) source?.Dispose(); }
        if (error != null) log.LogWarning("Files-Backup: {Error}", error);
        // Erst jetzt gibt der Storage Extender die längere Frist auf
        if (same && error == null && pending == 0 && !config.Current.FilesBackupOwnTarget) config.Update(x => x.FilesBackupCopied = true);
        _state = new State(false, DateTime.UtcNow, error, uploaded, count, bytes, pending,
            error == null && pending == 0 ? _state.CompleteSince ?? DateTime.UtcNow : _state.CompleteSince, done, total);
        Save();
    }

    private static TimeSpan Keep(ServerSettings s) => BackupRules.HoldDays(s) is int days ? TimeSpan.FromDays(days) : TimeSpan.MaxValue;

    /// <summary>
    /// Nach einer Wiederherstellung: Was die Datenbanken kennen, hier fehlt und auch im Storage Extender nicht liegt, vom Ziel
    /// des Files-Backups holen. Was im Storage Extender liegt, holt der Abgleich.
    /// </summary>
    public async Task RestoreAsync(IBlobRemote remote, CancellationToken ct)
    {
        var s = config.Current;
        var prefix = S3Mirror.Prefix(PrefixOf(s));
        long missing = 0, restored = 0, notThere = 0, failed = 0;
        string? error = null;
        void Show(bool running) => Restore = new RestoreState(running, missing, restored, notThere, failed, error);
        Show(true);
        using var mirrorRemote = s.S3Mirror && S3Mirror.Configured(s, config.S3SecretKey()) ? S3Mirror.RemoteOf(config) : null;
        var mirrorPrefix = S3Mirror.Prefix(s.S3Prefix);
        foreach (var space in spaces.All())
        {
            ct.ThrowIfCancellationRequested();
            var blobs = space.Db.Blobs;
            var wanted = S3Mirror.Referenced(space.Db).Where(id => !blobs.Exists(id)).ToList();
            if (wanted.Count == 0) continue;
            var atMirror = mirrorRemote == null ? new HashSet<string>() : await mirrorRemote.ListAsync(mirrorPrefix + space.User.Id + "/", ct);
            wanted = wanted.Where(id => !atMirror.Contains(mirrorPrefix + space.User.Id + "/" + id[..2] + "/" + id)).ToList();
            missing += wanted.Count;
            var userPrefix = prefix + space.User.Id + "/";
            var there = (await remote.ListSizesAsync(userPrefix, ct)).ToDictionary(x => x.Key, x => x.Size, StringComparer.Ordinal);
            foreach (var id in wanted)
            {
                ct.ThrowIfCancellationRequested();
                var key = userPrefix + id[..2] + "/" + id;
                if (!there.TryGetValue(key, out var size)) { notThere++; continue; }
                if (mirror.Headroom() < size) { error = "Auf dem lokalen Speichervolumen ist nur noch der frei zu lassende Speicherplatz verfügbar – das Zurückholen ist angehalten."; Show(false); log.LogWarning("Aus dem Files-Backup holen: {Error}", error); return; }
                var raw = blobs.RawTemp();
                try { await remote.DownloadAsync(key, raw, ct); blobs.Adopt(id, raw); restored++; }
                catch (Exception e) when (e is not OperationCanceledException) { failed++; error ??= S3Remote.Describe(e); if (File.Exists(raw)) File.Delete(raw); }
                if ((restored + failed) % 20 == 0) Show(true);
            }
        }
        if (error != null) log.LogWarning("Aus dem Files-Backup holen: {Error}", error);
        if (notThere > 0) log.LogWarning("Aus dem Files-Backup holen: {Count} Inhalte waren nicht am Ziel", notThere);
        Show(false);
    }

    private sealed class SwitchedOff : Exception;
}
