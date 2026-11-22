using System.Net;
using System.Net.Http.Json;
using DmOffice.Core;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Nicht überwachter Galerie-Ordner: nach dem Einlesen einmal zu Ende lesen (Bildangaben, Vorschaubilder), dann ruhen.</summary>
[Collection("server")]
public class GalleryDrainTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryDrainTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; }

    [Fact]
    public async Task Ordner_ohne_Ueberwachen_liest_nach_dem_Einlesen_zu_Ende()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var dora = _fx.Space("dora");
        var root = Directory.CreateTempSubdirectory("dmdrain").FullName;
        var before = config.Current.ExternalFolders;
        var budget = GalleryScanJob.StageBudget;
        try
        {
            for (var i = 0; i < 5; i++)
                File.WriteAllBytes(Path.Combine(root, $"foto{i}.jpg"), XmpForeignLayoutTests.JpegWithXmp(XmpForeignLayoutTests.PacketRot4));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "drain01", Name = "Archiv", Path = root }]);
            var c = _fx.As("dora");
            foreach (var old in GalleryFolders.Of(dora.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "drain01", path = "", watch = false })).StatusCode);
            var job = new GalleryScanJob(_fx.Factory.Services.GetRequiredService<Spaces>(), config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryScanJob>.Instance);

            // Erster Durchgang ohne Zeit für die Stufen: nur aufgelistet, nichts gelesen
            GalleryScanJob.StageBudget = TimeSpan.Zero;
            await job.RunOnceAsync(CancellationToken.None);
            var src = GalleryFolders.Of(dora.Db).Single();
            Assert.False(src.Watch);
            Assert.True(src.Draining);
            Assert.Equal(5, GalleryFolders.Stages(dora.Db, src.Id).Listed);

            // Nächster Durchgang: liest zu Ende, ohne neuen Knopfdruck - danach ruht der Ordner
            GalleryScanJob.StageBudget = TimeSpan.FromMinutes(1);
            await job.RunOnceAsync(CancellationToken.None);
            var s = GalleryFolders.Stages(dora.Db, src.Id);
            Assert.Equal(0, s.Listed);
            Assert.False(GalleryFolders.Of(dora.Db).Single().Draining);
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            GalleryScanJob.StageBudget = budget;
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
