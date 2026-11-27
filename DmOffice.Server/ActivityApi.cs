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

        var accounts = s.ActiveMailAccounts().Where(a => !string.IsNullOrEmpty(a.ImapHost)).ToList();
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
        if (s.ActiveMailAccounts().Any(a => a.TrashDays > 0 || a.JunkDays > 0)) items.Add(Base(user, Activity.MailTrash));

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
            // Noch nicht ausgelesene Fotos, aber der Job arbeitet gerade für ein anderes Konto (er liest einen Ordner nach dem anderen):
            // als „wartet“ zeigen - sonst steht hier „ruhig“, während in der Galerie leere Kacheln stehen
            else if (g.State != "running" && sources.Where(x => x.Watch || x.Draining).Sum(x => { var st = DmOffice.Gallery.GalleryFolders.Stages(space.Db, x.Id); return st.Listed + st.NoThumb; }) is var open and > 0)
            {
                var cur = DmOffice.Gallery.GalleryScanJob.Current;
                var on = cur == null ? "geht gleich weiter" : cur.Value.User == user ? $"gerade {cur.Value.Path} ({cur.Value.Step})" : "vorher noch der Ordner eines anderen Kontos";
                items.Add(g with { State = "waiting", Detail = $"{DmOffice.Core.Num.N(open)} Fotos noch nicht ausgelesen – {on}" });
            }
            else items.Add(g.State == "running" ? g : g with { Detail = $"{sources.Count} Ordner, {DmOffice.Core.Num.N(sources.Sum(x => x.Count))} Fotos und Videos" + (watched > 0 ? $", {watched} überwacht" : "") });
            if (sources.Any(x => x.Proxies)) items.Add(Base(user, Activity.GalleryProxies));
            if (Features.On(Features.GalleryMedium) && sources.Any(x => !x.Proxies)) items.Add(Base(user, Activity.GalleryMedium)); // [feature:galmedium]
        }
        // Abgleich nach dem Neuzuordnen eines Ordners: nur, solange er läuft oder sein Ergebnis noch da ist
        if (Activity.Get(user, Activity.GalleryRelink) is { } galleryRelink) items.Add(galleryRelink);
        if (Activity.Get(user, Activity.AudioRelink) is { } audioRelink) items.Add(audioRelink);
        // Übertragung zwischen Ablage und externen Ordnern: nur, solange sie läuft oder ihr Ergebnis noch da ist
        if (Activity.Get(user, Activity.FilesTransfer) is { } transfer) items.Add(transfer);
        if (Activity.Get(user, Activity.GallerySave) is { } saving) items.Add(saving);
        if (Activity.Get(user, Activity.PhotoExport) is { } exporting) items.Add(exporting); // [feature:photoexport]
        if (Features.On(Features.LensCorr) && Activity.Get(user, Activity.LensApply) is { } lensApply) items.Add(lensApply); // [feature:lenscorr]
        if (Features.On(Features.LensLib) && Activity.Get(user, Activity.LensLib) is { } lensLib) items.Add(lensLib); // [feature:lenslib]
        // Importprofile und Audiothek-Ordner: nur, wenn welche eingerichtet sind
        // Importprofile: Stand aus den Profilen selbst (wie bei den Kalender-Abos) - Fehler je aktivem Profil mit Namen, nicht nur der
        // letzte Lauf; ein erfolgreicher Lauf oder ein ausgeschaltetes Profil hinterlässt keinen alten Fehler
        if (DmOffice.Gallery.ImportProfiles.Of(space.Db) is { Count: > 0 } profiles)
        {
            var g = Base(user, Activity.GalleryImport);
            var failed = profiles.Where(x => x.Active && !string.IsNullOrEmpty(x.Error)).ToList();
            var error = failed.Count == 0 ? null : string.Join(" · ", failed.Select(x => $"{x.Name}: {x.Error}"));
            var last = profiles.Select(x => x.LastRun).Where(x => x != null).DefaultIfEmpty().Max();
            items.Add(g.State == "running" ? g
                : new Activity.Item(Activity.GalleryImport, error != null ? "error" : "idle", null, null, null, $"{profiles.Count} Importprofile", null, last ?? g.Finished, error, g.Completed));
        }
        if (DmOffice.Audio.AudioFolders.Of(space.Db) is { Count: > 0 } audio)
        {
            var g = Base(user, Activity.AudioScan);
            items.Add(g.State == "running" ? g : g with { Detail = $"{audio.Count} Ordner, {DmOffice.Core.Num.N(audio.Sum(x => x.Count))} Titel" });
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

        // Kalender-Abos: Stand aus den Abos selbst (zuletzt abgerufen, Fehler je Abo mit Namen) - auch ein Abruf beim Eintragen oder
        // „Aktualisieren“ zählt, und ein gelöschtes oder repariertes Abo hinterlässt keinen alten Fehler
        if (DmOffice.Pim.Subscriptions.All(space.Db) is { Count: > 0 } subs)
        {
            var running = Base(user, Activity.Subscriptions);
            var names = DmOffice.Pim.PimStore.Collections(space.Db, DmOffice.Pim.PimStore.Calendar).ToDictionary(c => c.Id, c => c.Name);
            var failed = subs.Where(x => !string.IsNullOrEmpty(x.LastError)).ToList();
            var error = failed.Count == 0 ? null : string.Join(" · ", failed.Select(x => $"{(names.TryGetValue(x.Collection, out var n) ? n : "?")}: {x.LastError}"));
            var last = subs.Select(x => x.LastFetch).Where(x => x != null).DefaultIfEmpty().Max();
            items.Add(running.State == "running" ? running
                : new Activity.Item(Activity.Subscriptions, error != null ? "error" : "idle", null, null, null, $"{subs.Count} Abos", null, last, error, running.Completed));
        }
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
                var detail = r ?? (fill != null ? "füllt den lokalen Speicherpool auf" : m.Running ? (m.Uploaded > 0 ? $"{DmOffice.Core.Num.N(m.Uploaded)} hochgeladen" : "prüft, was fehlt")
                    : m.LastRun != null ? $"{DmOffice.Core.Num.N(m.Mirrored)} von {DmOffice.Core.Num.N(m.Local)} Inhalten am Ziel" + (m.Pending > 0 ? $", {DmOffice.Core.Num.N(m.Pending)} offen" : "") : null);
                // Waehrend des Abgleichs bzw. Zurueckholens: so viele von so vielen - die Anzeige macht daraus „x von y“ und einen Balken
                var (done, total) = m.Restore is { Running: true, Missing: > 0 } back ? ((long?)(back.Restored + back.NotThere + back.Failed), (long?)back.Missing)
                    : fill != null ? ((long?)fill.Done, (long?)fill.Total)
                    : m.Running && r == null && m.Local > 0 ? ((long?)Math.Min(m.Mirrored, m.Local), (long?)m.Local) : (null, null);
                // Ausgeschaltet: kein alter Fehler mehr (er stammt aus der Zeit davor)
                items.Add(new Activity.Item(Activity.StorageSync, m.Running || r != null || fill != null ? "running" : c.S3Mirror && m.LastError != null ? "error" : "idle",
                    done, total, null, string.Join(" · ", new[] { c.S3Mirror ? null : "ausgeschaltet", detail }.Where(x => x != null)) is { Length: > 0 } d ? d : null, null, m.Running ? null : m.LastRun, c.S3Mirror ? m.LastError : null, 0));
            }
            var b = backup.Status;
            if (c.S3Backup || b.Running || b.LastRun != null)
                items.Add(new Activity.Item(Activity.CoreBackup, b.Running ? "running" : c.S3Backup && b.LastError != null ? "error" : "idle",
                    null, null, null, b.Running ? "sichert" : c.S3Backup ? null : "ausgeschaltet", null, b.Running ? null : b.LastRun, c.S3Backup ? b.LastError : null, 0));
            if (mountIndex != null && c.ExternalFolders.Any(x => x.Level != DmOffice.Storage.ExternalFolderDef.LevelNone))
            {
                var check = mountIndex.Checking;
                var read = mountIndex.Reading;
                items.Add(new Activity.Item(Activity.MountIndex, check != null || read != null ? "running" : "idle",
                    check == null ? read?.Done : null, check == null ? read?.Total : null, null,
                    check != null ? $"prüft „{check.Mount}“: {DmOffice.Core.Num.N(check.Done)} Einträge" : read != null ? $"wertet „{read.Mount}“ aus" : null, null,
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
                        : f.Running ? (f.Uploaded > 0 ? $"{DmOffice.Core.Num.N(f.Uploaded)} übertragen" : "prüft, was fehlt")
                        : !BackupRules.FilesOn(c) ? "ausgeschaltet"
                        : f.LastRun != null ? $"{DmOffice.Core.Num.N(f.Count)} Inhalte am Ziel" + (f.Pending > 0 ? $", {DmOffice.Core.Num.N(f.Pending)} offen" : "") : null;
                    var on = BackupRules.FilesOn(c);
                    items.Add(new Activity.Item(Activity.FilesBackup, running ? "running" : on && f.LastError != null ? "error" : "idle",
                        fDone, fTotal, null, text, null, running ? null : f.LastRun, on || running ? f.LastError : null, 0));
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
        // [feature:journal]
        // Tagebuch-Import (Diarium): nur, wenn seit dem Start einer lief
        if (DmOffice.Journal.JournalImport.Of(user).File != null) items.Add(Base(user, Activity.JournalImport));
        // [/feature:journal]

        // Datenexport (lesbare Kopie an ein Ziel der Person): nur, wenn eingerichtet - mit Steuerung (jobcontrol)
        if (Features.On(Features.JobControl) && DmOffice.Storage.PlainBackup.Settings(space.Db) is var plain && DmOffice.Storage.PlainBackup.Ready(plain))
        {
            var run = DmOffice.Storage.PlainBackupService.Progress(user);
            var st = DmOffice.Storage.PlainBackup.State(space.Db);
            var queued = DmOffice.Storage.PlainBackupService.Queued(user);
            items.Add(new Activity.Item(JobControl.DataExport, run != null ? "running" : queued ? "waiting" : st.LastError != null ? "error" : "idle",
                run?.Done, run?.Total, null, run?.Current ?? (run == null && st.LastRun != null ? $"{DmOffice.Core.Num.N(st.Files)} Dateien am Ziel" : null),
                run?.Started, run != null ? null : st.LastRun, run != null ? null : st.LastError, 0));
        }

        var compact = SettingsApi.CompactOf(user);
        items.Add(compact is { Started: not null }
            ? new Activity.Item(Activity.Compact, compact.Running ? "running" : compact.Error != null ? "error" : "idle",
                null, null, null, null, compact.Started,
                compact.Running ? null : compact.Finished, compact.Error, 0)
            : Base(user, Activity.Compact));
        return Features.On(Features.JobControl) ? items.Select(i => WithControl(space.User, i)).ToList() : items;
    }

    /// <summary>Wem ein steuerbarer Job gehört: Suche in Server-Mounts dem Server (nur Admins), sonst der Person.</summary>
    private static string? OwnerOf(UserRecord me, string kind) =>
        !JobControl.Kinds.Contains(kind) ? null : kind == Activity.MountIndex ? (me.IsAdmin ? JobControl.Server : null) : me.Id;

    /// <summary>Steuerbare Jobs: Zustand „angehalten“ bzw. „übersprungen bis …“ und das Recht zu steuern.</summary>
    private static Activity.Item WithControl(UserRecord me, Activity.Item i)
    {
        if (OwnerOf(me, i.Id) is not { } owner) return i;
        // Angehalten oder übersprungen: Hinweise wie „– startet gleich“ stimmen dann nicht
        string? Calm(string? d) => d is { } x && x.IndexOf(" – ", StringComparison.Ordinal) is var cut and > 0 ? x[..cut] : d;
        if (JobControl.Paused(owner, i.Id)) return i with { Hold = "paused", Control = true, State = i.State == "running" ? "running" : "waiting", Detail = Calm(i.Detail) };
        if (JobControl.SkippedUntil(owner, i.Id) is { } until) return i with { Hold = "skipped", HoldUntil = until, Control = true, Detail = Calm(i.Detail) };
        return i with { Control = true };
    }

    public sealed record ControlBody(string? Kind, string? Action);

    /// <summary>Nächster Termin eines Jobs (bis dahin ruht er nach „Überspringen“).</summary>
    private static DateTime NextTerm(UserSpace space, string kind, ServerConfig config)
    {
        var now = DateTime.UtcNow;
        switch (kind)
        {
            case Activity.GalleryScan: return now + DmOffice.Gallery.GalleryFolders.WatchEvery;
            case Activity.GalleryImport:
                var every = DmOffice.Gallery.ImportProfiles.Of(space.Db).Where(p => p.Active).Select(p => Math.Max(5, p.Every)).DefaultIfEmpty(60).Min();
                return now + TimeSpan.FromMinutes(every);
            case Activity.MountIndex:
                return DmOffice.Storage.ServerMounts.Active(config.Current).Select(f => DmOffice.Storage.ExternalIndexer.NextDue(f, DateTimeOffset.Now).UtcDateTime)
                    .DefaultIfEmpty(now.AddDays(1)).Min();
            case JobControl.DataExport:
                var s = DmOffice.Storage.PlainBackup.Settings(space.Db);
                return DmOffice.Storage.PlainBackupService.NextSlots(s, space.Zone, now, 1).FirstOrDefault(now.AddDays(1));
            case Activity.GalleryMedium: return DmOffice.Gallery.GalleryMediumJob.NextNight(DateTime.Now); // [feature:galmedium]
            default: return now;
        }
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/activity", (HttpContext http, MailSync mail, ServerConfig config, S3Mirror mirror, S3Backup backup, DbHealth health, FilesBackup files, DmOffice.Storage.ExternalIndexer mountIndex) =>
        {
            var items = Build(http.Space(), mail, config, mirror, backup, health, files, mountIndex);
            return Results.Ok(new { running = items.Count(i => i.State == "running"), items });
        }).RequireAuthorization();

        // Jobs steuern (jobcontrol): anhalten, fortsetzen, überspringen, abbrechen - eigene Jobs, Admins auch die des Servers
        app.MapPost("/api/activity/control", (HttpContext http, ControlBody body, ServerConfig config) =>
        {
            if (!Features.On(Features.JobControl)) return Results.NotFound();
            var space = http.Space();
            var kind = body.Kind ?? "";
            if (OwnerOf(space.User, kind) is not { } owner) return Results.StatusCode(403);
            switch (body.Action)
            {
                case "pause":
                    JobControl.Pause(owner, kind);
                    if (kind == JobControl.DataExport) DmOffice.Storage.PlainBackupService.Stop(owner);
                    break;
                case "resume": JobControl.Resume(owner, kind); break;
                case "skip":
                    JobControl.Skip(owner, kind, NextTerm(space, kind, config));
                    if (kind == JobControl.DataExport) DmOffice.Storage.PlainBackupService.Stop(owner);
                    break;
                case "cancel":
                    if (kind == JobControl.DataExport) DmOffice.Storage.PlainBackupService.Stop(owner);
                    else if (!JobControl.Cancel(owner, kind)) return Results.BadRequest(new { error = "Der Job läuft gerade nicht." });
                    break;
                default: return Results.BadRequest(new { error = "Unbekannte Aktion." });
            }
            return Results.Ok(new { paused = JobControl.Paused(owner, kind), skippedUntil = JobControl.SkippedUntil(owner, kind) });
        }).RequireAuthorization();
    }
}
