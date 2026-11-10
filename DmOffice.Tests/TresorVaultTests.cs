using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Core.Cryptomator;
using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>
/// System-Tresor im Cryptomator-Format (docs/tresor-cryptomator-konzept.md, Stufe 3): die Tresor-Dateien liegen verschlüsselt
/// unter /dmVAULT/vault in der Dateiablage, masterkey.cryptomator hängt am Tresor-Passwort, Ordner und Umbenennen
/// gehen in den Cryptomator-Tresor.
/// </summary>
[Collection("server")]
public class TresorVaultTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public TresorVaultTests(ChatFixture fx)
    {
        _fx = fx;
        Tresor.ArgonMemory = 1024; Tresor.ScryptCost = 1024; Tresor.ArgonIterations = 1;
        Tresor.Now = () => DateTime.UtcNow;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private IVaultFiles Files(string login) => new FileStoreVaultFiles(_fx.Space(login).Db, FileStoreVaultFiles.DefaultRoot);

    private static List<string> Dump(CryptomatorVault v)
    {
        var lines = new List<string>();
        void Walk(string dir)
        {
            foreach (var e in v.List(dir).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var path = dir + "/" + e.Name;
                if (e.IsDir) { lines.Add("D " + path); Walk(path); }
                else
                {
                    using var s = v.OpenRead(path);
                    lines.Add("F " + path + " " + new StreamReader(s).ReadToEnd());
                }
            }
        }
        Walk("");
        return lines.Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Tresor_liegt_als_Cryptomator_Tresor_in_der_Dateiablage()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" }));
        var db = _fx.Space("dora").Db;
        Assert.NotNull(FileStore.Get(db, FileStoreVaultFiles.DefaultRoot + "/" + VaultConfig.FileName));
        Assert.NotNull(FileStore.Get(db, FileStoreVaultFiles.DefaultRoot + "/" + MasterkeyFile.FileName));
        Assert.NotEmpty(Tresor.Meta(_fx.Id("dora"))!.Vault);

        var folder = (await Json(await c.PostAsJsonAsync("/api/tresor/files/folder", new { parent = 0, name = "Ämter" }))).GetProperty("id").GetInt64();
        var doc = (await Json(await c.PostAsync($"/api/tresor/files/upload?parent={folder}&name=Bescheid.txt", new StringContent("Inhalt 4711")))).GetProperty("id").GetInt64();
        var longName = "Langer Name " + string.Concat(Enumerable.Repeat("äöü ", 50)) + ".txt";
        var lng = (await Json(await c.PostAsync($"/api/tresor/files/upload?parent=0&name={Uri.EscapeDataString(longName)}", new StringContent("lang")))).GetProperty("id").GetInt64();
        Assert.Equal("Inhalt 4711", await c.GetStringAsync($"/api/tresor/files/{doc}/content"));
        // Größe in der Liste ist der Klartext
        var list = await Json(await c.GetAsync($"/api/tresor/files?dir={folder}"));
        Assert.Equal(11, list.GetProperty("items")[0].GetProperty("size").GetInt64());

        // Mit dem Tresor-Passwort öffnet ihn jedes Cryptomator - hier dieselbe Formatschicht über masterkey.cryptomator
        using (var v = CryptomatorVault.Open(Files("dora"), "tresor-pass-1")!)
            Assert.Equal(new List<string> { "D /Ämter", "F /Ämter/Bescheid.txt Inhalt 4711", "F /" + longName + " lang" }.Order(StringComparer.Ordinal), Dump(v));
        // In der Ablage nur verschlüsselte Namen und Inhalte
        Assert.All(FileStore.Descendants(db, FileStoreVaultFiles.DefaultRoot), f => Assert.DoesNotContain("Bescheid", f.Path));
        Assert.DoesNotContain(FileStore.Descendants(db, FileStoreVaultFiles.DefaultRoot).Where(f => !f.IsDir),
            f => Encoding.Latin1.GetString(FileStore.Data(db, f.Path)!).Contains("4711"));

        // Umbenennen, verschieben (auch in die lange Form), löschen: der Cryptomator-Tresor zieht mit
        await Json(await c.PutAsJsonAsync($"/api/tresor/files/{doc}", new { name = "Bescheid 2025.txt", parent = 0 }));
        await Json(await c.PutAsJsonAsync($"/api/tresor/files/{lng}", new { name = "kurz.txt", parent = folder }));
        await Json(await c.PutAsJsonAsync($"/api/tresor/files/{folder}", new { name = "Ämter und " + string.Concat(Enumerable.Repeat("Behörden ", 25)) }));
        var renamed = "Ämter und " + string.Concat(Enumerable.Repeat("Behörden ", 25)).Trim();
        using (var v = CryptomatorVault.Open(Files("dora"), "tresor-pass-1")!)
            Assert.Equal(new List<string> { "F /Bescheid 2025.txt Inhalt 4711", "D /" + renamed, "F /" + renamed + "/kurz.txt lang" }.Order(StringComparer.Ordinal), Dump(v));
        Assert.Equal("lang", await c.GetStringAsync($"/api/tresor/files/{lng}/content"));
        (await c.DeleteAsync($"/api/tresor/files/{folder}")).EnsureSuccessStatusCode();
        using (var v = CryptomatorVault.Open(Files("dora"), "tresor-pass-1")!)
            Assert.Equal(new List<string> { "F /Bescheid 2025.txt Inhalt 4711" }.Order(StringComparer.Ordinal), Dump(v));

        // Tresor-Passwort ändern: masterkey.cryptomator zieht mit
        await Json(await c.PostAsJsonAsync("/api/tresor/password", new { current = "tresor-pass-1", password = "neues-pass-2" }));
        Assert.Null(CryptomatorVault.Open(Files("dora"), "tresor-pass-1"));
        using (var v = CryptomatorVault.Open(Files("dora"), "neues-pass-2")!) Assert.Single(Dump(v));

        // Gegenprobe mit der echten Cryptomator-Bibliothek (tools/cryptomator-check), nur mit DMOFFICE_CRYPTOMATOR_CHECK=1
        if (Environment.GetEnvironmentVariable("DMOFFICE_CRYPTOMATOR_CHECK") == "1")
        {
            var dir = Path.Combine(Path.GetTempPath(), "cm-sys-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var f in FileStore.Descendants(db, FileStoreVaultFiles.DefaultRoot).Where(f => !f.IsDir))
                {
                    var target = Path.Combine(dir, f.Path[(FileStoreVaultFiles.DefaultRoot.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, FileStore.Data(db, f.Path)!);
                }
                foreach (var d in FileStore.Descendants(db, FileStoreVaultFiles.DefaultRoot).Where(f => f.IsDir))
                    Directory.CreateDirectory(Path.Combine(dir, d.Path[(FileStoreVaultFiles.DefaultRoot.Length + 1)..].Replace('/', Path.DirectorySeparatorChar)));
                var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "cryptomator-check", "check.ps1"));
                var psi = new ProcessStartInfo("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "dump", dir, "neues-pass-2"])
                { RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                Assert.Equal(0, p.ExitCode);
                Assert.Contains("F\t/Bescheid 2025.txt\t11\t", output);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }

        // Tresor löschen: der Ordner in der Ablage ist endgültig weg (kein Papierkorb)
        Tresor.Delete(_fx.Id("dora"));
        Assert.Null(FileStore.Get(db, FileStore.SystemVault));
        Assert.DoesNotContain(FileStore.Trash(db), t => t.Path.StartsWith(FileStore.SystemVault));
    }

    /// <summary>Stufe 3a: der Tresor über die Wege der Dateiablage (/api/files mit „tresor:“), auch zwischen Tresor und „Meine Dateien“.</summary>
    [Fact]
    public async Task Tresor_in_der_Dateiansicht()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        var db = _fx.Space("dora").Db;
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" }));
        static string Q(string s) => Uri.EscapeDataString(s);

        Assert.Empty((await Json(await c.GetAsync("/api/files/?path=tresor:"))).GetProperty("items").EnumerateArray());
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Belege" }));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Belege" })).StatusCode);

        // Hochladen samt Unterordner (Ordner hochladen)
        var form = new MultipartFormDataContent { { new StringContent("Quittung 4711"), "file", "2025/Quittung ä.txt" } };
        await Json(await c.PostAsync("/api/files/upload?path=" + Q("tresor:/Belege"), form));
        var list = await Json(await c.GetAsync("/api/files/?path=" + Q("tresor:/Belege/2025")));
        var item = list.GetProperty("items")[0];
        Assert.Equal("tresor:/Belege/2025/Quittung ä.txt", item.GetProperty("path").GetString());
        Assert.Equal("Quittung 4711", await c.GetStringAsync("/api/files/download?path=" + Q("tresor:/Belege/2025/Quittung ä.txt")));
        Assert.Equal(13, (await Json(await c.GetAsync("/api/files/info?path=" + Q("tresor:/Belege")))).GetProperty("size").GetInt64());

        // Teil-Upload (so lädt die Weboberfläche hoch) mit Ziel im Tresor
        var big = new byte[3 * 1024 * 1024 + 17];
        System.Security.Cryptography.RandomNumberGenerator.Fill(big);
        var up = await Json(await c.PostAsJsonAsync("/api/files/uploads", new { name = "Scan.pdf", size = big.Length, mime = "application/pdf" }));
        var upId = up.GetProperty("id").GetString();
        var chunk = up.GetProperty("chunkSize").GetInt32();
        for (var off = 0; off < big.Length; off += chunk)
            (await c.PutAsync($"/api/files/uploads/{upId}?offset={off}", new ByteArrayContent(big[off..Math.Min(big.Length, off + chunk)]))).EnsureSuccessStatusCode();
        await Json(await c.PostAsync($"/api/files/uploads/{upId}/finish?path=" + Q("tresor:/Belege") + "&conflict=rename", null));
        Assert.Equal(big, await c.GetByteArrayAsync("/api/files/download?path=" + Q("tresor:/Belege/Scan.pdf")));
        await Json(await c.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "tresor:/Belege/Scan.pdf" } }));

        // Umbenennen im Tresor
        await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "tresor:/Belege/2025/Quittung ä.txt", to = "tresor:/Belege/Quittung.txt", copy = false }));
        Assert.Equal("Quittung 4711", await c.GetStringAsync("/api/files/download?path=" + Q("tresor:/Belege/Quittung.txt")));

        // Aus „Meine Dateien“ in den Tresor verschieben: das Original ist danach endgültig weg, auch nicht im Papierkorb
        FileStore.Put(db, "Ausweis.pdf", Encoding.UTF8.GetBytes("Ausweis 0815"), "application/pdf");
        await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "Ausweis.pdf", to = "tresor:/Belege/Ausweis.pdf", copy = false }));
        Assert.Null(FileStore.Get(db, "Ausweis.pdf"));
        Assert.DoesNotContain(FileStore.Trash(db), t => t.Path == "Ausweis.pdf");
        Assert.Equal("Ausweis 0815", await c.GetStringAsync("/api/files/download?path=" + Q("tresor:/Belege/Ausweis.pdf")));
        // Belegt: Konflikt, mit „rename“ ein freier Name
        FileStore.Put(db, "Ausweis.pdf", Encoding.UTF8.GetBytes("zweiter"), "application/pdf");
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/files/move", new { from = "Ausweis.pdf", to = "tresor:/Belege/Ausweis.pdf", copy = true })).StatusCode);
        var renamed = await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "Ausweis.pdf", to = "tresor:/Belege/Ausweis.pdf", copy = true, conflict = "rename" }));
        Assert.Equal("tresor:/Belege/Ausweis (2).pdf", renamed.GetProperty("path").GetString());
        Assert.NotNull(FileStore.Get(db, "Ausweis.pdf"));

        // Aus dem Tresor herausnehmen (kopieren): in „Meine Dateien“ im Klartext, im Tresor bleibt das Original
        await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "tresor:/Belege", to = "Aus dem Tresor", copy = true }));
        Assert.Equal("Quittung 4711", Encoding.UTF8.GetString(FileStore.Data(db, "Aus dem Tresor/Quittung.txt")!));
        Assert.True((await Json(await c.PostAsJsonAsync("/api/files/exists", new { paths = new[] { "tresor:/Belege/Quittung.txt", "tresor:/fehlt" } })))
            .GetProperty("existing").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["tresor:/Belege/Quittung.txt"]));

        // ZIP mit entschlüsseltem Inhalt
        var zip = await c.PostAsync("/api/files/zip", new FormUrlEncodedContent([new("paths", "tresor:/Belege"), new("name", "Belege")]));
        Assert.Equal("application/zip", zip.Content.Headers.ContentType?.MediaType);
        using (var archive = new System.IO.Compression.ZipArchive(await zip.Content.ReadAsStreamAsync()))
            Assert.Contains(archive.Entries, e => e.FullName == "Belege/Quittung.txt");

        // Löschen im Tresor: endgültig
        await Json(await c.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "tresor:/Belege" } }));
        Assert.Empty((await Json(await c.GetAsync("/api/files/?path=tresor:"))).GetProperty("items").EnumerateArray());

        // Geschlossen: 423
        (await c.PostAsync("/api/tresor/close", null)).EnsureSuccessStatusCode();
        Assert.Equal((HttpStatusCode)423, (await c.GetAsync("/api/files/?path=tresor:")).StatusCode);
    }

    /// <summary>Leerer Tresor von vor dem Cryptomator-Format: beim Öffnen mit dem Passwort nachgerüstet (kein Umzug, nichts darin).</summary>
    [Fact]
    public async Task Leerer_alter_Tresor_wird_beim_Oeffnen_nachgeruestet()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        var db = _fx.Space("dora").Db;
        // Alter Stil: ohne Zugang zur Ablage angelegt - kein Masterkey, kein Cryptomator-Tresor
        var before = Tresor.UserDbOf;
        Tresor.UserDbOf = null;
        try { await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" })); }
        finally { Tresor.UserDbOf = before; }
        Assert.Empty(Tresor.Meta(_fx.Id("dora"))!.Vault);
        Assert.Null(FileStore.Get(db, FileStore.SystemVault));
        (await c.PostAsync("/api/tresor/close", null)).EnsureSuccessStatusCode();

        // Öffnen mit dem Passwort: jetzt Cryptomator-Tresor, masterkey.cryptomator mit diesem Passwort
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.NotEmpty(Tresor.Meta(_fx.Id("dora"))!.Vault);
        await Json(await c.PostAsync("/api/files/upload?path=" + Uri.EscapeDataString("tresor:"),
            new MultipartFormDataContent { { new StringContent("neu 4711"), "file", "neu.txt" } }));
        using (var v = CryptomatorVault.Open(Files("dora"), "tresor-pass-1")!)
            Assert.Equal(["F /neu.txt neu 4711"], Dump(v));
        Tresor.Delete(_fx.Id("dora"));
    }

    /// <summary>Alter Tresor mit Inhalt (per Passkey geöffnet und gefüllt): beim Öffnen mit dem Passwort samt Dateien übernommen.</summary>
    [Fact]
    public async Task Alter_Tresor_mit_Dateien_wird_samt_Inhalt_uebernommen()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        var before = Tresor.UserDbOf;
        Tresor.UserDbOf = null;
        try
        {
            await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" }));
            await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Akten" }));
            await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Akten/2026" }));
            await Json(await c.PostAsync("/api/files/upload?path=" + Uri.EscapeDataString("tresor:/Akten/2026"),
                new MultipartFormDataContent { { new StringContent("Rechnung 4711"), "file", "rechnung.txt" } }));
            await Json(await c.PostAsync("/api/files/upload?path=" + Uri.EscapeDataString("tresor:"),
                new MultipartFormDataContent { { new StringContent("oben"), "file", "oben.txt" } }));
            (await c.PostAsync("/api/tresor/close", null)).EnsureSuccessStatusCode();
        }
        finally { Tresor.UserDbOf = before; }
        Assert.Empty(Tresor.Meta(_fx.Id("dora"))!.Vault);
        Assert.True((await Json(await c.GetAsync("/api/tresor"))).GetProperty("oldFormat").GetBoolean());

        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.NotEmpty(Tresor.Meta(_fx.Id("dora"))!.Vault);
        Assert.False((await Json(await c.GetAsync("/api/tresor"))).GetProperty("oldFormat").GetBoolean());
        // In der Oberfläche wie vorher, Inhalt aus dem Cryptomator-Tresor
        Assert.Equal("Rechnung 4711", await c.GetStringAsync("/api/files/download?path=" + Uri.EscapeDataString("tresor:/Akten/2026/rechnung.txt")));
        (await c.PostAsync("/api/tresor/close", null)).EnsureSuccessStatusCode();
        // Mit Cryptomator und dem Tresor-Passwort lesbar
        using (var v = CryptomatorVault.Open(Files("dora"), "tresor-pass-1")!)
        {
            var dump = Dump(v);
            Assert.Contains("F /Akten/2026/rechnung.txt Rechnung 4711", dump);
            Assert.Contains("F /oben.txt oben", dump);
        }
        Tresor.Delete(_fx.Id("dora"));
    }

    /// <summary>Alter Tresor, geöffnet ohne Passwort (hier mit einem Wiederherstellungscode wie per Passkey): trotzdem umgestellt,
    /// masterkey.cryptomator kommt beim nächsten Öffnen mit dem Passwort dazu.</summary>
    [Fact]
    public async Task Alter_Tresor_wird_auch_ohne_Passwort_umgestellt()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        var before = Tresor.UserDbOf;
        Tresor.UserDbOf = null;
        string code;
        try
        {
            var setup = await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" }));
            code = setup.GetProperty("recoveryCodes")[0].GetString()!;
            await Json(await c.PostAsync("/api/files/upload?path=" + Uri.EscapeDataString("tresor:"),
                new MultipartFormDataContent { { new StringContent("drin"), "file", "a.txt" } }));
            (await c.PostAsync("/api/tresor/close", null)).EnsureSuccessStatusCode();
        }
        finally { Tresor.UserDbOf = before; }

        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { code }));
        var st = await Json(await c.GetAsync("/api/tresor"));
        Assert.False(st.GetProperty("oldFormat").GetBoolean());
        Assert.True(st.GetProperty("needsPassword").GetBoolean());
        Assert.Equal("drin", await c.GetStringAsync("/api/files/download?path=" + Uri.EscapeDataString("tresor:/a.txt")));
        Assert.NotNull(FileStore.Get(_fx.Space("dora").Db, FileStore.SystemVault));
        (await c.PostAsync("/api/tresor/close", null)).EnsureSuccessStatusCode();

        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.False((await Json(await c.GetAsync("/api/tresor"))).GetProperty("needsPassword").GetBoolean());
        (await c.PostAsync("/api/tresor/close", null)).EnsureSuccessStatusCode();
        using (var v = CryptomatorVault.Open(Files("dora"), "tresor-pass-1")!)
            Assert.Contains("F /a.txt drin", Dump(v));
        Tresor.Delete(_fx.Id("dora"));
    }

    /// <summary>Stufe 4: die Tresor-Datenbank liegt als .dmoffice/tresor.db im Cryptomator-Tresor, gearbeitet wird auf einer Arbeitskopie.</summary>
    [Fact]
    public async Task Tresor_Datenbank_liegt_im_Cryptomator_Tresor()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        var id = _fx.Id("dora");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" }));
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Akten" }));
        // Der Name der internen Daten ist vergeben
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/.dmoffice" })).StatusCode);
        Assert.True(Directory.Exists(Tresor.WorkDir(id)));

        // Schließen: zurückgeschrieben, Arbeitskopie weg
        (await c.PostAsync("/api/tresor/close", null)).EnsureSuccessStatusCode();
        Assert.False(Directory.Exists(Tresor.WorkDir(id)));
        using (var v = CryptomatorVault.Open(Files("dora"), "tresor-pass-1")!)
        {
            Assert.Contains(v.List("/.dmoffice"), e => e.Name == "tresor.db" && e.Size > 0);
            Assert.Contains(v.List("/"), e => e.Name == "Akten" && e.IsDir);
        }

        // Wieder öffnen: der Stand kommt aus dem Tresor
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.Contains("tresor:/Akten", (await Json(await c.GetAsync("/api/files/?path=tresor:"))).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("path").GetString()));

        // Nach Ruhe schreibt der Zeitgeber zurück (hier ohne Wartezeit)
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Neu nach Öffnen" }));
        var before = Tresor.WriteBackIdle;
        Tresor.WriteBackIdle = TimeSpan.Zero;
        try { Tresor.FlushAll(force: false); }
        finally { Tresor.WriteBackIdle = before; }
        // Gegenprobe ohne dmOffice: die Datenbank aus dem Tresor kennt den neuen Ordner
        var copy = Path.Combine(Path.GetTempPath(), "tdb-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var v = CryptomatorVault.Open(Files("dora"), "tresor-pass-1")!)
            using (var from = v.OpenRead(Tresor.DbInVault))
            using (var to = File.Create(copy)) from.CopyTo(to);
            var key = (await Tresor.KeyFromPasswordAsync(id, "tresor-pass-1"))!;
            using var tdb = new TresorDb(copy, key, Path.Combine(Path.GetTempPath(), "tdb-blobs"));
            Assert.NotNull(tdb.Scalar("SELECT id FROM files WHERE name = 'Neu nach Öffnen'"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(copy) + "*")) File.Delete(f); }
        Tresor.Delete(id);
        Assert.False(Directory.Exists(Tresor.WorkDir(id)));
    }

    /// <summary>Stufe 5: zentrale Sperre, Schalter für Nextcloud und WebDAV, Rechte im PROPFIND.</summary>
    [Fact]
    public async Task Schutz_und_Schalter_fuer_Nextcloud()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        var db = _fx.Space("dora").Db;
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" }));
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Akten" }));
        var dav = "/remote.php/dav/files/dora/";
        async Task<string> Propfind(string path)
        {
            var r = await c.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), dav + path) { Headers = { { "Depth", "1" } } });
            return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : "status " + (int)r.StatusCode;
        }

        // Zentrale Sperre: auch direkt in der Ablage nicht löschbar, verschiebbar, ersetzbar
        Assert.Throws<DavForbiddenException>(() => FileStore.Delete(db, FileStore.SystemVault));
        Assert.Throws<DavForbiddenException>(() => FileStore.Delete(db, FileStoreVaultFiles.DefaultRoot + "/vault.cryptomator"));
        Assert.Throws<DavForbiddenException>(() => FileStore.MoveOrCopy(db, FileStore.SystemVault, "weg", false, false));
        Assert.Throws<DavForbiddenException>(() => FileStore.MoveOrCopy(db, FileStoreVaultFiles.DefaultRoot + "/d", "weg", false, false));
        FileStore.Put(db, "fremd.txt", "x"u8.ToArray(), "text/plain");
        Assert.Throws<DavForbiddenException>(() => FileStore.MoveOrCopy(db, "fremd.txt", FileStoreVaultFiles.DefaultRoot + "/masterkey.cryptomator", true, false));
        FileStore.Delete(db, "fremd.txt");

        // Schalter aus (Vorgabe): über WebDAV weder sichtbar noch erreichbar
        Assert.DoesNotContain("dmVAULT", await Propfind(""));
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, dav + "dmVAULT/vault/vault.cryptomator"))).StatusCode);

        // Schalter an: sichtbar, Rechte ohne Löschen/Umbenennen/Verschieben, Löschen und Verschieben 403, Inhalte darunter frei
        var rootEtag = FileStore.Get(db, "")?.Etag;
        await Json(await c.PutAsJsonAsync("/api/tresor/settings", new { dav = true }));
        Assert.Contains("dmVAULT", await Propfind(""));
        // Neues ETag der Wurzel, sonst fragt der Nextcloud-Client nicht neu nach und sieht den Ordner nie
        Assert.NotEqual(rootEtag, FileStore.Get(db, "")?.Etag);
        var inner = await Propfind("dmVAULT/vault");
        Assert.Contains("<oc:permissions>RGCK</oc:permissions>", inner);
        Assert.Contains("<oc:permissions>RG</oc:permissions>", inner);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(new HttpRequestMessage(HttpMethod.Delete, dav + "dmVAULT"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(new HttpRequestMessage(HttpMethod.Delete, dav + "dmVAULT/vault/vault.cryptomator"))).StatusCode);
        var move = new HttpRequestMessage(new HttpMethod("MOVE"), dav + "dmVAULT/vault") { Headers = { { "Destination", "http://localhost" + dav + "umbenannt" } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(move)).StatusCode);
        Assert.NotNull(FileStore.Get(db, FileStoreVaultFiles.DefaultRoot + "/vault.cryptomator"));
        // Eine Datei unter d/ (wie Cryptomator sie schreibt) geht
        Assert.True((await c.PutAsync(dav + "dmVAULT/vault/d/test.c9r", new ByteArrayContent(new byte[100]))).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(new HttpRequestMessage(HttpMethod.Delete, dav + "dmVAULT/vault/d/test.c9r"))).StatusCode);

        // Wieder aus: verschwindet, nichts ist gelöscht
        await Json(await c.PutAsJsonAsync("/api/tresor/settings", new { dav = false }));
        Assert.DoesNotContain("dmVAULT", await Propfind(""));
        Assert.NotNull(FileStore.Get(db, FileStoreVaultFiles.DefaultRoot + "/vault.cryptomator"));

        // „Tresor löschen“ darf
        Tresor.Delete(_fx.Id("dora"));
        Assert.Null(FileStore.Get(db, FileStore.SystemVault));
    }

    [Fact]
    public async Task Tresor_herunterladen_und_Grenze_fuer_ZIP()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" }));
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Akten" }));
        var form = new MultipartFormDataContent { { new StringContent("Vertrag 4711"), "file", "Vertrag.txt" } };
        await Json(await c.PostAsync("/api/files/upload?path=" + Uri.EscapeDataString("tresor:/Akten"), form));

        var info = await Json(await c.GetAsync("/api/tresor/download"));
        Assert.True(info.GetProperty("available").GetBoolean());
        Assert.True(info.GetProperty("size").GetInt64() > 0);

        // Falsches Passwort: nichts
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/tresor/download/check", new { password = "falsch" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/tresor/download/zip?token=erfunden")).StatusCode);
        async Task<string> Token() => (await Json(await c.PostAsJsonAsync("/api/tresor/download/check", new { password = "tresor-pass-1" }))).GetProperty("token").GetString()!;

        // Das ZIP ist ein Cryptomator-Tresor, der sich mit dem Tresor-Passwort öffnen lässt
        var token = await Token();
        var r = await c.GetAsync("/api/tresor/download/zip?token=" + token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // Nur einmal
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/tresor/download/zip?token=" + token)).StatusCode);
        var dir = Path.Combine(Path.GetTempPath(), "dm-vaultzip-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var zip = new System.IO.Compression.ZipArchive(await r.Content.ReadAsStreamAsync()))
            {
                Assert.All(zip.Entries.Where(e => e.Length > 0), e => Assert.Equal(e.Length, e.CompressedLength));
                System.IO.Compression.ZipFileExtensions.ExtractToDirectory(zip, dir);
            }
            Assert.True(File.Exists(Path.Combine(dir, "vault", "vault.cryptomator")));
            Assert.True(File.Exists(Path.Combine(dir, "vault", "masterkey.cryptomator")));
            using var v = LocalVault.Open(Path.Combine(dir, "vault"), "tresor-pass-1")!;
            Assert.Contains("F /Akten/Vertrag.txt Vertrag 4711", Dump(v));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }

        // Für die App als TAR: dieselben Dateien, ebenso lesbar
        var tarDir = Path.Combine(Path.GetTempPath(), "dm-vaulttar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = await c.GetAsync("/api/tresor/download/zip?format=tar&token=" + await Token());
            Assert.Equal(HttpStatusCode.OK, t.StatusCode);
            Directory.CreateDirectory(tarDir);
            await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(await t.Content.ReadAsStreamAsync(), tarDir, false);
            using var v = LocalVault.Open(Path.Combine(tarDir, "vault"), "tresor-pass-1")!;
            Assert.Contains("F /Akten/Vertrag.txt Vertrag 4711", Dump(v));
        }
        finally { try { Directory.Delete(tarDir, true); } catch (IOException) { } }

        // Grenze für ZIP-Downloads: größer → 413, vorher schon in der Auskunft
        Dav.FilesApi.ZipLimitOf = () => 10;
        try
        {
            Assert.Equal(10, (await Json(await c.GetAsync("/api/files/zip-limit"))).GetProperty("limit").GetInt64());
            Assert.Equal((HttpStatusCode)413, (await c.PostAsync("/api/files/zip", new FormUrlEncodedContent([new("paths", "tresor:/Akten")]))).StatusCode);
            Assert.Equal((HttpStatusCode)413, (await c.GetAsync("/api/tresor/download/zip?token=" + await Token())).StatusCode);
        }
        finally { Dav.FilesApi.ZipLimitOf = null; }
        Tresor.Delete(_fx.Id("dora"));
    }

    [Fact]
    public async Task Offline_Kopie_fuer_die_App()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" }));
        static string Q(string s) => Uri.EscapeDataString(s);
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Akten" }));
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Andere" }));
        var longName = new string('x', 200) + " ä.txt";
        foreach (var (dir, name, text) in new[] { ("tresor:/Akten", "Vertrag.txt", "Vertrag 4711"), ("tresor:/Akten/2026", longName, "lang"), ("tresor:/Andere", "nicht.txt", "nicht") })
            await Json(await c.PostAsync("/api/files/upload?path=" + Q(dir), new MultipartFormDataContent { { new StringContent(text), "file", name } }));

        var items = (await Json(await c.GetAsync("/api/tresor/vault/offline?path=" + Q("tresor:/Akten")))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("path").GetString() == "masterkey.cryptomator");
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.GetProperty("etag").GetString())));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/tresor/vault/offline?path=" + Q("tresor:/Gibt es nicht"))).StatusCode);

        // Nur diese Dateien, einzeln geholt: ein Teil-Tresor, der sich mit dem Tresor-Passwort öffnen lässt
        var dirTmp = Path.Combine(Path.GetTempPath(), "dm-offline-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var i in items)
            {
                var rel = i.GetProperty("path").GetString()!;
                var target = Path.Combine(dirTmp, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, await c.GetByteArrayAsync("/api/tresor/vault/file?path=" + Q(rel)));
            }
            using var v = LocalVault.Open(dirTmp, "tresor-pass-1")!;
            var dump = Dump(v);
            Assert.Contains("F /Akten/Vertrag.txt Vertrag 4711", dump);
            Assert.Contains("F /Akten/2026/" + longName + " lang", dump);
            Assert.DoesNotContain(dump, l => l.Contains("Andere") || l.Contains(".dmoffice"));
        }
        finally { try { Directory.Delete(dirTmp, true); } catch (IOException) { } }

        // Ganzer Tresor: ohne die internen Daten
        var all = (await Json(await c.GetAsync("/api/tresor/vault/offline?path=tresor:"))).GetProperty("items").EnumerateArray().Count();
        Assert.True(all > items.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/tresor/vault/file?path=" + Q("../vault/masterkey.cryptomator"))).StatusCode);

        // Geschlossen: keine Liste (423), die verschlüsselten Dateien aber schon
        await Json(await c.PostAsync("/api/tresor/close", null));
        Assert.Equal((HttpStatusCode)423, (await c.GetAsync("/api/tresor/vault/offline?path=" + Q("tresor:/Akten"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/tresor/vault/file?path=masterkey.cryptomator")).StatusCode);
        Tresor.Delete(_fx.Id("dora"));
    }

    /// <summary>Von außen in den Cryptomator-Tresor geschrieben (Cryptomator über Nextcloud, WebDAV): erscheint beim Öffnen des Ordners.</summary>
    [Fact]
    public async Task Aenderungen_von_aussen_erscheinen_im_Tresor()
    {
        Tresor.Delete(_fx.Id("dora"));
        var c = _fx.As("dora");
        static string Q(string s) => Uri.EscapeDataString(s);
        async Task<List<(string Name, long Size)>> List(string path) =>
            (await Json(await c.GetAsync("/api/files/?path=" + Q(path)))).GetProperty("items").EnumerateArray()
                .Select(i => (i.GetProperty("name").GetString()!, i.GetProperty("size").GetInt64())).OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" }));
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "tresor:/Akten" }));
        foreach (var (name, text) in new[] { ("a.txt", "kurz"), ("b.txt", "weg") })
            await Json(await c.PostAsync("/api/files/upload?path=" + Q("tresor:/Akten"), new MultipartFormDataContent { { new StringContent(text), "file", name } }));
        // Die Datenbank liegt jetzt in .dmoffice im Tresor
        Tresor.Flush(_fx.Id("dora"), force: true);

        // Von außen, wie Cryptomator es täte: neue Datei, neuer Ordner mit Datei, Datei gelöscht, Datei mit anderer Größe
        using (var v = CryptomatorVault.Open(Files("dora"), "tresor-pass-1")!)
        {
            await v.WriteAsync("/Akten/extern ä.txt", new MemoryStream("von außen 4711"u8.ToArray()));
            v.CreateDirectory("/Neu");
            await v.WriteAsync("/Neu/x.txt", new MemoryStream("x"u8.ToArray()));
            v.Delete("/Akten/b.txt");
            await v.WriteAsync("/Akten/a.txt", new MemoryStream("jetzt deutlich länger"u8.ToArray()));
        }

        // Direkt in den neuen Ordner (der Weg dorthin wird mit abgeglichen)
        Assert.Equal([("x.txt", 1L)], await List("tresor:/Neu"));
        Assert.Equal(["Akten", "Neu"], (await List("tresor:")).Select(x => x.Name).ToList());
        var akten = await List("tresor:/Akten");
        Assert.Equal(["a.txt", "extern ä.txt"], akten.Select(x => x.Name).ToList());
        Assert.Equal(Encoding.UTF8.GetByteCount("jetzt deutlich länger"), akten[0].Size);
        Assert.Equal("von außen 4711", await c.GetStringAsync("/api/files/download?path=" + Q("tresor:/Akten/extern ä.txt")));
        Assert.Equal("jetzt deutlich länger", await c.GetStringAsync("/api/files/download?path=" + Q("tresor:/Akten/a.txt")));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/files/download?path=" + Q("tresor:/Akten/b.txt"))).StatusCode);
        // Zweimal öffnen ändert nichts; über dmOffice geht es normal weiter
        Assert.Equal(akten, await List("tresor:/Akten"));
        await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "tresor:/Neu/x.txt", to = "tresor:/Akten/x.txt" }));
        Assert.Empty(await List("tresor:/Neu"));
        Assert.Contains(("x.txt", 1L), await List("tresor:/Akten"));
        Tresor.Delete(_fx.Id("dora"));
    }

    /// <summary>Umbenennen in dmVAULT (Schema-Schritt 21): alter Ordner zieht samt Inhalt um, ein eigener Ordner dmVAULT weicht aus.</summary>
    [Fact]
    public void System_Tresor_heisst_dmVAULT()
    {
        var dir = Directory.CreateTempSubdirectory("dm-umzug-");
        var path = Path.Combine(dir.FullName, "u.db");
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var user = "umzug-" + Guid.NewGuid().ToString("N");
        string rootEtag;
        try
        {
            {
                var db = new UserDb(path, key, user);
                using (FileStore.SystemAccess())
                {
                    FileStore.Mkcol(db, "dmVAULT");
                    FileStore.Put(db, "dmVAULT/eigen.txt", "eigen"u8.ToArray(), "text/plain");
                    FileStore.Mkcol(db, FileStore.OldSystemVault);
                    FileStore.Mkcol(db, FileStore.OldSystemVault + "/vault");
                    FileStore.Mkcol(db, FileStore.OldSystemVault + "/vault/d");
                    FileStore.Put(db, FileStore.OldSystemVault + "/vault/vault.cryptomator", "kopf"u8.ToArray(), "application/octet-stream");
                    FileStore.Put(db, FileStore.OldSystemVault + "/vault/d/x.c9r", "inhalt"u8.ToArray(), "application/octet-stream");
                }
                rootEtag = FileStore.Get(db, "")!.Etag;
                db.Exec("PRAGMA user_version=20");
            }
            {
                var db = new UserDb(path, key, user);
                Assert.Equal(UserDb.LatestVersion, Convert.ToInt32(db.Scalar("PRAGMA user_version")));
                Assert.Null(FileStore.Get(db, FileStore.OldSystemVault));
                Assert.Equal("kopf", Encoding.UTF8.GetString(FileStore.Data(db, "dmVAULT/vault/vault.cryptomator")!));
                Assert.Equal("inhalt", Encoding.UTF8.GetString(FileStore.Data(db, "dmVAULT/vault/d/x.c9r")!));
                Assert.Equal(["dmVAULT/vault/d", "dmVAULT/vault/vault.cryptomator"], FileStore.Children(db, "dmVAULT/vault").Select(f => f.Path).Order(StringComparer.Ordinal).ToList());
                Assert.Equal("eigen", Encoding.UTF8.GetString(FileStore.Data(db, "dmVAULT (eigener Ordner)/eigen.txt")!));
                Assert.NotEqual(rootEtag, FileStore.Get(db, "")!.Etag);
                Assert.Equal(FileStoreVaultFiles.DefaultRoot, "dmVAULT/vault");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { dir.Delete(true); } catch (IOException) { } }
    }
}
