using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Dav;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Stichwörter im XMP: hinzufügen, entfernen; Lightrooms hierarchische Stichwörter gehen beim Entfernen mit.</summary>
public class PhotoMetaKeywordTests
{
    [Fact]
    public void Stichwoerter_hinzufuegen_und_entfernen_auch_hierarchisch()
    {
        const string old = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:lr="http://ns.adobe.com/lightroom/1.0/">
                <dc:subject><rdf:Bag><rdf:li>Urlaub</rdf:li><rdf:li>Pfelders</rdf:li></rdf:Bag></dc:subject>
                <lr:hierarchicalSubject><rdf:Bag><rdf:li>Orte|Italien|Pfelders</rdf:li><rdf:li>Reisen|Urlaub</rdf:li></rdf:Bag></lr:hierarchicalSubject>
              </rdf:Description></rdf:RDF></x:xmpmeta>
            """;
        var x = DmOffice.Dav.PhotoMeta.WriteXmp(old, new DmOffice.Dav.PhotoMeta.Fields(AddKeywords: ["Berge", "urlaub"], RemoveKeywords: ["Pfelders"]));
        var d = DmOffice.Gallery.Sidecar.Read(System.Text.Encoding.UTF8.GetBytes(x))!;
        Assert.Equal(["Urlaub", "Berge"], d.Keywords);
        Assert.DoesNotContain("Orte|Italien|Pfelders", x);
        Assert.Contains("Reisen|Urlaub", x);
    }
}

/// <summary>
/// Metadaten schreiben ohne exiftool (PhotoMeta): JPEG bis auf APP1 bitgleich, GPS lesbar, XMP-Zusammenführen erhält fremde Angaben;
/// Fotos aus einer Aktivität verorten (Interpolation auf der Strecke, Ablage mit Version, RAW per Sidecar).
/// </summary>
[Collection("server")]
public class PhotoMetaTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PhotoMetaTests(ChatFixture fx) => _fx = fx;

    /// <summary>Die Datei ohne alle APP1-Abschnitte (EXIF, XMP) - der Rest muss vor und nach dem Schreiben gleich sein.</summary>
    private static byte[] WithoutApp1(byte[] b)
    {
        var o = new List<byte> { b[0], b[1] };
        var pos = 2;
        while (b[pos] == 0xFF && b[pos + 1] != 0xDA)
        {
            var len = b[pos + 2] << 8 | b[pos + 3];
            if (b[pos + 1] != 0xE1) o.AddRange(b[pos..(pos + 2 + len)]);
            pos += 2 + len;
        }
        o.AddRange(b[pos..]);
        return [.. o];
    }

    private static (double Lat, double Lon)? Gps(byte[] jpeg) =>
        ImageMetadataReader.ReadMetadata(new MemoryStream(jpeg)).OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation() is { IsZero: false } g ? (g.Latitude, g.Longitude) : null;

    private static string? Tag<T>(byte[] jpeg, int tag) where T : MetadataExtractor.Directory =>
        ImageMetadataReader.ReadMetadata(new MemoryStream(jpeg)).OfType<T>().FirstOrDefault()?.GetString(tag);

    [Fact]
    public void Jpeg_mit_Exif_bekommt_GPS_und_bleibt_sonst_bitgleich()
    {
        var src = PhotoEditTests.Jpeg(6);
        var dst = PhotoMeta.WriteJpeg(src, new PhotoMeta.Fields(48.137154, -11.576124));
        Assert.Equal(WithoutApp1(src), WithoutApp1(dst));
        var g = Gps(dst)!.Value;
        Assert.Equal(48.137154, g.Lat, 6);
        Assert.Equal(-11.576124, g.Lon, 6);
        // Aufnahmezeit und Drehangabe wie vorher
        Assert.Equal("2026:10:27 13:48:20", Tag<ExifSubIfdDirectory>(dst, ExifDirectoryBase.TagDateTimeOriginal));
        Assert.Equal("6", Tag<ExifIfd0Directory>(dst, ExifDirectoryBase.TagOrientation));
        // Noch einmal schreiben: ersetzt, statt ein zweites GPS-IFD zu hängen
        var again = PhotoMeta.WriteJpeg(dst, new PhotoMeta.Fields(-33.5, 151.25));
        Assert.Equal((-33.5, 151.25), (Math.Round(Gps(again)!.Value.Lat, 6), Math.Round(Gps(again)!.Value.Lon, 6)));
        Assert.Equal(WithoutApp1(src), WithoutApp1(again));
    }

    /// <summary>EXIF-Abschnitt bis zur Höchstgröße aufgefüllt (wie bei Fujifilm) - mit Nullen oder mit etwas anderem.</summary>
    private static byte[] Padded(byte[] jpeg, byte fill)
    {
        var pos = 2;
        while (!(jpeg[pos + 1] == 0xE1 && Encoding.ASCII.GetString(jpeg, pos + 4, 4) == "Exif")) pos += 2 + (jpeg[pos + 2] << 8 | jpeg[pos + 3]);
        var len = jpeg[pos + 2] << 8 | jpeg[pos + 3];
        var grow = 65535 - len;
        var o = new List<byte>(jpeg[..pos]) { 0xFF, 0xE1, 0xFF, 0xFF };
        o.AddRange(jpeg[(pos + 4)..(pos + 2 + len)]);
        // fill 1: Muster wie bei Fujifilm (wiederholt sich alle 256 Byte), 2: Zufall (also echte Daten), sonst ein Byte
        var rnd = new Random(7);
        o.AddRange(Enumerable.Range(0, grow).Select(i => fill switch { 1 => (byte)(i % 256 * 7 + 3), 2 => (byte)rnd.Next(256), _ => fill }));
        o.AddRange(jpeg[(pos + 2 + len)..]);
        return [.. o];
    }

    [Fact]
    public void Fujifilm_voller_Exif_Abschnitt_GPS_kommt_in_die_Fuellung()
    {
        var src = Padded(PhotoEditTests.Jpeg(6), 0);
        Assert.Equal("2026:10:27 13:48:20", Tag<ExifSubIfdDirectory>(src, ExifDirectoryBase.TagDateTimeOriginal));
        var dst = PhotoMeta.WriteJpeg(src, new PhotoMeta.Fields(47.2692, 11.4041));
        Assert.Equal((47.2692, 11.4041), (Math.Round(Gps(dst)!.Value.Lat, 6), Math.Round(Gps(dst)!.Value.Lon, 6)));
        Assert.Equal("2026:10:27 13:48:20", Tag<ExifSubIfdDirectory>(dst, ExifDirectoryBase.TagDateTimeOriginal));
        Assert.Equal("6", Tag<ExifIfd0Directory>(dst, ExifDirectoryBase.TagOrientation));
        Assert.Equal(WithoutApp1(src), WithoutApp1(dst));
        Assert.Equal((47.2692, 11.4041), (Math.Round(Gps(PhotoMeta.WriteJpeg(Padded(PhotoEditTests.Jpeg(6), 1), new PhotoMeta.Fields(47.2692, 11.4041)))!.Value.Lat, 6), 11.4041));
        // Steht hinter den Daten etwas anderes als Füllung, bleibt die Datei, wie sie ist
        Assert.Throws<InvalidDataException>(() => PhotoMeta.WriteJpeg(Padded(PhotoEditTests.Jpeg(6), 2), new PhotoMeta.Fields(47.2692, 11.4041)));
    }

    [Fact]
    public void Jpeg_ohne_Exif_bekommt_neues_Exif_hinter_JFIF()
    {
        using var bmp = new SKBitmap(64, 48);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Green);
        var src = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        Assert.Null(Gps(src));
        var dst = PhotoMeta.WriteJpeg(src, new PhotoMeta.Fields(47.5, 9.75));
        Assert.Equal(WithoutApp1(src), WithoutApp1(dst));
        Assert.Equal((47.5, 9.75), (Math.Round(Gps(dst)!.Value.Lat, 6), Math.Round(Gps(dst)!.Value.Lon, 6)));
        // JFIF (APP0) bleibt der erste Abschnitt
        if (src[3] == 0xE0) Assert.Equal(0xE0, dst[3]);
        using var decoded = SKBitmap.Decode(dst);
        Assert.Equal(64, decoded.Width);
    }

    [Fact]
    public void Vorhandenes_GPS_IFD_in_Motorola_Reihenfolge_behaelt_die_Hoehe()
    {
        // TIFF „MM“: IFD0 (Drehangabe, Zeiger aufs GPS-IFD bei 38), GPS-IFD nur mit Höhe (Bezug 1, 300/1 bei 68)
        var t = new List<byte>();
        void U16(int v) { t.Add((byte)(v >> 8)); t.Add((byte)v); }
        void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        t.AddRange("MM"u8.ToArray()); U16(42); U32(8);
        U16(2);
        U16(0x0112); U16(3); U32(1); U16(6); U16(0);
        U16(0x8825); U16(4); U32(1); U32(38);
        U32(0);
        U16(2);
        U16(0x0005); U16(1); U32(1); t.AddRange(new byte[] { 1, 0, 0, 0 });
        U16(0x0006); U16(5); U32(1); U32(68);
        U32(0);
        U32(300); U32(1);
        var plain = PhotoEditTests.Jpeg(1);
        var len = 2 + 6 + t.Count;
        byte[] src = [0xFF, 0xD8, 0xFF, 0xE1, (byte)(len >> 8), (byte)len, .. "Exif\0\0"u8.ToArray(), .. t, .. WithoutApp1(plain)[2..]];
        var dst = PhotoMeta.WriteJpeg(src, new PhotoMeta.Fields(52.52, 13.405));
        Assert.Equal(WithoutApp1(src), WithoutApp1(dst));
        Assert.Equal((52.52, 13.405), (Math.Round(Gps(dst)!.Value.Lat, 6), Math.Round(Gps(dst)!.Value.Lon, 6)));
        var gps = ImageMetadataReader.ReadMetadata(new MemoryStream(dst)).OfType<GpsDirectory>().Single();
        Assert.Equal(1, gps.GetInt32(GpsDirectory.TagAltitudeRef));
        Assert.Equal(300.0, gps.GetRational(GpsDirectory.TagAltitude).ToDouble());
        Assert.Equal("6", Tag<ExifIfd0Directory>(dst, ExifDirectoryBase.TagOrientation));
    }

    [Fact]
    public void Bewertung_und_Titel_ins_eingebettete_Xmp()
    {
        var src = PhotoEditTests.Jpeg(1);
        var dst = PhotoMeta.WriteJpeg(src, new PhotoMeta.Fields(Rating: 4, Title: "Herbstwald", Description: "Am Waldrand"));
        Assert.Equal(WithoutApp1(src), WithoutApp1(dst));
        var row = FileMeta.Read("a.jpg", new MemoryStream(dst), TimeZoneInfo.Utc)!;
        Assert.Equal(4, row.Rating);
        Assert.Equal("Herbstwald", row.Title);
        // Zweites Schreiben ändert nur den Titel, die Bewertung bleibt
        var again = PhotoMeta.WriteJpeg(dst, new PhotoMeta.Fields(Title: "Tonau"));
        var row2 = FileMeta.Read("a.jpg", new MemoryStream(again), TimeZoneInfo.Utc)!;
        Assert.Equal(4, row2.Rating);
        Assert.Equal("Tonau", row2.Title);
    }

    [Fact]
    public void Markierung_und_Farbe_wie_Lightroom()
    {
        // JPEG: eingebettet, gelesen wie aus Lightroom (xmpDM:pick, xmp:Label deutsch, photoshop:LabelColor)
        var dst = PhotoMeta.WriteJpeg(PhotoEditTests.Jpeg(1), new PhotoMeta.Fields(Pick: -1, Label: "green"));
        var row = FileMeta.Read("a.jpg", new MemoryStream(dst), TimeZoneInfo.Utc)!;
        Assert.Equal((-1, "green"), (row.Pick, row.Label));
        // Sidecar: geschrieben und gelesen; 0 und leer entfernen die Angaben
        var x = PhotoMeta.WriteXmp(Lightroom, new PhotoMeta.Fields(Pick: 1, Label: "red"), packet: false);
        Assert.Contains("Rot", x);
        Assert.Contains("red", x);
        var d = DmOffice.Gallery.Sidecar.Read(Encoding.UTF8.GetBytes(x))!;
        Assert.Equal((1, "red", 2), (d.Pick, d.Label, d.Rating));
        var cleared = DmOffice.Gallery.Sidecar.Read(Encoding.UTF8.GetBytes(PhotoMeta.WriteXmp(x, new PhotoMeta.Fields(Pick: 0, Label: ""))))!;
        Assert.Equal(((int?)null, (string?)null), (cleared.Pick, cleared.Label));
    }

    private const string Lightroom = """
        <?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 7.0">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
            xmlns:darktable="http://darktable.sf.net/"
            xmlns:dc="http://purl.org/dc/elements/1.1/"
           crs:Exposure2012="+0.35" darktable:xmp_version="5">
           <darktable:history><rdf:Seq><rdf:li darktable:operation="exposure" darktable:enabled="1"/></rdf:Seq></darktable:history>
           <dc:subject><rdf:Bag><rdf:li>Wald</rdf:li><rdf:li>Herbst</rdf:li></rdf:Bag></dc:subject>
           <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Alt</rdf:li><rdf:li xml:lang="de">Alt deutsch</rdf:li></rdf:Alt></dc:title>
          </rdf:Description>
          <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/">
           <xmp:Rating>2</xmp:Rating>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

    [Fact]
    public void Xmp_zusammenfuehren_erhaelt_fremde_Angaben()
    {
        var text = PhotoMeta.WriteXmp(Lightroom, new PhotoMeta.Fields(48.1, -11.5, Rating: 4, Title: "Neu"), packet: false);
        // Fremdes bleibt samt Präfix
        Assert.Contains("crs:Exposure2012=\"+0.35\"", text);
        Assert.Contains("darktable:operation=\"exposure\"", text);
        Assert.Contains("<rdf:li>Wald</rdf:li>", text);
        Assert.Contains("Alt deutsch", text);
        Assert.Contains("<?xpacket end=\"w\"?>", text);
        // Die Bewertung steht genau einmal da, die alte (Element im zweiten Block) ist weg
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Rating"));
        Assert.DoesNotContain(">2<", text);
        var d = DmOffice.Gallery.Sidecar.Read(Encoding.UTF8.GetBytes(text))!;
        Assert.Equal(4, d.Rating);
        Assert.Equal("Neu", d.Title);
        Assert.Equal(new[] { "Wald", "Herbst" }, d.Keywords);
        Assert.Equal(48.1, d.Lat!.Value, 6);
        Assert.Equal(-11.5, d.Lon!.Value, 6);
        Assert.Contains("exif:GPSLatitude=\"48,6.000000N\"", text);
        Assert.Contains("exif:GPSLongitude=\"11,30.000000W\"", text);
        // Leerer Titel nimmt ihn heraus
        Assert.Null(DmOffice.Gallery.Sidecar.Read(Encoding.UTF8.GetBytes(PhotoMeta.WriteXmp(text, new PhotoMeta.Fields(Title: ""))))!.Title);
    }

    [Fact]
    public void Neues_Sidecar_und_unlesbares_bleibt_unangetastet()
    {
        var fresh = PhotoMeta.WriteXmp(null, new PhotoMeta.Fields(47.25, 9.5));
        Assert.StartsWith("<?xpacket begin=", fresh);
        Assert.Equal(47.25, DmOffice.Gallery.Sidecar.Read(Encoding.UTF8.GetBytes(fresh))!.Lat!.Value, 6);
        Assert.Throws<InvalidDataException>(() => PhotoMeta.WriteXmp("<x:xmpmeta kaputt", new PhotoMeta.Fields(1, 1)));
        Assert.Equal("IMG_1.NEF.xmp", PhotoMeta.SidecarName("IMG_1.NEF", ["IMG_1.NEF", "img_1.xmp", "IMG_1.NEF.xmp"]));
        Assert.Equal("img_1.xmp", PhotoMeta.SidecarName("IMG_1.NEF", ["IMG_1.NEF", "img_1.xmp"]));
        Assert.Equal("IMG_1.xmp", PhotoMeta.SidecarName("IMG_1.NEF", ["IMG_1.NEF"]));
        Assert.Equal(-33.8688, PhotoMeta.ParseAdobeGps(PhotoMeta.AdobeGps(-33.8688, true))!.Value, 6);
        Assert.Equal(151.2093, PhotoMeta.ParseAdobeGps("151,12,33.48E")!.Value, 4);
    }
}
