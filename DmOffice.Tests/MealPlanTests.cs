using DmOffice.Core;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Essensplan: Rezepte erkennen (Vorlage von notizi) und Rezeptverweis im Termin.</summary>
public class MealPlanTests
{
    [Fact]
    public void Rezepte_erkennt_man_an_der_Gestalt_der_Vorlage_nicht_am_Ordner()
    {
        Assert.True(Recipes.IsRecipe("Lasagne\n\nZutaten\n300 g Platten\nZubereitung\n1. Backen"));
        Assert.True(Recipes.IsRecipe("Allgäu Buffet\n \n￼ Arbeitszeit ￼ Koch-/Backzeit ￼ Sammlung stack\nZutaten\nRezept von anylist.com"));
        Assert.True(Recipes.IsRecipe("Paella\n 4 Portionen · Arbeitszeit 30 Min. · Koch-/Backzeit 45 Min."));
        Assert.True(Recipes.IsRecipe("Suppe\nRezept von chefkoch.de"));
        // Gewoehnliche Notizen, auch wenn das Wort vorkommt
        Assert.False(Recipes.IsRecipe("Einkauf\nZutaten fuer Samstag besorgen\nMilch"));
    }

    [Fact]
    public void Zutaten_ohne_Kaestchen_aus_dem_Abschnitt_unter_Zutaten()
    {
        Assert.Equal(new[] { "300 g Platten", "500 g Hackfleisch", "1 Dose Tomaten" },
            Recipes.IngredientsFromText("Lasagne\n\nZutaten\n- 300 g Platten\n• 500 g Hackfleisch\n\n1 Dose Tomaten\nZubereitung\n1. Backen"));
        // Wie bei "Allgäu Buffet": unter Zutaten steht nichts
        Assert.Empty(Recipes.IngredientsFromText("Allgäu Buffet\n \n\uFFFC Arbeitszeit \uFFFC Koch-/Backzeit\nZutaten\nRezept von anylist.com"));
        Assert.Empty(Recipes.IngredientsFromText("Ohne Abschnitt\n200 g Mehl"));
        Assert.False(Recipes.IsRecipe("Kaffeespezialitaeten im Vergleich"));
    }

    [Fact]
    public void Tag_recipe_gilt_ohne_Beachtung_der_Schreibweise_und_ohne_Gestalt()
    {
        Assert.True(Recipes.IsRecipe(new[] { "Hauptspeisen", "Recipe" }, "Nur ein Satz"));
        Assert.True(Recipes.IsRecipe(new[] { "recipe" }, ""));
        Assert.False(Recipes.IsRecipe(new[] { "Arbeit" }, "Nur ein Satz"));
        Assert.False(Recipes.IsRecipe((IEnumerable<string>?)null, "Nur ein Satz"));
        // Altbestand ohne Tag: weiter an der Gestalt
        Assert.True(Recipes.IsRecipe(Array.Empty<string>(), "Lasagne\nZutaten\n300 g"));
    }

    [Fact]
    public void Tags_kommen_mit_dem_Abgleich_und_bleiben_bei_Geraeten_ohne_Tags()
    {
        var dir = Directory.CreateTempSubdirectory("dmtags");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var notes = NotesDb.Of(new UserSpace(new UserRecord { Login = "t" }, db));
        var item = new Notizi.Sync.NoteItem
        {
            Id = "n1", Rev = 1, Title = "Lasagne", Html = "<p>Lasagne</p>", Plain = "Lasagne", Created = DateTime.UtcNow, Modified = DateTime.UtcNow,
            Tags = new() { "recipe", "Hauptspeisen" },
        };
        Assert.True(notes.ApplyNote(item, null!));
        Assert.Equal(new[] { "recipe", "Hauptspeisen" }, notes.ExportNote("n1", null!).Tags);
        Assert.Contains(Recipes.All(notes, Array.Empty<NoteFolder>()), r => r.Title == "Lasagne");

        // Element ohne Feld "tags" (Geraet vor den Tags): vorhandene Tags bleiben
        item.Rev = 2; item.Tags = null;
        notes.ApplyNote(item, null!);
        Assert.Equal(new[] { "recipe", "Hauptspeisen" }, notes.ExportNote("n1", null!).Tags);
        // Leere Liste heisst "keine Tags"
        item.Rev = 3; item.Tags = new();
        notes.ApplyNote(item, null!);
        Assert.Empty(notes.ExportNote("n1", null!).Tags!);

        // Eine Notiz, die nie Tags hatte, schreibt das Feld auch nicht
        var old = new Notizi.Sync.NoteItem { Id = "n2", Rev = 1, Title = "Alt", Html = "<p>Alt</p>", Plain = "Alt", Created = DateTime.UtcNow, Modified = DateTime.UtcNow };
        notes.ApplyNote(old, null!);
        Assert.Null(notes.ExportNote("n2", null!).Tags);
    }

    [Fact]
    public void Zutaten_sind_die_Kaestchen_der_Rezeptnotiz()
    {
        var note = new NoteRow
        {
            Id = 1, SyncId = "r1", Title = "Pasta", Plain = "Pasta",
            Html = "<div data-notizi=\"1\"><p>Pasta</p><h3>Zutaten</h3><ul><li>☐ 300 g Nudeln</li><li>☑ 2 Zwiebeln</li></ul><h3>Zubereitung</h3><ol><li>Kochen.</li></ol></div>",
        };
        Assert.Equal(new[] { "300 g Nudeln", "2 Zwiebeln" }, Recipes.Ingredients(note));
        Assert.Empty(Recipes.Ingredients(new NoteRow { Id = 2, SyncId = "r2", Html = "<div data-notizi=\"1\"><p>Nur Text</p></div>" }));
    }

    [Fact]
    public void Ordnerpfade_werden_von_der_Wurzel_gebildet()
    {
        var folders = new[]
        {
            new NoteFolder { SyncId = "r", Name = "80 Rezepte" },
            new NoteFolder { SyncId = "h", Parent = "r", Name = "40 Hauptspeisen" },
        };
        Assert.Equal("80 Rezepte / 40 Hauptspeisen", Recipes.FolderPaths(folders)["h"]);
    }

    [Fact]
    public void Termin_merkt_sich_das_Rezept_und_behaelt_es_beim_Aendern()
    {
        var dir = Directory.CreateTempSubdirectory("dmmeal");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        PimStore.EnsureDefaults(db);
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        var cal = PimStore.Collections(db, PimStore.Calendar).First();

        string Input(string? uri, string title, string? recipe, string? etag = null) =>
            CalendarModel.SaveEvent(space, new EventInput
            {
                Calendar = cal.Id, Uri = uri, Title = title, Start = "2027-10-05T17:00:00Z", End = "2027-10-05T18:00:00Z",
                Categories = new() { "Abendessen" }, Recipe = recipe, Etag = etag,
            });
        List<EventDto> Read() => CalendarModel.Events(space, new DateTime(2027, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 11, 1, 0, 0, 0, DateTimeKind.Utc), new[] { cal.Id });

        var id = Input(null, "Lasagne", "abc123");
        var uri = id[(id.IndexOf('/') + 1)..];
        var e = Assert.Single(Read());
        Assert.Equal("abc123", e.Recipe);
        Assert.Contains("Abendessen", e.Categories);

        // Ohne Angabe bleibt das Rezept, "" nimmt es weg (Freitext)
        Input(uri, "Lasagne (gross)", null);
        Assert.Equal("abc123", Assert.Single(Read()).Recipe);
        Input(uri, "Reste", "");
        Assert.Null(Assert.Single(Read()).Recipe);
    }
}
