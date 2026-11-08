using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Coach;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Coach Stufe 1: Berechnung mit Leitplanken, dynamisches Schrittziel, BLS, Open Food Facts, Tagebuch über die Schnittstelle.</summary>
public class CoachCalcTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);

    [Fact]
    public void Beispiel_aus_dem_Konzept()
    {
        var p = new CoachProfile { Birth = new DateOnly(1974, 1, 1), Sex = "m", HeightCm = 180, WeightKg = 86, Pal = 1.4, Goal = "lose", Pace = 0.5, Diet = "med" };
        var t = CoachTargetsCalc.Compute(p, null, Today);
        Assert.Equal(52, t.Age);
        Assert.Equal(1730, t.Bmr);
        Assert.Equal(2422, t.Tdee);
        // 0,5 kg/Woche = 550 kcal Defizit am Tag
        Assert.Equal(1870, t.Kcal);
        Assert.Equal(Math.Round(1870 * 0.18 / 4), t.Protein);
        Assert.Equal(1650, t.WaterMl);   // 35 ml × 86 kg × 55 % ≈ 1.656 → 1.650
        Assert.True(t.Complete);
    }

    [Fact]
    public void Leitplanken()
    {
        // Untergewicht: kein Abnehmziel
        var thin = CoachTargetsCalc.Compute(new CoachProfile { Birth = new DateOnly(2000, 5, 1), Sex = "f", HeightCm = 170, WeightKg = 50, Goal = "lose" }, null, Today);
        Assert.Equal("keep", thin.Goal);
        Assert.Contains(thin.Notes, n => n.Contains("BMI unter 18,5"));
        // Schwangerschaft: kein Abnehmziel
        Assert.Equal("keep", CoachTargetsCalc.Compute(new CoachProfile { Birth = new DateOnly(1995, 1, 1), Sex = "f", HeightCm = 165, WeightKg = 70, Goal = "lose", Pregnant = true }, null, Today).Goal);
        // Nie unter dem Grundumsatz bzw. 1.200 kcal
        var small = CoachTargetsCalc.Compute(new CoachProfile { Birth = new DateOnly(1950, 1, 1), Sex = "f", HeightCm = 150, WeightKg = 55, Pal = 1.2, Goal = "lose", Pace = 1.0 }, null, Today);
        Assert.True(small.Kcal >= Math.Max(small.Bmr!.Value, 1200));
        // Ohne Körperdaten keine Kalorienziele, aber ein Trinkziel
        var none = CoachTargetsCalc.Compute(new CoachProfile(), null, Today);
        Assert.Null(none.Kcal);
        Assert.Equal(1500, none.WaterMl);
        // Keto: wenig Kohlenhydrate, Hinweis
        var keto = CoachTargetsCalc.Compute(new CoachProfile { Birth = new DateOnly(1980, 1, 1), Sex = "m", HeightCm = 180, WeightKg = 80, Diet = "keto" }, null, Today);
        Assert.True(keto.Carbs <= 50);
        Assert.Contains(keto.Notes, n => n.Contains("Keto"));
    }

    [Fact]
    public void Dynamisches_Schrittziel_bewegt_sich_hoechstens_zehn_Prozent()
    {
        Assert.Equal(9000, CoachTargetsCalc.NextStepGoal(10000, [6200, 7400, 5800]));
        Assert.Equal(8570, CoachTargetsCalc.NextStepGoal(9000, [8100, 8600, 9000]));   // Ø 8.567, auf 10 gerundet
        Assert.Equal(9900, CoachTargetsCalc.NextStepGoal(9000, [12000, 13000, 11000]));
        Assert.Equal(3000, CoachTargetsCalc.NextStepGoal(3100, [100, 200, 300]));
        Assert.Equal(9000, CoachTargetsCalc.NextStepGoal(9000, []));
    }

    [Fact]
    public void BLS_suchen_und_Portionen()
    {
        Assert.Equal(7140, Bls.Entries.Count);
        var apple = Bls.Search("Apfel");
        Assert.NotEmpty(apple);
        Assert.StartsWith("Apfel", apple[0].Name);
        Assert.Equal("Hafer Flocken", Bls.Search("Haferflocken")[0].Name);
        Assert.Contains("Vollmilch", Bls.Search("Vollmilch")[0].Name);
        // Grundwort: „Sellerie“ findet zuerst rohen Knollen- bzw. Bleichsellerie, nicht Suppen und Salate
        Assert.Contains(Bls.Search("Sellerie")[0].Name, new[] { "Knollensellerie roh", "Bleichsellerie roh" });
        Assert.Equal("Bleichsellerie roh", Bls.Search("Staudensellerie")[0].Name);
        // „Kümmel“ ist keine Kümmelwurst (der BLS kennt Kümmel als Gewürz nicht)
        Assert.False(Bls.Fits("Kümmelwurst (Rohwurst)", "Kümmel"));
        Assert.True(Bls.Fits("Knollensellerie roh", "Sellerie"));
        var oats = Bls.Get("C133000")!;
        Assert.Equal(348, oats.Per100.Kcal);
        Assert.Contains(Portions.For("Apfel roh", false), p => p.Label.StartsWith("1 Apfel"));
        Assert.True(Portions.IsLiquid("Vollmilch 3,5 % Fett"));
        Assert.False(Portions.IsLiquid("Milchschokolade"));
        Assert.True(Portions.IsLiquid("Apfelsaft"));
        Assert.False(Portions.IsLiquid("Schweinefleisch mager, roh"));
        Assert.False(Portions.IsLiquid("Wassermelone roh"));
    }

    [Fact]
    public void Open_Food_Facts_lesen()
    {
        var json = """
            {"code":"4056489123456","status":1,"product":{"product_name_de":"Skyr Natur","brands":"Milbona, Lidl","quantity":"450 g",
             "serving_size":"150 g","serving_quantity":150,"product_quantity":"450","product_quantity_unit":"g",
             "nutriments":{"energy-kcal_100g":63,"proteins_100g":11,"carbohydrates_100g":4,"fat_100g":0.2,"salt_100g":0.1}}}
            """;
        var f = OpenFoodFacts.Parse("4056489123456", json)!;
        Assert.Equal("off:4056489123456", f.Id);
        Assert.Equal("Skyr Natur", f.Name);
        Assert.Equal("Milbona", f.Brand);
        Assert.Equal(63, f.Per100.Kcal);
        Assert.Contains(f.Portions, p => p.Grams == 150);
        Assert.Contains(f.Portions, p => p.Grams == 450);
        Assert.False(f.Liquid);
        Assert.Null(OpenFoodFacts.Parse("1", """{"status":0}"""));
    }
}

[Collection("server")]
public class CoachApiTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CoachApiTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Tag_mit_Tagebuch_Wasser_Schritten_und_Barcode()
    {
        var c = _fx.As("ben");
        var prof = await Json(await c.PutAsJsonAsync("/api/coach/profile", new
        {
            birth = "1980-06-15", sex = "m", heightCm = 182, weightKg = 84, pal = 1.4, goal = "keep", diet = "dge", stepMode = "dynamic", stepGoal = 7000,
            allergens = new[] { "peanuts" }, intolerances = Array.Empty<string>(), dislikes = Array.Empty<string>(),
        }));
        Assert.True(prof.GetProperty("targets").GetProperty("complete").GetBoolean());
        Assert.Contains("CC BY 4.0", prof.GetProperty("sources")[0].GetString());

        var date = "2026-10-20";
        // Haferflocken aus dem BLS, 50 g
        var e = await Json(await c.PostAsJsonAsync("/api/coach/diary", new { date, meal = "breakfast", food = "bls:C133000", amount = 50, portion = "1 Portion" }));
        Assert.Equal(174, e.GetProperty("kcal").GetDouble(), 0);
        // Menge ändern: Nährwerte wachsen mit
        e = await Json(await c.PutAsJsonAsync($"/api/coach/diary/{e.GetProperty("id").GetInt64()}", new { amount = 100.0 }));
        Assert.Equal(348, e.GetProperty("kcal").GetDouble(), 0);
        await Json(await c.PostAsJsonAsync("/api/coach/diary", new { date, meal = "snack", name = "Riegel", quick = new { kcal = 200, protein = 5, fat = 8, carbs = 25 } }));
        await Json(await c.PostAsJsonAsync("/api/coach/water", new { date, ml = 250 }));
        await Json(await c.PostAsJsonAsync("/api/coach/water", new { date, ml = 500 }));
        (await c.PostAsJsonAsync("/api/coach/activity", new { source = "healthconnect", days = new[] {
            new { date = "2026-10-17", steps = 6200, activeKcal = 300.0, weight = (double?)null },
            new { date = "2026-10-18", steps = 7400, activeKcal = 350.0, weight = (double?)null },
            new { date = "2026-10-19", steps = 5800, activeKcal = 250.0, weight = (double?)null },
            new { date, steps = 9000, activeKcal = 600.0, weight = (double?)83.5 } } })).EnsureSuccessStatusCode();

        var day = await Json(await c.GetAsync($"/api/coach/day?date={date}"));
        Assert.Equal(548, day.GetProperty("eaten").GetProperty("kcal").GetDouble());
        Assert.Equal(750, day.GetProperty("water").GetProperty("ml").GetInt32());
        Assert.Equal(9000, day.GetProperty("steps").GetProperty("steps").GetInt32());
        // Erstes dynamisches Ziel: 10.000
        Assert.Equal(10000, day.GetProperty("steps").GetProperty("goal").GetInt32());
        Assert.Equal(83.5, day.GetProperty("weight").GetDouble());
        // Bewegung über dem PAL zählt als Bonus (600 - BMR × 0,2)
        Assert.True(day.GetProperty("activityBonus").GetDouble() > 0);
        Assert.Single(day.GetProperty("meals").GetProperty("breakfast").EnumerateArray());
        // Nächster Tag: Ziel bewegt sich um höchstens 10 % Richtung Ø der letzten drei Tage
        var next = await Json(await c.GetAsync("/api/coach/day?date=2026-10-21"));
        Assert.Equal(9000, next.GetProperty("steps").GetProperty("goal").GetInt32());

        // Suche: schon Gegessenes zuerst
        var hits = (await Json(await c.GetAsync("/api/coach/foods/search?q=hafer&meal=breakfast"))).EnumerateArray().ToList();
        Assert.Equal("bls:C133000", hits[0].GetProperty("id").GetString());
        Assert.True(hits[0].GetProperty("mine").GetBoolean());

        // Barcode: Open Food Facts (hier ersetzt), dann gemerkt; unbekannt → 404; eigenes Produkt des Haushalts hat Vorrang
        var calls = 0;
        OpenFoodFacts.Fetch = (code, _) =>
        {
            calls++;
            return Task.FromResult<string?>(code == "4000000000017"
                ? """{"status":1,"product":{"product_name":"Testkekse","brands":"Hausmarke","nutriments":{"energy-kcal_100g":480,"proteins_100g":6,"carbohydrates_100g":65,"fat_100g":21}}}"""
                : null);
        };
        try
        {
            var off = await Json(await c.GetAsync("/api/coach/foods/barcode/4000000000017"));
            Assert.Equal("Testkekse", off.GetProperty("name").GetString());
            await Json(await c.GetAsync("/api/coach/foods/barcode/4000000000017"));
            Assert.Equal(1, calls);
            var unknown = await c.GetAsync("/api/coach/foods/barcode/4000000000024");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.True((await Json(new HttpResponseMessage(HttpStatusCode.OK) { Content = unknown.Content })).GetProperty("unknown").GetBoolean());
            var own = await Json(await c.PostAsJsonAsync("/api/coach/foods", new
            {
                name = "Omas Pflaumenkuchen", barcode = "4000000000024", per100 = new { kcal = 250, protein = 4, fat = 9, carbs = 38 },
                portions = new[] { new { label = "1 Stück", grams = 120 } }, liquid = false,
            }));
            Assert.StartsWith("own:", own.GetProperty("id").GetString());
            var byCode = await Json(await _fx.As("anna").GetAsync("/api/coach/foods/barcode/4000000000024"));
            Assert.Equal("Omas Pflaumenkuchen", byCode.GetProperty("name").GetString());
            // Eigenes taucht auch für andere im Haushalt in der Suche auf
            Assert.Contains((await Json(await _fx.As("anna").GetAsync("/api/coach/foods/search?q=pflaumenkuchen"))).EnumerateArray(),
                h => h.GetProperty("id").GetString() == own.GetProperty("id").GetString());
        }
        finally { OpenFoodFacts.Fetch = null; }

        // Tagebuch ist privat
        var other = await Json(await _fx.As("anna").GetAsync($"/api/coach/day?date={date}"));
        Assert.Equal(0, other.GetProperty("eaten").GetProperty("kcal").GetDouble());

        var stats = await Json(await c.GetAsync("/api/coach/stats?from=2026-10-17&to=2026-10-21"));
        var days = stats.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(5, days.Count);
        Assert.Equal(548, days[3].GetProperty("kcal").GetDouble());
        Assert.Equal(83.5, days[3].GetProperty("weight").GetDouble());

        // Falsche Eingaben verständlich abgelehnt
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/coach/water", new { date, ml = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/coach/profile", new { sex = "x", goal = "keep", diet = "dge", stepMode = "fixed" })).StatusCode);
    }
}

/// <summary>Coach Stufe 2: gelernte Vorschläge, Vorlagen, Kopieren, Freitext, Rezept-Nährwerte, „Laut Essensplan“.</summary>
[Collection("server")]
public class CoachStage2Tests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CoachStage2Tests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Vorschlaege_Vorlagen_Kopieren_Freitext_Rezept_und_Essensplan()
    {
        var c = _fx.As("cleo");
        var space = _fx.Space("cleo");
        var today = CoachStore.Today(space);
        string D(int n) => CoachStore.Day(today.AddDays(n));

        // Frühstück meist Haferflocken, abends meist Gouda - Vorschläge für das Frühstück: Haferflocken zuerst
        for (int i = 1; i <= 4; i++)
        {
            await Json(await c.PostAsJsonAsync("/api/coach/diary", new { date = D(-i), meal = "breakfast", food = "bls:C133000", amount = 50.0 }));
            await Json(await c.PostAsJsonAsync("/api/coach/diary", new { date = D(-i), meal = "dinner", food = "bls:" + Bls.Search("Gouda")[0].Code, amount = 40.0 }));
        }
        var rec = (await Json(await c.GetAsync("/api/coach/foods/recent?meal=breakfast"))).EnumerateArray().ToList();
        Assert.Equal("bls:C133000", rec[0].GetProperty("id").GetString());
        rec = (await Json(await c.GetAsync("/api/coach/foods/recent?meal=dinner"))).EnumerateArray().ToList();
        Assert.StartsWith("Gouda", rec[0].GetProperty("name").GetString());
        Assert.DoesNotContain(rec, x => x.GetProperty("id").GetString() == "bls:C133000");   // nur, was es schon zum Abendessen gab
        // Imbiss: Pommes mit üblichen Portionen, die Liste zeigt die erste samt Menge
        var pommes = Portions.For("Pommes frites", false);
        Assert.Contains(pommes, p => p.Label == "1 Portion groß" && p.Grams == 150);
        Assert.Equal("1 Portion klein (80 g)", CoachApi.Hit.Of(Bls.ToFood(Bls.Search("Pommes frites", 1)[0])).Portion);
        Assert.Equal("1 Glas (200 ml)", CoachApi.WithAmount("1 Glas", 200, true));
        // Richtwerte: Pommes groß mit Portion und Nährwerten, Allergene für die Profilprüfung
        var rw = Assert.Single(Richtwerte.Search("Pommes groß", 1));
        var rwf = Richtwerte.ToFood(rw);
        Assert.Equal(("1 große Portion", 250.0), (rwf.Portions[0].Label, rwf.Portions[0].Grams));
        Assert.Equal(rwf.Id, CoachApi.Resolve(null!, rwf.Id)!.Id);
        Assert.Contains("gluten", Richtwerte.ToFood(Richtwerte.Search("Bienenstich", 1)[0]).Info!.Allergens!);
        // Aus portionen.tsv: Bienenstich, Erdbeeren (Mehrzahl), Vollkornbrot
        Assert.Contains(Portions.For("Bienenstich (Hefeteig)", false), p => p.Label == "1 Stück" && p.Grams == 120);
        // Das Hauptwort vorn zählt, nicht eine Zutat dahinter
        Assert.Equal("1 Stück (10 cm)", Portions.For("Baguette-Brötchen mit Kochschinken, Käse, Tomaten und Remoulade", false)[0].Label);   // Baguette, nicht „1 Tomate“
        Assert.Contains(Portions.For("Erdbeeren", false), p => p.Label == "1 Schale");
        Assert.Contains(Portions.For("Vollkornbrot", false), p => p.Label == "1 Scheibe" && p.Grams == 50);
        Assert.Equal("1 Apfel (mittel, 150 g)", CoachApi.WithAmount("1 Apfel (mittel)", 150, false));
        // Häufig: beide gleich oft, gleich zu welcher Mahlzeit; Foto ohne freigeschaltete KI: nicht verfügbar, abgelehnt
        var freq = (await Json(await c.GetAsync("/api/coach/foods/frequent"))).EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
        Assert.Contains("bls:C133000", freq);
        Assert.False((await Json(await c.GetAsync("/api/coach/photo"))).GetProperty("available").GetBoolean());
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await c.PutAsync("/api/coach/photo", new ByteArrayContent([1, 2, 3]))).StatusCode);

        // Vorlage aus dem Frühstück von gestern, heute eintragen; Abendessen von gestern kopieren
        var t = await Json(await c.PostAsJsonAsync("/api/coach/templates", new { name = "Mein Frühstück", date = D(-1), meal = "breakfast" }));
        Assert.Equal(174, t.GetProperty("kcal").GetDouble(), 0);
        await Json(await c.PostAsJsonAsync($"/api/coach/templates/{t.GetProperty("id").GetString()}/apply", new { date = D(0) }));
        Assert.Equal(1, (await Json(await c.PostAsJsonAsync("/api/coach/diary/copy", new { from = D(-1), to = D(0), meal = "dinner" }))).GetProperty("added").GetInt32());
        var day = await Json(await c.GetAsync($"/api/coach/day?date={D(0)}"));
        Assert.Single(day.GetProperty("meals").GetProperty("breakfast").EnumerateArray());
        Assert.Single(day.GetProperty("meals").GetProperty("dinner").EnumerateArray());
        // „Wie gestern“ mit Auswahl: nur die gewählten Einträge (fremde Kennungen zählen nicht)
        var yBreakfast = (await Json(await c.GetAsync($"/api/coach/day?date={D(-1)}"))).GetProperty("meals").GetProperty("breakfast").EnumerateArray().ToList();
        Assert.Equal(1, (await Json(await c.PostAsJsonAsync("/api/coach/diary/copy",
            new { from = D(-1), to = D(0), meal = "breakfast", toMeal = "breakfast", ids = new[] { yBreakfast[0].GetProperty("id").GetInt64(), 999999L } }))).GetProperty("added").GetInt32());
        Assert.Equal(0, (await Json(await c.PostAsJsonAsync("/api/coach/diary/copy",
            new { from = D(-1), to = D(0), meal = "breakfast", ids = Array.Empty<long>() }))).GetProperty("added").GetInt32());
        Assert.Equal(2, (await Json(await c.GetAsync($"/api/coach/day?date={D(0)}"))).GetProperty("meals").GetProperty("breakfast").GetArrayLength());

        // Freitext: zwei Posten mit Lebensmittel und Gramm
        var parsed = (await Json(await c.PostAsJsonAsync("/api/coach/parse", new { text = "2 Eier und 50 g Haferflocken", meal = "breakfast" }))).EnumerateArray().ToList();
        Assert.Equal(2, parsed.Count);
        Assert.Equal(120, parsed[0].GetProperty("grams").GetDouble());
        Assert.StartsWith("Hühnerei", parsed[0].GetProperty("food").GetProperty("name").GetString());
        Assert.Equal("bls:C133000", parsed[1].GetProperty("food").GetProperty("id").GetString());
        Assert.Equal(50, parsed[1].GetProperty("grams").GetDouble());

        // Zuordnung merken: „Flocken“ ist ab jetzt Haferflocken
        (await c.PutAsJsonAsync("/api/coach/foodmap", new { name = "Flocken", food = "bls:C133000" })).EnsureSuccessStatusCode();
        Assert.Equal("bls:C133000", (await Json(await c.PostAsJsonAsync("/api/coach/parse", new { text = "30 g Flocken" })))[0].GetProperty("food").GetProperty("id").GetString());
        // Grundnahrungsmittel und „Kaffee mit Milch“: ein Schuss, nicht 100 ml
        var breakfast = (await Json(await c.PostAsJsonAsync("/api/coach/parse", new { text = "2 Brötchen mit Käse, ein Kaffee mit Milch" }))).EnumerateArray().ToList();
        Assert.Equal("bls:B511000", breakfast[0].GetProperty("food").GetProperty("id").GetString());
        Assert.Equal(120, breakfast[0].GetProperty("grams").GetDouble());
        Assert.Equal("bls:M400600", breakfast[1].GetProperty("food").GetProperty("id").GetString());
        Assert.Equal(20, breakfast[1].GetProperty("grams").GetDouble());
        Assert.Equal("bls:N410100", breakfast[2].GetProperty("food").GetProperty("id").GetString());
        Assert.Equal(30, breakfast[3].GetProperty("grams").GetDouble());
        // Rezeptzutaten roh, Knoblauchzehe 4 g
        var pasta = (await Json(await c.PostAsJsonAsync("/api/coach/parse", new { text = "400 g Spaghetti, 500 g Rinderhackfleisch, 1 Zwiebel, 2 Knoblauchzehen" }))).EnumerateArray().ToList();
        Assert.Equal(["bls:E401000", "bls:U010100", "bls:G480100", "bls:G490100"], pasta.Select(x => x.GetProperty("food").GetProperty("id").GetString()));
        Assert.Equal(80, pasta[2].GetProperty("grams").GetDouble());
        Assert.Equal(8, pasta[3].GetProperty("grams").GetDouble());

        // Rezept: Nährwerte aus den Zutaten, je Portion; als Lebensmittel eintragen
        var notes = NotesDb.Of(space);
        var id = notes.CreateNote(null);
        notes.SaveNote(id, "<div data-notizi=\"1\"><p>Porridge</p><p>für 2 Portionen</p><h3>Zutaten</h3><ul><li>☐ 100 g Haferflocken</li><li>☐ 400 ml Vollmilch</li><li>☐ 1 Prise Salz</li></ul></div>",
            "Porridge\r\nfür 2 Portionen\r\nZutaten\r\n☐ 100 g Haferflocken\r\n☐ 400 ml Vollmilch\r\n☐ 1 Prise Salz");
        notes.SetTags(id, [Recipes.Tag]);
        var sync = notes.GetNote(id)!.SyncId;
        var r = await Json(await c.GetAsync($"/api/coach/recipes/{sync}"));
        Assert.Equal(2, r.GetProperty("servings").GetInt32());
        Assert.False(r.GetProperty("servingsGuessed").GetBoolean());
        Assert.Equal(0, r.GetProperty("unmatched").GetInt32());
        var perServing = r.GetProperty("perServing").GetProperty("kcal").GetDouble();
        Assert.True(perServing is >= 250 and <= 320, string.Join(" | ", r.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("name").GetString() + " -> " + (l.GetProperty("food").ValueKind == JsonValueKind.Null ? "-" : l.GetProperty("food").GetProperty("name").GetString()) + " " + l.GetProperty("grams") + " g " + l.GetProperty("kcal") + " kcal")));   // 348 + ~260 kcal, halbiert
        var e = await Json(await c.PostAsJsonAsync("/api/coach/diary", new { date = D(0), meal = "lunch", food = "recipe:" + sync, amount = r.GetProperty("weight").GetDouble() / 2, portion = "1 Portion" }));
        Assert.Equal(perServing, e.GetProperty("kcal").GetDouble(), 0);
        // Portionen ändern
        r = await Json(await c.PutAsJsonAsync($"/api/coach/recipes/{sync}/servings", new { servings = 4 }));
        Assert.Equal(4, r.GetProperty("servings").GetInt32());
        // Ein Rezept, dessen Name wie seine Zutat heißt, darf sich nach dem Eintragen nicht selbst als Zutat finden (Endlosschleife)
        var id2 = notes.CreateNote(null);
        notes.SaveNote(id2, "<div data-notizi=\"1\"><p>Haferflocken Spezial</p><h3>Zutaten</h3><ul><li>☐ 80 g Haferflocken</li></ul></div>",
            "Haferflocken Spezial\r\nZutaten\r\n☐ 80 g Haferflocken");
        notes.SetTags(id2, [Recipes.Tag]);
        var sync2 = notes.GetNote(id2)!.SyncId;
        await Json(await c.PostAsJsonAsync("/api/coach/diary", new { date = D(0), meal = "snack", food = "recipe:" + sync2, amount = 20, portion = "1 Portion" }));
        var r2 = await Json(await c.GetAsync($"/api/coach/recipes/{sync2}"));
        Assert.Equal("bls:C133000", r2.GetProperty("lines")[0].GetProperty("food").GetProperty("id").GetString());

        // Laut Essensplan: das Rezept steht heute zum Abendessen im Plan
        var cal = PimStore.CreateCollection(space.Db, PimStore.Calendar, "essen-coach-test", "Essen Coach", "#3a7", "VEVENT");
        CalendarKinds.Set(space.Db, cal.Id, CalendarKinds.Meal);
        await Json(await c.PostAsJsonAsync("/api/coach/plan/accept", new { calendar = cal.Id, slots = new[] { new { date = D(0), meal = "Abendessen", recipe = sync, title = "Porridge", leftover = false } } }));
        var planned = (await Json(await c.GetAsync($"/api/coach/planned?date={D(0)}&meal=dinner"))).EnumerateArray().ToList();
        var p = Assert.Single(planned);
        Assert.Equal("recipe:" + sync, p.GetProperty("food").GetString());
        Assert.Empty((await Json(await c.GetAsync($"/api/coach/planned?date={D(0)}&meal=lunch"))).EnumerateArray());
    }

    [Fact]
    public async Task Basisrezepte_gehoeren_zur_Auslieferung()
    {
        // 7 Ernährungsformen × 3 Mahlzeiten × 10
        Assert.Equal(210, BaseRecipes.All.Count);
        Assert.Equal(210, BaseRecipes.All.Select(b => b.Id).Distinct().Count());
        Assert.All(BaseRecipes.All.GroupBy(b => (b.Diet, b.Meal)), g => Assert.Equal(10, g.Count()));
        Assert.Equal(21, BaseRecipes.All.Select(b => (b.Diet, b.Meal)).Distinct().Count());
        Assert.All(BaseRecipes.All, b =>
        {
            Assert.True(b.Ingredients.Count >= 4, b.Id);
            Assert.InRange(b.Steps.Count, 4, 8);
            Assert.True(b.Prep > 0 && b.Minutes >= b.Prep, b.Id);
            Assert.True(b.PerServing.Kcal > 0 && b.PerServing.Fiber != null, b.Id);
            // Nährwerte aus dem BLS: jede Zutat ab 2 g hat einen gültigen Code
            Assert.All(b.Ingredients, i => Assert.True(i.Grams < 2 || (i.Bls != null && Bls.Get(i.Bls) != null), b.Id + ": " + i.Name));
            // Stückzahl ohne Einheit braucht die Mehrzahl für den Portionen-Umschalter
            Assert.All(b.Ingredients, i => Assert.True(i.Amount == null || i.Unit != null || i.Plural != null, b.Id + ": " + i.Name));
            Assert.Equal(BaseRecipes.MaxServings, b.Ingredients[0].Texts.Count);
            // Passt zur eigenen Ernährungsform nach denselben Regeln wie der Essensplan
            if (b.Diet is "vegetarian" or "vegan" or "lowcarb" or "keto")
                Assert.Null(CoachPlan.Excluded(BaseRecipes.Candidate(b), new CoachProfile { Diet = b.Diet }));
        });
        Assert.All(BaseRecipes.All.Where(b => b.Diet == "protein"), b => Assert.Contains("highprotein", BaseRecipes.Diets(b)));

        // Küchenmengen für 1 bis 6 Portionen
        var pepper = new BaseRecipes.Ingredient(0.5, null, "rote Paprikaschote", "rote Paprikaschoten", 80, "G543100");
        Assert.Equal("½ rote Paprikaschote (80 g)", pepper.Text(1));
        Assert.Equal("1 rote Paprikaschote (160 g)", pepper.Text(2));
        Assert.Equal("1½ rote Paprikaschoten (240 g)", pepper.Text(3));
        Assert.Equal("3 Zehen Knoblauch (12 g)", new BaseRecipes.Ingredient(1, "Zehe", "Knoblauch", null, 4, "G490100").Text(3));
        Assert.Equal("300 g Hähnchenbrustfilet", new BaseRecipes.Ingredient(150, "g", "Hähnchenbrustfilet", null, 150, "V416100").Text(2));
        Assert.Equal("Salz und Pfeffer", new BaseRecipes.Ingredient(null, null, "Salz und Pfeffer", null, 1, "R114000").Text(4));
        Assert.Equal(["Gluten", "Milch"], CoachPlan.AllergensIn("Haferflocken\nMilch\nHeidelbeeren"));

        var c = _fx.As("dora");
        var list = (await Json(await c.GetAsync("/api/coach/recipes"))).EnumerateArray().Where(r => r.GetProperty("base").GetBoolean()).ToList();
        Assert.Equal(210, list.Count);
        var vegan = list.First(r => r.GetProperty("syncId").GetString() == "base-vegan-lunch-1");
        Assert.Contains("vegan", vegan.GetProperty("diets").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal("Basisrezepte / Vegan / Mittagessen", vegan.GetProperty("folder").GetString());

        // Ansicht und Eintragen: Nährwerte aus dem Kochbuch, eine Portion = Gewicht des Rezepts
        var b = BaseRecipes.ById("base-dge-breakfast-1")!;
        var r = await Json(await c.GetAsync("/api/coach/recipes/base-dge-breakfast-1"));
        Assert.Equal(b.PerServing.Kcal, r.GetProperty("perServing").GetProperty("kcal").GetDouble());
        Assert.Equal(b.Steps.Count, r.GetProperty("base").GetProperty("steps").GetArrayLength());
        var today = CoachStore.Day(CoachStore.Today(_fx.Space("dora")));
        var e = await Json(await c.PostAsJsonAsync("/api/coach/diary", new { date = today, meal = "breakfast", food = "recipe:" + b.Id, amount = b.Grams, portion = "1 Portion" }));
        Assert.InRange(e.GetProperty("kcal").GetDouble(), b.PerServing.Kcal - 2, b.PerServing.Kcal + 2);   // je 100 g gerundet

        // Übernehmen: eigene Rezept-Notiz mit denselben Zutaten
        var adopted = await Json(await c.PostAsync("/api/coach/recipes/base-dge-breakfast-1/adopt", null));
        var note = NotesDb.Of(_fx.Space("dora")).BySyncId(adopted.GetProperty("syncId").GetString()!)!;
        Assert.True(Recipes.IsRecipe(note.Tags, note.Plain));
        Assert.Equal(b.Lines, Recipes.Ingredients(note));
    }
}
