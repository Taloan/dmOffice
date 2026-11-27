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
    public GalleryDrainTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; GalleryTimeline.Grace = TimeSpan.Zero; }

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
            // Zeitleiste: noch nicht ausgelesen - eigener Abschnitt am Ende, nicht unter dem Dateidatum (galunread)
            async Task<List<(string K, int N)>> Buckets() => (await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/gallery/timeline/buckets?sort=taken&desc=true&stack=false"))
                .GetProperty("buckets").EnumerateArray().Select(b => (b.GetProperty("k").GetString()!, b.GetProperty("n").GetInt32())).ToList();
            var unread = await Buckets();
            Assert.Equal(("unread", 5), unread[^1]);

            // Stand von früher: eingelesen, aber ohne Merker zum Zu-Ende-Lesen - Offenes wird trotzdem abgearbeitet
            var cfg = GalleryFolders.Of(dora.Db);
            cfg.Single().Draining = false;
            GalleryFolders.Save(dora.Db, cfg);
            // Nächster Durchgang: liest zu Ende, ohne neuen Knopfdruck - danach ruht der Ordner
            GalleryScanJob.StageBudget = TimeSpan.FromMinutes(1);
            await job.RunOnceAsync(CancellationToken.None);
            var s = GalleryFolders.Stages(dora.Db, src.Id);
            Assert.Equal(0, s.Listed);
            Assert.False(GalleryFolders.Of(dora.Db).Single().Draining);
            await Task.Delay(50);
            Assert.DoesNotContain(await Buckets(), b => b.K == "unread");
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            GalleryScanJob.StageBudget = budget;
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Vorschaubilder_eines_nicht_ueberwachten_Server_Ordners_teilt_sich_eine_zweite_Person()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
        var dora = _fx.Space("dora");
        var cleo = _fx.Space("cleo");
        var root = Directory.CreateTempSubdirectory("dmshare").FullName;
        var before = config.Current.ExternalFolders;
        var shared = GalleryFolders.Shared;
        try
        {
            for (var i = 0; i < 3; i++)
                File.WriteAllBytes(Path.Combine(root, $"bild{i}.jpg"), XmpForeignLayoutTests.JpegWithXmp(XmpForeignLayoutTests.PacketRot4));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "share01", Name = "Familie", Path = root }]);
            var job = new GalleryScanJob(spaces, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryScanJob>.Instance);
            var d = _fx.As("dora");
            foreach (var old in GalleryFolders.Of(dora.Db)) await d.DeleteAsync("/api/gallery/sources/" + old.Id);
            foreach (var old in GalleryFolders.Of(cleo.Db)) await _fx.As("cleo").DeleteAsync("/api/gallery/sources/" + old.Id);
            // Dora hat die Vorschaubilder aus der Zeit vor dem gemeinsamen Bestand (nicht überwacht, fertig gelesen)
            GalleryFolders.Shared = null;
            Assert.Equal(HttpStatusCode.OK, (await d.PostAsJsonAsync("/api/gallery/sources", new { mount = "share01", path = "", watch = false })).StatusCode);
            await job.RunOnceAsync(CancellationToken.None);
            await job.RunOnceAsync(CancellationToken.None);
            var src = GalleryFolders.Of(dora.Db).Single();
            Assert.Equal((3, 0, 0), GalleryFolders.Stages(dora.Db, src.Id));
            GalleryFolders.Shared = shared;
            foreach (var i in Enumerable.Range(0, 3)) shared!.Remove("share01", $"bild{i}.jpg");
            // Eine andere Person hat bild2 schon zu einem anderen Stand gelesen: das darf nicht überschrieben werden
            shared!.Put("share01", "bild2.jpg", 4711, 2, null, [1, 2, 3]);

            // Nächster Durchgang: Doras Vorschaubilder kommen in den gemeinsamen Bestand - obwohl ihr Ordner nicht überwacht wird
            await job.RunOnceAsync(CancellationToken.None);
            var row = dora.Db.Query("SELECT size, modified FROM gallery_ext WHERE source = $s AND path = 'bild0.jpg'", r => (r.GetInt64(0), r.GetInt64(1)), ("$s", src.Id)).Single();
            var stamp = GalleryProxies.Stamp(row.Item1, row.Item2);
            Assert.NotNull(shared!.Find("share01", "bild0.jpg", stamp));
            Assert.True(shared.Find("share01", "bild0.jpg", stamp)!.HasThumb);
            Assert.NotNull(shared.Find("share01", "bild2.jpg", 4711));
            Assert.Null(shared.Find("share01", "bild2.jpg", stamp));
            // Doras eigene Zeile für bild2 behält ihr Vorschaubild (nicht auf den Bestand umgestellt)
            Assert.True(dora.Db.Query("SELECT length(thumb) FROM gallery_ext WHERE source = $s AND path = 'bild2.jpg'", r => r.GetInt64(0), ("$s", src.Id)).Single() > 0);
            await d.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            GalleryFolders.Shared = shared;
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Gleiche_Datei_mit_und_ohne_Sekundenbruchteile_liest_die_zweite_Person_nicht_neu()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
        var dora = _fx.Space("dora");
        var cleo = _fx.Space("cleo");
        var root = Directory.CreateTempSubdirectory("dmfrac").FullName;
        var before = config.Current.ExternalFolders;
        var shared = GalleryFolders.Shared!;
        try
        {
            // Ganze Sekunden im Ordner (wie SFTP)
            var whole = new DateTime(2025, 3, 4, 10, 20, 30, DateTimeKind.Utc);
            for (var i = 0; i < 3; i++)
            {
                var f = Path.Combine(root, $"pic{i}.jpg");
                File.WriteAllBytes(f, XmpForeignLayoutTests.JpegWithXmp(XmpForeignLayoutTests.PacketRot4));
                File.SetLastWriteTimeUtc(f, whole);
            }
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "frac01", Name = "Familie", Path = root }]);
            var job = new GalleryScanJob(spaces, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryScanJob>.Instance);
            var d = _fx.As("dora");
            var c = _fx.As("cleo");
            foreach (var old in GalleryFolders.Of(dora.Db)) await d.DeleteAsync("/api/gallery/sources/" + old.Id);
            foreach (var old in GalleryFolders.Of(cleo.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            foreach (var i in Enumerable.Range(0, 3)) shared.Remove("frac01", $"pic{i}.jpg");
            Assert.Equal(HttpStatusCode.OK, (await d.PostAsJsonAsync("/api/gallery/sources", new { mount = "frac01", path = "", watch = false })).StatusCode);
            await job.RunOnceAsync(CancellationToken.None);
            await job.RunOnceAsync(CancellationToken.None);
            var ds = GalleryFolders.Of(dora.Db).Single();
            Assert.Equal((3, 0, 0), GalleryFolders.Stages(dora.Db, ds.Id));
            // Doras Zeilen und der Bestand tragen noch die Zeit mit Bruchteilen vom früheren Ort (wie nach dem Neuzuordnen von CIFS)
            var size = dora.Db.Query("SELECT size FROM gallery_ext WHERE source = $s AND path = 'pic0.jpg'", r => r.GetInt64(0), ("$s", ds.Id)).Single();
            var ms = new DateTimeOffset(whole).ToUnixTimeMilliseconds();
            foreach (var i in Enumerable.Range(0, 3)) shared.Restamp("frac01", $"pic{i}.jpg", GalleryProxies.Stamp(size, ms), GalleryProxies.Stamp(size, ms + 437));
            dora.Db.Exec("UPDATE gallery_ext SET modified = modified + 437 WHERE source = $s", ("$s", ds.Id));

            // Cleo richtet denselben Ordner ein: nur auflisten (keine Zeit zum Lesen), beim nächsten Durchgang kommt alles aus dem Bestand
            var budget = GalleryScanJob.StageBudget;
            GalleryScanJob.StageBudget = TimeSpan.Zero;
            try
            {
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "frac01", path = "", watch = false })).StatusCode);
                await job.RunOnceAsync(CancellationToken.None);
                await job.RunOnceAsync(CancellationToken.None);
                await job.RunOnceAsync(CancellationToken.None);
            }
            finally { GalleryScanJob.StageBudget = budget; }
            var cs = GalleryFolders.Of(cleo.Db).Single();
            Assert.Equal((3, 0, 0), GalleryFolders.Stages(cleo.Db, cs.Id));
            // Beide stehen auf der ganzen Sekunde, der Bestand hat das Vorschaubild zu diesem Stand - auch Dora sieht ihre weiter
            Assert.Equal(ms, dora.Db.Query("SELECT modified FROM gallery_ext WHERE source = $s AND path = 'pic1.jpg'", r => r.GetInt64(0), ("$s", ds.Id)).Single());
            Assert.Equal(ms, cleo.Db.Query("SELECT modified FROM gallery_ext WHERE source = $s AND path = 'pic1.jpg'", r => r.GetInt64(0), ("$s", cs.Id)).Single());
            Assert.True(shared.Find("frac01", "pic1.jpg", GalleryProxies.Stamp(size, ms))!.HasThumb);
            // Vorschaubild im Bestand verdrängt (Stufe 1, ohne Bild): beide holen es neu, statt eine leere Kachel zu zeigen
            shared.Put("frac01", "pic2.jpg", GalleryProxies.Stamp(size, ms) + 2, 1, null, null);
            shared.Restamp("frac01", "pic2.jpg", GalleryProxies.Stamp(size, ms) + 2, GalleryProxies.Stamp(size, ms));
            Assert.False(shared.Find("frac01", "pic2.jpg", GalleryProxies.Stamp(size, ms))!.HasThumb);
            await job.RunOnceAsync(CancellationToken.None);
            await job.RunOnceAsync(CancellationToken.None);
            Assert.True(shared.Find("frac01", "pic2.jpg", GalleryProxies.Stamp(size, ms))!.HasThumb);
            Assert.Equal((3, 0, 0), GalleryFolders.Stages(dora.Db, ds.Id));
            Assert.Equal((3, 0, 0), GalleryFolders.Stages(cleo.Db, cs.Id));
            await d.DeleteAsync("/api/gallery/sources/" + ds.Id);
            await c.DeleteAsync("/api/gallery/sources/" + cs.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
