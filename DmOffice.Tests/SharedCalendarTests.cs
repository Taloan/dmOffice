using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Pim;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Ein Server mit vier Benutzern: Anna teilt, Ben liest, Cleo bearbeitet, Dora bekommt nichts.</summary>
public sealed class SharedCalendarFixture : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; }
    public Dictionary<string, (string Id, string AppPassword)> Users { get; } = new();
    private readonly DirectoryInfo _data;

    public SharedCalendarFixture()
    {
        _data = Directory.CreateTempSubdirectory("dmshared");
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

/// <summary>Gemeinsame Kalender: Lesen und Bearbeiten, Widerruf, fremde Benutzer - in der Web-API und in CalDAV.</summary>
[Collection("server")]
public class SharedCalendarTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public SharedCalendarTests(SharedCalendarFixture fx) => _fx = fx;

    private static Task<HttpResponseMessage> Send(HttpClient c, string method, string url, string? body = null, string? depth = null, params (string, string)[] headers)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        if (depth != null) req.Headers.Add("Depth", depth);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        return c.SendAsync(req);
    }

    private static string Ics(string uid, string title) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//DE\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART:20271005T170000Z\r\nDTEND:20271005T180000Z\r\nSUMMARY:{title}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private const string Propfind = """<?xml version="1.0"?><d:propfind xmlns:d="DAV:" xmlns:a="http://apple.com/ns/ical/"><d:prop><d:displayname/><d:resourcetype/><a:calendar-color/><d:current-user-privilege-set/></d:prop></d:propfind>""";

    private async Task<long> NewCalendar(string name)
    {
        var anna = _fx.As("anna");
        var r = await anna.PostAsJsonAsync("/api/pim/collections/calendars", new { name, color = "#2E7D32", components = "VEVENT" });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private async Task Share(long calendar, string[] members, string[]? readOnly = null)
    {
        var body = new { members = members.Select(m => _fx.Users[m].Id).ToArray(), readOnly = (readOnly ?? Array.Empty<string>()).Select(m => _fx.Users[m].Id).ToArray() };
        (await _fx.As("anna").PutAsJsonAsync($"/api/pim/shares/{calendar}", body)).EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> Calendars(string login) =>
        await (await _fx.As(login).GetAsync("/api/pim/collections/calendars")).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<long> SharedIdOf(string login, string name) =>
        Calendars(login).Result.EnumerateArray().First(c => c.GetProperty("name").GetString() == name && c.GetProperty("owner").ValueKind == JsonValueKind.String).GetProperty("id").GetInt64();

    private async Task<List<JsonElement>> Events(string login, params long[] calendars) =>
        (await (await _fx.As(login).GetAsync($"/api/pim/events?from=2027-10-01T00:00:00Z&to=2027-11-01T00:00:00Z" + (calendars.Length > 0 ? "&calendars=" + string.Join(",", calendars) : ""))).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

    private static object NewEvent(long calendar, string title) => new { calendar, title, start = "2027-10-05T17:00:00Z", end = "2027-10-05T18:00:00Z", allDay = false, busy = true };

    // ---------------- Web-API ----------------

    [Fact]
    public async Task Niemand_sieht_einen_Kalender_ohne_Freigabe()
    {
        var cal = await NewCalendar("Geheim");
        var eid = (await (await _fx.As("anna").PostAsJsonAsync("/api/pim/events", NewEvent(cal, "Nur Anna"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        foreach (var other in new[] { "ben", "dora" })
        {
            var list = await Calendars(other);
            Assert.DoesNotContain(list.EnumerateArray(), c => c.GetProperty("name").GetString() == "Geheim");
            Assert.DoesNotContain(await Events(other), e => e.GetProperty("title").GetString() == "Nur Anna");
            // Die Id des Besitzers bei einem anderen Benutzer ist dessen eigener (leerer) Kalender, nie der von Anna
            Assert.DoesNotContain(await Events(other, cal), e => e.GetProperty("title").GetString() == "Nur Anna");
            // Geratene Freigabe-Ids: es gibt sie nicht
            Assert.Equal(HttpStatusCode.NotFound, (await _fx.As(other).GetAsync($"/api/pim/export/-9999")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await _fx.As(other).PostAsJsonAsync("/api/pim/events", NewEvent(-9999, "Eingeschmuggelt"))).StatusCode);
        }
        Assert.NotNull(eid);
    }

    [Fact]
    public async Task Lesen_und_Bearbeiten_und_Widerruf()
    {
        var cal = await NewCalendar("Familie");
        await Share(cal, new[] { "ben", "cleo" }, readOnly: new[] { "ben" });
        var anna = _fx.As("anna");
        Assert.True((await anna.PostAsJsonAsync("/api/pim/events", NewEvent(cal, "Elternabend"))).IsSuccessStatusCode);

        // Beide sehen den Kalender, mit Besitzer und eigenem Recht
        var benCal = (await Calendars("ben")).EnumerateArray().First(c => c.GetProperty("name").GetString() == "Familie");
        Assert.Equal("Anna", benCal.GetProperty("owner").GetString());
        Assert.False(benCal.GetProperty("canWrite").GetBoolean());
        Assert.Equal("#2E7D32", benCal.GetProperty("color").GetString());
        var cleoCal = (await Calendars("cleo")).EnumerateArray().First(c => c.GetProperty("name").GetString() == "Familie");
        Assert.True(cleoCal.GetProperty("canWrite").GetBoolean());
        var benId = benCal.GetProperty("id").GetInt64();
        var cleoId = cleoCal.GetProperty("id").GetInt64();
        Assert.True(benId < 0 && cleoId < 0);

        // Ohne Angabe der Kalender liefert die Web-API auch die geteilten, mit Recht je Termin
        var seen = (await Events("ben")).First(e => e.GetProperty("title").GetString() == "Elternabend");
        Assert.Equal(benId, seen.GetProperty("calendar").GetInt64());
        Assert.False(seen.GetProperty("canWrite").GetBoolean());

        // Ben darf nur lesen
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync("/api/pim/events", NewEvent(benId, "Von Ben"))).StatusCode);
        var uri = seen.GetProperty("uri").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").DeleteAsync($"/api/pim/events/{benId}/{uri}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PutAsJsonAsync($"/api/pim/collections/{benId}", new { name = "Meins" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").DeleteAsync($"/api/pim/collections/{benId}")).StatusCode);

        // Cleo darf bearbeiten - Anna sieht es sofort, eine gemeinsame Quelle
        var created = await _fx.As("cleo").PostAsJsonAsync("/api/pim/events", NewEvent(cleoId, "Von Cleo"));
        Assert.True(created.IsSuccessStatusCode);
        Assert.StartsWith(cleoId + "/", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        Assert.Contains(await Events("anna", cal), e => e.GetProperty("title").GetString() == "Von Cleo");
        Assert.Contains(await Events("ben"), e => e.GetProperty("title").GetString() == "Von Cleo");
        // ... und Cleo loescht, was Anna eingetragen hat
        Assert.True((await _fx.As("cleo").DeleteAsync($"/api/pim/events/{cleoId}/{uri}")).IsSuccessStatusCode);
        Assert.DoesNotContain(await Events("anna", cal), e => e.GetProperty("title").GetString() == "Elternabend");

        // Widerruf: Ben ist raus, Cleo bleibt
        await Share(cal, new[] { "cleo" });
        Assert.DoesNotContain((await Calendars("ben")).EnumerateArray(), c => c.GetProperty("name").GetString() == "Familie");
        Assert.DoesNotContain(await Events("ben", benId), e => e.GetProperty("title").GetString() == "Von Cleo");
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync($"/api/pim/export/{benId}")).StatusCode);
        Assert.Contains(await Events("cleo", cleoId), e => e.GetProperty("title").GetString() == "Von Cleo");

        // Recht aendern: Cleo nur noch lesen
        await Share(cal, new[] { "cleo" }, readOnly: new[] { "cleo" });
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PostAsJsonAsync("/api/pim/events", NewEvent(cleoId, "Nochmal"))).StatusCode);

        // Austreten
        Assert.True((await _fx.As("cleo").PostAsync($"/api/pim/shares/{cleoId}/leave", null)).IsSuccessStatusCode);
        Assert.DoesNotContain((await Calendars("cleo")).EnumerateArray(), c => c.GetProperty("name").GetString() == "Familie");
    }

    [Fact]
    public async Task Termin_lässt_sich_zwischen_eigenem_und_geteiltem_Kalender_verschieben()
    {
        var cal = await NewCalendar("Essensplan");
        await Share(cal, new[] { "ben" });
        var benId = await SharedIdOf("ben", "Essensplan");
        var ben = _fx.As("ben");
        var own = (await Calendars("ben")).EnumerateArray().First(c => c.GetProperty("owner").ValueKind == JsonValueKind.Null).GetProperty("id").GetInt64();

        var id = (await (await ben.PostAsJsonAsync("/api/pim/events", NewEvent(own, "Pizza"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var uri = id[(id.IndexOf('/') + 1)..];
        var move = await ben.PostAsJsonAsync("/api/pim/events", new { calendar = own, uri, title = "Pizza", start = "2027-10-05T17:00:00Z", end = "2027-10-05T18:00:00Z", moveTo = benId });
        Assert.True(move.IsSuccessStatusCode);
        Assert.Contains(await Events("anna", cal), e => e.GetProperty("title").GetString() == "Pizza");
        Assert.DoesNotContain(await Events("ben", own), e => e.GetProperty("title").GetString() == "Pizza");
    }

    [Fact]
    public async Task Benutzer_wird_geloescht_Freigabe_geht_mit()
    {
        var cal = await NewCalendar("Weg");
        await Share(cal, new[] { "dora" });
        Assert.Contains((await Calendars("dora")).EnumerateArray(), c => c.GetProperty("name").GetString() == "Weg");
        // Wer den Kalender loescht, nimmt ihn allen weg
        Assert.True((await _fx.As("anna").DeleteAsync($"/api/pim/collections/{cal}")).IsSuccessStatusCode);
        Assert.DoesNotContain((await Calendars("dora")).EnumerateArray(), c => c.GetProperty("name").GetString() == "Weg");
    }

    // ---------------- CalDAV ----------------

    [Fact]
    public async Task CalDAV_zeigt_geteilte_Kalender_mit_Name_Farbe_und_Rechten()
    {
        var cal = await NewCalendar("Haushalt");
        await Share(cal, new[] { "ben", "cleo" }, readOnly: new[] { "ben" });

        foreach (var (login, canWrite) in new[] { ("ben", false), ("cleo", true) })
        {
            var r = await Send(_fx.As(login), "PROPFIND", $"/dav/calendars/{login}/", Propfind, "1");
            Assert.Equal((HttpStatusCode)207, r.StatusCode);
            var xml = await r.Content.ReadAsStringAsync();
            Assert.Contains("Haushalt (von Anna)", xml);
            Assert.Contains("#2E7D32", xml);
            Assert.Contains("/shared-", xml);
            // Lese-/Schreibrecht: "write" nur, wo erlaubt (das Verzeichnis selbst hat keinen Namen und zaehlt nicht)
            var part = xml[xml.IndexOf("Haushalt (von Anna)", StringComparison.Ordinal)..];
            part = part[..part.IndexOf("</d:response>", StringComparison.Ordinal)];
            Assert.Equal(canWrite, part.Contains("<d:write"));
        }
        // Dora sieht ihn nicht
        Assert.DoesNotContain("Haushalt", await (await Send(_fx.As("dora"), "PROPFIND", "/dav/calendars/dora/", Propfind, "1")).Content.ReadAsStringAsync());
    }

    private async Task<string> SharedHref(string login, string name)
    {
        var xml = await (await Send(_fx.As(login), "PROPFIND", $"/dav/calendars/{login}/", Propfind, "1")).Content.ReadAsStringAsync();
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace d = "DAV:";
        var resp = doc.Descendants(d + "response").First(r => r.ToString().Contains(name));
        return resp.Element(d + "href")!.Value;
    }

    [Fact]
    public async Task CalDAV_Lesen_Schreiben_Sync_und_Widerruf()
    {
        var cal = await NewCalendar("Reisen");
        await Share(cal, new[] { "ben", "cleo" }, readOnly: new[] { "ben" });
        var ownerHref = $"/dav/calendars/anna/{await OwnUri(cal)}/";
        var annaPut = await Send(_fx.As("anna"), "PUT", ownerHref + "a.ics", Ics("a1", "Urlaub"), null, ("If-None-Match", "*"));
        Assert.Equal(HttpStatusCode.Created, annaPut.StatusCode);

        var benHref = await SharedHref("ben", "Reisen (von Anna)");
        var cleoHref = await SharedHref("cleo", "Reisen (von Anna)");

        // Lesen: GET, REPORT calendar-query, sync-collection
        Assert.Contains("Urlaub", await _fx.As("ben").GetStringAsync(benHref + "a.ics"));
        var query = """<?xml version="1.0"?><c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/><c:calendar-data/></d:prop><c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"/></c:comp-filter></c:filter></c:calendar-query>""";
        var q = await Send(_fx.As("ben"), "REPORT", benHref, query, "1");
        var qxml = await q.Content.ReadAsStringAsync();
        Assert.Contains("Urlaub", qxml);
        Assert.Contains(benHref + "a.ics", qxml);   // Adresse beim Empfaenger, nicht die des Besitzers
        var sync = """<?xml version="1.0"?><d:sync-collection xmlns:d="DAV:"><d:sync-token/><d:sync-level>1</d:sync-level><d:prop><d:getetag/></d:prop></d:sync-collection>""";
        var s1 = await Send(_fx.As("ben"), "REPORT", benHref, sync, "0");
        var s1xml = await s1.Content.ReadAsStringAsync();
        Assert.Contains("a.ics", s1xml);
        var token = System.Xml.Linq.XDocument.Parse(s1xml).Descendants(System.Xml.Linq.XName.Get("sync-token", "DAV:")).First().Value;

        // Ben schreibt nicht
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(_fx.As("ben"), "PUT", benHref + "b.ics", Ics("b1", "Von Ben"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(_fx.As("ben"), "DELETE", benHref + "a.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(_fx.As("ben"), "PROPPATCH", benHref,
            """<?xml version="1.0"?><d:propertyupdate xmlns:d="DAV:"><d:set><d:prop><d:displayname>Meins</d:displayname></d:prop></d:set></d:propertyupdate>""")).StatusCode);
        Assert.Contains("Urlaub", await _fx.As("anna").GetStringAsync(ownerHref + "a.ics"));

        // Cleo schreibt - Anna und Ben sehen es sofort, auch ueber sync-collection
        var cleoPut = await Send(_fx.As("cleo"), "PUT", cleoHref + "c.ics", Ics("c1", "Von Cleo"), null, ("If-None-Match", "*"));
        Assert.Equal(HttpStatusCode.Created, cleoPut.StatusCode);
        Assert.Contains("Von Cleo", await _fx.As("anna").GetStringAsync(ownerHref + "c.ics"));
        var s2 = await (await Send(_fx.As("ben"), "REPORT", benHref, sync.Replace("<d:sync-token/>", $"<d:sync-token>{token}</d:sync-token>"), "0")).Content.ReadAsStringAsync();
        Assert.Contains("c.ics", s2);
        Assert.DoesNotContain("a.ics", s2);

        // Cleo aendert mit If-Match: ein veralteter ETag wird abgelehnt (Besitzerin hat inzwischen geaendert)
        var etag = (await _fx.As("cleo").GetAsync(cleoHref + "a.ics")).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NoContent, (await Send(_fx.As("anna"), "PUT", ownerHref + "a.ics", Ics("a1", "Urlaub 2"), null, ("If-Match", etag))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(_fx.As("cleo"), "PUT", cleoHref + "a.ics", Ics("a1", "Urlaub 3"), null, ("If-Match", etag))).StatusCode);

        // Verschieben aus dem geteilten in den eigenen Kalender und zurueck
        var own = $"/dav/calendars/cleo/personal/";
        Assert.True((await Send(_fx.As("cleo"), "MOVE", cleoHref + "c.ics", null, null, ("Destination", own + "c.ics"))).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("anna").GetAsync(ownerHref + "c.ics")).StatusCode);
        Assert.Contains("Von Cleo", await _fx.As("cleo").GetStringAsync(own + "c.ics"));
        Assert.True((await Send(_fx.As("cleo"), "COPY", own + "c.ics", null, null, ("Destination", cleoHref + "c2.ics"))).IsSuccessStatusCode);
        Assert.Contains("Von Cleo", await _fx.As("anna").GetStringAsync(ownerHref + "c2.ics"));
        // Nicht in einen nur lesbaren Kalender
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(_fx.As("ben"), "COPY", benHref + "a.ics", null, null, ("Destination", benHref + "x.ics"))).StatusCode);

        // Widerruf: Ben ist sofort draussen
        await Share(cal, new[] { "cleo" });
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync(benHref + "a.ics")).StatusCode);
        Assert.DoesNotContain("Reisen", await (await Send(_fx.As("ben"), "PROPFIND", "/dav/calendars/ben/", Propfind, "1")).Content.ReadAsStringAsync());
        Assert.False((await Send(_fx.As("ben"), "REPORT", benHref, sync, "0")).IsSuccessStatusCode);

        // Dora hat nie etwas bekommen - auch nicht mit erratener Adresse
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("dora").GetAsync(benHref + "a.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("dora").GetAsync(ownerHref + "a.ics")).StatusCode);

        // Cleo loescht den Kalender bei sich: sie steigt aus, Anna behaelt ihn
        Assert.Equal(HttpStatusCode.NoContent, (await Send(_fx.As("cleo"), "DELETE", cleoHref)).StatusCode);
        Assert.DoesNotContain("Reisen", await (await Send(_fx.As("cleo"), "PROPFIND", "/dav/calendars/cleo/", Propfind, "1")).Content.ReadAsStringAsync());
        Assert.Contains("Urlaub", await _fx.As("anna").GetStringAsync(ownerHref + "a.ics"));
    }

    [Fact]
    public async Task CalDAV_fremder_Benutzer_kann_nicht_in_den_Kalender_des_Besitzers()
    {
        var cal = await NewCalendar("Privat");
        var href = $"/dav/calendars/anna/{await OwnUri(cal)}/";
        await Send(_fx.As("anna"), "PUT", href + "p.ics", Ics("p1", "Privat"));
        // Unter fremdem Benutzernamen: 403 wie bisher
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("dora").GetAsync(href + "p.ics")).StatusCode);
        // Name der Freigaben ist nicht frei vergebbar
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(_fx.As("dora"), "MKCALENDAR", "/dav/calendars/dora/shared-1/")).StatusCode);
    }

    private async Task<string> OwnUri(long cal)
    {
        var anna = _fx.As("anna");
        var list = await anna.GetFromJsonAsync<JsonElement>("/api/pim/collections/calendars");
        return list.EnumerateArray().First(c => c.GetProperty("id").GetInt64() == cal).GetProperty("uri").GetString()!;
    }

    // ---------------- Freigaben-Datei ----------------

    [Fact]
    public void Aelteres_Format_ohne_ReadOnly_bleibt_bearbeitbar()
    {
        var dir = Directory.CreateTempSubdirectory("dmshares").FullName;
        File.WriteAllText(Path.Combine(dir, "shares.json"), """[{"Id":1,"Owner":"anna","Collection":3,"Members":["ben"]}]""");
        var s = new ShareStore(new DataPaths(dir)).ById(1)!;
        Assert.Empty(s.ReadOnly);

        var store = new ShareStore(new DataPaths(dir));
        store.Set("anna", 3, new[] { "ben", "cleo" }, new[] { "cleo", "nicht-mitglied" });
        Assert.Equal(new[] { "cleo" }, store.Of("anna", 3)!.ReadOnly);
        store.Leave(1, "cleo");
        Assert.Empty(store.ById(1)!.ReadOnly);
        // Besitzer steht nie in Members/ReadOnly
        store.Set("anna", 4, new[] { "anna", "ben" }, new[] { "anna" });
        Assert.Equal(new[] { "ben" }, store.Of("anna", 4)!.Members);
        Assert.Empty(store.Of("anna", 4)!.ReadOnly);
    }
}
