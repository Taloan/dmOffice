using System.Net;
using System.Text;
using System.Text.Json;
using DmOffice.Coach;

namespace DmOffice.Tests;

/// <summary>Garmin Personal API: Werte tolerant erkennen, Schlüssel gegen Token tauschen, abholen und eintragen.</summary>
[Collection("server")]
public class CoachGarminTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CoachGarminTests(ChatFixture fx) => _fx = fx;

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Tageswerte_im_verschachtelten_Format_von_usersummary_v2()
    {
        // Aufbau wie die echte Antwort (Werte erfunden): Schritte, Kalorien, Puls, Stress, Body Battery stecken in Unterobjekten
        var sum = J("""
            {"data":[{"calendarDate":"2026-10-25","source":"garmin","movement":{"steps":{"goal":5660,"value":2391,"distanceInMeters":2078.0},"floorsAscended":{"value":2}},
              "calories":{"burnedResting":2521,"burnedActive":156,"burnedTotal":2677},"heartRate":{"minValue":60,"maxValue":109,"restingValue":68},
              "stress":{"avgLevel":49,"maxLevel":93},"bodyBattery":{"minValue":5,"maxValue":18,"latestValue":5}}]}
            """);
        var v = Assert.Single(CoachGarmin.Parse(sum, null, null, null));
        Assert.Equal(2391, v.Steps);
        Assert.Equal(156, v.ActiveKcal);
        Assert.Equal(2078, v.DistanceM);
        Assert.Equal(68, v.RestingHr);
        Assert.Equal(49, v.Stress);
        Assert.Equal(18, v.BodyBatteryHigh);
        Assert.Equal(5, v.BodyBatteryLow);
    }

    [Fact]
    public void Werte_tolerant_erkennen()
    {
        // Form wie in Garmins eigenem Client-Test, dazu übliche Feldnamen aus Garmin Connect
        var sum = J("""{"data":[{"calendarDate":"2026-10-25","movement":{"steps":8123,"distanceInMeters":6100},"calories":{"activeKilocalories":512},"heartRate":{"restingHeartRate":54},"stress":{"averageStressLevel":31},"bodyBattery":{"highestValue":88,"lowestValue":12}}]}""");
        var sleep = J("""[{"calendarDate":"2026-10-25","sleepTimeSeconds":26100}]""");
        var weight = J("""{"dailyWeightSummaries":[{"summaryDate":"2026-10-25","latestWeight":{"weight":82460.0,"bodyFat":21.5}}]}""");
        var hrv = J("""{"hrvSummaries":[{"calendarDate":"2026-10-25","lastNightAvg":47}]}""");
        var v = Assert.Single(CoachGarmin.Parse(sum, sleep, weight, hrv));
        Assert.Equal(new DateOnly(2026, 10, 25), v.Day);
        Assert.Equal(8123, v.Steps);
        Assert.Equal(6100, v.DistanceM);
        Assert.Equal(512, v.ActiveKcal);
        Assert.Equal(54, v.RestingHr);
        Assert.Equal(435, v.SleepMin);
        Assert.Equal(82.5, v.WeightKg);
        Assert.Equal(21.5, v.BodyFat);
        Assert.Equal(31, v.Stress);
        Assert.Equal(88, v.BodyBatteryHigh);
        Assert.Equal(12, v.BodyBatteryLow);
        Assert.Equal(47, v.Hrv);
    }

    [Fact]
    public void Aktivitaeten_von_der_Uhr()
    {
        var acts = CoachGarmin.ParseActivities(J("""[{"activityId":111,"activityName":"Vöhringen Gehen","startTimeLocal":"2026-10-27 13:02:10","activityType":{"typeKey":"walking"},"duration":3757.0,"distance":4390.0,"calories":312.0},{"activityId":112,"activityName":"Vöhringen Gehen","startTimeLocal":"2026-10-27 15:40:00","activityType":{"typeKey":"walking"},"duration":1890.0,"distance":2580.0,"calories":146.0}]"""));
        Assert.Equal(2, acts.Count);
        Assert.Equal(("walking", 4390.0, 312.0), (acts[0].Type, acts[0].DistanceM!.Value, acts[0].Kcal!.Value));
        var db = _fx.Space("dora").Db;
        var day = new DateOnly(2026, 10, 27);
        CoachGarmin.StoreActivities(db, day, day, acts);
        Assert.Equal(2, CoachGarmin.ActivitiesOf(db, day).Count);
        // Bei Garmin gelöscht: beim nächsten Abholen auch hier weg
        CoachGarmin.StoreActivities(db, day, day, acts.Take(1).ToList());
        Assert.Single(CoachGarmin.ActivitiesOf(db, day));
    }

    [Fact]
    public void Strecke_aus_GPX_und_Details()
    {
        var gpx = """<?xml version="1.0"?><gpx xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg><trkpt lat="48.29" lon="10.08"><time>2026-10-27T12:10:00Z</time></trkpt><trkpt lat="48.291" lon="10.081"/></trkseg></trk></gpx>""";
        Assert.Equal(2, CoachGarmin.ParseGpx(gpx).Count);
        Assert.Equal(48.29, CoachGarmin.ParseGpx(gpx)[0][0]);
        Assert.Empty(CoachGarmin.ParseGpx("kein xml"));
        var det = CoachGarmin.ParseDetails(J("""{"geoPolylineDTO":{"polyline":[{"lat":48.29,"lon":10.08,"time":1},{"lat":48.3,"lon":10.09,"time":2}]}}"""));
        Assert.Equal(2, det.Count);
        var many = Enumerable.Range(0, 5000).Select(i => new[] { 48.0 + i * 1e-5, 10.0 }).ToList();
        var thin = CoachGarmin.Thin(many);
        Assert.Equal(2000, thin.Count);
        Assert.Same(many[^1], thin[^1]);
    }

    [Fact]
    public void Wasser_von_der_Uhr_zaehlt_mit()
    {
        var hy = J("""{"userId":1,"calendarDate":"2026-10-27","valueInML":1000.0,"goalInML":2500.0,"lastEntryTimestampLocal":"2026-10-27T09:12:00.0"}""");
        var v = Assert.Single(CoachGarmin.Parse(null, null, null, null, [hy]));
        Assert.Equal((1000, "2026-10-27T09:12:00.0"), (v.WaterMl, v.WaterAt));
        // Auch aus den Tageswerten („hydration.consumedInMl“) - ohne eigenen Abruf oder wenn der weniger meldet
        var sum = J("""{"data":[{"calendarDate":"2026-10-27","hydration":{"goalInMl":3200,"consumedInMl":2000}}]}""");
        Assert.Equal(2000, Assert.Single(CoachGarmin.Parse(sum, null, null, null)).WaterMl);
        Assert.Equal(2000, Assert.Single(CoachGarmin.Parse(sum, null, null, null, [hy])).WaterMl);
        // Getrennt gespeichert, in der Summe des Tages und der Wochenstatistik mitgezählt
        var db = _fx.Space("dora").Db;
        var day = new DateOnly(2026, 10, 27);
        CoachStore.AddWater(db, day, 250);
        CoachStore.SetExtra(db, day, "waterWatchMl", 1000);
        Assert.Equal(1250, CoachStore.WaterMl(db, day));
        Assert.Single(CoachStore.WaterOf(db, day));   // „Letztes zurücknehmen“ betrifft nur die eigenen
        Assert.Equal(1250, CoachStore.Stats(db, day, day).Single().Water);
    }

    private sealed class FakeGarmin : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.PathAndQuery;
            Calls.Add(req.Method + " " + path + " " + req.Headers.Authorization);
            if (path.Contains("/token"))
            {
                if (req.Headers.Authorization?.ToString() != "ApiKey richtiger-schluessel-123") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds()}}}")).TrimEnd('=');
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"eyJhbGciOiJub25lIn0.{payload}.sig") });
            }
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var body = path.Contains("dailysummaries") ? "{\"data\":[{\"calendarDate\":\"" + today + "\",\"movement\":{\"steps\":4321}}]}"
                : path.Contains("weight") ? "{\"dailyWeightSummaries\":[{\"summaryDate\":\"" + today + "\",\"latestWeight\":{\"weight\":80100}}]}"
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Abholen_und_eintragen()
    {
        var space = _fx.Space("dora");
        var fake = new FakeGarmin();
        var http = new HttpClient(fake);
        // Falscher Schlüssel: verständliche Meldung
        var ex = await Assert.ThrowsAsync<CoachGarmin.GarminException>(() => CoachGarmin.TokenAsync(http, "falscher-schluessel-123", default));
        Assert.Contains("nimmt den Schlüssel nicht an", ex.Message);

        CoachGarmin.Save(space.Db, new GarminSettings { Key = "richtiger-schluessel-123" });
        var today = CoachStore.Today(space);
        var r = await CoachGarmin.SyncAsync(space, http, today.AddDays(-2), today, default);
        Assert.Equal("ok", r.Sources["Tageswerte"]);
        Assert.Equal(4321, CoachStore.ActivityOf(space.Db, today)?.Steps);
        Assert.Equal("garmin", CoachStore.ActivityOf(space.Db, today)?.Source);
        Assert.Equal(80.1, CoachStore.Weight(space.Db, today));
        // Mit Bearer-Token abgefragt, Token zwischengespeichert (nur einmal getauscht)
        Assert.Single(fake.Calls, c => c.Contains("/token") && c.Contains("richtiger"));
        Assert.Contains(fake.Calls, c => c.Contains("dailysummaries") && c.EndsWith("sig") && c.Contains("Bearer"));
        Assert.NotNull(CoachGarmin.Settings(space.Db).LastSync);
    }
}
