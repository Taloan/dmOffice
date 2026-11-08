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

/// <summary>Titel, Bildunterschrift und Bewertung aus der Galerie: in eigene Fotos und in Galerie-Ordner (JPEG in die Datei, RAW in die Sidecar-Datei).</summary>
[Collection("server")]
public class GalleryInfoTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryInfoTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; }

    private static byte[] Jpeg()
    {
        using var bmp = new SKBitmap(300, 200);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.SteelBlue);
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static async Task<JsonElement> Info(HttpClient c, object body)
    {
        var r = await c.PostAsJsonAsync("/api/gallery/info", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // Die Dateien schreibt der Server im Hintergrund
        await GalleryInfo.AllPending();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failed");
    }

    [Fact]
    public async Task Eigenes_JPEG_bekommt_Titel_Beschreibung_und_Bewertung()
    {
        var user = "info" + Guid.NewGuid().ToString("N")[..6];
        var u = _fx.Factory.Services.GetRequiredService<UserStore>().Create(user, "Info", "geheim-123456", isAdmin: false);
        _fx.Users[user] = (u.Id, AppPasswords.Create(_fx.Factory.Services.GetRequiredService<Spaces>().Get(u.Id).Db, "Test"));
        var space = _fx.Space(user);
        space.UpdateSettings(s => s.PhotoWriteXmp = true);
        var c = _fx.As(user);
        FileStore.Put(space.Db, "urlaub.jpg", Jpeg(), "image/jpeg");
        FileStore.Put(space.Db, "notiz.txt", "x"u8.ToArray(), "text/plain");

        var failed = await Info(c, new { items = new[] { "urlaub.jpg", "notiz.txt" }, title = "Pfelders", description = "Am See", rating = 4 });
        Assert.Equal("notiz.txt", failed.EnumerateArray().Single().GetProperty("name").GetString());
        var m = FileMeta.List(space.Db, null).Single(x => x.Path == "urlaub.jpg");
        Assert.Equal(("Pfelders", "Am See", 4), (m.Title, m.Description, m.Rating));
        // In der Datei selbst (liest auch Lightroom)
        var text = System.Text.Encoding.UTF8.GetString(FileStore.Data(space.Db, "urlaub.jpg")!);
        Assert.Contains("Pfelders", text);

        // Nur die Bewertung entfernen: Titel und Beschreibung bleiben
        Assert.Empty((await Info(c, new { items = new[] { "urlaub.jpg" }, rating = 0 })).EnumerateArray());
        m = FileMeta.List(space.Db, null).Single(x => x.Path == "urlaub.jpg");
        Assert.Equal(("Pfelders", "Am See", (int?)null), (m.Title, m.Description, m.Rating));

        // Stichwörter: hinzufügen, dann eines entfernen - die übrigen bleiben
        Assert.Empty((await Info(c, new { items = new[] { "urlaub.jpg" }, addKeywords = new[] { "Berge", "Südtirol", "Wandern" } })).EnumerateArray());
        Assert.Empty((await Info(c, new { items = new[] { "urlaub.jpg" }, removeKeywords = new[] { "berge" } })).EnumerateArray());
        m = FileMeta.List(space.Db, null).Single(x => x.Path == "urlaub.jpg");
        Assert.Equal("Südtirol\nWandern", m.Keywords);

        // Markierung, dann Farbe, dann Sterne nacheinander (wie schnell gedrückte Tasten P, 6, 4)
        Assert.Empty((await Info(c, new { items = new[] { "urlaub.jpg" }, pick = 1 })).EnumerateArray());
        Assert.Empty((await Info(c, new { items = new[] { "urlaub.jpg" }, label = "red" })).EnumerateArray());
        Assert.Empty((await Info(c, new { items = new[] { "urlaub.jpg" }, rating = 4 })).EnumerateArray());
        m = FileMeta.List(space.Db, null).Single(x => x.Path == "urlaub.jpg");
        Assert.Equal((1, "red", 4), (m.Pick, m.Label, m.Rating));

        // Nichts angegeben: abgelehnt
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/gallery/info", new { items = new[] { "urlaub.jpg" } })).StatusCode);
    }

    [Fact]
    public async Task Galerie_Ordner_JPEG_in_die_Datei_RAW_in_die_Sidecar_Datei()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("ben");
        var writeBefore = space.Settings.PhotoWriteXmp;
        space.UpdateSettings(s => s.PhotoWriteXmp = true);
        var root = Directory.CreateTempSubdirectory("dminfo").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "foto.jpg"), Jpeg());
            File.WriteAllBytes(Path.Combine(root, "bild.raf"), new byte[4096]);
            // Lightroom-Sidecar mit Ort und fremden Angaben: bleiben erhalten
            File.WriteAllText(Path.Combine(root, "bild.xmp"), """
                <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                  <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:exif="http://ns.adobe.com/exif/1.0/"
                    xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/" xmp:Rating="2" crs:Exposure2012="+0.50"
                    exif:GPSLatitude="46,47.491843N" exif:GPSLongitude="11,4.998587E"/>
                </rdf:RDF></x:xmpmeta>
                """);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "info01", Name = "Info", Path = root }]);
            var c = _fx.As("ben");
            foreach (var old in GalleryFolders.Of(space.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "info01", path = "" })).StatusCode);
            var job = new GalleryScanJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);
            var src = GalleryFolders.Of(space.Db).Single();

            var failed = await Info(c, new { items = new[] { $"ext:{src.Id}/foto.jpg", $"ext:{src.Id}/bild.raf" }, title = "Südtirol", description = "Pfelders", rating = 5 });
            Assert.Empty(failed.EnumerateArray());

            // RAW: Sidecar-Datei geändert, Fremdes und Ort bleiben
            var xmp = File.ReadAllText(Path.Combine(root, "bild.xmp"));
            Assert.Contains("Südtirol", xmp);
            Assert.Contains("Exposure2012", xmp);
            Assert.Contains("GPSLatitude", xmp);
            Assert.False(File.Exists(Path.Combine(root, "bild.raf.xmp")));
            // JPEG: in der Datei selbst - mit Arbeitskopien schreibt PhotoWork im Hintergrund, also kurz warten
            string Written() { try { return System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "foto.jpg"))); } catch (IOException) { return ""; } }
            for (var i = 0; i < 100 && !Written().Contains("Pfelders"); i++) await Task.Delay(100);
            Assert.Contains("Pfelders", Written());
            for (var i = 0; i < 100 && PhotoWork.IsPending(src.Id, "foto.jpg"); i++) await Task.Delay(100);

            // Die Galerie zeigt es gleich - und das nächste Einlesen behält es
            async Task Check()
            {
                var meta = (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray().ToList();
                foreach (var n in new[] { "foto.jpg", "bild.raf" })
                {
                    var m = meta.Single(x => x.GetProperty("key").GetString() == $"ext:{src.Id}/{n}");
                    Assert.Equal(("Südtirol", "Pfelders", 5), (m.GetProperty("title").GetString(), m.GetProperty("description").GetString(), m.GetProperty("rating").GetInt32()));
                }
            }
            await Check();
            GalleryScanJob.Request(space.User.Id, src.Id);
            await job.RunOnceAsync(CancellationToken.None);
            await Check();

            // Gruppiert nach Quelle: Abschnitte tragen den Namen des Galerie-Ordners
            var buckets = (await c.GetFromJsonAsync<JsonElement>("/api/gallery/timeline/buckets?group=source")).GetProperty("buckets").EnumerateArray().ToList();
            Assert.Contains(buckets, b => b.TryGetProperty("l", out var l) && l.GetString() == "Info" && b.GetProperty("k").GetString()!.StartsWith('s'));

            // XMP-Dateien für den Ordner aus: RAW lässt sich nicht ändern (die Angaben stünden nirgends, wo die Galerie sie liest)
            await c.PutAsJsonAsync("/api/gallery/sources/" + src.Id, new { sidecars = false });
            failed = await Info(c, new { items = new[] { $"ext:{src.Id}/bild.raf" }, rating = 1 });
            Assert.Single(failed.EnumerateArray());
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            space.UpdateSettings(s => s.PhotoWriteXmp = writeBefore);
            Directory.Delete(root, recursive: true);
        }
    }
}
