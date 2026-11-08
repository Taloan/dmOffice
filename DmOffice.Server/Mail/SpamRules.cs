using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>Eine zusätzliche Erkennungsregel: regulärer Ausdruck (.NET) auf einen Teil der Mail.</summary>
public sealed class SpamRule
{
    /// <summary>Kurzname, Merkmal „sig:rule:&lt;id&gt;“ im lernenden Filter.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>subject, from, header (eine Kopfzeile, siehe Header), headers (alle Kopfzeilen), body (Text), links (Linkziele, eins je Zeile).</summary>
    public string Part { get; set; } = "subject";
    public string? Header { get; set; }
    public string Pattern { get; set; } = "";
    public bool IgnoreCase { get; set; } = true;
    /// <summary>Trifft, wenn der Ausdruck NICHT passt.</summary>
    public bool Negate { get; set; }
    /// <summary>feature (nur Merkmal, der Filter lernt das Gewicht), uncertain (mindestens unsicher), junk.</summary>
    public string Action { get; set; } = "feature";
}

/// <summary>
/// Zusätzliche Erkennungsregeln, die der Admin selbst pflegt (Verwaltung → E-Mail; aus, bis er sie einschaltet). Ein Treffer ist
/// ein Merkmal für den lernenden Filter, auf Wunsch auch eine feste Wirkung. Damit ein schlechter Ausdruck nichts lahmlegt: jeder
/// wird beim Speichern übersetzt und gegen Mustertexte auf Laufzeit geprüft, und jede Auswertung hat höchstens 100 ms - wird es
/// länger, zählt die Regel als nicht getroffen. Mitgeliefert werden nur eigene Startregeln; Regeln im SpamAssassin-Format lassen
/// sich einfügen (<see cref="Import"/>).
/// </summary>
public static partial class SpamRules
{
    public const int Max = 300, MaxPattern = 2000;
    public static readonly string[] Parts = ["subject", "from", "header", "headers", "body", "links"];
    public static readonly string[] Actions = ["feature", "uncertain", "junk"];
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Beim Start gesetzt; ohne (Tests) gibt es keine aktiven Regeln.</summary>
    public static ServerConfig? Config { get; set; }

    /// <summary>Eigene Startregeln (keine aus fremden Regelwerken).</summary>
    public static List<SpamRule> Defaults() =>
    [
        new() { Id = "betreff-grossbuchstaben", Name = "Betreff fast nur in Großbuchstaben", Part = "subject", IgnoreCase = false,
                Pattern = @"^(?=(?:[^a-zäöüß]*[A-ZÄÖÜ]){8})[^a-zäöüß]*$" },
        new() { Id = "betreff-leer", Name = "Leerer Betreff", Part = "subject", Pattern = @"^\s*$" },
        new() { Id = "keine-message-id", Name = "Keine Message-ID", Part = "header", Header = "Message-ID", Pattern = @"^\s*$" },
        new() { Id = "link-kuerzer", Name = "Link über einen Kurzlink-Dienst", Part = "links",
                Pattern = @"://(?:www\.)?(?:bit\.ly|tinyurl\.com|t\.co|is\.gd|cutt\.ly|rb\.gy|shorturl\.at|ow\.ly|tiny\.cc|s\.id)/" },
        new() { Id = "anrede-mit-adresse", Name = "Anrede mit der Mailadresse statt dem Namen", Part = "body",
                Pattern = @"^\s*(?:hallo|hello|hi|dear|guten tag|liebe[rs]?)\s+[\w.+-]+@[\w-]+\.[\w.-]+" },
        new() { Id = "gewinnversprechen", Name = "Gewinn oder Geschenk im Betreff", Part = "subject",
                Pattern = @"\b(?:gewinn\w*|gewonnen|geschenk\w*|gratis|belohnung|prämie|winner|prize|reward)\b" },
        new() { Id = "krypto-zahlung", Name = "Bitcoin-Zahlung verlangt", Part = "body",
                Pattern = @"\b(?:bitcoin|btc)\b[\s\S]{0,300}?\b(?:wallet|adresse|address)\b" },
    ];

    /// <summary>Die eingeschalteten Regeln (leer, solange der Admin die Erkennungsregeln nicht eingeschaltet hat).</summary>
    public static List<SpamRule> Active()
    {
        if (Config?.Current is not { SpamRulesOn: true } s) return [];
        return (s.SpamRules ?? Defaults()).Where(r => r.Enabled).ToList();
    }

    // ---------------- Prüfen ----------------

    private static readonly ConcurrentDictionary<(string, RegexOptions), Regex> Cache = new();

    private static RegexOptions OptionsOf(SpamRule r) =>
        RegexOptions.CultureInvariant | RegexOptions.Multiline | (r.IgnoreCase ? RegexOptions.IgnoreCase : 0);

    private static Regex RegexOf(SpamRule r)
    {
        if (Cache.Count > 2000) Cache.Clear();
        return Cache.GetOrAdd((r.Pattern, OptionsOf(r)), k => new Regex(k.Item1, k.Item2, Timeout));
    }

    // Mustertexte, an denen ungünstig verschachtelte Wiederholungen ((a+)+, (.*)*x …) hängen bleiben
    private static readonly string[] Probes =
    [
        new string('a', 3000) + "!", string.Concat(Enumerable.Repeat("ab ", 1500)) + "\u0001",
        new string(' ', 3000) + "x", string.Concat(Enumerable.Repeat("x.", 1500)) + "@", new string('1', 3000) + "-",
    ];

    /// <summary>Fehler als Satz für den Admin, oder null, wenn die Regel in Ordnung ist.</summary>
    public static string? Validate(SpamRule r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 120) return "Bitte einen Namen angeben (höchstens 120 Zeichen).";
        if (!Parts.Contains(r.Part)) return "Unbekannter Bereich der Mail.";
        if (!Actions.Contains(r.Action)) return "Unbekannte Wirkung.";
        if (r.Part == "header" && (r.Header == null || !HeaderName().IsMatch(r.Header))) return "Bitte den Namen der Kopfzeile angeben, etwa „Reply-To“.";
        if (string.IsNullOrEmpty(r.Pattern)) return "Bitte einen Ausdruck angeben.";
        if (r.Pattern.Length > MaxPattern) return $"Der Ausdruck ist zu lang (höchstens {MaxPattern} Zeichen).";
        Regex rx;
        try { rx = new Regex(r.Pattern, OptionsOf(r), Timeout); }
        catch (RegexParseException e) { return $"Ungültiger Ausdruck an Stelle {e.Offset}: {Describe(e.Error)}"; }
        catch (ArgumentException e) { return "Ungültiger Ausdruck: " + e.Message; }
        foreach (var probe in Probes)
            try { rx.IsMatch(probe); }
            catch (RegexMatchTimeoutException) { return "Der Ausdruck braucht zu lange (verschachtelte Wiederholungen wie (a+)+ oder (.*)* vermeiden)."; }
        return null;
    }

    private static string Describe(RegexParseError e) => e switch
    {
        RegexParseError.InsufficientClosingParentheses => "Es fehlt eine schließende Klammer „)“",
        RegexParseError.InsufficientOpeningParentheses => "Zu viele schließende Klammern „)“",
        RegexParseError.UnterminatedBracket => "Es fehlt eine schließende eckige Klammer „]“",
        RegexParseError.QuantifierAfterNothing => "Wiederholung (*, +, ?, {n}) ohne etwas davor",
        RegexParseError.NestedQuantifiersNotParenthesized => "Zwei Wiederholungen hintereinander (etwa „a**“)",
        RegexParseError.UnescapedEndingBackslash => "Rückstrich „\\“ am Ende",
        RegexParseError.ReversedCharacterRange => "Bereich rückwärts (etwa „[z-a]“)",
        RegexParseError.ReversedQuantifierRange => "Wiederholung rückwärts (etwa „{5,2}“)",
        RegexParseError.UnrecognizedEscape => "Unbekannte Rückstrich-Folge",
        RegexParseError.InvalidGroupingConstruct => "Ungültige Gruppe „(?…)“",
        RegexParseError.UndefinedNumberedReference or RegexParseError.UndefinedNamedReference => "Verweis auf eine Gruppe, die es nicht gibt",
        _ => e.ToString(),
    };

    /// <summary>Gespeicherte Liste prüfen und Kurznamen vergeben/eindeutig machen. Fehler mit dem Namen der Regel.</summary>
    public static (List<SpamRule> Rules, string? Error) Clean(IEnumerable<SpamRule> rules)
    {
        var list = new List<SpamRule>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rules)
        {
            r.Name = r.Name?.Trim() ?? "";
            r.Header = r.Part == "header" ? r.Header?.Trim() : null;
            if (Validate(r) is { } error) return ([], $"Regel „{(r.Name.Length > 0 ? r.Name : "ohne Namen")}“: {error}");
            var id = Slug(string.IsNullOrWhiteSpace(r.Id) ? r.Name : r.Id);
            var unique = id;
            for (int i = 2; !ids.Add(unique); i++) unique = $"{id}-{i}";
            r.Id = unique;
            list.Add(r);
        }
        if (list.Count > Max) return ([], $"Höchstens {Max} Regeln.");
        return (list, null);
    }

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var id = sb.ToString().Trim('-');
        return id.Length == 0 ? "regel" : id[..Math.Min(id.Length, 60)];
    }

    // ---------------- Anwenden ----------------

    /// <summary>Treffer und Zeitüberschreitungen je Regel seit dem Start (für die Verwaltung).</summary>
    public static readonly ConcurrentDictionary<string, (long Hits, long Timeouts)> Stats = new();

    /// <summary>Die Teile einer Mail, wie die Regeln sie sehen - der Text erst, wenn eine Regel ihn braucht.</summary>
    private sealed class MailParts(MimeMessage m)
    {
        private string? _body, _links, _headers;
        public string Text(SpamRule r) => r.Part switch
        {
            "subject" => m.Subject ?? "",
            "from" => m.From.Mailboxes.FirstOrDefault() is { } f ? $"{f.Name} <{f.Address}>".Trim() : "",
            "header" => string.Join('\n', m.Headers.Where(h => h.Field.Equals(r.Header, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value.Trim())),
            "headers" => _headers ??= string.Join('\n', m.Headers.Select(h => $"{h.Field}: {h.Value.Trim()}")),
            "body" => _body ??= Cap(MailStore.BodyText(m)),
            "links" => _links ??= string.Join('\n', Links().Matches((m.HtmlBody ?? "") + " " + (m.TextBody ?? "")).Select(x => x.Value).Distinct().Take(500)),
            _ => "",
        };
        private static string Cap(string s) => s.Length > 100_000 ? s[..100_000] : s;
    }

    /// <summary>Welche der Regeln treffen auf die Mail zu?</summary>
    public static List<SpamRule> Hits(MimeMessage m, IReadOnlyList<SpamRule>? rules = null)
    {
        rules ??= Active();
        if (rules.Count == 0) return [];
        var parts = new MailParts(m);
        var hits = new List<SpamRule>();
        foreach (var r in rules)
        {
            bool match;
            try { match = RegexOf(r).IsMatch(parts.Text(r)) != r.Negate; }
            catch (RegexMatchTimeoutException) { Stats.AddOrUpdate(r.Id, (0, 1), (_, v) => (v.Hits, v.Timeouts + 1)); continue; }
            catch (ArgumentException) { continue; }
            if (!match) continue;
            Stats.AddOrUpdate(r.Id, (1, 0), (_, v) => (v.Hits + 1, v.Timeouts));
            hits.Add(r);
        }
        return hits;
    }

    /// <summary>Probe in der Verwaltung: passt der Ausdruck auf den Text, und was genau?</summary>
    public static (bool Match, string? Found) Try(SpamRule r, string sample)
    {
        try
        {
            var x = RegexOf(r).Match(sample ?? "");
            if (r.Negate) return (!x.Success, null);
            return (x.Success, x.Success ? x.Value[..Math.Min(x.Value.Length, 200)] : null);
        }
        catch (RegexMatchTimeoutException) { return (false, null); }
    }

    // ---------------- Einfügen im SpamAssassin-Format ----------------

    public sealed record Imported(List<SpamRule> Rules, List<string> Skipped);

    /// <summary>
    /// Regelzeilen im Format von SpamAssassin übersetzen: header, body/rawbody/full, uri mit /…/flags oder m{…}flags, dazu
    /// describe (Name) und score (Wirkung: ab 5 Junk, ab 2 mindestens unsicher, sonst Merkmal). meta, eval: und Unbekanntes
    /// werden mit Grund übersprungen, ebenso Ausdrücke, die .NET nicht versteht.
    /// </summary>
    public static Imported Import(string text)
    {
        var rules = new Dictionary<string, SpamRule>(StringComparer.Ordinal);
        var describe = new Dictionary<string, string>();
        var score = new Dictionary<string, double>();
        var skipped = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = SaLine().Match(line);
            if (!m.Success) { skipped.Add($"{Short(line)}: nicht verstanden"); continue; }
            var (kind, name, rest) = (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value, m.Groups[3].Value.Trim());
            switch (kind)
            {
                case "describe": describe[name] = rest; continue;
                case "score":
                    if (double.TryParse(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc)) score[name] = sc;
                    continue;
                case "tflags" or "priority" or "lang" or "test": continue;
                case "meta": skipped.Add($"{name}: meta-Regeln (Verknüpfung anderer Regeln) gibt es hier nicht"); continue;
            }
            if (rest.StartsWith("eval:", StringComparison.OrdinalIgnoreCase)) { skipped.Add($"{name}: eval-Regeln brauchen SpamAssassin selbst"); continue; }
            var rule = new SpamRule { Id = name, Name = name };
            string expr = rest;
            if (kind == "header")
            {
                var h = SaHeader().Match(rest);
                if (h.Success && h.Groups[1].Value.Equals("exists", StringComparison.OrdinalIgnoreCase))
                {
                    // header NAME exists:X-Foo - Kopfzeile vorhanden
                    rule.Part = "header"; rule.Header = h.Groups[2].Value; rule.Pattern = "."; rule.IgnoreCase = false;
                    rules[name] = rule;
                    continue;
                }
                var hm = SaHeaderMatch().Match(rest);
                if (!hm.Success) { skipped.Add($"{name}: Kopfzeilen-Regel nicht verstanden"); continue; }
                var field = hm.Groups[1].Value;
                rule.Negate = hm.Groups[2].Value == "!~";
                expr = hm.Groups[3].Value.Trim();
                if (field.Equals("ALL", StringComparison.OrdinalIgnoreCase)) rule.Part = "headers";
                else if (field.Equals("Subject", StringComparison.OrdinalIgnoreCase)) rule.Part = "subject";
                else { rule.Part = "header"; rule.Header = field; }
            }
            else if (kind is "body" or "rawbody" or "full") rule.Part = "body";
            else if (kind == "uri") rule.Part = "links";
            else { skipped.Add($"{name}: Regelart „{kind}“ gibt es hier nicht"); continue; }
            // [if-unset: …] am Ende weglassen
            expr = IfUnset().Replace(expr, "").Trim();
            if (!PerlRegex(expr, out var pattern, out var flags)) { skipped.Add($"{name}: Ausdruck nicht verstanden"); continue; }
            rule.IgnoreCase = flags.Contains('i');
            var inline = new string(flags.Where(c => c is 's' or 'x' or 'm').Distinct().ToArray());
            rule.Pattern = (inline.Length > 0 ? $"(?{inline})" : "") + pattern;
            rules[name] = rule;
        }
        var result = new List<SpamRule>();
        foreach (var (name, r) in rules)
        {
            if (describe.TryGetValue(name, out var d) && d.Length > 0) r.Name = $"{d} ({name})";
            if (score.TryGetValue(name, out var s)) r.Action = s >= 5 ? "junk" : s >= 2 ? "uncertain" : "feature";
            if (Validate(r) is { } error) { skipped.Add($"{name}: {error}"); continue; }
            r.Id = Slug(name);
            result.Add(r);
        }
        return new Imported(result, skipped);
    }

    /// <summary>/…/flags oder m{…}flags, m!…!flags in Ausdruck und Schalter zerlegen; „\/“ wird zu „/“.</summary>
    private static bool PerlRegex(string s, out string pattern, out string flags)
    {
        pattern = ""; flags = "";
        if (s.Length < 2) return false;
        char open, close; int start;
        if (s[0] == '/') { open = close = '/'; start = 1; }
        else if (s[0] == 'm' && s.Length > 2 && !char.IsLetterOrDigit(s[1]) && !char.IsWhiteSpace(s[1]))
        {
            open = s[1]; start = 2;
            close = open switch { '{' => '}', '(' => ')', '[' => ']', '<' => '>', _ => open };
        }
        else return false;
        var end = -1;
        for (int i = s.Length - 1; i >= start; i--)
            if (s[i] == close && !(i > 0 && s[i - 1] == '\\' && (i < 2 || s[i - 2] != '\\'))) { end = i; break; }
        if (end < start) return false;
        flags = s[(end + 1)..].Trim();
        if (flags.Any(c => !"imsxgo".Contains(c))) return false;
        pattern = s[start..end];
        if (open == '/') pattern = pattern.Replace(@"\/", "/");
        return true;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;

    [GeneratedRegex(@"^(header|body|rawbody|full|uri|describe|score|meta|tflags|priority|lang|test)\s+([A-Za-z0-9_]+)\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SaLine();

    [GeneratedRegex(@"^(exists):\s*([A-Za-z0-9-]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SaHeader();

    [GeneratedRegex(@"^([A-Za-z0-9-]+)(?::(?:raw|addr|name|host|domain))?\s*(=~|!~)\s*(.+)$")]
    private static partial Regex SaHeaderMatch();

    [GeneratedRegex(@"\[if-unset:[^\]]*\]\s*$")]
    private static partial Regex IfUnset();

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,76}$")]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Links();
}
