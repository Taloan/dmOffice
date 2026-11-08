using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;

namespace DmOffice.Coach;

/// <summary>Garmin-Verbindung eines Benutzers (kv „coach.garmin“ in der verschlüsselten Benutzerdatenbank).</summary>
public sealed class GarminSettings
{
    /// <summary>Persönlicher API-Schlüssel aus Garmin Connect (Einstellungen → Persönlicher API-Schlüssel).</summary>
    public string? Key { get; set; }
    /// <summary>Gültig bis (yyyy-MM-dd), wie beim Anlegen gewählt - für die Erinnerung vor Ablauf.</summary>
    public string? Expires { get; set; }
    public long? LastSync { get; set; }
    /// <summary>Zuletzt komplett (gestern und heute) geholt - dazwischen alle 15 Minuten nur heute.</summary>
    public long? LastFull { get; set; }
    public string? LastError { get; set; }
    /// <summary>Die letzten 30 Tage schon geholt.</summary>
    public bool Backfilled { get; set; }
    /// <summary>Mit welcher Fassung des Lesens die 30 Tage geholt wurden - liest dmOffice mehr als damals, holt es sie einmal neu.</summary>
    public int BackfillVersion { get; set; }
    /// <summary>An den Ablauf schon erinnert (yyyy-MM-dd des Ablaufs).</summary>
    public string? ExpiryReminded { get; set; }
}

/// <summary>
/// Garmin Personal API (docs/dmcoach-konzept.md, „Bewegung, Schlaf, Uhr“): der Server holt mit dem persönlichen Schlüssel des
/// Benutzers seine eigenen Tageswerte ab - Schritte, aktive Kalorien, Strecke, Ruhepuls, Schlaf, Gewicht, dazu Body Battery, Stress
/// und HRV. Der Schlüssel wird gegen ein Zugangstoken (24 Stunden, JWT) getauscht; nur Lesen. Zuerst nur für Admins, für alle erst,
/// wenn der Admin es erlaubt. Garmin beschreibt die Antworten nicht im Einzelnen - die Werte werden darum tolerant gesucht
/// (mehrere übliche Feldnamen), und „Verbindung testen“ zeigt, was erkannt wurde.
/// </summary>
public static class CoachGarmin
{
    public const string Key = "coach.garmin";
    public const string BaseUrl = "https://personal.connectapi.garmin.com";
    private const string TokenPath = "/personalapitoken-service/personal-api/personal-api-key/token";

    public static GarminSettings Settings(UserDb db) => db.GetJson<GarminSettings>(Key) ?? new GarminSettings();
    public static void Save(UserDb db, GarminSettings s) => db.SetJson(Key, s);

    public static bool Allowed(UserSpace space, ServerConfig config) =>
        space.User.IsAdmin || config.Current.CoachGarminForAll;

    /// <summary>Fehler mit einem Satz für die Oberfläche.</summary>
    public sealed class GarminException(string message) : InvalidOperationException(message);

    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Exp)> Tokens = new();

    /// <summary>Zugangstoken zum Schlüssel (zwischengespeichert bis kurz vor Ablauf).</summary>
    public static async Task<string> TokenAsync(HttpClient http, string apiKey, CancellationToken ct)
    {
        var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        if (Tokens.TryGetValue(cacheKey, out var t) && t.Exp - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(10)) return t.Token;
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + TokenPath);
        req.Headers.TryAddWithoutValidation("Authorization", "ApiKey " + apiKey.Trim());
        req.Headers.Accept.ParseAdd("*/*");
        using var res = await http.SendAsync(req, ct);
        var body = (await res.Content.ReadAsStringAsync(ct)).Trim();
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new GarminException("Garmin nimmt den Schlüssel nicht an – er ist abgelaufen, gelöscht oder falsch kopiert. Lege in Garmin Connect einen neuen an.");
        if (!res.IsSuccessStatusCode) throw new GarminException($"Garmin antwortet nicht wie erwartet ({(int)res.StatusCode}). Bitte später noch einmal versuchen.");
        var exp = JwtExp(body) ?? DateTimeOffset.UtcNow.AddHours(23);
        Tokens[cacheKey] = (body, exp);
        return body;
    }

    private static DateTimeOffset? JwtExp(string token)
    {
        try
        {
            var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(part));
            return DateTimeOffset.FromUnixTimeSeconds((long)doc.RootElement.GetProperty("exp").GetDouble());
        }
        catch (Exception e) when (e is IndexOutOfRangeException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static async Task<JsonDocument?> GetAsync(HttpClient http, string token, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new GarminException("Garmin verweigert den Zugriff – der Schlüssel ist vermutlich abgelaufen oder gelöscht.");
        if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new GarminException("Garmin bremst gerade (zu viele Abrufe). Später geht es wieder.");
        if (!res.IsSuccessStatusCode) throw new GarminException($"Garmin antwortet nicht wie erwartet ({(int)res.StatusCode} bei {path.Split('?')[0]}).");
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    /// <summary>Wie <see cref="GetAsync"/>, aber der Text der Antwort (GPX ist XML). Null: gibt es nicht.</summary>
    private static async Task<string?> GetTextAsync(HttpClient http, string token, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new GarminException("Garmin gibt die Strecke nicht heraus (kein Zugriff mit diesem Schlüssel).");
        if (!res.IsSuccessStatusCode) throw new GarminException($"Garmin antwortet nicht wie erwartet ({(int)res.StatusCode} bei {path.Split('?')[0]}).");
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // ---------------- Strecke einer Aktivität ----------------

    /// <summary>Punkte aus einer GPX-Datei (trkpt lat/lon, dazu die Zeit in Unix-Sekunden, wenn angegeben), in der Reihenfolge der Aufzeichnung.</summary>
    public static List<double[]> ParseGpx(string xml)
    {
        var list = new List<double[]>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "trkpt"))
                if (double.TryParse((string?)p.Attribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var la)
                    && double.TryParse((string?)p.Attribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) && Math.Abs(la) <= 90 && Math.Abs(lo) <= 180)
                    list.Add(p.Elements().FirstOrDefault(e => e.Name.LocalName == "time")?.Value is { } tv
                        && DateTimeOffset.TryParse(tv, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                        ? [Math.Round(la, 6), Math.Round(lo, 6), at.ToUnixTimeMilliseconds() / 1000.0]
                        : [Math.Round(la, 6), Math.Round(lo, 6)]);
        }
        catch (System.Xml.XmlException) { }
        return list;
    }

    /// <summary>Punkte aus den Aktivitätsdetails (geoPolylineDTO.polyline: lat/lon, time in Millisekunden).</summary>
    public static List<double[]> ParseDetails(JsonElement root)
    {
        var list = new List<double[]>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("geoPolylineDTO", out var g) && g.ValueKind == JsonValueKind.Object
            && g.TryGetProperty("polyline", out var pl) && pl.ValueKind == JsonValueKind.Array)
            foreach (var p in pl.EnumerateArray())
                if (Number(p, "lat") is double la && Number(p, "lon") is double lo && Math.Abs(la) <= 90 && Math.Abs(lo) <= 180)
                    list.Add(Number(p, "time") is double t && t > 1e11 ? [Math.Round(la, 6), Math.Round(lo, 6), t / 1000] : [Math.Round(la, 6), Math.Round(lo, 6)]);
        return list;
    }

    /// <summary>Höchstens so viele Punkte (gleichmäßig ausgedünnt, Anfang und Ende bleiben).</summary>
    public static List<double[]> Thin(List<double[]> pts, int max = 2000)
    {
        if (pts.Count <= max) return pts;
        var step = (double)(pts.Count - 1) / (max - 1);
        return Enumerable.Range(0, max).Select(i => pts[(int)Math.Round(i * step)]).ToList();
    }

    /// <summary>
    /// Strecke einer Aktivität: einmal bei Garmin geholt (GPX, sonst die Details) und gemerkt. Ohne Strecke (Laufband, Krafttraining)
    /// wird das auch gemerkt und erst nach einem Tag wieder gefragt. Punkte: [lat, lon] oder [lat, lon, Unix-Sekunden].
    /// <paramref name="timed"/>: eine gemerkte Strecke ohne Zeiten (von früher) noch einmal holen - fürs Verorten von Fotos.
    /// </summary>
    public static async Task<List<double[]>> TrackAsync(UserSpace space, HttpClient http, long id, CancellationToken ct, bool timed = false)
    {
        var db = space.Db;
        EnsureActivities(db);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cached = db.Query("SELECT points, fetched FROM coach_garmin_track WHERE id = $i", r => (Points: r.GetString(0), Fetched: r.GetInt64(1)), ("$i", id)).FirstOrDefault();
        var known = cached.Points == null ? null : JsonSerializer.Deserialize<List<double[]>>(cached.Points);
        var stale = timed && known is { Count: > 0 } && !known.Any(p => p.Length >= 3) && now - cached.Fetched >= 600;
        if (!stale && (known is { Count: > 0 } || cached.Points != null && now - cached.Fetched < 86400)) return known ?? [];
        var s = Settings(db);
        if (string.IsNullOrWhiteSpace(s.Key)) throw new GarminException("Noch kein Garmin-Schlüssel eingetragen.");
        var token = await TokenAsync(http, s.Key, ct);
        var pts = await GetTextAsync(http, token, $"/download-service/export/gpx/activity/{id}", ct) is { } gpx ? ParseGpx(gpx) : [];
        if (pts.Count == 0)
            try { using var d = await GetAsync(http, token, $"/activity-service/activity/{id}/details?maxChartSize=10&maxPolylineSize=4000", ct); if (d != null) pts = ParseDetails(d.RootElement); }
            catch (GarminException) when (known != null) { }
        if (stale && pts.Count == 0) return known!;
        pts = Thin(pts);
        db.Exec("INSERT OR REPLACE INTO coach_garmin_track(id, points, fetched) VALUES($i, $p, $f)", ("$i", id), ("$p", JsonSerializer.Serialize(pts)), ("$f", now));
        return pts;
    }

    public static GarminActivity? ActivityById(UserDb db, long id)
    {
        EnsureActivities(db);
        return db.Query("SELECT id, day, name, type, start, duration, distance, kcal FROM coach_garmin_activity WHERE id = $i",
            r => new GarminActivity(r.GetInt64(0), DateOnly.ParseExact(r.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6), r.IsDBNull(7) ? null : r.GetDouble(7)), ("$i", id)).FirstOrDefault();
    }

    // ---------------- Werte tolerant finden ----------------

    private static readonly string[] DateNames = ["calendarDate", "summaryDate", "date", "startDate"];

    /// <summary>Alle Objekte mit einem Tagesdatum (yyyy-MM-dd) - egal, wie tief sie in der Antwort stecken.</summary>
    public static IEnumerable<(DateOnly Day, JsonElement Obj)> DayObjects(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var n in DateNames)
                if (e.TryGetProperty(n, out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: >= 10 } s
                    && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    yield return (day, e);
                    yield break;
                }
            foreach (var p in e.EnumerateObject())
                foreach (var x in DayObjects(p.Value)) yield return x;
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                foreach (var x in DayObjects(item)) yield return x;
    }

    /// <summary>Erste Zahl unter einem dieser Namen (in der Reihenfolge der Namen, auch in verschachtelten Objekten).</summary>
    public static double? Number(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
            if (Find(obj, n, 0) is double v) return v;
        return null;
    }

    /// <summary>Zahl an genau diesem Pfad („movement.steps.value“).</summary>
    public static double? At(JsonElement obj, string path)
    {
        var e = obj;
        foreach (var part in path.Split('.'))
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return null;
        return e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
    }

    private static double? Find(JsonElement e, string name, int depth)
    {
        if (depth > 6) return null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number) return p.Value.GetDouble();
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.Object && Find(p.Value, name, depth + 1) is double v) return v;
        }
        return null;
    }

    /// <summary>Was an einem Tag erkannt wurde.</summary>
    /// <param name="WaterMl">Auf der Uhr eingetragenes Wasser (Hydration)</param>
    /// <param name="WaterAt">Letzter Eintrag dazu, Ortszeit wie von Garmin („2026-10-27T09:12:00.0“)</param>
    public sealed record DayValues(DateOnly Day, int? Steps, double? ActiveKcal, double? DistanceM, int? RestingHr, int? SleepMin,
        double? WeightKg, double? BodyFat, int? Stress, int? BodyBatteryHigh, int? BodyBatteryLow, int? Hrv, int? WaterMl = null, string? WaterAt = null);

    /// <summary>Tageswerte aus den Antworten zusammensuchen (je Tag die erste passende Angabe).</summary>
    public static List<DayValues> Parse(JsonElement? summaries, JsonElement? sleep, JsonElement? weight, JsonElement? hrv, IEnumerable<JsonElement>? hydration = null)
    {
        var map = new SortedDictionary<DateOnly, DayValues>();
        DayValues Get(DateOnly d) => map.TryGetValue(d, out var v) ? v : map[d] = new DayValues(d, null, null, null, null, null, null, null, null, null, null, null);
        // Wasser (Hydration, je Tag eine Antwort): „valueInML“ und „lastEntryTimestampLocal“
        foreach (var hy in hydration ?? [])
            foreach (var (day, o) in DayObjects(hy))
            {
                var v = Get(day);
                var ml = Number(o, "valueInML", "valueInMl", "intakeInML");
                map[day] = v with
                {
                    WaterMl = v.WaterMl ?? (ml is >= 0 and < 20000 ? (int)Math.Round(ml.Value) : null),
                    WaterAt = v.WaterAt ?? (o.TryGetProperty("lastEntryTimestampLocal", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null),
                };
            }
        if (summaries is { } s)
            foreach (var (day, o) in DayObjects(s))
            {
                var v = Get(day);
                map[day] = v with
                {
                    // usersummary v2 verschachtelt („movement.steps.value“), ältere Antworten flach („totalSteps“)
                    Steps = v.Steps ?? Int(At(o, "movement.steps.value") ?? Number(o, "totalSteps", "steps")),
                    ActiveKcal = v.ActiveKcal ?? At(o, "calories.burnedActive") ?? Number(o, "activeKilocalories", "activeCalories", "activeKcal"),
                    DistanceM = v.DistanceM ?? At(o, "movement.steps.distanceInMeters") ?? Number(o, "totalDistanceMeters", "distanceInMeters", "totalDistance", "distance"),
                    RestingHr = v.RestingHr ?? Int(At(o, "heartRate.restingValue") ?? Number(o, "restingHeartRate", "restingHeartRateValue", "restingHr")),
                    Stress = v.Stress ?? Int(At(o, "stress.avgLevel") ?? Number(o, "averageStressLevel", "avgStressLevel", "overallStressLevel", "stressLevel")),
                    BodyBatteryHigh = v.BodyBatteryHigh ?? Int(At(o, "bodyBattery.maxValue") ?? Number(o, "bodyBatteryHighestValue", "highestValue", "bodyBatteryHigh")),
                    BodyBatteryLow = v.BodyBatteryLow ?? Int(At(o, "bodyBattery.minValue") ?? Number(o, "bodyBatteryLowestValue", "lowestValue", "bodyBatteryLow")),
                    // Wasser steht auch in den Tageswerten („hydration.consumedInMl“) - der größere Wert zählt, falls der eigene Abruf fehlt oder hinterherhinkt
                    WaterMl = Int(At(o, "hydration.consumedInMl")) is int sm and >= 0 and < 20000 && sm > (v.WaterMl ?? -1) ? sm : v.WaterMl,
                };
            }
        if (sleep is { } sl)
            foreach (var (day, o) in DayObjects(sl))
            {
                var v = Get(day);
                var sec = Number(o, "sleepTimeSeconds", "sleepTimeInSeconds", "totalSleepSeconds", "sleepDurationInSeconds", "durationInSeconds");
                map[day] = v with { SleepMin = v.SleepMin ?? (sec is double x && x > 0 ? (int)Math.Round(x / 60) : null) };
            }
        if (weight is { } w)
            foreach (var (day, o) in DayObjects(w))
            {
                var v = Get(day);
                // Garmin speichert das Gewicht in Gramm
                var kg = Number(o, "weight", "weightInGrams", "weightKg");
                if (kg is double g && g > 1000) kg = g / 1000;
                map[day] = v with { WeightKg = v.WeightKg ?? (kg is >= 20 and <= 400 ? Math.Round(kg.Value, 1, MidpointRounding.AwayFromZero) : null), BodyFat = v.BodyFat ?? Number(o, "bodyFat", "bodyFatPercentage") };
            }
        if (hrv is { } h)
            foreach (var (day, o) in DayObjects(h))
            {
                var v = Get(day);
                map[day] = v with { Hrv = v.Hrv ?? Int(Number(o, "lastNightAvg", "lastNightAverage", "weeklyAvg", "hrvValue")) };
            }
        return map.Values.ToList();
    }

    private static int? Int(double? v) => v is double x ? (int)Math.Round(x) : null;

    // ---------------- Abholen ----------------

    public sealed record SyncResult(int Days, List<DayValues> Values, Dictionary<string, string> Sources);

    /// <summary>Tage abholen und eintragen (je höchstens sieben Tage pro Abruf). Sources: je Bereich „ok“, „leer“ oder der Fehler.</summary>
    public static async Task<SyncResult> SyncAsync(UserSpace space, HttpClient http, DateOnly from, DateOnly to, CancellationToken ct, bool store = true)
    {
        var s = Settings(space.Db);
        if (string.IsNullOrWhiteSpace(s.Key)) throw new GarminException("Noch kein Garmin-Schlüssel eingetragen.");
        var token = await TokenAsync(http, s.Key, ct);
        var all = new List<DayValues>();
        var sources = new Dictionary<string, string>();
        for (var start = from; start <= to; start = start.AddDays(7))
        {
            var end = start.AddDays(6) > to ? to : start.AddDays(6);
            string a = start.ToString("yyyy-MM-dd"), b = end.ToString("yyyy-MM-dd");
            async Task<JsonDocument?> Try(string name, string path)
            {
                try { var d = await GetAsync(http, token, path, ct); sources[name] = d == null ? "leer" : "ok"; return d; }
                catch (GarminException e) when (name != "Tageswerte") { sources[name] = e.Message; return null; }
                catch (JsonException) { sources[name] = "keine lesbare Antwort"; return null; }
            }
            using var sum = await Try("Tageswerte", $"/usersummary-service/v2/dailysummaries?startDate={a}&endDate={b}");
            using var sleep = await Try("Schlaf", $"/sleep-service/sleep/dailySleepSummariesByDate?startDate={a}&endDate={b}");
            using var weight = await Try("Gewicht", $"/weight-service/weight/range/{a}/{b}?includeAll=true");
            using var hrv = await Try("HRV", $"/hrv-service/hrv/daily/{a}/{b}");
            // Einzelne Aktivitäten (Gehen, Rad, Laufen …) mit Dauer, Strecke und kcal
            using var acts = await Try("Aktivitäten", $"/activitylist-service/activities/search/activities?startDate={a}&endDate={b}&limit=100");
            if (acts != null && store) StoreActivities(space.Db, start, end, ParseActivities(acts.RootElement));
            // Wasser gibt es nur je Tag - darum nur für die letzten sieben Tage des Zeitraums
            var water = new List<JsonDocument>();
            for (var d = start; d <= end; d = d.AddDays(1))
                if (d > to.AddDays(-7) && await Try("Wasser", $"/usersummary-service/usersummary/hydration/daily/{d:yyyy-MM-dd}") is { } wd) water.Add(wd);
            try
            {
                all.AddRange(Parse(sum?.RootElement, sleep?.RootElement, weight?.RootElement, hrv?.RootElement, water.Select(w => w.RootElement)).Where(v => v.Day >= start && v.Day <= end));
            }
            finally { foreach (var w in water) w.Dispose(); }
        }
        if (store)
        {
            foreach (var v in all)
            {
                var day = CoachStore.Day(v.Day);
                if (v.Steps != null || v.ActiveKcal != null || v.DistanceM != null || v.SleepMin != null || v.RestingHr != null)
                    CoachStore.SetActivity(space.Db, new CoachStore.Activity(day, v.Steps is >= 0 and < 200000 ? v.Steps : null, v.ActiveKcal is >= 0 and < 20000 ? v.ActiveKcal : null,
                        v.DistanceM is >= 0 ? v.DistanceM : null, v.SleepMin is >= 0 and <= 1440 ? v.SleepMin : null, v.RestingHr is >= 20 and <= 250 ? v.RestingHr : null, "garmin"));
                if (v.WeightKg != null) CoachStore.SetWeight(space.Db, v.Day, v.WeightKg, v.BodyFat is >= 2 and <= 75 ? v.BodyFat : null, "garmin");
                CoachStore.SetExtra(space.Db, v.Day, "stress", v.Stress);
                CoachStore.SetExtra(space.Db, v.Day, "bodyBatteryHigh", v.BodyBatteryHigh);
                CoachStore.SetExtra(space.Db, v.Day, "bodyBatteryLow", v.BodyBatteryLow);
                CoachStore.SetExtra(space.Db, v.Day, "hrv", v.Hrv);
                CoachStore.SetExtra(space.Db, v.Day, "waterWatchMl", v.WaterMl);
                if (v.WaterAt != null && DateTime.TryParse(v.WaterAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                    CoachStore.SetExtra(space.Db, v.Day, "waterWatchAt", new DateTimeOffset(DmOffice.Dav.Ics.ToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), space.Zone), TimeSpan.Zero).ToUnixTimeSeconds());
            }
            s = Settings(space.Db);
            s.LastSync = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            s.LastError = null;
            Save(space.Db, s);
        }
        return new SyncResult(all.Count, all, sources);
    }

    /// <summary>Eine Aktivität von der Uhr. Start: Ortszeit wie von Garmin („2026-10-27 13:48:20“).</summary>
    public sealed record GarminActivity(long Id, DateOnly Day, string Name, string Type, string Start, double? DurationS, double? DistanceM, double? Kcal);

    /// <summary>Aktivitätenliste (activitylist-service): Liste von Objekten, auch in „activityList“ verpackt.</summary>
    public static List<GarminActivity> ParseActivities(JsonElement root)
    {
        var list = new List<GarminActivity>();
        var items = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("activityList", out var al) && al.ValueKind == JsonValueKind.Array ? al : default;
        if (items.ValueKind != JsonValueKind.Array) return list;
        foreach (var o in items.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object || Number(o, "activityId") is not double id) continue;
            var start = o.TryGetProperty("startTimeLocal", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "";
            if (start.Length < 10 || !DateOnly.TryParseExact(start[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            var name = o.TryGetProperty("activityName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
            var type = o.TryGetProperty("activityType", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("typeKey", out var tk) && tk.ValueKind == JsonValueKind.String
                ? tk.GetString()! : "";
            list.Add(new GarminActivity((long)id, day, name, type, start, Number(o, "duration", "movingDuration"), Number(o, "distance"), Number(o, "calories")));
        }
        return list;
    }

    /// <summary>Aktivitäten der abgeholten Tage ersetzen (bei Garmin gelöschte verschwinden auch hier).</summary>
    public static void StoreActivities(UserDb db, DateOnly from, DateOnly to, List<GarminActivity> list)
    {
        EnsureActivities(db);
        db.Exec("DELETE FROM coach_garmin_activity WHERE day >= $a AND day <= $b", ("$a", CoachStore.Day(from)), ("$b", CoachStore.Day(to)));
        foreach (var x in list.Where(x => x.Day >= from && x.Day <= to))
            db.Exec("INSERT OR REPLACE INTO coach_garmin_activity(id, day, name, type, start, duration, distance, kcal) VALUES($i,$d,$n,$t,$s,$du,$di,$k)",
                ("$i", x.Id), ("$d", CoachStore.Day(x.Day)), ("$n", x.Name), ("$t", x.Type), ("$s", x.Start), ("$du", x.DurationS), ("$di", x.DistanceM), ("$k", x.Kcal));
    }

    public static List<GarminActivity> ActivitiesOf(UserDb db, DateOnly day)
    {
        EnsureActivities(db);
        return db.Query("SELECT id, name, type, start, duration, distance, kcal FROM coach_garmin_activity WHERE day = $d ORDER BY start",
            r => new GarminActivity(r.GetInt64(0), day, r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6)), ("$d", CoachStore.Day(day)));
    }

    private static void EnsureActivities(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS coach_garmin_activity (id INTEGER PRIMARY KEY, day TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL, " +
                "start TEXT NOT NULL, duration REAL NULL, distance REAL NULL, kcal REAL NULL); CREATE INDEX IF NOT EXISTS coach_garmin_activity_day ON coach_garmin_activity(day); " +
                "CREATE TABLE IF NOT EXISTS coach_garmin_track (id INTEGER PRIMARY KEY, points TEXT NOT NULL, fetched INTEGER NOT NULL)");

    /// <summary>Rohantworten eines Tages (zum Nachsehen, welche Felder Garmin liefert) - nur für den Benutzer selbst.</summary>
    public static async Task<Dictionary<string, JsonElement?>> RawAsync(UserSpace space, HttpClient http, DateOnly day, CancellationToken ct)
    {
        var s = Settings(space.Db);
        if (string.IsNullOrWhiteSpace(s.Key)) throw new GarminException("Noch kein Garmin-Schlüssel eingetragen.");
        var token = await TokenAsync(http, s.Key, ct);
        var d = day.ToString("yyyy-MM-dd");
        var result = new Dictionary<string, JsonElement?>();
        foreach (var (name, path) in new[]
        {
            ("dailysummaries", $"/usersummary-service/v2/dailysummaries?startDate={d}&endDate={d}"),
            ("sleep", $"/sleep-service/sleep/dailySleepSummariesByDate?startDate={d}&endDate={d}"),
            ("weight", $"/weight-service/weight/range/{d}/{d}?includeAll=true"),
            ("hrv", $"/hrv-service/hrv/daily/{d}/{d}"),
            ("hydration", $"/usersummary-service/usersummary/hydration/daily/{d}"),
            ("activities", $"/activitylist-service/activities/search/activities?startDate={d}&endDate={d}&limit=100"),
        })
        {
            try { using var doc = await GetAsync(http, token, path, ct); result[name] = doc?.RootElement.Clone(); }
            catch (GarminException) { result[name] = null; }
        }
        return result;
    }

    // ---------------- Im Hintergrund ----------------

    /// <summary>Ein Benutzer: zuerst die letzten 30 Tage, danach alle 15 Minuten heute und alle drei Stunden gestern mit (maxAge: wie alt der letzte Abruf sein darf). Fehler bleiben zum Anzeigen stehen;
    /// eine Woche vor Ablauf erinnert Pukki an einen neuen Schlüssel.</summary>
    public const int BackfillVersion = 3;

    public static async Task RunAsync(UserSpace space, ChatStore? chat, HttpClient http, ServerConfig config, CancellationToken ct, TimeSpan? maxAgeOrNull = null)
    {
        var maxAge = maxAgeOrNull ?? TimeSpan.FromMinutes(14);
        var s = Settings(space.Db);
        if (string.IsNullOrWhiteSpace(s.Key) || !Allowed(space, config)) return;
        var today = CoachStore.Today(space);
        if (s.Expires is { } exp && DateOnly.TryParse(exp, CultureInfo.InvariantCulture, out var until) && until.AddDays(-7) <= today && s.ExpiryReminded != exp
            && chat != null && Pukki.RoomOf(chat, space.User.Id) is { } room)
        {
            chat!.Post(Pukki.Id, room, $"Dein Garmin-Schlüssel für den Coach läuft am {until.ToString("d. MMMM", CultureInfo.GetCultureInfo("de-DE"))} ab. Lege in Garmin Connect unter Einstellungen → Persönlicher API-Schlüssel einen neuen an und trag ihn im Coach unter „Profil und Ziele“ ein.");
            s.ExpiryReminded = exp;
            Save(space.Db, s);
        }
        // 2: Tageswerte im verschachtelten Format (Schritte, aktive kcal, Ruhepuls, Stress, Body Battery) - vorher kamen sie nicht an
        // 3: dazu das Wasser von der Uhr
        var backfilled = s.Backfilled && s.BackfillVersion >= BackfillVersion;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (backfilled && s.LastSync is long last && now - last < (long)maxAge.TotalSeconds) return;
        // Alle 15 Minuten nur heute; gestern (Schlaf der Nacht, nachgetragene Werte) alle drei Stunden mit
        var full = !backfilled || s.LastFull is not long lf || now - lf >= 3 * 3600;
        try
        {
            await SyncAsync(space, http, !backfilled ? today.AddDays(-29) : full ? today.AddDays(-1) : today, today, ct);
            s = Settings(space.Db);
            s.Backfilled = true;
            s.BackfillVersion = BackfillVersion;
            if (full) s.LastFull = now;
            Save(space.Db, s);
        }
        catch (Exception e) when (e is GarminException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            s = Settings(space.Db);
            s.LastError = e is GarminException ? e.Message : "Garmin ist gerade nicht erreichbar.";
            // Erst beim nächsten Takt wieder versuchen
            s.LastSync = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Save(space.Db, s);
        }
    }

    /// <summary>
    /// Vor einem Hinweis oder einer Antwort von Pukki, die von den Werten der Uhr abhängt: den heutigen Stand holen, wenn der
    /// letzte Abruf älter als fünf Minuten ist. Höchstens zehn Sekunden - klappt es nicht, gilt der letzte Stand.
    /// </summary>
    public static async Task RefreshAsync(UserSpace space, HttpClient http, ServerConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Settings(space.Db).Key)) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try { await RunAsync(space, null, http, config, cts.Token, TimeSpan.FromMinutes(5)); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { }
    }
}

/// <summary>Holt für alle mit Garmin-Schlüssel die Tageswerte: alle 15 Minuten heute, alle drei Stunden auch gestern.</summary>
public sealed class CoachGarminService(ChatStore chat, Spaces spaces, IHttpClientFactory http, ServerConfig config, ILogger<CoachGarminService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Release-Liste: in dieser Version nicht freigeschaltet - nichts im Hintergrund
        if (!DmOffice.Core.Features.On(DmOffice.Core.Features.Coach)) return;
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in spaces.All())
            {
                try { await CoachGarmin.RunAsync(space, chat, http.CreateClient(), config, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception e) { log.LogWarning(e, "Garmin fuer {User}", space.User.Login); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stop); } catch (OperationCanceledException) { return; }
        }
    }
}
