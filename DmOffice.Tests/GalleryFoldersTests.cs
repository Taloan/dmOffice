using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Galerie-Ordner: Fotos (auch RAW) und Videos aus einem externen Ordner einlesen, zeigen, nachführen.</summary>
[Collection("server")]
public class GalleryFoldersTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    // Die Tests pruefen das sofortige Nachfuehren: ohne die Schonfrist, in der GalleryList den vorigen Stand weiter ausliefert
    public GalleryFoldersTests(ChatFixture fx) { _fx = fx; DmOffice.Gallery.GalleryList.Grace = TimeSpan.Zero; }

    private static byte[] Jpeg(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.SteelBlue);
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    /// <summary>Großes JPEG mit Rauschen (lässt sich kaum packen) - größer als der gelesene Kopf der Datei.</summary>
    private static byte[] BigJpeg()
    {
        using var bmp = new SKBitmap(1600, 1200);
        var rnd = new Random(7);
        for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) bmp.SetPixel(x, y, new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
    }

    /// <summary>
    /// Im Foto-Editor gespeichert (Server-Ordner, den auch andere eingebunden haben): wer den Ordner danach einliest, bekommt die
    /// Aufnahmezeit aus der Datei - nicht einen Eintrag nur mit Vorschaubild, der das Foto beim Speicherdatum einsortierte.
    /// </summary>
    [Fact]
    public async Task Im_Editor_gespeichertes_Foto_hat_auch_fuer_andere_die_Aufnahmezeit()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var cleo = _fx.Space("cleo");
        var dora = _fx.Space("dora");
        var root = Directory.CreateTempSubdirectory("dmeditor").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "beeren.jpg"), PhotoEditTests.Jpeg(1));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "gemein02", Name = "Gemeinsam", Path = root }]);
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").PostAsJsonAsync("/api/gallery/sources", new { mount = "gemein02", path = "" })).StatusCode);
            var a = GalleryFolders.Of(cleo.Db).Single(x => x.Mount == "gemein02");
            await GalleryFolders.ScanAsync(cleo, config, a, CancellationToken.None);
            await GalleryFolders.HeadsAsync(cleo, config, a, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);

            // Cleo speichert eine gedrehte Kopie
            var r = await _fx.As("cleo").PostAsJsonAsync("/api/files/photo-edit", new { path = "ext:" + a.Id + "/beeren.jpg", rotate = 90, crop = (object?)null, overwrite = false });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var copy = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
            await DmOffice.Dav.PhotoWork.Drain();

            // Dora liest den Ordner danach ein: die Kopie hat die Aufnahmezeit 27.10.2026
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("dora").PostAsJsonAsync("/api/gallery/sources", new { mount = "gemein02", path = "" })).StatusCode);
            var b = GalleryFolders.Of(dora.Db).Single(x => x.Mount == "gemein02");
            await GalleryFolders.ScanAsync(dora, config, b, CancellationToken.None);
            await GalleryFolders.HeadsAsync(dora, config, b, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            await GalleryFolders.FullAsync(dora, config, b, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            var name = copy[(copy.LastIndexOf('/') + 1)..];
            var meta = (await _fx.As("dora").GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray().ToList();
            var m = meta.Single(x => x.GetProperty("key").GetString() == "ext:" + b.Id + "/" + name);
            Assert.True(m.TryGetProperty("taken", out var taken) && taken.ValueKind == JsonValueKind.Number, m.ToString());
            Assert.Equal(new DateOnly(2026, 10, 27), DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(taken.GetInt64()).UtcDateTime));
            await _fx.As("cleo").DeleteAsync("/api/gallery/sources/" + a.Id);
            await _fx.As("dora").DeleteAsync("/api/gallery/sources/" + b.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Server_Ordner_Vorschauen_und_Angaben_gemeinsam_und_bleiben_beim_Entfernen()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var cleo = _fx.Space("cleo");
        var dora = _fx.Space("dora");
        var root = Directory.CreateTempSubdirectory("dmgemeinsam").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            var file = Path.Combine(root, "gross.jpg");
            var big = BigJpeg();
            File.WriteAllBytes(file, big);
            var when = File.GetLastWriteTimeUtc(file);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "gemein01", Name = "Gemeinsam", Path = root }]);

            // Cleo liest den Ordner ganz ein
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").PostAsJsonAsync("/api/gallery/sources", new { mount = "gemein01", path = "" })).StatusCode);
            var a = GalleryFolders.Of(cleo.Db).Single(x => x.Mount == "gemein01");
            await GalleryFolders.ScanAsync(cleo, config, a, CancellationToken.None);
            await GalleryFolders.HeadsAsync(cleo, config, a, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            await GalleryFolders.FullAsync(cleo, config, a, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);

            // Inhalt unlesbar machen, Größe und Datum gleich: was Dora jetzt bekommt, kann nur aus dem gemeinsamen Speicher sein
            File.WriteAllBytes(file, new byte[big.Length]);
            File.SetLastWriteTimeUtc(file, when);
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("dora").PostAsJsonAsync("/api/gallery/sources", new { mount = "gemein01", path = "" })).StatusCode);
            var b = GalleryFolders.Of(dora.Db).Single(x => x.Mount == "gemein01");
            await GalleryFolders.ScanAsync(dora, config, b, CancellationToken.None);
            await GalleryFolders.HeadsAsync(dora, config, b, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            Assert.Equal((1, 0, 0), GalleryFolders.Stages(dora.Db, b.Id));
            var meta = (await _fx.As("dora").GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray().ToList();
            Assert.Contains(meta, m => m.GetProperty("key").GetString() == "ext:" + b.Id + "/gross.jpg" && m.GetProperty("width").GetInt32() == 1600);
            var thumbUrl = "/api/gallery/external/thumb?id=" + Uri.EscapeDataString(b.Id + "/gross.jpg");
            Assert.Equal("image/webp", (await _fx.As("dora").GetAsync(thumbUrl)).Content.Headers.ContentType?.MediaType);

            // Cleo entfernt den Ordner: Doras Vorschau bleibt
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").DeleteAsync("/api/gallery/sources/" + a.Id)).StatusCode);
            Assert.Equal("image/webp", (await _fx.As("dora").GetAsync(thumbUrl)).Content.Headers.ContentType?.MediaType);
            await _fx.As("dora").DeleteAsync("/api/gallery/sources/" + b.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Gemeinsamer_Speicher_nach_Wiederherstellung_leer_Vorschauen_kommen_neu()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var eva = _fx.Space("cleo");
        var root = Directory.CreateTempSubdirectory("dmrepair").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.jpg"), Jpeg(400, 300));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "repair01", Name = "Repair", Path = root }]);
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").PostAsJsonAsync("/api/gallery/sources", new { mount = "repair01", path = "" })).StatusCode);
            var src = GalleryFolders.Of(eva.Db).Single(x => x.Mount == "repair01");
            await GalleryFolders.ScanAsync(eva, config, src, CancellationToken.None);
            await GalleryFolders.HeadsAsync(eva, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            await GalleryFolders.FullAsync(eva, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            Assert.Equal((1, 0, 0), GalleryFolders.Stages(eva.Db, src.Id));
            Assert.Equal(0, GalleryFolders.RepairShared(eva, config, src));

            // Cache verloren (nicht im Core-Backup): die Benutzer-DB verweist ins Leere, der Job sähe nichts Offenes
            GalleryFolders.Shared!.RemoveMount("repair01");
            var thumbUrl = "/api/gallery/external/thumb?id=" + Uri.EscapeDataString(src.Id + "/a.jpg");
            Assert.Equal(1, GalleryFolders.RepairShared(eva, config, src));
            Assert.Equal((1, 1, 0), GalleryFolders.Stages(eva.Db, src.Id));
            await GalleryFolders.HeadsAsync(eva, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            await GalleryFolders.FullAsync(eva, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            Assert.Equal((1, 0, 0), GalleryFolders.Stages(eva.Db, src.Id));
            Assert.Equal("image/webp", (await _fx.As("cleo").GetAsync(thumbUrl)).Content.Headers.ContentType?.MediaType);
            Assert.Equal(0, GalleryFolders.RepairShared(eva, config, src));
            await _fx.As("cleo").DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Großes JPEG wie von der Kamera: hinter dem Bild eine rote Vorschau, deren Lage ein MPF-Block (APP2) im Kopf angibt
    /// (Little Endian, Lage ab dem Anfang des MPF-Kopfs).
    /// </summary>
    private static byte[] CameraJpeg()
    {
        var main = BigJpeg();
        using var pbmp = new SKBitmap(400, 300);
        var prnd = new Random(3);
        for (int y = 0; y < pbmp.Height; y++) for (int x = 0; x < pbmp.Width; x++) pbmp.SetPixel(x, y, new SKColor((byte)prnd.Next(225, 256), (byte)prnd.Next(30), (byte)prnd.Next(30)));
        var preview = pbmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
        // TIFF-Kopf (8) + IFD mit einem Eintrag (2 + 12 + 4) + zwei MP-Einträge (32)
        var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0 };
        tiff.AddRange(BitConverter.GetBytes(8u));
        tiff.AddRange(BitConverter.GetBytes((ushort)1));
        tiff.AddRange(BitConverter.GetBytes((ushort)0xB002)); tiff.AddRange(BitConverter.GetBytes((ushort)7));
        tiff.AddRange(BitConverter.GetBytes(32u)); tiff.AddRange(BitConverter.GetBytes(26u));
        tiff.AddRange(BitConverter.GetBytes(0u));
        var app2Len = 2 + 4 + tiff.Count + 32;
        var tiffStart = 2 + 4 + 4;   // SOI, Marker und Länge, „MPF “
        var previewAt = main.Length + 2 + app2Len;
        tiff.AddRange(BitConverter.GetBytes(0x20030000u)); tiff.AddRange(BitConverter.GetBytes((uint)main.Length)); tiff.AddRange(BitConverter.GetBytes(0u)); tiff.AddRange(new byte[4]);
        tiff.AddRange(BitConverter.GetBytes(0x00010001u)); tiff.AddRange(BitConverter.GetBytes((uint)preview.Length)); tiff.AddRange(BitConverter.GetBytes((uint)(previewAt - tiffStart))); tiff.AddRange(new byte[4]);
        var file = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE2, (byte)(app2Len >> 8), (byte)app2Len, (byte)'M', (byte)'P', (byte)'F', 0 };
        file.AddRange(tiff);
        file.AddRange(main.Skip(2));
        file.AddRange(preview);
        return file.ToArray();
    }

    [Fact]
    public void Gain_Map_im_JPEG_ist_keine_Vorschau()
    {
        var jpg = CameraJpeg();
        var type = new byte[] { 0x01, 0x00, 0x01, 0x00 };
        var at = jpg.AsSpan().IndexOf(type);
        Assert.True(at > 0);
        type.AsSpan().CopyTo(jpg.AsSpan(at));
        jpg[at] = 0; jpg[at + 2] = 0;
        Assert.Null(DmOffice.Dav.Thumbs.MpfPreview(jpg[..(512 * 1024)]));
    }

    [Fact]
    public async Task Grosses_Kamera_JPEG_Vorschaubild_aus_der_eingebetteten_Vorschau_ohne_ganze_Datei()
    {
        var jpg = CameraJpeg();
        var mp = Assert.IsType<(long, int)>(DmOffice.Dav.Thumbs.MpfPreview(jpg[..(512 * 1024)]));
        Assert.Equal(0xFF, jpg[mp.Item1]);
        Assert.Equal(0xD8, jpg[mp.Item1 + 1]);
        Assert.Null(DmOffice.Dav.Thumbs.MpfPreview(BigJpeg()));

        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("cleo");
        var root = Directory.CreateTempSubdirectory("dmmpf").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "DSC0001.JPG"), jpg);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "mpf01", Name = "Kamera", Path = root }]);
            var c = _fx.As("cleo");
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "mpf01", path = "" })).StatusCode);
            var src = GalleryFolders.Of(space.Db).Single(s => s.Mount == "mpf01");
            await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
            // Schon nach dem Kopf fertig - Stufe 3 (ganze Datei) entfällt
            await GalleryFolders.HeadsAsync(space, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            Assert.Equal((1, 0, 0), GalleryFolders.Stages(space.Db, src.Id));
            var res = await c.GetAsync("/api/gallery/external/thumb?id=" + Uri.EscapeDataString(src.Id + "/DSC0001.JPG"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var thumb = SKBitmap.Decode(await res.Content.ReadAsByteArrayAsync());
            var px = thumb.GetPixel(thumb.Width / 2, thumb.Height / 2);
            Assert.True(px.Red > 200 && px.Green < 60 && px.Blue < 60, px.ToString());   // aus der roten Vorschau, nicht aus dem Bild
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Nicht_erreichbarer_Speicher_loescht_den_Bestand_nicht()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("cleo");
        var root = Directory.CreateTempSubdirectory("dmhalb").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            var jpg = Jpeg(40, 30);
            for (var n = 0; n < 30; n++) File.WriteAllBytes(Path.Combine(root, $"f{n:D2}.jpg"), jpg);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "halb01", Name = "Halb", Path = root }]);
            var c = _fx.As("cleo");
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "halb01", path = "" })).StatusCode);
            var src = GalleryFolders.Of(space.Db).Single(s => s.Mount == "halb01");
            Assert.Equal(30, await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None));

            // Ausgehängte Freigabe (Ordner leer): Fehler, die 30 Einträge bleiben - „antwortet nicht“ ist nicht „gelöscht“
            var keep = Path.Combine(Path.GetTempPath(), "dmhalb-weg-" + Guid.NewGuid().ToString("N"));
            Directory.Move(root, keep); Directory.CreateDirectory(root);
            await Assert.ThrowsAsync<IOException>(() => GalleryFolders.ScanAsync(space, config, src, CancellationToken.None));
            Assert.Equal(30, GalleryFolders.Stages(space.Db, src.Id).Total);
            Directory.Delete(root); Directory.Move(keep, root);

            // Wirklich gelöscht (auch viele): die verschwinden
            for (var n = 2; n < 30; n++) File.Delete(Path.Combine(root, $"f{n:D2}.jpg"));
            await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
            Assert.Equal(2, GalleryFolders.Stages(space.Db, src.Id).Total);
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Einlesen_in_Stufen_erst_Platzhalter_dann_Kopf_dann_ganze_Datei()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("cleo");
        var root = Directory.CreateTempSubdirectory("dmstufen").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            var big = BigJpeg();
            Assert.True(big.Length > 600 * 1024);
            File.WriteAllBytes(Path.Combine(root, "gross.jpg"), big);
            File.WriteAllBytes(Path.Combine(root, "klein.jpg"), Jpeg(300, 200));
            File.WriteAllBytes(Path.Combine(root, "weg.jpg"), big);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "stufen01", Name = "Stufen", Path = root }]);
            var c = _fx.As("cleo");
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "stufen01", path = "" })).StatusCode);
            var src = GalleryFolders.Of(space.Db).Single(s => s.Mount == "stufen01");

            // Stufe 1: nur aufgelistet - beide stehen sofort in der Galerie, noch ohne Bildangaben
            Assert.Equal(3, await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None));
            Assert.Equal((3, 3, 0), GalleryFolders.Stages(space.Db, src.Id));
            var list = await c.GetFromJsonAsync<JsonElement>("/api/gallery/external");
            Assert.Equal(["gross.jpg", "klein.jpg", "weg.jpg"], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).Order());

            // Stufe 2: Kopf der Datei - Bildangaben für beide, das kleine ist schon fertig (ganz gelesen), das große braucht noch die ganze Datei
            await GalleryFolders.HeadsAsync(space, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            Assert.Equal((3, 0, 2), GalleryFolders.Stages(space.Db, src.Id));
            var meta = (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray().ToList();
            Assert.Contains(meta, m => m.GetProperty("key").GetString()!.EndsWith("gross.jpg") && m.GetProperty("width").GetInt32() == 1600);

            // Stufe 3: ganze Datei - Vorschaubild auch für das große; was inzwischen verschoben wurde, verschwindet gleich (nicht erst beim nächsten Einlesen)
            File.Delete(Path.Combine(root, "weg.jpg"));
            await GalleryFolders.FullAsync(space, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            Assert.Equal((2, 0, 0), GalleryFolders.Stages(space.Db, src.Id));
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/gallery/external/thumb?id=" + Uri.EscapeDataString(src.Id + "/gross.jpg"))).StatusCode);
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Externer_Ordner_wird_eingelesen_gezeigt_und_nachgefuehrt()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("ben");
        var root = Directory.CreateTempSubdirectory("dmgal").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Urlaub", "Tag 1"));
            Directory.CreateDirectory(Path.Combine(root, "@eaDir"));
            File.WriteAllBytes(Path.Combine(root, "Urlaub", "Tag 1", "strand.jpg"), Jpeg(1200, 800));
            // RAW: eingebettetes JPEG zwischen anderen Bytes
            File.WriteAllBytes(Path.Combine(root, "Urlaub", "kamera.cr2"), [.. new byte[300], .. Jpeg(640, 480), .. new byte[500]]);
            File.WriteAllBytes(Path.Combine(root, "Urlaub", "clip.mp4"), new byte[2000]);
            File.WriteAllText(Path.Combine(root, "Urlaub", "notizen.txt"), "kein Foto");
            File.WriteAllBytes(Path.Combine(root, "@eaDir", "versteckt.jpg"), Jpeg(10, 10));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "galerie01", Name = "Fotos", Path = root }]);

            var c = _fx.As("ben");
            var add = await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "galerie01", path = "Urlaub", watch = true });
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "galerie01", path = "gibtsnicht" })).StatusCode);
            var job = new GalleryScanJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);

            var list = await c.GetFromJsonAsync<JsonElement>("/api/gallery/external");
            var names = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
            Assert.Equal(["clip.mp4", "kamera.cr2", "strand.jpg"], names.Order());
            var strand = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "strand.jpg").GetProperty("id").GetString()!;
            var raw = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "kamera.cr2").GetProperty("id").GetString()!;
            Assert.Contains(list.GetProperty("meta").EnumerateArray(), m => m.GetProperty("key").GetString() == "ext:" + strand && m.GetProperty("width").GetInt32() == 1200);
            // Unverändert: mit dem ETag nur 304 (die Liste kommt nicht noch einmal); komprimiert, wenn der Browser es kann
            var first = await c.GetAsync("/api/gallery/external");
            var etag = first.Headers.ETag;
            Assert.NotNull(etag);
            using (var again = new HttpRequestMessage(HttpMethod.Get, "/api/gallery/external"))
            {
                again.Headers.IfNoneMatch.Add(etag!);
                Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(again)).StatusCode);
            }
            using (var packed = new HttpRequestMessage(HttpMethod.Get, "/api/gallery/external"))
            {
                packed.Headers.AcceptEncoding.ParseAdd("br");
                Assert.Contains("br", (await c.SendAsync(packed)).Content.Headers.ContentEncoding);
            }
            // Kompakte Form der Web-Galerie: dieselben Fotos, eigener Stempel, unverändert 304
            var compact = await c.GetAsync("/api/gallery/external/compact");
            var cj = await compact.Content.ReadFromJsonAsync<JsonElement>();
            var cdirs = cj.GetProperty("dirs").EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Equal(list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Order(),
                cj.GetProperty("rows").EnumerateArray().Select(r => cdirs[r[0].GetInt32()] + r[1].GetString()).Order());
            using (var again = new HttpRequestMessage(HttpMethod.Get, "/api/gallery/external/compact"))
            {
                again.Headers.IfNoneMatch.Add(compact.Headers.ETag!);
                Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(again)).StatusCode);
            }

            // Vorschaubilder: klein aus dem Index (auch RAW), gross beim ersten Mal aus dem Original
            foreach (var id in new[] { strand, raw })
            {
                var t = await c.GetAsync("/api/gallery/external/thumb?id=" + Uri.EscapeDataString(id));
                Assert.Equal(HttpStatusCode.OK, t.StatusCode);
                Assert.Equal("image/webp", t.Content.Headers.ContentType?.MediaType);
            }
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/gallery/external/thumb?size=1600&id=" + Uri.EscapeDataString(strand))).StatusCode);
            // Das Original im Browser - Video ohne Standbild: noch keins
            var file = await c.GetAsync("/api/gallery/external/file?inline=true&id=" + Uri.EscapeDataString(strand));
            Assert.Equal("image/jpeg", file.Content.Headers.ContentType?.MediaType);
            var clip = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "clip.mp4").GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/gallery/external/thumb?id=" + Uri.EscapeDataString(clip))).StatusCode);
            // Nie aus dem Galerie-Ordner hinaus
            Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync("/api/gallery/external/file?id=" + Uri.EscapeDataString(strand.Split('/')[0] + "/../../geheim.jpg"))).StatusCode);

            // Gelöscht und neu dazu: der nächste Durchgang führt nach
            File.Delete(Path.Combine(root, "Urlaub", "Tag 1", "strand.jpg"));
            File.WriteAllBytes(Path.Combine(root, "Urlaub", "neu.png"), Jpeg(50, 50));
            var src = GalleryFolders.Of(space.Db).Single();
            GalleryScanJob.Request(space.User.Id, src.Id);
            await job.RunOnceAsync(CancellationToken.None);
            names = (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
            Assert.Contains("neu.png", names);
            Assert.DoesNotContain("strand.jpg", names);
            Assert.Equal(3, GalleryFolders.Of(space.Db).Single().Count);
            // Nach der Änderung ein neuer Stempel - die alte Kopie im Browser gilt nicht mehr
            Assert.NotEqual(etag, (await c.GetAsync("/api/gallery/external")).Headers.ETag);

            // Entfernen räumt den Index
            Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync("/api/gallery/sources/" + src.Id)).StatusCode);
            Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("items").EnumerateArray());
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Viele große Vorschauen auf einmal (Browser wiederholt sie, wenn die Freigabe zurück ist): höchstens zwei rechnen zugleich,
    /// alle kommen trotzdem an; dasselbe Foto mehrfach angefragt ergibt dieselbe Vorschau.
    /// </summary>
    [Fact]
    public async Task Viele_grosse_Vorschauen_zugleich_kommen_alle_an()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var root = Directory.CreateTempSubdirectory("dmgal").FullName;
        var before = config.Current.ExternalFolders;
        var c = _fx.As("ben");
        string? sourceId = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Viele"));
            for (int i = 0; i < 8; i++) File.WriteAllBytes(Path.Combine(root, "Viele", $"f{i}.jpg"), Jpeg(1800 + i, 1200));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "galerie07", Name = "Fotos", Path = root }]);
            var add = await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "galerie07", path = "Viele" });
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
            sourceId = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            await new GalleryScanJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryScanJob>.Instance).RunOnceAsync(CancellationToken.None);

            var ids = Enumerable.Range(0, 8).Select(i => sourceId + "/Viele/f" + i + ".jpg").Concat(Enumerable.Repeat(sourceId + "/Viele/f0.jpg", 4)).ToList();
            var res = await Task.WhenAll(ids.Select(id => c.GetAsync("/api/gallery/external/thumb?size=2048&id=" + Uri.EscapeDataString(id))));
            Assert.All(res, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var same = await Task.WhenAll(res.Where((_, i) => ids[i] == sourceId + "/Viele/f0.jpg").Select(r => r.Content.ReadAsByteArrayAsync()));
            Assert.All(same, b => Assert.Equal(same[0], b));
        }
        finally
        {
            if (sourceId != null) await c.DeleteAsync("/api/gallery/sources/" + sourceId);
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }

    private const string Xmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:exif="http://ns.adobe.com/exif/1.0/" xmp:Rating="4"
            exif:GPSLatitude="46,47.491843N" exif:GPSLongitude="11,4.998587E">
            <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Sonnenuntergang</rdf:li></rdf:Alt></dc:title>
            <dc:description><rdf:Alt><rdf:li xml:lang="x-default">Am Strand von Rimini</rdf:li></rdf:Alt></dc:description>
            <dc:subject><rdf:Bag><rdf:li>Urlaub</rdf:li><rdf:li>Italien</rdf:li></rdf:Bag></dc:subject>
          </rdf:Description></rdf:RDF></x:xmpmeta>
        """;

    [Fact]
    public void Sidecar_lesen_und_zuordnen()
    {
        var d = Sidecar.Read(System.Text.Encoding.UTF8.GetBytes(Xmp))!;
        Assert.Equal("Sonnenuntergang", d.Title);
        Assert.Equal("Am Strand von Rimini", d.Description);
        Assert.Equal(["Urlaub", "Italien"], d.Keywords);
        Assert.Equal(4, d.Rating);
        var names = new Dictionary<string, string> { [Sidecar.Key("IMG_1.ARW.xmp")] = "IMG_1.ARW.xmp", [Sidecar.Key("IMG_2.xmp")] = "IMG_2.xmp" };
        Assert.Equal("IMG_1.ARW.xmp", Sidecar.For("IMG_1.ARW", names));
        Assert.Equal("IMG_2.xmp", Sidecar.For("img_2.jpg", names));
        Assert.Null(Sidecar.For("IMG_3.jpg", names));
        Assert.Null(Sidecar.Read("<!DOCTYPE x [<!ENTITY a 'b'>]><x>&a;</x>"u8.ToArray()));
    }

    [Fact]
    public async Task Sidecar_im_Galerie_Ordner_und_nachgefuehrt()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("ben");
        var root = Directory.CreateTempSubdirectory("dmside").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "foto.jpg"), Jpeg(300, 200));
            File.WriteAllText(Path.Combine(root, "foto.jpg.xmp"), Xmp);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "side01", Name = "Side", Path = root }]);
            var c = _fx.As("ben");
            foreach (var old in GalleryFolders.Of(space.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "side01", path = "" })).StatusCode);
            var job = new GalleryScanJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);
            JsonElement Meta() => GalleryMeta(c).Result;
            var m = Meta();
            Assert.Equal("Sonnenuntergang", m.GetProperty("title").GetString());
            Assert.Equal("Am Strand von Rimini", m.GetProperty("description").GetString());
            Assert.Equal("Urlaub\nItalien", m.GetProperty("keywords").GetString());
            // Ort aus der Sidecar-Datei (Atlas, Detailbereich)
            Assert.Equal(46.79153, m.GetProperty("lat").GetDouble(), 4);
            Assert.Equal(11.08331, m.GetProperty("lon").GetDouble(), 4);

            // Nur die Sidecar-Datei ändert sich (Lightroom): der nächste Durchgang übernimmt es
            File.WriteAllText(Path.Combine(root, "foto.jpg.xmp"), Xmp.Replace("Sonnenuntergang", "Abendrot"));
            File.SetLastWriteTimeUtc(Path.Combine(root, "foto.jpg.xmp"), DateTime.UtcNow.AddMinutes(1));
            var src = GalleryFolders.Of(space.Db).Single();
            GalleryScanJob.Request(space.User.Id, src.Id);
            await job.RunOnceAsync(CancellationToken.None);
            Assert.Equal("Abendrot", Meta().GetProperty("title").GetString());

            // Abgeschaltet: wieder nur, was im Foto steht
            await c.PutAsJsonAsync("/api/gallery/sources/" + src.Id, new { sidecars = false });
            await job.RunOnceAsync(CancellationToken.None);
            Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray()
                .Any(x => x.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String));
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Loeschen_im_Galerie_Ordner_mit_Sidecar_nur_wenn_beschreibbar()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("ben");
        var root = Directory.CreateTempSubdirectory("dmdel").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "foto.jpg"), Jpeg(300, 200));
            File.WriteAllText(Path.Combine(root, "foto.jpg.xmp"), Xmp);
            // RAW und JPG teilen sich „paar.xmp“ - bleibt, solange eins der beiden da ist
            File.WriteAllBytes(Path.Combine(root, "paar.jpg"), Jpeg(300, 200));
            File.WriteAllBytes(Path.Combine(root, "paar.arw"), new byte[64]);
            File.WriteAllText(Path.Combine(root, "paar.xmp"), Xmp);
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "del01", Name = "Del", Path = root, ReadOnly = true }]);
            var c = _fx.As("ben");
            foreach (var old in GalleryFolders.Of(space.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "del01", path = "" })).StatusCode);
            var src = GalleryFolders.Of(space.Db).Single();
            await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
            async Task<List<string>> Ids() => (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();
            Assert.Contains(src.Id + "/foto.jpg", await Ids());

            // Nur lesbar eingebunden: nichts geht
            var ro = await c.PostAsJsonAsync("/api/gallery/external/delete", new { ids = new[] { src.Id + "/foto.jpg" } });
            Assert.Equal(HttpStatusCode.Forbidden, ro.StatusCode);
            Assert.True(File.Exists(Path.Combine(root, "foto.jpg")));

            // Beschreibbar: Foto samt „foto.jpg.xmp“ weg, aus der Liste sofort
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "del01", Name = "Del", Path = root }]);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/external/delete", new { ids = new[] { src.Id + "/foto.jpg", src.Id + "/paar.jpg" } })).StatusCode);
            Assert.False(File.Exists(Path.Combine(root, "foto.jpg")));
            Assert.False(File.Exists(Path.Combine(root, "foto.jpg.xmp")));
            Assert.False(File.Exists(Path.Combine(root, "paar.jpg")));
            Assert.True(File.Exists(Path.Combine(root, "paar.arw")));
            Assert.True(File.Exists(Path.Combine(root, "paar.xmp")));
            var ids = await Ids();
            Assert.DoesNotContain(src.Id + "/foto.jpg", ids);
            Assert.DoesNotContain(src.Id + "/paar.jpg", ids);

            // Das letzte Foto des Paars: jetzt geht „paar.xmp“ mit
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/external/delete", new { ids = new[] { src.Id + "/paar.arw" } })).StatusCode);
            Assert.False(File.Exists(Path.Combine(root, "paar.xmp")));
            Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/gallery/external/delete", new { ids = new[] { "unbekannt/x.jpg" } })).StatusCode);
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Im_Editor_gespeichertes_Foto_steht_sofort_in_der_Galerie()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("ben");
        var root = Directory.CreateTempSubdirectory("dmedit").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "foto.jpg"), Jpeg(300, 200));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "edit01", Name = "Edit", Path = root }]);
            var c = _fx.As("ben");
            foreach (var old in GalleryFolders.Of(space.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "edit01", path = "" })).StatusCode);
            var src = GalleryFolders.Of(space.Db).Single();
            await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
            await GalleryFolders.HeadsAsync(space, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);

            // Als Kopie gespeichert: ohne neues Einlesen in der Liste, mit Vorschaubild
            var r = await c.PostAsJsonAsync("/api/files/photo-edit", new { path = "ext:" + src.Id + "/foto.jpg", rotate = 0, crop = new { x = 0, y = 0, w = 0.5, h = 1 }, overwrite = false });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var ids = (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();
            Assert.Contains(src.Id + "/foto_bearbeitet.jpg", ids);
            var thumb = await c.GetAsync("/api/gallery/external/thumb?id=" + Uri.EscapeDataString(src.Id + "/foto_bearbeitet.jpg") + "&size=256");
            Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
            Assert.True((await thumb.Content.ReadAsByteArrayAsync()).Length > 0);

            // Geschrieben wird im Hintergrund; währenddessen trägt das Einlesen sie nicht aus
            await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
            Assert.Contains(src.Id + "/foto_bearbeitet.jpg", (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()!));
            await DmOffice.Dav.PhotoWork.Drain();
            Assert.True(File.Exists(Path.Combine(root, "foto_bearbeitet.jpg")));
            // RAW nur mit der Photo-Engine (hier nicht eingerichtet)
            File.WriteAllBytes(Path.Combine(root, "roh.arw"), new byte[64]);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.PostAsJsonAsync("/api/files/photo-edit", new { path = "ext:" + src.Id + "/roh.arw", rotate = 0, crop = (object?)null, overwrite = false })).StatusCode);

            // Das nächste Einlesen kennt die Datei schon: nichts offen
            await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
            var (total, listed, noThumb) = GalleryFolders.Stages(space.Db, src.Id);
            Assert.Equal((3, 1, 0), (total, listed, noThumb));   // die Kopie ist bekannt; neu nur roh.arw
            await c.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<JsonElement> GalleryMeta(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray().Single();

    [Fact]
    public void Eingebettetes_JPEG_aus_RAW()
    {
        var jpg = Jpeg(320, 200);
        var small = Jpeg(32, 20);
        var raw = (byte[])[.. new byte[100], .. small, .. new byte[50], .. jpg, .. new byte[10]];
        var found = GalleryFolders.EmbeddedJpeg(raw)!;
        using var bmp = SKBitmap.Decode(found);
        Assert.Equal(320, bmp.Width);
        Assert.Null(GalleryFolders.EmbeddedJpeg(new byte[1000]));
    }

    /// <summary>Kleinstes RAW zum Testen: TIFF-Kopf mit IFD0 (nur Orientation, wenn angegeben), dahinter die JPEG-Vorschau.</summary>
    private static byte[] Raw(byte[] preview, int? orientation)
    {
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42); w.Write(8u);
        if (orientation is { } o) { w.Write((ushort)1); w.Write((ushort)0x0112); w.Write((ushort)3); w.Write(1u); w.Write((ushort)o); w.Write((ushort)0); }
        else w.Write((ushort)0);
        w.Write(0u);
        w.Write(new byte[64]);
        w.Write(preview);
        return m.ToArray();
    }

    private static (int W, int H) Size(byte[]? webp)
    {
        using var b = SKBitmap.Decode(webp!);
        return (b.Width, b.Height);
    }

    [Fact]
    public void RAW_Vorschau_wird_gedreht_aber_nie_doppelt()
    {
        // Ungedrehte Vorschau, Winkel im RAW: hochkant
        var t = Size(Dav.Thumbs.FromRaw(Raw(Jpeg(300, 200), 6), 256));
        Assert.True(t.H > t.W, $"{t.W}x{t.H}");
        // Hersteller hat die Vorschau schon gedreht und gibt den Winkel trotzdem an: bleibt hochkant
        t = Size(Dav.Thumbs.FromRaw(Raw(Jpeg(200, 300), 6), 256));
        Assert.True(t.H > t.W, $"{t.W}x{t.H}");
        // Ohne Winkel: quer wie gespeichert
        t = Size(Dav.Thumbs.FromRaw(Raw(Jpeg(300, 200), null), 256));
        Assert.True(t.W > t.H, $"{t.W}x{t.H}");
        // Auf dem Kopf (3) bleibt quer
        t = Size(Dav.Thumbs.FromRaw(Raw(Jpeg(300, 200), 3), 256));
        Assert.True(t.W > t.H, $"{t.W}x{t.H}");
    }
}
