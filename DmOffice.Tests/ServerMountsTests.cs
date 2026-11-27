using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Server-Mounts aller Arten (Schalter mountdrivers): Pfad im Container, WebDAV, SFTP, OneDrive, pCloud.</summary>
[Collection("server")]
public class ServerMountsTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ServerMountsTests(ChatFixture fx) => _fx = fx;

    private ServerConfig Config => _fx.Factory.Services.GetRequiredService<ServerConfig>();

    /// <summary>Mit eingeschaltetem (oder ausgeschaltetem) Schalter ausführen, danach wie vorher.</summary>
    private static async Task With(bool on, Func<Task> run)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(on ? before.Where(x => x != Features.MountDrivers) : before.Append(Features.MountDrivers));
        try { await run(); }
        finally { Features.Set(before); }
    }

    /// <summary>Im Browser angemeldet (Cookie) - die Verwaltung nimmt keine App-Passwörter an.</summary>
    private async Task<HttpClient> Browser(string login)
    {
        var c = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var r = await c.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return c;
    }

    private static object Body(params object[] folders) => new { folders, webDavForUsers = false, webDavPrivate = false };

    [Fact]
    public void Alte_Konfiguration_ist_ein_Pfad_im_Container()
    {
        // So gespeichert, bevor es andere Arten gab: ohne Kind
        var old = JsonSerializer.Deserialize<ExternalFolderDef>("""{"Id":"alt1","Name":"Musik","Path":"/external/musik","Users":[],"ReadOnly":true}""")!;
        Assert.Null(old.Kind);
        Assert.Equal(ServerMounts.Folder, ServerMounts.KindOf(old));
        Assert.True(ServerMounts.IsFolder(old));
        // Die Anmeldung beim Speichern wird nie mitgeschrieben
        old.Flow = null;
        Assert.DoesNotContain("Flow", JsonSerializer.Serialize(old));
        // Ohne Schalter gelten nur Pfade im Container
        var s = new ServerSettings { ExternalFolders = [old, new ExternalFolderDef { Id = "dav1", Kind = UserMount.WebDav, Url = "https://dav.example.org/" }] };
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set(before.Append(Features.MountDrivers));
            Assert.Equal(["alt1"], ServerMounts.Active(s).Select(f => f.Id));
            Features.Set(before.Where(x => x != Features.MountDrivers));
            Assert.Equal(["alt1", "dav1"], ServerMounts.Active(s).Select(f => f.Id));
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public async Task Treiber_nach_Art()
    {
        var root = Directory.CreateTempSubdirectory("dmsrvmount").FullName;
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set(before.Where(x => x != Features.MountDrivers && x != Features.Sftp));
            using (var d = ServerMounts.Open(Config, new ExternalFolderDef { Path = root })) Assert.IsType<FolderDriver>(d);
            using (var d = ServerMounts.Open(Config, new ExternalFolderDef { Kind = UserMount.WebDav, Url = "https://dav.example.org/x/", User = "u", Password = Config.Wrap("p") }))
                Assert.IsType<WebDavDriver>(d);
            using (var d = ServerMounts.Open(Config, new ExternalFolderDef { Kind = UserMount.WebDav, Url = "sftp://nas.example.org:2222/daten", User = "u", Password = Config.Wrap("p") }))
                Assert.IsType<SftpDriver>(d);
            using (var d = ServerMounts.Open(Config, new ExternalFolderDef { Kind = UserMount.OneDrive, RefreshToken = Config.Wrap("r"), Drive = "d", Item = "root" }))
                Assert.IsType<OneDriveDriver>(d);
            using (var d = ServerMounts.Open(Config, new ExternalFolderDef { Kind = UserMount.PCloud, Token = Config.Wrap("t"), Host = "eapi.pcloud.com", Folder = "Fotos" }))
                Assert.IsType<PCloudDriver>(d);
            Assert.Throws<KeyNotFoundException>(() => ServerMounts.Open(Config, new ExternalFolderDef { Kind = "ftp" }));
            await Task.CompletedTask;
        }
        finally { Features.Set(before); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Webdav_Mount_anlegen_Passwort_bleibt_auf_dem_Server()
    {
        var before = Config.Current.ExternalFolders;
        var check = ServerMounts.Check;
        var seen = new List<(string? Url, string? User, string? Password)>();
        ServerMounts.Check = (f, config, _) => { seen.Add((f.Url, f.User, config.Unwrap(f.Password))); return Task.CompletedTask; };
        try
        {
            await With(true, async () =>
            {
                var admin = await Browser("anna");
                var r = await admin.PutAsJsonAsync("/api/admin/external", Body(new
                {
                    id = "", name = "NAS", kind = "webdav", url = " https://dav.example.org/daten/ ", user = "max", password = "geheim-1", path = "", users = new[] { _fx.Id("cleo") }, readOnly = false,
                }));
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                var saved = Assert.Single(Config.Current.ExternalFolders);
                Assert.Equal(UserMount.WebDav, saved.Kind);
                Assert.Equal("https://dav.example.org/daten/", saved.Url);
                // Eingepackt gespeichert, geprüft wurde mit dem Klartext
                Assert.NotEqual("geheim-1", saved.Password);
                Assert.Equal("geheim-1", Config.Unwrap(saved.Password));
                Assert.Equal(("https://dav.example.org/daten/", "max", "geheim-1"), Assert.Single(seen));
                Assert.Equal(ExternalFolderDef.LevelNames, saved.SearchLevel);

                // Zurück an den Browser: ohne Passwort, nur dass es eins gibt
                var text = await admin.GetStringAsync("/api/admin/external");
                Assert.DoesNotContain("geheim-1", text);
                Assert.DoesNotContain(saved.Password!, text);
                var view = JsonDocument.Parse(text).RootElement.GetProperty("folders")[0];
                Assert.True(view.GetProperty("passwordSet").GetBoolean());
                Assert.Equal("webdav", view.GetProperty("kind").GetString());
                Assert.False(view.TryGetProperty("password", out _));

                // So zurückgeschickt, wie die Seite ihn bekam (Name geändert): Passwort bleibt, keine neue Prüfung, Kennung bleibt
                var echo = JsonSerializer.Deserialize<Dictionary<string, object?>>(view.GetRawText())!;
                echo["name"] = "NAS Keller";
                Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/external", Body(echo))).StatusCode);
                var again = Assert.Single(Config.Current.ExternalFolders);
                Assert.Equal(saved.Id, again.Id);
                Assert.Equal("NAS Keller", again.Name);
                Assert.Equal("geheim-1", Config.Unwrap(again.Password));
                Assert.Single(seen);

                // Ein mitgeschicktes Token wird nicht übernommen
                echo["refreshToken"] = "eingeschleust"; echo["token"] = "eingeschleust";
                Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/external", Body(echo))).StatusCode);
                Assert.Null(Config.Current.ExternalFolders[0].RefreshToken);
                Assert.Null(Config.Current.ExternalFolders[0].Token);

                // Anderer Server ohne Passwort: abgelehnt; mit neuem Passwort: geprüft und gespeichert
                echo["url"] = "https://anders.example.org/";
                var bad = await admin.PutAsJsonAsync("/api/admin/external", Body(echo));
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                Assert.Contains("Passwort", await bad.Content.ReadAsStringAsync());
                echo["password"] = "geheim-2";
                Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/external", Body(echo))).StatusCode);
                Assert.Equal("geheim-2", Config.Unwrap(Config.Current.ExternalFolders[0].Password));
                Assert.Equal(2, seen.Count);

                // Sichtbar nur für den freigegebenen Benutzer, als Server-Mount („folder“)
                var cleo = await _fx.As("cleo").GetFromJsonAsync<JsonElement>("/api/files/external/mounts");
                var m = Assert.Single(cleo.GetProperty("mounts").EnumerateArray(), x => x.GetProperty("id").GetString() == saved.Id);
                Assert.Equal("folder", m.GetProperty("kind").GetString());
                var dora = await _fx.As("dora").GetFromJsonAsync<JsonElement>("/api/files/external/mounts");
                Assert.DoesNotContain(dora.GetProperty("mounts").EnumerateArray(), x => x.GetProperty("id").GetString() == saved.Id);

                // Keine Verbindung: Meldung, nichts gespeichert
                ServerMounts.Check = (_, _, _) => throw new HttpRequestException("Keine Antwort");
                echo["password"] = "geheim-3";
                var down = await admin.PutAsJsonAsync("/api/admin/external", Body(echo));
                Assert.Equal(HttpStatusCode.BadRequest, down.StatusCode);
                Assert.Contains("Verbindung fehlgeschlagen", await down.Content.ReadAsStringAsync());
                Assert.Equal("geheim-2", Config.Unwrap(Config.Current.ExternalFolders[0].Password));
            });
        }
        finally { ServerMounts.Check = check; Config.Update(s => s.ExternalFolders = before); }
    }

    [Fact]
    public async Task Ort_aendern_behaelt_Kennung_und_bereinigt_Auswahl()
    {
        var before = Config.Current.ExternalFolders;
        var check = ServerMounts.Check;
        var checks = 0;
        ServerMounts.Check = (_, _, _) => { checks++; return Task.CompletedTask; };
        try
        {
            await With(true, async () =>
            {
                Config.Update(s => s.ExternalFolders = [new ExternalFolderDef
                {
                    Id = "sftp1", Name = "NAS", Kind = UserMount.WebDav, Url = "sftp://nas.invalid/~/alt", User = "u", Password = Config.Wrap("pw"),
                    Include = ["appdata", "borg"], ContentFolders = ["borg"], SearchLevel = ExternalFolderDef.LevelNames,
                }]);
                var admin = await Browser("anna");
                var view = (await admin.GetFromJsonAsync<JsonElement>("/api/admin/external")).GetProperty("folders")[0];
                var echo = JsonSerializer.Deserialize<Dictionary<string, object?>>(view.GetRawText())!;

                // Nur etwas anderes geändert: Auswahl bleibt, keine Prüfung
                echo["readOnly"] = true;
                Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/external", Body(echo))).StatusCode);
                Assert.Equal(["appdata", "borg"], Config.Current.ExternalFolders[0].Include);
                Assert.Equal(0, checks);

                // Anderer Ordner auf demselben Server: geprüft, Kennung und Passwort bleiben; die alte Auswahl gilt dort nicht
                // (der neue Ort lässt sich hier nicht auflisten - dann bleibt nichts davon übrig)
                echo["url"] = "sftp://nas.invalid/~/shares/storage";
                Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/external", Body(echo))).StatusCode);
                var now = Assert.Single(Config.Current.ExternalFolders);
                Assert.Equal("sftp1", now.Id);
                Assert.Equal("sftp://nas.invalid/~/shares/storage", now.Url);
                Assert.Equal("pw", Config.Unwrap(now.Password));
                Assert.Equal(1, checks);
                Assert.Empty(now.Include);
                Assert.Empty(now.ContentFolders);
                Assert.NotEqual(ServerMounts.Location(new ExternalFolderDef { Kind = UserMount.WebDav, Url = "sftp://nas.invalid/~/alt", User = "u" }), ServerMounts.Location(now));
            });
        }
        finally { ServerMounts.Check = check; Config.Update(s => s.ExternalFolders = before); }
    }

    [Fact]
    public async Task OneDrive_und_pCloud_nur_mit_Anmeldung()
    {
        var before = Config.Current.ExternalFolders;
        try
        {
            await With(true, async () =>
            {
                var admin = await Browser("anna");
                foreach (var kind in new[] { "onedrive", "pcloud" })
                {
                    var r = await admin.PutAsJsonAsync("/api/admin/external", Body(new { id = "", name = "Wolke", kind, path = "", users = Array.Empty<string>(), readOnly = true }));
                    Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
                    Assert.Contains("anmelden", await r.Content.ReadAsStringAsync());
                    // Eine fremde oder ausgedachte Anmeldung zählt nicht
                    r = await admin.PutAsJsonAsync("/api/admin/external", Body(new { id = "", name = "Wolke", kind, flow = "ausgedacht", path = "", users = Array.Empty<string>(), readOnly = true }));
                    Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
                }
                Assert.Empty(Config.Current.ExternalFolders);
                // Unbekannte Art
                Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/external",
                    Body(new { id = "", name = "X", kind = "ftp", path = "", users = Array.Empty<string>(), readOnly = true }))).StatusCode);
            });
        }
        finally { Config.Update(s => s.ExternalFolders = before); }
    }

    [Fact]
    public async Task Ohne_Schalter_wie_bisher()
    {
        var before = Config.Current.ExternalFolders;
        var root = Directory.CreateTempSubdirectory("dmsrvmount-alt").FullName;
        try
        {
            await With(false, async () =>
            {
                // Ein Eintrag anderer Art (etwa nach einem Wechsel auf eine ältere Version) wirkt nicht und bleibt beim Speichern erhalten
                var dav = new ExternalFolderDef { Id = "dav9", Name = "Wolke", Kind = UserMount.WebDav, Url = "https://dav.example.org/", Password = Config.Wrap("x") };
                Config.Update(s => s.ExternalFolders = [dav]);
                var admin = await Browser("anna");
                var list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/external");
                Assert.Empty(list.GetProperty("folders").EnumerateArray());
                Assert.DoesNotContain((await _fx.As("cleo").GetFromJsonAsync<JsonElement>("/api/files/external/mounts")).GetProperty("mounts").EnumerateArray(),
                    m => m.GetProperty("id").GetString() == "dav9");
                Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("cleo").GetAsync("/api/files/external/list?mount=dav9")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/external/server/dav9/top")).StatusCode);

                // Pfad im Container wie bisher: ohne Art gespeichert
                var r = await admin.PutAsJsonAsync("/api/admin/external", Body(new { id = "", name = "Musik", path = root, users = Array.Empty<string>(), readOnly = true }));
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                var folders = Config.Current.ExternalFolders;
                Assert.Equal(2, folders.Count);
                var music = folders.Single(f => f.Name == "Musik");
                Assert.Null(music.Kind);
                Assert.Equal(root, music.Path);
                Assert.Equal("x", Config.Unwrap(folders.Single(f => f.Id == "dav9").Password));

                // Andere Arten lassen sich nicht anlegen
                r = await admin.PutAsJsonAsync("/api/admin/external", Body(new { id = "", name = "Neu", kind = "webdav", url = "https://dav.example.org/", path = "", users = Array.Empty<string>(), readOnly = true }));
                Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/external/server/onedrive/start", null)).StatusCode);
            });
        }
        finally { Config.Update(s => s.ExternalFolders = before); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Benutzer_legen_keine_Server_Mounts_an()
    {
        var before = Config.Current.ExternalFolders;
        try
        {
            await With(true, async () =>
            {
                var cleo = await Browser("cleo");
                // Mit App-Passwort ist die Verwaltung ohnehin gesperrt
                Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.As("anna").GetAsync("/api/admin/external")).StatusCode);
                var r = await cleo.PutAsJsonAsync("/api/admin/external", Body(new { id = "", name = "Meins", kind = "webdav", url = "https://dav.example.org/", password = "x", path = "", users = Array.Empty<string>(), readOnly = false }));
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await cleo.GetAsync("/api/admin/external")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await cleo.PostAsync("/api/admin/external/server/onedrive/start", null)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await cleo.GetAsync("/api/admin/external/server/pcloud/app")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await cleo.GetAsync("/api/admin/external/server/x/top")).StatusCode);
                Assert.Empty(Config.Current.ExternalFolders);
            });
        }
        finally { Config.Update(s => s.ExternalFolders = before); }
    }

    [Fact]
    public async Task Suche_liest_ueber_den_Treiber()
    {
        var services = _fx.Factory.Services;
        var index = services.GetRequiredService<ExternalIndex>();
        var root = Directory.CreateTempSubdirectory("dmsrvmount-idx").FullName;
        var before = Config.Current.ExternalFolders;
        var open = index.OpenRemote;
        try
        {
            await With(true, async () =>
            {
                // Ein „entfernter“ Mount, dessen Treiber hier ein Ordner ist: die Suche darf nur über den Treiber lesen
                Directory.CreateDirectory(Path.Combine(root, "Briefe"));
                File.WriteAllText(Path.Combine(root, "Briefe", "anwalt.txt"), "Schreiben vom Notar");
                var opened = 0;
                index.OpenRemote = f => { Interlocked.Increment(ref opened); return new FolderDriver(root); };
                var def = new ExternalFolderDef
                {
                    Id = "ferne", Name = "Ferne", Kind = UserMount.WebDav, Url = "sftp://nas.example.org/daten", User = "u", Users = [_fx.Id("cleo")],
                    SearchLevel = ExternalFolderDef.LevelContents, SearchGroups = [ExternalFolderDef.GroupText],
                };
                Config.Update(s => s.ExternalFolders = [def]);
                await ExternalIndexer.RunOnceAsync(index, ServerMounts.Active(Config.Current), 100, default, forceScan: true);
                Assert.True(opened > 0);
                var cleo = _fx.As("cleo");
                var hits = (await cleo.GetFromJsonAsync<JsonElement>("/api/files/external/search?q=notar")).GetProperty("items").EnumerateArray().ToList();
                var hit = Assert.Single(hits);
                Assert.Equal("Briefe/anwalt.txt", hit.GetProperty("path").GetString());
                Assert.Empty((await _fx.As("dora").GetFromJsonAsync<JsonElement>("/api/files/external/search?q=notar")).GetProperty("items").EnumerateArray());

                // Andere Adresse: der Index wird neu aufgebaut
                Config.Update(s => s.ExternalFolders[0].Url = "sftp://anders.example.org/daten");
                index.OpenRemote = _ => throw new HttpRequestException("nicht erreichbar");
                await ExternalIndexer.RunOnceAsync(index, ServerMounts.Active(Config.Current), 100, default, forceScan: true);
                Assert.Empty((await cleo.GetFromJsonAsync<JsonElement>("/api/files/external/search?q=notar")).GetProperty("items").EnumerateArray());
            });
        }
        finally
        {
            index.OpenRemote = open;
            Config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, true);
        }
    }
}
