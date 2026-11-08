using System.Globalization;

namespace DmOffice.Coach;

/// <summary>
/// Richtwerte (rw:&lt;Name&gt;): rund 460 Gerichte und Lebensmittel mit üblicher Portion, Nährwerten je 100 g und typischen Allergenen
/// (Coach/Bls/richtwerte.tsv) - Schätzwerte für das, was BLS nicht oder nur je 100 g kennt (Imbiss, Gerichte, Kuchen, Getränke).
/// </summary>
public static class Richtwerte
{
    public sealed record Entry(string Key, string Category, string Name, string Portion, double Grams, Nutrients Per100, List<string> Allergens, string Folded, string[] Words);

    private static readonly Lazy<Dictionary<string, Entry>> All = new(Load);

    private static Dictionary<string, Entry> Load()
    {
        var map = new Dictionary<string, Entry>();
        using var s = typeof(Richtwerte).Assembly.GetManifestResourceStream("DmOffice.Coach.Data.richtwerte.tsv");
        if (s == null) return map;
        using var r = new StreamReader(s);
        static double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);
        while (r.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var c = line.Split('\t');
            if (c.Length < 8) continue;
            var folded = Bls.Fold(c[1]);
            var key = string.Join('-', Bls.Words(folded));
            if (key.Length == 0 || map.ContainsKey(key)) continue;
            map[key] = new Entry(key, c[0], c[1], Label(c[2]), D(c[3]), new Nutrients(D(c[4]), D(c[5]), D(c[6]), D(c[7])),
                c.Length > 8 && c[8].Length > 0 ? [.. c[8].Split(',')] : [], folded, Bls.Words(folded));
        }
        return map;
    }

    /// <summary>„1 Stück/Portion“ → „1 Stück“, „kleine Portion“ → „1 kleine Portion“.</summary>
    private static string Label(string p)
    {
        p = p.Trim();
        if (p.Contains('/')) p = p[..p.IndexOf('/')];
        return char.IsDigit(p[0]) ? p : "1 " + p;
    }

    public static Entry? Get(string key) => All.Value.GetValueOrDefault(key);

    public static List<Entry> Search(string q, int limit = 10)
    {
        var query = Bls.Words(Bls.Fold(q));
        if (query.Length == 0) return [];
        return All.Value.Values.Select(e => (e, s: Bls.Score(query, e.Folded, e.Words))).Where(x => x.s > 0)
            .OrderByDescending(x => x.s).ThenBy(x => x.e.Name.Length).Take(limit).Select(x => x.e).ToList();
    }

    public static Food ToFood(Entry e)
    {
        var liquid = e.Category.StartsWith("Getr", StringComparison.Ordinal) || Portions.IsLiquid(e.Name);
        var portions = new List<Portion> { new(e.Portion, e.Grams) };
        portions.AddRange(Portions.For(e.Name, liquid).Where(p => portions.All(x => x.Label != p.Label)));
        return new Food("rw:" + e.Key, e.Name, null, "rw", e.Per100, portions, liquid, Info: new OffInfo(null, null, null, [], null, e.Allergens));
    }
}
