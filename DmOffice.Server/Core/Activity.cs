using System.Collections.Concurrent;
using System.Text.Json;

namespace DmOffice.Core;

/// <summary>
/// Register der Hintergrundarbeit je Benutzer: jeder Dienst meldet mit <see cref="Start"/>, woran er gerade arbeitet
/// (Fortschritt, Ende, Fehler); die Oberflaeche liest den Stand ueber GET /api/activity. Wann eine Arbeit zuletzt fertig
/// wurde (und mit welchem Fehler), ueberlebt einen Neustart (<see cref="Init"/>, Datei activity.json im Datenverzeichnis);
/// sonst stuende nach jedem Neustart alles auf „noch nicht gelaufen“. Mehrere gleichzeitige Arbeiten derselben Art
/// (Vorschaubilder) zaehlen zusammen.
/// </summary>
public static class Activity
{
    public const string MailSync = "mail-sync", MailClassify = "mail-classify", MailQueue = "mail-queue", MailSlim = "mail-slim", MailTrash = "mail-trash",
        FilesBlobs = "files-blobs", FilesHashes = "files-hashes", FilesCleanup = "files-cleanup", TextIndex = "text-index", Thumbs = "thumbs",
        NotesSync = "notes-sync", Subscriptions = "subscriptions", Compact = "compact",
        StorageSync = "storage-sync", CoreBackup = "core-backup", FilesBackup = "files-backup", MountIndex = "mount-index", FilesMeta = "files-meta", GalleryScan = "gallery-scan", GalleryImport = "gallery-import", AudioScan = "audio-scan", GalleryProxies = "gallery-proxies", PhotoWrite = "photo-write", PhotoAuto = "photo-auto",
        Migration = "migration", Graph = "graph", Backup = "pre-update-backup", DbHealth = "db-health", FilesTransfer = "files-transfer", Dms = "dms";

    /// <summary>Eine Zeile der Anzeige. State: running, waiting (liegt an, laeuft gerade nicht), error, idle.</summary>
    public sealed record Item(string Id, string State, long? Done, long? Total, long? Pending, string? Detail,
        DateTime? Started, DateTime? Finished, string? Error, long Completed);

    internal sealed class Entry
    {
        public int Active, Failed;
        public long Completed, Done;
        public long? Total;
        public string? Detail, Error;
        public DateTime? Started, Finished;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static string Key(string user, string id) => user + "|" + id;

    // ---------------- Über einen Neustart hinweg ----------------

    private sealed record Saved(DateTime? Finished, string? Error, long Completed);
    private static string? _file;
    private static Timer? _timer;

    /// <summary>Beim Start: letzte Ergebnisse laden; danach wird nach jedem Ende (gebündelt) gespeichert.</summary>
    public static void Init(string dataDir)
    {
        _file = Path.Combine(dataDir, "activity.json");
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(_file)) is { } all)
                foreach (var (k, v) in all)
                    Entries.TryAdd(k, new Entry { Finished = v.Finished is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null, Error = v.Error, Completed = v.Completed });
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        _timer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static void Changed() => _timer?.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

    private static void Save()
    {
        if (_file == null) return;
        var all = new Dictionary<string, Saved>();
        foreach (var (k, e) in Entries)
            lock (e) if (e.Finished != null || e.Error != null) all[k] = new Saved(e.Finished, e.Error, e.Completed);
        try { File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(all)); File.Move(_file + ".tmp", _file, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Eine Arbeit beginnt. Mit "using" wird sie am Ende als erledigt gemeldet, falls nicht <see cref="Job.Fail"/> kam.</summary>
    public static Job Start(string user, string id, string? detail = null, long? total = null)
    {
        var e = Entries.GetOrAdd(Key(user, id), _ => new Entry());
        lock (e)
        {
            if (e.Active++ == 0) { e.Started = DateTime.UtcNow; e.Done = 0; e.Failed = 0; }
            e.Total = total;
            e.Detail = detail;
        }
        return new Job(e);
    }

    public sealed class Job : IDisposable
    {
        private readonly Entry _e;
        private bool _ended;
        internal Job(Entry e) => _e = e;

        public void Progress(long done, long? total = null, string? detail = null)
        {
            lock (_e)
            {
                _e.Done = done;
                if (total != null) _e.Total = total;
                if (detail != null) _e.Detail = detail;
            }
        }

        public void Fail(string error)
        {
            lock (_e) { _e.Error = error; _e.Failed++; }
            Dispose();
        }

        public void Dispose()
        {
            lock (_e)
            {
                if (_ended) return;
                _ended = true;
                _e.Completed++;
                if (--_e.Active == 0)
                {
                    _e.Finished = DateTime.UtcNow;
                    _e.Detail = null;
                    if (_e.Failed == 0) _e.Error = null;
                    Changed();
                }
            }
        }
    }

    /// <summary>Stand einer Sorte Arbeit; null, wenn sie noch nie gemeldet hat.</summary>
    public static Item? Get(string user, string id)
    {
        if (!Entries.TryGetValue(Key(user, id), out var e)) return null;
        lock (e)
            return new Item(id, e.Active > 0 ? "running" : e.Error != null ? "error" : "idle", e.Active > 0 ? e.Done : null,
                e.Total, null, e.Detail, e.Started, e.Active > 0 ? null : e.Finished, e.Error, e.Completed);
    }

    /// <summary>Nur fuer Tests: alles vergessen.</summary>
    public static void Reset() => Entries.Clear();
}
