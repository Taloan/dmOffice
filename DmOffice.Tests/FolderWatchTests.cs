using System.Collections.Concurrent;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Dms;
using DmOffice.Gallery;

namespace DmOffice.Tests;

/// <summary>Sofort verarbeiten: fertig gespeicherte Dateien in überwachten Ordnern der eigenen Ablage stoßen das Profil an, entprellt.</summary>
public class FolderWatchTests
{
    private static UserDb NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("folderwatch");
        return new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "u" + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>Ein Dienst nur für diesen Test: überwacht „Eingang“, zählt die Anstöße.</summary>
    private static (string Name, ConcurrentQueue<string> Fired) TestWatcher(string folder = "Eingang")
    {
        var name = "test-" + Guid.NewGuid().ToString("N")[..8];
        var fired = new ConcurrentQueue<string>();
        FolderWatch.Watch(new(name, TimeSpan.FromMilliseconds(400), _ => [("p1", folder)], (_, key) => fired.Enqueue(key)));
        return (name, fired);
    }

    private static async Task<bool> WaitFor(Func<bool> done, int ms = 8000)
    {
        for (var i = 0; i < ms / 50; i++) { if (done()) return true; await Task.Delay(50); }
        return done();
    }

    [Fact]
    public async Task Neue_Datei_im_Eingangsordner_stoesst_das_Profil_an()
    {
        var db = NewDb();
        var (_, fired) = TestWatcher();
        FileStore.EnsureDir(db, "Eingang/Scanner");
        FileStore.Put(db, "Eingang/Scanner/scan.pdf", "%PDF"u8.ToArray(), "application/pdf");
        Assert.True(await WaitFor(() => !fired.IsEmpty));
        Assert.Equal(["p1"], fired);
    }

    [Fact]
    public async Task Stapel_ergibt_einen_Anstoss()
    {
        var db = NewDb();
        var (_, fired) = TestWatcher();
        FileStore.EnsureDir(db, "Eingang");
        for (var i = 0; i < 50; i++) await FileStore.PutAsync(db, $"Eingang/f{i}.txt", new MemoryStream(Encoding.UTF8.GetBytes("x" + i)), "text/plain");
        Assert.True(await WaitFor(() => !fired.IsEmpty));
        await Task.Delay(800);
        Assert.Single(fired);
    }

    [Fact]
    public async Task Fremder_Ordner_und_eigene_Schreibvorgaenge_loesen_nichts_aus()
    {
        var db = NewDb();
        var (name, fired) = TestWatcher();
        FileStore.EnsureDir(db, "Eingang");
        FileStore.EnsureDir(db, "Eingangskorb");
        FileStore.EnsureDir(db, "Andere");
        FileStore.Put(db, "Andere/a.txt", "a"u8.ToArray(), "text/plain");
        FileStore.Put(db, "Eingangskorb/b.txt", "b"u8.ToArray(), "text/plain");
        // Was der Dienst selbst in den Ordner schreibt, stößt ihn nicht wieder an
        using (FolderWatch.As(name)) FileStore.Put(db, "Eingang/c.txt", "c"u8.ToArray(), "text/plain");
        await Task.Delay(1200);
        Assert.Empty(fired);
    }

    [Fact]
    public async Task Verschieben_und_Kopieren_in_den_Ordner_stossen_an()
    {
        var db = NewDb();
        var (_, fired) = TestWatcher();
        FileStore.EnsureDir(db, "Eingang");
        FileStore.Put(db, "a.txt", "a"u8.ToArray(), "text/plain");
        FileStore.MoveOrCopy(db, "a.txt", "Eingang/a.txt", overwrite: false, copy: false);
        Assert.True(await WaitFor(() => fired.Count == 1));
        FileStore.MoveOrCopy(db, "Eingang/a.txt", "b.txt", overwrite: false, copy: true);
        FileStore.MoveOrCopy(db, "b.txt", "Eingang/b.txt", overwrite: false, copy: true);
        Assert.True(await WaitFor(() => fired.Count == 2));
    }

    [Fact]
    public async Task DMS_Quelle_und_Importprofil_der_eigenen_Ablage_werden_angestossen_externe_nicht()
    {
        var db = NewDb();
        var fired = new ConcurrentQueue<(string, string)>();
        void Seen(string watcher, string user, string key) { if (user == db.UserId) fired.Enqueue((watcher, key)); }
        var quiet = ImportProfileJob.Quiet;
        FolderWatch.Fired += Seen;
        try
        {
            ImportProfileJob.Quiet = TimeSpan.FromMilliseconds(400);
            ImportProfileJob.WatchFolders();
            DmsJob.WatchFolders();
            FileStore.EnsureDir(db, "Scanner");
            FileStore.EnsureDir(db, "Upload");
            var dms = new DmsSource { Name = "Scanner", Folder = "Scanner" };
            var ext = new DmsSource { Name = "NAS", Mount = "nas", Folder = "Upload" };
            db.SetJson(DmsSources.Key, new List<DmsSource> { dms, ext });
            var photos = new ImportProfile { Name = "Fuji", Sources = [new("", "Upload")], Target = "Bilder" };
            var nas = new ImportProfile { Name = "NAS", Sources = [new("nas", "Scanner")], Target = "Bilder" };
            ImportProfiles.Save(db, [photos, nas]);
            FileStore.Put(db, "Scanner/scan.pdf", "%PDF"u8.ToArray(), "application/pdf");
            FileStore.Put(db, "Upload/DSCF0001.JPG", ImportProfilesTests.Jpeg(), "image/jpeg");
            Assert.True(await WaitFor(() => fired.Contains((ImportProfileJob.Watch, photos.Id)), 15000));
            Assert.True(await WaitFor(() => fired.Contains((DmsJob.Watch, dms.Id)), 15000));
            await Task.Delay(500);
            Assert.Equal(2, fired.Count);
        }
        finally
        {
            FolderWatch.Fired -= Seen;
            ImportProfileJob.Quiet = quiet;
            ImportProfileJob.WatchFolders();
        }
    }
}
