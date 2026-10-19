using DmOffice.Core;
using MailKit;
using MailKit.Search;

namespace DmOffice.Mail;

/// <summary>
/// Papierkorb und Junk automatisch leeren: was laut Mail-Datum aelter als die eingestellten Tage ist,
/// wird endgueltig geloescht. Laeuft stuendlich fuer alle geoeffneten Benutzer und gleich nach dem Einstellen.
/// </summary>
public sealed class MailCleanup(Spaces spaces, ImapPool pool, MailSync sync, ILogger<MailCleanup> log) : BackgroundService
{
    public static int DaysOf(MailAccount a, string special) => special == "trash" ? a.TrashDays : special == "junk" ? a.JunkDays : 0;

    /// <summary>Stichtag: Mails, die vor diesem Tag geschrieben wurden, kommen weg.</summary>
    public static DateTime Cutoff(int days, DateTime today) => today.Date.AddDays(-days);

    /// <summary>Beide Ordner eines Kontos aufraeumen; liefert die Zahl der geloeschten Mails.</summary>
    public static async Task<int> RunAsync(UserSpace space, MailAccount account, ImapPool pool, MailSync sync, CancellationToken ct)
    {
        var folders = MailStore.Folders(space.Db, account.Id);
        var todo = new[] { "trash", "junk" }
            .Select(sp => (Days: DaysOf(account, sp), Row: folders.FirstOrDefault(f => f.Special == sp)))
            .Where(x => x.Days > 0 && x.Row != null).ToList();
        if (todo.Count == 0) return 0;
        var count = 0;
        await pool.UseAsync(account, ImapPool.Sync, async c =>
        {
            foreach (var (days, row) in todo)
            {
                var f = await c.GetFolderAsync(row!.Path, ct);
                await f.OpenAsync(FolderAccess.ReadWrite, ct);
                var old = await f.SearchAsync(SearchQuery.SentBefore(Cutoff(days, DateTime.Today)).And(SearchQuery.NotDeleted), ct);
                if (old.Count == 0) continue;
                foreach (var batch in old.Chunk(1000))
                    await f.StoreAsync(batch, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
                // Papierkorb/Junk: hier darf alles Markierte endgueltig weg
                await f.ExpungeAsync(ct);
                count += old.Count;
                sync.Kick(space.User.Id, account.Id, row.Path);
            }
        }, ct);
        if (count > 0) sync.Touch(space.User.Id);
        return count;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in spaces.All())
                foreach (var account in space.Settings.MailAccounts.Where(a => a.TrashDays > 0 || a.JunkDays > 0).ToList())
                {
                    try
                    {
                        var n = await RunAsync(space, account, pool, sync, stop);
                        if (n > 0) log.LogInformation("Aufgeraeumt {Account}: {Count} alte Mails aus Papierkorb/Junk", account.Email, n);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                    catch (Exception e) { log.LogWarning("Aufraeumen {Account}: {Error}", account.Email, e.Message); }
                }
            try { await Task.Delay(TimeSpan.FromHours(1), stop); } catch (OperationCanceledException) { break; }
        }
    }
}
