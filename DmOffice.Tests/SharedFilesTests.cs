using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Office;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Geteilte Ordner der Dateiablage: Lesen/Bearbeiten, Widerruf, Außenstehende, WebDAV, Papierkorb und Kontingent beim Besitzer.</summary>
[Collection("server")]
public class SharedFilesTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public SharedFilesTests(SharedCalendarFixture fx) => _fx = fx;

    private const string Mit = "Mit mir geteilt";
    private static string Von(string folder, string owner = "Anna") => $"{Mit}/{folder} (von {owner})";

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static string Q(string s) => Uri.EscapeDataString(s);

    private async Task<HttpResponseMessage> Upload(string login, string dir, string name, string text, string? conflict = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", name } };
        return await _fx.As(login).PostAsync($"/api/files/upload?path={Q(dir)}" + (conflict != null ? "&conflict=" + conflict : ""), form);
    }

    private async Task Mkdir(string login, string path) =>
        Assert.Equal(HttpStatusCode.OK, (await _fx.As(login).PostAsJsonAsync("/api/files/mkdir", new { path })).StatusCode);

    /// <summary>Anna teilt einen Ordner mit den Genannten; Rechte: "ben" = nur lesen.</summary>
    private async Task Share(string path, string[] members, string[]? readOnly = null)
    {
        var body = new { path, members = members.Select(m => _fx.Users[m].Id).ToArray(), readOnly = (readOnly ?? []).Select(m => _fx.Users[m].Id).ToArray() };
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("anna").PutAsJsonAsync("/api/files/shares", body)).StatusCode);
    }

    private async Task<JsonElement> Items(string login, string path)
    {
        var r = await _fx.As(login).GetAsync($"/api/files?path={Q(path)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("items");
    }

    private async Task<HttpStatusCode> Get(string login, string url) => (await _fx.As(login).GetAsync(url)).StatusCode;

    private static string Unique(string name) => name + "-" + Guid.NewGuid().ToString("N")[..6];

    private UserDb DbOf(string login) => _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users[login].Id).Db;

    [Fact]
    public async Task Lesen_und_Bearbeiten_mit_Unterordnern_und_Widerruf()
    {
        var f = Unique("Fotos");
        await Mkdir("anna", f);
        await Mkdir("anna", f + "/Urlaub");
        Assert.Equal(HttpStatusCode.OK, (await Upload("anna", f + "/Urlaub", "strand.txt", "Meer")).StatusCode);
        await Mkdir("anna", Unique("Privat"));
        await Share(f, ["ben", "cleo"], ["ben"]);

        var mine = Von(f);
        // Sichtbar bei Ben und Cleo samt Unterordner, bei Dora nichts
        var shared = await Json(await _fx.As("ben").GetAsync("/api/files/shared"));
        var entry = shared.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("path").GetString() == mine);
        Assert.False(entry.GetProperty("canWrite").GetBoolean());
        Assert.Equal("Anna", entry.GetProperty("owner").GetString());
        Assert.Empty((await Json(await _fx.As("dora").GetAsync("/api/files/shared"))).GetProperty("items").EnumerateArray());
        // In der Wurzel steht "Mit mir geteilt" wie ein Ordner (so zeigt es auch die Handy-App), bei Dora nicht
        Assert.Contains((await Items("ben", "")).EnumerateArray(), x => x.GetProperty("path").GetString() == Mit && x.GetProperty("isDir").GetBoolean());
        var top = await Items("ben", Mit);
        Assert.Contains(top.EnumerateArray(), x => x.GetProperty("name").GetString() == $"{f} (von Anna)");
        Assert.Single((await Items("ben", mine)).EnumerateArray());

        // Lesen: ansehen, herunterladen, Suche
        var file = mine + "/Urlaub/strand.txt";
        var dl = await _fx.As("ben").GetAsync($"/api/files/download?path={Q(file)}");
        Assert.Equal("Meer", await dl.Content.ReadAsStringAsync());
        var hits = await Json(await _fx.As("ben").GetAsync("/api/files/search?q=strand"));
        Assert.Contains(hits.GetProperty("items").EnumerateArray(), x => x.GetProperty("path").GetString() == file);

        // Ben darf nicht schreiben, löschen, verschieben, Ordner anlegen
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload("ben", mine, "x.txt", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync("/api/files/mkdir", new { path = mine + "/neu" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync("/api/files/delete", new { paths = new[] { file } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync("/api/files/save", new { path = file, text = "neu", create = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync("/api/files/move", new { from = file, to = mine + "/b.txt", copy = false })).StatusCode);

        // Cleo darf: hochladen, anlegen, umbenennen, verschieben, speichern
        Assert.Equal(HttpStatusCode.OK, (await Upload("cleo", mine + "/Urlaub", "berg.txt", "Berg")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").PostAsJsonAsync("/api/files/mkdir", new { path = mine + "/Neu" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").PostAsJsonAsync("/api/files/move", new { from = mine + "/Urlaub/berg.txt", to = mine + "/Neu/berg.txt", copy = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").PostAsJsonAsync("/api/files/save", new { path = mine + "/Neu/berg.txt", text = "Gipfel", create = false })).StatusCode);
        // Alles liegt in der Ablage des Besitzers
        Assert.Equal("Gipfel", Encoding.UTF8.GetString(FileStore.Data(DbOf("anna"), f + "/Neu/berg.txt")!));
        // Anna sieht den Ordner als geteilt
        Assert.Contains((await Items("anna", "")).EnumerateArray(), x => x.GetProperty("path").GetString() == f && x.GetProperty("sharedWith").GetBoolean());

        // Den geteilten Ordner selbst kann Cleo nicht löschen oder umbenennen; "verschieben" in die eigene Ablage kopiert nur
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PostAsJsonAsync("/api/files/delete", new { paths = new[] { mine } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").PostAsJsonAsync("/api/files/move", new { from = mine, to = "Eigen", copy = false })).StatusCode);
        Assert.NotNull(FileStore.Get(DbOf("anna"), f));
        Assert.NotNull(FileStore.Get(DbOf("cleo"), "Eigen/Neu/berg.txt"));

        // Widerruf wirkt sofort
        await Share(f, ["cleo"]);
        Assert.Equal(HttpStatusCode.NotFound, await Get("ben", $"/api/files?path={Q(mine)}"));
        Assert.Equal(HttpStatusCode.NotFound, await Get("ben", $"/api/files/download?path={Q(file)}"));
        await Share(f, []);
        Assert.Equal(HttpStatusCode.NotFound, await Get("cleo", $"/api/files?path={Q(mine)}"));
        Assert.DoesNotContain((await Json(await _fx.As("cleo").GetAsync("/api/files/shared"))).GetProperty("items").EnumerateArray(), x => x.GetProperty("folder").GetString() == f);
    }

    [Fact]
    public async Task Außenstehende_sehen_nichts_weder_ueber_Pfad_noch_Datei_ID_noch_Vorschau()
    {
        var f = Unique("Akten");
        await Mkdir("anna", f);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var form = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "pixel.png" } };
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("anna").PostAsync($"/api/files/upload?path={Q(f)}", form)).StatusCode);
        var db = DbOf("anna");
        var fileId = FileStore.Get(db, f + "/pixel.png")!.FileId;
        await Share(f, ["ben"]);

        // Ben mit Freigabe: Vorschau über Pfad und über die Datei-ID (Nextcloud) - die ID beim Mitglied trägt die Freigabe
        var shareId = (await Json(await _fx.As("ben").GetAsync("/api/files/shared"))).GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("folder").GetString() == f).GetProperty("id").GetInt32();
        var benId = ((long)shareId << SharedFiles.IdShift) + fileId;
        Assert.Equal(HttpStatusCode.OK, await Get("ben", $"/api/files/thumb?path={Q(Von(f) + "/pixel.png")}"));
        Assert.Equal(HttpStatusCode.OK, await Get("ben", $"/index.php/core/preview?fileId={benId}&x=32&y=32"));

        // Dora hat keine Freigabe: weder Pfad noch ID noch Vorschau, auch nicht mit geratenen Nummern
        var path = Von(f);
        Assert.Equal(HttpStatusCode.NotFound, await Get("dora", $"/api/files?path={Q(path)}"));
        Assert.Equal(HttpStatusCode.NotFound, await Get("dora", $"/api/files/thumb?path={Q(path + "/pixel.png")}"));
        Assert.Equal(HttpStatusCode.NotFound, await Get("dora", $"/api/files/download?path={Q(path + "/pixel.png")}"));
        Assert.Equal(HttpStatusCode.NotFound, await Get("dora", $"/index.php/core/preview?fileId={benId}&x=32&y=32"));
        Assert.Equal(HttpStatusCode.NotFound, await Get("dora", $"/index.php/core/preview?fileId={fileId}&x=32&y=32"));
        Assert.Equal(HttpStatusCode.NotFound, await Get("dora", $"/index.php/core/preview?file={Q(path + "/pixel.png")}"));
        Assert.Empty((await Json(await _fx.As("dora").GetAsync("/api/files/search?q=pixel"))).GetProperty("items").EnumerateArray().ToList());
        // Auch die Freigabe-Verwaltung ist Sache des Besitzers
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PutAsJsonAsync("/api/files/shares", new { path = Von(f), members = new[] { _fx.Users["dora"].Id }, readOnly = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync("/api/files/links", new { path = Von(f), mode = "read" })).StatusCode);
    }

    [Fact]
    public async Task Nur_Ordner_und_nicht_notizi()
    {
        var f = Unique("Doku");
        await Mkdir("anna", f);
        await Upload("anna", f, "a.txt", "a");
        // Einzelne Dateien teilt man per Link
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("anna").PutAsJsonAsync("/api/files/shares", new { path = f + "/a.txt", members = new[] { _fx.Users["ben"].Id }, readOnly = Array.Empty<string>() })).StatusCode);
        // Der Abgleichordner von notizi und alles darin ist tabu - auch ein Ordner darüber, der ihn enthält
        var space = _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id);
        var root = FileStore.Normalize(space.Settings.Notizi.Root);
        FileStore.EnsureDir(space.Db, root + "/Unter");
        foreach (var p in new[] { root, root + "/Unter" })
        {
            var r = await _fx.As("anna").PutAsJsonAsync("/api/files/shares", new { path = p, members = new[] { _fx.Users["ben"].Id }, readOnly = Array.Empty<string>() });
            Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{p}: {r.StatusCode}");
        }
        Assert.Empty((await Json(await _fx.As("ben").GetAsync("/api/files/shared"))).GetProperty("items").EnumerateArray().Where(x => x.GetProperty("path").GetString()!.Contains("notizi", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Gelöschtes_kommt_in_den_Papierkorb_des_Besitzers_und_Quota_zaehlt_dort()
    {
        var f = Unique("Team");
        await Mkdir("anna", f);
        await Upload("anna", f, "alt.txt", "alt");
        await Share(f, ["cleo"]);
        var mine = Von(f);

        // Löschen durch Cleo: Papierkorb von Anna, nicht von Cleo
        var del = await _fx.As("cleo").PostAsJsonAsync("/api/files/delete", new { paths = new[] { mine + "/alt.txt" } });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Contains(FileStore.Trash(DbOf("anna")), t => t.Path == f + "/alt.txt");
        Assert.DoesNotContain(FileStore.Trash(DbOf("cleo")), t => t.Path.EndsWith("alt.txt"));
        Assert.Empty((await Json(await _fx.As("cleo").GetAsync("/api/files/trash"))).GetProperty("items").EnumerateArray().Where(x => x.GetProperty("path").GetString()!.EndsWith("alt.txt")));

        // Überschreiben: frühere Version beim Besitzer
        await Upload("anna", f, "v.txt", "eins");
        Assert.Equal(HttpStatusCode.OK, (await Upload("cleo", mine, "v.txt", "zwei")).StatusCode);
        var versions = FileStore.Versions(DbOf("anna"), FileStore.Get(DbOf("anna"), f + "/v.txt")!.FileId);
        Assert.NotEmpty(versions);

        // Kontingent: das des Besitzers, nicht das des Empfängers
        var annaId = _fx.Users["anna"].Id;
        var before = FileStore.QuotaOf;
        FileStore.QuotaOf = u => u == annaId ? UserStorage.Counted(DbOf("anna")) + 100 : 0;
        try
        {
            var big = await Upload("cleo", mine, "gross.bin", new string('x', 500));
            Assert.Equal(HttpStatusCode.InsufficientStorage, big.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Upload("cleo", mine, "klein.bin", "ok")).StatusCode);
            // Kopie in die eigene Ablage zählt bei Cleo (unbegrenzt) - und das Original bleibt
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").PostAsJsonAsync("/api/files/move", new { from = mine + "/klein.bin", to = "klein.bin", copy = false })).StatusCode);
        }
        finally { FileStore.QuotaOf = before; }
        Assert.NotNull(FileStore.Get(DbOf("anna"), f + "/klein.bin"));
        Assert.NotNull(FileStore.Get(DbOf("cleo"), "klein.bin"));
        // Eigene Ablage von Cleo wächst, Annas Belegung ohne die Kopie
        Assert.Equal(2, FileStore.Get(DbOf("cleo"), "klein.bin")!.Size);
    }

    [Fact]
    public async Task Eigenes_in_den_geteilten_Ordner_und_Verschieben_heraus_ist_Kopieren()
    {
        var f = Unique("Austausch");
        await Mkdir("anna", f);
        await Share(f, ["cleo"]);
        await Upload("cleo", "", "eigen.txt", "von Cleo");
        var mine = Von(f);
        // Hinein: kopiert zum Besitzer, bei Cleo weg
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").PostAsJsonAsync("/api/files/move", new { from = "eigen.txt", to = mine + "/eigen.txt", copy = false })).StatusCode);
        Assert.Equal("von Cleo", Encoding.UTF8.GetString(FileStore.Data(DbOf("anna"), f + "/eigen.txt")!));
        Assert.Null(FileStore.Get(DbOf("cleo"), "eigen.txt"));
        // Heraus: Kopie, Original bleibt beim Besitzer
        var r = await _fx.As("cleo").PostAsJsonAsync("/api/files/move", new { from = mine + "/eigen.txt", to = "zurueck.txt", copy = false });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await Json(r)).GetProperty("copied").GetBoolean());
        Assert.NotNull(FileStore.Get(DbOf("anna"), f + "/eigen.txt"));
        Assert.Equal("von Cleo", Encoding.UTF8.GetString(FileStore.Data(DbOf("cleo"), "zurueck.txt")!));
    }

    private static Task<HttpResponseMessage> Dav(HttpClient c, string method, string url, string? body = null, string? depth = null, params (string, string)[] headers)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/octet-stream");
        if (depth != null) req.Headers.Add("Depth", depth);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        return c.SendAsync(req);
    }

    private static async Task<List<string>> Hrefs(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.MultiStatus, r.StatusCode);
        XNamespace d = "DAV:";
        return XDocument.Parse(await r.Content.ReadAsStringAsync()).Descendants(d + "href").Select(h => Uri.UnescapeDataString(h.Value)).ToList();
    }

    [Fact]
    public async Task WebDAV_zeigt_Geteilt_mit_mir_im_Wurzelverzeichnis_mit_Rechten()
    {
        var f = Unique("Dav");
        await Mkdir("anna", f);
        await Upload("anna", f, "d.txt", "Inhalt");
        await Share(f, ["ben", "cleo"], ["ben"]);
        var folder = $"{f} (von Anna)";

        foreach (var prefix in new[] { "/remote.php/webdav/", "/remote.php/dav/files/ben/", "/dav/files/ben/" })
        {
            var ben = _fx.As("ben");
            var root = await Hrefs(await Dav(ben, "PROPFIND", prefix, depth: "1"));
            Assert.Contains(root, h => h.TrimEnd('/').EndsWith(Mit));
            var inShared = await Hrefs(await Dav(ben, "PROPFIND", prefix + Mit + "/", depth: "1"));
            Assert.Contains(inShared, h => h.TrimEnd('/').EndsWith(folder));
            var inFolder = await Hrefs(await Dav(ben, "PROPFIND", prefix + $"{Mit}/{folder}/", depth: "1"));
            Assert.Contains(inFolder, h => h.EndsWith("d.txt"));
            Assert.Equal("Inhalt", await (await ben.GetAsync(prefix + $"{Mit}/{folder}/d.txt")).Content.ReadAsStringAsync());
        }

        // Rechte als oc:permissions: Lesen ohne W/D/N/V/C/K
        const string body = """<?xml version="1.0"?><d:propfind xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns"><d:prop><oc:permissions/><oc:owner-id/></d:prop></d:propfind>""";
        async Task<string> Perm(string login, string path)
        {
            var r = await Dav(_fx.As(login), "PROPFIND", path, body, "0");
            return XDocument.Parse(await r.Content.ReadAsStringAsync()).Descendants(XNamespace.Get("http://owncloud.org/ns") + "permissions").Single().Value;
        }
        var file = $"/remote.php/webdav/{Mit}/{Uri.EscapeDataString(folder)}/d.txt";
        Assert.Equal("SG", await Perm("ben", file));
        Assert.Contains("W", await Perm("cleo", file));

        // Lesender Zugriff: schreiben und löschen verboten; schreibender: erlaubt, landet beim Besitzer
        var b = _fx.As("ben");
        Assert.Equal(HttpStatusCode.Forbidden, (await Dav(b, "PUT", file.Replace("d.txt", "neu.txt"), "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Dav(b, "DELETE", file)).StatusCode);
        var c = _fx.As("cleo");
        Assert.Equal(HttpStatusCode.Created, (await Dav(c, "PUT", file.Replace("d.txt", "von-cleo.txt"), "Hallo")).StatusCode);
        Assert.Equal("Hallo", Encoding.UTF8.GetString(FileStore.Data(DbOf("anna"), f + "/von-cleo.txt")!));
        Assert.Equal(HttpStatusCode.NoContent, (await Dav(c, "DELETE", file.Replace("d.txt", "von-cleo.txt"))).StatusCode);
        Assert.Contains(FileStore.Trash(DbOf("anna")), t => t.Path == f + "/von-cleo.txt");
        // In der Wurzel ist "Mit mir geteilt" selbst nur lesbar
        Assert.Equal(HttpStatusCode.NotFound, (await Dav(c, "PUT", $"/remote.php/webdav/{Mit}/x.txt", "x")).StatusCode);

        // Dora: der Ordner Mit mir geteilt ist leer/nicht da, der Pfad des anderen unerreichbar
        var dora = _fx.As("dora");
        Assert.DoesNotContain(await Hrefs(await Dav(dora, "PROPFIND", "/remote.php/webdav/", depth: "1")), h => h.TrimEnd('/').EndsWith(Mit));
        Assert.Equal(HttpStatusCode.NotFound, (await dora.GetAsync($"/remote.php/webdav/{Mit}/{Uri.EscapeDataString(folder)}/d.txt")).StatusCode);
        // Widerruf wirkt sofort
        await Share(f, ["cleo"]);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(file)).StatusCode);
    }

    [Fact]
    public async Task Office_Token_gilt_nur_solange_die_Freigabe_besteht()
    {
        var f = Unique("Office");
        await Mkdir("anna", f);
        await Upload("anna", f, "t.txt", "Text");
        await Share(f, ["ben", "cleo"], ["ben"]);
        var annaFile = FileStore.Get(DbOf("anna"), f + "/t.txt")!;
        var services = _fx.Factory.Services;
        string Token(string member) => WopiTokens.Issue(services, new WopiGrant(_fx.Users["anna"].Id, annaFile.FileId, true, "http://localhost", "http://localhost",
            services.GetRequiredService<UserStore>().ById(_fx.Users[member].Id)!.Stamp, null, member, V: _fx.Users[member].Id), purpose: WopiTokens.OnlyOfficePurpose).Token;

        var anna = _fx.As("anna");
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync($"/onlyoffice/file?t={Q(Token("cleo"))}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync($"/onlyoffice/file?t={Q(Token("ben"))}")).StatusCode);
        // Ben darf trotz Token mit W nicht schreiben
        Assert.False(SharedFiles.Allows(services, _fx.Users["ben"].Id, _fx.Users["anna"].Id, annaFile).Write);
        Assert.True(SharedFiles.Allows(services, _fx.Users["cleo"].Id, _fx.Users["anna"].Id, annaFile).Write);
        // Dora hat keine Freigabe: auch ein (geratener) Token trägt nichts
        Assert.Equal(HttpStatusCode.Unauthorized, (await anna.GetAsync($"/onlyoffice/file?t={Q(Token("dora"))}")).StatusCode);
        // Widerruf beendet auch laufende Sitzungen
        await Share(f, ["ben"], ["ben"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anna.GetAsync($"/onlyoffice/file?t={Q(Token("cleo"))}")).StatusCode);
    }

    [Fact]
    public async Task Personenfreigaben_erscheinen_beim_Besitzer_und_der_Ordner_im_Papierkorb_ruht()
    {
        var f = Unique("Ueberblick");
        await Mkdir("anna", f);
        await Share(f, ["ben"]);
        var list = (await Json(await _fx.As("anna").GetAsync("/api/files/shares"))).GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("item").GetProperty("path").GetString() == f);
        Assert.Equal("ben", list.GetProperty("members").EnumerateArray().Single().GetProperty("login").GetString());
        // Ordner in den Papierkorb: bei Ben verschwindet er, nach dem Zurückholen ist er wieder da
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("anna").PostAsJsonAsync("/api/files/delete", new { paths = new[] { f } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await Get("ben", $"/api/files?path={Q(Von(f))}"));
        var trash = FileStore.Trash(DbOf("anna")).First(t => t.Path == f);
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("anna").PostAsJsonAsync("/api/files/trash/restore", new { ids = new[] { trash.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Get("ben", $"/api/files?path={Q(Von(f))}"));
    }
}
