using DmOffice.Core;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Dav;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Aufnahmedaten aus Fotos: gelesen im Hintergrund, abgerufen für Galerie (alle) und Detailansicht (ein Ordner).</summary>
public class FileMetaTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public FileMetaTests(ChatFixture fx) => _fx = fx;

    /// <summary>Ein kleines JPEG mit EXIF: Canon EOS R5, RF-Objektiv, f/2.8, 1/250 s, ISO 200, aufgenommen am 6.5.2024 07:08:09.</summary>
    internal static byte[] ExifJpeg()
    {
        using var bmp = new SKBitmap(8, 6);
        using var img = SKImage.FromBitmap(bmp);
        var jpg = img.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();

        const int dataStart = 116;   // Kopf 8, IFD0 mit 3 Einträgen bis 50, Exif-IFD mit 5 Einträgen bis 116
        var data = new List<byte>();
        uint Add(byte[] b) { var at = (uint)(dataStart + data.Count); data.AddRange(b); if (data.Count % 2 == 1) data.Add(0); return at; }
        static byte[] A(string s) => Encoding.ASCII.GetBytes(s + "\0");
        static byte[] R(uint n, uint d) => [.. BitConverter.GetBytes(n), .. BitConverter.GetBytes(d)];
        static void E(List<byte> ifd, ushort tag, ushort type, uint count, uint value) { ifd.AddRange(BitConverter.GetBytes(tag)); ifd.AddRange(BitConverter.GetBytes(type)); ifd.AddRange(BitConverter.GetBytes(count)); ifd.AddRange(BitConverter.GetBytes(value)); }

        var make = A("Canon"); var model = A("Canon EOS R5"); var when = A("2024:05:06 07:08:09"); var lens = A("RF24-105mm F4 L IS USM");
        var ifd0 = new List<byte>(BitConverter.GetBytes((ushort)3));
        E(ifd0, 0x010F, 2, (uint)make.Length, Add(make));
        E(ifd0, 0x0110, 2, (uint)model.Length, Add(model));
        E(ifd0, 0x8769, 4, 1, 50);
        ifd0.AddRange(BitConverter.GetBytes(0u));
        var sub = new List<byte>(BitConverter.GetBytes((ushort)5));
        E(sub, 0x829A, 5, 1, Add(R(1, 250)));
        E(sub, 0x829D, 5, 1, Add(R(28, 10)));
        E(sub, 0x8827, 3, 1, 200);
        E(sub, 0x9003, 2, (uint)when.Length, Add(when));
        E(sub, 0xA434, 2, (uint)lens.Length, Add(lens));
        sub.AddRange(BitConverter.GetBytes(0u));
        byte[] tiff = [.. "II"u8, 0x2A, 0x00, 8, 0, 0, 0, .. ifd0, .. sub, .. data];

        var app1 = new List<byte> { 0xFF, 0xE1 };
        var len = 2 + 6 + tiff.Length;
        app1.Add((byte)(len >> 8)); app1.Add((byte)len);
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(tiff);
        return [0xFF, 0xD8, .. app1, .. jpg[2..]];
    }

    [Fact]
    public async Task Aufnahmedaten_fuer_Ordner_und_Galerie()
    {
        var space = _fx.Space("anna");
        var dir = "Meta-" + Guid.NewGuid().ToString("N")[..6];
        FileStore.EnsureDir(space.Db, dir);
        FileStore.Put(space.Db, dir + "/urlaub.jpg", ExifJpeg(), "image/jpeg");
        Assert.True(FileMeta.Pending(space.Db) >= 1);
        FileMeta.Compute(space, CancellationToken.None);
        Assert.Equal(0, FileMeta.Pending(space.Db));

        var c = _fx.As("anna");
        var inFolder = await c.GetFromJsonAsync<JsonElement>("/api/files/meta?path=" + Uri.EscapeDataString(dir));
        var row = inFolder.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("urlaub.jpg", row.GetProperty("key").GetString());
        Assert.Equal("Canon EOS R5", row.GetProperty("camera").GetString());
        Assert.Equal("RF24-105mm F4 L IS USM", row.GetProperty("lens").GetString());
        Assert.Equal(2.8, row.GetProperty("aperture").GetDouble());
        Assert.Equal(0.004, row.GetProperty("exposure").GetDouble(), 6);
        Assert.Equal(200, row.GetProperty("iso").GetInt32());
        Assert.Equal(new DateTime(2024, 5, 6), DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("taken").GetInt64()).UtcDateTime.Date);

        var all = await c.GetFromJsonAsync<JsonElement>("/api/files/meta");
        Assert.Contains(all.GetProperty("items").EnumerateArray(), r => r.GetProperty("key").GetString() == dir + "/urlaub.jpg");
        Assert.Equal(0, all.GetProperty("pending").GetInt64());

        // Verschoben: die Daten ziehen mit
        FileStore.MoveOrCopy(space.Db, dir + "/urlaub.jpg", dir + "/sommer.jpg", overwrite: false, copy: false);
        var moved = await c.GetFromJsonAsync<JsonElement>("/api/files/meta?path=" + Uri.EscapeDataString(dir));
        Assert.Equal("sommer.jpg", moved.GetProperty("items").EnumerateArray().Single().GetProperty("key").GetString());
    }

    [Fact]
    public void Nur_im_externen_Speicher_wird_gelesen_sobald_der_Inhalt_wieder_da_ist()
    {
        var space = _fx.Space("cleo");
        var db = space.Db;
        var path = "Weg-" + Guid.NewGuid().ToString("N")[..6] + ".jpg";
        FileStore.Put(db, path, ExifJpeg(), "image/jpeg");
        // Inhalt beiseite, als hätte SSBS ihn verdrängt (liegt nur noch im Storage Extender)
        var blob = (string)db.Scalar("SELECT blob FROM files WHERE path=$p", ("$p", path))!;
        var aside = Path.GetTempFileName();
        File.Move(db.Blobs.PathOf(blob), aside, overwrite: true);
        try
        {
            using (BlobStore.WithoutFetch()) FileMeta.Compute(space, CancellationToken.None);
            // Nicht leer abgehakt, aber auch nicht ewig „noch zu lesen“
            Assert.DoesNotContain(FileMeta.List(db, null), m => m.Path == path);
            Assert.Equal(0, FileMeta.Pending(db));
            // Wieder hier: beim nächsten Durchgang gelesen
            File.Move(aside, db.Blobs.PathOf(blob));
            FileMeta.Compute(space, CancellationToken.None);
            Assert.Equal("Canon EOS R5", FileMeta.List(db, null).Single(m => m.Path == path).Camera);
        }
        finally { if (File.Exists(aside)) File.Move(aside, db.Blobs.PathOf(blob), overwrite: true); }
    }
}
