using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Obergrenze je Hochlade-Link: Formular-Uploads, Uploads in Stücken und gleichzeitige Sitzungen zählen zusammen.</summary>
[Collection("server")]
public class LinkUploadLimitTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public LinkUploadLimitTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<string> UploadLink(string folder)
    {
        var owner = _fx.As("dora");
        await owner.PostAsJsonAsync("/api/files/mkdir", new { path = folder });
        return (await Json(await owner.PostAsJsonAsync("/api/files/links", new { path = folder, mode = "upload" }))).GetProperty("token").GetString()!;
    }

    private static MultipartFormDataContent Form(string name, int size) =>
        new() { { new ByteArrayContent(new byte[size]), "files", name } };

    // Die Verwaltung verlangt eine Browser-Anmeldung
    private async Task<HttpClient> Admin()
    {
        var c = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        (await c.PostAsJsonAsync("/api/auth/login", new { login = "anna", password = "geheim-123456" })).EnsureSuccessStatusCode();
        return c;
    }

    private async Task SetLimit(int mb)
    {
        var admin = await Admin();
        var limits = await Json(await admin.GetAsync("/api/admin/status"));
        var l = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(limits.GetProperty("fileLimits").GetRawText())!;
        var body = l.ToDictionary(x => x.Key, x => (object)x.Value);
        body["linkUploadMb"] = mb;
        Assert.Equal(mb, (await Json(await admin.PutAsJsonAsync("/api/admin/file-limits", body))).GetProperty("linkUploadMb").GetInt32());
    }

    private async Task WithLimit(int mb, Func<Task> test)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.UploadLimit));
        try { await SetLimit(mb); await test(); }
        finally { await SetLimit(0); Features.Set(before); }
    }

    [Fact]
    public Task Formular_und_Stuecke_zaehlen_gemeinsam_gegen_die_Grenze_des_Links() => WithLimit(1, async () =>
    {
        var token = await UploadLink("Grenze1");
        var anon = _fx.Factory.CreateClient();
        await Json(await anon.PostAsync($"/api/public/{token}/upload", Form("a.bin", 600 * 1024)));

        // Zweite Datei passt nicht mehr: 413 mit Meldung, nichts abgelegt
        var over = await anon.PostAsync($"/api/public/{token}/upload", Form("b.bin", 600 * 1024));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Contains("Obergrenze", await over.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").GetAsync("/api/files/download?path=Grenze1/b.bin")).StatusCode);

        // In Stücken: angekündigte Größe zu groß -> gleich beim Beginn abgelehnt; passende geht durch
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await anon.PostAsJsonAsync($"/api/public/{token}/uploads", new { name = "c.bin", size = 500 * 1024 })).StatusCode);
        var id = (await Json(await anon.PostAsJsonAsync($"/api/public/{token}/uploads", new { name = "d.bin", size = 300 * 1024 }))).GetProperty("id").GetString();
        await Json(await anon.PutAsync($"/api/public/{token}/uploads/{id}?offset=0", new ByteArrayContent(new byte[300 * 1024])));
        await Json(await anon.PostAsync($"/api/public/{token}/uploads/{id}/finish", null));

        // Jetzt 900 KB über diesen Link: auch 200 KB sind zu viel; ein anderer Link hat seine eigene Grenze
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await anon.PostAsync($"/api/public/{token}/upload", Form("e.bin", 200 * 1024))).StatusCode);
        var other = await UploadLink("Grenze2");
        await Json(await anon.PostAsync($"/api/public/{other}/upload", Form("f.bin", 600 * 1024)));

        // Grenze 0: keine eigene Grenze für den Link
        await SetLimit(0);
        await Json(await anon.PostAsync($"/api/public/{token}/upload", Form("g.bin", 600 * 1024)));
    });

    [Fact]
    public Task Gleichzeitige_Sitzungen_zaehlen_mit() => WithLimit(1, async () =>
    {
        var token = await UploadLink("Grenze3");
        var anon = _fx.Factory.CreateClient();
        var a = (await Json(await anon.PostAsJsonAsync($"/api/public/{token}/uploads", new { name = "a.bin", size = 700 * 1024 }))).GetProperty("id").GetString();
        // Die angekündigte Sitzung belegt ihren Platz: weder eine zweite Sitzung noch ein Formular-Upload passt daneben
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await anon.PostAsJsonAsync($"/api/public/{token}/uploads", new { name = "b.bin", size = 700 * 1024 })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await anon.PostAsync($"/api/public/{token}/upload", Form("c.bin", 400 * 1024))).StatusCode);

        // Viele Formular-Uploads zugleich: zusammen nie mehr als die Grenze
        await anon.DeleteAsync($"/api/public/{token}/uploads/{a}");
        var all = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => anon.PostAsync($"/api/public/{token}/upload", Form($"p{i}.bin", 300 * 1024))));
        Assert.InRange(all.Count(r => r.IsSuccessStatusCode), 0, 3);
        Assert.All(all.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode));
        var stored = DmOffice.Dav.FileStore.Children(_fx.Space("dora").Db, "Grenze3").Sum(f => f.Size);
        Assert.True(stored <= 1024 * 1024, $"{stored} Bytes abgelegt");
        Assert.Equal(all.Count(r => r.IsSuccessStatusCode) * 300 * 1024, stored);
    });
}
