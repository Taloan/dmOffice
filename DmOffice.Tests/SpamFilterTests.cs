using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

public class SpamFilterTests
{
    private static UserDb NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("dmspam");
        return new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
    }

    private static MimeMessage Mail(string id, string fromName, string from, string subject, string body)
    {
        var m = new MimeMessage { MessageId = id, Subject = subject };
        m.From.Add(new MailboxAddress(fromName, from));
        m.To.Add(new MailboxAddress("Ich", "ich@example.org"));
        m.Body = new TextPart("plain") { Text = body };
        return m;
    }

    private static MimeMessage Spam(string id) => Mail(id, "Gewinner", "promo@casino-spam.example", "Gewinn Casino Bonus",
        "viagra casino gewinn jackpot bonus lotterie");

    private static MimeMessage Ham(string id) => Mail(id, "Kollege", "kollege@firma.example", "Protokoll Besprechung",
        "protokoll besprechung projekt termin agenda quartal");

    [Fact]
    public void Ohne_Training_neutral()
    {
        Assert.Equal(0.5, SpamFilter.Score(NewDb(), Spam("s@test")));
    }

    [Fact]
    public void Junk_nahe_eins_gut_nahe_null()
    {
        var db = NewDb();
        for (int i = 0; i < 3; i++)
        {
            SpamFilter.Train(db, Spam($"spam{i}@test"), spam: true);
            SpamFilter.Train(db, Ham($"ham{i}@test"), spam: false);
        }
        Assert.Equal((3, 3), SpamFilter.Counts(db));

        var spam = SpamFilter.Score(db, Spam("neu-spam@test"));
        var ham = SpamFilter.Score(db, Ham("neu-ham@test"));
        Assert.True(spam > 0.9, $"Junk-Mail bekam {spam}");
        Assert.True(ham < 0.1, $"Gute Mail bekam {ham}");
    }
}
