using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Office;

/// <summary>JWT (HS256) wie ONLYOFFICE es nutzt - ohne fremdes Paket.</summary>
public static class Jwt
{
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Unb64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public static string Sign(JsonNode payload, string secret)
    {
        var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var body = B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var sig = B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)));
        return head + "." + body + "." + sig;
    }

    /// <summary>Payload, wenn Signatur (HS256) und Ablauf (exp, falls gesetzt) stimmen - sonst null.</summary>
    public static JsonObject? Read(string? token, string secret)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var header = JsonNode.Parse(Unb64(parts[0]));
            if ((string?)header?["alg"] != "HS256") return null;
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Unb64(parts[2]))) return null;
            var payload = JsonNode.Parse(Unb64(parts[1])) as JsonObject;
            if (payload?["exp"] is JsonValue exp && exp.TryGetValue<long>(out var e) && DateTimeOffset.FromUnixTimeSeconds(e) < DateTimeOffset.UtcNow.AddMinutes(-5)) return null;
            return payload;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}

/// <summary>
/// ONLYOFFICE Docs ueber die eigene Schnittstelle (api.js + Callback) - fuer Document Server ohne eingeschaltetes WOPI.
/// Ablauf: die Oberflaeche laedt api.js vom Document Server und bekommt von dmOffice die (mit dem JWT-Geheimnis signierte)
/// Konfiguration. Der Document Server holt die Datei ueber /onlyoffice/file und meldet Gespeichertes an /onlyoffice/callback;
/// beide Adressen tragen ein eigenes access_token (wie bei WOPI), der Callback zusaetzlich das JWT des Document Servers.
/// </summary>
public static class OnlyOffice
{
    /// <summary>Was die Community Edition bearbeitet bzw. zeigt.</summary>
    public static readonly string[] EditExtensions = ["docx", "docm", "dotx", "odt", "ott", "rtf", "xlsx", "xlsm", "xltx", "ods", "ots", "csv", "pptx", "pptm", "potx", "odp", "otp"];
    public static readonly string[] ViewExtensions = ["doc", "xls", "ppt"];

    public static Discovery NativeDiscovery()
    {
        var d = new Discovery { Product = "onlyoffice-docs", Native = true };
        foreach (var e in EditExtensions) { d.Edit[e] = ""; d.View[e] = ""; }
        foreach (var e in ViewExtensions) d.View[e] = "";
        return d;
    }

    public static string DocumentType(string ext) => ext switch
    {
        "xlsx" or "xlsm" or "xltx" or "xls" or "ods" or "ots" or "csv" => "cell",
        "pptx" or "pptm" or "potx" or "ppt" or "odp" or "otp" => "slide",
        _ => "word",
    };

    /// <summary>Schluessel des Dokuments: gleich fuer alle, die denselben Stand bearbeiten (gemeinsames Bearbeiten),
    /// neu nach jedem Speichern.</summary>
    public static string Key(string user, long fileId, string etag) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{user}|{fileId}|{etag}")))[..40].ToLowerInvariant();

    /// <summary>Welcher Stand je Dokumentschluessel zuletzt von uns gespeichert wurde (Zwischenspeichern, Mitbearbeiter).</summary>
    private static readonly ConcurrentDictionary<string, string> Written = new();

    /// <summary>Konfiguration fuer DocsAPI.DocEditor; mit Geheimnis signiert (Feld token).</summary>
    public static JsonObject Config(HttpContext http, WopiGrant grant, FileEntry f, string? secret)
    {
        var services = http.RequestServices;
        var ext = Path.GetExtension(f.Name).TrimStart('.').ToLowerInvariant();
        var withEtag = grant with { E = f.Etag };
        var (token, _) = WopiTokens.Issue(services, withEtag, WopiApi.GuestLifetime(grant), WopiTokens.OnlyOfficePurpose);
        var t = Uri.EscapeDataString(token);
        var cfg = new JsonObject
        {
            ["document"] = new JsonObject
            {
                ["fileType"] = ext,
                ["key"] = Key(grant.U, f.FileId, f.Etag),
                ["title"] = f.Name,
                ["url"] = grant.B + "/onlyoffice/file?t=" + t,
                ["permissions"] = new JsonObject
                {
                    ["edit"] = grant.W, ["comment"] = grant.W, ["review"] = grant.W, ["fillForms"] = grant.W,
                    ["download"] = grant.L == null, ["print"] = grant.L == null,   // Gaeste ueber einen Link: kein Download am Abrufzaehler vorbei
                },
            },
            ["documentType"] = DocumentType(ext),
            ["editorConfig"] = new JsonObject
            {
                ["callbackUrl"] = grant.B + "/onlyoffice/callback?t=" + t,
                ["lang"] = "de",
                ["mode"] = grant.W ? "edit" : "view",
                ["user"] = new JsonObject { ["id"] = grant.L != null ? "guest-" + grant.L : grant.U, ["name"] = grant.N },
                ["customization"] = new JsonObject
                {
                    ["forcesave"] = false,
                    ["close"] = new JsonObject { ["visible"] = true, ["text"] = "Zurück" },
                },
            },
        };
        if (!string.IsNullOrEmpty(secret)) cfg["token"] = Jwt.Sign(JsonNode.Parse(cfg.ToJsonString())!, secret);
        return cfg;
    }

    private static (WopiGrant Grant, UserSpace Space, FileEntry File)? Resolve(HttpContext http, string? token)
    {
        var services = http.RequestServices;
        var grant = WopiTokens.Read(services, token, WopiTokens.OnlyOfficePurpose);
        if (grant == null) return null;
        var user = services.GetRequiredService<UserStore>().ById(grant.V ?? grant.U);
        if (user == null || user.Stamp != grant.S) return null;
        UserSpace space;
        try { space = services.GetRequiredService<Spaces>().Get(grant.U); }
        catch (KeyNotFoundException) { return null; }
        if (FileStore.FilesOff(space.Db)) return null;
        var file = FileStore.ById(space.Db, grant.F);
        if (file == null || file.IsDir || FileStore.InRoot(file.Path, FileStore.ProtectedRoot(space.Db))) return null;
        if (grant.L != null && !WopiApi.LinkAllows(space.Db, grant.L, file)) return null;
        // Mit mir geteilte Datei: nur solange es die Freigabe gibt, schreiben nur mit Bearbeitungsrecht
        if (grant.V != null)
        {
            var (see, write) = SharedFiles.Allows(services, grant.V, grant.U, file);
            if (!see) return null;
            grant = grant with { W = grant.W && write };
        }
        // Gueltiges Token: der Document Server arbeitet fuer einen angemeldeten Benutzer - nicht bremsen
        http.Items[AnonThrottle.TrustedItem] = true;
        return (grant, space, file);
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/onlyoffice").AllowAnonymous();

        // Der Document Server holt die Datei
        g.MapGet("/file", (string? t, HttpContext http) =>
        {
            if (Resolve(http, t) is not { } r) return Results.StatusCode(401);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Stream(FileStore.OpenRead(r.Space.Db, r.File.Path), "application/octet-stream");
        });

        // Der Document Server meldet den Zustand; bei 2 (fertig) und 6 (Zwischenspeichern) liegt das Ergebnis unter url
        g.MapPost("/callback", async (string? t, HttpContext http, ServerConfig config, IHttpClientFactory clients, ILoggerFactory logs) =>
        {
            var log = logs.CreateLogger("OnlyOffice");
            IResult Answer(int error) => Results.Json(new { error });
            if (Resolve(http, t) is not { } r) return Results.StatusCode(401);
            JsonObject? body;
            try { body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject; }
            catch (JsonException) { return Results.BadRequest(); }
            if (body == null) return Results.BadRequest();
            // Mit Geheimnis gilt nur, was signiert ist: Token im Inhalt oder im Kopf ({"payload": ...})
            var secret = config.OfficeJwtSecret();
            if (!string.IsNullOrEmpty(secret))
            {
                var signed = Jwt.Read((string?)body["token"], secret);
                if (signed == null)
                {
                    var header = http.Request.Headers[config.Current.OfficeJwtHeader is { Length: > 0 } h ? h : "Authorization"].ToString();
                    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) header = header[7..];
                    signed = Jwt.Read(header, secret)?["payload"] as JsonObject;
                }
                if (signed == null) { log.LogWarning("ONLYOFFICE-Callback ohne gültiges JWT abgelehnt"); return Results.StatusCode(403); }
                body = signed;
            }
            var status = body["status"] is JsonValue sv && sv.TryGetValue<int>(out var si) ? si : 0;
            var key = (string?)body["key"] ?? "";
            if (status is not (2 or 6)) return Answer(0);
            if (!r.Grant.W) return Answer(1);
            if (key != Key(r.Grant.U, r.File.FileId, r.Grant.E ?? "")) { log.LogWarning("ONLYOFFICE-Callback mit fremdem Schlüssel"); return Answer(1); }
            var url = (string?)body["url"];
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var source)) return Answer(1);
            if (string.IsNullOrWhiteSpace(config.Current.OfficeUrl) || !Uri.TryCreate(config.Current.OfficeUrl.Trim(), UriKind.Absolute, out var office)) return Answer(1);
            // Nur vom eingestellten Document Server laden (kein Abruf beliebiger Adressen). Meldet er sich unter
            // einer anderen (internen) Adresse, gilt nur der Pfad - als Text an Schema, Host und Port des
            // eingestellten Servers gehaengt. Nicht new Uri(Basis, Text): ist Text selbst eine absolute Adresse
            // (".../http://192.168.1.1/..."), verwirft .NET die Basis und ruft die fremde Adresse ab.
            var fetch = source.Host == office.Host && source.Port == office.Port && source.Scheme == office.Scheme
                ? source : new Uri(office.GetLeftPart(UriPartial.Authority) + source.PathAndQuery);
            if (fetch.Scheme != office.Scheme || fetch.Host != office.Host || fetch.Port != office.Port)
            {
                log.LogWarning("ONLYOFFICE-Callback: Ergebnis-Adresse liegt nicht beim eingestellten Office-Server, abgelehnt");
                return Answer(1);
            }
            // Ohne JWT-Geheimnis ist der Callback-Inhalt nicht signiert: dann nur Ergebnisse aus dem Cache des Document
            // Servers, nicht beliebige (interne) Pfade des Office-Servers
            if (string.IsNullOrEmpty(secret) && !fetch.AbsolutePath.Contains("/cache/files/", StringComparison.Ordinal))
            {
                log.LogWarning("ONLYOFFICE-Callback ohne JWT: Ergebnis liegt nicht unter /cache/files/, abgelehnt");
                return Answer(1);
            }
            // Erst herunterladen (verschluesselt in einen Blob), dann entscheiden, wohin - ohne die Datei im Speicher zu halten
            using var client = clients.CreateClient("office-download");
            BlobInfo blob;
            try
            {
                using var res = await client.GetAsync(fetch, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
                if (!res.IsSuccessStatusCode) { log.LogWarning("ONLYOFFICE-Ergebnis {Url}: {Status}", fetch, (int)res.StatusCode); return Answer(1); }
                if (res.Content.Headers.ContentLength > 512L * 1024 * 1024) return Answer(1);
                await using var result = await res.Content.ReadAsStreamAsync(http.RequestAborted);
                blob = await r.Space.Db.Blobs.WriteAsync(result, 512L * 1024 * 1024, () => new IOException("Zu groß."), http.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException) { log.LogWarning("ONLYOFFICE-Ergebnis {Url}: {Error}", fetch, ex.Message); return Answer(1); }
            // Inzwischen anderswo geaendert (WebDAV, Handy, Nextcloud-Client)? Dann nicht ueberschreiben, sondern daneben legen.
            var db = r.Space.Db;
            var current = FileStore.Get(db, r.File.Path)!;
            var path = r.File.Path;
            if (current.Etag != r.Grant.E && (!Written.TryGetValue(key, out var mine) || mine != current.Etag))
            {
                var ext = Path.GetExtension(path);
                var stem = path[..^ext.Length];
                path = FileStore.FreeName(db, stem + " (Konflikt)" + ext);
                log.LogInformation("ONLYOFFICE: {Path} wurde inzwischen geändert - gespeichert als {Copy}", r.File.Path, path);
            }
            try { FileStore.PutBlob(db, path, blob, current.Mime ?? DavServer.Mime(path)); }
            catch (QuotaExceededException) { return Answer(1); }
            if (path == r.File.Path) Written[key] = FileStore.Get(db, path)!.Etag;
            FileStore.Used(db, r.File.FileId);
            return Answer(0);
        });
    }

    /// <summary>Verbindung und Geheimnis pruefen: api.js da, CommandService "version" mit signierter Anfrage.</summary>
    public static async Task<(bool Ok, string? Error, string? Version)> Check(IHttpClientFactory clients, string baseUrl, string? secret, string? header, CancellationToken ct)
    {
        var b = baseUrl.TrimEnd('/');
        using var client = clients.CreateClient("office");
        try
        {
            var payload = new JsonObject { ["c"] = "version" };
            var req = new HttpRequestMessage(HttpMethod.Post, b + "/coauthoring/CommandService.ashx");
            var body = new JsonObject { ["c"] = "version" };
            if (!string.IsNullOrEmpty(secret))
            {
                var token = Jwt.Sign(payload, secret);
                body["token"] = token;
                req.Headers.TryAddWithoutValidation(string.IsNullOrEmpty(header) ? "Authorization" : header,
                    "Bearer " + Jwt.Sign(new JsonObject { ["payload"] = JsonNode.Parse(payload.ToJsonString()) }, secret));
            }
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await client.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            var answer = JsonNode.Parse(text);
            var error = (int?)answer?["error"] ?? -1;
            return error switch
            {
                0 => (true, null, (string?)answer?["version"]),
                6 => (false, string.IsNullOrEmpty(secret)
                    ? "Der Document Server verlangt ein JWT-Geheimnis (JWT_SECRET des Containers) – bitte eintragen."
                    : "Das JWT-Geheimnis passt nicht zum Document Server.", null),
                _ => (false, $"Der Document Server antwortet unerwartet ({text[..Math.Min(text.Length, 120)]}).", null),
            };
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
        {
            return (false, $"{b}/coauthoring/CommandService.ashx antwortet nicht wie ONLYOFFICE: {ex.Message}", null);
        }
    }
}
