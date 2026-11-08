using System.Globalization;
using System.Text.RegularExpressions;

namespace DmOffice.Chat;

/// <summary>
/// Kurzbefehle an Pukki (Stufe 5), ohne KI: Wann und Was aus einem Satz wie „morgen 15 Uhr Zahnarzt“ oder
/// „Freitag 9:30 bis 11 Elternabend“. Erkannt werden heute/morgen/übermorgen, Wochentage („nächsten Montag“,
/// „Dienstag nächste Woche“), Datum (24.10. / 24.10.2026 / 3. November), „in drei Tagen“, Zeiträume („vom 1.8. bis
/// 14.8.“), Uhrzeit (15 Uhr, 15:30, um 9, halb drei, viertel nach vier, 3pm, in zwei Stunden), Tageszeiten
/// („heute Abend“, „um 8 abends“), „bis …“ und Wiederholungen („jeden Montag“, „alle zwei Wochen“). Der Rest ist der Titel.
/// </summary>
public static partial class PukkiParse
{
    /// <summary>Until: letzter Tag eines Zeitraums. Repeat: RRULE ohne „RRULE:“ (z. B. FREQ=WEEKLY). RepeatText: „jeden Montag“.</summary>
    public sealed record When(DateOnly? Date, TimeOnly? Start, TimeOnly? End, string Title, DateOnly? Until = null, string? Repeat = null, string? RepeatText = null);

    private static readonly string[] Days = ["montag", "dienstag", "mittwoch", "donnerstag", "freitag", "samstag", "sonntag"];
    private static readonly string[] DaysEn = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];
    private static readonly string[] ByDay = ["MO", "TU", "WE", "TH", "FR", "SA", "SU"];
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Zahlwörter bis zwölf (für Uhrzeiten und Mengen).</summary>
    public static readonly Dictionary<string, int> Numbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ein"] = 1, ["eins"] = 1, ["einer"] = 1, ["einem"] = 1, ["einen"] = 1, ["eine"] = 1, ["zwei"] = 2, ["drei"] = 3, ["vier"] = 4, ["fünf"] = 5, ["fuenf"] = 5,
        ["sechs"] = 6, ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9, ["zehn"] = 10, ["elf"] = 11, ["zwölf"] = 12, ["zwoelf"] = 12,
        ["vierzehn"] = 14, ["fünfzehn"] = 15, ["zwanzig"] = 20, ["dreißig"] = 30, ["dreissig"] = 30,
    };

    /// <summary>Häufige Vertipper und Kurzformen der Tageswörter.</summary>
    private static readonly Dictionary<string, string> Typos = new(StringComparer.Ordinal)
    {
        ["heut"] = "heute", ["heit"] = "heute", ["heite"] = "heute", ["hetue"] = "heute", ["huete"] = "heute", ["hüt"] = "heute",
        ["morgn"] = "morgen", ["morgne"] = "morgen", ["moregn"] = "morgen", ["mrogen"] = "morgen", ["mogen"] = "morgen", ["morhen"] = "morgen", ["mornge"] = "morgen",
        ["übermorgn"] = "übermorgen", ["uebermorgn"] = "übermorgen", ["ubermorgen"] = "übermorgen", ["übermorge"] = "übermorgen",
        ["montg"] = "montag", ["mointag"] = "montag", ["dienstg"] = "dienstag", ["dinstag"] = "dienstag", ["mitwoch"] = "mittwoch", ["mittwich"] = "mittwoch",
        ["donerstag"] = "donnerstag", ["donnerstg"] = "donnerstag", ["freitg"] = "freitag", ["freitga"] = "freitag", ["samstg"] = "samstag", ["samtag"] = "samstag",
        ["sonntg"] = "sonntag", ["sontag"] = "sonntag", ["sonnabend"] = "samstag",
    };

    private static readonly Dictionary<string, string> Short = new(StringComparer.Ordinal)
    {
        ["mo"] = "montag", ["di"] = "dienstag", ["mi"] = "mittwoch", ["do"] = "donnerstag", ["fr"] = "freitag", ["sa"] = "samstag", ["so"] = "sonntag",
    };

    private const string NumWord = @"\d{1,2}|eins|ein|zwei|drei|vier|fünf|fuenf|sechs|sieben|acht|neun|zehn|elf|zwölf|zwoelf";

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["januar"] = 1, ["jänner"] = 1, ["jaenner"] = 1, ["jan"] = 1, ["februar"] = 2, ["feber"] = 2, ["feb"] = 2, ["märz"] = 3, ["maerz"] = 3, ["mär"] = 3, ["mrz"] = 3,
        ["april"] = 4, ["apr"] = 4, ["mai"] = 5, ["juni"] = 6, ["jun"] = 6, ["juli"] = 7, ["jul"] = 7, ["august"] = 8, ["aug"] = 8, ["september"] = 9, ["sep"] = 9, ["sept"] = 9,
        ["oktober"] = 10, ["okt"] = 10, ["november"] = 11, ["nov"] = 11, ["dezember"] = 12, ["dez"] = 12,
        ["january"] = 1, ["february"] = 2, ["march"] = 3, ["may"] = 5, ["june"] = 6, ["july"] = 7, ["october"] = 10, ["december"] = 12,
    };

    [GeneratedRegex(@"(?:\b(?:am|vom|ab|zum|den|bis)\s+)?\b(\d{1,2})\.(\d{1,2})\.(\d{2,4})?(?=\s|$|,|-|–)", RegexOptions.IgnoreCase)]
    private static partial Regex DateRx();

    // „am 3.11 um …“ - ohne Schlusspunkt nur nach „am“, „vom“, „bis“ …
    [GeneratedRegex(@"\b(?:am|vom|ab|zum|den|bis)\s+(\d{1,2})\.(\d{1,2})(?=\s|$|,)(?!\s*(?:uhr|h)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ShortDateRx();

    // „3. November“, „am 3 nov 2027“
    [GeneratedRegex(@"(?:\b(?:am|vom|ab|zum|den|bis)\s+)?\b(\d{1,2})\.?\s*(januar|jänner|jaenner|februar|feber|märz|maerz|april|mai|juni|juli|august|september|oktober|november|dezember|january|february|march|june|july|october|december|jan|feb|mär|mrz|apr|jun|jul|aug|sept|sep|okt|nov|dez)\b\.?(?:\s+(\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex MonthDateRx();

    // „bis 16 Uhr“, „bis 16:30“
    [GeneratedRegex(@"\b(?:bis|until|to|-)\s*(\d{1,2})(?:[:.](\d{2}))?\s*(?:uhr|h)?(?=\s|$|,)", RegexOptions.IgnoreCase)]
    private static partial Regex UntilRx();

    // „um 15 Uhr“, „15:30“, „15 Uhr“, „um 9“, „15.30 Uhr“, „ab 9“, „von 9“
    [GeneratedRegex(@"(?:\b(?:um|at|ab|von|gegen)\s+(\d{1,2})(?:[:.](\d{2}))?\s*(?:uhr|h)?|\b(\d{1,2})[:](\d{2})(?:\s*(?:uhr|h))?|\b(\d{1,2})(?:\.(\d{2}))?\s*(?:uhr|h)\b)(?=\s|$|,)", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRx();

    // „in 2 Stunden“, „in einer halben Stunde“, „in 30 Minuten“
    [GeneratedRegex(@"\bin\s+(?:(einer\s+halben)|(" + NumWord + @"|einer|einem)(?:\s+(?:und\s+einer\s+)?(halben))?)\s+(stunden?|std|minuten?|min)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InTimeRx();

    // „in drei Tagen“, „in einer Woche“, „in 2 Monaten“, „heute in einer Woche“
    [GeneratedRegex(@"\b(?:heute\s+)?in\s+(" + NumWord + @"|einer|einem)\s+(tag(?:en)?|woche(?:n)?|monat(?:en)?|jahr(?:en)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InDaysRx();

    // Wiederholung: „jeden Montag“, „jeden Tag“, „täglich“, „alle zwei Wochen“, „jeden Monat“, „jedes Jahr“, „werktags“
    [GeneratedRegex(@"\b(?:(jede[nrs]?)\s+(zweiten\s+|2\.\s+)?(montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|tag|woche|monat|jahr|werktag|morgen|abend)|alle\s+(zwei|drei|vier|2|3|4)\s+(tage|wochen|monate|jahre)|(täglich|taeglich|wöchentlich|woechentlich|monatlich|jährlich|jaehrlich|werktags|every\s+day|daily|weekly|monthly|yearly))\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatRx();

    public static When Parse(string text, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        // Satzzeichen am Wortende (Fragezeichen, Ausrufezeichen) stören nur
        var s = " " + Regex.Replace(text.Trim(), @"[?!]+", " ") + " ";
        DateOnly? date = null, until = null;
        TimeOnly? start = null, end = null;
        string? repeat = null, repeatText = null;
        bool evening = false, morning = false, guess = false;

        // Wiederholung
        var rp = RepeatRx().Match(s);
        if (rp.Success)
        {
            (repeat, repeatText, var first) = Repeat(rp, today);
            if (repeat != null) { date = first; s = s.Remove(rp.Index, rp.Length); }
        }
        // „freitags“: jede Woche an diesem Tag (mit „alle zwei Wochen“ jede zweite)
        var plural = Regex.Match(s, @"\b(montags|dienstags|mittwochs|donnerstags|freitags|samstags|sonntags)\b", RegexOptions.IgnoreCase);
        if (plural.Success)
        {
            var ix = Array.IndexOf(Days, plural.Value.ToLowerInvariant()[..^1]);
            var target = (DayOfWeek)((ix + 1) % 7);
            date = today.AddDays(((int)target - (int)today.DayOfWeek + 7) % 7);
            var every = repeat != null && repeat.Contains("INTERVAL=2") ? "FREQ=WEEKLY;INTERVAL=2" : "FREQ=WEEKLY";
            repeat = every + ";BYDAY=" + ByDay[ix];
            repeatText = (every.Contains("INTERVAL") ? "jeden zweiten " : "jeden ") + Cap(Days[ix]);
            s = s.Remove(plural.Index, plural.Length);
        }

        // Zeiträume und Uhrzeiten in Worten werden zu Ziffern („halb drei“ → „um 2:30“)
        s = Words2Time(s, ref guess);

        // In … Stunden/Minuten: Tag und Uhrzeit ab jetzt
        var it = InTimeRx().Match(s);
        if (it.Success)
        {
            var n = it.Groups[1].Success ? 0.5 : Num(it.Groups[2].Value) + (it.Groups[3].Success ? 0.5 : 0);
            var minutes = it.Groups[4].Value.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? n : n * 60;
            if (minutes > 0)
            {
                var at = now.AddMinutes(minutes);
                date ??= DateOnly.FromDateTime(at);
                start = TimeOnly.FromDateTime(at);
                start = new TimeOnly(start.Value.Hour, start.Value.Minute);
                s = s.Remove(it.Index, it.Length);
            }
        }

        // Datum (mit Zeitraum „vom 1.8. bis 14.8.“)
        if (date == null)
        {
            var hits = new List<(int Index, int Length, DateOnly Day)>();
            foreach (Match m in DateRx().Matches(s))
                if (MakeDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : null, today) is { } d) hits.Add((m.Index, m.Length, d));
            foreach (Match m in MonthDateRx().Matches(s))
                if (Months.TryGetValue(m.Groups[2].Value, out var mo) && MakeDate(m.Groups[1].Value, mo.ToString(), m.Groups[3].Success ? m.Groups[3].Value : null, today) is { } d) hits.Add((m.Index, m.Length, d));
            foreach (Match m in ShortDateRx().Matches(s))
                if (!hits.Any(h => h.Index <= m.Index && m.Index < h.Index + h.Length) && MakeDate(m.Groups[1].Value, m.Groups[2].Value, null, today) is { } d) hits.Add((m.Index, m.Length, d));
            hits = hits.OrderBy(h => h.Index).ToList();
            if (hits.Count > 0)
            {
                date = hits[0].Day;
                if (hits.Count > 1 && hits[1].Day > hits[0].Day && Regex.IsMatch(s[(hits[0].Index + hits[0].Length)..hits[1].Index], @"^\s*(bis|-|–|to)?\s*$", RegexOptions.IgnoreCase))
                {
                    until = hits[1].Day;
                    // „bis 14.8.“ ohne Jahr, aber schon im nächsten Jahr gelandet: Zeitraum über Silvester
                    s = s.Remove(hits[1].Index, hits[1].Length);
                }
                s = s.Remove(hits[0].Index, hits[0].Length);
                s = Regex.Replace(s, @"^\s*(bis|-|–)\s+", " ");
            }
        }

        // In drei Tagen, in einer Woche
        if (date == null && InDaysRx().Match(s) is { Success: true } id)
        {
            var n = Num(id.Groups[1].Value);
            var unit = id.Groups[2].Value.ToLowerInvariant();
            date = unit.StartsWith("tag") ? today.AddDays(n) : unit.StartsWith("woche") ? today.AddDays(7 * n) : unit.StartsWith("monat") ? today.AddMonths(n) : today.AddYears(n);
            s = s.Remove(id.Index, id.Length);
        }

        // Wörter für den Tag
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var nextWeek = words.FindIndex(w => w.ToLowerInvariant().TrimEnd(',', '.') is "woche") is var wi && wi > 0
            && words[wi - 1].ToLowerInvariant() is "nächste" or "naechste" or "nächsten" or "naechsten" or "kommende" or "kommenden" ? wi : -1;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i].ToLowerInvariant().TrimEnd(',', '.', ':', ';');
            if (Typos.TryGetValue(w, out var fixedWord)) w = fixedWord;
            // „Sa 14 Uhr …“, „Fr. Elternabend“: Kürzel nur vorn oder vor einer Zahl (sonst ist „so“ ein Wort)
            if (Short.TryGetValue(w, out var longDay) && (words[i].EndsWith('.') || i + 1 < words.Count && (char.IsDigit(words[i + 1][0]) || words[i + 1].ToLowerInvariant() is "um" or "ab")))
                w = longDay;
            if (date != null) break;
            DateOnly? hit = w switch
            {
                "heute" or "today" or "tonight" => today,
                "morgen" or "tomorrow" => today.AddDays(1),
                "übermorgen" or "uebermorgen" => today.AddDays(2),
                "gestern" or "yesterday" => today.AddDays(-1),
                "vorgestern" => today.AddDays(-2),
                "wochenende" or "weekend" => today.DayOfWeek == DayOfWeek.Sunday ? today : today.AddDays(((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7),
                _ => null,
            };
            var dayIx = Array.IndexOf(Days, w) is var a and >= 0 ? a : Array.IndexOf(DaysEn, w);
            if (hit == null && dayIx >= 0)
            {
                var target = (DayOfWeek)((dayIx + 1) % 7);
                if (nextWeek >= 0)
                {
                    // „Dienstag nächste Woche“: der Dienstag der kommenden Kalenderwoche
                    var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7) + 7);
                    hit = monday.AddDays(dayIx);
                }
                else
                {
                    var ahead = ((int)target - (int)today.DayOfWeek + 7) % 7;
                    if (ahead == 0) ahead = 7;   // „Montag“ am Montag: der nächste
                    hit = today.AddDays(ahead);
                }
            }
            if (hit != null)
            {
                date = hit;
                words.RemoveAt(i);
                // „am“, „nächsten“, „next“, „on“ davor gehören dazu
                while (i > 0 && words[i - 1].ToLowerInvariant() is "am" or "nächsten" or "naechsten" or "kommenden" or "diesen" or "dieser" or "diesem" or "next" or "on" or "this" or "übers" or "ueber" or "über" or "das" or "zum")
                { words.RemoveAt(i - 1); i--; }
                // „heute morgen“ ist heute früh
                if (w is "heute" && i < words.Count && words[i].ToLowerInvariant().TrimEnd(',', '.') is "morgen") { morning = true; words.RemoveAt(i); }
                // Tageszeit danach: „morgen früh“, „Freitag Abend“
                else if (i < words.Count && DayPart(words[i]) is { } part) { if (part) evening = true; else morning = true; words.RemoveAt(i); }
                if (i > 0 && DayPart(words[i - 1]) is { } part2 && words[i - 1].ToLowerInvariant() is not "morgen") { if (part2) evening = true; else morning = true; words.RemoveAt(i - 1); i--; }
            }
        }
        if (nextWeek >= 0 && date != null)
            words.RemoveAll(w => w.ToLowerInvariant().TrimEnd(',', '.') is "woche" or "nächste" or "naechste" or "nächsten" or "naechsten" or "kommende" or "kommenden");
        s = " " + string.Join(' ', words) + " ";

        // Ende zuerst („bis 16 Uhr“), dann Beginn
        if (start == null)
        {
            var u = UntilRx().Match(s);
            if (u.Success && TryTime(u.Groups[1].Value, u.Groups[2].Value, out var e)) { end = e; s = s.Remove(u.Index, u.Length); }
            var t = TimeRx().Match(s);
            if (t.Success)
            {
                var (h, mi) = t.Groups[1].Success ? (t.Groups[1].Value, t.Groups[2].Value)
                    : t.Groups[3].Success ? (t.Groups[3].Value, t.Groups[4].Value) : (t.Groups[5].Value, t.Groups[6].Value);
                if (TryTime(h, mi, out var st))
                {
                    start = st;
                    // „um 3“ ohne Minuten meint am Tag meist 15 Uhr
                    if ((t.Groups[1].Success && !t.Groups[2].Success) || (t.Groups[5].Success && !t.Groups[6].Success)) guess = true;
                    s = s.Remove(t.Index, t.Length);
                }
            }
            if (start == null) end = null;
        }

        // Tageszeiten neben der Uhrzeit: „um 8 abends“, „abends um 8“, „3pm“
        var dp = Regex.Match(s, @"\s(abends|am abend|nachmittags|am nachmittag|pm|p\.m\.|nachts|in der nacht|morgens|früh|frueh|in der früh|am morgen|vormittags|am vormittag|a\.m\.)(?=\s)", RegexOptions.IgnoreCase);
        if (dp.Success && (start != null || date != null))
        {
            var v = dp.Groups[1].Value.ToLowerInvariant();
            if (v is "morgens" or "früh" or "frueh" or "in der früh" or "am morgen" or "vormittags" or "am vormittag" or "a.m.") morning = true; else if (v is not ("nachts" or "in der nacht")) evening = true;
            s = s.Remove(dp.Index, dp.Length);
        }
        if (start is { } st2)
        {
            if (evening && st2.Hour < 12) start = st2.AddHours(12);
            else if (!morning && guess && st2.Hour is >= 1 and <= 6) start = st2.AddHours(12);
            if (end is { } en && start is { } s3 && en <= s3 && en.Hour < 12) end = en.AddHours(12);
        }
        if (start != null && end != null && end <= start) end = null;

        // Füllwörter am Rand weg
        var title = Regex.Replace(s, @"\s+", " ").Trim().Trim(',', '-', ':', '–').Trim();
        title = Regex.Replace(title, @"^(?:am|um|an|zum|zur|für|fuer|ab|bis|von|vom)\s+", "", RegexOptions.IgnoreCase).Trim();
        title = Regex.Replace(title, @"\s+(?:am|um|an|ab|bis|von)$", "", RegexOptions.IgnoreCase).Trim().Trim(',', '-', ':').Trim();
        return new When(date, start, end, title, until, repeat, repeatText);
    }

    /// <summary>Tageszeit: true abends/nachmittags, false morgens/früh, null keine.</summary>
    private static bool? DayPart(string word) => word.ToLowerInvariant().TrimEnd(',', '.') switch
    {
        "abend" or "abends" or "nachmittag" or "nachmittags" or "evening" or "afternoon" or "tonight" => true,
        "früh" or "frueh" or "morgens" or "vormittag" or "vormittags" or "morning" => false,
        _ => null,
    };

    private static int Num(string w) => int.TryParse(w, out var n) ? n : Numbers.TryGetValue(w.ToLowerInvariant(), out var m) ? m : 0;

    private static DateOnly? MakeDate(string day, string month, string? year, DateOnly today)
    {
        if (!int.TryParse(day, out var d) || !int.TryParse(month, out var mo) || d is < 1 or > 31 || mo is < 1 or > 12) return null;
        var y = year != null && int.TryParse(year, out var yy) ? yy : today.Year;
        if (y < 100) y += 2000;
        try
        {
            var dt = new DateOnly(y, mo, d);
            // Ohne Jahr und schon vorbei: nächstes Jahr
            if (year == null && dt < today) dt = dt.AddYears(1);
            return dt;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Uhrzeiten in Worten zu Ziffern: „halb drei“ → „um 2:30“, „viertel nach vier“ → „um 4:15“, „3pm“ → „um 15:00“.</summary>
    private static string Words2Time(string s, ref bool guess)
    {
        var g = false;
        string H(string w, int add = 0) { var n = Num(w) + add; if (n <= 0) n += 12; return n.ToString(); }
        s = Regex.Replace(s, @"\b(?:um\s+)?halb\s+(" + NumWord + @")\b(?:\s*uhr)?", m => { g = true; return "um " + H(m.Groups[1].Value, -1) + ":30"; }, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(?:um\s+)?viertel\s+nach\s+(" + NumWord + @")\b(?:\s*uhr)?", m => { g = true; return "um " + H(m.Groups[1].Value) + ":15"; }, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(?:um\s+)?viertel\s+vor\s+(" + NumWord + @")\b(?:\s*uhr)?", m => { g = true; return "um " + H(m.Groups[1].Value, -1) + ":45"; }, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(?:um\s+)?(?:drei\s*viertel|dreiviertel)\s+(" + NumWord + @")\b(?:\s*uhr)?", m => { g = true; return "um " + H(m.Groups[1].Value, -1) + ":45"; }, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(?:um\s+)?(\d{1,2}|fünf|zehn|zwanzig)\s+(nach|vor)\s+(" + NumWord + @")\b(?:\s*uhr)?", m =>
        {
            var min = Num(m.Groups[1].Value); var h = Num(m.Groups[3].Value);
            if (min is < 1 or > 29 || h is < 1 or > 24) return m.Value;
            g = true;
            return m.Groups[2].Value.Equals("nach", StringComparison.OrdinalIgnoreCase) ? $"um {h}:{min:00}" : $"um {(h == 1 ? 12 : h - 1)}:{60 - min:00}";
        }, RegexOptions.IgnoreCase);
        // „um drei“, „um zehn Uhr“
        s = Regex.Replace(s, @"\bum\s+(eins|zwei|drei|vier|fünf|fuenf|sechs|sieben|acht|neun|zehn|elf|zwölf|zwoelf)\b(\s*uhr)?", m => { g = true; return "um " + Num(m.Groups[1].Value) + (m.Groups[2].Success ? " uhr" : ""); }, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(eins|zwei|drei|vier|fünf|fuenf|sechs|sieben|acht|neun|zehn|elf|zwölf|zwoelf)\s+uhr\b", m => { g = true; return Num(m.Groups[1].Value) + " uhr"; }, RegexOptions.IgnoreCase);
        // „14 Uhr 30“
        s = Regex.Replace(s, @"\b(\d{1,2})\s*uhr\s+(\d{2})\b", "$1:$2", RegexOptions.IgnoreCase);
        // „3pm“, „3 pm“, „10am“ - „10 am“ mit Leerzeichen ist meist das deutsche „am“
        s = Regex.Replace(s, @"\b(\d{1,2})(?::(\d{2}))?(?:\s*(pm|p\.m\.|a\.m\.)|(am))(?=\s|$|,)", m =>
        {
            var h = int.Parse(m.Groups[1].Value);
            if (h > 12) return m.Value;
            var pm = m.Groups[3].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase);
            h = pm ? (h % 12) + 12 : h % 12;
            return $"um {h}:{(m.Groups[2].Success ? m.Groups[2].Value : "00")}";
        }, RegexOptions.IgnoreCase);
        // Zeiträume: „von 14 bis 16 Uhr“, „14-16 Uhr“, „14 bis 16 Uhr“
        s = Regex.Replace(s, @"\b(?:von\s+)?(\d{1,2})(?:[:.](\d{2}))?\s*(?:uhr\s*)?(?:bis|-|–)\s*(\d{1,2})(?:[:.](\d{2}))?\s*(uhr|h)\b", m =>
            $"um {m.Groups[1].Value}:{(m.Groups[2].Success ? m.Groups[2].Value : "00")} bis {m.Groups[3].Value}:{(m.Groups[4].Success ? m.Groups[4].Value : "00")}", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bvon\s+(\d{1,2})(?:[:.](\d{2}))?\s+bis\s+(\d{1,2})(?:[:.](\d{2}))?(?=\s|$|,)", m =>
            $"um {m.Groups[1].Value}:{(m.Groups[2].Success ? m.Groups[2].Value : "00")} bis {m.Groups[3].Value}:{(m.Groups[4].Success ? m.Groups[4].Value : "00")}", RegexOptions.IgnoreCase);
        if (g) guess = true;
        return s;
    }

    /// <summary>Wiederholung aus „jeden Montag“ usw.: RRULE, Text für die Antwort, erster Tag.</summary>
    private static (string? Rule, string? Text, DateOnly? First) Repeat(Match m, DateOnly today)
    {
        DateOnly Next(int dayIx)
        {
            var target = (DayOfWeek)((dayIx + 1) % 7);
            return today.AddDays(((int)target - (int)today.DayOfWeek + 7) % 7);
        }
        if (m.Groups[3].Success)
        {
            var what = m.Groups[3].Value.ToLowerInvariant();
            var every2 = m.Groups[2].Success;
            var ix = Array.IndexOf(Days, what);
            if (ix >= 0)
                return ((every2 ? "FREQ=WEEKLY;INTERVAL=2" : "FREQ=WEEKLY") + ";BYDAY=" + ByDay[ix],
                    (every2 ? "jeden zweiten " : "jeden ") + Cap(what), Next(ix));
            return what switch
            {
                "tag" or "morgen" or "abend" => ("FREQ=DAILY", "täglich", today),
                "werktag" => ("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", "werktags", today.DayOfWeek is DayOfWeek.Saturday ? today.AddDays(2) : today.DayOfWeek is DayOfWeek.Sunday ? today.AddDays(1) : today),
                "woche" => (every2 ? "FREQ=WEEKLY;INTERVAL=2" : "FREQ=WEEKLY", every2 ? "alle zwei Wochen" : "wöchentlich", null),
                "monat" => ("FREQ=MONTHLY", "monatlich", null),
                "jahr" => ("FREQ=YEARLY", "jährlich", null),
                _ => (null, null, null),
            };
        }
        if (m.Groups[4].Success)
        {
            var n = Num(m.Groups[4].Value);
            var unit = m.Groups[5].Value.ToLowerInvariant();
            var freq = unit.StartsWith("tag") ? "DAILY" : unit.StartsWith("woche") ? "WEEKLY" : unit.StartsWith("monat") ? "MONTHLY" : "YEARLY";
            var word = n switch { 2 => "zwei", 3 => "drei", 4 => "vier", _ => n.ToString() };
            return ($"FREQ={freq};INTERVAL={n}", $"alle {word} {Cap(unit)}", null);
        }
        return m.Groups[6].Value.ToLowerInvariant() switch
        {
            "täglich" or "taeglich" or "every day" or "daily" => ("FREQ=DAILY", "täglich", null),
            "wöchentlich" or "woechentlich" or "weekly" => ("FREQ=WEEKLY", "wöchentlich", null),
            "monatlich" or "monthly" => ("FREQ=MONTHLY", "monatlich", null),
            "jährlich" or "jaehrlich" or "yearly" => ("FREQ=YEARLY", "jährlich", null),
            "werktags" => ("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", "werktags", null),
            _ => (null, null, null),
        };
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    private static bool TryTime(string h, string m, out TimeOnly time)
    {
        time = default;
        if (!int.TryParse(h, out var hh) || hh > 24) return false;
        if (hh == 24) hh = 0;
        var mm = 0;
        if (!string.IsNullOrEmpty(m) && (!int.TryParse(m, out mm) || mm > 59)) return false;
        time = new TimeOnly(hh, mm);
        return true;
    }

    /// <summary>Datum für Antworten: „heute“, „morgen“, „übermorgen“ oder „Freitag, 24.10.“ (anderes Jahr: mit Jahr).</summary>
    public static string DayText(DateOnly day, DateOnly today) =>
        day == today ? "heute" : day == today.AddDays(1) ? "morgen" : day == today.AddDays(2) ? "übermorgen"
        : day == today.AddDays(-1) ? "gestern"
        : day.ToString(day.Year == today.Year || day < today.AddDays(120) ? "dddd, d.M." : "dddd, d.M.yyyy", De);

    /// <summary>„am Freitag, 24.10.“ bzw. „heute“, „morgen“ - für Sätze wie „… steht am Freitag, 24.10. an“.</summary>
    public static string OnDay(DateOnly day, DateOnly today) =>
        Math.Abs(day.DayNumber - today.DayNumber) <= 2 && day >= today.AddDays(-1) ? DayText(day, today) : "am " + DayText(day, today);
}
