using DmOffice.Core;
using DmOffice.Dav;
using Xunit;

namespace DmOffice.Tests;

/// <summary>TIFF-Vorschau über die Photo-Engine (Kennung tiffpreview): Art, Größengrenze, ohne Kennung wie bisher.</summary>
public class TiffPreviewTests
{
    [Fact]
    public void Tiff_ist_mit_Kennung_ein_Bild_mit_eigener_Groessengrenze()
    {
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set(before.Where(x => x != Features.TiffPreview));
            Assert.Equal(Thumbs.Kind.Image, Thumbs.KindOf("lightroom.TIF", "image/tiff"));
            Assert.True(Thumbs.IsTiff("a.tiff"));
            // Lightroom-Export mit 16 Bit: über der Grenze für andere Bilder (250 MB), unter der für TIFF (400 MB)
            Assert.True(Thumbs.Expected("export.tif", "image/tiff", 300L * 1024 * 1024));
            Assert.False(Thumbs.Expected("foto.jpg", "image/jpeg", 300L * 1024 * 1024));
            Assert.False(Thumbs.Expected("riesig.tif", "image/tiff", 500L * 1024 * 1024));

            Features.Set([.. before, Features.TiffPreview]);
            Assert.False(Thumbs.IsTiff("a.tif"));
            Assert.Equal(Thumbs.Kind.None, Thumbs.KindOf("a.tif", "image/tiff"));
        }
        finally { Features.Set(before); }
    }
}
