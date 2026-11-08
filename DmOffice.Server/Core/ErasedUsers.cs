using System.Text.Json;

namespace DmOffice.Core;

/// <summary>
/// Gelöschte Konten: Der Server ist beim Löschen sofort leer, dieser Teil räumt die Ziele auf. Der Ordner des Kontos verschwindet im
/// Storage Extender und im Files-Backup direkt danach, auch wenn ein Ziel erst später erreichbar ist. Gelöscht wird nur der Ordner dieses
/// Kontos, die Inhalte anderer Benutzer werden nie berührt. Core-Backups bleiben als Ganzes bestehen; ein Vermerk am Ziel sorgt dafür, dass
/// ein eingespieltes älteres Core-Backup das Konto nicht zurückbringt.
/// </summary>
public static class ErasedUsers
{
    private const string FileName = "erased-users.json";
    /// <summary>Beim Einspielen eines Core-Backups gesetzt: einmal die Vermerke am Ziel prüfen.</summary>
    public const string CheckFlag = "check-erased-users.flag";
    public const string MarkFolder = "dmoffice-erased/";
    private static readonly object Gate = new();

    public sealed record Target(IBlobRemote Remote, string? Prefix);

    private static HashSet<string> Load(DataPaths paths)
    {
        var file = Path.Combine(paths.Root, FileName);
        if (!File.Exists(file)) return new(StringComparer.Ordinal);
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(file)) ?? new(StringComparer.Ordinal); }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private static void Save(DataPaths paths, HashSet<string> list)
    {
        var file = Path.Combine(paths.Root, FileName);
        if (list.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list));
        File.Move(temp, file, overwrite: true);
    }

    private static bool Valid(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    /// <summary>Konto wurde gelöscht: vormerken.</summary>
    public static void Note(DataPaths paths, string id)
    {
        lock (Gate)
        {
            var list = Load(paths);
            if (list.Add(id)) Save(paths, list);
        }
    }

    internal static List<string> Open(DataPaths paths)
    {
        lock (Gate) return Load(paths).ToList();
    }

    /// <summary>
    /// Ein Durchgang: Vermerk schreiben, dann den Ordner des Kontos löschen. Erledigt ist ein Konto erst, wenn ein Durchgang dort nichts
    /// mehr findet: Ein gerade laufender Abgleich könnte noch etwas nachgeschrieben haben. Kein Ziel erreichbar: beim nächsten Mal.
    /// </summary>
    internal static async Task<string?> PassAsync(DataPaths paths, IReadOnlyList<Target> folders, IReadOnlyList<Target> marks, CancellationToken ct)
    {
        string? error = null;
        foreach (var id in Open(paths))
        {
            ct.ThrowIfCancellationRequested();
            var done = true;
            if (!Valid(id)) { Finish(paths, id); continue; }
            foreach (var mark in marks)
                try { await WriteMarkAsync(mark, id, ct); }
                catch (Exception e) when (e is not OperationCanceledException) { error ??= S3Remote.Describe(e); done = false; }
            foreach (var target in folders)
                try
                {
                    // Der Schrägstrich gehört dazu: nur genau dieser Ordner
                    foreach (var key in await target.Remote.ListAsync(S3Mirror.Prefix(target.Prefix) + id + "/", ct))
                    {
                        await target.Remote.DeleteAsync(key, ct);
                        done = false;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException) { error ??= S3Remote.Describe(e); done = false; }
            if (done) Finish(paths, id);
        }
        return error;
    }

    private static void Finish(DataPaths paths, string id)
    {
        lock (Gate)
        {
            var list = Load(paths);
            if (list.Remove(id)) Save(paths, list);
        }
    }

    private static async Task WriteMarkAsync(Target target, string id, CancellationToken ct)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, DateTime.UtcNow.ToString("o"));
            await target.Remote.UploadAsync(file, S3Mirror.Prefix(target.Prefix) + MarkFolder + id, ct);
        }
        finally { File.Delete(file); }
    }

    /// <summary>
    /// Nach dem Einspielen eines Core-Backups, einmal: Konten mit Vermerk wieder entfernen, alle anderen bleiben unberührt.
    /// Ist ein Ziel nicht erreichbar, bleibt die Marke für den nächsten Versuch stehen.
    /// </summary>
    internal static async Task AfterRestoreAsync(DataPaths paths, IReadOnlyList<Target> marks, IReadOnlyCollection<string> existing, Action<string> erase, CancellationToken ct)
    {
        var flag = Path.Combine(paths.Root, CheckFlag);
        if (!File.Exists(flag)) return;
        var erased = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mark in marks)
        {
            var folder = S3Mirror.Prefix(mark.Prefix) + MarkFolder;
            foreach (var key in await mark.Remote.ListAsync(folder, ct)) erased.Add(key[folder.Length..]);
        }
        foreach (var id in existing.Where(erased.Contains).ToList()) erase(id);
        File.Delete(flag);
    }
}

/// <summary>Räumt die Ziele gelöschter Konten auf: sofort nach dem Löschen, dann alle paar Minuten, bis alles weg ist.</summary>
public sealed class ErasedUsersJob(IServiceProvider services, ServerConfig config, DataPaths paths, UserStore users, ILogger<ErasedUsersJob> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _kick = new(0);

    public void Kick() => _kick.Release();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Gelöschte Konten am Ziel: {Error}", S3Remote.Describe(e)); }
            try { await _kick.WaitAsync(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var flag = File.Exists(Path.Combine(paths.Root, ErasedUsers.CheckFlag));
        if (!flag && ErasedUsers.Open(paths).Count == 0) return;
        var s = config.Current;
        var open = new List<IBlobRemote>();
        ErasedUsers.Target Use(IBlobRemote remote, string? prefix) { open.Add(remote); return new(remote, prefix); }
        try
        {
            var folders = new List<ErasedUsers.Target>();
            var marks = new List<ErasedUsers.Target>();
            if (S3Mirror.Configured(s, config.S3SecretKey()))
            {
                var extender = Use(S3Mirror.RemoteOf(config), s.S3Prefix);
                folders.Add(extender);
                folders.Add(new(extender.Remote, S3Mirror.Prefix(s.S3Prefix) + FilesBackup.OwnFolder));
                marks.Add(extender);
            }
            if (s.FilesBackupOwnTarget && FilesBackup.Configured(config)) folders.Add(Use(FilesBackup.RemoteOf(config), s.FilesBackupS3Prefix));
            if (s.BackupOwnTarget && S3Backup.TargetConfigured(config)) marks.Add(Use(S3Backup.RemoteOf(config), S3Backup.PrefixOf(s)));
            if (flag)
                await ErasedUsers.AfterRestoreAsync(paths, marks, users.All().Select(u => u.Id).ToList(), id =>
                {
                    log.LogWarning("Konto {Id} wurde nach diesem Core-Backup gelöscht - wird wieder entfernt", id);
                    AdminApi.Erase(services, id);
                }, ct);
            var error = await ErasedUsers.PassAsync(paths, folders, marks, ct);
            if (error != null) log.LogWarning("Gelöschte Konten am Ziel: {Error} - nächster Versuch in wenigen Minuten", error);
        }
        finally { foreach (var remote in open) remote.Dispose(); }
    }
}
