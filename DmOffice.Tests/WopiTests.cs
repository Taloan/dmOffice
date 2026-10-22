using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Office;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Gefaelschter Office-Server: liefert eine Discovery wie Collabora (mit Proof-Key) und signiert Anfragen wie er.</summary>
public sealed class FakeOffice : HttpMessageHandler
{
    public const string Url = "https://office.test";
    public RSA Key { get; } = RSA.Create(2048);
    public bool Down { get; set; }
    public int Calls { get; private set; }

    public string Xml()
    {
        var p = Key.ExportParameters(false);
        var urlsrc = Url + "/browser/abc/cool.html?";
        string App(string name, params (string Ext, string Action)[] actions) =>
            $"<app name=\"{name}\">" + string.Concat(actions.Select(a => $"<action ext=\"{a.Ext}\" name=\"{a.Action}\" urlsrc=\"{urlsrc}\"/>")) + "</app>";
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?><wopi-discovery><net-zone name=\"external-http\">"
            + App("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ("", "edit"))
            + App("writer", ("odt", "edit"), ("rtf", "edit"))
            + App("calc", ("xlsx", "edit"), ("ods", "edit"), ("csv", "edit"))
            + App("impress", ("pptx", "edit"), ("odp", "edit"))
            + App("draw", ("pdf", "view"))
            + "</net-zone>"
            + $"<proof-key value=\"x\" modulus=\"{Convert.ToBase64String(p.Modulus!)}\" exponent=\"{Convert.ToBase64String(p.Exponent!)}\" oldvalue=\"\" oldmodulus=\"\" oldexponent=\"\"/>"
            + "</wopi-discovery>";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        if (Down) throw new HttpRequestException("Verbindung abgelehnt");
        var ok = request.RequestUri!.AbsolutePath == "/hosting/discovery";
        return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.NotFound)
        {
            Content = new StringContent(ok ? Xml() : "nicht hier", Encoding.UTF8, "text/xml"),
        });
    }

    /// <summary>Anfrage wie Collabora: Token in der Abfrage, X-WOPI-Proof ueber Token, Adresse und Zeitstempel.</summary>
    public HttpRequestMessage Request(HttpMethod method, string url, string token, DateTime? at = null, string? signUrl = null)
    {
        var full = url + (url.Contains('?') ? "&" : "?") + "access_token=" + Uri.EscapeDataString(token);
        var req = new HttpRequestMessage(method, full);
        var ticks = (at ?? DateTime.UtcNow).Ticks;
        var data = WopiProof.Data(token, signUrl ?? full, ticks);
        req.Headers.Add("X-WOPI-TimeStamp", ticks.ToString());
        req.Headers.Add("X-WOPI-Proof", Convert.ToBase64String(Key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        return req;
    }
}

public sealed class WopiFixture : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; }
    public FakeOffice Office { get; } = new();
    public UserSpace Berta { get; }
    public UserSpace Carl { get; }
    private readonly Dictionary<string, string> _passwords = new();
    private readonly DirectoryInfo _data;

    public WopiFixture()
    {
        _data = Directory.CreateTempSubdirectory("dmwopi");
        Environment.SetEnvironmentVariable("DMOFFICE_DATA", _data.FullName);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient("office").ConfigurePrimaryHttpMessageHandler(() => Office)));
        var users = Factory.Services.GetRequiredService<UserStore>();
        var spaces = Factory.Services.GetRequiredService<Spaces>();
        Berta = spaces.Get(users.Create("berta", "Berta Beispiel", "geheim-123456", isAdmin: false).Id);
        Carl = spaces.Get(users.Create("carl", "Carl Clever", "geheim-123456", isAdmin: false).Id);
        _passwords["berta"] = AppPasswords.Create(Berta.Db, "Test");
        _passwords["carl"] = AppPasswords.Create(Carl.Db, "Test");
        Factory.Services.GetRequiredService<ServerConfig>().Update(s => { s.OfficeUrl = FakeOffice.Url; s.OfficeWopiBase = "https://dm.test"; });
    }

    public HttpClient Client(string? login = "berta")
    {
        var c = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (login != null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{_passwords[login]}")));
        return c;
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { _data.Delete(true); } catch (IOException) { /* Datenbank noch offen */ }
    }
}

/// <summary>dmOffice als WOPI-Host mit gefaelschtem Office-Client: CheckFileInfo, GetFile, PutFile, Sperren, Konflikte, Tokens.</summary>
[Collection("server")]
public class WopiTests : IClassFixture<WopiFixture>
{
    private readonly WopiFixture _fx;
    public WopiTests(WopiFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>Datei anlegen und wie die Oberflaeche oeffnen: Token und WOPISrc.</summary>
    private async Task<(string Token, string Src)> Open(string path, byte[]? data = null, bool view = false)
    {
        var db = _fx.Berta.Db;
        if (FileStore.Parent(path) is { Length: > 0 } dir) FileStore.EnsureDir(db, dir);
        FileStore.Put(db, path, data ?? OfficeTemplates.Create(Path.GetExtension(path)), null);
        var s = await Json(await _fx.Client().PostAsJsonAsync("/api/office/open", new { path, view }));
        var url = s.GetProperty("url").GetString()!;
        var src = Uri.UnescapeDataString(url.Split("WOPISrc=")[1].Split('&')[0]);
        Assert.StartsWith(FakeOffice.Url + "/browser/abc/cool.html?WOPISrc=", url);
        Assert.StartsWith("https://dm.test/wopi/files/", src);
        return (s.GetProperty("token").GetString()!, src);
    }

    /// <summary>Die WOPISrc liegt (eingestellt) unter https://dm.test - im Test geht die Anfrage an den Testserver, signiert wird die echte Adresse.</summary>
    private async Task<HttpResponseMessage> Send(HttpMethod method, string src, string suffix, string token, Action<HttpRequestMessage>? headers = null,
        HttpContent? content = null, DateTime? at = null)
    {
        var local = new Uri(src).AbsolutePath + suffix;
        var signed = src + suffix + "?access_token=" + Uri.EscapeDataString(token);
        var req = _fx.Office.Request(method, local, token, at, signed);
        req.Content = content;
        headers?.Invoke(req);
        return await _fx.Client(null).SendAsync(req);
    }

    private Task<HttpResponseMessage> Op(string src, string token, string op, string? lockId = null, string? oldLock = null) =>
        Send(HttpMethod.Post, src, "", token, r =>
        {
            r.Headers.Add("X-WOPI-Override", op);
            if (lockId != null) r.Headers.Add("X-WOPI-Lock", lockId);
            if (oldLock != null) r.Headers.Add("X-WOPI-OldLock", oldLock);
        });

    private Task<HttpResponseMessage> Put(string src, string token, byte[] data, string? lockId, string? timestamp = null) =>
        Send(HttpMethod.Post, src, "/contents", token, r =>
        {
            r.Headers.Add("X-WOPI-Override", "PUT");
            if (lockId != null) r.Headers.Add("X-WOPI-Lock", lockId);
            if (timestamp != null) r.Headers.Add("X-COOL-WOPI-Timestamp", timestamp);
        }, new ByteArrayContent(data));

    [Fact]
    public async Task Config_nennt_die_Office_Arten()
    {
        var c = await Json(await _fx.Client().GetAsync("/api/office/config"));
        Assert.True(c.GetProperty("enabled").GetBoolean());
        var edit = c.GetProperty("edit").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("docx", edit);
        Assert.Contains("csv", edit);
        Assert.DoesNotContain("pdf", edit);
        Assert.Equal(["docx", "xlsx", "pptx"], c.GetProperty("create").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task CheckFileInfo_GetFile_PutFile_mit_Sperre()
    {
        var (token, src) = await Open("wopi/Bericht.docx");
        var info = await Json(await Send(HttpMethod.Get, src, "", token));
        Assert.Equal("Bericht.docx", info.GetProperty("BaseFileName").GetString());
        Assert.True(info.GetProperty("UserCanWrite").GetBoolean());
        Assert.Equal("Berta Beispiel", info.GetProperty("UserFriendlyName").GetString());
        Assert.Equal("http://localhost", info.GetProperty("PostMessageOrigin").GetString());
        var modified = info.GetProperty("LastModifiedTime").GetString()!;

        var get = await Send(HttpMethod.Get, src, "/contents", token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(OfficeTemplates.Create("docx"), await get.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, (await Op(src, token, "LOCK", "L1")).StatusCode);
        // Fremde Sperre: 409 mit der aktuellen
        var other = await Op(src, token, "LOCK", "L2");
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("L1", other.Headers.GetValues("X-WOPI-Lock").Single());
        var getLock = await Op(src, token, "GET_LOCK");
        Assert.Equal("L1", getLock.Headers.GetValues("X-WOPI-Lock").Single());

        // Speichern nur mit der richtigen Sperre
        var wrong = await Put(src, token, Encoding.UTF8.GetBytes("falsch"), "L2", modified);
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("L1", wrong.Headers.GetValues("X-WOPI-Lock").Single());
        var fresh = Samples.Docx("Neuer Inhalt aus Collabora");
        var put = await Json(await Put(src, token, fresh, "L1", modified));
        Assert.NotEqual(modified, put.GetProperty("LastModifiedTime").GetString());
        Assert.Equal(fresh, FileStore.Data(_fx.Berta.Db, "wopi/Bericht.docx"));
        // Wie gewohnt: der vorige Inhalt ist eine fruehere Version
        var f = FileStore.Get(_fx.Berta.Db, "wopi/Bericht.docx")!;
        Assert.Single(FileStore.Versions(_fx.Berta.Db, f.FileId));

        Assert.Equal(HttpStatusCode.OK, (await Op(src, token, "REFRESH_LOCK", "L1")).StatusCode);
        // UnlockAndRelock
        Assert.Equal(HttpStatusCode.OK, (await Op(src, token, "LOCK", "L3", oldLock: "L1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Op(src, token, "UNLOCK", "L1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Op(src, token, "UNLOCK", "L3")).StatusCode);
        Assert.Equal("", (await Op(src, token, "GET_LOCK")).Headers.GetValues("X-WOPI-Lock").Single());
    }

    [Fact]
    public async Task PutFile_erkennt_Aenderung_von_anderswo()
    {
        var (token, src) = await Open("wopi/Tabelle.xlsx");
        var seen = (await Json(await Send(HttpMethod.Get, src, "", token))).GetProperty("LastModifiedTime").GetString()!;
        await Task.Delay(20);
        // Inzwischen ueber WebDAV oder das Handy geaendert
        var elsewhere = Samples.Xlsx("von woanders", "a", "1");
        FileStore.Put(_fx.Berta.Db, "wopi/Tabelle.xlsx", elsewhere, null);
        var r = await Put(src, token, Samples.Xlsx("aus dem Editor", "a", "1"), null, seen);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(1010, (await Json(new HttpResponseMessage(HttpStatusCode.OK) { Content = r.Content })).GetProperty("COOLStatusCode").GetInt32());
        Assert.Equal(elsewhere, FileStore.Data(_fx.Berta.Db, "wopi/Tabelle.xlsx"));
        // Benutzer waehlt in Collabora "trotzdem speichern": ohne Zeitstempel
        Assert.Equal(HttpStatusCode.OK, (await Put(src, token, Samples.Xlsx("aus dem Editor", "a", "1"), null)).StatusCode);
    }

    [Fact]
    public async Task Abgelaufene_fremde_und_kaputte_Tokens()
    {
        var (token, src) = await Open("wopi/Brief.odt", Encoding.UTF8.GetBytes("odt"));
        var f = FileStore.Get(_fx.Berta.Db, "wopi/Brief.odt")!;
        var user = _fx.Berta.User;
        var grant = new WopiGrant(user.Id, f.FileId, true, "https://dm.test", "http://localhost", user.Stamp, null, "B");
        var (expired, _) = WopiTokens.Issue(_fx.Factory.Services, grant, TimeSpan.FromSeconds(-5));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, src, "", expired)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, src, "", token + "x")).StatusCode);

        // Carls Token fuer seine eigene Datei passt nicht auf Bertas Adresse
        FileStore.Put(_fx.Carl.Db, "carl.docx", OfficeTemplates.Create("docx"), null);
        var carl = await Json(await _fx.Client("carl").PostAsJsonAsync("/api/office/open", new { path = "carl.docx" }));
        var carlToken = carl.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, src, "", carlToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(src, carlToken, [1, 2, 3], null)).StatusCode);

        // Token fuer eine andere Datei derselben Benutzerin
        var (other, _) = await Open("wopi/Anderes.docx");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, src, "/contents", other)).StatusCode);

        // Nur ansehen: lesen ja, schreiben und sperren nein
        var (view, viewSrc) = await Open("wopi/Lesen.docx", view: true);
        var info = await Json(await Send(HttpMethod.Get, viewSrc, "", view));
        Assert.False(info.GetProperty("UserCanWrite").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(viewSrc, view, [1], null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Op(viewSrc, view, "LOCK", "X")).StatusCode);
    }

    [Fact]
    public async Task Proof_Key_wird_geprueft()
    {
        var (token, src) = await Open("wopi/Proof.docx");
        // Falsche Signatur
        var bad = await Send(HttpMethod.Get, src, "", token, r => { r.Headers.Remove("X-WOPI-Proof"); r.Headers.Add("X-WOPI-Proof", Convert.ToBase64String(new byte[256])); });
        Assert.Equal(HttpStatusCode.InternalServerError, bad.StatusCode);
        // Zu alt (wiederholte Anfrage)
        Assert.Equal(HttpStatusCode.InternalServerError, (await Send(HttpMethod.Get, src, "", token, at: DateTime.UtcNow.AddHours(-1))).StatusCode);
        // Ohne Proof: abgelehnt, denn der Office-Server veroeffentlicht Schluessel
        var none = await Send(HttpMethod.Get, src, "", token, r => { r.Headers.Remove("X-WOPI-Proof"); r.Headers.Remove("X-WOPI-TimeStamp"); });
        Assert.Equal(HttpStatusCode.InternalServerError, none.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, src, "", token)).StatusCode);
    }

    [Fact]
    public async Task Notizi_Ordner_bleibt_gesperrt()
    {
        var db = _fx.Berta.Db;
        FileStore.EnsureDir(db, "notizisync/sub");
        FileStore.Put(db, "notizisync/sub/geheim.docx", OfficeTemplates.Create("docx"), null);
        var r = await _fx.Client().PostAsJsonAsync("/api/office/open", new { path = "notizisync/sub/geheim.docx" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.Client().PostAsJsonAsync("/api/office/new", new { dir = "notizisync", kind = "docx", name = "x" })).StatusCode);

        // Auch ein (irgendwie erlangtes) Token fuer eine Datei dort hilft nicht
        var f = FileStore.Get(db, "notizisync/sub/geheim.docx")!;
        var u = _fx.Berta.User;
        var (token, _) = WopiTokens.Issue(_fx.Factory.Services, new WopiGrant(u.Id, f.FileId, true, "https://dm.test", "x", u.Stamp, null, "B"));
        var src = "https://dm.test/wopi/files/" + WopiTokens.FileId(u.Id, f.FileId);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, src, "", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Put(src, token, [1], null)).StatusCode);

        // Eine Datei, die erst spaeter in den notizi-Ordner wandert, ist ab dann ebenfalls zu
        var (t2, src2) = await Open("wopi/wandert.docx");
        FileStore.MoveOrCopy(db, "wopi/wandert.docx", "notizisync/wandert.docx", false, copy: false);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, src2, "", t2)).StatusCode);
    }

    [Fact]
    public async Task Speichern_unter_legt_neue_Datei_daneben()
    {
        var (token, src) = await Open("wopi/Vorlage.docx");
        var r = await Json(await Send(HttpMethod.Post, src, "", token, h =>
        {
            h.Headers.Add("X-WOPI-Override", "PUT_RELATIVE");
            h.Headers.Add("X-WOPI-SuggestedTarget", ".pdf");
        }, new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4"))));
        Assert.Equal("Vorlage.pdf", r.GetProperty("Name").GetString());
        Assert.StartsWith("https://dm.test/wopi/files/", r.GetProperty("Url").GetString());
        Assert.Equal("%PDF-1.4", Encoding.ASCII.GetString(FileStore.Data(_fx.Berta.Db, "wopi/Vorlage.pdf")!));

        // Fester Name, belegt, ohne Ueberschreiben: 409 mit einem freien Vorschlag
        var taken = await Send(HttpMethod.Post, src, "", token, h =>
        {
            h.Headers.Add("X-WOPI-Override", "PUT_RELATIVE");
            h.Headers.Add("X-WOPI-RelativeTarget", "Vorlage.pdf");
        }, new ByteArrayContent([1]));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("Vorlage (2).pdf", taken.Headers.GetValues("X-WOPI-ValidRelativeTarget").Single());

        // Mit Ueberschreiben (UTF-7: "Ü" = "+ANw-")
        var over = await Json(await Send(HttpMethod.Post, src, "", token, h =>
        {
            h.Headers.Add("X-WOPI-Override", "PUT_RELATIVE");
            h.Headers.Add("X-WOPI-RelativeTarget", "+ANw-bersicht.docx");
            h.Headers.Add("X-WOPI-OverwriteRelativeTarget", "true");
        }, new ByteArrayContent(OfficeTemplates.Create("docx"))));
        Assert.Equal("Übersicht.docx", over.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Neue_Dokumente_sind_gueltige_Pakete()
    {
        foreach (var kind in new[] { "docx", "xlsx", "pptx" })
        {
            var r = await Json(await _fx.Client().PostAsJsonAsync("/api/office/new", new { dir = "", kind, name = "Neu" }));
            Assert.Equal("Neu." + kind, r.GetProperty("path").GetString());
            using var zip = new ZipArchive(new MemoryStream(FileStore.Data(_fx.Berta.Db, "Neu." + kind)!));
            Assert.Contains(zip.Entries, e => e.FullName == "[Content_Types].xml");
            foreach (var e in zip.Entries)
                System.Xml.Linq.XDocument.Load(e.Open());
        }
        Assert.Equal(HttpStatusCode.Conflict, (await _fx.Client().PostAsJsonAsync("/api/office/new", new { dir = "", kind = "docx", name = "Neu.docx" })).StatusCode);
        // Text aus dem neuen Word-Dokument: leer, aber lesbar
        Assert.NotEqual("error", DmOffice.Search.TextExtract.Extract(OfficeTemplates.Create("docx"), "a.docx", null, ocr: false).Status);
    }

    [Fact]
    public async Task Oeffentlicher_Link_nur_ansehen()
    {
        var db = _fx.Berta.Db;
        FileStore.EnsureDir(db, "frei");
        FileStore.Put(db, "frei/Info.docx", OfficeTemplates.Create("docx"), null);
        var f = FileStore.Get(db, "frei/Info.docx")!;
        var link = await Json(await _fx.Client().PostAsJsonAsync("/api/files/links", new { path = "frei/Info.docx", mode = "read" }));
        var lt = link.GetProperty("token").GetString()!;
        var s = await Json(await _fx.Client(null).PostAsync($"/api/public/{lt}/office", null));
        Assert.False(s.GetProperty("write").GetBoolean());
        var token = s.GetProperty("token").GetString()!;
        var src = "https://dm.test/wopi/files/" + WopiTokens.FileId(_fx.Berta.User.Id, f.FileId);
        var info = await Json(await Send(HttpMethod.Get, src, "", token));
        Assert.False(info.GetProperty("UserCanWrite").GetBoolean());
        Assert.True(info.GetProperty("IsAnonymousUser").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(src, token, [1], null)).StatusCode);

        // Hochlade-Links: keine Office-Ansicht
        var up = await Json(await _fx.Client().PostAsJsonAsync("/api/files/links", new { path = "frei", mode = "upload" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.Client(null).PostAsync($"/api/public/{up.GetProperty("token").GetString()}/office?path=Info.docx", null)).StatusCode);

        // Link geloescht: Token wertlos
        Assert.True((await _fx.Client().DeleteAsync("/api/files/links/" + link.GetProperty("id").GetString())).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, src, "", token)).StatusCode);
    }

    [Fact]
    public async Task Discovery_Fehler_sind_lesbar()
    {
        var d = _fx.Factory.Services.GetRequiredService<OfficeDiscovery>();
        var (_, bad) = await d.Fetch("ftp://x", CancellationToken.None);
        Assert.Contains("https://", bad);
        var (_, notFound) = await d.Fetch(FakeOffice.Url + "/falsch", CancellationToken.None);
        Assert.Contains("404", notFound);
        _fx.Office.Down = true;
        try
        {
            var (_, down) = await d.Fetch(FakeOffice.Url, CancellationToken.None);
            Assert.Contains("nicht erreichbar", down);
        }
        finally { _fx.Office.Down = false; }
        Assert.Throws<FormatException>(() => Discovery.Parse("<html/>"));
        Assert.Equal("https://o/x?ui=de&rs=de&WOPISrc=https%3A%2F%2Fd%2Fwopi%2Ffiles%2F1&lang=de&closebutton=1",
            Discovery.ActionUrl("https://o/x?<ui=UI_LLCC&><rs=DC_LLCC&><dchat=DISABLE_CHAT&>", "https://d/wopi/files/1", "de"));
    }

    /// <summary>Echte Discovery von Collabora CODE 26.04.5.1 (Docker auf Unraid, Adresse ersetzt) - ohne Proof-Key.</summary>
    [Fact]
    public void Echte_Discovery_von_CODE_26_04()
    {
        var d = Discovery.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "code-26.04-discovery.xml")));
        Assert.Equal("collabora", d.Product);
        Assert.Null(d.ProofKey);
        foreach (var ext in new[] { "docx", "xlsx", "pptx", "odt", "ods", "odp", "rtf", "csv", "doc", "xls", "ppt" })
            Assert.True(d.Edit.ContainsKey(ext), ext);
        Assert.StartsWith("https://collabora.example.org/browser/", d.Edit["docx"]);
        Assert.EndsWith("cool.html?", d.Edit["docx"]);
        Assert.True(d.View.ContainsKey("pdf"));
        Assert.StartsWith("https://collabora.example.org/browser/22ce52fb51/cool.html?WOPISrc=",
            Discovery.ActionUrl(d.Edit["docx"], "https://dm.example.org/wopi/files/u_1", "de"));
    }
}
