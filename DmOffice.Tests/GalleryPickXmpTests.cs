using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Markierung P/X/U mit „Angaben in die Fotos schreiben“: als xmpDM:pick in die Datei (Kennung pickxmp).</summary>
[Collection("server")]
public class GalleryPickXmpTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryPickXmpTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; }

    [Fact]
    public async Task Markierung_kommt_in_die_Datei_und_geht_vor_alter_eigener()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var dora = _fx.Space("dora");
        var writeBefore = dora.Settings.PhotoWriteXmp;
        var root = Directory.CreateTempSubdirectory("dmpick").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "foto.jpg"), XmpForeignLayoutTests.JpegWithXmp(XmpForeignLayoutTests.PacketRot4));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "pick01", Name = "Pick", Path = root }]);
            var c = _fx.As("dora");
            foreach (var old in GalleryFolders.Of(dora.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "pick01", path = "" })).StatusCode);
            var job = new GalleryScanJob(_fx.Factory.Services.GetRequiredService<Spaces>(), config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);
            var src = GalleryFolders.Of(dora.Db).Single();
            var id = $"ext:{src.Id}/foto.jpg";

            // Vorher (Schreiben aus): X nur in dmOffice
            dora.UpdateSettings(s => s.PhotoWriteXmp = false);
            await Post(c, new { items = new[] { id }, pick = -1 });
            Assert.Equal(-1, await PickOf(c, id));

            // Schreiben an: P kommt in die Datei, die alte eigene X geht nicht mehr vor
            dora.UpdateSettings(s => s.PhotoWriteXmp = true);
            await Post(c, new { items = new[] { id }, pick = 1 });
            string Text() => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "foto.jpg")));
            for (var i = 0; i < 100 && !Text().Contains("pick=\"1\""); i++) await Task.Delay(100);
            Assert.Contains("pick=\"1\"", Text());
            Assert.Equal(1, await PickOf(c, id));

            // U: Eintrag in der Datei weg
            await Post(c, new { items = new[] { id }, pick = 0 });
            for (var i = 0; i < 100 && Text().Contains("pick=\"1\""); i++) await Task.Delay(100);
            Assert.DoesNotContain("pick=\"1\"", Text());
            Assert.Null(await PickOf(c, id));
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            dora.UpdateSettings(s => s.PhotoWriteXmp = writeBefore);
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PNG_mit_Schreiben_an_Markierung_und_Sterne_nur_in_dmOffice_ohne_Fehler()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var dora = _fx.Space("dora");
        var writeBefore = dora.Settings.PhotoWriteXmp;
        var root = Directory.CreateTempSubdirectory("dmpickpng").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            using (var bmp = new SkiaSharp.SKBitmap(8, 8))
            using (var img = SkiaSharp.SKImage.FromBitmap(bmp))
            using (var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(Path.Combine(root, "bild.png"), data.ToArray());
            var original = File.ReadAllBytes(Path.Combine(root, "bild.png"));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "pickpng", Name = "PNG", Path = root }]);
            var c = _fx.As("dora");
            foreach (var old in GalleryFolders.Of(dora.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "pickpng", path = "" })).StatusCode);
            var job = new GalleryScanJob(_fx.Factory.Services.GetRequiredService<Spaces>(), config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);
            var src = GalleryFolders.Of(dora.Db).Single();
            var id = $"ext:{src.Id}/bild.png";
            dora.UpdateSettings(s => s.PhotoWriteXmp = true);
            await Post(c, new { items = new[] { id }, pick = -1, rating = 3 });
            Assert.Equal(-1, await PickOf(c, id));
            // Die Datei bleibt unverändert; nur der Ort scheitert (braucht die Datei)
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "bild.png")));
            var r = await c.PostAsJsonAsync("/api/gallery/info", new { items = new[] { id }, lat = 50.0, lon = 8.0 });
            Assert.Single((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failed").EnumerateArray());
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            dora.UpdateSettings(s => s.PhotoWriteXmp = writeBefore);
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task Post(HttpClient c, object body)
    {
        var r = await c.PostAsJsonAsync("/api/gallery/info", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await GalleryInfo.AllPending();
        await DmOffice.Dav.PhotoWork.Drain();
        Assert.Empty((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failed").EnumerateArray());
    }

    private static async Task<int?> PickOf(HttpClient c, string id)
    {
        var m = (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray().Single(x => x.GetProperty("key").GetString() == id);
        return m.TryGetProperty("pick", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
    }
}
