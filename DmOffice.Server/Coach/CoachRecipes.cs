using System.Globalization;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Coach;

/// <summary>
/// Coach Stufe 2: Nährwerte eines Rezepts (Rezept-Notiz) aus seinen Zutaten - jede Zeile zerlegt (FoodText), einem
/// Lebensmittel zugeordnet (bestätigte Zuordnung, sonst bester Treffer), Menge in Gramm; Summe, je Portion, je 100 g.
/// Im Tagebuch ist ein Rezept ein Lebensmittel „recipe:&lt;sync_id&gt;“ (Portion = Gesamtgewicht / Portionen).
/// Dazu „Laut Essensplan“: was heute zu einer Mahlzeit im Essensplan steht.
/// </summary>
public static class CoachRecipes
{
    public sealed record Line(string Text, string? Name, CoachApi.Hit? Food, double? Grams, string? Portion, double Kcal, List<CoachApi.Hit> Alternatives, bool Vague);
    public sealed record Result(string SyncId, string Title, int Servings, bool ServingsGuessed, List<Line> Lines, Nutrients Total, Nutrients PerServing,
        double Weight, int Unmatched, BaseRecipes.Recipe? Base = null, int? KcalGiven = null);

    /// <summary>„für 4 Personen“, „Portionen: 4“, „4 Portionen“; sonst 4 (geschätzt).</summary>
    public static (int Servings, bool Guessed) Servings(string plain)
    {
        var m = Regex.Match(plain, @"(?:für|fuer)\s+(\d{1,2})\s*(?:Personen|Portionen|Pers\.)|(?:Portionen|Personen|Servings|Serves)\s*[:=]?\s*(\d{1,2})|(\d{1,2})\s*(?:Portionen|Personen|Stück\b)",
            RegexOptions.IgnoreCase);
        if (m.Success)
            foreach (var g in new[] { m.Groups[1], m.Groups[2], m.Groups[3] })
                if (g.Success && int.TryParse(g.Value, out var n) && n is > 0 and <= 50) return (n, false);
        return (4, true);
    }

    /// <summary>Saft, Wasser, Brühe: ml ≈ g. Für die Summe reicht das.</summary>
    public static Result? Compute(UserSpace space, CoachFoods foods, string syncId, int? servingsOverride = null)
    {
        if (BaseRecipes.IsBase(syncId)) return BaseRecipes.ById(syncId) is { } b ? BaseRecipes.Result(b) : null;
        var note = NotesDb.Of(space).BySyncId(syncId);
        if (note == null || !Recipes.IsRecipe(note.Tags, note.Plain)) return null;
        var today = CoachStore.Today(space);
        var lines = new List<Line>();
        var total = Nutrients.Zero;
        double weight = 0;
        int unmatched = 0;
        foreach (var text in Recipes.Ingredients(note).Take(60))
        {
            var item = FoodText.Ingredient(text);
            if (item == null) continue;
            var (food, alts) = CoachSuggest.Match(space, foods, item.Name, today, recipes: false);
            var (grams, portion) = CoachSuggest.Amount(item, food);
            double kcal = 0;
            if (food != null && grams is double g && g > 0)
            {
                var n = food.Per100.For(g);
                total = Add(total, n);
                weight += g;
                kcal = n.Kcal;
            }
            else if (!item.Vague) unmatched++;
            lines.Add(new Line(text, item.Name, food == null ? null : CoachApi.Hit.Of(food), grams, portion, Math.Round(kcal), alts, item.Vague));
        }
        var (servings, guessed) = Servings(note.Plain);
        if (servingsOverride is int so && so > 0) { servings = so; guessed = false; }
        else if (space.Db.Get(ServingsKey(syncId)) is { } saved && int.TryParse(saved, out var sv)) { servings = sv; guessed = false; }
        var per = Scale(total, 1.0 / servings);
        return new Result(syncId, note.DisplayTitle, servings, guessed, lines, Round(total), Round(per), Math.Round(weight), unmatched, KcalGiven: Recipes.Kcal(note.Plain));
    }

    /// <summary>Nährwerte je Portion aus Zutatenzeilen (Rezeptsuche, noch ohne Notiz); Unmatched: Zeilen ohne Zuordnung.</summary>
    public static (Nutrients PerServing, int Unmatched) Estimate(UserSpace space, CoachFoods foods, IEnumerable<string> ingredients, int servings)
    {
        var today = CoachStore.Today(space);
        var total = Nutrients.Zero;
        int unmatched = 0;
        foreach (var text in ingredients.Take(60))
        {
            var item = FoodText.Ingredient(text);
            if (item == null) continue;
            var (food, _) = CoachSuggest.Match(space, foods, item.Name, today, recipes: false);
            var (grams, _) = CoachSuggest.Amount(item, food);
            if (food != null && grams is double g && g > 0) total = Add(total, food.Per100.For(g));
            else if (!item.Vague) unmatched++;
        }
        return (Round(Scale(total, 1.0 / Math.Max(1, servings))), unmatched);
    }

    public static string ServingsKey(string syncId) => "coach.servings." + syncId;

    /// <summary>
    /// Rezept als Lebensmittel fürs Tagebuch: je 100 g aus der Summe, Portion = Gesamtgewicht / Portionen. Steht „Kalorien je Portion“
    /// im Rezept, gilt dieser Wert (die übrigen Nährwerte bleiben aus den Zutaten); ohne verwertbare Zutaten ist eine Portion dann 100 g.
    /// </summary>
    public static Food? AsFood(UserSpace space, CoachFoods foods, string syncId)
    {
        var r = Compute(space, foods, syncId);
        if (r == null) return null;
        if (r.Weight <= 0)
            return r.KcalGiven is int only
                ? new Food("recipe:" + syncId, r.Title, null, "recipe", new Nutrients(only, 0, 0, 0), [new Portion("1 Portion", 100), new Portion("½ Portion", 50)])
                : null;
        var per100 = Scale(r.Total, 100 / r.Weight);
        var portion = Math.Round(r.Weight / r.Servings, 1);
        if (r.KcalGiven is int given) per100 = per100 with { Kcal = given * 100 / portion };
        return new Food("recipe:" + syncId, r.Title, null, "recipe", Round(per100),
            [new Portion("1 Portion", portion), new Portion("½ Portion", Math.Round(portion / 2, 1)), new Portion("100 g", 100)]);
    }

    private static Nutrients Add(Nutrients a, Nutrients b) => new(a.Kcal + b.Kcal, a.Protein + b.Protein, a.Fat + b.Fat, a.Carbs + b.Carbs,
        (a.Fiber ?? 0) + (b.Fiber ?? 0), (a.Sugar ?? 0) + (b.Sugar ?? 0), (a.Salt ?? 0) + (b.Salt ?? 0), (a.SatFat ?? 0) + (b.SatFat ?? 0));
    private static Nutrients Scale(Nutrients n, double f) => n.For(f * 100);
    private static Nutrients Round(Nutrients n) => new(Math.Round(n.Kcal), Math.Round(n.Protein, 1), Math.Round(n.Fat, 1), Math.Round(n.Carbs, 1),
        n.Fiber is double fi ? Math.Round(fi, 1) : null, n.Sugar is double su ? Math.Round(su, 1) : null, n.Salt is double sa ? Math.Round(sa, 2) : null,
        n.SatFat is double sf ? Math.Round(sf, 1) : null);

    // ---------------- Laut Essensplan ----------------

    /// <summary>Guessed: ohne Rezept über den Namen zugeordnet (z. B. „Gulasch“ → BLS); Portion: die vorgeschlagene Menge.</summary>
    public sealed record Planned(string Title, string? Recipe, string? Food, double? KcalPerServing, string? FoodName = null, string? Portion = null, bool Guessed = false);

    private static readonly Dictionary<string, string> MealCategory = new() { ["breakfast"] = "Frühstück", ["lunch"] = "Mittagessen", ["dinner"] = "Abendessen" };

    /// <summary>Was an diesem Tag zu dieser Mahlzeit im Essensplan steht (Kalender der Art „Essensplan“).</summary>
    public static List<Planned> PlannedFor(UserSpace space, CoachFoods foods, DateOnly day, string meal)
    {
        if (!MealCategory.TryGetValue(meal, out var category)) return [];
        var cals = PimStore.Collections(space.Db, PimStore.Calendar)
            .Where(c => c.Components.Contains("VEVENT") && CalendarKinds.Of(space.Db, c.Id) == CalendarKinds.Meal).Select(c => c.Id).ToList();
        if (cals.Count == 0) return [];
        var from = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), space.Zone);
        var list = new List<Planned>();
        foreach (var e in CalendarModel.Events(space, from, from.AddDays(1), cals).Where(e => e.Categories.Contains(category, StringComparer.OrdinalIgnoreCase)))
        {
            Food? food = string.IsNullOrEmpty(e.Recipe) ? null : AsFood(space, foods, e.Recipe);
            if (food != null)
            {
                var kcal = food.Portions.FirstOrDefault() is { } p ? Math.Round(food.Per100.For(p.Grams).Kcal) : (double?)null;
                list.Add(new Planned(e.Title, e.Recipe, food.Id, kcal, food.Name, "1 Portion"));
                continue;
            }
            // Ohne (verwertbares) Rezept: über den Namen zuordnen, mit der üblichen Portion - ein Klick trägt ein
            var (best, _) = CoachSuggest.Match(space, foods, e.Title, day);
            if (best != null)
            {
                var (grams, portion) = CoachApi.Usual(best);
                list.Add(new Planned(e.Title, string.IsNullOrEmpty(e.Recipe) ? null : e.Recipe, best.Id, Math.Round(best.Per100.For(grams).Kcal), best.Name, portion ?? $"{grams:0} g", true));
            }
            else list.Add(new Planned(e.Title, string.IsNullOrEmpty(e.Recipe) ? null : e.Recipe, null, null));
        }
        return list;
    }
}
