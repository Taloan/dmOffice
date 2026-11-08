using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Notes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Ein Server mit vier Benutzern fuer den Chat: Anna, Ben, Cleo und Dora.</summary>
public sealed class ChatFixture : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; }
    public Dictionary<string, (string Id, string AppPassword)> Users { get; } = new();
    private readonly DirectoryInfo _data;

    public ChatFixture()
    {
        _data = Directory.CreateTempSubdirectory("dmchat");
        Environment.SetEnvironmentVariable("DMOFFICE_DATA", _data.FullName);
        Factory = new WebApplicationFactory<Program>();
        var users = Factory.Services.GetRequiredService<UserStore>();
        var spaces = Factory.Services.GetRequiredService<Spaces>();
        foreach (var (login, name) in new[] { ("anna", "Anna"), ("ben", "Ben"), ("cleo", "Cleo"), ("dora", "Dora") })
        {
            var u = users.Create(login, name, "geheim-123456", isAdmin: login == "anna");
            Users[login] = (u.Id, AppPasswords.Create(spaces.Get(u.Id).Db, "Test"));
        }
    }

    public string Id(string login) => Users[login].Id;

    public UserSpace Space(string login) => Factory.Services.GetRequiredService<Spaces>().Get(Id(login));

    public HttpClient As(string login)
    {
        var c = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{Users[login].AppPassword}")));
        return c;
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { _data.Delete(true); } catch (IOException) { /* Datenbank noch offen */ }
    }
}

/// <summary>Chat ueber die Web-API: Unterhaltungen, Gruppen, Lesestand, Warten auf Neues und Verweise auf Dateien und Notizen.</summary>
[Collection("server")]
public class ChatTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChatTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<long> Direct(string from, string to) =>
        (await Json(await _fx.As(from).PostAsJsonAsync("/api/chat/rooms", new { members = new[] { _fx.Id(to) } }))).GetProperty("id").GetInt64();

    private async Task<long> Group(string from, string title, params string[] members) =>
        (await Json(await _fx.As(from).PostAsJsonAsync("/api/chat/rooms", new { title, members = members.Select(_fx.Id).ToArray() }))).GetProperty("id").GetInt64();

    private async Task<long> Say(string from, long room, string text) =>
        (await Json(await _fx.As(from).PostAsJsonAsync($"/api/chat/rooms/{room}/messages", new { text }))).GetProperty("id").GetInt64();

    private async Task<List<JsonElement>> Messages(string login, long room) =>
        (await Json(await _fx.As(login).GetAsync($"/api/chat/rooms/{room}/messages"))).EnumerateArray().ToList();

    private async Task<int> Unread(string login) =>
        (await Json(await _fx.As(login).GetAsync("/api/chat/status"))).GetProperty("unread").GetInt32();

    [Fact]
    public async Task Zu_zweit_gibt_es_je_Paar_eine_Unterhaltung_mit_Lesestand()
    {
        var a = await Direct("anna", "ben");
        Assert.Equal(a, await Direct("ben", "anna"));

        var room = await Json(await _fx.As("ben").GetAsync($"/api/chat/rooms/{a}"));
        Assert.Equal("Anna", room.GetProperty("title").GetString());

        var before = await Unread("ben");
        var id = await Say("anna", a, "Hallo Ben");
        Assert.Equal(before + 1, await Unread("ben"));
        Assert.Contains(await Messages("ben", a), m => m.GetProperty("text").GetString() == "Hallo Ben" && m.GetProperty("senderName").GetString() == "Anna");

        (await _fx.As("ben").PostAsJsonAsync($"/api/chat/rooms/{a}/read", new { upTo = id })).EnsureSuccessStatusCode();
        Assert.Equal(before, await Unread("ben"));
        // Anna sieht, dass Ben gelesen hat
        var seen = await Json(await _fx.As("anna").GetAsync($"/api/chat/rooms/{a}"));
        Assert.Equal(id, seen.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("id").GetString() == _fx.Id("ben")).GetProperty("lastRead").GetInt64());

        // Wer nicht dabei ist, sieht nichts
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").GetAsync($"/api/chat/rooms/{a}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").PostAsJsonAsync($"/api/chat/rooms/{a}/messages", new { text = "x" })).StatusCode);
    }

    [Fact]
    public async Task Gruppe_neue_Mitglieder_sehen_nur_den_Verlauf_ab_ihrem_Beitritt()
    {
        var g = await Group("anna", "Familie", "ben");
        await Say("anna", g, "Vor Cleo");
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("cleo").GetAsync($"/api/chat/rooms/{g}/messages")).StatusCode);

        // Nur wer verwaltet, darf Leute hinzufuegen und umbenennen
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync($"/api/chat/rooms/{g}/members", new { users = new[] { _fx.Id("cleo") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PutAsJsonAsync($"/api/chat/rooms/{g}", new { title = "Meins" })).StatusCode);

        await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{g}/members", new { users = new[] { _fx.Id("cleo") } }));
        await Say("ben", g, "Nach Cleo");
        var seen = await Messages("cleo", g);
        Assert.DoesNotContain(seen, m => m.GetProperty("text").GetString() == "Vor Cleo");
        Assert.Contains(seen, m => m.GetProperty("text").GetString() == "Nach Cleo");
        Assert.Contains(seen, m => m.GetProperty("kind").GetString() == "system" && m.GetProperty("text").GetString()!.Contains("added"));

        // Anna geht: Ben verwaltet jetzt (am laengsten dabei)
        (await _fx.As("anna").PostAsync($"/api/chat/rooms/{g}/leave", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("anna").GetAsync($"/api/chat/rooms/{g}")).StatusCode);
        var room = await Json(await _fx.As("cleo").GetAsync($"/api/chat/rooms/{g}"));
        Assert.True(room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("id").GetString() == _fx.Id("ben")).GetProperty("admin").GetBoolean());
        (await _fx.As("ben").PutAsJsonAsync($"/api/chat/rooms/{g}", new { title = "Familie Neu" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Bearbeiten_Loeschen_und_Reagieren()
    {
        var a = await Direct("anna", "cleo");
        var id = await Say("anna", a, "Tippfeler");
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PutAsJsonAsync($"/api/chat/messages/{id}", new { text = "Fremd" })).StatusCode);
        var edited = await Json(await _fx.As("anna").PutAsJsonAsync($"/api/chat/messages/{id}", new { text = "Tippfehler" }));
        Assert.Equal("Tippfehler", edited.GetProperty("text").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("edited").ValueKind);

        var reacted = await Json(await _fx.As("cleo").PostAsJsonAsync($"/api/chat/messages/{id}/reactions", new { emoji = "👍" }));
        Assert.Equal(_fx.Id("cleo"), reacted.GetProperty("reactions")[0].GetProperty("users")[0].GetString());
        // Noch einmal: zurueckgenommen
        reacted = await Json(await _fx.As("cleo").PostAsJsonAsync($"/api/chat/messages/{id}/reactions", new { emoji = "👍" }));
        Assert.Equal(0, reacted.GetProperty("reactions").GetArrayLength());

        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").DeleteAsync($"/api/chat/messages/{id}")).StatusCode);
        (await _fx.As("anna").DeleteAsync($"/api/chat/messages/{id}")).EnsureSuccessStatusCode();
        var gone = (await Messages("cleo", a)).Single(m => m.GetProperty("id").GetInt64() == id);
        Assert.True(gone.GetProperty("deleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("text").ValueKind);
    }

    [Fact]
    public async Task Warten_kommt_mit_der_neuen_Nachricht_zurueck()
    {
        var a = await Direct("ben", "dora");
        var seq = (await Json(await _fx.As("dora").GetAsync("/api/chat/status"))).GetProperty("seq").GetInt64();
        var watch = Stopwatch.StartNew();
        var wait = _fx.As("dora").GetAsync($"/api/chat/wait?since={seq}&timeout=20");
        await Task.Delay(300);
        Assert.False(wait.IsCompleted);
        await Say("ben", a, "Bist du da?");
        var after = (await Json(await wait)).GetProperty("seq").GetInt64();
        Assert.True(after > seq);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));

        // Seit dem alten Stand: die Nachricht; seit dem neuen: nichts mehr
        var changes = await Json(await _fx.As("dora").GetAsync($"/api/chat/changes?since={seq}"));
        Assert.Contains(changes.GetProperty("messages").EnumerateArray(), m => m.GetProperty("text").GetString() == "Bist du da?");
        changes = await Json(await _fx.As("dora").GetAsync($"/api/chat/changes?since={after}"));
        Assert.Equal(0, changes.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Datei_als_Verweis_lesen_speichern_und_weg()
    {
        var a = await Direct("anna", "ben");
        var msg = await Json(await _fx.As("anna").PostAsync($"/api/chat/rooms/{a}/upload?name=einkauf.txt&text=Liste",
            new StringContent("Milch, Brot", Encoding.UTF8, "text/plain")));
        var id = msg.GetProperty("id").GetInt64();
        Assert.Equal("einkauf.txt", msg.GetProperty("refs")[0].GetProperty("name").GetString());
        // Liegt bei Anna in der Ablage unter Chat/
        Assert.NotNull(FileStore.Get(_fx.Space("anna").Db, "Chat/einkauf.txt"));

        Assert.Equal("Milch, Brot", await (await _fx.As("ben").GetAsync($"/api/chat/messages/{id}/refs/0/file")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("cleo").GetAsync($"/api/chat/messages/{id}/refs/0/file")).StatusCode);

        var saved = await Json(await _fx.As("ben").PostAsJsonAsync($"/api/chat/messages/{id}/refs/0/save", new { }));
        var path = saved.GetProperty("path").GetString()!;
        Assert.Equal("Milch, Brot", Encoding.UTF8.GetString(FileStore.Data(_fx.Space("ben").Db, path)!));

        // Anna loescht das Original: der Verweis ist weg, Bens Kopie bleibt
        FileStore.Delete(_fx.Space("anna").Db, "Chat/einkauf.txt");
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync($"/api/chat/messages/{id}/refs/0/file")).StatusCode);
        Assert.NotNull(FileStore.Get(_fx.Space("ben").Db, path));

        // Fremde Dateien lassen sich nicht anhaengen
        var bensFile = FileStore.Get(_fx.Space("ben").Db, path)!.FileId;
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{a}/messages",
            new { refs = new[] { new { type = "file", fileId = bensFile } } })).StatusCode);
    }

    [Fact]
    public async Task Dasselbe_Foto_an_zwei_Personen_liegt_nur_einmal_in_der_Ablage()
    {
        var toBen = await Direct("cleo", "ben");
        var toDora = await Direct("cleo", "dora");
        var bytes = Encoding.UTF8.GetBytes("gleiches-foto-" + Guid.NewGuid());
        var m1 = await Json(await _fx.As("cleo").PostAsync($"/api/chat/rooms/{toBen}/upload?name=foto.jpg", new ByteArrayContent(bytes)));
        var m2 = await Json(await _fx.As("cleo").PostAsync($"/api/chat/rooms/{toDora}/upload?name=foto.jpg", new ByteArrayContent(bytes)));
        Assert.Null(FileStore.Get(_fx.Space("cleo").Db, "Chat/foto (2).jpg"));
        Assert.NotNull(FileStore.Get(_fx.Space("cleo").Db, "Chat/foto.jpg"));
        Assert.Equal(Encoding.UTF8.GetString(bytes), await (await _fx.As("dora").GetAsync($"/api/chat/messages/{m2.GetProperty("id").GetInt64()}/refs/0/file")).Content.ReadAsStringAsync());

        // Die Dateiablage weiss, wo die Datei verschickt wurde
        var fileId = FileStore.Get(_fx.Space("cleo").Db, "Chat/foto.jpg")!.FileId;
        var shared = (await Json(await _fx.As("cleo").GetAsync($"/api/chat/shared?fileId={fileId}"))).EnumerateArray().ToList();
        Assert.Equal(2, shared.Count);
        Assert.Contains(shared, x => x.GetProperty("title").GetString() == "Ben");
        Assert.Contains(shared, x => x.GetProperty("title").GetString() == "Dora");
        // Fremde sehen nichts davon
        Assert.Empty((await Json(await _fx.As("ben").GetAsync($"/api/chat/shared?fileId={fileId}"))).EnumerateArray());

        // Speichern in einen gewaehlten Ordner
        var saved = await Json(await _fx.As("ben").PostAsJsonAsync($"/api/chat/messages/{m1.GetProperty("id").GetInt64()}/refs/0/save", new { folder = "Fotos/Von Cleo" }));
        Assert.Equal("Fotos/Von Cleo/foto.jpg", saved.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Notiz_als_Verweis_lesen_und_als_eigene_Notiz_speichern()
    {
        var notes = NotesDb.Of(_fx.Space("cleo"));
        var noteId = notes.CreateNote(null);
        notes.SaveNote(noteId, "<div data-notizi=\"1\"><p>Rezept Pfannkuchen</p><p>Mehl, Milch, Eier</p></div>", "Rezept Pfannkuchen\nMehl, Milch, Eier");
        var a = await Direct("cleo", "dora");
        var msg = await Json(await _fx.As("cleo").PostAsJsonAsync($"/api/chat/rooms/{a}/messages",
            new { text = "Für Sonntag", refs = new[] { new { type = "note", noteId, mode = "send" } } }));
        var id = msg.GetProperty("id").GetInt64();
        Assert.Equal("note", msg.GetProperty("refs")[0].GetProperty("type").GetString());

        var note = await Json(await _fx.As("dora").GetAsync($"/api/chat/messages/{id}/refs/0/note"));
        Assert.Contains("Mehl, Milch, Eier", note.GetProperty("html").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync($"/api/chat/messages/{id}/refs/0/note")).StatusCode);

        var saved = await Json(await _fx.As("dora").PostAsJsonAsync($"/api/chat/messages/{id}/refs/0/save", new { }));
        var copy = NotesDb.Of(_fx.Space("dora")).GetNote(saved.GetProperty("noteId").GetInt64())!;
        // Ohne Wahl im Ordner „Chat“ (wird angelegt)
        Assert.Equal("Chat", NotesDb.Of(_fx.Space("dora")).GetFolder(copy.Folder!)!.Name);
        Assert.Contains("Mehl, Milch, Eier", copy.Plain);
        Assert.NotEqual(notes.GetNote(noteId)!.SyncId, copy.SyncId);
    }

    private async Task<long> UploadAs(string from, long room, string name, string? mode = null)
    {
        var url = $"/api/chat/rooms/{room}/upload?name={name}" + (mode == null ? "" : $"&mode={mode}");
        return (await Json(await _fx.As(from).PostAsync(url, new StringContent(name + Guid.NewGuid(), Encoding.UTF8, "text/plain")))).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Zeigen_laesst_sich_nicht_speichern_Senden_schon_und_die_Art_laesst_sich_umstellen()
    {
        var a = await Direct("ben", "cleo");
        var shown = await UploadAs("ben", a, "geheim.txt", "show");
        var m = (await Messages("cleo", a)).Single(x => x.GetProperty("id").GetInt64() == shown);
        Assert.Equal("show", m.GetProperty("refs")[0].GetProperty("mode").GetString());
        // Ansehen ja, speichern nein
        (await _fx.As("cleo").GetAsync($"/api/chat/messages/{shown}/refs/0/file?inline=true")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PostAsJsonAsync($"/api/chat/messages/{shown}/refs/0/save", new { })).StatusCode);

        // Nur Ben kann die Art umstellen - danach darf Cleo speichern
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PutAsJsonAsync($"/api/chat/messages/{shown}/refs/0", new { mode = "send" })).StatusCode);
        await Json(await _fx.As("ben").PutAsJsonAsync($"/api/chat/messages/{shown}/refs/0", new { mode = "send" }));
        (await _fx.As("cleo").PostAsJsonAsync($"/api/chat/messages/{shown}/refs/0/save", new { })).EnsureSuccessStatusCode();

        // Zurueckziehen: Cleo kommt nicht mehr dran, Ben sieht es weiter
        var after = await Json(await _fx.As("ben").PutAsJsonAsync($"/api/chat/messages/{shown}/refs/0", new { revoke = true }));
        Assert.Equal("revoked", after.GetProperty("refs")[0].GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("cleo").GetAsync($"/api/chat/messages/{shown}/refs/0/file")).StatusCode);
        (await _fx.As("ben").GetAsync($"/api/chat/messages/{shown}/refs/0/file")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Weiterleiten_Gesendetes_frei_Gezeigtes_nur_mit_Erlaubnis_und_Zurueckziehen_wirkt_auf_Weiterleitungen()
    {
        var toCleo = await Direct("anna", "cleo");
        var cleoDora = await Direct("cleo", "dora");
        var sent = await UploadAs("anna", toCleo, "rezept.txt", "send");
        var shown = await UploadAs("anna", toCleo, "urlaub.txt", "show");

        // Gesendetes: Cleo leitet als Zeigen an Dora weiter
        var fw = (await Json(await _fx.As("cleo").PostAsJsonAsync($"/api/chat/messages/{sent}/forward", new { rooms = new[] { cleoDora }, mode = "show" }))).EnumerateArray().Single();
        var fwId = fw.GetProperty("id").GetInt64();
        Assert.True(fw.GetProperty("refs")[0].GetProperty("forwarded").GetBoolean());
        (await _fx.As("dora").GetAsync($"/api/chat/messages/{fwId}/refs/0/file?inline=true")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("dora").PostAsJsonAsync($"/api/chat/messages/{fwId}/refs/0/save", new { })).StatusCode);

        // Gezeigtes: ohne Erlaubnis kein Weiterzeigen, mit Erlaubnis schon
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PostAsJsonAsync($"/api/chat/messages/{shown}/forward", new { rooms = new[] { cleoDora } })).StatusCode);
        var annaSettings = (await Json(await _fx.As("anna").GetAsync("/api/chat/settings"))).GetProperty("settings");
        var changed = JsonSerializer.Deserialize<Dictionary<string, object?>>(annaSettings.GetRawText())!;
        changed["reshow"] = true;
        (await _fx.As("anna").PutAsJsonAsync("/api/chat/settings", changed)).EnsureSuccessStatusCode();
        (await _fx.As("cleo").PostAsJsonAsync($"/api/chat/messages/{shown}/forward", new { rooms = new[] { cleoDora } })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PostAsJsonAsync($"/api/chat/messages/{shown}/forward", new { rooms = new[] { cleoDora }, mode = "send" })).StatusCode);

        // Anna zieht das Original zurueck: auch Doras Weiterleitung ist weg
        await Json(await _fx.As("anna").PutAsJsonAsync($"/api/chat/messages/{sent}/refs/0", new { revoke = true }));
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").GetAsync($"/api/chat/messages/{fwId}/refs/0/file")).StatusCode);
        var doraSees = (await Messages("dora", cleoDora)).Single(x => x.GetProperty("id").GetInt64() == fwId);
        Assert.Equal("revoked", doraSees.GetProperty("refs")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Sichtbarkeitsdauer_und_Rechte_der_Verwaltung()
    {
        var a = await Direct("dora", "ben");
        var dora = (await Json(await _fx.As("dora").GetAsync("/api/chat/settings"))).GetProperty("settings");
        var s = JsonSerializer.Deserialize<Dictionary<string, object?>>(dora.GetRawText())!;
        s["showFor"] = "24h";
        (await _fx.As("dora").PutAsJsonAsync("/api/chat/settings", s)).EnsureSuccessStatusCode();
        var id = await UploadAs("dora", a, "kurz.txt", "show");
        var m = (await Messages("ben", a)).Single(x => x.GetProperty("id").GetInt64() == id);
        Assert.NotEqual(JsonValueKind.Null, m.GetProperty("refs")[0].GetProperty("expires").ValueKind);

        // Die Verwaltung (Anna ist Admin) verbietet Dora ablaufende Inhalte: Gezeigtes ist dann dauerhaft
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PutAsJsonAsync($"/api/chat/admin/rights/{_fx.Id("dora")}", new { expiring = false, locationRequests = false })).StatusCode);
        // Admin-Aufrufe nur mit Browser-Anmeldung - ein App-Passwort hat nie Admin-Rechte, auch nicht Annas
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("anna").PutAsJsonAsync($"/api/chat/admin/rights/{_fx.Id("dora")}", new { expiring = false, locationRequests = true })).StatusCode);
        _fx.Factory.Services.GetRequiredService<ChatStore>().SetRights(new ChatRights(_fx.Id("dora"), Expiring: false, LocationRequests: true));
        var id2 = await UploadAs("dora", a, "dauerhaft.txt", "show");
        var m2 = (await Messages("ben", a)).Single(x => x.GetProperty("id").GetInt64() == id2);
        Assert.Equal(JsonValueKind.Null, m2.GetProperty("refs")[0].GetProperty("expires").ValueKind);
        var mine = await Json(await _fx.As("dora").GetAsync("/api/chat/settings"));
        Assert.False(mine.GetProperty("rights").GetProperty("expiring").GetBoolean());
        Assert.True(mine.GetProperty("rights").GetProperty("locationRequests").GetBoolean());
        // Speichern setzt die Dauer dann auf dauerhaft
        (await _fx.As("dora").PutAsJsonAsync("/api/chat/settings", s)).EnsureSuccessStatusCode();
        Assert.Equal("forever", (await Json(await _fx.As("dora").GetAsync("/api/chat/settings"))).GetProperty("settings").GetProperty("showFor").GetString());
    }

    [Fact]
    public async Task Farbe_je_Unterhaltung_ist_persoenlich()
    {
        var a = await Direct("ben", "dora");
        var r = await Json(await _fx.As("ben").PutAsJsonAsync($"/api/chat/rooms/{a}", new { color = "#13A10E" }));
        Assert.Equal("#13A10E", r.GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Null, (await Json(await _fx.As("dora").GetAsync($"/api/chat/rooms/{a}"))).GetProperty("color").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("ben").PutAsJsonAsync($"/api/chat/rooms/{a}", new { color = "rot" })).StatusCode);
        r = await Json(await _fx.As("ben").PutAsJsonAsync($"/api/chat/rooms/{a}", new { color = "" }));
        Assert.Equal(JsonValueKind.Null, r.GetProperty("color").ValueKind);
    }

    [Fact]
    public async Task Termin_und_Kontakt_zeigen_und_senden_Kopie_bekommt_neue_UID()
    {
        var anna = _fx.Space("anna").Db;
        DmOffice.Pim.PimStore.EnsureDefaults(anna);
        var cal = DmOffice.Pim.PimStore.Collections(anna, DmOffice.Pim.PimStore.Calendar).First(c => c.Components.Contains("VEVENT"));
        DmOffice.Pim.PimStore.Put(anna, cal, "ausflug.ics",
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//DE\r\nBEGIN:VEVENT\r\nUID:ausflug-1\r\nDTSTART:20271010T080000Z\r\nDTEND:20271010T170000Z\r\nSUMMARY:Ausflug Zoo\r\nLOCATION:Tiergarten\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        var book = DmOffice.Pim.PimStore.Collections(anna, DmOffice.Pim.PimStore.AddressBook).First();
        DmOffice.Pim.PimStore.Put(anna, book, "oma.vcf", "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:oma-1\r\nFN:Oma Erna\r\nN:Erna;Oma;;;\r\nTEL;TYPE=CELL:+49 170 1234\r\nEND:VCARD\r\n");

        var a = await Direct("anna", "dora");
        var msg = await Json(await _fx.As("anna").PostAsJsonAsync($"/api/chat/rooms/{a}/messages", new
        {
            text = "Sonntag?",
            refs = new object[] { new { type = "event", collection = cal.Id, item = "ausflug.ics", mode = "send" }, new { type = "contact", collection = book.Id, item = "oma.vcf", mode = "show" } },
        }));
        var id = msg.GetProperty("id").GetInt64();
        Assert.Equal("Ausflug Zoo", msg.GetProperty("refs")[0].GetProperty("name").GetString());
        Assert.Equal("Oma Erna", msg.GetProperty("refs")[1].GetProperty("name").GetString());

        var ev = await Json(await _fx.As("dora").GetAsync($"/api/chat/messages/{id}/refs/0/item"));
        Assert.Equal("Tiergarten", ev.GetProperty("location").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync($"/api/chat/messages/{id}/refs/0/item")).StatusCode);

        // Senden: Dora uebernimmt den Termin in seinen Kalender - eigene Kopie mit neuer UID
        var saved = await Json(await _fx.As("dora").PostAsJsonAsync($"/api/chat/messages/{id}/refs/0/save", new { }));
        var ben = _fx.Space("dora").Db;
        var copy = DmOffice.Pim.PimStore.Object(ben, saved.GetProperty("collection").GetInt64(), saved.GetProperty("item").GetString()!)!;
        Assert.Contains("Ausflug Zoo", copy.Data);
        Assert.DoesNotContain("ausflug-1", copy.Data);

        // Zeigen: Kontakt ansehen ja, uebernehmen nein
        var c = await Json(await _fx.As("dora").GetAsync($"/api/chat/messages/{id}/refs/1/item"));
        Assert.Equal("Oma Erna", c.GetProperty("contact").GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("dora").PostAsJsonAsync($"/api/chat/messages/{id}/refs/1/save", new { })).StatusCode);
    }

    [Fact]
    public async Task Ordner_zeigen_und_senden_einzelne_Dateien_speichern()
    {
        var cleo = _fx.Space("cleo").Db;
        FileStore.EnsureDir(cleo, "Urlaub/Tag1");
        await FileStore.PutAsync(cleo, "Urlaub/Tag1/strand.txt", new MemoryStream(Encoding.UTF8.GetBytes("Sand")), "text/plain");
        var dir = FileStore.Get(cleo, "Urlaub")!.FileId;
        var a = await Direct("cleo", "ben");
        var shown = (await Json(await _fx.As("cleo").PostAsJsonAsync($"/api/chat/rooms/{a}/messages", new { refs = new[] { new { type = "folder", fileId = dir, mode = "show" } } }))).GetProperty("id").GetInt64();
        var sent = (await Json(await _fx.As("cleo").PostAsJsonAsync($"/api/chat/rooms/{a}/messages", new { refs = new[] { new { type = "folder", fileId = dir, mode = "send" } } }))).GetProperty("id").GetInt64();

        // Blaettern: Tag1 und darin strand.txt
        var top = (await Json(await _fx.As("ben").GetAsync($"/api/chat/messages/{shown}/refs/0/folder"))).EnumerateArray().ToList();
        Assert.Contains(top, x => x.GetProperty("path").GetString() == "Tag1" && x.GetProperty("isDir").GetBoolean());
        var inner = (await Json(await _fx.As("ben").GetAsync($"/api/chat/messages/{shown}/refs/0/folder?path=Tag1"))).EnumerateArray().Single();
        Assert.Equal("Tag1/strand.txt", inner.GetProperty("path").GetString());
        // Kein Ausbrechen aus dem Ordner
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("ben").GetAsync($"/api/chat/messages/{shown}/refs/0/folder?path=..")).StatusCode);

        // Gezeigt: ansehen ja, herunterladen und speichern nein
        Assert.Equal("Sand", await (await _fx.As("ben").GetAsync($"/api/chat/messages/{shown}/refs/0/folder/file?path=Tag1/strand.txt&inline=true")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").GetAsync($"/api/chat/messages/{shown}/refs/0/folder/file?path=Tag1/strand.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync($"/api/chat/messages/{shown}/refs/0/folder/save", new { path = "Tag1/strand.txt" })).StatusCode);

        // Gesendet: einzelne Datei bei mir speichern
        var saved = await Json(await _fx.As("ben").PostAsJsonAsync($"/api/chat/messages/{sent}/refs/0/folder/save", new { path = "Tag1/strand.txt", folder = "Von Cleo" }));
        Assert.Equal("Sand", Encoding.UTF8.GetString(FileStore.Data(_fx.Space("ben").Db, saved.GetProperty("path").GetString()!)!));
    }

    [Fact]
    public void Geloeschter_Benutzer_verlaesst_alle_Unterhaltungen()
    {
        var dir = Directory.CreateTempSubdirectory("dmchatunit");
        try
        {
            var chat = new ChatStore(Path.Combine(dir.FullName, "chat.db"), new byte[32]);
            var g = chat.CreateGroup("u1", "Team", new[] { "u2", "u3" });
            var d = chat.OpenDirect("u1", "u2");
            chat.Post("u1", g, "Hallo");
            chat.Forget("u1");
            Assert.Empty(chat.Rooms("u1"));
            // Die Gruppe bleibt mit neuem Verwalter, die Unterhaltung zu zweit mit Ben allein
            Assert.True(chat.Room("u2", g).Members.Single(m => m.Id == "u2").Admin);
            Assert.Single(chat.Room("u2", d).Members);
            Assert.Contains(chat.Messages("u2", g), m => m.Text == "Hallo" && m.Sender == "u1");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally { try { dir.Delete(true); } catch (IOException) { } }
    }
}
