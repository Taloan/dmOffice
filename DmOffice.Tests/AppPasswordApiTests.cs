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
    [InlineData("/api/office/config")]
    [InlineData("/api/office/open")]
    [InlineData("/api/office/new")]
    [InlineData("/api/settings/dav")]
    [InlineData("/api/settings/ui")]
    [InlineData("/api/settings/categories")]
    [InlineData("/api/settings/search-folders")]
    [InlineData("/api/auth/state")]
    [InlineData("/remote.php/dav/files/t/x.txt")]
    [InlineData("/remote.php/webdav/")]
    [InlineData("/ocs/v1.php/cloud/user")]
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
    [InlineData("/api/officex")]
    [InlineData("/api/admin/office")]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/remote.phpx")]
    [InlineData("/index.php/login/v2")]
    public void Verboten(string path) => Assert.False(Auth.AppPasswordAllowed(path));

    [Theory]
    [InlineData("/dav/calendars/t/", true)]
    [InlineData("/.well-known/caldav", true)]
    [InlineData("/remote.php/dav/files/", true)]
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

    [Fact]
    public void Umbenennen_und_Geraetename_beim_Koppeln()
    {
        var db = new UserDb(Path.Combine(Directory.CreateTempSubdirectory("apppw").FullName, "u.db"), new byte[32], "t");
        AppPasswords.Create(db, "Handy-App · 08.11.2026");
        AppPasswords.Create(db, "Mein Handy");
        var list = AppPasswords.List(db);
        var auto = list.First(x => x.Name.StartsWith("Handy-App", StringComparison.Ordinal)).Id;
        var own = list.First(x => x.Name == "Mein Handy").Id;
        // Die App nennt beim Koppeln ihr Gerät: ersetzt nur das Datum im vorgegebenen Namen
        AppPasswords.NameDevice(db, auto, "OPPO  CPH2581");
        AppPasswords.NameDevice(db, own, "OPPO CPH2581");
        list = AppPasswords.List(db);
        Assert.Equal("Handy-App · OPPO CPH2581", list.First(x => x.Id == auto).Name);
        Assert.Equal("Mein Handy", list.First(x => x.Id == own).Name);
        // Umbenennen
        AppPasswords.Rename(db, own, "  Pixel 8  ");
        Assert.Equal("Pixel 8", AppPasswords.List(db).First(x => x.Id == own).Name);
        Assert.Throws<ArgumentException>(() => AppPasswords.Rename(db, own, " "));
        Assert.Throws<ArgumentException>(() => AppPasswords.Rename(db, own, new string('x', 81)));
        Assert.Throws<KeyNotFoundException>(() => AppPasswords.Rename(db, "gibt-es-nicht", "x"));
    }

    [Theory]
    [InlineData("DESKTOP-4711 (Desktop Client - Windows)", "Nextcloud Desktop · DESKTOP-4711")]
    [InlineData("Mozilla/5.0 (Windows) mirall/3.13.0 (Nextcloud, windows-10.0)", "Nextcloud Desktop")]
    public void Name_aus_dem_Login_Flow(string userAgent, string name) => Assert.Equal(name, DmOffice.Dav.NextcloudApi.DeviceName(userAgent));
}
