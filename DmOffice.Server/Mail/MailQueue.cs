using System.Text.Json;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Postausgang: gesendete Mails warten hier, bis ihre Zeit da ist - ein paar Sekunden zum Zurueckrufen,
/// oder bis zum geplanten Zeitpunkt. Die fertige Nachricht (mit Anhaengen) liegt verschluesselt in der
/// Datenbank des Benutzers; <see cref="MailQueueSender"/> verschickt sie.
/// </summary>
public static class MailQueue
{
    public sealed record Item(long Id, string Account, string Source, MailCompose.Draft Draft, MimeMessage Message,
        DateTime SendAt, int Attempts, string State, string? Error);

    public sealed record Row(long Id, string Account, string Subject, string Recipients, string SendAt, string State, string? Error);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static void Ensure(UserDb db) =>
        db.Exec(@"CREATE TABLE IF NOT EXISTS mail_queue (id INTEGER PRIMARY KEY, account TEXT NOT NULL, source TEXT NOT NULL,
            draft TEXT NOT NULL, raw BLOB NOT NULL, send_at INTEGER NOT NULL, created INTEGER NOT NULL, subject TEXT NOT NULL,
            recipients TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'queued', attempts INTEGER NOT NULL DEFAULT 0, error TEXT)");

    public static long Add(UserDb db, string account, string source, MailCompose.Draft draft, MimeMessage message, DateTime sendAtUtc)
    {
        Ensure(db);
        if (string.IsNullOrEmpty(message.MessageId)) message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        using var ms = new MemoryStream();
        message.WriteTo(ms);
        var recipients = string.Join(", ", message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
            .Select(m => string.IsNullOrEmpty(m.Name) ? m.Address : m.Name));
        db.Exec(@"INSERT INTO mail_queue(account, source, draft, raw, send_at, created, subject, recipients)
                  VALUES($a, $s, $d, $r, $t, $c, $su, $re)",
            ("$a", account), ("$s", source), ("$d", JsonSerializer.Serialize(draft, Json)), ("$r", ms.ToArray()),
            ("$t", Ms(sendAtUtc)), ("$c", Ms(DateTime.UtcNow)), ("$su", message.Subject ?? ""), ("$re", recipients));
        return Convert.ToInt64(db.Scalar("SELECT last_insert_rowid()"));
    }

    public static List<Row> List(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT id, account, subject, recipients, send_at, state, error FROM mail_queue ORDER BY send_at",
            r => new Row(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).UtcDateTime.ToString("o"), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
    }

    public static int Count(UserDb db)
    {
        Ensure(db);
        return Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM mail_queue"));
    }

    /// <summary>Zurueckrufen/loeschen - nicht mehr, sobald der Versand begonnen hat.</summary>
    public static bool Cancel(UserDb db, long id)
    {
        Ensure(db);
        return db.Exec("DELETE FROM mail_queue WHERE id=$i AND state IN ('queued','failed')", ("$i", id)) > 0;
    }

    public static bool Reschedule(UserDb db, long id, DateTime sendAtUtc)
    {
        Ensure(db);
        return db.Exec("UPDATE mail_queue SET send_at=$t, state='queued', error=NULL, attempts=0 WHERE id=$i AND state IN ('queued','failed')",
            ("$t", Ms(sendAtUtc)), ("$i", id)) > 0;
    }

    /// <summary>Faellige Mails fuer den Versand reservieren (state 'sending'), damit keine doppelt rausgeht.</summary>
    public static List<Item> TakeDue(UserDb db, DateTime nowUtc)
    {
        Ensure(db);
        var ids = db.Query("SELECT id FROM mail_queue WHERE state='queued' AND send_at<=$t ORDER BY send_at LIMIT 20",
            r => r.GetInt64(0), ("$t", Ms(nowUtc)));
        var items = new List<Item>();
        foreach (var id in ids)
            if (db.Exec("UPDATE mail_queue SET state='sending' WHERE id=$i AND state='queued'", ("$i", id)) > 0 && Load(db, id) is { } it)
                items.Add(it);
        return items;
    }

    /// <summary>Herausnehmen (z. B. zurueck in die Entwuerfe); null, wenn sie schon unterwegs ist.</summary>
    public static Item? Take(UserDb db, long id)
    {
        Ensure(db);
        if (db.Exec("UPDATE mail_queue SET state='taking' WHERE id=$i AND state IN ('queued','failed')", ("$i", id)) == 0) return null;
        var it = Load(db, id);
        db.Exec("DELETE FROM mail_queue WHERE id=$i", ("$i", id));
        return it;
    }

    /// <summary>Hat das Herausnehmen nicht geklappt: wieder hinein, wie sie war.</summary>
    public static void Restore(UserDb db, Item it) => Add(db, it.Account, it.Source, it.Draft, it.Message, it.SendAt);

    public static void Done(UserDb db, long id) => db.Exec("DELETE FROM mail_queue WHERE id=$i", ("$i", id));

    /// <summary>Versand gescheitert: nach einer Minute noch einmal, nach drei Versuchen als Fehler stehen lassen.</summary>
    public static void Failed(UserDb db, long id, string error, int attempts)
    {
        if (attempts < 3)
            db.Exec("UPDATE mail_queue SET state='queued', attempts=$n, error=$e, send_at=$t WHERE id=$i",
                ("$n", attempts), ("$e", error), ("$t", Ms(DateTime.UtcNow.AddMinutes(1))), ("$i", id));
        else
            db.Exec("UPDATE mail_queue SET state='failed', attempts=$n, error=$e WHERE id=$i", ("$n", attempts), ("$e", error), ("$i", id));
    }

    /// <summary>Nach einem Neustart: was beim Versand haengen blieb, bleibt als Fehler stehen - lieber nachfragen als doppelt senden.</summary>
    public static void RecoverStuck(UserDb db)
    {
        Ensure(db);
        db.Exec("UPDATE mail_queue SET state='failed', error='Versand unterbrochen (Neustart). Bitte prüfen und erneut senden.' WHERE state IN ('sending','taking')");
    }

    private static Item? Load(UserDb db, long id) =>
        db.Query("SELECT id, account, source, draft, raw, send_at, attempts, state, error FROM mail_queue WHERE id=$i", r =>
        {
            var raw = (byte[])r.GetValue(4);
            return new Item(r.GetInt64(0), r.GetString(1), r.GetString(2),
                JsonSerializer.Deserialize<MailCompose.Draft>(r.GetString(3), Json) ?? new MailCompose.Draft(),
                MimeMessage.Load(new MemoryStream(raw)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)).UtcDateTime,
                r.GetInt32(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));
        }, ("$i", id)).FirstOrDefault();
}

/// <summary>Verschickt faellige Mails aus dem Postausgang aller geoeffneten Benutzer.</summary>
public sealed class MailQueueSender(Spaces spaces, ImapPool pool, MailSync sync, ILogger<MailQueueSender> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stop);
        var recovered = new HashSet<string>();
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in spaces.All())
            {
                try
                {
                    if (recovered.Add(space.User.Id)) MailQueue.RecoverStuck(space.Db);
                    foreach (var item in MailQueue.TakeDue(space.Db, DateTime.UtcNow))
                        await Send(space, item, stop);
                }
                catch (Exception e) { log.LogWarning("Postausgang {User}: {Error}", space.User.Login, e.Message); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stop); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task Send(UserSpace space, MailQueue.Item item, CancellationToken stop)
    {
        using var activity = Activity.Start(space.User.Id, Activity.MailQueue);
        MailAccount account, source;
        try { account = space.Account(item.Account); source = space.Settings.MailAccounts.FirstOrDefault(a => a.Id == item.Source) ?? account; }
        catch (Exception) { MailQueue.Failed(space.Db, item.Id, "Das Konto gibt es nicht mehr.", 3); sync.Touch(space.User.Id); return; }
        try
        {
            item.Message.Date = DateTimeOffset.Now;
            var warnings = await MailApi.Deliver(space, account, source, item.Draft, item.Message, pool, sync, log, stop);
            MailQueue.Done(space.Db, item.Id);
            foreach (var w in warnings) log.LogWarning("Postausgang {Account}: {Warning}", account.Email, w);
        }
        catch (Exception e)
        {
            // Nur wenn SMTP selbst scheitert - die Mail ist dann nicht raus
            log.LogWarning("Postausgang {Account}: Senden fehlgeschlagen: {Error}", account.Email, e.Message);
            MailQueue.Failed(space.Db, item.Id, e.Message, item.Attempts + 1);
            activity.Fail(e.Message);
        }
        sync.Touch(space.User.Id);
    }
}
