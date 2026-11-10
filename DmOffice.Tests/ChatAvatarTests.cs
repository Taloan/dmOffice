using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Profilbilder: hochladen (wird WebP), für andere abrufbar, mit Version und Akzentfarbe in den Mitgliedern; entfernen.</summary>
[Collection("server")]
public class ChatAvatarTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChatAvatarTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static byte[] Png(int width, int height)
    {
        using var bmp = new SKBitmap(width, height);
        using (var c = new SKCanvas(bmp)) c.Clear(new SKColor(0x7B, 0x1D, 0xD3));
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private async Task<JsonElement> MemberAs(string login, long room, string of) =>
        (await Json(await _fx.As(login).GetAsync($"/api/chat/rooms/{room}"))).GetProperty("members").EnumerateArray().Single(m => m.GetProperty("id").GetString() == _fx.Id(of));

    [Fact]
    public async Task Profilbild_hochladen_zeigen_entfernen()
    {
        var room = (await Json(await _fx.As("anna").PostAsJsonAsync("/api/chat/rooms", new { members = new[] { _fx.Id("ben") } }))).GetProperty("id").GetInt64();
        var before = await MemberAs("ben", room, "anna");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("avatar").ValueKind);

        // Akzentfarbe aus den Oberflächen-Einstellungen
        await _fx.As("anna").PutAsync("/api/settings/ui", new StringContent("{\"accent\":\"#E08A00\"}", Encoding.UTF8, "application/json"));
        Assert.Equal("#E08A00", (await MemberAs("ben", room, "anna")).GetProperty("accent").GetString());

        // Kein Bild: abgelehnt
        Assert.False((await _fx.As("anna").PutAsync("/api/chat/people/me/avatar", new ByteArrayContent(Encoding.UTF8.GetBytes("kein Bild")))).IsSuccessStatusCode);

        var version = (await Json(await _fx.As("anna").PutAsync("/api/chat/people/me/avatar", new ByteArrayContent(Png(1200, 700))))).GetProperty("avatar").GetString();
        Assert.False(string.IsNullOrEmpty(version));
        Assert.Equal(version, (await MemberAs("ben", room, "anna")).GetProperty("avatar").GetString());
        Assert.Contains((await Json(await _fx.As("ben").GetAsync("/api/chat/people"))).EnumerateArray(), p => p.GetProperty("avatar").GetString() == version);

        var img = await _fx.As("ben").GetAsync($"/api/chat/people/{_fx.Id("anna")}/avatar?v={version}");
        Assert.True(img.IsSuccessStatusCode);
        Assert.Equal("image/webp", img.Content.Headers.ContentType?.MediaType);
        using (var bmp = SKBitmap.Decode(await img.Content.ReadAsByteArrayAsync())) { Assert.Equal(256, bmp.Width); Assert.Equal(256, bmp.Height); }

        await _fx.As("anna").DeleteAsync("/api/chat/people/me/avatar");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync($"/api/chat/people/{_fx.Id("anna")}/avatar")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await MemberAs("ben", room, "anna")).GetProperty("avatar").ValueKind);
    }

    [Fact]
    public async Task Pukkis_Figur_waehlt_jeder_fuer_sich()
    {
        // Vorgabe: der Panda
        Assert.Equal("panda", (await Json(await _fx.As("anna").GetAsync("/api/chat/status"))).GetProperty("pukkiFigure").GetString());

        Assert.False((await _fx.As("anna").PutAsJsonAsync("/api/chat/pukki/figure", new { figure = "drache" })).IsSuccessStatusCode);
        await Json(await _fx.As("anna").PutAsJsonAsync("/api/chat/pukki/figure", new { figure = "walross" }));
        Assert.Equal("walross", (await Json(await _fx.As("anna").GetAsync("/api/chat/status"))).GetProperty("pukkiFigure").GetString());

        // Ben behaelt seinen Panda; Chat-Einstellungen speichern setzt die Figur nicht zurueck
        Assert.Equal("panda", (await Json(await _fx.As("ben").GetAsync("/api/chat/status"))).GetProperty("pukkiFigure").GetString());
        var settings = await Json(await _fx.As("anna").GetAsync("/api/chat/settings"));
        await Json(await _fx.As("anna").PutAsync("/api/chat/settings", new StringContent(settings.GetProperty("settings").GetRawText(), Encoding.UTF8, "application/json")));
        Assert.Equal("walross", (await Json(await _fx.As("anna").GetAsync("/api/chat/status"))).GetProperty("pukkiFigure").GetString());
    }
}
