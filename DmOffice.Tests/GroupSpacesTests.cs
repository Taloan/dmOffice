using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DmOffice.Tests;

/// <summary>Gruppenräume (Stufe 4): Kalender, Liste, Ordner, Notizordner beim Einrichtenden, geteilt mit allen Mitgliedern.</summary>
[Collection("server")]
public class GroupSpacesTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GroupSpacesTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<long> Group(string from, string title, params string[] members) =>
        (await Json(await _fx.As(from).PostAsJsonAsync("/api/chat/rooms", new { title, members = members.Select(_fx.Id).ToArray() }))).GetProperty("id").GetInt64();

    private async Task<List<JsonElement>> Spaces(string login, long room) =>
        (await Json(await _fx.As(login).GetAsync($"/api/chat/rooms/{room}/spaces"))).EnumerateArray().ToList();

    private async Task<bool> HasCalendar(string login, string name) =>
        (await Json(await _fx.As(login).GetAsync("/api/pim/collections/calendars"))).EnumerateArray().Any(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public async Task Raeume_folgen_den_Mitgliedern_und_enden_wenn_der_Besitzer_geht()
    {
        var title = "Familie " + Guid.NewGuid().ToString("N")[..6];
        var room = await Group("anna", title, "ben", "cleo");

        // Nur wer die Gruppe verwaltet, richtet Räume ein
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync($"/api/chat/rooms/{room}/spaces", new { kind = "calendar" })).StatusCode);
        foreach (var kind in new[] { "calendar", "list", "folder", "notes" })
            await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{room}/spaces", new { kind }));
        // Zweimal dieselbe Art geht nicht
        Assert.False((await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{room}/spaces", new { kind = "calendar" })).IsSuccessStatusCode);

        var mine = await Spaces("anna", room);
        Assert.Equal(4, mine.Count);
        Assert.All(mine, s => Assert.True(s.GetProperty("mine").GetBoolean()));
        Assert.All(mine, s => Assert.Equal(title, s.GetProperty("name").GetString()));

        // Ben: alles geteilt, mit Sprungzielen
        var bens = (await Spaces("ben", room)).ToDictionary(s => s.GetProperty("kind").GetString()!);
        Assert.False(bens["calendar"].GetProperty("mine").GetBoolean());
        Assert.True(long.Parse(bens["calendar"].GetProperty("target").GetString()!) < 0);
        var cals = (await Json(await _fx.As("ben").GetAsync("/api/pim/collections/calendars"))).EnumerateArray().ToList();
        var cal = cals.Single(c => c.GetProperty("id").GetInt64().ToString() == bens["calendar"].GetProperty("target").GetString());
        Assert.True(cal.GetProperty("canWrite").GetBoolean());
        var folder = bens["folder"].GetProperty("target").GetString()!;
        Assert.StartsWith("Mit mir geteilt/", folder);
        Assert.Contains((await Json(await _fx.As("ben").GetAsync("/api/files/shared"))).GetProperty("items").EnumerateArray(), x => x.GetProperty("path").GetString() == folder);
        Assert.Contains((await Json(await _fx.As("ben").GetAsync("/api/notes/shared"))).EnumerateArray(), x => x.GetProperty("root").GetString() == bens["notes"].GetProperty("target").GetString());

        // Im Verlauf steht, was eingerichtet wurde
        var messages = (await Json(await _fx.As("ben").GetAsync($"/api/chat/rooms/{room}/messages"))).EnumerateArray().ToList();
        Assert.Equal(4, messages.Count(m => m.GetProperty("kind").GetString() == "system" && m.GetProperty("text").GetString()!.Contains("\"space\"")));

        // Dora kommt dazu, Cleo wird entfernt
        await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{room}/members", new { users = new[] { _fx.Id("dora") } }));
        Assert.True(await HasCalendar("dora", title));
        Assert.True(await HasCalendar("cleo", title));
        Assert.True((await _fx.As("anna").DeleteAsync($"/api/chat/rooms/{room}/members/{_fx.Id("cleo")}")).IsSuccessStatusCode);
        Assert.False(await HasCalendar("cleo", title));

        // Anna geht: ihre Räume enden für die Gruppe, bleiben aber bei ihr
        Assert.True((await _fx.As("anna").PostAsync($"/api/chat/rooms/{room}/leave", null)).IsSuccessStatusCode);
        Assert.False(await HasCalendar("ben", title));
        Assert.Empty(await Spaces("ben", room));
        Assert.True(await HasCalendar("anna", title));
    }

    [Fact]
    public async Task Besitzer_nimmt_Raum_heraus_Daten_bleiben()
    {
        var title = "Verein " + Guid.NewGuid().ToString("N")[..6];
        var room = await Group("anna", title, "ben");
        await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{room}/spaces", new { kind = "calendar" }));
        Assert.True(await HasCalendar("ben", title));
        // Ben ist nicht Besitzer
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").DeleteAsync($"/api/chat/rooms/{room}/spaces/calendar")).StatusCode);
        Assert.True((await _fx.As("anna").DeleteAsync($"/api/chat/rooms/{room}/spaces/calendar")).IsSuccessStatusCode);
        Assert.False(await HasCalendar("ben", title));
        Assert.True(await HasCalendar("anna", title));
        Assert.Empty(await Spaces("anna", room));
    }
}
