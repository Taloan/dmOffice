using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Kalender-Erinnerungen 2.2: Auslösezeit (Europe/Berlin, Zeitumstellung), VALARM-Rundweg, Vorrang, Abo, Aufgaben.</summary>
[Collection("server")]
public class CalendarRemindTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public CalendarRemindTests(SharedCalendarFixture fx) => _fx = fx;

    private static readonly string[] Mine = [Features.CalRemind, Features.CalRemDefault, Features.CalBulkRemind, Features.CalRemTask];
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static T With<T>(bool on, Func<T> run)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(on ? before.Where(x => !Mine.Contains(x)) : before.Concat(Mine).Distinct());
        try { return run(); } finally { Features.Set(before); }
    }

    private static async Task WithAsync(bool on, Func<Task> run)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(on ? before.Where(x => !Mine.Contains(x)) : before.Concat(Mine).Distinct());
        try { await run(); } finally { Features.Set(before); }
    }

    private static UserSpace NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmremind");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        PimStore.EnsureDefaults(db);
        return new UserSpace(new UserRecord { Login = "t" }, db);
    }

    private static DateTime U(string s) => DateTime.SpecifyKind(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    [Theory]
    // Ganztägig „N Tage vorher um HH:MM“ (At), am Vortag um 18:00: Winterzeit (UTC+1), Sommerzeit (UTC+2), am Tag der Umstellung
    [InlineData("2026-01-15", 360, "2026-01-14T17:00:00")]
    [InlineData("2026-07-15", 360, "2026-07-14T16:00:00")]
    [InlineData("2026-03-30", 360, "2026-03-29T16:00:00")]   // Vortag ist der Umstellungstag (Sommerzeit ab 2:00)
    [InlineData("2026-03-29", 360, "2026-03-28T17:00:00")]   // Termin am Umstellungstag, Vortag noch Winterzeit
    [InlineData("2026-10-26", 1800, "2026-10-24T16:00:00")]  // 2 Tage vorher um 18:00, über das Ende der Sommerzeit
    [InlineData("2026-10-25", 360, "2026-10-24T16:00:00")]
    [InlineData("2026-05-04", -480, "2026-05-04T06:00:00")]  // am Tag selbst um 8:00
    public void Ganztaegig_feuert_zur_Ortszeit(string day, int minutes, string expectedUtc)
    {
        var d = DateOnly.Parse(day);
        var start = Ics.ToUtc(d.ToDateTime(TimeOnly.MinValue), Berlin);
        Assert.Equal(U(expectedUtc), CalendarReminders.Fire(start, true, d, minutes, true, Berlin));
    }

    [Theory]
    // Ganztägig relativ: ab 9:00 Ortszeit des Tages, Tage als Kalendertage, Rest genau - Winter, Sommer, Umstellung
    [InlineData("2026-01-15", 60, "2026-01-15T07:00:00")]    // 1 Stunde vorher = 8:00 MEZ
    [InlineData("2026-07-15", 60, "2026-07-15T06:00:00")]    // 8:00 MESZ
    [InlineData("2026-07-15", 0, "2026-07-15T07:00:00")]     // zur Zeit des Termins = 9:00
    [InlineData("2026-03-30", 1440, "2026-03-29T07:00:00")]  // 1 Tag vorher = Umstellungstag 9:00 MESZ
    [InlineData("2026-03-29", 1440, "2026-03-28T08:00:00")]  // Termin am Umstellungstag, Vortag 9:00 MEZ
    [InlineData("2026-03-29", 420, "2026-03-29T00:00:00")]   // 7 Stunden genau vor 9:00 MESZ (07:00Z) = 1:00 MEZ
    [InlineData("2026-10-26", 1440, "2026-10-25T08:00:00")]  // Vortag nach Ende der Sommerzeit, 9:00 MEZ
    [InlineData("2026-10-25", 120, "2026-10-25T06:00:00")]   // 7:00 MEZ am Umstellungstag
    [InlineData("2026-10-31", 10080, "2026-10-24T07:00:00")] // 1 Woche vorher, noch Sommerzeit
    public void Ganztaegig_relativ_ab_9_Uhr(string day, int minutes, string expectedUtc)
    {
        var d = DateOnly.Parse(day);
        var start = Ics.ToUtc(d.ToDateTime(TimeOnly.MinValue), Berlin);
        Assert.Equal(U(expectedUtc), CalendarReminders.Fire(start, true, d, minutes, false, Berlin));
    }

    [Fact]
    public void Tage_zaehlen_als_Kalendertage_Stunden_genau()
    {
        // 29.03.2026 10:00 Sommerzeit = 08:00Z; „1 Tag vorher“ = 28.03. 10:00 Winterzeit = 09:00Z (nicht 24 h)
        Assert.Equal(U("2026-03-28T09:00:00"), CalendarReminders.Fire(U("2026-03-29T08:00:00"), false, default, 1440, false, Berlin));
        // „24 Stunden“ als Minuten ergibt dasselbe (Tage werden nominal gerechnet), 2 Stunden genau
        Assert.Equal(U("2026-03-29T06:00:00"), CalendarReminders.Fire(U("2026-03-29T08:00:00"), false, default, 120, false, Berlin));
        // 1 Woche vor 01.11.2026 9:00 (Winterzeit, 08:00Z) = 25.10. 9:00 Winterzeit = 08:00Z
        Assert.Equal(U("2026-10-25T08:00:00"), CalendarReminders.Fire(U("2026-11-01T08:00:00"), false, default, 10080, true, Berlin));
    }

    [Theory]
    [InlineData(0, "-PT0M")]
    [InlineData(15, "-PT15M")]
    [InlineData(360, "-PT6H")]
    [InlineData(1800, "-P1DT6H")]
    [InlineData(1440, "-P1D")]
    [InlineData(10080, "-P7D")]
    [InlineData(-480, "PT8H")]
    [InlineData(90, "-PT1H30M")]
    public void Trigger_hin_und_zurueck(int minutes, string trigger)
    {
        Assert.Equal(trigger, CalendarReminders.Trigger(minutes));
        Assert.Equal(minutes, CalendarReminders.ParseTrigger(trigger));
    }

    [Fact]
    public void Trigger_liest_Wochen_und_Sekunden()
    {
        Assert.Equal(10080, CalendarReminders.ParseTrigger("-P1W"));
        Assert.Equal(-15, CalendarReminders.ParseTrigger("PT15M"));
        Assert.Equal(15, CalendarReminders.ParseTrigger("-PT900S"));
        Assert.Null(CalendarReminders.ParseTrigger("20261231T100000Z"));
    }

    private static string AllDay(UserSpace space, long cal, string title, string day, List<RemindRule>? reminders = null, int? reminder = null) =>
        CalendarModel.SaveEvent(space, new EventInput
        {
            Calendar = cal, Title = title, Start = day, End = DateOnly.Parse(day).AddDays(1).ToString("yyyy-MM-dd"), AllDay = true,
            Reminders = reminders, Reminder = reminder,
        });

    private static long Calendar(UserSpace space) => PimStore.Collections(space.Db, PimStore.Calendar).First(c => c.Components.Contains("VEVENT")).Id;

    [Fact]
    public void Mehrere_VALARM_schreiben_und_lesen()
    {
        With(true, () =>
        {
            var space = NewSpace();
            var cal = Calendar(space);
            var id = AllDay(space, cal, "Papier", "2030-03-05", [new() { Minutes = 360, At = true }, new() { Minutes = 1800, At = true }, new() { Minutes = -480, At = true }]);
            var uri = id[(id.IndexOf('/') + 1)..];
            var data = PimStore.Object(space.Db, cal, uri)!.Data;
            Assert.Contains("TRIGGER:-PT6H", data);
            Assert.Contains("TRIGGER:-P1DT6H", data);
            Assert.Contains("TRIGGER:PT8H", data);
            Assert.DoesNotContain("X-DM-REL", data);
            var e = CalendarModel.Events(space, U("2030-03-01T00:00:00"), U("2030-03-10T00:00:00"), [cal]).Single();
            Assert.Equal(new[] { -480, 360, 1800 }, e.Alarms!.Select(a => a.Minutes).Order());
            Assert.All(e.Alarms!, a => Assert.True(a.At));
            Assert.Equal(360, e.Reminder);   // altes Feld: der erste Alarm davor

            // Altes Feld allein (ältere Oberfläche, App): ändert nur den ersten, weitere bleiben
            CalendarModel.SaveEvent(space, new EventInput { Calendar = cal, Uri = uri, Title = "Papier", Start = "2030-03-05", End = "2030-03-06", AllDay = true, Reminder = 60 });
            e = CalendarModel.Events(space, U("2030-03-01T00:00:00"), U("2030-03-10T00:00:00"), [cal]).Single();
            Assert.Equal(new[] { -480, 60, 1800 }, e.Alarms!.Select(a => a.Minutes).Order());

            // Leere Liste entfernt alle relativen Alarme
            CalendarModel.SaveEvent(space, new EventInput { Calendar = cal, Uri = uri, Title = "Papier", Start = "2030-03-05", End = "2030-03-06", AllDay = true, Reminders = [] });
            Assert.DoesNotContain("VALARM", PimStore.Object(space.Db, cal, uri)!.Data);
            return 0;
        });
    }

    [Fact]
    public void Ganztaegig_relativ_im_VALARM_mit_X_DM_REL()
    {
        With(true, () =>
        {
            var space = NewSpace();
            var cal = Calendar(space);
            // 1 Stunde vorher (8:00) und 1 Tag vorher (Vortag 9:00): im VALARM RFC-gerecht ab 0:00, markiert
            var id = AllDay(space, cal, "Bio", "2030-03-05", [new() { Minutes = 60 }, new() { Minutes = 1440 }, new() { Minutes = 360, At = true }]);
            var uri = id[(id.IndexOf('/') + 1)..];
            var data = PimStore.Object(space.Db, cal, uri)!.Data;
            Assert.Contains("TRIGGER:PT8H", data);    // 60 - 540
            Assert.Contains("TRIGGER:-PT15H", data);  // 1440 - 540
            Assert.Contains("TRIGGER:-PT6H", data);   // At bleibt ab 0:00
            Assert.Equal(2, data.Split("X-DM-REL:TRUE").Length - 1);
            var e = CalendarModel.Events(space, U("2030-03-01T00:00:00"), U("2030-03-10T00:00:00"), [cal]).Single();
            Assert.Equal(new[] { "1440", "360at", "60" }, e.Alarms!.Select(a => a.Minutes + (a.At ? "at" : "")).Order());
            Assert.Equal(new[] { "Bio@03-04 08:00", "Bio@03-04 17:00", "Bio@03-05 07:00" }, Fired(space, cal, U("2030-03-01T00:00:00"), U("2030-03-08T00:00:00")));

            // Unverändert gespeichert: die Alarme bleiben dieselben (kein Neuschreiben)
            CalendarModel.SaveEvent(space, new EventInput { Calendar = cal, Uri = uri, Title = "Bio", Start = "2030-03-05", End = "2030-03-06", AllDay = true,
                Reminders = [new() { Minutes = 1440 }, new() { Minutes = 60 }, new() { Minutes = 360, At = true }] });
            Assert.Equal(3, PimStore.Object(space.Db, cal, uri)!.Data.Split("BEGIN:VALARM").Length - 1);
            return 0;
        });
    }

    [Fact]
    public void Ausdruecklich_keine_Erinnerung_trotz_Standard()
    {
        With(true, () =>
        {
            var space = NewSpace();
            var cal = Calendar(space);
            var from = U("2030-03-01T00:00:00");
            var to = U("2030-03-08T00:00:00");
            // Ohne Standard braucht „keine“ keine Markierung
            var id = AllDay(space, cal, "Bio", "2030-03-05", []);
            var uri = id[(id.IndexOf('/') + 1)..];
            Assert.DoesNotContain("X-DM-NOREMIND", PimStore.Object(space.Db, cal, uri)!.Data);

            CalendarReminders.SetDefault(space.Db, cal, new CalendarRemindDefault { Reminders = [new() { Minutes = 60 }] });
            Assert.Equal(new[] { "Bio@03-05 07:00" }, Fired(space, cal, from, to));
            // Mit Standard: ausdrücklich keine (steht im Termin, damit auch App und geteilte Personen es sehen)
            CalendarModel.SaveEvent(space, new EventInput { Calendar = cal, Uri = uri, Title = "Bio", Start = "2030-03-05", End = "2030-03-06", AllDay = true, Reminders = [] });
            Assert.Contains("X-DM-NOREMIND:TRUE", PimStore.Object(space.Db, cal, uri)!.Data);
            Assert.Empty(Fired(space, cal, from, to));
            Assert.Empty(CalendarReminders.Decorate(space.Db, CalendarModel.Events(space, from, to, [cal]), cal).Single().Reminders!);
            // Wieder „Standard des Kalenders“
            CalendarModel.SaveEvent(space, new EventInput { Calendar = cal, Uri = uri, Title = "Bio", Start = "2030-03-05", End = "2030-03-06", AllDay = true, RemindDefault = true });
            Assert.DoesNotContain("X-DM-NOREMIND", PimStore.Object(space.Db, cal, uri)!.Data);
            Assert.Equal(new[] { "Bio@03-05 07:00" }, Fired(space, cal, from, to));
            Assert.Null(CalendarReminders.Decorate(space.Db, CalendarModel.Events(space, from, to, [cal]), cal).Single().Reminders);
            return 0;
        });
    }

    [Fact]
    public void Ausgeschaltet_bleibt_es_bei_einer_Erinnerung()
    {
        With(false, () =>
        {
            var space = NewSpace();
            var cal = Calendar(space);
            var id = AllDay(space, cal, "Papier", "2030-03-05", [new() { Minutes = 360 }, new() { Minutes = 1800 }], reminder: 15);
            var data = PimStore.Object(space.Db, cal, id[(id.IndexOf('/') + 1)..])!.Data;
            Assert.Contains("TRIGGER:-PT15M", data);
            Assert.DoesNotContain("PT6H", data);
            Assert.False(CalendarReminders.Active);
            return 0;
        });
    }

    private static long Subscribed(UserSpace space, string ics)
    {
        var c = PimStore.CreateCollection(space.Db, PimStore.Calendar, "abo" + Guid.NewGuid().ToString("N")[..6], "Abfuhr", null, "VEVENT");
        Subscriptions.Add(space.Db, new Subscription { Collection = c.Id, Url = "http://127.0.0.1/abfuhr.ics" });
        foreach (var (uri, data) in PimApi.Split(c, ics)) PimStore.Put(space.Db, c, uri, data, zone: space.Zone);
        return c.Id;
    }

    private const string Abfuhr = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//DE
        BEGIN:VEVENT
        UID:papier-1
        DTSTART;VALUE=DATE:20300305
        DTEND;VALUE=DATE:20300306
        SUMMARY:Papiertonne
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-PT15M
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:rest-1
        DTSTART;VALUE=DATE:20300306
        DTEND;VALUE=DATE:20300307
        SUMMARY:Restmüll
        END:VEVENT
        END:VCALENDAR
        """;

    private static List<string> Fired(UserSpace space, long cal, DateTime from, DateTime to) =>
        CalendarReminders.DueIn(space, cal, space.Zone, from, to, false)
            .Select(d => d.Title + "@" + d.FireUtc.ToString("MM-dd HH:mm")).OrderBy(x => x).ToList();

    [Fact]
    public void Abo_Standard_Titelregel_und_eigene_Liste()
    {
        With(true, () =>
        {
            var space = NewSpace();
            var cal = Subscribed(space, Abfuhr.Replace("\n", "\r\n"));
            var from = U("2030-03-01T00:00:00");
            var to = U("2030-03-08T00:00:00");
            // Ohne Standard: der Alarm des Anbieters (15 min vor 0:00 MEZ = 22:45Z am Vortag)
            Assert.Equal(new[] { "Papiertonne@03-04 22:45" }, Fired(space, cal, from, to));

            // Standard „am Vortag 18:00“ ersetzt den Anbieter; Titelregel „Papier“ zusätzlich 2 Tage vorher 18:00
            CalendarReminders.SetDefault(space.Db, cal, new CalendarRemindDefault
            {
                Reminders = [new() { Minutes = 360, At = true }],
                Rules = [new() { Contains = "papier", Reminders = [new() { Minutes = 360, At = true }, new() { Minutes = 1800, At = true }] }],
            });
            Assert.Equal(new[] { "Papiertonne@03-03 17:00", "Papiertonne@03-04 17:00", "Restmüll@03-05 17:00" }, Fired(space, cal, from, to));

            // Eigene Erinnerung am Abo-Termin hat Vorrang; leere Liste = keine
            var rest = PimStore.ByUid(space.Db, cal, "rest-1")!.Uri;
            CalendarReminders.SetOverlay(space.Db, cal, rest, [new() { Minutes = -420, At = true }]);
            Assert.Contains("Restmüll@03-06 06:00", Fired(space, cal, from, to));
            CalendarReminders.SetOverlay(space.Db, cal, rest, []);
            Assert.DoesNotContain(Fired(space, cal, from, to), x => x.StartsWith("Restmüll"));
            CalendarReminders.SetOverlay(space.Db, cal, rest, null);
            Assert.Contains("Restmüll@03-05 17:00", Fired(space, cal, from, to));

            // Kalender-Standard aus: wieder wie vorher (Anbieter)
            var before = Features.InReleaseOff.ToList();
            Features.Set(before.Append(Features.CalRemDefault));
            try { Assert.Equal(new[] { "Papiertonne@03-04 22:45" }, Fired(space, cal, from, to)); }
            finally { Features.Set(before); }
            return 0;
        });
    }

    [Fact]
    public void Standard_am_Tag_um_entfaellt_bei_Termin_mit_Uhrzeit()
    {
        With(true, () =>
        {
            var space = NewSpace();
            var ics = Abfuhr.Replace("""
                BEGIN:VEVENT
                UID:rest-1
                """, """
                BEGIN:VEVENT
                UID:vortrag-1
                DTSTART:20300304T180000Z
                DTEND:20300304T190000Z
                SUMMARY:Vortrag
                END:VEVENT
                BEGIN:VEVENT
                UID:rest-1
                """);
            var cal = Subscribed(space, ics.Replace("\n", "\r\n"));
            // „Am Tag um 8:00“ (-480) und „Am Vortag um 18:00“ (360): ganztägig beide, mit Uhrzeit nur 6 h vor Beginn
            CalendarReminders.SetDefault(space.Db, cal, new CalendarRemindDefault
            {
                Reminders = [new() { Minutes = -480, At = true }, new() { Minutes = 360, At = true }],
            });
            var fired = Fired(space, cal, U("2030-03-01T00:00:00"), U("2030-03-08T00:00:00"));
            Assert.Equal(new[] { "Papiertonne@03-04 17:00", "Papiertonne@03-05 07:00", "Restmüll@03-05 17:00", "Restmüll@03-06 07:00", "Vortrag@03-04 12:00" }, fired);
            return 0;
        });
    }

    [Fact]
    public void Abgleich_laesst_eigene_Liste_stehen()
    {
        With(true, () =>
        {
            var space = NewSpace();
            var cal = Subscribed(space, Abfuhr.Replace("\n", "\r\n"));
            var uri = PimStore.ByUid(space.Db, cal, "papier-1")!.Uri;
            CalendarReminders.SetOverlay(space.Db, cal, uri, [new() { Minutes = 360, At = true }]);
            // Anbieter ändert den Termin (anderer Text): Subscriptions schreibt dasselbe Objekt (gleiche Uri) neu
            var coll = PimStore.Find(space.Db, cal)!;
            PimStore.Put(space.Db, coll, uri, PimStore.Object(space.Db, cal, uri)!.Data.Replace("Papiertonne", "Papiertonne (blau)"), zone: space.Zone);
            Assert.Equal(new[] { "Papiertonne (blau)@03-04 17:00" }, Fired(space, cal, U("2030-03-01T00:00:00"), U("2030-03-08T00:00:00")));
            return 0;
        });
    }

    [Fact]
    public void Aufgabe_einmal_anlegen_und_bei_Wegfall_loeschen()
    {
        With(true, () =>
        {
            var space = NewSpace();
            var cal = Calendar(space);
            var list = PimStore.Collections(space.Db, PimStore.Calendar).First(c => c.Components.Contains("VTODO")).Id;
            var id = AllDay(space, cal, "Gelber Sack", "2030-03-05");
            var uri = id[(id.IndexOf('/') + 1)..];
            CalendarReminders.SetOverlay(space.Db, cal, uri, [new() { Minutes = 1440 * 2, List = list }]);

            // 02.03.: noch nicht fällig (Aufgabe ab 03.03.)
            Assert.Equal(0, CalendarRemindTasks.Run(space, U("2030-03-02T10:00:00")));
            Assert.Equal(1, CalendarRemindTasks.Run(space, U("2030-03-03T10:00:00")));
            Assert.Equal(0, CalendarRemindTasks.Run(space, U("2030-03-03T10:05:00")));   // nicht doppelt
            var task = CalendarModel.Tasks(space, [list]).Single(t => t.Title == "Gelber Sack");
            Assert.Equal("2030-03-03", task.Due);

            // Termin verschoben: offene Aufgabe weg, neue am neuen Tag
            CalendarModel.SaveEvent(space, new EventInput { Calendar = cal, Uri = uri, Title = "Gelber Sack", Start = "2030-03-12", End = "2030-03-13", AllDay = true });
            CalendarRemindTasks.Run(space, U("2030-03-03T10:10:00"));
            Assert.DoesNotContain(CalendarModel.Tasks(space, [list]), t => t.Title == "Gelber Sack");

            // Vergangene Termine bekommen keine Aufgabe
            var old = AllDay(space, cal, "Altpapier", "2030-03-01");
            CalendarReminders.SetOverlay(space.Db, cal, old[(old.IndexOf('/') + 1)..], [new() { Minutes = 1440, List = list }]);
            CalendarRemindTasks.Run(space, U("2030-03-03T10:15:00"));
            Assert.DoesNotContain(CalendarModel.Tasks(space, [list]), t => t.Title == "Altpapier");

            // Erledigte bleibt, auch wenn der Termin wegfällt
            CalendarRemindTasks.Run(space, U("2030-03-10T10:00:00"));
            var t2 = CalendarModel.Tasks(space, [list]).Single(t => t.Title == "Gelber Sack");
            CalendarModel.SaveTask(space, PimApi.Input(t2, true));
            CalendarModel.DeleteEvent(space, cal, uri, null);
            CalendarRemindTasks.Run(space, U("2030-03-10T10:05:00"));
            Assert.Contains(CalendarModel.Tasks(space, [list]), t => t.Title == "Gelber Sack" && t.Completed);
            return 0;
        });
    }

    [Fact]
    public async Task Api_Mehrfachauswahl_Standard_und_faellige()
    {
        await WithAsync(true, async () =>
        {
            var anna = _fx.As("anna");
            var r = await anna.PostAsJsonAsync("/api/pim/collections/calendars", new { name = "Erinnerung Test", components = "VEVENT" });
            var cal = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
            var day = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd");
            var next = DateTime.UtcNow.AddDays(4).ToString("yyyy-MM-dd");
            var ids = new List<string>();
            foreach (var t in new[] { "Bio", "Papier" })
            {
                var s = await anna.PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = t, start = day, end = next, allDay = true });
                ids.Add((await s.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
            }
            var items = ids.Select(i => new { calendar = cal, uri = i[(i.IndexOf('/') + 1)..] }).ToArray();
            var b = await anna.PostAsJsonAsync("/api/pim/events/remind", new { items, reminders = new[] { new { minutes = 360, at = true }, new { minutes = 1800, at = true } } });
            Assert.Equal(2, (await b.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetInt32());

            var ev = await (await anna.GetAsync($"/api/pim/events?from={day}T00:00:00Z&to={next}T00:00:00Z&calendars={cal}")).Content.ReadFromJsonAsync<JsonElement>();
            Assert.All(ev.EnumerateArray(), e => Assert.Equal(new[] { 360, 1800 }, e.GetProperty("reminders").EnumerateArray().Select(x => x.GetProperty("minutes").GetInt32()).Order()));

            // Standard setzen und „Kalender-Standard“ für einen Termin
            (await anna.PutAsJsonAsync($"/api/pim/collections/{cal}/remind", new { reminders = new[] { new { minutes = 60 } } })).EnsureSuccessStatusCode();
            await anna.PostAsJsonAsync("/api/pim/events/remind", new { items = items.Take(1), reminders = (object?)null, @default = true });
            ev = await (await anna.GetAsync($"/api/pim/events?from={day}T00:00:00Z&to={next}T00:00:00Z&calendars={cal}")).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Null, ev.EnumerateArray().First(e => e.GetProperty("title").GetString() == "Bio").GetProperty("reminders").ValueKind);

            // Fällige: Bio am Tag um 8:00 Ortszeit (Standard 1 Stunde vor 9:00), Papier am Vortag 18:00
            var dayStart = Ics.ToUtc(DateOnly.Parse(day).ToDateTime(TimeOnly.MinValue), Berlin);
            var due = await (await anna.GetAsync($"/api/pim/reminders?from={dayStart.AddHours(-7):yyyy-MM-ddTHH:mm:ssZ}&to={dayStart.AddHours(9):yyyy-MM-ddTHH:mm:ssZ}")).Content.ReadFromJsonAsync<JsonElement>();
            var mine = due.EnumerateArray().Where(d => d.GetProperty("calendar").GetInt64() == cal).ToList();
            Assert.Equal(new[] { "Bio60", "Papier360at" }, mine.Select(d => d.GetProperty("title").GetString() + d.GetProperty("minutes").GetInt32() + (d.GetProperty("at").GetBoolean() ? "at" : "")).Order());
            var bio = mine.First(d => d.GetProperty("title").GetString() == "Bio");
            Assert.Equal(dayStart.AddHours(8).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), bio.GetProperty("fire").GetString());
            Assert.False(string.IsNullOrEmpty(bio.GetProperty("collection").GetString()));
        });
        // Ausgeschaltet: Endpunkte gibt es nicht
        await WithAsync(false, async () =>
            Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("anna").GetAsync("/api/pim/reminders?from=2030-01-01T00:00:00Z&to=2030-01-02T00:00:00Z")).StatusCode));
    }

    // ---------------- Geteilte Kalender ----------------

    private async Task ShareWith(long calendar, string[] members, string[] readOnly)
    {
        var body = new { members = members.Select(m => _fx.Users[m].Id).ToArray(), readOnly = readOnly.Select(m => _fx.Users[m].Id).ToArray() };
        (await _fx.As("anna").PutAsJsonAsync($"/api/pim/shares/{calendar}", body)).EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> SharedCal(string login, string name) =>
        (await (await _fx.As(login).GetAsync("/api/pim/collections/calendars")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == name && c.GetProperty("owner").ValueKind == JsonValueKind.String);

    private async Task<List<string>> DueFor(string login, long calendar, string from, string to) =>
        (await (await _fx.As(login).GetAsync($"/api/pim/reminders?from={from}&to={to}")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Where(d => d.GetProperty("calendar").GetInt64() == calendar)
            .Select(d => d.GetProperty("title").GetString() + "@" + d.GetProperty("fire").GetString()).Order().ToList();

    [Fact]
    public async Task Geteilt_Standard_und_Titelregel_des_Besitzers_gelten_fuer_alle()
    {
        await WithAsync(true, async () =>
        {
            var anna = _fx.As("anna");
            var name = "Familie Erinnerung " + Guid.NewGuid().ToString("N")[..6];
            var cal = (await (await anna.PostAsJsonAsync("/api/pim/collections/calendars", new { name, components = "VEVENT" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
            var annaSpace = _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id);
            var list = PimStore.CreateCollection(annaSpace.Db, PimStore.Calendar, "todo" + Guid.NewGuid().ToString("N")[..6], "Aufgaben", null, "VTODO").Id;
            // Standard: 1 Stunde vorher; Titelregel „Müll“: am Vortag 18:00 und eine Aufgabe 2 Tage vorher
            (await anna.PutAsJsonAsync($"/api/pim/collections/{cal}/remind", new
            {
                reminders = new[] { new { minutes = 60 } },
                rules = new[] { new { contains = "Müll", reminders = new object[] { new { minutes = 360, at = true }, new { minutes = 2880, list = list } } } },
            })).EnsureSuccessStatusCode();
            await anna.PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Restmüll", start = "2030-06-12", end = "2030-06-13", allDay = true });
            await anna.PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Elternabend", start = "2030-06-12T17:00:00Z", end = "2030-06-12T18:00:00Z", allDay = false });
            await ShareWith(cal, ["ben", "cleo"], ["ben"]);

            // Ben (nur lesen) und Cleo (schreiben) sehen den Standard des Besitzers
            var benCal = await SharedCal("ben", name);
            var cleoCal = await SharedCal("cleo", name);
            Assert.Equal(60, benCal.GetProperty("remind").GetProperty("reminders")[0].GetProperty("minutes").GetInt32());
            Assert.Equal("Müll", cleoCal.GetProperty("remind").GetProperty("rules")[0].GetProperty("contains").GetString());
            var benId = benCal.GetProperty("id").GetInt64();
            var cleoId = cleoCal.GetProperty("id").GetInt64();

            // Gleiche Auslösezeiten bei allen; die Aufgaben-Regel benachrichtigt niemanden
            var expected = new[] { "Elternabend@2030-06-12T16:00:00Z", "Restmüll@2030-06-11T16:00:00Z" };
            const string from = "2030-06-08T00:00:00Z", to = "2030-06-13T00:00:00Z";
            Assert.Equal(expected, await DueFor("anna", cal, from, to));
            Assert.Equal(expected, await DueFor("ben", benId, from, to));
            Assert.Equal(expected, await DueFor("cleo", cleoId, from, to));

            // Den Standard ändert nur der Besitzer
            Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PutAsJsonAsync($"/api/pim/collections/{cleoId}/remind", new { reminders = new[] { new { minutes = 5 } } })).StatusCode);

            // Aufgabe nur einmal, in Annas Liste; Ben und Cleo legen keine an
            var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
            Assert.Equal(1, CalendarRemindTasks.Run(annaSpace, U("2030-06-10T08:00:00")));
            Assert.Equal(0, CalendarRemindTasks.Run(spaces.Get(_fx.Users["ben"].Id), U("2030-06-10T08:00:00")));
            Assert.Equal(0, CalendarRemindTasks.Run(spaces.Get(_fx.Users["cleo"].Id), U("2030-06-10T08:00:00")));
            Assert.Single(CalendarModel.Tasks(annaSpace, [list]), t => t.Title == "Restmüll");

            // Cleo (schreiben) setzt „keine Erinnerung“ am Elternabend: gilt für alle, Standard bleibt sonst
            var cleoEvents = await (await _fx.As("cleo").GetAsync($"/api/pim/events?from={from}&to={to}&calendars={cleoId}")).Content.ReadFromJsonAsync<JsonElement>();
            var abend = cleoEvents.EnumerateArray().First(e => e.GetProperty("title").GetString() == "Elternabend");
            Assert.Equal(JsonValueKind.Null, abend.GetProperty("reminders").ValueKind);   // Standard gilt
            var save = await _fx.As("cleo").PostAsJsonAsync("/api/pim/events", new
            {
                calendar = cleoId, uri = abend.GetProperty("uri").GetString(), title = "Elternabend", start = "2030-06-12T17:00:00Z", end = "2030-06-12T18:00:00Z",
                allDay = false, reminders = Array.Empty<object>(),
            });
            save.EnsureSuccessStatusCode();
            Assert.Equal(new[] { "Restmüll@2030-06-11T16:00:00Z" }, await DueFor("anna", cal, from, to));
            Assert.Equal(new[] { "Restmüll@2030-06-11T16:00:00Z" }, await DueFor("ben", benId, from, to));
        });
    }

    [Fact]
    public async Task Geteiltes_Abo_mit_eigener_Liste_des_Besitzers()
    {
        await WithSharedAsync(true, async () =>
        {
            var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
            var annaSpace = spaces.Get(_fx.Users["anna"].Id);
            var cal = Subscribed(annaSpace, Abfuhr.Replace("\n", "\r\n"));
            var name = PimStore.Find(annaSpace.Db, cal)!.Name;
            await ShareWith(cal, ["dora"], ["dora"]);
            var doraId = (await SharedCal("dora", name)).GetProperty("id").GetInt64();
            const string from = "2030-03-01T00:00:00Z", to = "2030-03-08T00:00:00Z";
            // Ohne Standard: Alarm des Anbieters bei beiden
            Assert.Equal(new[] { "Papiertonne@2030-03-04T22:45:00Z" }, await DueFor("dora", doraId, from, to));

            // Annas eigene Liste am Abo-Termin gilt auch für Dora; Dora kann sie nicht ändern
            var rest = PimStore.ByUid(annaSpace.Db, cal, "rest-1")!.Uri;
            var r = await _fx.As("anna").PostAsJsonAsync("/api/pim/events/remind", new { items = new[] { new { calendar = cal, uri = rest } }, reminders = new[] { new { minutes = 120 } } });
            Assert.Equal(1, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetInt32());
            var expected = new[] { "Papiertonne@2030-03-04T22:45:00Z", "Restmüll@2030-03-06T06:00:00Z" };
            Assert.Equal(expected, await DueFor("anna", cal, from, to));
            Assert.Equal(expected, await DueFor("dora", doraId, from, to));
            // Doras eigene (leere) Liste gilt nur für sie (calremshared), Annas bleibt
            var d = await _fx.As("dora").PostAsJsonAsync("/api/pim/events/remind", new { items = new[] { new { calendar = doraId, uri = rest } }, reminders = Array.Empty<object>() });
            Assert.Equal(1, (await d.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetInt32());
            Assert.Equal(new[] { "Papiertonne@2030-03-04T22:45:00Z" }, await DueFor("dora", doraId, from, to));
            Assert.Equal(expected, await DueFor("anna", cal, from, to));
        });
    }

    // ---------------- Persönlich in geteilten Kalendern, Übernahme aus Android (calremshared) ----------------

    private static async Task WithSharedAsync(bool shared, Func<Task> run)
    {
        var before = Features.InReleaseOff.ToList();
        var on = Mine.Append(Features.CalRemShared).ToArray();
        Features.Set(shared ? before.Where(x => !on.Contains(x)) : before.Where(x => !Mine.Contains(x)).Append(Features.CalRemShared).Distinct());
        try { await run(); } finally { Features.Set(before); }
    }

    private static Task<HttpResponseMessage> Remind(HttpClient c, long calendar, string uri, object? reminders, bool add = false, bool @default = false) =>
        c.PostAsJsonAsync("/api/pim/events/remind", new { items = new[] { new { calendar, uri } }, reminders, add, @default });

    private static async Task<int> Changed(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetInt32();

    [Fact]
    public async Task Geteilt_nur_lesbar_eigene_Erinnerung_gilt_nur_fuer_mich()
    {
        await WithSharedAsync(true, async () =>
        {
            var anna = _fx.As("anna");
            var name = "Praxis " + Guid.NewGuid().ToString("N")[..6];
            var cal = (await (await anna.PostAsJsonAsync("/api/pim/collections/calendars", new { name, components = "VEVENT" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
            (await anna.PutAsJsonAsync($"/api/pim/collections/{cal}/remind", new { reminders = new[] { new { minutes = 60 } } })).EnsureSuccessStatusCode();
            var id = (await (await anna.PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Arzt", start = "2030-07-10T10:00:00Z", end = "2030-07-10T11:00:00Z", allDay = false }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            var uri = id[(id.IndexOf('/') + 1)..];
            await ShareWith(cal, ["ben", "cleo"], ["ben", "cleo"]);
            var benId = (await SharedCal("ben", name)).GetProperty("id").GetInt64();
            var cleoId = (await SharedCal("cleo", name)).GetProperty("id").GetInt64();
            const string from = "2030-07-09T00:00:00Z", to = "2030-07-11T00:00:00Z";

            // Ben setzt 15 Minuten: gilt statt des Standards nur für ihn, der Termin bleibt unverändert
            Assert.Equal(1, await Changed(await Remind(_fx.As("ben"), benId, uri, new[] { new { minutes = 15 } })));
            Assert.Equal(new[] { "Arzt@2030-07-10T09:45:00Z" }, await DueFor("ben", benId, from, to));
            Assert.Equal(new[] { "Arzt@2030-07-10T09:00:00Z" }, await DueFor("cleo", cleoId, from, to));
            Assert.Equal(new[] { "Arzt@2030-07-10T09:00:00Z" }, await DueFor("anna", cal, from, to));
            var benEv = (await (await _fx.As("ben").GetAsync($"/api/pim/events?from={from}&to={to}&calendars={benId}")).Content.ReadFromJsonAsync<JsonElement>())[0];
            Assert.Equal(15, benEv.GetProperty("myReminders")[0].GetProperty("minutes").GetInt32());
            Assert.Equal(JsonValueKind.Null, benEv.GetProperty("reminders").ValueKind);   // Standard des Besitzers
            var cleoEv = (await (await _fx.As("cleo").GetAsync($"/api/pim/events?from={from}&to={to}&calendars={cleoId}")).Content.ReadFromJsonAsync<JsonElement>())[0];
            Assert.Equal(JsonValueKind.Null, cleoEv.GetProperty("myReminders").ValueKind);

            // Hinzufügen behält, was gilt: bei Ben die eigene, bei Cleo den Standard des Besitzers
            Assert.Equal(1, await Changed(await Remind(_fx.As("ben"), benId, uri, new[] { new { minutes = 30 } }, add: true)));
            Assert.Equal(new[] { "Arzt@2030-07-10T09:30:00Z", "Arzt@2030-07-10T09:45:00Z" }, await DueFor("ben", benId, from, to));
            Assert.Equal(1, await Changed(await Remind(_fx.As("cleo"), cleoId, uri, new[] { new { minutes = 30 } }, add: true)));
            Assert.Equal(new[] { "Arzt@2030-07-10T09:00:00Z", "Arzt@2030-07-10T09:30:00Z" }, await DueFor("cleo", cleoId, from, to));

            // Ausdrücklich keine (leere Liste) nur für Ben; „Standard“ nimmt die eigene wieder weg
            await Remind(_fx.As("ben"), benId, uri, Array.Empty<object>());
            Assert.Empty(await DueFor("ben", benId, from, to));
            Assert.Equal(new[] { "Arzt@2030-07-10T09:00:00Z" }, await DueFor("anna", cal, from, to));
            await Remind(_fx.As("ben"), benId, uri, null, @default: true);
            Assert.Equal(new[] { "Arzt@2030-07-10T09:00:00Z" }, await DueFor("ben", benId, from, to));

            // Besitzer-Datenbank unverändert: kein VALARM im Termin
            var annaSpace = _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id);
            Assert.DoesNotContain("VALARM", PimStore.Object(annaSpace.Db, cal, uri)!.Data);
        });
        // Ohne calremshared: wie bisher nicht möglich
        await WithSharedAsync(false, async () =>
        {
            var anna = _fx.As("anna");
            var name = "Praxis aus " + Guid.NewGuid().ToString("N")[..6];
            var cal = (await (await anna.PostAsJsonAsync("/api/pim/collections/calendars", new { name, components = "VEVENT" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
            var id = (await (await anna.PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Arzt", start = "2030-07-10T10:00:00Z", end = "2030-07-10T11:00:00Z", allDay = false }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            await ShareWith(cal, ["ben"], ["ben"]);
            var benId = (await SharedCal("ben", name)).GetProperty("id").GetInt64();
            var r = await Remind(_fx.As("ben"), benId, id[(id.IndexOf('/') + 1)..], new[] { new { minutes = 15 } });
            Assert.Equal(1, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("skipped").GetInt32());
        });
    }

    [Fact]
    public async Task Uebernahme_fuegt_hinzu_eigener_Kalender_und_Abo()
    {
        await WithSharedAsync(true, async () =>
        {
            var anna = _fx.As("anna");
            var cal = (await (await anna.PostAsJsonAsync("/api/pim/collections/calendars", new { name = "Übernahme " + Guid.NewGuid().ToString("N")[..6], components = "VEVENT" }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
            (await anna.PutAsJsonAsync($"/api/pim/collections/{cal}/remind", new { reminders = new[] { new { minutes = 60 } } })).EnsureSuccessStatusCode();
            var id = (await (await anna.PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Termin", start = "2030-08-01T10:00:00Z", end = "2030-08-01T11:00:00Z", allDay = false }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            var uri = id[(id.IndexOf('/') + 1)..];
            const string from = "2030-07-31T00:00:00Z", to = "2030-08-02T00:00:00Z";
            Assert.Equal(new[] { "Termin@2030-08-01T09:00:00Z" }, await DueFor("anna", cal, from, to));

            // Eigener Kalender: der Standard (60) bleibt als eigene Erinnerung im Termin, 10 kommt dazu
            Assert.Equal(1, await Changed(await Remind(anna, cal, uri, new[] { new { minutes = 10 } }, add: true)));
            Assert.Equal(new[] { "Termin@2030-08-01T09:00:00Z", "Termin@2030-08-01T09:50:00Z" }, await DueFor("anna", cal, from, to));
            var annaSpace = _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id);
            var data = PimStore.Object(annaSpace.Db, cal, uri)!.Data;
            Assert.Contains("TRIGGER:-PT1H", data);
            Assert.Contains("TRIGGER:-PT10M", data);
            // Dieselbe noch einmal: keine doppelte
            await Remind(anna, cal, uri, new[] { new { minutes = 10 } }, add: true);
            Assert.Equal(2, PimStore.Object(annaSpace.Db, cal, uri)!.Data.Split("BEGIN:VALARM").Length - 1);

            // Abo: Alarm des Anbieters bleibt, 120 Minuten kommen dazu (eigene Liste ersetzt den Anbieter, enthält ihn also)
            var abo = Subscribed(annaSpace, Abfuhr.Replace("\n", "\r\n"));
            var papier = PimStore.ByUid(annaSpace.Db, abo, "papier-1")!.Uri;
            const string afrom = "2030-03-01T00:00:00Z", ato = "2030-03-08T00:00:00Z";
            var before = await DueFor("anna", abo, afrom, ato);
            Assert.Contains("Papiertonne@2030-03-04T22:45:00Z", before);
            Assert.Equal(1, await Changed(await Remind(anna, abo, papier, new[] { new { minutes = 120, at = true } }, add: true)));
            var after = await DueFor("anna", abo, afrom, ato);
            Assert.Contains("Papiertonne@2030-03-04T22:45:00Z", after);
            Assert.Contains("Papiertonne@2030-03-04T21:00:00Z", after);

            // Hinzufügen ohne Erinnerung: abgelehnt
            Assert.Equal(HttpStatusCode.BadRequest, (await Remind(anna, cal, uri, Array.Empty<object>(), add: true)).StatusCode);
        });
    }
}
