using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Tresor (docs/tresor-konzept.md): einrichten, öffnen je Gerät, Codes, Passwort ändern, automatisch schließen, gemerkte
/// Geräte, Zurücksetzen durch die Verwaltung, Neues bei geschlossenem Tresor, löschen.</summary>
[Collection("server")]
public class TresorTests : IClassFixture<ChatFixture>, IDisposable
{
    private readonly ChatFixture _fx;
    public TresorTests(ChatFixture fx)
    {
        _fx = fx;
        // Argon2id schnell für Tests
        Tresor.ArgonMemory = 1024; Tresor.ScryptCost = 1024;
        Tresor.ArgonIterations = 1;
        Tresor.Now = () => DateTime.UtcNow;
    }

    public void Dispose() => Tresor.Now = () => DateTime.UtcNow;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string?> Error(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();
    }

    /// <summary>Frischer Tresor für dora (App-Zugang); gibt Codes und Wiederherstellungsschlüssel zurück.</summary>
    private async Task<(HttpClient C, List<string> Codes, string Recovery)> Fresh()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        var r = await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1", name = "Testhandy" }));
        return (c, r.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList(), r.GetProperty("recoveryKey").GetString()!);
    }

    private async Task<HttpClient> Browser(string login)
    {
        var b = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        (await b.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).EnsureSuccessStatusCode();
        return b;
    }

    [Fact]
    public async Task Einrichten_oeffnen_schliessen()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        Assert.False((await Json(await c.GetAsync("/api/tresor"))).GetProperty("exists").GetBoolean());
        Assert.Contains("10 Zeichen", await Error(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "kurz" })));
        Assert.Contains("anderes Passwort", await Error(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "geheim-123456" })));

        var (_, codes, recovery) = await Fresh();
        Assert.Equal(10, codes.Count);
        Assert.Matches("^[A-Z2-9]{5}(-[A-Z2-9]{5}){3}$", codes[0]);
        Assert.Matches("^[A-Z2-9]{5}(-[A-Z2-9]{5}){7}$", recovery);
        var st = await Json(await c.GetAsync("/api/tresor"));
        Assert.True(st.GetProperty("open").GetBoolean());
        Assert.Equal("Testhandy", st.GetProperty("devices")[0].GetProperty("name").GetString());

        // Etwas in die Datenbank, schließen: nicht mehr erreichbar, Schlüssel weg
        var id = _fx.Id("dora");
        Tresor.DbOf(id)!.Exec("INSERT INTO kv(key, value) VALUES('probe', 'drin')");
        await Json(await c.PostAsync("/api/tresor/close", null));
        Assert.False(Tresor.OpenAnywhere(id));
        Assert.Null(Tresor.DbOf(id));
        // Auf der Platte nichts im Klartext: die Datenbank liegt jetzt im Cryptomator-Tresor der Ablage, die Arbeitskopie ist weg
        Assert.False(Directory.Exists(Tresor.WorkDir(id)));
        foreach (var f in Directory.EnumerateFiles(Tresor.Paths!.UserBlobs(id), "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("drin", Encoding.Latin1.GetString(File.ReadAllBytes(f)));
        Assert.DoesNotContain("tresor-pass-1", File.ReadAllText(Path.ChangeExtension(Tresor.DbPath(id), ".json")));

        Assert.Equal("Tresor-Passwort falsch.", await Error(await c.PostAsJsonAsync("/api/tresor/open", new { password = "falsch-falsch" })));
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.Equal("drin", Tresor.DbOf(id)!.Scalar("SELECT value FROM kv WHERE key = 'probe'"));
    }

    [Fact]
    public async Task Codes_Wiederherstellungsschluessel_und_Passwort_aendern()
    {
        var (c, codes, recovery) = await Fresh();
        await Json(await c.PostAsync("/api/tresor/close", null));
        // Code: einmal, in beliebiger Schreibweise
        var r = await Json(await c.PostAsJsonAsync("/api/tresor/open", new { code = codes[3].ToLowerInvariant().Replace("-", " ") }));
        Assert.Equal(9, r.GetProperty("codesLeft").GetInt32());
        await Json(await c.PostAsync("/api/tresor/close", null));
        Assert.Equal("Code falsch oder schon benutzt.", await Error(await c.PostAsJsonAsync("/api/tresor/open", new { code = codes[3] })));
        // Wiederherstellungsschlüssel geht mehrfach und lässt sich erneut zeigen
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { code = recovery }));
        Assert.Equal(recovery, (await Json(await c.GetAsync("/api/tresor/recovery-key"))).GetProperty("recoveryKey").GetString());
        // Neue Codes: die alten verfallen
        var fresh = (await Json(await c.PostAsync("/api/tresor/codes", null))).GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        await Json(await c.PostAsync("/api/tresor/close", null));
        Assert.Equal("Code falsch oder schon benutzt.", await Error(await c.PostAsJsonAsync("/api/tresor/open", new { code = codes[0] })));
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { code = fresh[0] }));

        // Passwort ändern: altes muss stimmen, danach gilt nur das neue - der Inhalt bleibt
        Tresor.DbOf(_fx.Id("dora"))!.Exec("INSERT INTO kv(key, value) VALUES('bleibt', 'ja')");
        Assert.Equal("Bisheriges Tresor-Passwort falsch.", await Error(await c.PostAsJsonAsync("/api/tresor/password", new { current = "falsch-falsch", password = "neues-pass-2" })));
        await Json(await c.PostAsJsonAsync("/api/tresor/password", new { current = "tresor-pass-1", password = "neues-pass-2" }));
        await Json(await c.PostAsync("/api/tresor/close", null));
        Assert.Equal("Tresor-Passwort falsch.", await Error(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" })));
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "neues-pass-2" }));
        Assert.Equal("ja", Tresor.DbOf(_fx.Id("dora"))!.Scalar("SELECT value FROM kv WHERE key = 'bleibt'"));
    }

    [Fact]
    public async Task Offen_je_Geraet_und_automatisch_schliessen()
    {
        var (app, _, _) = await Fresh();
        var browser = await Browser("dora");
        var st = await Json(await browser.GetAsync("/api/tresor"));
        Assert.False(st.GetProperty("open").GetBoolean());
        Assert.True(st.GetProperty("openAnywhere").GetBoolean());
        // Im Browser öffnen: Cookie, beide Geräte in der Liste
        var r = await browser.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1", name = "Firefox" });
        await Json(r);
        Assert.Contains(r.Headers.GetValues("Set-Cookie"), v => v.StartsWith(Tresor.Cookie + "=") && v.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        var devices = (await Json(await browser.GetAsync("/api/tresor"))).GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal(2, devices.Count);
        // Das Handy aus dem Browser schließen
        var phone = devices.Single(d => !d.GetProperty("mine").GetBoolean()).GetProperty("id").GetString();
        await Json(await browser.PostAsync($"/api/tresor/close/{phone}", null));
        Assert.False((await Json(await app.GetAsync("/api/tresor"))).GetProperty("open").GetBoolean());
        Assert.True((await Json(await browser.GetAsync("/api/tresor"))).GetProperty("open").GetBoolean());
        // Abmelden schließt
        await browser.PostAsync("/api/auth/logout", null);
        Assert.False(Tresor.OpenAnywhere(_fx.Id("dora")));

        // Automatisch schließen nach einer Stunde ohne Nutzung
        await Json(await app.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        await Json(await app.PutAsJsonAsync("/api/tresor/settings", new { autoClose = "1h", closeOnAdminReset = false }));
        var t0 = DateTime.UtcNow;
        Tresor.Now = () => t0.AddMinutes(50);
        Assert.True((await Json(await app.GetAsync("/api/tresor"))).GetProperty("open").GetBoolean());
        Tresor.Now = () => t0.AddMinutes(115);
        Assert.False((await Json(await app.GetAsync("/api/tresor"))).GetProperty("open").GetBoolean());
        Assert.False(Tresor.OpenAnywhere(_fx.Id("dora")));
        Tresor.Now = () => DateTime.UtcNow;
        // „Schließen bei Zurücksetzen durch die Verwaltung“ ist immer an, auch wenn eine ältere App es abschalten will
        await Json(await app.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        var s = (await Json(await app.PutAsJsonAsync("/api/tresor/settings", new { autoClose = "1d", closeOnAdminReset = false }))).GetProperty("settings");
        Assert.True(s.GetProperty("closeOnAdminReset").GetBoolean());
        Assert.True(Tresor.AdminReset(_fx.Id("dora")));
        Assert.False(Tresor.OpenAnywhere(_fx.Id("dora")));
    }

    [Fact]
    public async Task Gemerktes_Geraet_nach_Neustart_und_Zuruecksetzen_durch_die_Verwaltung()
    {
        var (c, _, _) = await Fresh();
        await Json(await c.PostAsync("/api/tresor/close", null));
        var device = (await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1", remember = true, name = "Pixel" }))).GetProperty("device");
        var (id, secret) = (device.GetProperty("id").GetString()!, device.GetProperty("secret").GetString()!);
        // Neustart des Servers: alles zu, das Gerät öffnet selbst wieder
        Tresor.Reset();
        Assert.False(Tresor.OpenAnywhere(_fx.Id("dora")));
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { deviceId = id, secret }));
        Assert.True(Tresor.OpenAnywhere(_fx.Id("dora")));
        // Nur gemerkt geöffnet: Heikles erst nach frischem Öffnen mit Passwort, Code oder Passkey
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/tresor/recovery-key")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/tresor/codes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/tresor/passkey", new { credentialId = "x", salt = Convert.ToBase64String(new byte[32]), secret = Convert.ToBase64String(new byte[32]) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/tresor/password", new { current = "", password = "neues-pass-123" })).StatusCode);
        // Falsches Geheimnis geht nicht
        await Json(await c.PostAsync("/api/tresor/close", null));
        await Error(await c.PostAsJsonAsync("/api/tresor/open", new { deviceId = id, secret = Convert.ToBase64String(new byte[32]) }));

        // Verwaltung setzt das Passwort zurück: Tresor zu, gemerkte Geräte vergessen
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { deviceId = id, secret }));
        var admin = await Browser("anna");
        await Json(await admin.PutAsJsonAsync($"/api/admin/users/{_fx.Id("dora")}", new { password = "geheim-123456" }));
        Assert.False(Tresor.OpenAnywhere(_fx.Id("dora")));
        var r = await c.PostAsJsonAsync("/api/tresor/open", new { deviceId = id, secret });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True(JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("forgotten").GetBoolean());
        // Mit dem Tresor-Passwort geht es wieder
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
    }

    [Fact]
    public async Task Neues_bei_geschlossenem_Tresor_wird_beim_Oeffnen_uebernommen()
    {
        var (c, _, _) = await Fresh();
        var id = _fx.Id("dora");
        Tresor.Absorbers["test"] = (db, payload) => db.Exec("INSERT OR REPLACE INTO kv(key, value) VALUES('eingang', $v)", ("$v", Encoding.UTF8.GetString(payload)));
        await Json(await c.PostAsync("/api/tresor/close", null));
        Tresor.Drop(id, "test", Encoding.UTF8.GetBytes("Wasser 250 ml"));
        Tresor.Drop(id, "unbekannt", [1, 2, 3]);
        Assert.Equal(2, Tresor.Pending(id));
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.Equal("Wasser 250 ml", Tresor.DbOf(id)!.Scalar("SELECT value FROM kv WHERE key = 'eingang'"));
        // Ohne Übernehmer bleibt es liegen
        Assert.Equal(1, Tresor.Pending(id));
        // Bei offenem Tresor gleich übernommen
        Tresor.Drop(id, "test", Encoding.UTF8.GetBytes("zweites"));
        Assert.Equal("zweites", Tresor.DbOf(id)!.Scalar("SELECT value FROM kv WHERE key = 'eingang'"));
        Tresor.Absorbers.TryRemove("test", out _);
    }

    [Fact]
    public async Task Loeschen_nur_mit_Tresor_Passwort()
    {
        var (c, _, _) = await Fresh();
        var id = _fx.Id("dora");
        Assert.Equal("Tresor-Passwort falsch.", await Error(await c.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/tresor") { Content = JsonContent.Create(new { password = "falsch-falsch" }) })));
        Assert.True(Tresor.Exists(id));
        await Json(await c.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/tresor") { Content = JsonContent.Create(new { password = "tresor-pass-1" }) }));
        Assert.False(Tresor.Exists(id));
        Assert.False(File.Exists(Tresor.DbPath(id)));
        Assert.False(Tresor.OpenAnywhere(id));
    }

    [Fact]
    public async Task Dateien_im_Tresor()
    {
        var (c, _, _) = await Fresh();
        var id = _fx.Id("dora");
        var folder = (await Json(await c.PostAsJsonAsync("/api/tresor/files/folder", new { parent = 0, name = "Ausweise" }))).GetProperty("id").GetInt64();
        // Hochladen: Text und ein Foto
        var text = new ByteArrayContent(Encoding.UTF8.GetBytes("Geheimer Inhalt 4711"));
        text.Headers.ContentType = new("text/plain");
        var doc = (await Json(await c.PostAsync($"/api/tresor/files/upload?parent={folder}&name=pass.txt", text))).GetProperty("id").GetInt64();
        using (var bmp = new SkiaSharp.SKBitmap(300, 200))
        {
            using (var cv = new SkiaSharp.SKCanvas(bmp)) cv.Clear(SkiaSharp.SKColors.Teal);
            var jpg = new ByteArrayContent(SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90).ToArray());
            jpg.Headers.ContentType = new("image/jpeg");
            await Json(await c.PostAsync($"/api/tresor/files/upload?parent={folder}&name=foto.jpg", jpg));
        }
        // Gleicher Name: zweiter bekommt (2)
        Assert.Equal("pass (2).txt", (await Json(await c.PostAsync($"/api/tresor/files/upload?parent={folder}&name=pass.txt", new StringContent("x")))).GetProperty("name").GetString());
        var list = await Json(await c.GetAsync($"/api/tresor/files?dir={folder}"));
        Assert.Equal(new[] { "foto.jpg", "pass (2).txt", "pass.txt" }, list.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray());
        Assert.Equal("Ausweise", list.GetProperty("trail")[0].GetProperty("name").GetString());
        Assert.Equal("Geheimer Inhalt 4711", await c.GetStringAsync($"/api/tresor/files/{doc}/content"));
        // Auf der Platte nichts im Klartext - weder im Tresor-Ordner der Dateiablage noch sonst bei der Person
        Assert.NotNull(DmOffice.Dav.FileStore.Get(_fx.Space("dora").Db, DmOffice.FileStoreVaultFiles.DefaultRoot + "/vault.cryptomator"));
        foreach (var f in Directory.EnumerateFiles(Tresor.Paths!.UserBlobs(id), "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("4711", Encoding.Latin1.GetString(File.ReadAllBytes(f)));
        // Vorschaubild entsteht und liegt danach im Tresor
        var photo = list.GetProperty("items")[0].GetProperty("id").GetInt64();
        var thumb = await c.GetAsync($"/api/tresor/files/{photo}/thumb?size=256");
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(Tresor.DbOf(id)!.Scalar("SELECT thumb_small FROM files WHERE id = $i", ("$i", photo)));

        // Umbenennen, verschieben (nicht in sich selbst), löschen
        await Json(await c.PutAsJsonAsync($"/api/tresor/files/{doc}", new { name = "reisepass.txt", parent = 0 }));
        Assert.Contains("reisepass.txt", (await c.GetStringAsync("/api/tresor/files")));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/tresor/files/{folder}", new { parent = folder })).StatusCode);

        // Kopie aus dem Tresor in die normale Ablage
        var copied = await Json(await c.PostAsJsonAsync("/api/tresor/files/copy-out", new { ids = new[] { doc, folder }, folder = "" }));
        Assert.Contains("reisepass.txt", copied.GetProperty("copied").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Geheimer Inhalt 4711", Encoding.UTF8.GetString(DmOffice.Dav.FileStore.Data(_fx.Space("dora").Db, "reisepass.txt")!));
        Assert.NotNull(DmOffice.Dav.FileStore.Get(_fx.Space("dora").Db, "Ausweise/foto.jpg"));

        await Json(await c.DeleteAsync($"/api/tresor/files/{folder}"));
        Assert.DoesNotContain("Ausweise", await c.GetStringAsync("/api/tresor/files"));
        // Zählt zur Belegung
        Assert.True(DmOffice.Core.UserStorage.Used(_fx.Space("dora").Db) >= Tresor.Size(id));

        // Geschlossen: 423 mit locked = tresor
        await Json(await c.PostAsync("/api/tresor/close", null));
        var r = await c.GetAsync("/api/tresor/files");
        Assert.Equal(HttpStatusCode.Locked, r.StatusCode);
        Assert.Equal("tresor", JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("locked").GetString());
        DmOffice.Dav.FileStore.Delete(_fx.Space("dora").Db, "reisepass.txt");
        DmOffice.Dav.FileStore.Delete(_fx.Space("dora").Db, "Ausweise");
    }

    [Fact]
    public async Task Bildbearbeitung_im_Tresor()
    {
        var (c, _, _) = await Fresh();
        byte[] jpg;
        using (var bmp = new SkiaSharp.SKBitmap(300, 200))
        {
            using (var cv = new SkiaSharp.SKCanvas(bmp)) cv.Clear(SkiaSharp.SKColors.Orange);
            jpg = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90).ToArray();
        }
        var content = new ByteArrayContent(jpg);
        content.Headers.ContentType = new("image/jpeg");
        var id = (await Json(await c.PostAsync("/api/tresor/files/upload?parent=0&name=foto.jpg", content))).GetProperty("id").GetInt64();
        var path = "tresor:" + id;
        await c.GetAsync($"/api/tresor/files/{id}/thumb?size=256");

        // Vorschau der Regler
        var preview = await c.PostAsJsonAsync("/api/files/photo-adjust/preview", new { path, adjust = new { exposure = 0.5 } });
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType?.MediaType);
        // Keine gemerkten Einstellungen in der Benutzerdatenbank
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await Json(await c.GetAsync("/api/files/photo-edits?path=" + path))).GetProperty("edit").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/files/photo-edits", new { path, rotate = 90 })).StatusCode);
        // KI: kein Zugriff
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/photo-adjust/suggest", new { path })).StatusCode);

        // Zuschneiden und überschreiben: dieselbe Datei, halb so breit, Vorschaubild neu
        Assert.Equal(path, (await Json(await c.PostAsJsonAsync("/api/files/photo-edit", new { path, rotate = 0, crop = new { x = 0, y = 0, w = 0.5, h = 1 }, overwrite = true }))).GetProperty("path").GetString());
        using (var b = SkiaSharp.SKBitmap.Decode(await c.GetByteArrayAsync($"/api/tresor/files/{id}/content"))) Assert.Equal((150, 200), (b.Width, b.Height));
        Assert.Null(Tresor.DbOf(_fx.Id("dora"))!.Scalar("SELECT thumb_small FROM files WHERE id = $i", ("$i", id)));
        // Als neue Datei daneben
        var copy = (await Json(await c.PostAsJsonAsync("/api/files/photo-edit", new { path, rotate = 90, overwrite = false }))).GetProperty("path").GetString()!;
        Assert.NotEqual(path, copy);
        Assert.Contains("foto (2).jpg", await c.GetStringAsync("/api/tresor/files"));

        // Geschlossen: 423
        await Json(await c.PostAsync("/api/tresor/close", null));
        Assert.Equal(HttpStatusCode.Locked, (await c.PostAsJsonAsync("/api/files/photo-adjust/preview", new { path })).StatusCode);
    }

    [Fact]
    public async Task Passkey_oeffnet_mit_dem_Geheimnis_aus_der_PRF_Erweiterung()
    {
        var (c, _, _) = await Fresh();
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        // Zu kurz: abgelehnt
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/tresor/passkey", new { credentialId = "abc", salt = Convert.ToBase64String(salt), secret = Convert.ToBase64String(new byte[8]) })).StatusCode);
        var id = (await Json(await c.PostAsJsonAsync("/api/tresor/passkey", new { credentialId = "cred-1", salt = Convert.ToBase64String(salt), secret = Convert.ToBase64String(secret), name = "Windows Hello" })))
            .GetProperty("id").GetString()!;
        var st = await Json(await c.GetAsync("/api/tresor"));
        var pk = st.GetProperty("passkeys")[0];
        Assert.Equal(("cred-1", Convert.ToBase64String(salt)), (pk.GetProperty("credentialId").GetString(), pk.GetProperty("salt").GetString()));
        await Json(await c.PostAsync("/api/tresor/close", null));
        // Falsches Geheimnis: nein; richtiges: ja
        await Error(await c.PostAsJsonAsync("/api/tresor/open", new { deviceId = id, secret = Convert.ToBase64String(new byte[32]) }));
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { deviceId = id, secret = Convert.ToBase64String(secret) }));
        // Zurücksetzen durch die Verwaltung lässt den Passkey (er braucht Windows Hello bzw. Fingerabdruck)
        var admin = await Browser("anna");
        await Json(await admin.PutAsJsonAsync($"/api/admin/users/{_fx.Id("dora")}", new { password = "geheim-123456" }));
        Assert.False(Tresor.OpenAnywhere(_fx.Id("dora")));
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { deviceId = id, secret = Convert.ToBase64String(secret) }));
        // Entfernen
        await Json(await c.DeleteAsync($"/api/tresor/remembered/{id}"));
        Assert.Equal(0, (await Json(await c.GetAsync("/api/tresor"))).GetProperty("passkeys").GetArrayLength());
    }

    /// <summary>notizi-Datenbank im Tresor (Stufe 4): ein Handy gleicht über WebDAV ab wie notizi (WebDavTransport, SyncEngine).</summary>
    [Fact]
    public async Task Notizi_Datenbank_im_Tresor()
    {
        var (c, _, _) = await Fresh();
        var id = _fx.Id("dora");
        var space = _fx.Space("dora");
        space.UpdateSettings(s => { s.Notizi.Enabled = true; s.Notizi.E2eePassword = "e2ee-geheim-1"; s.Notizi.DbFolder = null; });

        // Das Handy richtet die Datenbank „privat“ ein und schreibt eine Notiz
        var dir = Directory.CreateTempSubdirectory("dmtresornotes");
        var phone = new DmOffice.Notes.NotesDb(new UserDb(Path.Combine(dir.FullName, "handy.db"), new byte[32], "handy"));
        var transport = new Notizi.Sync.WebDavTransport(new Uri("http://localhost/dav/files/dora/notizisync/privat/"), "dora", _fx.Users["dora"].AppPassword,
            _fx.Factory.Server.CreateHandler());
        var store = await Notizi.Sync.SyncStore.CreateAsync(transport, "e2ee-geheim-1");
        await store.WriteNameAsync("Privat");
        void Write(string text)
        {
            var n = phone.CreateNote(null);
            phone.SaveNote(n, $"<div data-notizi=\"1\"><p>{text}</p></div>", text);
        }
        Write("Geheime Notiz");
        Assert.Empty((await new Notizi.Sync.SyncEngine(store, phone, "handy").RunAsync()).Problems);

        // In den Tresor verschieben: im offenen Bereich nichts mehr, im Tresor lesbar
        var dbs = await Json(await c.GetAsync("/api/notes/databases"));
        Assert.Contains(dbs.GetProperty("list").EnumerateArray(), e => e.GetProperty("folder").GetString() == "privat");
        await Json(await c.PostAsync("/api/notes/databases/privat/tresor", null));
        Assert.Null(DmOffice.Dav.FileStore.Get(space.Db, "notizisync/privat"));
        dbs = await Json(await c.GetAsync("/api/notes/databases"));
        Assert.Equal("Privat", dbs.GetProperty("tresor")[0].GetProperty("name").GetString());
        Assert.DoesNotContain(dbs.GetProperty("list").EnumerateArray(), e => e.GetProperty("folder").GetString() == "privat");
        async Task<List<string>> Titles() => (await Json(await c.GetAsync("/api/notes/vault/privat/?view=all"))).GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("title").GetString()!).ToList();
        Assert.Equal(["Geheime Notiz"], await Titles());
        // Nicht in der gewöhnlichen Suche
        Assert.Equal(0, (await Json(await c.GetAsync("/api/notes/?q=Geheime"))).GetProperty("items").GetArrayLength());

        // Tresor offen: das Handy gleicht ganz normal weiter ab (dieselben ETags)
        Write("Zweite Notiz");
        Assert.Empty((await new Notizi.Sync.SyncEngine(store, phone, "handy").RunAsync()).Problems);
        await Json(await c.PostAsync("/api/notes/vault/privat/sync", null));
        Assert.Contains("Zweite Notiz", await Titles());

        // Im Tresor bearbeiten geht zurück aufs Handy
        var created = (await Json(await c.PostAsJsonAsync("/api/notes/vault/privat/", new { folder = (string?)null }))).GetProperty("id").GetInt64();
        await Json(await c.PutAsJsonAsync($"/api/notes/vault/privat/{created}/content", new { blocks = new[] { new { html = "<p>Aus dmOffice</p>" } }, plain = "Aus dmOffice" }));
        // Sicherung: die Notizen im Tresor samt noch nicht Abgeglichenem - verschlüsselt, mit dem Tresor-Schlüssel lesbar
        var stage = Directory.CreateTempSubdirectory("dmtresorstage").FullName;
        Tresor.Backup(id, stage);
        var saved = Assert.Single(Directory.GetFiles(Path.Combine(stage, "users", id + ".tresor.notes"), "*.db"));
        Assert.DoesNotContain("Aus dmOffice", Encoding.Latin1.GetString(File.ReadAllBytes(saved)));
        using (var copy = new DmOffice.Notes.TresorNotesFile(saved, Tresor.SubKey(id, "tresor-notes")!))
        {
            var restored = new DmOffice.Notes.NotesDb(copy);
            Assert.Contains(restored.GetAllActive(), n => n.Plain.Contains("Aus dmOffice"));
            Assert.True(restored.PendingCount() > 0);
        }
        await Json(await c.PostAsync("/api/notes/vault/privat/sync", null));
        Assert.Empty((await new Notizi.Sync.SyncEngine(store, phone, "handy").RunAsync()).Problems);
        Assert.Contains(phone.GetAllActive(), n => n.Plain.Contains("Aus dmOffice"));

        // Tresor zu: Lesen gesperrt (423), Hochladen geht - notizi lädt dann nur hoch
        await Json(await c.PostAsync("/api/tresor/close", null));
        Assert.Equal((HttpStatusCode)423, (await c.GetAsync("/api/notes/vault/privat/")).StatusCode);
        var dav = _fx.As("dora");
        Assert.Equal((HttpStatusCode)423, (await dav.GetAsync("/dav/files/dora/notizisync/privat/info.json")).StatusCode);
        Write("Bei geschlossenem Tresor");
        await Assert.ThrowsAsync<Notizi.Sync.SyncLockedException>(() => new Notizi.Sync.SyncEngine(store, phone, "handy").RunAsync());
        var blind = Notizi.Sync.SyncStore.OpenFromInfo(transport, "e2ee-geheim-1", store.InfoBytes)!;
        var push = await new Notizi.Sync.SyncEngine(blind, phone, "handy").PushAsync();
        Assert.Equal(1, push.Uploaded);
        Assert.Empty(push.Problems);
        Assert.True(Tresor.Pending(id) > 0);
        // Ein veralteter Stand wird abgelehnt (bedingtes Hochladen)
        var info = DmOffice.Notes.TresorNotizi.Get(space, "privat/info.json")!;
        var stale = new HttpRequestMessage(HttpMethod.Put, "/dav/files/dora/notizisync/privat/info.json") { Content = new ByteArrayContent([1, 2, 3]) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"veraltet\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await dav.SendAsync(stale)).StatusCode);
        Assert.Equal(info.Etag, DmOffice.Notes.TresorNotizi.Get(space, "privat/info.json")!.Etag);

        // Wieder offen: übernommen und lesbar
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.Equal(0, Tresor.Pending(id));
        await Json(await c.PostAsync("/api/notes/vault/privat/sync", null));
        Assert.Contains("Bei geschlossenem Tresor", await Titles());

        // Aus dem Tresor holen: wieder im offenen Bereich, das Handy gleicht weiter ab
        await Json(await c.PostAsync("/api/notes/databases/privat/out", null));
        Assert.NotNull(DmOffice.Dav.FileStore.Get(space.Db, "notizisync/privat/info.json"));
        Assert.Empty(DmOffice.Notes.TresorNotizi.Folders(space));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/notes/vault/privat/")).StatusCode);
        Write("Wieder draußen");
        Assert.Empty((await new Notizi.Sync.SyncEngine(store, phone, "handy").RunAsync()).Problems);
    }
}
