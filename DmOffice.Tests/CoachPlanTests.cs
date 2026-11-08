using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Coach;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Coach Stufe 2: Ernährungsplan aus den eigenen Rezepten → Essensplan.</summary>
[Collection("server")]
public class CoachPlanTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CoachPlanTests(ChatFixture fx) => _fx = fx;

    private static CoachPlan.Candidate R(string id, string title, params string[] items) => new(id, title, items.ToList());

    [Fact]
    public void Filter_nach_Ernaehrungsform_Allergien_und_Abneigungen()
    {
        var bolo = R("1", "Spaghetti Bolognese", "500 g Spaghetti", "400 g Rinderhackfleisch", "1 Dose Tomaten");
        var curry = R("2", "Gemüsecurry", "1 Dose Kokosmilch", "2 Paprika", "200 g Reis");
        var omelett = R("3", "Omelett", "3 Eier", "50 g Käse");
        var lachs = R("4", "Lachs mit Brokkoli", "2 Lachsfilets", "1 Brokkoli", "Zitrone");
        var wild = R("5", "Wildreis-Pfanne", "200 g Wildreis", "1 Zucchini");

        var vegi = new CoachProfile { Diet = "vegetarian" };
        Assert.Equal("Fleisch oder Fisch", CoachPlan.Excluded(bolo, vegi));
        Assert.Equal("Fleisch oder Fisch", CoachPlan.Excluded(lachs, vegi));
        Assert.Null(CoachPlan.Excluded(omelett, vegi));
        Assert.Null(CoachPlan.Excluded(wild, vegi));   // Wildreis ist kein Wild

        var vegan = new CoachProfile { Diet = "vegan" };
        Assert.Equal("Tierisches", CoachPlan.Excluded(omelett, vegan));
        Assert.Null(CoachPlan.Excluded(curry, vegan));

        Assert.Equal("Allergen", CoachPlan.Excluded(omelett, new CoachProfile { Allergens = ["eggs"] }));
        Assert.Equal("Allergen", CoachPlan.Excluded(bolo, new CoachProfile { Allergens = ["gluten"] }));
        Assert.Null(CoachPlan.Excluded(curry, new CoachProfile { Allergens = ["eggs"] }));   // „Kokosmilch“ enthält kein Ei
        Assert.Equal("Laktose", CoachPlan.Excluded(omelett, new CoachProfile { Intolerances = ["lactose"] }));
        Assert.Equal("Abneigung", CoachPlan.Excluded(curry, new CoachProfile { Dislikes = ["Paprika"] }));
        Assert.Equal("Kohlenhydrate", CoachPlan.Excluded(curry, new CoachProfile { Diet = "keto" }));
        Assert.Null(CoachPlan.Excluded(lachs, new CoachProfile { Diet = "keto" }));

        // Fleisch nur im Gerichtnamen, Rezept ohne Zutaten: weder vegetarisch noch vegan
        Assert.Equal("Fleisch oder Fisch", CoachPlan.Excluded(R("6", "Pfefferpotthast", "500 g Zwiebeln"), vegi));
        Assert.Equal("Fleisch oder Fisch", CoachPlan.Excluded(R("7", "Matjestartar", "1 Apfel"), vegi));
        Assert.Equal("Fleisch oder Fisch", CoachPlan.Excluded(R("10", "Meeresfrüchte Salat", "1 Gurke"), vegi));
        Assert.Equal("Zutaten fehlen", CoachPlan.Excluded(R("8", "Peperoni Spieße"), vegan));
        Assert.DoesNotContain("vegan", CoachPlan.Diets(R("8", "Peperoni Spieße"), null, null));
        Assert.Null(CoachPlan.Excluded(R("9", "Omelett", "3 Eier"), vegi));   // „Omelett“ ist kein Mett
    }

    [Fact]
    public void Woche_ohne_Doppelte_mit_Resten_und_belegten_Mahlzeiten()
    {
        var pool = Enumerable.Range(1, 10).Select(i => R("r" + i, "Rezept " + i)).ToList();
        var from = new DateOnly(2026, 10, 26);
        var taken = new HashSet<string> { "2026-10-27|Abendessen" };
        var recent = new HashSet<string> { "r1", "r2" };
        var slots = CoachPlan.Suggest(pool, from, 7, ["Mittagessen", "Abendessen"], taken, recent, leftovers: true, seed: 7);

        Assert.DoesNotContain(slots, s => s.Date == "2026-10-27" && s.Meal == "Abendessen");
        // Reste: Mittag am Folgetag = Abendessen vom Vortag
        foreach (var dinner in slots.Where(s => s.Meal == "Abendessen"))
        {
            var next = DateOnly.Parse(dinner.Date).AddDays(1).ToString("yyyy-MM-dd");
            if (slots.FirstOrDefault(s => s.Date == next && s.Meal == "Mittagessen") is { } lunch)
                Assert.True(lunch.Leftover && lunch.Recipe == dinner.Recipe);
        }
        // Neu Gekochtes ohne Doppelte, kürzlich Gekochtes zuletzt
        var cooked = slots.Where(s => !s.Leftover).Select(s => s.Recipe).ToList();
        Assert.Equal(cooked.Count, cooked.Distinct().Count());
        Assert.DoesNotContain("r1", cooked.Take(8));

        // Ohne Reste und mit kleinem Vorrat: wiederholt erst, wenn alles einmal dran war
        var few = CoachPlan.Suggest(pool.Take(3).ToList(), from, 3, ["Abendessen"], new HashSet<string>(), new HashSet<string>(), false, 1);
        Assert.Equal(3, few.Select(s => s.Recipe).Distinct().Count());
        Assert.Empty(CoachPlan.Suggest([], from, 7, ["Abendessen"], new HashSet<string>(), new HashSet<string>(), true, 1));
    }

    [Fact]
    public async Task Vorschlag_aus_Rezeptnotizen_annehmen_in_den_Essensplan()
    {
        var space = _fx.Space("anna");
        var notes = NotesDb.Of(space);
        // Gemüsepfanne ohne „Kalorien je Portion“: passt zum Profil, wird aber nicht vorgeschlagen
        foreach (var (title, items, facts) in new[] { ("Linsensuppe", "200 g Linsen", "4 Portionen  ·  380 kcal je Portion"), ("Schweinebraten", "1 kg Schweinenacken", "650 kcal je Portion"),
            ("Pilzrisotto", "300 g Risottoreis", "Pro Portion ca. 520 kcal"), ("Gemüsepfanne", "2 Zucchini", "2 Portionen") })
        {
            var id = notes.CreateNote(null);
            notes.SaveNote(id, $"<div data-notizi=\"1\"><p>{title}</p><p>{facts}</p><h3>Zutaten</h3><ul><li>☐ {items}</li></ul></div>", $"{title}\r\n{facts}\r\nZutaten\r\n☐ {items}");
            notes.SetTags(id, [Recipes.Tag]);
        }
        var cal = PimStore.CreateCollection(space.Db, PimStore.Calendar, "essen-plan-test", "Essensplan Test", "#3a7", "VEVENT");
        CalendarKinds.Set(space.Db, cal.Id, CalendarKinds.Meal);
        var c = _fx.As("anna");
        await c.PutAsJsonAsync("/api/coach/profile", new
        {
            birth = "1980-06-15", sex = "f", heightCm = 170, weightKg = 70, pal = 1.4, goal = "keep", diet = "vegetarian", stepMode = "fixed", stepGoal = 7000,
            allergens = Array.Empty<string>(), intolerances = Array.Empty<string>(), dislikes = Array.Empty<string>(),
        });

        var r = await c.GetFromJsonAsync<JsonElement>("/api/coach/plan/suggest?from=2030-03-04&days=2&meals=Abendessen&leftovers=false&seed=3");
        Assert.True(r.GetProperty("excluded").GetProperty("Fleisch oder Fisch").GetInt32() >= 1);
        Assert.Equal(1, r.GetProperty("excluded").GetProperty(CoachPlan.NoKcal).GetInt32());
        Assert.Contains(r.GetProperty("calendars").EnumerateArray(), x => x.GetProperty("id").GetInt64() == cal.Id);
        var slots = r.GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(2, slots.Count);
        Assert.DoesNotContain(slots, s => s.GetProperty("title").GetString() is "Schweinebraten" or "Gemüsepfanne");

        // Rezeptliste: Kalorien je Portion laut Rezept, sonst aus den Zutaten (kcalGiven = false)
        var list = (await c.GetFromJsonAsync<JsonElement>("/api/coach/recipes")).EnumerateArray().ToList();
        var linsen = list.First(x => x.GetProperty("title").GetString() == "Linsensuppe");
        Assert.Equal(380, linsen.GetProperty("kcal").GetDouble());
        Assert.True(linsen.GetProperty("kcalGiven").GetBoolean());
        Assert.False(list.First(x => x.GetProperty("title").GetString() == "Gemüsepfanne").GetProperty("kcalGiven").GetBoolean());

        var res = await c.PostAsJsonAsync("/api/coach/plan/accept", new { calendar = cal.Id, slots });
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("made").GetInt32());
        var events = CalendarModel.Events(space, new DateTime(2030, 3, 3), new DateTime(2030, 3, 7), [cal.Id]);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => { Assert.Contains("Abendessen", e.Categories); Assert.False(string.IsNullOrEmpty(e.Recipe)); });

        // Jetzt belegt: kein neuer Vorschlag für diese Abende; anderer Kalender als Ziel wird abgelehnt
        r = await c.GetFromJsonAsync<JsonElement>("/api/coach/plan/suggest?from=2030-03-04&days=2&meals=Abendessen&leftovers=false");
        Assert.Equal(0, r.GetProperty("slots").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/coach/plan/accept", new { calendar = 999999, slots })).StatusCode);
    }
}
