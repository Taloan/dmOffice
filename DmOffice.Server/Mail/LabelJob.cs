using DmOffice.Core;
using MailKit;

namespace DmOffice.Mail;

/// <summary>
/// Lernende Kategorien im Hintergrund: übernimmt je Benutzer einmal die alte Smart Inbox (SmartInbox.Migrate) und schreibt
/// die übernommenen Zuordnungen als IMAP-Schlagwort an die Mails - je Konto und Ordner gebündelt, in Portionen.
/// </summary>
public sealed class LabelJob(Spaces spaces, ImapPool pool, ILogger<LabelJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in spaces.All())
            {
                try
                {
                    SmartInbox.Migrate(space);
                    await WritePending(space, stop);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { log.LogWarning(e, "Kategorien übernehmen {User}", space.User.Login); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stop); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task WritePending(UserSpace space, CancellationToken ct)
    {
        foreach (var (account, folder, cat, uids) in SmartInbox.Pending(space.Db))
        {
            if (space.Settings.MailAccounts.FirstOrDefault(a => a.Id == account) is not { } acc) { SmartInbox.DonePending(space.Db, account, folder, cat, uids); continue; }
            try
            {
                if (GraphCategories.Uses(acc)) await GraphCategories.SetAsync(space, acc, folder, uids, cat, true, ct);
                else await pool.UseAsync(acc, ImapPool.Sync, async c =>
                {
                    var f = await c.GetFolderAsync(folder, ct);
                    await f.OpenAsync(FolderAccess.ReadWrite, ct);
                    if (f.PermanentFlags.HasFlag(MessageFlags.UserDefined) || f.PermanentKeywords.Contains(cat))
                    {
                        var req = new StoreFlagsRequest(StoreAction.Add, MessageFlags.None) { Silent = true };
                        req.Keywords.Add(cat);
                        foreach (var chunk in uids.Chunk(200)) await f.StoreAsync(chunk.Select(u => new UniqueId(u)).ToList(), req, ct);
                    }
                }, ct);
                MailStore.SetKeyword(space.Db, account, folder, uids, cat, true);
                SmartInbox.DonePending(space.Db, account, folder, cat, uids);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Ordner weg oder Server nicht erreichbar: beim nächsten Durchgang wieder
                log.LogInformation("Kategorie an {Count} Mails: {Error}", uids.Count, e.Message);
            }
        }
    }
}
