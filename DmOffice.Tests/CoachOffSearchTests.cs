using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Coach;
using SkiaSharp;
using Xunit;

namespace DmOffice.Tests;

/// <summary>Coach: Markenprodukte bei Open Food Facts nach Namen, Produktbilder (geholt und verkleinert), eigene Bilder.</summary>
[Collection("server")]
public class CoachOffSearchTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CoachOffSearchTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(SKColors.OrangeRed);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 90).ToArray();
    }

    [Fact]
    public async Task Suche_bei_Open_Food_Facts_mit_Bildern()
    {
        var c = _fx.As("ben");
        var searches = 0;
        var fetched = new List<string>();
        OpenFoodFacts.SearchFetch = (q, _) =>
        {
            searches++;
            return Task.FromResult<string?>(q != "tomatensuppe" ? """{"products":[]}""" : """
                {"products":[
                  {"code":"4000000000123","product_name":"Tomatensuppe","brands":"Testmarke","nutriments":{"energy-kcal_100g":45,"proteins_100g":1,"carbohydrates_100g":7,"fat_100g":1.2},
                   "image_front_small_url":"https://images.openfoodfacts.org/images/products/400/000/000/0123/front_de.3.200.jpg"},
                  {"code":"4000000000130","product_name":"Suppe ohne Bild","nutriments":{"energy-kcal_100g":60},"image_front_small_url":"https://example.org/x.jpg"},
                  {"code":"4000000000147","product_name":"Ohne Nährwerte","nutriments":{}},
                  {"code":"abc","product_name":"Kein Barcode","nutriments":{"energy-kcal_100g":10}}
                ]}
                """);
        };
        OpenFoodFacts.ImageFetch = (url, _) => { fetched.Add(url); return Task.FromResult<byte[]?>(Png(800, 600)); };
        try
        {
            var r = await Json(await c.GetAsync("/api/coach/foods/search/off?q=Tomatensuppe"));
            Assert.False(r.GetProperty("limited").GetBoolean());
            var hits = r.GetProperty("hits").EnumerateArray().ToList();
            // Ohne Energie und ohne gültigen Barcode: weggelassen
            Assert.Equal(["off:4000000000123", "off:4000000000130"], hits.Select(h => h.GetProperty("id").GetString()));
            var image = hits[0].GetProperty("image").GetString()!;
            // Fremde Bildadresse wird nicht übernommen
            Assert.Equal(JsonValueKind.Null, hits[1].GetProperty("image").ValueKind);

            // Gleiche Suche: aus dem Zwischenspeicher
            await Json(await c.GetAsync("/api/coach/foods/search/off?q=tomatensuppe"));
            Assert.Equal(1, searches);
            // Zu kurz: gar nicht erst gefragt
            Assert.Empty((await Json(await c.GetAsync("/api/coach/foods/search/off?q=to"))).GetProperty("hits").EnumerateArray());
            Assert.Equal(1, searches);

            // Gefundenes wird gemerkt: in der eigenen Suche und per Barcode ohne neue Anfrage, mit Bild
            var local = (await Json(await c.GetAsync("/api/coach/foods/search?q=tomatensuppe"))).EnumerateArray().ToList();
            Assert.Contains(local, h => h.GetProperty("id").GetString() == "off:4000000000123" && h.GetProperty("image").GetString() == image);

            // Bild: einmal geholt, auf 200 px verkleinert (WebP), danach aus dem Speicher
            var res = await c.GetAsync(image);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("image/webp", res.Content.Headers.ContentType?.MediaType);
            using (var bmp = SKBitmap.Decode(await res.Content.ReadAsByteArrayAsync())) Assert.Equal(200, Math.Max(bmp.Width, bmp.Height));
            (await c.GetAsync(image)).EnsureSuccessStatusCode();
            Assert.Single(fetched);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/coach/foods/off%3A4000000000130/image")).StatusCode);

            // Höchstens 10 Suchen pro Minute: danach limited statt warten
            JsonElement last = default;
            for (var i = 0; i < 11; i++) last = await Json(await c.GetAsync($"/api/coach/foods/search/off?q=suppe{i}"));
            Assert.True(last.GetProperty("limited").GetBoolean());
        }
        finally { OpenFoodFacts.SearchFetch = null; OpenFoodFacts.ImageFetch = null; }
    }

    [Fact]
    public async Task Eigenes_Lebensmittel_mit_Bild()
    {
        var c = _fx.As("ben");
        var own = await Json(await c.PostAsJsonAsync("/api/coach/foods", new
        {
            name = "Bilderkuchen", per100 = new { kcal = 300, protein = 5, fat = 12, carbs = 40 }, portions = Array.Empty<object>(), liquid = false,
        }));
        var id = own.GetProperty("id").GetString()!;
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png(1200, 400));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "kuchen.png");
        var up = await Json(await c.PostAsync($"/api/coach/foods/{Uri.EscapeDataString(id)}/image", form));
        var path = up.GetProperty("image").GetString()!;
        var res = await c.GetAsync(path);
        res.EnsureSuccessStatusCode();
        using (var bmp = SKBitmap.Decode(await res.Content.ReadAsByteArrayAsync())) Assert.Equal(200, bmp.Width);
        // Auch andere im Haushalt sehen es in der Suche
        var hits = (await Json(await _fx.As("anna").GetAsync("/api/coach/foods/search?q=bilderkuchen"))).EnumerateArray().ToList();
        Assert.Equal(path, hits.Single(h => h.GetProperty("id").GetString() == id).GetProperty("image").GetString());

        // Kein Bild: abgelehnt; Bilder nur für eigene Lebensmittel
        var bad = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "x.png" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/coach/foods/{Uri.EscapeDataString(id)}/image", bad)).StatusCode);
        var bls = new MultipartFormDataContent { { new ByteArrayContent(Png(10, 10)), "file", "x.png" } };
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/coach/foods/bls%3AC133000/image", bls)).StatusCode);

        // Löschen nimmt das Bild mit
        (await c.DeleteAsync($"/api/coach/foods/{Uri.EscapeDataString(id)}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(path)).StatusCode);
    }

    [Fact]
    public void Nutri_Score_Eco_Score_und_Merkmale()
    {
        var food = OpenFoodFacts.Parse("4000000000500", """
            {"status":1,"product":{"product_name":"Tomatensauce","nutriments":{"energy-kcal_100g":45},"nutriscore_grade":"a","environmental_score_grade":"b",
             "nova_group":3,"ingredients_analysis_tags":["en:palm-oil-free","en:vegan"],"labels_tags":["en:no-gluten","en:eu-organic"]}}
            """)!;
        Assert.Equal("a", food.Info!.Nutri);
        Assert.Equal("b", food.Info.Eco);
        Assert.Equal(3, food.Info.Nova);
        Assert.Equal(["palm-oil-free", "vegan", "gluten-free", "organic"], food.Info.Flags);
        // „unknown“ und Ähnliches: keine Note
        var none = OpenFoodFacts.Parse("4000000000517", """{"status":1,"product":{"product_name":"X","nutriments":{"energy-kcal_100g":10},"nutriscore_grade":"unknown"}}""")!;
        Assert.Null(none.Info);
    }
}
