using System.Collections.Concurrent;

namespace DmOffice.Core;

/// <summary>
/// Register der Hintergrundarbeit je Benutzer: jeder Dienst meldet mit <see cref="Start"/>, woran er gerade arbeitet
/// (Fortschritt, Ende, Fehler); die Oberflaeche liest den Stand ueber GET /api/activity. Nur im Speicher - nach einem
/// Neustart faengt es leer an. Mehrere gleichzeitige Arbeiten derselben Art (Vorschaubilder) zaehlen zusammen.
/// </summary>
public static class Activity
{
    public const string MailSync = "mail-sync", MailQueue = "mail-queue", MailSlim = "mail-slim", MailTrash = "mail-trash",
        FilesBlobs = "files-blobs", FilesHashes = "files-hashes", FilesCleanup = "files-cleanup", TextIndex = "text-index", Thumbs = "thumbs",
        NotesSync = "notes-sync", Subscriptions = "subscriptions", Compact = "compact",
        Migration = "migration", Backup = "pre-update-backup";

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
