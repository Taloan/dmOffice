using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace DmOffice.Pim;

/// <summary>Wie viele Vorkommen eine Abfrage insgesamt noch ausrechnen darf (über alle Serien).</summary>
internal sealed class SeriesBudget(long left)
{
    public long Left = left;
}

/// <summary>
/// Serien begrenzt ausrechnen. Ical.Net rechnet jedes Vorkommen einzeln aus (spürbar langsam) und spult ohne COUNT Schritt
/// für Schritt bis zum Zeitraum vor, mit COUNT rechnet es alles ab DTSTART aus. Damit eine dichte oder sehr alte Regel
/// (minütlich ab 1970) oder ein riesiger Zeitraum den Server nicht belegt: vorher die Menge schätzen, den Beginn der Serie
/// rechnerisch bis kurz vor den Zeitraum vorziehen, den Zeitraum je Serie kürzen. Gespeichert wird nichts davon.
/// </summary>
internal static class SeriesLimit
{
    /// <summary>Höchstens so viele Vorkommen je Serie und Abfrage (Monatsansicht stündlich: rund 1000).</summary>
    public const int PerSeries = 2000;
    /// <summary>Höchstens so viele Vorkommen je Abfrage über alle Serien; danach nur noch der Termin selbst.</summary>
    public const long PerRequest = 30000;
    /// <summary>So viele Schritte bis zum Zeitraum spult Ical.Net selbst vor, darüber wird der Beginn vorgezogen.</summary>
    private const double MaxSkip = 100_000;

    /// <summary>
    /// Vorkommen im Zeitraum, höchstens <see cref="PerSeries"/> je Regel (ab Beginn des Zeitraums gezählt).
    /// Null: nicht ausrechenbar (sekündliche Regel, Budget erschöpft) - dann zeigt der Aufrufer den Termin selbst.
    /// Verändert die geladenen Ereignisse (Beginn, COUNT), nicht die gespeicherten Daten.
    /// </summary>
    public static HashSet<Ical.Net.DataTypes.Occurrence>? Occurrences(Ical.Net.Calendar ical, DateTime from, DateTime to, SeriesBudget? budget = null)
    {
        if (to <= from) return [];
        var end = to;
        double total = 0;
        foreach (var ev in ical.Events)
        {
            total += ev.RecurrenceDates?.GetAllPeriods().Count() ?? 0;
            var rules = ev.RecurrenceRules?.ToList() ?? [];
            if (rules.Count == 0) { total += 1; continue; }
            if (ev.DtStart == null) return null;
            foreach (var rule in rules)
            {
                // Sekündlich lehnt Ical.Net ohnehin ab; nie ausrechnen
                if (rule.Frequency is Ical.Net.FrequencyType.Secondly) return null;
                double step = Seconds(rule.Frequency) * Math.Max(1, rule.Interval);
                double factor = Factor(rule);
                var start = ev.DtStart.AsUtc;
                DateTime until = rule.Until is null ? DateTime.MaxValue : rule.Until.AsUtc;
                int count = rule.Count ?? 0;
                bool counted = count > 0;
                double skip = from > start ? (from - start).TotalSeconds / step : 0;

                // Beginn vorziehen: um ganze Schritte, eine Reserve davor. Bei COUNT nur ohne BY-Listen (jeder Schritt = ein Vorkommen).
                bool fixedStep = rule.Frequency is Ical.Net.FrequencyType.Minutely or Ical.Net.FrequencyType.Hourly
                    or Ical.Net.FrequencyType.Daily or Ical.Net.FrequencyType.Weekly;
                if (counted ? skip * factor > PerSeries : skip > MaxSkip)
                {
                    if (!fixedStep || (counted && !Plain(rule))) return null;
                    long k = (long)Math.Floor(skip) - 1;
                    if (counted && k >= count) k = count - 1;   // Serie endete vor dem Zeitraum: nur das letzte Vorkommen
                    if (k > 0)
                    {
                        Shift(ev, k * step);
                        if (counted) { count -= (int)k; rule.Count = count; }
                        start = ev.DtStart.AsUtc;
                    }
                }

                // Mit COUNT rechnet Ical.Net auch alles vor dem Zeitraum aus
                if (counted && from > start) total += Math.Min((from - start).TotalSeconds / step * factor, count * factor);

                // Vorkommen im Zeitraum schätzen, bei zu vielen den Zeitraum kürzen
                var a = from > start ? from : start;
                var b = to < until ? to : until;
                double inWindow = b > a ? ((b - a).TotalSeconds / step + 1) * factor : 0;
                if (counted) inWindow = Math.Min(inWindow, count * factor);
                if (inWindow > PerSeries)
                {
                    var cut = a.AddSeconds(Math.Min(step * PerSeries / factor, (DateTime.MaxValue - a).TotalSeconds - 1));
                    if (cut < end) end = cut;
                    inWindow = PerSeries;
                }
                total += inWindow;
            }
        }
        if (budget != null)
        {
            if (budget.Left < total) return null;
            budget.Left -= (long)Math.Ceiling(total);
        }
        if (end <= from) return [];
        // Ical.Net liefert ab dem Beginn alle Vorkommen, die in den Zeitraum hineinreichen, der Reihe nach und ohne Ende
        return ical.GetOccurrences(new CalDateTime(DateTime.SpecifyKind(from, DateTimeKind.Utc), CalDateTime.UtcTzId, true))
            .TakeWhileBefore(new CalDateTime(DateTime.SpecifyKind(end, DateTimeKind.Utc), CalDateTime.UtcTzId, true)).ToHashSet();
    }

    private static double Seconds(Ical.Net.FrequencyType f) => f switch
    {
        Ical.Net.FrequencyType.Minutely => 60,
        Ical.Net.FrequencyType.Hourly => 3600,
        Ical.Net.FrequencyType.Daily => 86400,
        Ical.Net.FrequencyType.Weekly => 7 * 86400,
        Ical.Net.FrequencyType.Monthly => 28 * 86400,   // kürzester Monat: eher zu viele geschätzt
        _ => 365 * 86400,
    };

    /// <summary>
    /// Vorkommen je Schritt, eher zu hoch geschätzt. Eine BY-Liste vervielfacht nur, wenn ihre Einheit feiner ist als die
    /// Frequenz (RFC 5545, BYHOUR bei täglich); sonst filtert sie (BYDAY bei täglich) und zählt nicht.
    /// </summary>
    private static double Factor(RecurrencePattern r)
    {
        var fq = r.Frequency;   // Secondly < Minutely < Hourly < Daily < Weekly < Monthly < Yearly
        double f = 1;
        void M(int n, bool expands, double each = 1) { if (n > 0 && expands) f *= n * each; }
        M(r.BySecond?.Count ?? 0, fq > Ical.Net.FrequencyType.Secondly);
        M(r.ByMinute?.Count ?? 0, fq > Ical.Net.FrequencyType.Minutely);
        M(r.ByHour?.Count ?? 0, fq > Ical.Net.FrequencyType.Hourly);
        M(r.ByMonthDay?.Count ?? 0, fq >= Ical.Net.FrequencyType.Monthly, fq == Ical.Net.FrequencyType.Yearly && (r.ByMonth?.Count ?? 0) == 0 ? 12 : 1);
        M(r.ByYearDay?.Count ?? 0, fq == Ical.Net.FrequencyType.Yearly);
        M(r.ByWeekNo?.Count ?? 0, fq == Ical.Net.FrequencyType.Yearly, 7);
        M(r.ByMonth?.Count ?? 0, fq == Ical.Net.FrequencyType.Yearly);
        if (r.ByDay is { Count: > 0 } days && fq >= Ical.Net.FrequencyType.Weekly)
            f *= days.Sum(d => d.Offset != int.MinValue ? 1 : fq switch
            {
                Ical.Net.FrequencyType.Yearly => 53,
                Ical.Net.FrequencyType.Monthly => 5,
                _ => 1,
            });
        return Math.Min(f, 1e9);
    }

    /// <summary>Ohne BY-Listen: jeder Schritt der Regel ist genau ein Vorkommen.</summary>
    private static bool Plain(RecurrencePattern r) =>
        (r.BySecond?.Count ?? 0) + (r.ByMinute?.Count ?? 0) + (r.ByHour?.Count ?? 0) + (r.ByDay?.Count ?? 0) + (r.ByMonthDay?.Count ?? 0)
        + (r.ByYearDay?.Count ?? 0) + (r.ByWeekNo?.Count ?? 0) + (r.ByMonth?.Count ?? 0) + (r.BySetPosition?.Count ?? 0) == 0;

    /// <summary>Beginn (und Ende) um ganze Schritte verschieben; in Ortszeit der Zone, wie Ical.Net selbst weiterzählt.</summary>
    private static void Shift(CalendarEvent ev, double seconds)
    {
        // DURATION bleibt, wie sie ist; DTEND wandert mit
        var s = ev.DtStart;
        var e = ev.DtEnd;
        ev.DtStart = new CalDateTime(s.Value.AddSeconds(seconds), s.TzId, s.HasTime);
        if (e != null) ev.DtEnd = new CalDateTime(e.Value.AddSeconds(seconds), e.TzId, e.HasTime);
    }
}
