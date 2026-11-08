using System.Collections.Concurrent;
using DmOffice.Core;

namespace DmOffice.Dms;

/// <summary>
/// Hintergrund vom DMS: fällige Quellen prüfen, wartende Dokumente verarbeiten (eines nach dem anderen), stündlich aufräumen.
/// Hochladen und „Jetzt prüfen“ stoßen ihn sofort an, ebenso eine neue Datei im Ordner einer Quelle in der eigenen Ablage
/// (<see cref="Dav.FolderWatch"/>).
/// </summary>
public sealed class DmsJob(Spaces spaces, ILogger<DmsJob> log) : BackgroundService
{
    private static readonly SemaphoreSlim Signal = new(0);
    private static readonly ConcurrentDictionary<string, bool> Wanted = new();

    /// <summary>Etwas Neues zu tun (neues Dokument).</summary>
    public static void Kick() { if (Signal.CurrentCount == 0) Signal.Release(); }

    /// <summary>Eine Quelle jetzt prüfen.</summary>
    public static void Request(string userId, string sourceId) { Wanted[userId + "/" + sourceId] = true; Kick(); }

    /// <summary>Quellen in der eigenen Ablage: ohne Anstoß (der kommt vom <see cref="Dav.FolderWatch"/>) nur so selten nachsehen.</summary>
    public static readonly TimeSpan OwnFallback = TimeSpan.FromHours(1);

    /// <summary>Name beim <see cref="Dav.FolderWatch"/>.</summary>
    public const string Watch = "dms";

    /// <summary>Quellen in der eigenen Ablage sofort prüfen, wenn dort eine Datei fertig gespeichert ist.</summary>
    public static void WatchFolders() => Dav.FolderWatch.Watch(new(Watch, TimeSpan.FromSeconds(5),
        db => Features.On(Features.Dms) ? DmsSources.Of(db).Where(s => s.Active && s.Mount.Length == 0).Select(s => (s.Id, s.Folder)) : [],
        Request));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        WatchFolders();
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }
        var cleaned = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Features.On(Features.Dms) && DmsHost.Current is { } host)
                {
                    var clean = DateTime.UtcNow - cleaned > TimeSpan.FromHours(1);
                    foreach (var space in spaces.All())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Dav.FileStore.FilesOff(space.Db)) continue;
                        try { using (Dav.FolderWatch.As(Watch)) await RunAsync(space, host, clean, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { log.LogWarning(e, "DMS {User}", space.User.Login); }
                    }
                    if (clean) cleaned = DateTime.UtcNow;
                }
                else Wanted.Clear();
            }
            catch (OperationCanceledException) { return; }
            try { await Signal.WaitAsync(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunAsync(UserSpace space, DmsHost host, bool clean, CancellationToken ct)
    {
        var db = space.Db;
        // Nur, wer das DMS schon benutzt (Tabellen oder Quellen da) - sonst nichts anlegen
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE name = 'dms_docs'") == null && DmsSources.Of(db).Count == 0 && DmsMailSources.Of(db).Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var s in DmsSources.Of(db))
        {
            var wanted = Wanted.TryRemove(space.User.Id + "/" + s.Id, out _);
            // Eigene Ablage: FolderWatch stößt bei jeder fertig gespeicherten Datei an; nur zur Sicherheit stündlich nachsehen.
            // Externe Ordner meldet niemand - die im eingestellten Abstand
            var every = s.Mount.Length == 0 ? OwnFallback : TimeSpan.FromMinutes(Math.Max(1, s.Every));
            var due = s.Active && (s.LastRun == null || now - s.LastRun.Value >= every);
            // Läuft gerade ein Stapel ein, kommt der Anstoß danach
            if (!wanted && (!due || Dav.FolderWatch.IsPending(Watch, space.User.Id, s.Id))) continue;
            // Eine Quelle hält die übrigen und die Warteschlange nie auf
            try
            {
                var (done, dup) = await DmsSources.RunAsync(space, host, s.Id, ct);
                if (done + dup > 0) log.LogInformation("DMS-Quelle ({User}): {Count} übernommen, {Dup} schon vorhanden", space.User.Login, done, dup);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { log.LogWarning(e, "DMS-Quelle ({User})", space.User.Login); }
        }
        // Mailordner als Quelle (nur mit Mail-Modul)
        if (DmsHost.Mail is { } mail && DmsMailSources.Of(db).Count > 0)
        {
            var wanted = Wanted.Keys.Where(k => k.StartsWith(space.User.Id + "/", StringComparison.Ordinal)).ToList();
            foreach (var k in wanted) Wanted.TryRemove(k, out _);
            try
            {
                var n = await DmsMailSources.RunDueAsync(space, mail, host.HtmlToText, wanted.Select(k => k[(space.User.Id.Length + 1)..]).ToHashSet(), ct);
                if (n > 0) log.LogInformation("DMS-Mailquellen ({User}): {Count} Dokumente", space.User.Login, n);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { log.LogWarning(e, "DMS-Mailquellen ({User})", space.User.Login); }
        }
        // Wartende Dokumente: in Portionen, damit andere Personen nicht lange warten
        DmsImport.MoveInboxOnce(space, host);
        var paused = DmsImport.Settings(db).Paused;
        if (!paused && await DmsImport.ProcessQueueAsync(space, host, 10, ct) >= 10) Kick();
        if (clean)
        {
            // Beim Start und stündlich: offene Inbox neu einordnen (etwa Vorschläge der KI, deren Eintrag es inzwischen gibt)
            foreach (var id in Dms.Reassess(db)) DmsImport.Refile(space, host, id, false, db);
            DmsImport.Cleanup(space, host, ct);
            await DmsImport.ScanExternalAsync(space, host, ct);
        }
        // Geänderte Angaben in die PDFs im externen Ordner
        await DmsImport.WriteMetaAsync(space, host, ct);
        // Tresor: nur solange er offen ist (Lesen, Texterkennung, Einordnen und Ablegen brauchen den Schlüssel)
        if (Core.Tresor.DbOf(space.User.Id) is { } tresor)
        {
            var store = DmsStore.ForTresor(space.Db, tresor);
            DmsImport.MoveInboxOnce(space, host, store);
            if (!paused && await DmsImport.ProcessQueueAsync(space, host, 10, ct, store) >= 10) Kick();
            if (clean) DmsImport.Cleanup(space, host, ct, store);
        }
    }
}
