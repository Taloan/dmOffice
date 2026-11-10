using DmOffice.Core.Cryptomator;
using DmOffice.Storage;

namespace DmOffice.Tests;

/// <summary>Cryptomator-Tresor in einem externen Ordner: über den Treiber des Ziels öffnen, lesen und schreiben.</summary>
public class DriverVaultFilesTests
{
    [Fact]
    public async Task Tresor_im_externen_Ordner_lesen_und_schreiben()
    {
        var dir = Directory.CreateTempSubdirectory("dmextvault").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Private"));
            using (var key = Masterkey.Generate())
            using (var v = CryptomatorVault.Create(new DiskVaultFiles(Path.Combine(dir, "Private")), key, "geheim-123", 2, ownsKey: false))
            {
                v.CreateDirectory("/Akten");
                // Größer als ein Block (32 KB), damit über Bereiche gelesen wird
                await v.WriteAsync("/Akten/gross.bin", new MemoryStream(Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray()));
            }
            var files = new DriverVaultFiles(new FolderDriver(dir), "Private");
            Assert.True(CryptomatorPaths.LooksLikeVault(files.List("").Select(e => (e.Name, e.IsDir))));
            using var vault = CryptomatorVault.Open(files, "geheim-123")!;
            Assert.Contains(vault.List("/"), e => e.Name == "Akten" && e.IsDir);
            using (var s = vault.OpenRead("/Akten/gross.bin"))
            {
                var m = new MemoryStream();
                s.CopyTo(m);
                Assert.Equal(Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray(), m.ToArray());
            }
            await vault.WriteAsync("/neu.txt", new MemoryStream("hallo"u8.ToArray()));
            using var r = new StreamReader(vault.OpenRead("/neu.txt"));
            Assert.Equal("hallo", r.ReadToEnd());
            Assert.Null(CryptomatorVault.Open(files, "falsch"));
            files.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
