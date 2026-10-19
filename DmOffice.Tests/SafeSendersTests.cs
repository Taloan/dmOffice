using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Spamfilter: Kontakte und selbst angeschriebene Adressen sind sichere Absender.</summary>
public class SafeSendersTests
{
    private static MimeMessage From(string address)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", address));
        m.To.Add(new MailboxAddress("Ich", "ich@beispiel.de"));
        m.Subject = "Hallo";
        m.Body = new TextPart("plain") { Text = "Hallo" };
        return m;
    }

    [Fact]
    public void Kontakte_und_Empfaenger_gesendeter_Mails_sind_sicher()
    {
        var dir = Directory.CreateTempSubdirectory("dmsafe");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        space.UpdateSettings(s => s.MailAccounts.Add(new MailAccount { Id = "a", Email = "Ich@Beispiel.de" }));

        // Adressbuch
        db.Exec("INSERT INTO contacts_fts(name, emails, org, phones) VALUES('Anna', 'anna@example.org', '', '')");
        // Gesendet-Ordner (auch mit anderem Programm gesendet), dazu ein Brief an mich selbst
        db.Exec("INSERT INTO mail_folders(account, path, name, special) VALUES('a', 'Sent', 'Gesendet', 'sent')");
        db.Exec("INSERT INTO mail(account,folder,uid,subject,date,recipients) VALUES('a','Sent',1,'Re',1,$r)",
            ("$r", "{\"to\":[{\"name\":\"Ben\",\"address\":\"Ben@Firma.de\"}],\"cc\":[{\"name\":null,\"address\":\"ich@beispiel.de\"}]}"));
        // Aus dmOffice gesendet
        var sent = new MimeMessage();
        sent.To.Add(new MailboxAddress("Cleo", "cleo@verein.de"));
        Pim.ContactStore.RememberRecipients(space, sent);

        Assert.True(SafeSenders.IsKnown(space, From("anna@example.org")));
        Assert.True(SafeSenders.IsKnown(space, From("ben@firma.de")));
        Assert.True(SafeSenders.IsKnown(space, From("CLEO@verein.de")));
        Assert.False(SafeSenders.IsKnown(space, From("werbung@spam.example")));
        // Eigene Adresse ist kein sicherer Absender (gefaelschter Absender)
        Assert.False(SafeSenders.IsKnown(space, From("ich@beispiel.de")));
    }
}
