using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Mail;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Antwortet nach einer Regel je Anfrage und merkt sich die Anfragen - Microsoft Graph und die Adressen zum
/// Herunter- und Hochladen. Kein echtes Konto, keine echten Tokens.</summary>
internal sealed class FakeOneDrive : HttpMessageHandler
{
    public sealed record Req(string Method, string Url, string? Auth, string? Range, string? ContentRange, byte[] Body);
    public readonly List<Req> Requests = new();
    public Func<Req, HttpResponseMessage> Answer = _ => Json(HttpStatusCode.NotFound, "{}");

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var r = new Req(request.Method.Method, Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri), request.Headers.Authorization?.ToString(),
            request.Headers.Range?.ToString(), request.Content?.Headers.ContentRange?.ToString(), body);
        lock (Requests) Requests.Add(r);
        return Answer(r);
    }
}

/// <summary>OneDrive als externer Ordner: der Treiber mit gewähltem Ordner als Wurzel gegen einen Graph-Fake.</summary>
public class OneDriveFolderDriverTests
{
    public OneDriveFolderDriverTests() => OneDriveDriver.Wait = (_, _) => Task.CompletedTask;

    private const string G = "https://graph.microsoft.com/v1.0/drives/D1/items/F1";

    private static (OneDriveDriver Driver, FakeOneDrive Fake) Make()
    {
        var fake = new FakeOneDrive();
        return (new OneDriveDriver(_ => Task.FromResult("TOKEN"), new HttpClient(fake), "D1", "F1"), fake);
    }

    [Fact]
    public async Task Auflisten_mit_Pfad_und_Folgeseiten()
    {
        var (d, fake) = Make();
        fake.Answer = r => r.Url.Contains("skiptoken")
            ? FakeOneDrive.Json(HttpStatusCode.OK, """{"value":[{"id":"3","name":"c.pdf","size":30,"file":{"mimeType":"application/pdf"},"lastModifiedDateTime":"2026-10-01T10:00:00Z"}]}""")
            : FakeOneDrive.Json(HttpStatusCode.OK, """
              {"value":[{"id":"1","name":"Belege","folder":{"childCount":2}},{"id":"2","name":"a b.txt","size":12,"file":{}}],
               "@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/D1/items/F1:/Steuer 2026:/children?$skiptoken=X"}
              """);
        var list = await d.ListAsync("Steuer 2026", CancellationToken.None);
        Assert.Equal(["Belege", "a b.txt", "c.pdf"], list.Select(e => e.Name));
        Assert.True(list[0].IsDir);
        Assert.Equal(12, list[1].Size);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), list[2].Modified);
        Assert.StartsWith(G + ":/Steuer 2026:/children?", fake.Requests[0].Url);
        Assert.All(fake.Requests, r => Assert.Equal("Bearer TOKEN", r.Auth));
    }

    [Fact]
    public async Task Folgeseite_nur_von_Graph()
    {
        var (d, fake) = Make();
        fake.Answer = _ => FakeOneDrive.Json(HttpStatusCode.OK, """{"value":[],"@odata.nextLink":"https://example.org/stehlen"}""");
        await Assert.ThrowsAsync<IOException>(() => d.ListAsync("", CancellationToken.None));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Nicht_vorhanden_ist_null_und_Wurzel_bleibt()
    {
        var (d, fake) = Make();
        fake.Answer = _ => FakeOneDrive.Json(HttpStatusCode.NotFound, """{"error":{"code":"itemNotFound","message":"x"}}""");
        Assert.Null(await d.StatAsync("weg.txt", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => d.ListAsync("weg", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.DeleteAsync("", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.MoveAsync("", "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => d.StatAsync("../x", CancellationToken.None));
        // Schon weg ist beim Löschen kein Fehler
        await d.DeleteAsync("weg.txt", CancellationToken.None);
    }

    [Fact]
    public async Task Lesen_ganz_und_mit_Ausschnitt()
    {
        var (d, fake) = Make();
        var content = Encoding.UTF8.GetBytes("Hallo OneDrive");
        fake.Answer = r => r.Range != null
            ? new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content[6..14]) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        await using (var s = await d.OpenReadAsync("gruss.txt", CancellationToken.None))
            Assert.Equal("Hallo OneDrive", await new StreamReader(s).ReadToEndAsync());
        Assert.Equal("OneDrive", Encoding.UTF8.GetString(await d.ReadRangeAsync("gruss.txt", 6, 8, CancellationToken.None)));
        Assert.All(fake.Requests, r => Assert.Equal(G + ":/gruss.txt:/content", r.Url));
        Assert.Equal("bytes=6-13", fake.Requests[1].Range);
    }

    [Fact]
    public async Task Kleine_Datei_in_einem_Stueck()
    {
        var (d, fake) = Make();
        fake.Answer = r => r.Method == "POST" ? FakeOneDrive.Json(HttpStatusCode.Conflict, """{"error":{"code":"nameAlreadyExists"}}""") : FakeOneDrive.Json(HttpStatusCode.Created, "{}");
        await d.WriteAsync("Neu/notiz.txt", new MemoryStream(Encoding.UTF8.GetBytes("klein")), CancellationToken.None);
        // Ordner davor angelegt (409: gibt es schon), dann PUT auf /content
        Assert.Equal("POST", fake.Requests[0].Method);
        Assert.Equal(G + "/children", fake.Requests[0].Url);
        Assert.Contains("\"name\":\"Neu\"", Encoding.UTF8.GetString(fake.Requests[0].Body));
        var put = fake.Requests[1];
        Assert.Equal("PUT", put.Method);
        Assert.Equal(G + ":/Neu/notiz.txt:/content", put.Url);
        Assert.Equal("klein", Encoding.UTF8.GetString(put.Body));
    }

    /// <summary>Ein Strom ohne Länge (wie ein hochgeladenes Formular) - Lesen nur der Reihe nach.</summary>
    private sealed class OneWay(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    [Fact]
    public async Task Grosse_Datei_ohne_Laenge_in_Teilen_ueber_Upload_Session()
    {
        var (d, fake) = Make();
        var data = new byte[OneDriveDriver.SimpleLimit + OneDriveDriver.Chunk + 1234];
        new Random(7).NextBytes(data);
        var received = new MemoryStream();
        var throttled = false;
        fake.Answer = r =>
        {
            if (r.Url.Contains("createUploadSession")) return FakeOneDrive.Json(HttpStatusCode.OK, """{"uploadUrl":"https://up.example/session1"}""");
            if (r.Url.StartsWith("https://up.example/"))
            {
                // Beim zweiten Teil einmal bremsen
                if (received.Length == OneDriveDriver.Chunk && !throttled)
                {
                    throttled = true;
                    var slow = FakeOneDrive.Json(HttpStatusCode.TooManyRequests, "{}");
                    slow.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                    return slow;
                }
                received.Write(r.Body);
                return FakeOneDrive.Json(received.Length == data.Length ? HttpStatusCode.Created : HttpStatusCode.Accepted, "{}");
            }
            return FakeOneDrive.Json(HttpStatusCode.Conflict, "{}");
        };
        // Wie ein hochgeladenes Formular: ohne Länge, nur der Reihe nach lesbar
        await d.WriteAsync("Videos/gross.bin", new OneWay(data), CancellationToken.None);

        Assert.Equal(data, received.ToArray());
        var parts = fake.Requests.Where(r => r.Url.StartsWith("https://up.example/")).ToList();
        // 2 Teile, der zweite nach der Drosselung noch einmal
        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.True(p.Body.Length <= OneDriveDriver.Chunk));
        Assert.All(parts, p => Assert.Null(p.Auth));
        Assert.Equal($"bytes 0-{OneDriveDriver.Chunk - 1}/{data.Length}", parts[0].ContentRange);
        Assert.Equal($"bytes {OneDriveDriver.Chunk}-{data.Length - 1}/{data.Length}", parts[1].ContentRange);
        Assert.Equal(parts[1].ContentRange, parts[2].ContentRange);
        var session = fake.Requests.Single(r => r.Url.Contains("createUploadSession"));
        Assert.Equal(G + ":/Videos/gross.bin:/createUploadSession", session.Url);
        Assert.Contains("replace", Encoding.UTF8.GetString(session.Body));
    }

    [Fact]
    public async Task Abgebrochener_Upload_verwirft_die_Session()
    {
        var (d, fake) = Make();
        fake.Answer = r =>
            r.Url.Contains("createUploadSession") ? FakeOneDrive.Json(HttpStatusCode.OK, """{"uploadUrl":"https://up.example/s2"}""")
            : r.Method == "PUT" && r.Url.StartsWith("https://up.example/") ? FakeOneDrive.Json(HttpStatusCode.InsufficientStorage, "{}")
            : FakeOneDrive.Json(HttpStatusCode.NoContent, "");
        var e = await Assert.ThrowsAsync<IOException>(() => d.WriteAsync("x.bin", new MemoryStream(new byte[OneDriveDriver.SimpleLimit + 10]), CancellationToken.None));
        Assert.Contains("voll", e.Message);
        Assert.Contains(fake.Requests, r => r.Method == "DELETE" && r.Url == "https://up.example/s2");
    }

    [Fact]
    public async Task Drosselung_mit_Retry_After()
    {
        var (d, fake) = Make();
        var n = 0;
        fake.Answer = _ =>
        {
            if (n++ < 2)
            {
                var r = FakeOneDrive.Json(HttpStatusCode.TooManyRequests, "{}");
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(n == 1 ? 3 : 600));
                return r;
            }
            return FakeOneDrive.Json(HttpStatusCode.OK, """{"value":[]}""");
        };
        Assert.Empty(await d.ListAsync("", CancellationToken.None));
        // Zweimal gewartet und erneut gefragt
        Assert.Equal(3, fake.Requests.Count);
    }

    [Fact]
    public async Task Verschieben_in_anderen_Ordner_und_Platz()
    {
        var (d, fake) = Make();
        fake.Answer = r => r.Method switch
        {
            "POST" => FakeOneDrive.Json(HttpStatusCode.Conflict, "{}"),
            "GET" when r.Url.Contains("quota") => FakeOneDrive.Json(HttpStatusCode.OK, """{"quota":{"used":100,"remaining":900,"total":1000}}"""),
            "GET" => FakeOneDrive.Json(HttpStatusCode.OK, """{"id":"ZIEL","name":"Archiv","folder":{}}"""),
            "PATCH" => FakeOneDrive.Json(HttpStatusCode.OK, "{}"),
            _ => FakeOneDrive.Json(HttpStatusCode.BadRequest, "{}"),
        };
        await d.MoveAsync("a.txt", "Archiv/b.txt", CancellationToken.None);
        var patch = fake.Requests.Single(r => r.Method == "PATCH");
        Assert.Equal(G + ":/a.txt", patch.Url);
        using var body = JsonDocument.Parse(patch.Body);
        Assert.Equal("b.txt", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("ZIEL", body.RootElement.GetProperty("parentReference").GetProperty("id").GetString());

        var space = await d.SpaceAsync(CancellationToken.None);
        Assert.Equal(100, space!.Used);
        Assert.Equal(900, space.Free);
    }

    [Fact]
    public async Task Vorhandenes_Ziel_wird_gemeldet()
    {
        var (d, fake) = Make();
        fake.Answer = r => r.Method == "GET" ? FakeOneDrive.Json(HttpStatusCode.OK, """{"id":"R"}""")
            : FakeOneDrive.Json(HttpStatusCode.Conflict, """{"error":{"code":"nameAlreadyExists","message":"x"}}""");
        var e = await Assert.ThrowsAsync<IOException>(() => d.MoveAsync("a.txt", "b.txt", CancellationToken.None));
        Assert.Contains("schon", e.Message);
    }

    [Fact]
    public async Task Anmeldung_fuer_OneDrive_geht_an_common()
    {
        var fake = new FakeOneDrive
        {
            Answer = r => FakeOneDrive.Json(HttpStatusCode.OK, r.Url.EndsWith("devicecode")
                ? """{"device_code":"DC","user_code":"ABCD-EFGH","verification_uri":"https://microsoft.com/devicelogin","expires_in":900,"interval":5}"""
                : """{"access_token":"AT","refresh_token":"RT","expires_in":3600}"""),
        };
        var oauth = new MicrosoftOAuth("client-123", new HttpClient(fake));
        var start = await oauth.StartAsync("u1", CancellationToken.None, MicrosoftOAuth.FilesScopes);
        Assert.Equal("ok", await oauth.PollAsync("u1", start.Flow, CancellationToken.None));
        Assert.Equal("RT", oauth.FilesGrant("u1", start.Flow));
        // Die Anmeldung für Mail oder Kalender passt nicht dazu, ein anderer Benutzer auch nicht
        Assert.Null(oauth.GraphGrant("u1", start.Flow));
        Assert.Null(oauth.FilesGrant("u2", start.Flow));
        Assert.All(fake.Requests, r => Assert.StartsWith("https://login.microsoftonline.com/common/", r.Url));
        Assert.Contains("Files.ReadWrite", Encoding.UTF8.GetString(fake.Requests[0].Body));
        Assert.Equal("AT", await oauth.FilesTokenAsync("m1", "RT", null, CancellationToken.None));
    }

    [Fact]
    public async Task Mail_bleibt_bei_consumers()
    {
        var fake = new FakeOneDrive
        {
            Answer = _ => FakeOneDrive.Json(HttpStatusCode.OK, """{"device_code":"DC","user_code":"X","expires_in":900,"interval":5}"""),
        };
        await new MicrosoftOAuth("client-123", new HttpClient(fake)).StartAsync("u1", CancellationToken.None);
        Assert.StartsWith("https://login.microsoftonline.com/consumers/", fake.Requests.Single().Url);
    }
}

/// <summary>OneDrive-Ordner in der Dateiablage: Schalter des Admins, gesperrte Einbindungen, Blättern und Hochladen über die API,
/// neues Refresh-Token landet im Ordner.</summary>
[Collection("server")]
public class OneDriveMountTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public OneDriveMountTests(FilesServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Schalter_sperrt_und_gibt_frei()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var space = _fx.Factory.Services.GetRequiredService<Spaces>().Get(users.ByLogin(FilesServerFixture.Login)!.Id);
        var fake = new FakeOneDrive();
        var (http, token) = (ExternalFolders.GraphHttp, ExternalFolders.FilesToken);
        var before = space.Db.GetJson<List<UserMount>>(ExternalFolders.UserKey);
        var tokens = new List<string?>();
        try
        {
            ExternalFolders.GraphHttp = new HttpClient(fake);
            // Erneuern: Microsoft gibt ein neues Refresh-Token aus
            ExternalFolders.FilesToken = (key, refresh, rotated, _) =>
            {
                tokens.Add(refresh);
                if (refresh == "RT1") rotated?.Invoke(key, "RT1", "RT2");
                return Task.FromResult("TOKEN");
            };
            space.Db.SetJson(ExternalFolders.UserKey, new List<UserMount>
            {
                new() { Id = "od1", Name = "Mein OneDrive", Kind = UserMount.OneDrive, RefreshToken = "RT1", Drive = "D1", Item = "F1" },
            });
            fake.Answer = r => r.Method switch
            {
                "GET" when r.Url.Contains("/children") => FakeOneDrive.Json(HttpStatusCode.OK, """{"value":[{"id":"1","name":"Rechnung.pdf","size":5,"file":{}}]}"""),
                "PUT" => FakeOneDrive.Json(HttpStatusCode.Created, "{}"),
                "POST" => FakeOneDrive.Json(HttpStatusCode.Created, """{"id":"A","name":"Ablage","folder":{}}"""),
                _ => FakeOneDrive.Json(HttpStatusCode.NotFound, "{}"),
            };
            var c = _fx.Client();

            // Aus: nicht angeboten, nicht sichtbar, gesperrt
            config.Update(s => s.ExternalOneDriveForUsers = false);
            var mounts = await c.GetFromJsonAsync<JsonElement>("/api/files/external/mounts");
            Assert.False(mounts.GetProperty("oneDrive").GetBoolean());
            Assert.DoesNotContain(mounts.GetProperty("mounts").EnumerateArray(), m => m.GetProperty("id").GetString() == "od1");
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/files/external/list?mount=od1&fresh=true")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/files/external/onedrive/start", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/external/onedrive", new { flow = "x", name = "y" })).StatusCode);
            Assert.Empty(fake.Requests);

            // An: angeboten und nutzbar
            config.Update(s => s.ExternalOneDriveForUsers = true);
            mounts = await c.GetFromJsonAsync<JsonElement>("/api/files/external/mounts");
            Assert.True(mounts.GetProperty("oneDrive").GetBoolean());
            Assert.True(mounts.GetProperty("canAdd").GetBoolean());
            Assert.Contains(mounts.GetProperty("mounts").EnumerateArray(), m => m.GetProperty("id").GetString() == "od1" && m.GetProperty("kind").GetString() == "onedrive");
            var list = await c.GetFromJsonAsync<JsonElement>("/api/files/external/list?mount=od1&fresh=true");
            Assert.Equal("Rechnung.pdf", list.GetProperty("items")[0].GetProperty("name").GetString());

            using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Inhalt")), "file", "neu.txt" } };
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/files/external/upload?mount=od1&path=Ablage", form)).StatusCode);
            var put = fake.Requests.Single(r => r.Method == "PUT");
            Assert.Equal("https://graph.microsoft.com/v1.0/drives/D1/items/F1:/Ablage/neu.txt:/content", put.Url);
            Assert.Equal("Inhalt", Encoding.UTF8.GetString(put.Body));

            // Das neue Refresh-Token steht jetzt im Ordner, das alte ist weg
            // (seit den externen Zielen liegt die Anmeldung beim Ziel, od1 ist die Einbindung darauf)
            var od1Access = space.Db.GetJson<List<UserMount>>(ExternalFolders.UserKey).Single(m => m.Id == "od1").Access!;
            Assert.Equal("RT2", space.Db.GetJson<List<UserMount>>(ExternalFolders.UserKey).Single(m => m.Id == od1Access).RefreshToken);
            // Ein überholtes Token überschreibt kein neueres
            ExternalFolders.Rotated(space, od1Access, "RT1", "RT3");
            Assert.Equal("RT2", space.Db.GetJson<List<UserMount>>(ExternalFolders.UserKey).Single(m => m.Id == od1Access).RefreshToken);

            // WebDAV-Schalter wirkt nicht auf OneDrive und umgekehrt
            config.Update(s => s.ExternalWebDavForUsers = false);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/files/external/list?mount=od1&fresh=true")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/external/mounts", new { name = "NAS", url = "https://example.org/dav/" })).StatusCode);

            // Ohne Anmeldung kein Ordnerinhalt
            Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/files/external/onedrive/folders?flow=unbekannt")).StatusCode);
        }
        finally
        {
            config.Update(s => s.ExternalOneDriveForUsers = false);
            space.Db.SetJson(ExternalFolders.UserKey, before);
            (ExternalFolders.GraphHttp, ExternalFolders.FilesToken) = (http, token);
        }
    }

}

/// <summary>Verwaltung → Storage → Externe Ordner: der Schalter für OneDrive neben dem für WebDAV.</summary>
[Collection("server")]
public class OneDriveAdminTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public OneDriveAdminTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Admin_schaltet_OneDrive()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        // Die Verwaltung braucht eine Anmeldung im Browser
        var admin = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        Assert.True((await admin.PostAsJsonAsync("/api/auth/login", new { login = "anna", password = "geheim-123456" })).IsSuccessStatusCode);
        var folders = config.Current.ExternalFolders;
        var cur = await admin.GetFromJsonAsync<JsonElement>("/api/admin/external");
        Assert.False(cur.GetProperty("oneDriveForUsers").GetBoolean());
        Assert.True(cur.GetProperty("microsoft").GetBoolean());
        try
        {
            var res = await admin.PutAsJsonAsync("/api/admin/external", new { folders = Array.Empty<object>(), webDavForUsers = false, webDavPrivate = false, oneDriveForUsers = true });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.True(config.Current.ExternalOneDriveForUsers);
            // Ältere Oberfläche ohne den Schalter lässt ihn stehen
            await admin.PutAsJsonAsync("/api/admin/external", new { folders = Array.Empty<object>(), webDavForUsers = false, webDavPrivate = false });
            Assert.True(config.Current.ExternalOneDriveForUsers);
        }
        finally { config.Update(s => { s.ExternalOneDriveForUsers = false; s.ExternalFolders = folders; }); }
    }
}
