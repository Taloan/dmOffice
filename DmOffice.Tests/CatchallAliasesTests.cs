using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Catchall-Aliase: Zuordnung über verschiedene Kopfzeilen (auch nach Weiterleitung), Sperren, unbekannte Adressen, Zufallsnamen.</summary>
[Collection("server")]
public class CatchallAliasesTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CatchallAliasesTests(ChatFixture fx) => _fx = fx;

    private static readonly string[] Domains = ["alias.example"];

    private static MimeMessage Mail(string headers, string from = "Shop <info@shop.example>", string? id = null)
    {
        var sb = new StringBuilder(headers);
        sb.Append($"From: {from}\r\nSubject: Angebot\r\nMessage-ID: <{id ?? Guid.NewGuid().ToString("N")}@shop.example>\r\n");
        sb.Append("Date: Tue, 27 Oct 2026 09:04:00 +0100\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHallo\r\n");
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string? Find(MimeMessage m) => CatchallAliases.Of(new CatchallAliases.Config { Domains = [.. Domains] }, m);

    private (UserSpace Space, MailAccount Account) Setup(string login, string unknown = "accept")
    {
        var space = _fx.Space(login);
        var account = space.Settings.MailAccounts.FirstOrDefault(a => a.Id == "acc-" + login);
        if (account == null)
        {
            account = new MailAccount { Id = "acc-" + login, Name = "Privat", Email = login + "@mailbox.example" };
            space.UpdateSettings(s => s.MailAccounts.Add(account));
        }
        CatchallAliases.Save(space.Db, new CatchallAliases.Config { Domains = ["Alias.Example"], Account = account.Id, Unknown = unknown }, space.Settings.MailAccounts);
        return (space, account);
    }

    [Fact]
    public void Zuordnung_ueber_Kopfzeilen()
    {
        // Direkt in An
        Assert.Equal("fuchs-7k2@alias.example", Find(Mail("To: Fuchs-7K2@Alias.Example\r\n")));
        // In Cc, An ist jemand anderes
        Assert.Equal("cc-weg@alias.example", Find(Mail("To: freund@other.example\r\nCc: \"Ich\" <cc-weg@alias.example>\r\n")));
        // Nur in den Umschlag-Kopfzeilen (Bcc, Verteiler): Delivered-To, X-Original-To, Envelope-To, X-Envelope-To
        Assert.Equal("bcc@alias.example", Find(Mail("Delivered-To: bcc@alias.example\r\nTo: undisclosed-recipients:;\r\n")));
        Assert.Equal("orig@alias.example", Find(Mail("X-Original-To: orig@alias.example\r\nTo: liste@verein.example\r\n")));
        Assert.Equal("env@alias.example", Find(Mail("Envelope-To: env@alias.example\r\nTo: liste@verein.example\r\n")));
        Assert.Equal("xenv@alias.example", Find(Mail("X-Envelope-To: <xenv@alias.example>\r\nTo: liste@verein.example\r\n")));
        // Umschlag geht vor An: dort steht bei Verteilern der Verteiler, beim Empfänger hier die eigentliche Adresse
        Assert.Equal("echt@alias.example", Find(Mail("X-Original-To: echt@alias.example\r\nTo: andere@alias.example\r\n")));
        // Zuletzt Received „for <…>“
        Assert.Equal("rcv@alias.example", Find(Mail("Received: from mx.shop.example by mx.hoster.example with ESMTPS id 4f2\r\n\tfor <rcv@alias.example>; Tue, 27 Oct 2026 09:04:00 +0100\r\nTo: liste@verein.example\r\n")));
        // Andere Domain, Unterdomain, nichts: kein Catchall-Alias
        Assert.Null(Find(Mail("To: ich@mailbox.example\r\n")));
        Assert.Null(Find(Mail("To: x@sub.alias.example\r\n")));
        Assert.Null(CatchallAliases.Of(new CatchallAliases.Config(), Mail("To: fuchs@alias.example\r\n")));
    }

    [Fact]
    public void Zuordnung_nach_Weiterleitung()
    {
        // Catch-all beim Hoster leitet an das eigene Postfach weiter: oben steht das Postfach, darunter die ursprüngliche Adresse
        var hoster = Mail("Delivered-To: ich@mailbox.example\r\nX-Forwarded-To: ich@mailbox.example\r\nX-Original-To: newsletter-3m8@alias.example\r\n" +
                          "Delivered-To: newsletter-3m8@alias.example\r\nTo: Kunde <kunde@shop.example>\r\n");
        Assert.Equal("newsletter-3m8@alias.example", Find(hoster));
        // Weiterleitung, die nur X-Forwarded-To mit dem Alias schreibt (manche Hoster: Ziel und Quelle vertauscht)
        Assert.Equal("fwd@alias.example", Find(Mail("Delivered-To: ich@mailbox.example\r\nX-Forwarded-To: fwd@alias.example\r\nTo: undisclosed-recipients:;\r\n")));
        // Weiterleitung ohne Umschlag-Kopfzeilen: dann An
        Assert.Equal("shop-9a4@alias.example", Find(Mail("Delivered-To: ich@mailbox.example\r\nTo: shop-9a4@alias.example\r\n")));
        // Envelope-To mit mehreren Adressen
        Assert.Equal("zwei@alias.example", Find(Mail("Envelope-To: ich@mailbox.example, zwei@alias.example\r\nTo: liste@verein.example\r\n")));
    }

    [Fact]
    public void Unbekannte_Adressen()
    {
        var (space, account) = Setup("cleo");
        var db = space.Db;
        var c = CatchallAliases.Load(db);
        Assert.Equal(["alias.example"], c.Domains);
        // Annehmen: nicht von selbst in die Liste (sonst legte Spam an erfundene Adressen Aliase und Ordner an), aber zum Übernehmen sichtbar
        var m = Mail("To: neu-1@alias.example\r\n");
        Assert.Null(CatchallAliases.Judge(db, account, m));
        CatchallAliases.Hit(db, c, "neu-1@alias.example", m.MessageId, "info@shop.example", "Shop", m.Date);
        Assert.Null(CatchallAliases.Get(db, "neu-1@alias.example"));
        Assert.Equal("unknown", CatchallAliases.StateOf(db, "neu-1@alias.example"));
        Assert.Equal(1, Assert.Single(CatchallAliases.Unknowns(db), u => u.Address == "neu-1@alias.example").Count);
        Assert.Null(CatchallAliases.Judge(db, account, m));
        // Übernehmen: jetzt ein Alias mit seinem Zähler, nicht mehr unter den unbekannten
        var a = CatchallAliases.Create(db, c, "neu-1@alias.example", "alias.example", "Shop");
        Assert.Equal(1, a.Count);
        Assert.Equal("open", CatchallAliases.StateOf(db, a.Address));
        Assert.DoesNotContain(CatchallAliases.Unknowns(db), u => u.Address == a.Address);

        // Gleich Junk: unbekannte nicht aufnehmen, sondern in Junk - selbst angelegte bleiben offen
        CatchallAliases.Save(db, new CatchallAliases.Config { Domains = ["alias.example"], Account = account.Id, Unknown = "junk" }, space.Settings.MailAccounts);
        c = CatchallAliases.Load(db);
        var fremd = Mail("Delivered-To: geraten@alias.example\r\nTo: geraten@alias.example\r\n");
        CatchallAliases.Hit(db, c, "geraten@alias.example", fremd.MessageId, null, null, fremd.Date);
        Assert.Null(CatchallAliases.Get(db, "geraten@alias.example"));
        Assert.Equal(new CatchallAliases.Verdict("geraten@alias.example", "junk", "alias-unknown"), CatchallAliases.Judge(db, account, fremd));
        Assert.Null(CatchallAliases.Judge(db, account, m));
        // Anderes Postfach: gilt dort nicht
        Assert.Null(CatchallAliases.Judge(db, new MailAccount { Id = "anders" }, fremd));
        // Im Spamfilter mit eigenem Grund
        var (score, rule, _) = SpamSignals.Judge(space, account, fremd, 0.05);
        Assert.Equal("alias-unknown", rule);
        Assert.True(score >= space.Settings.Spam.SpamCutoff);
    }

    [Fact]
    public void Sperren_und_entsperren()
    {
        var (space, account) = Setup("dora");
        var db = space.Db;
        var c = CatchallAliases.Load(db);
        var a = CatchallAliases.Create(db, c, "Gewinnspiel", null, "Gewinnspiel Baumarkt");
        Assert.Equal("gewinnspiel@alias.example", a.Address);
        Assert.Equal("Gewinnspiel Baumarkt", a.Note);
        Assert.False(a.Auto);
        var m = Mail("To: gewinnspiel@alias.example\r\n", "Werbung <promo@baumarkt.example>");
        Assert.Null(CatchallAliases.Judge(db, account, m));

        CatchallAliases.Update(db, a.Address, null, "junk", true);
        Assert.Equal(new CatchallAliases.Verdict(a.Address, "junk", "alias-blocked"), CatchallAliases.Judge(db, account, m));
        var (score, rule, _) = SpamSignals.Judge(space, account, m, null);
        Assert.Equal("alias-blocked", rule);
        Assert.True(score >= space.Settings.Spam.SpamCutoff);
        // Löschen statt Junk
        CatchallAliases.Update(db, a.Address, null, "delete", true);
        Assert.Equal("delete", CatchallAliases.Judge(db, account, m)!.Action);
        // Notiz ändern lässt die Sperre stehen
        Assert.Equal("delete", CatchallAliases.Update(db, a.Address, "Baumarkt", null, false).Blocked);
        // Entsperren
        Assert.Null(CatchallAliases.Update(db, a.Address, null, null, true).Blocked);
        Assert.Null(CatchallAliases.Judge(db, account, m));
        Assert.NotEqual("alias-blocked", SpamSignals.Judge(space, account, m, null).Rule);
    }

    [Fact]
    public void Zufallsnamen_und_Anlegen()
    {
        var rx = new Regex("^[a-z]+-[2-9][a-hjkmnp-z][2-9]$");
        var names = Enumerable.Range(0, 200).Select(_ => CatchallAliases.RandomName()).ToList();
        Assert.All(names, n => Assert.Matches(rx, n));
        Assert.True(names.Distinct().Count() > 150);

        var (space, _) = Setup("ben");
        var db = space.Db;
        var c = CatchallAliases.Load(db);
        var a = CatchallAliases.Create(db, c, null, null, "Forum");
        Assert.Matches(new Regex("^[a-z]+-[2-9][a-z][2-9]@alias\\.example$"), a.Address);
        Assert.NotEqual(a.Address, CatchallAliases.Create(db, c, "", null, null).Address);
        // Selbst gewählt; doppelt, ungültig, fremde Domain abgelehnt
        Assert.Equal("shop.de-2026@alias.example", CatchallAliases.Create(db, c, "Shop.de-2026", "alias.example", null).Address);
        Assert.Throws<ArgumentException>(() => CatchallAliases.Create(db, c, "shop.de-2026", null, null));
        Assert.Throws<ArgumentException>(() => CatchallAliases.Create(db, c, "mit leerzeichen", null, null));
        Assert.Throws<ArgumentException>(() => CatchallAliases.Create(db, c, ".punkt", null, null));
        Assert.Throws<ArgumentException>(() => CatchallAliases.Create(db, c, "ok", "fremd.example", null));
        // Von selbst aufgenommen: Anlegen übernimmt sie mit Notiz
        CatchallAliases.Hit(db, c, "auto@alias.example", "<x1@shop.example>", "a@shop.example", null, DateTimeOffset.UtcNow);
        var taken = CatchallAliases.Create(db, c, "auto", null, "Jetzt mit Notiz");
        Assert.False(taken.Auto);
        Assert.Equal(1, taken.Count);
        // Keine Domain: Fehler
        Assert.Throws<ArgumentException>(() => CatchallAliases.Create(db, new CatchallAliases.Config(), null, null, null));
        Assert.Throws<ArgumentException>(() => CatchallAliases.Save(db, new CatchallAliases.Config { Domains = ["kein domain"] }, space.Settings.MailAccounts));
    }

    [Fact]
    public void Uebersicht_Liste_und_Antworten_als_Alias()
    {
        var (space, account) = Setup("anna");
        var db = space.Db;
        var c = CatchallAliases.Load(db);
        var a = CatchallAliases.Create(db, c, "bahn", null, "Bahn-Konto");
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        CatchallAliases.Hit(db, c, a.Address, "<b1@bahn.example>", "Info@Bahn.example", "Bahn", t0);
        CatchallAliases.Hit(db, c, a.Address, "<b2@bahn.example>", "info@bahn.example", "Bahn", t0.AddDays(1));
        CatchallAliases.Hit(db, c, a.Address, "<b2@bahn.example>", "info@bahn.example", "Bahn", t0.AddDays(1));   // dieselbe Mail
        CatchallAliases.Hit(db, c, a.Address, "<x@partner.example>", "news@partner.example", null, t0.AddDays(2));
        var item = CatchallAliases.Get(db, a.Address)!;
        Assert.Equal(3, item.Count);
        Assert.Equal(t0.AddDays(2).ToUnixTimeMilliseconds(), item.Last!.Value.ToUnixTimeMilliseconds());
        var top = CatchallAliases.TopSenders(db, a.Address);
        Assert.Equal(("info@bahn.example", 2), (top[0].Address, top[0].Count));
        Assert.Equal("news@partner.example", top[1].Address);

        // Mailliste: „über …“ je Mail (über die Message-ID)
        var rows = new List<MailRow>
        {
            new(1, account.Id, "INBOX", 1, "<b1@bahn.example>", "Ticket", null, [], [], t0, 1, false, false, false, false, false, "", null, false, []),
            new(2, account.Id, "INBOX", 2, "<sonst@x.example>", "Hallo", null, [], [], t0, 1, false, false, false, false, false, "", null, false, []),
            new(3, "anderes", "INBOX", 3, "<b1@bahn.example>", "Kopie", null, [], [], t0, 1, false, false, false, false, false, "", null, false, []),
        };
        var tagged = CatchallAliases.Tag(db, rows);
        Assert.Equal(a.Address, tagged[0].Alias);
        Assert.Equal("open", tagged[0].AliasState);
        CatchallAliases.Update(db, a.Address, null, "junk", true);
        Assert.Equal("blocked", CatchallAliases.Tag(db, rows)[0].AliasState);
        CatchallAliases.Update(db, a.Address, null, null, true);
        Assert.Null(tagged[1].Alias);
        Assert.Null(tagged[2].Alias);

        // Antworten mit dem Alias: nur eingeschaltet, aus dem Postfach der Domain, nur bekannte Adressen
        Assert.False(CatchallAliases.CanSendAs(db, account, a.Address));
        CatchallAliases.Save(db, new CatchallAliases.Config { Domains = ["alias.example"], Account = account.Id, ReplyAs = true }, space.Settings.MailAccounts);
        Assert.True(CatchallAliases.CanSendAs(db, account, a.Address));
        Assert.False(CatchallAliases.CanSendAs(db, account, "unbekannt@alias.example"));
        Assert.False(CatchallAliases.CanSendAs(db, account, "bahn@mailbox.example"));
        Assert.False(CatchallAliases.CanSendAs(db, new MailAccount { Id = "anders" }, a.Address));
        var sent = MailCompose.Build(account, new MailCompose.Draft { To = "info@bahn.example", Subject = "Re: Ticket", Html = "Danke", From = a.Address }, []);
        Assert.Equal(a.Address, sent.From.Mailboxes.Single().Address);
        // Die Message-ID verrät das eigentliche Postfach nicht
        Assert.EndsWith("@alias.example", sent.MessageId);

        CatchallAliases.Delete(db, a.Address);
        Assert.Null(CatchallAliases.Get(db, a.Address));
        Assert.Empty(CatchallAliases.TopSenders(db, a.Address));
    }

    [Fact]
    public void Ordnernamen()
    {
        // Trennzeichen des Servers und IMAP-Platzhalter kommen in Ordnernamen nicht vor
        Assert.Equal("example.com", CatchallFolders.Safe("example.com", '/'));
        Assert.Equal("example_com", CatchallFolders.Safe("example.com", '.'));
        Assert.Equal("shop_de-2026", CatchallFolders.Safe("shop.de-2026", '.'));
        Assert.Equal("a_b_c", CatchallFolders.Safe("a*b%c", '/'));
        // Ordnerpfade gemerkt: Domain im kv, Alias in der Tabelle
        var (space, _) = Setup("dora");
        var a = CatchallAliases.Create(space.Db, CatchallAliases.Load(space.Db), "ordner", null, null);
        CatchallAliases.SetDomainFolder(space.Db, "alias.example", "alias.example");
        CatchallAliases.SetFolder(space.Db, a.Address, "alias.example/ordner");
        Assert.Equal("alias.example", CatchallAliases.DomainFolder(space.Db, "alias.example"));
        Assert.Equal("alias.example/ordner", CatchallAliases.Get(space.Db, a.Address)!.Folder);
        Assert.Equal(("ordner", "alias.example"), (CatchallAliases.LocalOf(a.Address), CatchallAliases.DomainOf(a.Address)));
    }

    [Fact]
    public void Entfernen_nach_Zeit_je_Alias()
    {
        var (space, account) = Setup("ben");
        var db = space.Db;
        var a = CatchallAliases.Create(db, CatchallAliases.Load(db), "newsletter", null, "Newsletter");
        var m = Mail("To: newsletter@alias.example\r\n");
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(CatchallAliases.ExpiryFor(a, account.Id, "alias.example/newsletter", 7, m, now));
        a = CatchallAliases.SetExpiry(db, a.Address, MailExpiry.Trash, 30);
        Assert.Equal((MailExpiry.Trash, 30), (a.ExpireKind, a.ExpireDays));
        var e = CatchallAliases.ExpiryFor(a, account.Id, "alias.example/newsletter", 7, m, now)!;
        Assert.Equal((account.Id, "alias.example/newsletter", 7u, m.MessageId, now.AddDays(30), MailExpiry.Trash), (e.Account, e.Folder, e.Uid, e.MessageId, e.At, e.Kind));
        Assert.Equal("Destroy", CatchallAliases.SetExpiry(db, a.Address, MailExpiry.Destroy, 1).ExpireKind == MailExpiry.Destroy ? "Destroy" : "?");
        Assert.Throws<ArgumentException>(() => CatchallAliases.SetExpiry(db, a.Address, MailExpiry.Trash, 0));
        Assert.Throws<ArgumentException>(() => CatchallAliases.SetExpiry(db, a.Address, "irgendwas", 5));
        // Sperren und Notiz lassen die Einstellung stehen, „aus“ nimmt sie weg
        Assert.Equal(1, CatchallAliases.Update(db, a.Address, "neu", "junk", true).ExpireDays);
        Assert.Null(CatchallAliases.SetExpiry(db, a.Address, "", null).ExpireKind);
    }

    [Fact]
    public async Task Api()
    {
        var http = _fx.As("cleo");
        Setup("cleo");
        var state = await (await http.GetAsync("/api/mail/aliases")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alias.example", state.GetProperty("config").GetProperty("domains")[0].GetString());
        var made = await http.PostAsJsonAsync("/api/mail/aliases", new { note = "Verein" });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var address = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("address").GetString()!;
        var blocked = await (await http.PutAsJsonAsync("/api/mail/aliases/item", new { address, block = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("junk", blocked.GetProperty("blocked").GetString());
        var item = await (await http.GetAsync("/api/mail/aliases/item?address=" + Uri.EscapeDataString(address))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Verein", item.GetProperty("alias").GetProperty("note").GetString());
        var timed = await (await http.PutAsJsonAsync("/api/mail/aliases/item", new { address, expireKind = "trash", expireDays = 14 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(14, timed.GetProperty("expireDays").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/mail/aliases/item", new { address, expireKind = "trash", expireDays = 0 })).StatusCode);
        var open = await (await http.PutAsJsonAsync("/api/mail/aliases/item", new { address, block = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, open.GetProperty("blocked").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/mail/aliases/config", new { domains = new[] { "nicht gültig" } })).StatusCode);
        // Löschen: ohne IMAP-Zugang nichts zu verschieben, danach ist der Alias weg
        var del = await http.DeleteAsync("/api/mail/aliases/item?address=" + Uri.EscapeDataString(address));
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(0, (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("moved").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync("/api/mail/aliases/item?address=" + Uri.EscapeDataString(address))).StatusCode);
    }
}
