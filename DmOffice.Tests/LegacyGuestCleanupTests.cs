using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Einmalige Aufräumung beim Start: Gastkonten früherer Versionen (guests.json, Schlüssel in chat.db) verschwinden.</summary>
[Collection("server")]
public class LegacyGuestCleanupTests
{
    private const string Password = "geheim-123456";

    private static DirectoryInfo NewData()
    {
        var data = Directory.CreateTempSubdirectory("dmlegacy");
        Environment.SetEnvironmentVariable("DMOFFICE_DATA", data.FullName);
        return data;
    }

    private static HttpClient As(WebApplicationFactory<Program> factory, string login, string appPassword)
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{appPassword}")));
        return c;
    }

    private static void WriteGuests(DirectoryInfo data, params string[] ids) =>
        File.WriteAllText(Path.Combine(data.FullName, "guests.json"), JsonSerializer.Serialize(ids.Select(id => new { Id = id, Login = "gast-" + id[..6], DisplayName = "Gast", Guest = new { Level = "chat", Pending = true } })));

    private static void Plant(DataPaths paths, string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.UserDb(id))!);
        File.WriteAllBytes(paths.UserDb(id), [1, 2, 3]);
        Directory.CreateDirectory(paths.UserBlobs(id));
        File.WriteAllBytes(Path.Combine(paths.UserBlobs(id), "x"), [1]);
    }

    [Fact]
    public async Task Gast_weg_Benutzer_bleiben_Raumliste_und_Export_funktionieren()
    {
        var data = NewData();
        var guest = Guid.NewGuid().ToString("N");
        string annaId, appPassword;
        long direct, group, message;
        DataPaths paths;
        using (var first = new WebApplicationFactory<Program>())
        {
            var users = first.Services.GetRequiredService<UserStore>();
            var chat = first.Services.GetRequiredService<ChatStore>();
            paths = first.Services.GetRequiredService<DataPaths>();
            var anna = users.Create("anna", "Anna", Password, isAdmin: true);
            var ben = users.Create("ben", "Ben", Password, isAdmin: false);
            annaId = anna.Id;
            appPassword = AppPasswords.Create(first.Services.GetRequiredService<Spaces>().Get(anna.Id).Db, "Test");
            direct = chat.OpenDirect(guest, anna.Id);
            message = chat.Post(guest, direct, "Hallo", actions: [new ChatAction("guest-yes", "Ja"), new ChatAction("guest-no", "Nein")]);
            group = chat.CreateGroup(anna.Id, "Familie", [guest, ben.Id]);
            chat.Post(guest, group, "Hallo Gruppe");
            chat.SetKv("guests.settings", "{}");
            chat.SetKv("guests.invite.abc", "{}");
            chat.SetKv("pukki.bleibt", "1");
            Plant(paths, guest);
        }
        // Ein Eintrag mit der Kennung eines Benutzers zählt nicht als Gast
        WriteGuests(data, guest, annaId);

        using var second = new WebApplicationFactory<Program>();
        var store = second.Services.GetRequiredService<ChatStore>();
        var after = second.Services.GetRequiredService<UserStore>();
        Assert.False(File.Exists(Path.Combine(data.FullName, "guests.json")));
        Assert.False(File.Exists(paths.UserDb(guest)));
        Assert.False(Directory.Exists(paths.UserBlobs(guest)));
        Assert.Null(after.ById(guest));
        Assert.NotNull(after.ById(annaId));
        Assert.Empty(store.KvLike("guests."));
        Assert.Equal("1", store.GetKv("pukki.bleibt"));
        Assert.DoesNotContain(guest, store.MembersOf(direct));
        Assert.DoesNotContain(guest, store.MembersOf(group));

        var c = As(second, "anna", appPassword);
        var rooms = await c.GetAsync("/api/chat/rooms");
        Assert.Equal(HttpStatusCode.OK, rooms.StatusCode);
        var ids = JsonDocument.Parse(await rooms.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToList();
        Assert.Contains(direct, ids);
        Assert.Contains(group, ids);
        foreach (var room in new[] { direct, group })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/chat/rooms/{room}/export")).StatusCode);

        // Die alten Knöpfe sind fort, die Nachricht bleibt
        var old = store.Message(annaId, message);
        Assert.Equal("Hallo", old.Text);
        Assert.Empty(old.Actions ?? []);
        // Sollte irgendwo noch einer stehen: Klick ohne Wirkung, kein Serverfehler
        var left = store.Post(annaId, direct, "Noch einer", actions: [new ChatAction("guest-yes", "Ja")]);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/chat/messages/{left}/actions/guest-yes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/chat/rooms")).StatusCode);

        // Alte Wege führen ins Leere, wie jeder andere unbekannte Weg
        var anon = second.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        Assert.Equal((await anon.GetAsync("/api/unbekannt/x")).StatusCode, (await anon.GetAsync("/api/guests/invite/x")).StatusCode);
        Assert.Equal((await c.GetAsync("/api/unbekannt/x")).StatusCode, (await c.GetAsync("/api/guests/mine")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, (await anon.GetAsync("/i/abcdefghijklmnopqrstuvwxyz")).StatusCode);
    }

    [Fact]
    public async Task Ohne_Datei_passiert_nichts_und_kaputte_Datei_haelt_den_Start_nicht_auf()
    {
        var data = NewData();
        using (var first = new WebApplicationFactory<Program>())
            first.Services.GetRequiredService<ChatStore>().SetKv("guests.settings", "{}");
        File.WriteAllText(Path.Combine(data.FullName, "guests.json"), "{ kaputt");
        using (var second = new WebApplicationFactory<Program>())
        {
            Assert.Equal(HttpStatusCode.OK, (await second.CreateClient().GetAsync("/api/health")).StatusCode);
            Assert.True(File.Exists(Path.Combine(data.FullName, "guests.json")));
            Assert.Equal("{}", second.Services.GetRequiredService<ChatStore>().GetKv("guests.settings"));
        }
    }

    [Fact]
    public void Scheitert_ein_Gast_bleibt_die_Datei_und_der_naechste_Start_holt_es_nach()
    {
        // Eine offene Datei laesst sich nur unter Windows nicht loeschen
        if (!OperatingSystem.IsWindows()) return;
        var data = NewData();
        var stuck = Guid.NewGuid().ToString("N");
        var other = Guid.NewGuid().ToString("N");
        DataPaths paths;
        using (var first = new WebApplicationFactory<Program>())
        {
            paths = first.Services.GetRequiredService<DataPaths>();
            first.Services.GetRequiredService<ChatStore>().SetKv("guests.settings", "{}");
            Plant(paths, stuck);
            Plant(paths, other);
        }
        WriteGuests(data, stuck, other);
        var file = Path.Combine(data.FullName, "guests.json");
        using (File.Open(paths.UserDb(stuck), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var second = new WebApplicationFactory<Program>())
        {
            Assert.NotNull(second.Services.GetRequiredService<UserStore>());
            Assert.True(File.Exists(file));
            Assert.False(File.Exists(paths.UserDb(other)));
            Assert.Equal("{}", second.Services.GetRequiredService<ChatStore>().GetKv("guests.settings"));
        }
        using var third = new WebApplicationFactory<Program>();
        Assert.Empty(third.Services.GetRequiredService<ChatStore>().KvLike("guests."));
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(paths.UserDb(stuck)));
    }
}
