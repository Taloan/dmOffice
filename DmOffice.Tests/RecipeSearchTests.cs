using System.Text.Json;
using DmOffice.Notes;
using Xunit;

namespace DmOffice.Tests;

/// <summary>Rezeptsuche: Treffer aus DuckDuckGo, nur Seiten mit schema.org-Rezept, Nährwerte geschätzt, Bilder nur aus der Suche.</summary>
[Collection("server")]
public class RecipeSearchTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public RecipeSearchTests(ChatFixture fx) => _fx = fx;

    private const string Ddg = """
        <div class="result"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fkoch.example%2Fruehrei&amp;rut=x">Rührei</a></div>
        <div class="result"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fblog.example%2Fohne-rezept&amp;rut=y">Blog</a></div>
        <div class="result"><a class="result__a" href="https://duckduckgo.com/y.js?ad=1">Anzeige</a></div>
        """;

    private const string Page = """
        <html><head><script type="application/ld+json">
        {"@context":"https://schema.org","@type":"Recipe","name":"Rührei","recipeYield":"2 Portionen","totalTime":"PT10M",
         "image":"https://koch.example/ruehrei.jpg","recipeIngredient":["4 Eier","2 EL Milch","1 Prise Salz","10 g Butter"],
         "recipeInstructions":[{"@type":"HowToStep","text":"Verquirlen."},{"@type":"HowToStep","text":"Stocken lassen."}]}
        </script></head><body></body></html>
        """;

    [Fact]
    public void Treffer_aus_DuckDuckGo()
    {
        Assert.Equal(["https://koch.example/ruehrei", "https://blog.example/ohne-rezept"], RecipeSearch.ParseDuckDuckGo(Ddg));
    }

    [Fact]
    public async Task Suche_liefert_nur_Rezepte_mit_Naehrwerten()
    {
        var c = _fx.As("ben");
        RecipeSearch.SearchFetch = (q, _) => Task.FromResult(Ddg);
        RecipeSearch.PageFetch = (u, _) => Task.FromResult(u.Contains("ruehrei") ? Page : "<html><body>Kein Rezept</body></html>");
        try
        {
            var r = await c.GetAsync("/api/notes/recipes/search?q=R%C3%BChrei");
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.IsSuccessStatusCode, body);
            var hits = JsonDocument.Parse(body).RootElement.GetProperty("hits").EnumerateArray().ToList();
            var h = Assert.Single(hits);
            Assert.Equal("Rührei", h.GetProperty("name").GetString());
            Assert.Equal("koch.example", h.GetProperty("source").GetString());
            Assert.Equal(2, h.GetProperty("servings").GetInt32());
            Assert.Equal(4, h.GetProperty("ingredients").GetArrayLength());
            // 4 Eier, Milch, Butter für 2 Portionen: grob 150-250 kcal je Portion
            Assert.InRange(h.GetProperty("kcal").GetDouble(), 150, 260);
            // Fremde Bildadressen holt der Server nicht
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await c.GetAsync("/api/notes/recipes/search/image?u=https%3A%2F%2Fevil.example%2Fx.jpg")).StatusCode);
        }
        finally { RecipeSearch.SearchFetch = null; RecipeSearch.PageFetch = null; }
    }
}
