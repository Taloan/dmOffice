using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Tests;

public class QuotaTests
{
    [Fact]
    public void Datei_ueber_der_Grenze_wird_abgewiesen()
    {
        var dir = Directory.CreateTempSubdirectory("dmquota");
        var id = "quota-" + Guid.NewGuid().ToString("N");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], id);
        // Nur fuer diesen Benutzer begrenzen - andere Tests laufen parallel
        var before = FileStore.QuotaOf;
        FileStore.QuotaOf = u => u == id ? 1000 : before?.Invoke(u) ?? 0;
        try
        {
            FileStore.EnsureDir(db, "a");
            FileStore.Put(db, "a/x.bin", new byte[600], null);
            Assert.Throws<QuotaExceededException>(() => FileStore.Put(db, "a/y.bin", new byte[600], null));
            // Ersetzen durch Kleineres geht, Kopieren ueber die Grenze nicht
            FileStore.Put(db, "a/x.bin", new byte[400], null);
            FileStore.Put(db, "a/y.bin", new byte[500], null);
            Assert.Throws<QuotaExceededException>(() => FileStore.MoveOrCopy(db, "a", "b", false, copy: true));
            FileStore.MoveOrCopy(db, "a", "b", false, copy: false);
            Assert.Equal(900, FileStore.Usage(db));
        }
        finally { FileStore.QuotaOf = before; }
    }

    [Theory]
    [InlineData("notizisync", true)]
    [InlineData("notizisync/notizi pr/db.bin", true)]
    [InlineData("notizisync2", false)]
    [InlineData("", false)]
    [InlineData("privat/notizisync", false)]
    public void Dateiablage_aus_laesst_nur_den_notizi_Ordner(string path, bool allowed) =>
        Assert.Equal(allowed, FileStore.InRoot(path, "notizisync"));
}
