using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>Ordner für WebDAV und Nextcloud ausblenden (Kontextmenü der Dateiablage) - auch nach dem Verschieben, Tresor-Ordner über die Tresor-Einstellung.</summary>
[Collection("server")]
public class DavHiddenTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public DavHiddenTests(FilesServerFixture fx) => _fx = fx;
    private const string L = FilesServerFixture.Login;

    private async Task<string> Propfind(string url)
    {
        var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        req.Headers.Add("Depth", "1");
        var res = await _fx.Client().SendAsync(req);
        return await res.Content.ReadAsStringAsync();
    }

    private async Task<JsonElement> Listing(string path) =>
        (await _fx.Client().GetFromJsonAsync<JsonElement>("/api/files?path=" + Uri.EscapeDataString(path))).GetProperty("items");

    [Fact]
    public async Task Ausgeblendeter_Ordner_fehlt_in_WebDAV_und_Nextcloud_bleibt_in_der_Ablage()
    {
        var name = "geheim-" + Guid.NewGuid().ToString("N")[..6];
        FileStore.EnsureDir(_fx.Db, name);
        FileStore.Put(_fx.Db, name + "/a.txt", "x"u8.ToArray(), "text/plain");
        var c = _fx.Client();
        Assert.Contains(name, await Propfind($"/remote.php/webdav/"));

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/files/dav-hidden", new { path = name, hidden = true })).StatusCode);
        Assert.DoesNotContain(name, await Propfind("/remote.php/webdav/"));
        Assert.DoesNotContain(name, await Propfind($"/dav/files/{L}/"));
        Assert.NotEqual((HttpStatusCode)207, (await c.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/remote.php/webdav/{name}/"))).StatusCode);
        // In der Ablage weiter da, gekennzeichnet
        var item = (await Listing("")).EnumerateArray().Single(i => i.GetProperty("name").GetString() == name);
        Assert.True(item.GetProperty("davHidden").GetBoolean());

        // Verschoben bleibt er ausgeblendet (die Dateinummer zählt, nicht der Pfad)
        FileStore.EnsureDir(_fx.Db, "oben-" + name);
        Assert.True((await c.PostAsJsonAsync("/api/files/move", new { from = name, to = $"oben-{name}/{name}", copy = false })).IsSuccessStatusCode);
        await Task.Delay(5200);   // Zwischenspeicher der Pfade
        var inner = await Propfind($"/remote.php/webdav/oben-{name}/");
        Assert.DoesNotContain($"oben-{name}/{name}", inner);

        // Wieder einblenden
        await c.PostAsJsonAsync("/api/files/dav-hidden", new { path = $"oben-{name}/{name}", hidden = false });
        Assert.Contains($"oben-{name}/{name}", await Propfind($"/remote.php/webdav/oben-{name}/"));
    }

    [Fact]
    public async Task Tresor_Ordner_ohne_Tresor_bleibt_ausgeblendet()
    {
        FileStore.EnsureDir(_fx.Db, FileStore.SystemVault);
        var r = await _fx.Client().PostAsJsonAsync("/api/files/dav-hidden", new { path = FileStore.SystemVault, hidden = false });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.DoesNotContain(">/remote.php/webdav/" + FileStore.SystemVault + "/<", await Propfind("/remote.php/webdav/"));
    }

    [Fact]
    public async Task Link_aus_der_App_endet_mit_dem_App_Passwort()
    {
        var name = "applink-" + Guid.NewGuid().ToString("N")[..6] + ".txt";
        FileStore.Put(_fx.Db, name, "x"u8.ToArray(), "text/plain");
        var token = AppPasswords.Create(_fx.Db, "Handy");
        var id = AppPasswords.Check(_fx.Db, token)!;
        var app = _fx.Client(signedIn: false);
        app.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{L}:{token}")));
        var link = await (await app.PostAsJsonAsync("/api/files/links", new { path = name, mode = "read" })).Content.ReadFromJsonAsync<JsonElement>();
        var linkId = link.GetProperty("id").GetString()!;
        Assert.NotNull(Links.Get(_fx.Db, linkId));

        var web = _fx.Client(signedIn: false, cookies: true);
        Assert.True((await web.PostAsJsonAsync("/api/auth/login", new { login = L, password = "geheim-123456" })).IsSuccessStatusCode);
        Assert.True((await web.DeleteAsync("/api/settings/app-passwords/" + id)).IsSuccessStatusCode);
        Assert.Null(Links.Get(_fx.Db, linkId));
    }
}
