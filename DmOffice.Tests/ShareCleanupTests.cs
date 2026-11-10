using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Benutzer geloescht: Freigaben aller Arten sind bei den anderen weg, ihre eigenen bleiben.</summary>
public class ShareCleanupTests
{
    private static DataPaths Paths() => new(Directory.CreateTempSubdirectory("dmcleanup").FullName);

    [Fact]
    public void Deleted_owner_takes_calendar_notes_files_and_links_with_him()
    {
        var paths = Paths();
        var lists = new ShareStore(paths);
        var folders = new FolderShareStore(paths);
        var links = new LinkIndex(paths);
        // Anna teilt mit Ben und Cleo: Kalender, Notizordner, Dateiordner und einen Link; Ben teilt etwas mit Cleo
        lists.Set("anna", 1, new[] { "ben", "cleo" });
        lists.SetFolder("anna", "rezepte", new[] { "ben" });
        folders.Set("anna", 10, new[] { "ben", "cleo" });
        links.Add("anna", "l1", "token-anna");
        lists.Set("ben", 2, new[] { "cleo" });
        folders.Set("ben", 20, new[] { "cleo" });
        links.Add("ben", "l2", "token-ben");

        lists.Forget("anna"); folders.Forget("anna"); links.Forget("anna");

        // bei Ben und Cleo ist nichts von Anna mehr zu sehen - auch nach einem Neustart
        var lists2 = new ShareStore(paths); var folders2 = new FolderShareStore(paths); var links2 = new LinkIndex(paths);
        Assert.DoesNotContain(lists2.All(), s => s.Owner == "anna");
        Assert.DoesNotContain(folders2.All(), s => s.Owner == "anna");
        Assert.Null(links2.Find("token-anna"));
        // Bens eigene Freigaben bleiben
        Assert.NotNull(lists2.Of("ben", 2));
        Assert.NotNull(folders2.Of("ben", 20));
        Assert.NotNull(links2.Find("token-ben"));
    }

    [Fact]
    public void Deleted_recipient_disappears_from_the_owners_shares()
    {
        var paths = Paths();
        var lists = new ShareStore(paths);
        var folders = new FolderShareStore(paths);
        lists.Set("anna", 1, new[] { "ben", "cleo" });
        lists.SetFolder("anna", "rezepte", new[] { "ben" }, new[] { "ben" });
        folders.Set("anna", 10, new[] { "ben", "cleo" }, new[] { "ben" });

        lists.Forget("ben");
        folders.Forget("ben");

        Assert.Equal(new[] { "cleo" }, lists.Of("anna", 1)!.Members);
        Assert.DoesNotContain(lists.All(), s => s.Members.Contains("ben") || s.ReadOnly.Contains("ben"));
        Assert.Null(lists.All().FirstOrDefault(s => s.Folder == "rezepte"));   // Ben war einziges Mitglied
        Assert.Equal(new[] { "cleo" }, folders.Of("anna", 10)!.Members);
        Assert.Empty(folders.Of("anna", 10)!.ReadOnly);
    }
}
