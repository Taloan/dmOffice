using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Formate, in die dmOffice nicht schreibt (PNG …): Angaben nur in dmOffice, kein Fehler.</summary>
[Collection("server")]
public class GalleryPngInfoTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryPngInfoTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; }

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
