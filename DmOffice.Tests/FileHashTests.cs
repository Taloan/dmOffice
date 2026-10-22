using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Notes;

namespace DmOffice.Tests;

/// <summary>Pruefsummen (SHA-256) der Dateiinhalte: beim Schreiben auf allen Wegen, Nachrechnen fuer Altes,
/// Abfrage "liegt dieser Inhalt schon irgendwo?" fuer die App (ohne Papierkorb und notizi-Ordner).</summary>
public class FileHashTests
{
    private static UserDb NewDb(string? root = null)
    {
        var dir = Directory.CreateTempSubdirectory("dmhash");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), RandomNumberGenerator.GetBytes(32), "hash-" + Guid.NewGuid().ToString("N"));
        if (root != null) db.SetJson("settings", new UserSettings { Notizi = new NotiziSettings { Root = root } });
        return db;
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string H(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string? Col(UserDb db, string sql, params (string, object?)[] args) => db.Scalar(sql, args) as string;

    [Fact]
    public void Schema_18_Spalte_und_Index()
    {
        var db = NewDb();
        Assert.Equal(18L, db.Scalar("PRAGMA user_version"));
        foreach (var t in UserDb.HashTables)
            Assert.Equal(1L, db.Scalar($"SELECT COUNT(*) FROM pragma_table_info('{t}') WHERE name='sha256'"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='files_sha256'"));
    }

    [Fact]
    public async Task Jeder_Schreibweg_traegt_die_Pruefsumme_ein()
    {
        var db = NewDb();
        FileStore.EnsureDir(db, "a");
        var x = B("Inhalt eins");
        FileStore.Put(db, "a/put.txt", x, "text/plain");
        Assert.Equal(H(x), FileHashes.Of(db, "a/put.txt"));

        // Stream ueber mehrere Bloecke
        var big = RandomNumberGenerator.GetBytes(200_000);
        await FileStore.PutAsync(db, "a/gross.bin", new MemoryStream(big), null);
        Assert.Equal(H(big), FileHashes.Of(db, "a/gross.bin"));

        // Neuer Name (Freigabelink), schon geschriebener Blob (OnlyOffice)
        var p = await FileStore.PutNewAsync(db, "a/put.txt", new MemoryStream(B("neu")), null);
        Assert.Equal(H(B("neu")), FileHashes.Of(db, p));
        FileStore.PutBlob(db, "a/office.docx", db.Blobs.Write(B("office")), null);
        Assert.Equal(H(B("office")), FileHashes.Of(db, "a/office.docx"));

        // Leere Datei hat auch eine Pruefsumme
        FileStore.Put(db, "a/leer.txt", [], null);
        Assert.Equal(H([]), FileHashes.Of(db, "a/leer.txt"));

        // Ueberschreiben: neue Pruefsumme, die Version behaelt die alte
        var y = B("Inhalt zwei");
        FileStore.Put(db, "a/put.txt", y, "text/plain");
        Assert.Equal(H(y), FileHashes.Of(db, "a/put.txt"));
        var fileId = FileStore.Get(db, "a/put.txt")!.FileId;
        Assert.Equal(H(x), Col(db, "SELECT sha256 FROM file_versions WHERE fileid=$f", ("$f", fileId)));

        // Version wiederherstellen
        var v = FileStore.Versions(db, fileId).Single();
        FileStore.RestoreVersion(db, "a/put.txt", v.Id);
        Assert.Equal(H(x), FileHashes.Of(db, "a/put.txt"));

        // Kopieren und Verschieben
        FileStore.MoveOrCopy(db, "a/put.txt", "a/kopie.txt", false, copy: true);
        Assert.Equal(H(x), FileHashes.Of(db, "a/kopie.txt"));
        FileStore.MoveOrCopy(db, "a", "b", false, copy: true);
        Assert.Equal(H(big), FileHashes.Of(db, "b/gross.bin"));
        FileStore.MoveOrCopy(db, "b/gross.bin", "b/verschoben.bin", false, copy: false);
        Assert.Equal(H(big), FileHashes.Of(db, "b/verschoben.bin"));

        // Papierkorb und zurueck
        FileStore.Delete(db, "a/kopie.txt", out var trashId);
        Assert.Equal(H(x), Col(db, "SELECT sha256 FROM file_trash_items WHERE trash_id=$t", ("$t", trashId)));
        var back = FileStore.Restore(db, trashId);
        Assert.Equal(H(x), FileHashes.Of(db, back));

        // Ordner haben keine
        Assert.Null(FileHashes.Of(db, "a"));
        Assert.Equal(0, FileHashes.PendingFiles(db));
    }

    [Fact]
    public async Task Notizi_Abgleich_traegt_die_Pruefsumme_ein()
    {
        var db = NewDb("notizisync");
        FileStore.EnsureDir(db, "notizisync");
        var t = new DbTransport(db, "notizisync");
        await t.WriteAsync("n.json", B("{\"a\":1}"), "");
        Assert.Equal(H(B("{\"a\":1}")), FileHashes.Of(db, "notizisync/n.json"));
    }

    /// <summary>Wie vor Version 18: Inhalt als Blob, aber ohne Pruefsumme.</summary>
    private static void Unhashed(UserDb db, string path, byte[] data)
    {
        FileStore.Put(db, path, data, null);
        db.Exec("UPDATE files SET sha256=NULL WHERE path=$p", ("$p", path));
    }

    [Fact]
    public void Nachrechnen_fuellt_fehlende_unterbrechbar_und_laesst_Geaendertes_in_Ruhe()
    {
        var db = NewDb();
        var data = Enumerable.Range(0, 5).Select(i => RandomNumberGenerator.GetBytes(70_000 + i)).ToList();
        for (int i = 0; i < data.Count; i++) Unhashed(db, $"f{i}.bin", data[i]);
        Unhashed(db, "leer.txt", []);
        // Version und Papierkorb ohne Pruefsumme
        FileStore.Put(db, "f0.bin", B("neu"), null);
        db.Exec("UPDATE file_versions SET sha256=NULL");
        FileStore.Delete(db, "f4.bin");
        db.Exec("UPDATE file_trash_items SET sha256=NULL");
        db.Exec("UPDATE files SET sha256=NULL");
        Assert.Equal(5, FileHashes.PendingFiles(db));   // f0 (neu), f1..f3, leer
        Assert.Equal(7, FileHashes.Pending(db));        // dazu die Version von f0 und f4 im Papierkorb

        // Abgebrochen: was fertig ist, bleibt; der naechste Lauf macht weiter
        using (var cts = new CancellationTokenSource())
        {
            Assert.Throws<OperationCanceledException>(() => FileHashes.Compute(db, cts.Token, n => { if (n == 2) cts.Cancel(); }));
        }
        Assert.Equal(5, FileHashes.Pending(db));
        Assert.Equal(5, FileHashes.Compute(db));
        Assert.Equal(0, FileHashes.Pending(db));
        Assert.Equal(H(B("neu")), FileHashes.Of(db, "f0.bin"));
        for (int i = 1; i < 4; i++) Assert.Equal(H(data[i]), FileHashes.Of(db, $"f{i}.bin"));
        Assert.Equal(H([]), FileHashes.Of(db, "leer.txt"));
        Assert.Equal(H(data[0]), Col(db, "SELECT sha256 FROM file_versions"));
        Assert.Equal(H(data[4]), Col(db, "SELECT sha256 FROM file_trash_items"));
        Assert.Equal(0, FileHashes.Compute(db));   // nichts mehr zu tun
    }

    [Fact]
    public void Nachrechnen_traegt_bei_fehlendem_oder_falschem_Blob_nichts_ein()
    {
        var db = NewDb();
        Unhashed(db, "weg.bin", B("verschwunden"));
        Unhashed(db, "kurz.bin", B("abgeschnitten?"));
        var blob = (string)db.Scalar("SELECT blob FROM files WHERE path='weg.bin'")!;
        File.Delete(db.Blobs.PathOf(blob));
        db.Exec("UPDATE files SET size=size+1 WHERE path='kurz.bin'");   // Laenge passt nicht zur Zeile
        Assert.Equal(0, FileHashes.Compute(db));
        Assert.Null(FileHashes.Of(db, "weg.bin"));
        Assert.Null(FileHashes.Of(db, "kurz.bin"));
    }

    [Fact]
    public void Auslagern_alter_Inhalte_bringt_die_Pruefsumme_mit()
    {
        var db = NewDb();
        var data = RandomNumberGenerator.GetBytes(100_000);
        db.Exec("INSERT INTO files(path,parent,is_dir,data,size,etag,mime,created,modified) VALUES('alt.bin','',0,$d,$s,'\"x\"',NULL,$n,$n)",
            ("$d", data), ("$s", data.LongLength), ("$n", DateTime.UtcNow.ToString("o")));
        // Solange der Inhalt noch in der Datenbank liegt, rechnet das Nachrechnen ihn nicht (macht das Auslagern)
        Assert.Equal(0, FileHashes.Pending(db));
        Assert.Equal(1, FileHashes.PendingFiles(db));
        FileStore.MigrateBlobs(db);
        Assert.Equal(H(data), FileHashes.Of(db, "alt.bin"));
    }

    [Fact]
    public void Abfrage_findet_Inhalt_ueberall_ausser_Papierkorb_und_notizi()
    {
        var db = NewDb("notizisync");
        FileStore.EnsureDir(db, "Fotos/2024");
        FileStore.EnsureDir(db, "notizisync");
        byte[] foto = B("foto"), weg = B("geloescht"), notiz = B("notiz"), fassung = B("fassung");
        FileStore.Put(db, "Fotos/2024/IMG_1.jpg", foto, null);
        FileStore.Put(db, "Fotos/anders.jpg", foto, null);
        FileStore.Put(db, "x.txt", weg, null);
        FileStore.Delete(db, "x.txt");
        FileStore.Put(db, "notizisync/n.json", notiz, null);
        FileStore.Put(db, "f.txt", fassung, null);
        FileStore.Put(db, "f.txt", B("jetzt"), null);

        var found = FileHashes.Lookup(db, [(H(foto), foto.Length), (H(weg), weg.Length), (H(notiz), notiz.Length),
            (H(fassung), fassung.Length), (H(foto).ToUpperInvariant(), foto.Length), (H(foto), foto.Length + 1), ("kaputt", 1)]);
        var hit = Assert.Single(found);
        Assert.Equal((H(foto), (long)foto.Length), hit.Key);
        Assert.Equal("Fotos/anders.jpg", hit.Value);   // der kuerzere Pfad als Beleg
    }
}

/// <summary>POST /api/files/hashes und die Pruefsumme in den Details - ueber HTTP mit App-Passwort.</summary>
[Collection("server")]
public class FileHashApiTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public FileHashApiTests(FilesServerFixture fx) => _fx = fx;

    private static string H(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public void App_Passwort_darf_die_Abfrage()
    {
        Assert.True(Auth.AppPasswordAllowed("/api/files/hashes"));
    }

    [Fact]
    public async Task Hochladen_ueber_Web_und_WebDAV_dann_abfragen()
    {
        var c = _fx.Client();
        var dir = "hash-" + Guid.NewGuid().ToString("N")[..6];
        Assert.True((await c.PostAsJsonAsync("/api/files/mkdir", new { path = dir })).IsSuccessStatusCode);
        var web = RandomNumberGenerator.GetBytes(90_000);
        var form = new MultipartFormDataContent { { new ByteArrayContent(web), "files", "web.jpg" } };
        Assert.True((await c.PostAsync($"/api/files/upload?path={dir}", form)).IsSuccessStatusCode);
        var dav = RandomNumberGenerator.GetBytes(1000);
        var put = await c.PutAsync($"/remote.php/dav/files/{FilesServerFixture.Login}/{dir}/dav.jpg", new ByteArrayContent(dav));
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        var saved = await c.PostAsJsonAsync("/api/files/save", new { path = $"{dir}/text.txt", text = "hallo", create = true });
        Assert.True(saved.IsSuccessStatusCode);

        var unknown = RandomNumberGenerator.GetBytes(10);
        var r = await c.PostAsJsonAsync("/api/files/hashes", new
        {
            items = new[]
            {
                new { sha256 = H(web), size = (long)web.Length }, new { sha256 = H(dav), size = (long)dav.Length },
                new { sha256 = H(B("hallo")), size = 5L }, new { sha256 = H(unknown), size = 10L },
            }
        });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var json = await r.Content.ReadFromJsonAsync<JsonElement>();
        var found = json.GetProperty("found").EnumerateArray().ToDictionary(e => e.GetProperty("sha256").GetString()!, e => e.GetProperty("path").GetString());
        Assert.Equal($"{dir}/web.jpg", found[H(web)]);
        Assert.Equal($"{dir}/dav.jpg", found[H(dav)]);
        Assert.Equal($"{dir}/text.txt", found[H(B("hallo"))]);
        Assert.False(found.ContainsKey(H(unknown)));
        Assert.Equal(0, json.GetProperty("pending").GetInt64());

        // Details zeigen die Pruefsumme
        var info = await c.GetFromJsonAsync<JsonElement>($"/api/files/info?path={dir}/dav.jpg");
        Assert.Equal(H(dav), info.GetProperty("sha256").GetString());

        // Zu viele auf einmal: abgelehnt
        var many = Enumerable.Range(0, FileHashes.MaxLookup + 1).Select(_ => new { sha256 = H(unknown), size = 10L }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/hashes", new { items = many })).StatusCode);
        // Nur im Zielordner gesucht ("Duplikate ueberspringen" aus)
        async Task<int> InDir(string d) => (await (await c.PostAsJsonAsync("/api/files/hashes", new { items = new[] { new { sha256 = H(dav), size = (long)dav.Length } }, dir = d }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("found").GetArrayLength();
        Assert.Equal(1, await InDir(dir));
        Assert.Equal(0, await InDir(dir + "/anderswo"));
        Assert.Equal(0, await InDir(""));

        // In den Papierkorb: sofort nicht mehr vorhanden
        Assert.True((await c.PostAsJsonAsync("/api/files/delete", new { paths = new[] { $"{dir}/dav.jpg" } })).IsSuccessStatusCode);
        var after = await (await c.PostAsJsonAsync("/api/files/hashes", new { items = new[] { new { sha256 = H(dav), size = (long)dav.Length } } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, after.GetProperty("found").GetArrayLength());

        // Ohne Anmeldung nicht
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Client(signedIn: false).PostAsJsonAsync("/api/files/hashes", new { items = Array.Empty<object>() })).StatusCode);
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
}
