using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

public class MailComposeTests
{
    [Fact]
    public void Message_id_carries_the_senders_domain()
    {
        var m = MailCompose.Build(new MailAccount { Email = "anna@beispiel.de" }, new MailCompose.Draft { To = "x@example.org", Html = "<p>x</p>" }, Array.Empty<(string, string, byte[])>());
        Assert.EndsWith("@beispiel.de", m.MessageId);
    }

    [Fact]
    public void Suchwort_nur_aus_Satzzeichen_faellt_weg()
    {
        Assert.Equal("", MailStore.FtsQuery("-"));
        Assert.Equal("\"rechnung\"* AND \"mai\"*", MailStore.FtsQuery("rechnung - mai"));
    }

    [Fact]
    public void Weiterleitung_behaelt_eingebettete_Bilder()
    {
        var builder = new BodyBuilder { HtmlBody = "<html><body><p>Hallo</p><img src=\"cid:bild1\"></body></html>" };
        var image = builder.LinkedResources.Add("bild.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }, new ContentType("image", "png"));
        image.ContentId = "bild1";
        var original = new MimeMessage { Subject = "Mit Bild", Body = builder.ToMessageBody() };
        original.From.Add(new MailboxAddress("Anna", "anna@example.org"));
        original.To.Add(new MailboxAddress("Ich", "ich@example.org"));

        var account = new MailAccount { Email = "ich@example.org" };
        var fwd = MailCompose.Forward(account, original, "ziel@example.org");

        Assert.Contains("cid:bild1", fwd.HtmlBody);
        Assert.Contains(fwd.BodyParts, p => p.ContentId == "bild1");
        // Nicht noch einmal als gewoehnlicher Anhang
        Assert.DoesNotContain(fwd.Attachments, p => p.ContentId == "bild1");
    }

    [Fact]
    public void Leere_Felder_sind_kein_Fehler()
    {
        var account = new MailAccount { Email = "ich@example.org" };
        var m = MailCompose.Build(account, new MailCompose.Draft { To = "anna@example.org", Cc = " ", Html = "<p>x</p>" },
            Array.Empty<(string, string, byte[])>());
        Assert.Single(m.To.Mailboxes);
        Assert.Empty(m.Cc);
    }
}
