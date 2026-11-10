using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Dav;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Fotos drehen und zuschneiden: Metadaten bleiben, nur Drehen ist verlustfrei, Kopie oder Überschreiben (mit Version).</summary>
[Collection("server")]
public class PhotoEditTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PhotoEditTests(ChatFixture fx) => _fx = fx;

    /// <summary>JPEG 400×300 (links rot, rechts blau) mit EXIF: Drehangabe, Aufnahmezeit und Bildmaße im EXIF-IFD.</summary>
    public static byte[] Jpeg(int orientation)
    {
        using var bmp = new SKBitmap(400, 300);
        using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.Blue); c.DrawRect(0, 0, 200, 300, new SKPaint { Color = SKColors.Red }); }
        var plain = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        // TIFF (II): IFD0 mit Orientation und Zeiger aufs EXIF-IFD; EXIF-IFD mit DateTimeOriginal (ASCII 20) und PixelX/YDimension
        var t = new List<byte>();
        void U16(int v) { t.Add((byte)v); t.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }
        t.AddRange("II"u8.ToArray()); U16(42); U32(8);
        U16(2);                                                // IFD0: 2 Einträge (ab 8)
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        U16(0x8769); U16(4); U32(1); U32(38);                  // EXIF-IFD bei 38 (8 + 2 + 24 + 4)
        U32(0);                                                // kein IFD1
        U16(3);                                                // EXIF-IFD: 3 Einträge (ab 38)
        U16(0x9003); U16(2); U32(20); U32(80);                 // Text bei 80 (38 + 2 + 36 + 4)
        U16(0xA002); U16(4); U32(1); U32(400);
        U16(0xA003); U16(4); U32(1); U32(300);
        U32(0);
        t.AddRange("2026:10:27 13:48:20\0"u8.ToArray());
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var len = 2 + 6 + t.Count;
        app1.Add((byte)(len >> 8)); app1.Add((byte)len);
        app1.AddRange("Exif\0\0"u8.ToArray()); app1.AddRange(t);
        return [0xFF, 0xD8, .. app1, .. plain[2..]];
    }

    private static string? Tag(byte[] jpeg, string dir, int tag) =>
        MetadataExtractor.ImageMetadataReader.ReadMetadata(new MemoryStream(jpeg)).FirstOrDefault(d => d.Name == dir)?.GetDescription(tag);

    [Fact]
    public void Nur_drehen_verlustfrei_ueber_die_Drehangabe()
    {
        var src = Jpeg(6);
        var turned = PhotoEdit.Apply(src, 90, null);
        Assert.Equal(3, PhotoEdit.Orientation(PhotoEdit.Segments(turned)));
        // Bilddaten unverändert (ab SOS gleich)
        int Sos(byte[] b) { for (var i = 2; i < b.Length - 1; i++) if (b[i] == 0xFF && b[i + 1] == 0xDA) return i; return -1; }
        Assert.Equal(src[Sos(src)..], turned[Sos(turned)..]);
        Assert.Equal(1, PhotoEdit.Compose(8, 90));
    }

    [Fact]
    public void Zuschneiden_behaelt_Metadaten_und_setzt_Masse()
    {
        // Gespeichert quer 400×300 mit „90° drehen“ (6) → gezeigt hochkant 300×400; Ausschnitt: obere Hälfte, linke Hälfte → 150×200
        var cut = PhotoEdit.Apply(Jpeg(6), 0, new PhotoEdit.Crop(0, 0, 0.5, 0.5));
        using var bmp = SKBitmap.Decode(cut);
        Assert.Equal((150, 200), (bmp.Width, bmp.Height));
        Assert.Equal(1, PhotoEdit.Orientation(PhotoEdit.Segments(cut)));
        Assert.Equal("2026:10:27 13:48:20", Tag(cut, "Exif SubIFD", 0x9003));
        Assert.Equal("150 pixels", Tag(cut, "Exif SubIFD", 0xA002));
        Assert.Equal("200 pixels", Tag(cut, "Exif SubIFD", 0xA003));
        // Oben links im hochkant gezeigten Bild ist die linke (rote) Hälfte des Originals, nach rechts gedreht oben
        var px = bmp.GetPixel(75, 100);
        Assert.True(px.Red > 200 && px.Blue < 60, px.ToString());
        Assert.Throws<ArgumentException>(() => PhotoEdit.Apply("kein jpeg"u8.ToArray(), 0, null));
    }

    /// <summary>
    /// Ergebnis der Photo-Engine (ohne Metadaten): Aufnahmezeit usw. kommen aus dem Original - sonst sortiert, wer den Ordner neu
    /// einliest, das Foto nach dem Speicherdatum.
    /// </summary>
    [Fact]
    public void Metadaten_aus_dem_Original_fuer_das_Ergebnis_der_Engine()
    {
        using var bmp = new SKBitmap(400, 300);
        using (var cv = new SKCanvas(bmp)) cv.Clear(SKColors.Green);
        var developed = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        Assert.Null(Tag(developed, "Exif SubIFD", 0x9003));
        // Nur übernehmen, nicht drehen: Metadaten dazu, Drehangabe 1 (die Engine liefert aufrecht)
        var kept = PhotoEdit.Apply(developed, 0, null, meta: Jpeg(6));
        Assert.Equal("2026:10:27 13:48:20", Tag(kept, "Exif SubIFD", 0x9003));
        Assert.Equal(1, PhotoEdit.Orientation(PhotoEdit.Segments(kept)));
        // Mit Ausschnitt ebenso
        var cut = PhotoEdit.Apply(developed, 0, new PhotoEdit.Crop(0, 0, 0.5, 0.5), meta: Jpeg(6));
        Assert.Equal("2026:10:27 13:48:20", Tag(cut, "Exif SubIFD", 0x9003));
        Assert.Equal("200 pixels", Tag(cut, "Exif SubIFD", 0xA002));
    }

    [Fact]
    public async Task Speichern_als_Kopie_oder_ueberschreiben_mit_Version()
    {
        var space = _fx.Space("ben");
        var c = _fx.As("ben");
        var name = "foto-" + Guid.NewGuid().ToString("N")[..6];
        var path = name + ".jpg";
        FileStore.Put(space.Db, path, Jpeg(1), "image/jpeg");
        var copy = await c.PostAsJsonAsync("/api/files/photo-edit", new { path, rotate = 90, crop = (object?)null, overwrite = false });
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        var copyPath = (await copy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        Assert.EndsWith(name + "_bearbeitet.jpg", copyPath);
        Assert.Equal(6, PhotoEdit.Orientation(PhotoEdit.Segments(FileStore.Data(space.Db, copyPath.TrimStart('/'))!)));
        // Noch eine Kopie: neuer Name
        var copy2 = await c.PostAsJsonAsync("/api/files/photo-edit", new { path, rotate = 180, crop = (object?)null, overwrite = false });
        Assert.EndsWith(name + "_bearbeitet 2.jpg", (await copy2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString());
        // Überschreiben: das Original bleibt als Version
        var over = await c.PostAsJsonAsync("/api/files/photo-edit", new { path, rotate = 0, crop = new { x = 0.25, y = 0.25, w = 0.5, h = 0.5 }, overwrite = true });
        Assert.Equal(HttpStatusCode.OK, over.StatusCode);
        using var bmp = SKBitmap.Decode(FileStore.Data(space.Db, path)!);
        Assert.Equal((200, 150), (bmp.Width, bmp.Height));
        Assert.Single(FileStore.Versions(space.Db, FileStore.Get(space.Db, path)!.FileId));
    }

    [Fact]
    public void Regler_Tonkurve_und_Farben()
    {
        var none = new PhotoAdjust.Values();
        Assert.True(none.IsNeutral);
        foreach (var x in new[] { 0.0, 0.3, 0.7, 1.0 }) Assert.Equal(x, PhotoAdjust.Curve(x, none), 6);
        // Tiefen hellen unten auf, Lichter dämpfen oben; Schwarz und Weiß bleiben
        var v = new PhotoAdjust.Values(Shadows: 40, Highlights: -40);
        Assert.True(PhotoAdjust.Curve(0.25, v) > 0.25);
        Assert.True(PhotoAdjust.Curve(0.8, v) < 0.8);
        Assert.Equal(0, PhotoAdjust.Curve(0, v), 6);
        Assert.Equal(1, PhotoAdjust.Curve(1, v), 6);
        Assert.True(PhotoAdjust.Curve(0.5, new PhotoAdjust.Values(Exposure: 0.5)) > 0.5);
        Assert.Equal(2, new PhotoAdjust.Values(Exposure: 5, Shadows: 200).Clamped().Exposure);
        // Belichtung −2 wie Lightroom Classic (gemessen an einem JPG): je Kanal 60→22,5, 120→48, 240→137 von 255
        foreach (var (input, lr) in new[] { (60, 22.5), (120, 48.0), (240, 137.0) })
            Assert.InRange(PhotoAdjust.Exposure(input / 255.0, -2) * 255, lr - 4, lr + 4);
        Assert.Equal(0.4, PhotoAdjust.Exposure(0.4, 0), 4);
        // Wärmer: mehr Rot als Blau bei Grau
        using var gray = new SKBitmap(4, 4);
        using (var c = new SKCanvas(gray)) c.Clear(new SKColor(128, 128, 128));
        using var warm = PhotoAdjust.Apply(gray, new PhotoAdjust.Values(Temperature: 40));
        var px = warm.GetPixel(1, 1);
        Assert.True(px.Red > px.Blue + 10, px.ToString());
        var st = PhotoAdjust.Measure(gray);
        Assert.Equal(128, st.Median);
    }

    [Fact]
    public async Task Vorschau_und_Speichern_mit_Reglern()
    {
        var space = _fx.Space("ben");
        var c = _fx.As("ben");
        var path = "regler-" + Guid.NewGuid().ToString("N")[..6] + ".jpg";
        FileStore.Put(space.Db, path, Jpeg(6), "image/jpeg");
        var prev = await c.PostAsJsonAsync("/api/files/photo-adjust/preview", new { path, adjust = new { exposure = 0.5 } });
        Assert.Equal("image/jpeg", prev.Content.Headers.ContentType?.MediaType);
        using (var b = SKBitmap.Decode(await prev.Content.ReadAsByteArrayAsync())) Assert.Equal((300, 400), (b.Width, b.Height));   // aufrecht
        // Ohne freigeschaltete KI: kein Vorschlag
        Assert.False((await (await c.GetAsync("/api/files/photo-adjust/ai")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/photo-adjust/suggest", new { path })).StatusCode);
        // Ohne Photo-Engine (PHOTO_ENGINE_URL leer): kein Engine-Vorschlag, Vorschau und Speichern rechnet dmOffice selbst
        Assert.False((await (await c.GetAsync("/api/files/photo-adjust/ai")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("engine").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/files/photo-adjust/engine", new { path })).StatusCode);
        // Nur Regler, ohne Zuschnitt: neu kodiert, Metadaten bleiben, aufrecht gespeichert
        var save = await c.PostAsJsonAsync("/api/files/photo-edit", new { path, rotate = 0, crop = (object?)null, overwrite = true, adjust = new { shadows = 30 } });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var data = FileStore.Data(space.Db, path)!;
        Assert.Equal(1, PhotoEdit.Orientation(PhotoEdit.Segments(data)));
        Assert.Equal("2026:10:27 13:48:20", Tag(data, "Exif SubIFD", 0x9003));

        // Mehrere automatisch verbessern: ohne Engine bzw. ohne KI abgelehnt, unbekannte Art ein Fehler
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.PostAsJsonAsync("/api/files/photo-auto", new { paths = new[] { path }, source = "engine", overwrite = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/photo-auto", new { paths = new[] { path }, source = "ai", overwrite = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/photo-auto", new { paths = new[] { path }, source = "x", overwrite = false })).StatusCode);
    }

    [Fact]
    public async Task Nicht_destruktiv_Einstellungen_statt_neuer_Datei()
    {
        var space = _fx.Space("ben");
        var c = _fx.As("ben");
        var path = "nd-" + Guid.NewGuid().ToString("N")[..6] + ".jpg";
        FileStore.Put(space.Db, path, Jpeg(1), "image/jpeg");
        var original = FileStore.Data(space.Db, path)!;
        // Einstellungen setzen: 90° gedreht, heller - die Datei bleibt, wie sie ist
        var put = await c.PutAsJsonAsync("/api/files/photo-edits", new { path, rotate = 90, crop = (object?)null, adjust = new { exposure = 1.0 }, origin = "hand" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var fp = (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fingerprint").GetString();
        Assert.False(string.IsNullOrEmpty(fp));
        Assert.Equal(original, FileStore.Data(space.Db, path));
        Assert.Equal(90, (await c.GetFromJsonAsync<JsonElement>("/api/files/photo-edits?path=" + path)).GetProperty("edit").GetProperty("rotate").GetInt32());
        // Liste: Etag mit Fingerabdruck; Vorschaubild bearbeitet (hochkant), mit orig=1 das Original (quer)
        var listed = (await c.GetFromJsonAsync<JsonElement>("/api/files/?path=")).GetProperty("items").EnumerateArray().First(i => i.GetProperty("path").GetString() == path);
        Assert.EndsWith("~e" + fp, listed.GetProperty("etag").GetString());
        using (var edited = SKBitmap.Decode(await c.GetByteArrayAsync("/api/files/thumb?path=" + path + "&size=256"))) Assert.True(edited.Height > edited.Width);
        using (var orig = SKBitmap.Decode(await c.GetByteArrayAsync("/api/files/thumb?path=" + path + "&size=256&orig=1"))) Assert.True(orig.Width > orig.Height);
        // Zurück zum Original
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync("/api/files/photo-edits?path=" + path)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await c.GetFromJsonAsync<JsonElement>("/api/files/photo-edits?path=" + path)).GetProperty("edit").ValueKind);
        using (var back = SKBitmap.Decode(await c.GetByteArrayAsync("/api/files/thumb?path=" + path + "&size=256"))) Assert.True(back.Width > back.Height);
    }
}
