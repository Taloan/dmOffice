using DmOffice.Core;
using Microsoft.Extensions.Logging;

namespace DmOffice.Tests;

/// <summary>Protokoll der Verwaltung: gebremste Anfragen einer Adresse stehen in einem Eintrag statt in hundert.</summary>
public class SystemLogTests
{
    [Fact]
    public void Ueberholte_Eintraege_verschwinden_aus_der_Anzeige()
    {
        var code = Guid.NewGuid().ToString("N");
        SystemLog.Add(LogLevel.Warning, "Auth", "Diesen Code eingeben: " + code);
        Assert.Contains(SystemLog.All(), e => e.Message.Contains(code));
        SystemLog.Withdraw(code);
        Assert.DoesNotContain(SystemLog.All(), e => e.Message.Contains(code));
    }

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("93.184.216.34", false)]
    public void Ki_Dienst_darf_lokal_laufen_aber_nicht_auf_Metadaten_Adressen(string address, bool blocked) =>
        Assert.Equal(blocked, DmOffice.Chat.PukkiAi.NotForAi(System.Net.IPAddress.Parse(address)));

    [Fact]
    public void Gebremste_Anfragen_einer_Adresse_in_einem_Eintrag()
    {
        var ip = "198.51.100." + Random.Shared.Next(1, 250);
        foreach (var p in new[] { "/phpinfo.php", "/.env", "/admin/phpinfo.php" }) SystemLog.Throttled(ip, "GET " + p);
        var mine = SystemLog.All().Where(e => e.Message.Contains(ip)).ToList();
        var e = Assert.Single(mine);
        Assert.Contains("3 Anfragen", e.Message);
        Assert.Contains("/admin/phpinfo.php", e.Message);
    }
}
