using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Verwaltung → Benutzer sperren (Vorstufe zum Löschen): keine Anmeldung, Sitzungen und App-Passwörter enden,
/// Hintergrundarbeit ruht, die Daten bleiben; entsperren.</summary>
[Collection("server")]
public class UserLockTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public UserLockTests(ChatFixture fx) => _fx = fx;

    private HttpClient Browser() =>
        _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });

    private static Task<HttpResponseMessage> LoginAs(HttpClient c, string login) =>
        c.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" });

    private static List<string> On(List<string> off) => off.Where(x => x != Features.UserLock).ToList();

    [Fact]
    public async Task Sperren_beendet_Zugang_Daten_bleiben_Entsperren()
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(On(before));
        try
        {
            var admin = Browser();
            (await LoginAs(admin, "anna")).EnsureSuccessStatusCode();
            var person = Browser();
            (await LoginAs(person, "ben")).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, (await person.GetAsync("/api/settings/security-log")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("ben").GetAsync("/api/pim/contacts")).StatusCode);

            // Das eigene Konto nicht
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/admin/users/{_fx.Id("anna")}/lock", null)).StatusCode);

            (await admin.PostAsync($"/api/admin/users/{_fx.Id("ben")}/lock", null)).EnsureSuccessStatusCode();

            // Offene Sitzung fällt heraus, neue Anmeldung abgewiesen (mit Hinweis), App-Passwort abgewiesen
            Assert.Equal(HttpStatusCode.Unauthorized, (await person.GetAsync("/api/settings/security-log")).StatusCode);
            var again = await LoginAs(Browser(), "ben");
            Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
            Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locked").GetBoolean());
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.As("ben").GetAsync("/api/pim/contacts")).StatusCode);
            // Falsches Passwort: dieselbe Antwort wie sonst (verrät nicht, dass das Konto gesperrt ist)
            var wrong = await Browser().PostAsJsonAsync("/api/auth/login", new { login = "ben", password = "falsch-falsch" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

            // Daten bleiben, Hintergrundarbeit ruht
            var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
            Assert.Contains(spaces.All(), s => s.User.Id == _fx.Id("ben"));
            Assert.DoesNotContain(spaces.Working(), s => s.User.Id == _fx.Id("ben"));
            Assert.Empty(AppPasswords.List(_fx.Space("ben").Db));

            // Verwaltung sieht die Sperre
            var list = await admin.GetFromJsonAsync<List<JsonElement>>("/api/admin/users");
            Assert.True(list!.Single(u => u.GetProperty("login").GetString() == "ben").GetProperty("locked").GetBoolean());
            Assert.False(list!.Single(u => u.GetProperty("login").GetString() == "cleo").GetProperty("locked").GetBoolean());

            // Entsperren: Anmeldung geht wieder, App-Passwörter bleiben gelöscht
            (await admin.PostAsync($"/api/admin/users/{_fx.Id("ben")}/unlock", null)).EnsureSuccessStatusCode();
            var back = Browser();
            (await LoginAs(back, "ben")).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/api/settings/security-log")).StatusCode);
            Assert.Contains(spaces.Working(), s => s.User.Id == _fx.Id("ben"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.As("ben").GetAsync("/api/pim/contacts")).StatusCode);
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public async Task Ohne_Freischaltung_nicht_da_und_ohne_Wirkung()
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Append(Features.UserLock));
        try
        {
            var admin = Browser();
            (await LoginAs(admin, "anna")).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/admin/users/{_fx.Id("cleo")}/lock", null)).StatusCode);
            // Ein gespeichertes Kennzeichen wirkt ohne Freischaltung nicht (Verhalten wie 2.1)
            _fx.Factory.Services.GetRequiredService<UserStore>().Update(_fx.Id("cleo"), u => u.Locked = true);
            try
            {
                (await LoginAs(Browser(), "cleo")).EnsureSuccessStatusCode();
                var list = await admin.GetFromJsonAsync<List<JsonElement>>("/api/admin/users");
                Assert.False(list!.Single(u => u.GetProperty("login").GetString() == "cleo").GetProperty("locked").GetBoolean());
            }
            finally { _fx.Factory.Services.GetRequiredService<UserStore>().Update(_fx.Id("cleo"), u => u.Locked = false); }
        }
        finally { Features.Set(before); }
    }
}
