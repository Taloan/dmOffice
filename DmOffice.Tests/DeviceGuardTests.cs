using System.Security.Claims;
using DmOffice.Core;
using Microsoft.AspNetCore.Http;

namespace DmOffice.Tests;

/// <summary>Die zweite Verteidigungslinie hinter der Pfadpruefung im Umschlag: Geraete und Umschlag duerfen
/// /api/auth nicht erreichen, App-Geraete nur ihre Freigabeliste.</summary>
public class DeviceGuardTests
{
    private static UserRecord User() => new() { Id = "u1", Login = "pat", Stamp = "s", IsAdmin = true };

    private static HttpContext Ctx(ClaimsPrincipal user, string path)
    {
        var http = new DefaultHttpContext { User = user };
        http.Request.Path = path;
        return http;
    }

    [Fact]
    public void App_Geraet_nur_in_seiner_Freigabe_und_nie_in_die_Anmeldung()
    {
        var dev = Auth.DevicePrincipal(User(), "x", "pw1");
        Assert.True(Auth.DeviceRequestAllowed(Ctx(dev, "/api/mail/list")));
        Assert.True(Auth.DeviceRequestAllowed(Ctx(dev, "/api/files/download")));
        // /api/auth/* ist fuer Geraete gesperrt - genau der Weg, den der ".."-Trick im Umschlag ansteuerte
        Assert.False(Auth.DeviceRequestAllowed(Ctx(dev, "/api/auth/2fa/begin")));
        Assert.False(Auth.DeviceRequestAllowed(Ctx(dev, "/api/auth/password")));
        // ausserhalb der Freigabeliste (Verwaltung, Einstellungen mit Passwoertern)
        Assert.False(Auth.DeviceRequestAllowed(Ctx(dev, "/api/admin/users")));
        Assert.False(Auth.DeviceRequestAllowed(Ctx(dev, "/api/settings/accounts/test")));
        // Nur der Status bleibt erlaubt (steht in der Freigabeliste); alles andere unter /api/auth nicht
        Assert.True(Auth.DeviceRequestAllowed(Ctx(dev, "/api/auth/state")));
        Assert.False(Auth.DeviceRequestAllowed(Ctx(dev, "/api/auth/logout")));
    }

    [Fact]
    public void Umschlag_Browser_erreicht_die_Anmeldung_nicht()
    {
        // Browser-Geraet aus dem Umschlag: volle Rolle, aber EnvelopeClaim - /api/auth bleibt gesperrt
        var p = Auth.Principal(User(), "x");
        ((ClaimsIdentity)p.Identity!).AddClaim(new Claim(Auth.EnvelopeClaim, "1"));
        Assert.True(Auth.DeviceRequestAllowed(Ctx(p, "/api/admin/users")));   // Browser darf Verwaltung (wie Cookie)
        Assert.False(Auth.DeviceRequestAllowed(Ctx(p, "/api/auth/2fa/confirm")));
        Assert.True(Auth.DeviceRequestAllowed(Ctx(p, "/api/auth/state")));
    }

    [Fact]
    public void Normale_Anfrage_ohne_Geraet_bleibt_unberuehrt()
    {
        var p = Auth.Principal(User(), "x");   // Cookie-Anmeldung, kein Device/Envelope
        Assert.True(Auth.DeviceRequestAllowed(Ctx(p, "/api/auth/2fa/begin")));
        Assert.True(Auth.DeviceRequestAllowed(Ctx(p, "/api/admin/users")));
    }
}
