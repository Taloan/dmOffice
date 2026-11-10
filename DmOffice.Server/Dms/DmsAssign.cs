using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Dms;

/// <summary>
/// Zuweisung im DMS: Regeln (Wörter, regulärer Ausdruck, Absender), Lernen („Automatisch“, naiver Bayes über <see cref="Learner"/>),
/// KI beim Import, dazu Dokumentdatum und Titel aus dem Text. Reihenfolge: Vorgaben der Quelle, Regeln, KI, Automatisch.
/// </summary>
public static class DmsAssign
{
    /// <summary>So viel vom Text zählt fürs Lernen.</summary>
    private const int LearnChars = 6000;

    /// <summary>Merkmale eines Dokuments: Wörter im Text und im Dateinamen, Absender und seine Domain.</summary>
    internal static HashSet<string> Tokens(string? text, string originalName, string? sender)
    {
        var t = new HashSet<string>();
        foreach (var w in SpamFilter.Words(Path.GetFileNameWithoutExtension(originalName))) t.Add("n:" + w);
        if (sender is { Length: > 0 } s)
        {
            s = s.Trim().ToLowerInvariant();
            t.Add("s:" + s);
            if (s.Contains('@')) t.Add("sd:" + s[(s.IndexOf('@') + 1)..]);
        }
        if (text != null)
            foreach (var w in SpamFilter.Words(text.Length > LearnChars ? text[..LearnChars] : text)) t.Add(w);
        return t;
    }

    internal static HashSet<string> Tokens(DmsStore db, long doc)
    {
        var (name, sender) = db.Query("SELECT original_name, sender FROM dms_docs WHERE id = $d", r => (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)), ("$d", doc))
            .FirstOrDefault();
        return Tokens(Dms.Text(db, doc), name ?? "", sender);
    }

    // ---------------- Regeln ----------------

    /// <summary>Passt die Regel eines Eintrags auf Text bzw. Absender?</summary>
    public static bool Matches(DmsEntry e, string text, string? sender)
    {
        if (e.MatchText is not { Length: > 0 } m) return false;
        var cmp = e.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        IEnumerable<string> Words() => m.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool HasWord(string w) => System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(w) + @"(?![\p{L}\p{N}])",
            e.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        try
        {
            return e.Match switch
            {
                Dms.Any => Words().Any(HasWord),
                Dms.All => Words().All(HasWord),
                Dms.Exact => text.Contains(m.Trim(), cmp),
                Dms.Regex => System.Text.RegularExpressions.Regex.IsMatch(text, m, e.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
                Dms.Sender => sender is { Length: > 0 } s && Words().Any(w =>
                    s.Equals(w, StringComparison.OrdinalIgnoreCase) || s.EndsWith("@" + w.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith("." + w.TrimStart('@'), StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    // ---------------- Entscheiden ----------------

    /// <summary>Ergebnis der Zuweisung für ein neues Dokument.</summary>
    /// <summary>Fixed*: aus Vorgabe oder Regel (gilt vor KI und Gelerntem); Tags: aus Vorgabe oder Regel bzw. gelernt.</summary>
    public sealed record Result(string? Correspondent, string? Doctype, List<string> Tags, bool ByAi, string? Title = null, string? DocDate = null,
        string? NewCorrespondent = null, bool FixedCorrespondent = false, bool FixedDoctype = false, List<string>? FixedTags = null);

    /// <summary>Vorgaben der Quelle (Korrespondent, Dokumenttyp, Tags).</summary>
    public sealed record Presets(string? Correspondent = null, string? Doctype = null, List<string>? Tags = null);

    /// <summary>Regeln und Gelerntes (ohne KI): je Liste die Einträge, die passen.</summary>
    public static Result Decide(DmsStore db, string text, string originalName, string? sender, Presets? presets)
    {
        var all = Dms.Entries(db.User);
        var tokens = Tokens(text, originalName, sender);
        return DecideWith(all, text, sender, presets, Probabilities(db, all, tokens));
    }

    /// <summary>Wie <see cref="Decide"/>, mit schon berechneten Wahrscheinlichkeiten (auch aus einem Modell im Speicher).</summary>
    internal static Result DecideWith(List<DmsEntry> all, string text, string? sender, Presets? presets, Dictionary<string, double?> probs)
    {
        (string? Id, bool Fixed) Single(string kind, string? preset)
        {
            if (preset != null && all.Any(e => e.Id == preset && e.Kind == kind)) return (preset, true);
            var list = all.Where(e => e.Kind == kind).ToList();
            var rule = list.Where(e => e.Match is not (Dms.Auto or Dms.None) && Matches(e, text, sender)).ToList();
            if (rule.Count > 0) return (Deepest(all, rule.Select(e => e.Id)).First(), true);
            var yes = list.Where(e => e.Match == Dms.Auto && Learner.Decide(probs.GetValueOrDefault(e.Id)) == true).Select(e => e.Id).ToList();
            yes = Deepest(all, yes);
            return (yes.OrderByDescending(x => probs.GetValueOrDefault(x) ?? 0).FirstOrDefault(), false);
        }
        var fixedTags = (presets?.Tags ?? []).Where(t => all.Any(e => e.Id == t && e.Kind == Dms.Tag)).ToHashSet();
        var tags = new HashSet<string>(fixedTags);
        foreach (var e in all.Where(e => e.Kind == Dms.Tag))
        {
            if (e.Match is not (Dms.Auto or Dms.None) && Matches(e, text, sender)) { tags.Add(e.Id); fixedTags.Add(e.Id); }
            else if (e.Match == Dms.Auto && Learner.Decide(probs.GetValueOrDefault(e.Id)) == true) tags.Add(e.Id);
        }
        var (c, cf) = Single(Dms.Correspondent, presets?.Correspondent);
        var (t, tf) = Single(Dms.Doctype, presets?.Doctype);
        return new Result(c, t, Deepest(all, tags), false, FixedCorrespondent: cf, FixedDoctype: tf, FixedTags: [.. fixedTags]);
    }

    /// <summary>Nur die genauesten: fällt ein Eintrag mit einem seiner Unterbegriffe zusammen, bleibt der Unterbegriff.</summary>
    private static List<string> Deepest(List<DmsEntry> all, IEnumerable<string> ids)
    {
        var set = ids.ToList();
        return set.Where(c => !set.Any(o => o != c && Dms.Above(all, o).Contains(c))).ToList();
    }

    /// <summary>Wahrscheinlichkeit je Eintrag mit Zuweisung „Automatisch“.</summary>
    /// <summary>Gezähltes aus dem Bereich; im Tresor zählt das Gelernte aus dem offenen Bereich mit (umgekehrt nie).</summary>
    private static Dictionary<string, double?> Probabilities(DmsStore db, List<DmsEntry> all, HashSet<string> tokens)
    {
        var auto = all.Where(e => e.Match == Dms.Auto).ToList();
        if (auto.Count == 0) return [];
        var byToken = new Dictionary<string, Dictionary<string, (int Pos, int Neg)>>();
        var trained = new Dictionary<string, (int Pos, int Neg)>();
        Count(db.Sql, byToken, trained, tokens);
        if (db.Tresor) { Dms.Ensure(db.User); Count(db.User, byToken, trained, tokens); }
        return Probabilities(all, tokens, byToken, trained);
    }

    private static void Count(ISqlStore db, Dictionary<string, Dictionary<string, (int Pos, int Neg)>> byToken, Dictionary<string, (int Pos, int Neg)> trained, HashSet<string> tokens)
    {
        using (var conn = db.Open())
            foreach (var chunk in tokens.Chunk(400))
            {
                var names = chunk.Select((_, i) => "$t" + i).ToList();
                foreach (var (tok, entry, p, n) in UserDb.Query(conn, $"SELECT token, entry, pos, neg FROM dms_tokens WHERE (pos > 0 OR neg > 0) AND token IN ({string.Join(",", names)})",
                             r => (r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)), chunk.Select((t, i) => (names[i], (object?)t)).ToArray()))
                {
                    if (!byToken.TryGetValue(tok, out var per)) byToken[tok] = per = new();
                    per[entry] = per.TryGetValue(entry, out var had) ? (had.Pos + p, had.Neg + n) : (p, n);
                }
            }
        foreach (var (entry, pos, neg) in db.Query("SELECT entry, COALESCE(SUM(label > 0),0), COALESCE(SUM(label < 0),0) FROM dms_trained GROUP BY entry",
                     r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2))))
            trained[entry] = trained.TryGetValue(entry, out var t) ? (t.Pos + pos, t.Neg + neg) : (pos, neg);
    }

    /// <summary>Wahrscheinlichkeit je Eintrag „Automatisch“ aus gezählten Merkmalen (Datenbank oder Modell im Speicher).</summary>
    internal static Dictionary<string, double?> Probabilities(List<DmsEntry> all, HashSet<string> tokens,
        Dictionary<string, Dictionary<string, (int Pos, int Neg)>> byToken, Dictionary<string, (int Pos, int Neg)> trained)
    {
        var auto = all.Where(e => e.Match == Dms.Auto).ToList();
        var result = new Dictionary<string, double?>();
        foreach (var e in auto)
        {
            // Der Rest: nur Einträge derselben Liste, ohne Ober- und Unterbegriffe
            var related = Dms.Below(all, e.Id);
            related.UnionWith(Dms.Above(all, e.Id));
            related.UnionWith(all.Where(x => x.Kind != e.Kind).Select(x => x.Id));
            var tr = trained.Where(x => !related.Contains(x.Key) || x.Key == e.Id).ToDictionary(x => x.Key, x => x.Value);
            result[e.Id] = Learner.Probability(e.Id, related, tokens, byToken, tr);
        }
        return result;
    }

    // ---------------- Datum und Titel ----------------

    private static readonly string[] MonthsDe = ["januar", "februar", "märz", "april", "mai", "juni", "juli", "august", "september", "oktober", "november", "dezember"];
    private static readonly string[] MonthsEn = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    private static readonly Regex DateRx = new(
        @"(?<![\d.])(?<d>\d{1,2})\.\s?(?<m>\d{1,2})\.\s?(?<y>\d{4}|\d{2})(?![\d])" +
        @"|(?<![\d-])(?<y2>\d{4})-(?<m2>\d{2})-(?<d2>\d{2})(?![\d])" +
        @"|(?<![\d])(?<d3>\d{1,2})\.?\s+(?<mn>[A-Za-zÄÖÜäöü]{3,9})\.?\s+(?<y3>\d{4})(?![\d])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Ein Datum aus dem Text: wie oft, wo zuerst (Ausschnitt), steht ein Hinweis wie „Datum“ oder „vom“ davor.</summary>
    public sealed record DateCandidate(DateOnly Date, int Count, string Context, bool Labeled);

    /// <summary>Davor ein deutlicher Hinweis auf das Datum des Schreibens („Datum:“, „Rechnungsdatum“) - diese Daten kommen ganz nach vorn.</summary>
    private static readonly Regex StrongLabel = new(@"(datum|date|dated|ausgestellt am|erstellt am)\W{0,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Davor ein schwacher Hinweis („vom“, „den“, „Stand“).</summary>
    private static readonly Regex WeakLabel = new(@"\b(vom|den|stand|am)\W{0,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Bezug auf ein anderes Schreiben („Ihr Schreiben vom“) - kein Hinweis auf das Datum dieses Dokuments.</summary>
    private static readonly Regex Reference = new(@"(ihr|ihre|ihrem|ihren|unser|unsere|your|our)\s+\w+\s+(vom|von|of|dated)\W{0,3}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Davor ein Hinweis, dass es nicht das Datum des Dokuments ist (Geburtstag, Fälligkeit, Zeitraum).</summary>
    private static readonly Regex NotDocDate = new(@"(geb\.?|geboren|geburtsdatum|birth|born|fällig|zahlbar|bis|gültig|valid|ab|seit)\W{0,3}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Datum des Dokuments: das beste der plausiblen Daten (<see cref="FindDates"/>), sonst null.</summary>
    public static DateOnly? FindDate(string text, DateOnly today) => FindDates(text, today).FirstOrDefault()?.Date;

    /// <summary>
    /// Alle plausiblen Daten im Text: gültig, ab 1990, nicht nach <paramref name="today"/> (ein Dokument ist nicht aus der Zukunft),
    /// nicht mit Geburtstag, Fälligkeit oder Zeitraum davor. Reihenfolge: mit „Datum“ davor zuerst, dann mit „vom“ oder „den“ (nicht bei
    /// Bezügen wie „Ihr Schreiben vom“), dann nach der Stelle im Text. Gleiche Daten einmal, mit Anzahl.
    /// </summary>
    public static List<DateCandidate> FindDates(string text, DateOnly today)
    {
        var head = text.Length > 20000 ? text[..20000] : text;
        var found = new List<(DateOnly Date, int Pos, string Context, int Score)>();
        foreach (Match m in DateRx.Matches(head))
        {
            int d, mo, y;
            if (m.Groups["d"].Success)
            {
                d = int.Parse(m.Groups["d"].Value); mo = int.Parse(m.Groups["m"].Value); y = int.Parse(m.Groups["y"].Value);
                if (y < 100) y += y > (today.Year % 100) + 1 ? 1900 : 2000;
            }
            else if (m.Groups["y2"].Success)
            {
                y = int.Parse(m.Groups["y2"].Value); mo = int.Parse(m.Groups["m2"].Value); d = int.Parse(m.Groups["d2"].Value);
            }
            else
            {
                var name = m.Groups["mn"].Value.ToLowerInvariant().TrimEnd('.');
                mo = Array.FindIndex(MonthsDe, x => x.StartsWith(name, StringComparison.Ordinal) && name.Length >= 3) + 1;
                if (mo == 0) mo = Array.FindIndex(MonthsEn, x => x.StartsWith(name, StringComparison.Ordinal) && name.Length >= 3) + 1;
                if (mo == 0) continue;
                d = int.Parse(m.Groups["d3"].Value); y = int.Parse(m.Groups["y3"].Value);
            }
            if (mo is < 1 or > 12 || d < 1 || y < 1990 || d > DateTime.DaysInMonth(y, mo)) continue;
            var date = new DateOnly(y, mo, d);
            if (date > today) continue;
            var before = head[Math.Max(0, m.Index - 30)..m.Index];
            if (NotDocDate.IsMatch(before)) continue;
            var from = Math.Max(0, m.Index - 30);
            var to = Math.Min(head.Length, m.Index + m.Length + 30);
            var context = Regex.Replace(head[from..to], @"\s+", " ").Trim();
            var score = Reference.IsMatch(before) ? 0 : StrongLabel.IsMatch(before) ? 2 : WeakLabel.IsMatch(before) ? 1 : 0;
            found.Add((date, m.Index, (from > 0 ? "…" : "") + context + (to < head.Length ? "…" : ""), score));
        }
        return found.GroupBy(f => f.Date)
            .Select(g => (Date: g.Key, Count: g.Count(), First: g.Min(x => x.Pos), Score: g.Max(x => x.Score), Context: g.OrderBy(x => x.Pos).First().Context))
            .OrderByDescending(g => g.Score).ThenBy(g => g.First)
            .Select(g => new DateCandidate(g.Date, g.Count, g.Context, g.Score > 0)).ToList();
    }

    private static readonly Regex ScannerName = new(@"^(scan|img|image|dok|doc|document|dokument|bild|file|datei|pdf|cam|dsc|photo|foto|brn|epson|canon|hp)?[\s_\-]*[\d\s_\-.]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Titel: Dateiname ohne Endung; bei Scanner-Namen (nur Ziffern/Datum) die erste aussagekräftige Zeile des Texts.</summary>
    public static string Title(string originalName, string? text, string? subject = null)
    {
        if (subject is { Length: > 0 }) return Cut(subject.Trim());
        var stem = Path.GetFileNameWithoutExtension(originalName).Trim();
        if (stem.Length > 0 && !ScannerName.IsMatch(stem)) return Cut(stem.Replace('_', ' '));
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = Regex.Replace(raw, @"\s+", " ").Trim();
            if (line.Length < 8 || line.Length > 90) continue;
            if (line.Count(char.IsLetter) < line.Length / 2) continue;
            if (line.Split(' ').Length < 2) continue;
            return Cut(line);
        }
        return stem.Length > 0 ? Cut(stem) : "Dokument";
    }

    private static string Cut(string s) => s.Length > 120 ? s[..120].TrimEnd() : s;

    // ---------------- KI ----------------

    private const string AiInstructions =
        "Du ordnest Dokumente in einem Dokumentenarchiv ein. Du bekommst den erkannten Text eines Dokuments, seinen Dateinamen und " +
        "die vorhandenen Listen für Korrespondent (wer hat geschrieben), Dokumenttyp (was ist es) und Tags (Themen). Wähle nur aus " +
        "den vorhandenen Einträgen, über ihre Nummer. Passt kein Korrespondent, darfst du unter \"new_correspondent\" einen kurzen " +
        "Namen vorschlagen. Antworte ausschließlich mit einem JSON-Objekt ohne weitere Worte: " +
        "{\"correspondent\": Nummer oder null, \"doctype\": Nummer oder null, \"tags\": [Nummern], \"title\": \"kurzer Titel\", " +
        "\"date\": \"JJJJ-MM-TT\" oder null, \"new_correspondent\": \"Name\" oder null}. Der Titel ist kurz und sachlich, ohne Datum " +
        "und ohne Korrespondent, in der Sprache des Dokuments.";

    /// <summary>
    /// Einordnen per KI (nur offener Bereich, nur wenn eingerichtet und erlaubt): der Text (höchstens 8000 Zeichen), Dateiname,
    /// Absender und Betreff sowie die Namen der Einträge gehen an den eingerichteten Dienst. Null, wenn es nicht geklappt hat.
    /// </summary>
    public static async Task<Result?> AiAsync(UserDb db, IDmsAi ai, string text, string originalName, string? sender, string? subject, CancellationToken ct)
    {
        var all = Dms.Entries(db);
        var numbered = all.Select((e, i) => (No: i + 1, Entry: e)).ToList();
        string ListOf(string kind) => string.Join("\n", numbered.Where(x => x.Entry.Kind == kind)
            .Select(x => $"{x.No}: {string.Join(" › ", Dms.PathOf(all, x.Entry.Id))}"));
        var prompt = $"Dateiname: {originalName}\n" + (sender != null ? $"Absender: {sender}\n" : "") + (subject != null ? $"Betreff: {subject}\n" : "") +
            $"\nKorrespondenten:\n{ListOf(Dms.Correspondent)}\n\nDokumenttypen:\n{ListOf(Dms.Doctype)}\n\nTags:\n{ListOf(Dms.Tag)}\n\n" +
            $"Text des Dokuments:\n{(text.Length > 8000 ? text[..8000] : text)}";
        var answer = await ai.WriteAsync(AiInstructions, prompt, ct);
        if (answer == null) return null;
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(answer[start..(end + 1)]);
            var root = doc.RootElement;
            string? Pick(string prop, string kind) =>
                root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                && numbered.FirstOrDefault(x => x.No == n) is { Entry: { } e } && e.Kind == kind ? e.Id : null;
            var tags = new List<string>();
            if (root.TryGetProperty("tags", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var t in ts.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) && numbered.FirstOrDefault(x => x.No == n) is { Entry: { Kind: Dms.Tag } e })
                        tags.Add(e.Id);
            string? Str(string prop) => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
            var date = Str("date") is { } ds && DateOnly.TryParseExact(ds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd)
                ? dd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            var title = Str("title");
            return new Result(Pick("correspondent", Dms.Correspondent), Pick("doctype", Dms.Doctype), tags.Distinct().ToList(), true,
                title is { Length: > 120 } ? title[..120] : title, date, Str("new_correspondent") is { Length: <= 120 } nc ? nc : null);
        }
        catch (JsonException) { return null; }
    }
}
