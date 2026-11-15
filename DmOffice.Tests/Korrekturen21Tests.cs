using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Korrekturen für 2.1: Textdateien im Tresor, Teil-Uploads über Nextcloud, Bilder in Notizen, Personen ohne Freischaltung.</summary>
[Collection("server")]
public class Korrekturen21Tests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public Korrekturen21Tests(ChatFixture fx)
    {
        _fx = fx;
        Tresor.ArgonMemory = 1024; Tresor.ScryptCost = 1024;
        Tresor.ArgonIterations = 1;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> Dav(HttpClient c, string method, string path, byte[]? body = null, params (string, string)[] headers)
    {
        var m = new HttpRequestMessage(new HttpMethod(method), path);
        if (body != null) m.Content = new ByteArrayContent(body);
        foreach (var (k, v) in headers) m.Headers.Add(k, v);
        return await c.SendAsync(m);
    }

    [Fact]
    public async Task Textdatei_im_Tresor_anlegen_und_bearbeiten()
    {
        var c = _fx.As("dora");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1", name = "Test" }));
        var created = await Json(await c.PostAsJsonAsync("/api/files/save", new { path = "tresor:/Notiz.txt", text = "", create = true }));
        var etag = created.GetProperty("etag").GetString();
        // Noch einmal anlegen: gibt es schon
        var again = await c.PostAsJsonAsync("/api/files/save", new { path = "tresor:/Notiz.txt", text = "", create = true });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var saved = await Json(await c.PostAsJsonAsync("/api/files/save", new { path = "tresor:/Notiz.txt", text = "Hallo Tresor", etag }));
        var next = saved.GetProperty("etag").GetString();
        Assert.NotEqual(etag, next);
        Assert.Equal("Hallo Tresor", await c.GetStringAsync("/api/files/download?path=" + Uri.EscapeDataString("tresor:/Notiz.txt")));

        // Mit dem alten Stand: Konflikt, und der Text bleibt
        var stale = await c.PostAsJsonAsync("/api/files/save", new { path = "tresor:/Notiz.txt", text = "alt", etag });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True(JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement.GetProperty("conflict").GetBoolean());
        // Trotzdem speichern (ohne Etag)
        await Json(await c.PostAsJsonAsync("/api/files/save", new { path = "tresor:/Notiz.txt", text = "neu" }));
        Assert.Equal("neu", await c.GetStringAsync("/api/files/download?path=" + Uri.EscapeDataString("tresor:/Notiz.txt")));
        // Nichts davon in der Dateiablage
        Assert.Null(DmOffice.Dav.FileStore.Get(_fx.Space("dora").Db, "Notiz.txt"));
    }

    [Fact]
    public async Task Teil_Upload_ueber_Nextcloud_folgt_den_ausgeblendeten_Ordnern()
    {
        var c = _fx.As("cleo");
        await Json(await c.PostAsJsonAsync("/api/files/mkdir", new { path = "Privat" }));
        await Json(await c.PostAsJsonAsync("/api/files/dav-hidden", new { path = "Privat", hidden = true }));
        var up = "/remote.php/dav/uploads/cleo/ziel-test";
        await Dav(c, "MKCOL", up);
        await Dav(c, "PUT", up + "/1", Encoding.UTF8.GetBytes("Inhalt"));
        // Ziel über die Nextcloud-Adresse und über /dav: beide Male nicht in den ausgeblendeten Ordner
        var viaNextcloud = await Dav(c, "MOVE", up + "/.file", null, ("Destination", "http://localhost/remote.php/dav/files/cleo/Privat/a.txt"));
        Assert.False(viaNextcloud.IsSuccessStatusCode);
        var viaDav = await Dav(c, "MOVE", up + "/.file", null, ("Destination", "http://localhost/dav/files/cleo/Privat/a.txt"));
        Assert.Equal(HttpStatusCode.Forbidden, viaDav.StatusCode);
        Assert.Null(DmOffice.Dav.FileStore.Get(_fx.Space("cleo").Db, "Privat/a.txt"));
        // Ein sichtbarer Ordner geht wie bisher
        var ok = await Dav(c, "MOVE", up + "/.file", null, ("Destination", "http://localhost/remote.php/dav/files/cleo/a.txt"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Bild_in_Notiz_hat_eine_Obergrenze()
    {
        var c = _fx.As("ben");
        var big = new ByteArrayContent(new byte[DmOffice.Notes.NotesApi.MaxNoteFile + 1]);
        var r = await c.PostAsync("/api/notes/images", new MultipartFormDataContent { { big, "file", "gross.png" } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        // Mit Grenze des Kontos: auch kleinere nur, solange Platz ist
        var db = _fx.Space("ben").Db;
        UserStorage.Refresh(db);
        var quota = UserStorage.Counted(db) + 100;
        DmOffice.Dav.FileStore.QuotaFor[db.UserId] = () => quota;
        try
        {
            var full = await c.PostAsync("/api/notes/images", new MultipartFormDataContent { { new ByteArrayContent(new byte[1000]), "file", "b.png" } });
            Assert.Equal(507, (int)full.StatusCode);
        }
        finally { DmOffice.Dav.FileStore.QuotaFor.TryRemove(db.UserId, out _); }
    }
}
