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

    public static List<Activity.Item> Build(UserSpace space, MailSync mail, ServerConfig? config = null, S3Mirror? mirror = null, S3Backup? backup = null, DbHealth? health = null, FilesBackup? files = null, DmOffice.Storage.ExternalIndexer? mountIndex = null)
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
        items.Add(WithPending(Base(user, Activity.FilesMeta), Cached(user, "meta", () => FileMeta.Pending(space.Db))));

        if (s.TextIndex.Enabled)
        {
            var queue = Cached(user, "text", () => Convert.ToInt64(space.Db.Scalar("SELECT COUNT(*) FROM text_queue") ?? 0L));
            var done = Cached(user, "textdone", () => Convert.ToInt64(space.Db.Scalar(
                "SELECT COUNT(*) FROM text_docs WHERE status <> 'error' AND (part IS NULL OR part >= 0)") ?? 0L));
            items.Add(WithPending(Base(user, Activity.TextIndex), queue, queue > 0 ? done : null, queue > 0 ? done + queue : null));
        }
        items.Add(Base(user, Activity.Thumbs));
        // Galerie-Ordner: nur, wenn welche eingerichtet sind; im Ruhezustand, wie viele und wie viele davon überwacht
        if (DmOffice.Gallery.GalleryFolders.Of(space.Db) is { Count: > 0 } sources)
        {
            var g = Base(user, Activity.GalleryScan);
            var watched = sources.Count(x => x.Watch);
            // Angefordert, aber noch nicht dran: als „wartet“ zeigen, und worauf (anderer eigener Ordner oder ein anderes Konto)
            var waiting = g.State == "running" ? [] : DmOffice.Gallery.GalleryScanJob.Waiting(user, sources);
            if (waiting.Count > 0)
            {
                var cur = DmOffice.Gallery.GalleryScanJob.Current;
                var on = cur == null ? "startet gleich" : cur.Value.User == user ? $"vorher noch {cur.Value.Path} ({cur.Value.Step})" : "vorher noch der Ordner eines anderen Kontos";
                items.Add(g with { State = "waiting", Detail = $"{string.Join(", ", waiting)} angefordert – {on}" });
            }
            else items.Add(g.State == "running" ? g : g with { Detail = $"{sources.Count} Ordner, {sources.Sum(x => x.Count):N0} Fotos und Videos" + (watched > 0 ? $", {watched} überwacht" : "") });
            if (sources.Any(x => x.Proxies)) items.Add(Base(user, Activity.GalleryProxies));
        }
        // Übertragung zwischen Ablage und externen Ordnern: nur, solange sie läuft oder ihr Ergebnis noch da ist
        if (Activity.Get(user, Activity.FilesTransfer) is { } transfer) items.Add(transfer);
        // Importprofile und Audiothek-Ordner: nur, wenn welche eingerichtet sind
        if (DmOffice.Gallery.ImportProfiles.Of(space.Db) is { Count: > 0 } profiles)
        {
            var g = Base(user, Activity.GalleryImport);
            items.Add(g.State == "running" ? g : g with { Detail = $"{profiles.Count} Importprofile" });
        }
        if (DmOffice.Audio.AudioFolders.Of(space.Db) is { Count: > 0 } audio)
        {
            var g = Base(user, Activity.AudioScan);
            items.Add(g.State == "running" ? g : g with { Detail = $"{audio.Count} Ordner, {audio.Sum(x => x.Count):N0} Titel" });
        }

        if (s.Notizi.Enabled)
        {
            var st = NotesSync.StateOf(space);
            var pending = NotesDb.Of(space).PendingCount();
            items.Add(new Activity.Item(Activity.NotesSync, st.Running ? "running" : st.Error != null ? "error" : pending > 0 ? "waiting" : "idle",
                null, null, pending > 0 ? pending : null, st.Report, null, st.Running ? null : st.LastRun, st.Error, 0));
        }

        // DMS: wartende Dokumente und überwachte Ordner, sobald das DMS benutzt wird (legt nichts an)
        if (Features.On(Features.Dms) && space.Db.Scalar("SELECT 1 FROM sqlite_master WHERE name = 'dms_docs'") != null)
        {
            var queued = Cached(user, "dms", () => DmOffice.Dms.Dms.QueuedCount(space.Db));
            var paused = DmOffice.Dms.DmsImport.Settings(space.Db).Paused;
            var dmsSources = DmOffice.Dms.DmsSources.Of(space.Db);
            var sourceError = dmsSources.Select(x => x.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e));
            var lastRun = dmsSources.Select(x => x.LastRun).Where(x => x != null).DefaultIfEmpty().Max();
            var dmsDetail = string.Join(" · ", new[] { paused ? "angehalten" : null, dmsSources.Count > 0 ? $"{dmsSources.Count} überwachte Ordner" : null }.Where(x => x != null));
            items.Add(new Activity.Item(Activity.Dms, sourceError != null ? "error" : queued > 0 ? "waiting" : "idle", null, null, queued > 0 ? queued : null,
                dmsDetail.Length > 0 ? dmsDetail : null, null, lastRun, sourceError, 0));
        }

        if (DmOffice.Pim.Subscriptions.All(space.Db).Count > 0) items.Add(Base(user, Activity.Subscriptions));
        if (DmOffice.Pim.Graph.All(space.Db).Count > 0) items.Add(Base(user, Activity.Graph));

        // Storage Extender und Core-Backup betreffen den ganzen Server: nur für die Verwaltung
        if (space.User.IsAdmin && config != null && mirror != null && backup != null)
        {
            var c = config.Current;
            var m = mirror.Status;
            if (c.S3Mirror || m.Running || m.LastRun != null)
            {
                // Zurückholen vom Ziel (nach Wiederherstellung, fehlende Inhalte) läuft im Hintergrund mit
                var r = m.Restore is { Running: true } rr ? (rr.Missing > 0 ? "holt zurück" : "prüft, was hier fehlt") : null;
                var fill = mirror.Prefill;
                var detail = r ?? (fill != null ? "füllt den lokalen Speicherpool auf" : m.Running ? (m.Uploaded > 0 ? $"{m.Uploaded:N0} hochgeladen" : "prüft, was fehlt")
                    : m.LastRun != null ? $"{m.Mirrored:N0} von {m.Local:N0} Inhalten am Ziel" + (m.Pending > 0 ? $", {m.Pending:N0} offen" : "") : null);
                // Waehrend des Abgleichs bzw. Zurueckholens: so viele von so vielen - die Anzeige macht daraus „x von y“ und einen Balken
                var (done, total) = m.Restore is { Running: true, Missing: > 0 } back ? ((long?)(back.Restored + back.NotThere + back.Failed), (long?)back.Missing)
                    : fill != null ? ((long?)fill.Done, (long?)fill.Total)
                    : m.Running && r == null && m.Local > 0 ? ((long?)Math.Min(m.Mirrored, m.Local), (long?)m.Local) : (null, null);
                items.Add(new Activity.Item(Activity.StorageSync, m.Running || r != null || fill != null ? "running" : m.LastError != null ? "error" : "idle",
                    done, total, null, string.Join(" · ", new[] { c.S3Mirror ? null : "ausgeschaltet", detail }.Where(x => x != null)) is { Length: > 0 } d ? d : null, null, m.Running ? null : m.LastRun, m.LastError, 0));
            }
            var b = backup.Status;
            if (c.S3Backup || b.Running || b.LastRun != null)
                items.Add(new Activity.Item(Activity.CoreBackup, b.Running ? "running" : b.LastError != null ? "error" : "idle",
                    null, null, null, b.Running ? "sichert" : c.S3Backup ? null : "ausgeschaltet", null, b.Running ? null : b.LastRun, b.LastError, 0));
            if (mountIndex != null && c.ExternalFolders.Any(x => x.Level != DmOffice.Storage.ExternalFolderDef.LevelNone))
            {
                var check = mountIndex.Checking;
                var read = mountIndex.Reading;
                items.Add(new Activity.Item(Activity.MountIndex, check != null || read != null ? "running" : "idle",
                    check == null ? read?.Done : null, check == null ? read?.Total : null, null,
                    check != null ? $"prüft „{check.Mount}“: {check.Done:N0} Einträge" : read != null ? $"wertet „{read.Mount}“ aus" : null, null,
                    check != null || read != null ? null : mountIndex.LastScan(c.ExternalFolders), null, 0));
            }
            if (files != null)
            {
                var f = files.Status;
                var back = files.Restore;
                if (BackupRules.FilesOn(c) || f.Running || f.LastRun != null || back?.Running == true)
                {
                    var running = f.Running || back?.Running == true;
                    var (fDone, fTotal) = back is { Running: true, Missing: > 0 } ? ((long?)(back.Restored + back.NotThere + back.Failed), (long?)back.Missing)
                        : f is { Running: true, Total: > 0 } ? ((long?)f.Done, (long?)f.Total) : (null, null);
                    var text = back?.Running == true ? "holt zurück"
                        : f.Running ? (f.Uploaded > 0 ? $"{f.Uploaded:N0} übertragen" : "prüft, was fehlt")
                        : !BackupRules.FilesOn(c) ? "ausgeschaltet"
                        : f.LastRun != null ? $"{f.Count:N0} Inhalte am Ziel" + (f.Pending > 0 ? $", {f.Pending:N0} offen" : "") : null;
                    items.Add(new Activity.Item(Activity.FilesBackup, running ? "running" : f.LastError != null ? "error" : "idle",
                        fDone, fTotal, null, text, null, running ? null : f.LastRun, f.LastError, 0));
                }
            }
        }
        // Datenbankprüfung: für die Verwaltung, mit Befund als Fehler (rot in der Aktivität)
        if (space.User.IsAdmin && health != null && (health.Running || health.Last != null))
        {
            var h = health.Last;
            var bad = h?.Items.Where(i => !i.Ok).ToList() ?? [];
            var fixedN = h?.Items.Count(i => i.Repaired) ?? 0;
            items.Add(new Activity.Item(Activity.DbHealth, health.Running ? "running" : bad.Count > 0 ? "error" : "idle", null, null, null,
                health.Running ? "prüft" : h == null ? null : $"{h.Items.Count} Datenbanken" + (fixedN > 0 ? $", {fixedN} Index neu aufgebaut" : "") + (bad.Count == 0 ? ", alles in Ordnung" : ""),
                null, health.Running ? null : h?.At, bad.Count > 0 ? "Beschädigt: " + string.Join(", ", bad.Select(i => i.Label)) + " – Verwaltung → Dateien → Datenbanken" : null, 0));
        }

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
        app.MapGet("/api/activity", (HttpContext http, MailSync mail, ServerConfig config, S3Mirror mirror, S3Backup backup, DbHealth health, FilesBackup files, DmOffice.Storage.ExternalIndexer mountIndex) =>
        {
            var items = Build(http.Space(), mail, config, mirror, backup, health, files, mountIndex);
            return Results.Ok(new { running = items.Count(i => i.State == "running"), items });
        }).RequireAuthorization();
    }
}
