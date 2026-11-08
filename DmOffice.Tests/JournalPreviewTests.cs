using DmOffice.Dav;
using DmOffice.Journal;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Tagebuch aus einer fertigen Vorschau (Galerie-Ordner): als JPEG in der eingestellten Größe, kleinere Vorschau so übernommen.</summary>
[Collection("server")]
public class JournalPreviewTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public JournalPreviewTests(ChatFixture fx) => _fx = fx;

    private static byte[] Image(int w, int h, SKEncodedImageFormat format)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Teal);
        return bmp.Encode(format, 80).ToArray();
    }

    [Fact]
    public void Vorschau_statt_Original()
    {
        var space = _fx.Space("anna");
        JournalMediaStore.SetImageSize(space.Db, 1024);
        var taken = new DateTime(2026, 10, 24, 17, 0, 0, DateTimeKind.Utc);

        // Große Vorschau (WebP, 2048 px): als JPEG auf 1024 px, Aufnahmezeit und Ort vom Aufrufer
        var big = JournalMediaStore.FromPreview(space, "DSCF0625.JPG", Image(2048, 1365, SKEncodedImageFormat.Webp), 2048, taken, 48.3, 8.6)!;
        Assert.Equal(("image/jpeg", 1024, taken), (big.Media.Mime, big.Media.Width ?? 0, big.Media.Taken!.Value));
        Assert.Equal((48.3, 8.6), (big.Lat!.Value, big.Lon!.Value));
        Assert.EndsWith(".jpg", big.Media.Path);

        // Ältere Vorschau (JPEG, 1600 px) bei 2048 eingestellt: so übernommen
        JournalMediaStore.SetImageSize(space.Db, 2048);
        var old = JournalMediaStore.FromPreview(space, "DSCF0626.JPG", Image(1600, 1067, SKEncodedImageFormat.Jpeg), 1600, taken, null, null)!;
        Assert.Equal(1600, old.Media.Width);
        Assert.NotNull(FileStore.Get(space.Db, old.Media.Path));

        // Originalgröße eingestellt: keine Vorschau, das Original wird geholt
        JournalMediaStore.SetImageSize(space.Db, 0);
        Assert.Null(JournalMediaStore.FromPreview(space, "x.jpg", Image(100, 100, SKEncodedImageFormat.Jpeg), 2048, null, null, null));
        JournalMediaStore.SetImageSize(space.Db, JournalMediaStore.DefaultSize);
    }
}
