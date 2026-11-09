using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Sichere Absender (nur verifiziert), Adressbuch gegen gefälschte Absender, Lernen aus dem Junk-Ordner, gemeinsames Junk-Wissen.</summary>
[Collection("server")]
public class SpamTrustTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public SpamTrustTests(ChatFixture fx) => _fx = fx;

    private static MimeMessage Mail(string from, string subject, string? auth, string body = "Hallo", string? id = null)
    {
        var sb = new StringBuilder();
        if (auth != null) sb.Append($"Authentication-Results: mx.gmx.net; {auth}\r\n");
        sb.Append("Received: from mail-out.example ([203.0.113.7]) by mx.gmx.net with ESMTPS; Tue, 27 Oct 2026 09:04:00 +0100\r\n");
        sb.Append($"From: {from}\r\nTo: ich@gmx.example\r\nSubject: {subject}\r\nMessage-ID: <{id ?? Guid.NewGuid().ToString("N")}@x>\r\n");
        sb.Append($"Date: Tue, 20 Oct 2026 09:04:00 +0100\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n{body}\r\n");
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private const string SparkasseOk = "dkim=pass header.d=musterbank.example; dmarc=pass header.from=musterbank.example";

    [Fact]
    public void Sichere_Absender_nur_verifiziert()
    {
        var db = _fx.Space("anna").Db;
        var real = Mail("Musterbank <info@musterbank.example>", "Ihr Kontoauszug", SparkasseOk);
        var r = SpamSignals.Of(real);
        Assert.Equal("@musterbank.example", SafeList.Add(db, real, r, "domain"));
        Assert.True(SafeList.Trusts(db, real, r));
        // Gefälscht: dieselbe Adresse, aber ohne Bestätigung des Mailservers - kein Vertrauen
        var fake = Mail("Musterbank <info@musterbank.example>", "Konto gesperrt", "spf=fail; dmarc=fail header.from=musterbank.example");
        Assert.False(SafeList.Trusts(db, fake, SpamSignals.Of(fake)));
        // Nicht verifizierbar: Eintrag wird abgelehnt, mit dem vorgegebenen Satz
        var plain = Mail("Verein <info@kleiner-verein.example>", "Einladung", null);
        var ex = Assert.Throws<ArgumentException>(() => SafeList.Add(db, plain, SpamSignals.Of(plain), "address"));
        Assert.Equal("Mails von dieser Domain lassen sich nicht verifizieren – ein Eintrag hilft hier nicht.", ex.Message);
        // Freemail: nur die einzelne Adresse
        var gmail = Mail("Tante Inge <inge@gmail.com>", "Hallo", "dkim=pass header.d=gmail.com; dmarc=pass header.from=gmail.com");
        var g = SpamSignals.Of(gmail);
        Assert.Contains("vertrauen lässt sich dort nur einzelnen Adressen", Assert.Throws<ArgumentException>(() => SafeList.Add(db, gmail, g, "domain")).Message);
        Assert.Equal("inge@gmail.com", SafeList.Add(db, gmail, g, "address"));
        var other = Mail("Betrüger <betrug@gmail.com>", "Hallo", "dkim=pass header.d=gmail.com; dmarc=pass header.from=gmail.com");
        Assert.False(SafeList.Trusts(db, other, SpamSignals.Of(other)));
        // Alias-Dienste: nie die Domain des Dienstes; bei Firefox Relay (eigene Tarnadresse als Absender) gar nichts
        var sl = Mail("Amazon.de - shipment-tracking at amazon.de <shipment-tracking_at_amazon_de_abcdef@simplelogin.co>", "Versandt", "dkim=pass header.d=simplelogin.co; dmarc=pass header.from=simplelogin.co");
        Assert.Contains("nur einzelnen Adressen", Assert.Throws<ArgumentException>(() => SafeList.Add(db, sl, SpamSignals.Of(sl), "domain")).Message);
        Assert.Equal("shipment-tracking_at_amazon_de_abcdef@simplelogin.co", SafeList.Add(db, sl, SpamSignals.Of(sl), "address"));
        var rl = Mail("\"info@shop.example [via Relay]\" <abc123xy@mozmail.com>", "Angebot", "dkim=pass header.d=mozmail.com; dmarc=pass header.from=mozmail.com");
        Assert.Throws<ArgumentException>(() => SafeList.Add(db, rl, SpamSignals.Of(rl), "address"));
        SafeList.Remove(db, "@musterbank.example");
        Assert.False(SafeList.Trusts(db, real, r));
    }

    [Fact]
    public void Adressbuch_schuetzt_nicht_vor_gefaelschtem_Absender()
    {
        var space = _fx.Space("ben");
        // „Chef“ hat schon Post von Ben bekommen (Gesendet) - also sicherer Absender
        MailStore.ReplaceFolders(space.Db, "acc-ben", new() { ("Sent", "Sent", "/", "sent", true) });
        space.Db.Exec(@"INSERT INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,has_att)
                        VALUES('acc-ben','Sent',1,'<sent@test>','Hi','Ben','ben@example.org','{""to"":[{""address"":""chef@firma.example""}]}',$d,10,1,0,0)",
            ("$d", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        SafeSenders.Forget(space.Db);
        var echt = Mail("Chef <chef@firma.example>", "Meeting", "dkim=pass header.d=firma.example; dmarc=pass header.from=firma.example");
        Assert.Equal((true, "trusted"), SpamSignals.Trusted(space, echt, SpamSignals.Of(echt)));
        // Chef-Betrug: gleiche Adresse, DMARC gescheitert - kein sicherer Absender, mindestens unsicher
        var betrug = Mail("Chef <chef@firma.example>", "Bitte sofort überweisen", "spf=fail; dmarc=fail header.from=firma.example");
        var r = SpamSignals.Of(betrug);
        Assert.False(SpamSignals.Trusted(space, betrug, r).Trusted);
        Assert.Equal("dmarc-fail", SpamSignals.Adjust(0.1, r, false, false, space.Settings.Spam).Rule);
    }

    [Fact]
    public void Aus_dem_Junk_Ordner_lernen()
    {
        var space = _fx.Space("cleo");
        var db = space.Db;
        MailStore.ReplaceFolders(db, "acc-cleo", new() { ("Junk", "Junk", "/", "junk", true) });
        long old = DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeMilliseconds(), fresh = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        void Put(uint uid, MimeMessage m, long date)
        {
            var ms = new MemoryStream(); m.WriteTo(ms);
            db.Exec(@"INSERT INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,has_att,cached)
                      VALUES('acc-cleo','Junk',$u,$id,'x','x','x@x.example','{}',$d,10,0,0,0,1)", ("$u", (long)uid), ("$id", m.MessageId), ("$d", date));
            MailStore.StoreRaw(db, "acc-cleo", "Junk", uid, ms.ToArray(), m);
        }
        var a = Mail("Gewinn <win@spam.example>", "Sie haben gewonnen", null, "Jetzt Preis abholen");
        var b = Mail("Kredit <loan@spam.example>", "Sofortkredit", null, "Ohne Schufa");
        var c = Mail("Neu <new@spam.example>", "Noch zu frisch", null, "Erst seit zwei Stunden da");
        var d = Mail("Selbst <self@spam.example>", "Von dmOffice verschoben", null, "Nicht aus eigenem Urteil lernen");
        // Fehlgriff des Anbieters: nachweislich echte Mail von Google - nicht als Junk lernen
        var e = Mail("Google <no-reply@accounts.google.com>", "Sicherheitswarnung", "dkim=pass header.d=accounts.google.com; dmarc=pass header.from=accounts.google.com", "Neue Anmeldung");
        Put(1, a, old); Put(2, b, old); Put(3, c, fresh); Put(4, d, old); Put(5, e, old);
        JunkLearn.MarkMoved(db, d);
        Assert.Equal(2, JunkLearn.Run(space));
        Assert.Equal(2, SpamFilter.Counts(db).Spam);
        // Kein zweites Mal
        Assert.Equal(0, JunkLearn.Run(space));
        // Wieder fällig erst nach der eingestellten Zeit (Standard einmal am Tag, wahlweise alle 6 Stunden)
        Assert.False(JunkLearn.Due(space, DateTimeOffset.UtcNow.AddHours(7)));
        Assert.True(JunkLearn.Due(space, DateTimeOffset.UtcNow.AddHours(25)));
        space.UpdateSettings(s => s.Spam.JunkFolderHours = 6);
        Assert.True(JunkLearn.Due(space, DateTimeOffset.UtcNow.AddHours(7)));
        // Abschaltbar
        space.UpdateSettings(s => s.Spam.JunkFolderHours = 0);
        Assert.False(JunkLearn.Due(space, DateTimeOffset.UtcNow.AddDays(10)));
        Assert.Equal(0, JunkLearn.Run(space, DateTimeOffset.UtcNow.AddDays(10)));
    }

    [Fact]
    public void Anfangs_nur_deutlicher_Junk()
    {
        var space = _fx.Space("ben");
        var account = new DmOffice.Core.MailAccount { Id = "acc-ben", Email = "ben@example.org", ImapHost = "imap.example.org" };
        var m = Mail("Shop <news@shop.example>", "Angebote der Woche", null, "Jetzt sparen");
        // Wenig gelernt: ein mittlerer Wert ist noch nicht „unsicher“, deutlicher Junk schon
        Assert.True(SpamSignals.FewLearned(space.Db));
        Assert.Equal("few-learned", SpamSignals.Judge(space, account, m, 0.5).Rule);
        Assert.Equal(0.95, SpamSignals.Judge(space, account, m, 0.95).Score);
    }

    [Fact]
    public void Im_Junk_Ordner_geloescht_wird_als_Junk_gelernt()
    {
        var space = _fx.Space("dora");
        var before = SpamFilter.Counts(space.Db).Spam;
        // Gelöscht heißt „Müll“ - gleich gelernt, auch ganz frisch
        Assert.True(JunkLearn.LearnDeleted(space, Mail("Gewinn <win@spam.example>", "Sie haben gewonnen", null, "Jetzt Preis abholen")));
        Assert.Equal(before + 1, SpamFilter.Counts(space.Db).Spam);
        // Echte Markenmail im Junk (Fehlgriff des Anbieters): nicht als Junk lernen
        Assert.False(JunkLearn.LearnDeleted(space, Mail("Google <no-reply@accounts.google.com>", "Sicherheitswarnung",
            "dkim=pass header.d=accounts.google.com; dmarc=pass header.from=accounts.google.com", "Neue Anmeldung")));
        Assert.Equal(before + 1, SpamFilter.Counts(space.Db).Spam);
    }

    [Fact]
    public void Gemeinsames_Junk_Wissen_per_Fingerabdruck()
    {
        var path = Path.Combine(Path.GetTempPath(), "jp-" + Guid.NewGuid().ToString("N") + ".db");
        var prints = new JunkPrints(path, RandomNumberGenerator.GetBytes(32), null);
        var text = "Beantworten und gewinnen Sie einen Samsung 65 Zoll 4K Smart TV. Sie wurden ausgewählt, um kostenlos an unserem Treueprogramm teilzunehmen – völlig gratis! Jetzt starten und Gewinn sichern.";
        Mail("\"F R I T Z !\" <a1@hub.werbenetz.example>", "Ihr Geschenk wartet __ID:aa11bb22cc33", null, "Hello ich@gmx.example! " + text, "eins");
        var anna = _fx.Space("anna");
        var dora = _fx.Space("dora");
        var cleo = _fx.Space("cleo");
        var forAnna = Mail("\"F R I T Z !\" <a1@hub.werbenetz.example>", "Ihr Geschenk wartet __ID:aa11bb22cc33", null, "Hello anna@gmx.de! " + text, "eins");
        prints.Report(anna, forAnna);
        // Bei Dora kommt dieselbe Kampagne an - andere Anrede, andere Zufallskennung, andere Absenderadresse
        var forDora = Mail("\"F R I T Z !\" <x9@hub.werbenetz.example>", "Ihr Geschenk wartet __ID:k2p99887766zz", null, "Hello dora@web.de! " + text, "zwei");
        Assert.Equal(1, prints.Reports(dora, forDora));
        Assert.Equal(0, prints.Reports(anna, forAnna));   // die eigene Meldung zählt nicht
        prints.Report(cleo, Mail("\"F R I T Z !\" <q@hub.werbenetz.example>", "Ihr Geschenk wartet __ID:zz11aa22bb33", null, "Hi cleo@gmx.at! " + text, "drei"));
        Assert.Equal(2, prints.Reports(dora, forDora));
        var r = SpamSignals.Of(forDora) with { SharedReports = 2 };
        Assert.Equal("shared-junk", SpamSignals.Adjust(0.1, r with { Obfuscated = false, RandomId = false }, false, false, new SpamSettings()).Rule);
        // „Kein Junk“ nimmt die eigene Meldung zurück
        prints.Withdraw(anna, forAnna);
        Assert.Equal(1, prints.Reports(dora, forDora));
        // Andere Mail: kein Treffer
        Assert.Equal(0, prints.Reports(dora, Mail("Oma <oma@web.de>", "Sonntag Kaffee?", null, "Liebe Dora, kommst du am Sonntag zum Kaffee? Ich backe einen Apfelkuchen und freue mich auf dich. Viele Grüße, Oma")));
        // Wer nicht mitmacht, bekommt keine Treffer
        dora.UpdateSettings(s => s.Spam.ShareJunk = false);
        Assert.Equal(0, prints.Reports(dora, forDora));
        dora.UpdateSettings(s => s.Spam.ShareJunk = true);
    }
}
