using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Office;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Gefaelschter ONLYOFFICE Document Server ohne WOPI, mit JWT: api.js, CommandService, Ergebnisdateien.</summary>
public sealed class FakeDocs : HttpMessageHandler
{
    public const string Url = "https://oo.test";
    public const string Secret = "test-geheimnis-nur-fuer-die-pruefung";
    public byte[] Result { get; set; } = [];
    public List<Uri> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request.RequestUri!);
        var path = request.RequestUri!.AbsolutePath;
        HttpResponseMessage Text(string s, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(s, Encoding.UTF8) };
        switch (path)
        {
            case "/hosting/discovery": return Text("Not Found", HttpStatusCode.NotFound);
            case "/web-apps/apps/api/documents/api.js": return Text("window.DocsAPI = {};");
            case "/coauthoring/CommandService.ashx":
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
                var ok = Jwt.Read((string?)body?["token"], Secret)?["c"]?.ToString() == "version";
                return Text(ok ? "{\"error\":0,\"version\":\"9.4.0\"}" : "{\"error\":6}");
            }
            default:
                if (path.StartsWith("/cache/")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Result) };
                return Text("nein", HttpStatusCode.NotFound);
        }
    }
}

public sealed class OnlyOfficeFixture : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; }
    public FakeDocs Docs { get; } = new();
    public UserSpace Berta { get; }
    private readonly string _password;
    private readonly DirectoryInfo _data;

    public OnlyOfficeFixture()
    {
        _data = Directory.CreateTempSubdirectory("dmonlyoffice");
        Environment.SetEnvironmentVariable("DMOFFICE_DATA", _data.FullName);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddHttpClient("office").ConfigurePrimaryHttpMessageHandler(() => Docs);
            s.AddHttpClient("office-download").ConfigurePrimaryHttpMessageHandler(() => Docs);
        }));
        var users = Factory.Services.GetRequiredService<UserStore>();
        Berta = Factory.Services.GetRequiredService<Spaces>().Get(users.Create("berta", "Berta Beispiel", "geheim-123456", isAdmin: false).Id);
        _password = AppPasswords.Create(Berta.Db, "Test");
        var config = Factory.Services.GetRequiredService<ServerConfig>();
        config.Update(s => { s.OfficeUrl = FakeDocs.Url; s.OfficeWopiBase = "https://dm.test"; });
        config.SetOfficeJwtSecret(FakeDocs.Secret);
    }

    public HttpClient Client(bool signedIn = true)
    {
        var c = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (signedIn) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"berta:{_password}")));
        return c;
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { _data.Delete(true); } catch (IOException) { /* Datenbank noch offen */ }
    }
}

/// <summary>ONLYOFFICE ueber die eigene Docs-API: signierte Konfiguration, Datei holen, Callback mit JWT, Konflikte.</summary>
[Collection("server")]
public class OnlyOfficeTests : IClassFixture<OnlyOfficeFixture>
{
    private readonly OnlyOfficeFixture _fx;
    public OnlyOfficeTests(OnlyOfficeFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>Datei anlegen, oeffnen; zurueck: die (aus dem JWT gelesene) Konfiguration.</summary>
    private async Task<JsonObject> Open(string path, byte[]? data = null)
    {
        var db = _fx.Berta.Db;
        if (FileStore.Parent(path) is { Length: > 0 } dir) FileStore.EnsureDir(db, dir);
        FileStore.Put(db, path, data ?? OfficeTemplates.Create(Path.GetExtension(path)), null);
        var s = await Json(await _fx.Client().PostAsJsonAsync("/api/office/open", new { path }));
        Assert.Equal(FakeDocs.Url + "/web-apps/apps/api/documents/api.js", s.GetProperty("api").GetString());
        var token = s.GetProperty("config").GetProperty("token").GetString();
        var signed = Jwt.Read(token, FakeDocs.Secret);
        Assert.NotNull(signed);
        Assert.Null(Jwt.Read(token, "falsch"));
        return signed!;
    }

    /// <summary>Lokaler Pfad samt Abfrage aus einer Adresse unter https://dm.test.</summary>
    private static string Local(string url) => new Uri(url).PathAndQuery;

    private async Task<HttpResponseMessage> Callback(JsonObject cfg, int status, string url = FakeDocs.Url + "/cache/files/data/x/output.docx", bool sign = true)
    {
        var body = new JsonObject
        {
            ["key"] = (string?)cfg["document"]!["key"], ["status"] = status, ["url"] = url,
            ["users"] = new JsonArray("u1"),
        };
        if (sign) body["token"] = Jwt.Sign(JsonNode.Parse(body.ToJsonString())!, FakeDocs.Secret);
        return await _fx.Client(false).PostAsync(Local((string)cfg["editorConfig"]!["callbackUrl"]!),
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    }

    [Fact]
    public async Task Pruefung_erkennt_Docs_API_und_Geheimnis()
    {
        var d = _fx.Factory.Services.GetRequiredService<OfficeDiscovery>();
        var (none, noSecret) = await d.Fetch(FakeDocs.Url, CancellationToken.None, null, null, useStored: false);
        Assert.Null(none);
        Assert.Contains("JWT-Geheimnis", noSecret);
        var (_, wrong) = await d.Fetch(FakeDocs.Url, CancellationToken.None, "falsch", null, useStored: false);
        Assert.Contains("passt nicht", wrong);
        var (ok, error) = await d.Fetch(FakeDocs.Url, CancellationToken.None);
        Assert.Null(error);
        Assert.True(ok!.Native);
        var cfg = await Json(await _fx.Client().GetAsync("/api/office/config"));
        Assert.Contains("docx", cfg.GetProperty("edit").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Konfiguration_Datei_holen_und_speichern()
    {
        var cfg = await Open("oo/Brief.docx");
        Assert.Equal("word", (string?)cfg["documentType"]);
        Assert.Equal("edit", (string?)cfg["editorConfig"]!["mode"]);
        Assert.Equal("Berta Beispiel", (string?)cfg["editorConfig"]!["user"]!["name"]);
        var fileUrl = (string)cfg["document"]!["url"]!;
        Assert.StartsWith("https://dm.test/onlyoffice/file?t=", fileUrl);

        // Der Document Server holt die Datei
        var got = await _fx.Client(false).GetAsync(Local(fileUrl));
        Assert.Equal(OfficeTemplates.Create("docx"), await got.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Client(false).GetAsync("/onlyoffice/file?t=kaputt")).StatusCode);

        // Bearbeiten (1) aendert nichts; ohne JWT abgelehnt; mit JWT gespeichert (2)
        Assert.Equal(0, (await Json(await Callback(cfg, 1))).GetProperty("error").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await Callback(cfg, 2, sign: false)).StatusCode);
        _fx.Docs.Result = Samples.Docx("Aus ONLYOFFICE gespeichert");
        Assert.Equal(0, (await Json(await Callback(cfg, 2))).GetProperty("error").GetInt32());
        Assert.Equal(_fx.Docs.Result, FileStore.Data(_fx.Berta.Db, "oo/Brief.docx"));
        var f = FileStore.Get(_fx.Berta.Db, "oo/Brief.docx")!;
        Assert.Single(FileStore.Versions(_fx.Berta.Db, f.FileId));
        // Gleiche Sitzung speichert nochmals (Zwischenspeichern, dann Schliessen): kein Konflikt
        _fx.Docs.Result = Samples.Docx("Zweiter Stand");
        Assert.Equal(0, (await Json(await Callback(cfg, 6))).GetProperty("error").GetInt32());
        Assert.Equal(_fx.Docs.Result, FileStore.Data(_fx.Berta.Db, "oo/Brief.docx"));
    }

    [Fact]
    public async Task Aenderung_von_anderswo_wird_Konfliktkopie()
    {
        var cfg = await Open("oo/Plan.xlsx");
        var elsewhere = Samples.Xlsx("von woanders", "a", "1");
        FileStore.Put(_fx.Berta.Db, "oo/Plan.xlsx", elsewhere, null);
        _fx.Docs.Result = Samples.Xlsx("aus ONLYOFFICE", "a", "1");
        Assert.Equal(0, (await Json(await Callback(cfg, 2, FakeDocs.Url + "/cache/files/data/y/output.xlsx"))).GetProperty("error").GetInt32());
        Assert.Equal(elsewhere, FileStore.Data(_fx.Berta.Db, "oo/Plan.xlsx"));
        Assert.Equal(_fx.Docs.Result, FileStore.Data(_fx.Berta.Db, "oo/Plan (Konflikt).xlsx"));
    }

    [Fact]
    public async Task Ergebnis_nur_vom_eingestellten_Server()
    {
        var cfg = await Open("oo/Ssrf.docx");
        _fx.Docs.Result = Samples.Docx("ok");
        lock (_fx.Docs.Requests) _fx.Docs.Requests.Clear();
        // Fremder Host in url: geladen wird trotzdem vom eingestellten Document Server
        Assert.Equal(0, (await Json(await Callback(cfg, 2, "http://169.254.169.254/cache/files/z/output.docx"))).GetProperty("error").GetInt32());
        lock (_fx.Docs.Requests)
            Assert.All(_fx.Docs.Requests, u => Assert.Equal("oo.test", u.Host));
        Assert.Equal(_fx.Docs.Result, FileStore.Data(_fx.Berta.Db, "oo/Ssrf.docx"));
    }

    [Fact]
    public async Task Notizi_Ordner_und_oeffentlicher_Link()
    {
        var db = _fx.Berta.Db;
        FileStore.EnsureDir(db, "notizisync");
        FileStore.Put(db, "notizisync/x.docx", OfficeTemplates.Create("docx"), null);
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.Client().PostAsJsonAsync("/api/office/open", new { path = "notizisync/x.docx" })).StatusCode);

        FileStore.Put(db, "Info.docx", OfficeTemplates.Create("docx"), null);
        var link = await Json(await _fx.Client().PostAsJsonAsync("/api/files/links", new { path = "Info.docx", mode = "read" }));
        var s = await Json(await _fx.Client(false).PostAsync($"/api/public/{link.GetProperty("token").GetString()}/office", null));
        var cfg = Jwt.Read(s.GetProperty("config").GetProperty("token").GetString(), FakeDocs.Secret)!;
        Assert.Equal("view", (string?)cfg["editorConfig"]!["mode"]);
        Assert.False((bool)cfg["document"]!["permissions"]!["edit"]!);
        _fx.Docs.Result = [1, 2, 3];
        Assert.Equal(1, (await Json(await Callback(cfg, 2))).GetProperty("error").GetInt32());
        Assert.NotEqual(_fx.Docs.Result, FileStore.Data(db, "Info.docx"));
    }

    [Fact]
    public void Jwt_wie_ONLYOFFICE()
    {
        var token = Jwt.Sign(new JsonObject { ["a"] = 1 }, "s");
        Assert.Equal(1, (int)Jwt.Read(token, "s")!["a"]!);
        var parts = token.Split('.');
        var forged = parts[0] + "." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"a\":2}")).TrimEnd('=') + "." + parts[2];
        Assert.Null(Jwt.Read(forged, "s"));
        Assert.Null(Jwt.Read("a.b", "s"));
        var expired = Jwt.Sign(new JsonObject { ["exp"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds() }, "s");
        Assert.Null(Jwt.Read(expired, "s"));
    }
}
