using DmOffice.Mail;

namespace DmOffice.Tests;

public class MailTrackersTests
{
    [Fact]
    public void Zaehlpixel_fallen_weg_auch_mit_erlaubten_Bildern()
    {
        var html = "<p>Hallo</p>" +
            "<img src=\"https://news.example.com/logo.png\" width=\"200\" height=\"60\">" +
            "<img src=\"https://t.example.com/x.gif\" width=\"1\" height=\"1\">" +
            "<img src=\"https://list.example.com/track/open.php?u=1\">" +
            "<img src=\"https://x.example.com/a.png\" style=\"display:none\">";
        var r = MailHtml.Sanitize(html, true, _ => null, trackers: true);
        Assert.Equal(3, r.Trackers);
        Assert.Contains("logo.png", r.Html);
        Assert.DoesNotContain("x.gif", r.Html);
        Assert.DoesNotContain("open.php", r.Html);
        Assert.True(r.HasRemoteImages);
        // Ohne Schutz bleibt alles wie bisher
        var off = MailHtml.Sanitize(html, true, _ => null);
        Assert.Equal(0, off.Trackers);
        Assert.Contains("x.gif", off.Html);
    }

    [Fact]
    public void Links_ohne_Angaben_zur_Nachverfolgung()
    {
        Assert.Equal("https://shop.example/p?id=7#top", MailTrackers.CleanLink("https://shop.example/p?id=7&utm_source=nl&utm_medium=mail&fbclid=abc#top"));
        Assert.Equal("https://shop.example/p", MailTrackers.CleanLink("https://shop.example/p?utm_campaign=x"));
        Assert.Null(MailTrackers.CleanLink("https://shop.example/p?id=7"));
        Assert.Null(MailTrackers.CleanLink("mailto:a@example.com?subject=utm_source"));
        var r = MailHtml.Sanitize("<a href=\"https://a.example/?gclid=1&q=2\">a</a><a href=\"https://b.example/\">b</a>", false, _ => null, trackers: true);
        Assert.Equal(1, r.CleanedLinks);
        Assert.Contains("https://a.example/?q=2", r.Html);
    }

    [Theory]
    [InlineData("<img src=\"https://a.example/b.png\" width=\"99999999999\" height=\"5\">")]
    [InlineData("<img src=\"https://a.example/b.png\" style=\"width:99999999999px;height:99999999999px\">")]
    public void Sehr_grosse_Masse_gelten_als_Bild(string html)
    {
        var r = MailHtml.Sanitize(html, true, _ => null, trackers: true);
        Assert.Equal(0, r.Trackers);
        Assert.Contains("a.example/b.png", r.Html);
    }
}
