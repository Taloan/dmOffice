using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DmOffice.Tests;

/// <summary>Unterhaltung exportieren: Verlauf als Text, Gesendetes als Anhang, Gezeigtes nur mit Namen.</summary>
[Collection("server")]
public class ChatExportTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChatExportTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task Upload(string login, long room, string name, string content, string mode)
    {
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        body.Headers.ContentType = new("text/plain");
        await Json(await _fx.As(login).PostAsync($"/api/chat/rooms/{room}/upload?name={Uri.EscapeDataString(name)}&mode={mode}", body));
    }

    [Fact]
    public async Task Export_mit_Verlauf_und_erlaubten_Anhaengen()
    {
        var group = (await Json(await _fx.As("anna").PostAsJsonAsync("/api/chat/rooms", new { title = "Ausflug", members = new[] { _fx.Id("ben") } }))).GetProperty("id").GetInt64();
        var first = await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = "Treffen um 9 am Bahnhof" }));
        await Json(await _fx.As("ben").PostAsJsonAsync($"/api/chat/rooms/{group}/messages", new { text = "Passt!", replyTo = first.GetProperty("id").GetInt64() }));
        await Upload("anna", group, "Packliste.txt", "Wanderschuhe, Regenjacke", "send");
        await Upload("anna", group, "Geheim.txt", "nur zum Ansehen", "show");

        var r = await _fx.As("ben").GetAsync($"/api/chat/rooms/{group}/export");
        Assert.True(r.IsSuccessStatusCode);
        Assert.Equal("application/zip", r.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
        var text = new StreamReader(zip.GetEntry("Unterhaltung.txt")!.Open()).ReadToEnd();
        Assert.Contains("Unterhaltung „Ausflug“", text);
        Assert.Contains("Anna: Treffen um 9 am Bahnhof", text);
        Assert.Contains("Ben: (Antwort auf Anna: „Treffen um 9 am Bahnhof“) Passt!", text);
        Assert.Contains("Anna hat die Gruppe „Ausflug“ erstellt", text);
        Assert.Contains("Datei „Packliste.txt“ → Anhänge/", text);
        Assert.Contains("Datei „Geheim.txt“ (nur gezeigt)", text);

        var packliste = zip.Entries.Single(e => e.FullName.StartsWith("Anhänge/") && e.FullName.EndsWith("Packliste.txt"));
        Assert.Equal("Wanderschuhe, Regenjacke", new StreamReader(packliste.Open()).ReadToEnd());
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("Geheim"));

        // Wer nicht dabei ist, bekommt nichts
        Assert.False((await _fx.As("cleo").GetAsync($"/api/chat/rooms/{group}/export")).IsSuccessStatusCode);
    }
}
