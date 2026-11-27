using Ical.Net.DataTypes;
using Ical.Net;
using System.Diagnostics;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Serien begrenzt ausrechnen: dichte, alte oder endlose Regeln belegen den Server nicht, normale bleiben unverändert.</summary>
public class SeriesLimitTests
{
    private static Ical.Net.Calendar Load(string dtstart, string rrule, string? dtend = null) => Ical.Net.Calendar.Load(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//DE\r\nBEGIN:VEVENT\r\nUID:x\r\nDTSTAMP:20260101T000000Z\r\n" +
        $"DTSTART:{dtstart}\r\nDTEND:{dtend ?? dtstart}\r\nRRULE:{rrule}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static readonly DateTime Day = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Normale_Serie_unveraendert()
    {
        var limited = SeriesLimit.Occurrences(Load("20200106T080000Z", "FREQ=WEEKLY;BYDAY=MO,WE", "20200106T090000Z"), Day, Day.AddDays(42))!;
        var plain = Load("20200106T080000Z", "FREQ=WEEKLY;BYDAY=MO,WE", "20200106T090000Z").GetOccurrences(new CalDateTime(Day, CalDateTime.UtcTzId, true)).TakeWhileBefore(new CalDateTime(Day.AddDays(42), CalDateTime.UtcTzId, true));
        Assert.Equal(12, limited.Count);
        Assert.Equal(plain.Select(o => o.Period.StartTime.AsUtc).OrderBy(x => x), limited.Select(o => o.Period.StartTime.AsUtc).OrderBy(x => x));
    }

    [Fact]
    public void Minuetlich_ab_1970_schnell_und_gedeckelt()
    {
        var sw = Stopwatch.StartNew();
        var occ = SeriesLimit.Occurrences(Load("19700101T000000Z", "FREQ=MINUTELY"), Day, Day.AddYears(1))!;
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
        Assert.InRange(occ.Count, 1, SeriesLimit.PerSeries + 1);
        // Vorkommen liegen auf dem Minutenraster und beginnen am Anfang des Zeitraums
        Assert.Equal(Day, occ.Min(o => o.Period.StartTime.AsUtc));
    }

    [Fact]
    public void Count_ab_1970_vorgezogen_mit_richtigen_Vorkommen()
    {
        var sw = Stopwatch.StartNew();
        // 100 Mio. Minuten ab 1970 reichen bis 2160; vorgezogen statt alles ab 1970 auszurechnen
        var occ = SeriesLimit.Occurrences(Load("19700101T000000Z", "FREQ=MINUTELY;COUNT=100000000"), Day, Day.AddHours(2))!;
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
        Assert.Equal(Enumerable.Range(0, 120).Select(i => Day.AddMinutes(i)), occ.Select(o => o.Period.StartTime.AsUtc).Where(t => t >= Day).OrderBy(t => t).Take(120));

        // Endet vor dem Zeitraum: nichts darin
        var ended = SeriesLimit.Occurrences(Load("19700101T000000Z", "FREQ=DAILY;COUNT=10000"), Day, Day.AddDays(7))!;
        Assert.DoesNotContain(ended, o => o.Period.StartTime.AsUtc >= Day);
        var ten = SeriesLimit.Occurrences(Load("19700101T000000Z", "FREQ=DAILY;COUNT=30000"), Day, Day.AddDays(7))!;
        Assert.Contains(ten, o => o.Period.StartTime.AsUtc == Day);   // 1970 + 30000 Tage reicht bis 2052
    }

    [Fact]
    public void Riesiger_Zeitraum_gekuerzt()
    {
        var sw = Stopwatch.StartNew();
        var occ = SeriesLimit.Occurrences(Load("19000101T000000Z", "FREQ=DAILY"), new DateTime(1900, 1, 1), new DateTime(2900, 1, 1))!;
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
        Assert.InRange(occ.Count, 1, SeriesLimit.PerSeries + 1);
    }

    [Fact]
    public void Sekuendlich_nicht_ausgerechnet_und_Budget_greift()
    {
        Assert.Null(SeriesLimit.Occurrences(Load("20260101T000000Z", "FREQ=SECONDLY"), Day, Day.AddDays(1)));
        var budget = new SeriesBudget(SeriesLimit.PerSeries * 2);
        Assert.NotNull(SeriesLimit.Occurrences(Load("20260101T000000Z", "FREQ=MINUTELY"), Day, Day.AddDays(30), budget));
        Assert.NotNull(SeriesLimit.Occurrences(Load("20260101T000000Z", "FREQ=MINUTELY"), Day, Day.AddDays(30), budget));
        Assert.Null(SeriesLimit.Occurrences(Load("20260101T000000Z", "FREQ=MINUTELY"), Day, Day.AddDays(30), budget));
    }
}
