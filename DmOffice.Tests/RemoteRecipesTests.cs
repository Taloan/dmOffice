using DmOffice.Core;
using DmOffice.Notes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>Rezepte fuer den Essensplan aus allen Notiz-Datenbanken, deren Passwort passt - nicht nur aus der geladenen.</summary>
public class RemoteRecipesTests
{
    private static (UserSpace Space, NotesDb Notes, NotesSync Sync) Setup()
    {
        var dir = Directory.CreateTempSubdirectory("dmrecipes");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Id = Guid.NewGuid().ToString("N"), Login = "t" }, db);
        space.UpdateSettings(s => { s.Notizi.Enabled = true; s.Notizi.E2eePassword = "geheim-123"; s.Notizi.DbFolder = "A"; });
        return (space, NotesDb.Of(space), new NotesSync(null!, NullLogger<NotesSync>.Instance));
    }

    private static void Switch(UserSpace space, string folder) =>
        space.UpdateSettings(s => { s.Notizi.DbFolder = folder; s.Notizi.SwitchDb(); });

    private static void Recipe(NotesDb notes, string title, params string[] ingredients)
    {
        var plain = title + "\nZutaten\n" + string.Join("\n", ingredients) + "\nZubereitung\nKochen.";
        var id = notes.CreateNote(null);
        notes.SaveNote(id, $"<div data-notizi=\"1\"><p>{title}</p></div>", plain);
    }

    [Fact]
    public async Task Recipes_of_other_databases_are_listed_and_give_their_ingredients()
    {
        var (space, notes, sync) = Setup();
        Recipe(notes, "Rezept aus A", "1 Ei");
        await sync.RunAsync(space, CancellationToken.None);          // A hat sein Rezept
        Switch(space, "B");
        await sync.RunAsync(space, CancellationToken.None);          // B ist neu und leer
        Recipe(notes, "Linsensuppe aus B", "200 g Linsen", "1 Zwiebel");
        await sync.RunAsync(space, CancellationToken.None);
        Switch(space, "A");
        await sync.RunAsync(space, CancellationToken.None);          // geladen ist jetzt A
        Assert.Equal(new[] { "Rezept aus A" }, notes.GetAllActive().Select(n => n.Plain.Split('\n')[0]).ToArray());

        var remote = await RemoteRecipes.AllAsync(space, null, CancellationToken.None);
        var entry = Assert.Single(remote);                            // nur B - A kommt aus dem lokalen Stand
        Assert.Equal("Linsensuppe aus B", entry.Title);
        Assert.Equal(0, entry.Id);
        Assert.False(string.IsNullOrEmpty(entry.Db));

        Assert.Single(await RemoteRecipes.AllAsync(space, "linsen", CancellationToken.None));
        Assert.Empty(await RemoteRecipes.AllAsync(space, "gibtsnicht", CancellationToken.None));

        var ing = await RemoteRecipes.IngredientsAsync(space, new HashSet<string> { entry.SyncId }, CancellationToken.None);
        Assert.Equal(new[] { "200 g Linsen", "1 Zwiebel" }, Assert.Single(ing).Items);
    }

    [Fact]
    public async Task Another_password_keeps_a_database_closed_and_without_notizi_nothing_is_read()
    {
        var (space, notes, sync) = Setup();
        Switch(space, "B");
        await sync.RunAsync(space, CancellationToken.None);
        Recipe(notes, "Geheimes Rezept", "1 Prise Salz");
        await sync.RunAsync(space, CancellationToken.None);
        Switch(space, "A");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Single(await RemoteRecipes.AllAsync(space, null, CancellationToken.None));

        // anderes Passwort: B oeffnet nicht mehr
        space.UpdateSettings(s => s.Notizi.E2eePassword = "falsch-456");
        Assert.Empty(await RemoteRecipes.AllAsync(space, null, CancellationToken.None));

        // ohne notizi gibt es nur die eine Datenbank
        space.UpdateSettings(s => { s.Notizi.E2eePassword = "geheim-123"; s.Notizi.Enabled = false; });
        Assert.Empty(await RemoteRecipes.AllAsync(space, null, CancellationToken.None));
    }
}
