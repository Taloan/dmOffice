using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DmOffice.Tests;

/// <summary>Coach: Schritte aus mehreren Quellen (Handy über Health Connect, Garmin) - der höhere Tageswert zählt.</summary>
[Collection("server")]
public class CoachActivityTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CoachActivityTests(ChatFixture fx) => _fx = fx;

    private async Task<int> Steps(HttpClient c, string date)
    {
        var r = await c.GetAsync($"/api/coach/day?date={date}");
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("steps").GetProperty("steps").GetInt32();
    }

    [Fact]
    public async Task Handy_ueberschreibt_die_Uhr_nicht_mit_weniger_Schritten()
    {
        var c = _fx.As("anna");
        var date = "2026-10-26";
        async Task Post(string source, int steps, double kcal) =>
            (await c.PostAsJsonAsync("/api/coach/activity", new { source, days = new[] { new { date, steps, activeKcal = kcal } } })).EnsureSuccessStatusCode();
        await Post("garmin", 4900, 210);
        // Das Handy kennt nur seine eigenen 9 Schritte
        await Post("healthconnect", 9, 1);
        Assert.Equal(4900, await Steps(c, date));
        // Später am Tag mehr: der neue, höhere Wert gilt
        await Post("healthconnect", 6100, 260);
        Assert.Equal(6100, await Steps(c, date));
    }

    [Fact]
    public async Task Tagesdaten_mit_Uhr_Zusatzwerten_und_Schnitt()
    {
        var c = _fx.As("anna");
        var db = _fx.Space("anna").Db;
        var day = new DateOnly(2026, 9, 10);
        DmOffice.Coach.CoachStore.SetExtra(db, day, "stress", 32);
        DmOffice.Coach.CoachStore.SetExtra(db, day, "bodyBatteryLow", 25);
        DmOffice.Coach.CoachStore.SetExtra(db, day, "bodyBatteryHigh", 90);
        DmOffice.Coach.CoachStore.SetExtra(db, day, "hrv", 48);
        for (var i = 1; i <= 3; i++)
            (await c.PostAsJsonAsync("/api/coach/activity", new { source = "garmin", days = new[] { new { date = day.AddDays(-i).ToString("yyyy-MM-dd"), sleepMin = 400 + i * 10, restingHr = 60 } } })).EnsureSuccessStatusCode();
        var r = await c.GetAsync("/api/coach/day?date=2026-09-10");
        r.EnsureSuccessStatusCode();
        var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(32, d.GetProperty("extra").GetProperty("stress").GetDouble());
        Assert.Equal(25, d.GetProperty("extra").GetProperty("bodyBatteryLow").GetDouble());
        Assert.Equal(90, d.GetProperty("extra").GetProperty("bodyBatteryHigh").GetDouble());
        Assert.Equal(48, d.GetProperty("extra").GetProperty("hrv").GetDouble());
        Assert.Equal(420, d.GetProperty("avg7").GetProperty("sleepMin").GetDouble());
        Assert.Equal(60, d.GetProperty("avg7").GetProperty("restingHr").GetDouble());
        // Fasten nur, wenn eingeschaltet
        Assert.Equal(JsonValueKind.Null, d.GetProperty("fasting").ValueKind);
    }

    [Fact]
    public void Nudeln_findet_Teigwaren()
    {
        var hits = DmOffice.Coach.Bls.Search("nudeln", 10);
        Assert.Contains(hits.Take(5), e => e.Name.StartsWith("Teigwaren", StringComparison.Ordinal) || e.Name.StartsWith("Eierteigwaren", StringComparison.Ordinal));
    }

    [Fact]
    public void Pruefung_gegen_Allergien_und_Unvertraeglichkeiten()
    {
        var p = new DmOffice.Coach.CoachProfile { Allergens = ["nuts"], Intolerances = ["lactose", "gluten"] };
        var w = DmOffice.Coach.CoachPlan.Check("Mini Pralinen", "Zucker, Vollmilchpulver, Haselnüsse, Weizenmehl, Butterreinfett", ["milk", "gluten"], p);
        Assert.Contains(w, x => x.Kind == "allergen" && x.Key == "nuts" && x.Found.Contains("haselnuss"));
        Assert.Contains(w, x => x.Key == "lactose" && x.Known);
        Assert.Contains(w, x => x.Key == "gluten" && x.Found.Contains("weizen"));
        // Laktosefrei: keine Warnung
        Assert.DoesNotContain(DmOffice.Coach.CoachPlan.Check("Milch laktosefrei", null, [], p), x => x.Key == "lactose");
    }
}
