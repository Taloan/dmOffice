using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>pCloud als externer Ordner: der Treiber gegen einen Fake der HTTP-JSON-API. Kein echtes Konto, keine echten Tokens.</summary>
public class PCloudDriverTests
{
    private static HttpResponseMessage Ok(string json) => FakeOneDrive.Json(HttpStatusCode.OK, json);

    private static (PCloudDriver Driver, FakeOneDrive Fake, List<TimeSpan> Waits) Make(string host = PCloudDriver.HostEu, string root = "Fotos")
    {
        var fake = new FakeOneDrive();
        var waits = new List<TimeSpan>();
        var d = new PCloudDriver(new HttpClient(fake), "TOKEN", host, root) { Wait = (t, _) => { waits.Add(t); return Task.CompletedTask; } };
        return (d, fake, waits);
    }

    [Fact]
    public async Task Auflisten_beim_Server_der_Region()
    {
        var (d, fake, _) = Make();
        fake.Answer = _ => Ok("""
            {"result":0,"metadata":{"name":"2026","isfolder":true,"folderid":5,"contents":[
              {"name":"Urlaub","isfolder":true,"folderid":7,"modified":"Wed, 02 Oct 2013 13:23:35 +0000"},
              {"name":"a b.jpg","isfolder":false,"fileid":9,"size":1234,"contenttype":"image/jpeg","modified":"Thu, 19 Sep 2013 07:31:46 +0000"}]}}
            """);
        var list = await d.ListAsync("2026", CancellationToken.None);
        Assert.Equal(["Urlaub", "a b.jpg"], list.Select(e => e.Name));
        Assert.True(list[0].IsDir);
        Assert.Equal(1234, list[1].Size);
        Assert.Equal("image/jpeg", list[1].ContentType);
        Assert.Equal(new DateTime(2013, 9, 19, 7, 31, 46, DateTimeKind.Utc), list[1].Modified);
        Assert.Equal("https://eapi.pcloud.com/listfolder?path=/Fotos/2026", fake.Requests[0].Url);
        Assert.Equal("Bearer TOKEN", fake.Requests[0].Auth);
    }

    [Fact]
    public void Nur_die_Server_von_pCloud()
    {
        Assert.Equal(PCloudDriver.HostEu, PCloudDriver.HostFor("eapi.pcloud.com", "2"));
        Assert.Equal(PCloudDriver.HostUs, PCloudDriver.HostFor("API.pcloud.com", null));
        // Ohne hostname: locationid 1 USA, 2 Europa
        Assert.Equal(PCloudDriver.HostEu, PCloudDriver.HostFor(null, "2"));
        Assert.Equal(PCloudDriver.HostUs, PCloudDriver.HostFor("", "1"));
        Assert.Throws<ArgumentException>(() => PCloudDriver.HostFor("evil.example.org", "2"));
        Assert.Throws<ArgumentException>(() => new PCloudDriver(new HttpClient(), "T", "api.pcloud.com.evil.org", ""));
        // Kein Weg aus der Wurzel
        Assert.Throws<ArgumentException>(() => new PCloudDriver(new HttpClient(), "T", PCloudDriver.HostUs, "../x"));
    }

    [Theory]
    [InlineData(2005, typeof(KeyNotFoundException))]
    [InlineData(2009, typeof(KeyNotFoundException))]
    [InlineData(2094, typeof(UnauthorizedAccessException))]
    [InlineData(2003, typeof(UnauthorizedAccessException))]
    [InlineData(2008, typeof(IOException))]
    [InlineData(2001, typeof(ArgumentException))]
    [InlineData(1234, typeof(IOException))]
    public async Task Fehlercodes_als_Meldung(int code, Type type)
    {
        var (d, fake, _) = Make();
        fake.Answer = _ => Ok($$"""{"result":{{code}},"error":"x"}""");
        var e = await Assert.ThrowsAnyAsync<Exception>(() => d.ListAsync("", CancellationToken.None));
        Assert.IsType(type, e);
        Assert.StartsWith("Auflisten: ", e.Message);
    }

    [Fact]
    public async Task Drosselung_und_Serverfehler_werden_wiederholt()
    {
        var (d, fake, waits) = Make();
        var n = 0;
        fake.Answer = _ => ++n switch
        {
            1 => Ok("""{"result":4000,"error":"Too many login tries from this IP address."}"""),
            2 => FakeOneDrive.Json(HttpStatusCode.ServiceUnavailable, ""),
            3 => Ok("""{"result":5000,"error":"Internal error. Try again later."}"""),
            _ => Ok("""{"result":0,"metadata":{"contents":[]}}"""),
        };
        Assert.Empty(await d.ListAsync("", CancellationToken.None));
        Assert.Equal(4, fake.Requests.Count);
        Assert.Equal(3, waits.Count);

        // Dauerhaft gedrosselt: nach fünf Versuchen eine Meldung
        fake.Requests.Clear();
        fake.Answer = _ => Ok("""{"result":4000,"error":"x"}""");
        var e = await Assert.ThrowsAsync<IOException>(() => d.ListAsync("", CancellationToken.None));
        Assert.Contains("bremst", e.Message);
        Assert.Equal(5, fake.Requests.Count);
    }

    [Fact]
    public async Task Lesen_ueber_getfilelink_mit_Ausschnitt()
    {
        var (d, fake, _) = Make();
        fake.Answer = r => r.Url.StartsWith("https://eapi.pcloud.com/getfilelink")
            ? Ok("""{"result":0,"hosts":["c12.pcloud.com","c13.pcloud.com"],"path":"/cBZx/a%20b.jpg","expires":"Thu, 19 Sep 2030 07:31:46 +0000"}""")
            : new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3, 4]) };
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await d.ReadRangeAsync("a b.jpg", 10, 4, CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await d.ReadRangeAsync("a b.jpg", 14, 4, CancellationToken.None));
        // Die Adresse wird gemerkt: einmal getfilelink, zweimal Herunterladen
        Assert.Single(fake.Requests, r => r.Url.Contains("/getfilelink"));
        var get = fake.Requests.Where(r => r.Url.StartsWith("https://c12.pcloud.com/")).ToList();
        Assert.Equal(2, get.Count);
        Assert.Equal("bytes=10-13", get[0].Range);
        Assert.Contains("path=/Fotos/a b.jpg", fake.Requests[0].Url);
    }

    [Fact]
    public async Task Fremde_Adresse_zum_Herunterladen_wird_abgelehnt()
    {
        var (d, fake, _) = Make();
        fake.Answer = _ => Ok("""{"result":0,"hosts":["evil.example.org"],"path":"/x"}""");
        await Assert.ThrowsAsync<IOException>(() => d.OpenReadAsync("a.jpg", CancellationToken.None));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Kleine_Datei_in_einem_Stueck()
    {
        var (d, fake, _) = Make();
        fake.Answer = r => r.Url.Contains("/createfolderifnotexists")
            ? Ok("""{"result":0,"metadata":{"folderid":42,"isfolder":true,"name":"x"}}""")
            : Ok("""{"result":0,"fileids":[1],"metadata":[{"fileid":1}]}""");
        await d.WriteAsync("2026/Neu/a.txt", new MemoryStream(Encoding.UTF8.GetBytes("Inhalt")), CancellationToken.None);
        Assert.Equal(["https://eapi.pcloud.com/createfolderifnotexists?path=/Fotos/2026", "https://eapi.pcloud.com/createfolderifnotexists?path=/Fotos/2026/Neu"],
            fake.Requests.Take(2).Select(r => r.Url));
        var put = fake.Requests.Last();
        Assert.Equal("PUT", put.Method);
        Assert.Equal("https://eapi.pcloud.com/uploadfile?folderid=42&filename=a.txt&nopartial=1", put.Url);
        Assert.Equal("Inhalt", Encoding.UTF8.GetString(put.Body));
    }

    [Fact]
    public async Task Hochladen_wird_nach_Serverfehler_wiederholt()
    {
        var (d, fake, _) = Make(root: "");
        var uploads = 0;
        fake.Answer = r => r.Url.Contains("/uploadfile")
            ? ++uploads == 1 ? Ok("""{"result":5001,"error":"Internal upload error."}""") : Ok("""{"result":0}""")
            : Ok("{}");
        await d.WriteAsync("a.txt", new MemoryStream(Encoding.UTF8.GetBytes("abc")), CancellationToken.None);
        var puts = fake.Requests.Where(r => r.Method == "PUT").ToList();
        Assert.Equal(2, puts.Count);
        // Zweiter Versuch mit dem ganzen Inhalt, Wurzel des Kontos ist Ordner 0
        Assert.All(puts, p => Assert.Equal("abc", Encoding.UTF8.GetString(p.Body)));
        Assert.Contains("folderid=0", puts[0].Url);
    }

    [Fact]
    public async Task Grosse_Datei_in_Teilen()
    {
        var (d, fake, _) = Make();
        d.Simple = 10; d.Chunk = 4;
        var writes = 0;
        fake.Answer = r => r.Url switch
        {
            var u when u.Contains("/listfolder") => Ok("""{"result":0,"metadata":{"folderid":5,"isfolder":true}}"""),
            var u when u.Contains("/upload_create") => Ok("""{"result":0,"uploadid":777}"""),
            // Der zweite Teil scheitert einmal - nur er wird wiederholt
            var u when u.Contains("/upload_write") => ++writes == 2 ? Ok("""{"result":5000}""") : Ok("""{"result":0}"""),
            var u when u.Contains("/upload_save") => Ok("""{"result":0,"metadata":{"fileid":3}}"""),
            _ => Ok("""{"result":1000}"""),
        };
        var data = Enumerable.Range(0, 11).Select(i => (byte)i).ToArray();
        // Ohne Länge (wie ein Formular) - landet erst auf der Platte
        await d.WriteAsync("gross.bin", new NoSeek(data), CancellationToken.None);
        var parts = fake.Requests.Where(r => r.Url.Contains("/upload_write")).ToList();
        Assert.Equal(["0", "4", "4", "8"], parts.Select(p => p.Url[(p.Url.IndexOf("uploadoffset=") + 13)..]));
        Assert.Equal(data, parts[0].Body.Concat(parts[2].Body).Concat(parts[3].Body));
        Assert.All(parts, p => Assert.Contains("uploadid=777", p.Url));
        Assert.Equal("https://eapi.pcloud.com/upload_save?uploadid=777&folderid=5&name=gross.bin", fake.Requests.Last().Url);
        Assert.DoesNotContain(fake.Requests, r => r.Url.Contains("/uploadfile") || r.Url.Contains("/upload_delete"));
    }

    [Fact]
    public async Task Abgebrochenes_Hochladen_verwirft_die_Sitzung()
    {
        var (d, fake, _) = Make(root: "");
        d.Simple = 2; d.Chunk = 2;
        fake.Answer = r => r.Url switch
        {
            var u when u.Contains("/upload_create") => Ok("""{"result":0,"uploadid":9}"""),
            var u when u.Contains("/upload_write") => Ok("""{"result":2008,"error":"User is over quota."}"""),
            _ => Ok("""{"result":0}"""),
        };
        var e = await Assert.ThrowsAsync<IOException>(() => d.WriteAsync("x.bin", new MemoryStream(new byte[5]), CancellationToken.None));
        Assert.Contains("voll", e.Message);
        Assert.Contains(fake.Requests, r => r.Url == "https://eapi.pcloud.com/upload_delete?uploadid=9");
    }

    [Fact]
    public async Task Ohne_Hochladen_in_Teilen_in_einem_Stueck()
    {
        var (d, fake, _) = Make(root: "");
        d.Simple = 2;
        fake.Answer = r => r.Url.Contains("/upload_create") ? Ok("""{"result":1001,"error":"Invalid method."}""") : Ok("""{"result":0}""");
        await d.WriteAsync("x.bin", new MemoryStream([1, 2, 3, 4, 5]), CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, fake.Requests.Single(r => r.Url.Contains("/uploadfile")).Body);
    }

    [Fact]
    public async Task Verschieben_Loeschen_und_Platz()
    {
        var (d, fake, _) = Make();
        var stats = new Dictionary<string, string>
        {
            ["/Fotos/alt"] = """{"result":0,"metadata":{"name":"alt","isfolder":true,"folderid":3}}""",
            ["/Fotos/a.jpg"] = """{"result":0,"metadata":{"name":"a.jpg","isfolder":false,"fileid":4,"size":1}}""",
        };
        fake.Answer = r =>
        {
            var u = r.Url;
            if (u.Contains("/stat?") || (u.Contains("/listfolder?") && u.Contains("nofiles")))
                return Ok(stats.TryGetValue(u[(u.IndexOf("path=") + 5)..].Split('&')[0], out var j) ? j : """{"result":2009,"error":"File not found."}""");
            if (u.Contains("/userinfo")) return Ok("""{"result":0,"email":"a@example.org","quota":10737418240,"usedquota":1073741824}""");
            return Ok("""{"result":0,"metadata":{"folderid":8,"isfolder":true}}""");
        };
        await d.MoveAsync("alt", "Archiv/neu", CancellationToken.None);
        Assert.Contains(fake.Requests, r => r.Url == "https://eapi.pcloud.com/renamefolder?path=/Fotos/alt&topath=/Fotos/Archiv/neu");
        await d.MoveAsync("a.jpg", "b.jpg", CancellationToken.None);
        Assert.Contains(fake.Requests, r => r.Url == "https://eapi.pcloud.com/renamefile?path=/Fotos/a.jpg&topath=/Fotos/b.jpg");
        // Ein vorhandenes Ziel ersetzt pCloud stillschweigend - hier gemeldet
        await Assert.ThrowsAsync<IOException>(() => d.MoveAsync("a.jpg", "alt", CancellationToken.None));

        await d.DeleteAsync("alt", CancellationToken.None);
        await d.DeleteAsync("a.jpg", CancellationToken.None);
        await d.DeleteAsync("gibtsnicht", CancellationToken.None);
        Assert.Contains(fake.Requests, r => r.Url == "https://eapi.pcloud.com/deletefolderrecursive?path=/Fotos/alt");
        Assert.Contains(fake.Requests, r => r.Url == "https://eapi.pcloud.com/deletefile?path=/Fotos/a.jpg");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.DeleteAsync("", CancellationToken.None));

        var space = await d.SpaceAsync(CancellationToken.None);
        Assert.Equal(1073741824, space!.Used);
        Assert.Equal(9663676416, space.Free);
        Assert.Null(await d.StatAsync("gibtsnicht", CancellationToken.None));
        Assert.True((await d.StatAsync("alt", CancellationToken.None))!.IsDir);
    }

    private sealed class NoSeek(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

/// <summary>pCloud in der Dateiablage: Schalter des Admins, Anmeldung mit Rücksprung (Region EU und USA), Ordnerauswahl,
/// Einbinden und Nutzen über die API.</summary>
[Collection("server")]
public class PCloudMountTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public PCloudMountTests(FilesServerFixture fx) => _fx = fx;

    private static HttpResponseMessage Ok(string json) => FakeOneDrive.Json(HttpStatusCode.OK, json);

    [Fact]
    public async Task Anmelden_waehlen_einbinden()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var space = _fx.Factory.Services.GetRequiredService<Spaces>().Get(users.ByLogin(FilesServerFixture.Login)!.Id);
        var fake = new FakeOneDrive();
        var (http, wait) = (PCloudFolders.Http, PCloudFolders.Wait);
        var before = space.Db.GetJson<List<UserMount>>(ExternalFolders.UserKey);
        try
        {
            space.Db.SetJson<PCloudFolders.PCloudApp?>(PCloudFolders.AppKey, null);
            PCloudFolders.Http = new HttpClient(fake);
            PCloudFolders.Wait = (_, _) => Task.CompletedTask;
            fake.Answer = r => r.Url switch
            {
                var u when u.EndsWith("/oauth2_token") => Ok("""{"result":0,"access_token":"PTOKEN","token_type":"bearer","uid":1}"""),
                var u when u.Contains("/userinfo") => Ok("""{"result":0,"email":"anna@example.org"}"""),
                var u when u.Contains("/listfolder") && u.Contains("nofiles") => Ok("""{"result":0,"metadata":{"contents":[{"name":"Fotos","isfolder":true},{"name":"Musik","isfolder":true}]}}"""),
                var u when u.Contains("/listfolder") => Ok("""{"result":0,"metadata":{"contents":[{"name":"Bild.jpg","isfolder":false,"size":3}]}}"""),
                _ => Ok("""{"result":2009}"""),
            };
            var c = _fx.Client();
            var anon = _fx.Factory.CreateClient();

            // Aus: nicht angeboten und gesperrt
            config.Update(s => s.ExternalPCloudForUsers = false);
            var mounts = await c.GetFromJsonAsync<JsonElement>("/api/files/external/mounts");
            Assert.False(mounts.GetProperty("pCloud").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/external/pcloud/start", new { clientId = "CID", clientSecret = "CSECRET" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/files/external/pcloud/app")).StatusCode);

            config.Update(s => s.ExternalPCloudForUsers = true);
            mounts = await c.GetFromJsonAsync<JsonElement>("/api/files/external/mounts");
            Assert.True(mounts.GetProperty("pCloud").GetBoolean());
            Assert.True(mounts.GetProperty("canAdd").GetBoolean());

            // Ohne eigene App keine Anmeldung
            var app = await c.GetFromJsonAsync<JsonElement>("/api/files/external/pcloud/app");
            Assert.Equal("", app.GetProperty("clientId").GetString());
            Assert.False(app.GetProperty("secretSet").GetBoolean());
            Assert.EndsWith("/api/files/external/pcloud/callback", app.GetProperty("redirect").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/external/pcloud/start", new { clientId = "CID" })).StatusCode);

            // Anmeldung beginnen: die eigene App wird gespeichert, das Secret verschlüsselt in der Datenbank des Benutzers
            var start = await (await c.PostAsJsonAsync("/api/files/external/pcloud/start", new { clientId = " CID ", clientSecret = "CSECRET" })).Content.ReadFromJsonAsync<JsonElement>();
            app = await c.GetFromJsonAsync<JsonElement>("/api/files/external/pcloud/app");
            Assert.Equal("CID", app.GetProperty("clientId").GetString());
            Assert.True(app.GetProperty("secretSet").GetBoolean());
            Assert.DoesNotContain("CSECRET", app.GetRawText());
            Assert.Equal("CSECRET", space.Db.GetJson<PCloudFolders.PCloudApp>(PCloudFolders.AppKey).Secret);
            var flow = start.GetProperty("flow").GetString()!;
            var url = start.GetProperty("url").GetString()!;
            Assert.StartsWith("https://my.pcloud.com/oauth2/authorize?client_id=CID&response_type=code&redirect_uri=", url);
            Assert.Contains(Uri.EscapeDataString("/api/files/external/pcloud/callback"), url);
            Assert.EndsWith("&state=" + flow, url);
            Assert.Equal("pending", (await c.GetFromJsonAsync<JsonElement>("/api/files/external/pcloud/status?flow=" + flow)).GetProperty("state").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/files/external/pcloud/folders?flow=" + flow)).StatusCode);

            // Fremde Adresse als Server: abgelehnt, nichts gesendet
            var page = await anon.GetAsync($"/api/files/external/pcloud/callback?code=C1&state={flow}&locationid=2&hostname=evil.example.org");
            Assert.Contains("failed", await page.Content.ReadAsStringAsync());
            Assert.Empty(fake.Requests);
            Assert.Equal("error", (await c.GetFromJsonAsync<JsonElement>("/api/files/external/pcloud/status?flow=" + flow)).GetProperty("state").GetString());

            // Neue Anmeldung, Rücksprung für ein Konto in Europa (ohne Anmeldung im Browser erreichbar)
            flow = (await (await c.PostAsJsonAsync("/api/files/external/pcloud/start", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flow").GetString()!;
            Assert.Contains("failed", await (await anon.GetAsync("/api/files/external/pcloud/callback?code=C1&state=falsch&hostname=eapi.pcloud.com")).Content.ReadAsStringAsync());
            var ok = await anon.GetAsync($"/api/files/external/pcloud/callback?code=C1&state={flow}&locationid=2&hostname=eapi.pcloud.com");
            Assert.Contains("Signed in to pCloud", await ok.Content.ReadAsStringAsync());
            var token = fake.Requests.Single(r => r.Url.EndsWith("/oauth2_token"));
            Assert.Equal("https://eapi.pcloud.com/oauth2_token", token.Url);
            Assert.Equal("POST", token.Method);
            var form = Encoding.UTF8.GetString(token.Body);
            Assert.Contains("client_id=CID", form);
            Assert.Contains("client_secret=CSECRET", form);
            Assert.Contains("code=C1", form);
            // Der Code gilt nur einmal
            await anon.GetAsync($"/api/files/external/pcloud/callback?code=C1&state={flow}&hostname=eapi.pcloud.com");
            Assert.Single(fake.Requests, r => r.Url.EndsWith("/oauth2_token"));

            var status = await c.GetFromJsonAsync<JsonElement>("/api/files/external/pcloud/status?flow=" + flow);
            Assert.Equal("ok", status.GetProperty("state").GetString());
            Assert.Equal("anna@example.org", status.GetProperty("account").GetString());
            // Das Token geht nie an den Browser
            Assert.DoesNotContain("PTOKEN", status.GetRawText());

            var folders = await c.GetFromJsonAsync<JsonElement>("/api/files/external/pcloud/folders?flow=" + flow);
            Assert.Equal(["Fotos", "Musik"], folders.GetProperty("folders").EnumerateArray().Select(x => x.GetString()));

            var added = await c.PostAsJsonAsync("/api/files/external/pcloud", new { flow, name = "Meine Fotos", path = "Fotos" });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var mount = await added.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pcloud", mount.GetProperty("kind").GetString());
            Assert.DoesNotContain("PTOKEN", mount.GetRawText());
            var saved = space.Db.GetJson<List<UserMount>>(ExternalFolders.UserKey).Single(m => m.Kind == UserMount.PCloud);
            Assert.Equal(("PTOKEN", "eapi.pcloud.com", "Fotos"), (saved.Token, saved.Host, saved.Folder));
            Assert.Equal("anna@example.org · pCloud/Fotos", saved.Account);
            // Die Anmeldung ist verbraucht
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/files/external/pcloud/status?flow=" + flow)).StatusCode);

            // Nutzen: Dateiliste über den Server der Region, mit dem Token
            fake.Requests.Clear();
            var list = await c.GetFromJsonAsync<JsonElement>($"/api/files/external/list?mount={saved.Id}&fresh=true");
            Assert.Equal("Bild.jpg", list.GetProperty("items")[0].GetProperty("name").GetString());
            Assert.Contains(fake.Requests, r => r.Url == "https://eapi.pcloud.com/listfolder?path=/Fotos" && r.Auth == "Bearer PTOKEN");

            // Konto in den USA: ohne hostname über locationid
            fake.Requests.Clear();
            flow = (await (await c.PostAsJsonAsync("/api/files/external/pcloud/start", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flow").GetString()!;
            await anon.GetAsync($"/api/files/external/pcloud/callback?code=C2&state={flow}&locationid=1");
            Assert.Equal("https://api.pcloud.com/oauth2_token", fake.Requests[0].Url);

            // Neu anmelden (Externe Ziele → Bearbeiten): dasselbe Ziel, neues Konto und neuer Ordner
            var replaced = await c.PostAsJsonAsync("/api/files/external/pcloud", new { flow, name = "Musik", path = "Musik", replace = saved.Id });
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
            var again = space.Db.GetJson<List<UserMount>>(ExternalFolders.UserKey).Where(m => m.Kind == UserMount.PCloud).ToList();
            Assert.Equal(saved.Id, Assert.Single(again).Id);
            Assert.Equal(("Musik", "api.pcloud.com", "Musik"), (again[0].Name, again[0].Host, again[0].Folder));
            // Ein unbekanntes Ziel lässt sich nicht neu anmelden
            flow = (await (await c.PostAsJsonAsync("/api/files/external/pcloud/start", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flow").GetString()!;
            await anon.GetAsync($"/api/files/external/pcloud/callback?code=C3&state={flow}&locationid=1");
            Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/files/external/pcloud", new { flow, name = "X", replace = "gibtsnicht" })).StatusCode);

            // Schalter aus: der Ordner ist gesperrt
            config.Update(s => s.ExternalPCloudForUsers = false);
            Assert.DoesNotContain((await c.GetFromJsonAsync<JsonElement>("/api/files/external/mounts")).GetProperty("mounts").EnumerateArray(), m => m.GetProperty("id").GetString() == saved.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/files/external/list?mount={saved.Id}&fresh=true")).StatusCode);
        }
        finally
        {
            config.Update(s => s.ExternalPCloudForUsers = false);
            space.Db.SetJson<PCloudFolders.PCloudApp?>(PCloudFolders.AppKey, null);
            space.Db.SetJson(ExternalFolders.UserKey, before);
            (PCloudFolders.Http, PCloudFolders.Wait) = (http, wait);
        }
    }

    [Fact]
    public async Task Abgelehnte_Anmeldung_wird_gemeldet()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var fake = new FakeOneDrive { Answer = _ => Ok("""{"result":2012,"error":"Invalid 'code' provided."}""") };
        var http = PCloudFolders.Http;
        try
        {
            PCloudFolders.Http = new HttpClient(fake);
            config.Update(s => s.ExternalPCloudForUsers = true);
            var c = _fx.Client();
            var flow = (await (await c.PostAsJsonAsync("/api/files/external/pcloud/start", new { clientId = "CID", clientSecret = "FALSCH" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flow").GetString()!;
            await _fx.Factory.CreateClient().GetAsync($"/api/files/external/pcloud/callback?code=C&state={flow}&hostname=api.pcloud.com");
            var status = await c.GetFromJsonAsync<JsonElement>("/api/files/external/pcloud/status?flow=" + flow);
            Assert.Equal("error", status.GetProperty("state").GetString());
            Assert.Contains("Client-ID und Client-Secret", status.GetProperty("error").GetString());

            // Abgebrochen bei pCloud
            flow = (await (await c.PostAsJsonAsync("/api/files/external/pcloud/start", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flow").GetString()!;
            await _fx.Factory.CreateClient().GetAsync($"/api/files/external/pcloud/callback?error=access_denied&state={flow}");
            status = await c.GetFromJsonAsync<JsonElement>("/api/files/external/pcloud/status?flow=" + flow);
            Assert.Equal("Die Anmeldung bei pCloud wurde abgebrochen.", status.GetProperty("error").GetString());
        }
        finally
        {
            config.Update(s => s.ExternalPCloudForUsers = false);
            PCloudFolders.Http = http;
        }
    }
}

/// <summary>Verwaltung → Storage → Externe Ordner: der Schalter für pCloud (den App-Zugang trägt jeder Benutzer selbst ein).</summary>
[Collection("server")]
public class PCloudAdminTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PCloudAdminTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Admin_schaltet_pCloud()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var admin = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        Assert.True((await admin.PostAsJsonAsync("/api/auth/login", new { login = "anna", password = "geheim-123456" })).IsSuccessStatusCode);
        var folders = config.Current.ExternalFolders;
        try
        {
            Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/admin/external")).GetProperty("pCloudForUsers").GetBoolean());
            var res = await admin.PutAsJsonAsync("/api/admin/external", new { folders = Array.Empty<object>(), webDavForUsers = false, webDavPrivate = false, pCloudForUsers = true });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.True(config.Current.ExternalPCloudForUsers);
            Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/admin/external")).GetProperty("pCloudForUsers").GetBoolean());
            // Ohne das Feld (etwa der Schalter für WebDAV) bleibt er stehen
            await admin.PutAsJsonAsync("/api/admin/external", new { folders = Array.Empty<object>(), webDavForUsers = true, webDavPrivate = false });
            Assert.True(config.Current.ExternalPCloudForUsers);
            await admin.PutAsJsonAsync("/api/admin/external", new { folders = Array.Empty<object>(), webDavForUsers = false, webDavPrivate = false, pCloudForUsers = false });
            Assert.False(config.Current.ExternalPCloudForUsers);
        }
        finally { config.Update(s => { s.ExternalPCloudForUsers = false; s.ExternalWebDavForUsers = false; s.ExternalFolders = folders; }); }
    }
}
