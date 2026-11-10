using System.Text.Json;

namespace DmOffice.Chat;

/// <summary>
/// Arten von Terminen (Stufe 5, Schritt 2): Gesundheit, Schule &amp; Kita, Abholen &amp; Bringen, Sport … Gespeichert als
/// CATEGORIES im Termin (der deutsche Name) - so kommen sie auch in anderen Kalendern an. Pukki ordnet neue Termine
/// selbst ein; im Termin-Dialog lässt sich die Art ändern. Das Einordnen nutzt Startwörter je Thema
/// (Chat/wissen/themen.json) und eine Assoziationstabelle aus Wikidata (Chat/wissen/assoziationen.json, CC0,
/// erzeugt mit tools/pukki-wissen/wikidata.mjs) - ohne KI, alles auf dem Server.
/// </summary>
public static class PukkiArts
{
    public sealed record Art(string Key, string De, string En, string Icon);

    public static readonly Art[] All =
    [
        new("gesundheit", "Gesundheit", "Health", "🩺"),
        new("schule", "Schule & Kita", "School & daycare", "🎒"),
        new("abholen", "Abholen & Bringen", "Pick-up & drop-off", "🚸"),
        new("sport", "Sport", "Sports", "⚽"),
        new("kurse", "Musik & Kurse", "Music & classes", "🎵"),
        new("familie", "Familie & Feiern", "Family & celebrations", "🎉"),
        new("freizeit", "Freizeit & Kultur", "Leisure & culture", "🎭"),
        new("reisen", "Reisen", "Travel", "✈️"),
        new("arbeit", "Arbeit", "Work", "💼"),
        new("behoerden", "Behörden & Finanzen", "Authorities & finance", "📄"),
        new("haushalt", "Haushalt & Handwerker", "Household & repairs", "🔧"),
        new("auto", "Auto", "Car", "🚗"),
        new("tiere", "Tiere", "Pets", "🐾"),
    ];

    public static Art? ByKey(string? key) => All.FirstOrDefault(a => a.Key == key);

    /// <summary>Die Art aus den Kategorien eines Termins (deutscher oder englischer Name).</summary>
    public static Art? FromCategories(IEnumerable<string>? categories) =>
        categories?.Select(c => All.FirstOrDefault(a => string.Equals(a.De, c.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(a.En, c.Trim(), StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(a => a != null);

    /// <summary>Ist die Kategorie eine Art (dann ersetzt eine neue Art sie)?</summary>
    public static bool IsArt(string category) => FromCategories([category]) != null;

    private sealed record Theme(string Art, List<string> De, List<string> En);

    private sealed class Knowledge
    {
        /// <summary>Startwort (Grundform, ohne Umlaute) → Arten.</summary>
        public readonly Dictionary<string, HashSet<string>> Seeds = new(StringComparer.Ordinal);
        /// <summary>Wort → verwandte Wörter (wichtigste zuerst).</summary>
        public readonly Dictionary<string, string[]> Assoc = new(StringComparer.Ordinal);
    }

    private static readonly Lazy<Knowledge> Data = new(Load);

    private static Stream Resource(string name) =>
        typeof(PukkiArts).Assembly.GetManifestResourceStream("DmOffice.Chat.wissen." + name) ?? throw new FileNotFoundException(name);

    private static Knowledge Load()
    {
        var k = new Knowledge();
        var themes = JsonSerializer.Deserialize<List<Theme>>(Resource("themen.json"), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        foreach (var t in themes)
            foreach (var w in t.De.Concat(t.En))
                foreach (var key in Keys(w))
                {
                    if (!k.Seeds.TryGetValue(key, out var set)) k.Seeds[key] = set = new();
                    set.Add(t.Art);
                }
        using var doc = JsonDocument.Parse(Resource("assoziationen.json"));
        foreach (var lang in doc.RootElement.EnumerateObject())
            foreach (var e in lang.Value.EnumerateObject())
            {
                var list = e.Value.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
                foreach (var key in Keys(e.Name))
                    k.Assoc[key] = k.Assoc.TryGetValue(key, out var old) ? old.Concat(list).Distinct().ToArray() : list;
            }
        return k;
    }

    /// <summary>Schlüssel eines Worts: klein, Grundform, ohne Umlaute.</summary>
    private static IEnumerable<string> Keys(string w) => PukkiWords.Forms(w.ToLowerInvariant()).Select(PukkiWords.Fold).Distinct();

    /// <summary>Wie viele Stichwörter und Assoziationen geladen sind (für Tests und Hinweise).</summary>
    public static (int Seeds, int Words) Size => (Data.Value.Seeds.Count, Data.Value.Assoc.Count);

    /// <summary>
    /// Art eines Termins aus Titel (und Beschreibung): Startwörter zählen am meisten, dann Wortteile („Zahnarzttermin“),
    /// dann Assoziationen („Kieferorthopäde“ → arzt → Gesundheit). Null: keine klare Art.
    /// </summary>
    public static Art? Classify(string title, string? description = null)
    {
        var k = Data.Value;
        var scores = new Dictionary<string, double>();
        void Add(IEnumerable<string> arts, double w) { foreach (var a in arts) scores[a] = scores.GetValueOrDefault(a) + w; }

        // „appointment“ sagt nichts über die Art: Auch der Friseur ist ein appointment
        var words = PukkiWords.Words(title).Select(w => (w, 1.0))
            .Concat(PukkiWords.Words(description ?? "").Take(30).Select(w => (w, 0.4)))
            .Where(x => !x.w.Equals("appointment", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (w, weight) in words)
        {
            var keys = Keys(w).ToList();
            // Ein Startwort, das zu mehreren Arten gehört („impfung“: Gesundheit und Tiere), teilt sich das Gewicht
            var direct = keys.SelectMany(x => k.Seeds.TryGetValue(x, out var s) ? s : []).Distinct().ToList();
            var found = direct.Count > 0;
            if (found) Add(direct, 3 * weight / direct.Count);
            else
            {
                // Zusammengesetzte Wörter: das längste Startwort am Anfang und das am Ende zählen je für sich
                // („kinder|arzt|termin“, „klavier|unterricht“, „fußball|training“)
                var folded = PukkiWords.Fold(w);
                var candidates = k.Seeds.Where(s => s.Key.Length >= 4 && folded.Length > s.Key.Length).ToList();
                foreach (var part in new[]
                {
                    candidates.Where(s => folded.StartsWith(s.Key)).OrderByDescending(s => s.Key.Length).FirstOrDefault(),
                    candidates.Where(s => folded.EndsWith(s.Key)).OrderByDescending(s => s.Key.Length).FirstOrDefault(),
                })
                    if (part.Key != null) { Add(part.Value, 2 * weight / part.Value.Count); found = true; }
            }
            // Assoziationen: helfen bei Gleichstand (schwach) und tragen allein, wenn kein Startwort passt; die vorderen zählen mehr
            var factor = found ? 0.3 : 1.0;
            foreach (var key in keys)
                if (k.Assoc.TryGetValue(key, out var assoc))
                {
                    for (int i = 0; i < assoc.Length; i++)
                        foreach (var ak in Keys(assoc[i]))
                            if (k.Seeds.TryGetValue(ak, out var s)) { Add(s, factor * weight * (1.0 - i * 0.05) / s.Count); break; }
                    break;
                }
        }
        if (scores.Count == 0) return null;
        var best = scores.OrderByDescending(s => s.Value).ToList();
        if (best[0].Value < 1.5) return null;
        // Knapp zwei Arten gleichauf: keine klare Art
        if (best.Count > 1 && best[1].Value >= best[0].Value * 0.9) return null;
        return ByKey(best[0].Key);
    }
}
