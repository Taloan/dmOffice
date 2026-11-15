using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Gemeinsamer Serverordner: Farbmarkierung, die eine Benutzerin in ein JPEG schreibt, sieht der andere nach dem Einlesen.</summary>
[Collection("server")]
public class GalleryLabelSharedTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryLabelSharedTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; }

    [Fact]
    public async Task Farbe_von_Cleo_sieht_Dora_nach_dem_Einlesen()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var cleo = _fx.Space("cleo");
        var dora = _fx.Space("dora");
        var writeBefore = cleo.Settings.PhotoWriteXmp;
        cleo.UpdateSettings(s => s.PhotoWriteXmp = true);
        var root = Directory.CreateTempSubdirectory("dmlabel").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            using (var bmp = new SKBitmap(64, 48)) { using (var cv = new SKCanvas(bmp)) cv.Clear(SKColors.Coral); Directory.CreateDirectory(Path.Combine(root, "PRI")); File.WriteAllBytes(Path.Combine(root, "PRI", "foto.jpg"), bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray()); }
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "label01", Name = "Gemeinsam", Path = root }]);
            async Task<GallerySource> Add(string who, UserSpace sp, string sub)
            {
                foreach (var old in GalleryFolders.Of(sp.Db)) await _fx.As(who).DeleteAsync("/api/gallery/sources/" + old.Id);
                Assert.Equal(HttpStatusCode.OK, (await _fx.As(who).PostAsJsonAsync("/api/gallery/sources", new { mount = "label01", path = sub })).StatusCode);
                return GalleryFolders.Of(sp.Db).Single(x => x.Mount == "label01");
            }
            async Task Read(UserSpace sp, GallerySource src)
            {
                await GalleryFolders.ScanAsync(sp, config, src, CancellationToken.None);
                await GalleryFolders.HeadsAsync(sp, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
                await GalleryFolders.FullAsync(sp, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            }
            async Task<string?> LabelOf(string who, GallerySource src, string rel)
            {
                var meta = (await _fx.As(who).GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray().ToList();
                var m = meta.SingleOrDefault(x => x.GetProperty("key").GetString() == "ext:" + src.Id + "/" + rel);
                if (m.ValueKind == JsonValueKind.Undefined) throw new Exception("Schluessel: " + string.Join(", ", meta.Select(x => x.GetProperty("key").GetString())) + " gesucht ext:" + src.Id + "/" + rel);
                return m.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            }
            var a = await Add("cleo", cleo, ""); await Read(cleo, a);
            var b = await Add("dora", dora, "PRI"); await Read(dora, b);
            Assert.Null(await LabelOf("dora", b, "PRI/foto.jpg"));

            var r = await _fx.As("cleo").PostAsJsonAsync("/api/gallery/info", new { items = new[] { $"ext:{a.Id}/PRI/foto.jpg" }, label = "red" });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            await GalleryInfo.Pending(cleo.User.Id);
            await DmOffice.Dav.PhotoWork.Drain();
            Assert.Contains("Rot", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "PRI", "foto.jpg"))));
            Assert.Equal("red", await LabelOf("cleo", a, "PRI/foto.jpg"));

            // Dora: wie auf dem Server über den Hintergrundjob
            var job = new GalleryScanJob(_fx.Factory.Services.GetRequiredService<Spaces>(), config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);
            Assert.Equal("red", await LabelOf("dora", b, "PRI/foto.jpg"));
            await _fx.As("cleo").DeleteAsync("/api/gallery/sources/" + a.Id);
            await _fx.As("dora").DeleteAsync("/api/gallery/sources/" + b.Id);
        }
        finally
        {
            cleo.UpdateSettings(s => s.PhotoWriteXmp = writeBefore);
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
