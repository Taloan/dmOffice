using System.Text.Json;
using DmOffice.Dav;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Farbmischer: Werte flach im JSON (hsl_s_red …), Begrenzung, Wirkung auf Farben, Grau bleibt grau.</summary>
public class PhotoMixerTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Werte_flach_begrenzt_und_wirksam()
    {
        var v = JsonSerializer.Deserialize<PhotoAdjust.Values>("""{"exposure":0,"hsl_s_red":-100,"hsl_l_blue":250,"other":"x"}""", Web)!.Clamped();
        Assert.False(v.IsNeutral);
        Assert.Equal(-100, v.Hsl('s', "red"));
        Assert.Equal(100, v.Hsl('l', "blue"));
        var json = JsonSerializer.Serialize(v, Web);
        Assert.Contains("\"hsl_s_red\":-100", json);
        Assert.DoesNotContain("other", json);
        Assert.True(JsonSerializer.Deserialize<PhotoAdjust.Values>("{}", Web)!.Clamped().IsNeutral);

        using var bmp = new SKBitmap(4, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        bmp.SetPixel(0, 0, new SKColor(220, 30, 30));
        bmp.SetPixel(1, 0, new SKColor(128, 128, 128));
        bmp.SetPixel(2, 0, new SKColor(30, 160, 40));
        PhotoAdjust.Mixer(bmp, v);
        // Rot fast grau (das Orange-Band reicht weich herein); Grau und Grün unverändert
        var red = bmp.GetPixel(0, 0);
        Assert.True(red.Red - red.Green < 40 && red.Red - red.Blue < 40, red.ToString());
        Assert.Equal(new SKColor(128, 128, 128), bmp.GetPixel(1, 0));
        Assert.Equal(new SKColor(30, 160, 40), bmp.GetPixel(2, 0));
    }
}
