using System.Globalization;
using System.Text;
using DmOffice.Dav;
using DmOffice.Gallery;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jfif;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Foto-Export (photoexport): Größe aus cm und dpi ohne Vergrößern, Dateinamen nach Muster ohne Überschreiben, dpi in der Datei, Ortsangaben weg, Fehler je Foto.</summary>
[Collection("server")]
public class PhotoExportTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PhotoExportTests(ChatFixture fx) => _fx = fx;

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    [Fact]
    public void Groesse_aus_cm_und_dpi_eingepasst_nie_vergroessert()
    {
        Assert.Equal(1181, PhotoExport.Pixels(10, 300));
        Assert.Equal(4961, PhotoExport.Pixels(42, 300));
        var a3 = new PhotoExport.Options { Size = PhotoExport.SizePrint, WidthCm = 42, HeightCm = 29.7, Dpi = 300 };
        // Quer in quer: Breite begrenzt
        Assert.Equal((4961, 3307, false), PhotoExport.Target(6000, 4000, a3));
        // Hochkant: das Maß dreht mit
        Assert.Equal((3307, 4961, false), PhotoExport.Target(4000, 6000, a3));
        // Ohne Mitdrehen: hochkant in quer eingepasst, nichts abgeschnitten
        Assert.Equal((2339, 3508, false), PhotoExport.Target(4000, 6000, a3 with { AutoOrient = false }));
        // Zu wenig Pixel: bleibt, wie es ist, und wird gemeldet
        Assert.Equal((2000, 1500, true), PhotoExport.Target(2000, 1500, a3));
        // Lange Kante
        var edge = new PhotoExport.Options { Size = PhotoExport.SizeLong, LongEdge = 2048 };
        Assert.Equal((2048, 1365, false), PhotoExport.Target(6000, 4000, edge));
        Assert.Equal((1365, 2048, false), PhotoExport.Target(4000, 6000, edge));
        Assert.Equal((6000, 4000, true), PhotoExport.Target(6000, 4000, edge with { LongEdge = 8000 }));
        Assert.Equal((6000, 4000, false), PhotoExport.Target(6000, 4000, new PhotoExport.Options()));
    }

    [Fact]
    public void Dateiname_nach_Muster_und_freier_Name_bei_Belegung()
    {
        var o = new PhotoExport.Options();
        var now = new DateTime(2026, 11, 21, 9, 0, 0);
        Assert.Equal("20260614_001.jpg", PhotoExport.NameFor(o.Pattern, ImportProfiles.Example, 1, now, De, o));
        Assert.Equal("DSCF0042_X100VI_012.png", PhotoExport.NameFor("{original_name}_{camera_model}_{sequence}", ImportProfiles.Example, 12, now, De, o with { Format = PhotoExport.Png }));
        Assert.Equal("Export", PhotoExport.CleanFolder(" / "));
        Assert.Equal("Druck/A3", PhotoExport.CleanFolder("/Druck/../A3/"));

        var db = _fx.Space("ben").Db;
        FileStore.EnsureDir(db, "ExportName");
        Assert.Equal("ExportName/a.jpg", PhotoExport.FreePath(db, "ExportName", "a.jpg"));
        FileStore.Put(db, "ExportName/a.jpg", [1, 2, 3], "image/jpeg");
        Assert.Equal("ExportName/a (2).jpg", PhotoExport.FreePath(db, "ExportName", "a.jpg"));
        FileStore.Put(db, "ExportName/a (2).jpg", [1, 2, 3], "image/jpeg");
        Assert.Equal("ExportName/a (3).jpg", PhotoExport.FreePath(db, "ExportName", "a.jpg"));
    }

    private static IReadOnlyList<MetadataExtractor.Directory> Meta(byte[] data) => ImageMetadataReader.ReadMetadata(new MemoryStream(data));

    [Fact]
    public async Task Dpi_im_JPG_und_PNG_Bearbeitung_angewendet()
    {
        var o = new PhotoExport.Options { Size = PhotoExport.SizeLong, LongEdge = 200, Dpi = 254 };
        // Gespeichert mit 90° drehen: hochkant 300×400 → lange Kante 200 → 150×200
        var edit = new PhotoEdits.Edit(90, null, null, "web", 0);
        var r = await PhotoExport.RenderAsync(PhotoEditTests.Jpeg(1), "a.jpg", edit, o, null, CancellationToken.None);
        using (var bmp = SKBitmap.Decode(r.Data)) Assert.Equal((150, 200), (bmp.Width, bmp.Height));
        var jfif = Meta(r.Data).OfType<JfifDirectory>().Single();
        Assert.Equal(254, jfif.GetInt32(JfifDirectory.TagResX));
        Assert.Equal(254, jfif.GetInt32(JfifDirectory.TagResY));
        Assert.Equal(1, jfif.GetInt32(JfifDirectory.TagUnits));
        // Aufnahmedaten bleiben, Drehangabe 1, neue Maße
        var sub = Meta(r.Data).OfType<ExifSubIfdDirectory>().Single();
        Assert.Equal("2026:10:27 13:48:20", sub.GetDescription(ExifDirectoryBase.TagDateTimeOriginal));
        Assert.Equal(150, sub.GetInt32(ExifDirectoryBase.TagExifImageWidth));
        Assert.Equal(1, Meta(r.Data).OfType<ExifIfd0Directory>().Single().GetInt32(ExifDirectoryBase.TagOrientation));

        // „Original“: Bearbeitung bleibt außen vor
        var plain = await PhotoExport.RenderAsync(PhotoEditTests.Jpeg(1), "a.jpg", edit, o with { Edited = false }, null, CancellationToken.None);
        Assert.Equal((200, 150), (plain.Width, plain.Height));

        var png = await PhotoExport.RenderAsync(PhotoEditTests.Jpeg(1), "a.jpg", null, o with { Format = PhotoExport.Png, Dpi = 300 }, null, CancellationToken.None);
        Assert.True(png.Data.AsSpan(1, 3).SequenceEqual("PNG"u8));
        using (var bmp = SKBitmap.Decode(png.Data)) Assert.Equal((200, 150), (bmp.Width, bmp.Height));
        var phys = Meta(png.Data).First(d => d.Name == "PNG-pHYs");
        Assert.Equal(11811, phys.GetInt32(MetadataExtractor.Formats.Png.PngDirectory.TagPixelsPerUnitX));
        Assert.Contains(Meta(png.Data), d => d is ExifSubIfdDirectory s && s.GetDescription(ExifDirectoryBase.TagDateTimeOriginal) == "2026:10:27 13:48:20");
    }

    /// <summary>JPEG 400×300 mit EXIF (Aufnahmezeit, Bildmaße, Auflösung 72 dpi) und GPS-IFD (Breite 48° 8' 0" N) sowie XMP mit exif:GPSLatitude.</summary>
    public static byte[] JpegWithGps()
    {
        using var bmp = new SKBitmap(400, 300);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Orange);
        var plain = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        var t = new List<byte>();
        void U16(int v) { t.Add((byte)v); t.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }
        t.AddRange("II"u8.ToArray()); U16(42); U32(8);
        // IFD0 ab 8: 5 Einträge → 8 + 2 + 60 + 4 = 74
        U16(5);
        U16(0x0112); U16(3); U32(1); U16(1); U16(0);
        U16(0x011A); U16(5); U32(1); U32(74);                 // XResolution bei 74
        U16(0x011B); U16(5); U32(1); U32(82);                 // YResolution bei 82
        U16(0x8769); U16(4); U32(1); U32(90);                 // EXIF-IFD bei 90
        U16(0x8825); U16(4); U32(1); U32(0);                  // GPS-IFD: Zeiger unten
        U32(0);
        U32(72); U32(1); U32(72); U32(1);                     // 74..90
        // EXIF-IFD ab 90: 3 Einträge → 90 + 2 + 36 + 4 = 132, Text 20 Bytes bis 152
        U16(3);
        U16(0x9003); U16(2); U32(20); U32(132);               // Text bei 132
        U16(0xA002); U16(4); U32(1); U32(400);
        U16(0xA003); U16(4); U32(1); U32(300);
        U32(0);
        t.AddRange("2026:10:27 13:48:20\0"u8.ToArray());      // 132..152
        // GPS-IFD ab 152: Breitenangabe „N“ im Eintrag, Breite (3 Brüche) dahinter
        var gpsAt = t.Count;
        U16(2);
        U16(0x0001); U16(2); U32(2); t.AddRange("N\0\0\0"u8.ToArray());
        U16(0x0002); U16(5); U32(3); U32(gpsAt + 2 + 24 + 4);
        U32(0);
        U32(48); U32(1); U32(8); U32(1); U32(0); U32(1);
        var tiff = t.ToArray();
        // Zeiger auf das GPS-IFD (Wert des fünften Eintrags von IFD0) richtigstellen
        var ptr = 8 + 2 + 4 * 12 + 8;
        BitConverter.GetBytes(gpsAt).CopyTo(tiff, ptr);
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var len = 2 + 6 + tiff.Length;
        app1.Add((byte)(len >> 8)); app1.Add((byte)len);
        app1.AddRange("Exif\0\0"u8.ToArray()); app1.AddRange(tiff);
        var xmpText = "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" exif:GPSLatitude=\"48,8.0N\" exif:DateTimeOriginal=\"2026-10-27T13:48:20\">"
            + "<exif:GPSLongitude>11,34.0E</exif:GPSLongitude></rdf:Description></rdf:RDF></x:xmpmeta>";
        var xmp = Encoding.Latin1.GetBytes(xmpText);
        var app1x = new List<byte> { 0xFF, 0xE1, (byte)((xmp.Length + 2) >> 8), (byte)(xmp.Length + 2) };
        app1x.AddRange(xmp);
        return [0xFF, 0xD8, .. app1, .. app1x, .. plain[2..]];
    }

    [Fact]
    public async Task Ortsangaben_auf_Wunsch_entfernt_Aufnahmedaten_bleiben()
    {
        var src = JpegWithGps();
        Assert.Contains(Meta(src), d => d is GpsDirectory);
        var o = new PhotoExport.Options { Dpi = 300 };
        var keep = (await PhotoExport.RenderAsync(src, "g.jpg", null, o with { RemoveGps = false }, null, CancellationToken.None)).Data;
        Assert.Contains(Meta(keep), d => d is GpsDirectory g && g.GetDescription(GpsDirectory.TagLatitudeRef) == "N");
        Assert.Contains("GPSLatitude", Encoding.Latin1.GetString(keep));

        var gone = (await PhotoExport.RenderAsync(src, "g.jpg", null, o, null, CancellationToken.None)).Data;
        Assert.DoesNotContain(Meta(gone), d => d is GpsDirectory);
        Assert.DoesNotContain("GPSLatitude", Encoding.Latin1.GetString(gone));
        Assert.DoesNotContain("GPSLongitude", Encoding.Latin1.GetString(gone));
        Assert.Contains("exif:DateTimeOriginal", Encoding.Latin1.GetString(gone));
        Assert.Equal("2026:10:27 13:48:20", Meta(gone).OfType<ExifSubIfdDirectory>().Single().GetDescription(ExifDirectoryBase.TagDateTimeOriginal));
        // Auflösung im EXIF auf die dpi gesetzt
        var ifd0 = Meta(gone).OfType<ExifIfd0Directory>().Single();
        Assert.Equal(300, ifd0.GetInt32(ExifDirectoryBase.TagXResolution));
        using var bmp = SKBitmap.Decode(gone);
        Assert.Equal((400, 300), (bmp.Width, bmp.Height));
    }

    [Fact]
    public async Task Fehler_an_einem_Foto_bricht_den_Export_nicht_ab()
    {
        var db = _fx.Space("ben").Db;
        var good = PhotoEditTests.Jpeg(1);
        var items = new List<PhotoExport.Item>
        {
            new("eins.jpg", 0, null, _ => Task.FromResult(good)),
            new("kaputt.jpg", 0, null, _ => Task.FromResult("kein Bild"u8.ToArray())),
            new("drei.jpg", 0, null, _ => Task.FromResult(good)),
        };
        var o = new PhotoExport.Options { Pattern = "{exif_date}_{sequence}", Folder = "ExportLauf", Size = PhotoExport.SizeLong, LongEdge = 100 };
        var r = await PhotoExport.RunAsync(db, "ben-test", items, o, "ExportLauf", TimeZoneInfo.Utc, De, null, null, CancellationToken.None);
        Assert.Equal(2, r.Written);
        Assert.Equal(1, r.Failed);
        Assert.Single(r.Errors);
        Assert.StartsWith("kaputt.jpg: ", r.Errors[0]);
        // Laufende Nummer = Platz in der Auswahl
        Assert.Equal(["ExportLauf/20261027_001.jpg", "ExportLauf/20261027_003.jpg"], r.Paths);
        using (var bmp = SKBitmap.Decode(FileStore.Data(db, "ExportLauf/20261027_001.jpg")!)) Assert.Equal((100, 75), (bmp.Width, bmp.Height));
        Assert.Equal("2 von 3 exportiert, 1 mit Fehler", PhotoExport.Summary(r, 3));

        // Noch einmal: nichts wird überschrieben
        var again = await PhotoExport.RunAsync(db, "ben-test", items[..1], o, "ExportLauf", TimeZoneInfo.Utc, De, null, null, CancellationToken.None);
        Assert.Equal(["ExportLauf/20261027_001 (2).jpg"], again.Paths);
    }

    [Fact]
    public async Task Unterordner_Titel_und_Beschreibung_wie_bei_den_Importprofilen()
    {
        var db = _fx.Space("ben").Db;
        var good = PhotoEditTests.Jpeg(1);
        var items = new List<PhotoExport.Item>
        {
            new("eins.jpg", 0, null, _ => Task.FromResult(good)),
            new("zwei.jpg", 0, null, _ => Task.FromResult(good)),
        };
        var o = new PhotoExport.Options
        {
            Folders = "{exif_year}/{exif_month}/{exif_day}", Pattern = "{exif_date}_{sequence}", Title = "Urlaub {exif_year}", Description = "Foto {sequence} vom {exif_date}",
            Size = PhotoExport.SizeLong, LongEdge = 100,
        };
        var r = await PhotoExport.RunAsync(db, "ben-test", items, o, "ExportUnter", TimeZoneInfo.Utc, De, null, null, CancellationToken.None);
        Assert.Equal(["ExportUnter/2026/10/27/20261027_001.jpg", "ExportUnter/2026/10/27/20261027_002.jpg"], r.Paths);
        var jpg = FileStore.Data(db, r.Paths[1])!;
        var xmp = Meta(jpg).OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().Single().GetXmpProperties();
        Assert.Equal("Urlaub 2026", xmp["dc:title[1]"]);
        Assert.Equal("Foto 002 vom 20261027", xmp["dc:description[1]"]);
        // Aufnahmedaten bleiben
        Assert.Equal("2026:10:27 13:48:20", Meta(jpg).OfType<ExifSubIfdDirectory>().Single().GetDescription(ExifDirectoryBase.TagDateTimeOriginal));

        // PNG: XMP im iTXt-Abschnitt
        var png = (await PhotoExport.RenderAsync(good, "p.jpg", null, o with { Format = PhotoExport.Png }, null, CancellationToken.None,
            new PhotoMeta.Fields(Title: "Titel", Description: "Beschreibung"))).Data;
        var px = Meta(png).OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().Single().GetXmpProperties();
        Assert.Equal("Titel", px["dc:title[1]"]);
        Assert.Equal("Beschreibung", px["dc:description[1]"]);
        using (var bmp = SKBitmap.Decode(png)) Assert.NotNull(bmp);

        // {title}/{description}: aus der Galerie, sonst aus der Datei
        var titled = (await PhotoExport.RenderAsync(good, "t.jpg", null, o with { Size = PhotoExport.SizeOriginal }, null, CancellationToken.None,
            new PhotoMeta.Fields(Title: "Aus der Datei", Description: "Text in der Datei"))).Data;
        var named = new List<PhotoExport.Item>
        {
            new("t1.jpg", 0, null, _ => Task.FromResult(titled)),
            new("t2.jpg", 0, null, _ => Task.FromResult(titled), "Aus der Galerie"),
        };
        var t = await PhotoExport.RunAsync(db, "ben-test", named, o with { Folders = "", Pattern = "{title}", Title = "{title}", Description = "{description}" },
            "ExportTitel", TimeZoneInfo.Utc, De, null, null, CancellationToken.None);
        Assert.Equal(["ExportTitel/Aus der Datei.jpg", "ExportTitel/Aus der Galerie.jpg"], t.Paths);
        var tx = Meta(FileStore.Data(db, t.Paths[1])!).OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().Single().GetXmpProperties();
        Assert.Equal("Aus der Galerie", tx["dc:title[1]"]);
        Assert.Equal("Text in der Datei", tx["dc:description[1]"]);

        // Schrägstrich im Dateinamen-Muster: dieselbe Meldung wie bei den Importprofilen statt stillem „_“
        Assert.Equal("Das Dateinamen-Muster darf keinen Schrägstrich enthalten.", (o with { Pattern = "{exif_date}\\{exif_year}" }).Patterns.Error());
        Assert.StartsWith("Unbekannter Platzhalter: {tag}", (o with { Folders = "{tag}" }).Patterns.Error());
    }

    [Fact]
    public void Alte_Vorgaben_ohne_Ordnerstruktur_Titel_und_Beschreibung_laden()
    {
        var db = _fx.Space("ben").Db;
        db.Set("gallery.export", """{"presets":[{"name":"A3","options":{"format":"png","pattern":"{original_name}","folder":"Druck"}}]}""");
        var p = PhotoExport.Of(db).Presets.Single();
        Assert.Equal(("png", "{original_name}", "Druck"), (p.Options.Format, p.Options.Pattern, p.Options.Folder));
        Assert.Equal(("", "", ""), (p.Options.Folders, p.Options.Title, p.Options.Description));
        db.Set("gallery.export", null);
    }
}
