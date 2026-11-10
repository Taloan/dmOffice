using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Notes;

namespace DmOffice.Tests;

/// <summary>
/// Rezept-Scan: Erkennung auf im Test erzeugten Texten bzw. TSV (keine echten Kochbuchseiten), Notiz in Rezeptgestalt,
/// Routen; Ende-zu-Ende mit Tesseract nur, wo es installiert ist.
/// </summary>
[Collection("server")]
public class RecipeScanTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public RecipeScanTests(ChatFixture fx) => _fx = fx;

    private static RecipeScan.Draft Scan(string text) => RecipeScan.Analyze(RecipeScan.FromText(text)).Draft;

    private static string[] Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Mit_Ueberschriften()
    {
        var d = Scan("""
            Kartoffelgratin
            Zubereitungszeit: 20 Min. | Backzeit: 45 Minuten

            Zutaten für 4 Personen
            1 kg festkochende Kartoffeln
            200 ml Sahne
            100 g geriebener Gouda
            1 Knoblauchzehe
            Salz, Pfeffer
            Muskatnuss

            Zubereitung
            Den Backofen auf 180 °C vorheizen. Die Kartoffeln schälen und in dünne Scheiben schneiden.
            Die Sahne mit Knoblauch, Salz, Pfeffer und Muskat würzen.

            Kartoffeln in eine Form schichten, die Sahne darübergießen und mit Käse bestreuen.

            Tipp: Statt Gouda schmeckt auch Bergkäse.
            """);
        Assert.Equal("Kartoffelgratin", d.Title);
        Assert.Equal("4 Portionen", d.Yield);
        Assert.Equal(("20 Min.", "45 Min."), (d.PrepTime, d.CookTime));
        Assert.Equal(["1 kg festkochende Kartoffeln", "200 ml Sahne", "100 g geriebener Gouda", "1 Knoblauchzehe", "Salz, Pfeffer", "Muskatnuss"], Lines(d.Ingredients));
        var steps = Lines(d.Steps);
        Assert.Equal(2, steps.Length);
        Assert.StartsWith("Den Backofen auf 180 °C vorheizen.", steps[0]);
        Assert.EndsWith("Muskat würzen.", steps[0]);
        Assert.StartsWith("Kartoffeln in eine Form", steps[1]);
        Assert.Equal("Statt Gouda schmeckt auch Bergkäse.", d.Tip);
    }

    [Fact]
    public void Titel_nach_Schriftgroesse()
    {
        // Kapitelname klein oben, Titel groß darunter
        var lines = new List<RecipeScan.Line>
        {
            new(1, 1, 100, 50, 18, "Hauptgerichte"),
            new(1, 2, 100, 120, 60, "Kartoffelgratin"),
            new(1, 3, 100, 220, 22, "Ein Klassiker, der immer gelingt und allen schmeckt."),
            new(1, 4, 100, 300, 22, "Für 4 Personen"),
            new(1, 5, 100, 340, 22, "1 kg Kartoffeln"),
            new(1, 5, 100, 370, 22, "200 ml Sahne"),
            new(1, 6, 100, 900, 22, "Die Kartoffeln schälen, in Scheiben schneiden und mit Sahne in eine Form geben."),
        };
        var (d, hints) = RecipeScan.Analyze(lines);
        Assert.Equal("Kartoffelgratin", d.Title);
        Assert.Contains("Hauptgerichte", d.Description);
        Assert.Contains("Ein Klassiker", d.Description);
        Assert.Equal(["1 kg Kartoffeln", "200 ml Sahne"], Lines(d.Ingredients));
        Assert.Single(Lines(d.Steps));
        Assert.Empty(hints);
    }

    [Fact]
    public void Ohne_Ueberschriften_und_nummerierte_Schritte()
    {
        var d = Scan("""
            Pfannkuchen

            250 g Mehl
            3 Eier
            500 ml Milch
            1 Prise Salz
            2 EL Butter zum Braten

            1. Mehl, Eier, Milch und Salz zu einem glatten Teig verrühren und 10 Minuten quellen lassen.
            2. Butter in einer Pfanne erhitzen und
            nacheinander dünne Pfannkuchen ausbacken.
            3. Warm servieren.
            """);
        Assert.Equal("Pfannkuchen", d.Title);
        Assert.Equal("", d.Yield);
        Assert.Equal(5, Lines(d.Ingredients).Length);
        Assert.Equal(["Mehl, Eier, Milch und Salz zu einem glatten Teig verrühren und 10 Minuten quellen lassen.",
            "Butter in einer Pfanne erhitzen und nacheinander dünne Pfannkuchen ausbacken.", "Warm servieren."], Lines(d.Steps));
    }

    [Fact]
    public void Unterabschnitte_und_umbrochene_Zutaten()
    {
        var d = Scan("""
            Apfelkuchen

            Für 1 Springform (26 cm)

            Für den Teig:
            200 g Mehl
            100 g Butter,
            in Stücken
            1 Ei
            Für die Füllung
            4 säuerliche Äpfel
            (z. B. Boskop)
            2 EL Zucker
            Außerdem
            Puderzucker zum Bestäuben

            Zubereitung
            Mehl, Butter und Ei zu einem Teig verkneten und 30 Minuten kühlen.
            """);
        Assert.Equal("1 Springform (26 cm)", d.Yield);
        Assert.Equal(["Für den Teig:", "200 g Mehl", "100 g Butter, in Stücken", "1 Ei", "Für die Füllung:", "4 säuerliche Äpfel (z. B. Boskop)",
            "2 EL Zucker", "Außerdem:", "Puderzucker zum Bestäuben"], Lines(d.Ingredients));
        var r = RecipeScan.ToRecipe(d);
        Assert.Equal(["Für den Teig", "Für die Füllung", "Außerdem"], r.Ingredients.Select(s => s.Name));
    }

    [Fact]
    public void Lesefehler_und_Silbentrennung()
    {
        var d = Scan("""
            Zwiebelkuchen

            Fur 6 Personen
            l/2 Würfel Hefe
            Vz TL Salz
            1O0 g Speck
            • 4 Zwiebeln

            Zubereitung
            Die Zwie-
            beln schälen und in Ringe schneiden. Den Speck würfeln.
            """);
        Assert.Equal("6 Portionen", d.Yield);
        Assert.Equal(["1/2 Würfel Hefe", "½ TL Salz", "100 g Speck", "4 Zwiebeln"], Lines(d.Ingredients));
        Assert.StartsWith("Die Zwiebeln schälen", d.Steps);
    }

    [Fact]
    public void Rezept_ueber_zwei_Seiten()
    {
        var d = Scan("""
            Lasagne
            Für 4 Personen
            500 g Hackfleisch
            1 Dose Tomaten (400 g)
            12 Lasagneplatten
            Zubereitung
            Das Hackfleisch krümelig anbraten, die Tomaten zugeben und 20 Minuten köcheln
            \f
            lassen. Mit Salz und Pfeffer abschmecken.
            Abwechselnd Soße und Platten in eine Form schichten und 40 Minuten backen.

            Für die Béchamel:
            30 g Butter
            30 g Mehl
            500 ml Milch
            """);
        Assert.Equal(["500 g Hackfleisch", "1 Dose Tomaten (400 g)", "12 Lasagneplatten", "Für die Béchamel:", "30 g Butter", "30 g Mehl", "500 ml Milch"],
            Lines(d.Ingredients));
        var steps = Lines(d.Steps);
        Assert.StartsWith("Das Hackfleisch krümelig anbraten", steps[0]);
        Assert.Contains("köcheln lassen. Mit Salz", steps[0]);
    }

    [Fact]
    public void Zweispaltig_aus_TSV()
    {
        // Zutaten links, Zubereitung rechts; eine Zutatenzeile mit zwei Spalten („1 Prise Salz“ steht daneben)
        var tsv = new StringBuilder("level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n");
        void Words(int block, int line, int left, int top, int h, string text)
        {
            var x = left;
            foreach (var w in text.Split(' '))
            {
                var width = w.Length * h / 2;
                tsv.Append(CultureInfo.InvariantCulture, $"5\t1\t{block}\t1\t{line}\t1\t{x}\t{top}\t{width}\t{h}\t95\t{w}\n");
                x += width + h / 3;
            }
        }
        Words(1, 1, 100, 80, 70, "Gemüsecurry");
        Words(2, 1, 100, 300, 24, "Für 2 Personen");
        Words(3, 1, 100, 360, 24, "1 Zwiebel");
        Words(3, 1, 600, 360, 24, "1 Prise Salz");
        Words(3, 2, 100, 400, 24, "400 ml Kokosmilch");
        Words(3, 3, 100, 440, 24, "2 EL Currypaste");
        Words(4, 1, 1100, 300, 24, "Die Zwiebel würfeln und in etwas Öl");
        Words(4, 2, 1100, 340, 24, "glasig dünsten. Currypaste und Kokosmilch");
        Words(4, 3, 1100, 380, 24, "zugeben und 15 Minuten köcheln lassen.");
        var lines = RecipeScan.FromTsv(tsv.ToString(), 1);
        Assert.Equal(new[] { "1 Zwiebel", "1 Prise Salz" }, lines.Where(l => l.Top == 360).Select(l => l.Text));
        var (d, _) = RecipeScan.Analyze(lines);
        Assert.Equal("Gemüsecurry", d.Title);
        Assert.Equal("2 Portionen", d.Yield);
        Assert.Equal(["1 Zwiebel", "1 Prise Salz", "400 ml Kokosmilch", "2 EL Currypaste"], Lines(d.Ingredients));
        Assert.Equal(["Die Zwiebel würfeln und in etwas Öl glasig dünsten. Currypaste und Kokosmilch zugeben und 15 Minuten köcheln lassen."], Lines(d.Steps));
    }

    [Fact]
    public void Zwei_Rezepte_auf_einer_Seite()
    {
        var (drafts, hints) = RecipeScan.AnalyzeAll(RecipeScan.FromText("""
            Tomatensalat

            Für 2 Personen
            4 Tomaten
            1 Schalotte
            2 EL Olivenöl

            Die Tomaten in Scheiben schneiden, mit Schalotte und Öl mischen und 10 Minuten ziehen lassen.

            Gurkensalat

            1 Salatgurke
            150 g Joghurt
            Salz

            Zubereitung
            Die Gurke hobeln und mit dem Joghurt mischen. Mit Salz abschmecken.
            """));
        Assert.Equal(["Tomatensalat", "Gurkensalat"], drafts.Select(d => d.Title));
        Assert.Equal("2 Portionen", drafts[0].Yield);
        Assert.Equal(["4 Tomaten", "1 Schalotte", "2 EL Olivenöl"], Lines(drafts[0].Ingredients));
        Assert.Equal(["1 Salatgurke", "150 g Joghurt", "Salz"], Lines(drafts[1].Ingredients));
        Assert.StartsWith("Die Gurke hobeln", drafts[1].Steps);
        Assert.Empty(hints);
        // Ein Rezept mit Unterabschnitt bleibt eins
        Assert.Single(RecipeScan.AnalyzeAll(RecipeScan.FromText("""
            Quiche

            200 g Mehl
            100 g Butter

            Den Teig kneten und kühlen. Dann in die Form drücken.

            Für den Belag

            200 g Lauch
            2 Eier

            Lauch dünsten, mit Eiern mischen, auf den Teig geben und backen.
            """)).Drafts);
    }

    [Fact]
    public void Seite_ohne_Rezept_meldet_was_fehlt()
    {
        var (d, hints) = RecipeScan.Analyze(RecipeScan.FromText("""
            Liebe Leserinnen und Leser, in diesem Kapitel geht es um die Grundlagen der Küche. Viel Freude beim Lesen.
            """));
        Assert.Contains("Keine Zutaten erkannt.", hints);
        Assert.Contains("Keine Zubereitung erkannt.", hints);
        Assert.Equal("", d.Ingredients);
    }

    [Theory]
    [InlineData("Für 4 Personen", "4 Portionen")]
    [InlineData("für zwei Portionen", "2 Portionen")]
    [InlineData("Ergibt 12 Stück", "12 Stück")]
    [InlineData("4 Portionen", "4 Portionen")]
    [InlineData("Für den Teig:", null)]
    [InlineData("Für die Soße", null)]
    public void Portionen(string text, string? expected) => Assert.Equal(expected, RecipeScan.Yield(text));

    [Theory]
    [InlineData("45 Minuten", "45 Min.")]
    [InlineData("1 Stunde 30 Minuten", "1 Std. 30 Min.")]
    [InlineData("20-30 min", "20–30 Min.")]
    public void Zeiten(string text, string expected) => Assert.Equal(expected, RecipeScan.TimeValue(text));

    [Theory]
    [InlineData("4 Portionen  ·  520 kcal je Portion  ·  Arbeitszeit 20 Min.", 520)]
    [InlineData("Pro Portion ca. 2180 kJ / 520 kcal, 24 g E, 18 g F, 60 g KH", 520)]
    [InlineData("Kalorien je Portion: 450", 450)]
    [InlineData("pro Person: etwa 610 kcal", 610)]
    [InlineData("380 kcal/Portion", 380)]
    [InlineData("320 calories per serving", 320)]
    [InlineData("Eine Portion hat viele Kalorien.", null)]
    [InlineData("200 g Mehl", null)]
    public void Kalorien_je_Portion(string text, int? expected) => Assert.Equal(expected, Recipes.Kcal(text));

    [Fact]
    public void Kalorien_je_Portion_auf_der_Seite()
    {
        var d = Scan("""
            Linsensuppe
            Für 4 Personen
            Pro Portion ca. 2180 kJ / 520 kcal, 24 g E, 18 g F, 60 g KH

            Zutaten
            250 g Linsen
            1 Zwiebel

            Zubereitung
            Die Zwiebel würfeln und mit den Linsen in Brühe 30 Minuten kochen.
            """);
        Assert.Equal("520", d.Calories);
        Assert.DoesNotContain("kcal", d.Description + d.Steps + d.Ingredients);
        Assert.Equal("4 Portionen", d.Yield);

        // Überschrift „Nährwerte pro Portion:“, die Werte in der Zeile darunter
        var e = Scan("""
            Gurkensalat
            Zutaten
            1 Salatgurke
            2 EL Essig

            Zubereitung
            Die Gurke hobeln und mit Essig mischen.

            Nährwerte pro Portion:
            90 kcal | 2 g Eiweiß | 5 g Fett
            """);
        Assert.Equal("90", e.Calories);
        Assert.DoesNotContain("kcal", e.Steps + e.Description);
        Assert.Equal("", Scan("Brot\nZutaten\n500 g Mehl\nZubereitung\nKneten und backen.").Calories);
    }

    [Fact]
    public void Kalorien_je_Portion_aus_dem_Link()
    {
        var html = """
            <html><head><script type="application/ld+json">
            {"@context":"https://schema.org","@type":"Recipe","name":"Pilzrisotto","recipeYield":"4","recipeIngredient":["300 g Risottoreis"],
             "nutrition":{"@type":"NutritionInformation","calories":"2176 kJ"}}
            </script></head></html>
            """;
        var r = RecipeImport.FromHtml(html, new Uri("https://example.org/risotto"))!;
        Assert.Equal("520", r.Calories);
        var (_, plain) = RecipeImport.ToNote(r, null);
        Assert.Contains("4 Portionen  ·  520 kcal je Portion", plain);
        Assert.Equal(520, Recipes.Kcal(plain));
        Assert.Equal((4, false), RecipeNutrition.Servings(plain));
    }

    [Fact]
    public void KI_Antwort_lesen()
    {
        var d = RecipeScan.FromAiJson("""
            Hier das Rezept:
            ```json
            {"title":"Linsensuppe","yield":4,"prepTime":"15 Min.","calories":"ca. 380 kcal","ingredients":["250 g Linsen","1 Zwiebel"],"steps":["Zwiebel würfeln.","Linsen\nkochen."],"tip":""}
            ```
            """)!.Single();
        var two = RecipeScan.FromAiJson("""{"recipes":[{"title":"A","ingredients":["1 Ei"]},{"title":"B","steps":["Rühren."]},{"title":""}]}""")!;
        Assert.Equal(["A", "B"], two.Select(x => x.Title));
        Assert.Equal("Linsensuppe", d.Title);
        Assert.Equal("4 Portionen", d.Yield);
        Assert.Equal("380", d.Calories);
        Assert.Equal("", two[0].Calories);
        Assert.Equal("250 g Linsen\n1 Zwiebel", d.Ingredients);
        Assert.Equal("Zwiebel würfeln.\nLinsen kochen.", d.Steps);
        Assert.Null(RecipeScan.FromAiJson("Leider kann ich das nicht lesen."));
        Assert.Null(RecipeScan.FromAiJson("{kaputt"));
        Assert.Null(RecipeScan.FromAiJson("{\"title\":\"\",\"ingredients\":[]}"));
        Assert.Null(RecipeScan.FromAiJson("{\"recipes\":[]}"));
    }

    [Fact]
    public void Notiz_hat_Rezeptgestalt()
    {
        var d = new RecipeScan.Draft("Linsensuppe", "4 Portionen", "15 Min.", "30 Min.", "", "Wärmt im Winter.",
            "250 g Linsen\n1 Zwiebel\nFür die Einlage:\n2 Würstchen", "1. Zwiebel würfeln.\n2. Linsen kochen.", "Mit Essig abschmecken.", "Omas Küche, S. 45", "380");
        var (html, plain) = RecipeImport.ToNote(RecipeScan.ToRecipe(d), null);
        Assert.Contains("<p>4 Portionen  ·  380 kcal je Portion  ·  Arbeitszeit 15 Min.", html);
        Assert.Equal(380, Recipes.Kcal(plain));
        Assert.Contains("<h3>Zutaten</h3><ul><li>☐ 250 g Linsen</li><li>☐ 1 Zwiebel</li></ul><h4>Für die Einlage</h4>", html);
        Assert.Contains("<ol><li>Zwiebel würfeln.</li><li>Linsen kochen.</li></ol>", html);
        Assert.Contains("<h3>Tipp</h3><p>Mit Essig abschmecken.</p>", html);
        Assert.Contains("<p>Rezept von Omas Küche, S. 45</p>", html);
        Assert.DoesNotContain("<a href", html);
        Assert.True(Recipes.IsRecipe(plain.Replace("\r", "")));
        var note = new NoteRow { Id = 1, SyncId = "x", Html = html, Plain = plain };
        Assert.Equal(["250 g Linsen", "1 Zwiebel", "2 Würstchen"], Recipes.Ingredients(note));
        // Die Portionen stehen in der Eckdatenzeile
        Assert.Equal((4, false), RecipeNutrition.Servings(plain));
    }

    [Fact]
    public async Task Anlegen_mit_Titelbild()
    {
        var client = _fx.As("ben");
        var draft = new RecipeScan.Draft("Gescanntes Rezept", "2 Portionen", "", "", "", "", "100 g Reis\n1 Paprika", "Reis kochen.", "", "");
        var png = Png();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(draft, JsonSerializerOptions.Web)), "draft" },
            { new ByteArrayContent(png), "image", "titel.png" },
        };
        var r = await client.PostAsync("/api/notes/recipes/scan/save", form);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var note = await client.GetFromJsonAsync<JsonElement>($"/api/notes/{id}");
        Assert.Equal("Gescanntes Rezept", note.GetProperty("title").GetString());
        Assert.Contains("recipe", note.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        var raw = note.GetRawText();
        Assert.Contains("blob:", raw);
        // Die Seiten selbst bleiben nicht an der Notiz
        Assert.Equal(0, note.GetProperty("attachments").GetArrayLength());
        var list = await client.GetFromJsonAsync<JsonElement>("/api/notes/recipes?q=Gescanntes");
        Assert.Single(list.EnumerateArray());
    }

    [Fact]
    public async Task Status_und_Fehler()
    {
        var client = _fx.As("ben");
        var state = await client.GetFromJsonAsync<JsonElement>("/api/notes/recipes/scan/state");
        Assert.Equal(DmOffice.Search.Ocr.Available, state.GetProperty("ocr").GetBoolean());
        Assert.False(state.GetProperty("ai").GetBoolean());
        using var empty = new MultipartFormDataContent { { new StringContent("x"), "folder" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/notes/recipes/scan", empty)).StatusCode);
        using var noDraft = new MultipartFormDataContent { { new StringContent("{kaputt"), "draft" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/notes/recipes/scan/save", noDraft)).StatusCode);
    }

    [OcrFact]
    public async Task Ende_zu_Ende_mit_Tesseract()
    {
        var pdf = Samples.Pdf(16, "Kartoffelsuppe", "", "Fuer 4 Personen", "1 kg Kartoffeln", "1 Zwiebel", "1 l Gemuesebruehe", "200 ml Sahne", "",
            "Zubereitung", "Kartoffeln und Zwiebel schaelen und wuerfeln.", "In der Bruehe 20 Minuten kochen.", "Mit der Sahne puerieren.");
        var result = await RecipeScan.ScanAsync([("seite.pdf", pdf)], CancellationToken.None);
        var d = Assert.Single(result.Drafts);
        Assert.Equal("Kartoffelsuppe", d.Title);
        Assert.Equal("4 Portionen", d.Yield);
        Assert.Contains("1 kg Kartoffeln", d.Ingredients);
        Assert.Contains("200 ml Sahne", d.Ingredients);
        Assert.Contains("20 Minuten kochen", d.Steps);
        Assert.Single(result.Images);
    }

    private static byte[] Png()
    {
        using var bmp = new SkiaSharp.SKBitmap(40, 30);
        bmp.Erase(SkiaSharp.SKColors.Orange);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray();
    }
}
