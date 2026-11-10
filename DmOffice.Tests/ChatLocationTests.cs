using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Standort-Anfrage: Recht der Verwaltung, Einstellung des Gefragten, Antworten per Knopf, Weiterleiten; Live-Teilen.</summary>
[Collection("server")]
public class ChatLocationTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChatLocationTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private ChatStore Chat => _fx.Factory.Services.GetRequiredService<ChatStore>();

    private void Allow(string login, bool askOthers, bool beAsked)
    {
        Chat.SetRights(new ChatRights(_fx.Id(login), LocationRequests: askOthers));
        var db = _fx.Space(login).Db;
        var s = ChatSettings.Of(db);
        s.LocationRequests = beAsked;
        ChatSettings.Save(db, s);
    }

    private async Task<long> Room(string a, string b) =>
        (await Json(await _fx.As(a).PostAsJsonAsync("/api/chat/rooms", new { members = new[] { _fx.Id(b) } }))).GetProperty("id").GetInt64();

    private async Task<bool> AskLocation(string login, long room) =>
        (await Json(await _fx.As(login).GetAsync($"/api/chat/rooms/{room}"))).GetProperty("askLocation").GetBoolean();

    [Fact]
    public async Task Fragen_nur_mit_Recht_und_Einverstaendnis()
    {
        var room = await Room("anna", "ben");
        // Weder Recht noch Einstellung
        Allow("anna", askOthers: false, beAsked: false);
        Allow("ben", askOthers: false, beAsked: false);
        Assert.False(await AskLocation("anna", room));
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("anna").PostAsync($"/api/chat/rooms/{room}/location-request", null)).StatusCode);
        // Recht ja, aber Ben will nicht gefragt werden
        Allow("anna", askOthers: true, beAsked: false);
        Assert.False(await AskLocation("anna", room));
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("anna").PostAsync($"/api/chat/rooms/{room}/location-request", null)).StatusCode);
        // Beides: geht - aber nicht umgekehrt (Ben hat kein Recht)
        Allow("ben", askOthers: false, beAsked: true);
        Assert.True(await AskLocation("anna", room));
        Assert.False(await AskLocation("ben", room));
        var req = await Json(await _fx.As("anna").PostAsync($"/api/chat/rooms/{room}/location-request", null));
        Assert.Equal(["loc-yes", "loc-no"], req.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task Standort_schicken_und_ablehnen()
    {
        var room = await Room("cleo", "dora");
        Allow("cleo", askOthers: true, beAsked: false);
        Allow("dora", askOthers: false, beAsked: true);

        // Ja: Dora schickt ihre Position als Antwort; die Knöpfe sind danach weg
        var req = (await Json(await _fx.As("cleo").PostAsync($"/api/chat/rooms/{room}/location-request", null))).GetProperty("id").GetInt64();
        // Cleo kann die eigene Anfrage nicht selbst beantworten
        Assert.False((await _fx.As("cleo").PostAsJsonAsync($"/api/chat/rooms/{room}/location", new { lat = 1.0, lon = 2.0, request = req })).IsSuccessStatusCode);
        var loc = await Json(await _fx.As("dora").PostAsJsonAsync($"/api/chat/rooms/{room}/location", new { lat = 48.2081743, lon = 16.3738189, accuracy = 12.4, request = req }));
        Assert.Equal(req, loc.GetProperty("replyTo").GetInt64());
        var r = loc.GetProperty("refs")[0];
        Assert.Equal("location", r.GetProperty("type").GetString());
        Assert.Equal(48.208174, r.GetProperty("lat").GetDouble(), 6);
        Assert.Equal(16.373819, r.GetProperty("lon").GetDouble(), 6);
        Assert.Equal(12, r.GetProperty("accuracy").GetDouble());
        Assert.Empty(Chat.Message(_fx.Id("cleo"), req).Actions ?? []);
        // Zweimal beantworten geht nicht
        Assert.False((await _fx.As("dora").PostAsJsonAsync($"/api/chat/rooms/{room}/location", new { lat = 1.0, lon = 2.0, request = req })).IsSuccessStatusCode);

        // Nein: kurze Antwort, kein Standort
        var req2 = (await Json(await _fx.As("cleo").PostAsync($"/api/chat/rooms/{room}/location-request", null))).GetProperty("id").GetInt64();
        Assert.True((await _fx.As("dora").PostAsync($"/api/chat/messages/{req2}/actions/loc-no", null)).IsSuccessStatusCode);
        var last = Chat.Messages(_fx.Id("cleo"), room).MaxBy(m => m.Id)!;
        Assert.Equal(req2, last.ReplyTo);
        Assert.Empty(last.Refs);
        Assert.Empty(Chat.Message(_fx.Id("cleo"), req2).Actions ?? []);

        // Weiterleiten: den eigenen Standort ja, den fremden nicht
        var other = await Room("cleo", "anna");
        var locId = loc.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PostAsJsonAsync($"/api/chat/messages/{locId}/forward", new { rooms = new[] { other } })).StatusCode);
        var doraAnna = await Room("dora", "anna");
        await Json(await _fx.As("dora").PostAsJsonAsync($"/api/chat/messages/{locId}/forward", new { rooms = new[] { doraAnna } }));

        // Gezeigt, nicht gesendet; zurückgezogen sieht Cleo keine Koordinaten mehr
        Assert.Equal("show", r.GetProperty("mode").GetString());
        Assert.False((await _fx.As("dora").PutAsJsonAsync($"/api/chat/messages/{locId}/refs/0", new { mode = "send" })).IsSuccessStatusCode);
        await Json(await _fx.As("dora").PutAsJsonAsync($"/api/chat/messages/{locId}/refs/0", new { revoke = true }));
        var seen = (await Json(await _fx.As("cleo").GetAsync($"/api/chat/rooms/{room}/messages"))).EnumerateArray().First(m => m.GetProperty("id").GetInt64() == locId);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("refs")[0].GetProperty("lat").ValueKind);

        // Ungültige Position
        Assert.False((await _fx.As("dora").PostAsJsonAsync($"/api/chat/rooms/{room}/location", new { lat = 95.0, lon = 2.0 })).IsSuccessStatusCode);
    }
    [Fact]
    public async Task Live_teilen_aktualisiert_dieselbe_Nachricht()
    {
        var room = await Room("anna", "dora");
        // Mehr als 30 Minuten gehen nicht
        Assert.False((await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{room}/location", new { lat = 1.0, lon = 2.0, minutes = 31 })).IsSuccessStatusCode);

        var loc = await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{room}/location", new { lat = 48.2, lon = 16.37, accuracy = 20.0, minutes = 15 }));
        var id = loc.GetProperty("id").GetInt64();
        var until = loc.GetProperty("refs")[0].GetProperty("liveUntil").GetDateTime();
        Assert.InRange((until - DateTime.UtcNow).TotalMinutes, 14, 15.1);
        var count = Chat.Messages(_fx.Id("anna"), room).Count;

        // Neue Position: dieselbe Nachricht, keine neue
        var upd = await Json(await _fx.As("anna").PutAsJsonAsync($"/api/chat/rooms/{room}/location/{id}", new { lat = 48.21, lon = 16.38, accuracy = 8.0 }));
        Assert.Equal(id, upd.GetProperty("id").GetInt64());
        Assert.Equal(count, Chat.Messages(_fx.Id("anna"), room).Count);
        var seen = (await Json(await _fx.As("dora").GetAsync($"/api/chat/rooms/{room}/messages"))).EnumerateArray().First(m => m.GetProperty("id").GetInt64() == id);
        Assert.Equal(48.21, seen.GetProperty("refs")[0].GetProperty("lat").GetDouble(), 6);
        Assert.Equal(8, seen.GetProperty("refs")[0].GetProperty("accuracy").GetDouble());

        // Nur der Absender aktualisiert; ungültige Position abgelehnt
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("dora").PutAsJsonAsync($"/api/chat/rooms/{room}/location/{id}", new { lat = 1.0, lon = 2.0 })).StatusCode);
        Assert.False((await _fx.As("anna").PutAsJsonAsync($"/api/chat/rooms/{room}/location/{id}", new { lat = 1.0, lon = 200.0 })).IsSuccessStatusCode);

        // Beenden: die letzte Position bleibt, Aktualisieren geht nicht mehr
        var stopped = await Json(await _fx.As("anna").PostAsync($"/api/chat/rooms/{room}/location/{id}/stop", null));
        Assert.True(stopped.GetProperty("refs")[0].GetProperty("liveUntil").GetDateTime() <= DateTime.UtcNow);
        Assert.Equal(48.21, stopped.GetProperty("refs")[0].GetProperty("lat").GetDouble(), 6);
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("anna").PutAsJsonAsync($"/api/chat/rooms/{room}/location/{id}", new { lat = 1.0, lon = 2.0 })).StatusCode);

        // Abgelaufen (spätestens nach 30 Minuten) nimmt der Server nichts mehr an
        var loc2 = (await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{room}/location", new { lat = 1.0, lon = 2.0, minutes = 30 }))).GetProperty("id").GetInt64();
        Chat.UpdateRef(_fx.Id("anna"), loc2, 0, r => r with { LiveUntil = DateTime.UtcNow.AddSeconds(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("anna").PutAsJsonAsync($"/api/chat/rooms/{room}/location/{loc2}", new { lat = 1.0, lon = 2.0 })).StatusCode);

        // Einmaliger Standort ist kein Live-Standort; Weiterleiten nimmt nur die Position mit
        var once = await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{room}/location", new { lat = 1.0, lon = 2.0 }));
        Assert.Equal(JsonValueKind.Null, once.GetProperty("refs")[0].GetProperty("liveUntil").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("anna").PutAsJsonAsync($"/api/chat/rooms/{room}/location/{once.GetProperty("id").GetInt64()}", new { lat = 1.0, lon = 2.0 })).StatusCode);
        var annaBen = await Room("anna", "ben");
        var fwd = (await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/messages/{loc2}/forward", new { rooms = new[] { annaBen } }))).EnumerateArray().First();
        Assert.Null(Chat.Message(_fx.Id("anna"), fwd.GetProperty("id").GetInt64()).Refs[0].LiveUntil);
    }
}
