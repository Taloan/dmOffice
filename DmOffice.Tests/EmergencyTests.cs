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

        // Cleo fragt an: Anna und Ben sehen das rote Abzeichen, Dora nicht
        var ask = await _fx.As("cleo").PostAsync($"/api/emergency/packages/{pkg}/request", null);
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var req = (await Json(ask)).GetProperty("id").GetString()!;
        foreach (var (who, n) in new[] { ("anna", 1), ("ben", 1), ("dora", 0), ("cleo", 0) })
            Assert.Equal(n, (await Json(await _fx.As(who).GetAsync("/api/emergency/badge"))).GetProperty("count").GetInt32());
        // Nicht zweimal, und Cleo kann nicht gegen sich selbst widersprechen
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("cleo").PostAsync($"/api/emergency/packages/{pkg}/request", null)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await _fx.As("cleo").PostAsync($"/api/emergency/requests/{req}/deny", null)).StatusCode);

        // Ben: „Kein Notfall“ - ein Widerspruch genuegt; Cleo darf nicht gleich wieder
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("ben").PostAsync($"/api/emergency/requests/{req}/deny", null)).StatusCode);
        Assert.Equal(EmergencyRequest.Denied, store.Request(req)!.State);
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("cleo").PostAsync($"/api/emergency/packages/{pkg}/request", null)).StatusCode);

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
    public void Notfallsitzung_darf_Sicherheit_nicht_aendern(string path, string method, bool blocked) =>
        Assert.Equal(blocked, EmergencyApi.Blocked(path, method));
}
