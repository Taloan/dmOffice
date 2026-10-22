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

    [Fact]
    public void Junk_sperrt_alle_Bilder_und_Links_auch_wenn_Bilder_erlaubt_waeren()
    {
        var html = """<p>Hallo <a href="https://boese.example/klick">hier</a></p><img src="cid:bild1"><img src="data:image/png;base64,AAAA"><img src="https://boese.example/p.gif">""";
        var r = MailHtml.Sanitize(html, true, _ => "/api/mail/a/part?index=1", junk: true);
        Assert.DoesNotContain("href=", r.Html);
        Assert.DoesNotContain("src=", r.Html);
        Assert.Contains("hier", r.Html);
        Assert.Contains("boese.example/klick", r.Html);   // nur als Hinweis (title)
        Assert.True(r.HasRemoteImages);

        // Ohne Junk bleiben eingebettete Bilder und Links wie bisher
        var normal = MailHtml.Sanitize(html, false, _ => "/api/mail/a/part?index=1").Html;
        Assert.Contains("href=\"https://boese.example/klick\"", normal);
        Assert.Contains("/api/mail/a/part?index=1", normal);
    }
}
