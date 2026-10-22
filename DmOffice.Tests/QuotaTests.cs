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
        // Das Kontingent gilt fuer alles (Datenbank und Dateien): 1000 Byte Platz fuer Dateien ueber dem, was die Datenbank belegt
        var baseline = UserStorage.Database(db);
        FileStore.QuotaOf = u => u == id ? baseline + 1000 : before?.Invoke(u) ?? 0;
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

    [Fact]
    public void Das_Kontingent_gilt_fuer_die_Datenbank_mit_und_ueber_der_Grenze_gibt_es_nichts_Neues()
    {
        var dir = Directory.CreateTempSubdirectory("dmquota2");
        var id = "quota-" + Guid.NewGuid().ToString("N");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], id);
        var before = FileStore.QuotaOf;
        var database = UserStorage.Database(db);
        Assert.True(database > 0);
        try
        {
            // Grenze unter dem, was die Datenbank allein belegt: ausgeschoepft, keine Datei kommt hinzu
            FileStore.QuotaOf = u => u == id ? database / 2 : before?.Invoke(u) ?? 0;
            Assert.True(UserStorage.Exceeded(db));
            FileStore.EnsureDir(db, "a");
            Assert.Throws<QuotaExceededException>(() => FileStore.Put(db, "a/x.bin", new byte[10], null));

            // Genug Platz: nicht ausgeschoepft, Dateien gehen wieder; ohne Grenze nie
            FileStore.QuotaOf = u => u == id ? database + 1_000_000 : before?.Invoke(u) ?? 0;
            Assert.False(UserStorage.Exceeded(db));
            FileStore.Put(db, "a/x.bin", new byte[10], null);
            FileStore.QuotaOf = u => u == id ? 0 : before?.Invoke(u) ?? 0;
            Assert.False(UserStorage.Exceeded(db));
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
