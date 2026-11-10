using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DmOffice.Storage;

/// <summary>
/// Ein Ordner in pCloud über die HTTP-JSON-API (https://docs.pcloud.com). Angemeldet mit einem OAuth-Token (Bearer), das
/// nicht abläuft; jede Anfrage geht an den Server der Region des Kontos (api.pcloud.com für die USA, eapi.pcloud.com für
/// Europa). Wurzel ist ein Ordnerpfad im Konto, darunter Pfade wie gewohnt. Lesen über getfilelink (auch Ausschnitte),
/// Schreiben bis <see cref="SimpleMax"/> mit uploadfile in einem Stück, darüber in Teilen von <see cref="ChunkSize"/>
/// (upload_create, upload_write, upload_save) - nie die ganze Datei im Speicher. pCloud meldet Fehler meist mit HTTP 200
/// und einem Code in „result“; 4xxx (Drosselung) und 5xxx (Serverfehler) werden mit Pause wiederholt.
/// </summary>
public sealed class PCloudDriver : IStorageDriver
{
    public const string HostUs = "api.pcloud.com", HostEu = "eapi.pcloud.com";
    /// <summary>Bis hierher in einem Stück (uploadfile), darüber in Teilen.</summary>
    public const int SimpleMax = 16 * 1024 * 1024;
    public const int ChunkSize = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _token, _host, _root;

    /// <summary>Warten vor einem neuen Versuch - in Tests ersetzbar.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = Task.Delay;
    /// <summary>Teilgröße und Grenze für ein Stück - in Tests kleiner.</summary>
    public int Chunk { get; set; } = ChunkSize;
    public long Simple { get; set; } = SimpleMax;

    /// <param name="host">api.pcloud.com oder eapi.pcloud.com - andere werden abgelehnt</param>
    /// <param name="root">Pfad des gewählten Ordners im Konto, etwa „/Fotos“; leer oder „/“: das ganze Konto</param>
    public PCloudDriver(HttpClient http, string token, string host, string root)
    {
        _http = http;
        _token = token;
        _host = CheckHost(host);
        var r = StoragePath.Clean(root);
        _root = r.Length == 0 ? "" : "/" + r;
    }

    /// <summary>Nur die beiden API-Server von pCloud - die Adresse kommt aus dem Rücksprung nach der Anmeldung.</summary>
    public static string CheckHost(string? host) => host?.Trim().ToLowerInvariant() switch
    {
        HostUs => HostUs,
        HostEu => HostEu,
        _ => throw new ArgumentException("Unbekannter pCloud-Server."),
    };

    /// <summary>Server zur Region aus dem Rücksprung: hostname, sonst locationid (1 USA, 2 Europa).</summary>
    public static string HostFor(string? hostname, string? locationId) =>
        !string.IsNullOrEmpty(hostname) ? CheckHost(hostname) : locationId == "2" ? HostEu : HostUs;

    /// <summary>Voller Pfad im Konto zu einem Pfad unter der Wurzel.</summary>
    private string Abs(string path)
    {
        var rel = StoragePath.Clean(path);
        var p = rel.Length == 0 ? _root : _root + "/" + rel;
        return p.Length == 0 ? "/" : p;
    }

    // ---------------- Anfragen ----------------

    private string Url(string method, IEnumerable<(string Key, string Value)> args) =>
        $"https://{_host}/{method}?" + string.Join('&', args.Select(a => a.Key + "=" + Uri.EscapeDataString(a.Value)));

    /// <summary>Eine Methode aufrufen, bei Drosselung und Serverfehlern wiederholt. body baut den Inhalt je Versuch neu.</summary>
    private async Task<JsonElement> CallAsync(string method, string what, CancellationToken ct, Func<HttpContent>? body = null, params (string, string)[] args)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Put, Url(method, args)) { Content = body?.Invoke() };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            TimeSpan? retry = null;
            JsonElement json = default;
            using (var res = await _http.SendAsync(req, ct))
            {
                if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                    retry = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                else if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new UnauthorizedAccessException(what + ": pCloud lehnt die Anmeldung ab - unter Einstellungen → Dateien → Externe Ziele bearbeiten und neu anmelden.");
                else if (!res.IsSuccessStatusCode)
                    throw new IOException($"{what}: pCloud antwortet mit {(int)res.StatusCode} {res.ReasonPhrase}.");
                else json = await ReadJsonAsync(res, ct);
            }
            if (retry == null)
            {
                var code = json.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : -1;
                if (code == 0) return json;
                if (code is >= 4000 and < 6000 && attempt < 5) retry = TimeSpan.FromSeconds(code < 5000 ? 10 * attempt : 3 * attempt);
                else throw Error(code, json.TryGetProperty("error", out var e) ? e.GetString() : null, what);
            }
            if (attempt >= 5) throw new IOException(what + ": pCloud ist gerade überlastet, bitte später noch einmal.");
            await Wait(retry.Value > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : retry.Value, ct);
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct); return doc.RootElement.Clone(); }
        catch (JsonException) { throw new IOException("Unerwartete Antwort von pCloud."); }
    }

    /// <summary>Fehlercodes der API (https://docs.pcloud.com/errors/) als verständliche Meldung.</summary>
    public static Exception Error(int code, string? message, string what) => code switch
    {
        2002 or 2005 or 2009 or 2055 => new KeyNotFoundException(what + ": nicht gefunden."),
        2004 => new IOException(what + ": unter diesem Namen gibt es schon etwas."),
        1000 or 2000 or 2094 or 2095 => new UnauthorizedAccessException(what + ": pCloud lehnt die Anmeldung ab - unter Einstellungen → Dateien → Externe Ziele bearbeiten und neu anmelden."),
        2003 => new UnauthorizedAccessException(what + ": keine Berechtigung."),
        2008 => new IOException(what + ": der pCloud-Speicher ist voll."),
        2001 or 2010 => new ArgumentException(what + ": ungültiger Name."),
        2042 => new UnauthorizedAccessException(what + ": der oberste Ordner lässt sich nicht umbenennen."),
        2043 => new IOException(what + ": ein Ordner lässt sich nicht in sich selbst verschieben."),
        >= 4000 and < 5000 => new IOException(what + ": pCloud bremst gerade (zu viele Anfragen), bitte später noch einmal."),
        >= 5000 and < 6000 => new IOException(what + ": pCloud meldet einen internen Fehler, bitte später noch einmal."),
        _ => new IOException($"{what}: pCloud meldet Fehler {code}" + (message is { Length: > 0 } ? ": " + message : ".")),
    };

    private static DriverEntry Entry(JsonElement m)
    {
        var isDir = m.TryGetProperty("isfolder", out var f) && f.ValueKind == JsonValueKind.True;
        var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var size = !isDir && m.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
        var type = m.TryGetProperty("contenttype", out var t) ? t.GetString() : null;
        return new DriverEntry(name, isDir, size, m.TryGetProperty("modified", out var d) ? ParseDate(d.GetString()) : DateTime.MinValue, type);
    }

    /// <summary>pCloud schreibt Zeiten wie „Thu, 19 Sep 2013 07:31:46 +0000“.</summary>
    public static DateTime ParseDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
        if (s.Length > 5 && s[^5] is '+' or '-' && s[^6] == ' ') s = s[..^2] + ":" + s[^2..];
        return DateTimeOffset.TryParseExact(s, "ddd, dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.UtcDateTime : DateTime.MinValue;
    }

    private static JsonElement Meta(JsonElement json) =>
        json.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : throw new IOException("Unerwartete Antwort von pCloud.");

    // ---------------- IStorageDriver ----------------

    public async Task<List<DriverEntry>> ListAsync(string path, CancellationToken ct)
    {
        var meta = Meta(await CallAsync("listfolder", "Auflisten", ct, null, ("path", Abs(path))));
        return meta.TryGetProperty("contents", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(Entry).ToList() : [];
    }

    /// <summary>Ordner direkt darunter - für die Auswahl beim Einbinden.</summary>
    public async Task<List<string>> FoldersAsync(string path, CancellationToken ct)
    {
        var meta = Meta(await CallAsync("listfolder", "Auflisten", ct, null, ("path", Abs(path)), ("nofiles", "1")));
        return meta.TryGetProperty("contents", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(Entry).Where(e => e.IsDir).Select(e => e.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];
    }

    public async Task<DriverEntry?> StatAsync(string path, CancellationToken ct)
    {
        var abs = Abs(path);
        if (abs == "/") return new DriverEntry("", true, 0, DateTime.MinValue, null);
        // stat kennt Dateien; ein Ordner meldet sich dort nicht überall - dann über listfolder ohne Dateien
        try { return Entry(Meta(await CallAsync("stat", "Abfragen", ct, null, ("path", abs)))); }
        catch (KeyNotFoundException) { }
        try
        {
            var m = Meta(await CallAsync("listfolder", "Abfragen", ct, null, ("path", abs), ("nofiles", "1")));
            return Entry(m) with { Name = abs[(abs.LastIndexOf('/') + 1)..] };
        }
        catch (KeyNotFoundException) { return null; }
    }

    private readonly Dictionary<string, (string Url, DateTime Until)> _links = new();

    /// <summary>Adresse zum Herunterladen (einige Minuten gemerkt - die Audiothek liest in vielen kleinen Ausschnitten).</summary>
    private async Task<string> LinkAsync(string path, bool fresh, CancellationToken ct)
    {
        var abs = Abs(path);
        lock (_links)
            if (!fresh && _links.TryGetValue(abs, out var l) && l.Until > DateTime.UtcNow) return l.Url;
        var json = await CallAsync("getfilelink", "Lesen", ct, null, ("path", abs));
        var host = json.TryGetProperty("hosts", out var h) && h.ValueKind == JsonValueKind.Array ? h.EnumerateArray().Select(x => x.GetString()).FirstOrDefault() : null;
        var p = json.TryGetProperty("path", out var pp) ? pp.GetString() : null;
        // Nur Server von pCloud, nur HTTPS
        if (host == null || p == null || !p.StartsWith('/') || !(host.EndsWith(".pcloud.com", StringComparison.OrdinalIgnoreCase) && Uri.CheckHostName(host) == UriHostNameType.Dns))
            throw new IOException("Lesen: pCloud liefert keine Adresse zum Herunterladen.");
        var url = "https://" + host + p;
        lock (_links) _links[abs] = (url, DateTime.UtcNow.AddMinutes(5));
        return url;
    }

    /// <summary>Herunterladen; ist die gemerkte Adresse abgelaufen, einmal eine neue holen.</summary>
    private async Task<HttpResponseMessage> GetAsync(string path, RangeHeaderValue? range, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var url = await LinkAsync(path, fresh: attempt > 1, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = range;
            var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.IsSuccessStatusCode) return res;
            res.Dispose();
            if (attempt >= 3) throw new IOException($"Lesen: pCloud antwortet mit {(int)res.StatusCode}.");
            if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) await Wait(TimeSpan.FromSeconds(5 * attempt), ct);
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var res = await GetAsync(path, null, ct);
        try { return await res.Content.ReadAsStreamAsync(ct); }
        catch { res.Dispose(); throw; }
    }

    public async Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct)
    {
        if (count <= 0) return [];
        using var res = await GetAsync(path, new RangeHeaderValue(offset, offset + count - 1), ct);
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        // Ohne Ausschnitt (200 statt 206): den Anfang überspringen
        if (res.StatusCode == HttpStatusCode.OK && offset > 0)
        {
            var skip = new byte[81920];
            for (long left = offset; left > 0;)
            {
                var n = await s.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, left)), ct);
                if (n == 0) break;
                left -= n;
            }
        }
        var buf = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(got, count - got), ct);
            if (n == 0) break;
            got += n;
        }
        return got == count ? buf : buf[..got];
    }

    /// <summary>Änderungszeit für das nächste Hochladen in diesem Ablauf (Übertragen): pCloud nimmt sie nur beim Hochladen an, nachträglich geht es nicht.</summary>
    public static readonly AsyncLocal<DateTime?> UploadTime = new();

    private static (string, string)[] WithTime(params (string, string)[] args) => UploadTime.Value is { } t
        ? [.. args, ("mtime", new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))]
        : args;

    public async Task WriteAsync(string path, Stream data, CancellationToken ct)
    {
        var rel = StoragePath.Clean(path);
        if (rel.Length == 0) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht überschreiben.");
        var folder = await FolderIdAsync(StoragePath.Parent(rel), ct);
        var name = rel.Split('/').Last();

        // Länge unbekannt oder nicht zurückzuspulen: erst auf die Platte, nicht in den Speicher - ein Versuch muss sich wiederholen lassen
        Stream source = data;
        FileStream? spool = null;
        try
        {
            if (!data.CanSeek)
            {
                spool = new FileStream(Path.Combine(Path.GetTempPath(), "dmoffice-pcloud-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await data.CopyToAsync(spool, ct);
                spool.Position = 0;
                source = spool;
            }
            var start = source.Position;
            var total = source.Length - start;
            if (total > Simple && await UploadInPartsAsync(folder, name, source, total, ct)) return;
            source.Position = start;
            await CallAsync("uploadfile", "Schreiben", ct, () =>
            {
                source.Position = start;
                var c = new StreamContent(new LeaveOpen(source), 81920);
                c.Headers.ContentLength = total;
                c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return c;
            }, WithTime(("folderid", folder), ("filename", name), ("nopartial", "1")));
        }
        finally { if (spool != null) await spool.DisposeAsync(); }
        lock (_links) _links.Remove(Abs(rel));
    }

    /// <summary>
    /// Große Datei in Teilen: Sitzung anlegen, Teile mit Versatz schreiben (jeder Teil wird bei Fehlern einzeln wiederholt),
    /// zum Schluss unter dem Namen speichern. False: pCloud kennt das Hochladen in Teilen nicht - dann in einem Stück.
    /// </summary>
    private async Task<bool> UploadInPartsAsync(string folder, string name, Stream source, long total, CancellationToken ct)
    {
        string id;
        try
        {
            var created = await CallAsync("upload_create", "Schreiben", ct);
            id = created.TryGetProperty("uploadid", out var u) ? u.ToString() : throw new IOException("Unerwartete Antwort von pCloud.");
        }
        catch (IOException) { return false; }
        var done = false;
        try
        {
            var buf = new byte[(int)Math.Min(Chunk, total)];
            for (long at = 0; at < total;)
            {
                var n = (int)Math.Min(buf.Length, total - at);
                await source.ReadExactlyAsync(buf.AsMemory(0, n), ct);
                await CallAsync("upload_write", "Schreiben", ct, () => new ByteArrayContent(buf, 0, n),
                    ("uploadid", id), ("uploadoffset", at.ToString(CultureInfo.InvariantCulture)));
                at += n;
            }
            await CallAsync("upload_save", "Schreiben", ct, null, WithTime(("uploadid", id), ("folderid", folder), ("name", name)));
            done = true;
            return true;
        }
        finally
        {
            // Abgebrochen: die Sitzung bei pCloud verwerfen
            if (!done)
                try { await CallAsync("upload_delete", "Schreiben", CancellationToken.None, null, ("uploadid", id)); }
                catch (Exception) { }
        }
    }

    /// <summary>Kennung eines Ordners unter der Wurzel; fehlende Ordner werden angelegt.</summary>
    private async Task<string> FolderIdAsync(string path, CancellationToken ct)
    {
        var rel = StoragePath.Clean(path);
        if (rel.Length == 0)
            return _root.Length == 0 ? "0" : Id(Meta(await CallAsync("listfolder", "Ordner", ct, null, ("path", _root), ("nofiles", "1"))));
        var meta = await MkdirAsync(rel, ct);
        return Id(meta);
    }

    private static string Id(JsonElement meta) =>
        meta.TryGetProperty("folderid", out var f) ? f.ToString() : throw new IOException("Unerwartete Antwort von pCloud.");

    Task IStorageDriver.MkdirAsync(string path, CancellationToken ct) => MkdirAsync(path, ct);

    /// <summary>Ordner samt fehlender davor anlegen; liefert die Daten des letzten.</summary>
    public async Task<JsonElement> MkdirAsync(string path, CancellationToken ct)
    {
        var rel = StoragePath.Clean(path);
        var at = "";
        JsonElement last = default;
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            at = StoragePath.Join(at, part);
            last = Meta(await CallAsync("createfolderifnotexists", "Ordner anlegen", ct, null, ("path", Abs(at))));
        }
        return last;
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        if (StoragePath.Clean(path).Length == 0) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht löschen.");
        var e = await StatAsync(path, ct);
        if (e == null) return;
        try
        {
            if (e.IsDir) await CallAsync("deletefolderrecursive", "Löschen", ct, null, ("path", Abs(path)));
            else await CallAsync("deletefile", "Löschen", ct, null, ("path", Abs(path)));
        }
        catch (KeyNotFoundException) { }
        lock (_links) _links.Remove(Abs(path));
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        var a = StoragePath.Clean(from); var b = StoragePath.Clean(to);
        if (a.Length == 0) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht verschieben.");
        if (b.Length == 0) throw new ArgumentException("Ungültiges Ziel.");
        var e = await StatAsync(a, ct) ?? throw new KeyNotFoundException("Verschieben: nicht gefunden.");
        // renamefile ersetzt ein vorhandenes Ziel stillschweigend - das soll es hier nicht
        if (await StatAsync(b, ct) != null) throw new IOException("Verschieben: unter diesem Namen gibt es schon etwas.");
        if (StoragePath.Parent(b) is { Length: > 0 } parent) await MkdirAsync(parent, ct);
        await CallAsync(e.IsDir ? "renamefolder" : "renamefile", "Verschieben", ct, null, ("path", Abs(a)), ("topath", Abs(b)));
        lock (_links) _links.Remove(Abs(a));
    }

    public async Task<DriverSpace?> SpaceAsync(CancellationToken ct)
    {
        var json = await CallAsync("userinfo", "Platz abfragen", ct);
        long? N(string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        return N("quota") is { } quota && N("usedquota") is { } used ? new DriverSpace(used, Math.Max(0, quota - used)) : null;
    }

    /// <summary>E-Mail-Adresse des angemeldeten Kontos - zum Anzeigen beim Ordner.</summary>
    public async Task<string?> AccountAsync(CancellationToken ct)
    {
        var json = await CallAsync("userinfo", "Konto", ct);
        return json.TryGetProperty("email", out var e) ? e.GetString() : null;
    }

    public void Dispose() { }

    /// <summary>Der Inhalt einer Anfrage schließt seinen Stream - den Quellstrom dabei offen lassen, sonst geht kein zweiter Versuch.</summary>
    private sealed class LeaveOpen(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
