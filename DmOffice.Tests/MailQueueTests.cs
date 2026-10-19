using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Postausgang: verzoegert/geplant senden, zurueckrufen, nichts doppelt.</summary>
public class MailQueueTests
{
    private static (UserDb Db, MimeMessage Message) Setup()
    {
        var dir = Directory.CreateTempSubdirectory("dmqueue");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Ich", "ich@beispiel.de"));
        m.To.Add(new MailboxAddress("Anna", "anna@example.org"));
        m.Subject = "Hallo";
        m.Body = new TextPart("plain") { Text = "Hallo Anna" };
        return (db, m);
    }

    [Fact]
    public void Wartet_bis_zur_Zeit_und_geht_nur_einmal_raus()
    {
        var (db, m) = Setup();
        var now = DateTime.UtcNow;
        var id = MailQueue.Add(db, "a", "a", new MailCompose.Draft { Subject = "Hallo" }, m, now.AddSeconds(10));
        Assert.Equal(1, MailQueue.Count(db));
        Assert.Empty(MailQueue.TakeDue(db, now));

        var due = MailQueue.TakeDue(db, now.AddSeconds(11));
        var item = Assert.Single(due);
        Assert.Equal("Hallo", item.Message.Subject);
        Assert.False(string.IsNullOrEmpty(item.Message.MessageId));
        // Schon reserviert: kein zweites Mal, und Zurueckrufen geht nicht mehr
        Assert.Empty(MailQueue.TakeDue(db, now.AddSeconds(20)));
        Assert.False(MailQueue.Cancel(db, id));

        MailQueue.Done(db, id);
        Assert.Equal(0, MailQueue.Count(db));
    }

    [Fact]
    public void Zurueckrufen_und_neu_planen()
    {
        var (db, m) = Setup();
        var now = DateTime.UtcNow;
        var a = MailQueue.Add(db, "a", "a", new MailCompose.Draft(), m, now.AddSeconds(10));
        Assert.True(MailQueue.Cancel(db, a));
        Assert.Equal(0, MailQueue.Count(db));

        var b = MailQueue.Add(db, "a", "a", new MailCompose.Draft(), m, now.AddDays(1));
        Assert.True(MailQueue.Reschedule(db, b, now));
        Assert.Single(MailQueue.TakeDue(db, now.AddSeconds(1)));
    }

    [Fact]
    public void Fehler_wird_wiederholt_und_nach_Neustart_nicht_doppelt_gesendet()
    {
        var (db, m) = Setup();
        var now = DateTime.UtcNow;
        var id = MailQueue.Add(db, "a", "a", new MailCompose.Draft(), m, now);
        Assert.Single(MailQueue.TakeDue(db, now));
        MailQueue.Failed(db, id, "SMTP weg", 1);
        Assert.Empty(MailQueue.TakeDue(db, now.AddSeconds(30)));   // erst nach einer Minute wieder
        Assert.Single(MailQueue.TakeDue(db, now.AddMinutes(2)));
        MailQueue.Failed(db, id, "SMTP weg", 3);
        Assert.Equal("failed", MailQueue.List(db).Single().State);

        // Haengt eine beim Neustart im Versand, wird sie nicht still erneut gesendet
        var other = MailQueue.Add(db, "a", "a", new MailCompose.Draft(), m, now);
        Assert.Single(MailQueue.TakeDue(db, now));
        MailQueue.RecoverStuck(db);
        Assert.All(MailQueue.List(db), r => Assert.Equal("failed", r.State));
        Assert.True(MailQueue.Cancel(db, other));
    }

    [Fact]
    public void Herausnehmen_fuer_die_Entwuerfe()
    {
        var (db, m) = Setup();
        var id = MailQueue.Add(db, "a", "a", new MailCompose.Draft { Subject = "Hallo" }, m, DateTime.UtcNow.AddHours(1));
        var item = MailQueue.Take(db, id);
        Assert.NotNull(item);
        Assert.Equal(0, MailQueue.Count(db));
        Assert.Null(MailQueue.Take(db, id));
        MailQueue.Restore(db, item!);
        Assert.Equal(1, MailQueue.Count(db));
    }
}
