using System.Globalization;
using System.Text.RegularExpressions;

namespace DmOffice.Notes;

/// <summary>
/// Zerlegt eine Zutatenzeile in Menge und Lebensmittel - reine Regeln, ohne Datenbank („200 g Mehl“, „½ Bund Petersilie“,
/// „1 Dose Tomaten (400 g)“). Das Zuordnen zum BLS und das Umrechnen von Stück in Gramm (Portionstabelle) macht der Aufrufer.
/// </summary>
public static class FoodText
{
    /// <summary>
    /// Ein Posten. Amount fehlt, wenn keine Menge dasteht („Salz“, „etwas Butter“). Unit ist vereinheitlicht (g, ml, EL, TL,
    /// Stück …), fehlt bei einer bloßen Zahl („2 Eier“ = 2 Stück). Grams bzw. Ml nur, wenn sie sich ohne Lebensmittel bestimmen
    /// lassen (Gewicht, Volumen, Angabe in Klammern). Note: was nach dem Komma bzw. in Klammern stand („gehackt“).
    /// </summary>
    public sealed record Item(double? Amount, string? Unit, string Name, string? Note = null, double? Grams = null, double? Ml = null)
    {
        /// <summary>Ungefähre Menge statt Zahl („etwas“, „nach Geschmack“, „eine Prise“ ohne Zahl).</summary>
        public bool Vague { get; init; }
    }

    // Einheit (vereinheitlicht) -> Gramm bzw. Milliliter je Einheit, sofern unabhängig vom Lebensmittel
    private sealed record UnitDef(string Name, double? Grams = null, double? Ml = null);

    private static readonly Dictionary<string, UnitDef> Units = Build();

    private static Dictionary<string, UnitDef> Build()
    {
        var d = new Dictionary<string, UnitDef>(StringComparer.OrdinalIgnoreCase);
        void Add(UnitDef u, params string[] names) { foreach (var n in names) d[n] = u; }
        Add(new("g", Grams: 1), "g", "gr", "gr.", "gramm", "gram", "grams");
        Add(new("kg", Grams: 1000), "kg", "kilo", "kilogramm");
        Add(new("mg", Grams: 0.001), "mg", "milligramm");
        Add(new("ml", Ml: 1), "ml", "milliliter");
        Add(new("cl", Ml: 10), "cl", "zentiliter");
        Add(new("dl", Ml: 100), "dl", "deziliter");
        Add(new("l", Ml: 1000), "l", "liter", "ltr", "ltr.");
        // Löffel und Tasse: Volumen nach üblichem Maß (EL 15 ml, TL 5 ml, Tasse 250 ml) - Gramm hängen vom Lebensmittel ab
        Add(new("EL", Ml: 15), "el", "el.", "essl", "essl.", "esslöffel", "eßlöffel", "tbsp", "tablespoon", "tablespoons");
        Add(new("TL", Ml: 5), "tl", "tl.", "teel", "teel.", "teelöffel", "tsp", "teaspoon", "teaspoons");
        Add(new("Tasse", Ml: 250), "tasse", "tassen", "cup", "cups");
        Add(new("Glas"), "glas", "gläser", "glass");
        Add(new("Becher"), "becher");
        Add(new("Dose"), "dose", "dosen", "can", "cans");
        Add(new("Packung"), "packung", "packungen", "pck", "pck.", "pk", "pkg", "päckchen", "pack");
        Add(new("Bund"), "bund", "bunch");
        Add(new("Zehe"), "zehe", "zehen", "clove", "cloves");
        Add(new("Prise"), "prise", "prisen", "pinch");
        Add(new("Msp"), "msp", "msp.", "messerspitze", "messerspitzen");
        Add(new("Scheibe"), "scheibe", "scheiben", "slice", "slices");
        Add(new("Stück"), "stück", "stk", "stk.", "st", "st.", "piece", "pieces");
        Add(new("Portion"), "portion", "portionen", "serving");
        Add(new("Handvoll"), "handvoll", "hand voll");
        Add(new("Zweig"), "zweig", "zweige", "stiel", "stiele");
        Add(new("Blatt"), "blatt", "blätter");
        Add(new("Würfel"), "würfel");
        Add(new("Flasche"), "flasche", "flaschen", "bottle");
        Add(new("Schuss"), "schuss", "spritzer", "dash");
        Add(new("Kopf"), "kopf", "köpfe");
        Add(new("Knolle"), "knolle", "knollen");
        Add(new("Stange"), "stange", "stangen");
        Add(new("Tüte"), "tüte", "tüten", "beutel");
        Add(new("Riegel"), "riegel");
        Add(new("Kugel"), "kugel", "kugeln");
        return d;
    }

    private static readonly Dictionary<string, double> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ein"] = 1, ["eine"] = 1, ["einen"] = 1, ["einem"] = 1, ["einer"] = 1, ["eins"] = 1, ["zwei"] = 2, ["drei"] = 3,
        ["vier"] = 4, ["fünf"] = 5, ["sechs"] = 6, ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9, ["zehn"] = 10, ["zwölf"] = 12,
        ["halb"] = 0.5, ["halbe"] = 0.5, ["halben"] = 0.5, ["halber"] = 0.5, ["anderthalb"] = 1.5, ["eineinhalb"] = 1.5,
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["half"] = 0.5,
    };

    private static readonly string[] VagueWords = ["etwas", "einige", "einigen", "wenig", "ein wenig", "ein paar", "nach geschmack", "nach belieben", "evtl.", "evtl", "eventuell", "optional", "some", "to taste"];

    private static readonly Dictionary<char, double> Fractions = new() { ['½'] = 0.5, ['¼'] = 0.25, ['¾'] = 0.75, ['⅓'] = 1 / 3d, ['⅔'] = 2 / 3d, ['⅛'] = 0.125 };

    // Zahl: 2 | 2,5 | 2.5 | 1/2 | 1 1/2 | 1 ½ | ½ | 2-3 | 2–3 | 2 bis 3 (Spanne: Mittelwert)
    private static readonly Regex Number = new(
        @"^(?:(?<f>\d+/\d+)|(?<a>\d+(?:[.,]\d+)?)(?:\s+(?<f>\d+/\d+)|\s*(?<f>[½¼¾⅓⅔⅛]))?|(?<f>[½¼¾⅓⅔⅛]))(?:\s*(?:-|–|bis)\s*(?<b>\d+(?:[.,]\d+)?))?",
        RegexOptions.CultureInvariant);

    /// <summary>Eine Zutatenzeile zerlegen. Leere Zeilen, Überschriften („Für den Teig:“) und reine Hinweise ergeben null.</summary>
    public static Item? Ingredient(string line)
    {
        var s = Clean(line);
        if (s.Length == 0 || s.EndsWith(':')) return null;

        // Hinweis in Klammern abnehmen; eine Gewichts-/Volumenangabe darin gilt als Menge („1 Dose Tomaten (400 g)“)
        string? note = null;
        double? grams = null, ml = null;
        var paren = Regex.Match(s, @"\(([^)]*)\)");
        if (paren.Success)
        {
            var inner = paren.Groups[1].Value.Trim();
            if (ReadAmount(inner) is { } amount && amount.Unit is { } u && (u.Grams != null || u.Ml != null) && amount.Rest.Trim().Length == 0)
            {
                grams = u.Grams * amount.Value; ml = u.Ml * amount.Value;
            }
            else if (inner.Length > 0) note = inner;
            s = (s[..paren.Index] + s[(paren.Index + paren.Length)..]).Trim();
            s = Regex.Replace(s, @"\s{2,}", " ");
        }

        // Was nach dem ersten Komma steht, ist Zubereitung („Zwiebeln, gehackt“)
        var comma = s.IndexOf(',');
        if (comma > 0 && !char.IsDigit(s[comma - 1]))
        {
            var after = s[(comma + 1)..].Trim();
            if (after.Length > 0) note = note == null ? after : after + "; " + note;
            s = s[..comma].Trim();
        }

        var vague = false;
        foreach (var v in VagueWords)
            if (s.StartsWith(v + " ", StringComparison.OrdinalIgnoreCase) || s.Equals(v, StringComparison.OrdinalIgnoreCase))
            {
                vague = true; s = s[v.Length..].Trim(); break;
            }
        foreach (var v in VagueWords)
            if (s.EndsWith(" " + v, StringComparison.OrdinalIgnoreCase))
            {
                vague = true; s = s[..^(v.Length + 1)].Trim(); break;
            }

        var read = ReadAmount(s);
        double? value = read?.Value;
        var unit = read?.Unit;
        var name = (read?.Rest ?? s).Trim();
        name = Regex.Replace(name, @"^(?:von|vom|of)\s+", "", RegexOptions.IgnoreCase).Trim(' ', '-', '–', '.');
        if (name.Length == 0) return null;
        if (unit?.Grams != null && value != null) grams ??= unit.Grams * value;
        if (unit?.Ml != null && value != null) ml ??= unit.Ml * value;
        // Prise oder Messerspitze ohne Zahl: eine
        if (value == null && unit != null) value = 1;
        return new Item(value is { } x ? Math.Round(x, 3) : null, unit?.Name, name, note, Round(grams), Round(ml)) { Vague = vague || (value == null && unit == null && IsSeasoning(name)) };
    }

    private sealed record Amount(double Value, UnitDef? Unit, string Rest);

    /// <summary>Menge und Einheit am Anfang („200 g“, „1 ½ EL“, „zwei Scheiben“, „200g“); Rest = was dahinter steht.</summary>
    private static Amount? ReadAmount(string s)
    {
        double? value = null;
        var rest = s;
        var m = Number.Match(s);
        if (m.Success && m.Length > 0 && (m.Groups["a"].Success || m.Groups["f"].Success))
        {
            var a = m.Groups["a"].Success ? Parse(m.Groups["a"].Value) : 0;
            var f = m.Groups["f"].Success ? Fraction(m.Groups["f"].Value) : 0;
            var first = a + f;
            if (m.Groups["b"].Success)
            {
                value = (first + Parse(m.Groups["b"].Value)) / 2;
            }
            else value = first;
            rest = s[m.Length..].TrimStart();
        }
        else
        {
            var word = Regex.Match(s, @"^(\p{L}+)\s+");
            if (word.Success && Words.TryGetValue(word.Groups[1].Value, out var w))
            {
                value = w;
                rest = s[word.Length..];
                // „ein halber Liter“, „eine halbe Zwiebel“
                var half = Regex.Match(rest, @"^(halbe[nrs]?|halb)\s+", RegexOptions.IgnoreCase);
                if (half.Success) { value = 0.5; rest = rest[half.Length..]; }
            }
        }
        if (value == null)
        {
            // Einheit ohne Zahl: „Prise Salz“, „Msp. Muskat“
            var u0 = Regex.Match(s, @"^(\p{L}+\.?)\s+");
            if (u0.Success && Units.TryGetValue(u0.Groups[1].Value, out var only) && only.Grams == null && only.Ml == null)
                return new Amount(1, only, s[u0.Length..]);
            return null;
        }
        // Einheit direkt dahinter (auch ohne Leerzeichen: „200g“, „1,5l“)
        var unit = Regex.Match(rest, @"^(\p{L}+\.?)(?=\s|$)");
        if (unit.Success && Units.TryGetValue(unit.Groups[1].Value, out var def))
            return new Amount(value.Value, def, rest[unit.Length..].TrimStart());
        return new Amount(value.Value, null, rest);
    }

    private static readonly string[] Seasonings = ["salz", "pfeffer", "zucker", "muskat", "paprikapulver", "chili", "kräuter", "öl zum braten", "salt", "pepper"];
    private static bool IsSeasoning(string name) => Seasonings.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));

    private static double Parse(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    private static double Fraction(string s)
    {
        if (s.Length == 1 && Fractions.TryGetValue(s[0], out var f)) return f;
        var parts = s.Split('/');
        return parts.Length == 2 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d != 0
            ? double.Parse(parts[0], CultureInfo.InvariantCulture) / d : 0;
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;

    /// <summary>Aufzählungszeichen und Kästchen weg, „1½“ → „1 ½“, Leerraum vereinheitlicht.</summary>
    private static string Clean(string s)
    {
        s = s.Replace(' ', ' ').Replace(' ', ' ').Trim();
        s = s.TrimStart('-', '–', '•', '*', '·', '☐', '☑', '☒', ' ').Trim();
        s = Regex.Replace(s, @"(\d)([½¼¾⅓⅔⅛])", "$1 $2");
        return Regex.Replace(s, @"[ \t]{2,}", " ");
    }
}
