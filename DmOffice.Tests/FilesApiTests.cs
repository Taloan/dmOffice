using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Testserver, bei dem jede Anfrage ihre Absenderadresse aus "X-Test-Ip" bekommt (Bremse je Adresse).</summary>
public sealed class FilesServerFixture : IDisposable
{
    public const string Login = "berta";
    public WebApplicationFactory<Program> Factory { get; }
    public string AppPassword { get; }
    public UserDb Db { get; }
    private readonly DirectoryInfo _data;

    private sealed class TestIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Ip", out var ip)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return n(ctx);
            });
            next(app);
        };
    }

    public FilesServerFixture()
    {
        _data = Directory.CreateTempSubdirectory("dmfilesapi");
        Environment.SetEnvironmentVariable("DMOFFICE_DATA", _data.FullName);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddTransient<IStartupFilter, TestIp>()));
        var users = Factory.Services.GetRequiredService<UserStore>();
        var u = users.Create(Login, "Berta Beispiel", "geheim-123456", isAdmin: false);
        Db = Factory.Services.GetRequiredService<Spaces>().Get(u.Id).Db;
        AppPassword = AppPasswords.Create(Db, "Test");
    }

    /// <summary>Angemeldet (App-Passwort) oder anonym; jeder Client mit eigener Adresse.</summary>
    public HttpClient Client(bool signedIn = true, bool cookies = false)
    {
        var c = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = cookies });
        c.DefaultRequestHeaders.Add("X-Test-Ip", $"10.{Random.Shared.Next(255)}.{Random.Shared.Next(255)}.{Random.Shared.Next(1, 255)}");
        if (signedIn)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Login}:{AppPassword}")));
        return c;
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { _data.Delete(true); } catch (IOException) { /* Datenbank noch offen */ }
    }
}

/// <summary>Die Dateiablage ueber /api/files und die oeffentlichen Links ueber /api/public.</summary>
[Collection("server")]
public class FilesApiTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public FilesApiTests(FilesServerFixture fx) => _fx = fx;
    private const string L = FilesServerFixture.Login;

    private static async Task Upload(HttpClient c, string dir, string name, byte[] data, string? conflict = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(data), "files", name } };
        var r = await c.PostAsync($"/api/files/upload?path={Uri.EscapeDataString(dir)}" + (conflict != null ? "&conflict=" + conflict : ""), form);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task Dav(HttpClient c, string method, string url, string? body = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) req.Content = new StringContent(body);
        var r = await c.SendAsync(req);
        // MKCOL auf Vorhandenes (anderer Test war schneller): 405
        Assert.True(r.IsSuccessStatusCode || r.StatusCode == (HttpStatusCode)207 || method == "MKCOL" && r.StatusCode == HttpStatusCode.MethodNotAllowed,
            $"{method} {url}: {(int)r.StatusCode}");
    }

    [Fact]
    public async Task Details_zu_Meine_Dateien_liefern_Groesse_und_Datum()
    {
        var c = _fx.Client();
        await Upload(c, "", "wurzel.txt", Encoding.UTF8.GetBytes("12345"));
        var info = await Json(await c.GetAsync("/api/files/info"));   // so fragt die Oberflaeche (leerer Pfad faellt weg)
        Assert.True(info.GetProperty("size").GetInt64() >= 5);
        Assert.True(info.GetProperty("item").GetProperty("modified").GetDateTime() > DateTime.UtcNow.AddDays(-1));
    }

    [Fact]
    public async Task Papierkorb_ueber_die_Schnittstelle_und_WebDAV()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "pk" });
        await Upload(c, "pk", "a.txt", Encoding.UTF8.GetBytes("aaa"));
        await Upload(c, "pk", "b.txt", Encoding.UTF8.GetBytes("bbb"));
        var del = await Json(await c.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "pk/a.txt" } }));
        var id = del.GetProperty("trash")[0].GetInt64();
        // Ueber WebDAV geloescht: auch im Papierkorb
        await Dav(c, "DELETE", $"/dav/files/{L}/pk/b.txt");
        var trash = await Json(await c.GetAsync("/api/files/trash"));
        var paths = trash.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()).ToList();
        Assert.Contains("pk/a.txt", paths);
        Assert.Contains("pk/b.txt", paths);
        Assert.Equal(30, trash.GetProperty("days").GetInt32());

        var restored = await Json(await c.PostAsJsonAsync("/api/files/trash/restore", new { ids = new[] { id } }));
        Assert.Equal("pk/a.txt", restored.GetProperty("restored")[0].GetString());
        Assert.Equal("aaa", await c.GetStringAsync("/api/files/download?path=pk/a.txt"));

        Assert.True((await c.PutAsJsonAsync("/api/files/trash/settings", new { days = 7 })).IsSuccessStatusCode);
        Assert.Equal(7, (await Json(await c.GetAsync("/api/files/trash"))).GetProperty("days").GetInt32());
        await c.PostAsync("/api/files/trash/empty", null);
        Assert.Empty((await Json(await c.GetAsync("/api/files/trash"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Hochladen_mit_Aenderungszeit_legt_Unterordner_an()
    {
        // Automatisches Hochladen der Handy-App: Zielordner nach Datum gibt es noch nicht, Zeit der Aufnahme bleibt
        var c = _fx.Client();
        var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "files", "IMG_1.jpg" } };
        var ms = new DateTimeOffset(2021, 5, 4, 3, 2, 1, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var r = await Json(await c.PostAsync($"/api/files/upload?path=Fotos/2021/05&conflict=skip&mtime={ms}", form));
        Assert.Equal("Fotos/2021/05/IMG_1.jpg", r.GetProperty("files")[0].GetProperty("path").GetString());
        var item = (await Json(await c.GetAsync("/api/files?path=Fotos/2021/05"))).GetProperty("items")[0];
        Assert.Equal(3, item.GetProperty("size").GetInt64());
        Assert.Equal(ms, DateTimeOffset.Parse(item.GetProperty("modified").GetString()!).ToUnixTimeMilliseconds());
        // Nochmal mit skip: bleibt, wird als uebersprungen gemeldet
        form = new MultipartFormDataContent { { new ByteArrayContent([9]), "files", "IMG_1.jpg" } };
        r = await Json(await c.PostAsync($"/api/files/upload?path=Fotos/2021/05&conflict=skip&mtime={ms}", form));
        Assert.True(r.GetProperty("files")[0].GetProperty("skipped").GetBoolean());
    }

    [Fact]
    public async Task Konflikte_beim_Hochladen_und_Verschieben()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "kf" });
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "kf/ziel" });
        await Upload(c, "kf", "x.txt", Encoding.UTF8.GetBytes("1"));
        await Upload(c, "kf/ziel", "x.txt", Encoding.UTF8.GetBytes("2"));
        var ex = await Json(await c.PostAsJsonAsync("/api/files/exists", new { paths = new[] { "kf/x.txt", "kf/y.txt" } }));
        Assert.Equal("kf/x.txt", Assert.Single(ex.GetProperty("existing").EnumerateArray()).GetString());

        var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "files", "x.txt" } };
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/api/files/upload?path=kf&conflict=fail", form)).StatusCode);
        await Upload(c, "kf", "x.txt", Encoding.UTF8.GetBytes("3"), "rename");
        Assert.Equal("3", await c.GetStringAsync("/api/files/download?path=kf/x (2).txt"));

        var r = await c.PostAsJsonAsync("/api/files/move", new { from = "kf/x.txt", to = "kf/ziel/x.txt", copy = false });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("\"conflict\":true", await r.Content.ReadAsStringAsync());
        var moved = await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "kf/x.txt", to = "kf/ziel/x.txt", copy = true, conflict = "rename" }));
        Assert.Equal("kf/ziel/x (2).txt", moved.GetProperty("path").GetString());
        var replaced = await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "kf/x.txt", to = "kf/ziel/x.txt", copy = false, conflict = "replace" }));
        Assert.True(replaced.GetProperty("replaced").GetInt64() > 0);
        Assert.Equal("1", await c.GetStringAsync("/api/files/download?path=kf/ziel/x.txt"));
        var skipped = await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "kf/x (2).txt", to = "kf/ziel/x.txt", copy = false, conflict = "skip" }));
        Assert.True(skipped.GetProperty("skipped").GetBoolean());
    }

    [Fact]
    public async Task Zip_eines_Ordners_und_der_Wurzel_ohne_notizi()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "zp" });
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "zp/unter" });
        await Upload(c, "zp", "eins.txt", Encoding.UTF8.GetBytes("eins"));
        await Upload(c, "zp/unter", "zwei.txt", Encoding.UTF8.GetBytes("zwei"));
        await Dav(c, "MKCOL", $"/dav/files/{L}/notizisync");
        await Dav(c, "PUT", $"/dav/files/{L}/notizisync/geheim.bin", "geheim");

        var r = await c.GetAsync("/api/files/zip?paths=zp&name=zp");
        Assert.Equal("application/zip", r.Content.Headers.ContentType?.MediaType);
        using (var zip = new ZipArchive(await r.Content.ReadAsStreamAsync()))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("zp/eins.txt", names);
            Assert.Contains("zp/unter/zwei.txt", names);
            using var sr = new StreamReader(zip.GetEntry("zp/unter/zwei.txt")!.Open());
            Assert.Equal("zwei", sr.ReadToEnd());
        }
        // Auswahl per Formular, ganze Ablage: ohne den notizi-Ordner
        var form = new FormUrlEncodedContent([new("paths", ""), new("name", "alles")]);
        var all = await c.PostAsync("/api/files/zip", form);
        using var zipAll = new ZipArchive(await all.Content.ReadAsStreamAsync());
        Assert.Contains("zp/eins.txt", zipAll.Entries.Select(e => e.FullName));
        Assert.DoesNotContain(zipAll.Entries, e => e.FullName.Contains("notizisync") || e.FullName.Contains("geheim"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/files/zip?paths=notizisync")).StatusCode);
    }

    [Fact]
    public async Task Notizi_Ordner_bleibt_fuer_alle_neuen_Funktionen_unsichtbar()
    {
        var c = _fx.Client();
        await Dav(c, "MKCOL", $"/dav/files/{L}/notizisync");
        await Dav(c, "PUT", $"/dav/files/{L}/notizisync/suchmich.bin", "x");
        // Suchen, Zuletzt, Vorschaubild, Freigabe, Favorit, Loeschen
        Assert.DoesNotContain("suchmich", await c.GetStringAsync("/api/files/search?q=suchmich"));
        Assert.DoesNotContain("suchmich", await c.GetStringAsync("/api/files/recent"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/files/thumb?path=notizisync/suchmich.bin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/files/links", new { path = "notizisync", mode = "read" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/files/favorite", new { path = "notizisync", on = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "notizisync/suchmich.bin" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/files/info?path=notizisync")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/files/move", new { from = "notizisync", to = "weg", copy = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/files/save", new { path = "notizisync/neu.txt", text = "x", create = true })).StatusCode);
        // Ueber WebDAV im notizi-Ordner geloescht: endgueltig, nicht im Papierkorb
        await Dav(c, "DELETE", $"/dav/files/{L}/notizisync/suchmich.bin");
        Assert.DoesNotContain("suchmich", await c.GetStringAsync("/api/files/trash"));
    }

    [Fact]
    public async Task Versionen_Favoriten_Suche_und_Textdatei()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "fs" });
        var created = await Json(await c.PostAsJsonAsync("/api/files/save", new { path = "fs/notiz.md", text = "# Eins", create = true }));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/files/save", new { path = "fs/notiz.md", text = "x", create = true })).StatusCode);
        var etag = created.GetProperty("etag").GetString();
        await Json(await c.PostAsJsonAsync("/api/files/save", new { path = "fs/notiz.md", text = "# Zwei", etag }));
        // Alter ETag: inzwischen geaendert
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/files/save", new { path = "fs/notiz.md", text = "# Drei", etag })).StatusCode);

        var versions = (await Json(await c.GetAsync("/api/files/versions?path=fs/notiz.md"))).GetProperty("items");
        var v = Assert.Single(versions.EnumerateArray()).GetProperty("id").GetInt64();
        Assert.Equal("# Eins", await c.GetStringAsync($"/api/files/download?path=fs/notiz.md&version={v}"));
        await Json(await c.PostAsJsonAsync("/api/files/versions/restore", new { path = "fs/notiz.md", id = v }));
        Assert.Equal("# Eins", await c.GetStringAsync("/api/files/download?path=fs/notiz.md"));

        Assert.True((await c.PostAsJsonAsync("/api/files/favorite", new { path = "fs", on = true })).IsSuccessStatusCode);
        var favs = await Json(await c.GetAsync("/api/files/favorites"));
        Assert.Equal("fs", Assert.Single(favs.GetProperty("items").EnumerateArray()).GetProperty("path").GetString());
        var list = await Json(await c.GetAsync("/api/files/?path="));
        Assert.True(list.GetProperty("items").EnumerateArray().First(i => i.GetProperty("path").GetString() == "fs").GetProperty("favorite").GetBoolean());

        var hits = await Json(await c.GetAsync("/api/files/search?q=NOTIZ&type=text"));
        Assert.Contains(hits.GetProperty("items").EnumerateArray(), i => i.GetProperty("path").GetString() == "fs/notiz.md");
        Assert.Empty((await Json(await c.GetAsync("/api/files/search?q=notiz&type=image"))).GetProperty("items").EnumerateArray());
        Assert.Contains("fs/notiz.md", await c.GetStringAsync("/api/files/recent"));
    }

    [Fact]
    public async Task Vorschaubild_und_Bereichsanfragen_ueber_die_Schnittstelle()
    {
        var c = _fx.Client();
        using var bmp = new SKBitmap(800, 400);
        using (var cv = new SKCanvas(bmp)) cv.Clear(SKColors.Orange);
        await Upload(c, "", "bild.png", SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray());
        var r = await c.GetAsync("/api/files/thumb?path=bild.png&size=256");
        Assert.Equal("image/webp", r.Content.Headers.ContentType?.MediaType);
        using var thumb = SKBitmap.Decode(await r.Content.ReadAsByteArrayAsync());
        Assert.Equal(256, thumb.Width);

        await Upload(c, "", "zahlen.txt", Encoding.ASCII.GetBytes("0123456789"));
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/files/download?path=zahlen.txt");
        req.Headers.Range = new RangeHeaderValue(3, 5);
        var part = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("345", await part.Content.ReadAsStringAsync());
        // Inline nur fuer sichere Arten: HTML kommt als Download
        await Upload(c, "", "seite.html", Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
        var html = await c.GetAsync("/api/files/download?path=seite.html&inline=true");
        Assert.Equal("application/octet-stream", html.Content.Headers.ContentType?.MediaType);
    }

    private async Task<string> Share(HttpClient c, string path, string mode, string? password = null, DateTime? expires = null)
    {
        var link = await Json(await c.PostAsJsonAsync("/api/files/links", new { path, mode, password, expires }));
        return link.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task Oeffentlicher_Link_nur_lesen_mit_Pfadschutz()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "teilen" });
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "teilen/unter" });
        await Upload(c, "teilen/unter", "info.txt", Encoding.UTF8.GetBytes("öffentlich"));
        await Upload(c, "", "privat.txt", Encoding.UTF8.GetBytes("privat"));
        var token = await Share(c, "teilen", "read");
        Assert.True(token.Length >= 22);

        var anon = _fx.Client(signedIn: false);
        var info = await Json(await anon.GetAsync($"/api/public/{token}"));
        Assert.Equal("teilen", info.GetProperty("name").GetString());
        Assert.Equal("Berta Beispiel", info.GetProperty("owner").GetString());
        Assert.Equal("unter", Assert.Single(info.GetProperty("items").EnumerateArray()).GetProperty("path").GetString());
        Assert.Equal("öffentlich", await anon.GetStringAsync($"/api/public/{token}/download?path=unter/info.txt"));
        // Pfad-Tricks: nichts ausserhalb der Freigabe
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync($"/api/public/{token}/download?path=../privat.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync($"/api/public/{token}/download?path=unter/..%2F..%2Fprivat.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/{token}/download?path=privat.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/{token}/download?path=%2Fprivat.txt")).StatusCode);
        // Nur lesen: kein Hochladen
        var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "files", "boese.txt" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.PostAsync($"/api/public/{token}/upload", form)).StatusCode);
        // ZIP des freigegebenen Ordners
        using (var zip = new ZipArchive(await (await anon.GetAsync($"/api/public/{token}/zip")).Content.ReadAsStreamAsync()))
            Assert.Contains("teilen/unter/info.txt", zip.Entries.Select(e => e.FullName));
        // Uebersicht und Widerruf
        var links = await Json(await c.GetAsync("/api/files/links"));
        var link = links.GetProperty("items").EnumerateArray().First(l => l.GetProperty("token").GetString() == token);
        Assert.True(link.GetProperty("downloads").GetInt64() >= 2);
        Assert.True((await c.DeleteAsync($"/api/files/links/{link.GetProperty("id").GetString()}")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/{token}")).StatusCode);
    }

    [Fact]
    public async Task Oeffentlicher_Link_der_Wurzel_zeigt_den_notizi_Ordner_nicht()
    {
        var c = _fx.Client();
        await Dav(c, "MKCOL", $"/dav/files/{L}/notizisync");
        await Dav(c, "PUT", $"/dav/files/{L}/notizisync/db.bin", "geheim");
        var token = await Share(c, "", "read");
        var anon = _fx.Client(signedIn: false);
        Assert.DoesNotContain("notizisync", await anon.GetStringAsync($"/api/public/{token}"));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/{token}?path=notizisync")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/{token}/download?path=notizisync/db.bin")).StatusCode);
        var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "files", "notizisync/x.bin" } };
        // Nur lesen ohnehin - auch als Upload-Link kein Weg hinein
        var up = await Share(c, "", "upload");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsync($"/api/public/{up}/upload", form)).StatusCode);
        using var zip = new ZipArchive(await (await anon.GetAsync($"/api/public/{token}/zip")).Content.ReadAsStreamAsync());
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("notizisync"));
    }

    [Fact]
    public async Task Oeffentlicher_Link_mit_Passwort_Ablauf_und_Hochladen()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "eingang" });
        await Upload(c, "eingang", "vorhanden.txt", Encoding.UTF8.GetBytes("v"));

        // Passwort: erst 401, falsches 403, richtiges setzt ein Cookie
        var pw = await Share(c, "eingang", "read", password: "sesam-öffne");
        var anon = _fx.Client(signedIn: false, cookies: true);
        var locked = await anon.GetAsync($"/api/public/{pw}");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("needsPassword", await locked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.PostAsJsonAsync($"/api/public/{pw}/auth", new { password = "falsch" })).StatusCode);
        Assert.True((await anon.PostAsJsonAsync($"/api/public/{pw}/auth", new { password = "sesam-öffne" })).IsSuccessStatusCode);
        Assert.True((await anon.GetAsync($"/api/public/{pw}")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Client(signedIn: false).GetAsync($"/api/public/{pw}/download?path=vorhanden.txt")).StatusCode);

        // Abgelaufen
        var old = await Share(c, "eingang", "read", expires: DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync($"/api/public/{old}")).StatusCode);

        // Nur hochladen: Inhalt bleibt verborgen, belegte Namen bekommen eine Nummer, nichts wird ueberschrieben
        var drop = await Share(c, "eingang", "drop");
        var d = await Json(await anon.GetAsync($"/api/public/{drop}"));
        Assert.Empty(d.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync($"/api/public/{drop}/download?path=vorhanden.txt")).StatusCode);
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("neu")), "files", "vorhanden.txt" } };
        form.Last().Headers.ContentType = new MediaTypeHeaderValue("text/html");
        var up = await Json(await anon.PostAsync($"/api/public/{drop}/upload", form));
        // Einwurf: der Inhalt bleibt verborgen - kein (umbenannter) Name zurueck, nur die Zahl
        Assert.Equal(JsonValueKind.Null, up.GetProperty("files").ValueKind);
        Assert.Equal(1, up.GetProperty("count").GetInt32());
        Assert.Equal("v", await c.GetStringAsync("/api/files/download?path=eingang/vorhanden.txt"));
        Assert.Equal("neu", await c.GetStringAsync("/api/files/download?path=eingang/vorhanden (2).txt"));

        // Datei-Link kann kein Hochladen
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/links", new { path = "eingang/vorhanden.txt", mode = "upload" })).StatusCode);
    }

    [Fact]
    public async Task Durchprobieren_von_Links_wird_gebremst()
    {
        var anon = _fx.Client(signedIn: false);
        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/{Dav_Links_NewToken()}")).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await anon.GetAsync($"/api/public/{Dav_Links_NewToken()}")).StatusCode);
        // Andere Adresse: nicht betroffen (der echte Link geht dort weiter)
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "bremse" });
        var token = await Share(c, "bremse", "read");
        Assert.True((await _fx.Client(signedIn: false).GetAsync($"/api/public/{token}")).IsSuccessStatusCode);
    }

    private static string Dav_Links_NewToken() => DmOffice.Dav.Links.NewToken();
}
