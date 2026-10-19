using System.Collections.Concurrent;
using DmOffice.Core;
using MailKit.Net.Imap;

namespace DmOffice.Mail;

/// <summary>
/// Aenderungen an Mails (Fahne, gelesen, verschieben, loeschen) stehen sofort in der eigenen Datenbank;
/// zum IMAP-Server gehen sie danach im Hintergrund - je Konto der Reihe nach, mit Wiederholung.
/// Solange eine Aenderung unterwegs ist, ueberschreibt der Sync sie nicht mit dem alten Stand des Servers.
/// Klappt es endgueltig nicht, holt der Sync den Ordner neu und die Oberflaeche zeigt wieder den Serverstand.
/// </summary>
public sealed class MailOutbox(ImapPool pool, MailSync sync, ILogger<MailOutbox> log)
{
    private static readonly TimeSpan[] Retry = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<string, Task> _chains = new();

    /// <param name="removes">true: die Mails verschwinden aus dem Ordner (verschieben, loeschen)</param>
    /// <param name="after">nach Erfolg, z. B. den Zielordner abgleichen</param>
    public void Enqueue(UserSpace space, MailAccount account, string folder, IReadOnlyCollection<uint> uids, bool removes,
        string what, Func<ImapClient, CancellationToken, Task> work, Action? after = null)
    {
        var user = space.User.Id;
        var keys = uids.Select(u => MailPending.Key(user, account.Id, folder, u, removes)).ToList();
        foreach (var k in keys) MailPending.Add(k);
        var chainKey = user + "|" + account.Id;
        lock (_chains)
        {
            var prev = _chains.GetValueOrDefault(chainKey) ?? Task.CompletedTask;
            _chains[chainKey] = prev.ContinueWith(_ => Run(space, account, folder, what, work, after, keys), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task Run(UserSpace space, MailAccount account, string folder, string what,
        Func<ImapClient, CancellationToken, Task> work, Action? after, List<string> keys)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    await pool.UseAsync(account, ImapPool.Ui, c => work(c, cts.Token), cts.Token);
                    after?.Invoke();
                    return;
                }
                catch (Exception e) when (attempt < Retry.Length)
                {
                    log.LogInformation("IMAP {What} ({Account}/{Folder}) Versuch {N}: {Error}", what, account.Email, folder, attempt + 1, e.Message);
                    await Task.Delay(Retry[attempt]);
                }
                catch (Exception e)
                {
                    log.LogWarning("IMAP {What} ({Account}/{Folder}) aufgegeben: {Error}", what, account.Email, folder, e.Message);
                    // Ordner komplett neu abgleichen (auch die Fahnen), damit wieder der Serverstand gilt
                    space.Db.Exec("UPDATE mail_folders SET modseq=0 WHERE account=$a AND path=$p", ("$a", account.Id), ("$p", folder));
                    foreach (var k in keys) MailPending.Remove(k);
                    keys.Clear();
                    sync.Kick(space.User.Id, account.Id, folder);
                    return;
                }
            }
        }
        finally
        {
            foreach (var k in keys) MailPending.Remove(k);
            sync.Touch(space.User.Id);
        }
    }
}

/// <summary>Was gerade zum IMAP-Server unterwegs ist - der Sync laesst diese Mails in Ruhe.</summary>
public static class MailPending
{
    private static readonly ConcurrentDictionary<string, int> Open = new();

    public static string Key(string user, string account, string folder, uint uid, bool removes) =>
        string.Join('\u001f', user, account, folder, uid.ToString(), removes ? "r" : "f");

    public static void Add(string key) => Open.AddOrUpdate(key, 1, (_, n) => n + 1);

    public static void Remove(string key)
    {
        if (Open.AddOrUpdate(key, 0, (_, n) => n - 1) <= 0) Open.TryRemove(key, out _);
    }

    /// <summary>Wird gerade verschoben oder geloescht - beim Sync nicht wieder aufnehmen.</summary>
    public static bool Removing(string user, string account, string folder, uint uid) =>
        Open.ContainsKey(Key(user, account, folder, uid, true));

    /// <summary>Fahne oder gelesen wird gerade gesetzt - beim Sync nicht mit dem alten Stand ueberschreiben.</summary>
    public static bool Flagging(string user, string account, string folder, uint uid) =>
        Open.ContainsKey(Key(user, account, folder, uid, false));

    public static bool Any => !Open.IsEmpty;
}
