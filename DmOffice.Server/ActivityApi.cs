using System.Collections.Concurrent;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Mail;
using DmOffice.Notes;

namespace DmOffice;

/// <summary>GET /api/activity: was der Server fuer diesen Benutzer gerade im Hintergrund tut und was zuletzt erledigt wurde.
/// Die Dienste melden ueber <see cref="Activity"/>; wo es schon Zaehler gibt (Statistik, Mail-Zustand), kommen die her.</summary>
public static class ActivityApi
{
    private static readonly ConcurrentDictionary<string, (DateTime At, long Value)> Counts = new();

    /// <summary>Zaehler, die auf grossen Datenbanken kosten, hoechstens alle 15 s neu zaehlen.</summary>
    private static long Cached(string user, string name, Func<long> count)
    {
        var key = user + "|" + name;
        if (Counts.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(15)) return c.Value;
        var v = count();
        Counts[key] = (DateTime.UtcNow, v);
        return v;
    }

    private static Activity.Item Base(string user, string id) =>
        Activity.Get(user, id) ?? new Activity.Item(id, "idle", null, null, null, null, null, null, null, 0);

    /// <summary>Noch Anliegendes: laeuft nichts, aber es gibt zu tun, heisst der Zustand "waiting".</summary>
    private static Activity.Item WithPending(Activity.Item i, long pending, long? done = null, long? total = null) =>
        i with
        {
            Pending = pending > 0 ? pending : null,
            Done = done ?? i.Done,
            Total = total ?? i.Total,
            State = i.State == "idle" && pending > 0 ? "waiting" : i.State,
        };

    public static List<Activity.Item> Build(UserSpace space, MailSync mail)
    {
        var user = space.User.Id;
        var s = space.Settings;
        var items = new List<Activity.Item>();

        var accounts = s.MailAccounts.Where(a => !string.IsNullOrEmpty(a.ImapHost)).ToList();
        if (accounts.Count > 0)
        {
            var states = accounts.Select(a => mail.StateOf(user, a.Id)).ToList();
            var running = states.FirstOrDefault(x => x.Running);
            var last = states.Select(x => x.LastQuick > x.LastFull ? x.LastQuick : x.LastFull).Max();
            var error = states.Select(x => x.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e));
            var pending = states.Sum(x => (long)x.PendingBodies);
            items.Add(new Activity.Item(Activity.MailSync, running != null ? "running" : error != null ? "error" : pending > 0 ? "waiting" : "idle",
                null, null, pending > 0 ? pending : null, running?.Phase, null, running != null ? null : last, error, 0));
            items.Add(WithPending(Base(user, Activity.MailQueue), MailQueue.Count(space.Db)));
        }
        if (s.MailAccounts.Any(a => a.Slim)) items.Add(Base(user, Activity.MailSlim));
        if (s.MailAccounts.Any(a => a.TrashDays > 0 || a.JunkDays > 0)) items.Add(Base(user, Activity.MailTrash));

        items.Add(WithPending(Base(user, Activity.FilesBlobs), Cached(user, "blobs", () => FileStore.PendingBlobs(space.Db))));
        items.Add(WithPending(Base(user, Activity.FilesHashes), Cached(user, "hashes", () => FileHashes.Pending(space.Db))));
        items.Add(Base(user, Activity.FilesCleanup));

        if (s.TextIndex.Enabled)
        {
            var queue = Cached(user, "text", () => Convert.ToInt64(space.Db.Scalar("SELECT COUNT(*) FROM text_queue") ?? 0L));
            var done = Cached(user, "textdone", () => Convert.ToInt64(space.Db.Scalar(
                "SELECT COUNT(*) FROM text_docs WHERE status <> 'error' AND (part IS NULL OR part >= 0)") ?? 0L));
            items.Add(WithPending(Base(user, Activity.TextIndex), queue, queue > 0 ? done : null, queue > 0 ? done + queue : null));
        }
        items.Add(Base(user, Activity.Thumbs));

        if (s.Notizi.Enabled)
        {
            var st = NotesSync.StateOf(space);
            var pending = NotesDb.Of(space).PendingCount();
            items.Add(new Activity.Item(Activity.NotesSync, st.Running ? "running" : st.Error != null ? "error" : pending > 0 ? "waiting" : "idle",
                null, null, pending > 0 ? pending : null, st.Report, null, st.Running ? null : st.LastRun, st.Error, 0));
        }

        if (DmOffice.Pim.Subscriptions.All(space.Db).Count > 0) items.Add(Base(user, Activity.Subscriptions));

        var compact = SettingsApi.CompactOf(user);
        items.Add(compact is { Started: not null }
            ? new Activity.Item(Activity.Compact, compact.Running ? "running" : compact.Error != null ? "error" : "idle",
                null, null, null, null, compact.Started,
                compact.Running ? null : compact.Finished, compact.Error, 0)
            : Base(user, Activity.Compact));
        return items;
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/activity", (HttpContext http, MailSync mail) =>
        {
            var items = Build(http.Space(), mail);
            return Results.Ok(new { running = items.Count(i => i.State == "running"), items });
        }).RequireAuthorization();
    }
}
