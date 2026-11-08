using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DmOffice.Core;
using DmOffice.Notes;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>
/// Freigabestufe „Zeigen“ (Version 2): ansehen in dmOffice ja; herunterladen, kopieren, exportieren und der
/// Abgleich auf Geräte (WebDAV, CalDAV) nein. Senden (nur lesen) und Teilen (bearbeiten) bleiben wie bisher.
/// </summary>
[Collection("server")]
public class ShowShareTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public ShowShareTests(SharedCalendarFixture fx) => _fx = fx;

    private const string Mit = "Mit mir geteilt";

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static string Q(string s) => Uri.EscapeDataString(s);

    private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..6];

    private string[] Ids(params string[] logins) => logins.Select(m => _fx.Users[m].Id).ToArray();

    private static Task<HttpResponseMessage> Send(HttpClient c, string method, string url, string? body = null, string? depth = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        if (depth != null) req.Headers.Add("Depth", depth);
        return c.SendAsync(req);
    }

    // ---------------- Dateiablage ----------------

    [Fact]
    public async Task Gezeigter_Ordner_ansehen_ja_herunterladen_kopieren_und_WebDAV_nein()
    {
        var anna = _fx.As("anna");
        var f = Unique("Urlaub");
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsJsonAsync("/api/files/mkdir", new { path = f })).StatusCode);
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Strand")), "file", "a.txt" } };
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsync($"/api/files/upload?path={Q(f)}", form)).StatusCode);
        // Ben: zeigen, Cleo: senden (nur lesen)
        var share = new { path = f, members = Ids("ben", "cleo"), readOnly = Ids("cleo"), showOnly = Ids("ben") };
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync("/api/files/shares", share)).StatusCode);

        // Der Besitzer sieht die Stufe je Person
        var members = (await Json(await anna.GetAsync($"/api/files/shares?path={Q(f)}"))).GetProperty("items")[0].GetProperty("members").EnumerateArray().ToList();
        Assert.Equal("show", members.Single(m => m.GetProperty("login").GetString() == "ben").GetProperty("level").GetString());
        Assert.Equal("send", members.Single(m => m.GetProperty("login").GetString() == "cleo").GetProperty("level").GetString());

        var mine = $"{Mit}/{f} (von Anna)";
        var file = mine + "/a.txt";
        var ben = _fx.As("ben");
        var entry = (await Json(await ben.GetAsync("/api/files/shared"))).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("path").GetString() == mine);
        Assert.True(entry.GetProperty("showOnly").GetBoolean());
        Assert.False(entry.GetProperty("canWrite").GetBoolean());
        var dir = await Json(await ben.GetAsync($"/api/files?path={Q(mine)}"));
        Assert.True(dir.GetProperty("showOnly").GetBoolean());
        Assert.Single(dir.GetProperty("items").EnumerateArray());

        // Ansehen im Browser geht, als Datei herunterladen, ZIP und herauskopieren nicht
        var view = await ben.GetAsync($"/api/files/download?path={Q(file)}&inline=true");
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal("Strand", await view.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/api/files/download?path={Q(file)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/api/files/zip?paths={Q(mine)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PostAsJsonAsync("/api/files/move", new { from = file, to = "kopie.txt", copy = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PostAsJsonAsync("/api/files/move", new { from = file, to = "kopie.txt", copy = false })).StatusCode);

        // Senden: herunterladen und eine eigene Kopie erlaubt
        var cleo = _fx.As("cleo");
        Assert.Equal(HttpStatusCode.OK, (await cleo.GetAsync($"/api/files/download?path={Q(file)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cleo.PostAsJsonAsync("/api/files/move", new { from = file, to = Unique("kopie") + ".txt", copy = true })).StatusCode);

        // Geräte-Abgleich (WebDAV): der gezeigte Ordner fehlt, ist auch direkt nicht erreichbar; bei Cleo ist er da
        XNamespace d = "DAV:";
        async Task<List<string>> Hrefs(HttpClient c, string url)
        {
            var r = await Send(c, "PROPFIND", url, depth: "1");
            if (r.StatusCode == HttpStatusCode.NotFound) return [];
            Assert.Equal(HttpStatusCode.MultiStatus, r.StatusCode);
            return XDocument.Parse(await r.Content.ReadAsStringAsync()).Descendants(d + "href").Select(h => Uri.UnescapeDataString(h.Value)).ToList();
        }
        Assert.DoesNotContain(await Hrefs(ben, $"/remote.php/webdav/{Mit}/"), h => h.Contains(f));
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/remote.php/webdav/{Mit}/{Q(f + " (von Anna)")}/a.txt")).StatusCode);
        Assert.Contains(await Hrefs(cleo, $"/remote.php/webdav/{Mit}/"), h => h.Contains(f));

        // Hochgestuft auf Senden: jetzt geht auch das Herunterladen
        share = new { path = f, members = Ids("ben", "cleo"), readOnly = Ids("ben", "cleo"), showOnly = Array.Empty<string>() };
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync("/api/files/shares", share)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ben.GetAsync($"/api/files/download?path={Q(file)}")).StatusCode);
    }

    // ---------------- Kalender ----------------

    [Fact]
    public async Task Gezeigter_Kalender_nur_in_dmOffice_ohne_Export_und_ohne_CalDAV()
    {
        var anna = _fx.As("anna");
        var name = Unique("Ferien");
        var r = await anna.PostAsJsonAsync("/api/pim/collections/calendars", new { name, color = "#2E7D32", components = "VEVENT" });
        var cal = (await Json(r)).GetProperty("id").GetInt64();
        Assert.True((await anna.PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Strand", start = "2027-10-05T17:00:00Z", end = "2027-10-05T18:00:00Z", allDay = false, busy = true })).IsSuccessStatusCode);
        var share = new { members = Ids("ben", "cleo"), readOnly = Ids("cleo"), showOnly = Ids("ben") };
        (await anna.PutAsJsonAsync($"/api/pim/shares/{cal}", share)).EnsureSuccessStatusCode();

        var own = (await Json(await anna.GetAsync("/api/pim/collections/calendars"))).EnumerateArray().Single(c => c.GetProperty("id").GetInt64() == cal);
        Assert.Contains(own.GetProperty("sharedWith").EnumerateArray(), m => m.GetProperty("level").GetString() == "show");

        var ben = _fx.As("ben");
        var mine = (await Json(await ben.GetAsync("/api/pim/collections/calendars"))).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        Assert.True(mine.GetProperty("showOnly").GetBoolean());
        Assert.False(mine.GetProperty("canWrite").GetBoolean());
        var id = mine.GetProperty("id").GetInt64();
        var events = await Json(await ben.GetAsync($"/api/pim/events?from=2027-10-01T00:00:00Z&to=2027-11-01T00:00:00Z&calendars={id}"));
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("title").GetString() == "Strand");
        // Export (ganzer Kalender oder ein Termin als .ics) ist gesperrt
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/api/pim/export/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/api/pim/events/{id}/x.ics/ics")).StatusCode);

        // CalDAV: bei Ben nicht da, bei Cleo (senden) schon
        const string propfind = """<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:displayname/></d:prop></d:propfind>""";
        Assert.DoesNotContain(name, await (await Send(ben, "PROPFIND", "/dav/calendars/ben/", propfind, "1")).Content.ReadAsStringAsync());
        Assert.Contains(name, await (await Send(_fx.As("cleo"), "PROPFIND", "/dav/calendars/cleo/", propfind, "1")).Content.ReadAsStringAsync());
        var shareId = -id;
        Assert.Equal(HttpStatusCode.NotFound, (await Send(ben, "PROPFIND", $"/dav/calendars/ben/shared-{shareId}/", propfind, "1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").GetAsync($"/api/pim/export/{id}")).StatusCode);
    }

    // ---------------- Notizen ----------------

    [Fact]
    public async Task Gezeigter_Notizordner_Anhang_ansehen_ja_herunterladen_nein()
    {
        var anna = _fx.As("anna");
        var r = await anna.PostAsJsonAsync("/api/notes/folders", new { name = Unique("Rezepte") });
        var folder = (await Json(r)).GetProperty("id").GetString()!;
        var note = (await Json(await anna.PostAsJsonAsync("/api/notes", new { folder }))).GetProperty("id").GetInt64();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Zutaten")), "files", "liste.txt" } };
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsync($"/api/notes/{note}/attachments", form)).StatusCode);
        var blob = NotesDb.Of(_fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id)).GetNote(note)!.Attachments.Single().Blob;
        var share = new { members = Ids("ben", "cleo"), readOnly = Ids("cleo"), showOnly = Ids("ben") };
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync($"/api/notes/shares/{folder}", share)).StatusCode);

        var ben = _fx.As("ben");
        var shared = (await Json(await ben.GetAsync("/api/notes/shared"))).EnumerateArray().Single(s => s.GetProperty("root").GetString() == folder);
        Assert.True(shared.GetProperty("showOnly").GetBoolean());
        Assert.Contains(shared.GetProperty("sharedWith").EnumerateArray(), m => m.GetProperty("level").GetString() == "send");
        // Ein Textanhang kaeme auch ohne download=true als Datei an: gesperrt (Bilder in der Notiz bleiben sichtbar)
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/api/notes/blob/{blob}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/api/notes/blob/{blob}?download=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").GetAsync($"/api/notes/blob/{blob}?download=true")).StatusCode);
    }
}
