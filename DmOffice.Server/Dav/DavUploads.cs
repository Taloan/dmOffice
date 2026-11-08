using System.Globalization;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Hochladen in Stuecken fuer Nextcloud-Clients (Version 2, "chunking v2" wie Nextcloud):
/// <list type="bullet">
/// <item>MKCOL /remote.php/dav/uploads/{login}/{transfer} - Sitzung anlegen</item>
/// <item>PUT .../{transfer}/{teil} - ein Teil (Namen sortierbar, meist Nummern; beliebig gross, nur das Kontingent zaehlt)</item>
/// <item>PROPFIND .../{transfer} - welche Teile schon da sind (Fortsetzen)</item>
/// <item>MOVE .../{transfer}/.file mit Destination - zusammensetzen und ablegen (OC-Total-Length, X-OC-Mtime, If-Match)</item>
/// <item>DELETE .../{transfer} - abbrechen</item>
/// </list>
/// Teile liegen verschluesselt als Blobs, die Sitzung im Schluessel/Wert-Speicher der Benutzerdatenbank - kein neues
/// Schema. Sitzungen verfallen nach einem Tag.
/// </summary>
public static partial class DavServer
{
    private const string DavUploadPrefix = "upload.dav.";
    private static readonly JsonSerializerOptions DavUploadJson = new(JsonSerializerDefaults.Web);
    /// <summary>Offene Sitzungen je Benutzer: Sync-Programme laden mehrere Dateien zugleich, abgebrochene Sitzungen bleiben einen Tag.</summary>
    internal const int MaxOpenDavUploads = 50;

    private sealed class DavUpload
    {
        public DateTime Created { get; set; }
        /// <summary>Teil-Name -> Blob und Laenge.</summary>
        public Dictionary<string, FilesApi.UploadChunk> Chunks { get; set; } = new();
        public long Received => Chunks.Values.Sum(c => c.Length);
    }

    private static bool ValidName(string s) => s.Length is > 0 and <= 200 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && s is not "." and not "..";

    private static DavUpload? LoadUpload(UserDb db, string transfer) =>
        db.Get(DavUploadPrefix + transfer) is { } json ? JsonSerializer.Deserialize<DavUpload>(json, DavUploadJson) : null;

    private static void SaveUpload(UserDb db, string transfer, DavUpload u) => db.Set(DavUploadPrefix + transfer, JsonSerializer.Serialize(u, DavUploadJson));

    private static void DropUpload(UserDb db, string transfer, DavUpload u)
    {
        foreach (var c in u.Chunks.Values) if (c.Blob != null) try { db.Blobs.Delete(c.Blob); } catch (IOException) { }
        db.Set(DavUploadPrefix + transfer, null);
    }

    public static void SweepUploads(UserDb db)
    {
        foreach (var key in db.Query("SELECT key FROM kv WHERE key LIKE $p", r => r.GetString(0), ("$p", DavUploadPrefix + "%")).ToList())
            if (LoadUpload(db, key[DavUploadPrefix.Length..]) is { } u && DateTime.UtcNow - u.Created > TimeSpan.FromDays(1)) DropUpload(db, key[DavUploadPrefix.Length..], u);
    }

    /// <summary>Teile der Reihe nach: Nummern nach Zahl, sonst nach Namen.</summary>
    private static List<FilesApi.UploadChunk> Ordered(DavUpload u) =>
        (u.Chunks.Keys.All(k => long.TryParse(k, out _))
            ? u.Chunks.OrderBy(kv => long.Parse(kv.Key, CultureInfo.InvariantCulture))
            : u.Chunks.OrderBy(kv => kv.Key, StringComparer.Ordinal)).Select(kv => kv.Value).ToList();

    /// <summary>Grosse Dateien kommen in einem Stueck (WebDAV) oder als grosse Teile: die Grenze fuer den Koerper aufheben.</summary>
    private static void NoBodyLimit(HttpContext http)
    {
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = null;
    }

    private static List<DavUpload> OpenUploads(UserDb db) =>
        db.Query("SELECT key FROM kv WHERE key LIKE $p", r => r.GetString(0), ("$p", DavUploadPrefix + "%"))
            .Select(k => LoadUpload(db, k[DavUploadPrefix.Length..])).OfType<DavUpload>().ToList();

    /// <summary>Wie viel noch hineinpasst (Kontingent): Alle offenen Sitzungen zählen mit, freed: was dabei wegfällt. long.MaxValue ohne Kontingent.</summary>
    private static long Room(UserDb db, long freed)
    {
        var quota = FileStore.Quota(db);
        return quota > 0 ? Math.Max(0, quota - UserStorage.Counted(db) - OpenUploads(db).Sum(o => o.Received) + freed) : long.MaxValue;
    }

    /// <summary>
    /// Fehlen Teile? Ohne OC-Total-Length lässt es sich nur an den Nummern erkennen: entweder fortlaufend ab 0 oder 1, oder
    /// (ältere Clients) jede Nummer ist die Stelle des Teils in der Datei. Bei anderen Namen gibt es keinen Anhaltspunkt.
    /// </summary>
    private static bool MissesParts(DavUpload u)
    {
        if (u.Chunks.Count == 0 || !u.Chunks.Keys.All(k => long.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
        var parts = u.Chunks.Select(kv => (No: long.Parse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture), kv.Value.Length)).OrderBy(p => p.No).ToList();
        var counted = parts[0].No is 0 or 1 && parts.Zip(parts.Skip(1)).All(p => p.Second.No == p.First.No + 1);
        long at = 0;
        var byOffset = true;
        foreach (var p in parts) { byOffset &= p.No == at; at += p.Length; }
        return !counted && !byOffset;
    }

    /// <summary>segs: "uploads", login, transfer[, teil]</summary>
    private static async Task Uploads(HttpContext http, UserSpace space, List<string> segs, string method)
    {
        var db = space.Db;
        // Nur der eigene Bereich, nicht fuer den notizi-Zugang, nicht bei abgeschalteter Dateiablage
        if (segs.Count < 2 || segs[1] != space.User.Login || Scope(http) == AppPasswords.ScopeNotizi || FileStore.FilesOff(db))
        {
            http.Response.StatusCode = 403;
            return;
        }
        if (method == "OPTIONS") { http.Response.Headers.Allow = "OPTIONS, MKCOL, PUT, PROPFIND, MOVE, DELETE"; http.Response.StatusCode = 200; return; }
        if (segs.Count == 2)
        {
            // Die Sammlung der Uploads selbst: leer anzeigen, sonst nichts
            if (method == "PROPFIND") { await WriteUploadList(http, space, null, null); return; }
            http.Response.StatusCode = 405;
            return;
        }
        var transfer = segs[2];
        if (!ValidName(transfer) || segs.Count > 4) { http.Response.StatusCode = 400; return; }
        var u = LoadUpload(db, transfer);
        var part = segs.Count == 4 ? segs[3] : null;

        switch (method)
        {
            case "MKCOL" when part == null:
                SweepUploads(db);
                if (u != null) { http.Response.StatusCode = 405; return; }   // gibt es schon - wie Nextcloud
                if (OpenUploads(db).Count >= MaxOpenDavUploads) { http.Response.StatusCode = 429; return; }
                SaveUpload(db, transfer, new DavUpload { Created = DateTime.UtcNow });
                http.Response.StatusCode = 201;
                return;
            case "PROPFIND":
                if (u == null) { http.Response.StatusCode = 404; return; }
                await WriteUploadList(http, space, transfer, part == null ? u : null);
                return;
            case "DELETE":
                if (u == null) { http.Response.StatusCode = 404; return; }
                if (part == null) DropUpload(db, transfer, u);
                else if (u.Chunks.Remove(part, out var gone))
                {
                    if (gone.Blob != null) try { db.Blobs.Delete(gone.Blob); } catch (IOException) { }
                    SaveUpload(db, transfer, u);
                }
                http.Response.StatusCode = 204;
                return;
            case "PUT" when part != null && part != ".file":
            {
                if (u == null) { http.Response.StatusCode = 404; return; }
                if (!ValidName(part)) { http.Response.StatusCode = 400; return; }
                // Nicht beliebig viele Teile je Upload (Nextcloud schickt Stuecke von 10 MB und mehr)
                if (u.Chunks.Count >= FilesApi.MaxChunks && !u.Chunks.ContainsKey(part)) { http.Response.StatusCode = 400; return; }
                NoBodyLimit(http);
                var room = Room(db, u.Chunks.TryGetValue(part, out var same) ? same.Length : 0);
                var blob = await db.Blobs.WriteAsync(http.Request.Body, room, () => new QuotaExceededException(FileStore.Quota(db)), http.RequestAborted);
                // Neu laden: andere Teile koennen gleichzeitig angekommen sein
                var gate = UploadLocks.GetOrAdd(space.User.Id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(http.RequestAborted);
                try
                {
                    u = LoadUpload(db, transfer);
                    if (u == null) { db.Blobs.Delete(blob.Id); http.Response.StatusCode = 404; return; }
                    // Gleichzeitig angekommene Teile (auch anderer Sitzungen) sind jetzt mitgezählt
                    if (blob.Size > Room(db, u.Chunks.TryGetValue(part, out var replaced) ? replaced.Length : 0))
                    {
                        db.Blobs.Delete(blob.Id);
                        throw new QuotaExceededException(FileStore.Quota(db));
                    }
                    if (u.Chunks.TryGetValue(part, out var old) && old.Blob != null) try { db.Blobs.Delete(old.Blob); } catch (IOException) { }
                    u.Chunks[part] = new FilesApi.UploadChunk(blob.Id, blob.Size);
                    SaveUpload(db, transfer, u);
                }
                finally { gate.Release(); }
                http.Response.StatusCode = 201;
                return;
            }
            case "MOVE" when part == ".file":
            {
                if (u == null) { http.Response.StatusCode = 404; return; }
                var destSegs = DestinationSegments(http);
                var dest = destSegs == null ? null : Resolve(http, space, destSegs.Value);
                if (dest is not { Kind: Kind.Files } || dest.FilePath!.Length == 0) { http.Response.StatusCode = 400; return; }
                if (long.TryParse(Header(http, "OC-Total-Length"), out var total) && total != u.Received)
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync($"Es fehlen Teile ({u.Received} von {total} Bytes).");
                    return;
                }
                if (Header(http, "OC-Total-Length") == null && MissesParts(u))
                {
                    http.Response.StatusCode = 400;
                    await http.Response.WriteAsync("Es fehlen Teile.");
                    return;
                }
                DateTime? mtime = double.TryParse(Header(http, "X-OC-Mtime"), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0
                    ? DateTime.UnixEpoch.AddSeconds(Math.Floor(secs)) : null;
                var loc = SharedFiles.Resolve(http, dest.FilePath).Creatable();
                var ids = Ordered(u).Where(c => c.Blob != null).Select(c => c.Blob!).ToList();
                (string Etag, bool Created) result;
                await using (var body = FilesApi.OpenBlobs(db, ids))
                    result = await FileStore.PutAsync(loc.Db, loc.Path, body, Mime(dest.FilePath), Header(http, "If-Match"), Header(http, "If-None-Match"), mtime, http.RequestAborted);
                DropUpload(db, transfer, u);
                http.Response.Headers.ETag = result.Etag;
                http.Response.Headers["OC-ETag"] = result.Etag;
                if (FileStore.Get(loc.Db, loc.Path) is { } f) http.Response.Headers["OC-FileId"] = OcId(loc.Show(f));
                if (mtime != null) http.Response.Headers["X-OC-MTime"] = "accepted";
                http.Response.StatusCode = result.Created ? 201 : 204;
                return;
            }
            default:
                http.Response.StatusCode = 405;
                return;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> UploadLocks = new();

    /// <summary>PROPFIND: die Sammlung und (Tiefe 1) ihre Teile mit Groesse.</summary>
    private static async Task WriteUploadList(HttpContext http, UserSpace space, string? transfer, DavUpload? u)
    {
        var baseHref = $"/remote.php/dav/uploads/{Esc(space.User.Login)}/" + (transfer == null ? "" : Esc(transfer) + "/");
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\">");
        void Entry(string href, long? length)
        {
            sb.Append("<d:response><d:href>").Append(System.Security.SecurityElement.Escape(href)).Append("</d:href><d:propstat><d:prop>");
            sb.Append(length == null ? "<d:resourcetype><d:collection/></d:resourcetype>" : $"<d:resourcetype/><d:getcontentlength>{length}</d:getcontentlength>");
            sb.Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
        }
        Entry(baseHref, null);
        if (u != null && Header(http, "Depth") != "0")
            foreach (var (name, c) in u.Chunks) Entry(baseHref + Esc(name), c.Length);
        sb.Append("</d:multistatus>");
        http.Response.StatusCode = 207;
        http.Response.ContentType = "application/xml; charset=utf-8";
        await http.Response.WriteAsync(sb.ToString());
    }
}
