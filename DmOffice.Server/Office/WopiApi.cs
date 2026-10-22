using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.AspNetCore.Http.Features;

namespace DmOffice.Office;

/// <summary>
/// dmOffice als WOPI-Host: Collabora Online/CODE (und ONLYOFFICE mit eingeschaltetem WOPI) bearbeiten Dateien der Ablage.
/// /wopi/files/{id} spricht nur mit dem Office-Server, Zugang allein ueber das access_token (nie das Anmelde-Cookie).
/// Gespeichert wird ueber FileStore - fruehere Versionen, Papierkorb, Volltext und Vorschaubilder greifen wie sonst.
/// Der notizi-Abgleichordner bleibt aussen vor.
/// </summary>
public static class WopiApi
{
    public record OpenBody(string Path, bool? View);
    public record NewBody(string? Dir, string Kind, string Name);
    /// <param name="JwtSecret">null: bleibt wie es ist; leer: loeschen</param>
    public record AdminBody(string? Url, string? WopiBase, bool Force = false, string? JwtSecret = null, string? JwtHeader = null);

    // ---------------- Hilfen ----------------

    private sealed record Target(WopiGrant Grant, string Token, UserSpace Space, FileEntry File)
    {
        public UserDb Db => Space.Db;
    }

    /// <summary>WOPI-Antworten mit Namen wie in der Vorgabe (PascalCase) - nicht camelCase wie der Rest der Schnittstelle.</summary>
    private static readonly JsonSerializerOptions Pascal = new() { PropertyNamingPolicy = null };

    private static IResult Status(int code, string? message = null) =>
        message == null ? Results.StatusCode(code) : Results.Json(new { error = message }, statusCode: code);

    /// <summary>Token, Datei-Id, Benutzer, Link, Datei und Proof pruefen. Fehler als fertige WOPI-Antwort.</summary>
    private static async Task<(Target? Target, IResult? Error)> Resolve(HttpContext http, string id)
    {
        var services = http.RequestServices;
        var token = http.Request.Query["access_token"].ToString();
        var grant = WopiTokens.Read(services, token);
        if (grant == null || id != WopiTokens.FileId(grant.U, grant.F)) return (null, Status(401));
        var user = services.GetRequiredService<UserStore>().ById(grant.V ?? grant.U);
        if (user == null || user.Stamp != grant.S) return (null, Status(401));
        UserSpace space;
        try { space = services.GetRequiredService<Spaces>().Get(grant.U); }
        catch (KeyNotFoundException) { return (null, Status(401)); }
        if (FileStore.FilesOff(space.Db)) return (null, Status(404));
        var file = FileStore.ById(space.Db, grant.F);
        if (file == null || file.IsDir || FileStore.InRoot(file.Path, FileStore.ProtectedRoot(space.Db))) return (null, Status(404));
        if (grant.L != null && !LinkAllows(space.Db, grant.L, file)) return (null, Status(401));
        // Mit mir geteilte Datei: nur solange es die Freigabe gibt, schreiben nur mit Bearbeitungsrecht
        if (grant.V != null)
        {
            var (see, write) = SharedFiles.Allows(services, grant.V, grant.U, file);
            if (!see) return (null, Status(401));
            grant = grant with { W = grant.W && write };
        }
        if (!await ProofOk(http, grant, token)) return (null, Status(500, "Proof-Key-Prüfung fehlgeschlagen."));
        // Gueltiges Token: der Office-Server arbeitet fuer einen angemeldeten Benutzer - nicht bremsen
        http.Items[AnonThrottle.TrustedItem] = true;
        return (new Target(grant, token, space, file), null);
    }

    /// <summary>Ansehen ueber einen oeffentlichen Link: nur solange es den Link ("read") gibt und die Datei darunter liegt.</summary>
    public static bool LinkAllows(UserDb db, string linkId, FileEntry file)
    {
        var link = Links.Get(db, linkId);
        var root = link == null ? null : FileStore.ById(db, link.FileId);
        return link != null && link.Mode == "read" && !link.Expired(DateTime.UtcNow) && !link.UsedUp && root != null
               && (root.FileId == file.FileId || root.IsDir && (root.Path.Length == 0 || file.Path.StartsWith(root.Path + "/", StringComparison.Ordinal)));
    }

    /// <summary>X-WOPI-Proof pruefen, wenn der Office-Server Schluessel veroeffentlicht. Adresse: die WOPISrc-Basis aus dem Token
    /// plus Pfad und Abfrage, wie sie ankommen - so stoeren Proxys nicht, die Host oder Schema umschreiben.</summary>
    private static async Task<bool> ProofOk(HttpContext http, WopiGrant grant, string token)
    {
        var discovery = http.RequestServices.GetRequiredService<OfficeDiscovery>();
        var d = await discovery.Get(http.RequestAborted);
        // Fail-closed: hat dieser Office-Server schon Proof-Keys geliefert, ist eine fehlende Discovery
        // (nicht erreichbar, absichtlich gestoert) kein Freibrief. Nur Server ganz ohne Proof-Keys gehen ohne.
        if (d?.ProofKey == null) return !discovery.ProofExpected;
        var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } r ? r : http.Request.PathBase + http.Request.Path + http.Request.QueryString;
        var url = grant.B.TrimEnd('/') + raw;
        var h = http.Request.Headers;
        string? Header(string name) => h.TryGetValue(name, out var v) ? v.ToString() : null;
        bool Check(Discovery x) => WopiProof.Valid(x, token, url, Header("X-WOPI-TimeStamp"), Header("X-WOPI-Proof"), Header("X-WOPI-ProofOld"), DateTime.UtcNow);
        if (Check(d)) return true;
        // Vielleicht neue Schluessel: frisch holen - hoechstens einmal je Minute, und die letzte gute Discovery
        // bleibt als Rueckfall. Sonst koennte jede Anfrage mit falschem Proof einen Abruf erzwingen.
        if (discovery.RefreshForProof())
        {
            var fresh = await discovery.Get(http.RequestAborted);
            if (fresh != null && (fresh.ProofKey == null ? !discovery.ProofExpected : Check(fresh))) return true;
        }
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Wopi")
            .LogWarning("WOPI-Proof passt nicht zu {Url} - stimmt die WOPISrc-Adresse (Einstellung Office) mit der überein, unter der der Office-Server dmOffice aufruft?", url.Split('?')[0]);
        return false;
    }

    private static string Time(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

    private static string Ext(string name) => Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

    /// <summary>Die WOPISrc-Basis: eingestellt, sonst die Adresse, unter der der Browser dmOffice gerade aufruft.</summary>
    private static string BaseOf(HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<ServerConfig>().Current.OfficeWopiBase;
        return !string.IsNullOrWhiteSpace(configured) ? configured.Trim().TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
    }

    private static string OriginOf(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    /// <summary>Gaeste ueber einen oeffentlichen Link: kuerzer gueltige Tokens (sonst die vollen zehn Stunden).</summary>
    internal static TimeSpan? GuestLifetime(WopiGrant grant) => grant.L != null ? TimeSpan.FromHours(2) : null;

    /// <summary>Alles fuer den Editor im Browser: Adresse fuer das iframe (per POST) und das Token.</summary>
    private static object Session(HttpContext http, Discovery d, WopiGrant grant, FileEntry f, bool write)
    {
        if (d.Native)
        {
            var config = http.RequestServices.GetRequiredService<ServerConfig>();
            return new
            {
                api = config.Current.OfficeUrl!.TrimEnd('/') + "/web-apps/apps/api/documents/api.js",
                config = OnlyOffice.Config(http, grant, f, config.OfficeJwtSecret()),
                name = f.Name, path = f.Path, write, product = d.Product,
            };
        }
        var ext = Ext(f.Name);
        var urlsrc = write ? d.Edit[ext] : d.View.GetValueOrDefault(ext) ?? d.Edit[ext];
        var (token, expires) = WopiTokens.Issue(http.RequestServices, grant, GuestLifetime(grant));
        var src = grant.B + "/wopi/files/" + WopiTokens.FileId(grant.U, grant.F);
        return new
        {
            url = Discovery.ActionUrl(urlsrc, src, "de"),
            token,
            ttl = expires.ToUnixTimeMilliseconds(),
            name = f.Name,
            path = f.Path,
            write,
            product = d.Product,
        };
    }

    /// <summary>Endungen, die in der Oberflaeche zum Office-Server gehen: Office-Dateien, die die Discovery kennt.</summary>
    private static (string[] Edit, string[] View) Extensions(Discovery d) =>
        (d.Edit.Keys.Where(Discovery.OfficeExtensions.Contains).Order().ToArray(),
         d.View.Keys.Where(e => Discovery.OfficeExtensions.Contains(e) || e == "pdf").Order().ToArray());

    /// <summary>Endungen, die die oeffentliche Seite im Office-Betrachter zeigen kann (leer ohne Office-Server).</summary>
    public static async Task<string[]> PublicExtensions(HttpContext http)
    {
        var discovery = http.RequestServices.GetRequiredService<OfficeDiscovery>();
        if (!discovery.Configured) return [];
        var d = await discovery.Get(http.RequestAborted);
        return d == null ? [] : d.View.Keys.Where(Discovery.OfficeExtensions.Contains).Order().ToArray();
    }

    /// <summary>Ansehen ueber einen oeffentlichen Link (nur "read", nur Office-Arten). Fuer PublicApi.</summary>
    public static async Task<IResult> PublicView(HttpContext http, UserSpace space, FileLink link, FileEntry f, Func<IResult?> take)
    {
        var d = await http.RequestServices.GetRequiredService<OfficeDiscovery>().Get(http.RequestAborted);
        var ext = Ext(f.Name);
        if (d == null || f.IsDir || !Discovery.OfficeExtensions.Contains(ext) || !d.View.ContainsKey(ext))
            return Status(404, "Hier gibt es keine Office-Ansicht.");
        if (take() is { } used) return used;
        var grant = new WopiGrant(space.User.Id, f.FileId, false, BaseOf(http), OriginOf(http), space.User.Stamp, link.Id, "Gast");
        return Results.Ok(Session(http, d, grant, f, false));
    }

    // ---------------- Endpunkte ----------------

    public static void Map(WebApplication app)
    {
        var wopi = app.MapGroup("/wopi/files/{id}").AllowAnonymous();

        // CheckFileInfo
        wopi.MapGet("", async (string id, HttpContext http) =>
        {
            var (t, error) = await Resolve(http, id);
            if (t == null) return error!;
            var f = t.File;
            var guest = t.Grant.L != null;
            return Results.Json(new Dictionary<string, object?>
            {
                ["BaseFileName"] = f.Name,
                ["OwnerId"] = t.Grant.U,
                ["UserId"] = guest ? "guest-" + t.Grant.L : t.Grant.U,
                ["UserFriendlyName"] = t.Grant.N,
                ["IsAnonymousUser"] = guest,
                ["Size"] = f.Size,
                ["Version"] = f.Etag.Trim('"'),
                ["LastModifiedTime"] = Time(f.Modified),
                ["UserCanWrite"] = t.Grant.W,
                ["ReadOnly"] = !t.Grant.W,
                ["UserCanNotWriteRelative"] = !t.Grant.W,
                ["UserCanRename"] = false,
                ["SupportsRename"] = false,
                ["SupportsLocks"] = true,
                ["SupportsGetLock"] = true,
                ["SupportsExtendedLockLength"] = true,
                ["SupportsUpdate"] = true,
                ["PostMessageOrigin"] = t.Grant.O,
                ["EnableOwnerTermination"] = false,
                ["DisablePrint"] = false,
                ["HideExportOption"] = false,
                ["EnableShare"] = false,
            }, Pascal);
        });

        // GetFile
        wopi.MapGet("/contents", async (string id, HttpContext http) =>
        {
            var (t, error) = await Resolve(http, id);
            if (t == null) return error!;
            http.Response.Headers["X-WOPI-ItemVersion"] = t.File.Etag.Trim('"');
            http.Response.Headers.CacheControl = "no-store";
            return Results.Stream(FileStore.OpenRead(t.Db, t.File.Path), "application/octet-stream");
        });

        // PutFile
        wopi.MapPost("/contents", async (string id, HttpContext http) =>
        {
            var (t, error) = await Resolve(http, id);
            if (t == null) return error!;
            if (!t.Grant.W) return Status(401);
            var lockId = http.Request.Headers["X-WOPI-Lock"].ToString();
            var current = WopiLocks.Current(t.Grant.U, t.Grant.F);
            if (current != null && current != lockId) return LockConflict(http, current);
            // Konflikt erkennen: Collabora schickt mit, welchen Stand es geladen hat (LastModifiedTime aus CheckFileInfo).
            // Hat sich die Datei inzwischen anders geaendert (WebDAV, Handy, andere Sitzung), fragt Collabora den Benutzer.
            var stamp = http.Request.Headers["X-COOL-WOPI-Timestamp"].ToString() is { Length: > 0 } c ? c : http.Request.Headers["X-LOOL-WOPI-Timestamp"].ToString();
            if (stamp.Length > 0 && (!DateTime.TryParse(stamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var seen)
                                     || seen != DateTime.SpecifyKind(t.File.Modified, DateTimeKind.Utc)))
                return Results.Json(new { COOLStatusCode = 1010, LOOLStatusCode = 1010 }, Pascal, statusCode: 409);
            try { await FileStore.PutAsync(t.Db, t.File.Path, http.Request.Body, t.File.Mime ?? DavServer.Mime(t.File.Path), ct: http.RequestAborted); }
            catch (QuotaExceededException ex) { return Status(413, ex.Message); }
            var now = FileStore.Get(t.Db, t.File.Path)!;
            FileStore.Used(t.Db, now.FileId);
            http.Response.Headers["X-WOPI-ItemVersion"] = now.Etag.Trim('"');
            return Results.Json(new { LastModifiedTime = Time(now.Modified) }, Pascal);
        });

        // Lock, Unlock, RefreshLock, GetLock, PutRelativeFile - unterschieden an X-WOPI-Override
        wopi.MapPost("", async (string id, HttpContext http) =>
        {
            var (t, error) = await Resolve(http, id);
            if (t == null) return error!;
            var op = http.Request.Headers["X-WOPI-Override"].ToString().ToUpperInvariant();
            var lockId = http.Request.Headers["X-WOPI-Lock"].ToString();
            var current = WopiLocks.Current(t.Grant.U, t.Grant.F);
            switch (op)
            {
                case "GET_LOCK":
                    http.Response.Headers["X-WOPI-Lock"] = current ?? "";
                    return Results.Ok();
                case "PUT_USER_INFO":
                    return Results.Ok();
                case "LOCK" or "UNLOCK" or "REFRESH_LOCK" or "PUT_RELATIVE" when !t.Grant.W:
                    return Status(401);
                case "LOCK":
                {
                    if (lockId.Length is 0 or > 1024) return Status(400);
                    var old = http.Request.Headers["X-WOPI-OldLock"].ToString();
                    if (old.Length > 0)
                    {
                        // UnlockAndRelock
                        if (current != old) return LockConflict(http, current);
                    }
                    else if (current != null && current != lockId) return LockConflict(http, current);
                    WopiLocks.Set(t.Grant.U, t.Grant.F, lockId);
                    http.Response.Headers["X-WOPI-ItemVersion"] = t.File.Etag.Trim('"');
                    return Results.Ok();
                }
                case "REFRESH_LOCK":
                    if (current == null || current != lockId) return LockConflict(http, current);
                    WopiLocks.Set(t.Grant.U, t.Grant.F, lockId);
                    return Results.Ok();
                case "UNLOCK":
                    if (current == null || current != lockId) return LockConflict(http, current);
                    WopiLocks.Remove(t.Grant.U, t.Grant.F);
                    http.Response.Headers["X-WOPI-ItemVersion"] = t.File.Etag.Trim('"');
                    return Results.Ok();
                case "PUT_RELATIVE":
                    return await PutRelative(http, t);
                default:
                    return Status(501);
            }
        });

        // ---------------- Oberflaeche (angemeldet) ----------------

        var api = app.MapGroup("/api/office").RequireAuthorization();

        // Ist ein Office-Server eingestellt, und welche Arten kann er?
        api.MapGet("/config", async (HttpContext http, OfficeDiscovery discovery) =>
        {
            if (!discovery.Configured || FileStore.FilesOff(http.Space().Db)) return Results.Ok(new { enabled = false });
            var d = await discovery.Get(http.RequestAborted);
            if (d == null) return Results.Ok(new { enabled = false, error = "Der Office-Server ist gerade nicht erreichbar." });
            var (edit, view) = Extensions(d);
            return Results.Ok(new { enabled = true, product = d.Product, edit, view, create = OfficeTemplates.Kinds.Where(edit.Contains).ToArray() });
        });

        // Datei im Editor oeffnen: Adresse und Token fuer das iframe
        api.MapPost("/open", async (OpenBody body, HttpContext http, OfficeDiscovery discovery) =>
        {
            var space = http.Space();
            if (FileStore.FilesOff(space.Db)) return Status(403, "Die Dateiablage ist für dieses Konto abgeschaltet.");
            var loc = SharedFiles.Resolve(http, FileStore.Normalize(body.Path));
            if (loc.Virtual || loc.Protected(loc.Path)) return Status(404, "Datei nicht gefunden.");
            var f = FileStore.Get(loc.Db, loc.Path);
            if (f == null || f.IsDir) return Status(404, "Datei nicht gefunden.");
            var d = await discovery.Get(http.RequestAborted);
            if (d == null) return Status(503, discovery.Configured ? "Der Office-Server ist gerade nicht erreichbar." : "Kein Office-Server eingestellt.");
            var ext = Ext(f.Name);
            if (!Discovery.OfficeExtensions.Contains(ext) || !d.View.ContainsKey(ext)) return Status(400, "Diese Dateiart kann der Office-Server nicht öffnen.");
            var write = body.View != true && d.Edit.ContainsKey(ext) && loc.CanWrite;
            // In einem geteilten Ordner arbeitet der Office-Server in der Datei des Besitzers; V ist das Mitglied
            var grant = new WopiGrant(loc.Space.User.Id, f.FileId, write, BaseOf(http), OriginOf(http), space.User.Stamp, null, space.User.DisplayName,
                V: loc.Shared ? space.User.Id : null);
            if (!loc.Shared) FileStore.Used(space.Db, f.FileId);
            return Results.Ok(Session(http, d, grant, loc.Show(f), write));
        });

        // Neues leeres Dokument (docx, xlsx, pptx)
        api.MapPost("/new", (NewBody body, HttpContext http) =>
        {
            var space = http.Space();
            if (FileStore.FilesOff(space.Db)) return Status(403, "Die Dateiablage ist für dieses Konto abgeschaltet.");
            var kind = body.Kind.TrimStart('.').ToLowerInvariant();
            if (!OfficeTemplates.Kinds.Contains(kind)) return Status(400, "Unbekannte Dokumentart.");
            var name = (body.Name ?? "").Trim();
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\')) return Status(400, "Ungültiger Name.");
            if (!name.EndsWith("." + kind, StringComparison.OrdinalIgnoreCase)) name += "." + kind;
            var dir = FileStore.Normalize(body.Dir ?? "");
            var loc = SharedFiles.Resolve(http, FileStore.Normalize(dir.Length == 0 ? name : dir + "/" + name));
            if (loc.Protected(loc.Path)) return Status(404, "Ordner nicht gefunden.");
            try { loc = loc.Creatable(); }
            catch (UnauthorizedAccessException ex) { return Status(403, ex.Message); }
            var p = loc.Show(loc.Path);
            try { FileStore.Put(loc.Db, loc.Path, OfficeTemplates.Create(kind), DavServer.Mime(p), ifNoneMatch: "*"); }
            catch (Pim.PreconditionFailedException) { return Results.Json(new { error = "Gibt es schon.", conflict = true }, statusCode: 409); }
            catch (DavConflictException ex) { return Status(409, ex.Message); }
            catch (QuotaExceededException ex) { return Status(507, ex.Message); }
            return Results.Ok(new { path = p });
        });

        // ---------------- Verwaltung ----------------

        var admin = app.MapGroup("/api/admin/office").RequireAuthorization("admin");

        admin.MapGet("", async (ServerConfig config, OfficeDiscovery discovery, HttpContext http) =>
        {
            var s = config.Current;
            var jwt = !string.IsNullOrEmpty(s.OfficeJwtSecret);
            if (string.IsNullOrWhiteSpace(s.OfficeUrl))
                return Results.Ok(new { url = "", wopiBase = s.OfficeWopiBase ?? "", ok = false, jwt, jwtHeader = s.OfficeJwtHeader ?? "" });
            var (d, error) = await discovery.Fetch(s.OfficeUrl, http.RequestAborted);
            return Results.Ok(new
            {
                url = s.OfficeUrl, wopiBase = s.OfficeWopiBase ?? "", ok = d != null, error, jwt, jwtHeader = s.OfficeJwtHeader ?? "",
                product = d?.Product, native = d?.Native == true, proof = d?.ProofKey != null, edit = d == null ? null : Extensions(d).Edit,
            });
        });

        // Speichern: vorher pruefen; mit Force auch, wenn der Server gerade nicht antwortet. Leere Adresse schaltet ab.
        admin.MapPut("", async (AdminBody body, ServerConfig config, OfficeDiscovery discovery, HttpContext http) =>
        {
            var url = (body.Url ?? "").Trim().TrimEnd('/');
            var wopiBase = (body.WopiBase ?? "").Trim().TrimEnd('/');
            if (wopiBase.Length > 0 && (!Uri.TryCreate(wopiBase, UriKind.Absolute, out var wb) || wb.Scheme is not ("http" or "https")))
                return Status(400, "WOPI-Adresse: bitte mit https:// angeben, etwa https://office.example.org.");
            // Das Geheimnis signiert auch die Editor-Konfiguration, die jeder Betrachter (auch ein Gast) sieht -
            // ein kurzes liesse sich daraus offline erraten
            if (body.JwtSecret is { Length: > 0 } js && js.Trim().Length < 32)
                return Status(400, "Das JWT-Geheimnis muss mindestens 32 Zeichen lang sein (z. B. openssl rand -hex 32).");
            Discovery? d = null;
            var secret = body.JwtSecret?.Trim() ?? config.OfficeJwtSecret();
            var header = (body.JwtHeader ?? config.Current.OfficeJwtHeader ?? "").Trim();
            if (url.Length > 0)
            {
                string? error;
                (d, error) = await discovery.Fetch(url, http.RequestAborted, secret, header, useStored: false);
                if (d == null && !body.Force) return Status(400, error);
            }
            config.Update(s =>
            {
                s.OfficeUrl = url.Length == 0 ? null : url;
                s.OfficeWopiBase = wopiBase.Length == 0 ? null : wopiBase;
                s.OfficeJwtHeader = header.Length == 0 || header == "Authorization" ? null : header;
            });
            if (body.JwtSecret != null) config.SetOfficeJwtSecret(body.JwtSecret.Trim());
            discovery.Forget();
            return Results.Ok(new { ok = d != null, product = d?.Product, native = d?.Native == true, proof = d?.ProofKey != null, edit = d == null ? null : Extensions(d).Edit });
        });
    }

    private static IResult LockConflict(HttpContext http, string? current)
    {
        http.Response.Headers["X-WOPI-Lock"] = current ?? "";
        return Results.StatusCode(409);
    }

    /// <summary>X-WOPI-SuggestedTarget/RelativeTarget sind UTF-7 kodiert (WOPI-Vorgabe).</summary>
    private static string Utf7(string s)
    {
#pragma warning disable SYSLIB0001
        try { return Encoding.UTF7.GetString(Encoding.ASCII.GetBytes(s)); }
#pragma warning restore SYSLIB0001
        catch (Exception) { return s; }
    }

    /// <summary>PutRelativeFile ("Speichern unter", Export): neue Datei neben der alten, Antwort mit eigenem Token dafuer.</summary>
    private static async Task<IResult> PutRelative(HttpContext http, Target t)
    {
        var h = http.Request.Headers;
        var suggested = h["X-WOPI-SuggestedTarget"].ToString();
        var relative = h["X-WOPI-RelativeTarget"].ToString();
        if (suggested.Length > 0 == relative.Length > 0) return Status(501);
        var dir = FileStore.Parent(t.File.Path)!;
        string name;
        if (suggested.Length > 0)
        {
            var s = Utf7(suggested).Trim();
            name = s.StartsWith('.') ? Path.GetFileNameWithoutExtension(t.File.Name) + s : s;
        }
        else name = Utf7(relative).Trim();
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.Length == 0 || name is "." or ".." || name.Any(char.IsControl)) return Status(400);
        var path = FileStore.Normalize(dir.Length == 0 ? name : dir + "/" + name);
        if (FileStore.InRoot(path, FileStore.ProtectedRoot(t.Db))) return Status(400);
        if (suggested.Length > 0) path = FileStore.FreeName(t.Db, path);
        else if (FileStore.Get(t.Db, path) is { } existing)
        {
            var overwrite = string.Equals(h["X-WOPI-OverwriteRelativeTarget"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            var locked = existing.IsDir ? null : WopiLocks.Current(t.Grant.U, existing.FileId);
            if (!overwrite || existing.IsDir || locked != null)
            {
                http.Response.Headers["X-WOPI-ValidRelativeTarget"] = FileStore.FreeName(t.Db, path) is var free ? free[(free.LastIndexOf('/') + 1)..] : "";
                if (locked != null) http.Response.Headers["X-WOPI-Lock"] = locked;
                return Results.StatusCode(409);
            }
        }
        try { await FileStore.PutAsync(t.Db, path, http.Request.Body, DavServer.Mime(path), ct: http.RequestAborted); }
        catch (QuotaExceededException ex) { return Status(413, ex.Message); }
        var f = FileStore.Get(t.Db, path)!;
        var grant = t.Grant with { F = f.FileId };
        var (token, _) = WopiTokens.Issue(http.RequestServices, grant);
        var src = grant.B + "/wopi/files/" + WopiTokens.FileId(grant.U, f.FileId);
        return Results.Json(new { Name = f.Name, Url = src + "?access_token=" + Uri.EscapeDataString(token) }, Pascal);
    }
}
