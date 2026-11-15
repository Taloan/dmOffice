using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>
/// „In Ablage sichern“: Fotos aus einem Galerie-Ordner als Kopie in die Ablage, unter Uploads/Herkunft/Jahr/Monat; was schon in
/// der Ablage liegt, wird übersprungen; die Quelle bleibt; Vorgaben je Person.
/// </summary>
[Collection("server")]
public class GallerySaveTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GallerySaveTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; }

    [Fact]
    public void Zielordner_nach_Ordnung()
    {
        var t = new DateTime(2026, 11, 9, 10, 9, 44);
        Assert.Equal("Uploads/Sony/2026/11", GallerySave.TargetDir("Uploads", GallerySave.SourceMonth, "Sony", "RAW/PRI/2026/11/09/x.jpg", t));
        Assert.Equal("Uploads/2026/11", GallerySave.TargetDir("Uploads", GallerySave.Month, "Sony", "x.jpg", t));
        Assert.Equal("Fotos/Neu/2026/11/09", GallerySave.TargetDir("/Fotos/Neu/", GallerySave.Day, "Sony", "x.jpg", t));
        Assert.Equal("Uploads/Sony/RAW/PRI/2026/11/09", GallerySave.TargetDir("Uploads", GallerySave.Structure, "Sony", "RAW/PRI/2026/11/09/x.jpg", t));
        Assert.Equal("Uploads/Sony", GallerySave.TargetDir("Uploads", GallerySave.Structure, "Sony", "x.jpg", t));
        Assert.Equal("Uploads/Sony/2026/11", GallerySave.TargetDir("", GallerySave.SourceMonth, "Sony", "x.jpg", t));
    }

    [Fact]
    public async Task Kopiert_in_die_Ablage_ueberspringt_Vorhandenes_und_laesst_die_Quelle()
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.SaveToAblage));
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var ben = _fx.Space("ben");
        var root = Directory.CreateTempSubdirectory("dmsave").FullName;
        var folders = config.Current.ExternalFolders;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "2025"));
            var a = Path.Combine(root, "2025", "neu.jpg");
            var b = Path.Combine(root, "schon-da.jpg");
            File.WriteAllBytes(a, PhotoEditTests.Jpeg(1));
            File.WriteAllBytes(b, PhotoEditTests.Jpeg(2));
            // Das Dateidatum zählt nicht: das Testbild trägt als Aufnahmezeit Oktober 2026
            File.SetLastWriteTimeUtc(a, new DateTime(2025, 3, 15, 12, 0, 0, DateTimeKind.Utc));
            // Dasselbe Bild liegt schon in Bens Ablage
            FileStore.EnsureDir(ben.Db, "Bilder");
            FileStore.Put(ben.Db, "Bilder/schon-da.jpg", File.ReadAllBytes(b), "image/jpeg");

            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "save01", Name = "Kamera Archiv", Path = root }]);
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("ben").PostAsJsonAsync("/api/gallery/sources", new { mount = "save01", path = "" })).StatusCode);
            var src = GalleryFolders.Of(ben.Db).Single(x => x.Mount == "save01");
            await GalleryFolders.ScanAsync(ben, config, src, CancellationToken.None);
            await GalleryFolders.HeadsAsync(ben, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            GalleryTimeline.Forget(ben.User.Id);

            // Vorgaben: Uploads, Herkunft/Jahr/Monat
            var settings = await _fx.As("ben").GetFromJsonAsync<JsonElement>("/api/gallery/save/settings");
            Assert.Equal(("Uploads", "source-month", true), (settings.GetProperty("folder").GetString(), settings.GetProperty("order").GetString(), settings.GetProperty("skipKnown").GetBoolean()));

            var r = await _fx.As("ben").PostAsJsonAsync("/api/gallery/save", new { ids = new[] { "ext:" + src.Id + "/2025/neu.jpg", src.Id + "/schon-da.jpg" } });
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            var target = "Uploads/Kamera Archiv/2026/10/neu.jpg";
            for (var i = 0; i < 100 && FileStore.Get(ben.Db, target) == null; i++) await Task.Delay(100);
            await Task.Delay(300);
            Assert.True(FileStore.Get(ben.Db, target) != null, "In der Ablage: " + string.Join(", ", FileStore.Descendants(ben.Db, "Uploads").Select(f => f.Path)));
            Assert.Equal(File.ReadAllBytes(a), ReadAll(ben.Db, target));
            // Schon vorhanden: nicht noch einmal
            Assert.Null(FileStore.Get(ben.Db, "Uploads/Kamera Archiv/2026/10/schon-da.jpg"));
            Assert.DoesNotContain(FileStore.Descendants(ben.Db, "Uploads"), f => f.Name.StartsWith("schon-da", StringComparison.Ordinal));
            // Die Quelle bleibt
            Assert.True(File.Exists(a) && File.Exists(b));
            // Fertig: nichts mehr offen (Zwischensymbol in App und Web verschwindet)
            var open = 1;
            for (var i = 0; i < 50 && open > 0; i++) { open = (await _fx.As("ben").GetFromJsonAsync<JsonElement>("/api/gallery/save/pending")).GetProperty("ids").GetArrayLength(); if (open > 0) await Task.Delay(100); }
            Assert.Equal(0, open);

            // Noch einmal sichern: liegt schon in der Ablage, keine zweite Kopie
            Assert.Equal(HttpStatusCode.Accepted, (await _fx.As("ben").PostAsJsonAsync("/api/gallery/save", new { ids = new[] { src.Id + "/2025/neu.jpg" } })).StatusCode);
            await Task.Delay(1000);
            Assert.Null(FileStore.Get(ben.Db, "Uploads/Kamera Archiv/2026/10/neu (2).jpg"));

            // Eigene Vorgaben
            var put = await _fx.As("ben").PutAsJsonAsync("/api/gallery/save/settings", new { folder = "Fotos/Gesichert", order = "month", skipKnown = true, hintSeen = true });
            var saved = await put.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(("Fotos/Gesichert", "month", true), (saved.GetProperty("folder").GetString(), saved.GetProperty("order").GetString(), saved.GetProperty("hintSeen").GetBoolean()));
            Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("ben").PutAsJsonAsync("/api/gallery/save/settings", new { folder = "x", order = "quatsch" })).StatusCode);
            // Nur Ablage-Fotos ausgewählt: nichts zu tun
            Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("ben").PostAsJsonAsync("/api/gallery/save", new { ids = new[] { "Bilder" } })).StatusCode);

            // Ausgeschaltet: nicht erreichbar
            Features.Set(before);
            if (Features.InReleaseOff.Contains(Features.SaveToAblage))
                Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync("/api/gallery/save/settings")).StatusCode);
        }
        finally
        {
            Features.Set(before);
            config.Update(s => s.ExternalFolders = folders);
            ben.UpdateSettings(s => { s.SaveFolder = "Uploads"; s.SaveOrder = "source-month"; s.SaveHintSeen = false; });
        }
    }

    private static byte[] ReadAll(UserDb db, string path)
    {
        using var s = FileStore.OpenRead(db, path);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}
