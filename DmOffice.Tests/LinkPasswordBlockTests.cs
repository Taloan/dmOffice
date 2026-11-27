namespace DmOffice.Tests;

/// <summary>Passwort eines öffentlichen Links: Fehlversuche einer Adresse sperren nicht alle anderen aus.</summary>
public class LinkPasswordBlockTests
{
    [Fact]
    public void Fuenf_Fehler_einer_Adresse_sperren_nur_diese_Adresse()
    {
        var link = "link:test-" + Guid.NewGuid().ToString("N");
        var a = "link-ip:test-a-" + Guid.NewGuid().ToString("N");
        var b = "link-ip:test-b-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 5; i++) Auth.LinkFailed(link, a);
        Assert.True(Auth.LinkBlocked(link, a));
        Assert.False(Auth.LinkBlocked(link, b));
    }

    [Fact]
    public void Viele_Fehler_von_vielen_Adressen_sperren_den_Link()
    {
        var link = "link:test-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < Auth.LinkFailuresAll; i++) Auth.LinkFailed(link, "link-ip:test-" + Guid.NewGuid().ToString("N"));
        Assert.True(Auth.LinkBlocked(link, "link-ip:test-" + Guid.NewGuid().ToString("N")));
    }
}
