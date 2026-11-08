using System.Collections.Concurrent;
using System.Globalization;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Coach;

/// <summary>
/// Coach-Daten einer Person in ihrer Benutzerdatenbank (privat, verschlüsselt, eigene Tabellen per CREATE IF NOT EXISTS):
/// Tagebuch (coach_diary), Wasser, Körperdaten, Aktivität (Uhr/Health Connect), Verlauf des dynamischen Schrittziels.
/// Tage als yyyy-MM-dd in der Zeitzone der Person.
/// </summary>
public static class CoachStore
{
    public static readonly string[] Meals = ["breakfast", "lunch", "dinner", "snack"];
    public const string ProfileKey = "coach.profile";

    private static readonly ConcurrentDictionary<UserDb, bool> Ready = new();

    public static void Ensure(UserDb db)
    {
        if (Ready.ContainsKey(db)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS coach_diary (id INTEGER PRIMARY KEY AUTOINCREMENT, day TEXT NOT NULL, meal TEXT NOT NULL, food TEXT NULL,
    name TEXT NOT NULL, brand TEXT NULL, amount REAL NULL, portion TEXT NULL, kcal REAL NOT NULL, protein REAL NOT NULL, fat REAL NOT NULL, carbs REAL NOT NULL,
    fiber REAL NULL, sugar REAL NULL, salt REAL NULL, at INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS coach_diary_day ON coach_diary(day);
CREATE INDEX IF NOT EXISTS coach_diary_food ON coach_diary(food);
CREATE TABLE IF NOT EXISTS coach_water (id INTEGER PRIMARY KEY AUTOINCREMENT, day TEXT NOT NULL, ml INTEGER NOT NULL, at INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS coach_water_day ON coach_water(day);
CREATE TABLE IF NOT EXISTS coach_body (day TEXT PRIMARY KEY, weight REAL NULL, fat REAL NULL, source TEXT NOT NULL DEFAULT 'manual');
CREATE TABLE IF NOT EXISTS coach_activity (day TEXT PRIMARY KEY, steps INTEGER NULL, active_kcal REAL NULL, distance REAL NULL, sleep_min INTEGER NULL,
    resting_hr INTEGER NULL, source TEXT NOT NULL, updated INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS coach_steps_goal (day TEXT PRIMARY KEY, goal INTEGER NOT NULL);");
        Ready[db] = true;
    }

    public static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateOnly Today(UserSpace space) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone));
    private static long Unix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---------------- Profil ----------------

    public static CoachProfile Profile(UserDb db) => db.GetJson<CoachProfile>(ProfileKey) ?? new CoachProfile();
    public static void SaveProfile(UserDb db, CoachProfile p) => db.SetJson(ProfileKey, p);

    /// <summary>Zuletzt eingetragenes Gewicht bis zu diesem Tag, sonst das aus dem Profil.</summary>
    public static double? Weight(UserDb db, DateOnly upTo)
    {
        Ensure(db);
        return db.Query("SELECT weight FROM coach_body WHERE weight IS NOT NULL AND day <= $d ORDER BY day DESC LIMIT 1", r => r.GetDouble(0), ("$d", Day(upTo)))
            .Cast<double?>().FirstOrDefault() ?? Profile(db).WeightKg;
    }

    // ---------------- Tagebuch ----------------

    public sealed record Entry(long Id, string Day, string Meal, string? Food, string Name, string? Brand, double? Amount, string? Portion,
        double Kcal, double Protein, double Fat, double Carbs, double? Fiber, double? Sugar, double? Salt, long At);

    private const string EntryCols = "id, day, meal, food, name, brand, amount, portion, kcal, protein, fat, carbs, fiber, sugar, salt, at";
    private static Entry ReadEntry(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetDouble(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.GetDouble(8), r.GetDouble(9), r.GetDouble(10), r.GetDouble(11), r.IsDBNull(12) ? null : r.GetDouble(12),
        r.IsDBNull(13) ? null : r.GetDouble(13), r.IsDBNull(14) ? null : r.GetDouble(14), r.GetInt64(15));

    public static List<Entry> Diary(UserDb db, DateOnly day)
    {
        Ensure(db);
        return db.Query($"SELECT {EntryCols} FROM coach_diary WHERE day=$d ORDER BY at, id", ReadEntry, ("$d", Day(day)));
    }

    public static Entry? EntryById(UserDb db, long id)
    {
        Ensure(db);
        return db.Query($"SELECT {EntryCols} FROM coach_diary WHERE id=$i", ReadEntry, ("$i", id)).FirstOrDefault();
    }

    public static string Meal(string? meal) => Meals.Contains(meal) ? meal! : throw new ArgumentException("Unbekannte Mahlzeit.");

    /// <summary>Eintrag aus einem Lebensmittel und einer Menge (g/ml).</summary>
    public static Entry Add(UserDb db, DateOnly day, string meal, Food food, double amount, string? portion)
    {
        if (amount <= 0 || amount > 10000) throw new ArgumentException("Die Menge passt nicht (1 g bis 10 kg).");
        var n = food.Per100.For(amount);
        return Insert(db, day, Meal(meal), food.Id, food.Name, food.Brand, amount, portion, n);
    }

    /// <summary>Schnell eintragen: nur Name und Nährwerte (ohne Lebensmittel).</summary>
    public static Entry AddQuick(UserDb db, DateOnly day, string meal, string? name, Nutrients n)
    {
        if (n.Kcal < 0 || n.Kcal > 10000) throw new ArgumentException("Die Energie passt nicht.");
        var title = string.IsNullOrWhiteSpace(name) ? "Schnell eingetragen" : name.Trim();
        return Insert(db, day, Meal(meal), null, title.Length > 120 ? title[..120] : title, null, null, null, n);
    }

    /// <summary>Eintrag mit fertigen Nährwerten (Vorlage, Kopie, Rezept).</summary>
    public static Entry AddRaw(UserDb db, DateOnly day, string meal, string? food, string name, string? brand, double? amount, string? portion, Nutrients n) =>
        Insert(db, day, Meal(meal), food, name, brand, amount, portion, n);

    private static Entry Insert(UserDb db, DateOnly day, string meal, string? food, string name, string? brand, double? amount, string? portion, Nutrients n)
    {
        Ensure(db);
        static double R(double v) => Math.Round(v, 2);
        static double? Rn(double? v) => v is double d ? Math.Round(d, 2) : null;
        var id = (long)db.Scalar(@"INSERT INTO coach_diary(day, meal, food, name, brand, amount, portion, kcal, protein, fat, carbs, fiber, sugar, salt, at)
            VALUES($d,$m,$f,$n,$b,$a,$p,$k,$pr,$fa,$c,$fi,$s,$sa,$t) RETURNING id",
            ("$d", Day(day)), ("$m", meal), ("$f", food), ("$n", name), ("$b", brand), ("$a", amount), ("$p", portion), ("$k", R(n.Kcal)), ("$pr", R(n.Protein)),
            ("$fa", R(n.Fat)), ("$c", R(n.Carbs)), ("$fi", Rn(n.Fiber)), ("$s", Rn(n.Sugar)), ("$sa", Rn(n.Salt)), ("$t", Unix()))!;
        return EntryById(db, id)!;
    }

    /// <summary>Menge oder Mahlzeit ändern; die Nährwerte wachsen mit der Menge mit.</summary>
    public static Entry Update(UserDb db, long id, double? amount, string? meal, string? portion)
    {
        var e = EntryById(db, id) ?? throw new KeyNotFoundException("Eintrag nicht gefunden.");
        var m = meal == null ? e.Meal : Meal(meal);
        if (amount is double a && e.Amount is double old && old > 0)
        {
            if (a <= 0 || a > 10000) throw new ArgumentException("Die Menge passt nicht (1 g bis 10 kg).");
            var f = a / old;
            db.Exec(@"UPDATE coach_diary SET amount=$a, portion=$p, kcal=kcal*$f, protein=protein*$f, fat=fat*$f, carbs=carbs*$f,
                fiber=fiber*$f, sugar=sugar*$f, salt=salt*$f, meal=$m WHERE id=$i", ("$a", a), ("$p", portion), ("$f", f), ("$m", m), ("$i", id));
        }
        else db.Exec("UPDATE coach_diary SET meal=$m WHERE id=$i", ("$m", m), ("$i", id));
        return EntryById(db, id)!;
    }

    public static void Delete(UserDb db, long id) { Ensure(db); db.Exec("DELETE FROM coach_diary WHERE id=$i", ("$i", id)); }

    /// <summary>
    /// Zuletzt Gegessenes (für die Auswahl): je Lebensmittel einmal, mit der zuletzt gewählten Menge; zuerst, was es zu dieser
    /// Mahlzeit oft gab, dann der Rest der letzten 60 Tage.
    /// </summary>
    public sealed record Recent(string Food, string Name, string? Brand, double Amount, string? Portion, double Kcal, int Count, int MealCount);

    public static List<Recent> RecentFoods(UserDb db, string? meal, DateOnly today, int limit = 30)
    {
        Ensure(db);
        var rows = db.Query($@"SELECT food, name, brand, amount, portion, kcal, meal FROM coach_diary
                WHERE food IS NOT NULL AND amount IS NOT NULL AND day >= $from ORDER BY at DESC, id DESC",
            r => (Food: r.GetString(0), Name: r.GetString(1), Brand: r.IsDBNull(2) ? null : r.GetString(2), Amount: r.GetDouble(3),
                Portion: r.IsDBNull(4) ? null : r.GetString(4), Kcal: r.GetDouble(5), Meal: r.GetString(6)), ("$from", Day(today.AddDays(-60))));
        return rows.GroupBy(x => x.Food).Select(g =>
        {
            var last = g.First();
            return new Recent(g.Key, last.Name, last.Brand, last.Amount, last.Portion, Math.Round(last.Kcal), g.Count(), g.Count(x => x.Meal == meal));
        }).OrderByDescending(x => x.MealCount).ThenByDescending(x => x.Count).Take(limit).ToList();
    }

    // ---------------- Wasser ----------------

    public sealed record Water(long Id, int Ml, long At);

    public static List<Water> WaterOf(UserDb db, DateOnly day)
    {
        Ensure(db);
        return db.Query("SELECT id, ml, at FROM coach_water WHERE day=$d ORDER BY at, id", r => new Water(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2)), ("$d", Day(day)));
    }

    public static Water AddWater(UserDb db, DateOnly day, int ml)
    {
        if (ml is < 10 or > 3000) throw new ArgumentException("Die Menge passt nicht (10 ml bis 3 l).");
        Ensure(db);
        var id = (long)db.Scalar("INSERT INTO coach_water(day, ml, at) VALUES($d,$m,$t) RETURNING id", ("$d", Day(day)), ("$m", ml), ("$t", Unix()))!;
        return new Water(id, ml, Unix());
    }

    public static void DeleteWater(UserDb db, long id) { Ensure(db); db.Exec("DELETE FROM coach_water WHERE id=$i", ("$i", id)); }

    // ---------------- Körper und Aktivität ----------------

    public static void SetWeight(UserDb db, DateOnly day, double? weight, double? fat, string source = "manual")
    {
        if (weight is double w && (w < 20 || w > 400)) throw new ArgumentException("Das Gewicht passt nicht (20 bis 400 kg).");
        if (fat is double f && (f < 2 || f > 75)) throw new ArgumentException("Der Körperfettanteil passt nicht (2 bis 75 %).");
        Ensure(db);
        if (weight == null && fat == null) { db.Exec("DELETE FROM coach_body WHERE day=$d AND source=$s", ("$d", Day(day)), ("$s", source)); return; }
        db.Exec(@"INSERT INTO coach_body(day, weight, fat, source) VALUES($d,$w,$f,$s)
            ON CONFLICT(day) DO UPDATE SET weight=COALESCE($w, weight), fat=COALESCE($f, fat), source=$s",
            ("$d", Day(day)), ("$w", weight), ("$f", fat), ("$s", source));
    }

    public sealed record Activity(string Day, int? Steps, double? ActiveKcal, double? Distance, int? SleepMin, int? RestingHr, string Source);

    /// <summary>
    /// Tageswerte von der Uhr (Health Connect, Garmin) - was fehlt, bleibt, wie es war. Schritte, aktive Kalorien und Strecke
    /// nehmen den höheren Wert: liefern Handy und Uhr beide, zählt das Handy oft nur, was es selbst gemessen hat (z. B. 9 Schritte
    /// statt der 4.900 der Uhr), und im Lauf des Tages wachsen die Werte nur.
    /// </summary>
    public static void SetActivity(UserDb db, Activity a)
    {
        Ensure(db);
        db.Exec(@"INSERT INTO coach_activity(day, steps, active_kcal, distance, sleep_min, resting_hr, source, updated) VALUES($d,$s,$k,$di,$sl,$hr,$src,$t)
            ON CONFLICT(day) DO UPDATE SET steps=MAX(COALESCE($s, steps), COALESCE(steps, $s)), active_kcal=MAX(COALESCE($k, active_kcal), COALESCE(active_kcal, $k)),
                distance=MAX(COALESCE($di, distance), COALESCE(distance, $di)),
                sleep_min=COALESCE($sl, sleep_min), resting_hr=COALESCE($hr, resting_hr), source=$src, updated=$t",
            ("$d", a.Day), ("$s", a.Steps), ("$k", a.ActiveKcal), ("$di", a.Distance), ("$sl", a.SleepMin), ("$hr", a.RestingHr), ("$src", a.Source), ("$t", Unix()));
    }

    public static Activity? ActivityOf(UserDb db, DateOnly day)
    {
        Ensure(db);
        return db.Query("SELECT day, steps, active_kcal, distance, sleep_min, resting_hr, source FROM coach_activity WHERE day=$d", ReadActivity, ("$d", Day(day))).FirstOrDefault();
    }

    /// <summary>Weitere Tageswerte von der Uhr (Body Battery, Stress, HRV …) - Tabelle coach_extra, null löscht nichts.</summary>
    public static void SetExtra(UserDb db, DateOnly day, string key, double? value)
    {
        if (value is not double v) return;
        db.Exec("CREATE TABLE IF NOT EXISTS coach_extra (day TEXT NOT NULL, key TEXT NOT NULL, value REAL NOT NULL, PRIMARY KEY (day, key))");
        db.Exec("INSERT INTO coach_extra(day, key, value) VALUES($d,$k,$v) ON CONFLICT(day, key) DO UPDATE SET value=$v", ("$d", Day(day)), ("$k", key), ("$v", v));
    }

    public static Dictionary<string, double> ExtrasOf(UserDb db, DateOnly day)
    {
        db.Exec("CREATE TABLE IF NOT EXISTS coach_extra (day TEXT NOT NULL, key TEXT NOT NULL, value REAL NOT NULL, PRIMARY KEY (day, key))");
        return db.Query("SELECT key, value FROM coach_extra WHERE day=$d", r => (r.GetString(0), r.GetDouble(1)), ("$d", Day(day))).ToDictionary(x => x.Item1, x => x.Item2);
    }

    /// <summary>Wasser, das auf der Uhr (Garmin) eingetragen wurde - getrennt von den eigenen Einträgen, die Summe des Tages
    /// und wann zuletzt (Unix-Sekunden).</summary>
    public static (int Ml, long? At) WatchWater(UserDb db, DateOnly day)
    {
        var x = ExtrasOf(db, day);
        return ((int)Math.Round(x.GetValueOrDefault("waterWatchMl")), x.TryGetValue("waterWatchAt", out var at) ? (long)at : null);
    }

    /// <summary>Getrunken an einem Tag: eigene Einträge und die von der Uhr.</summary>
    public static int WaterMl(UserDb db, DateOnly day) => WaterOf(db, day).Sum(w => w.Ml) + WatchWater(db, day).Ml;

    /// <summary>Wann die Tageswerte zuletzt kamen (Unix-Sekunden) - für Erinnerungen nur mit frischen Schritten.</summary>
    public static long? ActivityUpdated(UserDb db, DateOnly day)
    {
        Ensure(db);
        return db.Scalar("SELECT updated FROM coach_activity WHERE day=$d", ("$d", Day(day))) as long?;
    }

    private static Activity ReadActivity(SqliteDataReader r) => new(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetDouble(2),
        r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetString(6));

    /// <summary>Schrittziel des Tages: fest aus dem Profil oder dynamisch (einmal je Tag berechnet und gemerkt).</summary>
    public static int StepGoal(UserDb db, DateOnly day)
    {
        var p = Profile(db);
        if (p.StepMode != "dynamic") return Math.Clamp(p.StepGoal, 1000, 50000);
        Ensure(db);
        if (db.Scalar("SELECT goal FROM coach_steps_goal WHERE day=$d", ("$d", Day(day))) is long g) return (int)g;
        var prev = db.Query("SELECT day, goal FROM coach_steps_goal WHERE day < $d ORDER BY day DESC LIMIT 1", r => (Day: r.GetString(0), Goal: r.GetInt32(1)), ("$d", Day(day))).FirstOrDefault();
        int goal;
        if (prev.Day == null) goal = 10000;
        else
        {
            // Durchschnitt der letzten drei Tage mit Daten vor diesem Tag
            var last = db.Query("SELECT steps FROM coach_activity WHERE day < $d AND steps IS NOT NULL AND steps > 0 ORDER BY day DESC LIMIT 3", r => r.GetInt32(0), ("$d", Day(day)));
            goal = CoachTargetsCalc.NextStepGoal(prev.Goal, last);
        }
        db.Exec("INSERT OR IGNORE INTO coach_steps_goal(day, goal) VALUES($d,$g)", ("$d", Day(day)), ("$g", goal));
        return goal;
    }

    // ---------------- Statistik ----------------

    public sealed record DayStat(string Day, double Kcal, double Protein, double Carbs, double Fat, int Water, int? Steps, int? StepGoal,
        double? Weight, double? ActiveKcal, int? SleepMin, int? RestingHr, int Entries);

    public static List<DayStat> Stats(UserDb db, DateOnly from, DateOnly to)
    {
        Ensure(db);
        var f = Day(from); var t = Day(to);
        var food = db.Query("SELECT day, SUM(kcal), SUM(protein), SUM(carbs), SUM(fat), COUNT(*) FROM coach_diary WHERE day BETWEEN $f AND $t GROUP BY day",
            r => (r.GetString(0), K: r.GetDouble(1), P: r.GetDouble(2), C: r.GetDouble(3), F: r.GetDouble(4), N: r.GetInt32(5)), ("$f", f), ("$t", t)).ToDictionary(x => x.Item1);
        var water = db.Query("SELECT day, SUM(ml) FROM coach_water WHERE day BETWEEN $f AND $t GROUP BY day", r => (r.GetString(0), r.GetInt32(1)), ("$f", f), ("$t", t)).ToDictionary(x => x.Item1, x => x.Item2);
        // Dazu das Wasser von der Uhr
        db.Exec("CREATE TABLE IF NOT EXISTS coach_extra (day TEXT NOT NULL, key TEXT NOT NULL, value REAL NOT NULL, PRIMARY KEY (day, key))");
        foreach (var (d, ml) in db.Query("SELECT day, value FROM coach_extra WHERE key='waterWatchMl' AND day BETWEEN $f AND $t", r => (r.GetString(0), (int)Math.Round(r.GetDouble(1))), ("$f", f), ("$t", t)))
            water[d] = water.GetValueOrDefault(d) + ml;
        var act = db.Query("SELECT day, steps, active_kcal, distance, sleep_min, resting_hr, source FROM coach_activity WHERE day BETWEEN $f AND $t", ReadActivity, ("$f", f), ("$t", t)).ToDictionary(x => x.Day);
        var body = db.Query("SELECT day, weight FROM coach_body WHERE weight IS NOT NULL AND day BETWEEN $f AND $t", r => (r.GetString(0), r.GetDouble(1)), ("$f", f), ("$t", t)).ToDictionary(x => x.Item1, x => x.Item2);
        var goals = db.Query("SELECT day, goal FROM coach_steps_goal WHERE day BETWEEN $f AND $t", r => (r.GetString(0), r.GetInt32(1)), ("$f", f), ("$t", t)).ToDictionary(x => x.Item1, x => x.Item2);
        var p = Profile(db);
        var list = new List<DayStat>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var k = Day(d);
            food.TryGetValue(k, out var fo);
            act.TryGetValue(k, out var a);
            int? goal = goals.TryGetValue(k, out var g) ? g : p.StepMode != "dynamic" ? p.StepGoal : null;
            list.Add(new DayStat(k, Math.Round(fo.K), Math.Round(fo.P, 1), Math.Round(fo.C, 1), Math.Round(fo.F, 1), water.GetValueOrDefault(k),
                a?.Steps, goal, body.TryGetValue(k, out var w) ? w : null, a?.ActiveKcal, a?.SleepMin, a?.RestingHr, fo.N));
        }
        return list;
    }

    /// <summary>Alles löschen (Profil, Tagebuch, Wasser, Körper, Aktivität).</summary>
    public static void DeleteAll(UserDb db)
    {
        Ensure(db);
        db.Exec("DELETE FROM coach_diary; DELETE FROM coach_water; DELETE FROM coach_body; DELETE FROM coach_activity; DELETE FROM coach_steps_goal; DROP TABLE IF EXISTS coach_extra;");
        // Auch die Garmin-Verbindung, den Ort und die Erinnerungen - „alles löschen“ heißt alles
        db.Set(CoachGarmin.Key, null); db.Set(CoachNudges.LocationKey, null); db.Set("coach.nudges", null);
        db.Set(ProfileKey, null);
    }
}
