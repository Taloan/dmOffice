using System.Text;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Tests;

public class FileCategoriesTests
{
    private static UserSpace NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmfcat");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        return new UserSpace(new UserRecord { Login = "t" }, db);
    }

    private static FileEntry Put(UserSpace space, string path, string text = "x")
    {
        if (FileStore.Parent(path) is { Length: > 0 } parent) FileStore.EnsureDir(space.Db, parent);
        FileStore.Put(space.Db, path, Encoding.UTF8.GetBytes(text), "text/plain");
        return FileStore.Get(space.Db, path)!;
    }

    private static List<string> In(UserSpace space, string cat) => FileCategories.FilesIn(space.Db, cat).Select(f => f.Path).OrderBy(p => p).ToList();

    private static List<FileCategories.Assigned> CatsOf(UserSpace space, string path) =>
        FileCategories.Of(space.Db, [FileStore.Get(space.Db, path)!.FileId]).Single().Value.Cats;

    [Fact]
    public void Baum_anlegen_verschieben_und_loeschen()
    {
        var space = NewSpace();
        var fin = FileCategories.Create(space.Db, "Finanzen", null);
        var steuer = FileCategories.Create(space.Db, "Steuer", fin.Id);
        var y = FileCategories.Create(space.Db, "2025", steuer.Id);
        Assert.Throws<ArgumentException>(() => FileCategories.Create(space.Db, "steuer", fin.Id));
        Assert.Throws<ArgumentException>(() => FileCategories.Update(space.Db, fin.Id, null, y.Id, null, null, null));
        Assert.Equal("Finanzen › Steuer › 2025", FileCategories.FullName(FileCategories.All(space.Db), y.Id));

        // Oberkategorie zeigt alles darunter
        FileCategories.Set(space, [Put(space, "a.txt")], [y.Id], null);
        FileCategories.Set(space, [Put(space, "b.txt")], [fin.Id], null);
        Assert.Equal(["a.txt", "b.txt"], In(space, fin.Id));
        Assert.Equal(["a.txt"], In(space, steuer.Id));

        // Löschen: Unterkategorie rückt hoch, ihre Dateien bleiben drin
        FileCategories.Delete(space.Db, steuer.Id);
        Assert.Equal(fin.Id, FileCategories.All(space.Db).Single(c => c.Id == y.Id).Parent);
        Assert.Equal(["a.txt", "b.txt"], In(space, fin.Id));

        // Vorschlag nur bei leerer Liste
        Assert.Equal(0, FileCategories.CreateSuggestion(space.Db));
        Assert.True(FileCategories.CreateSuggestion(NewSpace().Db) > 5);
    }

    [Fact]
    public void Zuordnung_bleibt_beim_Verschieben_und_Tags_sind_frei()
    {
        var space = NewSpace();
        var c = FileCategories.Create(space.Db, "Verträge", null);
        var f = Put(space, "vertrag.txt");
        FileCategories.Set(space, [f], [c.Id], null, ["Wichtig", "wichtig", "2024"]);
        FileStore.EnsureDir(space.Db, "Ablage");
        FileStore.MoveOrCopy(space.Db, "vertrag.txt", "Ablage/vertrag.txt", overwrite: false, copy: false);
        Assert.Equal(["Ablage/vertrag.txt"], In(space, c.Id));
        Assert.Equal(["Ablage/vertrag.txt"], FileCategories.FilesTagged(space.Db, "WICHTIG").Select(x => x.Path).ToList());
        Assert.Equal(2, FileCategories.Tags(space.Db).Count);
        FileCategories.Set(space, [FileStore.Get(space.Db, "Ablage/vertrag.txt")!], null, null, null, ["wichtig"]);
        Assert.Empty(FileCategories.FilesTagged(space.Db, "Wichtig"));
    }

    [Fact]
    public void Lernt_aus_Zuweisungen_und_ordnet_neue_Dateien_ein()
    {
        var space = NewSpace();
        var fin = FileCategories.Create(space.Db, "Finanzen", null);
        var steuer = FileCategories.Create(space.Db, "Steuer", fin.Id);
        var auto = FileCategories.Create(space.Db, "Fahrzeug", null);
        var manual = FileCategories.Create(space.Db, "Urlaub", null, learn: false);

        // Von Hand: drei Steuerbescheide, drei Fahrzeug-Unterlagen
        for (int i = 0; i < 3; i++)
        {
            FileCategories.Set(space, [Put(space, $"Post/steuerbescheid finanzamt {2020 + i}.txt")], [steuer.Id], null);
            FileCategories.Set(space, [Put(space, $"Auto/kfz versicherung werkstatt {i}.txt")], [auto.Id], null);
        }
        // Was vor dem Lernen da war, ist verarbeitet
        FileCategories.ProcessQueue(space);
        FileCategories.Set(space, [FileStore.Get(space.Db, "Post/steuerbescheid finanzamt 2020.txt")!], [steuer.Id], null);

        // Neu: nach Namen eindeutig Steuer (nur die genaueste Kategorie, nicht zusätzlich Finanzen)
        Put(space, "Scans/steuerbescheid finanzamt 2024.txt");
        // Neu im Ordner, aus dem schon zwei Dateien zu Fahrzeug gehören
        Put(space, "Auto/tüv.txt");
        // Nichts Bekanntes
        Put(space, "Rezepte/kuchen.txt");
        Assert.Equal(3, FileCategories.ProcessQueue(space));
        Assert.Equal([new FileCategories.Assigned(steuer.Id, true)], CatsOf(space, "Scans/steuerbescheid finanzamt 2024.txt"));
        Assert.Equal([new FileCategories.Assigned(auto.Id, true)], CatsOf(space, "Auto/tüv.txt"));
        Assert.Empty(CatsOf(space, "Rezepte/kuchen.txt"));
        Assert.DoesNotContain(manual.Id, CatsOf(space, "Scans/steuerbescheid finanzamt 2024.txt").Select(a => a.Id));

        // Bestätigen: nicht mehr automatisch; Entfernen: kommt beim Wiederherstellen nicht von selbst wieder
        FileCategories.Set(space, [FileStore.Get(space.Db, "Scans/steuerbescheid finanzamt 2024.txt")!], [steuer.Id], null);
        Assert.False(CatsOf(space, "Scans/steuerbescheid finanzamt 2024.txt").Single().Auto);
        FileCategories.Set(space, [FileStore.Get(space.Db, "Auto/tüv.txt")!], null, [auto.Id]);
        Assert.Empty(CatsOf(space, "Auto/tüv.txt"));
        Assert.Empty(FileCategories.Decide(space.Db, FileStore.Get(space.Db, "Auto/tüv.txt")!));
    }

    [Fact]
    public void Vorhandene_Dateien_nachtraeglich_einordnen()
    {
        var space = NewSpace();
        // Bestand von vorher: ohne lernende Kategorie kommt nichts in die Warteschlange
        Put(space, "Alt/steuerbescheid finanzamt 2019.txt");
        Put(space, "Alt/kuchen.txt");
        Assert.Equal(0, FileCategories.QueueExisting(space));

        var steuer = FileCategories.Create(space.Db, "Steuer", null);
        var auto = FileCategories.Create(space.Db, "Fahrzeug", null);
        // Zwei Kategorien: was zur einen gehört, ist für die andere „der Rest“ - erst damit entscheidet das Gelernte
        for (int i = 0; i < 3; i++)
        {
            FileCategories.Set(space, [Put(space, $"Post/steuerbescheid finanzamt {2020 + i}.txt")], [steuer.Id], null);
            FileCategories.Set(space, [Put(space, $"Auto/kfz versicherung werkstatt {i}.txt")], [auto.Id], null);
        }
        FileCategories.ProcessQueue(space);
        FileCategories.Set(space, [FileStore.Get(space.Db, "Post/steuerbescheid finanzamt 2020.txt")!], [steuer.Id], null);
        // Zuordnungsassistent: je Ordner gezählt, Probelauf schreibt nichts, beide Kategorien haben genug gelernt
        Assert.Contains(FileCategories.UncategorizedByFolder(space), f => f.Folder == "Alt" && f.Count == 2);
        var (files, ready, _) = FileCategories.ExistingPreview(space);
        Assert.Equal(2, files);
        Assert.Contains("Steuer", ready);
        var sample = FileCategories.Sample(space, ["Alt"]);
        Assert.Contains(sample, x => x.Path == "Alt/steuerbescheid finanzamt 2019.txt" && x.Cats.SequenceEqual(["Steuer"]));
        Assert.Contains(sample, x => x.Path == "Alt/kuchen.txt" && x.Cats.Count == 0);
        Assert.Empty(CatsOf(space, "Alt/steuerbescheid finanzamt 2019.txt"));
        // Nur gewählte Ordner
        Assert.Equal(0, FileCategories.QueueExisting(space, ["Post"]));
        // Nur Dateien ohne Kategorie: die zwei alten
        Assert.Equal(2, FileCategories.QueueExisting(space, ["Alt"]));
        FileCategories.ProcessQueue(space);
        Assert.Equal([new FileCategories.Assigned(steuer.Id, true)], CatsOf(space, "Alt/steuerbescheid finanzamt 2019.txt"));
        Assert.Empty(CatsOf(space, "Alt/kuchen.txt"));
        // Unbestätigte entfernen: die automatische geht, die von Hand zugeordneten bleiben
        Assert.Equal(1, FileCategories.RemoveUnconfirmed(space.Db, null));
        Assert.Empty(CatsOf(space, "Alt/steuerbescheid finanzamt 2019.txt"));
        Assert.NotEmpty(CatsOf(space, "Post/steuerbescheid finanzamt 2021.txt"));
        // Englischer Vorschlag
        var en = NewSpace();
        Assert.True(FileCategories.CreateSuggestion(en.Db, "en") > 0);
        Assert.Contains(FileCategories.All(en.Db), c => c.Name == "Finances");
    }
}
