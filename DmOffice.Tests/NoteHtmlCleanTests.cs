using DmOffice.Notes;

namespace DmOffice.Tests;

/// <summary>Notizinhalt, den andere lesen: keine Bedienelemente, nichts über der Oberfläche, Bilder nur aus der Notiz.</summary>
public class NoteHtmlCleanTests
{
    [Fact]
    public void Formular_und_Knopf_verschwinden()
    {
        var html = NotesApi.Clean("<ul><li><form action=\"/api/admin/users/x/reset-2fa\" method=\"post\"><button>Weiter</button><input name=\"a\" value=\"b\"></form></li></ul>");
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("<input", html);
        Assert.DoesNotContain("reset-2fa", html);
    }

    [Fact]
    public void Formulartext_verschwindet()
    {
        var html = NotesApi.Clean("<p>vor<textarea>geheim</textarea><select><option>wahl</option></select>nach</p>");
        Assert.DoesNotContain("geheim", html);
        Assert.DoesNotContain("wahl", html);
        Assert.Contains("vor", html);
        Assert.Contains("nach", html);
    }

    [Fact]
    public void Nichts_legt_sich_ueber_die_Oberflaeche()
    {
        var html = NotesApi.Clean("<p style=\"position:fixed;top:0;left:0;z-index:99999;transform:scale(9);color:red;text-align:center\">x</p>");
        Assert.DoesNotContain("position", html);
        Assert.DoesNotContain("z-index", html);
        Assert.DoesNotContain("top", html);
        Assert.DoesNotContain("transform", html);
        Assert.Contains("color", html);
        Assert.Contains("text-align", html);
    }

    [Fact]
    public void Bilder_nur_aus_der_Notiz()
    {
        Assert.Contains("src=\"blob:bild.png\"", NotesApi.Clean("<p><img src=\"blob:bild.png\" /></p>"));
        Assert.Contains("data:image/png", NotesApi.Clean("<p><img src=\"data:image/png;base64,iVBORw0KGgo=\" /></p>"));
        Assert.DoesNotContain("tracker", NotesApi.Clean("<p><img src=\"https://tracker.test/p.gif\" /></p>"));
        Assert.DoesNotContain("/api/", NotesApi.Clean("<p><img src=\"/api/files/x\" /></p>"));
        Assert.DoesNotContain("tracker", NotesApi.Clean("<p><img src=\"blob:a\" srcset=\"https://tracker.test/p.gif 2x\" /></p>"));
        Assert.DoesNotContain("tracker", NotesApi.Clean("<p style=\"background-image:url(https://tracker.test/p.gif)\">x</p>"));
    }

    [Fact]
    public void Links_bleiben_nur_als_Weblink()
    {
        Assert.Contains("href=\"https://example.org/\"", NotesApi.Clean("<p><a href=\"https://example.org/\">x</a></p>"));
        Assert.DoesNotContain("data:", NotesApi.Clean("<p><a href=\"data:text/html;base64,PGI+\">x</a></p>"));
        Assert.DoesNotContain("blob:", NotesApi.Clean("<p><a href=\"blob:http://x/y\">x</a></p>"));
    }

    [Fact]
    public void Gewoehnlicher_Inhalt_bleibt()
    {
        var html = NotesApi.Clean("<p><b>fett</b> <span style=\"color:#c00\">rot</span></p><div data-block=\"image\"><img src=\"blob:x\" /></div>");
        Assert.Contains("<b>fett</b>", html);
        Assert.Contains("color", html);
        Assert.Contains("data-block=\"image\"", html);
        Assert.Contains("src=\"blob:x\"", html);
    }
}
