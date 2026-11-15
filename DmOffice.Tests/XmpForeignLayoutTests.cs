using DmOffice.Dav;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>XMP, das die Bibliothek nicht liest (Namensräume nur an x:xmpmeta): Bewertung und Farbe kommen trotzdem an.</summary>
public class XmpForeignLayoutTests
{
    internal const string PacketRot4 = "<?xpacket begin=\"\ufeff\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n<x:xmpmeta xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\" "
        + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:x=\"adobe:ns:meta/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:xmpDM=\"http://ns.adobe.com/xmp/1.0/DynamicMedia/\">\n"
        + " <rdf:RDF>\n  <rdf:Description rdf:about=\"\" xmp:Rating=\"4\" xmpDM:pick=\"0\" xmp:Label=\"Rot\" photoshop:LabelColor=\"red\">\n"
        + "   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\" /></rdf:Alt></dc:title>\n  </rdf:Description>\n </rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>";

    internal static byte[] JpegWithXmp(string packet)
    {
        using var bmp = new SKBitmap(32, 24);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Olive);
        var jpg = bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
        var payload = System.Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0").Concat(System.Text.Encoding.UTF8.GetBytes(packet)).ToArray();
        var len = payload.Length + 2;
        return jpg.Take(2).Concat(new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len }).Concat(payload).Concat(jpg.Skip(2)).ToArray();
    }

    [Fact]
    public void Bewertung_und_Farbe_aus_XMP_mit_Namensraeumen_an_xmpmeta()
    {
        var row = FileMeta.Read("foto.jpg", new MemoryStream(JpegWithXmp(PacketRot4)), TimeZoneInfo.Utc);
        Assert.NotNull(row);
        Assert.Equal(4, row!.Rating);
        Assert.Equal("red", row.Label);
        Assert.Null(row.Pick);
    }
}
