using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>
/// Security Center: ohne Anmeldung nur die festen Wege - ein unbekannter Pfad ohne jedes Anmeldemerkmal und Scanner sperren
/// sofort; mit Anmeldemerkmal, aus erlaubten Netzen und von Adressen mit gültiger Anmeldung nicht. Gelöschte App-Passwörter
/// gelten als altes Gerät, nie vergebene als Raten.
/// </summary>
[Collection("server")]
public class SecurityCenterTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public SecurityCenterTests(FilesServerFixture fx) => _fx = fx;

    private static string Ip() => $"203.0.113.{Random.Shared.Next(1, 255)}";
    private HttpClient From(string ip, string? appPassword = null, string? agent = null)
    {
        var c = _fx.Client(signedIn: false);
        c.DefaultRequestHeaders.Remove("X-Test-Ip");
        c.DefaultRequestHeaders.Add("X-Test-Ip", ip);
        if (appPassword != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{FilesServerFixture.Login}:{appPassword}")));
        if (agent != null) c.DefaultRequestHeaders.UserAgent.ParseAdd(agent);
        return c;
    }
    private static SecurityCenter Sc => SecurityCenter.Shared!;

    [Fact]
    public async Task Unbekannter_Pfad_ohne_Anmeldung_sperrt_sofort()
    {
        var ip = Ip();
        var c = From(ip);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/phpinfo.php")).StatusCode);
        Assert.NotNull(Sc.BlockOf(ip));
        // Danach ist auch Erlaubtes zu - für diese Adresse
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/health")).StatusCode);
        // ... außer wirklich Angemeldeten (Haushalt hinter derselben Adresse)
        Assert.Equal(HttpStatusCode.OK, (await From(ip, _fx.AppPassword).GetAsync("/api/files/")).StatusCode);
        // Ein vorgetäuschtes Merkmal genügt nicht
        Assert.Equal(HttpStatusCode.Forbidden, (await From(ip, "falsch").GetAsync("/api/health")).StatusCode);
        Assert.Contains(Sc.Events(ip), e => e.Kind == "falle" && e.Path == "/phpinfo.php");
        Assert.True(Sc.Unblock(ip, "test"));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task Pfad_ohne_Punkt_bekommt_nicht_mehr_die_Startseite()
    {
        var ip = Ip();
        Assert.Equal(HttpStatusCode.NotFound, (await From(ip).GetAsync("/wp-admin")).StatusCode);
        Assert.NotNull(Sc.BlockOf(ip));
        Sc.Unblock(ip, "test");
    }

    [Fact]
    public async Task Scanner_im_User_Agent()
    {
        var ip = Ip();
        await From(ip, agent: "sqlmap/1.7").GetAsync("/");
        Assert.Contains("Scanner", Sc.BlockOf(ip)!.Reason);
        Sc.Unblock(ip, "test");
    }

    [Fact]
    public async Task Nicht_gesperrt_mit_Anmeldemerkmal_erlaubtem_Netz_oder_gueltiger_Anmeldung()
    {
        // Abgelaufene Sitzung, altes Gerät: irgendein Merkmal - kein Abtasten
        var ip = Ip();
        await From(ip, "veraltet").GetAsync("/api/gibt-es-nicht");
        Assert.Null(Sc.BlockOf(ip));
        // Heimnetz, Tailscale
        await From("10.1.2.3").GetAsync("/.env");
        await From("100.101.102.103").GetAsync("/.env");
        Assert.Null(Sc.BlockOf("10.1.2.3"));
        // Von hier hat sich gerade jemand gültig angemeldet: festhalten, nicht sperren
        var home = Ip();
        Assert.Equal(HttpStatusCode.OK, (await From(home, _fx.AppPassword).GetAsync("/api/files/")).StatusCode);
        await From(home).GetAsync("/.git/config");
        Assert.Null(Sc.BlockOf(home));
        Assert.Contains(Sc.Events(home), e => e.Kind == "falle" && e.Detail != null && e.Detail.Contains("gültiger Anmeldung"));
        // Die Seiten der Oberfläche und Freigabelinks sind kein Abtasten
        var visitor = Ip();
        foreach (var p in new[] { "/", "/s/abc", "/a/abc", "/favicon.ico", "/apple-touch-icon.png", "/status.php" }) await From(visitor).GetAsync(p);
        Assert.Null(Sc.BlockOf(visitor));
    }

    [Fact]
    public async Task Schreibweisen_der_Anmeldeseite_und_Geraeteanfragen_sind_erlaubt()
    {
        // Großbuchstaben, Schrägstrich am Ende, Symbole und Dateien, die Browser und Geräte von sich aus abfragen
        foreach (var p in new[] { "/login/", "/LOGIN", "/Login/", "/index.html/", "/favicon.png", "/browserconfig.xml", "/apple-app-site-association" })
        {
            var ip = Ip();
            await From(ip).GetAsync(p);
            Assert.True(Sc.BlockOf(ip) == null, $"{p} hat gesperrt");
        }
        // Unbekanntes bleibt eine Falle
        var probe = Ip();
        await From(probe).GetAsync("/login.php");
        Assert.NotNull(Sc.BlockOf(probe));
        Sc.Unblock(probe, "test");
    }

    [Fact]
    public async Task Geloeschtes_App_Passwort_ist_altes_Geraet_erratenes_ist_verdaechtig()
    {
        var token = AppPasswords.Create(_fx.Db, "Altes Handy");
        var id = AppPasswords.List(_fx.Db).First(x => x.Name == "Altes Handy").Id;
        AppPasswords.Delete(_fx.Db, id);
        Assert.Equal("Altes Handy", AppPasswords.WasValid(_fx.Db, token));
        // Altes Gerät von einer neuen Adresse (WLAN → Mobilfunk): abgelehnt, aber weder gesperrt noch verdächtig
        var ip = Ip();
        for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await From(ip, token).GetAsync("/api/files/")).StatusCode);
        Assert.Null(Sc.BlockOf(ip));
        Assert.Contains(Sc.Events(ip), e => e.Kind == "altes-geraet" && e.Detail!.Contains("Altes Handy"));
        Assert.DoesNotContain(Sc.Events(ip), e => e.Kind == "app-passwort");
        // Nie vergeben: Raten
        var guess = Ip();
        await From(guess, "AAAA-BBBB-CCCC-DDDD").GetAsync("/api/files/");
        Assert.Contains(Sc.Events(guess), e => e.Kind == "app-passwort");
    }

    [Fact]
    public void Von_Hand_sperren_und_Erlaubt_Liste()
    {
        var ip = Ip();
        var b = Sc.BlockNow(ip, "von Hand gesperrt", null, "admin");
        Assert.Null(b.Until);
        Assert.Contains(Sc.Blocks(), x => x.Key == ip);
        Sc.Unblock(ip, "admin");
        Sc.Allow("198.51.100.0/24", "Büro");
        Assert.True(Sc.Allowed(IPAddress.Parse("198.51.100.77")));
        Sc.Disallow("198.51.100.0/24");
        Assert.False(Sc.Allowed(IPAddress.Parse("198.51.100.77")));
        Assert.Throws<ArgumentException>(() => SecurityCenter.ParseNet("kein netz"));
    }

    [Fact]
    public async Task AbuseIPDB_bekannte_Adresse_sofort_gesperrt_Fallen_gemeldet()
    {
        var bad = Ip();
        var good = Ip();
        var asked = new List<string>();
        var reported = new List<(string Ip, string Categories, string Comment)>();
        SecurityCenter.CheckOverride = (_, ip) => { lock (asked) asked.Add(ip); return Task.FromResult<int?>(ip == bad ? 100 : 0); };
        SecurityCenter.ReportOverride = (ip, cat, comment) => reported.Add((ip, cat, comment));
        var before = Sc.Current;
        try
        {
            Sc.SaveAbuseKey("test-key");
            Sc.Save(before with { AbuseCheck = true, AbuseScore = 90, AbuseDays = 7, AbuseReport = true });
            // Bekannt: schon der erste, erlaubte Weg ist zu
            Assert.Equal(HttpStatusCode.Forbidden, (await From(bad).GetAsync("/api/health")).StatusCode);
            Assert.Equal("AbuseIPDB: 100 %", Sc.BlockOf(bad)?.Reason);
            Assert.Contains(Sc.Events(bad), e => e.Kind == "ruf");
            // Unauffällig: durch, und am selben Tag nur einmal gefragt
            Assert.Equal(HttpStatusCode.OK, (await From(good).GetAsync("/api/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await From(good).GetAsync("/api/health")).StatusCode);
            Assert.Single(asked, x => x == good);
            // In die Falle gelaufen: gesperrt und gemeldet, auf Englisch mit dem Namen des Systems
            Assert.Equal(HttpStatusCode.NotFound, (await From(good).GetAsync("/wp-login.php")).StatusCode);
            var r = Assert.Single(reported, x => x.Ip == good);
            Assert.Contains("dmoffice.de", r.Comment);
            Assert.Contains("/wp-login.php", r.Comment);
            // Melden aus: nichts geht hinaus
            Sc.Save(Sc.Current with { AbuseReport = false });
            var other = Ip();
            await From(other).GetAsync("/xmlrpc.php");
            Assert.DoesNotContain(reported, x => x.Ip == other);
            Sc.Unblock(other, "test");
        }
        finally
        {
            Sc.Save(before);
            Sc.SaveAbuseKey(null);
            SecurityCenter.CheckOverride = null;
            SecurityCenter.ReportOverride = null;
            Sc.Unblock(bad, "test");
            Sc.Unblock(good, "test");
        }
    }
}
