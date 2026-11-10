using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace DmOffice.Tests;

/// <summary>Schutz der Schnittstelle: Anfragen von fremden Seiten, Anmeldeablauf, App-Passwoerter und Mailkonten.</summary>
[Collection("server")]
public class ApiHardeningTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public ApiHardeningTests(FilesServerFixture fx) => _fx = fx;
    private const string L = FilesServerFixture.Login;
    private const string Password = "geheim-123456";

    private async Task<HttpClient> SignedIn()
    {
        var c = _fx.Client(signedIn: false, cookies: true);
        var r = await c.PostAsJsonAsync("/api/auth/login", new { login = L, password = Password });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return c;
    }

    private static HttpRequestMessage Mkdir(string path, string? fetchSite)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/files/mkdir") { Content = JsonContent.Create(new { path }) };
        if (fetchSite != null) req.Headers.Add("Sec-Fetch-Site", fetchSite);
        return req;
    }

    [Fact]
    public async Task Aendernde_Cookie_Anfragen_von_Nachbar_Seiten_werden_abgewiesen()
    {
        var c = await SignedIn();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Mkdir("csrf-" + stamp + "-a", "same-site"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Mkdir("csrf-" + stamp + "-b", "cross-site"))).StatusCode);
        // Von der eigenen Seite, oder ohne den Kopf (aeltere Browser, Programme): erlaubt
        Assert.True((await c.SendAsync(Mkdir("csrf-" + stamp + "-c", "same-origin"))).IsSuccessStatusCode);
        Assert.True((await c.SendAsync(Mkdir("csrf-" + stamp + "-d", null))).IsSuccessStatusCode);
        // Lesen von einer Nachbar-Seite bleibt unberuehrt (kein Zustand geaendert)
        var get = new HttpRequestMessage(HttpMethod.Get, "/api/files/info");
        get.Headers.Add("Sec-Fetch-Site", "same-site");
        Assert.True((await c.SendAsync(get)).IsSuccessStatusCode);
        // App-Passwort (kein Cookie) ist nicht betroffen
        Assert.True((await _fx.Client().SendAsync(Mkdir("csrf-" + stamp + "-e", "cross-site"))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Anmeldung_aus_neuem_Browser_wird_gemeldet()
    {
        int Notices() => DmOffice.Core.SecurityNotice.Of(_fx.Db).Count(e => e.What.Contains("neuer Browser"));
        _fx.Db.Set("auth.known-browsers", null);
        var before = Notices();
        // Erste Anmeldung nach Einführung: still als bekannt
        var first = await SignedIn();
        Assert.Equal(before, Notices());
        // Derselbe Browser noch einmal: nichts
        var again = await first.PostAsJsonAsync("/api/auth/login", new { login = L, password = Password });
        Assert.True(again.IsSuccessStatusCode);
        Assert.Equal(before, Notices());
        // Ein anderer Browser: gemeldet
        await SignedIn();
        Assert.Equal(before + 1, Notices());
        Assert.Equal("Firefox unter Windows", Auth.BrowserName("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0"));
    }

    [Fact]
    public async Task Sitzungsliste_zeigt_Browser_und_meldet_einzelne_ab()
    {
        var a = await SignedIn();
        var b = await SignedIn();
        var list = await a.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/sessions");
        var mine = list.EnumerateArray().Single(x => x.GetProperty("current").GetBoolean());
        var other = (await b.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/sessions")).EnumerateArray().Single(x => x.GetProperty("current").GetBoolean());
        Assert.NotEqual(mine.GetProperty("id").GetString(), other.GetProperty("id").GetString());
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("id").GetString() == other.GetProperty("id").GetString());

        // Browser b aus der Liste abmelden: sein Cookie gilt nicht mehr, a bleibt angemeldet
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync("/api/auth/sessions/" + other.GetProperty("id").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/auth/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/auth/sessions")).StatusCode);

        // Abmelden nimmt den eigenen Eintrag heraus
        var c = await SignedIn();
        var cId = (await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/sessions")).EnumerateArray().Single(x => x.GetProperty("current").GetBoolean()).GetProperty("id").GetString();
        await c.PostAsync("/api/auth/logout", null);
        Assert.DoesNotContain((await a.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/sessions")).EnumerateArray(), x => x.GetProperty("id").GetString() == cId);
    }

    [Fact]
    public async Task Login_Flow_warnt_bei_Anmeldung_aus_anderem_Netz()
    {
        // Jemand beginnt die Anmeldung woanders und schickt den Link (Phishing per Zustimmung)
        var foreign = DmOffice.Dav.NextcloudApi.Flows.Start("Nextcloud Desktop", v1: false, "203.0.113.7");
        var page = await (await _fx.Client(signedIn: false).GetAsync("/index.php/login/v2/flow/" + foreign.LoginToken)).Content.ReadAsStringAsync();
        Assert.Contains("anderen Netz", page);
        Assert.Contains("nicht geprüft", page);
        // Aus demselben Netz: keine Warnung
        var anon = _fx.Client(signedIn: false);
        var start = await (await anon.PostAsync("/index.php/login/v2", null)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var own = new Uri(start.GetProperty("login").GetString()!).PathAndQuery;
        Assert.DoesNotContain("anderen Netz", await (await anon.GetAsync(own)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_Flow_verlangt_auch_angemeldet_das_Passwort()
    {
        var anon = _fx.Client(signedIn: false);
        anon.DefaultRequestHeaders.UserAgent.ParseAdd("mirall/3.0");
        var start = await (await anon.PostAsync("/index.php/login/v2", null)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var loginUrl = new Uri(start.GetProperty("login").GetString()!).PathAndQuery;
        var pollToken = start.GetProperty("poll").GetProperty("token").GetString()!;

        var c = await SignedIn();
        // Nur ein Klick (angemeldet, ohne Passwort): kein App-Passwort
        var click = await c.PostAsync(loginUrl, new FormUrlEncodedContent(new Dictionary<string, string> { ["login"] = L }));
        Assert.DoesNotContain("Fertig", await click.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsync("/index.php/login/v2/poll", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = pollToken }))).StatusCode);

        // Mit Passwort: freigegeben, App-Passwort nur fuer DAV
        var ok = await c.PostAsync(loginUrl, new FormUrlEncodedContent(new Dictionary<string, string> { ["login"] = L, ["password"] = Password }));
        Assert.Contains("Fertig", await ok.Content.ReadAsStringAsync());
        var poll = await anon.PostAsync("/index.php/login/v2/poll", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = pollToken }));
        var appPassword = (await poll.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("appPassword").GetString()!;
        var dav = _fx.Client(signedIn: false);
        dav.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{L}:{appPassword}")));
        var prop = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/files/{L}/");
        prop.Headers.Add("Depth", "0");
        Assert.Equal((HttpStatusCode)207, (await dav.SendAsync(prop)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await dav.GetAsync("/api/mail/accounts")).StatusCode);
    }

    [Fact]
    public async Task Fehlversuche_mit_dem_Namen_sperren_das_App_Passwort_nicht()
    {
        // Eigenes Konto - die Sperre soll die anderen Tests nicht treffen
        var services = _fx.Factory.Services;
        var login = "sperre" + Guid.NewGuid().ToString("N")[..6];
        var u = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<DmOffice.Core.UserStore>(services)
            .Create(login, "Sperre", "geheim-123456", isAdmin: false);
        var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<DmOffice.Core.Spaces>(services).Get(u.Id).Db;
        var appPassword = AppPasswords.Create(db, "Test");

        var attacker = _fx.Client(signedIn: false);
        for (int i = 0; i < 6; i++) await attacker.PostAsJsonAsync("/api/auth/login", new { login, password = "falsch" + i });
        // Web-Anmeldung mit dem Namen ist jetzt gesperrt (Schutz des Kontos) ...
        Assert.Equal((HttpStatusCode)429, (await _fx.Client(signedIn: false).PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).StatusCode);
        // ... aber DAV- und App-Zugaenge mit App-Passwort laufen weiter
        var dav = _fx.Client(signedIn: false);
        dav.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{appPassword}")));
        var prop = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/files/{login}/");
        prop.Headers.Add("Depth", "0");
        Assert.Equal((HttpStatusCode)207, (await dav.SendAsync(prop)).StatusCode);
    }

    [Fact]
    public async Task Bekannter_Browser_hat_eigenen_Zaehler_fuer_Fehlversuche()
    {
        var services = _fx.Factory.Services;
        var login = "bekannt" + Guid.NewGuid().ToString("N")[..6];
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<DmOffice.Core.UserStore>(services)
            .Create(login, "Bekannt", "geheim-123456", isAdmin: false);
        // Die richtige Person hat sich in diesem Browser schon einmal angemeldet
        var browser = _fx.Client(signedIn: false, cookies: true);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).StatusCode);
        await browser.PostAsync("/api/auth/logout", null);

        // Fehlversuche von einer anderen Adresse
        var attacker = _fx.Client(signedIn: false);
        for (int i = 0; i < 6; i++) await attacker.PostAsJsonAsync("/api/auth/login", new { login, password = "falsch" + i });
        // Neuer Browser: wie bisher gesperrt
        Assert.Equal((HttpStatusCode)429, (await _fx.Client(signedIn: false).PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).StatusCode);
        // Bekannter Browser: kommt hinein
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).StatusCode);
        // ... der Zähler des Namens bleibt dabei stehen
        Assert.Equal((HttpStatusCode)429, (await _fx.Client(signedIn: false).PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).StatusCode);
        // Falsche Passwörter im bekannten Browser bremsen nur ihn selbst
        await browser.PostAsync("/api/auth/logout", null);
        for (int i = 0; i < 5; i++) await browser.PostAsJsonAsync("/api/auth/login", new { login, password = "vertippt" + i });
        Assert.Equal((HttpStatusCode)429, (await browser.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).StatusCode);
    }

    [Fact]
    public async Task Gespeichertes_Mailpasswort_geht_nicht_an_einen_neuen_Server()
    {
        var c = await SignedIn();

        var account = new
        {
            name = "Test", email = "berta@example.invalid", displayName = "Berta",
            imapHost = "imap.example.invalid", imapPort = 993, imapUser = "berta", imapPassword = "pw-imap",
            smtpHost = "smtp.example.invalid", smtpPort = 465, smtpUser = "berta", smtpPassword = "pw-smtp",
        };
        var created = await c.PostAsJsonAsync("/api/settings/accounts", account);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;

        // Anderer Server, Passwort leer (= "das gespeicherte behalten"): abgewiesen
        var put = await c.PutAsJsonAsync($"/api/settings/accounts/{id}", new
        {
            account.name, account.email, account.displayName,
            imapHost = "boese.example.invalid", account.imapPort, account.imapUser, imapPassword = "",
            account.smtpHost, account.smtpPort, account.smtpUser, smtpPassword = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("Passwort", await put.Content.ReadAsStringAsync());
        var test = await c.PostAsJsonAsync("/api/settings/accounts/test", new
        {
            id, account.name, account.email, account.displayName,
            imapHost = "boese.example.invalid", account.imapPort, account.imapUser, imapPassword = "",
            account.smtpHost, account.smtpPort, account.smtpUser, smtpPassword = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, test.StatusCode);
        // Nur die Signatur geaendert (gleicher Server): geht ohne Passwort
        var same = await c.PutAsJsonAsync($"/api/settings/accounts/{id}", new
        {
            account.name, account.email, account.displayName, signature = "Gruss",
            account.imapHost, account.imapPort, account.imapUser, imapPassword = "",
            account.smtpHost, account.smtpPort, account.smtpUser, smtpPassword = "",
        });
        Assert.True(same.IsSuccessStatusCode, await same.Content.ReadAsStringAsync());
        // Loopback als Ziel: abgewiesen
        var local = await c.PostAsJsonAsync("/api/settings/accounts", new
        {
            account.name, account.email, account.displayName,
            imapHost = "127.0.0.1", account.imapPort, account.imapUser, account.imapPassword,
            account.smtpHost, account.smtpPort, account.smtpUser, account.smtpPassword,
        });
        Assert.Equal(HttpStatusCode.BadRequest, local.StatusCode);
    }

    [Fact]
    public async Task Browser_Umschlag_gilt_nur_mit_gueltiger_Anmeldung()
    {
        var c = await SignedIn();
        using var mine = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var reg = await c.PostAsJsonAsync("/api/e2e/register", new { pubKey = E2e.ExportPublic(mine) });
        Assert.True(reg.IsSuccessStatusCode, await reg.Content.ReadAsStringAsync());
        var r = System.Text.Json.JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement;
        var url = $"/api/e2e/{r.GetProperty("userId").GetString()}/{r.GetProperty("deviceId").GetString()}";
        HttpRequestMessage Envelope()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(new byte[40]) };
            req.Headers.Add(E2e.CounterHeader, "1");
            return req;
        }

        // Mit Anmeldung: der Umschlag wird geprüft (hier unecht: 400); ohne Cookie gilt das Gerät nicht (401)
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Envelope())).StatusCode);
        var stranger = _fx.Client(signedIn: false, cookies: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.SendAsync(Envelope())).StatusCode);
        // Nach dem Abmelden auch nicht mehr
        await c.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Envelope())).StatusCode);
    }
}
