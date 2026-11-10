using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Aufbewahrung: Ältere Nachrichten verschwinden samt Reaktionen; einstellen darf nur die Verwaltung.</summary>
[Collection("server")]
public class ChatRetentionTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChatRetentionTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public void Fristen()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(ChatRetention.Cutoff("forever", now));
        Assert.Equal(now.AddYears(-1), ChatRetention.Cutoff("1y", now));
        Assert.Equal(now.AddMonths(-3), ChatRetention.Cutoff("3m", now));
    }

    [Fact]
    public async Task Alte_Nachrichten_werden_geloescht_und_nur_die_Verwaltung_stellt_ein()
    {
        var group = (await Json(await _fx.As("anna").PostAsJsonAsync("/api/chat/rooms", new { title = "Alt", members = new[] { _fx.Id("ben") } }))).GetProperty("id").GetInt64();
        var old = await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = "Von früher" }));
        await Json(await _fx.As("ben").PostAsJsonAsync($"/api/chat/messages/{old.GetProperty("id").GetInt64()}/reactions", new { emoji = "👍" }));
        await Task.Delay(50);
        var cut = DateTime.UtcNow;
        await Task.Delay(50);
        var fresh = await Json(await _fx.As("ben").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = "Von heute" }));

        var chat = _fx.Factory.Services.GetRequiredService<ChatStore>();
        Assert.True(chat.Purge(cut) >= 2);   // Gründung und alte Nachricht
        var left = (await Json(await _fx.As("ben").GetAsync($"/api/chat/rooms/{group}/messages"))).EnumerateArray().ToList();
        Assert.Equal([fresh.GetProperty("id").GetInt64()], left.Select(m => m.GetProperty("id").GetInt64()));
        var room = (await Json(await _fx.As("ben").GetAsync("/api/chat/rooms"))).EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == group);
        Assert.Equal("Von heute", room.GetProperty("last").GetProperty("text").GetString());

        // Einstellen: nur die Verwaltung (angemeldet im Web - App-Passwoerter sind nie Verwaltung), nur bekannte Fristen
        Assert.Equal("forever", ChatRetention.Of(chat));
        Assert.False((await _fx.As("anna").PutAsJsonAsync("/api/chat/admin/retention", new { value = "1y" })).IsSuccessStatusCode);
        Assert.Throws<ArgumentException>(() => ChatRetention.Set(chat, "7d"));
        ChatRetention.Set(chat, "1y");
        Assert.Equal("1y", ChatRetention.Of(chat));
        Assert.Equal(0, ChatRetention.Run(chat, DateTime.UtcNow));
        ChatRetention.Set(chat, "forever");
    }
}
