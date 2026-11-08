using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>Was anderen nicht zugänglich ist und nicht aus der Hand geht: Termine, Tresor, notizi-Zugang, Tagebuch-Medien.</summary>
[Collection("server")]
public class ProtectedDataTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ProtectedDataTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private const string LoginPassword = "geheim-123456";

    /// <summary>Angemeldet wie im Browser: Tresor und Einstellungen gehen nicht mit einem App-Passwort.</summary>
    private async Task<HttpClient> Browser(string login)
    {
        var c = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        (await c.PostAsJsonAsync("/api/auth/login", new { login, password = LoginPassword })).EnsureSuccessStatusCode();
        return c;
    }

    private static string Ics(string uid, string organizer, string attendee, string title = "Gleiche Kennung") =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20301105T100000Z\r\nDTSTART:20301210T100000Z\r\nDTEND:20301210T110000Z\r\nSUMMARY:{title}\r\n"
        + $"ORGANIZER:mailto:{organizer}@dmoffice.intern\r\nATTENDEE:mailto:{attendee}@dmoffice.intern\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public async Task Fremder_Termin_mit_gleicher_Kennung_laesst_den_eigenen_in_Ruhe()
    {
        var name = "Kal" + Guid.NewGuid().ToString("N")[..6];
        var anna = _fx.As("anna");
        var cal = (await Json(await anna.PostAsJsonAsync("/api/pim/collections/calendar", new { name, color = "#222", components = "VEVENT,VTODO" }))).GetProperty("id").GetInt64();
        await Json(await anna.PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Wichtig", start = "2030-12-10T10:00:00Z", end = "2030-12-10T11:00:00Z" }));
        (await anna.PutAsJsonAsync($"/api/pim/shares/{cal}", new { members = new[] { _fx.Id("ben") }, readOnly = new[] { _fx.Id("ben") } })).EnsureSuccessStatusCode();
        const string range = "from=2030-12-01T00:00:00Z&to=2030-12-30T00:00:00Z";
        async Task<string[]> Titles() => (await Json(await anna.GetAsync($"/api/pim/events?{range}&calendars={cal}"))).EnumerateArray().Select(e => e.GetProperty("title").GetString()!).ToArray();

        var ben = _fx.As("ben");
        var shared = (await Json(await ben.GetAsync("/api/pim/collections/calendar"))).EnumerateArray().First(c => c.GetProperty("name").GetString()!.StartsWith(name)).GetProperty("id").GetInt64();
        var uid = (await Json(await ben.GetAsync($"/api/pim/events?{range}&calendars={shared}"))).EnumerateArray().First().GetProperty("uid").GetString()!;
        var own = await Json(await ben.PostAsJsonAsync("/api/pim/collections/calendar", new { name = name + "B", color = "#333", components = "VEVENT,VTODO" }));
        var ownId = own.GetProperty("id").GetInt64();
        var ownUri = (await Json(await ben.GetAsync("/api/pim/collections/calendar"))).EnumerateArray().First(c => c.GetProperty("id").GetInt64() == ownId).GetProperty("uri").GetString();
        var dav = _fx.As("ben");

        // Absage eines eigenen Termins mit derselben Kennung
        Assert.True((await dav.PutAsync($"/dav/calendars/ben/{ownUri}/k1.ics", new StringContent(Ics(uid, "ben", "anna"), Encoding.UTF8, "text/calendar"))).IsSuccessStatusCode);
        (await ben.DeleteAsync($"/api/pim/events/{ownId}/k1.ics")).EnsureSuccessStatusCode();
        Assert.Equal(["Wichtig"], await Titles());

        // Änderung eines eigenen Termins mit derselben Kennung
        Assert.True((await dav.PutAsync($"/dav/calendars/ben/{ownUri}/k2.ics", new StringContent(Ics(uid, "ben", "anna"), Encoding.UTF8, "text/calendar"))).IsSuccessStatusCode);
        (await ben.PostAsJsonAsync("/api/pim/events", new { calendar = ownId, uri = "k2.ics", title = "Von Ben", start = "2030-12-11T10:00:00Z", end = "2030-12-11T11:00:00Z", notify = true })).EnsureSuccessStatusCode();
        Assert.Equal(["Wichtig"], await Titles());

        // Termin ohne Organisator, bei dem die Besitzerin selbst Teilnehmerin ist
        var plain = "Plain" + Guid.NewGuid().ToString("N")[..8];
        (await anna.PutAsync($"/dav/calendars/anna/{await CalUri(anna, cal)}/p.ics", new StringContent(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:t\r\nBEGIN:VEVENT\r\nUID:{plain}\r\nDTSTAMP:20301105T100000Z\r\nDTSTART:20301212T100000Z\r\nDTEND:20301212T110000Z\r\nSUMMARY:Ohne\r\nATTENDEE:mailto:anna@dmoffice.intern\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n",
            Encoding.UTF8, "text/calendar"))).EnsureSuccessStatusCode();
        Assert.True((await dav.PutAsync($"/dav/calendars/ben/{ownUri}/k3.ics", new StringContent(Ics(plain, "ben", "anna"), Encoding.UTF8, "text/calendar"))).IsSuccessStatusCode);
        (await ben.DeleteAsync($"/api/pim/events/{ownId}/k3.ics?notify=true")).EnsureSuccessStatusCode();
        Assert.Contains("Ohne", await Titles());
    }

    private static async Task<string?> CalUri(HttpClient c, long cal) =>
        (await Json(await c.GetAsync("/api/pim/collections/calendar"))).EnumerateArray().First(x => x.GetProperty("id").GetInt64() == cal).GetProperty("uri").GetString();

    [Fact]
    public async Task System_Tresor_oeffnet_nur_ueber_seinen_eigenen_Weg()
    {
        var c = await Browser("cleo");
        var password = "Tresor-" + Guid.NewGuid().ToString("N");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password, name = "Test" }));
        var r = await c.PostAsJsonAsync("/api/files/vault/unlock", new { path = "dmVAULT/vault", password });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/vault/unlock", new { path = "dmVAULT//vault/", password })).StatusCode);
    }

    [Fact]
    public async Task Kerndateien_des_Tresors_bleiben_und_gehen_nicht_in_den_Chat()
    {
        var c = await Browser("dora");
        var password = "Tresor-" + Guid.NewGuid().ToString("N");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password, name = "Test" }));
        (await c.PutAsJsonAsync("/api/tresor/settings", new { dav = true })).EnsureSuccessStatusCode();
        var dav = _fx.As("dora");
        foreach (var file in new[] { "vault.cryptomator", "masterkey.cryptomator" })
            Assert.Equal(HttpStatusCode.Forbidden, (await dav.PutAsync($"/dav/files/dora/dmVAULT/vault/{file}", new StringContent("kaputt"))).StatusCode);
        (await c.PostAsync("/api/tresor/close-all", null)).EnsureSuccessStatusCode();
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password, name = "Test" }));
        // Passwort ändern schreibt masterkey.cryptomator weiter
        var other = password + "x";
        await Json(await c.PostAsJsonAsync("/api/tresor/password", new { current = password, password = other }));

        var space = _fx.Space("dora");
        var key = FileStore.Get(space.Db, "dmVAULT/vault/masterkey.cryptomator")!;
        var room = (await Json(await c.PostAsJsonAsync("/api/chat/rooms", new { kind = "direct", user = _fx.Id("ben"), with = _fx.Id("ben"), members = new[] { _fx.Id("ben") } }))).GetProperty("id").GetInt64();
        var sent = await c.PostAsJsonAsync($"/api/chat/rooms/{room}/messages", new { text = "x", refs = new[] { new { type = "file", fileId = key.FileId, mode = "send" } } });
        Assert.False(sent.IsSuccessStatusCode);

        // Auch nicht ins DMS
        (await c.PostAsJsonAsync("/api/dms/import-files", new { paths = new[] { "dmVAULT/vault/masterkey.cryptomator" }, move = false })).EnsureSuccessStatusCode();
        Assert.Empty((await Json(await c.GetAsync("/api/dms/docs"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Tresor_laesst_sich_nach_abgebrochener_Einrichtung_neu_einrichten()
    {
        var space = _fx.Space("anna");
        using (FileStore.SystemAccess())
        {
            FileStore.EnsureDir(space.Db, "dmVAULT/vault");
            FileStore.Put(space.Db, "dmVAULT/vault/vault.cryptomator", "halb"u8.ToArray(), "application/octet-stream");
            FileStore.Put(space.Db, "dmVAULT/vault/masterkey.cryptomator", "halb"u8.ToArray(), "application/octet-stream");
        }
        var c = await Browser("anna");
        var password = "Tresor-" + Guid.NewGuid().ToString("N");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password, name = "Test" }));
        (await c.PostAsync("/api/tresor/close-all", null)).EnsureSuccessStatusCode();
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password, name = "Test" }));
    }

    [Fact]
    public async Task Interne_Daten_des_Tresors_schreibt_nur_dmOffice()
    {
        var c = await Browser("ben");
        var password = "Tresor-" + Guid.NewGuid().ToString("N");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password, name = "Test" }));
        (await c.PutAsJsonAsync("/api/tresor/settings", new { dav = true })).EnsureSuccessStatusCode();
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password, name = "Test" }));
        var note = new MultipartFormDataContent { { new ByteArrayContent("inhalt"u8.ToArray()), "file", "a.txt" } };
        (await c.PostAsync("/api/files/upload?path=" + Uri.EscapeDataString("tresor:/"), note)).EnsureSuccessStatusCode();
        (await c.PostAsync("/api/tresor/close-all", null)).EnsureSuccessStatusCode();
        var nodes = JsonSerializer.Deserialize<string[]>(_fx.Space("ben").Db.Get("tresor.internal")!)!;
        Assert.Equal(2, nodes.Length);
        var dav = _fx.As("ben");
        Assert.Equal(HttpStatusCode.Forbidden, (await dav.PutAsync("/dav/files/ben/" + nodes[1], new StringContent("kaputt"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dav.DeleteAsync("/dav/files/ben/" + nodes[1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dav.DeleteAsync("/dav/files/ben/" + nodes[0])).StatusCode);
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password, name = "Test" }));
        // dmOffice selbst schreibt weiter
        (await c.PostAsync("/api/tresor/close-all", null)).EnsureSuccessStatusCode();
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password, name = "Test" }));
    }

    [Fact]
    public async Task Notizi_Zugang_braucht_das_Passwort_und_Ordnernamen_werden_geprueft()
    {
        var c = await Browser("ben");
        Assert.False((await c.PostAsJsonAsync("/api/settings/notizi/connect", new { })).IsSuccessStatusCode);
        Assert.False((await c.PostAsJsonAsync("/api/settings/notizi/connect", new { password = "falsch" })).IsSuccessStatusCode);
        var ok = await Json(await c.PostAsJsonAsync("/api/settings/notizi/connect", new { password = LoginPassword }));
        Assert.False(string.IsNullOrEmpty(ok.GetProperty("password").GetString()));
        foreach (var root in new[] { ".", "..", "a/../b", "dmVAULT", "dmVAULT/vault" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/settings/notizi", new { enabled = false, root, dbFolder = "" })).StatusCode);
    }

    [Fact]
    public async Task Geteiltes_Tagebuch_gibt_keine_anderen_Dateien_des_Besitzers_her()
    {
        var anna = _fx.As("anna");
        var ben = _fx.As("ben");
        var folder = "Geheim" + Guid.NewGuid().ToString("N")[..6];
        var space = _fx.Space("anna");
        FileStore.EnsureDir(space.Db, folder);
        FileStore.Put(space.Db, folder + "/g.txt", "geheim"u8.ToArray(), "text/plain");
        var name = "TB" + Guid.NewGuid().ToString("N")[..6];
        var own = (await Json(await anna.PostAsJsonAsync("/api/journal/journals", new { name, color = "#123" }))).GetProperty("id").GetInt64();
        (await anna.PutAsJsonAsync($"/api/pim/shares/{own}", new { members = new[] { _fx.Id("ben") } })).EnsureSuccessStatusCode();
        var j = (await Json(await ben.GetAsync("/api/journal/journals"))).EnumerateArray().First(x => x.GetProperty("name").GetString() == name).GetProperty("id").GetInt64();

        var media = new[] { new { path = folder + "/g.txt", mime = "text/plain" } };
        Assert.False((await ben.PostAsJsonAsync("/api/journal/entries", new { journal = j, date = "2030-01-01T10:00:00Z", html = "<p>x</p>", media })).IsSuccessStatusCode);
        Assert.False((await ben.PostAsJsonAsync("/api/journal/from-media", new { journal = j, media })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/journal/file?journal={j}&path={Uri.EscapeDataString(folder + "/g.txt")}")).StatusCode);

        // Über die Kalender-Wege gibt es das Tagebuch für Mitglieder nicht
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/pim/export/{j}")).StatusCode);
        var dav = _fx.As("ben");
        Assert.Equal(HttpStatusCode.NotFound, (await dav.PutAsync($"/dav/calendars/ben/shared{j}/x.ics", new StringContent("x"))).StatusCode);

        // Was im Medienordner des Tagebuchs liegt (dorthin kommt alles Hochgeladene), geht weiter
        var path = Journal.JournalMediaStore.Root + "/2030/01/" + name + ".txt";
        FileStore.EnsureDir(space.Db, Journal.JournalMediaStore.Root + "/2030/01");
        FileStore.Put(space.Db, path, "hallo"u8.ToArray(), "text/plain");
        await Json(await ben.PostAsJsonAsync("/api/journal/entries", new { journal = j, date = "2030-01-02T10:00:00Z", html = "<p>y</p>", media = new[] { new { path, mime = "text/plain" } } }));
        Assert.True((await ben.GetAsync($"/api/journal/file?journal={j}&path={Uri.EscapeDataString(path)}")).IsSuccessStatusCode);
    }
}
