using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace DmOffice.Storage;

/// <summary>Ein Eintrag in einem fremden Speicher.</summary>
public sealed record DriverEntry(string Name, bool IsDir, long Size, DateTime Modified, string? ContentType);

/// <summary>Belegt und frei in Bytes; Belegt null, wenn der Speicher es nicht verraet.</summary>
public sealed record DriverSpace(long? Used, long Free);

/// <summary>
/// Ein fremder Speicher (Version 2): ein Ordner auf dem Server (Unraid-Freigabe, USB-Platte) oder ein WebDAV-Server
/// (Hetzner Storage Box, NAS, Nextcloud). Pfade sind relativ zur Wurzel des Treibers, mit „/“ getrennt, ohne „..“ -
/// <see cref="StoragePath.Clean"/> prueft das, bevor ein Treiber etwas anfasst. Grundlage fuer externe Ordner in der
/// Dateiablage und fuer weitere Ziele von Kopie und Sicherung (DriverRemote).
/// </summary>
public interface IStorageDriver : IDisposable
{
    Task<List<DriverEntry>> ListAsync(string path, CancellationToken ct);
    /// <summary>Null: gibt es nicht.</summary>
    Task<DriverEntry?> StatAsync(string path, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct);
    /// <summary>Schreiben (ersetzt); fehlende Ordner davor werden angelegt.</summary>
    Task WriteAsync(string path, Stream data, CancellationToken ct);
    /// <summary>Ordner anlegen, samt fehlender Ordner davor.</summary>
    Task MkdirAsync(string path, CancellationToken ct);
    /// <summary>Datei oder Ordner (mit Inhalt) loeschen; schon weg ist kein Fehler.</summary>
    Task DeleteAsync(string path, CancellationToken ct);
    Task MoveAsync(string from, string to, CancellationToken ct);
    /// <summary>Platz am Ziel - null, wenn der Speicher es nicht verraet.</summary>
    Task<DriverSpace?> SpaceAsync(CancellationToken ct);
    /// <summary>Änderungszeit setzen (Übertragen behält die der Quelle), so gut der Speicher es kann; nicht jeder kann es.</summary>
    Task SetModifiedAsync(string path, DateTime utc, CancellationToken ct) => Task.CompletedTask;
}

public static class StoragePath
{
    /// <summary>Relativen Pfad pruefen und vereinheitlichen: keine „..“, keine Laufwerke, keine Steuerzeichen.</summary>
    public static string Clean(string? path)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
            if (p is "." or ".." || p.Contains(':') || p.Any(char.IsControl) || p.Length > 255)
                throw new ArgumentException("Ungültiger Pfad.");
        return string.Join('/', parts);
    }

    public static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "/" + b;
    public static string Parent(string p) => p.Contains('/') ? p[..p.LastIndexOf('/')] : "";
}

/// <summary>
/// Ein Ordner auf dem Server. Die Wurzel legt der Admin fest; nichts darf aus ihr heraus - weder ueber „..“ noch
/// ueber Verknuepfungen (Symlinks), die nach aussen zeigen.
/// </summary>
public sealed class FolderDriver : IStorageDriver
{
    private readonly string _root;

    public FolderDriver(string root)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Den Ordner gibt es auf dem Server nicht.");
    }

    /// <summary>Voller Pfad innerhalb der Wurzel; Verknuepfungen auf dem Weg dorthin muessen innerhalb bleiben.</summary>
    private string Full(string path)
    {
        var rel = StoragePath.Clean(path);
        var full = rel.Length == 0 ? _root : Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar)) throw new UnauthorizedAccessException("Außerhalb des Ordners.");
        // Jede Stufe pruefen: eine Verknuepfung mitten im Pfad koennte nach draussen zeigen
        var at = _root;
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, part);
            FileSystemInfo info = Directory.Exists(at) ? new DirectoryInfo(at) : new FileInfo(at);
            if (info.Exists && info.LinkTarget != null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (target == null || (target != _root && !target.StartsWith(_root + Path.DirectorySeparatorChar)))
                    throw new UnauthorizedAccessException("Verknüpfung zeigt aus dem Ordner heraus.");
            }
        }
        return full;
    }

    private static DriverEntry Entry(FileSystemInfo i) => i is DirectoryInfo
        ? new DriverEntry(i.Name, true, 0, i.LastWriteTimeUtc, null)
        : new DriverEntry(i.Name, false, ((FileInfo)i).Length, i.LastWriteTimeUtc, null);

    public Task<DriverSpace?> SpaceAsync(CancellationToken ct)
    {
        // Die Platte, auf der der Ordner liegt (bei Unraid-Freigaben das ganze Array)
        try { var d = new DriveInfo(_root); return Task.FromResult<DriverSpace?>(new DriverSpace(d.TotalSize - d.TotalFreeSpace, d.AvailableFreeSpace)); }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return Task.FromResult<DriverSpace?>(null); }
    }

    public Task<List<DriverEntry>> ListAsync(string path, CancellationToken ct)
    {
        var dir = new DirectoryInfo(Full(path));
        if (!dir.Exists) throw new KeyNotFoundException("Ordner nicht gefunden.");
        var list = new List<DriverEntry>();
        foreach (var i in dir.EnumerateFileSystemInfos())
        {
            // Verknuepfungen nach draussen gar nicht erst zeigen
            if (i.LinkTarget != null)
            {
                var t = i.ResolveLinkTarget(true)?.FullName;
                if (t == null || (t != _root && !t.StartsWith(_root + Path.DirectorySeparatorChar))) continue;
            }
            list.Add(Entry(i));
        }
        return Task.FromResult(list);
    }

    public Task<DriverEntry?> StatAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        FileSystemInfo i = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        return Task.FromResult(i.Exists ? Entry(i) with { Name = Path.GetFileName(full) } : null);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(Full(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous));

    public async Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct)
    {
        await using var f = new FileStream(Full(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        f.Position = offset;
        var buf = new byte[Math.Max(0, Math.Min(count, f.Length - offset))];
        await f.ReadExactlyAsync(buf, ct);
        return buf;
    }

    public async Task WriteAsync(string path, Stream data, CancellationToken ct)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Erst fertig schreiben, dann an seinen Platz - ein Abbruch hinterlaesst keine halbe Datei
        // Zufälliger Name und CreateNew (O_EXCL): eine vorher hingelegte Verknüpfung unter dem Namen wird nicht verfolgt
        var tmp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".dmoffice-part";
        try
        {
            await using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await data.CopyToAsync(f, ct);
            File.Move(tmp, full, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public Task MkdirAsync(string path, CancellationToken ct) { Directory.CreateDirectory(Full(path)); return Task.CompletedTask; }

    public Task SetModifiedAsync(string path, DateTime utc, CancellationToken ct) { SetModified(path, utc); return Task.CompletedTask; }

    private void SetModified(string path, DateTime utc) { try { File.SetLastWriteTimeUtc(Full(path), DateTime.SpecifyKind(utc, DateTimeKind.Utc)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        if (full == _root) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht löschen.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string from, string to, CancellationToken ct)
    {
        var a = Full(from); var b = Full(to);
        if (a == _root) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht verschieben.");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        if (Directory.Exists(a)) Directory.Move(a, b); else File.Move(a, b, overwrite: false);
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

/// <summary>
/// Ein WebDAV-Server (RFC 4918): Auflisten mit PROPFIND (Tiefe 1), Lesen mit GET (auch Ausschnitte), Schreiben mit PUT,
/// Ordner mit MKCOL, Loeschen, Verschieben. Anmeldung mit Benutzername und Passwort (Basic) - nur ueber HTTPS sinnvoll,
/// im Heimnetz auch HTTP.
/// </summary>
public sealed class WebDavDriver : IStorageDriver
{
    /// <summary>Antwort eines WebDAV-Servers als XML: höchstens 32 MB, ohne DTD - ein fremder Server soll den Speicher nicht füllen.</summary>
    private static async Task<XDocument> ReadXml(HttpResponseMessage res, CancellationToken ct)
    {
        const long max = 32L * 1024 * 1024;
        if (res.Content.Headers.ContentLength > max) throw new IOException("Die Antwort des Servers ist zu groß.");
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        var m = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (m.Length + n > max) throw new IOException("Die Antwort des Servers ist zu groß.");
            m.Write(buf, 0, n);
        }
        m.Position = 0;
        using var reader = System.Xml.XmlReader.Create(m, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private readonly HttpClient _http;
    private readonly bool _own;
    private readonly Uri _base;
    private static readonly XNamespace D = "DAV:";

    /// <param name="baseUrl">Wurzel, etwa https://u123.your-storagebox.de/dmoffice/ oder https://cloud.example.org/remote.php/dav/files/anna/</param>
    /// <param name="http">Nur fuer Tests (eigener Handler); sonst baut der Treiber sich einen.</param>
    public WebDavDriver(string baseUrl, string? user, string? password, HttpClient? http = null)
    {
        _base = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        if (_base.Scheme is not ("http" or "https")) throw new ArgumentException("Die Adresse muss mit http:// oder https:// beginnen.");
        _own = http == null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        if (!string.IsNullOrEmpty(user))
            _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + (password ?? ""))));
    }

    private readonly AuthenticationHeaderValue? _auth;
    private bool _rootMade;
    /// <summary>Angelegte Ordner merken und nicht vor jedem Schreiben erneut anlegen (für kurzlebige Treiber mit vielen Dateien,
    /// etwa einen Lauf der Datensicherung; wer lange lebt, merkt sich nichts - der Ordner könnte inzwischen fehlen).</summary>
    public bool RememberDirs { get; set; }
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

    private Uri Url(string path, bool dir = false)
    {
        var rel = StoragePath.Clean(path);
        var enc = string.Join('/', rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri(_base, enc + (dir && enc.Length > 0 ? "/" : ""));
    }

    private HttpRequestMessage Req(string method, Uri url)
    {
        var r = new HttpRequestMessage(new HttpMethod(method), url);
        if (_auth != null) r.Headers.Authorization = _auth;
        return r;
    }

    private static void Check(HttpResponseMessage res, string what)
    {
        if (res.IsSuccessStatusCode) return;
        throw res.StatusCode switch
        {
            HttpStatusCode.NotFound => new KeyNotFoundException(what + ": nicht gefunden."),
            HttpStatusCode.Unauthorized => new UnauthorizedAccessException(what + ": Anmeldung abgelehnt (Benutzername oder Passwort)."),
            HttpStatusCode.Forbidden => new UnauthorizedAccessException(what + ": keine Berechtigung."),
            HttpStatusCode.InsufficientStorage => new IOException(what + ": der Speicher ist voll."),
            _ => new IOException($"{what}: der WebDAV-Server antwortet mit {(int)res.StatusCode} {res.ReasonPhrase}."),
        };
    }

    private const string PropBody =
        "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getcontenttype/></d:prop></d:propfind>";

    private async Task<List<(Uri Href, DriverEntry Entry)>> Propfind(string path, int depth, CancellationToken ct)
    {
        using var req = Req("PROPFIND", Url(path, dir: true));
        req.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
        req.Content = new StringContent(PropBody, Encoding.UTF8, "application/xml");
        using var res = await _http.SendAsync(req, ct);
        // Datei statt Ordner: manche Server wollen sie ohne Schraegstrich
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MovedPermanently && depth == 0 && path.Length > 0)
        {
            using var req2 = Req("PROPFIND", Url(path));
            req2.Headers.Add("Depth", "0");
            req2.Content = new StringContent(PropBody, Encoding.UTF8, "application/xml");
            using var res2 = await _http.SendAsync(req2, ct);
            return await Parse(res2, path, ct);
        }
        return await Parse(res, path, ct);
    }

    private async Task<List<(Uri, DriverEntry)>> Parse(HttpResponseMessage res, string path, CancellationToken ct)
    {
        Check(res, "Auflisten");
        var xml = await ReadXml(res, ct);
        var list = new List<(Uri, DriverEntry)>();
        foreach (var r in xml.Root?.Elements(D + "response") ?? [])
        {
            var href = (string?)r.Element(D + "href");
            if (href == null) continue;
            var prop = r.Elements(D + "propstat").Where(p => ((string?)p.Element(D + "status"))?.Contains(" 200") != false)
                .Select(p => p.Element(D + "prop")).FirstOrDefault(p => p != null);
            if (prop == null) continue;
            var isDir = prop.Element(D + "resourcetype")?.Element(D + "collection") != null;
            long.TryParse((string?)prop.Element(D + "getcontentlength"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            var modified = DateTime.TryParse((string?)prop.Element(D + "getlastmodified"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var m) ? m : DateTime.MinValue;
            var url = new Uri(_base, href);
            var name = Uri.UnescapeDataString(url.AbsolutePath.TrimEnd('/').Split('/').Last());
            list.Add((url, new DriverEntry(name, isDir, isDir ? 0 : size, modified, (string?)prop.Element(D + "getcontenttype"))));
        }
        return list;
    }

    /// <summary>RFC 4331 (quota-used-bytes, quota-available-bytes) - Nextcloud und viele NAS kennen das.</summary>
    public async Task<DriverSpace?> SpaceAsync(CancellationToken ct)
    {
        using var req = Req("PROPFIND", Url("", dir: true));
        req.Headers.Add("Depth", "0");
        req.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:quota-used-bytes/><d:quota-available-bytes/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        using var res = await _http.SendAsync(req, ct);
        Check(res, "Platz abfragen");
        var prop = (await ReadXml(res, ct)).Descendants(D + "prop");
        long? Read(string name) => prop.Select(p => (string?)p.Element(D + name)).Where(v => v != null)
            .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (long?)null).FirstOrDefault();
        // Negativ heisst bei Nextcloud: unbekannt oder unbegrenzt
        var free = Read("quota-available-bytes");
        var used = Read("quota-used-bytes");
        return free is >= 0 ? new DriverSpace(used is >= 0 ? used : null, free.Value) : null;
    }

    public async Task<List<DriverEntry>> ListAsync(string path, CancellationToken ct)
    {
        var self = Url(path, dir: true).AbsolutePath.TrimEnd('/');
        return (await Propfind(path, 1, ct)).Where(x => x.Href.AbsolutePath.TrimEnd('/') != self).Select(x => x.Entry).ToList();
    }

    public async Task<DriverEntry?> StatAsync(string path, CancellationToken ct)
    {
        try { return (await Propfind(path, 0, ct)).Select(x => x.Entry).FirstOrDefault(); }
        catch (KeyNotFoundException) { return null; }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var req = Req("GET", Url(path));
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        try { Check(res, "Lesen"); return await res.Content.ReadAsStreamAsync(ct); }
        catch { res.Dispose(); req.Dispose(); throw; }
    }

    public async Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct)
    {
        using var req = Req("GET", Url(path));
        req.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        Check(res, "Lesen");
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        // Kann der Server keine Ausschnitte (200 statt 206), den Anfang ueberspringen
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
        var buf = new byte[81920];
        while (ms.Length < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count - ms.Length)), ct);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    public async Task WriteAsync(string path, Stream data, CancellationToken ct)
    {
        var rel = StoragePath.Clean(path);
        if (StoragePath.Parent(rel) is { Length: > 0 } parent) await MkdirAsync(parent, ct);
        using var req = Req("PUT", Url(rel));
        req.Content = new StreamContent(data, 81920);
        if (data.CanSeek) req.Content.Headers.ContentLength = data.Length - data.Position;
        using var res = await _http.SendAsync(req, ct);
        Check(res, "Schreiben");
    }

    /// <summary>Nextcloud und ownCloud nehmen DAV:lastmodified als Unix-Zeit an; andere Server lehnen es ab oder übergehen es - dann bleibt die Zeit des Hochladens.</summary>
    public async Task SetModifiedAsync(string path, DateTime utc, CancellationToken ct)
    {
        using var req = Req("PROPPATCH", Url(StoragePath.Clean(path)));
        var unix = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        req.Content = new StringContent($"<?xml version=\"1.0\"?><d:propertyupdate xmlns:d=\"DAV:\"><d:set><d:prop><d:lastmodified>{unix}</d:lastmodified></d:prop></d:set></d:propertyupdate>",
            Encoding.UTF8, "application/xml");
        using var _ = await _http.SendAsync(req, ct);
    }

    public async Task MkdirAsync(string path, CancellationToken ct)
    {
        var at = "";
        foreach (var part in StoragePath.Clean(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            at = StoragePath.Join(at, part);
            if (RememberDirs && _dirs.Contains(at)) continue;
            using var req = Req("MKCOL", Url(at, dir: true));
            using var res = await _http.SendAsync(req, ct);
            // 409 ganz oben: den Ordner der Adresse selbst gibt es noch nicht - einmal anlegen und noch einmal
            if (res.StatusCode == HttpStatusCode.Conflict && !at.Contains('/') && !_rootMade)
            {
                _rootMade = true;
                using var root = Req("MKCOL", _base);
                using var made = await _http.SendAsync(root, ct);
                if (made.StatusCode != HttpStatusCode.MethodNotAllowed) Check(made, "Ordner der Adresse anlegen");
                using var again = Req("MKCOL", Url(at, dir: true));
                using var res2 = await _http.SendAsync(again, ct);
                if (res2.StatusCode != HttpStatusCode.MethodNotAllowed) Check(res2, "Ordner anlegen");
                if (RememberDirs) _dirs.Add(at);
                continue;
            }
            // 405: gibt es schon
            if (res.StatusCode != HttpStatusCode.MethodNotAllowed) Check(res, "Ordner anlegen");
            if (RememberDirs) _dirs.Add(at);
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        if (StoragePath.Clean(path).Length == 0) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht löschen.");
        using var req = Req("DELETE", Url(path));
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode != HttpStatusCode.NotFound) Check(res, "Löschen");
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        if (StoragePath.Clean(from).Length == 0) throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht verschieben.");
        if (StoragePath.Parent(StoragePath.Clean(to)) is { Length: > 0 } parent) await MkdirAsync(parent, ct);
        using var req = Req("MOVE", Url(from));
        req.Headers.Add("Destination", Url(to).AbsoluteUri);
        req.Headers.Add("Overwrite", "F");
        using var res = await _http.SendAsync(req, ct);
        Check(res, "Verschieben");
    }

    public void Dispose() { if (_own) _http.Dispose(); }
}
