using System.Globalization;
using DmOffice.Core;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Coach;

/// <summary>
/// Ernährungsplan → Essensplan (Coach Stufe 2, docs/dmcoach-konzept.md): schlägt für eine Woche Rezepte aus den eigenen
/// Rezept-Notizen vor, die zu Ernährungsform, Allergien, Unverträglichkeiten und Abneigungen passen; kein Gericht zweimal pro
/// Woche, kürzlich Gekochtes zuletzt, auf Wunsch Reste (Abendessen → Mittag am nächsten Tag). Angenommen landen die Vorschläge
/// als gewöhnliche Termine im Essensplan-Kalender (Kategorie = Mahlzeit, X-DM-RECIPE = Rezept), „Zutaten auf die Einkaufsliste“
/// gibt es dort schon. Allergene erkennt er an Wörtern in Titel und Zutaten - ein Hinweis, keine Garantie.
/// </summary>
public static class CoachPlan
{
    /// <param name="Kcal">Kalorien je Portion laut Rezept (Basisrezepte: berechnet); ohne Angabe schlägt der Plan das Rezept nicht vor.</param>
    public sealed record Candidate(string SyncId, string Title, List<string> Ingredients, double? Kcal = null);

    /// <summary>Grund, wenn ein Rezept keine Angabe „Kalorien je Portion“ hat - ohne sie lässt sich nicht sagen, wie es in den Tag passt.</summary>
    public const string NoKcal = "Kalorien fehlen";
    public sealed record Slot(string Date, string Meal, string? Recipe, string Title, bool Leftover = false);

    /// <summary>Mahlzeiten wie im Essensplan (der Schlüssel steht als Kategorie im Termin) mit Uhrzeit.</summary>
    public static readonly (string Key, int Hour)[] Meals = [("Frühstück", 8), ("Mittagessen", 12), ("Abendessen", 18)];

    // Wortstämme je Allergen (EU-Hauptallergene) - kurze Wörter nur als ganzes Wort, längere auch in Zusammensetzungen
    private static readonly Dictionary<string, string[]> AllergenWords = new()
    {
        ["gluten"] = ["weizen", "mehl", "dinkel", "roggen", "gerste", "hafer", "nudel", "spaghetti", "pasta", "lasagne", "brot", "brötchen", "semmel", "paniermehl", "semmelbrösel", "couscous", "bulgur", "grieß", "seitan", "teig", "tortilla", "wrap", "pizza", "sojasauce"],
        ["crustaceans"] = ["garnele", "shrimp", "krabbe", "hummer", "languste", "krebs", "scampi", "gamba", "meeresfrücht", "frutti di mare"],
        ["eggs"] = ["ei", "eier", "eigelb", "eiklar", "mayonnaise", "mayo"],
        ["fish"] = ["fisch", "lachs", "thunfisch", "forelle", "kabeljau", "sardelle", "sardine", "hering", "dorsch", "seelachs", "makrele", "zander", "anchovis", "matjes", "scholle", "rotbarsch", "heilbutt", "pangasius", "seehecht", "karpfen", "aal", "sprotte"],
        ["peanuts"] = ["erdnuss"],
        ["soy"] = ["soja", "tofu", "edamame", "miso", "tempeh"],
        ["milk"] = ["milch", "butter", "sahne", "käse", "joghurt", "jogurt", "quark", "schmand", "crème fraîche", "creme fraiche", "mozzarella", "parmesan", "frischkäse", "rahm", "feta", "ricotta", "mascarpone", "molke", "gouda", "emmentaler", "burrata"],
        ["nuts"] = ["mandel", "haselnuss", "walnuss", "cashew", "pistazie", "pekannuss", "macadamia", "paranuss", "nüsse"],
        ["celery"] = ["sellerie"],
        ["mustard"] = ["senf"],
        ["sesame"] = ["sesam", "tahin", "tahini"],
        ["sulphites"] = ["wein", "sekt", "essig"],
        ["lupin"] = ["lupine"],
        ["molluscs"] = ["muschel", "tintenfisch", "calamari", "oktopus", "schnecke", "sepia"],
    };

    private static readonly string[] MeatWords = ["fleisch", "hack", "rind", "schwein", "huhn", "hähnchen", "hühnchen", "pute", "puten", "speck", "schinken", "salami",
        "wurst", "würstchen", "lamm", "kalb", "ente", "gans", "bacon", "chorizo", "gelatine", "reh", "hirsch", "leber", "steak", "schnitzel", "filet", "kotelett", "brühwürfel fleisch",
        // Gerichte, die Fleisch im Namen tragen, ohne es zu nennen
        "potthast", "gulasch", "braten", "roulade", "frikadelle", "bulette", "kassler", "leberkäse", "mett", "döner", "gyros", "kebab", "bolognese", "carbonara", "geschnetzeltes", "cordon bleu"];
    private static readonly string[] AnimalExtra = ["honig"];
    // Pflanzliches mit „Milch“/„Butter“ im Namen: keine Milch (für Nüsse und Soja zählen sie weiter als Allergen)
    private static readonly string[] PlantMilk = ["kokosmilch", "hafermilch", "mandelmilch", "sojamilch", "reismilch", "erdnussbutter", "kakaobutter",
        "kokosjoghurt", "sojajoghurt", "hafersahne", "sojasahne", "kokossahne", "veganer käse", "vegane butter"];
    private static readonly string[] CarbWords = ["nudel", "spaghetti", "pasta", "reis", "kartoffel", "brot", "brötchen", "mehl", "zucker", "polenta", "couscous", "bulgur", "gnocchi", "lasagne", "pizza"];

    public sealed record Warning(string Kind, string Key, string Label, List<string> Found, bool Known);

    private static readonly Dictionary<string, string> AllergenLabel = new()
    {
        ["gluten"] = "Gluten", ["crustaceans"] = "Krebstiere", ["eggs"] = "Eier", ["fish"] = "Fisch", ["peanuts"] = "Erdnüsse", ["soy"] = "Soja",
        ["milk"] = "Milch", ["nuts"] = "Schalenfrüchte", ["celery"] = "Sellerie", ["mustard"] = "Senf", ["sesame"] = "Sesam",
        ["sulphites"] = "Sulfite", ["lupin"] = "Lupinen", ["molluscs"] = "Weichtiere",
    };
    private static readonly string[] FructoseWords = ["fruktose", "fructose", "fruchtzucker", "invertzucker", "glukose-fruktose", "glucose-fructose", "honig", "agave", "sorbit", "apfelsaft", "birnensaft"];
    private static readonly string[] HistamineWords = ["rotwein", "sauerkraut", "salami", "parmesan", "thunfisch", "sardelle", "makrele", "hefeextrakt", "essig", "rohwurst", "emmentaler", "gouda", "camembert"];

    /// <summary>
    /// Passt ein Lebensmittel zu Allergien und Unverträglichkeiten im Profil? Known: von Open Food Facts als Allergen
    /// angegeben; sonst in Name und Zutaten gefundene Wörter (ein Hinweis, keine Garantie).
    /// </summary>
    public static List<Warning> Check(string name, string? ingredients, IReadOnlyCollection<string> offAllergens, CoachProfile p)
    {
        var text = (name + "\n" + ingredients).ToLowerInvariant();
        var words = text.Split([' ', '\n', ',', ';', '(', ')', '.', ':', '/', '-', '–', '%', '*'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        // Ohne Umlaute verglichen: „Haselnüsse“ enthält „haselnuss“
        var folded = Bls.Fold(text);
        List<string> Found(IEnumerable<string> list) => list.Where(w => w.Length <= 3 ? words.Contains(w) : folded.Contains(Bls.Fold(w))).Distinct().Take(6).ToList();
        var dairy = PlantMilk.Aggregate(text, (t, w) => t.Replace(w, " "));
        List<string> Milk() => AllergenWords["milk"].Where(dairy.Contains).Distinct().Take(6).ToList();
        var list = new List<Warning>();
        foreach (var a in p.Allergens)
        {
            var known = offAllergens.Contains(a);
            var found = a == "milk" ? Milk() : AllergenWords.TryGetValue(a, out var w) ? Found(w) : [];
            if (known || found.Count > 0) list.Add(new Warning("allergen", a, AllergenLabel.GetValueOrDefault(a, a), found, known));
        }
        if (p.Intolerances.Contains("gluten") && !text.Contains("glutenfrei") && (offAllergens.Contains("gluten") || Found(AllergenWords["gluten"]).Count > 0))
            list.Add(new Warning("intolerance", "gluten", "Gluten", Found(AllergenWords["gluten"]), offAllergens.Contains("gluten")));
        if (p.Intolerances.Contains("lactose") && !text.Contains("laktosefrei") && (offAllergens.Contains("milk") || Milk().Count > 0))
            list.Add(new Warning("intolerance", "lactose", "Laktose", Milk(), offAllergens.Contains("milk")));
        if (p.Intolerances.Contains("fructose") && Found(FructoseWords) is { Count: > 0 } fr) list.Add(new Warning("intolerance", "fructose", "Fruktose", fr, false));
        if (p.Intolerances.Contains("histamine") && Found(HistamineWords) is { Count: > 0 } hi) list.Add(new Warning("intolerance", "histamine", "Histamin", hi, false));
        return list;
    }

    /// <summary>EU-Hauptallergene, die in einem Text (Zutaten) vorkommen, als Bezeichnung („Gluten“, „Milch“) - nach denselben Wörtern wie <see cref="Check"/>.</summary>
    public static List<string> AllergensIn(string text)
    {
        var all = new CoachProfile { Allergens = [.. AllergenWords.Keys] };
        return Check("", text, [], all).Where(w => w.Kind == "allergen").Select(w => w.Label).ToList();
    }

    /// <summary>
    /// Ernährungsformen, zu denen ein Rezept passt - nach denselben Wörtern wie der Plan (vegetarian, vegan, lowcarb - gilt auch für keto);
    /// eiweißreich (highprotein) nach den Nährwerten: mindestens 25 % der Energie aus Eiweiß.
    /// </summary>
    public static List<string> Diets(Candidate c, double? kcal, double? protein)
    {
        var list = new List<string>();
        foreach (var d in new[] { "vegetarian", "vegan", "lowcarb" })
            if (Excluded(c, new CoachProfile { Diet = d }) == null) list.Add(d);
        if (kcal is > 0 && protein is double p && p * 4 / kcal >= 0.25) list.Add("highprotein");
        return list;
    }

    /// <summary>Passt das Rezept? Sonst ein kurzer Grund (für die Anzeige, wie viele warum herausfallen).</summary>
    public static string? Excluded(Candidate c, CoachProfile p)
    {
        var text = (c.Title + "\n" + string.Join("\n", c.Ingredients)).ToLowerInvariant();
        var words = text.Split([' ', '\n', ',', ';', '(', ')', '.', ':', '/', '-', '–'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        bool Has(IEnumerable<string> list) => list.Any(w => w.Length <= 3 || w.Contains(' ') ? words.Contains(w) || (w.Contains(' ') && text.Contains(w)) : text.Contains(w));
        var dairyText = PlantMilk.Aggregate(text, (t, w) => t.Replace(w, " "));
        bool HasMilk() => AllergenWords["milk"].Any(dairyText.Contains);
        foreach (var a in p.Allergens)
            if (AllergenWords.TryGetValue(a, out var list) && (a == "milk" ? HasMilk() : Has(list))) return "Allergen";
        if (p.Intolerances.Contains("lactose") && HasMilk() && !text.Contains("laktosefrei")) return "Laktose";
        if (p.Intolerances.Contains("gluten") && Has(AllergenWords["gluten"]) && !text.Contains("glutenfrei")) return "Gluten";
        foreach (var d in p.Dislikes)
            if (d.Length > 1 && text.Contains(d.ToLowerInvariant())) return "Abneigung";
        var meat = Has(MeatWords) || Has(AllergenWords["fish"]) || Has(AllergenWords["crustaceans"]) || Has(AllergenWords["molluscs"]);
        if (p.Diet is "vegetarian" or "vegan" && meat) return "Fleisch oder Fisch";
        if (p.Diet == "vegan" && (HasMilk() || Has(AllergenWords["eggs"]) || Has(AnimalExtra)) && !text.Contains("vegan")) return "Tierisches";
        if (p.Diet is "keto" or "lowcarb" && Has(CarbWords)) return "Kohlenhydrate";
        // Ohne Zutaten lässt sich nicht sagen, ob es passt („Pfefferpotthast“ ist sonst vegan)
        if (p.Diet is "vegetarian" or "vegan" or "keto" or "lowcarb" && c.Ingredients.Count == 0) return "Zutaten fehlen";
        return null;
    }

    /// <summary>
    /// Vorschläge für die freien Mahlzeiten (taken: schon geplant, „yyyy-MM-dd|Mahlzeit“). Reihenfolge zufällig (seed), kürzlich
    /// Geplantes zuletzt, kein Rezept zweimal, bis der Vorrat verbraucht ist. Reste: das Abendessen gibt es am nächsten Mittag noch einmal.
    /// </summary>
    public static List<Slot> Suggest(IReadOnlyList<Candidate> pool, DateOnly from, int days, IReadOnlyList<string> meals,
        IReadOnlySet<string> taken, IReadOnlySet<string> recent, bool leftovers, int seed)
    {
        var rnd = new Random(seed);
        var order = pool.OrderBy(c => recent.Contains(c.SyncId) ? 1 : 0).ThenBy(_ => rnd.Next()).ToList();
        var queue = new Queue<Candidate>(order);
        Candidate? Next()
        {
            if (order.Count == 0) return null;
            if (queue.Count == 0) foreach (var c in order) queue.Enqueue(c);   // Vorrat verbraucht: von vorn
            return queue.Dequeue();
        }
        var result = new List<Slot>();
        var leftoverFor = new Dictionary<DateOnly, Candidate>();
        for (int i = 0; i < days; i++)
        {
            var day = from.AddDays(i);
            var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var (meal, _) in Meals)
            {
                if (!meals.Contains(meal) || taken.Contains(date + "|" + meal)) continue;
                if (meal == "Mittagessen" && leftoverFor.TryGetValue(day, out var rest))
                {
                    result.Add(new Slot(date, meal, rest.SyncId, rest.Title, Leftover: true));
                    continue;
                }
                if (Next() is not { } c) return result;
                result.Add(new Slot(date, meal, c.SyncId, c.Title));
                if (leftovers && meal == "Abendessen" && meals.Contains("Mittagessen")) leftoverFor[day.AddDays(1)] = c;
            }
        }
        return result;
    }

    // ---------------- Schnittstelle ----------------

    public record AcceptBody(long Calendar, List<Slot> Slots);

    private static async Task<List<Candidate>> CandidatesAsync(UserSpace space, CancellationToken ct)
    {
        var notes = NotesDb.Of(space).GetAllActive();
        var list = notes.Where(n => Recipes.IsRecipe(n.Tags, n.Plain)).Select(n => new Candidate(n.SyncId, n.DisplayTitle, Recipes.Ingredients(n), Recipes.Kcal(n.Plain))).ToList();
        // Rezepte aus den anderen notizi-Datenbanken (deren Passwort passt) - wie im Essensplan
        var remote = await RemoteRecipes.AllAsync(space, null, ct);
        var missing = remote.Select(r => r.SyncId).Where(id => list.All(c => c.SyncId != id)).ToHashSet();
        if (missing.Count > 0)
            foreach (var r in await RemoteRecipes.IngredientsAsync(space, missing, ct)) list.Add(new Candidate(r.SyncId, r.Title, r.Items, r.Kcal));
        // Zu wenig eigene mit Kalorien je Portion für eine Woche: Basisrezepte der eigenen Ernährungsform dazu (Mittag- und Abendessen)
        if (list.Count(c => c.Kcal != null) < 14)
        {
            var diet = CoachStore.Profile(space.Db).Diet;
            list.AddRange(BaseRecipes.All.Where(b => b.Diet == diet && b.Meal != "breakfast").Select(BaseRecipes.Candidate));
        }
        return list;
    }

    private static List<Collection> MealCalendars(UserSpace space) =>
        PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT") && CalendarKinds.Of(space.Db, c.Id) == CalendarKinds.Meal
            && !Subscriptions.IsSubscribed(space.Db, c.Id)).ToList();

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/coach/plan").RequireAuthorization();

        api.MapGet("/suggest", async (string? from, int? days, string? meals, bool? leftovers, int? seed, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var start = from != null && DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : CoachStore.Today(space);
            var n = Math.Clamp(days ?? 7, 1, 14);
            var wanted = (meals ?? "Mittagessen,Abendessen").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(m => Meals.Any(x => x.Key == m)).ToList();
            var profile = CoachStore.Profile(space.Db);
            var all = await CandidatesAsync(space, ct);
            // Nur Rezepte mit Kalorien je Portion - sonst lässt sich nicht sagen, wie sie in den Plan passen
            string? Why(Candidate c) => Excluded(c, profile) ?? (c.Kcal == null ? NoKcal : null);
            var excluded = all.Select(Why).Where(r => r != null).GroupBy(r => r!).ToDictionary(g => g.Key, g => g.Count());
            var pool = all.Where(c => Why(c) == null).ToList();

            // Was im Essensplan schon steht (diese Tage: belegt; letzte 14 Tage: kürzlich gekocht)
            var cals = MealCalendars(space);
            var zone = space.Zone;
            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(start.AddDays(-14).ToDateTime(TimeOnly.MinValue), zone);
            var toUtc = TimeZoneInfo.ConvertTimeToUtc(start.AddDays(n).ToDateTime(TimeOnly.MinValue), zone);
            var events = cals.Count == 0 ? [] : CalendarModel.Events(space, fromUtc, toUtc, cals.Select(c => c.Id));
            var taken = new HashSet<string>();
            var recent = new HashSet<string>();
            foreach (var e in events)
            {
                if (!DateTime.TryParse(e.Start, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var s)) continue;
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(s, zone));
                if (day >= start) { foreach (var m in e.Categories) taken.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + m); }
                if (!string.IsNullOrEmpty(e.Recipe)) recent.Add(e.Recipe);
            }
            var slots = Suggest(pool, start, n, wanted, taken, recent, leftovers ?? true, seed ?? Environment.TickCount);
            return new
            {
                slots, recipes = all.Count, usable = pool.Count, excluded,
                calendars = cals.Select(c => new { c.Id, c.Name, c.Color }),
            };
        });

        api.MapPost("/accept", (AcceptBody body, HttpContext http) =>
        {
            var space = http.Space();
            if (MealCalendars(space).All(c => c.Id != body.Calendar)) return Results.BadRequest(new { error = "Bitte einen Essensplan-Kalender wählen." });
            var made = 0;
            foreach (var s in body.Slots.Take(60))
            {
                var meal = Meals.FirstOrDefault(m => m.Key == s.Meal);
                if (meal.Key == null || string.IsNullOrWhiteSpace(s.Title)
                    || !DateOnly.TryParseExact(s.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                var at = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(meal.Hour, 0)), space.Zone);
                CalendarModel.SaveEvent(space, new EventInput
                {
                    Calendar = body.Calendar, Title = s.Title.Trim(), Start = at.ToString("o", CultureInfo.InvariantCulture),
                    End = at.AddHours(1).ToString("o", CultureInfo.InvariantCulture), Busy = false, Categories = [meal.Key], Recipe = s.Recipe ?? "",
                });
                made++;
            }
            return Results.Ok(new { made });
        });
    }
}
