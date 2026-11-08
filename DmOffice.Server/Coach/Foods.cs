using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DmOffice.Coach;

/// <summary>Nährwerte je 100 g (bei Getränken je 100 ml). Null: unbekannt.</summary>
public sealed record Nutrients(double Kcal, double Protein, double Fat, double Carbs, double? Fiber = null, double? Sugar = null, double? Salt = null, double? SatFat = null)
{
    public static readonly Nutrients Zero = new(0, 0, 0, 0);

    /// <summary>Für eine Menge in g bzw. ml.</summary>
    public Nutrients For(double amount)
    {
        var f = amount / 100.0;
        return new(Kcal * f, Protein * f, Fat * f, Carbs * f, Fiber * f, Sugar * f, Salt * f, SatFat * f);
    }

    /// <summary>Passt die Energie zu Eiweiß, Kohlenhydraten und Fett (4/4/9 kcal, Ballaststoffe 2 kcal)? Grob, für Hinweise.</summary>
    public bool Plausible()
    {
        var calc = 4 * Protein + 4 * Carbs + 9 * Fat + 2 * (Fiber ?? 0);
        if (Kcal < 5 && calc < 5) return true;
        return Math.Abs(calc - Kcal) <= Math.Max(25, Kcal * 0.2);
    }
}

/// <summary>Portion zum Auswählen: „1 Scheibe“ = 50 g.</summary>
public sealed record Portion(string Label, double Grams);

/// <summary>
/// Ein Lebensmittel, egal woher: bls:&lt;Code&gt; (Bundeslebensmittelschlüssel), off:&lt;Barcode&gt; (Open Food Facts),
/// own:&lt;Kennung&gt; (selbst angelegt im Haushalt).
/// </summary>
public sealed record Food(string Id, string Name, string? Brand, string Source, Nutrients Per100, List<Portion> Portions, bool Liquid = false,
    string? Barcode = null, string? Quantity = null, OffInfo? Info = null);

/// <summary>
/// Was Open Food Facts zusätzlich weiß: Nutri-Score und Eco-Score (a–e), NOVA-Gruppe (1–4, Verarbeitungsgrad) und Merkmale
/// (palm-oil, palm-oil-free, vegan, vegetarian, gluten-free, lactose-free, organic).
/// </summary>
public sealed record OffInfo(string? Nutri, string? Eco, int? Nova, List<string> Flags, string? Ingredients = null, List<string>? Allergens = null);

/// <summary>
/// Bundeslebensmittelschlüssel 4.0 (Max Rubner-Institut, CC BY 4.0): 7.140 Grundlebensmittel und Gerichte, mitgeliefert als
/// Coach/Bls/bls40.tsv.gz (aufbereitet aus der Excel-Mappe, nur die Nährstoffe, die Coach braucht). Liegt im Speicher.
/// </summary>
public static class Bls
{
    public const string Attribution = "Max Rubner-Institut (2025): Bundeslebensmittelschlüssel (BLS), Version 4.0 — Deutsche Nährstoffdatenbank. Karlsruhe. DOI: 10.25826/Data20251217-134202-0 (CC BY 4.0)";

    public sealed record Entry(string Code, string Name, string NameEn, Nutrients Per100, Dictionary<string, double> Micro, string Folded, string[] Words);

    private static readonly Lazy<Dictionary<string, Entry>> All = new(Load);
    public static IReadOnlyCollection<Entry> Entries => All.Value.Values;
    public static Entry? Get(string code) => All.Value.GetValueOrDefault(code);

    private static Dictionary<string, Entry> Load()
    {
        using var s = typeof(Bls).Assembly.GetManifestResourceStream("DmOffice.Coach.Data.bls40.tsv.gz")
            ?? throw new InvalidOperationException("BLS-Daten fehlen im Programm.");
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        var head = r.ReadLine()!.Split('\t');
        int Col(string n) => Array.IndexOf(head, n);
        var map = new Dictionary<string, Entry>();
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            var c = line.Split('\t');
            if (c.Length < head.Length) continue;
            double? N(string n) => double.TryParse(c[Col(n)], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            var per = new Nutrients(N("ENERCC") ?? 0, N("PROT625") ?? 0, N("FAT") ?? 0, N("CHO") ?? 0, N("FIBT"), N("SUGAR"), N("NACL"), N("FASAT"));
            var micro = new Dictionary<string, double>();
            foreach (var m in new[] { "VITB12", "VITD", "VITC", "FOL", "FE", "CA", "MG", "K", "ZN", "ID", "ALC" })
                if (N(m) is double v) micro[m] = v;
            var folded = Fold(c[1]);
            map[c[0]] = new Entry(c[0], c[1], c[2], per, micro, folded, Words(folded));
        }
        return map;
    }

    /// <summary>Kleinbuchstaben, Umlaute vereinfacht - „Möhre“ findet man auch als „mohre“ und „moehre“.</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(ch switch { 'ä' => "a", 'ö' => "o", 'ü' => "u", 'ß' => "ss", 'é' or 'è' => "e", _ => ch.ToString() });
        return sb.ToString().Replace("ae", "a").Replace("oe", "o").Replace("ue", "u");
    }

    public static string[] Words(string folded) => Regex.Split(folded, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToArray();

    /// <summary>Wie gut passt ein Name zur Suche? 0: gar nicht. Ganze Wörter vor Wortanfängen vor Teilwörtern, kurze Namen vorn.</summary>
    public static double Score(string[] query, string folded, string[] words)
    {
        double score = 0;
        foreach (var q in query)
        {
            if (words.Contains(q)) score += 5;
            // Grundwort einer Zusammensetzung: „Knollensellerie“ ist Sellerie, „Selleriesuppe“ nicht
            else if (IsHead(q, words)) score += 5;
            else if (words.Any(w => w.StartsWith(q, StringComparison.Ordinal))) score += 3;
            else if (folded.Contains(q, StringComparison.Ordinal)) score += 1;
            // „Haferflocken“ findet auch „Hafer Flocken“ (BLS schreibt manches getrennt)
            else if (q.Length >= 5 && string.Concat(words).Contains(q, StringComparison.Ordinal)) score += 2;
            else return 0;
        }
        // Erstes Wort genau („Vollmilch frisch …“) vor bloßem Wortanfang („Vollmilchpulver“)
        if (words.Length > 0 && words[0] == query[0]) score += 4;
        else if (words.Length > 0 && IsHead(query[0], [words[0]])) score += 4;
        else if (words.Length > 0 && words[0].StartsWith(query[0], StringComparison.Ordinal)) score += 1;
        // Ohne Leerzeichen verglichen: „Hafer Flocken“ ist genau „Haferflocken“ und gehört vor „Haferflocken-Nussplätzchen“
        var compact = string.Concat(words);
        var q0 = string.Concat(query);
        if (compact == q0) score += 10;
        else if (compact.StartsWith(q0, StringComparison.Ordinal)) score += 3;
        // „roh“ ist bei Grundlebensmitteln meist gemeint, wenn nichts anderes dasteht
        if (words.Contains("roh")) score += 1;
        // Kurze Namen (Grundlebensmittel) vor langen (Gerichte mit Zutatenliste)
        return score - folded.Length / 12.0;
    }

    private static bool IsHead(string q, string[] words) => q.Length >= 4 && words.Any(w => w.Length > q.Length + 2 && w.EndsWith(q, StringComparison.Ordinal));

    /// <summary>Passt der Name wirklich zur Suche - jedes Wort ganz oder als Grundwort, nicht nur als Wortanfang („Kümmel“ ist keine „Kümmelwurst“)?</summary>
    public static bool Fits(string name, string query)
    {
        var words = Words(Fold(name));
        // Getrennt geschrieben zählt auch („Hafer Flocken“ sind Haferflocken)
        bool Joined(string q) => Enumerable.Range(0, words.Length).Any(i => Enumerable.Range(i + 2, words.Length - i - 1).Any(j => string.Concat(words[i..j]) == q));
        return Words(Fold(query)).All(q => words.Contains(q) || IsHead(q, words) || Joined(q) || (Synonyms.TryGetValue(q, out var x) && words.Contains(x)));
    }

    /// <summary>Alltagswörter, die der BLS anders nennt („Nudeln“ heißen dort „Teigwaren“).</summary>
    private static readonly Dictionary<string, string> Synonyms = new()
    {
        ["nudel"] = "teigwaren", ["nudeln"] = "teigwaren", ["pasta"] = "teigwaren", ["spaghetti"] = "teigwaren", ["spagetti"] = "teigwaren",
        ["makkaroni"] = "teigwaren", ["penne"] = "teigwaren", ["fusilli"] = "teigwaren", ["bandnudeln"] = "teigwaren", ["tagliatelle"] = "teigwaren",
        ["hahnchen"] = "huhn", ["hendl"] = "huhn", ["hackfleisch"] = "hack", ["semmel"] = "brotchen", ["schrippe"] = "brotchen",
        ["quark"] = "speisequark", ["sahne"] = "schlagsahne", ["erdapfel"] = "kartoffel", ["paradeiser"] = "tomate",
        ["staudensellerie"] = "bleichsellerie", ["stangensellerie"] = "bleichsellerie", ["selleriestangen"] = "bleichsellerie",
    };

    public static List<Entry> Search(string q, int limit = 30)
    {
        var query = Words(Fold(q));
        if (query.Length == 0) return [];
        // Dazu dieselbe Suche mit den BLS-Wörtern (etwas weniger Gewicht, damit wörtliche Treffer vorn bleiben)
        var alt = query.Select(w => Synonyms.TryGetValue(w, out var x) ? x : w).ToArray();
        var hasAlt = !alt.SequenceEqual(query);
        // Gerichte (BLS-Gruppe X) hinter Grundlebensmittel, außer man sucht sie genau
        return Entries.Select(e => (e, s: Math.Max(Score(query, e.Folded, e.Words), hasAlt ? Score(alt, e.Folded, e.Words) - 0.5 : 0))).Where(x => x.s > 0)
            .Select(x => (x.e, s: x.e.Code.StartsWith('X') ? x.s - 2 : x.s))
            .OrderByDescending(x => x.s).ThenBy(x => x.e.Name.Length).Take(limit).Select(x => x.e).ToList();
    }

    public static Food ToFood(Entry e) => new("bls:" + e.Code, e.Name, null, "bls", e.Per100, Portions.For(e.Name, Portions.IsLiquid(e.Name)), Portions.IsLiquid(e.Name));
}

/// <summary>
/// Portionen zum Antippen: eine kleine, kuratierte Tabelle häufiger Lebensmittel (Stichwort im Namen → Portionen), dazu
/// allgemeine Größen. Werte aus üblichen Haushaltsmengen (ein Ei ≈ 60 g, eine Scheibe Brot ≈ 50 g ...).
/// </summary>
public static class Portions
{
    /// <summary>Eingebaute Tabelle, danach die aus Coach/Bls/portionen.tsv (Wort, Portion, Gramm).</summary>
    // Erst beim ersten Gebrauch - Builtin steht weiter unten und ist beim Anlegen der statischen Felder sonst noch leer
    private static readonly Lazy<(string Word, Portion[] Portions)[]> All = new(() => [.. Builtin, .. FromFile()]);
    private static (string Word, Portion[] Portions)[] Table => All.Value;

    private static IEnumerable<(string Word, Portion[] Portions)> FromFile()
    {
        using var s = typeof(Portions).Assembly.GetManifestResourceStream("DmOffice.Coach.Data.portionen.tsv");
        if (s == null) return [];
        using var r = new StreamReader(s);
        var rows = new List<(string Word, Portion P)>();
        while (r.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var c = line.Split('\t');
            if (c.Length == 3 && double.TryParse(c[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) && g > 0)
                rows.Add((Bls.Fold(c[0].Trim()), new Portion(c[1].Trim(), g)));
        }
        return rows.GroupBy(x => x.Word).Select(g => (g.Key, g.Select(x => x.P).ToArray())).ToList();
    }

    private static readonly (string Word, Portion[] Portions)[] Builtin =
    [
        ("ei", [new("1 Ei (M)", 60), new("1 Ei (L)", 70)]),
        ("huhnerei", [new("1 Ei (M)", 60), new("1 Ei (L)", 70)]),
        ("apfel", [new("1 Apfel (mittel)", 150), new("1 Apfel (groß)", 200)]),
        ("banane", [new("1 Banane (mittel)", 120)]),
        ("birne", [new("1 Birne", 160)]),
        ("orange", [new("1 Orange", 150)]),
        ("mandarine", [new("1 Mandarine", 60)]),
        ("kiwi", [new("1 Kiwi", 75)]),
        ("pfirsich", [new("1 Pfirsich", 130)]),
        ("tomate", [new("1 Tomate", 80), new("1 Cocktailtomate", 15)]),
        ("gurke", [new("½ Gurke", 200), new("1 Scheibe", 10)]),
        ("paprika", [new("1 Paprika", 150)]),
        ("karotte", [new("1 Karotte", 70)]),
        ("mohre", [new("1 Möhre", 70)]),
        ("zwiebel", [new("1 Zwiebel", 80)]),
        ("speisezwiebel", [new("1 Zwiebel", 80)]),
        ("knoblauch", [new("1 Zehe", 4), new("1 Knolle", 40)]),
        ("gemusepaprika", [new("1 Paprika", 150)]),
        ("zucchini", [new("1 Zucchini", 200)]),
        ("kartoffel", [new("1 Kartoffel (mittel)", 100)]),
        ("avocado", [new("½ Avocado", 70)]),
        ("brot", [new("1 Scheibe", 50), new("1 dünne Scheibe", 35)]),
        ("brotchen", [new("1 Brötchen", 60)]),
        ("semmel", [new("1 Semmel", 60)]),
        ("toast", [new("1 Scheibe Toast", 25)]),
        ("brezel", [new("1 Brezel", 70)]),
        ("croissant", [new("1 Croissant", 60)]),
        ("knackebrot", [new("1 Scheibe", 10)]),
        ("kase", [new("1 Scheibe", 20), new("1 Würfel", 10)]),
        ("gouda", [new("1 Scheibe", 20)]),
        ("mozzarella", [new("1 Kugel", 125)]),
        ("feta", [new("1 Packung", 200)]),
        ("butter", [new("1 Portion", 10), new("1 TL", 5)]),
        ("margarine", [new("1 Portion", 10)]),
        ("marmelade", [new("1 TL", 10), new("1 EL", 20)]),
        ("konfitur", [new("1 TL", 10), new("1 EL", 20)]),
        ("honig", [new("1 TL", 10), new("1 EL", 20)]),
        ("nutella", [new("1 TL", 10)]),
        ("joghurt", [new("1 Becher", 150), new("1 großer Becher", 500)]),
        ("skyr", [new("1 Becher", 150), new("1 großer Becher", 450)]),
        ("quark", [new("1 Becher", 250), new("1 EL", 30)]),
        ("haferflocken", [new("1 EL", 10), new("1 Portion", 50)]),
        ("hafer flocken", [new("1 EL", 10), new("1 Portion", 50)]),
        ("musli", [new("1 Portion", 50)]),
        ("cornflakes", [new("1 Portion", 30)]),
        ("reis", [new("1 Portion gekocht", 180), new("1 Portion roh", 70)]),
        ("nudel", [new("1 Portion gekocht", 200), new("1 Portion roh", 80)]),
        ("spaghetti", [new("1 Portion gekocht", 200), new("1 Portion roh", 80)]),
        ("pizza", [new("1 Pizza", 350), new("1 Stück", 120)]),
        ("wurst", [new("1 Scheibe", 15), new("1 Würstchen", 50)]),
        ("salami", [new("1 Scheibe", 8)]),
        ("schinken", [new("1 Scheibe", 20)]),
        ("hahnchen", [new("1 Brustfilet", 150)]),
        ("schnitzel", [new("1 Schnitzel", 150)]),
        ("frikadelle", [new("1 Frikadelle", 80)]),
        ("lachs", [new("1 Filet", 125)]),
        ("schokolade", [new("1 Rippe", 6), new("1 Tafel", 100)]),
        ("keks", [new("1 Keks", 10)]),
        ("kuchen", [new("1 Stück", 100)]),
        ("torte", [new("1 Stück", 120)]),
        ("nuss", [new("1 Handvoll", 25)]),
        ("mandel", [new("1 Handvoll", 25)]),
        ("ol", [new("1 EL", 10), new("1 TL", 4)]),
        ("zucker", [new("1 TL", 4), new("1 Stück", 3)]),
        ("milch", [new("1 Glas", 200), new("1 Schuss", 20)]),
        ("saft", [new("1 Glas", 200)]),
        ("bier", [new("1 Flasche", 500), new("1 kleines Glas", 330)]),
        ("wein", [new("1 Glas", 150)]),
        ("kaffee", [new("1 Tasse", 150), new("1 Becher", 250)]),
        ("espresso", [new("1 Espresso", 30)]),
        ("cappuccino", [new("1 Tasse", 200)]),
        ("tee", [new("1 Tasse", 200)]),
        ("cola", [new("1 Glas", 200), new("1 Dose", 330)]),
        ("limonade", [new("1 Glas", 200)]),
        ("suppe", [new("1 Teller", 300)]),
        // Imbiss und Restaurant: übliche Portionen als Anhaltspunkt (Ketten wie McDonald's: klein 80 g, mittel 115 g, groß 150 g)
        ("pommes", [new("1 Portion klein", 80), new("1 Portion mittel", 115), new("1 Portion groß", 150)]),
        ("currywurst", [new("1 Currywurst", 150)]),
        ("bratwurst", [new("1 Bratwurst", 120)]),
        ("rostbratwurst", [new("1 Rostbratwurst", 50)]),
        ("bockwurst", [new("1 Bockwurst", 100)]),
        ("wiener", [new("1 Paar", 100)]),
        ("doner", [new("1 Döner", 350)]),
        ("kebab", [new("1 Döner", 350)]),
        ("durum", [new("1 Dürüm", 400)]),
        ("gyros", [new("1 Portion", 250)]),
        ("falafel", [new("1 Falafel", 20), new("1 Portion", 150)]),
        ("hamburger", [new("1 Hamburger", 105)]),
        ("cheeseburger", [new("1 Cheeseburger", 120)]),
        ("burger", [new("1 Burger", 220)]),
        ("nugget", [new("6 Nuggets", 100), new("9 Nuggets", 150)]),
        ("hot dog", [new("1 Hot Dog", 120)]),
        ("wrap", [new("1 Wrap", 220)]),
        ("sandwich", [new("1 Sandwich", 150)]),
        ("lasagne", [new("1 Portion", 350)]),
        ("auflauf", [new("1 Portion", 350)]),
        ("kartoffelsalat", [new("1 Portion", 200)]),
        ("nudelsalat", [new("1 Portion", 200)]),
        ("bratkartoffel", [new("1 Portion", 200)]),
        ("kartoffelpuree", [new("1 Portion", 200)]),
        ("knodel", [new("1 Knödel", 90)]),
        ("spatzle", [new("1 Portion", 250)]),
        ("gulasch", [new("1 Portion", 250)]),
        ("salat", [new("1 Schale", 150), new("1 große Schale", 300)]),
        ("muffin", [new("1 Muffin", 80)]),
        ("donut", [new("1 Donut", 60)]),
        ("berliner", [new("1 Berliner", 70)]),
        ("waffel", [new("1 Waffel", 60)]),
        ("pfannkuchen", [new("1 Pfannkuchen", 80)]),
        ("eiscreme", [new("1 Kugel", 60)]),
        ("speiseeis", [new("1 Kugel", 60)]),
        ("chips", [new("1 Handvoll", 25), new("1 Tüte", 175)]),
        ("popcorn", [new("1 Portion", 50)]),
    ];

    /// <summary>Tabellenwörter, die auch als Ende einer Zusammensetzung passen (längere zuerst, damit „brotchen“ vor „brot“ kommt).</summary>
    private static readonly string[] CompoundEnds = ["schokolade", "brotchen", "schinken", "joghurt", "kuchen", "quark", "milch", "wurst", "torte",
        "suppe", "kase", "brot", "saft", "bier", "salat", "auflauf", "burger"];

    private static readonly string[] LiquidWords = ["milch", "saft", "getrank", "bier", "wein", "kaffee", "espresso", "cappuccino", "tee",
        "wasser", "limonade", "cola", "schorle", "smoothie", "brühe", "bruhe", "sirup", "likor", "schnaps", "sekt", "kakao", "drink"];

    public static bool IsLiquid(string name)
    {
        var f = Bls.Fold(name);
        // „Milchschokolade“, „Teewurst“ sind keine Getränke - nur am Wortanfang und nicht, wenn Fester im Namen steckt
        if (f.Contains("schokolade") || f.Contains("pulver") || f.Contains("wurst") || f.Contains("kase") || f.Contains("brot")) return false;
        // Auch in Wortzusammensetzungen (am Anfang oder Ende): „Vollmilch“, „Apfelsaft“, „Weißbier“ - aber nicht „Schweinefleisch“, „Wassermelone“
        if (f.Contains("schwein") || f.Contains("melone") || f.Contains("traube")) return false;
        return Bls.Words(f).Any(w => LiquidWords.Any(l => w.StartsWith(l, StringComparison.Ordinal) || w.EndsWith(l, StringComparison.Ordinal)));
    }

    /// <summary>Portionen zu einem Namen: passende aus der Tabelle (erstes Wort zuerst), dann 100 g bzw. 100 ml.</summary>
    public static List<Portion> For(string name, bool liquid)
    {
        var words = Bls.Words(Bls.Fold(name));
        var folded = Bls.Fold(name);
        var list = new List<Portion>();
        // Treffer nach ihrer Stelle im Namen: das Hauptwort steht vorn - „Baguette-Brötchen mit … Tomaten“ ist ein Brötchen, keine Tomate
        var hits = new List<(int At, Portion[] Portions)>();
        foreach (var (word, portions) in Table)
        {
            var parts = word.Split(' ');
            var at = parts.Length > 1 ? folded.IndexOf(word, StringComparison.Ordinal)
                : Array.FindIndex(words, w => w == word || w.StartsWith(word, StringComparison.Ordinal) && w.Length <= word.Length + 3);
            if (at < 0) continue;
            // Mehrwortige Einträge: Stelle im Text auf die Wortzählung umrechnen (Wörter davor)
            if (parts.Length > 1) at = Bls.Words(folded[..at]).Length;
            hits.Add((at, [.. portions]));
        }
        foreach (var (_, portions) in hits.OrderBy(h => h.At))
        {
            foreach (var p in portions) if (list.All(x => x.Label != p.Label)) list.Add(p);
            if (list.Count >= 4) break;
        }
        // Sonst Wortzusammensetzungen mit bekanntem Ende: „Weizenbrötchen“, „Schnittkäse“, „Roggenmischbrot“, „Vollmilch“
        if (list.Count == 0)
            foreach (var end in CompoundEnds)
                if (words.Any(w => w.Length > end.Length && w.EndsWith(end, StringComparison.Ordinal)))
                {
                    list.AddRange(Table.First(x => x.Word == end).Portions);
                    break;
                }
        list.Add(new Portion(liquid ? "100 ml" : "100 g", 100));
        return list;
    }
}
