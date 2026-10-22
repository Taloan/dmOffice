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
