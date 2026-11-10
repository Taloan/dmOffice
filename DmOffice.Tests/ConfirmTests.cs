using System.Net;
using System.Net.Http.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Heikles aus einer laufenden Sitzung nur mit dem Anmelde-Passwort: 2FA einschalten, App-Passwort anlegen.</summary>
[Collection("server")]
public class ConfirmTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ConfirmTests(ChatFixture fx) => _fx = fx;

    private async Task<HttpClient> Browser(string login)
    {
        var b = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        (await b.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).EnsureSuccessStatusCode();
        return b;
    }

    [Fact]
    public async Task App_Passwort_und_Zwei_Faktor_nur_mit_Passwort()
    {
        var b = await Browser("cleo");
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/settings/app-passwords", new { name = "Fremd" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/settings/app-passwords", new { name = "Fremd", password = "falsch-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/settings/app-passwords", new { name = "Eigen", password = "geheim-123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/auth/2fa/begin", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/auth/2fa/begin", new { password = "geheim-123456" })).StatusCode);
        // Gemeldet: im Sicherheitsprotokoll
        var db = _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Id("cleo")).Db;
        Assert.Contains(SecurityNotice.Of(db), e => e.What.Contains("App-Passwort"));
    }

    [Fact]
    public async Task Neue_Weiterleitung_nur_mit_Passwort()
    {
        var b = await Browser("cleo");
        var rules = new[] { new { name = "Alles", conditions = Array.Empty<object>(), actions = new[] { new { type = "forward", value = "fremd@example.org" } } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PutAsJsonAsync("/api/settings/rules", rules)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.PutAsJsonAsync("/api/settings/rules", new { rules, password = "geheim-123456" })).StatusCode);
        // Dieselbe Weiterleitung erneut speichern (etwa nach anderer Änderung): ohne Passwort
        Assert.Equal(HttpStatusCode.OK, (await b.PutAsJsonAsync("/api/settings/rules", rules)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.PutAsJsonAsync("/api/settings/rules", Array.Empty<object>())).StatusCode);
    }

    [Fact]
    public async Task Mit_Zwei_Faktor_neue_Weiterleitung_nur_mit_Code()
    {
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var id = _fx.Id("dora");
        var b = await Browser("dora");
        users.Update(id, u => { u.TotpSecret = users.Wrap(Totp.NewSecret()); u.RecoveryCodes = [Passwords.TokenHash("RETTUNG-1")]; });
        try
        {
            var rules = new[] { new { name = "Alles", conditions = Array.Empty<object>(), actions = new[] { new { type = "forward", value = "fremd@example.org" } } } };
            // Passwort allein reicht nicht
            Assert.Equal(HttpStatusCode.Forbidden, (await b.PutAsJsonAsync("/api/settings/rules", new { rules, password = "geheim-123456" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await b.PutAsJsonAsync("/api/settings/rules", new { rules, password = "geheim-123456", code = "RETTUNG-1" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await b.PutAsJsonAsync("/api/settings/rules", Array.Empty<object>())).StatusCode);
        }
        finally { users.Update(id, u => { u.TotpSecret = null; u.RecoveryCodes = []; }); }
    }
}
