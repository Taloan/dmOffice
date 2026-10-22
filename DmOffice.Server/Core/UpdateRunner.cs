namespace DmOffice.Core;

/// <summary>
/// Beim Start und danach alle zehn Minuten (oder auf "Jetzt starten"/"Erneut pruefen"): jede Benutzerdatenbank oeffnen,
/// die noch auf ihre Datenaenderung wartet - die Vorabpruefung in <see cref="Spaces.Get"/> entscheidet, ob sie jetzt
/// laufen darf (Platz, Sicherung, Freigabe). Taeglich werden alte Sicherungen weggeraeumt.
/// </summary>
public sealed class UpdateRunner(Spaces spaces, UserStore users, UpdateCenter update, ILogger<UpdateRunner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var since = update.Generation;
            await Task.Run(Pass, CancellationToken.None);
            await update.WaitAsync(TimeSpan.FromMinutes(10), since, ct);
        }
    }

    private DateTime _lastCleanup = DateTime.MinValue;

    private void Pass()
    {
        try
        {
            foreach (var u in users.All())
            {
                var plan = update.Plan(u);
                if (!plan.Pending || plan.State == "running") continue;
                if (plan.State == "ready" || (plan.State == "error" && plan.Message != null))
                    log.LogInformation("Datenänderung {User}: Version {From} → {To}, {Steps} Schritte, Sicherung {Backup}", u.Login, plan.From, plan.To, plan.Steps.Count, plan.WillBackup ? "ja" : "nein");
                else
                    log.LogWarning("Datenänderung {User} wartet ({State}): fehlen {Missing} MB Platz, Sicherung braucht {Backup} MB", u.Login, plan.State, plan.MissingBytes / 1024 / 1024, plan.BackupBytes / 1024 / 1024);
                try { spaces.Get(u.Id); update.Kick(); /* danach darf die Auslagerung der Dateiinhalte los */ }
                catch (UpdatePendingException ex) { log.LogWarning("Datenänderung {User}: {Message}", u.Login, ex.Message); }
                catch (Exception ex) { log.LogWarning("Datenänderung {User}: {Error}", u.Login, ex.Message); }
            }
            // Freigaben gelten nur fuer die eine Aenderung: ist nichts mehr offen, fangen sie von vorn an
            if (!users.All().Any(u => update.Plan(u).Pending)) update.ResetApprovals(migrationDone: true, blobsDone: false);

            if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromHours(23))
            {
                _lastCleanup = DateTime.UtcNow;
                var n = update.CleanupBackups();
                if (n > 0) log.LogInformation("Sicherungen vor Datenänderungen: {Count} alte gelöscht (nach {Days} Tagen)", n, update.KeepDays);
            }
        }
        catch (Exception ex) { log.LogWarning("Datenänderungen prüfen: {Error}", ex.Message); }
    }
}
