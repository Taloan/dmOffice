using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>@Erwaehnungen: Name, Vorname, Login, „@alle“ - und Erwaehnte erfahren es auch, wenn sie stumm geschaltet haben.</summary>
[Collection("server")]
public class ChatMentionsTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChatMentionsTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Theory]
    [InlineData("@Ben schau mal", "Ben", true)]
    [InlineData("Hallo @ben, morgen?", "Ben", true)]
    [InlineData("Hallo @Ben Test!", "Ben Test", true)]
    [InlineData("schreib an ben@example.org", "ben", false)]
    [InlineData("@Benjamin kommt", "Ben", false)]
    [InlineData("Ben ohne At", "Ben", false)]
    public void Erkennt_Namen_als_eigenes_Wort(string text, string name, bool expected) =>
        Assert.Equal(expected, ChatMentions.Has(text, name));

    [Fact]
    public async Task Erwaehnung_erreicht_auch_Stumme_und_wird_gezaehlt()
    {
        var group = (await Json(await _fx.As("anna").PostAsJsonAsync("/api/chat/rooms",
            new { title = "Verein", members = new[] { _fx.Id("ben"), _fx.Id("cleo") } }))).GetProperty("id").GetInt64();
        await Json(await _fx.As("ben").PutAsJsonAsync($"/api/chat/rooms/{group}", new { muted = true }));
        await Json(await _fx.As("cleo").PutAsJsonAsync($"/api/chat/rooms/{group}", new { muted = true }));

        // Vergleichsstand (Pukkis Begruessung zaehlt mit)
        async Task<int> Unread(string login) { await _fx.As(login).GetAsync("/api/chat/rooms"); return (await Json(await _fx.As(login).GetAsync("/api/chat/status"))).GetProperty("unread").GetInt32(); }
        var benBefore = await Unread("ben");
        var cleoBefore = await Unread("cleo");

        var posted = new List<ChatPosted>();
        var chat = _fx.Factory.Services.GetRequiredService<ChatStore>();
        void OnPosted(ChatPosted p) { lock (posted) posted.Add(p); }
        chat.Posted += OnPosted;
        try
        {
            // Ohne Erwaehnung: Stumme bleiben aussen vor
            await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = "Training faellt aus" }));
            // Vorname: nur Ben
            var msg = await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = "@ben bringst du die Baelle mit?" }));
            Assert.Equal([_fx.Id("ben")], msg.GetProperty("mentions").EnumerateArray().Select(x => x.GetString()!).ToArray());
            // „@alle“: alle ausser mir
            var all = await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = "Danke @alle!" }));
            Assert.Equal(2, all.GetProperty("mentions").GetArrayLength());
        }
        finally { chat.Posted -= OnPosted; }

        Assert.Empty(posted[0].Recipients);
        Assert.Equal([_fx.Id("ben")], posted[1].Recipients);
        Assert.Equal(2, posted[2].Recipients.Count);

        // Ben: drei ungelesen, zwei davon erwaehnen ihn; die Zahl am Symbol zaehlt nur die Erwaehnungen (stumm)
        var room = (await Json(await _fx.As("ben").GetAsync("/api/chat/rooms"))).EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == group);
        Assert.Equal(3, room.GetProperty("unread").GetInt32());
        Assert.Equal(2, room.GetProperty("mentioned").GetInt32());
        Assert.Equal(benBefore + 2, await Unread("ben"));
        Assert.Equal(cleoBefore + 1, await Unread("cleo"));
    }

    [Fact]
    public async Task Bearbeiten_aendert_die_Erwaehnten()
    {
        var group = (await Json(await _fx.As("cleo").PostAsJsonAsync("/api/chat/rooms",
            new { title = "Chor", members = new[] { _fx.Id("dora"), _fx.Id("anna") } }))).GetProperty("id").GetInt64();
        var msg = await Json(await _fx.As("cleo").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = "@Dora Noten?" }));
        var edited = await Json(await _fx.As("cleo").PutAsJsonAsync($"/api/chat/messages/{msg.GetProperty("id").GetInt64()}", new { text = "@Anna Noten?" }));
        Assert.Equal([_fx.Id("anna")], edited.GetProperty("mentions").EnumerateArray().Select(x => x.GetString()!).ToArray());
        // Wer nicht dabei ist, kann nicht erwaehnt werden
        var other = await Json(await _fx.As("cleo").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = "@ben?" }));
        Assert.Equal(0, other.GetProperty("mentions").GetArrayLength());
    }
}
