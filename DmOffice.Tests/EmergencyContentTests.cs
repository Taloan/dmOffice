using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Emergency;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>
/// Notfallpaket mit Notfalltext und Referenzkopien: verschlüsselt mit dem Paketschlüssel, lesbar für den Besitzer aus dem
/// Tresor und für die Vertrauensperson erst nach der Frist mit dem Notfallcode; Kopie folgt einer ersetzten Datei.
/// </summary>
[Collection("server")]
public class EmergencyContentTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public EmergencyContentTests(SharedCalendarFixture fx) => _fx = fx;

    private string Id(string login) => _fx.Users[login].Id;
    private T Get<T>() where T : notnull => _fx.Factory.Services.GetRequiredService<T>();
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public void Schluessel_aus_Code_und_Serverhaelfte()
    {
        var k = EmergencyVault.Create();
        var box = EmergencyVault.Seal(k.PublicKey, "Geheim"u8.ToArray());
        // Code in Gruppen, klein geschrieben, mit Leerzeichen - zählt gleich
        var typed = EmergencyVault.Format(k.Code).ToLowerInvariant().Replace("-", " ");
        var priv = EmergencyVault.PrivateKey(EmergencyVault.PackageKey(typed, k.ServerHalf, k.Salt), k.SealedPrivate);
        Assert.Equal("Geheim", Encoding.UTF8.GetString(EmergencyVault.Open(priv, box)));
        // Ohne die Hälfte des Servers oder mit falschem Code: nichts
        Assert.ThrowsAny<CryptographicException>(() => EmergencyVault.PrivateKey(EmergencyVault.PackageKey(k.Code, new byte[32], k.Salt), k.SealedPrivate));
        Assert.ThrowsAny<CryptographicException>(() => EmergencyVault.PrivateKey(EmergencyVault.PackageKey("AAAAAAAAAAAAAAAAAAAA", k.ServerHalf, k.Salt), k.SealedPrivate));
    }

    [Fact]
    public async Task Notfalltext_und_Kopie_erst_nach_der_Frist_mit_Code()
    {
        var store = Get<EmergencyStore>();
        var content = Get<EmergencyContent>();
        var db = Get<Spaces>().Get(Id("anna")).Db;
        var file = "Testament-" + Guid.NewGuid().ToString("N")[..6] + ".txt";
        await FileStore.PutAsync(db, file, new MemoryStream("Stand 1"u8.ToArray()), "text/plain");
        var pkg = store.Save(Id("anna"), new EmergencyPackage { Name = "Familie", Requesters = [Id("cleo")], WaitDays = 2, HasText = true }).Id;

        // Schlüssel in einem Tresor der Besitzerin
        var dir = Directory.CreateTempSubdirectory("tresor-").FullName;
        using var tresor = new TresorDb(Path.Combine(dir, "t.db"), RandomNumberGenerator.GetBytes(32), Path.Combine(dir, "blobs"));
        var code = content.CreateKeys(tresor, Id("anna"), pkg);
        Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){4}$", code);
        content.WriteText(Id("anna"), store.Package(pkg)!, "<h2>Was zu tun ist</h2><p>Der Ordner steht im Regal.</p>");
        content.AddFile(Id("anna"), pkg, FileStore.Get(db, file)!.FileId);
        var p = store.Package(pkg)!;
        Assert.Contains("Regal", content.ReadText(p, content.PrivateForOwner(tresor, p)));
        // Auf der Platte nichts im Klartext
        var raw = Directory.GetFiles(Path.Combine(Get<DataPaths>().Root, "emergency", pkg), "*.bin", SearchOption.AllDirectories);
        Assert.Equal(2, raw.Length);
        Assert.All(raw, f => Assert.DoesNotContain("Regal", Encoding.UTF8.GetString(File.ReadAllBytes(f))));

        // Vor der Frist: auch mit Code nichts
        var cleo = _fx.As("cleo");
        Assert.Equal(HttpStatusCode.NotFound, (await cleo.PostAsJsonAsync($"/api/emergency/packages/{pkg}/open", new { code })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cleo.PostAsync($"/api/emergency/packages/{pkg}/request", null)).StatusCode);
        Get<EmergencyService>().Tick(DateTime.UtcNow.AddDays(3));

        // Danach: falscher Code nein, richtiger ja - Text zuerst, Dateien darunter
        Assert.Equal(HttpStatusCode.Forbidden, (await cleo.PostAsJsonAsync($"/api/emergency/packages/{pkg}/open", new { code = "ABCD-EFGH-JKMN-PQRS-TUVW" })).StatusCode);
        var opened = await Json(await cleo.PostAsJsonAsync($"/api/emergency/packages/{pkg}/open", new { code }));
        Assert.Contains("Regal", opened.GetProperty("html").GetString());
        var fid = opened.GetProperty("files")[0].GetProperty("id").GetString();
        // Andere sehen nichts
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").PostAsJsonAsync($"/api/emergency/packages/{pkg}/open", new { code })).StatusCode);

        // Herkunft ersetzt: die Kopie folgt
        await FileStore.PutAsync(db, file, new MemoryStream("Stand 2"u8.ToArray()), "text/plain");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/emergency/packages/{pkg}/files/{fid}");
        req.Headers.Add("X-Notfallcode", code);
        content.Refresh(store.Package(pkg)!);
        var got = await cleo.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal("Stand 2", await got.Content.ReadAsStringAsync());

        // Gelöscht: die letzte Kopie bleibt, mit Hinweis
        FileStore.Delete(db, file);
        Assert.True(content.Refresh(store.Package(pkg)!).Files[0].Missing);

        store.Delete(Id("anna"), pkg);
        content.Forget(pkg);
        Assert.False(Directory.Exists(Path.Combine(Get<DataPaths>().Root, "emergency", pkg)));
    }
}
