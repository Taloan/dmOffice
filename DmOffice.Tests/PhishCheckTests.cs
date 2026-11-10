using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Phishing-Merkmale: Absenderpruefung, Linktext gegen Linkziel, aehnliche Domains, erstmaliger Absender.</summary>
public class PhishCheckTests
{
    private static readonly PhishCheck.Context Ctx = new()
    {
        KnownDomains = new HashSet<string> { "paypal.com", "sparkasse.de", "apple.com", "firma-mueller.de" },
        OwnHosts = ["imap.beispiel.de", "beispiel.de"],
    };

    /// <summary>Mail mit HTML-Text; headers werden oben eingefuegt, in der angegebenen Reihenfolge (erste = oberste).</summary>
    private static MimeMessage Mail(string from, string html = "<p>Hallo</p>", params (string Name, string Value)[] headers)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", from));
        m.To.Add(new MailboxAddress("Ich", "ich@beispiel.de"));
        m.Subject = "Test";
        m.Body = new TextPart("html") { Text = html };
        for (int i = headers.Length - 1; i >= 0; i--) m.Headers.Insert(0, headers[i].Name, headers[i].Value);
        return m;
    }

    private static List<PhishCheck.Finding> Check(MimeMessage m, PhishCheck.Context? ctx = null) => PhishCheck.Analyze(m, ctx ?? Ctx);

    // ---------------- Absenderpruefung ----------------

    [Fact]
    public void Dmarc_fehlgeschlagen_beim_eigenen_Mailserver_ist_ein_starkes_Merkmal()
    {
        var f = Assert.Single(Check(Mail("chef@firma.example", headers:
            ("Authentication-Results", "mx.beispiel.de; spf=pass smtp.mailfrom=firma.example; dkim=none; dmarc=fail (p=reject) header.from=firma.example"))));
        Assert.Equal("auth", f.Kind);
        Assert.True(f.Strong);
        Assert.Equal("dmarc", f.Detail);
        Assert.Equal("firma.example", f.Domain);
    }

    [Fact]
    public void Fremde_Authentication_Results_zaehlen_nicht()
    {
        // Vom Absender selbst hineingeschrieben: andere Kennung
        Assert.Empty(Check(Mail("chef@firma.example", headers:
            ("Authentication-Results", "mx.boese.example; dmarc=fail header.from=firma.example"))));
        // Die oberste Zeile des eigenen Servers gilt, eine darunter mit gleicher Kennung nicht
        Assert.Empty(Check(Mail("chef@firma.example", headers:
            [("Authentication-Results", "mx.beispiel.de; dmarc=pass header.from=firma.example"),
             ("Authentication-Results", "mx.beispiel.de; dmarc=fail header.from=firma.example")])));
    }

    [Fact]
    public void Kennung_der_obersten_Empfangsstation_gilt_als_eigener_Server()
    {
        var f = Assert.Single(Check(Mail("chef@firma.example", headers:
            [("Received", "by 2002:a05:6a10:1234 with SMTP id x; Wed, 21 Oct 2026 10:00:00 -0700"),
             ("Received", "from mail.firma.example (mail.firma.example [192.0.2.1]) by mx.google.com with ESMTPS id y; Wed, 21 Oct 2026 10:00:00 -0700"),
             ("Authentication-Results", "mx.google.com; dmarc=fail (p=NONE) header.from=firma.example")])));
        Assert.Equal("dmarc", f.Detail);
    }

    [Fact]
    public void Spf_fehlgeschlagen_ohne_Dmarc_ist_mittel_mit_Dkim_nichts()
    {
        var f = Assert.Single(Check(Mail("a@firma.example", headers: ("Authentication-Results", "mx.beispiel.de; spf=fail smtp.mailfrom=firma.example; dkim=none"))));
        Assert.Equal(("auth", false, "spf"), (f.Kind, f.Strong, f.Detail));
        Assert.Empty(Check(Mail("a@firma.example", headers: ("Authentication-Results", "mx.beispiel.de; spf=fail smtp.mailfrom=firma.example; dkim=pass header.d=firma.example"))));
        Assert.Empty(Check(Mail("a@firma.example", headers: ("Authentication-Results", "mx.beispiel.de; spf=softfail smtp.mailfrom=firma.example"))));
    }

    [Fact]
    public void Dmarc_fehlgeschlagen_nach_Weiterleitung_mit_Arc_ist_kein_Merkmal()
    {
        Assert.Empty(Check(Mail("a@firma.example", headers: ("Authentication-Results", "mx.beispiel.de; arc=pass; dmarc=fail header.from=firma.example"))));
    }

    [Fact]
    public void Exchange_ohne_Kennung_zaehlt_nur_bei_Microsoft_Konten()
    {
        var header = ("Authentication-Results", "spf=fail (sender IP is 192.0.2.1) smtp.mailfrom=firma.example; dkim=none (message not signed) header.d=none;dmarc=fail action=quarantine header.from=firma.example;compauth=fail reason=000");
        Assert.Empty(Check(Mail("a@firma.example", headers: header)));
        var ms = new PhishCheck.Context { KnownDomains = Ctx.KnownDomains, OwnHosts = ["outlook.office365.com", "firma.onmicrosoft.com"] };
        Assert.Equal("dmarc", Assert.Single(Check(Mail("a@firma.example", headers: header), ms)).Detail);
    }

    // ---------------- Linktext und Linkziel ----------------

    [Fact]
    public void Linktext_mit_anderer_Domain_als_das_Ziel_ist_ein_starkes_Merkmal()
    {
        var f = Assert.Single(Check(Mail("info@bank.example", "<p><a href=\"https://www.boese.example/s\">www.sparkasse.de</a></p>")));
        Assert.Equal(("link", true, "www.sparkasse.de", "www.boese.example"), (f.Kind, f.Strong, f.Shown, f.Target));
        // Auch mit https:// im Text
        Assert.Contains(Check(Mail("info@bank.example", "<a href=\"http://192.0.2.7/x\">https://www.paypal.com/signin</a>")),
            x => x.Kind == "link" && x.Target == "192.0.2.7");
    }

    [Fact]
    public void Gleiche_Domain_oder_gewoehnlicher_Linktext_ist_kein_Merkmal()
    {
        Assert.Empty(Check(Mail("info@bank.example",
            "<a href=\"https://banking.sparkasse.de/x\">www.sparkasse.de</a> <a href=\"https://boese.example\">Hier klicken</a>" +
            " <a href=\"https://boese.example/b.pdf\">bericht.pdf</a> <a href=\"mailto:a@b.example\">www.sparkasse.de</a>")));
    }

    [Fact]
    public void Bestaetigter_Newsletter_mit_Zaehl_Links_ist_nur_ein_Hinweis()
    {
        var html = "<a href=\"https://click.versand.example/abc\">www.shop.example</a>";
        var dmarcPass = ("Authentication-Results", "mx.beispiel.de; dmarc=pass header.from=shop.example");
        var unsub = ("List-Unsubscribe", "<https://shop.example/abmelden>");
        Assert.False(Assert.Single(Check(Mail("news@shop.example", html, dmarcPass, unsub))).Strong);
        // Ohne bestandenes DMARC bleibt es eine Warnung
        Assert.True(Assert.Single(Check(Mail("news@shop.example", html, unsub))).Strong);
    }

    // ---------------- Aehnliche Domains ----------------

    [Theory]
    [InlineData("service@paypa1.com", "paypal.com")]          // Ziffer statt Buchstabe
    [InlineData("info@sparkase.de", "sparkasse.de")]          // ein Zeichen weniger
    [InlineData("info@firma-meuller.de", "firma-mueller.de")] // vertauscht, bekannt aus dem Adressbuch
    [InlineData("id@xn--80ak6aa92e.com", "apple.com")]        // ganz kyrillisch: "аррӏе.com"
    public void Absender_mit_aehnlicher_Domain(string from, string like)
    {
        var f = Assert.Single(Check(Mail(from)));
        Assert.Equal(("lookalike", true, "sender", like), (f.Kind, f.Strong, f.Detail, f.Like));
    }

    [Fact]
    public void Gemischte_Schriften_und_vorgeschobene_bekannte_Domain_im_Link()
    {
        // "pаypal.com" mit kyrillischem а
        var mixed = new System.Globalization.IdnMapping().GetAscii("pаypal.com");
        var found = Check(Mail("info@shop.example",
            $"<a href=\"https://{mixed}/\">Login</a> <a href=\"https://paypal.com.konto-check.example/\">Login</a>"));
        Assert.Contains(found, f => f.Kind == "lookalike" && f.Detail == "mixed" && f.Domain == "pаypal.com");
        Assert.Contains(found, f => f.Kind == "lookalike" && f.Detail == "link" && f.Like == "paypal.com" && f.Domain == "paypal.com.konto-check.example");
    }

    [Fact]
    public void Bekannte_Domains_und_Unterdomains_sind_keine_Nachahmung()
    {
        Assert.Empty(Check(Mail("service@paypal.com", "<a href=\"https://www.paypal.com/x\">Angebote</a> <a href=\"https://mail.sparkasse.de\">Bank</a>")));
        // Kurze Namen gleichen einander zu oft zufaellig
        Assert.Empty(Check(Mail("a@appel.com")));
    }

    [Fact]
    public void Anderer_erster_Buchstabe_nur_bei_aehnlichem_Zeichen()
    {
        // pCloud ist ein eigener Dienst, kein nachgeahmtes iCloud
        var ctx = new PhishCheck.Context { KnownDomains = new HashSet<string>(Ctx.KnownDomains) { "icloud.com" }, OwnHosts = Ctx.OwnHosts };
        Assert.DoesNotContain(Check(Mail("team@pcloud.com", "<a href=\"https://e.pcloud.com/x\">Konto</a>"), ctx), f => f.Kind == "lookalike");
        // l statt i am Anfang sieht gleich aus
        Assert.Equal("icloud.com", Assert.Single(Check(Mail("id@lcloud.com"), ctx)).Like);
    }

    // ---------------- Erstmaliger Absender ----------------

    [Fact]
    public void Erstmaliger_Absender_mit_Anmeldelink()
    {
        var html = "<p>Ihr Zugang wurde gesperrt.</p><a href=\"https://hilfe.example/x\">Jetzt anmelden</a>";
        var f = Assert.Single(Check(Mail("Support@Hilfe.example", html)));
        Assert.Equal(("newsender", false, "support@hilfe.example", "hilfe.example"), (f.Kind, f.Strong, f.Domain, f.Target));
        // Auch am Ziel erkannt
        Assert.Single(Check(Mail("a@hilfe.example", "<a href=\"https://hilfe.example/account/verify\">Hier</a>")));
        // Bekannter Absender oder schon frueher geschrieben: kein Merkmal
        Assert.Empty(Check(Mail("a@hilfe.example", html), new PhishCheck.Context { KnownSender = true, KnownDomains = Ctx.KnownDomains }));
        Assert.Empty(Check(Mail("a@hilfe.example", html), new PhishCheck.Context { WroteBefore = () => true, KnownDomains = Ctx.KnownDomains }));
        // Ohne Anmeldelink kein Merkmal
        Assert.Empty(Check(Mail("a@hilfe.example", "<a href=\"https://hilfe.example/rezept\">Zum Rezept</a>")));
    }

    // ---------------- Cloud-Speicher, versteckte Adresse, Drohung ----------------

    [Fact]
    public void Bankimitat_von_sauberem_Server_wird_an_Speicherlink_Adresse_und_Frist_erkannt()
    {
        // Echte Vorlage: Volksbank-Logo, Absender eines gekaperten Servers (SPF und DKIM bestanden), Button auf Amazon S3,
        // ref = Base64 der eigenen Adresse, Drohung mit Sperre und Frist
        var html = "<p>Unserer Datenbank zufolge ist die Bestätigung auf Ihrer Seite noch ausstehend.</p>"
            + "<p>Bitte erledigen Sie den Vorgang innerhalb von 7 Tagen. Falls nicht, werden wir Ihre Transaktionsfunktionen vorläufig sperren.</p>"
            + "<p>Frist: 27.10.2026</p><a href=\"https://s3.eu-west-3.amazonaws.com/cdn-vw64ylc3-456356/7cbga4vi0n?ref=" + Convert.ToBase64String("ich@beispiel.de"u8.ToArray()) + "\">Vorgang durchführen</a>";
        var found = Check(Mail("admin@gekapert.example", html, ("Authentication-Results",
            "beispiel.de; dkim=pass header.i=@gekapert.example; spf=pass smtp.mailfrom=admin@gekapert.example; dmarc=none header.from=gekapert.example")),
            new PhishCheck.Context { KnownDomains = Ctx.KnownDomains, OwnHosts = Ctx.OwnHosts, OwnAddresses = ["ich@beispiel.de"] });
        Assert.Equal(["hosted", "myaddress", "pressure"], found.Select(f => f.Kind));
        Assert.All(found, f => Assert.False(f.Strong));
        Assert.Equal(("Vorgang durchführen", "s3.eu-west-3.amazonaws.com"), (found[0].Shown, found[0].Target));
        Assert.Equal("ich@beispiel.de", found[1].Domain);

        // Bekannter Absender: nichts davon
        Assert.Empty(Check(Mail("admin@gekapert.example", html), new PhishCheck.Context { KnownSender = true, OwnAddresses = ["ich@beispiel.de"] }));
    }

    [Fact]
    public void Speicherlink_Adresse_und_Frist_einzeln_nicht_ueberempfindlich()
    {
        // Offen im Abmeldelink stehende Adresse zählt nicht, nur versteckte
        Assert.Empty(Check(Mail("news@shop.example", "<a href=\"https://shop.example/abmelden?email=ich@beispiel.de\">Abmelden</a>")));
        // Seite auf dem eigenen Speicher des Absenders: kein Hinweis
        Assert.Empty(Check(Mail("team@netlify.app", "<a href=\"https://docs.netlify.app/x\">Doku</a>")));
        // Frist ohne Drohung (Rechnung) und Drohung ohne Frist: kein Hinweis
        Assert.Empty(Check(Mail("rechnung@firma.example", "<p>Bitte zahlen Sie innerhalb von 14 Tagen.</p><a href=\"https://firma.example/r\">Rechnung</a>")));
        Assert.Empty(Check(Mail("info@firma.example", "<p>Gestohlene Karten werden gesperrt.</p><a href=\"https://firma.example/r\">Mehr</a>")));
        // Bucket-Link ohne S3 im Namen ist kein gemieteter Speicher
        Assert.Empty(Check(Mail("info@firma.example", "<a href=\"https://ec2-1-2-3-4.compute.amazonaws.com/x\">x</a>")));
    }

    [Fact]
    public void Ueber_SimpleLogin_weitergeleitet()
    {
        // SimpleLogin ersetzt den Absender durch eine Antwortadresse; der Mailserver bestätigt simplelogin.co
        MimeMessage Sl(string html, string? auth = "dkim=pass header.d=simplelogin.co; dmarc=pass header.from=simplelogin.co")
        {
            var m = auth == null ? Mail("x@y.example", html) : Mail("x@y.example", html, ("Authentication-Results", "imap.beispiel.de; " + auth));
            m.From.Clear();
            m.From.Add(new MailboxAddress("Amazon.de - shipment-tracking at amazon.de", "shipment-tracking_at_amazon_de_abcdef@simplelogin.co"));
            return m;
        }
        var ctx = new PhishCheck.Context { KnownDomains = new HashSet<string> { "amazon.de" }, OwnHosts = Ctx.OwnHosts };
        // Anmeldelink auf die Seite des eigentlichen Absenders (amazon.de): kein Hinweis
        Assert.Empty(Check(Sl("<a href=\"https://www.amazon.de/ap/signin\">Anmelden</a>"), ctx));
        // Anmeldelink woandershin: Hinweis mit dem eigentlichen Absender und dem Dienst
        var f = Assert.Single(Check(Sl("<a href=\"https://amazon-konto.example/login\">Anmelden</a>"), ctx));
        Assert.Equal(("newsender", "shipment-tracking@amazon.de", "SimpleLogin", "amazon-konto.example"), (f.Kind, f.Domain, f.Detail, f.Target));
        // Ohne Bestätigung des Dienstes: Hinweis mit der Antwortadresse, wie bei jedem anderen Absender
        Assert.Equal("shipment-tracking_at_amazon_de_abcdef@simplelogin.co",
            Assert.Single(Check(Sl("<a href=\"https://amazon-konto.example/login\">Anmelden</a>", auth: null), ctx)).Domain);
        // Ablesen der Adresse
        Assert.Equal(("SimpleLogin", "shipment-tracking@amazon.de", "amazon.de"), Forwarders.Decode("shipment-tracking_at_amazon_de_abcdef@simplelogin.co", null) is { } i ? (i.Service, i.Original, i.Domain) : default);
        Assert.Equal("info@shop.example", Forwarders.Decode("alias+info=shop.example@addy.io", null)?.Original);
        Assert.Equal("news@firma.example", Forwarders.Decode("abc123@simplelogin.co", "Firma - news at firma.example")?.Original);
        Assert.Null(Forwarders.Decode("info@amazon.de", "Amazon"));
        // Firefox Relay: eigene Tarnadresse als Absender, der eigentliche steht im Namen
        var relay = Forwarders.Decode("abc123xy@mozmail.com", "\"info@shop.example [via Relay]\"");
        Assert.Equal(("Firefox Relay", "info@shop.example", false), (relay!.Service, relay.Original, relay.PerSender));
        Assert.Null(Forwarders.Decode("abc123xy@mozmail.com", "Shop"));
        Assert.Null(Forwarders.Decode("kaputt_at_@simplelogin.co", null));
    }

    [Theory]
    [InlineData("mail.google.com", "google.com")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("finanzonline.bmf.gv.at", "bmf.gv.at")]
    [InlineData("example.de", "example.de")]
    [InlineData("192.0.2.1", "192.0.2.1")]
    public void Registrierbare_Domain(string host, string expected) => Assert.Equal(expected, PhishCheck.Registrable(host));

    // ---------------- Mit Benutzerdatenbank ----------------

    [Fact]
    public void Adressbuch_und_fruehere_Mails_fliessen_ein()
    {
        var dir = Directory.CreateTempSubdirectory("dmphish");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        var account = new MailAccount { Id = "a", Email = "ich@beispiel.de", ImapHost = "imap.beispiel.de" };
        space.UpdateSettings(s => s.MailAccounts.Add(account));
        db.Exec("INSERT INTO contacts_fts(name, emails, org, phones) VALUES('Anna', 'anna@firma-mueller.de', '', '')");
        SafeSenders.Forget(db);

        // Nachgeahmte Domain eines Kontakts
        var f = Assert.Single(PhishCheck.Check(space, account, Mail("anna@firma-meuller.de")));
        Assert.Equal("firma-mueller.de", f.Like);

        // Erstmaliger Absender mit Anmeldelink - bis eine zweite Mail von ihm in der Datenbank liegt
        var login = Mail("info@neu.example", "<a href=\"https://neu.example/login\">Anmelden</a>");
        db.Exec("INSERT INTO mail(account,folder,uid,subject,date,recipients,from_addr) VALUES('a','INBOX',1,'x',1,'{}','Info@Neu.example')");
        Assert.Equal("newsender", Assert.Single(PhishCheck.Check(space, account, login)).Kind);
        db.Exec("INSERT INTO mail(account,folder,uid,subject,date,recipients,from_addr) VALUES('a','INBOX',2,'y',2,'{}','info@neu.example')");
        Assert.Empty(PhishCheck.Check(space, account, login));
    }
}
