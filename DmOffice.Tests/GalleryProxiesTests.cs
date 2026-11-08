using DmOffice.Gallery;

namespace DmOffice.Tests;

/// <summary>Große Vorschauen der Galerie-Ordner: verschlüsselt abgelegt, nur mit passendem Stand gültig, je Ordner zu entfernen.</summary>
public class GalleryProxiesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proxies-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Ablegen_lesen_Stand_und_Entfernen()
    {
        var key = new byte[32]; Random.Shared.NextBytes(key);
        var p = new GalleryProxies(_dir, key);
        var webp = "RIFF....WEBPVP8 Inhalt"u8.ToArray();
        var stamp = GalleryProxies.Stamp(12345, 1_700_000_000_000);

        Assert.False(p.Has("anna", "src1", "Urlaub/a.jpg", stamp));
        p.Put("anna", "src1", "Urlaub/a.jpg", stamp, webp);
        Assert.True(p.Has("anna", "src1", "Urlaub/a.jpg", stamp));
        Assert.Equal(webp, p.Get("anna", "src1", "Urlaub/a.jpg", stamp));

        // Verschlüsselt: der Inhalt steht nicht im Klartext in der Datei
        var file = Directory.GetFiles(_dir, "*.bin", SearchOption.AllDirectories).Single();
        Assert.DoesNotContain("Inhalt", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file)));

        // Original geändert (anderer Stand): gilt nicht mehr
        var changed = GalleryProxies.Stamp(12346, 1_700_000_000_000);
        Assert.False(p.Has("anna", "src1", "Urlaub/a.jpg", changed));
        Assert.Null(p.Get("anna", "src1", "Urlaub/a.jpg", changed));

        // Anderer Schlüssel: nicht lesbar
        var other = new byte[32]; Random.Shared.NextBytes(other);
        Assert.Null(new GalleryProxies(_dir, other).Get("anna", "src1", "Urlaub/a.jpg", stamp));

        Assert.Equal(1, p.Usage("anna", "src1").Count);
        p.RemoveSource("anna", "src1");
        Assert.False(p.Has("anna", "src1", "Urlaub/a.jpg", stamp));
        Assert.Equal(0, p.Usage("anna", "src1").Count);
    }
}
