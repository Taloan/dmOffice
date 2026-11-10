using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Gallery;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Alben: feste (Mitglieder legen eigene dazu, nehmen nur eigene heraus), smarte (Filter auf dem Server), Zeigen im Chat.</summary>
[Collection("server")]
public class AlbumTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public AlbumTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        var text = await r.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static byte[] Jpeg(SKColor color)
    {
        using var bmp = new SKBitmap(300, 200);
        using (var c = new SKCanvas(bmp)) c.Clear(color);
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static async Task Upload(HttpClient c, string dir, string name, byte[] data)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(data), "files", name } };
        Assert.True((await c.PostAsync($"/api/files/upload?path={Uri.EscapeDataString(dir)}", form)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Festes_Album_teilen_dazulegen_herausnehmen_und_im_Chat_zeigen()
    {
        var ben = _fx.As("ben");
        var anna = _fx.As("anna");
        await Upload(ben, "Alb", "see.jpg", Jpeg(SKColors.Blue));
        await Upload(anna, "Alb", "berg.jpg", Jpeg(SKColors.Green));

        var id = (await Json(await ben.PostAsJsonAsync("/api/gallery/albums", new { name = "Urlaub" }))).GetProperty("id").GetInt64();
        Assert.Equal(1, (await Json(await ben.PostAsJsonAsync($"/api/gallery/albums/{id}/add", new { paths = new[] { "Alb/see.jpg", "Alb/gibtsnicht.jpg" } }))).GetProperty("added").GetInt32());

        // Anna sieht es noch nicht; nach dem Teilen schon - und legt eigenes dazu
        Assert.DoesNotContain((await Json(await anna.GetAsync("/api/gallery/albums"))).EnumerateArray(), a => a.GetProperty("id").GetInt64() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.GetAsync($"/api/gallery/albums/{id}/items")).StatusCode);
        await Json(await ben.PutAsJsonAsync($"/api/gallery/albums/{id}", new { members = new[] { _fx.Id("anna") } }));
        await Json(await anna.PostAsJsonAsync($"/api/gallery/albums/{id}/add", new { paths = new[] { "Alb/berg.jpg" } }));
        var items = (await Json(await anna.GetAsync($"/api/gallery/albums/{id}/items"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["berg.jpg", "see.jpg"], items.Select(i => i.GetProperty("name").GetString()).Order());
        var benKey = items.Single(i => i.GetProperty("name").GetString() == "see.jpg").GetProperty("key").GetString()!;
        var annaKey = items.Single(i => i.GetProperty("name").GetString() == "berg.jpg").GetProperty("key").GetString()!;

        // Vorschau und Original von Bens Foto für Anna - nur über das Album
        var thumb = await anna.GetAsync($"/api/gallery/albums/{id}/thumb?key={Uri.EscapeDataString(benKey)}");
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync($"/api/gallery/albums/{id}/file?inline=true&key={Uri.EscapeDataString(benKey)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.GetAsync($"/api/gallery/albums/{id}/thumb?key={Uri.EscapeDataString(_fx.Id("ben") + "|f:999999")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("cleo").GetAsync($"/api/gallery/albums/{id}/thumb?key={Uri.EscapeDataString(benKey)}")).StatusCode);

        // Anna darf Bens Foto nicht herausnehmen, ihr eigenes schon; umbenennen darf nur Ben
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.PostAsJsonAsync($"/api/gallery/albums/{id}/remove", new { keys = new[] { benKey } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.PutAsJsonAsync($"/api/gallery/albums/{id}", new { name = "Meins" })).StatusCode);

        // Im Chat zeigen: Cleo sieht Liste und Bild, Herunterladen nicht; Senden wird zu Zeigen
        var room = (await Json(await ben.PostAsJsonAsync("/api/chat/rooms", new { members = new[] { _fx.Id("cleo") } }))).GetProperty("id").GetInt64();
        var msg = await Json(await ben.PostAsJsonAsync($"/api/chat/rooms/{room}/messages", new { text = "Schaut mal", refs = new[] { new { type = "album", fileId = id, mode = "send" } } }));
        Assert.Equal("show", msg.GetProperty("refs")[0].GetProperty("mode").GetString());
        var mid = msg.GetProperty("id").GetInt64();
        var cleo = _fx.As("cleo");
        var shown = await Json(await cleo.GetAsync($"/api/chat/messages/{mid}/refs/0/album"));
        Assert.Equal(2, shown.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await cleo.GetAsync($"/api/chat/messages/{mid}/refs/0/album/thumb?key={Uri.EscapeDataString(annaKey)}")).StatusCode);
        var file = await cleo.GetAsync($"/api/chat/messages/{mid}/refs/0/album/file?key={Uri.EscapeDataString(benKey)}");
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType?.MediaType);

        // Ben nimmt Annas Foto heraus; Anna steigt aus, Ben löscht - danach ist im Chat nichts mehr zu sehen
        await Json(await ben.PostAsJsonAsync($"/api/gallery/albums/{id}/remove", new { keys = new[] { annaKey } }));
        Assert.Single((await Json(await ben.GetAsync($"/api/gallery/albums/{id}/items"))).GetProperty("items").EnumerateArray());
        await Json(await anna.DeleteAsync($"/api/gallery/albums/{id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await anna.GetAsync($"/api/gallery/albums/{id}/items")).StatusCode);
        await Json(await ben.DeleteAsync($"/api/gallery/albums/{id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await cleo.GetAsync($"/api/chat/messages/{mid}/refs/0/album")).StatusCode);
    }

    [Fact]
    public async Task Smart_Album_findet_nach_Filter_und_ist_fuer_Mitglieder_nur_zum_Ansehen()
    {
        var dora = _fx.As("dora");
        await Upload(dora, "Smart", "a.jpg", Jpeg(SKColors.Red));
        await Upload(dora, "Smart", "b.png", Jpeg(SKColors.Red));
        var rule = JsonSerializer.Serialize(new { cols = new[] { "type", "year", "camera", "keyword" }, sel = new[] { new[] { "JPG" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>() } });
        var id = (await Json(await dora.PostAsJsonAsync("/api/gallery/albums", new { name = "Nur JPG", kind = "smart", rule }))).GetProperty("id").GetInt64();
        var names = (await Json(await dora.GetAsync($"/api/gallery/albums/{id}/items"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
        Assert.Contains("a.jpg", names);
        Assert.DoesNotContain("b.png", names);
        Assert.Equal(HttpStatusCode.BadRequest, (await dora.PostAsJsonAsync($"/api/gallery/albums/{id}/add", new { paths = new[] { "Smart/b.png" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await dora.PostAsJsonAsync("/api/gallery/albums", new { name = "Leer", kind = "smart" })).StatusCode);

        // Werte wie die Filterleiste im Web
        var p = new AlbumApi.Photo("u", "f:1", "x.ARW", 1, null, DateTime.UtcNow, "e",
            new Dav.FileMeta.Row("x.ARW", null, "Fujifilm X-T5", null, 35, 2.8, 0.004, 400, null, null, 600, 400, null, 3, null, "Urlaub\nItalien"), null, "x.ARW", null);
        var zone = TimeZoneInfo.Utc;
        Assert.Equal(["2.8"], AlbumApi.Values("aperture", p, zone));
        Assert.Equal(["0.004"], AlbumApi.Values("exposure", p, zone));
        Assert.Equal(["ARW"], AlbumApi.Values("type", p, zone));
        Assert.Equal(["quer"], AlbumApi.Values("orientation", p, zone));
        Assert.Equal(["Urlaub", "Italien"], AlbumApi.Values("keyword", p, zone));
        Assert.Equal(["3"], AlbumApi.Values("rating", p, zone));
    }
}
