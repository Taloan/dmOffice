using DmOffice.Storage;

namespace DmOffice.Tests;

/// <summary>Externe Ordner mit Auswahl der obersten Ebene: nur die gewählten Ordner, oben nichts anlegen.</summary>
public class SubsetDriverTests
{
    [Fact]
    public async Task Nur_gewaehlte_Ordner_oben_nichts_aendern()
    {
        var root = Directory.CreateTempSubdirectory("dmsubset").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Musik", "Alben"));
            Directory.CreateDirectory(Path.Combine(root, "Privat"));
            File.WriteAllText(Path.Combine(root, "oben.txt"), "x");
            File.WriteAllText(Path.Combine(root, "Privat", "geheim.txt"), "x");
            using var d = new SubsetDriver(new FolderDriver(root), ["Musik"]);
            var ct = CancellationToken.None;

            Assert.Equal(["Musik"], (await d.ListAsync("", ct)).Select(e => e.Name));
            Assert.Equal(["Alben"], (await d.ListAsync("Musik", ct)).Select(e => e.Name));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => d.ListAsync("Privat", ct));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => d.OpenReadAsync("Privat/geheim.txt", ct));
            Assert.Null(await d.StatAsync("oben.txt", ct));

            await d.WriteAsync("Musik/neu.txt", new MemoryStream([1]), ct);
            Assert.True(File.Exists(Path.Combine(root, "Musik", "neu.txt")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.WriteAsync("neu.txt", new MemoryStream([1]), ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.DeleteAsync("Musik", ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.MoveAsync("Musik/neu.txt", "Privat/neu.txt", ct));
        }
        finally { Directory.Delete(root, true); }
    }
}
