using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>
/// „Angaben in die Fotos schreiben“ an, das Schreiben ins Foto scheitert aber (Ordner nur zum Lesen, JPEG mit ungewöhnlichem Aufbau):
/// Markierung und Sterne bleiben in der Datenbank und gehen beim erneuten Einlesen der Datei nicht verloren.
/// </summary>
[Collection("server")]
public class GalleryWriteFallbackTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryWriteFallbackTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; }

    [Fact]
    public Task Ordner_nur_zum_Lesen_Ablehnen_bleibt_nach_Neueinlesen() =>
        Run("wfro", readOnly: true, XmpForeignLayoutTests.JpegWithXmp(XmpForeignLayoutTests.PacketRot4));

    [Fact]
    public Task Jpeg_mit_ungewoehnlichem_Aufbau_Ablehnen_bleibt_nach_Neueinlesen()
    {
        // Füllbytes zwischen zwei Abschnitten: Leser kommen damit zurecht, dmOffice schreibt in eine solche Datei nicht
        var ok = XmpForeignLayoutTests.JpegWithXmp(XmpForeignLayoutTests.PacketRot4);
        var odd = new byte[ok.Length + 3];
        ok.AsSpan(0, 2).CopyTo(odd);
        odd[2] = 0x00; odd[3] = 0x00; odd[4] = 0x00;
        ok.AsSpan(2).CopyTo(odd.AsSpan(5));
        return Run("wfodd", readOnly: false, odd);
    }

    private async Task Run(string mountId, bool readOnly, byte[] jpeg)
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var dora = _fx.Space("dora");
        var writeBefore = dora.Settings.PhotoWriteXmp;
        var root = Directory.CreateTempSubdirectory("dmwf").FullName;
        var before = config.Current.ExternalFolders;
        var file = Path.Combine(root, "handy.jpg");
        try
        {
            File.WriteAllBytes(file, jpeg);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = mountId, Name = "Handy", Path = root, ReadOnly = readOnly }]);
            var c = _fx.As("dora");
            foreach (var old in GalleryFolders.Of(dora.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = mountId, path = "" })).StatusCode);
            var job = new GalleryScanJob(_fx.Factory.Services.GetRequiredService<Spaces>(), config, Microsoft.Extensions.Logging.Abstractions.NullLogger<GalleryScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);
            var src = GalleryFolders.Of(dora.Db).Single();
            var id = $"ext:{src.Id}/handy.jpg";

            dora.UpdateSettings(s => s.PhotoWriteXmp = true);
            var r = await c.PostAsJsonAsync("/api/gallery/info", new { items = new[] { id }, pick = -1, rating = 2 });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Empty((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failed").EnumerateArray());
            await GalleryInfo.AllPending();
            await DmOffice.Dav.PhotoWork.Drain();
            // Die Datei bleibt, wie sie war
            Assert.Equal(jpeg, File.ReadAllBytes(file));
            Assert.Equal((-1, 2), await MetaOf(c, id));

            // Erneut einlesen (Datei mit neuer Zeit: der Eintrag kommt wieder aus der Datei) - die Angaben bleiben
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
            await job.RunOnceAsync(CancellationToken.None);
            await GalleryFolders.ScanAsync(dora, config, src, CancellationToken.None);
            await GalleryFolders.HeadsAsync(dora, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            await GalleryFolders.FullAsync(dora, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            await GalleryFolders.VisibleAsync(dora, config, [src.Id + "/handy.jpg"], CancellationToken.None);
            Assert.Equal((-1, 2), await MetaOf(c, id));
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            dora.UpdateSettings(s => s.PhotoWriteXmp = writeBefore);
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task<(int?, int?)> MetaOf(HttpClient c, string id)
    {
        var m = (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray().Single(x => x.GetProperty("key").GetString() == id);
        int? N(string k) => m.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
        return (N("pick"), N("rating"));
    }
}
