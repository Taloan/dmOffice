using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Nur gezeigte Dateien lassen sich nur ansehen; offene Uploads je Benutzer sind begrenzt.</summary>
[Collection("server")]
public class ViewOnlyAndUploadLimitTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ViewOnlyAndUploadLimitTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Gezeigte_Datei_nur_ansehen_nicht_speichern()
    {
        var room = (await Json(await _fx.As("anna").PostAsJsonAsync("/api/chat/rooms", new { members = new[] { _fx.Id("ben") } }))).GetProperty("id").GetInt64();
        async Task<long> Send(string name, byte[] data) =>
            (await Json(await _fx.As("anna").PostAsync($"/api/chat/rooms/{room}/upload?name={name}&mode=show", new ByteArrayContent(data)))).GetProperty("id").GetInt64();
        var docx = await Send("Vertrag.docx", [0x50, 0x4b, 3, 4, 1, 2, 3]);
        var txt = await Send("Notiz.txt", "Hallo"u8.ToArray());
        // Mit inline=true kaeme die docx als Datei zum Speichern heraus - gesperrt
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").GetAsync($"/api/chat/messages/{docx}/refs/0/file?inline=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").GetAsync($"/api/chat/messages/{docx}/refs/0/file")).StatusCode);
        // Text ansehen geht, die Besitzerin darf alles
        Assert.True((await _fx.As("ben").GetAsync($"/api/chat/messages/{txt}/refs/0/file?inline=true")).IsSuccessStatusCode);
        Assert.True((await _fx.As("anna").GetAsync($"/api/chat/messages/{docx}/refs/0/file")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Hoechstens_zehn_offene_Uploads_je_Benutzer()
    {
        var ids = new List<string>();
        for (var i = 0; i < 10; i++)
            ids.Add((await Json(await _fx.As("cleo").PostAsJsonAsync("/api/files/uploads", new { name = $"t{i}.bin", size = 10, mime = "application/octet-stream" }))).GetProperty("id").GetString()!);
        Assert.Equal((HttpStatusCode)429, (await _fx.As("cleo").PostAsJsonAsync("/api/files/uploads", new { name = "zuviel.bin", size = 10 })).StatusCode);
        foreach (var id in ids) await _fx.As("cleo").DeleteAsync($"/api/files/uploads/{id}");
        Assert.True((await _fx.As("cleo").PostAsJsonAsync("/api/files/uploads", new { name = "wieder.bin", size = 10 })).IsSuccessStatusCode);
    }
}
