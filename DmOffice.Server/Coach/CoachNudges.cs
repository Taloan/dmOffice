using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;

namespace DmOffice.Coach;

/// <summary>Wo man gerade ist (aus der App, wenn freigegeben) - für das Wetter bei der Bewegungs-Erinnerung.</summary>
public sealed class CoachLocation
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public long At { get; set; }
}

/// <summary>
/// Pukki als Coach (docs/dmcoach-konzept.md, nur wenn eingeschaltet): erinnert ans Trinken, wenn man hinter dem Tagesverlauf
/// liegt, an Bewegung mit Wetter, Sonnenuntergang und freier Zeit im Kalender, und schickt sonntags den Wochenrückblick. Nie in
/// der Ruhezeit des Chats, nie nachts, ohne Druck. Die Regeln sind reine Funktionen (testbar), der Dienst prüft alle fünf Minuten.
/// </summary>
public static class CoachNudges
{
    public const string LocationKey = "coach.location";
    private const string StateKey = "coach.nudges";
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Was schon erinnert wurde (je Tag zurückgesetzt).</summary>
    public sealed class State
    {
        public string? Day { get; set; }
        public int Water { get; set; }
        public long WaterAt { get; set; }
        public bool Move { get; set; }
        public string? Week { get; set; }
    }

    // ---------------- Regeln ----------------

    /// <summary>
    /// Trinken: Soll im Tagesverlauf linear von 8 bis 20 Uhr. Erinnert zwischen 10 und 20 Uhr, wenn man mehr als etwa anderthalb
    /// Gläser zurückliegt - höchstens dreimal am Tag, frühestens zwei Stunden nach der letzten Erinnerung und eine Stunde nach dem
    /// letzten Glas.
    /// </summary>
    /// <param name="size">Das erste eigene Gefäß (ml): wer nur beim Anbrechen einer 1-l-Flasche einträgt, liegt zwischendurch scheinbar
    /// zurück - erinnert wird erst bei drei Vierteln davon.</param>
    public static string? Water(int ml, int goal, DateTime local, State st, DateTime? lastDrink, int size = 250)
    {
        if (local.Hour < 10 || local.Hour >= 20 || ml >= goal || st.Water >= 3) return null;
        if (lastDrink is DateTime d && local - d < TimeSpan.FromHours(1)) return null;
        var share = Math.Clamp((local.TimeOfDay.TotalHours - 8) / 12, 0, 1);
        var expected = goal * share;
        if (ml > expected - Math.Max(350, size * 0.75)) return null;
        var soll = Math.Round(expected / 250) * 0.25;
        return $"Kleine Erinnerung: Bis jetzt hast du heute {L(ml / 1000.0)} l getrunken – bis {local:HH} Uhr wären etwa {L(soll)} l gut. Zeit, etwas zu trinken? 💧";
    }

    public sealed record WeatherHour(int Hour, double Temp, int? Rain);
    public sealed record Busy(TimeOnly Start, TimeOnly End);

    /// <summary>
    /// Bewegung: einmal am Tag zwischen 13 und 19 Uhr, wenn noch mindestens 1.500 Schritte fehlen und man hinter dem Tagesverlauf
    /// liegt (Soll linear von 7 bis 21 Uhr). Schlägt die erste trockene Stunde (Regen unter 30 %) vor dem Sonnenuntergang vor, in
    /// der kein Termin liegt; ohne Wetter nur die Schritte.
    /// </summary>
    public static string? Move(int steps, int goal, DateTime local, IReadOnlyList<WeatherHour>? weather, TimeOnly? sunset, IReadOnlyList<Busy> busy)
    {
        if (local.Hour < 13 || local.Hour >= 19) return null;
        var missing = goal - steps;
        if (missing < 1500) return null;
        var share = Math.Clamp((local.TimeOfDay.TotalHours - 7) / 14, 0, 1);
        if (steps >= goal * share * 0.85) return null;
        var minutes = Math.Max(10, (int)Math.Round(missing / 100.0 / 5) * 5);
        var text = $"Dir fehlen heute noch {missing.ToString("N0", De)} Schritte – etwa {minutes} Minuten zu Fuß.";
        if (weather == null || weather.Count == 0) return text + " Hast du heute noch Lust auf eine Runde draußen? 🚶";
        // Eine Stunde passt, wenn ihre Mitte vor dem Sonnenuntergang liegt (17:52 → 17 Uhr geht noch), spätestens bis 21 Uhr
        var end = sunset is TimeOnly ss ? Math.Min(ss.Minute >= 30 ? ss.Hour + 1 : ss.Hour, 21) : 21;
        bool Free(int h) => !busy.Any(b => b.Start < new TimeOnly(h, 59) && b.End > new TimeOnly(h, 0));
        var from = local.Minute < 30 ? local.Hour : local.Hour + 1;
        var slot = weather.Where(w => w.Hour >= from && w.Hour < end && (w.Rain ?? 0) < 30 && Free(w.Hour)).OrderBy(w => w.Hour).FirstOrDefault();
        if (slot == null)
            return weather.Any(w => w.Hour >= from && w.Hour < end && (w.Rain ?? 0) >= 30)
                ? text + " Draußen sieht es bis zum Abend eher nass aus – vielleicht ein Spaziergang mit Schirm, Treppen statt Aufzug oder ein paar Runden durchs Haus?"
                : text + " Bis zum Abend ist dein Kalender ziemlich voll – vielleicht passt ein kurzer Gang zwischendurch?";
        var next = busy.Where(b => b.Start >= new TimeOnly(slot.Hour, 0)).OrderBy(b => b.Start).FirstOrDefault();
        var when = slot.Hour <= local.Hour ? "Gerade ist es trocken" : $"Ab {slot.Hour} Uhr ist es trocken";
        return text + $" {when} bei {Math.Round(slot.Temp):0} °C"
            + (sunset is TimeOnly s2 ? $", Sonnenuntergang {s2:HH\\:mm}" : "")
            + (next != null ? $" – und bis {next.Start:HH\\:mm} hast du keinen Termin." : " – und danach hast du keinen Termin mehr.")
            + " 🚶";
    }

    private static string L(double liters) => liters.ToString("0.0#", De);

    // ---------------- Durchgang je Benutzer ----------------

    /// <summary>Alle Erinnerungen eines Benutzers prüfen und höchstens eine schicken. Aufgerufen alle fünf Minuten.</summary>
    public static async Task RunAsync(IServiceProvider services, UserSpace space, ChatStore chat, IHttpClientFactory http, DateTime nowUtc, CancellationToken ct)
    {
        var db = space.Db;
        var p = CoachStore.Profile(db);
        if (!p.Nudges) return;
        if (Pukki.RoomOf(chat, space.User.Id) is not { } room) return;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, space.Zone);
        if (ChatSettings.Of(db).InQuiet(local)) return;
        var today = DateOnly.FromDateTime(local);
        var st = db.GetJson<State>(StateKey) ?? new State();
        // Wasser und Schritte: wäre eine Erinnerung fällig, erst den aktuellen Stand der Uhr holen und noch einmal prüfen
        // (sonst „trink was“, obwohl längst auf der Uhr eingetragen) - so fragt die Prüfung alle fünf Minuten Garmin nicht jedes Mal
        var refreshed = false;
        async Task<bool> Fresh()
        {
            if (refreshed) return false;
            refreshed = true;
            await CoachGarmin.RefreshAsync(space, http.CreateClient(), services.GetRequiredService<ServerConfig>(), ct);
            return true;
        }
        if (st.Day != CoachStore.Day(today)) st = new State { Day = CoachStore.Day(today), Week = st.Week };

        string? text = null;
        var weekly = false;
        // Sonntags um 18 Uhr der Wochenrückblick (einmal je Woche)
        var week = ISOWeek.GetYear(local) + "-" + ISOWeek.GetWeekOfYear(local);
        if (p.NudgeWeek && local.DayOfWeek == DayOfWeek.Sunday && local.Hour == 18 && st.Week != week
            && CoachStore.Stats(db, today.AddDays(-6), today).Any(d => d.Entries > 0))
        {
            st.Week = week;
            var targets = CoachTargetsCalc.Compute(p, CoachStore.Weight(db, today), today);
            text = "Dein Wochenrückblick: " + CoachKnowledge.Week(db, today, p, targets).Text.Replace("Deine letzten sieben Tage: ", "");
            // Das Vorhaben aus dem letzten Rückblick nennen; die Antwort auf die Frage merkt sich Pukki als neues (PukkiWeekGoal.cs)
            if (Pukki.WeekGoalOf(chat, space.User.Id) is { } goal)
                text = text.Replace(" Was möchtest du dir", $" Vorgenommen hattest du dir: „{goal}“. Was möchtest du dir");
            weekly = true;
        }
        if (text == null && p.NudgeWater)
        {
            string? WaterNudge()
            {
                var water = CoachStore.WaterOf(db, today);
                // Auch was auf der Uhr (Garmin) eingetragen wurde
                var watch = CoachStore.WatchWater(db, today);
                var targets = CoachTargetsCalc.Compute(p, CoachStore.Weight(db, today), today);
                // Nur, wer überhaupt Wasser einträgt - sonst wäre jede Erinnerung falsch
                var tracks = water.Count > 0 || watch.Ml > 0 || CoachStore.Stats(db, today.AddDays(-7), today.AddDays(-1)).Any(d => d.Water > 0);
                long? lastAt = water.Count > 0 ? water.Max(w => w.At) : null;
                if (watch.At is long wa && (lastAt == null || wa > lastAt)) lastAt = wa;
                var last = lastAt is long la ? TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(la).UtcDateTime, space.Zone) : (DateTime?)null;
                return tracks && nowUtc - DateTimeOffset.FromUnixTimeSeconds(st.WaterAt).UtcDateTime >= TimeSpan.FromHours(2)
                    ? Water(water.Sum(w => w.Ml) + watch.Ml, targets.WaterMl, local, st, last, p.WaterSizes.FirstOrDefault(250)) : null;
            }
            var w = WaterNudge();
            if (w != null && await Fresh()) w = WaterNudge();
            if (w != null)
            {
                text = w;
                st.Water++;
                st.WaterAt = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            }
        }
        if (text == null && p.NudgeMove && !st.Move)
        {
            var act = CoachStore.ActivityOf(db, today);
            // Sähe es nach zu wenig Bewegung aus: erst die aktuellen Schritte holen
            if (act?.Steps is int s0 && Move(s0, CoachStore.StepGoal(db, today), local, null, null, []) != null && await Fresh())
                act = CoachStore.ActivityOf(db, today);
            // Nur mit frischen Schritten (von der Uhr bzw. der App in den letzten drei Stunden) - alte Zahlen lügen
            if (act?.Steps is int steps && CoachStore.ActivityUpdated(db, today) is long upd && nowUtc - DateTimeOffset.FromUnixTimeSeconds(upd).UtcDateTime < TimeSpan.FromHours(3))
            {
                var goal = CoachStore.StepGoal(db, today);
                if (Move(steps, goal, local, null, null, []) != null)
                {
                    var (lat, lon) = Where(space);
                    (List<WeatherHour> Hours, TimeOnly? Sunset)? weather = lat is double la && lon is double lo ? await CoachWeather.TodayAsync(http.CreateClient(), la, lo, ct) : null;
                    var busy = Pukki.DayEvents(services, space, today).Where(e => !e.AllDay).Select(e => new Busy(e.Start, e.End)).ToList();
                    text = Move(steps, goal, local, weather?.Hours, weather?.Sunset, busy);
                    st.Move = true;
                }
            }
        }
        db.SetJson(StateKey, st);
        if (text != null) chat.Post(Pukki.Id, room, text);
        if (text != null && weekly) Pukki.WeekGoalAsked(chat, space.User.Id);
    }

    /// <summary>Ort für das Wetter: aus der App (höchstens sechs Stunden alt), sonst der Wohnort aus Pukkis Tagesüberblick.</summary>
    public static (double? Lat, double? Lon) Where(UserSpace space)
    {
        if (space.Db.GetJson<CoachLocation>(LocationKey) is { } l && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - l.At < 6 * 3600) return (l.Lat, l.Lon);
        var b = PukkiBriefing.Of(space.Db);
        return (b.Lat, b.Lon);
    }
}

/// <summary>Open-Meteo für heute: Temperatur und Regenwahrscheinlichkeit je Stunde, Sonnenuntergang (30 Minuten zwischengespeichert).</summary>
public static class CoachWeather
{
    private static readonly ConcurrentDictionary<string, (DateTime At, List<CoachNudges.WeatherHour> Hours, TimeOnly? Sunset)> Cache = new();

    public static async Task<(List<CoachNudges.WeatherHour> Hours, TimeOnly? Sunset)?> TodayAsync(HttpClient http, double lat, double lon, CancellationToken ct)
    {
        var key = $"{lat:0.##}|{lon:0.##}";
        if (Cache.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(30)) return (c.Hours, c.Sunset);
        var inv = CultureInfo.InvariantCulture;
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString("0.####", inv)}&longitude={lon.ToString("0.####", inv)}" +
                  "&hourly=temperature_2m,precipitation_probability&daily=sunset&forecast_days=1&timezone=auto";
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
            var h = doc.RootElement.GetProperty("hourly");
            var times = h.GetProperty("time");
            var hours = new List<CoachNudges.WeatherHour>();
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                var t = DateTime.Parse(times[i].GetString()!, inv);
                var temp = h.GetProperty("temperature_2m")[i];
                var rain = h.GetProperty("precipitation_probability")[i];
                hours.Add(new CoachNudges.WeatherHour(t.Hour, temp.ValueKind == JsonValueKind.Number ? temp.GetDouble() : 0,
                    rain.ValueKind == JsonValueKind.Number ? rain.GetInt32() : null));
            }
            TimeOnly? sunset = doc.RootElement.GetProperty("daily").GetProperty("sunset")[0].GetString() is { } s ? TimeOnly.FromDateTime(DateTime.Parse(s, inv)) : null;
            Cache[key] = (DateTime.UtcNow, hours, sunset);
            return (hours, sunset);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Prüft alle fünf Minuten die Erinnerungen von Pukki als Coach für alle, die sie eingeschaltet haben.</summary>
public sealed class CoachNudgeService(IServiceProvider services, ChatStore chat, Spaces spaces, IHttpClientFactory http, ILogger<CoachNudgeService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Release-Liste: in dieser Version nicht freigeschaltet - nichts im Hintergrund
        if (!DmOffice.Core.Features.On(DmOffice.Core.Features.Coach)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(60), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in spaces.All())
            {
                try { await CoachNudges.RunAsync(services, space, chat, http, DateTime.UtcNow, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception e) { log.LogWarning(e, "Coach-Erinnerung fuer {User}", space.User.Login); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stop); } catch (OperationCanceledException) { return; }
        }
    }
}
