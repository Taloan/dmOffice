using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace DmOffice.Tests;

/// <summary>Hochladen in Stücken: zusammensetzen, fortsetzen an der richtigen Stelle, abbrechen, als Chat-Anhang.</summary>
[Collection("server")]
public class ChunkedUploadTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ChunkedUploadTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<string> Start(string login, string name, long size, string mime = "application/octet-stream") =>
        (await Json(await _fx.As(login).PostAsJsonAsync("/api/files/uploads", new { name, size, mime }))).GetProperty("id").GetString()!;

    private Task<HttpResponseMessage> Put(string login, string id, long offset, byte[] data, int from, int length) =>
        _fx.As(login).PutAsync($"/api/files/uploads/{id}?offset={offset}", new ByteArrayContent(data, from, length));

    [Fact]
    public async Task Grosse_Datei_in_Stuecken_mit_Fortsetzen()
    {
        var data = RandomNumberGenerator.GetBytes(20 * 1024 * 1024 + 12345);
        var id = await Start("anna", "Video.mp4", data.Length, "video/mp4");
        const int chunk = 8 * 1024 * 1024;

        await Json(await Put("anna", id, 0, data, 0, chunk));
        // Abbruch und Neustart des Geräts: falsche Stelle wird abgelehnt und nennt den Stand
        var wrong = await Put("anna", id, 0, data, 0, chunk);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(chunk, JsonDocument.Parse(await wrong.Content.ReadAsStringAsync()).RootElement.GetProperty("received").GetInt64());
        Assert.Equal(chunk, (await Json(await _fx.As("anna").GetAsync($"/api/files/uploads/{id}"))).GetProperty("received").GetInt64());
        // Noch nicht fertig: abschließen geht nicht
        Assert.False((await _fx.As("anna").PostAsync($"/api/files/uploads/{id}/finish?path=Filme", null)).IsSuccessStatusCode);

        await Json(await Put("anna", id, chunk, data, chunk, chunk));
        await Json(await Put("anna", id, 2 * chunk, data, 2 * chunk, data.Length - 2 * chunk));
        var done = await Json(await _fx.As("anna").PostAsync($"/api/files/uploads/{id}/finish?path=Filme&conflict=rename", null));
        var file = done.GetProperty("files")[0];
        Assert.Equal("Filme/Video.mp4", file.GetProperty("path").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), file.GetProperty("sha256").GetString()!.ToLowerInvariant());
        var back = await _fx.As("anna").GetByteArrayAsync("/api/files/download?path=" + Uri.EscapeDataString("Filme/Video.mp4"));
        Assert.Equal(data, back);
        // Die Sitzung ist weg
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _fx.As("anna").GetAsync($"/api/files/uploads/{id}")).StatusCode);
    }

    [Fact]
    public async Task Abbrechen_und_zu_grosses_Teil()
    {
        var data = RandomNumberGenerator.GetBytes(1000);
        var id = await Start("ben", "a.bin", 500);
        // Mehr als angekündigt: abgelehnt
        Assert.False((await Put("ben", id, 0, data, 0, 1000)).IsSuccessStatusCode);
        await _fx.As("ben").DeleteAsync($"/api/files/uploads/{id}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync($"/api/files/uploads/{id}")).StatusCode);
        // Fremde Sitzungen gibt es nicht
        var mine = await Start("ben", "b.bin", 10);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _fx.As("cleo").GetAsync($"/api/files/uploads/{mine}")).StatusCode);
    }

    [Fact]
    public async Task Ueber_Upload_Link_in_Stuecken()
    {
        var owner = _fx.As("dora");
        await owner.PostAsJsonAsync("/api/files/mkdir", new { path = "Eingang" });
        var token = (await Json(await owner.PostAsJsonAsync("/api/files/links", new { path = "Eingang", mode = "upload" }))).GetProperty("token").GetString();
        var anon = _fx.Factory.CreateClient();
        var data = RandomNumberGenerator.GetBytes(9 * 1024 * 1024 + 7);
        var id = (await Json(await anon.PostAsJsonAsync($"/api/public/{token}/uploads", new { name = "Ordner/Film.mp4", size = data.Length }))).GetProperty("id").GetString();
        const int chunk = 8 * 1024 * 1024;
        await Json(await anon.PutAsync($"/api/public/{token}/uploads/{id}?offset=0", new ByteArrayContent(data, 0, chunk)));
        // Angemeldet (ohne Link) gibt es diese Sitzung nicht - und umgekehrt
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await owner.GetAsync($"/api/files/uploads/{id}")).StatusCode);
        Assert.Equal(chunk, (await Json(await anon.GetAsync($"/api/public/{token}/uploads/{id}"))).GetProperty("received").GetInt64());
        await Json(await anon.PutAsync($"/api/public/{token}/uploads/{id}?offset={chunk}", new ByteArrayContent(data, chunk, data.Length - chunk)));
        var done = await Json(await anon.PostAsync($"/api/public/{token}/uploads/{id}/finish", null));
        Assert.Equal("Film.mp4", done.GetProperty("files")[0].GetString());
        Assert.Equal(data, await owner.GetByteArrayAsync("/api/files/download?path=" + Uri.EscapeDataString("Eingang/Ordner/Film.mp4")));
        // Nur-lesen-Links nehmen nichts an
        var read = (await Json(await owner.PostAsJsonAsync("/api/files/links", new { path = "Eingang", mode = "read" }))).GetProperty("token").GetString();
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await anon.PostAsJsonAsync($"/api/public/{read}/uploads", new { name = "x.bin", size = 3 })).StatusCode);
    }

    private async Task<HttpResponseMessage> Dav(HttpClient c, string method, string url, byte[]? body = null, params (string, string)[] headers)
    {
        var m = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) m.Content = new ByteArrayContent(body);
        foreach (var (k, v) in headers) m.Headers.Add(k, v);
        return await c.SendAsync(m);
    }

    [Fact]
    public async Task Nextcloud_Sitzungen_zaehlen_gemeinsam_gegen_das_Kontingent()
    {
        var c = _fx.As("cleo");
        var db = _fx.Space("cleo").Db;
        var bytes = RandomNumberGenerator.GetBytes(600);
        DmOffice.Core.UserStorage.Refresh(db);
        var quota = DmOffice.Core.UserStorage.Counted(db) + 1000;
        DmOffice.Dav.FileStore.QuotaFor[db.UserId] = () => quota;
        try
        {
            var a = "/remote.php/dav/uploads/cleo/sitzung-a";
            var b = "/remote.php/dav/uploads/cleo/sitzung-b";
            await Dav(c, "MKCOL", a);
            await Dav(c, "MKCOL", b);
            Assert.Equal(System.Net.HttpStatusCode.Created, (await Dav(c, "PUT", a + "/1", bytes)).StatusCode);
            Assert.Equal(507, (int)(await Dav(c, "PUT", b + "/1", bytes)).StatusCode);
            // Dasselbe Teil noch einmal ersetzt sich selbst
            Assert.Equal(System.Net.HttpStatusCode.Created, (await Dav(c, "PUT", a + "/1", bytes)).StatusCode);
            await Dav(c, "DELETE", a);
            Assert.Equal(System.Net.HttpStatusCode.Created, (await Dav(c, "PUT", b + "/1", bytes)).StatusCode);
            await Dav(c, "DELETE", b);
        }
        finally { DmOffice.Dav.FileStore.QuotaFor.TryRemove(db.UserId, out _); }
    }

    [Fact]
    public async Task Nextcloud_Luecke_in_den_Teilen_faellt_ohne_Gesamtlaenge_auf()
    {
        var c = _fx.As("ben");
        var dav = "/remote.php/dav/uploads/ben/mit-luecke";
        await Dav(c, "MKCOL", dav);
        await Dav(c, "PUT", dav + "/1", new byte[10]);
        await Dav(c, "PUT", dav + "/3", new byte[10]);
        var dest = ("Destination", "http://localhost/remote.php/dav/files/ben/Luecke.bin");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Dav(c, "MOVE", dav + "/.file", null, dest)).StatusCode);
        // Teil nachgereicht: geht. Nummern als Stelle in der Datei sind ebenfalls lückenlos
        await Dav(c, "PUT", dav + "/2", new byte[10]);
        Assert.Equal(System.Net.HttpStatusCode.Created, (await Dav(c, "MOVE", dav + "/.file", null, dest)).StatusCode);
        var offsets = "/remote.php/dav/uploads/ben/nach-stelle";
        await Dav(c, "MKCOL", offsets);
        await Dav(c, "PUT", offsets + "/000000000000000", new byte[10]);
        await Dav(c, "PUT", offsets + "/000000000000010", new byte[10]);
        Assert.Equal(System.Net.HttpStatusCode.Created, (await Dav(c, "MOVE", offsets + "/.file", null, ("Destination", "http://localhost/remote.php/dav/files/ben/Stelle.bin"))).StatusCode);
    }

    [Fact]
    public async Task Verschieben_und_Kopieren_beachten_If_Match()
    {
        var c = _fx.As("ben");
        var file = "/remote.php/dav/files/ben/Etag-Probe.txt";
        var put = await Dav(c, "PUT", file, new byte[3]);
        var etag = put.Headers.ETag!.Tag;
        await Dav(c, "PUT", file, new byte[4]);
        var to = ("Destination", "http://localhost/remote.php/dav/files/ben/Etag-Ziel.txt");
        Assert.Equal(412, (int)(await Dav(c, "MOVE", file, null, to, ("If-Match", etag))).StatusCode);
        Assert.Equal(412, (int)(await Dav(c, "COPY", file, null, to, ("If-Match", etag))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await c.GetAsync("/remote.php/dav/files/ben/Etag-Ziel.txt")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Created, (await Dav(c, "MOVE", file, null, to, ("If-Match", "*"))).StatusCode);
    }

    [Fact]
    public async Task Nextcloud_in_Stuecken()
    {
        var c = _fx.As("ben");
        var dav = $"/remote.php/dav/uploads/ben/web-file-upload-123";
        var data = RandomNumberGenerator.GetBytes(3 * 1000 + 5);
        Assert.Equal(System.Net.HttpStatusCode.Created, (await c.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), dav))).StatusCode);
        // Teile in beliebiger Reihenfolge, Nummern werden nach Zahl sortiert (2 vor 10)
        await c.PutAsync($"{dav}/10", new ByteArrayContent(data, 2000, data.Length - 2000));
        await c.PutAsync($"{dav}/1", new ByteArrayContent(data, 0, 1000));
        await c.PutAsync($"{dav}/2", new ByteArrayContent(data, 1000, 1000));
        var list = await c.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), dav) { Headers = { { "Depth", "1" } } });
        Assert.Equal(207, (int)list.StatusCode);
        Assert.Contains("<d:getcontentlength>1005</d:getcontentlength>", await list.Content.ReadAsStringAsync());
        // Fehlt etwas, wird nichts abgelegt
        var wrong = new HttpRequestMessage(new HttpMethod("MOVE"), $"{dav}/.file");
        wrong.Headers.Add("Destination", "http://localhost/remote.php/dav/files/ben/Video.bin");
        wrong.Headers.Add("OC-Total-Length", "99999");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.SendAsync(wrong)).StatusCode);
        var move = new HttpRequestMessage(new HttpMethod("MOVE"), $"{dav}/.file");
        move.Headers.Add("Destination", "http://localhost/remote.php/dav/files/ben/Video.bin");
        move.Headers.Add("OC-Total-Length", data.Length.ToString());
        move.Headers.Add("X-OC-Mtime", "1700000000");
        var moved = await c.SendAsync(move);
        Assert.Equal(System.Net.HttpStatusCode.Created, moved.StatusCode);
        Assert.True(moved.Headers.Contains("OC-FileId"));
        Assert.Equal(data, await c.GetByteArrayAsync("/remote.php/dav/files/ben/Video.bin"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await c.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), dav))).StatusCode);
        // Fremde Bereiche gibt es nicht
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await c.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), "/remote.php/dav/uploads/anna/x"))).StatusCode);
        // Die Clients erfahren davon ueber die Faehigkeiten
        Assert.Contains("\"chunking\":\"1.0\"", await c.GetStringAsync("/ocs/v2.php/cloud/capabilities?format=json"));
    }

    [Fact]
    public async Task Als_Chat_Anhang_verschicken()
    {
        var room = (await Json(await _fx.As("cleo").PostAsJsonAsync("/api/chat/rooms", new { members = new[] { _fx.Id("dora") } }))).GetProperty("id").GetInt64();
        var data = RandomNumberGenerator.GetBytes(3000);
        var id = await Start("cleo", "Sprachnachricht.webm", data.Length, "audio/webm");
        await Json(await Put("cleo", id, 0, data, 0, data.Length));
        var msg = await Json(await _fx.As("cleo").PostAsync($"/api/chat/uploads/{id}/send?room={room}&text=" + Uri.EscapeDataString("Hör mal"), null));
        Assert.Equal("Hör mal", msg.GetProperty("text").GetString());
        var r = msg.GetProperty("refs")[0];
        Assert.Equal("Sprachnachricht.webm", r.GetProperty("name").GetString());
        Assert.Equal("audio/webm", r.GetProperty("mime").GetString());
        var got = await _fx.As("dora").GetByteArrayAsync($"/api/chat/messages/{msg.GetProperty("id").GetInt64()}/refs/0/file");
        Assert.Equal(data, got);
    }
}
