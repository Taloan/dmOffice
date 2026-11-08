using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>PDF-Vorschau als Seitenbilder über die Download-Adresse (pdfpages / pdfpage), mit den Rechten des Download-Pfads.</summary>
[Collection("server")]
public class PdfPagesTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PdfPagesTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Seitenzahl_und_Seite_als_JPEG()
    {
        var space = _fx.Space("anna");
        FileStore.Put(space.Db, "vorschau-test.pdf", Samples.Pdf("Seite eins"), "application/pdf");
        FileStore.Put(space.Db, "vorschau-test.txt", "kein pdf"u8.ToArray(), "text/plain");
        var c = _fx.As("anna");

        var count = await c.GetFromJsonAsync<JsonElement>("/api/files/download?path=vorschau-test.pdf&inline=true&pdfpages=1");
        Assert.Equal(1, count.GetProperty("count").GetInt32());

        var page = await c.GetAsync("/api/files/download?path=vorschau-test.pdf&inline=true&pdfpage=0&width=400");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("image/jpeg", page.Content.Headers.ContentType?.MediaType);
        Assert.Null(page.Content.Headers.ContentDisposition);
        var jpeg = await page.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);

        // Keine PDF: abgelehnt; fremde Datei: wie beim Download nicht da
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await c.GetAsync("/api/files/download?path=vorschau-test.txt&pdfpages=1")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await _fx.As("cleo").GetAsync("/api/files/download?path=vorschau-test.pdf&pdfpages=1")).StatusCode);

        // Ohne die Angaben: die PDF selbst
        var raw = await c.GetAsync("/api/files/download?path=vorschau-test.pdf&inline=true");
        Assert.True(PdfPages.IsPdf(await raw.Content.ReadAsByteArrayAsync()));
    }
}
