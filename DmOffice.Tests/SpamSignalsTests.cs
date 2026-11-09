using System.Text;
using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Spamfilter: Echtheit (DMARC/DKIM), Markenimitation, verschleierte Namen, Zufallskennungen - und wie sie die Einstufung ändern.</summary>
public class SpamSignalsTests
{
    private static MimeMessage Mail(string from, string subject, string? auth = null, string by = "mx.gmx.net", bool list = false)
    {
        var sb = new StringBuilder();
        if (auth != null) sb.Append($"Authentication-Results: {auth}\r\n");
        sb.Append($"Received: from mail-out.example ([203.0.113.7]) by {by} with ESMTPS; Tue, 27 Oct 2026 09:04:00 +0100\r\n");
        sb.Append($"From: {from}\r\nTo: dimes@gmx.de\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@x>\r\n");
        if (list) sb.Append("List-Unsubscribe: <https://example.invalid/u>\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\n\r\nHallo\r\n");
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static readonly SpamSettings Settings = new();

    [Fact]
    public void FRITZ_Geschenk_ist_eindeutig_Junk()
    {
        var m = Mail("\"F R I T Z !\" <erltJ2FJD@hub.advantageaff.com>", "Ihr FRITZ! Geschenk wartet __ID:ia3q12854589s6",
            "mx.gmx.net; dkim=pass header.d=advantageaff.com; dmarc=pass header.from=hub.advantageaff.com", list: true);
        var r = SpamSignals.Of(m);
        Assert.True(r.Obfuscated);
        Assert.True(r.RandomId);
        // Auch wenn der lernende Filter die Mail für harmlos hält: verschleierter Name mit Zufallskennung
        var (score, rule) = SpamSignals.Adjust(0.05, r, trusted: false, own: false, Settings);
        Assert.True(score >= Settings.SpamCutoff);
        Assert.Equal("obfuscated-random", rule);
        Assert.Contains("sig:obfuscated-name", SpamSignals.Tokens(r));
    }

    [Theory]
    [InlineData("Paul Fritz <paul@gmx.de>", false)]                        // Personenname
    [InlineData("\"Cloud-Support-Team\" <exklusiv-angebot@zohomail.com>", true)]
    [InlineData("\"ADAC Umfrage-Belohnung\" <exklusiv-angebot@zohomail.com>", true)]
    [InlineData("\"Kundenservice\" <info@firma.example>", false)]         // eigene Domain, kein Freemail
    [InlineData("\"Malerbetrieb Huber\" <huber.maler@gmail.com>", false)]
    public void Freemail_mit_Firmenname(string from, bool expected)
    {
        var a = MimeKit.MailboxAddress.Parse(from);
        Assert.Equal(expected, SpamSignals.FreemailOrgName(a.Name, PhishCheck.DomainOf(a.Address)));
    }

    [Fact]
    public void Eigene_FRITZBox_bleibt_unauffaellig()
    {
        // Die Push-Mail der eigenen FRITZ!Box kommt von der eigenen Adresse
        var r = SpamSignals.Of(Mail("\"FRITZ!Box\" <dimes@gmx.de>", "FRITZ!Box Info"));
        Assert.Equal((0.1, (string?)null), SpamSignals.Adjust(0.1, r, trusted: false, own: true, Settings));
    }

    [Fact]
    public void Bestaetigte_Marken_Absender_sind_nicht_unsicher()
    {
        var m = Mail("Google <no-reply@accounts.google.com>", "Sicherheitswarnung für Ihr verknüpftes Google-Konto",
            "mx.gmx.net; dkim=pass header.d=accounts.google.com; spf=pass; dmarc=pass header.from=accounts.google.com");
        var r = SpamSignals.Of(m);
        Assert.True(r.VerifiedBrand);
        Assert.Contains("google.com", r.DkimDomains);
        var (score, rule) = SpamSignals.Adjust(0.62, r, false, false, Settings);
        Assert.True(score < Settings.HamCutoff);
        Assert.Equal("verified-brand", rule);
        // Was der Filter klar als Junk kennt (etwa Kalender-Spam über Google), bleibt Junk
        Assert.Equal(0.95, SpamSignals.Adjust(0.95, r, false, false, Settings).Score);
        Assert.Contains("sig:dkim:google.com", SpamSignals.Tokens(r));
    }

    [Fact]
    public void Google_Sicherheitswarnung_im_Microsoft_Konto()
    {
        // Wie ein echtes Beispiel (live.de): Exchange Online schreibt Authentication-Results ohne Serverkennung
        var m = Mail("Google <no-reply@accounts.google.com>", "Sicherheitswarnung für dimesde@gmail.com",
            "spf=pass (sender IP is 209.85.220.73) smtp.mailfrom=accounts.google.com; dkim=pass (signature was verified) header.d=accounts.google.com;dmarc=pass action=none header.from=accounts.google.com;compauth=pass reason=100",
            by: "AM0PR02MB1234.eurprd02.prod.outlook.com");
        var r = SpamSignals.Of(m);
        Assert.True(r.VerifiedBrand);
        // Der lernende Filter sagte 23 % („unsicher“) - jetzt gut
        var (score, rule) = SpamSignals.Adjust(0.23, r, false, false, Settings);
        Assert.True(score < Settings.HamCutoff);
        Assert.Equal("verified-brand", rule);
    }

    [Fact]
    public void Selbst_geschriebene_Pruefergebnisse_zaehlen_nicht()
    {
        // Authentication-Results mit fremder Kennung kann jeder Absender hineinschreiben
        var m = Mail("Google <security@g00gle-konto.example>", "Ihr Konto wurde gesperrt",
            "evil.example; dkim=pass header.d=google.com; dmarc=pass header.from=google.com");
        var r = SpamSignals.Of(m);
        Assert.False(r.VerifiedBrand);
        Assert.Null(r.VerifiedDomain);
        // Kein „bestätigter Absender“ - der Wert des Filters bleibt, wie er ist
        Assert.Equal((0.5, (string?)null), SpamSignals.Adjust(0.5, r, false, false, Settings));
    }

    [Fact]
    public void Gefaelschte_Absenderadresse_mindestens_unsicher()
    {
        var r = SpamSignals.Of(Mail("Chef <chef@firma.example>", "Bitte überweisen", "mx.gmx.net; spf=fail; dmarc=fail header.from=firma.example"));
        Assert.True(r.DmarcFailed);
        Assert.Equal((0.6, "dmarc-fail"), SpamSignals.Adjust(0.1, r, false, false, Settings));
    }

    [Fact]
    public void Bestaetigter_Absender_mit_mittlerem_Wert_nicht_unsicher()
    {
        // Newsletter einer Bank, vom eigenen Mailserver per DKIM bestätigt (über eine Weiterleitung angekommen)
        var m = Mail("Zinsbank <news@e.zinsbank.example>", "Gute Neuigkeiten! Wir erhöhen die Zinsen",
            "mx1.mailhost.example; dkim=pass (2048-bit key) header.d=e.zinsbank.example header.s=cd2", by: "mx1.mailhost.example");
        var r = SpamSignals.Of(m);
        Assert.Equal("zinsbank.example", r.VerifiedDomain);
        Assert.Equal((0.19, "verified-sender"), SpamSignals.Adjust(0.44, r, false, false, Settings));
        // Deutlich Junk bleibt es auch mit Signatur - Spammer signieren ihre eigenen Domains
        Assert.Equal((0.8, (string?)null), SpamSignals.Adjust(0.8, r, false, false, Settings));
        // Ohne Bestätigung bleibt ein mittlerer Wert „unsicher“
        Assert.Null(SpamSignals.Adjust(0.44, SpamSignals.Of(Mail("Zinsbank <news@e.zinsbank.example>", "Zinsen")), false, false, Settings).Rule);
    }

    [Fact]
    public void Sichere_Absender_nicht_unsicher()
    {
        var r = SpamSignals.Of(Mail("Anna <anna@example.org>", "Treffen"));
        Assert.Equal((0.19, "trusted"), SpamSignals.Adjust(0.5, r, trusted: true, own: false, Settings));
    }

    [Theory]
    [InlineData("Ihr FRITZ! Geschenk wartet __ID:ia3q12854589s6", true)]
    [InlineData("Gewinnbenachrichtigung #Ref: 8f3k29dk4x", true)]
    [InlineData("Rechnung Nr. 2026-10-123", false)]
    [InlineData("Ihre Bestellung 302-1234567-1234567", false)]
    [InlineData("Termin am Dienstag", false)]
    public void Zufallskennung_im_Betreff(string subject, bool random) => Assert.Equal(random, SpamSignals.Of(Mail("a <a@b.example>", subject)).RandomId);

    /// <summary>Wie echte Beispiele: kostenloses Zoho-Postfach, Firmenname, an eine fremde Adresse, Bcc an einen selbst.</summary>
    private static MimeMessage Zoho(string name, string subject, string link, bool flagged)
    {
        var sb = new StringBuilder();
        sb.Append("Authentication-Results: gmx.net; dkim=pass header.i=exklusiv-angebot@zohomail.com header.s=zm2022; spf=softfail; dmarc=pass header.from=zohomail.com\r\n");
        sb.Append("Received: from 139-162-173-130.fonic.online ([139.162.173.130]) by mx-ha.gmx.net (mxgmx006) with ESMTP; Mon, 26 Oct 2026 09:30:20 +0100\r\n");
        sb.Append("Received: from mail.zoho.com by mx.zohomail.com with SMTP; Mon, 26 Oct 2026 00:40:49 -0700\r\n");
        sb.Append($"From: {name} <exklusiv-angebot@zohomail.com>\r\nTo: \"fremd\" <fremde.person@gmail.example>\r\nSubject: {subject}\r\n");
        sb.Append($"Message-ID: <{Guid.NewGuid():N}@zohomail.com>\r\nEnvelope-To: <dimes@gmx.de>\r\n");
        if (flagged) sb.Append("X-Spam-Flag: YES\r\n");
        sb.Append($"Content-Type: text/html; charset=utf-8\r\n\r\n<p>Guten Tag,</p><a href=\"https://w.linkhd.example/x\">{link}</a>\r\n");
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    [Fact]
    public void Cloud_Phishing_von_Freemail_wird_erkannt()
    {
        var m = Zoho("Cloud-Support-Team", "Letzte Warnung: Ihre Cloud-Daten werden gelöscht", "Zahlung aktualisieren & Daten behalten", flagged: true);
        var ctx = new PhishCheck.Context { OwnHosts = ["imap.gmx.net", "gmx.de"], OwnAddresses = ["dimes@gmx.de"] };
        var found = PhishCheck.Analyze(m, ctx);
        var f = Assert.Single(found, x => x.Kind == "freemail-org");
        Assert.True(f.Strong);
        Assert.Equal("not-to-me", f.Detail);
        // Der Zahlungslink zählt wie ein Anmeldelink
        Assert.Contains(found, x => x.Kind == "newsender");
        // Spamfilter: GMX hat sie als Spam gekennzeichnet - mindestens unsicher, und kein „bestätigter Absender“ (Freemail)
        var r = SpamSignals.Of(m, ["imap.gmx.net", "gmx.de"]);
        Assert.True(r.ServerSpam);
        Assert.True(r.FreemailOrg);
        Assert.Contains("srv-spam:yes", SpamHeaders.Tokens(m));
        Assert.Equal("server-spam", SpamSignals.Adjust(0.1, r, false, false, Settings).Rule);
    }

    [Fact]
    public void ADAC_Gewinnspiel_von_Freemail_wird_erkannt()
    {
        var m = Zoho("ADAC Umfrage-Belohnung", "ADAC: Ihr persönlicher Vorteil – jetzt entdecken!", "Jetzt teilnehmen", flagged: false);
        var found = PhishCheck.Analyze(m, new PhishCheck.Context { OwnHosts = ["imap.gmx.net", "gmx.de"], OwnAddresses = ["dimes@gmx.de"] });
        Assert.True(Assert.Single(found, x => x.Kind == "freemail-org").Strong);
        // Spamfilter: Firmenname von Freemail, nicht an einen selbst - mindestens unsicher, auch mit DMARC „pass“ für zohomail.com
        var r = SpamSignals.Of(m, ["imap.gmx.net", "gmx.de"]) with { NotToMe = true };
        Assert.Equal("zohomail.com", r.VerifiedDomain);
        Assert.Equal("freemail-org", SpamSignals.Adjust(0.3, r, false, false, Settings).Rule);
        Assert.True(SpamSignals.NotToMe(m, ["dimes@gmx.de"]));
    }
}
