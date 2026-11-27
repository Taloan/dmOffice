using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Anhang in die Ablage speichern: der Name kommt vom Absender und wird bereinigt.</summary>
public class MailSaveNameTests
{
    [Theory]
    [InlineData("Rechnung.pdf", "Rechnung.pdf")]
    [InlineData("..\\..\\x.pdf", "x.pdf")]
    [InlineData("a/b/../c.txt", "c.txt")]
    [InlineData("..", "anhang-3")]
    [InlineData(" . ", "anhang-3")]
    [InlineData("a\r\nb\u0001.txt", "ab.txt")]
    [InlineData(null, "anhang-3")]
    [InlineData("ordner/", "anhang-3")]
    public void Name_wird_bereinigt(string? raw, string expected) => Assert.Equal(expected, MailApi.SaveName(raw, 3));

    [Fact]
    public void Lange_Namen_behalten_die_Endung()
    {
        var n = MailApi.SaveName(new string('x', 300) + ".pdf", 0);
        Assert.Equal(200, n.Length);
        Assert.EndsWith(".pdf", n);
    }
}
