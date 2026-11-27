using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Emergency;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>
/// Notfallzugang: anfragen, ein Widerspruch genuegt, Herausgenommene zaehlen nicht mehr, nach der Frist gewaehrt, zuruecknehmen,
/// Verwaltung, und was eine Notfallsitzung nicht darf.
/// </summary>
[Collection("server")]
public class EmergencyTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public EmergencyTests(SharedCalendarFixture fx) => _fx = fx;

    private string Id(string login) => _fx.Users[login].Id;
    private T Get<T>() where T : notnull => _fx.Factory.Services.GetRequiredService<T>();
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Notfallcode für ein Paket anlegen (Tresor der Besitzerin in einem Wegwerf-Ordner).</summary>
    private string Keys(string owner, string package)
    {
        var dir = Directory.CreateTempSubdirectory("tresor-").FullName;
        using var tresor = new TresorDb(Path.Combine(dir, "t.db"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), Path.Combine(dir, "blobs"));
        return Get<EmergencyContent>().CreateKeys(tresor, owner, package);
    }

    private async Task<long> Folder(string login, string path)
    {
        Assert.Equal(HttpStatusCode.OK, (await _fx.As(login).PostAsJsonAsync("/api/files/mkdir", new { path })).StatusCode);
        return FileStore.Get(Get<Spaces>().Get(Id(login)).Db, path)!.FileId;
    }

    [Fact]
    public async Task Anfrage_Widerspruch_Frist_und_Zuruecknehmen()
    {
        var path = "Verträge-" + Guid.NewGuid().ToString("N")[..6];
        var folder = await Folder("anna", path);
        // Mit Ben ist der Ordner schon geteilt - er darf darum widersprechen
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("anna").PutAsJsonAsync("/api/files/shares", new { path, members = new[] { Id("ben") }, readOnly = Array.Empty<string>() })).StatusCode);

        // Einrichten nur im Browser - ein Handy (App-Passwort) darf das nicht
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("anna").PutAsJsonAsync("/api/emergency/packages", new
        {
            name = "Familie", folders = new[] { folder }, requesters = new[] { Id("cleo") }, waitDays = 3,
        })).StatusCode);
        var store = Get<EmergencyStore>();
        var pkg = store.Save(Id("anna"), new EmergencyPackage { Name = "Familie", Folders = [folder], Requesters = [Id("cleo")], WaitDays = 3 }).Id;

        // Nur wer anfragen darf, sieht das Paket
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").GetAsync($"/api/emergency/packages/{pkg}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").GetAsync($"/api/emergency/packages/{pkg}")).StatusCode);

        // Ohne Notfallcode lässt sich das Paket nicht anfordern
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("cleo").PostAsJsonAsync($"/api/emergency/packages/{pkg}/request", new { code = "" })).StatusCode);
        var code = Keys(Id("anna"), pkg);
        // Falscher Notfallcode: abgelehnt, keine Anfrage
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PostAsJsonAsync($"/api/emergency/packages/{pkg}/request", new { code = "AAAA-AAAA-AAAA-AAAA-AAAA" })).StatusCode);
        Assert.Empty(store.RequestsOf(pkg));
        // Dora darf nicht, auch nicht mit dem richtigen Code
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").PostAsJsonAsync($"/api/emergency/packages/{pkg}/request", new { code })).StatusCode);

        // Cleo fragt an: Anna und Ben sehen das rote Abzeichen, Dora nicht
        var ask = await _fx.As("cleo").PostAsJsonAsync($"/api/emergency/packages/{pkg}/request", new { code = code.ToLowerInvariant().Replace("-", " ") });
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var req = (await Json(ask)).GetProperty("id").GetString()!;
        foreach (var (who, n) in new[] { ("anna", 1), ("ben", 1), ("dora", 0), ("cleo", 0) })
            Assert.Equal(n, (await Json(await _fx.As(who).GetAsync("/api/emergency/badge"))).GetProperty("count").GetInt32());
        // Nicht zweimal, und Cleo kann nicht gegen sich selbst widersprechen
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("cleo").PostAsJsonAsync($"/api/emergency/packages/{pkg}/request", new { code })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await _fx.As("cleo").PostAsync($"/api/emergency/requests/{req}/deny", null)).StatusCode);

        // Ben: „Kein Notfall“ - ein Widerspruch genuegt; Cleo darf nicht gleich wieder
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("ben").PostAsync($"/api/emergency/requests/{req}/deny", null)).StatusCode);
        Assert.Equal(EmergencyRequest.Denied, store.Request(req)!.State);
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("cleo").PostAsJsonAsync($"/api/emergency/packages/{pkg}/request", new { code })).StatusCode);

        // Anna nimmt Ben das Widerspruchsrecht: sein Widerspruch zaehlt nicht mehr, die Frist laeuft weiter
        store.Save(Id("anna"), new EmergencyPackage { Id = pkg, Name = "Familie", Folders = [folder], Requesters = [Id("cleo")], NoVeto = [Id("ben")], WaitDays = 3 });
        Assert.Equal(EmergencyRequest.Open, store.Request(req)!.State);
        Assert.Equal(0, (await Json(await _fx.As("ben").GetAsync("/api/emergency/badge"))).GetProperty("count").GetInt32());

        // Vor der Frist nichts, danach genau der Ordner zum Lesen
        var svc = Get<EmergencyService>();
        svc.Tick(DateTime.UtcNow.AddDays(1));
        Assert.Equal(EmergencyRequest.Open, store.Request(req)!.State);
        svc.Tick(DateTime.UtcNow.AddDays(4));
        Assert.Equal(EmergencyRequest.Granted, store.Request(req)!.State);
        var share = Get<FolderShareStore>().Of(Id("anna"), folder)!;
        Assert.Contains(Id("cleo"), share.Members);
        Assert.Contains(Id("cleo"), share.ReadOnly);
        Assert.Contains(Id("ben"), share.Members);

        // Zuruecknehmen: nur was der Notfall hinzugefuegt hat - Ben bleibt
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("anna").PostAsync($"/api/emergency/requests/{req}/revoke", null)).StatusCode);
        share = Get<FolderShareStore>().Of(Id("anna"), folder)!;
        Assert.DoesNotContain(Id("cleo"), share.Members);
        Assert.Contains(Id("ben"), share.Members);
        Assert.Equal(EmergencyRequest.Revoked, store.Request(req)!.State);

        store.Delete(Id("anna"), pkg);
        Assert.Null(store.Package(pkg));
    }

    [Fact]
    public async Task Person_ohne_Konto_fragt_ueber_ihren_Link_an_und_bekommt_nach_der_Frist_ein_Konto()
    {
        var path = "Ordner-" + Guid.NewGuid().ToString("N")[..6];
        var folder = await Folder("anna", path);
        var mails = new List<(string To, string Subject, string Text)>();
        EmergencyMail.Capture = (to, subject, text) => { lock (mails) mails.Add((to, subject, text)); };
        var store = Get<EmergencyStore>();
        var pkg = store.Save(Id("anna"), new EmergencyPackage
        {
            Name = "Ohne Konto", Folders = [folder], WaitDays = 2, LinkBase = "https://office.example",
            Contacts = [new EmergencyContact { Name = "Erika Muster", Email = "Erika.Muster@example.org" }],
        });
        try
        {
            var contact = Assert.Single(pkg.Contacts);
            Assert.Equal("erika.muster@example.org", contact.Email);
            var token = store.TokenOf(contact);
            var anon = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            FormUrlEncodedContent Form(params (string, string)[] f) => new(f.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)));

            // Falscher Link: nichts; richtiger: Seite ohne Anmeldung
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/n/" + new string('x', 43))).StatusCode);
            var page = await anon.GetAsync("/n/" + token);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("Erika Muster", await page.Content.ReadAsStringAsync());

            // Ohne Notfallcode im Paket: kein Formular, Anfordern abgelehnt
            Assert.DoesNotContain("name=\"code\"", await page.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsync($"/n/{token}/request", Form(("code", "x")))).StatusCode);
            var code = Keys(Id("anna"), pkg.Id);
            Assert.Contains("name=\"code\"", await (await anon.GetAsync("/n/" + token)).Content.ReadAsStringAsync());

            // Anfordern nur mit dem Notfallcode - keine Mail dafür
            Assert.Equal(HttpStatusCode.Forbidden, (await anon.PostAsync($"/n/{token}/request", Form(("code", "AAAA-AAAA-AAAA-AAAA-AAAA")))).StatusCode);
            Assert.Empty(store.RequestsOf(pkg.Id));
            Assert.Empty(mails);
            Assert.Equal(HttpStatusCode.Redirect, (await anon.PostAsync($"/n/{token}/request", Form(("code", code)))).StatusCode);
            var req = Assert.Single(store.RequestsOf(pkg.Id));
            Assert.Equal(EmergencyStore.ContactPrefix + contact.Id, req.Requester);
            Assert.Equal(EmergencyRequest.Open, req.State);
            // Anna darf widersprechen und sieht die Anfrage
            Assert.Contains(store.ToDecide(Id("anna")), r => r.Id == req.Id);
            Assert.Contains("Erika Muster", Get<EmergencyService>().Name(req.Requester));
            // Nicht zweimal
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsync($"/n/{token}/request", Form(("code", code)))).StatusCode);

            // Vor der Frist kein Konto; danach Konto, Ordner zum Lesen und Mail mit Anmeldename
            var svc = Get<EmergencyService>();
            svc.Tick(DateTime.UtcNow.AddDays(1));
            Assert.Null(store.Package(pkg.Id)!.Contacts[0].UserId);
            svc.Tick(DateTime.UtcNow.AddDays(3));
            var granted = store.Request(req.Id)!;
            Assert.Equal(EmergencyRequest.Granted, granted.State);
            var userId = store.Package(pkg.Id)!.Contacts[0].UserId!;
            Assert.Equal(userId, granted.Requester);
            Assert.Contains(userId, store.Package(pkg.Id)!.Requesters);
            var user = Get<UserStore>().ById(userId)!;
            Assert.Equal("erika.muster", user.Login);
            Assert.Equal("Erika Muster", user.DisplayName);
            Assert.Contains(userId, Get<FolderShareStore>().Of(Id("anna"), folder)!.ReadOnly);
            for (var i = 0; i < 50 && !mails.Any(m => m.Text.Contains("erika.muster")); i++) await Task.Delay(100);
            Assert.Contains(mails, m => m.Text.Contains("Anmeldename: erika.muster") && m.Text.Contains("/n/" + token));

            // Passwort über den Link: wieder mit dem Notfallcode, nur einmal
            Assert.Equal(HttpStatusCode.Forbidden, (await anon.PostAsync($"/n/{token}/password", Form(("code", "AAAA-AAAA-AAAA-AAAA-AAAA"), ("password", "Notfall-2026!"), ("password2", "Notfall-2026!")))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsync($"/n/{token}/password", Form(("code", code), ("password", "Notfall-2026!"), ("password2", "anders-2026!")))).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await anon.PostAsync($"/n/{token}/password", Form(("code", code), ("password", "Notfall-2026!"), ("password2", "Notfall-2026!")))).StatusCode);
            Assert.True(Passwords.Verify("Notfall-2026!", Get<UserStore>().ById(userId)!.PasswordHash));
            Assert.True(store.Package(pkg.Id)!.Contacts[0].PasswordSet);
            Assert.Throws<InvalidOperationException>(() => store.SetPassword(pkg.Id, contact.Id, "Nochmal-2026!"));
            Assert.Contains("erika.muster", await (await anon.GetAsync("/n/" + token)).Content.ReadAsStringAsync());

            // Zurücknehmen: Ordner weg, das Konto bleibt
            store.Revoke(req.Id, Id("anna"));
            Assert.DoesNotContain(userId, Get<FolderShareStore>().Of(Id("anna"), folder)?.Members ?? []);
            Assert.NotNull(Get<UserStore>().ById(userId));
            Get<UserStore>().Delete(userId);
            store.Forget(userId);
        }
        finally
        {
            EmergencyMail.Capture = null;
            store.Delete(Id("anna"), pkg.Id);
        }
    }

    [Fact]
    public void Person_ohne_Konto_entfernt_beendet_ihre_offene_Anfrage_neue_Adresse_nicht()
    {
        var store = Get<EmergencyStore>();
        var p = store.Save(Id("anna"), new EmergencyPackage
        {
            Name = "Adresse", Account = true, WaitDays = 5, Contacts = [new EmergencyContact { Name = "Max", Email = "max@example.org" }],
        });
        var c = p.Contacts[0];
        var r = store.Ask(p.Id, EmergencyStore.ContactPrefix + c.Id);
        // Name ändern: Anfrage läuft weiter, Link bleibt
        p = store.Save(Id("anna"), new EmergencyPackage { Id = p.Id, Name = "Adresse", Account = true, WaitDays = 5, Contacts = [new EmergencyContact { Id = c.Id, Name = "Max M.", Email = "max@example.org" }] });
        Assert.Equal(EmergencyRequest.Open, store.Request(r.Id)!.State);
        Assert.Equal(c.TokenHash, p.Contacts[0].TokenHash);
        // Neue oder keine Adresse: die Anfrage läuft weiter (angefordert wird mit dem Notfallcode, die Mail ist nur ein Hinweis)
        store.Save(Id("anna"), new EmergencyPackage { Id = p.Id, Name = "Adresse", Account = true, WaitDays = 5, Contacts = [new EmergencyContact { Id = c.Id, Name = "Max M.", Email = "" }] });
        Assert.Equal(EmergencyRequest.Open, store.Request(r.Id)!.State);
        // Entfernt: die Anfrage endet
        store.Save(Id("anna"), new EmergencyPackage { Id = p.Id, Name = "Adresse", Account = true, WaitDays = 5, Requesters = [Id("cleo")] });
        Assert.Equal(EmergencyRequest.Withdrawn, store.Request(r.Id)!.State);
        // Ungültige Adresse oder niemand, der anfragen darf: abgelehnt
        Assert.Throws<ArgumentException>(() => store.Save(Id("anna"), new EmergencyPackage { Name = "X", Account = true, Contacts = [new EmergencyContact { Name = "Y", Email = "kein-at" }] }));
        Assert.Throws<ArgumentException>(() => store.Save(Id("anna"), new EmergencyPackage { Name = "X", Account = true }));
        store.Delete(Id("anna"), p.Id);
    }

    [Fact]
    public async Task Rechte_nur_bis_zur_eigenen_Ebene()
    {
        // Ben ist kein Administrator: Admin-Rechte kann er auch über ein Notfallpaket nicht weitergeben
        var res = await _fx.As("ben").PutAsJsonAsync("/api/emergency/packages", new { name = "Hinterhalt", admin = true, requesters = new[] { Id("dora") }, waitDays = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.False(EmergencyStore.MayGrant(Get<UserStore>().ById(Id("ben"))!, true));
        Assert.True(EmergencyStore.MayGrant(Get<UserStore>().ById(Id("ben"))!, false));
        Assert.True(EmergencyStore.MayGrant(Get<UserStore>().ById(Id("anna"))!, true));
        // Paket von früher (Besitzer inzwischen kein Admin): beim Gewähren keine Admin-Rechte
        var store = Get<EmergencyStore>();
        var p = store.Save(Id("ben"), new EmergencyPackage { Name = "Alt", Admin = true, Requesters = [Id("dora")], WaitDays = 1 });
        store.Ask(p.Id, Id("dora"));
        Get<EmergencyService>().Tick(DateTime.UtcNow.AddDays(2));
        Assert.False(Get<UserStore>().ById(Id("dora"))!.IsAdmin);
        store.Delete(Id("ben"), p.Id);
    }

    [Fact]
    public void Verwaltung_und_Konto_nur_solange_gewaehrt()
    {
        var store = Get<EmergencyStore>();
        var p = store.Save(Id("anna"), new EmergencyPackage { Name = "Partner", Account = true, Admin = true, Requesters = [Id("dora")], WaitDays = 1 });
        var r = store.Ask(p.Id, Id("dora"));
        Assert.False(store.MayEnter(r.Id, Id("anna")));
        Get<EmergencyService>().Tick(DateTime.UtcNow.AddDays(2));
        Assert.True(Get<UserStore>().ById(Id("dora"))!.IsAdmin);
        Assert.True(store.MayEnter(r.Id, Id("anna")));
        Assert.False(store.MayEnter(r.Id, Id("ben")));   // nur im Konto des Besitzers

        store.Revoke(r.Id, Id("anna"));
        Assert.False(Get<UserStore>().ById(Id("dora"))!.IsAdmin);
        Assert.False(store.MayEnter(r.Id, Id("anna")));
        store.Delete(Id("anna"), p.Id);
    }

    [Fact]
    public void Kein_Notfall_per_Knopf_im_Chat()
    {
        var store = Get<EmergencyStore>();
        var p = store.Save(Id("anna"), new EmergencyPackage { Name = "Knopf", Admin = true, Requesters = [Id("dora")], WaitDays = 5 });
        var r = store.Ask(p.Id, Id("dora"));
        Assert.Contains("Danke", EmergencyService.DenyFromChat(_fx.Factory.Services, Id("anna"), r.Id));
        Assert.Equal(EmergencyRequest.Denied, store.Request(r.Id)!.State);
        Assert.Contains("schon entschieden", EmergencyService.DenyFromChat(_fx.Factory.Services, Id("anna"), r.Id));
        store.Delete(Id("anna"), p.Id);
    }

    private async Task<HttpClient> Browser(string login)
    {
        var b = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        (await b.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).EnsureSuccessStatusCode();
        return b;
    }

    [Fact]
    public async Task Konto_eines_Admins_nur_mit_Rolle_user()
    {
        var store = Get<EmergencyStore>();
        var p = store.Save(Id("anna"), new EmergencyPackage { Name = "Konto", Account = true, Requesters = [Id("ben")], WaitDays = 1, Contacts = [new EmergencyContact { Name = "Ohne Konto" }] });
        var r = store.Ask(p.Id, Id("ben"));
        Get<EmergencyService>().Tick(DateTime.UtcNow.AddDays(2));
        try
        {
            var b = await Browser("ben");
            Assert.Equal(HttpStatusCode.OK, (await b.PostAsync($"/api/auth/emergency/{r.Id}", null)).StatusCode);
            // Im Konto von Anna, aber ohne Verwaltung - auch bei den folgenden Anfragen (Rolle wird je Anfrage geprüft)
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync("/api/chat/admin/rights")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await b.PostAsJsonAsync("/api/tickets", new { method = "GET", url = "/api/files" })).StatusCode);
            }
            Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/auth/state")).StatusCode);
            // Die Links der Benutzer ohne Konto bleiben in der Notfallsitzung verborgen
            var mine = (await Json(await b.GetAsync("/api/emergency"))).GetProperty("mine");
            Assert.All(mine.EnumerateArray().SelectMany(x => x.GetProperty("contacts").EnumerateArray()), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("token").ValueKind));
            Assert.Contains(mine.EnumerateArray(), x => x.GetProperty("contacts").GetArrayLength() > 0);
            // Anna selbst bleibt Admin
            Assert.Equal(HttpStatusCode.OK, (await (await Browser("anna")).GetAsync("/api/chat/admin/rights")).StatusCode);
        }
        finally
        {
            store.Revoke(r.Id, Id("anna"));
            store.Delete(Id("anna"), p.Id);
        }
    }

    [Theory]
    [InlineData("/api/auth/password", "POST", true)]
    [InlineData("/api/auth/2fa/disable", "POST", true)]
    [InlineData("/api/admin/users", "GET", true)]
    [InlineData("/api/settings/app-passwords", "POST", true)]
    [InlineData("/api/settings/app-passwords", "GET", false)]
    [InlineData("/api/emergency/packages", "PUT", true)]
    [InlineData("/api/emergency", "GET", false)]
    [InlineData("/api/e2e/register", "POST", true)]
    [InlineData("/api/files", "GET", false)]
    [InlineData("/api/mail/messages", "GET", false)]
    [InlineData("/api/auth/logout-all", "POST", true)]
    [InlineData("/api/auth/oidc/unlink", "POST", true)]
    [InlineData("/api/auth/delete-account", "POST", true)]
    [InlineData("/api/auth/sessions/abc", "DELETE", true)]
    [InlineData("/api/auth/state", "GET", false)]
    [InlineData("/api/auth/sessions", "GET", false)]
    [InlineData("/api/auth/logout", "POST", false)]
    [InlineData("/api/tickets", "POST", true)]
    public void Notfallsitzung_darf_Sicherheit_nicht_aendern(string path, string method, bool blocked) =>
        Assert.Equal(blocked, EmergencyApi.Blocked(path, method));
}
