using System.Globalization;
using System.Text.Json;
using DmOffice.Notes;

namespace DmOffice.Coach;

/// <summary>
/// Basisrezepte (Coach/Bls/basisrezepte.json): 210 Rezepte für eine Person, je Ernährungsform (dge, med, vegetarian,
/// vegan, protein, lowcarb, keto) und Mahlzeit zehn - Teil der Auslieferung, für alle gleich, ohne Notiz. Jede Zutat hat eine
/// Küchenmenge („½ rote Paprikaschote“), Gramm und den BLS-Code; Nährwerte und Allergene werden daraus berechnet.
/// Im Tagebuch und im Essensplan heißen sie wie Rezept-Notizen („recipe:base-…“); „Übernehmen“ legt eine eigene Rezept-Notiz an.
/// </summary>
public static class BaseRecipes
{
    public const string Prefix = "base-";

    /// <summary>Höchstzahl Portionen im Umschalter von Web und App.</summary>
    public const int MaxServings = 6;

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly Dictionary<string, string> UnitPlural = new()
    {
        ["Zehe"] = "Zehen", ["Scheibe"] = "Scheiben", ["Dose"] = "Dosen", ["Stange"] = "Stangen", ["Zweig"] = "Zweige", ["Prise"] = "Prisen", ["Blatt"] = "Blätter",
    };

    /// <summary>Amount/Unit: Küchenmenge für eine Portion (ohne Unit: Stück, dann Plural für mehr als eins); ohne Amount: „Salz und Pfeffer“.</summary>
    public sealed record Ingredient(double? Amount, string? Unit, string Name, string? Plural, double Grams, string? Bls)
    {
        /// <summary>Die Zeile für 1 bis 6 Portionen (Portionen-Umschalter in Web und App).</summary>
        public List<string> Texts => Enumerable.Range(1, MaxServings).Select(Text).ToList();

        /// <summary>„½ rote Paprikaschote (80 g)“, „150 g Hähnchenbrustfilet“, „1 EL Olivenöl (10 g)“.</summary>
        public string Text(int servings)
        {
            if (Amount is not double a) return Name;
            var q = a * servings;
            var grams = Bls != null ? " (" + (Grams * servings).ToString("0.#", De) + " g)" : "";
            if (Unit is "g" or "ml") return Qty(q) + " " + Unit + " " + Name;
            if (Unit != null) return Qty(q) + " " + (q > 1 ? UnitPlural.GetValueOrDefault(Unit, Unit) : Unit) + " " + Name + grams;
            return Qty(q) + " " + (q > 1 ? Plural ?? Name : Name) + grams;
        }
    }

    /// <summary>Zahl mit Bruch: 0,5 → „½“, 1,5 → „1½“, 0,3 → „0,3“.</summary>
    public static string Qty(double q)
    {
        if (q >= 10) return Math.Round(q).ToString("0", De);
        var whole = Math.Floor(q + 1e-9);
        var frac = q - whole;
        string? f = Math.Abs(frac) < 0.01 ? "" : Math.Abs(frac - 0.25) < 0.01 ? "¼" : Math.Abs(frac - 0.5) < 0.01 ? "½" : Math.Abs(frac - 0.75) < 0.01 ? "¾"
            : Math.Abs(frac - 1 / 3d) < 0.01 ? "⅓" : Math.Abs(frac - 2 / 3d) < 0.01 ? "⅔" : null;
        if (f == null) return q.ToString("0.#", De);
        return (whole > 0 ? whole.ToString("0", De) : f.Length == 0 ? "0" : "") + f;
    }

    public sealed record Recipe(string Id, string Diet, string Meal, string Title, int Prep, int Cook, List<Ingredient> Ingredients, List<string> Steps,
        string? Tip, string? Hint)
    {
        /// <summary>Foto des Rezepts, wenn es eins gibt (Coach/Bls/bilder) - sonst zeigt die Rezeptkarte eine Fläche in der Farbe der Mahlzeit.</summary>
        public string? Image => HasImage(Id) ? "/api/coach/recipes/" + Id + "/image" : null;

        /// <summary>Vorbereitung und Garzeit zusammen.</summary>
        public int Minutes => Prep + Cook;

        public double Grams => Math.Round(Ingredients.Sum(i => i.Grams));

        /// <summary>Zutatenzeilen für eine Portion, wie in einer Rezept-Notiz: „½ rote Paprikaschote (80 g)“.</summary>
        public List<string> Lines => Ingredients.Select(i => i.Text(1)).ToList();

        /// <summary>Nährwerte je Portion aus dem BLS (Zutaten ohne Code sind Gewürze unter 2 g).</summary>
        public Nutrients PerServing => perServing ??= Compute(Ingredients);
        private Nutrients? perServing;

        /// <summary>EU-Hauptallergene nach den Zutaten, z. B. „Gluten“, „Milch“.</summary>
        public List<string> Allergens => CoachPlan.AllergensIn(string.Join('\n', Ingredients.Select(i => i.Name)));
    }

    private static Nutrients Compute(List<Ingredient> list)
    {
        double kcal = 0, protein = 0, fat = 0, carbs = 0, fiber = 0, sugar = 0, salt = 0, satFat = 0;
        foreach (var i in list)
        {
            if (i.Bls == null || Bls.Get(i.Bls) is not { } e) continue;
            var n = e.Per100.For(i.Grams);
            kcal += n.Kcal; protein += n.Protein; fat += n.Fat; carbs += n.Carbs;
            fiber += n.Fiber ?? 0; sugar += n.Sugar ?? 0; salt += n.Salt ?? 0; satFat += n.SatFat ?? 0;
        }
        return new(Math.Round(kcal), Math.Round(protein, 1), Math.Round(fat, 1), Math.Round(carbs, 1), Math.Round(fiber, 1), Math.Round(sugar, 1),
            Math.Round(salt, 2), Math.Round(satFat, 1));
    }

    public static readonly Dictionary<string, string> DietNames = new()
    {
        ["dge"] = "Mischkost (DGE)", ["med"] = "Mediterran", ["vegetarian"] = "Vegetarisch", ["vegan"] = "Vegan",
        ["protein"] = "Eiweißreich", ["lowcarb"] = "Low Carb", ["keto"] = "Keto",
    };
    public static readonly Dictionary<string, string> MealNames = new() { ["breakfast"] = "Frühstück", ["lunch"] = "Mittagessen", ["dinner"] = "Abendessen" };

    private static readonly Lazy<List<Recipe>> List = new(Load);
    private static readonly Lazy<Dictionary<string, Recipe>> ById_ = new(() => List.Value.ToDictionary(r => r.Id));

    public static IReadOnlyList<Recipe> All => List.Value;

    private const string ImagePrefix = "DmOffice.Coach.Bilder.";
    private static readonly Lazy<HashSet<string>> Images = new(() => typeof(BaseRecipes).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(ImagePrefix, StringComparison.Ordinal) && n.EndsWith(".webp", StringComparison.Ordinal))
        .Select(n => n[ImagePrefix.Length..^5]).ToHashSet(StringComparer.Ordinal));

    // Gleiches Gericht in einer anderen Ernährungsform: dessen Foto
    private static readonly Lazy<Dictionary<string, string>> Same = new(() =>
    {
        using var s = typeof(BaseRecipes).Assembly.GetManifestResourceStream(ImagePrefix + "gleiche.json");
        return s == null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
    });

    private static string ImageId(string id) => Images.Value.Contains(id) ? id : Same.Value.GetValueOrDefault(id) ?? id;

    public static bool HasImage(string id) => Images.Value.Contains(ImageId(id));

    /// <summary>Das Foto (WebP) oder null.</summary>
    public static byte[]? Image(string id)
    {
        if (!HasImage(id)) return null;
        using var s = typeof(BaseRecipes).Assembly.GetManifestResourceStream(ImagePrefix + ImageId(id) + ".webp");
        if (s == null) return null;
        var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
    public static bool IsBase(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);
    public static Recipe? ById(string id) => IsBase(id) && ById_.Value.TryGetValue(id, out var r) ? r : null;

    private static List<Recipe> Load()
    {
        using var s = typeof(BaseRecipes).Assembly.GetManifestResourceStream("DmOffice.Coach.Data.basisrezepte.json");
        if (s == null) return [];
        using var doc = JsonDocument.Parse(s);
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return doc.RootElement.EnumerateArray().Select(e => new Recipe(
            e.GetProperty("id").GetString()!, e.GetProperty("diet").GetString()!, e.GetProperty("meal").GetString()!, e.GetProperty("title").GetString()!,
            e.GetProperty("prep").GetInt32(), e.GetProperty("cook").GetInt32(),
            e.GetProperty("ingredients").EnumerateArray().Select(i => new Ingredient(
                i.TryGetProperty("q", out var q) ? q.GetDouble() : null, Str(i, "u"), i.GetProperty("n").GetString()!, Str(i, "p"),
                i.GetProperty("g").GetDouble(), Str(i, "bls"))).ToList(),
            e.GetProperty("steps").EnumerateArray().Select(x => x.GetString()!).ToList(),
            Str(e, "tip"), Str(e, "hint"))).ToList();
    }

    /// <summary>Wie ein Ordner angezeigt: „Basisrezepte / Vegan / Frühstück“.</summary>
    public static string Folder(Recipe r) => "Basisrezepte / " + DietNames.GetValueOrDefault(r.Diet, r.Diet) + " / " + MealNames.GetValueOrDefault(r.Meal, r.Meal);

    public static CoachPlan.Candidate Candidate(Recipe r) => new(r.Id, r.Title, r.Lines, Math.Round(r.PerServing.Kcal));

    /// <summary>Ernährungsformen für den Filter: die aus dem Kochbuch und die, die Coach an den Zutaten erkennt.</summary>
    public static List<string> Diets(Recipe r)
    {
        var list = CoachPlan.Diets(Candidate(r), r.PerServing.Kcal, r.PerServing.Protein);
        list.AddRange(r.Diet switch
        {
            "vegan" => ["vegan", "vegetarian"], "vegetarian" => ["vegetarian"], "lowcarb" => ["lowcarb"], "keto" => ["lowcarb", "keto"], "protein" => ["highprotein"],
            // Mischkost und Mediterran gibt es nur als Basisrezepte (an den Zutaten nicht zu erkennen)
            var d => [d],
        });
        return list.Distinct().ToList();
    }

    /// <summary>Für die Rezeptansicht: Zutaten mit Gramm, Nährwerte aus dem BLS, eine Portion.</summary>
    public static CoachRecipes.Result Result(Recipe r) => new(r.Id, r.Title, 1, false,
        r.Ingredients.Select((i, n) => new CoachRecipes.Line(r.Lines[n], null, null, i.Grams, null, 0, [], false)).ToList(),
        r.PerServing, r.PerServing, r.Grams, 0, r);

    /// <summary>Als eigene Rezept-Notiz in der Gestalt der Vorlage (Tag „recipe“).</summary>
    public static long Adopt(NotesDb db, Recipe r, string? folder)
    {
        var description = "Allergene: " + (r.Allergens.Count == 0 ? "keine der 14 Hauptallergene" : string.Join(", ", r.Allergens))
            + (r.Tip != null ? " · " + r.Tip : "") + (r.Hint != null ? " · " + r.Hint : "");
        var recipe = new RecipeImport.Recipe(r.Title, description, "1 Portion", r.Prep + " Min.", r.Cook > 0 ? r.Cook + " Min." : "", r.Minutes + " Min.",
            [new RecipeImport.Section("", r.Lines)], [new RecipeImport.Section("", r.Steps)], null, "dmOffice-Basisrezept",
            Calories: Math.Round(r.PerServing.Kcal).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var (html, plain) = RecipeImport.ToNote(recipe, null);
        // Kein Link als Quelle - nur der Hinweis
        html = html.Replace("<a href=\"dmOffice-Basisrezept\">dmOffice-Basisrezept</a>", "dmOffice-Basisrezept");
        var id = db.CreateNote(folder);
        db.SaveNote(id, html, plain);
        db.SetTags(id, [Recipes.Tag]);
        return id;
    }
}
