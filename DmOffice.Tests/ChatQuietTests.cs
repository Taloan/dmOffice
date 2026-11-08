using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;

namespace DmOffice.Tests;

/// <summary>Ruhezeiten: auch ueber Mitternacht; der Stand sagt, was gerade gemeldet werden darf.</summary>
[Collection("server")]
public class ChatQuietTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChatQuietTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Theory]
    [InlineData("22:00", "07:00", "23:30", true)]
    [InlineData("22:00", "07:00", "03:00", true)]
    [InlineData("22:00", "07:00", "07:00", false)]
    [InlineData("22:00", "07:00", "12:00", false)]
    [InlineData("13:00", "15:00", "14:00", true)]
    [InlineData("13:00", "15:00", "15:30", false)]
    [InlineData("08:00", "08:00", "08:00", false)]
    public void Ruhezeit_auch_ueber_Mitternacht(string from, string to, string at, bool quiet)
    {
        var s = new ChatSettings { Quiet = true, QuietFrom = from, QuietTo = to };
        Assert.Equal(quiet, s.InQuiet(DateTime.Parse("2026-10-21T" + at)));
        Assert.False(new ChatSettings { Quiet = false, QuietFrom = from, QuietTo = to }.InQuiet(DateTime.Parse("2026-10-21T" + at)));
    }

    [Fact]
    public async Task Stand_meldet_die_Ruhezeit()
    {
        async Task<JsonElement> Quiet() => (await Json(await _fx.As("ben").GetAsync("/api/chat/status"))).GetProperty("quiet");
        Assert.Equal(JsonValueKind.Null, (await Quiet()).ValueKind);

        var now = Pukki.Now(_fx.Space("ben"));
        var settings = (await Json(await _fx.As("ben").GetAsync("/api/chat/settings"))).GetProperty("settings");
        var changed = JsonSerializer.Deserialize<Dictionary<string, object>>(settings.GetRawText())!;
        changed["quiet"] = true;
        changed["quietFrom"] = now.AddHours(-1).ToString("HH:mm");
        changed["quietTo"] = now.AddHours(1).ToString("HH:mm");
        await Json(await _fx.As("ben").PutAsJsonAsync("/api/chat/settings", changed));
        Assert.Equal("all", (await Quiet()).GetString());

        changed["quietMentions"] = true;
        await Json(await _fx.As("ben").PutAsJsonAsync("/api/chat/settings", changed));
        Assert.Equal("mentions", (await Quiet()).GetString());

        changed["quietFrom"] = "25:00";
        Assert.False((await _fx.As("ben").PutAsJsonAsync("/api/chat/settings", changed)).IsSuccessStatusCode);
    }
}
