using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>Kopieren zwischen zwei Ablagen bricht ab: am Ziel bleibt nichts halb liegen, die Quelle bleibt.</summary>
[Collection("server")]
public class KopierAbbruchTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public KopierAbbruchTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Abbruch_entfernt_die_Teilkopie()
    {
        var ben = _fx.Space("ben");
        var anna = _fx.Space("anna");
        var dir = "Abbruch-" + Guid.NewGuid().ToString("N")[..6];
        FileStore.EnsureDir(ben.Db, dir);
        FileStore.Put(ben.Db, dir + "/a.txt", "erste"u8.ToArray(), "text/plain");
        FileStore.Put(ben.Db, dir + "/z.txt", "zweite"u8.ToArray(), "text/plain");
        // Zweite Datei unlesbar: das Kopieren scheitert mittendrin
        ben.Db.Exec("UPDATE files SET blob = 'gibt-es-nicht', data = NULL WHERE path = $p", ("$p", dir + "/z.txt"));

        await Assert.ThrowsAnyAsync<Exception>(() => SharedFiles.CopyAcross(new FileLoc(ben, dir, null), new FileLoc(anna, dir, null), false));
        Assert.Null(FileStore.Get(anna.Db, dir));
        Assert.Null(FileStore.Get(anna.Db, dir + "/a.txt"));
        Assert.NotNull(FileStore.Get(ben.Db, dir + "/a.txt"));
    }
}
