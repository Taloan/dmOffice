using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DmOffice.Storage;

/// <summary>
/// OneDrive einer Person über Microsoft Graph (OneDrive kennt kein WebDAV) - Ziel der Datensicherung und externer Ordner.
/// Adressiert wird über den Pfad unter der Wurzel: das ganze Laufwerk (/me/drive/root:/a/b:) oder ein gewählter Ordner
/// (/drives/{laufwerk}/items/{ordner}:/a/b:). Kleine Dateien gehen in einer Anfrage hoch, große gestreamt in Stücken über eine Upload-Sitzung -
/// nie ganz im Speicher. Bremst Microsoft (429, 503), wartet der Treiber, so lange es die Antwort verlangt, und versucht es
/// erneut. Das Access-Token liefert der Aufrufer (Anmeldung per Gerätecode, <see cref="DmOffice.Mail.MicrosoftOAuth"/>).
/// </summary>
public sealed class OneDriveDriver : IStorageDriver
{
    public const string Graph = "https://graph.microsoft.com/v1.0";
    /// <summary>Bis zu dieser Größe ein einfaches PUT; darüber eine Upload-Sitzung (Microsoft erlaubt einfach bis 4 MB).</summary>
    public const long SimpleLimit = 4 * 1024 * 1024;
    /// <summary>Stückgröße der Upload-Sitzung: ein Vielfaches von 320 KiB, wie Microsoft es verlangt.</summary>
    public const int Chunk = 16 * 320 * 1024;
    private const int MaxRetries = 6;

    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly HttpClient _http;
    private readonly string _base;
    /// <summary>Wurzel des Treibers; null: /me/drive/root (das eigene Laufwerk ganz).</summary>
    private readonly string? _drive, _item;
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

    /// <param name="baseUrl">Nur für Tests (nachgebauter Graph-Server); sonst Microsoft Graph.</param>
    public OneDriveDriver(Func<CancellationToken, Task<string>> token, HttpClient http, string baseUrl = Graph)
    {
        _token = token;
        _http = http;
        _base = baseUrl.TrimEnd('/');
    }

    /// <summary>Ein Ordner als Wurzel (externer Ordner).</summary>
    /// <param name="drive">Kennung des Laufwerks (drive.id)</param>
    /// <param name="item">Kennung des Ordners (driveItem.id); "root" für das ganze Laufwerk</param>
    public OneDriveDriver(Func<CancellationToken, Task<string>> token, HttpClient http, string drive, string item, string baseUrl = Graph)
        : this(token, http, baseUrl)
    {
        _drive = drive;
        _item = item;
    }

    private string Root => _drive == null ? _base + "/me/drive/root"
        : _base + "/drives/" + Uri.EscapeDataString(_drive) + (_item is null or "root" ? "/root" : "/items/" + Uri.EscapeDataString(_item));

    /// <summary>Wartezeit vor einem neuen Versuch (Tests verkürzen sie).</summary>
    public static Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = Task.Delay;

    private static string Enc(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    /// <summary>Adresse eines Eintrags; suffix etwa „/children“ oder „/content“.</summary>
    private string Item(string path, string suffix = "")
    {
        var rel = StoragePath.Clean(path);
        return rel.Length == 0 ? Root + suffix : Root + ":/" + Enc(rel) + (suffix.Length > 0 ? ":" + suffix : "");
    }

    /// <summary>Senden mit Anmeldung; bei 429/503 warten (Retry-After) und erneut, bei Netzfehlern ebenso.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool auth = true, HttpCompletionOption read = HttpCompletionOption.ResponseContentRead)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = make();
            if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
            HttpResponseMessage res;
            try { res = await _http.SendAsync(req, read, ct); }
            catch (HttpRequestException) when (attempt < MaxRetries) { await Wait(Backoff(attempt, null), ct); continue; }
            if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout && attempt < MaxRetries)
            {
                var after = res.Headers.RetryAfter?.Delta ?? (res.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
                res.Dispose();
                await Wait(Backoff(attempt, after), ct);
                continue;
            }
            return res;
        }
    }

    private static TimeSpan Backoff(int attempt, TimeSpan? after) =>
        after is { } a && a > TimeSpan.Zero ? (a > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : a) : TimeSpan.FromSeconds(Math.Min(60, 2 << attempt));

    private static async Task CheckAsync(HttpResponseMessage res, string what)
    {
        if (res.IsSuccessStatusCode) return;
        var message = "";
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) message = m.GetString() ?? "";
        }
        catch (JsonException) { }
        var tail = message.Length > 0 ? " (" + message + ")" : "";
        throw res.StatusCode switch
        {
            HttpStatusCode.NotFound => new KeyNotFoundException(what + ": nicht gefunden."),
            HttpStatusCode.Unauthorized => new UnauthorizedAccessException(what + ": Anmeldung bei OneDrive abgelehnt."),
            HttpStatusCode.Forbidden => new UnauthorizedAccessException(what + ": keine Berechtigung" + tail + "."),
            HttpStatusCode.InsufficientStorage => new IOException(what + ": OneDrive ist voll."),
            HttpStatusCode.Conflict => new IOException(what + ": gibt es schon" + tail + "."),
            _ => new IOException($"{what}: OneDrive antwortet mit {(int)res.StatusCode}{tail}."),
        };
    }

    private static DriverEntry Entry(JsonElement e)
    {
        var dir = e.TryGetProperty("folder", out _);
        var size = !dir && e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
        var modified = e.TryGetProperty("lastModifiedDateTime", out var m) && m.ValueKind == JsonValueKind.String
            && DateTime.TryParse(m.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue;
        var mime = e.TryGetProperty("file", out var f) && f.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
        return new DriverEntry(e.GetProperty("name").GetString() ?? "", dir, size, modified, mime);
    }

    public async Task<List<DriverEntry>> ListAsync(string path, CancellationToken ct)
    {
        var list = new List<DriverEntry>();
        string? next = Item(path, "/children") + "?$select=name,size,folder,file,lastModifiedDateTime&$top=999";
        while (next != null)
        {
            var url = next;
            // Folgeseiten kommen von Microsoft - nur Graph selbst (das Token geht mit)
            if (!url.StartsWith(_base + "/", StringComparison.Ordinal)) throw new IOException("Unerwartete Adresse von Microsoft.");
            using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            await CheckAsync(res, "Auflisten");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray()) list.Add(Entry(e));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
        }
        return list;
    }

    public async Task<DriverEntry?> StatAsync(string path, CancellationToken ct)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Item(path) + "?$select=name,size,folder,file,lastModifiedDateTime"), ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await CheckAsync(res, "Abfragen");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return Entry(doc.RootElement);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Item(path, "/content")), ct, read: HttpCompletionOption.ResponseHeadersRead);
        try { await CheckAsync(res, "Lesen"); return await res.Content.ReadAsStreamAsync(ct); }
        catch { res.Dispose(); throw; }
    }

    public async Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct)
    {
        using var res = await SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, Item(path, "/content"));
            r.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
            return r;
        }, ct);
        await CheckAsync(res, "Lesen");
        var all = await res.Content.ReadAsByteArrayAsync(ct);
        // Kann der Server keine Ausschnitte (200 statt 206), selbst schneiden
        return res.StatusCode == HttpStatusCode.PartialContent ? all : all.AsSpan((int)Math.Min(offset, all.Length), (int)Math.Min(count, Math.Max(0, all.Length - offset))).ToArray();
    }

    public async Task WriteAsync(string path, Stream data, CancellationToken ct)
    {
        var rel = StoragePath.Clean(path);
        if (rel.Length == 0) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht überschreiben.");
        // Länge unbekannt (etwa ein hochgeladenes Formular): erst auf die Platte, nicht in den Speicher - die Upload-Sitzung
        // braucht die Gesamtgröße in jedem Stück
        if (!data.CanSeek)
        {
            await using var spool = new FileStream(Path.Combine(Path.GetTempPath(), "dmoffice-onedrive-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await data.CopyToAsync(spool, ct);
            spool.Position = 0;
            await WriteAsync(rel, spool, ct);
            return;
        }
        if (StoragePath.Parent(rel) is { Length: > 0 } parent) await MkdirAsync(parent, ct);
        long? length = data.CanSeek ? data.Length - data.Position : null;
        if (length is { } small && small <= SimpleLimit)
        {
            var start = data.Position;
            using var res = await SendAsync(() =>
            {
                data.Position = start;
                var r = new HttpRequestMessage(HttpMethod.Put, Item(rel, "/content")) { Content = new StreamContent(new NoDispose(data), 81920) };
                r.Content.Headers.ContentLength = small;
                r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return r;
            }, ct);
            await CheckAsync(res, "Schreiben");
            return;
        }
        await UploadSessionAsync(rel, data, length!.Value, ct);
    }

    public async Task SetModifiedAsync(string path, DateTime utc, CancellationToken ct)
    {
        var rel = StoragePath.Clean(path);
        var when = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, Item(rel))
        {
            Content = new StringContent("{\"fileSystemInfo\":{\"lastModifiedDateTime\":\"" + when + "\"}}", Encoding.UTF8, "application/json"),
        }, ct);
    }

    /// <summary>Große Datei: Upload-Sitzung, Stücke zu <see cref="Chunk"/> Byte nacheinander (die Adresse trägt ihre eigene Berechtigung).</summary>
    private async Task UploadSessionAsync(string rel, Stream data, long length, CancellationToken ct)
    {
        string uploadUrl;
        using (var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Item(rel, "/createUploadSession"))
               {
                   Content = new StringContent("{\"item\":{\"@microsoft.graph.conflictBehavior\":\"replace\"}}", Encoding.UTF8, "application/json"),
               }, ct))
        {
            await CheckAsync(res, "Hochladen beginnen");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            uploadUrl = doc.RootElement.GetProperty("uploadUrl").GetString()!;
        }
        var buffer = new byte[Chunk];
        long sent = 0;
        try
        {
            while (sent < length)
            {
                var n = 0;
                while (n < buffer.Length && sent + n < length)
                {
                    var r = await data.ReadAsync(buffer.AsMemory(n, (int)Math.Min(buffer.Length - n, length - sent - n)), ct);
                    if (r == 0) throw new IOException("Die Datei wurde beim Lesen kürzer.");
                    n += r;
                }
                var from = sent;
                var size = n;
                using var res = await SendAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buffer, 0, size) };
                    req.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, from + size - 1, length);
                    return req;
                }, ct, auth: false);
                await CheckAsync(res, "Hochladen");
                sent += n;
            }
        }
        catch
        {
            // Sitzung aufgeben, damit keine halben Dateien liegen bleiben
            try { using var _ = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, uploadUrl), CancellationToken.None); } catch (HttpRequestException) { }
            throw;
        }
    }

    public async Task MkdirAsync(string path, CancellationToken ct)
    {
        var at = "";
        foreach (var part in StoragePath.Clean(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var parent = at;
            at = StoragePath.Join(at, part);
            if (_dirs.Contains(at)) continue;
            using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Item(parent, "/children"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["name"] = part, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail",
                }), Encoding.UTF8, "application/json"),
            }, ct);
            // 409: gibt es schon
            if (res.StatusCode != HttpStatusCode.Conflict) await CheckAsync(res, "Ordner anlegen");
            _dirs.Add(at);
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var rel = StoragePath.Clean(path);
        if (rel.Length == 0) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht löschen.");
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Item(rel)), ct);
        if (res.StatusCode != HttpStatusCode.NotFound) await CheckAsync(res, "Löschen");
        _dirs.RemoveWhere(d => d == rel || d.StartsWith(rel + "/", StringComparison.Ordinal));
    }

    /// <summary>Verschieben ersetzt nichts: liegt am Ziel schon etwas, kommt eine IOException (wie bei den anderen Treibern).</summary>
    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        var a = StoragePath.Clean(from);
        var b = StoragePath.Clean(to);
        if (a.Length == 0) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht verschieben.");
        var parent = StoragePath.Parent(b);
        if (parent.Length > 0) await MkdirAsync(parent, ct);
        if (b.Length == 0) throw new ArgumentException("Ungültiges Ziel.");
        var leaf = b[(b.LastIndexOf('/') + 1)..];
        // Eigenes Laufwerk: Ziel über den Pfad; gewählter Ordner: über die Kennung des Zielordners
        var target = _drive == null
            ? new Dictionary<string, string> { ["path"] = "/drive/root:" + (parent.Length > 0 ? "/" + parent : "") }
            : new Dictionary<string, string> { ["id"] = await IdAsync(parent, ct) };
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["parentReference"] = target,
            ["name"] = leaf,
            ["@microsoft.graph.conflictBehavior"] = "fail",
        });
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, Item(a)) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, ct);
        await CheckAsync(res, "Verschieben");
    }

    private async Task<string> IdAsync(string path, CancellationToken ct)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Item(path) + "?$select=id"), ct);
        await CheckAsync(res, "Zielordner");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetString() ?? "";
    }

    public async Task<DriverSpace?> SpaceAsync(CancellationToken ct)
    {
        var drive = _drive == null ? _base + "/me/drive" : _base + "/drives/" + Uri.EscapeDataString(_drive);
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, drive + "?$select=quota"), ct);
        await CheckAsync(res, "Platz abfragen");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("quota", out var q)) return null;
        long? N(string name) => q.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        return N("remaining") is { } free ? new DriverSpace(N("used"), free) : null;
    }

    /// <summary>Konto, mit dem angemeldet ist (Anzeige in den Einstellungen).</summary>
    public async Task<string?> AccountAsync(CancellationToken ct)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _base + "/me?$select=userPrincipalName,mail,displayName"), ct);
        await CheckAsync(res, "Konto abfragen");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        string? S(string n) => doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return S("mail") ?? S("userPrincipalName") ?? S("displayName");
    }

    // ---------------- Ordnerauswahl beim Einbinden als externer Ordner ----------------

    /// <summary>Ordner direkt unter der Wurzel, mit Kennung, nach Namen sortiert.</summary>
    public async Task<List<(string Id, string Name)>> FoldersAsync(CancellationToken ct)
    {
        var list = new List<(string Id, string Name)>();
        string? next = Item("", "/children") + "?$select=id,name,folder&$top=999";
        while (next != null)
        {
            var url = next;
            if (!url.StartsWith(_base + "/", StringComparison.Ordinal)) throw new IOException("Unerwartete Adresse von Microsoft.");
            using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            await CheckAsync(res, "Auflisten");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray().Where(e => e.TryGetProperty("folder", out _)))
                list.Add((e.GetProperty("id").GetString() ?? "", e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }
        return list.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Das eigene Laufwerk des angemeldeten Kontos: Kennung, Art (personal, business) und Inhaber.</summary>
    public async Task<(string Id, string Type, string? Owner)> MyDriveAsync(CancellationToken ct)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _base + "/me/drive?$select=id,driveType,owner"), ct);
        await CheckAsync(res, "OneDrive");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var json = doc.RootElement;
        if (!json.TryGetProperty("id", out var id)) throw new IOException("Unerwartete Antwort von Microsoft.");
        string? owner = json.TryGetProperty("owner", out var o) && o.TryGetProperty("user", out var u) && u.TryGetProperty("displayName", out var n) ? n.GetString() : null;
        return (id.GetString() ?? "", json.TryGetProperty("driveType", out var t) ? t.GetString() ?? "" : "", owner);
    }

    public void Dispose() { }

    /// <summary>Der Inhalt eines Wiederholungsversuchs darf den Stream nicht schließen.</summary>
    private sealed class NoDispose(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
