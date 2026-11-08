using System.Net.Http.Json;
using System.Text.Json;

namespace DmOffice.Tests;

/// <summary>
/// Zustellung und Lesebestätigungen; eigene Nachrichten zu zweit nur ändern, solange ungelesen (ohne Lesebestätigung:
/// bis zur Zustellung). In Gruppen immer.
/// </summary>
[Collection("server")]
public class ChatReceiptsTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChatReceiptsTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<long> Post(string login, long room, string text) =>
        (await Json(await _fx.As(login).PostAsJsonAsync($"/api/chat/rooms/{room}/messages", new { text }))).GetProperty("id").GetInt64();

    private Task<HttpResponseMessage> Edit(string login, long message, string text) =>
        _fx.As(login).PutAsJsonAsync($"/api/chat/messages/{message}", new { text });

    /// <summary>Wie ein Mitglied aus Sicht von login dasteht (lastRead, delivered, receipts).</summary>
    private async Task<JsonElement> Member(string login, long room, string of) =>
        (await Json(await _fx.As(login).GetAsync($"/api/chat/rooms/{room}"))).GetProperty("members").EnumerateArray().Single(m => m.GetProperty("id").GetString() == _fx.Id(of));

    [Fact]
    public async Task Zu_zweit_bis_gelesen_ohne_Lesebestaetigung_bis_zugestellt()
    {
        var room = (await Json(await _fx.As("anna").PostAsJsonAsync("/api/chat/rooms", new { members = new[] { _fx.Id("ben") } }))).GetProperty("id").GetInt64();
        var first = await Post("anna", room, "Hallo Ben");

        // Noch nicht abgeholt: weder zugestellt noch gelesen - ändern geht
        Assert.True((await Member("anna", room, "ben")).GetProperty("delivered").GetInt64() < first);
        Assert.True((await Edit("anna", first, "Hallo Ben!")).IsSuccessStatusCode);

        // Ben holt ab: zugestellt, aber noch nicht gelesen - ändern geht weiter
        await Json(await _fx.As("ben").GetAsync($"/api/chat/rooms/{room}/messages"));
        Assert.True((await Member("anna", room, "ben")).GetProperty("delivered").GetInt64() >= first);
        Assert.True((await Edit("anna", first, "Hallo Ben :)")).IsSuccessStatusCode);

        // Ben liest: jetzt nicht mehr - weder ändern noch löschen
        await _fx.As("ben").PostAsJsonAsync($"/api/chat/rooms/{room}/read", new { upTo = first });
        Assert.True((await Member("anna", room, "ben")).GetProperty("lastRead").GetInt64() >= first);
        var refused = await Edit("anna", first, "doch anders");
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("gelesen", await refused.Content.ReadAsStringAsync());
        Assert.False((await _fx.As("anna").DeleteAsync($"/api/chat/messages/{first}")).IsSuccessStatusCode);

        // Ben schaltet die Lesebestätigungen aus: Anna sieht seinen Lesestand nicht mehr, die Zustellung schon
        var settings = (await Json(await _fx.As("ben").GetAsync("/api/chat/settings"))).GetProperty("settings");
        var changed = JsonSerializer.Deserialize<Dictionary<string, object>>(settings.GetRawText())!;
        changed["readReceipts"] = false;
        await Json(await _fx.As("ben").PutAsJsonAsync("/api/chat/settings", changed));
        var ben = await Member("anna", room, "ben");
        Assert.False(ben.GetProperty("receipts").GetBoolean());
        Assert.Equal(0, ben.GetProperty("lastRead").GetInt64());

        // Dann geht ändern nur bis zur Zustellung - ob er gelesen hat, lässt sich so nicht herausfinden
        var second = await Post("anna", room, "Noch eine Frage");
        Assert.True((await Edit("anna", second, "Noch eine Frage …")).IsSuccessStatusCode);
        await Json(await _fx.As("ben").GetAsync("/api/chat/rooms"));
        var late = await Edit("anna", second, "zu spät");
        Assert.False(late.IsSuccessStatusCode);
        Assert.Contains("zugestellt", await late.Content.ReadAsStringAsync());

        // Ben selbst sieht seinen eigenen Lesestand weiter
        Assert.True((await Member("ben", room, "ben")).GetProperty("lastRead").GetInt64() >= first);
    }

    [Fact]
    public async Task In_Gruppen_jederzeit_aendern_und_loeschen()
    {
        var group = (await Json(await _fx.As("cleo").PostAsJsonAsync("/api/chat/rooms", new { title = "Team", members = new[] { _fx.Id("dora") } }))).GetProperty("id").GetInt64();
        var msg = await Post("cleo", group, "Treffen um 10");
        await Json(await _fx.As("dora").GetAsync($"/api/chat/rooms/{group}/messages"));
        await _fx.As("dora").PostAsJsonAsync($"/api/chat/rooms/{group}/read", new { upTo = msg });
        Assert.True((await Edit("cleo", msg, "Treffen um 11")).IsSuccessStatusCode);
        Assert.True((await _fx.As("cleo").DeleteAsync($"/api/chat/messages/{msg}")).IsSuccessStatusCode);
    }
}
