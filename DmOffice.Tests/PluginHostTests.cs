using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Plugin-Rahmen: Manifest, Siegel, Laden mit eigener NuGet-Abhängigkeit, Routen mit Sperre, Schema, Datenexport.</summary>
[Collection("server")]
public class PluginHostTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dmplugins");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<string> _before = Features.InReleaseOff.ToList();

    public PluginHostTests()
    {
        Features.Set(_before.Where(x => x != Features.Plugins && x != Features.PluginRequires));
        PluginHost.Reset();
    }

    public void Dispose()
    {
        PluginHost.Reset();
        Features.Set(_before);
        _key.Dispose();
        try { _dir.Delete(true); } catch (IOException) { /* Assembly bleibt geladen */ } catch (UnauthorizedAccessException) { }
    }

    private List<(string, byte[])> Keys => [("Test", _key.ExportSubjectPublicKeyInfo())];

    /// <summary>Ordner wie im Image: plugin.json, server/bin mit DLL und Abhängigkeiten, web/entry.js, lang/en.json; gesiegelt.</summary>
    private string Sample(string core = ">=1 <2", bool seal = true, string key = "sample")
    {
        var dir = Path.Combine(_dir.FullName, key);
        var bin = Path.Combine(dir, "server", "bin");
        Directory.CreateDirectory(bin);
        var built = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DmOffice.Tests.Plugin", "bin",
            Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)))!, "net10.0");
        foreach (var f in Directory.GetFiles(built)) File.Copy(f, Path.Combine(bin, Path.GetFileName(f)));
        Directory.CreateDirectory(Path.Combine(dir, "web"));
        File.WriteAllText(Path.Combine(dir, "web", "entry.js"), "export default { views: [] }");
        Directory.CreateDirectory(Path.Combine(dir, "lang"));
        File.WriteAllText(Path.Combine(dir, "lang", "en.json"), "{\"Beispiel\": \"Sample\"}");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            key, version = "1.0.0", api = 1, label = new { de = "Beispiel", en = "Sample" }, core, requires = new[] { "notes" },
            server = new { assembly = "server/bin/DmOffice.Tests.Plugin.dll" }, web = new { entry = "web/entry.js" }, autoInstall = true,
        }));
        if (seal) PluginSeal.Seal(dir, PluginManifest.Read(dir), _key);
        return dir;
    }

    [Fact]
    public void Manifest_prueft_Schluessel_Version_Pfade_und_Kern_Stufe()
    {
        Assert.True(PluginManifest.CoreFits(">=1 <2"));
        Assert.True(PluginManifest.CoreFits("1"));
        Assert.False(PluginManifest.CoreFits(">=2"));
        Assert.False(PluginManifest.CoreFits(""));
        var dir = Path.Combine(_dir.FullName, "bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), "{\"key\":\"bad\",\"version\":\"1.0.0\",\"core\":\"1\",\"web\":{\"entry\":\"../x.js\"}}");
        Assert.Contains("nicht im Plugin-Ordner", Assert.Throws<InvalidDataException>(() => PluginManifest.Read(dir)).Message);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), "{\"key\":\"other\",\"version\":\"1.0.0\",\"core\":\"1\",\"web\":{\"entry\":\"x.js\"}}");
        Assert.Contains("Ordner heißt nicht", Assert.Throws<InvalidDataException>(() => PluginManifest.Read(dir)).Message);
    }

    [Fact]
    public void Siegel_erkennt_geaenderte_fehlende_und_zusaetzliche_Dateien()
    {
        var dir = Sample();
        var m = PluginManifest.Read(dir);
        Assert.True(PluginSeal.Verify(dir, m, Keys).Valid);
        // Fremder Schlüssel
        using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            Assert.False(PluginSeal.Verify(dir, m, [("x", other.ExportSubjectPublicKeyInfo())]).Valid);
        File.AppendAllText(Path.Combine(dir, "web", "entry.js"), "//");
        Assert.Contains("verändert", PluginSeal.Verify(dir, m, Keys).Reason);
        PluginSeal.Seal(dir, m, _key);
        File.WriteAllText(Path.Combine(dir, "web", "extra.js"), "");
        Assert.Contains("nicht im Siegel", PluginSeal.Verify(dir, m, Keys).Reason);
        File.Delete(Path.Combine(dir, "web", "extra.js"));
        File.Delete(Path.Combine(dir, "lang", "en.json"));
        Assert.Contains("fehlt", PluginSeal.Verify(dir, m, Keys).Reason);
    }

    [Fact]
    public void Siegel_von_seal_mjs_gilt_im_Kern()
    {
        // Mit tools/plugins/seal.mjs (Node) und einem Wegwerfschlüssel gesiegelt; der private Schlüssel ist gelöscht
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "plugin-seal");
        var keys = PluginSeal.TrustedKeys(File.ReadAllText(Path.Combine(dir, "public.txt")).Trim());
        var demo = Path.Combine(dir, "demo");
        var r = PluginSeal.Verify(demo, PluginManifest.Read(demo), keys);
        Assert.True(r.Valid, r.Reason);
        Assert.Equal("Betreiber", r.Signer);
    }


    [Fact]
    public void Ohne_Kennung_plugins_wird_nichts_gelesen()
    {
        Sample();
        Features.Set(_before.Append(Features.Plugins));
        PluginHost.Discover(new ServiceCollection(), root: _dir.FullName, keys: Keys);
        Assert.Empty(PluginHost.All);
    }

    [Fact]
    public void Ohne_Siegel_oder_mit_falscher_Stufe_nicht_geladen()
    {
        Sample(seal: false, key: "unsealed");
        Sample(core: ">=2", key: "future");
        PluginHost.Discover(new ServiceCollection(), root: _dir.FullName, keys: Keys);
        Assert.Equal("Ohne Siegel.", PluginHost.All.Single(e => e.Key == "unsealed").Problem);
        Assert.Contains("Kern-Schnittstelle", PluginHost.All.Single(e => e.Key == "future").Problem);
        Assert.Empty(PluginHost.Loaded);
    }

    [Fact]
    public async Task Geladen_mit_eigener_Abhaengigkeit_Routen_aus_ergibt_404_Schema_und_Export()
    {
        Sample();
        var settings = new ServerSettings();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // Anmeldung wie im Server (vereinfacht: Kopfzeile X-Test-User) und Bremse für öffentliche Wege
        builder.Services.AddAuthentication("test").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuth>("test", null);
        builder.Services.AddAuthorization(o => o.AddPolicy("admin", p => p.RequireRole("admin")));
        builder.Services.AddRateLimiter(o => o.AddPolicy("public", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("x")));
        var dataDir = Directory.CreateTempSubdirectory("dmpluginsdata");
        var paths = new DataPaths(dataDir.FullName);
        var users = new UserStore(paths, new Vault(paths, null));
        users.Create("anna", "Anna", "geheim-geheim-1", true);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(new Vault(paths, null));
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton<ServerDataStore>();
        builder.Services.AddSingleton<DmOffice.Sdk.IServerDataAccessor>(sp => sp.GetRequiredService<ServerDataStore>());
        builder.Services.AddSingleton<DmOffice.Sdk.IPeople, People>();
        PluginHost.Discover(builder.Services, root: _dir.FullName, keys: Keys);
        var entry = Assert.Single(PluginHost.Loaded);
        Assert.Equal("Test", entry.Signer);
        await using var app = builder.Build();
        PluginHost.UseHosts(app);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        PluginHost.Map(app);
        PluginHost.UseSettings(() => settings);
        await app.StartAsync();
        var anon = app.GetTestClient();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", users.All().Single().Id);

        // Ohne Anmeldung keine Plugin-Route; der öffentliche Weg unter /p/<key> geht
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/sample/hello")).StatusCode);
        Assert.Equal("pong", await anon.GetStringAsync("/p/sample/ping"));
        // Eigener Rechnername: alles dorthin, /api bleibt
        var host = new HttpRequestMessage(HttpMethod.Get, "/");
        host.Headers.Host = "hilfe.sample.test";
        Assert.Equal("pong", await (await anon.SendAsync(host)).Content.ReadAsStringAsync());
        var hostApi = new HttpRequestMessage(HttpMethod.Get, "/api/sample/hello");
        hostApi.Headers.Host = "hilfe.sample.test";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(hostApi)).StatusCode);
        var other = new HttpRequestMessage(HttpMethod.Get, "/");
        other.Headers.Host = "andere.sample.test";
        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(other)).StatusCode);
        // Serverweite Datenbank mit Schema, angemeldete Person
        var shared = await (await client.PostAsync("/api/sample/shared", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, shared.GetProperty("count").GetInt32());
        var store = app.Services.GetRequiredService<ServerDataStore>();
        Assert.Equal("anna", store.For("sample").Scalar("SELECT who FROM sample_shared"));
        Assert.Single(store.All());

        // Wege außerhalb von /api/<key> legt der Kern nicht an
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/samplex/other")).StatusCode);
        Assert.Equal("inside", await client.GetStringAsync("/api/sample/g/x"));
        Assert.True(PluginHost.OwnRoute("/api/sample", "sample"));
        Assert.True(PluginHost.OwnRoute("api/Sample/x/{id}", "sample"));
        Assert.False(PluginHost.OwnRoute("/api/admin/users", "sample"));
        Assert.False(PluginHost.OwnRoute("/apix/sample", "sample"));
        // Admin-Gruppe unter /api/<key>/admin nur mit Admin-Rolle; Person aus der Anfrage
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/sample/admin/who")).StatusCode);
        var adminClient = app.GetTestClient();
        adminClient.DefaultRequestHeaders.Add("X-Test-User", users.All().Single().Id);
        adminClient.DefaultRequestHeaders.Add("X-Test-Role", "admin");
        Assert.Equal(users.All().Single().Id, await adminClient.GetStringAsync("/api/sample/admin/who"));

        // Route des Plugins, Dienst aus ConfigureServices, Humanizer aus dem Plugin-Ordner
        var hello = await client.GetFromJsonAsync<JsonElement>("/api/sample/hello");
        Assert.Equal("plugin sample hello", hello.GetProperty("text").GetString());
        // Ausgeschaltet: 404
        settings.Plugins = new() { ["sample"] = new PluginSetting { On = false } };
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sample/hello")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/p/sample/ping")).StatusCode);
        var hostOff = new HttpRequestMessage(HttpMethod.Get, "/");
        hostOff.Headers.Host = "hilfe.sample.test";
        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(hostOff)).StatusCode);
        Assert.Empty(PluginHost.ExportAreas());
        settings.Plugins["sample"].On = true;
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/sample/hello")).GetProperty("count").GetInt32());

        // Schema beim Öffnen der Benutzerdatenbank, Stand in plugin_schema; zweites Öffnen ändert nichts
        var dbDir = Directory.CreateTempSubdirectory("dmpluginsdb");
        try
        {
            var path = Path.Combine(dbDir.FullName, "u.db");
            var db = new UserDb(path, new byte[32], "t");
            Assert.Equal(2L, db.Scalar("SELECT version FROM plugin_schema WHERE key='sample'"));
            db = new UserDb(path, new byte[32], "t");
            Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM sample_items"));

            // Datenexport: Bereich mit Name in der Sprache des Exports, Inhalt mit Humanizer
            var area = Assert.Single(PluginHost.ExportAreas());
            Assert.Equal("Sample", area.Area.Label.In("en"));
            var item = Assert.Single(area.Area.Items(new FakeUser(db)));
            using var ms = new MemoryStream();
            await item.Write(ms);
            Assert.Equal("eins\ntwo words", Encoding.UTF8.GetString(ms.ToArray()));
        }
        finally { SqliteClear(); try { dbDir.Delete(true); } catch (IOException) { } }
        await app.StopAsync();
        SqliteClear();
        try { dataDir.Delete(true); } catch (IOException) { }
    }

    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private sealed class FakeUser(UserDb db) : DmOffice.Sdk.IUserData
    {
        public string UserId => "t";
        public TimeZoneInfo Zone => TimeZoneInfo.Utc;
        public int Execute(string sql, params (string Name, object? Value)[] args) => db.Exec(sql, args);
        public List<T> Query<T>(string sql, Func<System.Data.IDataRecord, T> read, params (string Name, object? Value)[] args) => db.Query(sql, r => read(r), args);
        public object? Scalar(string sql, params (string Name, object? Value)[] args) => db.Scalar(sql, args);
    }

    /// <summary>Anmeldung für den Test: Kopfzeile X-Test-User mit der Kennung der Person.</summary>
    private sealed class TestAuth(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> o,
        Microsoft.Extensions.Logging.ILoggerFactory l, System.Text.Encodings.Web.UrlEncoder e)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var id)) return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
            var claims = new List<System.Security.Claims.Claim> { new(System.Security.Claims.ClaimTypes.NameIdentifier, id.ToString()) };
            if (Request.Headers.TryGetValue("X-Test-Role", out var role)) claims.Add(new(System.Security.Claims.ClaimTypes.Role, role.ToString()));
            var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test"));
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, "test")));
        }
    }
}
