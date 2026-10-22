using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Was die offiziellen Nextcloud-Clients (Desktop, Android, iOS) vor dem Abgleich erfragen:
/// Erkennung (/status.php), Faehigkeiten und Benutzer (OCS) und die Anmeldung im Browser
/// (Login Flow v2, fuer die Handy-Apps auch v1). Am Ende steht ein gewoehnliches App-Passwort;
/// den Abgleich selbst macht WebDAV unter /remote.php (DavServer).
/// </summary>
public static class NextcloudApi
{
    /// <summary>Versionsnummer, die wir als Nextcloud melden - neu genug, dass kein Client warnt.</summary>
    public const string Version = "30.0.5";

    /// <summary>Kennung dieser "Instanz" - Nextcloud haengt sie an jede Datei-Nummer (oc:id, OC-FileId).</summary>
    public const string InstanceId = "ocdmoffice00";

    /// <summary>Adressen, die Nextcloud-Clients kennen - nie mit der Oberflaeche (index.html) beantworten.</summary>
    public static bool Reserved(PathString path) =>
        path.StartsWithSegments("/status.php") || path.StartsWithSegments("/ocs") || path.StartsWithSegments("/remote.php")
        || path.StartsWithSegments("/index.php") || path.StartsWithSegments("/login/v2") || path.StartsWithSegments("/login/flow");

    public static void Map(WebApplication app)
    {
        app.MapGet("/status.php", () => Results.Json(new
        {
            installed = true,
            maintenance = false,
            needsDbUpgrade = false,
            version = Version + ".0",
            versionstring = Version,
            edition = "",
            productname = "dmOffice",
            extendedSupport = false
        }));

        // Faehigkeiten fragt der Client schon vor der Anmeldung - wie bei Nextcloud ohne Anmeldung lesbar
        foreach (var v in new[] { 1, 2 })
        {
            int ok = v == 1 ? 100 : 200;
            app.MapGet($"/ocs/v{v}.php/cloud/capabilities", () => Ocs(ok, Capabilities()));
            // Ein notizi-Zugang bekommt nur seinen Abgleichordner - keine Kontodaten (Mailadresse, Kontingent)
            app.MapGet($"/ocs/v{v}.php/cloud/user", (HttpContext http) =>
                http.User.FindFirst(Auth.ScopeClaim)?.Value == AppPasswords.ScopeNotizi ? Results.StatusCode(403) : Ocs(ok, UserData(http.Space())))
                .RequireAuthorization("dav");
        }

        // ---------------- Vorschaubilder ----------------
        // Der Windows-Client (virtuelle Dateien) holt sie fuer den Explorer mit
        // /index.php/core/preview?fileId=<oc:id>&x=&y=&a=true; die anderen Adressen fuer Handy-Apps und aeltere Clients
        app.MapGet("/index.php/core/preview", (HttpContext http, string? fileId, string? file, int? x, int? y) => Preview(http, fileId, file, x, y)).RequireAuthorization("dav");
        app.MapGet("/index.php/core/preview.png", (HttpContext http, string? fileId, string? file, int? x, int? y) => Preview(http, fileId, file, x, y)).RequireAuthorization("dav");
        app.MapGet("/index.php/apps/files/api/v1/thumbnail/{w:int}/{h:int}/{**path}", (HttpContext http, int w, int h, string path) => Preview(http, null, path, w, h)).RequireAuthorization("dav");

        // ---------------- Login Flow v2 (Desktop) ----------------
        foreach (var prefix in new[] { "/index.php/login/v2", "/login/v2" })
        {
            app.MapPost(prefix, (HttpContext http) =>
            {
                var flow = Flows.Start(DeviceName(http.Request.Headers.UserAgent.ToString()), v1: false);
                var origin = Origin(http);
                return Results.Json(new
                {
                    poll = new { token = flow.PollToken, endpoint = origin + "/index.php/login/v2/poll" },
                    login = origin + "/index.php/login/v2/flow/" + flow.LoginToken
                });
            }).RequireRateLimiting("public");
            app.MapPost(prefix + "/poll", async (HttpContext http) =>
            {
                var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync() : null;
                var token = form?["token"].ToString() ?? http.Request.Query["token"].ToString();
                // Erst nach der Freigabe, und nur einmal - danach ist das Passwort hier vergessen
                if (Flows.TakeResult(token) is not { } r) return Results.NotFound();
                return Results.Json(new { server = r.Server, loginName = r.LoginName, appPassword = r.AppPassword });
            });
        }
        app.MapGet("/index.php/login/v2/flow/{token}", (string token, HttpContext http) => FlowPage(http, token, null));
        app.MapPost("/index.php/login/v2/flow/{token}", (string token, HttpContext http, UserStore users, Spaces spaces) => Grant(http, token, users, spaces));

        // ---------------- Login Flow v1 (Handy-Apps, in einer eingebetteten Webansicht) ----------------
        app.MapGet("/index.php/login/flow", [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("public")] (HttpContext http) =>
        {
            var flow = Flows.Start(DeviceName(http.Request.Headers.UserAgent.ToString()), v1: true);
            return Results.Redirect("/index.php/login/v2/flow/" + flow.LoginToken);
        });
    }

    private static IResult Ocs(int statusCode, object data) => Results.Json(new
    {
        ocs = new { meta = new { status = "ok", statuscode = statusCode, message = "OK" }, data }
    });

    private static object Capabilities()
    {
        var parts = Version.Split('.').Select(int.Parse).ToArray();
        return new
        {
            version = new { major = parts[0], minor = parts[1], micro = parts[2], @string = Version, edition = "", extendedSupport = false },
            capabilities = new Dictionary<string, object>
            {
                ["core"] = new Dictionary<string, object> { ["pollinterval"] = 60, ["webdav-root"] = "remote.php/webdav" },
                // Ohne "chunking": Kleine Dateien kommen mit einem PUT. Grosse wuerde der Client in Stuecken
                // nach altem Verfahren schicken - das lehnt DavServer ab (noch nicht umgesetzt).
                ["dav"] = new Dictionary<string, object>(),
                ["files"] = new Dictionary<string, object>
                {
                    ["bigfilechunking"] = false,
                    ["undelete"] = false,
                    ["versioning"] = false,
                    ["blacklisted_files"] = new[] { ".htaccess" }
                }
            }
        };
    }

    private static object UserData(UserSpace space)
    {
        var used = UserStorage.Used(space.Db);
        var quota = FileStore.Quota(space.Db);
        return new Dictionary<string, object?>
        {
            ["id"] = space.User.Login,
            ["enabled"] = true,
            ["display-name"] = space.User.DisplayName,
            ["displayname"] = space.User.DisplayName,
            ["email"] = space.Settings.MailAccounts.FirstOrDefault()?.Email,
            ["quota"] = new
            {
                free = quota > 0 ? Math.Max(0, quota - used) : -3,
                used,
                total = quota > 0 ? quota : used,
                relative = quota > 0 ? Math.Round(100.0 * used / quota, 2) : 0,
                quota = quota > 0 ? quota : -3
            }
        };
    }

    /// <summary>
    /// Vorschaubild nach Nummer (oc:id wie "00000042ocdmoffice00" oder nur die Zahl) oder Pfad. Wie bei Nextcloud
    /// 404, wenn es keines gibt. Den Abgleichordner von notizi gibt es hier nicht (wie in /api/files und
    /// /remote.php), ein notizi-Zugang bekommt gar nichts.
    /// </summary>
    private static async Task<IResult> Preview(HttpContext http, string? fileId, string? file, int? x, int? y)
    {
        if (http.User.FindFirst(Auth.ScopeClaim)?.Value == AppPasswords.ScopeNotizi) return Results.StatusCode(403);
        var space = http.Space();
        var db = space.Db;
        if (FileStore.FilesOff(db)) return Results.StatusCode(403);
        // Auch in mit mir geteilten Ordnern (fileid mit Freigabe in den oberen Bits, Pfad unter "Mit mir geteilt") - nur mit Freigabe
        FileEntry? f;
        FileLoc? loc;
        if (!string.IsNullOrEmpty(fileId))
        {
            var digits = new string(fileId.TakeWhile(char.IsAsciiDigit).ToArray());
            var found = long.TryParse(digits, out var id) && id > 0 ? SharedFiles.ById(http, id) : null;
            (loc, f) = found is { } hit ? (hit.Loc, hit.File) : (null, null);
        }
        else if (!string.IsNullOrEmpty(file))
        {
            try { loc = SharedFiles.Resolve(http, FileStore.Normalize(file)); }
            catch (Exception e) when (e is ArgumentException or KeyNotFoundException) { return Results.NotFound(); }
            f = loc.Virtual ? null : FileStore.Get(loc.Db, loc.Path);
        }
        else return Results.BadRequest();
        if (f == null || loc == null || f.IsDir || loc.Protected(f.Path)) return Results.NotFound();
        db = loc.Db;

        // Nextcloud: ohne Angabe 32x32 - dafuer reicht die kleine Version
        var data = await Thumbs.Get(db, f, Math.Max(x ?? 32, y ?? 32), http.RequestAborted);
        if (data == null) return Results.NotFound();
        var mime = "image/webp";
        if (!http.Request.Headers.Accept.ToString().Contains("image/webp", StringComparison.OrdinalIgnoreCase))
        {
            if (Thumbs.Classic(data) is not { } c) return Results.NotFound();
            (data, mime) = c;
        }
        http.Response.Headers.CacheControl = "private, max-age=3600";
        http.Response.Headers.ETag = f.Etag.StartsWith('"') ? f.Etag : $"\"{f.Etag}\"";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Bytes(data, mime);
    }

    /// <summary>Basis-Adresse, wie der Client sie sieht (hinter einem Proxy aus X-Forwarded-*).</summary>
    private static string Origin(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";

    /// <summary>Name des App-Passworts aus dem User-Agent des Clients.</summary>
    internal static string DeviceName(string userAgent)
    {
        var ua = userAgent ?? "";
        string kind = ua.Contains("mirall", StringComparison.OrdinalIgnoreCase) ? "Desktop"
            : ua.Contains("android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : ua.Contains("iOS", StringComparison.Ordinal) || ua.Contains("iPhone", StringComparison.Ordinal) ? "iOS"
            : "";
        if (kind.Length > 0) return "Nextcloud " + kind;
        ua = ua.Trim();
        return ua.Length == 0 ? "Nextcloud-Client" : "Nextcloud-Client (" + (ua.Length > 40 ? ua[..40] + "…" : ua) + ")";
    }

    // ==================================================================
    //  Anmeldung im Browser
    // ==================================================================

    /// <summary>Laufende Anmeldungen - im Speicher, 20 Minuten gueltig. Nach einem Neustart neu anmelden.</summary>
    internal static class Flows
    {
        public sealed class Flow
        {
            public required string PollToken { get; init; }
            public required string LoginToken { get; init; }
            public required string Name { get; init; }
            public required bool V1 { get; init; }
            public DateTime Expires { get; } = DateTime.UtcNow.AddMinutes(20);
            public Result? Result { get; set; }
        }

        public sealed record Result(string Server, string LoginName, string AppPassword);

        private static readonly ConcurrentDictionary<string, Flow> ByPoll = new();
        private static readonly ConcurrentDictionary<string, Flow> ByLogin = new();

        public static Flow Start(string name, bool v1)
        {
            Cleanup();
            // Obergrenze gegen anonymes Zumuellen: so viele offene Flows kann es nicht geben
            if (ByPoll.Count > 10_000) throw new InvalidOperationException("Zu viele offene Anmeldungen. Bitte später erneut versuchen.");
            var f = new Flow { PollToken = Passwords.NewToken() + Passwords.NewToken(), LoginToken = Passwords.NewToken(), Name = name, V1 = v1 };
            ByPoll[f.PollToken] = f;
            ByLogin[f.LoginToken] = f;
            return f;
        }

        public static Flow? ByLoginToken(string token) =>
            ByLogin.TryGetValue(token ?? "", out var f) && f.Expires > DateTime.UtcNow ? f : null;

        public static Result? TakeResult(string token)
        {
            if (!ByPoll.TryGetValue(token ?? "", out var f) || f.Expires <= DateTime.UtcNow || f.Result == null) return null;
            ByPoll.TryRemove(f.PollToken, out _);
            ByLogin.TryRemove(f.LoginToken, out _);
            return f.Result;
        }

        public static void Done(Flow f, Result r)
        {
            f.Result = r;
            // v1 bekommt das Ergebnis sofort per Weiterleitung, v2 holt es per Abfrage ab
            ByLogin.TryRemove(f.LoginToken, out _);
            if (f.V1) ByPoll.TryRemove(f.PollToken, out _);
        }

        private static void Cleanup()
        {
            foreach (var (k, f) in ByPoll) if (f.Expires <= DateTime.UtcNow) ByPoll.TryRemove(k, out _);
            foreach (var (k, f) in ByLogin) if (f.Expires <= DateTime.UtcNow) ByLogin.TryRemove(k, out _);
        }
    }

    /// <summary>Angemeldet in der Weboberflaeche (Cookie) und die Anfrage kommt von hier, nicht von einer fremden Seite?</summary>
    private static UserRecord? CookieUser(HttpContext http, UserStore users)
    {
        if (http.User.Identity?.IsAuthenticated != true) return null;
        var origin = http.Request.Headers.Origin.ToString();
        if (http.Request.Method == "POST" && origin.Length > 0 && !string.Equals(origin, Origin(http), StringComparison.OrdinalIgnoreCase)) return null;
        return users.ById(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
    }

    private static IResult FlowPage(HttpContext http, string token, string? error)
    {
        var flow = Flows.ByLoginToken(token);
        var users = http.RequestServices.GetRequiredService<UserStore>();
        var me = CookieUser(http, users);
        string E(string s) => WebUtility.HtmlEncode(s);
        string body;
        if (flow == null)
            body = "<h1>Anmeldung abgelaufen</h1><p>Bitte im Programm die Anmeldung neu beginnen.</p>";
        else
        {
            // Auch angemeldet nur mit Passwort: sonst genuegte ein Klick auf einen untergeschobenen Link, um einem
            // fremden Programm ein App-Passwort zu geben (Phishing per Zustimmung)
            var fields = me != null
                ? $"""
                  <p>Angemeldet als <b>{E(me.DisplayName)}</b> ({E(me.Login)}). Zur Bestätigung bitte dein Passwort eingeben.</p>
                  <input type="hidden" name="login" value="{E(me.Login)}">
                  <label>Passwort<input name="password" type="password" autocomplete="current-password" required autofocus></label>
                  <label>Code (nur mit Zwei-Faktor-Anmeldung)<input name="code" inputmode="numeric" autocomplete="one-time-code"></label>
                  """
                : """
                  <label>Anmeldename<input name="login" autocomplete="username" required autofocus></label>
                  <label>Passwort<input name="password" type="password" autocomplete="current-password" required></label>
                  <label>Code (nur mit Zwei-Faktor-Anmeldung)<input name="code" inputmode="numeric" autocomplete="one-time-code"></label>
                  """;
            body = $"""
                <h1>Zugriff erlauben?</h1>
                <p><b>{E(flow.Name)}</b> möchte deine dmOffice-Dateien abgleichen. Es bekommt dafür ein eigenes
                App-Passwort, das du in den Einstellungen jederzeit widerrufen kannst.</p>
                <p class="warn">Nur erlauben, wenn du das Programm gerade selbst eingerichtet hast.</p>
                {(error != null ? $"<p class=\"err\">{E(error)}</p>" : "")}
                <form method="post">{fields}<button>Zugriff erlauben</button></form>
                """;
        }
        return Page(http, body, flow == null ? 404 : error != null ? 400 : 200);
    }

    private static IResult Page(HttpContext http, string body, int status = 200)
    {
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
        http.Response.Headers["X-Frame-Options"] = "DENY";
        http.Response.Headers.CacheControl = "no-store";
        var html = $$"""
            <!doctype html><html lang="de"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>dmOffice – Anmeldung</title>
            <style>
            body{font-family:system-ui,sans-serif;max-width:26rem;margin:3rem auto;padding:0 1rem;color:#1b1b1b;background:#fafafa}
            h1{font-size:1.4rem} label{display:block;margin:.8rem 0;font-size:.9rem}
            input{display:block;width:100%;box-sizing:border-box;padding:.5rem;margin-top:.25rem;font-size:1rem}
            button{margin-top:1rem;padding:.6rem 1.2rem;font-size:1rem;background:#0F6CBD;color:#fff;border:0;border-radius:4px}
            .warn{color:#8a5a00}.err{color:#b00020}
            </style>
            <body>{{body}}</body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }

    private static async Task<IResult> Grant(HttpContext http, string token, UserStore users, Spaces spaces)
    {
        var flow = Flows.ByLoginToken(token);
        if (flow == null) return FlowPage(http, token, null);

        UserRecord? user;
        {
            // Passwort (und Code) immer pruefen - auch wenn die Weboberflaeche angemeldet ist (gegen Phishing per Zustimmung).
            // Mit derselben Bremse wie beim Anmelden.
            var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync() : null;
            var login = form?["login"].ToString() ?? "";
            var password = form?["password"].ToString() ?? "";
            var code = form?["code"].ToString() ?? "";
            var ip = Auth.IpKey(http.Connection.RemoteIpAddress);
            var nameKey = Auth.NameKey(login);
            if (Auth.Blocked(ip) || Auth.Blocked(nameKey)) return FlowPage(http, token, "Zu viele Versuche, bitte kurz warten.");
            var u = users.ByLogin(login);
            var ok = await Passwords.VerifyLimitedAsync(password, u?.PasswordHash ?? Auth.DummyHash, http.RequestAborted);
            if (ok == null) return FlowPage(http, token, "Der Server ist gerade ausgelastet. Bitte gleich noch einmal versuchen.");
            if (ok == false || u == null)
            {
                Auth.Failed(ip); Auth.Failed(nameKey);
                await Task.Delay(700);
                return FlowPage(http, token, "Anmeldename oder Passwort falsch.");
            }
            if (u.TwoFactor && (string.IsNullOrWhiteSpace(code) || !Auth.CheckSecondFactor(users, u, code)))
            {
                if (!string.IsNullOrWhiteSpace(code)) { Auth.Failed(ip); Auth.Failed(nameKey); await Task.Delay(700); }
                return FlowPage(http, token, string.IsNullOrWhiteSpace(code) ? "Bitte den Code aus der Authenticator-App eingeben." : "Code falsch oder abgelaufen.");
            }
            Auth.Succeeded(ip); Auth.Succeeded(nameKey);
            user = u;
        }

        // Bereich "dav": der Login-Flow dient DAV-Clients (Nextcloud, DAVx5, Thunderbird). Kein Zugriff auf die
        // Web-API (Mail, Kalender, Notizen) - so richtet ein per Phishing erschlichenes Passwort weniger Schaden an.
        var appPassword = AppPasswords.Create(spaces.Get(user.Id).Db, flow.Name, AppPasswords.ScopeDav);
        var server = Origin(http);
        Flows.Done(flow, new Flows.Result(server, user.Login, appPassword));
        if (flow.V1)
            return Results.Redirect($"nc://login/server:{server}&user:{Uri.EscapeDataString(user.Login)}&password:{Uri.EscapeDataString(appPassword)}");
        return Page(http, "<h1>Fertig</h1><p>Das Programm ist angemeldet. Du kannst dieses Fenster schließen.</p>");
    }
}
