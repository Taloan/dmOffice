namespace DmOffice.Core;

/// <summary>
/// Naiver Bayes „Eintrag gegen den Rest“ ohne Datenbankbezug - gemeinsam für die Kategorien der Dateiablage und DMS.
/// Gezählt wird je Merkmal (Wort, Ordner, Absender …), wie oft es bei Dokumenten des Eintrags (pos) bzw. bei ausdrücklich
/// abgelehnten (neg) vorkam; Dokumente anderer Einträge zählen als „Rest“ (verwandte Einträge, etwa Ober- und Unterbegriffe,
/// nicht).
/// </summary>
public static class Learner
{
    /// <summary>Gelernt wird erst ab so vielen Dokumenten je Seite, entschieden nur bei so hoher Wahrscheinlichkeit.</summary>
    public const int MinTrained = 3;
    public const double MinProb = 0.85;

    /// <summary>
    /// Wahrscheinlichkeit, dass ein Dokument mit diesen Merkmalen zum Eintrag gehört; null, wenn zu wenig gelernt ist oder keines der
    /// Merkmale je vorkam.
    /// </summary>
    /// <param name="byToken">je Merkmal: je Eintrag (pos, neg)</param>
    /// <param name="trained">je Eintrag: Zahl der gelernten Dokumente (pos, neg)</param>
    /// <param name="related">Einträge, die nicht als „Rest“ zählen (Ober- und Unterbegriffe)</param>
    public static double? Probability(string entry, ISet<string> related, IEnumerable<string> tokens,
        IReadOnlyDictionary<string, Dictionary<string, (int Pos, int Neg)>> byToken, IReadOnlyDictionary<string, (int Pos, int Neg)> trained)
    {
        bool Rest(string other) => other != entry && !related.Contains(other);
        var nPos = trained.TryGetValue(entry, out var own) ? own.Pos : 0;
        var nNeg = (trained.TryGetValue(entry, out var own2) ? own2.Neg : 0) + trained.Where(x => Rest(x.Key)).Sum(x => x.Value.Pos);
        if (nPos < MinTrained || nNeg < MinTrained) return null;
        double pos = Math.Log((double)nPos / (nPos + nNeg)), neg = Math.Log((double)nNeg / (nPos + nNeg));
        bool any = false;
        foreach (var t in tokens)
        {
            if (!byToken.TryGetValue(t, out var per)) continue;
            var p = per.TryGetValue(entry, out var mine) ? mine.Pos : 0;
            var n = (per.TryGetValue(entry, out var mine2) ? mine2.Neg : 0) + per.Where(x => Rest(x.Key)).Sum(x => x.Value.Pos);
            if (p == 0 && n == 0) continue;
            any = true;
            pos += Math.Log((p + 0.1) / (nPos + 0.2));
            neg += Math.Log((n + 0.1) / (nNeg + 0.2));
        }
        if (!any) return null;
        return 1 / (1 + Math.Exp(neg - pos));
    }

    /// <summary>Ja (≥ <see cref="MinProb"/>), nein (≤ 1 − MinProb) oder unklar.</summary>
    public static bool? Decide(double? probability) =>
        probability is not { } p ? null : p >= MinProb ? true : 1 - p >= MinProb ? false : null;
}
