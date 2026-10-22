using System.Globalization;
using System.Text;

namespace DmOffice.Dav;

/// <summary>
/// Eine Zeile aus iCalendar (RFC 5545) oder vCard (RFC 6350) - beide teilen
/// dieselbe Schreibweise: NAME;PARAM=WERT:Inhalt.
/// </summary>
public sealed class Prop
{
    public string Name { get; set; }
    public List<KeyValuePair<string, string>> Params { get; } = new();
    /// <summary>Roh, also noch mit \, \; \n - siehe <see cref="Text"/>.</summary>
    public string Value { get; set; }

    public Prop(string name, string value)
    {
        Name = name.ToUpperInvariant();
        Value = value;
    }

    public string? Param(string name) =>
        Params.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public Prop With(string name, string value)
    {
        Params.RemoveAll(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        Params.Add(new(name.ToUpperInvariant(), value));
        return this;
    }

    /// <summary>Der Inhalt als Text, entschluesselt.</summary>
    public string Text => Ics.Unescape(Value);

    /// <summary>Alle TYPE-Werte, klein: "work", "cell" ...</summary>
    public List<string> Types() =>
        Params.Where(p => p.Key.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
              .SelectMany(p => p.Value.Split(','))
              .Select(t => t.Trim('"').ToLowerInvariant()).Where(t => t.Length > 0).ToList();
}

public sealed class Component
{
    public string Name { get; set; }
    public List<Prop> Props { get; } = new();
    public List<Component> Children { get; } = new();

    public Component(string name) => Name = name.ToUpperInvariant();

    public Prop? Get(string name) => Props.FirstOrDefault(p => p.Name == name);
    public IEnumerable<Prop> All(string name) => Props.Where(p => p.Name == name);
    public string? Text(string name) => Get(name)?.Text;

    public void Remove(string name) => Props.RemoveAll(p => p.Name == name);

    /// <summary>Setzt einen Textwert; leer oder null entfernt ihn.</summary>
    public void SetText(string name, string? text)
    {
        if (string.IsNullOrEmpty(text)) { Remove(name); return; }
        Set(name, Ics.Escape(text));
    }

    public Prop Set(string name, string raw)
    {
        var p = Get(name);
        if (p == null) { p = new Prop(name, raw); Props.Add(p); }
        else { p.Value = raw; p.Params.Clear(); }
        return p;
    }

    public IEnumerable<Component> Of(string name) => Children.Where(c => c.Name == name);

    public override string ToString()
    {
        var sb = new StringBuilder();
        Ics.Write(this, sb);
        return sb.ToString();
    }
}

public static class Ics
{
    // ---------------- Lesen ----------------

    /// <summary>Hoechste Verschachtelung, die gelesen wird. Echte Daten brauchen 3 (VCALENDAR › VEVENT › VALARM,
    /// VTIMEZONE › STANDARD); tiefer Verschachteltes wird samt Inhalt verworfen. Ohne Grenze liesse eine
    /// fremde Datei (Abo, Einladung, Import) das rekursive Schreiben den Stack ueberlaufen - Prozessende.</summary>
    public const int MaxDepth = 8;

    /// <summary>Alle Komponenten der obersten Ebene (VCALENDAR, VCARD).</summary>
    public static List<Component> Parse(string text)
    {
        var roots = new List<Component>();
        var stack = new Stack<Component>();
        int skipped = 0;
        foreach (var line in Unfold(text))
        {
            var p = ParseLine(line);
            if (p == null) continue;
            if (p.Name == "BEGIN")
            {
                if (skipped > 0 || stack.Count >= MaxDepth) { skipped++; continue; }
                var c = new Component(p.Value.Trim());
                if (stack.Count > 0) stack.Peek().Children.Add(c); else roots.Add(c);
                stack.Push(c);
            }
            else if (p.Name == "END")
            {
                if (skipped > 0) skipped--;
                else if (stack.Count > 0) stack.Pop();
            }
            else if (skipped == 0 && stack.Count > 0)
            {
                stack.Peek().Props.Add(p);
            }
        }
        return roots;
    }

    /// <summary>Ist die Verschachtelung tiefer als <see cref="MaxDepth"/>? Fuer Daten, die roh gespeichert und spaeter
    /// auch von Ical.Net gelesen werden.</summary>
    public static bool TooDeep(string text)
    {
        int depth = 0;
        foreach (var line in Unfold(text))
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { if (++depth > MaxDepth) return true; }
            else if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)) depth = Math.Max(0, depth - 1);
        }
        return false;
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var current = new StringBuilder();
        foreach (var raw in lines)
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (current.Length > 0) yield return current.ToString();
            current.Clear().Append(raw);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    internal static Prop? ParseLine(string line)
    {
        // Name und Parameter bis zum ersten Doppelpunkt ausserhalb von Anfuehrungszeichen
        int colon = -1;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon < 0) return null;

        var head = line[..colon];
        var segments = SplitUnquoted(head, ';');
        var name = segments[0];
        // vCard-Gruppen ("item1.EMAIL") - die Gruppe bleibt im Namen nicht
        // erhalten; die Zuordnung zu X-ABLabel geht dabei verloren, der Wert nicht.
        var dot = name.IndexOf('.');
        if (dot > 0) name = name[(dot + 1)..];
        var prop = new Prop(name, line[(colon + 1)..]);
        foreach (var seg in segments.Skip(1))
        {
            var eq = seg.IndexOf('=');
            if (eq < 0) prop.Params.Add(new("TYPE", seg));          // vCard 2.1: ;WORK;VOICE
            else prop.Params.Add(new(seg[..eq].ToUpperInvariant(), seg[(eq + 1)..].Trim('"')));
        }
        return prop;
    }

    private static List<string> SplitUnquoted(string s, char sep)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (var ch in s)
        {
            if (ch == '"') quoted = !quoted;
            if (ch == sep && !quoted) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    // ---------------- Schreiben ----------------

    public static void Write(Component c, StringBuilder sb)
    {
        sb.Append("BEGIN:").Append(c.Name).Append("\r\n");
        foreach (var p in c.Props) Fold(Line(p), sb);
        foreach (var child in c.Children) Write(child, sb);
        sb.Append("END:").Append(c.Name).Append("\r\n");
    }

    private static string Line(Prop p)
    {
        var sb = new StringBuilder(p.Name);
        foreach (var (k, v) in p.Params)
        {
            sb.Append(';').Append(k).Append('=');
            sb.Append(v.IndexOfAny(new[] { ':', ';', ',' }) >= 0 && !k.Equals("TYPE", StringComparison.OrdinalIgnoreCase)
                ? "\"" + v + "\"" : v);
        }
        return sb.Append(':').Append(p.Value).ToString();
    }

    /// <summary>Hoechstens 75 Byte je Zeile, ohne ein UTF-8-Zeichen zu teilen.</summary>
    private static void Fold(string line, StringBuilder sb)
    {
        int bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            int len = rune.Utf8SequenceLength;
            if (bytes + len > 75)
            {
                sb.Append("\r\n ");
                bytes = 1;
            }
            sb.Append(rune.ToString());
            bytes += len;
        }
        sb.Append("\r\n");
    }

    public static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Teilt einen strukturierten Wert (N, ADR) an ungeschuetzten Semikolons.</summary>
    public static List<string> Structured(string raw)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length) { sb.Append(raw[i]).Append(raw[++i]); continue; }
            if (raw[i] == ';') { result.Add(Unescape(sb.ToString())); sb.Clear(); }
            else sb.Append(raw[i]);
        }
        result.Add(Unescape(sb.ToString()));
        return result;
    }

    public static string JoinStructured(IEnumerable<string?> parts) =>
        string.Join(";", parts.Select(p => Escape(p ?? "")));

    // ---------------- Zeiten ----------------

    /// <summary>
    /// Ein Zeitpunkt aus DTSTART, DUE und dergleichen, als UTC. allDay:
    /// nur ein Datum (VALUE=DATE) - dann Mitternacht UTC dieses Tages.
    /// </summary>
    public static DateTime? Time(Prop? p, TimeZoneInfo fallback, out bool allDay)
    {
        allDay = false;
        if (p == null) return null;
        var v = p.Value.Trim();
        if (v.Length == 8 || p.Param("VALUE")?.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true)
        {
            allDay = true;
            return DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
        }
        bool utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        if (utc) return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        var tz = Zone(p.Param("TZID")) ?? fallback;
        return ToUtc(local, tz);
    }

    /// <summary>
    /// Ortszeit nach UTC. Eine Zeit in der Luecke beim Wechsel auf Sommerzeit
    /// (2027-03-28 02:30 in Berlin) gibt es nicht - ConvertTimeToUtc wirft dann.
    /// RFC 5545 3.3.5: mit dem Versatz vor der Luecke deuten, also um die Luecke
    /// vorruecken (02:30 wird 03:30 Sommerzeit).
    /// </summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (!tz.IsInvalidTime(local)) return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        // Rueckwaerts bis vor die Luecke, dort gilt der alte Versatz
        var before = local;
        for (int i = 0; i < 50 && tz.IsInvalidTime(before); i++) before = before.AddMinutes(-30);
        return DateTime.SpecifyKind(local - tz.GetUtcOffset(before), DateTimeKind.Utc);
    }

    public static TimeZoneInfo? Zone(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        // Manche Programme schreiben "/Europe/Berlin" oder Pfade wie "/mozilla.org/.../Europe/Berlin"
        var candidates = new[] { id, id.TrimStart('/'), string.Join("/", id.Split('/').TakeLast(2)) };
        foreach (var c in candidates)
            if (TimeZoneInfo.TryFindSystemTimeZoneById(c, out var tz)) return tz;
        return null;
    }

    public static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    public static string Date(DateTime t) => t.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    public static string Local(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// VTIMEZONE fuer eine Zone aus der Regel, die heute gilt - genug fuer
    /// Termine ab jetzt; Nextcloud und die gaengigen Programme lesen die
    /// IANA-Kennung ohnehin selbst.
    /// </summary>
    public static Component Timezone(TimeZoneInfo tz)
    {
        var vtz = new Component("VTIMEZONE");
        vtz.Set("TZID", tz.Id);
        var rule = tz.GetAdjustmentRules().LastOrDefault(r => r.DateStart <= DateTime.UtcNow && r.DateEnd >= DateTime.UtcNow);
        string Offset(TimeSpan o) => (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString("hhmm");

        if (rule == null || rule.DaylightDelta == TimeSpan.Zero)
        {
            var std = new Component("STANDARD");
            std.Set("DTSTART", "19700101T000000");
            std.Set("TZOFFSETFROM", Offset(tz.BaseUtcOffset));
            std.Set("TZOFFSETTO", Offset(tz.BaseUtcOffset));
            vtz.Children.Add(std);
            return vtz;
        }

        var baseOffset = tz.BaseUtcOffset + rule.BaseUtcOffsetDelta;
        var dstOffset = baseOffset + rule.DaylightDelta;
        vtz.Children.Add(Transition("DAYLIGHT", rule.DaylightTransitionStart, Offset(baseOffset), Offset(dstOffset)));
        vtz.Children.Add(Transition("STANDARD", rule.DaylightTransitionEnd, Offset(dstOffset), Offset(baseOffset)));
        return vtz;

        static Component Transition(string name, TimeZoneInfo.TransitionTime t, string from, string to)
        {
            var c = new Component(name);
            c.Set("DTSTART", $"1970{t.Month:00}{(t.IsFixedDateRule ? t.Day : 1):00}T{t.TimeOfDay:HHmmss}");
            c.Set("TZOFFSETFROM", from);
            c.Set("TZOFFSETTO", to);
            if (t.IsFixedDateRule)
                c.Set("RRULE", $"FREQ=YEARLY;BYMONTH={t.Month};BYMONTHDAY={t.Day}");
            else
            {
                var week = t.Week == 5 ? -1 : t.Week;
                c.Set("RRULE", $"FREQ=YEARLY;BYMONTH={t.Month};BYDAY={week}{t.DayOfWeek.ToString()[..2].ToUpperInvariant()}");
            }
            return c;
        }
    }

    public static string Stamp() => Utc(DateTime.UtcNow);
}
