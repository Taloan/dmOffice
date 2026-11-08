using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DmOffice.Tests;

/// <summary>Tagebuch mit KI: nur mit Freigabe, nur das Gewählte geht mit, die Wahl wird Vorgabe, Antwort als Absätze.</summary>
[Collection("server")]
public class JournalAiTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public JournalAiTests(ChatFixture fx) => _fx = fx;

    /// <summary>Kleine OpenAI-kompatible Gegenstelle: merkt sich die Anfrage, antwortet mit festem Text.</summary>
    private sealed class FakeAi : IDisposable
    {
        private readonly HttpListener _l = new();
        public int Port { get; }
        public string? LastBody { get; private set; }
        public FakeAi(string answer)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _l.GetContextAsync(); } catch { return; }
                    LastBody = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } }));
                    c.Response.ContentType = "application/json";
                    await c.Response.OutputStream.WriteAsync(bytes);
                    c.Response.Close();
                }
            });
        }
        public void Dispose() { _l.Stop(); _l.Close(); }
    }

    [Fact]
    public async Task Eintrag_mit_KI_schreiben()
    {
        var c = _fx.As("ben");
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var id = _fx.Id("ben");
        using var ai = new FakeAi("Ein ruhiger Tag.\n\nAbends noch spazieren <b>gewesen</b>.");
        try
        {
            // Ohne Freigabe: aus
            Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/journal/ai")).GetProperty("allowed").GetBoolean());
            config.Update(s => { s.AiProvider = "openai"; s.AiEndpoint = $"http://127.0.0.1:{ai.Port}/v1"; s.AiModel = "test"; });
            users.Update(id, u => u.AiAllowed = true);
            var st = await c.GetFromJsonAsync<JsonElement>("/api/journal/ai");
            Assert.True(st.GetProperty("allowed").GetBoolean());
            Assert.Equal("medium", st.GetProperty("settings").GetProperty("length").GetString());

            (await c.PostAsJsonAsync("/api/coach/activity", new { source = "garmin", days = new[] { new { date = "2026-10-20", steps = 8123 } } })).EnsureSuccessStatusCode();
            var r = await c.PostAsJsonAsync("/api/journal/ai/write", new
            {
                date = "2026-10-20", length = "short", fitness = true, events = false, place = false, weather = true, news = false,
                placeName = "Geheimort", weatherText = "sonnig, 18 °C", mood = 4, title = "", notes = "Spaziergang am See",
            });
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.IsSuccessStatusCode, body);
            var html = JsonDocument.Parse(body).RootElement.GetProperty("html").GetString();
            // Absätze, nichts von der KI als HTML übernommen
            Assert.Equal("<p>Ein ruhiger Tag.</p><p>Abends noch spazieren &lt;b&gt;gewesen&lt;/b&gt;.</p>", html);
            // Mitgeschickt: Schritte, Wetter, eigener Text - der Ort nicht (abgewählt)
            Assert.Contains("8.123 Schritte", ai.LastBody);
            Assert.Contains("sonnig", ai.LastBody);
            Assert.Contains("Spaziergang am See", ai.LastBody);
            Assert.DoesNotContain("Geheimort", ai.LastBody);
            Assert.Contains("zwei bis drei S\\u00E4tze", ai.LastBody);
            // Die Wahl ist jetzt die Vorgabe
            var again = (await c.GetFromJsonAsync<JsonElement>("/api/journal/ai")).GetProperty("settings");
            Assert.Equal("short", again.GetProperty("length").GetString());
            Assert.False(again.GetProperty("place").GetBoolean());
            Assert.False(again.GetProperty("events").GetBoolean());

            // Tagebuch im Tresor: nichts an die KI
            var before = ai.LastBody;
            var tresor = await c.PostAsJsonAsync("/api/journal/ai/write", new
            {
                date = "2026-10-21", length = "short", fitness = false, events = false, place = false, weather = false, news = false,
                title = "", notes = "Nur für mich", journal = 1L << 40,
            });
            Assert.Equal(HttpStatusCode.BadRequest, tresor.StatusCode);
            Assert.Equal(before, ai.LastBody);
        }
        finally
        {
            users.Update(id, u => u.AiAllowed = false);
            config.Update(s => { s.AiProvider = "anthropic"; s.AiEndpoint = null; s.AiModel = null; });
        }
    }
}
