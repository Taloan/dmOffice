using System.Security.Claims;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Was die Handy-App mit dem App-Passwort an der Web-API darf - und was nicht.</summary>
public class AppPasswordApiTests
{
    [Theory]
    [InlineData("/dav/calendars/t/personal/")]
    [InlineData("/.well-known/caldav")]
    [InlineData("/api/mail/status")]
    [InlineData("/api/mail/acc1/messages")]
    [InlineData("/api/mail/acc1/send")]
    [InlineData("/api/pim/contacts")]
    [InlineData("/api/pim/events")]
    [InlineData("/api/notes")]
    [InlineData("/api/notes/abc")]
    [InlineData("/api/files/x/y.pdf")]
    [InlineData("/api/settings/dav")]
    [InlineData("/api/settings/ui")]
    [InlineData("/api/settings/categories")]
    [InlineData("/api/settings/search-folders")]
    [InlineData("/api/auth/state")]
    public void Erlaubt(string path) => Assert.True(Auth.AppPasswordAllowed(path));

    [Theory]
    [InlineData("/api/admin/users")]
    [InlineData("/api/admin")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/password")]
    [InlineData("/api/auth/2fa/disable")]
    [InlineData("/api/auth/setup")]
    [InlineData("/api/settings/")]
    [InlineData("/api/settings/app-passwords")]
    [InlineData("/api/settings/app-passwords/abc")]
    [InlineData("/api/settings/accounts")]
    [InlineData("/api/settings/accounts/test")]
    [InlineData("/api/settings/notizi")]
    [InlineData("/api/settings/general")]
    [InlineData("/api/settings/dav/evil")]
    [InlineData("/api/mailx")]
    [InlineData("/")]
    [InlineData("/index.html")]
    public void Verboten(string path) => Assert.False(Auth.AppPasswordAllowed(path));

    [Theory]
    [InlineData("/dav/calendars/t/", true)]
    [InlineData("/.well-known/caldav", true)]
    [InlineData("/api/e2e/register", false)]
    [InlineData("/api/mail/status", false)]
    [InlineData("/api/auth/state", false)]
    [InlineData("/", false)]
    public void Basic_Aufforderung_nur_fuer_DAV(string path, bool expected) => Assert.Equal(expected, Auth.BasicChallenge(path));

    [Fact]
    public void Geraete_Principal_hat_nie_Admin_Rolle()
    {
        var admin = new UserRecord { Id = "1", Login = "chef", IsAdmin = true, Stamp = "s" };
        var p = Auth.DevicePrincipal(admin, Auth.AppPasswordScheme);
        Assert.False(p.IsInRole("admin"));
        Assert.True(p.IsInRole("user"));
        Assert.Equal("chef", p.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("1", p.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("1", p.FindFirstValue(Auth.DeviceClaim));
        // Der Browser-Principal desselben Benutzers ist Admin
        Assert.True(Auth.Principal(admin, "Cookies").IsInRole("admin"));
    }
}
