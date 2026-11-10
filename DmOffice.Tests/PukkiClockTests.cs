using DmOffice.Chat;

namespace DmOffice.Tests;

/// <summary>Pukkis Morgenüberblick nennt die Zeitumstellung - am Tag davor und am Morgen danach, nach der Zeitzone des Benutzers.</summary>
public class PukkiClockTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Fact]
    public void Zeitumstellung_davor_danach_und_sonst_nichts()
    {
        // 2026: Sommerzeit ab 29. März, Winterzeit ab 25. Oktober
        Assert.StartsWith("Heute Nacht wird auf Winterzeit", Pukki.ClockChange(Berlin, new DateOnly(2026, 10, 24)));
        Assert.StartsWith("Heute Nacht wurde auf Winterzeit", Pukki.ClockChange(Berlin, new DateOnly(2026, 10, 25)));
        Assert.StartsWith("Heute Nacht wird auf Sommerzeit", Pukki.ClockChange(Berlin, new DateOnly(2026, 3, 28)));
        Assert.StartsWith("Heute Nacht wurde auf Sommerzeit", Pukki.ClockChange(Berlin, new DateOnly(2026, 3, 29)));
        Assert.Null(Pukki.ClockChange(Berlin, new DateOnly(2026, 10, 26)));
        Assert.Null(Pukki.ClockChange(Berlin, new DateOnly(2026, 7, 1)));
        // Ohne Zeitumstellung (UTC): nie
        Assert.Null(Pukki.ClockChange(TimeZoneInfo.Utc, new DateOnly(2026, 10, 25)));
    }
}
