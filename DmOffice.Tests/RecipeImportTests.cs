using System.Net;
using System.Net.Http.Json;
using DmOffice.Notes;

namespace DmOffice.Tests;

/// <summary>Rezept per Link: schema.org-Rezept aus einer Seite lesen und als Notiz in notizi-Gestalt anlegen.</summary>
[Collection("server")]
public class RecipeImportTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public RecipeImportTests(ChatFixture fx) => _fx = fx;

    private const string Page = """
        <html><head><title>Kürbissuppe</title>
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"WebSite","name":"Kochseite"}</script>
        <script type="application/ld+json">
        {"@context":"https://schema.org","@graph":[
          {"@type":"Organization","name":"Kochseite"},
          {"@type":["Recipe","NewsArticle"],"name":"Kürbissuppe &amp; Ingwer","description":"<p>Cremig und schnell.</p>",
           "image":[{"@type":"ImageObject","url":"/bilder/suppe.jpg"}],
           "recipeYield":["4","4 Portionen"],"prepTime":"PT15M","cookTime":"PT1H5M","totalTime":"PT1H20M",
           "recipeIngredient":["1 Hokkaido (ca. 1 kg)","2 Zwiebeln","1 EL Ingwer, gerieben","500 ml Gemüsebrühe"],
           "recipeInstructions":[
             {"@type":"HowToSection","name":"Suppe","itemListElement":[
               {"@type":"HowToStep","text":"Kürbis würfeln."},{"@type":"HowToStep","text":"Mit Brühe 25 Minuten kochen."}]},
             {"@type":"HowToSection","name":"Anrichten","itemListElement":[{"@type":"HowToStep","text":"Pürieren &amp; servieren."}]}]}
        ]}
        </script></head><body>…</body></html>
        """;

    [Fact]
    public void Liest_das_Rezept_aus_JSON_LD()
    {
        var r = RecipeImport.FromHtml(Page, new Uri("https://www.kochseite.de/rezepte/kuerbissuppe"))!;
        Assert.Equal("Kürbissuppe & Ingwer", r.Name);
        Assert.Equal("Cremig und schnell.", r.Description);
        Assert.Equal("4 Portionen", r.Yield);
        Assert.Equal(("15 Min.", "1 Std. 5 Min.", "1 Std. 20 Min."), (r.PrepTime, r.CookTime, r.TotalTime));
        Assert.Equal(4, r.Ingredients.Single().Items.Count);
        Assert.Equal(["Suppe", "Anrichten"], r.Steps.Select(s => s.Name));
        Assert.Equal("Pürieren & servieren.", r.Steps[1].Items.Single());
        Assert.Equal("https://www.kochseite.de/bilder/suppe.jpg", r.Image);
    }

    [Fact]
    public void Notiz_hat_die_Gestalt_der_Vorlage()
    {
        var r = RecipeImport.FromHtml(Page, new Uri("https://www.kochseite.de/rezepte/kuerbissuppe"))!;
        var (html, plain) = RecipeImport.ToNote(r, null);
        Assert.StartsWith("<div data-notizi=\"1\"><p>Kürbissuppe &amp; Ingwer</p>", html);
        Assert.Contains("<h3>Zutaten</h3><ul><li>☐ 1 Hokkaido (ca. 1 kg)</li>", html);
        Assert.Contains("<h4>Anrichten</h4><ol><li>Pürieren &amp; servieren.</li></ol>", html);
        Assert.Contains("Rezept von <a href=\"https://www.kochseite.de/rezepte/kuerbissuppe\">kochseite.de</a>", html);
        // Essensplan und Einkaufsliste erkennen es wie ein Rezept aus notizi
        Assert.True(Recipes.IsRecipe(plain.Replace("\r", "")));
        var note = new NoteRow { Id = 1, SyncId = "x", Html = html, Plain = plain };
        Assert.Equal(["1 Hokkaido (ca. 1 kg)", "2 Zwiebeln", "1 EL Ingwer, gerieben", "500 ml Gemüsebrühe"], Recipes.Ingredients(note));
    }

    [Fact]
    public void Ohne_Rezept_und_Dauer()
    {
        Assert.Null(RecipeImport.FromHtml("<html><script type=\"application/ld+json\">{\"@type\":\"Article\"}</script></html>", new Uri("https://x.de/")));
        Assert.Null(RecipeImport.FromHtml("<html><script type=\"application/ld+json\">{kaputt</script></html>", new Uri("https://x.de/")));
        Assert.Equal("45 Min.", RecipeImport.Duration("P0DT0H45M"));
        Assert.Equal("2 Std.", RecipeImport.Duration("PT120M"));
        Assert.Equal("", RecipeImport.Duration("PT0M"));
        Assert.Equal("ca. 30 Minuten", RecipeImport.Duration("ca. 30 Minuten"));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.1.50", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.110.106.122", false)]
    [InlineData("::1", false)]
    [InlineData("fd12::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2a00:1450:4001::1", true)]
    public void Nur_Adressen_im_Internet(string ip, bool ok) => Assert.Equal(ok, RecipeImport.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task Link_ins_eigene_Netz_wird_abgelehnt()
    {
        var r = await _fx.As("ben").PostAsJsonAsync("/api/notes/recipes/import", new { url = "http://127.0.0.1:8080/" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await _fx.As("ben").PostAsJsonAsync("/api/notes/recipes/import", new { url = "ftp://example.org/rezept" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
