using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;

namespace DmOffice.Coach;

/// <summary>
/// Coach Stufe 2, cleveres Erfassen: Vorschläge aus dem eigenen Verlauf (Mahlzeit, Wochentag, Uhrzeit, mit Abklingen),
/// Vorlagen („Mein Frühstück“), Mahlzeit von einem anderen Tag übernehmen, Freitext („2 Brötchen mit Käse“) → Lebensmittel.
/// </summary>
public static class CoachSuggest
{
    // ---------------- Vorschläge ----------------

    /// <summary>
    /// Was man zu dieser Mahlzeit typischerweise isst: jede frühere Mahlzeit zählt, mit Abklingen (30 Tage), mehr bei gleicher
    /// Mahlzeit, gleichem Wochentag und ähnlicher Uhrzeit. Je Lebensmittel einmal, mit der zuletzt gewählten Menge.
    /// </summary>
    public static List<CoachStore.Recent> Suggest(UserDb db, string? meal, DateOnly today, DateTime nowLocal, int limit = 20)
    {
        CoachStore.Ensure(db);
        var rows = db.Query(@"SELECT food, name, brand, amount, portion, kcal, meal, day, at FROM coach_diary
                WHERE food IS NOT NULL AND amount IS NOT NULL AND day >= $from ORDER BY at DESC, id DESC",
            r => (Food: r.GetString(0), Name: r.GetString(1), Brand: r.IsDBNull(2) ? null : r.GetString(2), Amount: r.GetDouble(3),
                Portion: r.IsDBNull(4) ? null : r.GetString(4), Kcal: r.GetDouble(5), Meal: r.GetString(6), Day: r.GetString(7), At: r.GetInt64(8)),
            ("$from", CoachStore.Day(today.AddDays(-120))));
        var weekday = today.DayOfWeek;
        return rows.GroupBy(x => x.Food).Select(g =>
        {
            double score = 0;
            foreach (var x in g)
            {
                var d = DateOnly.ParseExact(x.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var age = Math.Max(0, today.DayNumber - d.DayNumber);
                var w = Math.Exp(-age / 30.0);
                if (x.Meal == meal) w *= 3;
                if (d.DayOfWeek == weekday) w *= 1.3;
                var hour = DateTimeOffset.FromUnixTimeSeconds(x.At).ToLocalTime().Hour;
                if (Math.Abs(hour - nowLocal.Hour) <= 2) w *= 1.2;
                score += w;
            }
            var last = g.First();
            return (Item: new CoachStore.Recent(g.Key, last.Name, last.Brand, last.Amount, last.Portion, Math.Round(last.Kcal), g.Count(), g.Count(x => x.Meal == meal)), Score: score);
        // Zu einer Mahlzeit nur, was schon einmal zu dieser Mahlzeit gegessen wurde („Zuletzt zu dieser Mahlzeit“)
        }).Where(x => meal == null || x.Item.MealCount > 0).OrderByDescending(x => x.Score).Take(limit).Select(x => x.Item).ToList();
    }

    /// <summary>Häufig: was in den letzten 120 Tagen am öftesten eingetragen wurde, gleich zu welcher Mahlzeit (Menge vom letzten Mal).</summary>
    public static List<CoachStore.Recent> Frequent(UserDb db, DateOnly today, int limit = 30)
    {
        CoachStore.Ensure(db);
        var rows = db.Query(@"SELECT food, name, brand, amount, portion, kcal FROM coach_diary
                WHERE food IS NOT NULL AND amount IS NOT NULL AND day >= $from ORDER BY at DESC, id DESC",
            r => (Food: r.GetString(0), Name: r.GetString(1), Brand: r.IsDBNull(2) ? null : r.GetString(2), Amount: r.GetDouble(3),
                Portion: r.IsDBNull(4) ? null : r.GetString(4), Kcal: r.GetDouble(5)),
            ("$from", CoachStore.Day(today.AddDays(-120))));
        return rows.GroupBy(x => x.Food).OrderByDescending(g => g.Count()).Take(limit)
            .Select(g => { var l = g.First(); return new CoachStore.Recent(g.Key, l.Name, l.Brand, l.Amount, l.Portion, Math.Round(l.Kcal), g.Count(), 0); }).ToList();
    }

    // ---------------- Vorlagen ----------------

    public sealed record TemplateItem(string? Food, string Name, string? Brand, double? Amount, string? Portion, Nutrients N);
    public sealed record Template(string Id, string Name, string Meal, List<TemplateItem> Items, double Kcal);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void EnsureTemplates(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS coach_templates (id TEXT PRIMARY KEY, name TEXT NOT NULL, meal TEXT NOT NULL, items TEXT NOT NULL, created INTEGER NOT NULL)");

    public static List<Template> Templates(UserDb db)
    {
        EnsureTemplates(db);
        return db.Query("SELECT id, name, meal, items FROM coach_templates ORDER BY name COLLATE NOCASE", r =>
        {
            var items = JsonSerializer.Deserialize<List<TemplateItem>>(r.GetString(3), Json) ?? [];
            return new Template(r.GetString(0), r.GetString(1), r.GetString(2), items, Math.Round(items.Sum(i => i.N.Kcal)));
        });
    }

    /// <summary>Eine Mahlzeit eines Tages als Vorlage speichern („Mein Frühstück“).</summary>
    public static Template SaveTemplate(UserDb db, string? name, DateOnly day, string meal)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        if (n.Length > 80) throw new ArgumentException("Der Name ist zu lang (höchstens 80 Zeichen).");
        var entries = CoachStore.Diary(db, day).Where(e => e.Meal == CoachStore.Meal(meal)).ToList();
        if (entries.Count == 0) throw new ArgumentException("In dieser Mahlzeit steht noch nichts.");
        var items = entries.Select(e => new TemplateItem(e.Food, e.Name, e.Brand, e.Amount, e.Portion,
            new Nutrients(e.Kcal, e.Protein, e.Fat, e.Carbs, e.Fiber, e.Sugar, e.Salt))).ToList();
        EnsureTemplates(db);
        var id = Guid.NewGuid().ToString("N")[..10];
        db.Exec("INSERT INTO coach_templates(id, name, meal, items, created) VALUES($i,$n,$m,$it,$t)",
            ("$i", id), ("$n", n), ("$m", meal), ("$it", JsonSerializer.Serialize(items, Json)), ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return Templates(db).First(t => t.Id == id);
    }

    public static void DeleteTemplate(UserDb db, string id) { EnsureTemplates(db); db.Exec("DELETE FROM coach_templates WHERE id=$i", ("$i", id)); }

    /// <summary>Vorlage eintragen: dieselben Posten mit denselben Nährwerten.</summary>
    public static int ApplyTemplate(UserDb db, string id, DateOnly day, string? meal)
    {
        var t = Templates(db).FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Vorlage nicht gefunden.");
        foreach (var i in t.Items) CoachStore.AddRaw(db, day, CoachStore.Meal(meal ?? t.Meal), i.Food, i.Name, i.Brand, i.Amount, i.Portion, i.N);
        return t.Items.Count;
    }

    /// <summary>
    /// Mahlzeit(en) eines anderen Tages übernehmen („wie gestern“). meal null: alle Mahlzeiten; ids: nur diese Einträge
    /// (Auswahl in der Liste bei „Wie gestern“).
    /// </summary>
    public static int Copy(UserDb db, DateOnly from, DateOnly to, string? meal, string? toMeal = null, IReadOnlyCollection<long>? ids = null)
    {
        var entries = CoachStore.Diary(db, from).Where(e => (meal == null || e.Meal == meal) && (ids == null || ids.Contains(e.Id))).ToList();
        foreach (var e in entries)
            CoachStore.AddRaw(db, to, toMeal ?? e.Meal, e.Food, e.Name, e.Brand, e.Amount, e.Portion, new Nutrients(e.Kcal, e.Protein, e.Fat, e.Carbs, e.Fiber, e.Sugar, e.Salt));
        return entries.Count;
    }

    // ---------------- Zutat bzw. Freitext → Lebensmittel ----------------

    /// <summary>Vorschlag für eine Zeile: Lebensmittel (oder keins), Menge in g/ml, Portion zum Anzeigen, Alternativen.</summary>
    public sealed record Proposal(string Text, string Name, Food? Food, double? Grams, string? Portion, List<CoachApi.Hit> Alternatives, bool Vague);

    /// <summary>
    /// Zuordnung Wort → Lebensmittel, wie man sie einmal bestätigt hat („Tomaten“ → BLS „Tomate roh“); je Person im kv
    /// „coach.foodmap“. Schlüssel: der Name vereinfacht (klein, ohne Umlaute).
    /// </summary>
    public const string MapKey = "coach.foodmap";

    public static Dictionary<string, string> FoodMap(UserDb db) =>
        db.Get(MapKey) is { } s ? JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [] : [];

    public static void Remember(UserDb db, string name, string foodId)
    {
        var map = FoodMap(db);
        map[Bls.Fold(name).Trim()] = foodId;
        if (map.Count > 3000) map = map.TakeLast(3000).ToDictionary();
        db.Set(MapKey, JsonSerializer.Serialize(map));
    }

    /// <summary>
    /// Grundzutaten, bei denen die Suche im BLS sonst daneben läge („Salz“ → Salzkartoffeln, „Milch“ → Milchpulver): Name
    /// (vereinfacht, auch Mehrzahl) → BLS-Code.
    /// </summary>
    private static readonly Dictionary<string, string> Basics = new()
    {
        ["salz"] = "R111000", ["speisesalz"] = "R111000", ["jodsalz"] = "R113000", ["pfeffer"] = "R258100", ["zucker"] = "S111000",
        ["brauner zucker"] = "S112000", ["mehl"] = "C214100", ["weizenmehl"] = "C214100", ["butter"] = "Q611000", ["milch"] = "M111300",
        ["vollmilch"] = "M111300", ["fettarme milch"] = "M111200", ["ei"] = "E111100", ["eier"] = "E111100", ["eigelb"] = "E112100",
        ["eiweiss"] = "E113100", ["eiklar"] = "E113100", ["sahne"] = "M173800", ["schlagsahne"] = "M173800", ["wasser"] = "N110000",
        ["olivenol"] = "Q120000", ["ol"] = "Q180000", ["rapsol"] = "Q180000", ["sonnenblumenol"] = "Q320000", ["kartoffel"] = "K110100",
        ["kartoffeln"] = "K110100", ["tomate"] = "G561100", ["tomaten"] = "G561100", ["knoblauch"] = "G490100", ["knoblauchzehe"] = "G490100",
        ["knoblauchzehen"] = "G490100", ["brotchen"] = "B511000", ["semmel"] = "B511000", ["semmeln"] = "B511000", ["brot"] = "B271000",
        ["kase"] = "M400600", ["gouda"] = "M402600", ["emmentaler"] = "M304600", ["kaffee"] = "N410100", ["apfel"] = "F110100",
        ["banane"] = "F503100", ["bananen"] = "F503100", ["honig"] = "S120000", ["quark"] = "M713100",
        ["magerquark"] = "M713100", ["karotte"] = "G620100", ["karotten"] = "G620100", ["mohre"] = "G620100", ["mohren"] = "G620100",
        ["gurke"] = "G520100", ["orangensaft"] = "F603600", ["apfelsaft"] = "F110600", ["lachs"] = "T410100",
        // Rezeptzutaten: roh statt fertiger Gerichte („Spaghetti“ → Teigwaren, nicht „Spaghetti Aglio e olio“)
        ["spaghetti"] = "E401000", ["nudeln"] = "E401000", ["pasta"] = "E401000", ["penne"] = "E401000", ["teigwaren"] = "E401000",
        ["makkaroni"] = "E401000", ["tagliatelle"] = "E401000", ["fusilli"] = "E401000", ["reis"] = "C352000", ["hackfleisch"] = "U010100",
        ["hack"] = "U010100", ["rinderhackfleisch"] = "U010100", ["rinderhack"] = "U010100", ["gemischtes hackfleisch"] = "U010100",
        ["schweinehackfleisch"] = "U020100", ["zwiebel"] = "G480100", ["zwiebeln"] = "G480100", ["paprika"] = "G543100",
        ["paprikaschote"] = "G543100", ["rote paprika"] = "G543100", ["gelbe paprika"] = "G542100", ["grune paprika"] = "G541100",
        ["champignons"] = "K701100", ["champignon"] = "K701100", ["zucchini"] = "G582100", ["spinat"] = "G211100", ["lauch"] = "G470100",
        ["porree"] = "G470100", ["blumenkohl"] = "G311100", ["tomatenmark"] = "R160000", ["dose tomaten"] = "G568900",
        ["geschalte tomaten"] = "G568900", ["gemusebruhe"] = "X416243", ["hahnchenbrust"] = "V416100", ["hahnchenbrustfilet"] = "V416100",
        ["parmesan"] = "M306400", ["mozzarella"] = "M032100", ["sellerie"] = "G660100", ["knollensellerie"] = "G660100",
        ["staudensellerie"] = "G220100", ["stangensellerie"] = "G220100", ["bleichsellerie"] = "G220100", ["selleriestangen"] = "G220100",
    };

    /// <summary>Was man ohne Mengenangabe in Kaffee oder Tee gibt: ein Schuss Milch, ein Teelöffel Zucker.</summary>
    private static readonly Dictionary<string, (double Grams, string Portion)> InDrink = new()
    {
        ["milch"] = (30, "1 Schuss"), ["vollmilch"] = (30, "1 Schuss"), ["fettarme milch"] = (30, "1 Schuss"), ["hafermilch"] = (30, "1 Schuss"),
        ["sahne"] = (20, "1 Schuss"), ["kaffeesahne"] = (10, "1 Portion"), ["zucker"] = (5, "1 TL"), ["honig"] = (7, "1 TL"),
    };

    /// <summary>
    /// Bestes Lebensmittel zu einem Namen: bestätigte Zuordnung, Grundzutat, selbst Gegessenes, Haushalt, gemerkte Produkte, BLS (roh bevorzugt).
    /// Für die Zutaten eines Rezepts ohne Rezepte (recipes: false) - ein Rezept als Zutat würde sich sonst selbst ausrechnen wollen.
    /// </summary>
    public static (Food? Best, List<CoachApi.Hit> Alternatives) Match(UserSpace space, CoachFoods foods, string name, DateOnly today, bool recipes = true)
    {
        Food? Resolve(string id) => !recipes && id.StartsWith("recipe:", StringComparison.Ordinal) ? null : CoachApi.Resolve(space, foods, id);
        var db = space.Db;
        var query = Bls.Words(Bls.Fold(name));
        if (query.Length == 0) return (null, []);
        var hits = new List<Food>();
        if (FoodMap(db).TryGetValue(Bls.Fold(name).Trim(), out var mapped) && Resolve(mapped) is { } m) hits.Add(m);
        if (Basics.TryGetValue(Bls.Fold(name).Trim(), out var basic) && Bls.Get(basic) is { } b) hits.Add(Bls.ToFood(b));
        foreach (var r in CoachStore.RecentFoods(db, null, today, 300))
            if (Bls.Score(query, Bls.Fold(r.Name + " " + r.Brand), Bls.Words(Bls.Fold(r.Name + " " + r.Brand))) > 0 && Resolve(r.Food) is { } f) hits.Add(f);
        hits.AddRange(foods.SearchOwn(name, 5));
        hits.AddRange(foods.SearchCached(name, 5));
        hits.AddRange(Bls.Search(name, 10).Select(Bls.ToFood));
        var distinct = hits.DistinctBy(f => f.Id).ToList();
        // Aus dem BLS nur, was wirklich passt - sonst lieber nicht zugeordnet („Kümmel“ → nicht „Kümmelwurst“)
        var best = distinct.FirstOrDefault(f => f.Source != "bls" || f.Id == "bls:" + basic || Bls.Fits(f.Name, name));
        return (best, distinct.Where(f => f != best).Take(5).Select(CoachApi.Hit.Of).ToList());
    }

    /// <summary>Menge in Gramm (bzw. ml) zu einem zerlegten Posten und einem Lebensmittel; null, wenn sie sich nicht bestimmen lässt.</summary>
    public static (double? Grams, string? Portion) Amount(FoodText.Item item, Food? food)
    {
        if (item.Grams is double g) return (g, Fmt(item.Amount, item.Unit));
        if (item.Ml is double ml) return (ml, Fmt(item.Amount, item.Unit));
        if (food == null) return (null, null);
        var count = item.Amount ?? 1;
        // EL/TL ohne Gramm: übliche Mengen
        if (item.Unit is "EL") return (count * (food.Portions.FirstOrDefault(p => p.Label.Contains("EL"))?.Grams ?? 15), Fmt(item.Amount, item.Unit));
        if (item.Unit is "TL") return (count * (food.Portions.FirstOrDefault(p => p.Label.Contains("TL"))?.Grams ?? 5), Fmt(item.Amount, item.Unit));
        if (item.Vague) return (food.Liquid ? 10 : 2, item.Unit ?? "etwas");
        // Kleine Küchenmengen
        var small = (item.Unit ?? "").ToLowerInvariant() switch { "prise" or "prisen" => 0.5, "msp" or "messerspitze" => 0.3, "zehe" or "zehen" => 4, "bund" => 30, "spritzer" => 2, _ => 0 };
        if (small > 0) return (Math.Round(count * small, 1), Fmt(item.Amount, item.Unit));
        // Stück, Scheibe, Portion …: die passende Portion des Lebensmittels, sonst die erste genannte
        var unit = (item.Unit ?? "").ToLowerInvariant();
        var portion = food.Portions.FirstOrDefault(p => unit.Length > 0 && p.Label.Contains(unit, StringComparison.OrdinalIgnoreCase))
            ?? food.Portions.FirstOrDefault(p => p.Grams != 100) ?? food.Portions.FirstOrDefault();
        return portion == null ? (null, null) : (Math.Round(count * portion.Grams, 1), count == 1 ? portion.Label : $"{count:0.##} × {portion.Label}");
    }

    private static string? Fmt(double? amount, string? unit) =>
        amount == null ? unit : (amount.Value.ToString("0.##", CultureInfo.GetCultureInfo("de-DE")) + (unit != null ? " " + unit : ""));

    /// <summary>Freitext → Vorschläge je Posten (zum Bestätigen in der Oberfläche).</summary>
    public static List<Proposal> Parse(UserSpace space, CoachFoods foods, string text, DateOnly today)
    {
        var list = new List<Proposal>();
        string? drink = null;
        foreach (var item in FoodText.Meal(text).Take(20))
        {
            var (food, alts) = Match(space, foods, item.Name, today);
            var (grams, portion) = Amount(item, food);
            var folded = Bls.Fold(item.Name).Trim();
            // „Kaffee mit Milch und Zucker“: ohne Menge nur ein Schuss bzw. ein Teelöffel
            if (drink != null && item is { Amount: null, Unit: null, Grams: null, Ml: null } && InDrink.TryGetValue(folded, out var small))
                (grams, portion) = small;
            if (Regex.IsMatch(folded, @"kaffee|tee\b|cappuccino|espresso|latte|kakao")) drink = folded;
            else if (!InDrink.ContainsKey(folded)) drink = null;
            list.Add(new Proposal(text, item.Name, food, grams, portion, alts, item.Vague));
        }
        return list;
    }
}
