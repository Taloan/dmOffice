using DmOffice.Coach;

namespace DmOffice.Tests;

/// <summary>Coach: Zutatenzeilen und Freitext in Menge, Einheit und Lebensmittel zerlegen.</summary>
public class FoodTextTests
{
    [Theory]
    [InlineData("200 g Mehl", 200, "g", "Mehl", 200.0, null)]
    [InlineData("200g Mehl", 200, "g", "Mehl", 200.0, null)]
    [InlineData("1,5 kg Kartoffeln", 1.5, "kg", "Kartoffeln", 1500.0, null)]
    [InlineData("500ml Milch", 500, "ml", "Milch", null, 500.0)]
    [InlineData("1/2 TL Salz", 0.5, "TL", "Salz", null, 2.5)]
    [InlineData("1 ½ EL Olivenöl", 1.5, "EL", "Olivenöl", null, 22.5)]
    [InlineData("1½ EL Olivenöl", 1.5, "EL", "Olivenöl", null, 22.5)]
    [InlineData("½ Bund Petersilie", 0.5, "Bund", "Petersilie", null, null)]
    [InlineData("2-3 Zwiebeln", 2.5, null, "Zwiebeln", null, null)]
    [InlineData("2 Eier", 2, null, "Eier", null, null)]
    [InlineData("☐ 300 g Nudeln", 300, "g", "Nudeln", 300.0, null)]
    [InlineData("- 3 Esslöffel Zucker", 3, "EL", "Zucker", null, 45.0)]
    [InlineData("eine halbe Zwiebel", 0.5, null, "Zwiebel", null, null)]
    [InlineData("zwei Scheiben Brot", 2, "Scheibe", "Brot", null, null)]
    [InlineData("1 Prise Salz", 1, "Prise", "Salz", null, null)]
    [InlineData("Prise Muskat", 1, "Prise", "Muskat", null, null)]
    [InlineData("1 l Wasser", 1, "l", "Wasser", null, 1000.0)]
    public void Zutatenzeile(string line, double amount, string? unit, string name, double? grams, double? ml)
    {
        var i = FoodText.Ingredient(line)!;
        Assert.Equal(amount, i.Amount!.Value, 3);
        Assert.Equal(unit, i.Unit);
        Assert.Equal(name, i.Name);
        Assert.Equal(grams, i.Grams);
        Assert.Equal(ml, i.Ml);
    }

    [Fact]
    public void Klammern_und_Komma()
    {
        var dose = FoodText.Ingredient("1 Dose Tomaten (400 g)")!;
        Assert.Equal((1d, "Dose", "Tomaten", 400d), (dose.Amount!.Value, dose.Unit, dose.Name, dose.Grams!.Value));
        Assert.Null(dose.Note);

        var zwiebel = FoodText.Ingredient("2 Zwiebeln, fein gehackt")!;
        Assert.Equal(("Zwiebeln", "fein gehackt"), (zwiebel.Name, zwiebel.Note));

        var ei = FoodText.Ingredient("3 Eier (Größe M)")!;
        Assert.Equal(("Eier", "Größe M"), (ei.Name, ei.Note));
    }

    [Fact]
    public void Ohne_Menge()
    {
        var salz = FoodText.Ingredient("Salz und Pfeffer")!;
        Assert.Null(salz.Amount);
        Assert.True(salz.Vague);

        var butter = FoodText.Ingredient("etwas Butter")!;
        Assert.Equal(("Butter", true), (butter.Name, butter.Vague));
        Assert.Null(butter.Amount);

        Assert.Null(FoodText.Ingredient("Für den Teig:"));
        Assert.Null(FoodText.Ingredient("   "));
    }

    [Fact]
    public void Freitext_aus_dem_Tagebuch()
    {
        var items = FoodText.Meal("2 Brötchen mit Käse und Kaffee");
        Assert.Equal(["Brötchen", "Käse", "Kaffee"], items.Select(i => i.Name));
        Assert.Equal(2, items[0].Amount);
        Assert.Null(items[1].Amount);

        var müsli = FoodText.Meal("Müsli, Banane + 200 ml Milch");
        Assert.Equal(["Müsli", "Banane", "Milch"], müsli.Select(i => i.Name));
        Assert.Equal(200, müsli[2].Ml);

        // Komma in der Zahl trennt nicht, Klammern auch nicht
        var wasser = FoodText.Meal("1,5 l Wasser; Apfel (groß, rot)");
        Assert.Equal(2, wasser.Count);
        Assert.Equal(1500, wasser[0].Ml);
        Assert.Equal(("Apfel", "groß, rot"), (wasser[1].Name, wasser[1].Note));
    }
}
