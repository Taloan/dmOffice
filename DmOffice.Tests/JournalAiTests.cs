using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
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
        public List<string> Bodies { get; } = [];
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
                    lock (Bodies) Bodies.Add(LastBody);
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
            Assert.Contains("sonnig", ai.LastBody);
            Assert.Contains("Spaziergang am See", ai.LastBody);
            Assert.DoesNotContain("Geheimort", ai.LastBody);
            Assert.Contains("zwei bis drei S\\u00E4tze", ai.LastBody);
            // Die Wahl ist jetzt die Vorgabe
            var again = (await c.GetFromJsonAsync<JsonElement>("/api/journal/ai")).GetProperty("settings");
            Assert.Equal("short", again.GetProperty("length").GetString());
            Assert.False(again.GetProperty("place").GetBoolean());
            Assert.False(again.GetProperty("events").GetBoolean());

            // Fotos (Schalter aiphotos): erst beschreiben lassen, die Beschreibung geht in den Text; Tresor-Ordner nie
            var featuresBefore = Features.InReleaseOff.ToList();
            Features.Set(featuresBefore.Where(x => x != Features.AiPhotos));
            try
            {
                using var bmp = new SKBitmap(300, 200);
                using (var cv = new SKCanvas(bmp)) cv.Clear(SKColors.SeaGreen);
                var jpg = bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
                var form = new MultipartFormDataContent { { new ByteArrayContent(jpg), "files", "see.jpg" } };
                Assert.True((await c.PostAsync($"/api/files/upload?path={Uri.EscapeDataString("/Urlaub")}", form)).IsSuccessStatusCode);
                var journal = (await c.GetFromJsonAsync<JsonElement>("/api/journal/journals")).EnumerateArray().First(x => x.GetProperty("owner").ValueKind == JsonValueKind.Null).GetProperty("id").GetInt64();
                lock (ai.Bodies) ai.Bodies.Clear();
                var withPhoto = await c.PostAsJsonAsync("/api/journal/ai/write", new
                {
                    date = "2026-10-20", length = "short", fitness = false, events = false, place = false, weather = false, news = false,
                    title = "", notes = "Am See", journal, photos = true, photoPaths = new[] { "/Urlaub/see.jpg", "/gibt/es/nicht.jpg" },
                });
                Assert.True(withPhoto.IsSuccessStatusCode, await withPhoto.Content.ReadAsStringAsync());
                Assert.Equal(2, ai.Bodies.Count);
                Assert.Contains("image_url", ai.Bodies[0]);
                Assert.Contains("Auf den Fotos des Tages", ai.Bodies[1]);
                Assert.DoesNotContain("image_url", ai.Bodies[1]);
                Assert.Contains("Fotos", (await withPhoto.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").ToString());
                // Abgewählt: kein Bild geht raus
                lock (ai.Bodies) ai.Bodies.Clear();
                (await c.PostAsJsonAsync("/api/journal/ai/write", new
                {
                    date = "2026-10-20", length = "short", fitness = false, events = false, place = false, weather = false, news = false,
                    title = "", notes = "Am See", journal, photos = false, photoPaths = new[] { "/Urlaub/see.jpg" },
                })).EnsureSuccessStatusCode();
                Assert.Single(ai.Bodies);
                Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/journal/ai")).GetProperty("settings").GetProperty("photos").GetBoolean());
            }
            finally { Features.Set(featuresBefore); }

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

    private async Task<string> Photo(string login, string name, long journal, bool @private)
    {
        var c = _fx.As(login);
        using var bmp = new SKBitmap(120, 80);
        using (var cv = new SKCanvas(bmp)) cv.Clear(SKColors.Coral);
        var data = bmp.Encode(SKEncodedImageFormat.Png, 90).ToArray();
        var id = (await (await c.PostAsJsonAsync("/api/files/uploads", new { name, size = data.Length, mime = "image/png" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        (await c.PutAsync($"/api/files/uploads/{id}?offset=0", new ByteArrayContent(data))).EnsureSuccessStatusCode();
        var media = (await (await c.PostAsync($"/api/journal/uploads/{id}/attach?journal={journal}", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("media");
        if (@private)
            (await c.PostAsJsonAsync("/api/journal/entries", new { journal, date = "2026-10-22T10:00:00Z", html = "<p>x</p>", @private = true, media = new[] { media } })).EnsureSuccessStatusCode();
        return media.GetProperty("path").GetString()!;
    }

    [Fact]
    public async Task KI_im_geteilten_Tagebuch_nur_mit_sichtbaren_oder_selbst_angehaengten_Fotos()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var id = _fx.Id("ben");
        using var ai = new FakeAi("Ein Tag.");
        var featuresBefore = Features.InReleaseOff.ToList();
        try
        {
            config.Update(s => { s.AiProvider = "openai"; s.AiEndpoint = $"http://127.0.0.1:{ai.Port}/v1"; s.AiModel = "test"; });
            users.Update(id, u => u.AiAllowed = true);
            Features.Set(featuresBefore.Where(x => x != Features.AiPhotos));
            var anna = _fx.As("anna");
            var j = (await anna.GetFromJsonAsync<JsonElement>("/api/journal/journals")).EnumerateArray().First(x => x.GetProperty("owner").ValueKind == JsonValueKind.Null).GetProperty("id").GetInt64();
            var secret = await Photo("anna", "privat-ki.png", j, true);
            (await anna.PutAsJsonAsync($"/api/pim/shares/{j}", new { members = new[] { id } })).EnsureSuccessStatusCode();
            var ben = _fx.As("ben");
            var sj = (await ben.GetFromJsonAsync<JsonElement>("/api/journal/journals")).EnumerateArray().First(x => x.GetProperty("owner").GetString() == "Anna").GetProperty("id").GetInt64();

            async Task<int> Write(string path)
            {
                lock (ai.Bodies) ai.Bodies.Clear();
                (await ben.PostAsJsonAsync("/api/journal/ai/write", new
                {
                    date = "2026-10-22", length = "short", fitness = false, events = false, place = false, weather = false, news = false,
                    title = "", notes = "Ausflug", journal = sj, photos = true, photoPaths = new[] { path },
                })).EnsureSuccessStatusCode();
                lock (ai.Bodies) return ai.Bodies.Count(b => b.Contains("image_url"));
            }
            // Ein Foto aus Annas privatem Eintrag geht nicht an die KI, ein selbst angehängtes schon
            Assert.Equal(0, await Write(secret));
            Assert.Equal(1, await Write(await Photo("ben", "von-ben-ki.png", sj, false)));
            // Anhängen lässt sich Annas Foto ebenso wenig
            Assert.Equal(HttpStatusCode.Forbidden, (await ben.PostAsJsonAsync("/api/journal/entries", new
            {
                journal = sj, date = "2026-10-22T11:00:00Z", html = "<p>y</p>", media = new[] { new { path = secret, mime = "image/png", name = "privat-ki.png" } },
            })).StatusCode);
        }
        finally
        {
            Features.Set(featuresBefore);
            users.Update(id, u => u.AiAllowed = false);
            _fx.Space("ben").Db.Set(DmOffice.Journal.JournalAi.Key, null);
            config.Update(s => { s.AiProvider = "anthropic"; s.AiEndpoint = null; s.AiModel = null; });
        }
    }
}
