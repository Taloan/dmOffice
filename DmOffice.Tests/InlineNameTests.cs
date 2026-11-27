using System.Net;
using DmOffice.Dav;
using Microsoft.AspNetCore.Http;

namespace DmOffice.Tests;

/// <summary>
/// Angezeigte Dateien (inline) tragen ihren Namen: sonst speichert der Browser einen neuen Tab oder einen Download unter einer
/// zufälligen Kennung ohne Endung. Gilt für Dateien, DMS, Haushalt, Helpdesk und Mail-Anhänge (alle über FilesApi.InlineName).
/// </summary>
[Collection("server")]
public class InlineNameTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public InlineNameTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public void Inline_mit_Namen_und_ohne_Pfad()
    {
        var http = new DefaultHttpContext();
        FilesApi.InlineName(http, "Belege/2026/Rechnung Büro.pdf");
        var cd = http.Response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("inline", cd);
        Assert.Contains("filename*=UTF-8''Rechnung%20B%C3%BCro.pdf", cd);
        Assert.DoesNotContain("Belege", cd);

        var empty = new DefaultHttpContext();
        FilesApi.InlineName(empty, null);
        Assert.Equal(0, empty.Response.Headers.ContentDisposition.Count);
    }

    [Fact]
    public async Task Datei_angezeigt_und_heruntergeladen_mit_Namen()
    {
        var space = _fx.Space("anna");
        FileStore.Put(space.Db, "Ihre Rechnung 4711.pdf", Samples.Pdf("Rechnung"), "application/pdf");
        var c = _fx.As("anna");

        var shown = await c.GetAsync("/api/files/download?path=Ihre%20Rechnung%204711.pdf&inline=true");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("application/pdf", shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", shown.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Ihre Rechnung 4711.pdf", shown.Content.Headers.ContentDisposition?.FileNameStar);

        var saved = await c.GetAsync("/api/files/download?path=Ihre%20Rechnung%204711.pdf");
        Assert.Equal("attachment", saved.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Ihre Rechnung 4711.pdf", saved.Content.Headers.ContentDisposition?.FileNameStar);
    }
}
