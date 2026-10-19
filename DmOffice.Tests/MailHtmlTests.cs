using DmOffice.Mail;

namespace DmOffice.Tests;

public class MailHtmlTests
{
    [Fact]
    public void Skripte_und_JsonLd_erscheinen_nicht_als_Text()
    {
        var html = """
            <html><head><title>Ihre Buchung</title><script type="application/ld+json">{"@type":"FlightReservation","reservationNumber":"K7QZ2M"}</script></head>
            <body><p>Hallo</p><script>alert('x')</script><script type="application/ld+json">{"@type":"ParcelDelivery"}</script><p>Gute Reise</p></body></html>
            """;
        var clean = MailHtml.Sanitize(html, false, _ => null).Html;
        Assert.Contains("Hallo", clean);
        Assert.Contains("Gute Reise", clean);
        Assert.DoesNotContain("reservationNumber", clean);
        Assert.DoesNotContain("ParcelDelivery", clean);
        Assert.DoesNotContain("alert", clean);
        Assert.DoesNotContain("Ihre Buchung", clean);
    }

    [Fact]
    public void Unbekannte_Tags_behalten_ihren_Text()
    {
        var clean = MailHtml.Sanitize("<p>vor <blink>blinkend</blink> nach</p><style>p{color:red}</style>", false, _ => null).Html;
        Assert.Contains("blinkend", clean);
        Assert.Contains("<style>", clean);
    }
}
