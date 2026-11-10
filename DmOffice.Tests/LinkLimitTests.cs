using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>Freigabelinks mit Download-Grenze: nach der erlaubten Zahl Abrufe ist Schluss, auch bei gleichzeitigen Abrufen.</summary>
public class LinkLimitTests
{
    [Fact]
    public void Grenze_wird_eingehalten_und_der_Link_laeuft_ab()
    {
        var dir = Directory.CreateTempSubdirectory("dmlink");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        FileStore.Put(db, "a.txt", "hallo"u8.ToArray(), "text/plain");
        var f = FileStore.Get(db, "a.txt")!;
        var link = Links.Create(db, f.FileId, "read", null, null, maxDownloads: 3);
        Assert.Equal(3, link.MaxDownloads);

        var ok = Enumerable.Range(0, 10).AsParallel().Count(_ => Links.Take(db, link.Id));
        Assert.Equal(3, ok);
        Assert.True(Links.Get(db, link.Id)!.UsedUp);

        // 0 = unbegrenzt
        Links.Update(db, link.Id, "read", null, null, maxDownloads: 0);
        Assert.Null(Links.Get(db, link.Id)!.MaxDownloads);
        Assert.False(Links.Get(db, link.Id)!.UsedUp);
        Assert.True(Links.Take(db, link.Id));
    }
}
