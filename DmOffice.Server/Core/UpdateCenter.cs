using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace DmOffice.Core;

/// <summary>Freier Platz auf dem Datentraeger, auf dem ein Ordner liegt.</summary>
public static class DiskSpace
{
    /// <summary>Freie Bytes; null, wenn sich das nicht feststellen laesst (dann wird nicht blockiert).
    /// DMOFFICE_ASSUME_FREE_MB ersetzt die Abfrage durch einen festen Wert - zum Ausprobieren der Platzpruefung
    /// und fuer Dateisysteme, die falsche Zahlen melden.</summary>
    public static long? Free(string path)
    {
        try
        {
            if (double.TryParse(Environment.GetEnvironmentVariable("DMOFFICE_ASSUME_FREE_MB"), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var assumed) && assumed >= 0) return (long)(assumed * 1024 * 1024);
            var p = Path.GetFullPath(path);
            while (!Directory.Exists(p))
            {
                var up = Path.GetDirectoryName(p);
                if (up == null) break;
                p = up;
            }
            return new DriveInfo(p).AvailableFreeSpace;
        }
        catch { return null; }
    }

    public static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{(bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture)} GB" : bytes >= 1L << 20 ? $"{(bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture)} MB" : $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB";
}

/// <summary>Die Datenbank eines Benutzers wird (noch) nicht geoeffnet: ihre Datenaenderung wartet auf Platz, auf die
/// Freigabe der Verwaltung oder ist gescheitert. Die Anwendung laeuft weiter, nur dieser Benutzer kommt noch nicht an seine Daten.</summary>
public sealed class UpdatePendingException(string message, string reason) : InvalidOperationException(message)
{
    /// <summary>space, consent, backup-space, backup-failed, error</summary>
    public string Reason { get; } = reason;
}

/// <summary>Was fuer einen Benutzer an Datenaenderung ansteht und ob sie laufen darf.</summary>
public sealed class UserPlan
{
    public string UserId { get; set; } = "";
    public string Login { get; set; } = "";
    public int From { get; set; }
    public int To { get; set; }
    public bool Pending { get; set; }
    /// <summary>current, ready, running, consent, space, backup-space, error</summary>
    public string State { get; set; } = "current";
    public string? Message { get; set; }
    public List<StepInfo> Steps { get; set; } = new();
    public long DbBytes { get; set; }
    /// <summary>Zusaetzlicher Platz, den die Schritte waehrend des Umbaus brauchen (Journal, Kopien).</summary>
    public long MigrationBytes { get; set; }
    public long BackupBytes { get; set; }
    public bool BackupExists { get; set; }
    public bool WillBackup { get; set; }
    /// <summary>Dateiinhalt, der danach noch aus der Datenbank in Blob-Dateien wandert (eigener Schritt, siehe Blobs).</summary>
    public long ContentBytes { get; set; }
    public bool NeedsConsent { get; set; }
    public long? FreeBytes { get; set; }
    /// <summary>Wie viel fuer den Umbau samt Reserve noch fehlt (0: reicht).</summary>
    public long MissingBytes { get; set; }
    /// <summary>Wie viel zusaetzlich fuer die Sicherung fehlt (0: reicht).</summary>
    public long BackupMissingBytes { get; set; }
    public int EtaSeconds { get; set; }
}

public sealed record StepInfo(int Version, string Title, bool Heavy);

/// <summary>Auslagerung der Dateiinhalte (BlobMigration) eines Benutzers: Lage und Platz.</summary>
public sealed class BlobReport
{
    /// <summary>idle, ready, running, consent, space, paused</summary>
    public string State { get; set; } = "idle";
    public long Pending { get; set; }
    public long Bytes { get; set; }
    public long? FreeBytes { get; set; }
    public long MissingBytes { get; set; }
    public int EtaSeconds { get; set; }
    public DateTime At { get; set; }
}

/// <summary>
/// Update-Sicherheit: bevor eine Datenaenderung (Schemaschritte, Auslagerung der Dateiinhalte) laeuft, prueft dmOffice den
/// freien Platz, legt eine Sicherung an (<see cref="PreUpdateBackup"/>) und fragt bei grossen Umbauten die Verwaltung.
/// Reicht der Platz nicht, wird nichts angefasst: die Datenbank bleibt, wie sie ist, die Anwendung laeuft weiter, und die
/// Verwaltung zeigt, was fehlt. Die Freigaben stehen in update.json im Datenordner.
/// </summary>
public sealed class UpdateCenter
{
    private const long MB = 1024L * 1024;

    private sealed class Consent
    {
        public bool MigrationApproved { get; set; }
        public bool BlobsApproved { get; set; }
        public bool Paused { get; set; }
        /// <summary>Schemaversion, fuer die der Betreiber ausdruecklich ohne Sicherung fortfahren wollte.</summary>
        public int NoBackupFor { get; set; }
    }

    private readonly DataPaths _paths;
    private readonly UserStore _users;
    private readonly ServerConfig _config;
    private readonly object _lock = new();
    private readonly string _file;
    private Consent _consent;
    private long _kicks;
    private TaskCompletionSource _kicked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, (DateTime At, string Message)> _failed = new();
    private readonly ConcurrentDictionary<string, BlobReport> _blobs = new();
    private readonly ConcurrentDictionary<string, (DateTime At, UserDb.Inspection Info)> _inspected = new();

    /// <summary>Freier Platz am Ort eines Ordners - fuer Tests austauschbar.</summary>
    public Func<string, long?> FreeBytes { get; set; } = DiskSpace.Free;
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    /// <summary>Ist die Datenbank des Benutzers schon geoeffnet (und damit auf dem neuen Stand)? Setzt <see cref="Spaces"/>.</summary>
    public Func<string, bool>? IsOpen { get; set; }
    /// <summary>Platz, der nach dem Umbau mindestens frei bleiben soll.</summary>
    public long Reserve { get; set; } = 256 * MB;
    /// <summary>Ab so viel Umbau (Bytes) startet er nicht still, sondern erst nach "Jetzt starten".</summary>
    public long BigBytes { get; set; } = 1024 * MB;
    /// <summary>Wartezeit nach einem gescheiterten Umbau, bevor es von selbst neu versucht wird.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(5);
    public string AppVersion { get; set; } = SettingsApi.Version();

    public UpdateCenter(DataPaths paths, UserStore users, ServerConfig config)
    {
        _paths = paths;
        _users = users;
        _config = config;
        _file = Path.Combine(paths.Root, "update.json");
        try { _consent = File.Exists(_file) ? JsonSerializer.Deserialize<Consent>(File.ReadAllText(_file)) ?? new() : new(); }
        catch { _consent = new(); }
    }

    // ---------------- Freigaben ----------------

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_consent));
        File.Move(tmp, _file, overwrite: true);
    }

    public bool Paused { get { lock (_lock) return _consent.Paused; } }
    public bool MigrationApproved { get { lock (_lock) return _consent.MigrationApproved; } }
    public bool BlobsApproved { get { lock (_lock) return _consent.BlobsApproved; } }
    public bool WithoutBackupApproved { get { lock (_lock) return _consent.NoBackupFor == UserDb.LatestVersion; } }

    /// <summary>"Jetzt starten": grosse Umbauten freigeben und fortsetzen; optional ausdruecklich ohne Sicherung.</summary>
    public void Start(bool withoutBackup, bool approve = true)
    {
        lock (_lock)
        {
            if (approve)
            {
                _consent.MigrationApproved = true;
                _consent.BlobsApproved = true;
                _consent.Paused = false;
            }
            if (withoutBackup) _consent.NoBackupFor = UserDb.LatestVersion;
            Save();
        }
        _failed.Clear();
        Kick();
    }

    public void SetPaused(bool paused)
    {
        lock (_lock) { _consent.Paused = paused; Save(); }
        if (!paused) Kick();
    }

    /// <summary>Freigaben zuruecknehmen, sobald nichts mehr ansteht - die naechste grosse Aenderung fragt von vorn.</summary>
    public void ResetApprovals(bool migrationDone, bool blobsDone)
    {
        lock (_lock)
        {
            var changed = false;
            if (migrationDone && _consent.MigrationApproved) { _consent.MigrationApproved = false; changed = true; }
            if (blobsDone && _consent.BlobsApproved) { _consent.BlobsApproved = false; changed = true; }
            if (migrationDone && _consent.NoBackupFor != 0) { _consent.NoBackupFor = 0; changed = true; }
            if (changed) Save();
        }
    }

    /// <summary>Hintergrunddienste jetzt wecken (neue Freigabe, "Erneut pruefen").</summary>
    public void Kick()
    {
        Interlocked.Increment(ref _kicks);
        Interlocked.Exchange(ref _kicked, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>Zaehler der Weckrufe - vor einem Durchgang merken und an <see cref="WaitAsync"/> geben, damit kein Ruf waehrenddessen verloren geht.</summary>
    public long Generation => Interlocked.Read(ref _kicks);

    /// <summary>Hoechstens <paramref name="max"/> warten - oder bis jemand <see cref="Kick"/> ruft (alle Wartenden werden geweckt).</summary>
    public async Task WaitAsync(TimeSpan max, long since, CancellationToken ct)
    {
        if (Generation != since) return;
        try { await Task.WhenAny(_kicked.Task, Task.Delay(max, ct)); } catch (OperationCanceledException) { }
    }

    // ---------------- Vorabpruefung ----------------

    private UserDb.Inspection Inspect(UserRecord u)
    {
        if (_inspected.TryGetValue(u.Id, out var c) && Now() - c.At < TimeSpan.FromSeconds(10)) return c.Info;
        var info = UserDb.Inspect(_paths.UserDb(u.Id), _users.DbKey(u));
        _inspected[u.Id] = (Now(), info);
        return info;
    }

    /// <summary>Was fuer diesen Benutzer ansteht, ohne etwas zu veraendern.</summary>
    public UserPlan Plan(UserRecord u)
    {
        var plan = new UserPlan { UserId = u.Id, Login = u.Login, To = UserDb.LatestVersion, From = UserDb.LatestVersion };
        var path = _paths.UserDb(u.Id);
        if (IsOpen?.Invoke(u.Id) == true || !File.Exists(path) || new FileInfo(path).Length == 0) return plan;
        UserDb.Inspection info;
        try { info = Inspect(u); }
        catch (Exception ex) { plan.State = "error"; plan.Message = ex.Message; return plan; }

        plan.From = info.Version;
        plan.DbBytes = info.FileBytes + info.WalBytes;
        plan.ContentBytes = info.ContentBytes;
        if (info.Version >= plan.To) return plan;

        plan.Pending = true;
        plan.Steps = UserDb.Steps.Where(s => s.Version > info.Version).Select(s => new StepInfo(s.Version, s.Title, s.Version is 3 or 11)).ToList();
        // Schritt 3 kopiert den Mailinhalt, Schritt 11 schreibt jede Datei samt Inhalt neu: beides liegt im Journal, bis es festgeschrieben ist
        plan.MigrationBytes = Math.Max(32 * MB, plan.DbBytes / 50) + (long)(info.MailRawBytes * 1.2) + (long)(info.FileDataBytes * 1.1);
        plan.BackupExists = PreUpdateBackup.Has(_paths, u.Id, info.Version);
        plan.WillBackup = !plan.BackupExists && !WithoutBackupApproved;
        plan.BackupBytes = plan.BackupExists ? 0 : plan.DbBytes;
        plan.NeedsConsent = plan.MigrationBytes > BigBytes;
        plan.EtaSeconds = (int)Math.Min(int.MaxValue, 3 + (info.MailRawBytes + info.FileDataBytes) / (40 * MB) + plan.BackupBytes / (80 * MB));
        plan.FreeBytes = FreeBytes(_paths.Root);

        var need = plan.MigrationBytes + Reserve;
        if (plan.FreeBytes is { } free)
        {
            plan.MissingBytes = Math.Max(0, need - free);
            if (plan.WillBackup) plan.BackupMissingBytes = Math.Max(0, need + plan.BackupBytes - free) - plan.MissingBytes;
        }

        var running = Activity.Get(u.Id, Activity.Migration) is { State: "running" };
        if (running) plan.State = "running";
        else if (plan.MissingBytes > 0) { plan.State = "space"; plan.Message = "Nicht genug freier Platz fÃ¼r die DatenÃ¤nderung."; }
        else if (plan.NeedsConsent && !MigrationApproved) plan.State = "consent";
        else if (plan.WillBackup && plan.BackupMissingBytes > 0) plan.State = "backup-space";
        else plan.State = "ready";

        if (_failed.TryGetValue(u.Id, out var f) && plan.State is "ready" or "error" && Now() - f.At < RetryAfter)
        {
            plan.State = "error";
            plan.Message = f.Message;
        }
        return plan;
    }

    /// <summary>
    /// Vor dem Oeffnen der Datenbank (Spaces.Get): darf der Umbau jetzt laufen? Legt die Sicherung an, wenn noetig.
    /// Gibt true zurueck, wenn ein Umbau folgt; false, wenn die Datenbank schon aktuell oder neu ist. Reicht der Platz nicht,
    /// fehlt die Freigabe oder scheitert die Sicherung, kommt <see cref="UpdatePendingException"/> - und es wurde nichts angefasst.
    /// </summary>
    public bool Gate(UserRecord user)
    {
        UserPlan plan;
        try { plan = Plan(user); }
        catch { return false; }
        if (!plan.Pending) return false;
        switch (plan.State)
        {
            case "space":
                throw new UpdatePendingException($"Die DatenÃ¤nderung wartet: es fehlen etwa {DiskSpace.Format(plan.MissingBytes)} freier Platz.", "space");
            case "consent":
                throw new UpdatePendingException("Die DatenÃ¤nderung ist groÃŸ und wartet auf â€žJetzt startenâ€œ in der Verwaltung.", "consent");
            case "backup-space":
                throw new UpdatePendingException($"FÃ¼r die Sicherung vor der DatenÃ¤nderung fehlen etwa {DiskSpace.Format(plan.BackupMissingBytes)}. Die Verwaltung kann ohne Sicherung fortfahren.", "backup-space");
            case "error":
                throw new UpdatePendingException("Die letzte DatenÃ¤nderung ist gescheitert: " + plan.Message, "error");
        }
        if (plan.WillBackup)
        {
            using var activity = Activity.Start(user.Id, Activity.Backup);
            try { PreUpdateBackup.Create(_paths, user, _users.DbKey(user), plan.From, plan.To, AppVersion, Now()); }
            catch (Exception ex)
            {
                activity.Fail(ex.Message);
                Failed(user.Id, ex);
                throw new UpdatePendingException("Die Sicherung vor der DatenÃ¤nderung ist fehlgeschlagen, die Daten sind unverÃ¤ndert: " + ex.Message, "backup-failed");
            }
        }
        return true;
    }

    /// <summary>Der Umbau ist gescheitert (alle Schritte vorher sind zurueckgerollt) - eine Weile nicht von selbst neu versuchen.</summary>
    public void Failed(string userId, Exception ex)
    {
        _failed[userId] = (Now(), ex.Message);
        _inspected.TryRemove(userId, out _);
    }

    public void Succeeded(string userId)
    {
        _failed.TryRemove(userId, out _);
        _inspected.TryRemove(userId, out _);
    }

    // ---------------- Auslagerung der Dateiinhalte ----------------

    /// <summary>Darf die Auslagerung fuer diesen Benutzer jetzt laufen? Prueft Platz am Blob-Ort, Freigabe und Pause.</summary>
    public BlobReport CheckBlobs(string userId, long pending, long bytes)
    {
        var r = new BlobReport { Pending = pending, Bytes = bytes, At = Now(), State = "idle" };
        if (pending > 0)
        {
            r.FreeBytes = FreeBytes(_paths.Blobs);
            r.EtaSeconds = (int)Math.Min(int.MaxValue, 3 + bytes / (30 * MB));
            // Blob-Dateien sind wenig groesser als der Klartext (Kopf und Pruefwert je Block)
            var need = (long)(bytes * 1.02) + Reserve;
            if (r.FreeBytes is { } free && free < need) { r.State = "space"; r.MissingBytes = need - free; }
            else if (bytes > BigBytes && !BlobsApproved) r.State = "consent";
            else if (Paused) r.State = "paused";
            else r.State = "ready";
        }
        _blobs[userId] = r;
        return r;
    }

    /// <summary>Waehrend der Auslagerung zwischen den Portionen: weiter? (Pause, Platz wird knapp.)</summary>
    public bool BlobsMayContinue(string userId)
    {
        if (Paused) { SetBlobState(userId, "paused"); return false; }
        if (FreeBytes(_paths.Blobs) is { } free && free < Reserve / 2)
        {
            if (_blobs.TryGetValue(userId, out var r)) { r.State = "space"; r.MissingBytes = Reserve / 2 - free; r.FreeBytes = free; }
            return false;
        }
        return true;
    }

    public void SetBlobState(string userId, string state)
    {
        if (_blobs.TryGetValue(userId, out var r)) r.State = state;
    }

    public BlobReport? BlobsOf(string userId) => _blobs.GetValueOrDefault(userId);

    // ---------------- Sicherungen ----------------

    public int KeepDays => _config.Current.PreUpdateKeepDays;

    public void SetKeepDays(int days) => _config.Update(s => s.PreUpdateKeepDays = Math.Clamp(days, 0, 3650));

    /// <summary>Alte Sicherungen wegraeumen, solange keine Datenbank auf ihren Umbau wartet.</summary>
    public int CleanupBackups()
    {
        if (_users.All().Any(u => Plan(u).Pending)) return 0;
        return PreUpdateBackup.Cleanup(_paths, KeepDays, Now());
    }

    // ---------------- Anzeige ----------------

    /// <summary>Alles fuer die Verwaltung: Plaene, Auslagerung, Sicherungen, Platz.</summary>
    public object Describe()
    {
        var users = _users.All();
        var plans = users.Select(Plan).ToList();
        var backups = PreUpdateBackup.List(_paths);
        return new
        {
            version = AppVersion,
            schema = UserDb.LatestVersion,
            dataDir = _paths.Root,
            blobDir = _paths.Blobs,
            freeData = FreeBytes(_paths.Root),
            freeBlobs = FreeBytes(_paths.Blobs),
            reserve = Reserve,
            keepDays = KeepDays,
            paused = Paused,
            approved = new { migration = MigrationApproved, blobs = BlobsApproved, withoutBackup = WithoutBackupApproved },
            attention = plans.Any(p => p.Pending && p.State != "running") || Attention(users),
            users = plans.Select(p => new
            {
                plan = p,
                activity = Activity.Get(p.UserId, Activity.Migration),
                backupActivity = Activity.Get(p.UserId, Activity.Backup),
            }),
            blobs = users.Select(u => new
            {
                userId = u.Id, u.Login,
                report = _blobs.GetValueOrDefault(u.Id),
                activity = Activity.Get(u.Id, Activity.FilesBlobs),
            }),
            backups = backups.Select(b => new { b.Name, b.Created, b.AppVersion, b.Bytes, b.Path, users = b.Users }),
            backupBytes = backups.Sum(b => b.Bytes),
        };
    }

    /// <summary>Wartet bei einem Benutzer die Auslagerung auf Platz oder Freigabe?</summary>
    public bool Attention(IEnumerable<UserRecord> users) =>
        users.Any(u => _blobs.GetValueOrDefault(u.Id) is { State: "space" or "consent" });

    /// <summary>Kurzfassung fuer den angemeldeten Benutzer: steht seine Datenaenderung noch aus?</summary>
    public (UserPlan Plan, BlobReport? Blobs) Mine(UserRecord u) => (Plan(u), _blobs.GetValueOrDefault(u.Id));

    public bool AnyAttention() => Attention(_users.All()) || _users.All().Any(u => Plan(u) is { Pending: true, State: not "running" });
}
