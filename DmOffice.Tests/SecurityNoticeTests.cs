using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Tresor Stufe 1: Ändert die Verwaltung die Sicherheit eines Kontos, erfährt die Person es (Pukki) und es steht im Protokoll.</summary>
[Collection("server")]
public class SecurityNoticeTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public SecurityNoticeTests(ChatFixture fx) => _fx = fx;

    private async Task<HttpClient> Login(string login, string password = "geheim-123456")
    {
        var c = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        (await c.PostAsJsonAsync("/api/auth/login", new { login, password })).EnsureSuccessStatusCode();
        return c;
    }

    private async Task<List<JsonElement>> Log(string login) =>
        (await (await _fx.As(login).GetAsync("/api/settings/security-log")).Content.ReadFromJsonAsync<List<JsonElement>>())!;

    [Fact]
    public async Task Verwaltung_setzt_Passwort_Person_bekommt_Nachricht_und_Protokoll()
    {
        var admin = await Login("anna");
        (await admin.PutAsJsonAsync($"/api/admin/users/{_fx.Id("cleo")}", new { password = "neu-geheim-123" })).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/admin/users/{_fx.Id("cleo")}/reset-2fa", null)).EnsureSuccessStatusCode();

        var log = await Log("cleo");
        Assert.Equal("Zwei-Faktor-Anmeldung abgeschaltet", log[0].GetProperty("what").GetString());
        Assert.Equal("Passwort neu gesetzt", log[1].GetProperty("what").GetString());
        Assert.Equal("Anna", log[1].GetProperty("by").GetString());

        var chat = _fx.Factory.Services.GetRequiredService<ChatStore>();
        var room = Pukki.RoomOf(chat, _fx.Id("cleo"))!.Value;
        Assert.Contains(chat.Messages(_fx.Id("cleo"), room, limit: 5), m => m.Text?.Contains("Passwort neu gesetzt") == true && m.Text.Contains("Anna"));

        // Eigene Änderung: nur ins Protokoll, ohne Verwaltung
        var cleo = await Login("cleo", "neu-geheim-123");
        (await cleo.PostAsJsonAsync("/api/auth/password", new { old = "neu-geheim-123", @new = "geheim-123456" })).EnsureSuccessStatusCode();
        log = await Log("cleo");
        Assert.Equal("Passwort geändert", log[0].GetProperty("what").GetString());
        Assert.Equal(JsonValueKind.Null, log[0].GetProperty("by").ValueKind);
    }

    [Fact]
    public async Task Verwaltungsrecht_und_eigenes_Konto()
    {
        var admin = await Login("anna");
        (await admin.PutAsJsonAsync($"/api/admin/users/{_fx.Id("dora")}", new { isAdmin = true })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/admin/users/{_fx.Id("dora")}", new { isAdmin = false })).EnsureSuccessStatusCode();
        var log = await Log("dora");
        Assert.Equal("Verwaltungsrecht entzogen", log[0].GetProperty("what").GetString());
        Assert.Equal("Verwaltungsrecht erteilt", log[1].GetProperty("what").GetString());
        // Nichts geändert (Name): kein Eintrag
        (await admin.PutAsJsonAsync($"/api/admin/users/{_fx.Id("dora")}", new { displayName = "Dora" })).EnsureSuccessStatusCode();
        Assert.Equal(2, (await Log("dora")).Count);
    }
}
