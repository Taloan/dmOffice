using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Gerade gesendete Mails liegen beim Eintreffen in "Gesendet" gleich vollstaendig vor.</summary>
public class SentCacheTests
{
    private static MimeMessage Mail(int bodyBytes)
    {
        var m = new MimeMessage { MessageId = Guid.NewGuid().ToString("N") + "@x.de", Subject = "Test" };
        m.From.Add(new MailboxAddress("Ich", "ich@x.de"));
        m.Body = new TextPart("plain") { Text = new string('a', bodyBytes) };
        return m;
    }

    [Fact]
    public void Remembered_mail_is_taken_once_by_its_message_id()
    {
        var m = Mail(100);
        SentCache.Remember("u", "a", "Gesendet", m, 1_000_000);
        Assert.Null(SentCache.Take("u", "a", "INBOX", m.MessageId));          // anderer Ordner
        var raw = SentCache.Take("u", "a", "Gesendet", "<" + m.MessageId + ">");   // mit spitzen Klammern wie im Kopf
        Assert.NotNull(raw);
        Assert.Equal("Test", MimeMessage.Load(new MemoryStream(raw!)).Subject);
        Assert.Null(SentCache.Take("u", "a", "Gesendet", m.MessageId));       // nur einmal
    }

    [Fact]
    public void Mail_over_the_accounts_limit_is_not_kept()
    {
        var m = Mail(5000);
        SentCache.Remember("u", "a", "Gesendet", m, 1000);
        Assert.Null(SentCache.Take("u", "a", "Gesendet", m.MessageId));
    }
}
