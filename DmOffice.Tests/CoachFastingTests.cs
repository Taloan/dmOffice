using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Coach;

namespace DmOffice.Tests;

/// <summary>Coach Stufe 2: Fasten-Tracker (16:8 u. a. mit Timer, 5:2 mit Kalorienziel, Leitplanken, Serie, Erinnerung).</summary>
[Collection("server")]
public class CoachFastingTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CoachFastingTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object Profile(bool pregnant = false, string birth = "1980-06-15", double weight = 80) => new
    {
        birth, sex = "f", heightCm = 170, weightKg = weight, pal = 1.4, goal = "keep", diet = "dge", stepMode = "fixed", stepGoal = 7000, pregnant,
        allergens = Array.Empty<string>(), intolerances = Array.Empty<string>(), dislikes = Array.Empty<string>(),
    };

    [Fact]
    public async Task Einschalten_starten_beenden_und_Verlauf()
    {
        var c = _fx.As("cleo");
        await Json(await c.PutAsJsonAsync("/api/coach/profile", Profile()));
        var v = await Json(await c.GetAsync("/api/coach/fasting"));
        Assert.False(v.GetProperty("settings").GetProperty("enabled").GetBoolean());
        // Ausgeschaltet: kein Start
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/coach/fasting/start", new { })).StatusCode);

        v = await Json(await c.PutAsJsonAsync("/api/coach/fasting", new { enabled = true, plan = "16:8", customHours = 16, eatFrom = "12:00", remind = false }));
        Assert.Equal(16, v.GetProperty("goalHours").GetDouble());

        // Vor 17 Stunden begonnen (nachgetragen), jetzt beendet: Ziel erreicht
        v = await Json(await c.PostAsJsonAsync("/api/coach/fasting/start", new { at = DateTime.UtcNow.AddHours(-17) }));
        Assert.True(v.GetProperty("current").GetProperty("hours").GetDouble() >= 16.9);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/coach/fasting/start", new { })).StatusCode);
        v = await Json(await c.PostAsJsonAsync("/api/coach/fasting/stop", new { }));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("current").ValueKind);
        var last = v.GetProperty("history")[0];
        Assert.True(last.GetProperty("reached").GetBoolean());
        Assert.Equal(1, v.GetProperty("stats").GetProperty("streak").GetInt32());

        // Ändern und Löschen
        var id = last.GetProperty("id").GetInt64();
        v = await Json(await c.PutAsJsonAsync($"/api/coach/fasting/{id}", new { start = DateTime.UtcNow.AddHours(-10), end = DateTime.UtcNow }));
        Assert.False(v.GetProperty("history")[0].GetProperty("reached").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/coach/fasting/{id}", new { start = DateTime.UtcNow, end = DateTime.UtcNow.AddHours(-1) })).StatusCode);
        v = await Json(await c.DeleteAsync($"/api/coach/fasting/{id}"));
        Assert.Equal(0, v.GetProperty("history").GetArrayLength());

        // Nachtragen höchstens 48 Stunden zurück
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/coach/fasting/start", new { at = DateTime.UtcNow.AddDays(-3) })).StatusCode);
        await c.PutAsJsonAsync("/api/coach/fasting", new { enabled = false, plan = "16:8", customHours = 16, eatFrom = "12:00", remind = false });
    }

    [Fact]
    public async Task Leitplanken_Schwangerschaft_Minderjaehrig_Untergewicht()
    {
        var c = _fx.As("dora");
        foreach (var p in new[] { Profile(pregnant: true), Profile(birth: "2012-01-01"), Profile(weight: 50) })
        {
            await Json(await c.PutAsJsonAsync("/api/coach/profile", p));
            var r = await c.PutAsJsonAsync("/api/coach/fasting", new { enabled = true, plan = "16:8", customHours = 16, eatFrom = "12:00", remind = false });
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.NotEqual(JsonValueKind.Null, (await Json(await c.GetAsync("/api/coach/fasting"))).GetProperty("notAllowed").ValueKind);
        }
        await Json(await c.PutAsJsonAsync("/api/coach/profile", Profile()));
        Assert.Equal(JsonValueKind.Null, (await Json(await c.GetAsync("/api/coach/fasting"))).GetProperty("notAllowed").ValueKind);
        // 5:2 braucht ein bis drei Fastentage und hat keinen Timer
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/coach/fasting", new { enabled = true, plan = "5:2", customHours = 16, eatFrom = "12:00", fastDays = Array.Empty<int>(), remind = false })).StatusCode);
        var v = await Json(await c.PutAsJsonAsync("/api/coach/fasting", new { enabled = true, plan = "5:2", customHours = 16, eatFrom = "12:00", fastDays = new[] { 0, 1, 2, 3, 4, 5, 6 }.Take(2), remind = false }));
        Assert.Equal(500, v.GetProperty("lowDay").GetProperty("kcal").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/coach/fasting/start", new { })).StatusCode);
        await c.PutAsJsonAsync("/api/coach/fasting", new { enabled = false, plan = "16:8", customHours = 16, eatFrom = "12:00", remind = false });
    }

    [Fact]
    public void Serie_Plan_und_Fastentag()
    {
        var zone = TimeZoneInfo.Utc;
        var today = new DateOnly(2026, 10, 26);
        DateTime At(int day, int hour) => new(2026, 10, day, hour, 0, 0, DateTimeKind.Utc);
        var done = new[]
        {
            new CoachFasting.Fast(1, At(25, 20), At(26, 12), 16, false),  // 16 h, heute beendet
            new CoachFasting.Fast(2, At(24, 20), At(25, 13), 16, false),  // 17 h
            new CoachFasting.Fast(3, At(23, 20), At(24, 8), 16, false),   // 12 h: Ziel verfehlt
            new CoachFasting.Fast(4, At(22, 20), At(23, 13), 16, false),
        };
        Assert.Equal(2, CoachFasting.Streak(done, zone, today));
        // Heute noch nichts beendet: Serie bis gestern zählt weiter
        Assert.Equal(1, CoachFasting.Streak(done.Skip(1), zone, today));

        var s = new FastingSettings { Enabled = true, Plan = "18:6", EatFrom = "11:30" };
        Assert.Equal(new DateTime(2026, 10, 26, 17, 30, 0), CoachFasting.PlannedStart(s, today));
        Assert.Equal(20, CoachFasting.GoalHours(new FastingSettings { Plan = "custom", CustomHours = 20 }));
        Assert.Equal(23, CoachFasting.GoalHours(new FastingSettings { Plan = "custom", CustomHours = 30 }));

        var five = new FastingSettings { Enabled = true, Plan = "5:2", FastDays = [1, 4] };
        Assert.Equal((true, 600), CoachFasting.LowDay(five, new CoachProfile { Sex = "m" }, today));          // Montag
        Assert.Equal((false, 600), CoachFasting.LowDay(five, new CoachProfile { Sex = "m" }, today.AddDays(1)));
    }
}
