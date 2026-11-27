using System.Collections.Concurrent;

namespace DmOffice.Core;

/// <summary>Ein früheres Ziel (Storage Extender, Core-Backup, Files-Backup) mit seinen Zugangsdaten - eingepackt wie die aktuellen.</summary>
public sealed class FormerTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>storage, backup, files</summary>
    public string Purpose { get; set; } = "storage";
    public string Target { get; set; } = "s3";
    public string? Endpoint { get; set; }
    public string? Region { get; set; }
    public string? Bucket { get; set; }
    public string? Prefix { get; set; }
    public string? AccessKey { get; set; }
    public string? Secret { get; set; }
    public string? WebDavUrl { get; set; }
    public string? WebDavUser { get; set; }
    public string? WebDavPassword { get; set; }
    public string? Folder { get; set; }
    public DateTime Replaced { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Alte Daten am bisherigen Ziel: Wechselt der Admin den Ort von Storage Extender, eigenem Core-Backup- oder Files-Backup-Ziel,
/// merkt sich dmOffice den alten Ort samt Zugangsdaten. Unter Verwaltung › Speicher lässt sich dann löschen, was dmOffice dort
/// abgelegt hat (nur unter seinem Präfix bzw. Ordner), oder das Ziel vergessen (Daten bleiben). Gelöscht wird nie, solange ein
/// aktives Ziel denselben Ort nutzt oder sich die Präfixe überschneiden.
/// </summary>
public static class FormerTargets
{
    /// <summary>Ort ohne Präfix (Art, Adresse, Bucket bzw. Ordner) und Präfix mit „/“ am Ende (leer: ganzes Ziel).</summary>
    public sealed record Place(string Base, string Prefix);

    private static string N(string? s) => (s ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    public static Place PlaceOf(FormerTarget f) => new(f.Target switch
    {
        "webdav" => "webdav|" + N(f.WebDavUrl),
        "folder" => "folder|" + N(f.Folder),
        _ => "s3|" + N(f.Endpoint) + "|" + N(f.Bucket),
    }, S3Mirror.Prefix(f.Prefix));

    /// <summary>Das eingestellte Ziel für einen Zweck, null wenn keins (oder es das des Storage Extenders mitnutzt).</summary>
    public static FormerTarget? Snapshot(string purpose, ServerSettings s)
    {
        var f = purpose switch
        {
            "backup" when s.BackupOwnTarget => new FormerTarget
            {
                Target = s.BackupTarget ?? "s3", Endpoint = s.BackupS3Endpoint, Region = s.BackupS3Region, Bucket = s.BackupS3Bucket, Prefix = s.BackupS3Prefix,
                AccessKey = s.BackupS3AccessKey, Secret = s.BackupS3SecretKey, WebDavUrl = s.BackupWebDavUrl, WebDavUser = s.BackupWebDavUser,
                WebDavPassword = s.BackupWebDavPassword, Folder = s.BackupFolder,
            },
            "files" when s.FilesBackupOwnTarget => new FormerTarget
            {
                Target = s.FilesBackupTarget ?? "s3", Endpoint = s.FilesBackupS3Endpoint, Region = s.FilesBackupS3Region, Bucket = s.FilesBackupS3Bucket, Prefix = s.FilesBackupS3Prefix,
                AccessKey = s.FilesBackupS3AccessKey, Secret = s.FilesBackupS3SecretKey, WebDavUrl = s.FilesBackupWebDavUrl, WebDavUser = s.FilesBackupWebDavUser,
                WebDavPassword = s.FilesBackupWebDavPassword, Folder = s.FilesBackupFolder,
            },
            "storage" => new FormerTarget
            {
                Target = s.StorageTarget ?? "s3", Endpoint = s.S3Endpoint, Region = s.S3Region, Bucket = s.S3Bucket, Prefix = s.S3Prefix,
                AccessKey = s.S3AccessKey, Secret = s.S3SecretKey, WebDavUrl = s.StorageWebDavUrl, WebDavUser = s.StorageWebDavUser,
                WebDavPassword = s.StorageWebDavPassword, Folder = s.StorageFolder,
            },
            _ => null,
        };
        if (f == null) return null;
        f.Purpose = purpose;
        var empty = f.Target switch
        {
            "webdav" => string.IsNullOrWhiteSpace(f.WebDavUrl),
            "folder" => string.IsNullOrWhiteSpace(f.Folder),
            _ => string.IsNullOrWhiteSpace(f.Endpoint) || string.IsNullOrWhiteSpace(f.Bucket),
        };
        return empty ? null : f;
    }

    /// <summary>Orte, die gerade in Gebrauch sind (alle Zwecke).</summary>
    public static List<Place> InUse(ServerSettings s) =>
        new[] { "storage", "backup", "files" }.Select(p => Snapshot(p, s)).OfType<FormerTarget>().Select(PlaceOf).ToList();

    /// <summary>Überschneiden sich zwei Orte (gleiche Basis und ein Präfix liegt im anderen)?</summary>
    public static bool Overlap(Place a, Place b) =>
        a.Base == b.Base && (a.Prefix.StartsWith(b.Prefix, StringComparison.Ordinal) || b.Prefix.StartsWith(a.Prefix, StringComparison.Ordinal));

    public static bool StillUsed(FormerTarget f, ServerSettings s) => InUse(s).Any(p => Overlap(p, PlaceOf(f)));

    /// <summary>
    /// Nach dem Speichern eines Ziels: Hat sich der Ort geändert, das alte Ziel merken (einmal je Ort und Präfix) - aber nur, wenn dort
    /// etwas liegen kann: Der Zweck war eingeschaltet, oder ein kurzes Nachzählen (S3, WebDAV) findet Dateien. Ein nie erreichtes Ziel ist kein Altbestand.
    /// </summary>
    public static void Track(ServerConfig config, string purpose, ServerSettings before)
    {
        if (Snapshot(purpose, before) is not { } old) return;
        var now = Snapshot(purpose, config.Current);
        if (now != null && PlaceOf(now) == PlaceOf(old)) return;
        var wasActive = purpose switch { "storage" => before.S3Mirror, "backup" => before.S3Backup, "files" => before.FilesBackup, _ => true };
        if (wasActive) { Remember(config, old); return; }
        // Ein lokaler Ordner, der nie in Gebrauch war, enthält nichts von dmOffice (Nachzählen fände dort fremde Dateien)
        if (old.Target == "folder") return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var remote = Remote(config, old);
                var list = await remote.ListSizesAsync(S3Mirror.Prefix(old.Prefix), cts.Token);
                if (list.Count > 0) Remember(config, old);
            }
            catch { /* nicht erreichbar: dort hat dmOffice nichts abgelegt */ }
        });
    }

    private static void Remember(ServerConfig config, FormerTarget old) => config.Update(s =>
    {
        s.FormerTargets.RemoveAll(x => PlaceOf(x) == PlaceOf(old));
        s.FormerTargets.Add(old);
    });

    public static IBlobRemote Remote(ServerConfig config, FormerTarget f) =>
        S3Mirror.RemoteOf(f.Target, f.Endpoint, f.Region ?? "", f.Bucket, f.AccessKey, config.Unwrap(f.Secret), f.WebDavUrl, f.WebDavUser, config.Unwrap(f.WebDavPassword), f.Folder);

    /// <summary>Wo, für die Anzeige (ohne Zugangsdaten).</summary>
    public static string Describe(FormerTarget f) => f.Target switch
    {
        "webdav" => (f.WebDavUrl ?? "").Trim(),
        "folder" => (f.Folder ?? "").Trim(),
        _ => $"{(f.Endpoint ?? "").Trim().TrimEnd('/')} · Bucket {f.Bucket}",
    } + (string.IsNullOrWhiteSpace(f.Prefix) ? "" : " · " + f.Prefix!.Trim('/'));

    // ---------------- Zählen und Löschen ----------------

    public sealed record Run(bool Busy, long Done, long Total, string? Error, long? Count, long? Bytes);
    private static readonly ConcurrentDictionary<string, Run> Runs = new();
    public static Run? RunOf(string id) => Runs.TryGetValue(id, out var r) ? r : null;

    /// <summary>Wie viel liegt dort (unter dem Präfix)?</summary>
    public static async Task<(long Count, long Bytes)> CountAsync(ServerConfig config, FormerTarget f, CancellationToken ct)
    {
        using var remote = Remote(config, f);
        var list = await remote.ListSizesAsync(S3Mirror.Prefix(f.Prefix), ct);
        Runs[f.Id] = new Run(false, 0, list.Count, null, list.Count, list.Sum(x => x.Size));
        return (list.Count, list.Sum(x => x.Size));
    }

    /// <summary>Im Hintergrund löschen; danach vergisst dmOffice das Ziel. Fortschritt unter „Aktivität“ der Person, die es angestoßen hat.</summary>
    public static bool StartDelete(ServerConfig config, FormerTarget f, string userId, ILogger log)
    {
        if (StillUsed(f, config.Current)) throw new InvalidOperationException("Dieser Ort wird gerade wieder genutzt – dort wird nichts gelöscht.");
        if (RunOf(f.Id) is { Busy: true }) return false;
        Runs[f.Id] = new Run(true, 0, 0, null, null, null);
        _ = Task.Run(async () =>
        {
            using var job = Activity.Start(userId, Activity.StorageCleanup, Describe(f));
            try
            {
                using var remote = Remote(config, f);
                var prefix = S3Mirror.Prefix(f.Prefix);
                var keys = (await remote.ListSizesAsync(prefix, CancellationToken.None)).Select(x => x.Key).ToList();
                long done = 0;
                Runs[f.Id] = new Run(true, 0, keys.Count, null, keys.Count, null);
                job.Progress(0, keys.Count);
                var progress = new SyncProgress(n =>
                {
                    var d = Interlocked.Add(ref done, n);
                    Runs[f.Id] = new Run(true, d, keys.Count, null, keys.Count, null);
                    job.Progress(d, keys.Count);
                });
                // Nochmals prüfen: inzwischen könnte der Ort wieder eingestellt worden sein
                if (StillUsed(f, config.Current)) throw new InvalidOperationException("Dieser Ort wird inzwischen wieder genutzt – nichts gelöscht.");
                await remote.DeleteManyAsync(prefix, keys, progress, CancellationToken.None);
                config.Update(s => s.FormerTargets.RemoveAll(x => x.Id == f.Id));
                Runs[f.Id] = new Run(false, keys.Count, keys.Count, null, 0, 0);
                log.LogInformation("Altes Ziel geleert: {Where}, {Count} Objekte", Describe(f), keys.Count);
            }
            catch (Exception e)
            {
                var msg = e is InvalidOperationException ? e.Message : S3Remote.Describe(e);
                Runs[f.Id] = (RunOf(f.Id) ?? new Run(false, 0, 0, null, null, null)) with { Busy = false, Error = msg };
                job.Fail(msg);
                log.LogWarning("Altes Ziel leeren: {Error}", msg);
            }
        });
        return true;
    }

    /// <summary>IProgress ohne SynchronizationContext: meldet sofort im aufrufenden Thread.</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
