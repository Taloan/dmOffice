using System.Net;
using System.Net.Http.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Standard-Mailkonto (Features.DefaultAccount): genau eines ist Standard, Vorgabe das erste.</summary>
[Collection("server")]
public class DefaultAccountTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public DefaultAccountTests(FilesServerFixture fx) => _fx = fx;

    [Fact]
    public void Standard_ist_das_gewaehlte_sonst_das_erste()
    {
        var before = Features.InReleaseOff.ToList();
        try
        {
            var a = new MailAccount { Id = "a" }; var b = new MailAccount { Id = "b" };
            var s = new UserSettings { MailAccounts = [a, b] };
            Features.Set([]);
            Assert.Same(a, s.DefaultAccount());
            Assert.True(s.IsDefault(a));
            s.DefaultMailAccount = "b";
            Assert.Same(b, s.DefaultAccount());
            Assert.Equal(["b", "a"], s.AccountsDefaultFirst().Select(x => x.Id));
            Assert.False(s.IsDefault(a));
            // Konto entfernt: wieder das erste
            s.DefaultMailAccount = "weg";
            Assert.Same(a, s.DefaultAccount());
            Assert.Null(new UserSettings().DefaultAccount());
            // Ohne Freischaltung: wie bisher das erste, keines als Standard markiert
            s.DefaultMailAccount = "b";
            Features.Set([Features.DefaultAccount]);
            Assert.Same(a, s.DefaultAccount());
            Assert.False(s.IsDefault(a));
            Assert.False(s.IsDefault(b));
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public async Task Standard_ueber_die_API_festlegen()
    {
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var space = _fx.Factory.Services.GetRequiredService<Spaces>().Get(users.ByLogin(FilesServerFixture.Login)!.Id);
        string id1 = "std1-" + Guid.NewGuid().ToString("N")[..6], id2 = "std2-" + Guid.NewGuid().ToString("N")[..6];
        space.UpdateSettings(s =>
        {
            s.MailAccounts.Add(new MailAccount { Id = id1, Name = "Eins", Email = "eins@example.org" });
            s.MailAccounts.Add(new MailAccount { Id = id2, Name = "Zwei", Email = "zwei@example.org" });
        });
        var c = _fx.Client(signedIn: false, cookies: true);
        var login = await c.PostAsJsonAsync("/api/auth/login", new { login = FilesServerFixture.Login, password = "geheim-123456" });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set([]);
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsync($"/api/settings/accounts/{id2}/default", JsonContent.Create(new { }))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsync("/api/settings/accounts/gibtsnicht/default", JsonContent.Create(new { }))).StatusCode);
            var list = await c.GetFromJsonAsync<List<Row>>("/api/mail/accounts");
            Assert.Equal([id2], list!.Where(r => r.IsDefault).Select(r => r.Id));
            var settings = await c.GetFromJsonAsync<Settings>("/api/settings/");
            Assert.Equal([id2], settings!.Accounts.Where(r => r.IsDefault).Select(r => r.Id));

            Features.Set([Features.DefaultAccount]);
            Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsync($"/api/settings/accounts/{id1}/default", JsonContent.Create(new { }))).StatusCode);
            list = await c.GetFromJsonAsync<List<Row>>("/api/mail/accounts");
            Assert.DoesNotContain(list!, r => r.IsDefault);
        }
        finally
        {
            Features.Set(before);
            space.UpdateSettings(s => { s.MailAccounts.RemoveAll(a => a.Id == id1 || a.Id == id2); s.DefaultMailAccount = null; });
        }
    }

    private sealed record Row(string Id, bool IsDefault);
    private sealed record Settings(List<Row> Accounts);
}
