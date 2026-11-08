using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Pukki rechnet nach der Zeitzone des Geräts, spricht die Sprache der Oberfläche und kennzeichnet Antworten der KI.</summary>
[Collection("server")]
public class PukkiLocaleTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiLocaleTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<long> PukkiRoom(string login) =>
        (await Json(await _fx.As(login).GetAsync("/api/chat/rooms"))).EnumerateArray()
            .Single(r => r.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("id").GetString() == Pukki.Id)).GetProperty("id").GetInt64();

    private async Task<List<JsonElement>> Messages(string login, long room) =>
        (await Json(await _fx.As(login).GetAsync($"/api/chat/rooms/{room}/messages"))).EnumerateArray().ToList();

    private async Task<string> Ask(string login, long room, object body)
    {
        var before = (await Messages(login, room)).Count;
        await Json(await _fx.As(login).PostAsJsonAsync($"/api/chat/rooms/{room}/messages", body));
        for (int i = 0; i < 50; i++)
        {
            var reply = (await Messages(login, room)).Skip(before + 1).FirstOrDefault(m => m.GetProperty("sender").GetString() == Pukki.Id);
            if (reply.ValueKind != JsonValueKind.Undefined) return reply.GetProperty("text").GetString()!;
            await Task.Delay(100);
        }
        throw new TimeoutException("Pukki hat nicht geantwortet.");
    }

    [Fact]
    public async Task Termin_gilt_in_der_Zeitzone_des_Geraets_und_die_Antwort_ist_englisch()
    {
        var room = await PukkiRoom("anna");
        var space = _fx.Space("anna");
        var title = "Dentist" + Guid.NewGuid().ToString("N")[..4];

        var reply = await Ask("anna", room, new { text = $"appointment tomorrow 3pm {title}", zone = "America/New_York", lang = "en" });

        Assert.Contains("Added: tomorrow, 15:00", reply);
        Assert.Contains("in calendar", reply);
        Assert.DoesNotContain("Eingetragen", reply);
        var ny = Dav.Ics.Zone("America/New_York")!;
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ny)).AddDays(1);
        using (UserSpace.UseZone(ny))
        {
            // Das Wort „appointment“ ist der Befehl, nicht Teil des Titels
            var ev = Pukki.DayEvents(_fx.Factory.Services, space, tomorrow).Single(e => e.Title == title);
            Assert.Equal(new TimeOnly(15, 0), ev.Start);
        }
        Assert.NotEqual(ny.BaseUtcOffset, space.Zone.BaseUtcOffset);
    }

    [Fact]
    public async Task Ohne_Angabe_des_Geraets_gelten_Konto_und_Deutsch()
    {
        var room = await PukkiRoom("anna");
        var reply = await Ask("anna", room, new { text = "termin morgen 15 Uhr Bank" + Guid.NewGuid().ToString("N")[..4] });
        Assert.Contains("Eingetragen: morgen, 15:00", reply);
    }

    [Fact]
    public void Termin_beim_Friseur_ist_keine_Gesundheit() =>
        Assert.NotEqual("gesundheit", PukkiArts.Classify("Hairdresser appointment")?.Key);

    [Fact]
    public void Tage_in_der_Sprache_der_Antwort()
    {
        var today = new DateOnly(2026, 11, 5);
        Assert.Equal("morgen", Pukki.DayWords(today.AddDays(1), today));
        using (Pukki.InEnglish())
        {
            Assert.Equal("tomorrow", Pukki.DayWords(today.AddDays(1), today));
            Assert.Equal("Monday, 9 November", Pukki.DayWords(today.AddDays(4), today));
        }
    }

    [Fact]
    public async Task Antwort_der_KI_ist_gekennzeichnet()
    {
        var room = await PukkiRoom("anna");
        var chat = _fx.Factory.Services.GetRequiredService<ChatStore>();
        var fromAi = chat.Post(Pukki.Id, room, "Antwort aus der KI", mood: Pukki.AiMood);
        var own = chat.Post(Pukki.Id, room, "Pukkis eigene Antwort");
        var list = await Messages("anna", room);
        var ai = list.Single(m => m.GetProperty("id").GetInt64() == fromAi);
        Assert.True(ai.GetProperty("ai").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ai.GetProperty("mood").ValueKind);
        Assert.False(list.Single(m => m.GetProperty("id").GetInt64() == own).GetProperty("ai").GetBoolean());
    }

    [Fact]
    public async Task Hinweis_auf_den_KI_Dienst_nur_mit_Freigabe()
    {
        var state = await Json(await _fx.As("anna").GetAsync("/api/chat/pukki/ai"));
        Assert.False(state.GetProperty("on").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("service").ValueKind);
    }
}
