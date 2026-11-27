using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Schnellsammlung (Kennung quickcoll): eine je Benutzer, vom System angelegt, Taste B schaltet hinein/heraus.</summary>
[Collection("server")]
public class QuickCollectionTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public QuickCollectionTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static byte[] Jpeg()
    {
        using var bmp = new SKBitmap(60, 40);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Orange);
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static async Task Upload(HttpClient c, string dir, string name)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Jpeg()), "files", name } };
        Assert.True((await c.PostAsync($"/api/files/upload?path={Uri.EscapeDataString(dir)}", form)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Schnellsammlung_umschalten_und_nicht_aenderbar()
    {
        var c = _fx.As("ben");
        var other = _fx.As("anna");
        await Upload(c, "Quick", "a.jpg");
        await Upload(c, "Quick", "b.jpg");

        // Vom System angelegt, genau eine, steht vorn
        var list = (await Json(await c.GetAsync("/api/gallery/albums"))).EnumerateArray().ToList();
        Assert.Single(list, a => a.GetProperty("kind").GetString() == "quick");
        Assert.Equal("quick", list[0].GetProperty("kind").GetString());
        var id = list[0].GetProperty("id").GetInt64();
        Assert.Single((await Json(await c.GetAsync("/api/gallery/albums"))).EnumerateArray(), a => a.GetProperty("kind").GetString() == "quick");

        // B: hinein; nur eins drin → beide hinein; beide drin → beide heraus
        var r = await Json(await c.PostAsJsonAsync("/api/gallery/albums/quick/toggle", new { paths = new[] { "Quick/a.jpg" } }));
        Assert.True(r.GetProperty("in").GetBoolean());
        r = await Json(await c.PostAsJsonAsync("/api/gallery/albums/quick/toggle", new { paths = new[] { "Quick/a.jpg", "Quick/b.jpg" } }));
        Assert.True(r.GetProperty("in").GetBoolean());
        var paths = (await Json(await c.GetAsync("/api/gallery/albums/quick"))).GetProperty("paths").EnumerateArray().Select(p => p.GetString()).Order().ToList();
        Assert.Equal(["Quick/a.jpg", "Quick/b.jpg"], paths);

        // Mit Vorgabe (das Foto unter dem Cursor bestimmt): hinein bleibt hinein
        r = await Json(await c.PostAsJsonAsync("/api/gallery/albums/quick/toggle", new { paths = new[] { "Quick/a.jpg" }, @in = true }));
        Assert.True(r.GetProperty("in").GetBoolean());
        Assert.Equal(2, r.GetProperty("count").GetInt32());

        // Aus der Ansicht der Schnellsammlung selbst (alb:-Pfad) wieder heraus
        var items = (await Json(await c.GetAsync($"/api/gallery/albums/{id}/items"))).GetProperty("items").EnumerateArray().ToList();
        var key = items.Single(i => i.GetProperty("name").GetString() == "b.jpg").GetProperty("key").GetString();
        Assert.Equal("Quick/b.jpg", items.Single(i => i.GetProperty("name").GetString() == "b.jpg").GetProperty("path").GetString());
        r = await Json(await c.PostAsJsonAsync("/api/gallery/albums/quick/toggle", new { paths = new[] { $"alb:{id}/{key}" } }));
        Assert.False(r.GetProperty("in").GetBoolean());
        Assert.Equal(1, r.GetProperty("count").GetInt32());

        // Nicht umbenennen, nicht teilen, nicht löschen, nicht öffentlich zeigen
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/gallery/albums/{id}", new { name = "Anders" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/gallery/albums/{id}", new { members = new[] { _fx.Id("anna") } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.DeleteAsync($"/api/gallery/albums/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/gallery/albums/{id}/links", new { download = false })).StatusCode);
        Assert.Equal(1, (await Json(await c.GetAsync($"/api/gallery/albums/{id}/items"))).GetProperty("items").GetArrayLength());

        // Andere sehen sie nicht und kommen nicht an ein fremdes Foto per alb:-Pfad
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/gallery/albums/{id}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync("/api/gallery/albums/quick/toggle", new { paths = new[] { $"alb:{id}/{key}" } })).StatusCode);
    }
}
