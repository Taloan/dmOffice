using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>
/// Eigene Cryptomator-Tresore in der Dateiablage öffnen (docs/tresor-cryptomator-konzept.md, Stufe 3b) - mit einem von Cryptomator
/// angelegten Tresor (Fixtures/cryptomator/reference, Passwort „Pässwört ü 1“) als Ordner TEST/dmOffice.
/// </summary>
[Collection("server")]
public class CryptomatorFolderTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public CryptomatorFolderTests(ChatFixture fx) => _fx = fx;
    private const string Password = "Pässwört ü 1";

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string Q(string s) => Uri.EscapeDataString(s);

    /// <summary>Den Referenz-Tresor Datei für Datei in die Ablage legen, wie ein Nextcloud-Client es täte.</summary>
    private void PutReference(Core.UserDb db, string root)
    {
        if (FileStore.Get(db, root) != null) FileStore.Delete(db, root);
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cryptomator", "reference");
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories).Order())
            FileStore.EnsureDir(db, root + "/" + Path.GetRelativePath(src, d).Replace('\\', '/'));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            FileStore.Put(db, root + "/" + Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllBytes(f), "application/octet-stream");
    }

    [Fact]
    public async Task Eigenen_Cryptomator_Tresor_oeffnen_und_bearbeiten()
    {
        var c = _fx.As("dora");
        var db = _fx.Space("dora").Db;
        PutReference(db, "TEST/dmOffice");

        // Erkennen: in der Liste von TEST gekennzeichnet, der Ordner selbst gesperrt
        Assert.Contains("TEST/dmOffice", (await Json(await c.GetAsync("/api/files/?path=TEST"))).GetProperty("vaults").EnumerateArray().Select(x => x.GetString()));
        Assert.False((await Json(await c.GetAsync("/api/files/?path=" + Q("TEST/dmOffice")))).GetProperty("vault").GetProperty("unlocked").GetBoolean());
        Assert.Equal((HttpStatusCode)423, (await c.GetAsync("/api/files/?path=" + Q("cmv:/TEST/dmOffice"))).StatusCode);

        // Entsperren: falsches Passwort, dann richtig
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/vault/unlock", new { path = "TEST/dmOffice", password = "falsch" })).StatusCode);
        Assert.Equal("cmv:/TEST/dmOffice", (await Json(await c.PostAsJsonAsync("/api/files/vault/unlock", new { path = "TEST/dmOffice", password = Password }))).GetProperty("path").GetString());
        Assert.True((await Json(await c.GetAsync("/api/files/?path=" + Q("TEST/dmOffice")))).GetProperty("vault").GetProperty("unlocked").GetBoolean());

        // Lesen: Namen und Inhalt wie in Cryptomator
        var root = await Json(await c.GetAsync("/api/files/?path=" + Q("cmv:/TEST/dmOffice")));
        var names = root.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Contains("Grüße aus Köln.txt", names);
        Assert.Contains("Ebene 1", names);
        Assert.Equal("Ärger über Öl und Übermut – ß", await c.GetStringAsync("/api/files/download?path=" + Q("cmv:/TEST/dmOffice/Grüße aus Köln.txt")));

        // Ändern: Ordner, Hochladen, Umbenennen, Löschen
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "cmv:/TEST/dmOffice/Neu von dmOffice" }));
        var form = new MultipartFormDataContent { { new StringContent("Hallo aus dmOffice"), "file", "Notiz ü.txt" } };
        await Json(await c.PostAsync("/api/files/upload?path=" + Q("cmv:/TEST/dmOffice/Neu von dmOffice"), form));
        await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "cmv:/TEST/dmOffice/Neu von dmOffice/Notiz ü.txt", to = "cmv:/TEST/dmOffice/Ebene 1/Notiz umbenannt.txt", copy = false }));
        Assert.Equal("Hallo aus dmOffice", await c.GetStringAsync("/api/files/download?path=" + Q("cmv:/TEST/dmOffice/Ebene 1/Notiz umbenannt.txt")));
        await Json(await c.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "cmv:/TEST/dmOffice/leer.txt" } }));

        // Herausnehmen (Kopie) und Hineinverschieben (Original endgültig weg)
        await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "cmv:/TEST/dmOffice/Grüße aus Köln.txt", to = "Köln Kopie.txt", copy = true }));
        Assert.Equal("Ärger über Öl und Übermut – ß", Encoding.UTF8.GetString(FileStore.Data(db, "Köln Kopie.txt")!));
        await Json(await c.PostAsJsonAsync("/api/files/move", new { from = "Köln Kopie.txt", to = "cmv:/TEST/dmOffice/Köln zurück.txt", copy = false }));
        Assert.Null(FileStore.Get(db, "Köln Kopie.txt"));
        Assert.DoesNotContain(FileStore.Trash(db), t => t.Path == "Köln Kopie.txt");

        // Die verschlüsselten Dateien sind weiter normale Dateien der Ablage - .bkup von Cryptomator bleibt unberührt
        Assert.Contains(FileStore.Descendants(db, "TEST/dmOffice"), f => f.Path.EndsWith(".bkup"));

        // Gegenprobe: Cryptomator liest den von dmOffice geänderten Tresor (nur mit DMOFFICE_CRYPTOMATOR_CHECK=1)
        if (Environment.GetEnvironmentVariable("DMOFFICE_CRYPTOMATOR_CHECK") == "1")
        {
            var dir = Path.Combine(Path.GetTempPath(), "cm-own-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var f in FileStore.Descendants(db, "TEST/dmOffice"))
                {
                    var target = Path.Combine(dir, f.Path["TEST/dmOffice/".Length..].Replace('/', Path.DirectorySeparatorChar));
                    if (f.IsDir) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, FileStore.Data(db, f.Path)!);
                }
                var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "cryptomator-check", "check.ps1"));
                var psi = new ProcessStartInfo("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "dump", dir, Password])
                { RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                Assert.Equal(0, p.ExitCode);
                var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Hallo aus dmOffice"))).ToLowerInvariant();
                Assert.Contains($"F\t/Ebene 1/Notiz umbenannt.txt\t18\t{sha}", output);
                Assert.Contains("D\t/Neu von dmOffice\t", output);
                Assert.DoesNotContain("/leer.txt", output);
                Assert.Contains("F\t/Köln zurück.txt\t", output.Normalize(NormalizationForm.FormC));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }

        // Sperren: danach 423
        await Json(await c.PostAsJsonAsync("/api/files/vault/lock", new { path = "TEST/dmOffice" }));
        Assert.Equal((HttpStatusCode)423, (await c.GetAsync("/api/files/?path=" + Q("cmv:/TEST/dmOffice"))).StatusCode);
        FileStore.Delete(db, "TEST");
    }
}
