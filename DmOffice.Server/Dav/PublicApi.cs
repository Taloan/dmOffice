using DmOffice.Core;
using Microsoft.AspNetCore.DataProtection;

namespace DmOffice.Dav;

/// <summary>
/// Oeffentliche Links ohne Anmeldung: /s/{token} ist die Seite (Oberflaeche), /api/public/{token} die Schnittstelle.
/// Schutz: Tokens mit 144 Bit Zufall, nur ihr Hash im Index; Anfragen je Adresse gebremst (Program.cs, Richtlinie
/// "public"); falsche Tokens und Passwoerter zaehlen wie falsche Anmeldungen (Auth.Failed). Pfade unter dem Link werden
/// normalisiert (kein ".."), muessen unter der freigegebenen Datei liegen und nie im notizi-Ordner.
/// </summary>
public static class PublicApi
{
    public record AuthBody(string Password);
    public record UploadBody(string? Name, long Size);

    private sealed record Access(UserSpace Space, FileLink Link, FileEntry Root, string Protected)
    {
        public UserDb Db => Space.Db;
        public bool Hidden(string path) => FileStore.IsSystem(path, Protected);
    }

    private static string Ip(HttpContext http) => "link-ip:" + Auth.IpKey(http.Connection.RemoteIpAddress);

    private static IResult Gone(string message, int status = 404) => Results.Json(new { error = message }, statusCode: status);

    /// <summary>Token pruefen und alles aufloesen; Fehler als fertige Antwort.</summary>
    private static (Access? Access, IResult? Error) Resolve(HttpContext http, string token, bool needPassword = true)
    {
        var ip = Ip(http);
        if (Auth.Blocked(ip)) return (null, Gone("Zu viele Versuche. Bitte später noch einmal.", 429));
        var hit = token.Length is >= 16 and <= 64 ? http.RequestServices.GetRequiredService<LinkIndex>().Find(token) : null;
        UserSpace? space = null;
        FileLink? link = null;
        if (hit is { } h)
        {
            try { space = http.RequestServices.GetRequiredService<Spaces>().Get(h.Owner); } catch (KeyNotFoundException) { }
            link = space == null ? null : Links.Get(space.Db, h.Id);
        }
        // Token vergleichen (der Index kennt nur Hashes; so faellt auch ein Hash-Zufallstreffer auf)
        if (space == null || link == null || !CryptographicEquals(link.Token, token))
        {
            Auth.Failed(ip);
            return (null, Gone("Diesen Link gibt es nicht (mehr)."));
        }
        if (FileStore.FilesOff(space.Db)) return (null, Gone("Diesen Link gibt es nicht (mehr)."));
        if (link.Expired(DateTime.UtcNow)) return (null, Gone("Dieser Link ist abgelaufen.", 410));
        if (link.UsedUp) return (null, Gone("Dieser Link ist abgelaufen – er wurde so oft abgerufen wie erlaubt.", 410));
        var root = FileStore.ProtectedRoot(space.Db);
        var entry = FileStore.ById(space.Db, link.FileId);
        if (entry == null || FileStore.InRoot(entry.Path, root) || FileStore.IsSystem(space.Db, entry.Path)) return (null, Gone("Diesen Link gibt es nicht (mehr)."));
        if (needPassword && link.HasPassword && !Unlocked(http, space.Db, link))
            return (null, Results.Json(new { error = "Passwort nötig.", needsPassword = true, name = entry.Name }, statusCode: 401));
        return (new Access(space, link, entry, root), null);
    }

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    private static IDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("dmoffice.files.link");

    private static string CookieName(FileLink link) => "dml_" + link.Id;

    /// <summary>Passwort einmal richtig eingegeben: Cookie fuer diesen Link (einen Tag), wird ungueltig, wenn sich das Passwort aendert.</summary>
    private static bool Unlocked(HttpContext http, UserDb db, FileLink link)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName(link), out var value)) return false;
        try
        {
            var parts = Protector(http).Unprotect(value).Split('|');
            return parts.Length == 3 && parts[0] == link.Id && long.TryParse(parts[1], out var until) && until > DateTime.UtcNow.Ticks
                   && parts[2] == Stamp(db, link);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    private static string Stamp(UserDb db, FileLink link) => LinkIndex.Hash(Links.PasswordHash(db, link.Id) ?? "")[..16];

    /// <summary>Pfad unter dem Link: normalisiert, darunter, nicht geschuetzt, vorhanden.</summary>
    private static FileEntry Under(Access a, string? rel)
    {
        var r = FileStore.Normalize(rel ?? "");
        if (r.Length == 0) return a.Root;
        if (!a.Root.IsDir) throw new KeyNotFoundException("Nicht gefunden.");
        var full = a.Root.Path.Length == 0 ? r : a.Root.Path + "/" + r;
        if (a.Hidden(full)) throw new KeyNotFoundException("Nicht gefunden.");
        return FileStore.Get(a.Db, full) ?? throw new KeyNotFoundException("Nicht gefunden.");
    }

    private static string Rel(Access a, string path) => path == a.Root.Path ? "" : a.Root.Path.Length == 0 ? path : path[(a.Root.Path.Length + 1)..];

    private static object Item(Access a, FileEntry f) => new { path = Rel(a, f.Path), f.Name, f.IsDir, f.Size, f.Mime, f.Modified, f.Etag };

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/public/{token}").RequireRateLimiting("public");

        api.MapGet("/", async (string token, string? path, HttpContext http) =>
        {
            var (a, error) = Resolve(http, token);
            if (a == null) return error!;
            var drop = a.Link.Mode == "drop";
            // Einwurf-Modus: der Inhalt bleibt verborgen. "path" ignorieren und kein "item" liefern - sonst
            // verriete die Antwort (gefunden/404, Groesse, Datum), welche Dateien im Ordner liegen.
            var f = drop ? a.Root : Under(a, path);
            // Office-Ansicht nur bei Links "nur ansehen": welche Endungen der Office-Server zeigen kann
            var office = a.Link.Mode == "read" ? await DmOffice.Office.WopiApi.PublicExtensions(http) : [];
            return Results.Ok(new
            {
                name = a.Root.Name.Length == 0 ? "Dateien" : a.Root.Name,
                owner = a.Space.User.DisplayName,
                mode = a.Link.Mode,
                expires = a.Link.Expires,
                isDir = a.Root.IsDir,
                path = drop ? "" : Rel(a, f.Path),
                item = drop ? null : Item(a, f),
                office,
                items = !f.IsDir || drop ? [] : FileStore.Children(a.Db, f.Path).Where(c => !a.Hidden(c.Path)).Select(c => Item(a, c)).ToList(),
            });
        });

        api.MapPost("/auth", async (string token, AuthBody body, HttpContext http) =>
        {
            var (a, error) = Resolve(http, token, needPassword: false);
            if (a == null) return error!;
            var key = "link:" + a.Link.Id;
            if (Auth.Blocked(key) || Auth.Blocked(Ip(http))) return Gone("Zu viele Versuche. Bitte später noch einmal.", 429);
            var ok = await Passwords.VerifyLimitedAsync(body.Password ?? "", Links.PasswordHash(a.Db, a.Link.Id), http.RequestAborted);
            if (ok == null) return Gone("Der Server ist gerade ausgelastet. Bitte gleich noch einmal versuchen.", 503);
            if (ok == false)
            {
                Auth.Failed(key);
                Auth.Failed(Ip(http));
                return Gone("Falsches Passwort.", 403);
            }
            Auth.Succeeded(key);
            var until = DateTime.UtcNow.AddDays(1);
            http.Response.Cookies.Append(CookieName(a.Link), Protector(http).Protect($"{a.Link.Id}|{until.Ticks}|{Stamp(a.Db, a.Link)}"), new CookieOptions
            {
                HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/api/public/" + token, Expires = until,
            });
            return Results.Ok();
        });

        api.MapGet("/download", (string token, string? path, bool? inline, HttpContext http) =>
        {
            var (a, error) = Resolve(http, token);
            if (a == null) return error!;
            if (a.Link.Mode == "drop") return Gone("Nur zum Hochladen.", 403);
            var f = Under(a, path);
            if (f.IsDir) throw new ArgumentException("Ordner als ZIP herunterladen.");
            // Jeder Abruf zaehlt, auch Ansehen im Browser - sonst liesse sich die Grenze mit inline=true umgehen
            if (!Links.Take(a.Db, a.Link.Id)) return Gone("Dieser Link ist abgelaufen – er wurde so oft abgerufen wie erlaubt.", 410);
            return FilesApi.Send(http, a.Db, f, inline == true);
        });

        // Office-Datei im Betrachter des Office-Servers ansehen - nur bei Links "nur ansehen", nie bearbeiten. Zaehlt als Abruf.
        api.MapPost("/office", async (string token, string? path, HttpContext http) =>
        {
            var (a, error) = Resolve(http, token);
            if (a == null) return error!;
            if (a.Link.Mode != "read") return Gone("Hier gibt es keine Office-Ansicht.", 403);
            var f = Under(a, path);
            if (f.IsDir) throw new ArgumentException("Kein Dokument.");
            return await DmOffice.Office.WopiApi.PublicView(http, a.Space, a.Link, f,
                () => Links.Take(a.Db, a.Link.Id) ? null : Gone("Dieser Link ist abgelaufen – er wurde so oft abgerufen wie erlaubt.", 410));
        });

        api.MapGet("/zip",async (string token, string? path, HttpContext http) =>
        {
            var (a, error) = Resolve(http, token);
            if (a == null) { await error!.ExecuteAsync(http); return; }
            if (a.Link.Mode == "drop") { await Gone("Nur zum Hochladen.", 403).ExecuteAsync(http); return; }
            var f = Under(a, path);
            if (!Links.Take(a.Db, a.Link.Id)) { await Gone("Dieser Link ist abgelaufen – er wurde so oft abgerufen wie erlaubt.", 410).ExecuteAsync(http); return; }
            await Zip.Write(http, a.Db, FileStore.Parent(f.Path) ?? "", [f.Path], a.Hidden, (f.Name.Length == 0 ? "Dateien" : f.Name) + ".zip", () => { });
        });

        api.MapGet("/thumb", async (string token, string? path, int? size, string? v, HttpContext http) =>
        {
            var (a, error) = Resolve(http, token);
            if (a == null) return error!;
            if (a.Link.Mode == "drop") return Results.NotFound();
            // Mit Abrufgrenze nur kleine Vorschaubilder - ein 1600-px-Bild waere sonst ein Download, der nicht zaehlt
            if (a.Link.MaxDownloads is > 0) size = 256;
            return await FilesApi.SendThumb(http, a.Db, Under(a, path), size, v);
        });

        // Hochladen in einen Ordner-Link ("upload", "drop"): nie ueberschreiben - belegte Namen bekommen eine Nummer
        api.MapPost("/upload", async (string token, string? path, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            var (a, dir, error) = UploadTarget(http, token, path);
            if (a == null) return error!;
            var names = new List<string>();
            await foreach (var file in MultipartUpload.Files(request, ct))
            {
                if (FileStore.Normalize(file.FileName).Length == 0) continue;
                // Grenzen gegen einen anonymen Link-Gast: nicht beliebig viele Dateien in einer Anfrage
                if (names.Count >= 1000) return Gone("Zu viele Dateien in einer Anfrage.", 400);
                try { names.Add(await StoreNew(a, dir!, file.FileName, file.Body, ct)); }
                catch (QuotaExceededException) { return Gone("Der Speicher des Empfängers ist voll.", 507); }
            }
            // Im Einwurf-Modus ("drop") bleibt der Ordnerinhalt verborgen - auch die (umbenannten) Namen nicht zurueckgeben,
            // sonst liesse sich ueber "X (2).pdf" erkennen, was schon im Ordner liegt.
            return Results.Ok(a.Link.Mode == "drop" ? new { files = (List<string>?)null, count = names.Count } : new { files = (List<string>?)names, count = names.Count });
        }).DisableAntiforgery();

        // In Stuecken (Version 2): wie /api/files/uploads, ohne Groessengrenze je Datei. Die Teile liegen beim Besitzer
        // des Links, die Sitzung gehoert zu diesem Link; abschliessen legt die Datei wie oben an (nie ueberschreiben).
        api.MapPost("/uploads", (string token, UploadBody body, HttpContext http) =>
        {
            var (a, _, error) = UploadTarget(http, token, null);
            return a == null ? error! : FilesApi.StartSession(a.Db, body.Name, body.Size, null, a.Link.Id);
        });
        api.MapGet("/uploads/{id}", (string token, string id, HttpContext http) =>
        {
            var (a, _, error) = UploadTarget(http, token, null);
            return a == null ? error! : FilesApi.SessionStatus(a.Db, id, a.Link.Id);
        });
        api.MapPut("/uploads/{id}", async (string token, string id, long offset, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            var (a, _, error) = UploadTarget(http, token, null);
            return a == null ? error! : await FilesApi.PutChunk(a.Db, id, a.Link.Id, offset, request.Body, ct);
        });
        api.MapPost("/uploads/{id}/finish", async (string token, string id, string? path, HttpContext http, CancellationToken ct) =>
        {
            var (a, dir, error) = UploadTarget(http, token, path);
            if (a == null) return error!;
            var session = FilesApi.RequireComplete(a.Db, id, a.Link.Id);
            string name;
            try { await using (var body = FilesApi.OpenSession(a.Db, session)) name = await StoreNew(a, dir!, session.Name, body, ct); }
            catch (QuotaExceededException) { return Gone("Der Speicher des Empfängers ist voll.", 507); }
            FilesApi.DropSession(a.Db, session);
            // Im Einwurf-Modus bleibt der Ordnerinhalt verborgen - auch der (umbenannte) Name
            return Results.Ok(a.Link.Mode == "drop" ? new { files = (string[]?)null, count = 1 } : new { files = (string[]?)[name], count = 1 });
        });
        api.MapDelete("/uploads/{id}", (string token, string id, HttpContext http) =>
        {
            var (a, _, error) = UploadTarget(http, token, null);
            return a == null ? error! : FilesApi.CancelSession(a.Db, id, a.Link.Id);
        });
    }

    /// <summary>Darf ueber diesen Link hochgeladen werden, und in welchen Ordner?</summary>
    private static (Access? Access, FileEntry? Dir, IResult? Error) UploadTarget(HttpContext http, string token, string? path)
    {
        var (a, error) = Resolve(http, token);
        if (a == null) return (null, null, error);
        if (a.Link.Mode == "read" || !a.Root.IsDir) return (null, null, Gone("Hochladen ist hier nicht erlaubt.", 403));
        var dir = a.Link.Mode == "drop" ? a.Root : Under(a, path);
        if (!dir.IsDir) throw new ArgumentException("Kein Ordner.");
        // Gueltiger Upload-Link: grosse Dateien kommen in vielen Teilen - nicht als Anfragen ohne Anmeldung bremsen
        http.Items[AnonThrottle.TrustedItem] = true;
        return (a, dir, null);
    }

    /// <summary>Eine Datei neu anlegen (Name darf Unterordner enthalten); liefert den Namen, den sie bekommen hat.</summary>
    private static async Task<string> StoreNew(Access a, FileEntry dir, string fileName, Stream body, CancellationToken ct)
    {
        var rel = FileStore.Normalize(fileName);
        // Einwerfen („drop“): nur der Dateiname, keine Unterordner - sonst verriete die Antwort (Fehler oder nicht), was im
        // verborgenen Ordner schon liegt (etwa „Vertrag.pdf/x“ scheitert, wenn es Vertrag.pdf gibt)
        if (a.Link.Mode == "drop") rel = rel[(rel.LastIndexOf('/') + 1)..];
        if (rel.Length == 0) throw new ArgumentException("Dateiname fehlt.");
        // Keine tiefen Pfade und keine endlosen Namen aus einer anonymen Anfrage
        if (rel.Count(c => c == '/') > 16 || rel.Length > 255) throw new ArgumentException("Dateiname oder Pfad zu lang.");
        var full = dir.Path.Length == 0 ? rel : dir.Path + "/" + rel;
        if (a.Hidden(full)) throw new KeyNotFoundException("Nicht gefunden.");
        if (FileStore.Parent(full) is { Length: > 0 } parent) FileStore.EnsureDir(a.Db, parent);
        // Art nur nach Endung - nie, was der Hochladende behauptet
        full = await FileStore.PutNewAsync(a.Db, full, body, DavServer.Mime(full), ct);
        return full[(full.LastIndexOf('/') + 1)..];
    }
}
