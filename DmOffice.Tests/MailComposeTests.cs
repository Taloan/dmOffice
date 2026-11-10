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

    // iPhone: Text, Bild (inline, ohne Content-ID), Text als Teile nacheinander in multipart/mixed
    private static MimeMessage IPhoneMail(bool pictureAsAttachment = false)
    {
        var picture = new MimePart("image", "jpeg")
        {
            Content = new MimeContent(new MemoryStream([0xFF, 0xD8, 0xFF, 0xD9])),
            ContentDisposition = new ContentDisposition(pictureAsAttachment ? ContentDisposition.Attachment : ContentDisposition.Inline) { FileName = "image0.jpeg" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        var mixed = new Multipart("mixed")
        {
            new TextPart("html") { Text = "<div>Danke für das schöne Geschenk</div>" },
            picture,
            new TextPart("html") { Text = "<div>Von meinem iPhone gesendet</div>" },
        };
        return new MimeMessage { Body = mixed };
    }

    [Fact]
    public void IPhone_Text_Bild_Text_erscheinen_nacheinander_statt_als_Anhaenge()
    {
        var m = IPhoneMail();
        var parts = MailCompose.Parts(m);
        var used = new HashSet<int>();
        var html = MailCompose.InlineSequence(m, parts, used);
        Assert.NotNull(html);
        var a = html!.IndexOf("Geschenk", StringComparison.Ordinal);
        var b = html.IndexOf("cid:" + MailCompose.SequencePart, StringComparison.Ordinal);
        var c = html.IndexOf("iPhone gesendet", StringComparison.Ordinal);
        Assert.True(a >= 0 && a < b && b < c);
        // Bild und zweiter Text gelten als verwendet und erscheinen nicht als Anhang
        Assert.Equal(parts.Count, used.Count);
    }

    [Fact]
    public void Bild_als_echter_Anhang_bleibt_Anhang()
    {
        var m = IPhoneMail(pictureAsAttachment: true);
        var used = new HashSet<int>();
        Assert.Null(MailCompose.InlineSequence(m, MailCompose.Parts(m), used));
        Assert.Empty(used);
    }
}
