using System.Text;
using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Catchall-Aliase je Domain (Kennung catchalldomains): Übernahme der früheren Form, Postfach je Domain, „Welche Aliase
/// annehmen?“, „Auch im Posteingang“ als Ansicht, Antworten vom Alias oder aus „Antworten von“, Absender abgelehnt.</summary>
public class CatchallDomainsTests
{
    private static (UserSpace Space, MailAccount A, MailAccount B) Setup()
    {
        var dir = Directory.CreateTempSubdirectory("dmcatchdom");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        var a = new MailAccount { Id = "a", Name = "Privat", Email = "ich@mailbox.example" };
        var b = new MailAccount { Id = "b", Name = "Arbeit", Email = "ich@arbeit.example" };
        space.UpdateSettings(s => { s.MailAccounts.Add(a); s.MailAccounts.Add(b); });
        return (space, a, b);
    }

    private static MimeMessage Mail(string to, string? id = null)
    {
        var raw = $"To: {to}\r\nFrom: Shop <info@shop.example>\r\nSubject: Angebot\r\nMessage-ID: <{id ?? Guid.NewGuid().ToString("N")}@shop.example>\r\n"
                  + "Date: Tue, 27 Oct 2026 09:04:00 +0100\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHallo\r\n";
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));
    }

    [Fact]
    public void Update_uebernimmt_fruehere_Einstellungen_ohne_Aenderung()
    {
        var (space, a, _) = Setup();
        var db = space.Db;
        CatchallAliases.Save(db, new CatchallAliases.Config { Domains = ["eins.example", "zwei.example"], Account = a.Id, Unknown = "junk", ReplyAs = false },
            space.Settings.MailAccounts);
        var sites = CatchallAliases.SitesOf(CatchallAliases.Load(db));
        Assert.Equal(["eins.example", "zwei.example"], sites.Select(s => s.Domain));
        Assert.All(sites, s =>
        {
            Assert.Equal(a.Id, s.Account);
            Assert.Equal("known", s.Accept);       // bisher „Unbekannte → Junk“
            Assert.False(s.Inbox);                  // Posteingang aus
            Assert.Equal(a.Id, s.ReplyFrom);        // „Antworten mit dem Alias“ war aus: Adresse des Postfachs
        });
        // Einmal übernommen und gespeichert - danach die Form je Domain
        var c = CatchallAliases.Migrated(db);
        Assert.NotNull(c.Sites);
        Assert.Equal(2, CatchallAliases.Load(db).Sites!.Count);

        // „Antworten mit dem Alias“ an, Unbekannte annehmen: Antworten vom Alias, alle annehmen
        CatchallAliases.Save(db, new CatchallAliases.Config { Domains = ["eins.example"], Account = a.Id, Unknown = "accept", ReplyAs = true }, space.Settings.MailAccounts);
        var s1 = Assert.Single(CatchallAliases.SitesOf(CatchallAliases.Load(db)));
        Assert.Equal(("all", (string?)null), (s1.Accept, s1.ReplyFrom));
    }

    [Fact]
    public void Ohne_Kennung_bleibt_alles_wie_bisher()
    {
        var (space, a, b) = Setup();
        var before = Features.InReleaseOff.ToList();
        try
        {
            CatchallAliases.SaveSites(space.Db, [new() { Domain = "eins.example", Account = a.Id }, new() { Domain = "zwei.example", Account = b.Id, Inbox = true }],
                space.Settings.MailAccounts);
            Features.Set([.. before, Features.CatchallDomains]);
            // Ohne Kennung zählt die frühere Form: ein Postfach (das der ersten Domain) für alle Domains, kein Posteingang
            var sites = CatchallAliases.SitesOf(CatchallAliases.Load(space.Db));
            Assert.All(sites, s => { Assert.Equal(a.Id, s.Account); Assert.False(s.Inbox); });
            Assert.True(CatchallAliases.ActiveFor(CatchallAliases.Load(space.Db), a.Id));
            Assert.False(CatchallAliases.ActiveFor(CatchallAliases.Load(space.Db), b.Id));
            Assert.Null(CatchallAliases.View(space.Db, "ca:@zwei.example"));
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public void Domains_je_Postfach_und_Annehmen_je_Domain()
    {
        var (space, a, b) = Setup();
        var db = space.Db;
        var c = CatchallAliases.SaveSites(db, [
            new() { Domain = "Eins.Example", Account = a.Id, Accept = "known" },
            new() { Domain = "zwei.example", Account = b.Id, Accept = "all" },
        ], space.Settings.MailAccounts);
        Assert.Equal(["eins.example", "zwei.example"], c.Domains);
        Assert.True(CatchallAliases.ActiveFor(c, a.Id));
        Assert.True(CatchallAliases.ActiveFor(c, b.Id));
        Assert.False(CatchallAliases.ActiveFor(c, "anders"));

        // Zuordnung nur zu den Domains des Postfachs, in dem die Mail liegt
        var m = Mail("x@zwei.example");
        Assert.Null(CatchallAliases.Of(c, m, a.Id));
        Assert.Equal("x@zwei.example", CatchallAliases.Of(c, m, b.Id));

        // „Nur angelegte“: nicht angelegte gleich in Junk; „Alle“: angenommen
        Assert.Equal("alias-unknown", CatchallAliases.Judge(db, a, Mail("neu@eins.example"))!.Rule);
        Assert.Null(CatchallAliases.Judge(db, b, Mail("neu@zwei.example")));
        CatchallAliases.Create(db, c, "shop", "eins.example", null);
        Assert.Null(CatchallAliases.Judge(db, a, Mail("shop@eins.example")));

        // Jede Domain gehört zu genau einem Postfach; ohne Postfach geht es nicht
        Assert.Throws<ArgumentException>(() => CatchallAliases.SaveSites(db, [new() { Domain = "eins.example", Account = a.Id }, new() { Domain = "eins.example", Account = b.Id }], space.Settings.MailAccounts));
        Assert.Throws<ArgumentException>(() => CatchallAliases.SaveSites(db, [new() { Domain = "drei.example" }], space.Settings.MailAccounts));
        Assert.Throws<ArgumentException>(() => CatchallAliases.SaveSites(db, [new() { Domain = "kein domain", Account = a.Id }], space.Settings.MailAccounts));
    }

    [Fact]
    public void Antworten_vom_Alias_auch_nicht_angelegt_gesperrte_nicht()
    {
        var (space, a, b) = Setup();
        var db = space.Db;
        var c = CatchallAliases.SaveSites(db, [new() { Domain = "eins.example", Account = a.Id }], space.Settings.MailAccounts);
        var shop = CatchallAliases.Create(db, c, "shop", null, null);

        // Angelegt und nicht angelegt: vom Alias
        Assert.Equal((a.Id, (string?)"shop@eins.example"), CatchallAliases.ReplySender(db, a, "shop@eins.example"));
        Assert.Equal((a.Id, (string?)"nie-angelegt@eins.example"), CatchallAliases.ReplySender(db, a, "nie-angelegt@eins.example"));
        Assert.True(CatchallAliases.CanSendAs(db, a, "nie-angelegt@eins.example"));
        // Nur aus dem Postfach der Domain, nur Adressen der Domain
        Assert.False(CatchallAliases.CanSendAs(db, b, "shop@eins.example"));
        Assert.False(CatchallAliases.CanSendAs(db, a, "x@fremd.example"));
        // Gesperrt: nicht
        CatchallAliases.Update(db, shop.Address, null, "junk", true);
        Assert.False(CatchallAliases.CanSendAs(db, a, shop.Address));
        Assert.Null(CatchallAliases.ReplySender(db, a, shop.Address));
        Assert.DoesNotContain(CatchallAliases.Senders(db), x => x.Address == shop.Address);

        // „Für diese Domain immer so“: Antworten kommen aus dem gewählten Mailkonto, „Von“ bleibt von Hand änderbar
        CatchallAliases.SetReplyFrom(db, "eins.example", b.Id, space.Settings.MailAccounts);
        Assert.Equal((b.Id, (string?)null), CatchallAliases.ReplySender(db, a, "nie-angelegt@eins.example"));
        Assert.True(CatchallAliases.CanSendAs(db, a, "nie-angelegt@eins.example"));
        CatchallAliases.SetReplyFrom(db, "eins.example", null, space.Settings.MailAccounts);
        Assert.Equal((a.Id, (string?)"nie-angelegt@eins.example"), CatchallAliases.ReplySender(db, a, "nie-angelegt@eins.example"));

        // Erstes Senden als Alias der Domain geht sofort (Ablehnung kommt im Verfassen an), danach mit Verzögerung
        Assert.True(CatchallAliases.FirstSendAs(db, a, "nie-angelegt@eins.example"));
        Assert.False(CatchallAliases.FirstSendAs(db, a, null));
        CatchallAliases.ConfirmSend(db, "nie-angelegt@eins.example");
        Assert.False(CatchallAliases.FirstSendAs(db, a, "anderer@eins.example"));
    }

    [Fact]
    public void Absender_abgelehnt_erkennen()
    {
        Assert.True(CatchallAliases.SenderRejected(new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.SenderNotAccepted,
            MailKit.Net.Smtp.SmtpStatusCode.MailboxNameNotAllowed, "553 5.7.1 Sender address rejected: not owned by user")));
        Assert.False(CatchallAliases.SenderRejected(new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.RecipientNotAccepted,
            MailKit.Net.Smtp.SmtpStatusCode.MailboxUnavailable, "550 No such user")));
        Assert.False(CatchallAliases.SenderRejected(new IOException("Verbindung weg")));
    }

    [Fact]
    public void Auch_im_Posteingang_Ansicht_ueber_mehrere_Ordner_jede_Mail_einmal()
    {
        var (space, a, _) = Setup();
        var db = space.Db;
        var c = CatchallAliases.SaveSites(db, [new() { Domain = "eins.example", Account = a.Id, Inbox = true }], space.Settings.MailAccounts);
        CatchallAliases.Create(db, c, "shop", null, null);
        MailStore.ReplaceFolders(db, a.Id, [("INBOX", "INBOX", "/", "inbox", true), ("Archiv", "Archiv", "/", null, true), ("Junk", "Junk", "/", "junk", true)]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (folder, uid, mid, to) in new[] { ("INBOX", 1, "<m1@x>", "shop@eins.example"), ("Archiv", 2, "<m2@x>", "shop@eins.example"),
                     ("INBOX", 3, "<m3@x>", "neu@eins.example"), ("INBOX", 4, "<m4@x>", "ich@mailbox.example"), ("Junk", 5, "<m5@x>", "shop@eins.example") })
        {
            db.Exec(@"INSERT INTO mail(account,folder,uid,message_id,subject,from_addr,recipients,date,size,keywords)
                      VALUES($a,$f,$u,$m,'Betreff','info@shop.example',$r,$d,10,'')",
                ("$a", a.Id), ("$f", folder), ("$u", uid), ("$m", mid), ("$r", "{\"to\":[{\"name\":null,\"address\":\"" + to + "\"}],\"cc\":[]}"), ("$d", now - uid));
            if (to.EndsWith("@eins.example")) CatchallAliases.Hit(db, c, to, mid, "info@shop.example", null, DateTimeOffset.UtcNow);
        }

        // Alias über Posteingang und Archiv, ohne Junk
        var (total, _, items) = MailStore.Query(db, CatchallAliases.View(db, "ca:shop@eins.example")!, 0, 50);
        Assert.Equal(2, total);
        Assert.Equal(["<m1@x>", "<m2@x>"], items.Select(i => i.MessageId).Order());
        // Die ganze Domain, auch nicht angelegte Aliase
        Assert.Equal(3, MailStore.Query(db, CatchallAliases.View(db, "ca:@eins.example")!, 0, 50).Total);
        Assert.Null(CatchallAliases.View(db, "ca:@fremd.example"));
        // Zähler für den Ordnerbereich: Domain und angelegte Aliase
        Assert.Equal(["ca:@eins.example", "ca:shop@eins.example"], CatchallAliases.Views(db));

        // Die Mails liegen weiter im Posteingang - einmal
        Assert.Equal(3, MailStore.List(db, a.Id, "INBOX", 0, 50, null).Total);

        // Spalte „An“: filtern nach Alias, sortieren nach „An“
        Assert.Equal(["<m1@x>"], MailStore.List(db, a.Id, "INBOX", 0, 50, "to:shop@eins.example").Items.Select(i => i.MessageId));
        Assert.Equal(["<m4@x>"], MailStore.List(db, a.Id, "INBOX", 0, 50, "to:ich@mailbox.example").Items.Select(i => i.MessageId));
        Assert.Equal(["<m4@x>", "<m3@x>", "<m1@x>"], MailStore.List(db, a.Id, "INBOX", 0, 50, null, "to").Items.Select(i => i.MessageId));
        // Filter nach Absender (Klick auf Bild oder Namen), Groß-/Kleinschreibung egal
        Assert.Equal(3, MailStore.List(db, a.Id, "INBOX", 0, 50, "from:Info@Shop.example").Total);
        Assert.Equal(0, MailStore.List(db, a.Id, "INBOX", 0, 50, "from:jemand@anders.example").Total);
    }

    [Fact]
    public void Signatur_Alias_vor_Domain_vor_Konto()
    {
        var all = new List<MailSignatures.Signature>
        {
            new() { Id = "k", Name = "Konto", Accounts = ["a"] },
            new() { Id = "d", Name = "Domain", Domains = ["eins.example"] },
            new() { Id = "x", Name = "Alias", Aliases = ["shop@eins.example"] },
        };
        Assert.Equal("x", MailSignatures.Resolve(all, "a", "Shop@Eins.Example")?.Id);
        Assert.Equal("d", MailSignatures.Resolve(all, "a", "anderer@eins.example")?.Id);
        Assert.Equal("k", MailSignatures.Resolve(all, "a", "x@zwei.example")?.Id);
        Assert.Equal("k", MailSignatures.Resolve(all, "a", null)?.Id);
        Assert.Null(MailSignatures.Resolve(all, "b", null));
    }
}
