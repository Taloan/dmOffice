using System.Globalization;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Coach;

/// <summary>Was der Benutzer je Vorsorge/Impfung angegeben hat (kv „coach.checks“).</summary>
public sealed class CheckState
{
    /// <summary>Zuletzt erledigt (yyyy-MM-dd).</summary>
    public string? Done { get; set; }
    /// <summary>„Nicht für mich“.</summary>
    public bool Skip { get; set; }
    /// <summary>Pukki fragt frühestens wieder ab (yyyy-MM-dd).</summary>
    public string? AskAfter { get; set; }
}

public sealed class CheckSettings
{
    public Dictionary<string, CheckState> Items { get; set; } = [];
    /// <summary>Pukki erinnert an Fälliges (höchstens einmal pro Woche).</summary>
    public bool Remind { get; set; } = true;
    public string? LastAsked { get; set; }
}

/// <summary>
/// Vorsorge und Impfungen nach Alter und Geschlecht (Coach): Standardimpfungen für Erwachsene nach dem STIKO-Impfkalender 2026
/// (Epidemiologisches Bulletin 4/2026) und die Früherkennungsuntersuchungen der gesetzlichen Krankenversicherung (G-BA). Der
/// Benutzer trägt ein, wann er es zuletzt gemacht hat; daraus ergibt sich „fällig“. Pukki fragt bei Fälligem, ob er eine
/// Erinnerung in den Kalender eintragen soll. Nur Information – die Entscheidung gehört in die Arztpraxis.
/// </summary>
public static class CoachChecks
{
    /// <summary>Months: 0 = einmalig. Born1970: nur nach 1970 Geborene. Season: jährlich im Herbst (Monate 9–12 fällig).</summary>
    public sealed record Check(string Key, string Kind, string Title, string? Sex, int MinAge, int? MaxAge, int Months, string Text, string Source, string Url,
        bool Born1970 = false, bool Season = false);

    private const string Stiko = "STIKO-Impfkalender 2026 (RKI)";
    private const string StikoUrl = "https://www.rki.de/DE/Themen/Infektionskrankheiten/Impfen/Impfkalender/impfkalender-node.html";
    private const string Gba = "G-BA, Früherkennungsuntersuchungen";
    private const string GbaUrl = "https://www.g-ba.de/themen/methodenbewertung/frueherkennungsuntersuchungen/";

    public static readonly Check[] All =
    [
        new("td", "vaccine", "Tetanus und Diphtherie auffrischen", null, 18, null, 120, "Alle 10 Jahre. Die nächste fällige Auffrischung einmalig zusammen mit Keuchhusten (Tdap).", Stiko, StikoUrl),
        new("mmr", "vaccine", "Masern (MMR)", null, 18, null, 0, "Einmal, wenn du nach 1970 geboren bist und der Impfstatus unklar ist, du nicht oder nur einmal als Kind geimpft wurdest.", Stiko, StikoUrl, Born1970: true),
        new("flu", "vaccine", "Grippe (Influenza)", null, 60, null, 12, "Jedes Jahr im Herbst/Winter.", Stiko, StikoUrl, Season: true),
        new("covid", "vaccine", "COVID-19", null, 60, null, 12, "Jedes Jahr im Herbst.", Stiko, StikoUrl, Season: true),
        new("pneumo", "vaccine", "Pneumokokken", null, 60, null, 0, "Einmal mit PCV20.", Stiko, StikoUrl),
        new("zoster", "vaccine", "Gürtelrose (Herpes zoster)", null, 60, null, 0, "Zwei Impfungen mit dem Totimpfstoff im Abstand von 2 bis 6 Monaten. In natürlichen Experimenten (u. a. Wales) gab es danach rund 20 % weniger Demenzfälle (Eyting et al., Nature 2025).", Stiko, StikoUrl),
        new("rsv", "vaccine", "RSV", null, 75, null, 0, "Einmal im Spätsommer/Herbst vor Beginn der RSV-Saison.", Stiko, StikoUrl),

        new("checkup18", "screening", "Gesundheits-Check", null, 18, 34, 0, "Einmal zwischen 18 und 35 – Blutdruck, Blutwerte, Impfstatus.", Gba, GbaUrl),
        new("checkup35", "screening", "Gesundheits-Check", null, 35, null, 36, "Ab 35 alle 3 Jahre – Blutdruck, Blutwerte, Impfstatus.", Gba, GbaUrl),
        new("hepbc", "screening", "Hepatitis B und C (Test)", null, 35, null, 0, "Einmal ab 35, meist beim Gesundheits-Check.", Gba, GbaUrl),
        new("skin", "screening", "Hautkrebs-Screening", null, 35, null, 24, "Ab 35 alle 2 Jahre.", Gba, GbaUrl),
        new("cervix20", "screening", "Gebärmutterhalskrebs (Abstrich)", "f", 20, 34, 12, "Von 20 bis 34 jährlich.", Gba, GbaUrl),
        new("cervix35", "screening", "Gebärmutterhalskrebs (Abstrich und HPV-Test)", "f", 35, null, 36, "Ab 35 alle 3 Jahre.", Gba, GbaUrl),
        new("breast30", "screening", "Brust abtasten (Krebsfrüherkennung)", "f", 30, null, 12, "Ab 30 jährlich in der Frauenarztpraxis.", Gba, GbaUrl),
        new("mammo", "screening", "Mammographie", "f", 50, 75, 24, "Von 50 bis 75 alle 2 Jahre, mit Einladung.", Gba, GbaUrl),
        new("colon", "screening", "Darmkrebs-Früherkennung", null, 50, null, 24, "Ab 50: Stuhltest alle 2 Jahre oder bis zu zwei Darmspiegelungen im Abstand von 10 Jahren.", Gba, GbaUrl),
        new("prostate", "screening", "Prostata (Krebsfrüherkennung)", "m", 45, null, 12, "Ab 45 jährlich.", Gba, GbaUrl),
        new("aaa", "screening", "Bauchaorta (Ultraschall)", "m", 65, null, 0, "Einmal ab 65.", Gba, GbaUrl),
        new("lung", "screening", "Lungenkrebs-Früherkennung (Niedrigdosis-CT)", null, 50, 75, 12, "Nur für langjährige starke Raucherinnen und Raucher (auch frühere), von 50 bis 75 jährlich.", Gba, GbaUrl),
    ];

    private const string Key = "coach.checks";
    public static CheckSettings Settings(UserDb db) => db.GetJson<CheckSettings>(Key) ?? new CheckSettings();
    public static void Save(UserDb db, CheckSettings s) => db.SetJson(Key, s);

    public sealed record Status(Check Check, string State, string? Done, string? Next);

    /// <summary>
    /// Was für Alter und Geschlecht gilt, mit Stand: due (fällig), ok (erledigt, nächste ab Next), skip (nicht für mich),
    /// done (einmalig erledigt). Ohne Geburtsdatum: leer. Divers/ohne Geschlecht: nur die Untersuchungen für alle.
    /// </summary>
    public static List<Status> For(CoachProfile p, CheckSettings s, DateOnly today)
    {
        if (p.Birth is not DateOnly birth) return [];
        var age = CoachTargetsCalc.AgeOf(birth, today) ?? 0;
        var list = new List<Status>();
        foreach (var c in All)
        {
            if (age < c.MinAge || (c.MaxAge is int max && age > max)) continue;
            if (c.Sex != null && c.Sex != p.Sex) continue;
            if (c.Born1970 && birth.Year <= 1970) continue;
            var st = s.Items.GetValueOrDefault(c.Key);
            if (st?.Skip == true) { list.Add(new(c, "skip", st.Done, null)); continue; }
            var done = st?.Done != null && DateOnly.TryParseExact(st.Done, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
            if (done == null) { list.Add(new(c, "due", null, null)); continue; }
            if (c.Months == 0) { list.Add(new(c, "done", st!.Done, null)); continue; }
            var next = done.Value.AddMonths(c.Months);
            // Herbstimpfungen: die nächste Saison beginnt im September
            if (c.Season) next = new DateOnly(done.Value.Month >= 9 ? done.Value.Year + 1 : done.Value.Year, 9, 1);
            list.Add(new(c, next <= today ? "due" : "ok", st!.Done, next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return list;
    }

    /// <summary>Erinnerung „Termin vereinbaren“ am nächsten Werktag um 9 Uhr im eigenen Kalender; gibt den Tag zurück.</summary>
    public static DateOnly AddToCalendar(UserSpace space, Check c)
    {
        var db = space.Db;
        PimStore.EnsureDefaults(db);
        var cal = PimStore.Collections(db, PimStore.Calendar)
            .Where(x => x.Components.Contains("VEVENT") && CalendarKinds.Of(db, x.Id) == CalendarKinds.Normal && !Subscriptions.IsSubscribed(db, x.Id) && !Graph.IsLinked(db, x.Id))
            .OrderBy(x => x.Uri == "personal" ? 0 : 1).FirstOrDefault() ?? throw new InvalidOperationException("Es gibt keinen eigenen Kalender.");
        var day = CoachStore.Today(space).AddDays(1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        var at = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(9, 0)), space.Zone);
        CalendarModel.SaveEvent(space, new EventInput
        {
            Calendar = cal.Id, Title = "Termin vereinbaren: " + c.Title, Description = c.Text + "\n\nQuelle: " + c.Source + " – " + c.Url,
            Start = at.ToString("o", CultureInfo.InvariantCulture), End = at.AddMinutes(15).ToString("o", CultureInfo.InvariantCulture), Busy = false, Reminder = 0,
        });
        return day;
    }

    public static void Mark(UserDb db, string key, Action<CheckState> change)
    {
        if (All.All(c => c.Key != key)) throw new KeyNotFoundException("Unbekannte Vorsorge.");
        var s = Settings(db);
        if (!s.Items.TryGetValue(key, out var st)) s.Items[key] = st = new CheckState();
        change(st);
        Save(db, s);
    }

    // ---------------- Pukki ----------------

    public const string Prefix = "check-";

    /// <summary>
    /// Einmal pro Woche (vormittags) das erste Fällige, nach dem Pukki seit drei Monaten nicht gefragt hat: „… empfiehlt ab deinem
    /// Alter …“ mit Knöpfen Kalender, Schon erledigt, Später, Nicht für mich. Nur mit Geburtsdatum und eingeschalteter Erinnerung.
    /// </summary>
    public static void Ask(UserSpace space, ChatStore chat, DateTime nowUtc)
    {
        var db = space.Db;
        var s = Settings(db);
        if (!s.Remind) return;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, space.Zone);
        var today = DateOnly.FromDateTime(local);
        if (local.Hour is < 10 or >= 12) return;
        if (s.LastAsked != null && DateOnly.TryParseExact(s.LastAsked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last) && today < last.AddDays(7)) return;
        if (Pukki.RoomOf(chat, space.User.Id) is not { } room) return;
        var p = CoachStore.Profile(db);
        var due = For(p, s, today).FirstOrDefault(x => x.State == "due"
            && (s.Items.GetValueOrDefault(x.Check.Key)?.AskAfter is not { } after || string.CompareOrdinal(after, today.ToString("yyyy-MM-dd")) <= 0));
        if (due == null) return;
        var c = due.Check;
        s.LastAsked = today.ToString("yyyy-MM-dd");
        if (!s.Items.TryGetValue(c.Key, out var st)) s.Items[c.Key] = st = new CheckState();
        st.AskAfter = today.AddMonths(3).ToString("yyyy-MM-dd");
        Save(db, s);
        var who = c.Kind == "vaccine" ? "Die STIKO" : "Die gesetzliche Krankenversicherung";
        var what = c.Kind == "vaccine" ? "die Impfung" : "die Untersuchung";
        var text = $"{who} empfiehlt ab {c.MinAge} Jahren {what} „{c.Title}“. {c.Text}" + (due.Done != null ? $" Zuletzt eingetragen: {Fmt(due.Done)}." : "")
            + "\n\nSoll ich dir eine Erinnerung in den Kalender eintragen, einen Termin zu vereinbaren?";
        chat.Post(Pukki.Id, room, text, actions:
        [
            new ChatAction(Prefix + "cal", "Ja, eintragen", c.Key),
            new ChatAction(Prefix + "done", "Schon erledigt", c.Key),
            new ChatAction(Prefix + "later", "Später", c.Key),
            new ChatAction(Prefix + "skip", "Nicht für mich", c.Key),
        ]);
    }

    /// <summary>Knopf unter Pukkis Frage.</summary>
    public static string Act(UserSpace space, string key, string? data)
    {
        var c = All.FirstOrDefault(x => x.Key == data) ?? throw new KeyNotFoundException("Unbekannte Vorsorge.");
        var today = CoachStore.Today(space);
        switch (key[Prefix.Length..])
        {
            case "cal":
                var day = AddToCalendar(space, c);
                return $"Eingetragen: „Termin vereinbaren: {c.Title}“ am {day.ToString("dddd, d. MMMM", new CultureInfo("de-DE"))} um 9 Uhr. Wenn du den Termin hattest, trag ihn im Coach unter „Hinweise“ als erledigt ein.";
            case "done":
                Mark(space.Db, c.Key, st => st.Done = today.ToString("yyyy-MM-dd"));
                return "Gut, dann trage ich das als heute erledigt ein. Das genaue Datum kannst du im Coach unter „Hinweise“ ändern.";
            case "later":
                Mark(space.Db, c.Key, st => st.AskAfter = today.AddMonths(1).ToString("yyyy-MM-dd"));
                return "In Ordnung, ich frage in einem Monat noch einmal.";
            case "skip":
                Mark(space.Db, c.Key, st => st.Skip = true);
                return "Verstanden, danach frage ich nicht mehr. Im Coach unter „Hinweise“ kannst du es wieder einschalten.";
        }
        return "Das kenne ich nicht.";
    }

    private static string Fmt(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d. M. yyyy", CultureInfo.InvariantCulture);

    // ---------------- Schnittstelle ----------------

    public record MarkBody(string? Done, bool? Skip);
    public record RemindBody(bool Remind);

    private static object View(UserSpace space)
    {
        var p = CoachStore.Profile(space.Db);
        var s = Settings(space.Db);
        return new
        {
            complete = p.Birth != null && p.Sex != null, remind = s.Remind,
            items = For(p, s, CoachStore.Today(space)).Select(x => new
            {
                x.Check.Key, x.Check.Kind, x.Check.Title, x.Check.Text, x.Check.Source, x.Check.Url, x.Check.MinAge, x.State, x.Done, x.Next,
            }),
        };
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/coach/checks").RequireAuthorization();
        api.MapGet("", (HttpContext http) => View(http.Space()));
        api.MapPut("/settings", (RemindBody body, HttpContext http) =>
        {
            var s = Settings(http.Space().Db); s.Remind = body.Remind; Save(http.Space().Db, s);
            return Results.Ok(View(http.Space()));
        });
        api.MapPut("/{key}", (string key, MarkBody body, HttpContext http) =>
        {
            var space = http.Space();
            if (body.Done is { Length: > 0 } d && (!DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day > CoachStore.Today(space)))
                throw new ArgumentException("Bitte ein Datum bis heute angeben.");
            Mark(space.Db, key, st =>
            {
                if (body.Done != null) st.Done = body.Done.Length > 0 ? body.Done : null;
                if (body.Skip is bool skip) st.Skip = skip;
            });
            return Results.Ok(View(space));
        });
        api.MapPost("/{key}/calendar", (string key, HttpContext http) =>
        {
            var space = http.Space();
            var c = All.FirstOrDefault(x => x.Key == key) ?? throw new KeyNotFoundException("Unbekannte Vorsorge.");
            return Results.Ok(new { day = AddToCalendar(space, c).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        });
    }
}

/// <summary>Pukkis Vorsorge-Frage: jede Viertelstunde prüfen (gefragt wird vormittags, höchstens einmal pro Woche).</summary>
public sealed class CoachChecksReminder(ChatStore chat, Spaces spaces, ILogger<CoachChecksReminder> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Release-Liste: in dieser Version nicht freigeschaltet - nichts im Hintergrund
        if (!DmOffice.Core.Features.On(DmOffice.Core.Features.Coach)) return;
        try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in spaces.All())
            {
                try { CoachChecks.Ask(space, chat, DateTime.UtcNow); }
                catch (Exception e) { log.LogWarning(e, "Vorsorge-Erinnerung fuer {User}", space.User.Login); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stop); } catch (OperationCanceledException) { return; }
        }
    }
}
