using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Automatisch abmelden beim Verschieben in den Junk-Ordner (Mail/AutoUnsubscribe.cs).</summary>
[Collection("autounsub")]
public class AutoUnsubscribeTests
{
    private static (UserSpace Space, MailAccount Account) NewSpace(bool on)
    {
        var dir = Directory.CreateTempSubdirectory("dmunsub");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t", Id = "u" + Guid.NewGuid().ToString("N")[..6] }, db);
        var account = new MailAccount { Email = "ich@example.org", FocusedInbox = true };
        space.UpdateSettings(s => { s.MailAccounts.Add(account); s.AutoUnsubscribe = on; });
        return (space, account);
    }

    private static MimeMessage Newsletter(string from, string? listId = "news.example.com", string? header = "<https://example.com/u>")
    {
        var m = new MimeMessage { Subject = "Angebote der Woche" };
        m.From.Add(new MailboxAddress("Shop", from));
        if (header != null) m.Headers.Add("List-Unsubscribe", header);
        if (listId != null) m.Headers.Add("List-Id", listId);
        return m;
    }

    [Fact]
    public async Task Meldet_einmal_je_Liste_ab_und_haelt_das_Ergebnis_fest()
    {
        var (space, account) = NewSpace(on: true);
        var calls = 0;
        AutoUnsubscribe.Attempt = (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult((AutoUnsubscribe.Unconfirmed, (string?)"link")); };
        try
        {
            var first = AutoUnsubscribe.Trigger(space, account, Newsletter("news@example.com"));
            Assert.NotNull(first);
            // Zweite Mail derselben Liste im selben Zug: kein zweiter Versuch
            Assert.Null(AutoUnsubscribe.Trigger(space, account, Newsletter("news@example.com")));
            await first!;
            Assert.Equal(1, calls);
            var e = Assert.Single(AutoUnsubscribe.Log(space.Db));
            Assert.Equal(AutoUnsubscribe.Unconfirmed, e.Result);
            Assert.Equal("link", e.How);
            Assert.Equal("Shop <news@example.com>", e.Sender);
            // Mail ohne Abmeldung: nichts zu tun
            Assert.Null(AutoUnsubscribe.Trigger(space, account, Newsletter("x@example.net", "andere", header: null)));
        }
        finally { AutoUnsubscribe.Attempt = null; }
    }

    [Fact]
    public async Task Fehlgeschlagenes_wird_beim_naechsten_Mal_wieder_versucht()
    {
        var (space, account) = NewSpace(on: true);
        AutoUnsubscribe.Attempt = (_, _, _) => Task.FromResult((AutoUnsubscribe.Failed, (string?)null));
        try
        {
            await AutoUnsubscribe.Trigger(space, account, Newsletter("a@example.com"))!;
            Assert.Equal(AutoUnsubscribe.Failed, AutoUnsubscribe.Log(space.Db).Single().Result);
            Assert.NotNull(AutoUnsubscribe.Trigger(space, account, Newsletter("a@example.com")));
        }
        finally { AutoUnsubscribe.Attempt = null; }
    }

    [Fact]
    public void Schalter_aus_oder_Smart_Inbox_aus_tut_nichts()
    {
        var (space, account) = NewSpace(on: false);
        Assert.Null(AutoUnsubscribe.Trigger(space, account, Newsletter("b@example.com")));
        space.UpdateSettings(s => { s.AutoUnsubscribe = true; s.SmartInbox = false; });
        Assert.Null(AutoUnsubscribe.Trigger(space, account, Newsletter("b@example.com")));
        Assert.Empty(AutoUnsubscribe.Log(space.Db));
    }
}
