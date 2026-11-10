using System.Net;
using System.Net.Http.Json;
using System.Text;
using DmOffice.Core;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Externe Ordner in der Dateiablage: Ordner auf dem Server (vom Admin freigegeben) und eigene WebDAV-Ordner.</summary>
[Collection("server")]
public class ExternalFoldersTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public ExternalFoldersTests(FilesServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Uebertragen_in_Galerie_Ordner_legt_Vorschau_gleich_an()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var me = users.ByLogin(FilesServerFixture.Login)!.Id;
        var root = Directory.CreateTempSubdirectory("dmext-gal").FullName;
        var before = config.Current.ExternalFolders;
        var galleryBefore = DmOffice.Gallery.GalleryFolders.Of(_fx.Db);
        var name = "Fotos-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "galnas", Name = "NAS", Path = root, Users = [me] }]);
            Directory.CreateDirectory(Path.Combine(root, "RAW"));
            var src = new DmOffice.Gallery.GallerySource { Mount = "galnas", Path = "RAW" };
            DmOffice.Gallery.GalleryFolders.Save(_fx.Db, [.. galleryBefore ?? [], src]);
            DmOffice.Dav.FileStore.EnsureDir(_fx.Db, name);
            using (var bmp = new SkiaSharp.SKBitmap(300, 200)) { using (var cv = new SkiaSharp.SKCanvas(bmp)) cv.Clear(SkiaSharp.SKColors.SteelBlue);
                DmOffice.Dav.FileStore.Put(_fx.Db, name + "/bild.jpg", bmp.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80).ToArray(), "image/jpeg"); }

            var c = _fx.Client();
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/files/external/transfer", new { fromMount = (string?)null, paths = new[] { name }, toMount = "galnas", to = "RAW", move = true })).StatusCode);
            byte[]? thumb = null;
            for (var i = 0; i < 100 && thumb == null; i++)
            {
                await Task.Delay(100);
                thumb = _fx.Db.Query("SELECT thumb FROM gallery_ext WHERE source = $s AND path = $p", r => r.IsDBNull(0) ? null : (byte[])r.GetValue(0),
                    ("$s", src.Id), ("$p", name + "/bild.jpg")).FirstOrDefault();
            }
            // Eintrag mit Vorschau ist da, ohne dass die Galerie den Ordner eingelesen hat
            Assert.NotNull(thumb);
            // Verschoben: an der Quelle endgültig weg, nicht im Papierkorb (es liegt ja im Ziel)
            for (var i = 0; i < 50 && DmOffice.Dav.FileStore.Get(_fx.Db, name) != null; i++) await Task.Delay(100);
            Assert.Null(DmOffice.Dav.FileStore.Get(_fx.Db, name));
            Assert.DoesNotContain(DmOffice.Dav.FileStore.Trash(_fx.Db), t => t.Path == name || t.Path.StartsWith(name + "/"));
        }
        finally
        {
            DmOffice.Gallery.GalleryFolders.Save(_fx.Db, galleryBefore ?? []);
            config.Update(s => s.ExternalFolders = before); Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Uebertragen_zwischen_Ablage_und_Serverordner()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var me = users.ByLogin(FilesServerFixture.Login)!.Id;
        var root = Directory.CreateTempSubdirectory("dmext-tr").FullName;
        var before = config.Current.ExternalFolders;
        var name = "Urlaub-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "nas", Name = "NAS", Path = root, Users = [me] }]);
            DmOffice.Dav.FileStore.EnsureDir(_fx.Db, name + "/Tag 2");
            DmOffice.Dav.FileStore.Put(_fx.Db, name + "/a.jpg", "AAA"u8.ToArray(), "image/jpeg");
            DmOffice.Dav.FileStore.Put(_fx.Db, name + "/Tag 2/b.jpg", "BB"u8.ToArray(), "image/jpeg");
            var c = _fx.Client();
            async Task Done(Func<bool> ok) { for (var i = 0; i < 100 && !ok(); i++) await Task.Delay(100); Assert.True(ok()); }

            // Ablage → Serverordner (kopieren): beide Dateien samt Unterordner dort, das Original bleibt
            var r = await c.PostAsJsonAsync("/api/files/external/transfer", new { fromMount = (string?)null, paths = new[] { name }, toMount = "nas", to = "", move = false });
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            await Done(() => File.Exists(Path.Combine(root, name, "Tag 2", "b.jpg")));
            Assert.Equal("AAA", File.ReadAllText(Path.Combine(root, name, "a.jpg")));
            // Änderungszeit der Quelle bleibt (sonst sortierte die Galerie die Fotos auf den Tag der Übertragung)
            Assert.True(Math.Abs((File.GetLastWriteTimeUtc(Path.Combine(root, name, "a.jpg")) - DmOffice.Dav.FileStore.Get(_fx.Db, name + "/a.jpg")!.Modified.ToUniversalTime()).TotalSeconds) < 2);
            Assert.NotNull(DmOffice.Dav.FileStore.Get(_fx.Db, name + "/a.jpg"));

            // Noch einmal dorthin: gleichnamig vorhanden - die Vorschau nennt Umfang und Konflikt, ohne Wahl kommt 409
            var preview = await (await c.PostAsJsonAsync("/api/files/external/transfer/preview", new { fromMount = (string?)null, paths = new[] { name }, toMount = "nas", to = "", move = false })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(2, preview.GetProperty("files").GetInt32());
            Assert.Equal(2, preview.GetProperty("dirs").GetInt32());
            Assert.Equal(5, preview.GetProperty("bytes").GetInt32());
            Assert.True(preview.GetProperty("conflicts")[0].GetProperty("folder").GetBoolean());
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/files/external/transfer", new { fromMount = (string?)null, paths = new[] { name }, toMount = "nas", to = "", move = false })).StatusCode);

            // Zusammenführen und verschieben, vorhandene Dateien behalten: eine neue Datei kommt dazu, die vorhandene bleibt im Ziel
            // unverändert und an der Quelle (übersprungen), die übertragene ist an der Quelle weg
            File.WriteAllText(Path.Combine(root, name, "a.jpg"), "ALT");
            DmOffice.Dav.FileStore.Put(_fx.Db, name + "/neu.jpg", "N"u8.ToArray(), "image/jpeg");
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/files/external/transfer", new { fromMount = (string?)null, paths = new[] { name }, toMount = "nas", to = "", move = true, conflict = "skip" })).StatusCode);
            await Done(() => File.Exists(Path.Combine(root, name, "neu.jpg")) && DmOffice.Dav.FileStore.Get(_fx.Db, name + "/neu.jpg") == null);
            Assert.Equal("ALT", File.ReadAllText(Path.Combine(root, name, "a.jpg")));
            Assert.NotNull(DmOffice.Dav.FileStore.Get(_fx.Db, name + "/a.jpg"));

            // Serverordner → Ablage (verschieben) in einen anderen Ordner: dort angekommen, im Serverordner weg
            DmOffice.Dav.FileStore.EnsureDir(_fx.Db, "zurueck-" + name);
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/files/external/transfer", new { fromMount = "nas", paths = new[] { name }, toMount = (string?)null, to = "zurueck-" + name, move = true })).StatusCode);
            await Done(() => !Directory.Exists(Path.Combine(root, name)));
            Assert.Equal("BB", Encoding.UTF8.GetString(DmOffice.Dav.FileStore.Data(_fx.Db, $"zurueck-{name}/{name}/Tag 2/b.jpg")!));
        }
        finally { config.Update(s => s.ExternalFolders = before); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Nicht_erreichbarer_Ordner_wird_gemeldet()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var me = users.ByLogin(FilesServerFixture.Login)!.Id;
        var root = Directory.CreateTempSubdirectory("dmext-da").FullName;
        var gone = Path.Combine(Path.GetTempPath(), "dmext-weg-" + Guid.NewGuid().ToString("N"));
        var before = config.Current.ExternalFolders;
        try
        {
            config.Update(s => s.ExternalFolders =
            [
                new ExternalFolderDef { Id = "erreichbar", Name = "Da", Path = root, Users = [me] },
                new ExternalFolderDef { Id = "abgehaengt", Name = "Weg", Path = gone, Users = [me] },
            ]);
            var mounts = (await _fx.Client().GetFromJsonAsync<System.Text.Json.JsonElement>("/api/files/external/mounts")).GetProperty("mounts").EnumerateArray()
                .ToDictionary(m => m.GetProperty("id").GetString()!, m => m.GetProperty("available").GetBoolean());
            Assert.True(mounts["erreichbar"]);
            Assert.False(mounts["abgehaengt"]);
        }
        finally { config.Update(s => s.ExternalFolders = before); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Ordner_auf_dem_Server_blaettern_schreiben_lesen()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var me = users.ByLogin(FilesServerFixture.Login)!.Id;
        var root = Directory.CreateTempSubdirectory("dmext").FullName;
        var other = Directory.CreateTempSubdirectory("dmext-anderer").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            config.Update(s => s.ExternalFolders =
            [
                new ExternalFolderDef { Id = "musik", Name = "Musik", Path = root, Users = [me] },
                new ExternalFolderDef { Id = "archiv", Name = "Archiv", Path = root, Users = [me], ReadOnly = true },
                new ExternalFolderDef { Id = "fremd", Name = "Fremd", Path = other, Users = ["jemand-anderes"] },
            ]);
            var c = _fx.Client();
            var mounts = await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/files/external/mounts");
            var ids = mounts.GetProperty("mounts").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
            Assert.Contains("musik", ids);
            Assert.DoesNotContain("fremd", ids);
            // Platz fuer die Speicherplatz-Seite
            var space = await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/files/external/space");
            var musik = space.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "musik");
            Assert.True(musik.GetProperty("free").GetInt64() > 0);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, musik.GetProperty("error").ValueKind);

            // Hochladen und auflisten
            using (var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Hallo extern")), "file", "gruss.txt" } })
                Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/files/external/upload?mount=musik&path=Unterordner", form)).StatusCode);
            Assert.True(File.Exists(Path.Combine(root, "Unterordner", "gruss.txt")));
            var list = await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/files/external/list?mount=musik&path=Unterordner");
            Assert.Contains(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("name").GetString() == "gruss.txt");

            // Zwischenspeicher: an dmOffice vorbei abgelegt erscheint erst mit „Aktualisieren“ (fresh) - oder nach dem Auffrischen im Hintergrund
            File.WriteAllText(Path.Combine(root, "Unterordner", "direkt.txt"), "vorbei");
            Assert.DoesNotContain((await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/files/external/list?mount=musik&path=Unterordner")).GetProperty("items").EnumerateArray(),
                i => i.GetProperty("name").GetString() == "direkt.txt");
            Assert.Contains((await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/files/external/list?mount=musik&path=Unterordner&fresh=true")).GetProperty("items").EnumerateArray(),
                i => i.GetProperty("name").GetString() == "direkt.txt");
            File.Delete(Path.Combine(root, "Unterordner", "direkt.txt"));

            // Vorschaubild für die Kachelansicht (einmal gemacht, dann aus dem Zwischenspeicher)
            using (var bmp = new SkiaSharp.SKBitmap(400, 300))
            {
                using (var cv = new SkiaSharp.SKCanvas(bmp)) cv.Clear(SkiaSharp.SKColors.Orange);
                File.WriteAllBytes(Path.Combine(root, "bild.jpg"), bmp.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80).ToArray());
            }
            for (int n = 0; n < 2; n++)
            {
                var th = await c.GetAsync("/api/files/external/thumb?mount=musik&path=bild.jpg");
                Assert.Equal(HttpStatusCode.OK, th.StatusCode);
                Assert.Equal("image/webp", th.Content.Headers.ContentType?.MediaType);
            }
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/files/external/thumb?mount=musik&path=Unterordner/gruss.txt")).StatusCode);

            // Herunterladen: als Datei, nie als Seite
            var res = await c.GetAsync("/api/files/external/download?mount=musik&path=Unterordner/gruss.txt");
            Assert.Equal("Hallo extern", await res.Content.ReadAsStringAsync());
            Assert.Equal("application/octet-stream", res.Content.Headers.ContentType?.MediaType);

            // Umbenennen, Ordner anlegen, loeschen
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/files/external/move", new { mount = "musik", from = "Unterordner/gruss.txt", to = "Unterordner/hallo.txt" })).StatusCode);
            Assert.True(File.Exists(Path.Combine(root, "Unterordner", "hallo.txt")));
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/files/external/mkdir", new { mount = "musik", path = "Neu" })).StatusCode);
            Assert.True(Directory.Exists(Path.Combine(root, "Neu")));
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/files/external/delete", new { mount = "musik", paths = new[] { "Neu", "Unterordner/hallo.txt" } })).StatusCode);
            Assert.False(Directory.Exists(Path.Combine(root, "Neu")));

            // Nur lesen
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/external/mkdir", new { mount = "archiv", path = "X" })).StatusCode);
            // Fremder Ordner und Ausbrechen
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/files/external/list?mount=fremd")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/files/external/list?mount=musik&path=../..")).StatusCode);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true); Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task Eigener_WebDav_Ordner_nicht_ins_Heimnetz()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var c = _fx.Client();
        try
        {
            // Nicht freigeschaltet
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/external/mounts", new { name = "NAS", url = "http://192.168.178.2/dav/" })).StatusCode);
            config.Update(s => { s.ExternalWebDavForUsers = true; s.ExternalWebDavPrivate = false; });
            foreach (var url in new[] { "http://127.0.0.1:1/dav/", "http://192.168.178.2/dav/", "http://localhost:1/" })
            {
                var res = await c.PostAsJsonAsync("/api/files/external/mounts", new { name = "NAS", url });
                Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
                Assert.Contains("Heimnetz", await res.Content.ReadAsStringAsync());
            }
            Assert.True(ExternalFolders.Private(IPAddress.Parse("10.1.2.3")));
            Assert.True(ExternalFolders.Private(IPAddress.Parse("::ffff:192.168.1.1")));
            Assert.True(ExternalFolders.Private(IPAddress.Parse("fd00::1")));
            Assert.False(ExternalFolders.Private(IPAddress.Parse("88.198.1.1")));
            foreach (var ip in new[] { "64:ff9b::a9fe:a9fe", "2002:c0a8:101::1", "::7f00:1", "198.18.0.1", "192.0.0.8" })
                Assert.True(ExternalFolders.Private(IPAddress.Parse(ip)), ip);
            foreach (var ip in new[] { "169.254.169.254", "::ffff:127.0.0.1", "64:ff9b::7f00:1", "fe80::1", "0.0.0.0", "239.1.1.1" })
                Assert.True(ExternalFolders.NeverAllowed(IPAddress.Parse(ip)), ip);
            foreach (var ip in new[] { "192.168.178.2", "10.0.0.5", "fd00::1" })
                Assert.False(ExternalFolders.NeverAllowed(IPAddress.Parse(ip)), ip);
        }
        finally { config.Update(s => { s.ExternalWebDavForUsers = false; s.ExternalWebDavPrivate = false; }); }
    }

    [Fact]
    public async Task Externes_Ziel_bearbeiten()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
        var space = spaces.Get(users.ByLogin(FilesServerFixture.Login)!.Id);
        var other = spaces.Get(users.Create("fremd" + Guid.NewGuid().ToString("N")[..6], "Fremd", "geheim-123456", isAdmin: false).Id);
        var check = ExternalFolders.CheckWebDav;
        var seen = new List<UserMount>();
        ExternalFolders.CheckWebDav = (m, _, _) =>
        {
            seen.Add(m);
            return m.Password == "falsch" ? throw new HttpRequestException("401 Unauthorized") : Task.CompletedTask;
        };
        var c = _fx.Client();
        string? id = null;
        try
        {
            config.Update(s => s.ExternalWebDavForUsers = true);
            var added = await c.PostAsJsonAsync("/api/files/external/mounts", new { name = "Nextcloud", url = "https://a.example/dav/", user = "anna", password = "alt" });
            added.EnsureSuccessStatusCode();
            id = (await added.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;
            // Eine Einbindung darauf (ohne Verbindung angelegt) und ein Ziel eines anderen Benutzers
            var list = ExternalFolders.Own(space);
            var bound = new UserMount { Name = "Fotos", Kind = UserMount.WebDav, Access = id, Path = "Fotos" };
            list.Add(bound);
            space.Db.SetJson(ExternalFolders.UserKey, list);
            var foreign = new UserMount { Name = "Fremd", Kind = UserMount.WebDav, Url = "https://f.example/", Password = "f" };
            ExternalFolders.Own(other);   // noch leer: gilt als umgestellt, das Ziel bleibt ein Ziel
            other.Db.SetJson(ExternalFolders.UserKey, new List<UserMount> { foreign });

            // Andere Adresse ohne Passwort: das gespeicherte geht nicht an den neuen Server
            var res = await c.PutAsJsonAsync("/api/files/external/mounts/" + id, new { name = "Cloud", url = "https://b.example/dav/", user = "anna", password = "" });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.NotEqual("https://b.example/dav/", seen.LastOrDefault()?.Url);
            res = await c.PutAsJsonAsync("/api/files/external/mounts/" + id, new { name = "Cloud", url = "https://b.example/dav/", user = "anna", password = "alt" });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var target = ExternalFolders.Own(space).Single(m => m.Id == id);
            Assert.Equal(("Cloud", "https://b.example/dav/", "alt"), (target.Name, target.Url, target.Password));
            Assert.Equal("alt", seen[^1].Password);

            // Zum Bearbeiten: Adresse und Benutzer, das Passwort nicht
            var edit = await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/files/external/mounts/" + id);
            Assert.Equal(("https://b.example/dav/", "anna", true), (edit.GetProperty("url").GetString(), edit.GetProperty("user").GetString(), edit.GetProperty("passwordSet").GetBoolean()));
            Assert.DoesNotContain("alt", edit.GetRawText());

            // Nur umbenennen: keine neue Verbindungsprüfung
            var checks = seen.Count;
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/files/external/mounts/" + id, new { name = "Cloud 2" })).StatusCode);
            Assert.Equal(checks, seen.Count);
            target = ExternalFolders.Own(space).Single(m => m.Id == id);
            Assert.Equal(("Cloud 2", "https://b.example/dav/", "anna", "alt"), (target.Name, target.Url, target.User, target.Password));

            // Neues Passwort; ein falsches ändert nichts
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/files/external/mounts/" + id, new { name = "Cloud 2", password = "neu" })).StatusCode);
            Assert.Equal("neu", ExternalFolders.Own(space).Single(m => m.Id == id).Password);
            var bad = await c.PutAsJsonAsync("/api/files/external/mounts/" + id, new { name = "Kaputt", password = "falsch" });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("Verbindung fehlgeschlagen", await bad.Content.ReadAsStringAsync());
            target = ExternalFolders.Own(space).Single(m => m.Id == id);
            Assert.Equal(("Cloud 2", "neu"), (target.Name, target.Password));
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/files/external/mounts/" + id, new { name = " " })).StatusCode);

            // Die Einbindung zeigt weiter auf das Ziel (gleiche Id), mit den neuen Zugangsdaten
            var mount = ExternalFolders.MountsOf(space, config).Single(m => m.Id == bound.Id);
            Assert.Equal((id, "Fotos", "b.example"), (mount.Access, mount.Path, mount.Account));
            Assert.Contains(ExternalFolders.TargetsOf(space, config), m => m.Id == id && m.Name == "Cloud 2");

            // Die Einbindung umbenennen (Name in Dateien), die Id bleibt; leer behält den Namen
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/files/external/mounts/{bound.Id}/include", new { include = new[] { "A" }, name = " Bilder " })).StatusCode);
            var renamed = ExternalFolders.MountsOf(space, config).Single(m => m.Id == bound.Id);
            Assert.Equal(("Bilder", id), (renamed.Name, renamed.Access));
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/files/external/mounts/{bound.Id}/include", new { include = Array.Empty<string>() })).StatusCode);
            Assert.Equal("Bilder", ExternalFolders.MountsOf(space, config).Single(m => m.Id == bound.Id).Name);

            // Eine Einbindung ist kein Ziel, ein fremdes Ziel nicht zu ändern
            Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync("/api/files/external/mounts/" + bound.Id, new { name = "X" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync("/api/files/external/mounts/" + foreign.Id, new { name = "X", password = "p" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/files/external/mounts/" + foreign.Id)).StatusCode);
            var kept = ExternalFolders.Own(other).Single();
            Assert.Equal(("Fremd", "f"), (kept.Name, kept.Password));
        }
        finally
        {
            ExternalFolders.CheckWebDav = check;
            if (id != null) await c.DeleteAsync("/api/files/external/mounts/" + id);
            config.Update(s => s.ExternalWebDavForUsers = false);
        }
    }

    [Fact]
    public async Task Mehrere_kopieren_verschieben_loeschen()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var me = users.ByLogin(FilesServerFixture.Login)!.Id;
        var root = Directory.CreateTempSubdirectory("dmext-mehr").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            config.Update(s => s.ExternalFolders =
            [
                new ExternalFolderDef { Id = "mehr", Name = "Mehr", Path = root, Users = [me] },
                new ExternalFolderDef { Id = "mehr-lesen", Name = "Lesen", Path = root, Users = [me], ReadOnly = true },
            ]);
            File.WriteAllText(Path.Combine(root, "a.txt"), "A");
            Directory.CreateDirectory(Path.Combine(root, "Ordner", "Unter"));
            File.WriteAllText(Path.Combine(root, "Ordner", "Unter", "b.txt"), "B");
            Directory.CreateDirectory(Path.Combine(root, "Ziel"));
            var c = _fx.Client();

            // Datei und Ordner samt Inhalt kopieren
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/files/external/copy", new { mount = "mehr", paths = new[] { "a.txt", "Ordner" }, to = "Ziel" })).StatusCode);
            Assert.Equal("A", File.ReadAllText(Path.Combine(root, "Ziel", "a.txt")));
            Assert.Equal("B", File.ReadAllText(Path.Combine(root, "Ziel", "Ordner", "Unter", "b.txt")));
            Assert.True(File.Exists(Path.Combine(root, "a.txt")));
            // Schon da, in sich selbst, nur zum Lesen
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/files/external/copy", new { mount = "mehr", paths = new[] { "a.txt" }, to = "Ziel" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/external/copy", new { mount = "mehr", paths = new[] { "Ordner" }, to = "Ordner/Unter" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/external/copy", new { mount = "mehr-lesen", paths = new[] { "a.txt" }, to = "Ordner" })).StatusCode);

            // Mehrere auf einmal löschen
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/files/external/delete", new { mount = "mehr", paths = new[] { "Ziel/a.txt", "Ziel/Ordner" } })).StatusCode);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "Ziel")));
        }
        finally { config.Update(s => s.ExternalFolders = before); Directory.Delete(root, true); }
    }
}
