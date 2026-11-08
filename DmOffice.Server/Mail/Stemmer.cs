using System.Text;

namespace DmOffice.Mail;

/// <summary>
/// Wortstamm nach dem deutschen Snowball-Verfahren (snowballstem.org, „German“): „Bestellungen“, „Bestellung“ → „bestell“,
/// „versendet“ → „versendet“, „Rechnungen“ → „rechnung“. Zerlegt keine zusammengesetzten Wörter. Englische Plurale auf -s
/// kürzt es mit. Für die lernenden Kategorien (wenig Gelerntes - verwandte Formen zählen zusammen), nicht für den Spamfilter.
/// </summary>
public static class Stemmer
{
    private const string Vowels = "aeiouyäöü";
    private const string SEnding = "bdfghklmnrt";
    private const string StEnding = "bdfghklmnt";

    public static string Stem(string word)
    {
        var w = new StringBuilder(word.ToLowerInvariant().Replace("ß", "ss"));
        // u und y zwischen Vokalen zählen nicht als Vokal
        for (int i = 1; i < w.Length - 1; i++)
            if (IsVowel(w[i - 1]) && IsVowel(w[i + 1]) && w[i] is 'u' or 'y') w[i] = char.ToUpperInvariant(w[i]);
        var s = w.ToString();
        int r1 = Region(s, 0), r2 = Region(s, r1);
        if (r1 < 3) r1 = Math.Min(3, s.Length);

        // Schritt 1: em ern er | e en es | s (nach gültigem s-Ende)
        var step1 = Longest(s, ["ern", "em", "er", "en", "es", "e", "s"]);
        if (step1 != null && s.Length - step1.Length >= r1)
        {
            if (step1 == "s")
            {
                if (s.Length >= 2 && SEnding.Contains(s[^2])) s = s[..^1];
            }
            else
            {
                s = s[..^step1.Length];
                if (step1 is "e" or "en" or "es" && s.EndsWith("niss", StringComparison.Ordinal)) s = s[..^1];
            }
        }
        // Schritt 2: en er est | st (nach gültigem st-Ende und mindestens drei Buchstaben davor)
        var step2 = Longest(s, ["est", "en", "er", "st"]);
        if (step2 != null && s.Length - step2.Length >= r1)
        {
            if (step2 == "st") { if (s.Length >= 6 && StEnding.Contains(s[^3])) s = s[..^2]; }
            else s = s[..^step2.Length];
        }
        // Schritt 3: Ableitungen im Bereich R2
        bool InR2(int len) => s.Length - len >= r2;
        var step3 = Longest(s, ["heit", "keit", "isch", "lich", "end", "ung", "ig", "ik"]);
        if (step3 != null && InR2(step3.Length))
        {
            switch (step3)
            {
                case "end" or "ung":
                    s = s[..^step3.Length];
                    if (s.EndsWith("ig", StringComparison.Ordinal) && InR2(2) && !s.EndsWith("eig", StringComparison.Ordinal)) s = s[..^2];
                    break;
                case "ig" or "ik" or "isch":
                    if (!s[..^step3.Length].EndsWith('e')) s = s[..^step3.Length];
                    break;
                case "lich" or "heit":
                    s = s[..^step3.Length];
                    if ((s.EndsWith("er", StringComparison.Ordinal) || s.EndsWith("en", StringComparison.Ordinal)) && s.Length - 2 >= r1) s = s[..^2];
                    break;
                case "keit":
                    s = s[..^4];
                    if (s.EndsWith("lich", StringComparison.Ordinal) && InR2(4)) s = s[..^4];
                    else if (s.EndsWith("ig", StringComparison.Ordinal) && InR2(2)) s = s[..^2];
                    break;
            }
        }
        return s.Replace('U', 'u').Replace('Y', 'y').Replace('ä', 'a').Replace('ö', 'o').Replace('ü', 'u');
    }

    private static bool IsVowel(char c) => Vowels.Contains(c);

    /// <summary>Beginn des Bereichs nach dem ersten Nicht-Vokal, der auf einen Vokal folgt (ab from).</summary>
    private static int Region(string s, int from)
    {
        for (int i = Math.Max(1, from + 1); i < s.Length; i++)
            if (!IsVowel(s[i]) && IsVowel(s[i - 1])) return i + 1;
        return s.Length;
    }

    private static string? Longest(string s, string[] suffixes) =>
        suffixes.Where(x => s.EndsWith(x, StringComparison.Ordinal)).OrderByDescending(x => x.Length).FirstOrDefault();
}
