using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Anhang zum Ansehen: Server und Oberfläche müssen dasselbe als ansehbar werten, sonst öffnet „In neuem Tab“ einen Download
/// (in der installierten App ein leeres Fenster).</summary>
public class MailAttachmentMimeTests
{
    [Theory]
    [InlineData("application/pdf", "a.pdf", "application/pdf")]
    [InlineData("application/x-pdf", "Rechnung.PDF", "application/pdf")]
    [InlineData("binary/octet-stream", "a.pdf", "application/pdf")]
    [InlineData("application/octet-stream", "foto.jpg", "image/jpeg")]
    [InlineData("image/png", "ohne-endung", "image/png")]
    [InlineData("image/svg+xml", "bild.svg", null)]
    [InlineData("text/html", "seite.html", null)]
    [InlineData("text/plain", "notiz.txt", null)]
    public void Ansehbar_nach_Art_oder_Endung(string mime, string name, string? expected)
        => Assert.Equal(expected, MailApi.ViewableMime(mime, name));
}
