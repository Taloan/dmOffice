using DmOffice.Chat;
using DmOffice.Coach;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Pukki als Coach: Erinnerungen ans Trinken, an Bewegung (mit Wetter und Kalender) und der Wochenrückblick.</summary>
public class CoachNudgeRuleTests
{
    private static DateTime At(int h, int m = 0) => new(2026, 10, 27, h, m, 0);

    [Fact]
    public void Trinken_nur_wenn_deutlich_hinter_dem_Tagesverlauf()
    {
        var st = new CoachNudges.State();
        // 15 Uhr: Soll etwa 58 % von 2000 ml = 1170 ml
        Assert.Contains("bis 15 Uhr wären etwa 1,25 l", CoachNudges.Water(500, 2000, At(15), st, null));
        Assert.Null(CoachNudges.Water(900, 2000, At(15), st, null));                 // knapp dahinter: keine Erinnerung
        Assert.Null(CoachNudges.Water(500, 2000, At(15), st, At(14, 30)));           // eben erst getrunken
        Assert.Null(CoachNudges.Water(0, 2000, At(9), st, null));                    // zu früh
        Assert.Null(CoachNudges.Water(0, 2000, At(21), st, null));                   // zu spät
        Assert.Null(CoachNudges.Water(500, 2000, At(15), new CoachNudges.State { Water = 3 }, null));   // höchstens dreimal
        // 1-l-Flaschen: erst bei drei Vierteln Rückstand
        Assert.Null(CoachNudges.Water(500, 2000, At(15), st, null, 1000));
        Assert.NotNull(CoachNudges.Water(300, 2000, At(15), st, null, 1000));
    }

    [Fact]
    public void Bewegung_mit_trockener_freier_Stunde_und_Sonnenuntergang()
    {
        var weather = Enumerable.Range(0, 24).Select(h => new CoachNudges.WeatherHour(h, 14, h is 15 ? 80 : 10)).ToList();
        var busy = new List<CoachNudges.Busy> { new(new TimeOnly(16, 0), new TimeOnly(17, 0)), new(new TimeOnly(19, 0), new TimeOnly(20, 0)) };
        var text = CoachNudges.Move(3500, 10000, At(14, 40), weather, new TimeOnly(17, 52), busy);
        // 15 Uhr regnet es, 16 Uhr ist ein Termin - also 17 Uhr; der nächste Termin ist um 19 Uhr
        Assert.Equal("Dir fehlen heute noch 6.500 Schritte – etwa 65 Minuten zu Fuß. Ab 17 Uhr ist es trocken bei 14 °C, Sonnenuntergang 17:52 – und bis 19:00 hast du keinen Termin. 🚶", text);
        // Ohne Wetter nur die Schritte
        Assert.StartsWith("Dir fehlen heute noch 6.500 Schritte", CoachNudges.Move(3500, 10000, At(14, 40), null, null, []));
        // Alles nass bis zum Sonnenuntergang
        var rain = Enumerable.Range(0, 24).Select(h => new CoachNudges.WeatherHour(h, 9, 90)).ToList();
        Assert.Contains("eher nass", CoachNudges.Move(3500, 10000, At(14, 40), rain, new TimeOnly(17, 52), []));
        // Gut im Plan, fast am Ziel, zu früh oder zu spät: keine Erinnerung
        Assert.Null(CoachNudges.Move(6000, 10000, At(14, 40), weather, null, []));
        Assert.Null(CoachNudges.Move(9000, 10000, At(18, 30), weather, null, []));
        Assert.Null(CoachNudges.Move(0, 10000, At(11), weather, null, []));
        Assert.Null(CoachNudges.Move(0, 10000, At(19, 30), weather, null, []));
    }
}

[Collection("server")]
public class CoachNudgeTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CoachNudgeTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Pukki_erinnert_nur_eingeschaltet_und_einmal()
    {
        var services = _fx.Factory.Services;
        var chat = services.GetRequiredService<ChatStore>();
        var http = services.GetRequiredService<IHttpClientFactory>();
        var space = _fx.Space("ben");
        Pukki.Ensure(chat, space.User.Id);
        var room = chat.DirectRoom(space.User.Id, Pukki.Id)!.Value;
        var today = CoachStore.Today(space);
        // 15 Uhr Ortszeit heute
        var at = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(new TimeOnly(15, 0)), space.Zone);
        CoachStore.AddWater(space.Db, today.AddDays(-1), 1500);   // trägt Wasser ein
        int Count() => chat.Messages(space.User.Id, room, limit: 50).Count(m => m.Sender == Pukki.Id && m.Text?.Contains("etwas zu trinken") == true);

        // Aus: nichts
        await CoachNudges.RunAsync(services, space, chat, http, at, default);
        Assert.Equal(0, Count());
        // Ein: einmal, beim nächsten Durchgang nicht gleich wieder
        CoachStore.SaveProfile(space.Db, new CoachProfile { Nudges = true, NudgeMove = false, NudgeWeek = false });
        await CoachNudges.RunAsync(services, space, chat, http, at, default);
        Assert.Equal(1, Count());
        await CoachNudges.RunAsync(services, space, chat, http, at.AddMinutes(5), default);
        Assert.Equal(1, Count());
        // Ort aus der App: gerundet gemerkt
        var c = _fx.As("ben");
        (await c.PutAsync("/api/coach/location", System.Net.Http.Json.JsonContent.Create(new { lat = 48.137154, lon = 11.576124 }))).EnsureSuccessStatusCode();
        Assert.Equal((48.14, 11.58), CoachNudges.Where(space) is (double la, double lo) ? (la, lo) : (0, 0));
    }
}
