using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Hauptschalter der Module: der Admin schaltet Module serverweit aus (API 404, featuresOff) und wieder an.</summary>
[Collection("server")]
public class ModulesTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ModulesTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Hauptschalter_Modul_aus_404_und_in_featuresOff_wieder_an_erreichbar()
    {
        // Verwaltung nur mit Anmeldung im Browser (Cookie), nicht mit App-Passwort
        var c = _fx.Factory.CreateClient(new() { HandleCookies = true });
        Assert.True((await c.PostAsJsonAsync("/api/auth/login", new { login = "anna", password = "geheim-123456" })).IsSuccessStatusCode);
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var before = Features.InReleaseOff.ToList();
        var wasOff = config.Current.ModulesOff;
        try
        {
            Features.Set([]);
            config.Update(s => s.ModulesOff = []);
            Assert.NotEqual(HttpStatusCode.NotFound, (await c.GetAsync("/api/journal/journals")).StatusCode);

            // Ausschalten über die Verwaltung
            var put = await c.PutAsJsonAsync("/api/admin/modules", new { id = "journal", on = false });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.False(Features.On(Features.Journal));
            Assert.Equal(new[] { "journal" }, config.Current.ModulesOff);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/journal/journals")).StatusCode);
            Assert.Contains("\"featuresOff\":[\"journal\"]", await c.GetStringAsync("/api/auth/state"));
            var list = await c.GetFromJsonAsync<JsonElement>("/api/admin/modules");
            var journal = list.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "journal");
            Assert.False(journal.GetProperty("on").GetBoolean());
            Assert.True(journal.GetProperty("released").GetBoolean());
            Assert.Equal(Features.Modules.Length, list.GetArrayLength());

            // Nur die Verwaltung schaltet
            Assert.NotEqual(HttpStatusCode.OK, (await _fx.As("cleo").PutAsJsonAsync("/api/admin/modules", new { id = "journal", on = true })).StatusCode);
            Assert.False(Features.On(Features.Journal));

            // Unbekanntes Modul und Grundausstattung: nicht schaltbar
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/admin/modules", new { id = "mail", on = false })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/admin/modules", new { id = "notes", on = false })).StatusCode);

            // Wieder an
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/admin/modules", new { id = "journal", on = true })).StatusCode);
            Assert.True(Features.On(Features.Journal));
            Assert.NotEqual(HttpStatusCode.NotFound, (await c.GetAsync("/api/journal/journals")).StatusCode);
            Assert.DoesNotContain("journal", await c.GetStringAsync("/api/auth/state"));

            // Früher gespeicherter Schalter für die Grundausstattung zählt nicht mehr
            config.Update(s => s.ModulesOff = ["notes"]);
            Assert.True(Features.On(Features.Notes));
            Assert.NotEqual(HttpStatusCode.NotFound, (await c.GetAsync("/api/notes/folders")).StatusCode);
        }
        finally { Features.Set(before); config.Update(s => s.ModulesOff = wasOff); }
    }

    [Fact]
    public void Audiothek_Schalter_wird_einmalig_uebernommen()
    {
        static ServerSettings Load(string? json)
        {
            var dir = Directory.CreateTempSubdirectory("dmmodules").FullName;
            try
            {
                if (json != null) File.WriteAllText(Path.Combine(dir, "server.json"), json);
                return new ServerConfig(new DataPaths(dir)).Current;
            }
            finally { Directory.Delete(dir, true); }
        }
        // Neu und bisher aus: Audiothek bleibt aus
        Assert.Equal(new[] { "audio" }, Load(null).ModulesOff);
        Assert.Equal(new[] { "audio" }, Load("{\"Audio\": false}").ModulesOff);
        // Bisher an: alle Module an
        Assert.Empty(Load("{\"Audio\": true}").ModulesOff!);
        // Schon übernommen: der alte Schalter zählt nicht mehr
        Assert.Empty(Load("{\"Audio\": false, \"ModulesOff\": []}").ModulesOff!);
        Assert.Equal(new[] { "gallery" }, Load("{\"Audio\": true, \"ModulesOff\": [\"gallery\"]}").ModulesOff);
    }
}
