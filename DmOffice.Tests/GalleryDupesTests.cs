using System.Text;
using DmOffice.Dav;
using DmOffice.Gallery;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>
/// Doppelte in der Galerie: Lightroom ergänzt beim Import Metadaten im Dateikopf - die Prüfsumme der Bilddaten bleibt gleich,
/// ein anderes Bild hat eine andere. Gestapelt wird nur bei gleichem Kandidaten-Schlüssel und gleicher Prüfsumme, die Ablage oben.
/// </summary>
public class GalleryDupesTests
{
    private static byte[] Jpeg(SKColor color)
    {
        using var bmp = new SKBitmap(64, 48);
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    private static byte[] Segment(byte marker, byte[] body) =>
        [0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)((body.Length + 2) & 0xFF), .. body];

    /// <summary>Wie Lightroom: XMP gleich hinter dem Anfang, Photoshop-Block (APP13) vor dem Bildbeginn.</summary>
    private static byte[] LikeLightroom(byte[] jpeg)
    {
        var xmp = Segment(0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmp:Rating=\"3\"/>"));
        var ps = Segment(0xED, Encoding.ASCII.GetBytes("Photoshop 3.0\08BIM"));
        var sof = IndexOfMarker(jpeg, 0xC0);
        return [.. jpeg[..2], .. xmp, .. jpeg[2..sof], .. ps, .. jpeg[sof..]];
    }

    private static int IndexOfMarker(byte[] b, byte marker)
    {
        var i = 2;
        while (b[i + 1] != marker) i += 2 + ((b[i + 2] << 8) | b[i + 3]);
        return i;
    }

    private static Task<string?> Hash(byte[] b) => GalleryDupes.ImageHashAsync(new MemoryStream(b), CancellationToken.None);

    [Fact]
    public async Task Metadaten_im_Kopf_aendern_die_Pruefsumme_der_Bilddaten_nicht()
    {
        var a = Jpeg(SKColors.Orange);
        var b = LikeLightroom(a);
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a)), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)));
        Assert.Equal(await Hash(a), await Hash(b));
        Assert.NotNull(await Hash(a));
    }

    [Fact]
    public async Task Anderes_Bild_andere_Pruefsumme_und_kein_JPEG_ergibt_null()
    {
        Assert.NotEqual(await Hash(Jpeg(SKColors.Orange)), await Hash(Jpeg(SKColors.Blue)));
        Assert.Null(await Hash(Encoding.ASCII.GetBytes("kein Bild")));
    }

    private static GalleryTimeline.Entry Photo(string id, long taken = 1_762_341_675_000, string camera = "Galaxy Z Fold7", List<GalleryTimeline.Entry>? stack = null) =>
        new(id, id[(id.LastIndexOf('/') + 1)..], 13_789_128, "image/jpeg", 0, "e", id.StartsWith("ext:") ? 0 : 1,
            new FileMeta.Row(id, taken, camera, null, 6, 1.7, 1.0 / 3309, 80, null, null, 4000, 3000, null, null, null, null), null, Stack: stack);

    [Fact]
    public void Gleiches_Foto_in_Ablage_und_Galerie_Ordner_wird_gestapelt_Ablage_oben()
    {
        var own = Photo("Bilder/2026/20261105_122115.jpg");
        var raw = Photo("ext:s1/RAW/PRI/2026/11/05/x.dng");
        var ext = Photo("ext:s1/RAW/PRI/2026/11/05/20261105_122115_samsung.jpg", stack: [raw]);
        var other = Photo("Bilder/2026/20261105_122116.jpg", taken: 1_762_341_676_000);
        var hashes = new Dictionary<string, string> { [own.Id] = "aa", [ext.Id] = "aa", [other.Id] = "aa" };

        var list = GalleryDupes.Stack([ext, other, own], hashes);

        Assert.Equal(2, list.Count);
        var lead = Assert.Single(list, e => e.Stack != null);
        Assert.Equal(own.Id, lead.Id);
        Assert.Equal([ext.Id, raw.Id], lead.Stack!.Select(x => x.Id));
        Assert.All(lead.Stack!, x => Assert.True(x.Dupe));
        Assert.Contains(list, e => e.Id == other.Id);
    }

    [Fact]
    public void Gleicher_Schluessel_aber_andere_Bilddaten_bleibt_getrennt()
    {
        // Serienbild: gleiche Sekunde, gleiche Einstellungen, andere Bilddaten
        var a = Photo("Bilder/a.jpg");
        var b = Photo("ext:s1/b.jpg");
        var list = GalleryDupes.Stack([a, b], new Dictionary<string, string> { [a.Id] = "aa", [b.Id] = "bb" });
        Assert.Equal(2, list.Count);
        Assert.All(list, e => Assert.Null(e.Stack));
    }

    [Fact]
    public void Ohne_Kamera_oder_Aufnahmezeit_kein_Kandidat()
    {
        Assert.Null(GalleryDupes.CandidateKey(Photo("Bilder/a.jpg", camera: "")));
        Assert.Null(GalleryDupes.CandidateKey(Photo("Bilder/a.heic")));
        Assert.NotNull(GalleryDupes.CandidateKey(Photo("Bilder/a.jpg")));
    }
}
