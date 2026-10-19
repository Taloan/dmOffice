using System.Net;
using System.Text.RegularExpressions;

namespace DmOffice.Pim;

/// <summary>
/// Vorschlaege fuer Einkaufs- und Checklisten aus einem anderen Programm uebernehmen: entweder das
/// HTML der Vorschlagsliste von AnyList (im Browser kopiert) oder einfach ein Eintrag je Zeile.
/// Nur der Vorschlagsspeicher wird gefuellt, auf die Liste selbst kommt nichts.
/// </summary>
public static partial class ListImport
{
    public sealed record Item(string Name, string? Unit);

    [GeneratedRegex(@"ALAutocompleteSuggestionItemName""><span>(?<name>.*?)<span class=""ALAutocompleteSuggestionItemDetails"">(?<details>.*?)</span>", RegexOptions.Singleline)]
    private static partial Regex AnyListCell();
    [GeneratedRegex(@"<!--.*?-->|<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex Tags();
    // "(ca. 300 g) Lasagneplatten" - Mengenangabe aus einem Rezept vorne weg
    [GeneratedRegex(@"^\([^)]*\)\s*")]
    private static partial Regex LeadingParen();
    // Einheit am Ende der Mengenangabe: "(500 g)", "(2 TL)", "(0 × 400 g)"; nur Zahlen ergeben keine Einheit
    [GeneratedRegex(@"^\(?[\d.,/½¼¾×x\s–-]*\s(?<unit>[^\d\s()]{1,10})\)?$")]
    private static partial Regex UnitOf();
    // Aufzaehlungszeichen und Kaestchen am Zeilenanfang
    [GeneratedRegex(@"^\s*(?:[-*•·☐☑✓✔]|\[[ xX]?\])\s*")]
    private static partial Regex Bullet();

    public static List<Item> Parse(string text)
    {
        var raw = new List<Item>();
        if (text.Contains("ALAutocompleteSuggestionItemName"))
        {
            foreach (Match m in AnyListCell().Matches(text))
            {
                var name = Clean(m.Groups["name"].Value);
                // Erste Zeile der Liste ist "Add "a"" - der Knopf zum Anlegen, kein Vorschlag
                if (name.StartsWith("Add \"") || name.StartsWith("„")) continue;
                var details = Clean(m.Groups["details"].Value);
                var u = UnitOf().Match(details);
                raw.Add(new Item(name, u.Success ? u.Groups["unit"].Value : null));
            }
        }
        else
        {
            foreach (var line in text.Split('\n'))
                raw.Add(new Item(Clean(Bullet().Replace(line, "")), null));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Item>();
        foreach (var it in raw)
        {
            var name = LeadingParen().Replace(it.Name, "").Trim();
            if (name.Length == 0 || name.Length > 200) continue;
            name = char.ToUpper(name[0]) + name[1..];
            if (seen.Add(name)) result.Add(it with { Name = name });
        }
        return result;
    }

    private static string Clean(string s) => Regex.Replace(WebUtility.HtmlDecode(Tags().Replace(s, "")), @"\s+", " ").Trim();

    /// <summary>Neue Vorschlaege anlegen (bekannte bleiben, wie sie sind). Liefert die Zahl der neuen.</summary>
    public static int Apply(Core.UserDb db, long id, IEnumerable<Item> items)
    {
        var m = Lists.Get(db, id);
        if (m.Type == Lists.Tasks) return 0;
        int n = 0;
        foreach (var it in items)
        {
            if (m.History.Any(h => string.Equals(h.Name, it.Name, StringComparison.OrdinalIgnoreCase))) continue;
            m.History.Add(new Lists.Known { Name = it.Name, Unit = it.Unit, Last = DateTime.UtcNow });
            n++;
        }
        if (n > 0) Lists.Save(db, id, m);
        return n;
    }

    public static int CountNew(Core.UserDb db, long id, IEnumerable<Item> items)
    {
        var m = Lists.Get(db, id);
        return items.Count(it => !m.History.Any(h => string.Equals(h.Name, it.Name, StringComparison.OrdinalIgnoreCase)));
    }
}
