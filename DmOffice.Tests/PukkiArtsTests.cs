using DmOffice.Chat;

namespace DmOffice.Tests;

/// <summary>Arten von Terminen: Startwörter, Wortteile und Assoziationen aus Wikidata.</summary>
public class PukkiArtsTests
{
    [Theory]
    [InlineData("Zahnarzt", "gesundheit")]
    [InlineData("Kieferorthopäde mit Lena", "gesundheit")]
    [InlineData("Kinderarzttermin", "gesundheit")]
    [InlineData("Impfung", "gesundheit")]
    [InlineData("Elternabend", "schule")]
    [InlineData("Mathe Klassenarbeit", "schule")]
    [InlineData("Kinder abholen", "abholen")]
    [InlineData("Ben vom Bahnhof holen", "abholen")]
    [InlineData("Fußballtraining", "sport")]
    [InlineData("Volleyball", "sport")]
    [InlineData("Schwimmkurs", "sport")]
    [InlineData("Klavierunterricht", "kurse")]
    [InlineData("Chorprobe", "kurse")]
    [InlineData("Geburtstag Oma", "familie")]
    [InlineData("Hochzeit von Tim und Anna", "familie")]
    [InlineData("Kino mit Ben", "freizeit")]
    [InlineData("Flug nach Rom", "reisen")]
    [InlineData("Meeting mit Kunde", "arbeit")]
    [InlineData("Finanzamt", "behoerden")]
    [InlineData("Steuererklärung abgeben", "behoerden")]
    [InlineData("Klempner kommt", "haushalt")]
    [InlineData("Sperrmüll", "haushalt")]
    [InlineData("TÜV", "auto")]
    [InlineData("Reifenwechsel", "auto")]
    [InlineData("Tierarzt mit Bello", "tiere")]
    public void Ordnet_ein(string title, string art) => Assert.Equal(art, PukkiArts.Classify(title)?.Key);

    [Theory]
    [InlineData("Blumenkohl")]
    [InlineData("xyz")]
    public void Ohne_klare_Art(string title) => Assert.Null(PukkiArts.Classify(title));

    [Fact]
    public void Wissen_ist_geladen()
    {
        var (seeds, words) = PukkiArts.Size;
        Assert.True(seeds > 300, $"{seeds} Startwörter");
        Assert.True(words > 2000, $"{words} Wörter mit Assoziationen");
    }

    [Fact]
    public void Kategorien_lesen()
    {
        Assert.Equal("sport", PukkiArts.FromCategories(["Privat", "Sport"])?.Key);
        Assert.Equal("gesundheit", PukkiArts.FromCategories(["health"])?.Key);
        Assert.Null(PukkiArts.FromCategories(["Privat"]));
    }
}
