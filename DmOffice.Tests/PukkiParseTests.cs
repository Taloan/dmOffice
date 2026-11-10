using DmOffice.Chat;

namespace DmOffice.Tests;

/// <summary>Kurzbefehle an Pukki: Wann und Was aus einem Satz.</summary>
public class PukkiParseTests
{
    // Mittwoch, 21.10.2026, 10:00
    private static readonly DateTime Now = new(2026, 10, 21, 10, 0, 0);

    [Theory]
    [InlineData("morgen 15 Uhr Zahnarzt", "2026-10-22", "15:00", null, "Zahnarzt")]
    [InlineData("Zahnarzt morgen um 15:30", "2026-10-22", "15:30", null, "Zahnarzt")]
    [InlineData("Freitag 9:30 bis 11 Elternabend", "2026-10-23", "09:30", "11:00", "Elternabend")]
    [InlineData("am 24.12. Weihnachten bei Oma", "2026-12-24", null, null, "Weihnachten bei Oma")]
    [InlineData("1.3. Geburtstag Ben", "2027-03-01", null, null, "Geburtstag Ben")]
    [InlineData("heute Kinder abholen 16 Uhr", "2026-10-21", "16:00", null, "Kinder abholen")]
    [InlineData("nächsten Dienstag um 9 Elterngespräch", "2026-10-27", "09:00", null, "Elterngespräch")]
    [InlineData("Milch kaufen", null, null, null, "Milch kaufen")]
    [InlineData("tomorrow 8:15 dentist", "2026-10-22", "08:15", null, "dentist")]
    [InlineData("übermorgen 18.30 Uhr Training", "2026-10-23", "18:30", null, "Training")]
    [InlineData("übermorgen um halb drei Kaffee", "2026-10-23", "14:30", null, "Kaffee")]
    [InlineData("morgen viertel vor fünf Klavier", "2026-10-22", "16:45", null, "Klavier")]
    [InlineData("Dienstag nächste Woche um 11 Bank", "2026-10-27", "11:00", null, "Bank")]
    [InlineData("tomorrow 3pm dentist", "2026-10-22", "15:00", null, "dentist")]
    [InlineData("morgen um 3 am Nachmittag Besprechung", "2026-10-22", "15:00", null, "Besprechung")]
    [InlineData("morgn 9 uhr friseur", "2026-10-22", "09:00", null, "friseur")]
    [InlineData("heute abend kino", "2026-10-21", null, null, "kino")]
    [InlineData("morgen von 14 bis 16 Uhr Workshop", "2026-10-22", "14:00", "16:00", "Workshop")]
    [InlineData("Sa 14 Uhr Kindergeburtstag", "2026-10-24", "14:00", null, "Kindergeburtstag")]
    [InlineData("in 3 Tagen um 10 Uhr Ablesung", "2026-10-24", "10:00", null, "Ablesung")]
    [InlineData("am 3. November 10 Uhr Notar", "2026-11-03", "10:00", null, "Notar")]
    [InlineData("Mittagessen am Freitag: Fisch", "2026-10-23", null, null, "Mittagessen Fisch")]
    public void Erkennt_Wann_und_Titel(string text, string? date, string? start, string? end, string title)
    {
        var w = PukkiParse.Parse(text, Now);
        Assert.Equal(date, w.Date?.ToString("yyyy-MM-dd"));
        Assert.Equal(start, w.Start?.ToString("HH:mm"));
        Assert.Equal(end, w.End?.ToString("HH:mm"));
        Assert.Equal(title, w.Title);
    }

    [Fact]
    public void Erkennt_Zeitraum_und_Wiederholung()
    {
        var urlaub = PukkiParse.Parse("Urlaub vom 1.8. bis 14.8.", Now);
        Assert.Equal(new DateOnly(2027, 8, 1), urlaub.Date);
        Assert.Equal(new DateOnly(2027, 8, 14), urlaub.Until);
        Assert.Equal("Urlaub", urlaub.Title);

        var training = PukkiParse.Parse("jeden Montag 18 Uhr Training", Now);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", training.Repeat);
        Assert.Equal(new DateOnly(2026, 10, 26), training.Date);
        Assert.Equal("Training", training.Title);

        var chor = PukkiParse.Parse("alle zwei Wochen freitags 17 Uhr Chor", Now);
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", chor.Repeat);
        Assert.Equal("Chor", chor.Title);
    }
}
