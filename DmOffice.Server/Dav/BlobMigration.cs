using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Nach dem Start: alte Dateiinhalte aus den Benutzerdatenbanken in den BlobStore auslagern (FileStore.MigrateBlobs),
/// in Portionen und unterbrechbar - ein Neustart macht dort weiter, wo es aufgehoert hat. Bis dahin liest FileStore
/// auch aus der Datenbank. Der Platz in der Datenbank wird erst mit dem Verdichten frei (Einstellungen → Statistik);
/// das startet hier niemand von selbst, denn es sperrt die Datenbank fuer die Dauer.
/// Vorher prueft <see cref="UpdateCenter.CheckBlobs"/>: reicht der Platz am Blob-Ort nicht, bleibt alles, wie es ist, und die
/// Verwaltung zeigt, wie viel fehlt; grosse Mengen starten erst nach "Jetzt starten"; "Pausieren" haelt zwischen den Portionen an.
/// Wiederholt sich alle zehn Minuten und auf Wunsch (neue Freigabe, Platz frei geworden).
/// </summary>
public sealed class BlobMigration(Spaces spaces, UpdateCenter update, ILogger<BlobMigration> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var since = update.Generation;
            await Task.Run(() => Pass(ct), CancellationToken.None);
            await update.WaitAsync(TimeSpan.FromMinutes(10), since, ct);
        }
    }

    /// <summary>Ein Durchgang ueber alle Benutzer.</summary>
    public void Pass(CancellationToken ct)
    {
        bool open = false;
        foreach (var space in spaces.All())
        {
            if (ct.IsCancellationRequested) return;
            try { open |= Run(space, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Activity.Start(space.User.Id, Activity.FilesBlobs).Fail(ex.Message); update.SetBlobState(space.User.Id, "idle"); log.LogWarning("Dateiinhalte auslagern {User}: {Error}", space.User.Login, ex.Message); }
        }
        // Freigabe galt nur fuer diese Auslagerung
        if (!open) update.ResetApprovals(migrationDone: false, blobsDone: true);
    }

    /// <returns>true, wenn fuer diesen Benutzer noch etwas offen ist (wartet auf Platz, Freigabe oder Pause).</returns>
    public bool Run(UserSpace space, CancellationToken ct)
    {
        var db = space.Db;
        var pending = FileStore.PendingBlobs(db);
        var report = update.CheckBlobs(space.User.Id, pending, pending == 0 ? 0 : FileStore.PendingBlobBytes(db));
        if (pending == 0) return false;
        if (report.State != "ready")
        {
            log.LogWarning("Dateiinhalte {User}: {Count} Einträge ({Mb} MB) warten ({State}); es fehlen {Missing} MB Platz",
                space.User.Login, pending, report.Bytes / 1024 / 1024, report.State, report.MissingBytes / 1024 / 1024);
            return true;
        }
        using var activity = Activity.Start(space.User.Id, Activity.FilesBlobs, total: pending);
        update.SetBlobState(space.User.Id, "running");
        log.LogInformation("Dateiinhalte {User}: {Count} Einträge werden aus der Datenbank ausgelagert", space.User.Login, pending);
        long done = 0;
        while (true)
        {
            var n = FileStore.MigrateBlobs(db, ct, max: 200);
            done += n;
            activity.Progress(done);
            // Journal klein halten - jede Zeile gibt Seiten frei
            db.Checkpoint();
            if (n == 0 || FileStore.PendingBlobs(db) == 0) break;
            if (!update.BlobsMayContinue(space.User.Id))
            {
                log.LogInformation("Dateiinhalte {User}: angehalten nach {Done} Einträgen", space.User.Login, done);
                return true;
            }
            log.LogInformation("Dateiinhalte {User}: {Done} von {Count} ausgelagert", space.User.Login, done, pending);
        }
        db.Set("blobs_migrated", DateTime.UtcNow.ToString("o"));
        update.SetBlobState(space.User.Id, "idle");
        var (_, _, free) = db.SpaceInfo();
        log.LogInformation("Dateiinhalte {User}: fertig ({Done} Einträge). In der Datenbank sind {Free} MB frei - Verdichten unter Einstellungen → Statistik gibt sie zurück",
            space.User.Login, done, free / 1024 / 1024);
        return false;
    }
}
