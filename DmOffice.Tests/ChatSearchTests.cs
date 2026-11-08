using System.Net.Http.Json;
using System.Text.Json;

namespace DmOffice.Tests;

/// <summary>Suche im Chat: nur Sichtbares, Umlaute egal, nichts von vor dem Beitritt.</summary>
[Collection("server")]
public class ChatSearchTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChatSearchTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<List<JsonElement>> Search(string login, string q) =>
        (await Json(await _fx.As(login).GetAsync("/api/chat/search?q=" + Uri.EscapeDataString(q)))).EnumerateArray().ToList();

    [Fact]
    public async Task Findet_Sichtbares_ohne_Umlaut_Sorgen()
    {
        var word = "Kuchenrezept" + Guid.NewGuid().ToString("N")[..5];
        var group = (await Json(await _fx.As("anna").PostAsJsonAsync("/api/chat/rooms", new { title = "Backen", members = new[] { _fx.Id("ben") } }))).GetProperty("id").GetInt64();
        await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = $"Grüße aus Österreich – hier das {word} mit Äpfeln" }));

        var hits = await Search("ben", $"oesterreich {word.ToLowerInvariant()} äpfeln");
        var hit = Assert.Single(hits);
        Assert.Equal(group, hit.GetProperty("room").GetInt64());
        Assert.Equal("Backen", hit.GetProperty("roomTitle").GetString());
        Assert.Contains(word, hit.GetProperty("message").GetProperty("text").GetString());

        // Nicht dabei: nichts
        Assert.Empty(await Search("cleo", word));
        // Später dazugekommen: der Verlauf davor bleibt verborgen
        await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{group}/members", new { users = new[] { _fx.Id("dora") } }));
        Assert.Empty(await Search("dora", word));
        // Alle Wörter müssen passen
        Assert.Empty(await Search("ben", word + " Birnen"));
    }
}
