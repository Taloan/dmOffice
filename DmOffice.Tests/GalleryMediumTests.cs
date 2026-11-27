using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>
/// Mittlere Vorschau (galmedium): entsteht beim Einlesen und beim Ablegen in einen Galerie-Ordner, wird für die Großansicht
/// gleich geliefert (statt das Original zu holen), nachts nachgeholt und mit dem Foto entfernt.
/// </summary>
[Collection("server")]
public class GalleryMediumTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryMediumTests(ChatFixture fx) => _fx = fx;

    /// <summary>Rauschen: als JPEG mehrere MB - zu groß für das Lesen nur des Kopfs, geht durch das Lesen der ganzen Datei.</summary>
    private static byte[] Noisy(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        var rnd = new Random(7);
        var px = new SKColor[w * h];
        for (var i = 0; i < px.Length; i++) px[i] = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        bmp.Pixels = px;
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 85).ToArray();
    }

    private static byte[] Plain(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(color);
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static int LongEdge(byte[] image)
    {
        using var codec = SKCodec.Create(new MemoryStream(image));
        return Math.Max(codec.Info.Width, codec.Info.Height);
    }

    private static long StampOf(UserDb db, string source, string path) =>
        db.Query("SELECT size, modified FROM gallery_ext WHERE source = $s AND path = $p", r => GalleryProxies.Stamp(r.GetInt64(0), r.GetInt64(1)), ("$s", source), ("$p", path)).Single();

    /// <summary>Schalter galmedium für die Dauer des Tests an.</summary>
    private static async Task WithMediumAsync(Func<Task> run)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.GalleryMedium));
        try { await run(); } finally { Features.Set(before); }
    }

    [Fact]
    public Task Einlesen_erzeugt_mittlere_Vorschau_und_Grossansicht_nimmt_sie() => WithMediumAsync(async () =>
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var medium = GalleryFolders.Medium;
        Assert.NotNull(medium);
        var root = Directory.CreateTempSubdirectory("dmmed").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "gross.jpg"), Noisy(2400, 1600));
            File.WriteAllBytes(Path.Combine(root, "klein.jpg"), Plain(1800, 1200, SKColors.Teal));
            Assert.True(new FileInfo(Path.Combine(root, "gross.jpg")).Length > 1024 * 1024);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "medium01", Name = "Archiv", Path = root }]);
            var space = _fx.Space("anna");
            var c = _fx.As("anna");
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "medium01", path = "", watch = false })).StatusCode);
            var src = GalleryFolders.Of(space.Db).Single(s => s.Mount == "medium01");
            Assert.False(src.Proxies);
            await new GalleryScanJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryScanJob>.Instance).RunOnceAsync(CancellationToken.None);

            // Beide Wege des Einlesens: ganze Datei (gross) und nur der Kopf, der die ganze Datei ist (klein)
            foreach (var name in new[] { "gross.jpg", "klein.jpg" })
                Assert.True(medium!.Has(GalleryProxies.SharedScope, "medium01", name, StampOf(space.Db, src.Id, name)), name);

            // Großansicht: sofort die mittlere Vorschau (1280 px), keine große entsteht dafür
            var id = Uri.EscapeDataString(src.Id + "/gross.jpg");
            var res = await c.GetAsync($"/api/gallery/external/thumb?id={id}&size=1600");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(GalleryFolders.MediumSize, LongEdge(await res.Content.ReadAsByteArrayAsync()));
            Assert.Equal(0, GalleryFolders.FolderProxyUsage(space, config, src).Count);
            // Kachel mit ready=1: auch die mittlere
            res = await c.GetAsync($"/api/gallery/external/thumb?id={id}&size=1600&ready=1");
            Assert.Equal(GalleryFolders.MediumSize, LongEdge(await res.Content.ReadAsByteArrayAsync()));

            // Editor/Gesichter (original): nicht die mittlere, sondern die große aus dem Original - danach geht die große vor
            var big = await GalleryFolders.ThumbAsync(space, config, src.Id + "/gross.jpg", Thumbs.Large, CancellationToken.None, original: true);
            Assert.Equal(GalleryProxies.Size, LongEdge(big!));
            var best = await GalleryFolders.ThumbAsync(space, config, src.Id + "/gross.jpg", 1600, CancellationToken.None);
            Assert.Equal(GalleryProxies.Size, LongEdge(best!));

            // Schalter aus: die mittlere wird nicht geliefert (bisheriger Weg)
            Features.Set(Features.InReleaseOff.Append(Features.GalleryMedium));
            var off = await GalleryFolders.ThumbAsync(space, config, src.Id + "/klein.jpg", 1600, CancellationToken.None);
            Assert.Equal(1800, LongEdge(off!));
            Features.Set(Features.InReleaseOff.Where(x => x != Features.GalleryMedium));

            // Datei gelöscht: nach dem Einlesen ist auch die mittlere Vorschau weg
            var gone = StampOf(space.Db, src.Id, "klein.jpg");
            File.Delete(Path.Combine(root, "klein.jpg"));
            await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
            Assert.False(medium!.Has(GalleryProxies.SharedScope, "medium01", "klein.jpg", gone));
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public Task Mit_grossen_Vorschauen_keine_mittlere() => WithMediumAsync(async () =>
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var root = Directory.CreateTempSubdirectory("dmmed").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "gross.jpg"), Noisy(2000, 1500));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "medium02", Name = "Archiv", Path = root }]);
            var space = _fx.Space("ben");
            var c = _fx.As("ben");
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "medium02", path = "", watch = false, proxies = true })).StatusCode);
            var src = GalleryFolders.Of(space.Db).Single(s => s.Mount == "medium02");
            if (!src.Proxies)
            {
                var all = GalleryFolders.Of(space.Db);
                all.Single(s => s.Id == src.Id).Proxies = true;
                GalleryFolders.Save(space.Db, all);
                src = GalleryFolders.Of(space.Db).Single(s => s.Id == src.Id);
            }
            await new GalleryScanJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryScanJob>.Instance).RunOnceAsync(CancellationToken.None);
            var stamp = StampOf(space.Db, src.Id, "gross.jpg");
            Assert.True(GalleryFolders.Proxies!.Has(GalleryProxies.SharedScope, "medium02", "gross.jpg", stamp));
            Assert.False(GalleryFolders.Medium!.Has(GalleryProxies.SharedScope, "medium02", "gross.jpg", stamp));
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public Task Uebertragen_aus_der_Ablage_legt_Vorschauen_aus_der_lokalen_Datei_an() => WithMediumAsync(async () =>
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var root = Directory.CreateTempSubdirectory("dmmed").FullName;
        Directory.CreateDirectory(Path.Combine(root, "fotos", "2026"));
        var before = config.Current.ExternalFolders;
        try
        {
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "medium03", Name = "Archiv", Path = root }]);
            var space = _fx.Space("cleo");
            var c = _fx.As("cleo");
            // Galerie-Ordner ist ein Unterordner des Server-Ordners: Pfade im Index gelten ab dem Server-Ordner
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "medium03", path = "fotos", watch = false })).StatusCode);
            var src = GalleryFolders.Of(space.Db).Single(s => s.Mount == "medium03");
            await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
            FileStore.Put(space.Db, "urlaub-medium.jpg", Noisy(2000, 1500), "image/jpeg");

            var res = await c.PostAsJsonAsync("/api/files/external/transfer", new { fromMount = (string?)null, paths = new[] { "urlaub-medium.jpg" }, toMount = "medium03", to = "fotos/2026", move = true });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            const string rel = "fotos/2026/urlaub-medium.jpg";
            for (var i = 0; i < 100 && (!File.Exists(Path.Combine(root, "fotos", "2026", "urlaub-medium.jpg"))
                || space.Db.Scalar("SELECT stage FROM gallery_ext WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", rel)) is not 2L); i++)
                await Task.Delay(100);

            // Ein Eintrag, fertig (Stufe 2) mit Bildangaben und kleinem Vorschaubild - und die mittlere Vorschau
            Assert.Equal(1L, space.Db.Scalar("SELECT COUNT(*) FROM gallery_ext WHERE source = $s AND path LIKE '%urlaub-medium.jpg'", ("$s", src.Id)));
            Assert.Equal(2L, space.Db.Scalar("SELECT stage FROM gallery_ext WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", rel)));
            Assert.Equal(2000L, space.Db.Scalar("SELECT width FROM gallery_ext WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", rel)));
            var stamp = StampOf(space.Db, src.Id, rel);
            Assert.True(GalleryFolders.Medium!.Has(GalleryProxies.SharedScope, "medium03", rel, stamp));
            Assert.NotNull(await GalleryFolders.ThumbAsync(space, config, src.Id + "/" + rel, Thumbs.Small, CancellationToken.None));

            // Das nächste Einlesen kennt die Datei schon: kein zweiter Eintrag, Stand bleibt
            await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
            Assert.Equal(1L, space.Db.Scalar("SELECT COUNT(*) FROM gallery_ext WHERE source = $s AND path LIKE '%urlaub-medium.jpg'", ("$s", src.Id)));
            Assert.Equal(2L, space.Db.Scalar("SELECT stage FROM gallery_ext WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", rel)));
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public Task Nachts_nachholen_mit_Grenze() => WithMediumAsync(async () =>
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var root = Directory.CreateTempSubdirectory("dmmed").FullName;
        var before = config.Current.ExternalFolders;
        var perNight = GalleryMediumJob.PerNight;
        var pause = GalleryMediumJob.Pause;
        try
        {
            for (var i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(root, $"f{i}.jpg"), Plain(1600 + i, 1000, SKColors.Coral));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "medium04", Name = "Archiv", Path = root }]);
            var space = _fx.Space("dora");
            var c = _fx.As("dora");
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "medium04", path = "", watch = false })).StatusCode);
            var src = GalleryFolders.Of(space.Db).Single(s => s.Mount == "medium04");
            await new GalleryScanJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryScanJob>.Instance).RunOnceAsync(CancellationToken.None);
            src = GalleryFolders.Of(space.Db).Single(s => s.Id == src.Id);
            await PhotoWork.Drain();
            // Bestand von vor dem Schalter: ohne mittlere Vorschau
            foreach (var i in Enumerable.Range(0, 3)) GalleryFolders.Medium!.Remove(GalleryProxies.SharedScope, "medium04", $"f{i}.jpg");
            Assert.Equal(3, GalleryFolders.MissingMedium(space, config, src).Count);

            GalleryMediumJob.PerNight = 2;
            GalleryMediumJob.Pause = TimeSpan.Zero;
            var job = new GalleryMediumJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryMediumJob>.Instance);
            var night = new DateTime(2031, 3, 4, 2, 0, 0);
            var first = await job.RunOnceAsync(night, CancellationToken.None);
            var has = Enumerable.Range(0, 3).Count(i => GalleryFolders.Medium!.Has(GalleryProxies.SharedScope, "medium04", $"f{i}.jpg", StampOf(space.Db, src.Id, $"f{i}.jpg")));
            Assert.True(first == 2 && has == 2, $"erster Lauf {first}, vorhanden {has}, Zähler {space.Db.Get("gallery.medium.night")}");
            Assert.Single(GalleryFolders.MissingMedium(space, config, src));
            // Grenze der Nacht erreicht: nichts mehr; in der nächsten Nacht der Rest
            var second = await job.RunOnceAsync(night.AddHours(1), CancellationToken.None);
            Assert.Equal(0, second);
            Assert.Single(GalleryFolders.MissingMedium(space, config, src));
            await job.RunOnceAsync(night.AddDays(1), CancellationToken.None);
            Assert.Empty(GalleryFolders.MissingMedium(space, config, src));
            Assert.True(GalleryMediumJob.Night(night) && !GalleryMediumJob.Night(night.AddHours(5)));

            // Angehalten (Aktivität): läuft nicht
            foreach (var i in Enumerable.Range(0, 3)) GalleryFolders.Medium!.Remove(GalleryProxies.SharedScope, "medium04", $"f{i}.jpg");
            JobControl.Pause(space.User.Id, Activity.GalleryMedium);
            try { await job.RunOnceAsync(night.AddDays(2), CancellationToken.None); }
            finally { JobControl.Resume(space.User.Id, Activity.GalleryMedium); }
            Assert.Equal(3, GalleryFolders.MissingMedium(space, config, src).Count);
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            GalleryMediumJob.PerNight = perNight;
            GalleryMediumJob.Pause = pause;
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    });
}
