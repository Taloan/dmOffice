using System.Globalization;

namespace DmOffice.Core;

/// <summary>
/// Zeitplan des Core-Backups (das Files-Backup folgt ihm), Kennung backupcron: entweder „alle N Stunden, Tage oder Wochen“
/// mit Uhrzeit (bei Wochen mit Wochentag) oder eine eigene Regel in Cron-Syntax. Ohne Schalter oder ohne Einstellung gilt der
/// frühere feste Abstand (S3BackupEveryHours).
/// </summary>
public static class BackupSchedule
{
    public const string KindEvery = "every", KindCron = "cron";

    /// <summary>Ein Zeitplan aus einzelnen Werten (Vorschau, Datenexport) - in der Form der Servereinstellungen, damit alles eine Rechnung nutzt.</summary>
    public static ServerSettings Plan(string? kind, string? at, int? count, string? unit, int? weekday, string? cron) => new()
    {
        S3BackupScheduleKind = kind, S3BackupAt = at ?? "03:00", S3BackupEveryCount = Math.Clamp(count ?? 1, 1, 999),
        S3BackupEveryUnit = unit is "h" or "d" or "w" ? unit : "d", S3BackupWeekday = Math.Clamp(weekday ?? 1, 0, 6), S3BackupCron = cron,
    };

    /// <summary>Gilt der neue Zeitplan?</summary>
    public static bool Active(ServerSettings s) => Features.On(Features.BackupCron) && s.S3BackupScheduleKind is KindEvery or KindCron;

    /// <summary>Abstand in Stunden für Haltefristen und „überfällig“: fester Abstand, bei Cron der größte Abstand der nächsten Läufe.</summary>
    public static int SpanHours(ServerSettings s, DateTime now)
    {
        if (!Active(s)) return -1;
        if (s.S3BackupScheduleKind == KindEvery) return Math.Max(1, EveryHours(s));
        var runs = Cron.TryParse(s.S3BackupCron, out var cron, out _) ? cron!.Next(now, 6) : [];
        if (runs.Count < 2) return 24;
        return (int)Math.Ceiling(runs.Zip(runs.Skip(1), (a, b) => (b - a).TotalHours).Max());
    }

    private static int EveryHours(ServerSettings s) => Math.Clamp(s.S3BackupEveryCount, 1, 999) * (s.S3BackupEveryUnit switch { "d" => 24, "w" => 168, _ => 1 });

    private static TimeOnly At(ServerSettings s) => TimeOnly.TryParse(s.S3BackupAt ?? "03:00", CultureInfo.InvariantCulture, out var at) ? at : new TimeOnly(3, 0);

    /// <summary>Fällig: Seit dem letzten geplanten Zeitpunkt (bis jetzt) wurde noch nicht gesichert.</summary>
    public static bool Due(ServerSettings s, DateTime now, DateTime? lastRunUtc)
    {
        var slot = Previous(s, now);
        if (slot == null) return false;
        return lastRunUtc is not { } last || last.ToLocalTime() < slot;
    }

    /// <summary>Der letzte geplante Zeitpunkt bis jetzt (Ortszeit des Servers).</summary>
    public static DateTime? Previous(ServerSettings s, DateTime now)
    {
        if (s.S3BackupScheduleKind == KindCron) return Cron.TryParse(s.S3BackupCron, out var c, out _) ? c!.Previous(now) : null;
        var at = At(s);
        var hours = EveryHours(s);
        var anchor = now.Date + at.ToTimeSpan();
        if (anchor > now) anchor = anchor.AddDays(-1);
        switch (s.S3BackupEveryUnit)
        {
            case "w":
                // Der gewählte Wochentag; alle N Wochen gerechnet ab einer festen Woche (1.1.2001 war ein Montag)
                while ((int)anchor.DayOfWeek != Math.Clamp(s.S3BackupWeekday, 0, 6)) anchor = anchor.AddDays(-1);
                var weeks = Math.Clamp(s.S3BackupEveryCount, 1, 999);
                var since = (int)Math.Floor((anchor.Date - new DateTime(2001, 1, 1)).TotalDays / 7);
                return anchor.AddDays(-7 * (((since % weeks) + weeks) % weeks));
            case "d":
                var days = Math.Clamp(s.S3BackupEveryCount, 1, 999);
                var n = (int)(anchor.Date - new DateTime(2001, 1, 1)).TotalDays;
                return anchor.AddDays(-(((n % days) + days) % days));
            default:
                // Alle N Stunden ab der Uhrzeit (03:00, 09:00, 15:00 …)
                return anchor.AddHours(Math.Floor((now - anchor).TotalHours / hours) * hours);
        }
    }

    /// <summary>Die nächsten Läufe ab jetzt - für die Vorschau unter dem Zeitplan.</summary>
    public static List<DateTime> Next(ServerSettings s, DateTime now, int count)
    {
        if (s.S3BackupScheduleKind == KindCron) return Cron.TryParse(s.S3BackupCron, out var c, out _) ? c!.Next(now, count) : [];
        var list = new List<DateTime>();
        var step = s.S3BackupEveryUnit switch { "w" => TimeSpan.FromDays(7 * Math.Clamp(s.S3BackupEveryCount, 1, 999)), "d" => TimeSpan.FromDays(Math.Clamp(s.S3BackupEveryCount, 1, 999)), _ => TimeSpan.FromHours(EveryHours(s)) };
        var t = Previous(s, now);
        if (t == null) return list;
        // Unter einem Tag beginnt jeder Tag wieder bei der Uhrzeit
        while (list.Count < count)
        {
            t = t.Value + step;
            if (s.S3BackupEveryUnit is not ("d" or "w") && t.Value.TimeOfDay < At(s).ToTimeSpan() && (t.Value - step).Date < t.Value.Date) t = t.Value.Date + At(s).ToTimeSpan();
            list.Add(t.Value);
        }
        return list;
    }
}

/// <summary>
/// Cron-Regel mit fünf Feldern: Minute Stunde Tag Monat Wochentag (0 oder 7 = Sonntag). Erlaubt: *, Listen (1,15), Bereiche (1-5),
/// Schritte (*/2, 1-10/3), Namen (MON, JAN) und beim Wochentag „n-ter im Monat“ (MON#1). Anders als klassisches Cron gelten Tag
/// UND Wochentag zusammen, wenn beide eingeschränkt sind - so heißt „0 3 1-7 * MON“ wie gemeint „erster Montag im Monat“.
/// </summary>
public sealed class Cron
{
    private readonly bool[] _min = new bool[60], _hour = new bool[24], _dom = new bool[32], _month = new bool[13], _dow = new bool[7];
    private readonly HashSet<(int Dow, int Nth)> _nth = [];
    private bool _domAny, _dowAny;

    private static readonly string[] Months = ["", "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] Days = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    public static bool TryParse(string? text, out Cron? cron, out string? error)
    {
        cron = null; error = null;
        var parts = (text ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) { error = "Eine Cron-Regel hat fünf Felder: Minute Stunde Tag Monat Wochentag."; return false; }
        var c = new Cron();
        try
        {
            Fill(parts[0], c._min, 0, 59, null, "Minute");
            Fill(parts[1], c._hour, 0, 23, null, "Stunde");
            c._domAny = parts[2] is "*" or "?";
            Fill(parts[2], c._dom, 1, 31, null, "Tag");
            Fill(parts[3], c._month, 1, 12, Months, "Monat");
            c._dowAny = parts[4] is "*" or "?";
            foreach (var item in parts[4].Split(','))
            {
                if (item.Contains('#'))
                {
                    var bits = item.Split('#');
                    var d = Value(bits[0], 0, 7, Days, "Wochentag") % 7;
                    if (!int.TryParse(bits[1], out var nth) || nth is < 1 or > 5) throw new FormatException("Nach # steht der wievielte Wochentag im Monat (1 bis 5).");
                    c._nth.Add((d, nth));
                }
                else
                {
                    var tmp = new bool[8];
                    Fill(item, tmp, 0, 7, Days, "Wochentag");
                    for (var i = 0; i < 8; i++) if (tmp[i]) c._dow[i % 7] = true;
                }
            }
        }
        catch (FormatException e) { error = e.Message; return false; }
        cron = c;
        return true;
    }

    private static void Fill(string field, bool[] set, int min, int max, string[]? names, string what)
    {
        foreach (var item in field.Split(','))
        {
            var step = 1;
            var range = item;
            if (item.Contains('/'))
            {
                var bits = item.Split('/');
                range = bits[0];
                if (!int.TryParse(bits[1], out step) || step < 1) throw new FormatException($"{what}: ungültiger Schritt „{bits[1]}“.");
            }
            int from, to;
            if (range is "*" or "?") { from = min; to = max; }
            else if (range.Contains('-'))
            {
                var bits = range.Split('-');
                from = Value(bits[0], min, max, names, what); to = Value(bits[1], min, max, names, what);
                if (to < from) throw new FormatException($"{what}: Bereich „{range}“ ist verkehrt herum.");
            }
            else { from = Value(range, min, max, names, what); to = item.Contains('/') ? max : from; }
            for (var i = from; i <= to; i += step) set[i] = true;
        }
    }

    private static int Value(string text, int min, int max, string[]? names, string what)
    {
        if (names != null)
        {
            var i = Array.FindIndex(names, n => n.Length > 0 && string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        if (!int.TryParse(text, out var v) || v < min || v > max) throw new FormatException($"{what}: „{text}“ liegt nicht zwischen {min} und {max}.");
        return v;
    }

    private bool DayMatches(DateTime d)
    {
        if (!_month[d.Month]) return false;
        var dow = (int)d.DayOfWeek;
        var byDow = _dowAny || _dow[dow] || _nth.Contains((dow, (d.Day - 1) / 7 + 1));
        return (_domAny || _dom[d.Day]) && byDow;
    }

    /// <summary>Die nächsten Zeitpunkte nach <paramref name="after"/> (Ortszeit), höchstens fünf Jahre voraus.</summary>
    public List<DateTime> Next(DateTime after, int count)
    {
        var list = new List<DateTime>();
        var day = after.Date;
        for (var i = 0; i < 366 * 5 && list.Count < count; i++, day = day.AddDays(1))
        {
            if (!DayMatches(day)) continue;
            for (var h = 0; h < 24 && list.Count < count; h++)
                if (_hour[h])
                    for (var m = 0; m < 60 && list.Count < count; m++)
                        if (_min[m] && day.AddHours(h).AddMinutes(m) is var t && t > after) list.Add(t);
        }
        return list;
    }

    /// <summary>Der letzte Zeitpunkt bis einschließlich <paramref name="now"/>, höchstens ein Jahr zurück.</summary>
    public DateTime? Previous(DateTime now)
    {
        var day = now.Date;
        for (var i = 0; i < 400; i++, day = day.AddDays(-1))
        {
            if (!DayMatches(day)) continue;
            for (var h = 23; h >= 0; h--)
                if (_hour[h])
                    for (var m = 59; m >= 0; m--)
                        if (_min[m] && day.AddHours(h).AddMinutes(m) is var t && t <= now) return t;
        }
        return null;
    }
}
