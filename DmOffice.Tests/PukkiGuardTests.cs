using DmOffice.Chat;

namespace DmOffice.Tests;

/// <summary>Feste Grenzen vor der KI: Medikamente, Beschwerden und Notfälle gehen nicht an die KI.</summary>
public class PukkiGuardTests
{
    [Fact]
    public void Medikamente_und_Beschwerden_nicht_ueber_die_KI()
    {
        Assert.Equal(PukkiGuard.Medication, PukkiGuard.GuardForAi("ibuprofen und alkohol")?.Text);
        Assert.NotNull(PukkiGuard.GuardForAi("Kopfschmerzen?"));
        Assert.Null(PukkiGuard.GuardForAi("Wie lange dauert es, bis man verblutet ist"));
        Assert.Null(PukkiGuard.GuardForAi("Was tun, wenn jemand bewusstlos ist?"));
        Assert.Contains("Bleib bei der Person", PukkiGuard.GuardForAi("Er liegt am Boden und atmet nicht")?.Text);
    }
}
