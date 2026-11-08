using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Lernende Kategorien: Voreinstellungen, Lernen aus Setzen und Entfernen (je Kategorie ja/nein, mehrere je Mail),
/// fest gemerkte Absender, Übernahme der alten Smart Inbox.</summary>
public class SmartInboxTests
{
    private static readonly TimeZoneInfo Berlin = DmOffice.Dav.Ics.Zone("Europe/Berlin")!;

    private static UserSpace NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmsmart");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        return new UserSpace(new UserRecord { Login = "t" }, db);
    }

    private static SmartInbox.Doc Doc(string from, string subject, string text = "", bool unsubscribe = false, string name = "")
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(name, from));
        m.To.Add(new MailboxAddress("Alex", "alex@beispiel.de"));
        m.Subject = subject;
        m.MessageId = Guid.NewGuid().ToString("N") + "@test";
        m.Body = new TextPart("plain") { Text = text };
        if (unsubscribe) m.Headers.Add(HeaderId.ListUnsubscribe, "<https://example.com/unsub>");
        return SmartInbox.Of(m, Berlin);
    }

    [Theory]
    [InlineData("bestellung@shop.example", "Ihre Bestellung 4711 ist unterwegs", false, "orders")]
    [InlineData("rechnung@stadtwerke.example", "Ihre Rechnung für Oktober", false, "orders")]
    [InlineData("noreply@booking.com", "Your trip to Rome", false, "travel")]
    [InlineData("info@bahn.de", "Ihre Fahrkarte für Berlin", false, "travel")]
    [InlineData("notification@facebookmail.com", "Anna hat dich erwähnt", false, "social")]
    [InlineData("service@versicherung.example", "Ihre Police 2027", false, "contracts")]
    [InlineData("hallo@laden.example", "Herbstrabatt: 20 % auf alles", true, "newsletter")]
    [InlineData("noreply@github.com", "Build erfolgreich", false, "notify")]
    [InlineData("anna@beispiel.de", "Kaffee morgen?", false, "")]
    public void Regeln(string from, string subject, bool unsubscribe, string expected) =>
        Assert.Equal(expected, SmartInbox.Rules(Doc(from, subject, unsubscribe: unsubscribe)));

    [Fact]
    public void Voreinstellung_nur_fuer_lernende_Kategorien_mit_Preset()
    {
        var space = NewSpace();
        space.UpdateSettings(s => s.Categories =
        [
            new MailCategory { Key = "dm_bestellungen", Name = "Bestellungen", Smart = true, Preset = "orders" },
            new MailCategory { Key = "dm_rechnung", Name = "Rechnung" },
        ]);
        Assert.Equal(["dm_bestellungen"], SmartInbox.Decide(space, Doc("bestellung@shop.example", "Ihre Bestellung 4711")));
        Assert.Empty(SmartInbox.Decide(space, Doc("anna@beispiel.de", "Kaffee morgen?")));
    }

    [Fact]
    public void Lernt_aus_Setzen_und_Entfernen_mehrere_je_Mail()
    {
        var space = NewSpace();
        space.UpdateSettings(s => s.Categories =
        [
            new MailCategory { Key = "dm_schule", Name = "Schule", Smart = true },
            new MailCategory { Key = "dm_verein", Name = "Verein", Smart = true },
            new MailCategory { Key = "dm_bestellungen", Name = "Bestellungen", Smart = true, Preset = "orders" },
        ]);
        var db = space.Db;
        Assert.Empty(SmartInbox.Decide(space, Doc("sekretariat@gymnasium.example", "Elternabend am Donnerstag")));

        // Zweimal derselbe Absender „gehört dazu“: ab jetzt alles von dort
        SmartInbox.Train(db, Doc("sekretariat@gymnasium.example", "Elternabend"), "dm_schule", true);
        SmartInbox.Train(db, Doc("sekretariat@gymnasium.example", "Wandertag"), "dm_schule", true);
        Assert.Contains("dm_schule", SmartInbox.Decide(space, Doc("sekretariat@gymnasium.example", "Zeugnisausgabe")));

        // Zweimal „gehört nicht dazu“ schlägt die Voreinstellung
        SmartInbox.Train(db, Doc("info@laden.example", "Ihre Bestellung 1"), "dm_bestellungen", false);
        SmartInbox.Train(db, Doc("info@laden.example", "Ihre Bestellung 2"), "dm_bestellungen", false);
        Assert.DoesNotContain("dm_bestellungen", SmartInbox.Decide(space, Doc("info@laden.example", "Ihre Bestellung 3")));
        Assert.Contains("dm_bestellungen", SmartInbox.Decide(space, Doc("versand@anderer.example", "Ihre Bestellung 9")));

        // Wörter: genug Beispiele, dann tragen sie auch bei neuen Absendern - und eine Mail kann zwei Kategorien bekommen
        for (var i = 0; i < 4; i++)
        {
            SmartInbox.Train(db, Doc($"lehrer{i}@schule{i}.example", "Klassenfahrt Elternbrief", "Liebe Eltern, Klassenfahrt Elternbrief Klasse 7b"), "dm_schule", true);
            SmartInbox.Train(db, Doc($"vorstand{i}@verein{i}.example", "Mitgliederversammlung Vereinsheim", "Mitgliederversammlung Vereinsheim Beitrag"), "dm_verein", true);
            SmartInbox.Train(db, Doc($"kumpel{i}@web{i}.example", "Grillen am Wochenende", "Bier Grillen Fussball Wochenende"), "dm_schule", false);
        }
        Assert.Equal(["dm_schule"], SmartInbox.Decide(space, Doc("neu@grundschule.example", "Elternbrief Klassenfahrt", "Liebe Eltern, Klassenfahrt Klasse 7b")));
        Assert.Equal(["dm_verein"], SmartInbox.Decide(space, Doc("kasse@neuverein.example", "Mitgliederversammlung", "Mitgliederversammlung Vereinsheim Beitrag")));
        // Steht die Kategorie über den gelernten Absender fest, kommt keine Voreinstellung dazu
        SmartInbox.SetSender(db, "shop@verein.example", "dm_verein");
        Assert.Equal(["dm_verein"], SmartInbox.Decide(space, Doc("shop@verein.example", "Ihre Bestellung Vereinstrikot")));

        // Umlernen: dieselbe Mail andersherum zählt nicht doppelt
        var d = Doc("x@y.example", "Test");
        SmartInbox.Train(db, d, "dm_verein", true);
        SmartInbox.Train(db, d, "dm_verein", false);
        Assert.Equal(-1L, db.Scalar("SELECT label FROM label_trained WHERE key=$k AND cat='dm_verein'", ("$k", d.Key)));
    }

    [Fact]
    public void Hinter_SimpleLogin_zaehlt_der_eigentliche_Absender_und_nach_zwei_Zuordnungen_die_gewollte_Kategorie()
    {
        var space = NewSpace();
        space.UpdateSettings(s => s.Categories =
        [
            new MailCategory { Key = "dm_bestellungen", Name = "Bestellungen", Smart = true },
            new MailCategory { Key = "dm_rechnung", Name = "Rechnung", Smart = true },
            new MailCategory { Key = "dm_newsletter", Name = "Newsletter", Smart = true, Preset = "newsletter" },
        ]);
        var db = space.Db;
        // SimpleLogin: je Absender eine eigene Antwortadresse, dazu ein eigener Abmeldekopf (Alias abschalten)
        SmartInbox.Doc Amazon(string local, string subject, string text) =>
            Doc($"{local}_at_amazon_de_{Guid.NewGuid().ToString("N")[..6]}@simplelogin.co", subject, text, unsubscribe: true, name: $"Amazon.de - {local} at amazon.de");
        var first = Amazon("versandbestaetigung", "Versendet: Kaffeemaschine", "Dein Paket wurde versendet Summe 49,99 Bestellnr");
        Assert.Equal("versandbestaetigung@amazon.de", first.From);
        Assert.False(first.Newsletter);
        Assert.Equal("orders", SmartInbox.Rules(first));

        // Gelernt: Rechnungen (mit ähnlichen Wörtern) und echte Newsletter
        for (var i = 0; i < 4; i++)
        {
            SmartInbox.Train(db, Doc($"rechnung{i}@stadtwerke{i}.example", "Ihre Rechnung", "Rechnung Summe Betrag Bestellnr Zahlung"), "dm_rechnung", true);
            SmartInbox.Train(db, Doc($"news{i}@laden{i}.example", "Herbstrabatt", "Angebote Rabatt Summe sparen", unsubscribe: true), "dm_newsletter", true);
        }
        // Zwei Amazon-Mails (verschiedene Antwortadressen) von Hand zu „Bestellungen“
        SmartInbox.Train(db, first, "dm_bestellungen", true);
        SmartInbox.Train(db, Amazon("bestellbestaetigung", "Bestellt: Lichterkette", "Danke für deine Bestellung Summe 24,99"), "dm_bestellungen", true);

        // Die nächste Amazon-Mail über eine neue Antwortadresse: genau „Bestellungen“, nicht Newsletter oder Rechnung
        var next = Amazon("shipment-tracking", "Versendet: „ZCLIKEU LED Lichterkette…“", "Dein Paket wurde versendet Ankunft heute Summe 24,99 Bestellnr Verkauf durch");
        Assert.Equal(["dm_bestellungen"], SmartInbox.Decide(space, next));
    }

    [Theory]
    [InlineData("Bestellungen", "bestell")]
    [InlineData("Bestellung", "bestell")]
    [InlineData("Rechnungen", "rechnung")]
    [InlineData("Lieferungen", "liefer")]
    [InlineData("versendet", "versendet")]
    [InlineData("Häuser", "haus")]
    [InlineData("orders", "ord")]
    public void Wortstamm(string word, string stem) => Assert.Equal(stem, Stemmer.Stem(word));

    [Fact]
    public void Fuellwoerter_zaehlen_nicht() =>
        Assert.Equal(["bestellung", "versendet"], SpamFilter.Words("Ihre Bestellung wurde und ist versendet").ToList());

    [Fact]
    public void Fester_Absender_und_Hauptschalter()
    {
        var space = NewSpace();
        space.UpdateSettings(s => s.Categories = [new MailCategory { Key = "dm_laden", Name = "Laden", Smart = true }]);
        SmartInbox.SetSender(space.Db, "News@Laden.example", "dm_laden");
        Assert.Equal(["dm_laden"], SmartInbox.Decide(space, Doc("news@laden.example", "Herbstrabatt", unsubscribe: true)));
        // Lernt sie nicht mehr, setzt dmOffice sie auch nicht
        space.UpdateSettings(s => s.Categories![0].Smart = false);
        Assert.Empty(SmartInbox.Decide(space, Doc("news@laden.example", "Herbstrabatt", unsubscribe: true)));
        var acc = new MailAccount { Id = "a1" };
        Assert.True(space.Settings.CategoriesOn(acc));
        space.UpdateSettings(s => s.SmartCategories = false);
        Assert.False(space.Settings.CategoriesOn(acc));
    }

    [Fact]
    public void Uebernahme_der_alten_Smart_Inbox()
    {
        var space = NewSpace();
        var db = space.Db;
        // Alter Stand: Tabellen der Smart Inbox, „Reisen“ ausgeblendet, eine eigene Farbkategorie als Smart-Kategorie
        db.Exec(@"CREATE TABLE smart_mail (mail INTEGER PRIMARY KEY, cat TEXT NOT NULL);
CREATE TABLE smart_senders (address TEXT PRIMARY KEY, cat TEXT NOT NULL);
CREATE TABLE smart_tokens (token TEXT NOT NULL, cat TEXT NOT NULL, n INTEGER NOT NULL, PRIMARY KEY(token, cat)) WITHOUT ROWID;
CREATE TABLE smart_trained (key TEXT PRIMARY KEY, cat TEXT NOT NULL)");
        db.Exec("INSERT INTO mail(account, folder, uid, date, seen, flagged, keywords) VALUES('a1','INBOX',7,0,0,0,' ')");
        var mailId = (long)db.Scalar("SELECT id FROM mail WHERE uid=7")!;
        db.Exec("INSERT INTO smart_mail VALUES($m,'orders')", ("$m", mailId));
        db.Exec("INSERT INTO smart_senders VALUES('shop@x.example','orders')");
        db.Exec("INSERT INTO smart_tokens VALUES('s:bestellung','orders',3), ('s:elternabend','kw:dm_schule',2)");
        db.Exec("INSERT INTO smart_trained VALUES('k1','orders'), ('k2','kw:dm_schule'), ('k3','')");
        space.UpdateSettings(s => { s.SmartHidden = ["travel"]; s.Categories = [new MailCategory { Key = "dm_schule", Name = "Schule", Smart = true }]; });

        SmartInbox.Migrate(space);

        var cats = DmOffice.Settings.Categories.Of(space.Settings);
        Assert.Contains(cats, c => c.Key == "dm_bestellungen" && c.Smart && c.Preset == "orders");
        Assert.DoesNotContain(cats, c => c.Preset == "travel");
        Assert.True(space.Settings.CategoriesMigrated);
        Assert.Equal(3L, db.Scalar("SELECT pos FROM label_tokens WHERE token='s:bestellung' AND cat='dm_bestellungen'"));
        Assert.Equal(2L, db.Scalar("SELECT pos FROM label_tokens WHERE token='s:elternabend' AND cat='dm_schule'"));
        Assert.Equal(3L, db.Scalar("SELECT COUNT(*) FROM label_trained"));
        Assert.NotNull(db.Scalar("SELECT 1 FROM label_senders WHERE address='shop@x.example' AND cat='dm_bestellungen'"));
        // Die bisherige Zuordnung wartet aufs Schreiben zum Mailserver
        Assert.Equal(("a1", "INBOX", "dm_bestellungen", 7u), SmartInbox.Pending(db).Select(p => (p.Account, p.Folder, p.Cat, p.Uids.Single())).Single());
        Assert.Null(db.Scalar("SELECT 1 FROM sqlite_master WHERE name='smart_mail'"));
        // Ein zweites Mal ändert nichts
        SmartInbox.Migrate(space);
        Assert.Equal(cats.Count, DmOffice.Settings.Categories.Of(space.Settings).Count);
    }
}
