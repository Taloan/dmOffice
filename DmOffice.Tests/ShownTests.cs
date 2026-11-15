using DmOffice.Dav;
using Microsoft.AspNetCore.Http;

namespace DmOffice.Tests;

/// <summary>Dateien, deren Typ von außen kommt: gezeigt nur Sicheres, mit Sandbox; HTML, SVG, Skripte als Datei zum Speichern.</summary>
public class ShownTests
{
    [Theory]
    [InlineData("image/svg+xml", "x.svg", false)]
    [InlineData("text/html", "x.html", false)]
    [InlineData("text/javascript", "x.js", false)]
    [InlineData("application/xhtml+xml", "x.xhtml", false)]
    [InlineData("image/jpeg", "x.jpg", true)]
    [InlineData("application/pdf", "x.pdf", true)]
    [InlineData("text/plain", "x.txt", true)]
    [InlineData(null, "x.svg", false)]
    [InlineData("image/svg+xml; charset=utf-8", "x.svg", false)]
    [InlineData("IMAGE/SVG+XML", "x.svg", false)]
    [InlineData(" Image/Svg+Xml ;q=1", "x.svg", false)]
    [InlineData("Text/HTML; charset=utf-8", "x.html", false)]
    [InlineData("IMAGE/JPEG", "x.jpg", true)]
    public void Nur_Sicheres_wird_gezeigt(string? mime, string name, bool shown)
    {
        var http = new DefaultHttpContext();
        var (m, download) = FilesApi.Shown(http, mime, name, inline: true);
        Assert.Equal(shown, download == null);
        if (!shown) Assert.Equal("application/octet-stream", m);
        if (shown && m != "application/pdf") Assert.Contains("sandbox", http.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal("nosniff", http.Response.Headers.XContentTypeOptions.ToString());
    }

    [Theory]
    [InlineData("image/svg+xml; charset=utf-8", false)]
    [InlineData("text/html; charset=utf-8", false)]
    [InlineData("TEXT/HTML", false)]
    [InlineData("text/plain; charset=utf-8", true)]
    [InlineData("image/PNG", true)]
    public void Nur_gezeigt_vergleicht_den_Typ_ohne_Parameter(string mime, bool viewable)
    {
        var f = new FileEntry("a/x", false, 1, "\"e\"", mime, DateTime.UtcNow, DateTime.UtcNow, 1);
        Assert.Equal(viewable, FilesApi.Viewable(f));
    }
}
