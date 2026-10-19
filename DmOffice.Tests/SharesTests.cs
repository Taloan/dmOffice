using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Geteilte Listen: wer was sieht, Austreten, Aufraeumen.</summary>
public class SharesTests
{
    private static ShareStore NewStore(out string dir)
    {
        dir = Directory.CreateTempSubdirectory("dmshares").FullName;
        return new ShareStore(new DataPaths(dir));
    }

    [Fact]
    public void Teilen_austreten_und_aufraeumen()
    {
        var store = NewStore(out var dir);
        // Der Besitzer selbst wird nicht Mitglied
        store.Set("anna", 5, new[] { "ben", "anna", "cleo" });
        var s = store.Of("anna", 5)!;
        Assert.Equal(new[] { "ben", "cleo" }, s.Members);
        store.Set("anna", 7, new[] { "ben" });
        Assert.NotEqual(s.Id, store.Of("anna", 7)!.Id);

        // Bleibt nach Neustart erhalten
        Assert.Equal(2, new ShareStore(new DataPaths(dir)).All().Count);

        store.Leave(s.Id, "ben");
        Assert.Equal(new[] { "cleo" }, store.ById(s.Id)!.Members);
        store.Leave(s.Id, "cleo");
        Assert.Null(store.ById(s.Id));   // letztes Mitglied weg: Freigabe weg

        // Benutzer geloescht: als Mitglied raus, eigene Freigaben weg
        store.Set("ben", 1, new[] { "anna" });
        store.Forget("ben");
        Assert.Null(store.Of("ben", 1));
        Assert.Null(store.Of("anna", 7));   // ben war einziges Mitglied

        // Leere Mitgliederliste hebt die Freigabe auf
        store.Set("anna", 9, new[] { "cleo" });
        store.Set("anna", 9, Array.Empty<string>());
        Assert.Empty(store.All());
    }
}
