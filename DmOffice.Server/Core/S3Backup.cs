using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DmOffice.Core;

/// <summary>
/// Vollstaendige Sicherung nach S3 (Version 2), zusammen mit der Kopie der Dateiinhalte (S3Mirror):
/// nachts ein konsistenter Stand aller Datenbanken (VACUUM INTO, verschluesselt wie im Betrieb), der Benutzerliste,
/// der Einstellungen und des Schluesselrings - als eine Datei, mit einem aus dem Hauptschluessel abgeleiteten
/// Schluessel noch einmal als Ganzes verschluesselt (BlobStore-Format: Bloecke mit AES-GCM, Abschneiden faellt auf).
/// Nicht dabei: der Hauptschluessel (gehoert getrennt aufbewahrt). fcm.json (Firebase-Dienstkonto) ist dabei - die ganze Sicherung
/// ist verschluesselt, nach dem Wiederherstellen gehen Push-Nachrichten an die App gleich wieder.
/// In S3: {Praefix}backups/{yyyyMMdd-HHmmss}_{Blob}.dmb - der Blob-Name gehoert zur Entschluesselung.
/// Wiederherstellen: auf einem leeren dmOffice mit demselben Hauptschluessel (Ersteinrichtung, Auth).
/// </summary>
public sealed class S3Backup(DataPaths paths, Vault vault, UserStore users, ServerConfig config, IServiceProvider services, ILogger<S3Backup> log) : BackgroundService
{
    public const string Folder = "backups/";
    private readonly SemaphoreSlim _kick = new(0);
    private volatile bool _now;

    /// <param name="OtherServer">Zuletzt gesehen: ein anderer Server am selben Ziel - solange räumt dieser keine alten Stände weg.</param>
    public sealed record State(bool Running, DateTime? LastRun, string? LastError, string? LastName, long LastBytes, DateTime? OtherServer = null);
    private State _state = new(false, null, null, null, 0);
    public State Status => _state;

    public sealed record Stand(string Key, string Name, DateTime Created, long Bytes);

    /// <summary>Schluessel der Sicherungsdateien, aus dem Hauptschluessel abgeleitet.</summary>
    public static byte[] KeyOf(byte[] master) => HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "dmoffice s3 backup v1"u8.ToArray());

    /// <summary>Sofort sichern (Knopf in der Verwaltung).</summary>
    public void RunNow() { _now = true; _kick.Release(); }

    /// <summary>Warum gerade nicht gesichert werden kann - null: geht.</summary>
    /// <summary>Wohin das Core-Backup geht: ein eigenes Ziel oder dasselbe wie der Storage Extender.</summary>
    public static bool TargetConfigured(ServerConfig config)
    {
        var s = config.Current;
        if (!s.BackupOwnTarget) return S3Mirror.Configured(s, config.S3SecretKey());
        return s.BackupTarget switch
        {
            "webdav" => !string.IsNullOrWhiteSpace(s.BackupWebDavUrl),
            "folder" => !string.IsNullOrWhiteSpace(s.BackupFolder),
            _ => !string.IsNullOrWhiteSpace(s.BackupS3Endpoint) && !string.IsNullOrWhiteSpace(s.BackupS3Bucket) && !string.IsNullOrWhiteSpace(s.BackupS3AccessKey) && !string.IsNullOrEmpty(config.BackupS3SecretKey()),
        };
    }
    public static IBlobRemote RemoteOf(ServerConfig config)
    {
        var s = config.Current;
        return s.BackupOwnTarget
            ? S3Mirror.RemoteOf(s.BackupTarget, s.BackupS3Endpoint, s.BackupS3Region, s.BackupS3Bucket, s.BackupS3AccessKey, config.BackupS3SecretKey(), s.BackupWebDavUrl, s.BackupWebDavUser, config.BackupWebDavPassword(), s.BackupFolder)
            : S3Mirror.RemoteOf(config);
    }
    public static string? PrefixOf(ServerSettings s) => s.BackupOwnTarget ? s.BackupS3Prefix : s.S3Prefix;

    public string? Blocked() =>
        !TargetConfigured(config) ? "Das Ziel des Core-Backups ist nicht eingerichtet."
        : vault.KeyInDataDir ? "Der Hauptschlüssel liegt als Datei im Datenverzeichnis. Eine Sicherung, die ihn nicht enthält, ließe sich nach einem Ausfall nicht öffnen – setze DMOFFICE_MASTER_KEY in der Container-Vorlage (oben in der Verwaltung steht, wie) und bewahre ihn getrennt auf."
        : null;

    /// <summary>
    /// Letzte Sicherung über einen Neustart hinweg: sonst gilt nach jedem Neustart „noch nie gesichert“ und das Core-Backup
    /// läuft eine Minute nach dem Start, egal welche Uhrzeit eingestellt ist.
    /// </summary>
    private string StateFile => Path.Combine(paths.Root, "core-backup.json");
    private sealed record Saved(DateTime? LastRun, string? LastName, long LastBytes, string? LastError);

    /// <summary>
    /// Am Ziel nichts loeschen: Das Core-Backup ist eingeschaltet, aber sein letzter Lauf ist gescheitert, ueberfaellig oder steht
    /// noch aus - die vorhandenen Staende bleiben dann laenger stehen und brauchen ihre Inhalte.
    /// </summary>
    public static bool HoldDeletes(ServerSettings s, DataPaths paths, DateTime nowUtc)
    {
        if (!s.S3Backup) return false;
        Saved? saved = null;
        try { var f = Path.Combine(paths.Root, "core-backup.json"); if (File.Exists(f)) saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(f)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return saved?.LastRun is not { } last || saved.LastError != null || BackupRules.Overdue(s, DateTime.SpecifyKind(last, DateTimeKind.Utc), nowUtc);
    }

    /// <summary>Naechster geplanter Lauf (Ortszeit des Servers).</summary>
    public static DateTime NextRun(ServerSettings s, DateTime now, DateTime? lastRunUtc)
    {
        if (!TimeOnly.TryParse(s.S3BackupAt ?? "03:00", CultureInfo.InvariantCulture, out var at)) at = new TimeOnly(3, 0);
        var start = now.Date + at.ToTimeSpan() - TimeSpan.FromDays(1);
        for (var i = 0; i < 24 * 40; i++)
        {
            var t = start.AddHours(i);
            if (t > now && Due(s, t, lastRunUtc)) return t;
        }
        return now;
    }

    private void SaveState()
    {
        try { File.WriteAllText(StateFile, JsonSerializer.Serialize(new Saved(_state.LastRun, _state.LastName, _state.LastBytes, _state.LastError))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Core-Backup: Stand nicht gespeichert: {Error}", e.Message); }
    }

    /// <summary>Stand beim Start: aus der Datei; fehlt sie (erster Start mit dieser Version), die neueste Sicherung am Ziel.</summary>
    private async Task LoadStateAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(StateFile) && JsonSerializer.Deserialize<Saved>(await File.ReadAllTextAsync(StateFile, ct)) is { } st)
            {
                _state = _state with { LastRun = st.LastRun is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null, LastName = st.LastName, LastBytes = st.LastBytes, LastError = st.LastError };
                return;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        if (!TargetConfigured(config)) return;
        try
        {
            using var remote = RemoteOf(config);
            if ((await ListAsync(remote, PrefixOf(config.Current), ct)).FirstOrDefault() is { } newest)
                _state = _state with { LastRun = DateTime.SpecifyKind(newest.Created, DateTimeKind.Utc), LastName = newest.Name, LastBytes = newest.Bytes };
        }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogInformation("Core-Backup: letzte Sicherung am Ziel nicht gefunden: {Error}", e.Message); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await LoadStateAsync(ct); await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var s = config.Current;
            var due = s.S3Backup && Due(s, DateTime.Now);
            if ((_now || due) && Blocked() is null)
            {
                _now = false;
                try { using var remote = RemoteOf(config); await RunAsync(remote, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                // Ziel nicht erreichbar (z. B. Ordner nicht eingehaengt): melden statt den Server zu beenden
                catch (Exception e) { log.LogWarning("Core-Backup: {Error}", e.Message); _state = _state with { LastError = "Ziel nicht erreichbar: " + e.Message }; SaveState(); }
            }
            else if (_now || due) { _now = false; _state = _state with { LastError = Blocked() }; }
            try { await _kick.WaitAsync(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Erlaubte Abstände in Stunden (stündlich bis monatlich).</summary>
    public static readonly int[] Intervals = [1, 3, 6, 12, 24, 168, BackupRules.Monthly];

    /// <summary>
    /// Faellig: Unter einem Tag zu jedem Zeitpunkt ab der Uhrzeit im eingestellten Abstand (03:00, 09:00, 15:00 …), wenn seit
    /// dem letzten dieser Zeitpunkte noch nicht gesichert wurde. Täglich bzw. wöchentlich: nach der Uhrzeit, wenn die letzte
    /// Sicherung mindestens einen halben Tag bzw. sechseinhalb Tage zurückliegt.
    /// </summary>
    internal static bool Due(ServerSettings s, DateTime now, DateTime? lastRunUtc)
    {
        if (!TimeOnly.TryParse(s.S3BackupAt ?? "03:00", CultureInfo.InvariantCulture, out var at)) at = new TimeOnly(3, 0);
        var every = Intervals.Contains(s.S3BackupEveryHours) ? s.S3BackupEveryHours : 24;
        var anchor = now.Date + at.ToTimeSpan();
        if (anchor > now) anchor = anchor.AddDays(-1);
        if (lastRunUtc is not { } lastUtc) return true;
        var last = lastUtc.ToLocalTime();
        if (every < 24)
        {
            var slot = anchor.AddHours(Math.Floor((now - anchor).TotalHours / every) * every);
            return last < slot;
        }
        return last < anchor && anchor - last >= TimeSpan.FromHours(every - 12);
    }

    private bool Due(ServerSettings s, DateTime now) => Due(s, now, _state.LastRun);

    /// <summary>So lange wartet das Core-Backup hoechstens auf den Abgleich der Dateien.</summary>
    internal static TimeSpan PassWait = TimeSpan.FromMinutes(30);

    public async Task RunAsync(IBlobRemote remote, CancellationToken ct)
    {
        _state = _state with { Running = true, LastError = null };
        // Die Tresor-Datenbank liegt verschlüsselt in den Dateien: zuerst zurückschreiben, damit der Abgleich und der Stand dieselbe Fassung sehen
        foreach (var u in users.All()) FlushTresor(u.Id);
        // Erst die Dateien, dann die Datenbanken: so kennt das Core-Backup nur Inhalte, die schon am Ziel liegen
        var mirror = config.Current.S3Mirror ? services.GetService<S3Mirror>() : null;
        if (mirror != null)
            try { await mirror.NextPassAsync(ct).WaitAsync(PassWait, ct); }
            catch (TimeoutException) { log.LogWarning("Core-Backup: der Abgleich der Dateien dauert noch – gesichert wird trotzdem, ganz frische Dateien fehlen darin womöglich"); }
        // Files-Backup: ebenso vor dem Stand, damit dieser nur auf Inhalte zeigt, die schon dort liegen
        if (BackupRules.FilesOn(config.Current) && services.GetService<FilesBackup>() is { } files)
            try { await files.BeforeCoreAsync(ct).WaitAsync(PassWait, ct); }
            catch (TimeoutException) { log.LogWarning("Core-Backup: das Files-Backup läuft noch – gesichert wird trotzdem"); }
        var work = Path.Combine(paths.Root, "backup-work");
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            var stage = Directory.CreateDirectory(Path.Combine(work, "stand")).FullName;
            var now = DateTime.UtcNow;
            Snapshot(stage, now, mirror);
            // Packen und verschluesseln in einem Zug
            var store = new BlobStore(Path.Combine(work, "out"), KeyOf(vault.Key));
            BlobInfo info;
            await using (var w = store.Create())
            {
                await TarFile.CreateFromDirectoryAsync(stage, w, includeBaseDirectory: false, ct);
                info = w.Complete();
            }
            var name = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + info.Id;
            await remote.UploadAsync(store.PathOf(info.Id!), S3Mirror.Prefix(PrefixOf(config.Current)) + Folder + name + ".dmb", ct);
            _state = new State(false, now, null, name, new FileInfo(store.PathOf(info.Id!)).Length, _state.OtherServer);
            SaveState();
            await CleanupAsync(remote, ct);
            log.LogInformation("S3-Sicherung {Name} ({Bytes} Bytes)", name, _state.LastBytes);
        }
        catch (OperationCanceledException) { _state = _state with { Running = false }; throw; }
        catch (Exception e)
        {
            _state = _state with { Running = false, LastRun = DateTime.UtcNow, LastError = S3Remote.Describe(e) };
            SaveState();
            log.LogWarning("S3-Sicherung: {Error}", _state.LastError);
        }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { } }
    }

    private void FlushTresor(string userId)
    {
        try { Tresor.Flush(userId, force: true); }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Core-Backup: Tresor nicht zurückgeschrieben: {Error}", e.GetType().Name); }
    }

    /// <summary>Konsistenter Stand des Datenverzeichnisses ohne Dateiinhalte (die liegen schon in S3).</summary>
    private void Snapshot(string stage, DateTime now, S3Mirror? mirror = null)
    {
        // Welche Inhalte jetzt am Ziel liegen: nach einer Wiederherstellung ist alles Weitere dort danach entstanden (S3Mirror.RescueAsync)
        if (mirror != null)
            foreach (var u in users.All())
                if (mirror.AtTarget(u.Id) is { } ids)
                {
                    Directory.CreateDirectory(Path.Combine(stage, S3Mirror.KnownFolder));
                    File.WriteAllText(Path.Combine(stage, S3Mirror.KnownFolder, u.Id + ".json"), JsonSerializer.Serialize(ids));
                }
        var manifest = new Manifest { Created = now, AppVersion = SettingsApi.Version(), Release = SettingsApi.Release() };
        Directory.CreateDirectory(Path.Combine(stage, "users"));
        foreach (var u in users.All())
        {
            var source = paths.UserDb(u.Id);
            if (!File.Exists(source)) continue;
            // Vor der Benutzerdatenbank: Sie muss auf die Fassung der Tresor-Datenbank zeigen, die im Stand gilt
            FlushTresor(u.Id);
            Vacuum(UserDb.ConnectionStringOf(source, users.DbKey(u)), Path.Combine(stage, "users", u.Id + ".db"));
            // Tresor der Person: verschlüsselt mit ihrem Tresor-Schlüssel, samt Dateien
            Tresor.Backup(u.Id, stage);
            manifest.Users.Add(new ManifestUser(u.Id, u.Login));
        }
        services.GetRequiredService<Chat.ChatStore>().SnapshotTo(Path.Combine(stage, "chat.db"));
        services.GetRequiredService<Gallery.AlbumStore>().SnapshotTo(Path.Combine(stage, "albums.db"));
        services.GetRequiredService<WebPush>().SnapshotTo(Path.Combine(stage, "push.db"));
        foreach (var f in Directory.EnumerateFiles(paths.Root))
        {
            var n = Path.GetFileName(f);
            if (n.EndsWith(".db") || n.EndsWith("-wal") || n.EndsWith("-shm") || n.EndsWith(".tmp") || n.EndsWith(".part")
                || n.Equals("master.key", StringComparison.OrdinalIgnoreCase) || n == S3Mirror.ServerIdFile) continue;
            File.Copy(f, Path.Combine(stage, n));
        }
        if (Directory.Exists(paths.Keys))
            foreach (var f in Directory.EnumerateFiles(paths.Keys, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(stage, Path.GetRelativePath(paths.Root, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
        File.WriteAllText(Path.Combine(stage, "backup.json"), JsonSerializer.Serialize(manifest));
    }

    internal static void Vacuum(string connectionString, string target)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=30000";
        pragma.ExecuteNonQuery();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "VACUUM INTO $f";
        cmd.Parameters.AddWithValue("$f", target);
        cmd.ExecuteNonQuery();
    }

    public sealed class Manifest
    {
        public DateTime Created { get; set; }
        public string AppVersion { get; set; } = "";
        /// <summary>Name der Version ("2.0"), ab 2.0 im Stand; das Werkzeug liest nur solche Staende.</summary>
        public string? Release { get; set; }
        public List<ManifestUser> Users { get; set; } = new();
    }
    public sealed record ManifestUser(string Id, string Login);

    /// <summary>Staende in S3, neueste zuerst.</summary>
    public static async Task<List<Stand>> ListAsync(IBlobRemote remote, string? prefix, CancellationToken ct)
    {
        var p = S3Mirror.Prefix(prefix) + Folder;
        var list = new List<Stand>();
        foreach (var (key, size) in await remote.ListSizesAsync(p, ct))
        {
            var name = key[p.Length..];
            if (!name.EndsWith(".dmb")) continue;
            name = name[..^4];
            if (ParseName(name) is not { } created) continue;
            list.Add(new Stand(key, name, created, size));
        }
        return list.OrderByDescending(s => s.Created).ToList();
    }

    /// <summary>„20261023-030000_&lt;blob&gt;“ → Zeitpunkt (UTC); null: kein Sicherungsname.</summary>
    public static DateTime? ParseName(string name)
    {
        var i = name.IndexOf('_');
        if (i != 15 || !BlobStore.ValidId(name[(i + 1)..])) return null;
        return DateTime.TryParseExact(name[..i], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    /// <summary>Alte Staende loeschen: Es bleiben die neuesten, so viele wie eingestellt (BackupRules.KeepCount).</summary>
    private async Task CleanupAsync(IBlobRemote remote, CancellationToken ct)
    {
        // Sichert ein anderer dmOffice-Server ans selbe Ziel, raeumt keiner dessen Staende weg
        var other = await S3Mirror.OtherServerAsync(remote, PrefixOf(config.Current), S3Mirror.ServerId(paths), S3Mirror.KeepDeleted(config.Current), ct);
        _state = _state with { OtherServer = other };
        if (other != null) { log.LogWarning("Alte Stände des Core-Backups bleiben: Am Ziel speichert ein anderer Server"); return; }
        // Meldet die Datenbankpruefung eine beschaedigte Datenbank, bleiben die alten Staende - in ihnen steckt die heile Fassung
        if (services.GetService<DbHealth>()?.Last?.Items.Any(i => !i.Ok) == true) { log.LogWarning("Alte Sicherungsstaende bleiben: eine Datenbank ist beschaedigt"); return; }
        var all = await ListAsync(remote, PrefixOf(config.Current), ct);
        foreach (var s in all.Skip(BackupRules.KeepCount(config.Current)))
            await remote.DeleteAsync(s.Key, ct);
    }

    /// <summary>Vermerke anderer Server am Ziel des Core-Backups entfernen (der „andere“ war dieser Server vor einem Umzug oder einer Wiederherstellung).</summary>
    public async Task<int> ForgetOthersAsync(CancellationToken ct)
    {
        if (!TargetConfigured(config)) return 0;
        using var remote = RemoteOf(config);
        var folder = S3Mirror.Prefix(PrefixOf(config.Current)) + S3Mirror.ServersFolder;
        var self = S3Mirror.ServerId(paths);
        var n = 0;
        foreach (var (key, _) in await remote.ListSizesAsync(folder, ct))
            if (!key.EndsWith("/" + self, StringComparison.Ordinal)) { await remote.DeleteAsync(key, ct); n++; }
        _state = _state with { OtherServer = null };
        return n;
    }

    public const string PendingFolder = "restore-pending";

    /// <summary>
    /// Beim Start, bevor irgendetwas Dateien im Datenverzeichnis oeffnet: einen wiederhergestellten Stand aus dem
    /// Wartebereich an seinen Platz legen (vorhandene Dateien ersetzen) und das Holen der Dateiinhalte vormerken.
    /// Gibt die Zahl der Dateien zurueck (0: nichts wartete).
    /// </summary>
    public static int ApplyPending(DataPaths paths)
    {
        var pending = Path.Combine(paths.Root, PendingFolder);
        if (!Directory.Exists(pending) || !File.Exists(Path.Combine(pending, "backup.json"))) return 0;
        // Datenbanken der Ersteinrichtung, die kein Stand enthält (Spamabgleich, Sicherheit, Suchindex): Ihre Schlüssel liegen in keys/
        // und werden gleich ersetzt - sie liessen sich danach nicht mehr öffnen. Sie entstehen beim Start neu.
        foreach (var db in Directory.EnumerateFiles(paths.Root, "*.db"))
        {
            if (File.Exists(Path.Combine(pending, Path.GetFileName(db)))) continue;
            foreach (var part in new[] { db, db + "-wal", db + "-shm" }) if (File.Exists(part)) File.Delete(part);
        }
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(pending, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(pending, f);
            if (rel == "backup.json") continue;
            // Tresor-Dateien gehören zu den Dateiinhalten (blobs/<Benutzer>/tresor), nicht ins Datenverzeichnis
            var to = Tresor.RestoreTarget(paths, rel) is { } blob ? blob : Path.GetFullPath(Path.Combine(paths.Root, rel));
            if (to.Length == 0 || (!to.StartsWith(paths.Root + Path.DirectorySeparatorChar) && !to.StartsWith(paths.Blobs + Path.DirectorySeparatorChar))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            // Reste der leeren Ersteinrichtung (WAL einer frischen chat.db) duerfen nicht ueber den Stand gelegt werden
            foreach (var side in new[] { to + "-wal", to + "-shm" }) if (File.Exists(side)) File.Delete(side);
            File.Move(f, to, overwrite: true);
            n++;
        }
        Directory.Delete(pending, recursive: true);
        File.WriteAllText(Path.Combine(paths.Root, S3Mirror.RestoreFlag), DateTime.UtcNow.ToString("o"));
        File.WriteAllText(Path.Combine(paths.Root, FilesBackup.RestoreFlag), DateTime.UtcNow.ToString("o"));
        File.WriteAllText(Path.Combine(paths.Root, ErasedUsers.CheckFlag), DateTime.UtcNow.ToString("o"));
        return n;
    }

    /// <summary>
    /// Wiederherstellen in ein leeres Datenverzeichnis: Stand laden, mit dem Hauptschluessel (oder dem vorigen)
    /// entschluesseln und auspacken. Danach muss der Server neu starten; die Dateiinhalte holt dann S3Mirror.
    /// </summary>
    public static async Task<Manifest> RestoreAsync(DataPaths paths, Vault vault, IBlobRemote remote, Stand stand, CancellationToken ct)
    {
        var work = Path.Combine(paths.Root, "restore-work");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        try
        {
            var raw = Path.Combine(work, "stand.dmb");
            Directory.CreateDirectory(work);
            await remote.DownloadAsync(stand.Key, raw, ct);
            var id = stand.Name[(stand.Name.IndexOf('_') + 1)..];
            Exception? last = null;
            foreach (var master in new[] { vault.Key, vault.Previous })
            {
                if (master == null) continue;
                var store = new BlobStore(Path.Combine(work, "in"), KeyOf(master));
                var target = Path.Combine(work, "stand");
                try
                {
                    // Erst ganz pruefen, dann auspacken - eine beschaedigte Datei hinterlaesst nichts im Datenverzeichnis
                    store.Adopt(id, raw);
                    Directory.CreateDirectory(target);
                    await using (var s = store.OpenRead(id)) await TarFile.ExtractToDirectoryAsync(s, target, overwriteFiles: false, ct);
                    var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(target, "backup.json"))) ?? throw new InvalidDataException("Die Sicherung hat keine Inhaltsangabe.");
                    // In den Wartebereich - an ihren Platz kommt der Stand beim Neustart, bevor der Server Dateien oeffnet
                    // (ApplyPending): offene Datenbanken liessen sich jetzt nicht sauber ersetzen
                    var pending = Path.Combine(paths.Root, PendingFolder);
                    if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
                    Directory.Move(target, pending);
                    return manifest;
                }
                catch (Exception e) when (e is CryptographicException or InvalidDataException) { last = e; if (!File.Exists(raw)) await remote.DownloadAsync(stand.Key, raw, ct); }
            }
            throw new InvalidOperationException(S3Remote.Unreadable, last);
        }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { } }
    }
}
