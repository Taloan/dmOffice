using System.Globalization;
using System.Text.RegularExpressions;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Urteil des eigenen Mailservers als Merkmale für den lernenden Spamfilter: SpamAssassin (X-Spam-Flag, X-Spam-Status,
/// X-Spam-Score, X-Spam-Level), rspamd (X-Rspamd-Score, X-Spamd-Result), Microsoft (SCL) und einfache „X-Spam: yes“.
/// Es zählen nur Zeilen über der zweiten Received-Zeile - die hat die eigene Empfangsstation geschrieben; weiter unten kann
/// der Absender selbst „X-Spam-Status: No“ hineinschreiben. Wie stark die Merkmale wirken, lernt der Filter aus den eigenen
/// Klicks auf „Junk“ und „Kein Junk“ - ein Server, der oft danebenliegt, zählt so von selbst wenig.
/// </summary>
public static partial class SpamHeaders
{
    public static IEnumerable<string> Tokens(MimeMessage m)
    {
        var top = new List<Header>();
        int received = 0;
        foreach (var h in m.Headers)
        {
            if (h.Id == HeaderId.Received && ++received >= 2) break;
            top.Add(h);
        }
        string? Get(string name) => top.FirstOrDefault(h => h.Field.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

        var verdict = Get("X-Spam-Flag") ?? Get("X-Spam");
        // GMX und andere setzen die Kennzeichnung unter die Received-Zeilen. „Ja“ darf überall stehen (ein Absender gewinnt
        // nichts, wenn er seine eigene Mail als Spam kennzeichnet), „nein“ zählt nur oben vom eigenen Server
        if (verdict == null && SaysSpam(m)) verdict = "yes";
        if (verdict == null && Get("X-Spam-Status") is { } status) verdict = status.Split(',', ' ')[0];
        if (verdict != null)
        {
            var v = verdict.ToLowerInvariant();
            if (v.StartsWith("yes") || v == "true") yield return "srv-spam:yes";
            else if (v.StartsWith("no") || v == "false") yield return "srv-spam:no";
        }

        double? score = null;
        if (Get("X-Spam-Status") is { } st && ScoreRx().Match(st) is { Success: true } sm) score = Num(sm.Groups[1].Value);
        score ??= Num(Get("X-Spam-Score")) ?? Num(Get("X-Rspamd-Score"));
        if (score == null && Get("X-Spamd-Result") is { } sr && RspamdRx().Match(sr) is { Success: true } rm) score = Num(rm.Groups[1].Value);
        if (score == null && Get("X-Spam-Level") is { } level && level.Length > 0 && level.All(c => c == '*')) score = level.Length;
        if (score is double s) yield return "srv-score:" + (s < 0 ? "neg" : s < 2 ? "low" : s < 5 ? "mid" : s < 10 ? "high" : "vhigh");

        // Microsoft: Spam Confidence Level -1 (vertraut) … 9 (sicher Spam)
        var scl = Num(Get("X-MS-Exchange-Organization-SCL"));
        if (scl == null && (Get("X-Forefront-Antispam-Report") ?? Get("X-Microsoft-Antispam")) is { } fr && SclRx().Match(fr) is { Success: true } x) scl = Num(x.Groups[1].Value);
        if (scl is double c) yield return "srv-scl:" + (c <= 1 ? "low" : c <= 4 ? "mid" : "high");
    }

    /// <summary>Hat ein Mailserver die Mail als Spam gekennzeichnet (X-Spam-Flag: YES, irgendwo im Kopf)?</summary>
    public static bool SaysSpam(MimeMessage m) =>
        m.Headers.Any(h => h.Field.Equals("X-Spam-Flag", StringComparison.OrdinalIgnoreCase) && h.Value.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase));

    private static double? Num(string? s) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex(@"score=(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ScoreRx();
    [GeneratedRegex(@"\[\s*(-?\d+(?:\.\d+)?)\s*/")]
    private static partial Regex RspamdRx();
    [GeneratedRegex(@"SCL:(-?\d)")]
    private static partial Regex SclRx();
}
