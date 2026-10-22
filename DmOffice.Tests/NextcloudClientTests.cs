using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Ein Server im Testprozess mit eigenem, leerem Datenordner und einem Benutzer.</summary>
public sealed class NextcloudServerFixture : IDisposable
{
    public const string Login = "anna";
    public const string Password = "geheim-123456";
    public WebApplicationFactory<Program> Factory { get; }
    public string AppPassword { get; }
    private readonly DirectoryInfo _data;

    public NextcloudServerFixture()
    {
        _data = Directory.CreateTempSubdirectory("dmnc");
        // Program.cs liest den Datenordner beim Start
        Environment.SetEnvironmentVariable("DMOFFICE_DATA", _data.FullName);
        Factory = new WebApplicationFactory<Program>();
        var users = Factory.Services.GetRequiredService<UserStore>();
        var u = users.Create(Login, "Anna Muster", Password, isAdmin: true);
        AppPassword = AppPasswords.Create(Factory.Services.GetRequiredService<Spaces>().Get(u.Id).Db, "Test");
    }

    public HttpClient Client(string? appPassword = null)
    {
        var c = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows) mirall/3.14.0stable-Win64 (Nextcloud, windows-10.0.22631 ClientArchitecture: x86_64)");
        if (appPassword != null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Login}:{appPassword}")));
        return c;
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { _data.Delete(true); } catch (IOException) { /* Datenbank noch offen */ }
    }
}

/// <summary>
/// Die Anfragen der Nextcloud-Clients (Desktop 3.x, nachgestellt): Erkennung, Anmeldung mit
/// Login Flow v2, Auflisten, Hochladen, Aendern mit ETag-Weitergabe nach oben, Verschieben, Loeschen.
/// </summary>
[Collection("server")]
public class NextcloudClientTests : IClassFixture<NextcloudServerFixture>
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace OC = "http://owncloud.org/ns";
    private readonly NextcloudServerFixture _fx;
    private string Base => $"/remote.php/dav/files/{NextcloudServerFixture.Login}/";

    public NextcloudClientTests(NextcloudServerFixture fx) => _fx = fx;

    /// <summary>Wie der Desktop-Client bei der Erkennung und beim Abgleich fragt (LsColJob).</summary>
    private const string ClientPropfind = """
        <?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getlastmodified/><d:getcontentlength/><d:getetag/><oc:size xmlns:oc="http://owncloud.org/ns"/><oc:id xmlns:oc="http://owncloud.org/ns"/><oc:fileid xmlns:oc="http://owncloud.org/ns"/><oc:downloadURL xmlns:oc="http://owncloud.org/ns"/><oc:dDC xmlns:oc="http://owncloud.org/ns"/><oc:permissions xmlns:oc="http://owncloud.org/ns"/><oc:checksums xmlns:oc="http://owncloud.org/ns"/><nc:is-encrypted xmlns:nc="http://nextcloud.org/ns"/></d:prop></d:propfind>
        """;

    private sealed record Item(string Href, bool IsDir, string Etag, string Id, long FileId, string Permissions, long Size, string? Modified);

    private static async Task<HttpResponseMessage> Send(HttpClient c, string method, string url, string? body = null, string? depth = null, params (string, string)[] headers)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, method == "PUT" ? "application/octet-stream" : "application/xml");
        if (depth != null) req.Headers.Add("Depth", depth);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        return await c.SendAsync(req);
    }

    private static async Task<List<Item>> Propfind(HttpClient c, string url, string depth = "1")
    {
        var r = await Send(c, "PROPFIND", url, ClientPropfind, depth);
        Assert.Equal((HttpStatusCode)207, r.StatusCode);
        var doc = XDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.Root!.Elements(D + "response").Select(resp =>
        {
            var prop = resp.Elements(D + "propstat").First(ps => ps.Element(D + "status")!.Value.Contains("200")).Element(D + "prop")!;
            return new Item(Uri.UnescapeDataString(resp.Element(D + "href")!.Value),
                prop.Element(D + "resourcetype")?.Element(D + "collection") != null,
                prop.Element(D + "getetag")!.Value, prop.Element(OC + "id")!.Value, long.Parse(prop.Element(OC + "fileid")!.Value),
                prop.Element(OC + "permissions")!.Value, long.Parse(prop.Element(OC + "size")!.Value), prop.Element(D + "getlastmodified")?.Value);
        }).ToList();
    }

    private async Task<Item> Self(HttpClient c, string rel) => (await Propfind(c, Base + rel, "0")).Single();

    [Fact]
    public async Task Erkennung_status_und_Faehigkeiten_ohne_Anmeldung()
    {
        var c = _fx.Client();
        var status = JsonDocument.Parse(await c.GetStringAsync("/status.php")).RootElement;
        Assert.True(status.GetProperty("installed").GetBoolean());
        Assert.False(status.GetProperty("maintenance").GetBoolean());
        Assert.StartsWith("30.", status.GetProperty("version").GetString());

        foreach (var (v, code) in new[] { (1, 100), (2, 200) })
        {
            var caps = JsonDocument.Parse(await c.GetStringAsync($"/ocs/v{v}.php/cloud/capabilities?format=json")).RootElement.GetProperty("ocs");
            Assert.Equal(code, caps.GetProperty("meta").GetProperty("statuscode").GetInt32());
            Assert.Equal("remote.php/webdav", caps.GetProperty("data").GetProperty("capabilities").GetProperty("core").GetProperty("webdav-root").GetString());
        }

        // So erkennt der Client die Anmeldeart: WebDAV ohne Anmeldung -> 401 mit Basic -> Login Flow v2
        var r = await Send(c, "PROPFIND", "/remote.php/dav/files/", ClientPropfind, "0");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("Basic", r.Headers.WwwAuthenticate.ToString());

        // Die Oberflaeche antwortet nicht unter Nextcloud-Adressen (sonst HTML statt Fehler)
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/index.php/avatar/anna/64")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/ocs/v2.php/apps/notifications/api/v2/notifications")).StatusCode);
    }

    [Fact]
    public async Task Login_Flow_v2_gibt_ein_App_Passwort_das_fuer_WebDAV_gilt()
    {
        var c = _fx.Client();
        var start = JsonDocument.Parse(await (await c.PostAsync("/index.php/login/v2", null)).Content.ReadAsStringAsync()).RootElement;
        var token = start.GetProperty("poll").GetProperty("token").GetString()!;
        var endpoint = new Uri(start.GetProperty("poll").GetProperty("endpoint").GetString()!);
        var login = new Uri(start.GetProperty("login").GetString()!);
        Assert.Equal("/index.php/login/v2/poll", endpoint.AbsolutePath);

        async Task<HttpResponseMessage> Poll() => await c.PostAsync(endpoint.AbsolutePath, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        Assert.Equal(HttpStatusCode.NotFound, (await Poll()).StatusCode);

        // Im Browser: Seite mit Formular, falsches Passwort wird abgewiesen, richtiges gibt frei
        var page = await c.GetStringAsync(login.AbsolutePath);
        Assert.Contains("Nextcloud Desktop", page);
        Assert.Contains("<form method=\"post\">", page);
        var wrong = await c.PostAsync(login.AbsolutePath, new FormUrlEncodedContent(new Dictionary<string, string> { ["login"] = NextcloudServerFixture.Login, ["password"] = "falsch" }));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Poll()).StatusCode);
        var ok = await c.PostAsync(login.AbsolutePath, new FormUrlEncodedContent(new Dictionary<string, string> { ["login"] = NextcloudServerFixture.Login, ["password"] = NextcloudServerFixture.Password }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var done = await Poll();
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var result = JsonDocument.Parse(await done.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(NextcloudServerFixture.Login, result.GetProperty("loginName").GetString());
        Assert.StartsWith("http", result.GetProperty("server").GetString());
        var appPassword = result.GetProperty("appPassword").GetString()!;
        // Nur einmal abholbar, und die Seite ist danach verbraucht
        Assert.Equal(HttpStatusCode.NotFound, (await Poll()).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(login.AbsolutePath)).StatusCode);

        // Mit dem neuen App-Passwort: Benutzer und Dateiwurzel
        var authed = _fx.Client(appPassword);
        var user = JsonDocument.Parse(await authed.GetStringAsync("/ocs/v1.php/cloud/user?format=json")).RootElement.GetProperty("ocs");
        Assert.Equal(100, user.GetProperty("meta").GetProperty("statuscode").GetInt32());
        Assert.Equal(NextcloudServerFixture.Login, user.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("Anna Muster", user.GetProperty("data").GetProperty("display-name").GetString());
        var root = await Self(authed, "");
        Assert.True(root.IsDir);
        Assert.Equal("RGDNVCK", root.Permissions);
        Assert.Equal(Base, root.Href);
        // Das App-Passwort steht in der Liste und heisst nach dem Client
        Assert.Contains(AppPasswords.List(_fx.Factory.Services.GetRequiredService<Spaces>()
            .Get(_fx.Factory.Services.GetRequiredService<UserStore>().ByLogin(NextcloudServerFixture.Login)!.Id).Db), e => e.Name == "Nextcloud Desktop");
    }

    [Fact]
    public async Task Login_Flow_v1_leitet_an_die_App_weiter()
    {
        var c = _fx.Client();
        var r = await c.GetAsync("/index.php/login/flow");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var page = r.Headers.Location!.OriginalString;
        var ok = await c.PostAsync(page, new FormUrlEncodedContent(new Dictionary<string, string> { ["login"] = NextcloudServerFixture.Login, ["password"] = NextcloudServerFixture.Password }));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.StartsWith("nc://login/server:http", ok.Headers.Location!.OriginalString);
        Assert.Contains("&user:anna&password:", ok.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Hochladen_aendern_verschieben_loeschen_wie_der_Client()
    {
        var c = _fx.Client(_fx.AppPassword);
        var rootBefore = await Self(c, "");

        // Ordner anlegen: OC-FileId im Kopf, Wurzel hat einen neuen ETag
        var mk = await Send(c, "MKCOL", Base + "Diarium");
        Assert.Equal(HttpStatusCode.Created, mk.StatusCode);
        Assert.EndsWith(NextcloudApi.InstanceId, mk.Headers.GetValues("OC-FileId").Single());
        Assert.Equal(HttpStatusCode.Created, (await Send(c, "MKCOL", Base + "Diarium/2026")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(c, "MKCOL", Base + "Anderes")).StatusCode);
        Assert.NotEqual(rootBefore.Etag, (await Self(c, "")).Etag);

        // Hochladen mit Aenderungszeit, wie der Client (X-OC-Mtime)
        var mtime = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero);
        var put = await Send(c, "PUT", Base + "Diarium/2026/eintrag%20eins.txt", "Hallo", null, ("X-OC-Mtime", mtime.ToUnixTimeSeconds().ToString()), ("OC-Checksum", "SHA1:abc"));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal("accepted", put.Headers.GetValues("X-OC-MTime").Single());
        var fileId = put.Headers.GetValues("OC-FileId").Single();
        var etag = put.Headers.GetValues("OC-ETag").Single();

        var list = await Propfind(c, Base + "Diarium/2026/");
        Assert.Equal(2, list.Count);
        var file = list.Single(i => !i.IsDir);
        Assert.Equal(Base + "Diarium/2026/eintrag eins.txt", file.Href);
        Assert.Equal(fileId, file.Id);
        Assert.Equal(etag, file.Etag);
        Assert.Equal("RGDNVW", file.Permissions);
        Assert.Equal(5, file.Size);
        Assert.Equal(mtime.UtcDateTime, DateTime.Parse(file.Modified!).ToUniversalTime());
        // Ordnergroesse zaehlt alles darunter
        Assert.Equal(5, (await Self(c, "Diarium")).Size);

        // Aendern: ETag der Datei und aller Ordner darueber neu, der Nachbarordner bleibt
        var before = (root: await Self(c, ""), diarium: await Self(c, "Diarium"), year: await Self(c, "Diarium/2026"), other: await Self(c, "Anderes"));
        var change = await Send(c, "PUT", Base + "Diarium/2026/eintrag%20eins.txt", "Hallo Welt", null, ("If-Match", etag));
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(fileId, change.Headers.GetValues("OC-FileId").Single());
        Assert.NotEqual(etag, change.Headers.GetValues("OC-ETag").Single());
        Assert.NotEqual(before.root.Etag, (await Self(c, "")).Etag);
        Assert.NotEqual(before.diarium.Etag, (await Self(c, "Diarium")).Etag);
        Assert.NotEqual(before.year.Etag, (await Self(c, "Diarium/2026")).Etag);
        Assert.Equal(before.other.Etag, (await Self(c, "Anderes")).Etag);
        // Veralteter ETag: 412, nichts ueberschrieben
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send(c, "PUT", Base + "Diarium/2026/eintrag%20eins.txt", "alt", null, ("If-Match", etag))).StatusCode);

        // Herunterladen
        var get = await c.GetAsync(Base + "Diarium/2026/eintrag%20eins.txt");
        Assert.Equal("Hallo Welt", await get.Content.ReadAsStringAsync());
        Assert.Equal(fileId, get.Headers.GetValues("OC-FileId").Single());

        // Verschieben (absolute Destination wie der Client): Nummer bleibt, beide Seiten neue ETags
        var otherBefore = await Self(c, "Anderes");
        var yearBefore = await Self(c, "Diarium/2026");
        var move = await Send(c, "MOVE", Base + "Diarium/2026/eintrag%20eins.txt", null, null,
            ("Destination", "http://localhost" + Base + "Anderes/umbenannt%20%C3%A4.txt"), ("Overwrite", "F"));
        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        Assert.Equal(fileId, move.Headers.GetValues("OC-FileId").Single());
        var moved = await Self(c, "Anderes/umbenannt%20%C3%A4.txt");
        Assert.Equal(fileId, moved.Id);
        Assert.Equal(Base + "Anderes/umbenannt ä.txt", moved.Href);
        Assert.NotEqual(otherBefore.Etag, (await Self(c, "Anderes")).Etag);
        Assert.NotEqual(yearBefore.Etag, (await Self(c, "Diarium/2026")).Etag);
        Assert.Equal((HttpStatusCode)404, (await Send(c, "PROPFIND", Base + "Diarium/2026/eintrag%20eins.txt", ClientPropfind, "0")).StatusCode);

        // Ordner verschieben: der Inhalt behaelt seine Nummern
        var yearId = (await Self(c, "Diarium/2026")).Id;
        Assert.Equal(HttpStatusCode.Created, (await Send(c, "MOVE", Base + "Diarium/2026", null, null, ("Destination", Base + "Anderes/2026"))).StatusCode);
        Assert.Equal(yearId, (await Self(c, "Anderes/2026")).Id);

        // Kopieren: neue Nummer
        Assert.Equal(HttpStatusCode.Created, (await Send(c, "COPY", Base + "Anderes/umbenannt%20%C3%A4.txt", null, null, ("Destination", Base + "Diarium/kopie.txt"))).StatusCode);
        var copy = await Self(c, "Diarium/kopie.txt");
        Assert.NotEqual(fileId, copy.Id);

        // Loeschen: weg, Ordner darueber neu, die Nummer kommt nie wieder
        var rootBeforeDelete = await Self(c, "");
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync(Base + "Diarium/kopie.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Base + "Diarium/kopie.txt")).StatusCode);
        Assert.NotEqual(rootBeforeDelete.Etag, (await Self(c, "")).Etag);
        var again = await Send(c, "PUT", Base + "Diarium/neu.txt", "x");
        Assert.True(long.Parse(again.Headers.GetValues("OC-FileId").Single()[..8]) > copy.FileId);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync(Base + "Anderes")).StatusCode);
    }

    [Fact]
    public async Task Alte_webdav_Adresse_und_fremde_Benutzer()
    {
        var c = _fx.Client(_fx.AppPassword);
        Assert.Equal(HttpStatusCode.Created, (await Send(c, "PUT", "/remote.php/webdav/alt.txt", "a")).StatusCode);
        var list = await Propfind(c, "/remote.php/webdav/");
        Assert.Equal("/remote.php/webdav/", list[0].Href);
        Assert.Contains(list, i => i.Href == "/remote.php/webdav/alt.txt");
        // Gleiche Datei unter beiden Adressen, auch unter /dav
        Assert.Equal("a", await c.GetStringAsync(Base + "alt.txt"));
        Assert.Equal("a", await c.GetStringAsync($"/dav/files/{NextcloudServerFixture.Login}/alt.txt"));
        // MOVE von der alten Adresse auf die neue
        Assert.Equal(HttpStatusCode.Created, (await Send(c, "MOVE", "/remote.php/webdav/alt.txt", null, null, ("Destination", Base + "alt2.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync(Base + "alt2.txt")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, "PROPFIND", "/remote.php/dav/files/fremd/", ClientPropfind, "0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, "PROPFIND", $"/remote.php/dav/calendars/{NextcloudServerFixture.Login}/", ClientPropfind, "0")).StatusCode);
        // Falsches Passwort
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(_fx.Client("falsch"), "PROPFIND", Base, ClientPropfind, "0")).StatusCode);
    }

    [Fact]
    public async Task Hochladen_in_Stuecken_wird_abgelehnt_statt_Stuecke_abzulegen()
    {
        var c = _fx.Client(_fx.AppPassword);
        var r = await Send(c, "PUT", Base + "gross.bin-chunking-4711-3-0", "teil", null, ("OC-Chunked", "1"));
        Assert.Equal(HttpStatusCode.NotImplemented, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Base + "gross.bin-chunking-4711-3-0")).StatusCode);
    }

    [Fact]
    public async Task Abgleichordner_von_notizi_ist_fuer_Nextcloud_unsichtbar()
    {
        var c = _fx.Client(_fx.AppPassword);
        // Ueber /dav (wie notizi) angelegt
        await Send(c, "MKCOL", $"/dav/files/{NextcloudServerFixture.Login}/notizisync");   // gibt es ggf. schon (405)
        Assert.True((await Send(c, "PUT", $"/dav/files/{NextcloudServerFixture.Login}/notizisync/db.bin", "x")).IsSuccessStatusCode);

        Assert.DoesNotContain(await Propfind(c, Base), i => i.Href.Contains("notizisync"));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, "PROPFIND", Base + "notizisync/", ClientPropfind, "1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Base + "notizisync/db.bin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, "PUT", "/remote.php/webdav/notizisync/neu.txt", "y")).StatusCode);
        // Fuer notizi bleibt alles da
        Assert.Equal("x", await c.GetStringAsync($"/dav/files/{NextcloudServerFixture.Login}/notizisync/db.bin"));

        // Auch die Dateiablage der Weboberflaeche zeigt und aendert ihn nicht (geschuetzter Bereich)
        var web = _fx.Factory.CreateClient();
        Assert.True((await web.PostAsJsonAsync("/api/auth/login", new { login = NextcloudServerFixture.Login, password = NextcloudServerFixture.Password })).IsSuccessStatusCode);
        Assert.DoesNotContain("notizisync", await web.GetStringAsync("/api/files/?path="));
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync("/api/files/?path=notizisync")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsJsonAsync("/api/files/delete", new { path = "notizisync" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsJsonAsync("/api/files/move", new { from = "notizisync/db.bin", to = "weg.bin", copy = true })).StatusCode);
        Assert.Equal("x", await c.GetStringAsync($"/dav/files/{NextcloudServerFixture.Login}/notizisync/db.bin"));
    }

    private static byte[] Png(int w, int h, bool transparent = false)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        bmp.Erase(transparent ? new SkiaSharp.SKColor(0, 120, 200, 100) : new SkiaSharp.SKColor(0, 120, 200));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray();
    }

    private static async Task<HttpResponseMessage> PutBytes(HttpClient c, string url, byte[] data) =>
        await c.PutAsync(url, new ByteArrayContent(data));

    private static readonly XNamespace NC = "http://nextcloud.org/ns";

    private UserSpace Space() => _fx.Factory.Services.GetRequiredService<Spaces>()
        .Get(_fx.Factory.Services.GetRequiredService<UserStore>().ByLogin(NextcloudServerFixture.Login)!.Id);

    /// <summary>nc:has-preview je Name in einem Ordner.</summary>
    private static async Task<Dictionary<string, string?>> HasPreview(HttpClient c, string url)
    {
        var r = await Send(c, "PROPFIND", url, """<?xml version="1.0"?><d:propfind xmlns:d="DAV:" xmlns:nc="http://nextcloud.org/ns"><d:prop><nc:has-preview/></d:prop></d:propfind>""", "1");
        var doc = XDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.Root!.Elements(D + "response").ToDictionary(e => Uri.UnescapeDataString(e.Element(D + "href")!.Value).TrimEnd('/').Split('/').Last(),
            e => e.Descendants(NC + "has-preview").FirstOrDefault()?.Value);
    }

    [Fact]
    public async Task Vorschau_has_preview_nur_fuer_Bilder_PDF_und_Videos_mit_Standbild()
    {
        var c = _fx.Client(_fx.AppPassword);
        await Send(c, "MKCOL", Base + "vorschau");
        Assert.True((await PutBytes(c, Base + "vorschau/bild.png", Png(40, 30))).IsSuccessStatusCode);
        Assert.True((await Send(c, "PUT", Base + "vorschau/text.txt", "hallo")).IsSuccessStatusCode);
        Assert.True((await Send(c, "PUT", Base + "vorschau/film.mp4", "kein echtes video")).IsSuccessStatusCode);
        Assert.True((await PutBytes(c, Base + "vorschau/kaputt.jpg", [1, 2, 3, 4])).IsSuccessStatusCode);

        var p = await HasPreview(c, Base + "vorschau/");
        Assert.Equal("false", p["vorschau"]);
        Assert.Equal("true", p["bild.png"]);
        Assert.Equal("false", p["text.txt"]);
        Assert.Equal("false", p["film.mp4"]);
        Assert.Equal("true", p["kaputt.jpg"]);

        // Fehlgeschlagen merkt sich der Server - danach kein has-preview mehr
        var kaputt = await Self(c, "vorschau/kaputt.jpg");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/index.php/core/preview?fileId={kaputt.Id}&x=256&y=256&a=true")).StatusCode);
        Assert.Equal("false", (await HasPreview(c, Base + "vorschau/"))["kaputt.jpg"]);

        // Standbild fuer das Video (wie aus der Weboberflaeche)
        var space = Space();
        Assert.True(Thumbs.Accept(space.Db, FileStore.Get(space.Db, "vorschau/film.mp4")!, Png(64, 36)));
        Assert.Equal("true", (await HasPreview(c, Base + "vorschau/"))["film.mp4"]);
    }

    [Fact]
    public async Task Vorschau_fuer_den_Explorer_als_JPEG_oder_PNG()
    {
        var c = _fx.Client(_fx.AppPassword);
        await Send(c, "MKCOL", Base + "bilder");
        Assert.True((await PutBytes(c, Base + "bilder/foto.png", Png(800, 600))).IsSuccessStatusCode);
        Assert.True((await PutBytes(c, Base + "bilder/glas.png", Png(100, 100, transparent: true))).IsSuccessStatusCode);
        var foto = await Self(c, "bilder/foto.png");
        Assert.EndsWith(NextcloudApi.InstanceId, foto.Id);

        // So fragt der Windows-Client (ShellExtensionsServer::processThumbnailRequest): oc:id, Groesse, a=true
        var r = await c.GetAsync($"/index.php/core/preview?fileId={foto.Id}&x=256&y=256&a=true");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/jpeg", r.Content.Headers.ContentType!.MediaType);
        using (var bmp = SkiaSharp.SKBitmap.Decode(await r.Content.ReadAsByteArrayAsync()))
            Assert.Equal(256, Math.Max(bmp.Width, bmp.Height));
        // Groesser angefragt: die grosse Version (hier das Bild in voller Groesse, nie vergroessert)
        using (var bmp = SkiaSharp.SKBitmap.Decode(await c.GetByteArrayAsync($"/index.php/core/preview?fileId={foto.FileId}&x=1024&y=1024&a=true")))
            Assert.Equal(800, bmp.Width);

        // Durchsichtig bleibt durchsichtig
        var glas = await c.GetAsync("/index.php/core/preview.png?file=" + Uri.EscapeDataString("/bilder/glas.png") + "&x=64&y=64");
        Assert.Equal(HttpStatusCode.OK, glas.StatusCode);
        Assert.Equal("image/png", glas.Content.Headers.ContentType!.MediaType);

        // Adresse der Handy-Apps; wer WebP nimmt, bekommt WebP
        var req = new HttpRequestMessage(HttpMethod.Get, "/index.php/apps/files/api/v1/thumbnail/128/128/bilder/foto.png");
        req.Headers.Accept.ParseAdd("image/webp");
        var webp = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, webp.StatusCode);
        Assert.Equal("image/webp", webp.Content.Headers.ContentType!.MediaType);

        // Nichts zum Zeigen, Ordner, unbekannt, ungueltiger Pfad, ohne Anmeldung
        await Send(c, "PUT", Base + "bilder/liesmich.txt", "text");
        var txt = await Self(c, "bilder/liesmich.txt");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/index.php/core/preview?fileId={txt.Id}&x=256&y=256")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/index.php/core/preview?fileId={(await Self(c, "bilder")).Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/index.php/core/preview?fileId=99999999ocdmoffice00")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/index.php/core/preview.png?file=" + Uri.EscapeDataString("../bilder/foto.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Client().GetAsync($"/index.php/core/preview?fileId={foto.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Client("falsch").GetAsync($"/index.php/core/preview?fileId={foto.Id}")).StatusCode);
    }

    [Fact]
    public async Task Vorschau_aus_dem_Abgleichordner_von_notizi_gibt_es_nicht()
    {
        var c = _fx.Client(_fx.AppPassword);
        await Send(c, "MKCOL", $"/dav/files/{NextcloudServerFixture.Login}/notizisync");
        Assert.True((await PutBytes(c, $"/dav/files/{NextcloudServerFixture.Login}/notizisync/bild.png", Png(50, 50))).IsSuccessStatusCode);
        var space = Space();
        var f = FileStore.Get(space.Db, "notizisync/bild.png")!;

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/index.php/core/preview?fileId={DavServer.OcId(f)}&x=256&y=256&a=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/index.php/core/preview.png?file=/notizisync/bild.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/index.php/apps/files/api/v1/thumbnail/256/256/notizisync/bild.png")).StatusCode);
        // Auch die Weboberflaeche nicht
        var web = _fx.Factory.CreateClient();
        Assert.True((await web.PostAsJsonAsync("/api/auth/login", new { login = NextcloudServerFixture.Login, password = NextcloudServerFixture.Password })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync("/api/files/thumb?path=notizisync/bild.png")).StatusCode);

        // Ein notizi-Zugang bekommt ueberhaupt keine Vorschau
        Assert.True((await PutBytes(c, Base + "frei.png", Png(50, 50))).IsSuccessStatusCode);
        var notizi = AppPasswords.Create(space.Db, "notizi", AppPasswords.ScopeNotizi);
        try { Assert.Equal(HttpStatusCode.Forbidden, (await _fx.Client(notizi).GetAsync("/index.php/core/preview.png?file=/frei.png")).StatusCode); }
        // Sonst blendet /dav den Ordner fuer die anderen Tests aus (eigener notizi-Zugang in Gebrauch)
        finally { AppPasswords.Delete(space.Db, AppPasswords.List(space.Db).Single(e => e.Scope == AppPasswords.ScopeNotizi).Id); }
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/index.php/core/preview.png?file=/frei.png")).StatusCode);
    }
}

/// <summary>ETag-Weitergabe und Datei-Nummern direkt an der Ablage.</summary>
public class FileStoreNextcloudTests
{
    private static UserDb NewDb() => new(Path.Combine(Directory.CreateTempSubdirectory("dmfs").FullName, "u.db"), new byte[32], "t");

    [Fact]
    public void Aenderung_tief_unten_gibt_allen_Ordnern_darueber_neue_ETags()
    {
        var db = NewDb();
        FileStore.EnsureDir(db, "a/b/c");
        FileStore.EnsureDir(db, "x");
        FileStore.Put(db, "a/b/c/f.txt", "1"u8.ToArray(), "text/plain");
        string E(string p) => FileStore.Get(db, p)!.Etag;
        var before = new[] { "", "a", "a/b", "a/b/c", "x" }.ToDictionary(p => p, E);
        FileStore.Put(db, "a/b/c/f.txt", "2"u8.ToArray(), "text/plain");
        foreach (var p in new[] { "", "a", "a/b", "a/b/c" }) Assert.NotEqual(before[p], E(p));
        Assert.Equal(before["x"], E("x"));
    }

    [Fact]
    public void Nummern_bleiben_beim_Verschieben_und_Ueberschreiben_und_kommen_nie_wieder()
    {
        var db = NewDb();
        FileStore.Put(db, "f.txt", "1"u8.ToArray(), null);
        var id = FileStore.Get(db, "f.txt")!.FileId;
        Assert.True(id > 0);
        FileStore.Put(db, "f.txt", "2"u8.ToArray(), null);
        Assert.Equal(id, FileStore.Get(db, "f.txt")!.FileId);
        FileStore.MoveOrCopy(db, "f.txt", "g.txt", false, copy: false);
        Assert.Equal(id, FileStore.Get(db, "g.txt")!.FileId);
        FileStore.Delete(db, "g.txt");
        FileStore.Put(db, "h.txt", "3"u8.ToArray(), null);
        Assert.True(FileStore.Get(db, "h.txt")!.FileId > id);
    }

    [Fact]
    public void Aenderungszeit_vom_Client_wird_uebernommen()
    {
        var db = NewDb();
        var t = new DateTime(2025, 5, 4, 3, 2, 1, DateTimeKind.Utc);
        FileStore.Put(db, "f.txt", "1"u8.ToArray(), null, mtime: t);
        Assert.Equal(t, FileStore.Get(db, "f.txt")!.Modified.ToUniversalTime());
    }
}
