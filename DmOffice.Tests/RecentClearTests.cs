using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>„Zuletzt verwendet“ leeren: geöffnete Dateien vergessen, geänderte erst nach dem Zeitpunkt wieder zeigen.</summary>
[Collection("server")]
public class RecentClearTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public RecentClearTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public void Verlauf_beginnt_nach_dem_Leeren_neu()
    {
        var db = _fx.Space("cleo").Db;
        var name = $"verlauf-{Guid.NewGuid():N}.txt";
        FileStore.Put(db, name, "alt"u8.ToArray(), "text/plain");
        Assert.Contains(FileStore.Recent(db), x => x.File.Path == name);
        FileStore.ClearRecent(db);
        var cleared = DateTime.UtcNow.AddSeconds(1);
        Assert.DoesNotContain(FileStore.Recent(db, since: cleared), x => x.File.Path == name);
        // Danach geändert: wieder da
        var later = DateTime.UtcNow.AddSeconds(-5);
        Assert.Contains(FileStore.Recent(db, since: later), x => x.File.Path == name);
    }
}
