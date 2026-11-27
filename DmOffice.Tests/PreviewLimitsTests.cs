using System.Net;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Vorschau- und Bildpfade: Pixel- und Größengrenzen, begrenzte Gleichzeitigkeit.</summary>
public class PreviewLimitsTests
{
    [Fact]
    public void ReadLimit_liest_hoechstens_die_Grenze_auch_bei_falscher_Groessenangabe()
    {
        var data = new byte[300_000];
        Assert.Equal(data.Length, ReadLimit.ReadAll(new MemoryStream(data), 300_000, expected: 10).Length);
        Assert.Throws<ReadLimit.TooLargeException>(() => ReadLimit.ReadAll(new MemoryStream(data), 299_999, expected: 10));
        // Eine viel zu hohe Angabe legt nicht mehr als die Grenze an
        Assert.Equal(data.Length, ReadLimit.ReadAll(new MemoryStream(data), 400_000, expected: long.MaxValue).Length);
    }

    [Fact]
    public async Task ReadLimit_asynchron_wirft_ueber_der_Grenze()
    {
        var data = new byte[200_000];
        Assert.Equal(data.Length, (await ReadLimit.ReadAllAsync(new MemoryStream(data), 200_000)).Length);
        await Assert.ThrowsAsync<ReadLimit.TooLargeException>(() => ReadLimit.ReadAllAsync(new MemoryStream(data), 100_000));
    }

    /// <summary>Kleine Datei, große Fläche: PNG wird nicht verkleinert dekodiert.</summary>
    [Fact]
    public void Small_lehnt_Bilder_ueber_der_Pixelgrenze_ab()
    {
        byte[] png;
        using (var bmp = new SKBitmap(new SKImageInfo(8000, 8000, SKColorType.Gray8, SKAlphaType.Opaque)))
        {
            bmp.Erase(SKColors.White);
            using var img = SKImage.FromBitmap(bmp);
            png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        }
        Assert.True(8000L * 8000 > Thumbs.MaxPixels);
        var ex = Assert.Throws<ArgumentException>(() => PhotoAdjust.Small(png, 1400));
        Assert.Equal("Das Foto ist zu groß.", ex.Message);
    }

    [Fact]
    public void Small_verkleinert_normale_Fotos_weiter()
    {
        using var small = PhotoAdjust.Small(Jpeg(2000, 1000), 500);
        Assert.Equal(500, Math.Max(small.Width, small.Height));
    }

    /// <summary>JPEG, dessen Kopf eine riesige Fläche nennt - wenige Bytes, entpackt Gigabytes.</summary>
    [Fact]
    public void Bearbeiten_lehnt_Fotos_ueber_der_Pixelgrenze_ab()
    {
        var jpeg = Jpeg(16, 16);
        var sof = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        Assert.True(sof > 0);
        // Höhe und Breite im SOF0-Kopf: 16000 x 16000
        jpeg[sof + 5] = 16000 >> 8; jpeg[sof + 6] = 16000 & 0xFF;
        jpeg[sof + 7] = 16000 >> 8; jpeg[sof + 8] = 16000 & 0xFF;
        Assert.True(16000L * 16000 > PhotoEdit.MaxPixels);
        var ex = Assert.Throws<ArgumentException>(() => PhotoEdit.Apply(jpeg, 0, new PhotoEdit.Crop(0, 0, 0.5, 0.5)));
        Assert.Equal("Das Foto ist zu groß.", ex.Message);
    }

    [Fact]
    public void Bearbeiten_schneidet_normale_Fotos_weiter_zu()
    {
        var cut = PhotoEdit.Apply(Jpeg(400, 200), 0, new PhotoEdit.Crop(0, 0, 0.5, 0.5));
        using var bmp = SKBitmap.Decode(cut);
        Assert.Equal(200, bmp.Width);
        Assert.Equal(100, bmp.Height);
    }

    private static byte[] Jpeg(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.SteelBlue);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static async Task<WebApplication> PdfApp(RequestDelegate download)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        PdfPages.Use(app);
        app.MapGet("/dl", download);
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Pdf_Vorschau_bricht_bei_zu_grosser_Laengenangabe_sofort_ab()
    {
        var firstWriteFailed = false;
        await using var app = await PdfApp(async ctx =>
        {
            ctx.Response.ContentLength = PdfPages.MaxBytes + 1;
            try { await ctx.Response.Body.WriteAsync("%PDF-1.4"u8.ToArray()); }
            catch (IOException) { firstWriteFailed = true; throw; }
        });
        var res = await app.GetTestClient().GetAsync("/dl?pdfpages=1");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
        Assert.True(firstWriteFailed);
    }

    [Fact]
    public async Task Pdf_Vorschau_ohne_Anmeldung_rechnet_nur_eine_zur_Zeit()
    {
        int running = 0, most = 0;
        var release = new TaskCompletionSource();
        await using var app = await PdfApp(async ctx =>
        {
            var now = Interlocked.Increment(ref running);
            lock (release) most = Math.Max(most, now);
            await Task.WhenAny(release.Task, Task.Delay(1500));
            Interlocked.Decrement(ref running);
            ctx.Response.StatusCode = 404;
        });
        var client = app.GetTestClient();
        var calls = Enumerable.Range(0, 3).Select(_ => client.GetAsync("/dl?pdfpage=0")).ToList();
        await Task.Delay(500);
        release.SetResult();
        await Task.WhenAll(calls);
        Assert.Equal(1, most);
    }
}
