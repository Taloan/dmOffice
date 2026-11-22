using System.Net;
using System.Net.Http.Json;
using DmOffice.Core;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>
/// Foto im Galerie-Ordner gilt als gelesen, hat aber kein kleines Vorschaubild (beim Einlesen unvollständig oder nicht lesbar):
/// die Kachel holt es nach, statt dauerhaft leer zu bleiben.
/// </summary>
[Collection("server")]
public class GalleryThumbHealTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryThumbHealTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; GalleryTimeline.Grace = TimeSpan.Zero; }

    [Fact]
    public async Task Fehlendes_Vorschaubild_wird_bei_der_Anfrage_nachgeholt()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var dora = _fx.Space("dora");
        var root = Directory.CreateTempSubdirectory("dmheal").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "gut.jpg"), XmpForeignLayoutTests.JpegWithXmp(XmpForeignLayoutTests.PacketRot4));
            File.WriteAllBytes(Path.Combine(root, "kaputt.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5]);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "heal01", Name = "Archiv", Path = root }]);
            var c = _fx.As("dora");
            foreach (var old in GalleryFolders.Of(dora.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "heal01", path = "", watch = false })).StatusCode);
            var job = new GalleryScanJob(_fx.Factory.Services.GetRequiredService<Spaces>(), config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);
            var src = GalleryFolders.Of(dora.Db).Single();
            Assert.Equal(0, GalleryFolders.Stages(dora.Db, src.Id).Listed);

            // Wie nach einem Lesefehler beim Einlesen: gelesen (Stufe 2), aber ohne Vorschaubild
            dora.Db.Exec("UPDATE gallery_ext SET thumb = NULL WHERE source = $s", ("$s", src.Id));
            var shared = GalleryFolders.Shared;
            shared?.Remove("heal01", "gut.jpg");
            shared?.Remove("heal01", "kaputt.jpg");
            byte[]? Stored(string p) => dora.Db.Query("SELECT thumb FROM gallery_ext WHERE source = $s AND path = $p",
                r => r.IsDBNull(0) ? null : (byte[])r.GetValue(0), ("$s", src.Id), ("$p", p)).FirstOrDefault();
            Assert.Null(Stored("gut.jpg"));

            var ok = await c.GetAsync($"/api/gallery/external/thumb?id={Uri.EscapeDataString(src.Id + "/gut.jpg")}&size=256");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("image/webp", ok.Content.Headers.ContentType?.MediaType);
            // Abgelegt: im Index, bei Server-Ordnern im gemeinsamen Bestand (der Index verweist dann darauf)
            Assert.NotNull(Stored("gut.jpg"));
            if (shared != null)
            {
                var row = dora.Db.Query("SELECT size, modified FROM gallery_ext WHERE source = $s AND path = 'gut.jpg'", r => (r.GetInt64(0), r.GetInt64(1)), ("$s", src.Id)).Single();
                Assert.NotNull(shared.Thumb("heal01", "gut.jpg", GalleryProxies.Stamp(row.Item1, row.Item2)));
            }

            // Nicht lesbar: weiter ohne Vorschau, kein Fehler
            var bad = await c.GetAsync($"/api/gallery/external/thumb?id={Uri.EscapeDataString(src.Id + "/kaputt.jpg")}&size=256");
            Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/gallery/external/thumb?id={Uri.EscapeDataString(src.Id + "/kaputt.jpg")}&size=256")).StatusCode);
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
