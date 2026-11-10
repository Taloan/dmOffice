using System.Globalization;
using System.Text.RegularExpressions;

namespace DmOffice.Notes;

/// <summary>
/// Grobe Nährwerte eines Rezepts aus seinen Zutatenzeilen (Rezeptsuche): jede Zeile zerlegt (FoodText), dem passenden
/// Lebensmittel im BLS zugeordnet (Grundzutat, sonst bester Treffer), Menge in Gramm; Summe je Portion.
/// </summary>
public static class RecipeNutrition
{
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

    /// <summary>Nährwerte je Portion aus Zutatenzeilen; Unmatched: Zeilen ohne Zuordnung.</summary>
    public static (Nutrients PerServing, int Unmatched) Estimate(IEnumerable<string> ingredients, int servings)
    {
        var total = Nutrients.Zero;
        int unmatched = 0;
        foreach (var text in ingredients.Take(60))
        {
            var item = FoodText.Ingredient(text);
            if (item == null) continue;
            var food = Match(item.Name);
            var (grams, _) = Amount(item, food);
            if (food != null && grams is double g && g > 0) total = Add(total, food.Per100.For(g));
            else if (!item.Vague) unmatched++;
        }
        return (Round(Scale(total, 1.0 / Math.Max(1, servings))), unmatched);
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

    /// <summary>Bestes Lebensmittel zu einem Namen: Grundzutat, sonst BLS (nur, was wirklich passt - „Kümmel“ ist keine „Kümmelwurst“).</summary>
    public static Food? Match(string name)
    {
        var query = Bls.Words(Bls.Fold(name));
        if (query.Length == 0) return null;
        var hits = new List<Food>();
        if (Basics.TryGetValue(Bls.Fold(name).Trim(), out var basic) && Bls.Get(basic) is { } b) hits.Add(Bls.ToFood(b));
        hits.AddRange(Bls.Search(name, 10).Select(Bls.ToFood));
        return hits.DistinctBy(f => f.Id).FirstOrDefault(f => f.Id == "bls:" + basic || Bls.Fits(f.Name, name));
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

    private static Nutrients Add(Nutrients a, Nutrients b) => new(a.Kcal + b.Kcal, a.Protein + b.Protein, a.Fat + b.Fat, a.Carbs + b.Carbs,
        (a.Fiber ?? 0) + (b.Fiber ?? 0), (a.Sugar ?? 0) + (b.Sugar ?? 0), (a.Salt ?? 0) + (b.Salt ?? 0), (a.SatFat ?? 0) + (b.SatFat ?? 0));
    private static Nutrients Scale(Nutrients n, double f) => n.For(f * 100);
    private static Nutrients Round(Nutrients n) => new(Math.Round(n.Kcal), Math.Round(n.Protein, 1), Math.Round(n.Fat, 1), Math.Round(n.Carbs, 1),
        n.Fiber is double fi ? Math.Round(fi, 1) : null, n.Sugar is double su ? Math.Round(su, 1) : null, n.Salt is double sa ? Math.Round(sa, 2) : null,
        n.SatFat is double sf ? Math.Round(sf, 1) : null);
}
