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
    public void Nur_Sicheres_wird_gezeigt(string? mime, string name, bool shown)
    {
        var http = new DefaultHttpContext();
        var (m, download) = FilesApi.Shown(http, mime, name, inline: true);
        Assert.Equal(shown, download == null);
        if (!shown) Assert.Equal("application/octet-stream", m);
        if (shown && m != "application/pdf") Assert.Contains("sandbox", http.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal("nosniff", http.Response.Headers.XContentTypeOptions.ToString());
    }
}
