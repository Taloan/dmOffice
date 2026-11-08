using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Alben öffentlich zeigen: Link anlegen, ohne Anmeldung ansehen, Passwort, Herunterladen nur mit Erlaubnis, löschen.</summary>
[Collection("server")]
public class AlbumLinkTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public AlbumLinkTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        var text = await r.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Album_oeffentlich_zeigen()
    {
        var ben = _fx.As("ben");
        using var bmp = new SKBitmap(300, 200);
        using (var cv = new SKCanvas(bmp)) cv.Clear(SKColors.Orange);
        var form = new MultipartFormDataContent { { new ByteArrayContent(bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray()), "files", "sonne.jpg" } };
        Assert.True((await ben.PostAsync("/api/files/upload?path=AlbLink", form)).IsSuccessStatusCode);
        var id = (await Json(await ben.PostAsJsonAsync("/api/gallery/albums", new { name = "Sommer" }))).GetProperty("id").GetInt64();
        await Json(await ben.PostAsJsonAsync($"/api/gallery/albums/{id}/add", new { paths = new[] { "AlbLink/sonne.jpg" } }));

        // Nur der Besitzer legt Links an
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("cleo").PostAsJsonAsync($"/api/gallery/albums/{id}/links", new { download = false })).StatusCode);
        var link = await Json(await ben.PostAsJsonAsync($"/api/gallery/albums/{id}/links", new { days = 7, download = false }));
        var token = link.GetProperty("token").GetString()!;

        // Ohne Anmeldung: Liste und Vorschau ja, keine Pfade oder Benutzerkennungen, Original nein
        var anon = _fx.Factory.CreateClient();
        var page = await Json(await anon.GetAsync($"/api/public-album/{token}/"));
        Assert.Equal("Sommer", page.GetProperty("name").GetString());
        var item = page.GetProperty("items").EnumerateArray().Single();
        Assert.DoesNotContain(_fx.Id("ben"), item.ToString());
        Assert.DoesNotContain("AlbLink", item.ToString());
        var k = item.GetProperty("k").GetString()!;
        Assert.Equal("image/webp", (await anon.GetAsync($"/api/public-album/{token}/thumb?k={k}")).Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync($"/api/public-album/{token}/file?k={k}&download=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public-album/{token}/thumb?k=gibtsnicht")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public-album/{token[..^2]}xx/")).StatusCode);

        // Mit Passwort und Herunterladen
        var link2 = await Json(await ben.PostAsJsonAsync($"/api/gallery/albums/{id}/links", new { password = "geheim-123", download = true }));
        var token2 = link2.GetProperty("token").GetString()!;
        var visitor = _fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"/api/public-album/{token2}/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.PostAsJsonAsync($"/api/public-album/{token2}/auth", new { password = "falsch" })).StatusCode);
        await Json(await visitor.PostAsJsonAsync($"/api/public-album/{token2}/auth", new { password = "geheim-123" }));
        var k2 = (await Json(await visitor.GetAsync($"/api/public-album/{token2}/"))).GetProperty("items").EnumerateArray().Single().GetProperty("k").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/public-album/{token2}/file?k={k2}&download=true")).StatusCode);

        // Löschen: danach weg
        Assert.Equal(2, (await Json(await ben.GetAsync($"/api/gallery/albums/{id}/links"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await ben.DeleteAsync($"/api/gallery/albums/{id}/links/{link.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public-album/{token}/")).StatusCode);
    }

    [Fact]
    public async Task Papierkorb_zeigt_Vorschaubild_geloeschter_Fotos()
    {
        var dora = _fx.As("dora");
        using var bmp = new SKBitmap(300, 200);
        using (var cv = new SKCanvas(bmp)) cv.Clear(SKColors.Teal);
        var form = new MultipartFormDataContent { { new ByteArrayContent(bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray()), "files", "weg.jpg" } };
        Assert.True((await dora.PostAsync("/api/files/upload?path=Trash1", form)).IsSuccessStatusCode);
        Assert.Equal("image/webp", (await dora.GetAsync("/api/files/thumb?path=Trash1%2Fweg.jpg&size=256")).Content.Headers.ContentType?.MediaType);
        await Json(await dora.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "Trash1/weg.jpg" } }));
        var id = (await Json(await dora.GetAsync("/api/files/trash"))).GetProperty("items").EnumerateArray().First(i => i.GetProperty("name").GetString() == "weg.jpg").GetProperty("id").GetInt64();
        Assert.Equal("image/webp", (await dora.GetAsync($"/api/files/trash/{id}/thumb?size=256")).Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync($"/api/files/trash/{id}/thumb")).StatusCode);
    }
}
