using System.Globalization;
using DmOffice.Chat;
using DmOffice.Core;

namespace DmOffice.Coach;

/// <summary>Einstellung des Fastens (kv „coach.fasting“). Aus, bis man es selbst einschaltet.</summary>
public sealed class FastingSettings
{
    public bool Enabled { get; set; }
    /// <summary>16:8, 14:10, 18:6, 5:2 oder custom</summary>
    public string Plan { get; set; } = "16:8";
    /// <summary>Fastenstunden bei „custom“ (12 bis 23).</summary>
    public int CustomHours { get; set; } = 16;
    /// <summary>Beginn des Essensfensters (HH:mm); das Fasten beginnt, wenn es endet.</summary>
    public string EatFrom { get; set; } = "12:00";
    /// <summary>5:2: Wochentage mit wenig Kalorien (0 = Sonntag … 6 = Samstag).</summary>
    public List<int> FastDays { get; set; } = [1, 4];
    /// <summary>Pukki erinnert bei Beginn und wenn das Ziel erreicht ist.</summary>
    public bool Remind { get; set; } = true;
}

/// <summary>
/// Fasten-Tracker (Coach Stufe 2, docs/dmcoach-konzept.md): Intervallfasten 16:8, 14:10, 18:6, eigener Plan (Timer mit
/// Start und Ende, Verlauf, Serie) und 5:2 (zwei Tage pro Woche mit 500 bzw. 600 kcal). Nur auf Wunsch eingeschaltet und
/// nicht in Schwangerschaft/Stillzeit, unter 18 Jahren oder bei Untergewicht (BMI unter 18,5).
/// </summary>
public static class CoachFasting
{
    private const string Key = "coach.fasting";
    public static readonly string[] Plans = ["16:8", "14:10", "18:6", "5:2", "custom"];

    public sealed record Fast(long Id, DateTime Start, DateTime? End, double Goal, bool Notified)
    {
        public double Hours(DateTime now) => Math.Max(0, ((End ?? now) - Start).TotalHours);
    }

    public static void Ensure(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS coach_fasts(id INTEGER PRIMARY KEY, start TEXT NOT NULL, \"end\" TEXT, goal REAL NOT NULL, notified INTEGER NOT NULL DEFAULT 0)");

    public static FastingSettings Settings(UserDb db) => db.GetJson<FastingSettings>(Key) ?? new FastingSettings();
    public static void Save(UserDb db, FastingSettings s) => db.SetJson(Key, s);

    /// <summary>Fastenstunden des Plans (5:2: kein Timer, 0).</summary>
    public static double GoalHours(FastingSettings s) => s.Plan switch
    {
        "16:8" => 16, "14:10" => 14, "18:6" => 18, "custom" => Math.Clamp(s.CustomHours, 12, 23), _ => 0,
    };

    /// <summary>Darf gefastet werden? Sonst der Grund (sachlich, ohne Vorwurf).</summary>
    public static string? NotAllowed(UserSpace space)
    {
        var p = CoachStore.Profile(space.Db);
        var today = CoachStore.Today(space);
        if (p.Pregnant) return "In Schwangerschaft und Stillzeit ist Fasten nicht vorgesehen.";
        var t = CoachTargetsCalc.Compute(p, CoachStore.Weight(space.Db, today), today);
        if (t.Age is int age && age < 18) return "Unter 18 Jahren ist Fasten nicht vorgesehen.";
        if (t.Bmi is double bmi && bmi < 18.5) return "Bei einem BMI unter 18,5 ist Fasten nicht vorgesehen. Wenn du dir Sorgen um dein Gewicht machst, sprich mit einer Ärztin oder einem Arzt.";
        return null;
    }

    public static Fast? Current(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT id, start, goal, notified FROM coach_fasts WHERE \"end\" IS NULL ORDER BY start DESC LIMIT 1",
            r => new Fast(r.GetInt64(0), Utc(r.GetString(1)), null, r.GetDouble(2), r.GetInt64(3) != 0)).FirstOrDefault();
    }

    public static List<Fast> History(UserDb db, int limit = 60)
    {
        Ensure(db);
        return db.Query("SELECT id, start, \"end\", goal, notified FROM coach_fasts WHERE \"end\" IS NOT NULL ORDER BY start DESC LIMIT $n",
            r => new Fast(r.GetInt64(0), Utc(r.GetString(1)), Utc(r.GetString(2)), r.GetDouble(3), r.GetInt64(4) != 0), ("$n", limit));
    }

    public static Fast Start(UserDb db, FastingSettings s, DateTime at)
    {
        if (Current(db) != null) throw new InvalidOperationException("Es läuft schon ein Fasten.");
        var goal = GoalHours(s);
        if (goal <= 0) throw new InvalidOperationException("Beim Plan 5:2 gibt es keinen Timer – an Fastentagen gilt ein Kalorienziel.");
        db.Exec("INSERT INTO coach_fasts(start, goal) VALUES($s, $g)", ("$s", Iso(at)), ("$g", goal));
        return Current(db)!;
    }

    public static Fast Stop(UserDb db, DateTime at)
    {
        var cur = Current(db) ?? throw new InvalidOperationException("Es läuft kein Fasten.");
        if (at < cur.Start) throw new ArgumentException("Das Ende liegt vor dem Beginn.");
        db.Exec("UPDATE coach_fasts SET \"end\" = $e WHERE id = $id", ("$e", Iso(at)), ("$id", cur.Id));
        return cur with { End = at };
    }

    public static void Change(UserDb db, long id, DateTime start, DateTime? end)
    {
        Ensure(db);
        if (end is DateTime e && e < start) throw new ArgumentException("Das Ende liegt vor dem Beginn.");
        if (end is DateTime e2 && (e2 - start).TotalHours > 7 * 24) throw new ArgumentException("Ein Fasten dauert hier höchstens sieben Tage.");
        if (db.Exec("UPDATE coach_fasts SET start = $s, \"end\" = $e WHERE id = $id", ("$s", Iso(start)), ("$e", end is DateTime x ? Iso(x) : null), ("$id", id)) == 0)
            throw new KeyNotFoundException("Diesen Eintrag gibt es nicht.");
    }

    public static void Delete(UserDb db, long id) { Ensure(db); db.Exec("DELETE FROM coach_fasts WHERE id = $id", ("$id", id)); }

    /// <summary>
    /// Serie: Tage in Folge (bis heute bzw. gestern), an denen ein Fasten mit erreichtem Ziel endete. Ein Tag ohne zählt als
    /// Unterbrechung; ein heute noch laufendes Fasten bricht die Serie nicht.
    /// </summary>
    public static int Streak(IEnumerable<Fast> done, TimeZoneInfo zone, DateOnly today)
    {
        var days = done.Where(f => f.End is DateTime e && f.Hours(e) >= f.Goal)
            .Select(f => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(f.End!.Value, zone))).ToHashSet();
        var d = days.Contains(today) ? today : today.AddDays(-1);
        var n = 0;
        while (days.Contains(d)) { n++; d = d.AddDays(-1); }
        return n;
    }

    /// <summary>5:2: heute ein Fastentag? Dann das Kalorienziel (500 Frauen, 600 Männer, divers 550).</summary>
    public static (bool FastDay, int Kcal) LowDay(FastingSettings s, CoachProfile p, DateOnly day) =>
        (s.Enabled && s.Plan == "5:2" && s.FastDays.Contains((int)day.DayOfWeek), p.Sex == "f" ? 500 : p.Sex == "m" ? 600 : 550);

    /// <summary>Wann das Fasten nach Plan heute beginnt (Ende des Essensfensters), in Ortszeit; null bei 5:2.</summary>
    public static DateTime? PlannedStart(FastingSettings s, DateOnly day)
    {
        var goal = GoalHours(s);
        if (goal <= 0 || !TimeOnly.TryParseExact(s.EatFrom, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)) return null;
        return day.ToDateTime(from).AddHours(24 - goal);
    }

    public static object View(UserSpace space)
    {
        var db = space.Db;
        var s = Settings(db);
        var now = DateTime.UtcNow;
        var today = CoachStore.Today(space);
        var cur = Current(db);
        var done = History(db);
        var month = done.Where(f => f.Start >= now.AddDays(-30)).ToList();
        var (fastDay, kcal) = LowDay(s, CoachStore.Profile(db), today);
        return new
        {
            settings = s,
            notAllowed = NotAllowed(space),
            goalHours = GoalHours(s),
            plannedStart = PlannedStart(s, today) is DateTime ps ? TimeZoneInfo.ConvertTimeToUtc(ps, space.Zone) : (DateTime?)null,
            current = cur == null ? null : new { cur.Id, cur.Start, goal = cur.Goal, hours = Math.Round(cur.Hours(now), 2), ends = cur.Start.AddHours(cur.Goal) },
            history = done.Take(30).Select(f => new { f.Id, f.Start, f.End, goal = f.Goal, hours = Math.Round(f.Hours(now), 1), reached = f.Hours(now) >= f.Goal }),
            stats = new
            {
                count = month.Count, reached = month.Count(f => f.Hours(now) >= f.Goal),
                avgHours = month.Count > 0 ? Math.Round(month.Average(f => f.Hours(now)), 1) : (double?)null,
                streak = Streak(done, space.Zone, today),
            },
            lowDay = s.Plan == "5:2" ? new { fastDay, kcal } : null,
        };
    }

    public static void Validate(FastingSettings s)
    {
        if (!Plans.Contains(s.Plan)) throw new ArgumentException("Unbekannter Plan.");
        s.CustomHours = Math.Clamp(s.CustomHours, 12, 23);
        if (!TimeOnly.TryParseExact(s.EatFrom, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ArgumentException("Uhrzeit bitte als HH:mm.");
        s.FastDays = s.FastDays.Where(d => d is >= 0 and <= 6).Distinct().OrderBy(d => d).ToList();
        if (s.Plan == "5:2" && s.FastDays.Count is < 1 or > 3) throw new ArgumentException("Bei 5:2 bitte ein bis drei Fastentage wählen.");
    }

    public record SettingsBody(bool Enabled, string Plan, int CustomHours, string EatFrom, List<int>? FastDays, bool Remind);
    public record AtBody(DateTime? At);
    public record ChangeBody(DateTime Start, DateTime? End);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/coach/fasting").RequireAuthorization();
        api.MapGet("", (HttpContext http) => View(http.Space()));
        api.MapPut("", (SettingsBody body, HttpContext http) =>
        {
            var space = http.Space();
            var s = new FastingSettings { Enabled = body.Enabled, Plan = body.Plan, CustomHours = body.CustomHours, EatFrom = body.EatFrom, FastDays = body.FastDays ?? [1, 4], Remind = body.Remind };
            Validate(s);
            if (s.Enabled && NotAllowed(space) is { } why) return Results.Json(new { error = why }, statusCode: 403);
            Save(space.Db, s);
            return Results.Ok(View(space));
        });
        api.MapPost("/start", (AtBody body, HttpContext http) =>
        {
            var space = http.Space();
            var s = Settings(space.Db);
            if (!s.Enabled) return Results.BadRequest(new { error = "Fasten ist ausgeschaltet." });
            if (NotAllowed(space) is { } why) return Results.Json(new { error = why }, statusCode: 403);
            Start(space.Db, s, Clamp(body.At));
            return Results.Ok(View(space));
        });
        api.MapPost("/stop", (AtBody body, HttpContext http) => { var space = http.Space(); Stop(space.Db, Clamp(body.At)); return Results.Ok(View(space)); });
        api.MapPut("/{id:long}", (long id, ChangeBody body, HttpContext http) =>
        {
            var space = http.Space();
            Change(space.Db, id, body.Start.ToUniversalTime(), body.End?.ToUniversalTime());
            return Results.Ok(View(space));
        });
        api.MapDelete("/{id:long}", (long id, HttpContext http) => { var space = http.Space(); Delete(space.Db, id); return Results.Ok(View(space)); });
    }

    /// <summary>Nachgetragener Beginn bzw. Ende: höchstens 48 Stunden zurück, nicht in der Zukunft.</summary>
    private static DateTime Clamp(DateTime? at)
    {
        var now = DateTime.UtcNow;
        var t = at?.ToUniversalTime() ?? now;
        if (t > now) t = now;
        if (t < now.AddHours(-48)) throw new ArgumentException("Nachtragen geht höchstens 48 Stunden zurück.");
        return t;
    }

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    private static DateTime Utc(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// Erinnerungen über Pukki (nur mit „Erinnern“ und einem Pukki-Chat): wenn das Fasten nach Plan beginnen soll und keins läuft,
    /// und wenn ein laufendes sein Ziel erreicht. Je einmal; aufgerufen jede Minute.
    /// </summary>
    public static void Remind(UserSpace space, ChatStore chat, DateTime nowUtc)
    {
        var db = space.Db;
        var s = Settings(db);
        if (!s.Enabled || !s.Remind || NotAllowed(space) != null) return;
        if (Pukki.RoomOf(chat, space.User.Id) is not { } room) return;
        var cur = Current(db);
        if (cur != null && !cur.Notified && cur.Hours(nowUtc) >= cur.Goal)
        {
            db.Exec("UPDATE coach_fasts SET notified = 1 WHERE id = $id", ("$id", cur.Id));
            chat.Post(Pukki.Id, room, $"Geschafft: {cur.Goal:0.#} Stunden gefastet. Du kannst das Fasten im Coach beenden, wenn du isst.");
            return;
        }
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, space.Zone);
        var today = DateOnly.FromDateTime(local);
        if (cur == null && PlannedStart(s, today) is DateTime planned && local >= planned && local < planned.AddMinutes(30))
        {
            var key = "coach.fasting.reminded";
            if (db.Get(key) == today.ToString("yyyy-MM-dd")) return;
            db.Set(key, today.ToString("yyyy-MM-dd"));
            chat.Post(Pukki.Id, room, $"Dein Essensfenster ist zu Ende – Zeit, mit dem Fasten zu beginnen ({s.Plan}). Starten kannst du es im Coach.");
        }
    }
}

/// <summary>Prüft jede Minute die Fasten-Erinnerungen aller Benutzer.</summary>
public sealed class CoachFastingReminder(ChatStore chat, Spaces spaces, ILogger<CoachFastingReminder> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Release-Liste: in dieser Version nicht freigeschaltet - nichts im Hintergrund
        if (!DmOffice.Core.Features.On(DmOffice.Core.Features.Coach)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in spaces.All())
            {
                try { CoachFasting.Remind(space, chat, DateTime.UtcNow); }
                catch (Exception e) { log.LogWarning(e, "Fasten-Erinnerung fuer {User}", space.User.Login); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (OperationCanceledException) { return; }
        }
    }
}
