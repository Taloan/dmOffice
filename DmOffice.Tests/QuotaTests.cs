using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Tests;

// Nicht parallel zu Tests, die den Storage Extender (FileStore.CacheRoom, gemeinsam fuer alle) einschalten
[Collection("server")]
public class QuotaTests
{
    [Fact]
    public void Datei_ueber_der_Grenze_wird_abgewiesen()
    {
        var dir = Directory.CreateTempSubdirectory("dmquota");
        var id = "quota-" + Guid.NewGuid().ToString("N");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], id);
        // Nur fuer diesen Benutzer begrenzen - andere Tests laufen parallel
        // Das Kontingent gilt fuer alles (Datenbank und Dateien): 1000 Byte Platz fuer Dateien ueber dem, was die Datenbank belegt
        var baseline = UserStorage.Database(db);
        FileStore.QuotaFor[id] = () => baseline + 1000;
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
        finally { FileStore.QuotaFor.TryRemove(id, out _); }
    }

    [Fact]
    public async Task Das_DMS_zaehlt_Wartendes_und_Originale_gegen_das_Kontingent()
    {
        var dir = Directory.CreateTempSubdirectory("dmquota3");
        var id = "quota-" + Guid.NewGuid().ToString("N");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], id);
        var host = new DmOffice.Dms.DmsHost(new ServerConfig(new DataPaths(Path.Combine(dir.FullName, "data"))));
        DmOffice.Dms.DmsHost.Current = host;
        var space = new UserSpace(new UserRecord { Login = "q", Id = id }, db);
        DmOffice.Dms.Dms.Ensure(db);
        var baseline = UserStorage.Counted(db);
        FileStore.QuotaFor[id] = () => baseline + 2_000_000;
        try
        {
            async Task<DmOffice.Dms.DmsImport.Result> Upload(int size, DmOffice.Dms.DmsImport.Info? info = null, string name = "a.bin")
            {
                var bytes = new byte[size];
                Random.Shared.NextBytes(bytes);
                return await DmOffice.Dms.DmsImport.ImportAsync(space, new MemoryStream(bytes), name, "application/octet-stream", info ?? new("upload"), default);
            }
            // Wartet: zählt schon, und was nicht mehr passt, wird gar nicht angenommen
            await Upload(1_500_000);
            Assert.True(UserStorage.Counted(db) >= baseline + 1_500_000);
            await Assert.ThrowsAsync<QuotaExceededException>(() => Upload(1_000_000, name: "b.bin"));
            // Abgelegt wird es trotzdem: Der eigene Inhalt zählt dabei nicht doppelt
            await DmOffice.Dms.DmsImport.ProcessQueueAsync(space, host, 10, default);
            Assert.Equal("done", db.Scalar("SELECT state FROM dms_docs ORDER BY id LIMIT 1"));
            // Aufnehmen mit Verschieben: Die Datei verlässt die Ablage, also nicht doppelt rechnen
            UserStorage.Refresh(db);
            await Upload(1_000_000, new("files", Freed: 1_500_000), "c.bin");
        }
        finally { FileStore.QuotaFor.TryRemove(id, out _); }
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void Neue_Namen_haben_eine_Laengengrenze(int length, bool ok)
    {
        var dir = Directory.CreateTempSubdirectory("dmname");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "name-" + Guid.NewGuid().ToString("N"));
        var name = new string('a', length);
        if (ok)
        {
            FileStore.Put(db, name, new byte[1], null);
            FileStore.Mkcol(db, name[..254] + "b");
        }
        else
        {
            Assert.Throws<ArgumentException>(() => FileStore.Put(db, name, new byte[1], null));
            Assert.Throws<ArgumentException>(() => FileStore.Mkcol(db, name));
        }
    }

    [Fact]
    public void Das_Kontingent_gilt_fuer_die_Datenbank_mit_und_ueber_der_Grenze_gibt_es_nichts_Neues()
    {
        var dir = Directory.CreateTempSubdirectory("dmquota2");
        var id = "quota-" + Guid.NewGuid().ToString("N");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], id);
        var database = UserStorage.Database(db);
        Assert.True(database > 0);
        try
        {
            // Grenze unter dem, was die Datenbank allein belegt: ausgeschoepft, keine Datei kommt hinzu
            FileStore.QuotaFor[id] = () => database / 2;
            Assert.True(UserStorage.Exceeded(db));
            FileStore.EnsureDir(db, "a");
            Assert.Throws<QuotaExceededException>(() => FileStore.Put(db, "a/x.bin", new byte[10], null));

            // Genug Platz: nicht ausgeschoepft, Dateien gehen wieder; ohne Grenze nie
            FileStore.QuotaFor[id] = () => database + 1_000_000;
            Assert.False(UserStorage.Exceeded(db));
            FileStore.Put(db, "a/x.bin", new byte[10], null);
            FileStore.QuotaFor[id] = () => 0;
            Assert.False(UserStorage.Exceeded(db));
        }
        finally { FileStore.QuotaFor.TryRemove(id, out _); }
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
