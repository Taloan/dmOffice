using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Coach;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Coach: Vorsorge und Impfungen nach Alter und Geschlecht (STIKO 2026, G-BA), fällig/erledigt, Kalender.</summary>
[Collection("server")]
public class CoachChecksTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CoachChecksTests(ChatFixture fx) => _fx = fx;

    private static readonly DateOnly Today = new(2026, 10, 26);
    private static List<string> Keys(CoachProfile p, CheckSettings? s = null) => CoachChecks.For(p, s ?? new CheckSettings(), Today).Select(x => x.Check.Key).ToList();

    [Fact]
    public void Nur_was_zu_Alter_und_Geschlecht_passt()
    {
        // 50-jähriger Mann (geboren 1976): keine Kinder- oder Jugendsachen, kein 18–34, aber Darm, Prostata, Haut, Check-up ab 35
        var man50 = Keys(new CoachProfile { Birth = new DateOnly(1976, 3, 1), Sex = "m" });
        Assert.Contains("colon", man50);
        Assert.Contains("prostate", man50);
        Assert.Contains("checkup35", man50);
        Assert.Contains("mmr", man50);   // nach 1970 geboren
        Assert.DoesNotContain("checkup18", man50);
        Assert.DoesNotContain("cervix20", man50);
        Assert.DoesNotContain("mammo", man50);
        Assert.DoesNotContain("flu", man50);   // erst ab 60
        Assert.DoesNotContain("aaa", man50);   // erst ab 65

        // 68-jährige Frau: Impfungen ab 60, Mammographie bis 75, kein MMR (vor 1970 geboren), keine Männersachen, kein RSV (ab 75)
        var woman68 = Keys(new CoachProfile { Birth = new DateOnly(1958, 5, 1), Sex = "f" });
        Assert.Contains("flu", woman68);
        Assert.Contains("zoster", woman68);
        Assert.Contains("pneumo", woman68);
        Assert.Contains("mammo", woman68);
        Assert.DoesNotContain("mmr", woman68);
        Assert.DoesNotContain("prostate", woman68);
        Assert.DoesNotContain("rsv", woman68);

        // 25-Jährige: Check-up einmal, Abstrich jährlich, Tetanus - nichts ab 35
        var young = Keys(new CoachProfile { Birth = new DateOnly(2001, 1, 1), Sex = "f" });
        Assert.Equal(["td", "mmr", "checkup18", "cervix20"], young);
        // Ohne Geburtsdatum nichts
        Assert.Empty(Keys(new CoachProfile { Sex = "m" }));
    }

    [Fact]
    public void Faellig_erledigt_und_Herbstimpfung()
    {
        var p = new CoachProfile { Birth = new DateOnly(1958, 5, 1), Sex = "m" };
        var s = new CheckSettings();
        s.Items["skin"] = new CheckState { Done = "2025-03-01" };      // 2 Jahre: nächste März 2027
        s.Items["colon"] = new CheckState { Done = "2024-01-10" };     // 2 Jahre: fällig seit Januar 2026
        s.Items["flu"] = new CheckState { Done = "2025-11-02" };       // Herbst: nächste ab September 2026 - fällig
        s.Items["pneumo"] = new CheckState { Done = "2020-01-01" };    // einmalig: erledigt
        s.Items["prostate"] = new CheckState { Skip = true };
        var st = CoachChecks.For(p, s, Today).ToDictionary(x => x.Check.Key);
        Assert.Equal(("ok", "2027-03-01"), (st["skin"].State, st["skin"].Next));
        Assert.Equal("due", st["colon"].State);
        Assert.Equal(("due", "2026-09-01"), (st["flu"].State, st["flu"].Next));
        Assert.Equal("done", st["pneumo"].State);
        Assert.Equal("skip", st["prostate"].State);
        Assert.Equal("due", st["aaa"].State);   // nie eingetragen

        s.Items["flu"] = new CheckState { Done = "2026-10-01" };
        Assert.Equal(("ok", "2027-09-01"), (CoachChecks.For(p, s, Today).Single(x => x.Check.Key == "flu").State, CoachChecks.For(p, s, Today).Single(x => x.Check.Key == "flu").Next));
    }

    [Fact]
    public async Task Eintragen_und_Erinnerung_im_Kalender()
    {
        var c = _fx.As("cleo");
        await c.PutAsJsonAsync("/api/coach/profile", new
        {
            birth = "1970-04-01", sex = "f", heightCm = 170, weightKg = 70, pal = 1.4, goal = "keep", diet = "dge", stepMode = "fixed", stepGoal = 7000,
            allergens = Array.Empty<string>(), intolerances = Array.Empty<string>(), dislikes = Array.Empty<string>(),
        });
        var v = await c.GetFromJsonAsync<JsonElement>("/api/coach/checks");
        Assert.True(v.GetProperty("complete").GetBoolean());
        Assert.Contains(v.GetProperty("items").EnumerateArray(), i => i.GetProperty("key").GetString() == "mammo" && i.GetProperty("state").GetString() == "due");

        var r = await (await c.PutAsJsonAsync("/api/coach/checks/mammo", new { done = DateTime.Today.ToString("yyyy-MM-dd") })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", r.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "mammo").GetProperty("state").GetString());
        Assert.False((await c.PutAsJsonAsync("/api/coach/checks/mammo", new { done = "2999-01-01" })).IsSuccessStatusCode);

        var calRes = await c.PostAsync("/api/coach/checks/skin/calendar", null);
        Assert.True(calRes.IsSuccessStatusCode, await calRes.Content.ReadAsStringAsync());
        var cal = await calRes.Content.ReadFromJsonAsync<JsonElement>();
        var day = DateOnly.Parse(cal.GetProperty("day").GetString()!);
        Assert.True(day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
        var space = _fx.Space("cleo");
        var cals = PimStore.Collections(space.Db, PimStore.Calendar).Select(x => x.Id).ToList();
        var events = CalendarModel.Events(space, day.ToDateTime(TimeOnly.MinValue).AddDays(-1), day.ToDateTime(TimeOnly.MinValue).AddDays(2), cals);
        Assert.Contains(events, e => e.Title == "Termin vereinbaren: Hautkrebs-Screening");

        // Pukki-Knöpfe: Schon erledigt, Nicht für mich
        Assert.Contains("erledigt", CoachChecks.Act(space, "check-done", "skin"));
        CoachChecks.Act(space, "check-skip", "aaa");
        Assert.True(CoachChecks.Settings(space.Db).Items["aaa"].Skip);
    }

    [Fact]
    public void Hinweise_aus_dem_Tagebuch()
    {
        var space = _fx.Space("dora");
        var today = CoachStore.Today(space);
        // Sechs Tage nur Salamibrot und Kaffee: keine Hülsenfrüchte, keine Nüsse, kein Fisch, viel Wurst
        for (int i = 1; i <= 6; i++)
        {
            CoachStore.AddQuick(space.Db, today.AddDays(-i), "breakfast", "Salamibrot", new Nutrients(350, 15, 18, 30, 3));
            CoachStore.AddQuick(space.Db, today.AddDays(-i), "snack", "Kaffee mit Milch", new Nutrients(30, 2, 1, 3, 0));
        }
        var keys = CoachInsights.For(space).Where(x => x.Kind == "diary").Select(x => x.Key).ToList();
        Assert.Contains("legumes", keys);
        Assert.Contains("nuts", keys);
        Assert.Contains("fish", keys);
        Assert.Contains("processed", keys);
        Assert.Contains("fiber", keys);
        Assert.Contains(CoachInsights.For(space), x => x.Key == "blood" && x.Kind == "know");
    }
}
