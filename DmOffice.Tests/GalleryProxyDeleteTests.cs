using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Große Vorschauen: Kacheln (ready=1) erzeugen keine, Öffnen schon; Platz je Ordner und Löschen.</summary>
[Collection("server")]
public class GalleryProxyDeleteTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryProxyDeleteTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Kacheln_erzeugen_keine_Loeschen_gibt_frei()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var root = Directory.CreateTempSubdirectory("dmpx").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            using (var bmp = new SKBitmap(3000, 2000))
            {
                using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Olive);
                File.WriteAllBytes(Path.Combine(root, "gross.jpg"), bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray());
            }
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "pxdel01", Name = "Archiv", Path = root }]);
            var c1 = _fx.As("anna");
            Assert.Equal(HttpStatusCode.OK, (await c1.PostAsJsonAsync("/api/gallery/sources", new { mount = "pxdel01", path = "", watch = false })).StatusCode);
            await new GalleryScanJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryScanJob>.Instance).RunOnceAsync(CancellationToken.None);
            async Task<JsonElement> Src() => (await c1.GetFromJsonAsync<JsonElement>("/api/gallery/sources")).GetProperty("sources").EnumerateArray().Single(x => x.GetProperty("mount").GetString() == "pxdel01");
            var srcId = (await Src()).GetProperty("id").GetString();
            var id = Uri.EscapeDataString(srcId + "/gross.jpg");

            // Kachel: nur das kleine Bild, keine große Vorschau entsteht
            Assert.Equal(HttpStatusCode.OK, (await c1.GetAsync($"/api/gallery/external/thumb?id={id}&size=1600&ready=1")).StatusCode);
            Assert.Equal(0, (await Src()).GetProperty("proxyCount").GetInt32());
            // Öffnen: große Vorschau entsteht und zählt beim Ordner
            Assert.Equal(HttpStatusCode.OK, (await c1.GetAsync($"/api/gallery/external/thumb?id={id}&size=1600")).StatusCode);
            Assert.Equal(1, (await Src()).GetProperty("proxyCount").GetInt32());
            // Jetzt liefert auch die Kachel die große
            Assert.Equal(HttpStatusCode.OK, (await c1.GetAsync($"/api/gallery/external/thumb?id={id}&size=1600&ready=1")).StatusCode);

            // Löschen: weg, „vorab“ aus
            var del = await c1.DeleteAsync($"/api/gallery/sources/{srcId}/proxies");
            Assert.Equal(1, (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32());
            Assert.Equal(0, (await Src()).GetProperty("proxyCount").GetInt32());
            await c1.DeleteAsync("/api/gallery/sources/" + srcId);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }
}
