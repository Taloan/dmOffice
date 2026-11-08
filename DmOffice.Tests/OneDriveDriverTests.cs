using System.Net;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;
using DmOffice.Storage;

namespace DmOffice.Tests;

/// <summary>OneDrive als Ziel der Datensicherung - gegen einen nachgebauten Graph-Server (kein echtes Konto).</summary>
public class OneDriveDriverTests
{
    /// <summary>OneDrive im Speicher: Pfad → Inhalt (null = Ordner). Kennt die Aufrufe, die der Treiber macht.</summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]?> Items = new(StringComparer.Ordinal) { [""] = null };
        public int Throttle;
        public int Requests, SessionChunks;
        public readonly List<string> Tokens = [];
        private readonly Dictionary<string, (string Path, long Length, MemoryStream Data)> _sessions = [];

        private static HttpResponseMessage Json(HttpStatusCode code, object body) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        private object Meta(string path)
        {
            var name = path.Length == 0 ? "root" : path[(path.LastIndexOf('/') + 1)..];
            return Items[path] is { } data
                ? new Dictionary<string, object> { ["name"] = name, ["size"] = data.Length, ["file"] = new { mimeType = "application/octet-stream" }, ["lastModifiedDateTime"] = "2026-10-31T08:00:00Z" }
                : new Dictionary<string, object> { ["name"] = name, ["folder"] = new { childCount = 0 }, ["lastModifiedDateTime"] = "2026-10-31T08:00:00Z" };
        }

        private static string Parent(string p) => p.Contains('/') ? p[..p.LastIndexOf('/')] : "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests++;
            var url = req.RequestUri!;
            if (url.Host == "upload.test") return await UploadAsync(req);
            if (Throttle > 0) { Throttle--; var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1)); return r; }
            Tokens.Add(req.Headers.Authorization?.Parameter ?? "");
            var raw = url.AbsolutePath["/v1.0".Length..];
            if (raw == "/me") return Json(HttpStatusCode.OK, new { mail = "erika@example.org" });
            if (raw == "/me/drive") return Json(HttpStatusCode.OK, new { quota = new { remaining = 5_000_000_000L, used = 1L } });
            string path, op;
            if (raw.StartsWith("/me/drive/root:/", StringComparison.Ordinal))
            {
                var rest = raw["/me/drive/root:/".Length..];
                var colon = rest.IndexOf(':');
                path = Uri.UnescapeDataString(colon < 0 ? rest : rest[..colon]);
                op = colon < 0 ? "" : rest[(colon + 1)..];
            }
            else { path = ""; op = raw["/me/drive/root".Length..]; }

            switch (req.Method.Method, op)
            {
                case ("GET", "/children"):
                    if (!Items.TryGetValue(path, out var dir) || dir != null) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    return Json(HttpStatusCode.OK, new { value = Items.Keys.Where(k => k.Length > 0 && Parent(k) == path).Select(Meta).ToList() });
                case ("GET", ""):
                    return Items.ContainsKey(path) ? Json(HttpStatusCode.OK, Meta(path)) : new HttpResponseMessage(HttpStatusCode.NotFound);
                case ("GET", "/content"):
                    if (!Items.TryGetValue(path, out var file) || file == null) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (req.Headers.Range?.Ranges.FirstOrDefault() is { From: { } from, To: { } to })
                        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(file[(int)from..(int)Math.Min(to + 1, file.Length)]) };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) };
                case ("PUT", "/content"):
                    if (!Items.TryGetValue(Parent(path), out var p) || p != null) return new HttpResponseMessage(HttpStatusCode.Conflict);
                    Items[path] = await req.Content!.ReadAsByteArrayAsync(ct);
                    return Json(HttpStatusCode.Created, Meta(path));
                case ("POST", "/createUploadSession"):
                    var id = Guid.NewGuid().ToString("N");
                    _sessions[id] = (path, 0, new MemoryStream());
                    return Json(HttpStatusCode.OK, new { uploadUrl = "https://upload.test/s/" + id });
                case ("POST", "/children"):
                    using (var doc = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)))
                    {
                        var child = (path.Length == 0 ? "" : path + "/") + doc.RootElement.GetProperty("name").GetString();
                        if (Items.ContainsKey(child)) return Json(HttpStatusCode.Conflict, new { error = new { message = "nameAlreadyExists" } });
                        Items[child] = null;
                        return Json(HttpStatusCode.Created, Meta(child));
                    }
                case ("DELETE", ""):
                    if (!Items.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    foreach (var k in Items.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) Items.Remove(k);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case ("PATCH", ""):
                    using (var doc = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)))
                    {
                        if (!Items.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                        var parent = doc.RootElement.GetProperty("parentReference").GetProperty("path").GetString()!["/drive/root:".Length..].TrimStart('/');
                        var dest = (parent.Length == 0 ? "" : parent + "/") + doc.RootElement.GetProperty("name").GetString();
                        if (Items.ContainsKey(dest)) return Json(HttpStatusCode.Conflict, new { error = new { message = "nameAlreadyExists" } });
                        foreach (var k in Items.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                        {
                            Items[dest + k[path.Length..]] = Items[k];
                            Items.Remove(k);
                        }
                        return Json(HttpStatusCode.OK, Meta(dest));
                    }
            }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        private async Task<HttpResponseMessage> UploadAsync(HttpRequestMessage req)
        {
            var id = req.RequestUri!.AbsolutePath.Split('/').Last();
            if (req.Method == HttpMethod.Delete) { _sessions.Remove(id); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            if (req.Headers.Authorization != null) return new HttpResponseMessage(HttpStatusCode.BadRequest);   // Upload-Adresse ohne Anmeldung
            var (path, _, data) = _sessions[id];
            var range = req.Content!.Headers.ContentRange!;
            var bytes = await req.Content.ReadAsByteArrayAsync();
            if (range.From != data.Length || (range.To + 1 < range.Length && bytes.Length % (320 * 1024) != 0)) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            data.Write(bytes);
            SessionChunks++;
            if (data.Length < range.Length) return Json(HttpStatusCode.Accepted, new { nextExpectedRanges = new[] { data.Length + "-" } });
            Items[path] = data.ToArray();
            _sessions.Remove(id);
            return Json(HttpStatusCode.Created, Meta(path));
        }
    }

    public OneDriveDriverTests() => OneDriveDriver.Wait = (_, _) => Task.CompletedTask;

    private static OneDriveDriver Drive(FakeGraph g) => new(_ => Task.FromResult("tok"), new HttpClient(g), "https://graph.test/v1.0");

    [Fact]
    public async Task Datensicherung_nach_OneDrive_mit_trash_und_grosser_Datei()
    {
        var g = new FakeGraph();
        var dir = Directory.CreateTempSubdirectory("dmonedrive");
        try
        {
            var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "o", Path.Combine(dir.FullName, "blobs"));
            PimStore.EnsureDefaults(db);
            var space = new UserSpace(new UserRecord { Login = "o", DisplayName = "O" }, db);
            FileStore.EnsureDir(db, "Ordner mit Ümlaut");
            FileStore.Put(db, "Ordner mit Ümlaut/a.txt", Encoding.UTF8.GetBytes("eins"), "text/plain");
            var big = new byte[OneDriveDriver.SimpleLimit + OneDriveDriver.Chunk + 1234];
            Random.Shared.NextBytes(big);
            FileStore.Put(db, "gross.bin", big, "application/octet-stream");
            var s = new PlainBackupSettings { Enabled = true, Target = PlainBackup.OneDrive, OneDriveToken = "r", Folder = "Datensicherung", Areas = [PlainBackup.Files] };
            var now = new DateTime(2026, 10, 31, 2, 0, 0, DateTimeKind.Utc);
            async Task<PlainBackup.Result> Run()
            {
                using var d = new SubfolderDriver(Drive(g), s.Folder);
                return await PlainBackup.RunAsync(db, s, d, PlainBackupAreas.Items(space, s, null), Path.Combine(dir.FullName, "tmp"), () => "liesmich", null, CancellationToken.None, () => now);
            }
            g.Throttle = 2;   // Microsoft bremst: warten und erneut
            var first = await Run();
            Assert.Null(first.Error);
            Assert.Equal("eins", Encoding.UTF8.GetString(g.Items["Datensicherung/Dateien/Ordner mit Ümlaut/a.txt"]!));
            Assert.Equal(big, g.Items["Datensicherung/Dateien/gross.bin"]);
            Assert.True(g.SessionChunks >= 2);
            Assert.DoesNotContain(g.Items.Keys, k => k.EndsWith(".part", StringComparison.Ordinal));
            Assert.All(g.Tokens, t => Assert.Equal("tok", t));

            // Nichts geändert: keine Übertragung
            Assert.Equal(0, (await Run()).Written);
            // Geändert: ältere Version nach .trash
            FileStore.Put(db, "Ordner mit Ümlaut/a.txt", Encoding.UTF8.GetBytes("zwei"), "text/plain");
            Assert.Null((await Run()).Error);
            Assert.Equal("zwei", Encoding.UTF8.GetString(g.Items["Datensicherung/Dateien/Ordner mit Ümlaut/a.txt"]!));
            Assert.Equal("eins", Encoding.UTF8.GetString(g.Items["Datensicherung/.trash/Dateien/Ordner mit Ümlaut/a.txt"]!));
            // Umbenannt: im Ziel verschoben, nicht neu gesendet
            FileStore.MoveOrCopy(db, "gross.bin", "gross-neu.bin", false, false);
            var moved = await Run();
            Assert.Equal(1, moved.Moved);
            Assert.Equal(big, g.Items["Datensicherung/Dateien/gross-neu.bin"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Verschieben_ersetzt_nichts_und_Lesen_mit_Ausschnitt()
    {
        var g = new FakeGraph();
        using var d = Drive(g);
        await d.WriteAsync("x/a.txt", new MemoryStream(Encoding.UTF8.GetBytes("Hallo Welt")), CancellationToken.None);
        await d.WriteAsync("x/b.txt", new MemoryStream([1]), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => d.MoveAsync("x/a.txt", "x/b.txt", CancellationToken.None));
        Assert.Equal("Welt", Encoding.UTF8.GetString(await d.ReadRangeAsync("x/a.txt", 6, 4, CancellationToken.None)));
        Assert.Null(await d.StatAsync("x/fehlt.txt", CancellationToken.None));
        Assert.Contains(await d.ListAsync("x", CancellationToken.None), e => e.Name == "a.txt" && e.Size == 10);
        await d.DeleteAsync("x/fehlt.txt", CancellationToken.None);   // schon weg: kein Fehler
        Assert.Equal(5_000_000_000L, (await d.SpaceAsync(CancellationToken.None))!.Free);
        Assert.Equal("erika@example.org", await d.AccountAsync(CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.DeleteAsync("", CancellationToken.None));
    }
}
