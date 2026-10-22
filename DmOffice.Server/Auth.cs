using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using DmOffice.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace DmOffice;

/// <summary>
/// Zwei Wege hinein: die Weboberflaeche mit Anmeldename und Passwort
/// (Cookie), und DAV-Programme mit Anmeldename und App-Passwort (Basic).
/// Das Login-Passwort gilt fuer DAV nicht - ein App-Passwort laesst sich
/// einzeln widerrufen, das Login-Passwort steht in keinem Handy.
/// </summary>
public static class Auth
{
    /// <summary>TOTP-Code oder ein unbenutzter Wiederherstellungscode (danach verbraucht).</summary>
    public static bool CheckSecondFactor(UserStore users, UserRecord u, string code)
    {
        if (u.TotpSecret == null) return true;
        long? step = null;
        try { step = Totp.Verify(users.Unwrap(u.TotpSecret), code, u.TotpLastStep); }
        catch (System.Security.Cryptography.CryptographicException) { /* Geheimnis nicht lesbar - Wiederherstellungscode hilft weiter */ }
        if (step is long s)
        {
            users.Update(u.Id, x => x.TotpLastStep = s);
            return true;
        }
        var hash = Passwords.TokenHash(code.Trim());
        if (u.RecoveryCodes.Contains(hash))
        {
            users.Update(u.Id, x => x.RecoveryCodes.Remove(hash));
            return true;
        }
        return false;
    }

    public const string AppPasswordScheme = "AppPassword";
    private static readonly ConcurrentDictionary<string, (int Count, DateTime Until)> Failures = new();

    public static ClaimsPrincipal Principal(UserRecord u, string scheme) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, u.Id),
            new Claim(ClaimTypes.Name, u.Login),
            new Claim(ClaimTypes.Role, u.IsAdmin ? "admin" : "user"),
            new Claim(StampClaim, u.Stamp)
        }, scheme));

    public const string StampClaim = "dm:stamp";
    /// <summary>Bereich eines App-Passworts (AppPasswords.ScopeNotizi), fehlt bei vollem Zugang.</summary>
    public const string ScopeClaim = "dm:scope";

    /// <summary>Nach einem neuen Stempel: diese Sitzung behalten, alle anderen fallen raus.</summary>
    public static Task Reissue(HttpContext http, UserStore users, string id) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(users.ById(id)!, CookieAuthenticationDefaults.AuthenticationScheme),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

    /// <summary>Jede Anfrage mit Cookie: gibt es den Benutzer noch, stimmt der Stempel?
    /// Die Rolle kommt immer aus users.json - ein entzogenes Admin-Recht gilt sofort.</summary>
    public static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
        var p = ctx.Principal;
        var u = users.ById(p?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        if (u == null || (p!.FindFirstValue(StampClaim) ?? "") != u.Stamp)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        if (p!.IsInRole("admin") != u.IsAdmin || p.FindFirstValue(ClaimTypes.Name) != u.Login)
        {
            ctx.ReplacePrincipal(Principal(u, CookieAuthenticationDefaults.AuthenticationScheme));
            ctx.ShouldRenew = true;
        }
    }

    /// <summary>Fuer unbekannte Anmeldenamen genauso lange rechnen wie fuer bekannte.</summary>
    internal static readonly string DummyHash = Passwords.Hash(Guid.NewGuid().ToString());

    /// <summary>Bremse gegen Durchprobieren - je Adresse und je Anmeldename
    /// (gegen wechselnde Adressen). Schluessel fuer Namen: NameKey(login).</summary>
    public static bool Blocked(string key) => Failures.TryGetValue(key, out var f) && f.Until > DateTime.UtcNow;

    /// <summary>Fuer die Meldung beim Sperren (gesetzt beim Start).</summary>
    public static Microsoft.Extensions.Logging.ILogger? Log { get; set; }

    private static DateTime _lastFailurePrune;

    /// <summary>Schluessel einer Adresse fuer die Fehlversuche: IPv4, bei IPv6 das /64-Netz - sonst haette ein
    /// Angreifer mit einem /64 unbegrenzt viele Schluessel (und je fuenf Versuche).</summary>
    public static string IpKey(System.Net.IPAddress? ip) => ip == null ? "?" : AnonThrottle.KeyOf(ip) ?? ip.ToString();

    public static void Failed(string key, string? why = null)
    {
        // Nicht endlos wachsen lassen: ab 10 000 Eintraegen hoechstens alle 10 s alles wegraeumen, was gerade
        // nicht gesperrt ist (abgelaufene Sperren und Zaehler unter der Schwelle)
        if (Failures.Count > 10_000 && DateTime.UtcNow - _lastFailurePrune > TimeSpan.FromSeconds(10))
        {
            _lastFailurePrune = DateTime.UtcNow;
            foreach (var (k, v) in Failures.ToArray())
                if (v.Until < DateTime.UtcNow) Failures.TryRemove(k, out _);
        }
        var count = (Failures.TryGetValue(key, out var f) ? f.Count : 0) + 1;
        Failures[key] = (count, count >= 5 ? DateTime.UtcNow.AddMinutes(Math.Min(30, count)) : DateTime.MinValue);
        if (count == 5) Log?.LogWarning("Zu viele Fehlversuche von {Key} ({Why}) - fuer {Minutes} Minuten gesperrt", key, why ?? "Anmeldung", Math.Min(30, count));
    }

    public static string NameKey(string login) => "u:" + UserStore.Normalize(login);

    /// <summary>Anspruch einer Anmeldung mit App-Passwort (Handy-App) - nie mit Admin-Rolle.</summary>
    public const string DeviceClaim = "dm:device";
    /// <summary>Id des benutzten App-Passworts - fuer die Geraete-Registrierung (E2e).</summary>
    public const string AppPasswordIdClaim = "dm:apppw";
    /// <summary>Anfrage kam aus dem verschluesselten Umschlag (E2e) - dort darf sich kein weiteres Geraet registrieren.</summary>
    public const string EnvelopeClaim = "dm:envelope";

    /// <summary>
    /// Was ein App-Passwort ausser DAV darf: Mail, Kalender, Kontakte, Notizen, Dateien und ein paar
    /// Einstellungen zum Lesen. Nicht: Verwaltung, Anmeldung/Passwort/2FA, Mail-Konten mit ihren
    /// Passwoertern, weitere App-Passwoerter (sonst koennte ein verlorenes Handy sich selbst verlaengern).
    /// </summary>
    public static bool AppPasswordAllowed(string path)
    {
        var p = path.TrimEnd('/');
        if (DavLike(p)) return true;
        if (!p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.Equals("/api/auth/state", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Equals("/api/e2e/register", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/api/e2e/unregister/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Equals("/api/push/register", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Equals("/api/tickets", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var prefix in new[] { "/api/mail", "/api/pim", "/api/notes", "/api/files", "/api/office" })
            if (p.Equals(prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var exact in new[] { "/api/activity", "/api/settings/dav", "/api/settings/ui", "/api/settings/categories", "/api/settings/search-folders" })
            if (p.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Schranke fuer Geraete- und Umschlag-Anfragen, auf dem bereits von Kestrel normalisierten Pfad - als
    /// zweite Verteidigungslinie hinter der Pfadpruefung im Umschlag (E2e). Faengt ab, falls ein Pfad die
    /// erste Pruefung formal erfuellt, aber in Wahrheit woanders hinzeigt (z. B. ueber "..").
    /// Anmeldung, Passwort und 2FA (/api/auth/*) bleiben fuer Geraet und Umschlag grundsaetzlich gesperrt;
    /// die Weboberflaeche ruft diese Wege ohnehin direkt auf, nicht durch den Umschlag.
    /// </summary>
    public static bool DeviceRequestAllowed(HttpContext http)
    {
        var u = http.User;
        bool device = u.HasClaim(c => c.Type == DeviceClaim);
        bool envelope = u.HasClaim(c => c.Type == EnvelopeClaim);
        if (!device && !envelope) return true;
        var path = http.Request.Path.Value ?? "";
        var p = path.TrimEnd('/');
        if (p.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("/api/auth/state", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase))
            return false;
        // App-Geraet (Rolle "user"): zusaetzlich die Freigabeliste auf dem echten Pfad erzwingen.
        // Browser-Umschlag laeuft wie das Cookie und ist hier nicht weiter eingeschraenkt.
        if (device && !AppPasswordAllowed(path)) return false;
        return true;
    }

    /// <summary>
    /// Schutz gegen CSRF von Nachbar-Seiten derselben Domain (SameSite=Lax schuetzt nur gegen fremde Domains): eine
    /// aendernde Anfrage (nicht GET/HEAD/OPTIONS), die per Anmelde-Cookie angemeldet ist, muss laut Browser von der
    /// eigenen Seite kommen. Sec-Fetch-Site setzt der Browser selbst (nicht von Skripten faelschbar) und haengt nicht
    /// an Proxy- oder Host-Einstellungen. Fehlt der Kopf (alte Browser, Programme), wird nicht geblockt.
    /// </summary>
    public static bool CrossSiteCookieWrite(HttpContext http)
    {
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method)) return false;
        if (http.User.Identity is not { IsAuthenticated: true, AuthenticationType: CookieAuthenticationDefaults.AuthenticationScheme }) return false;
        var site = http.Request.Headers["Sec-Fetch-Site"].ToString();
        return site is "cross-site" or "same-site";
    }

    /// <summary>WWW-Authenticate: Basic nur fuer DAV - im Browser loest der Kopf sonst ein Anmeldefenster aus.</summary>
    public static bool BasicChallenge(string path) => DavLike(path);

    /// <summary>DAV und was Nextcloud-Clients brauchen (WebDAV unter /remote.php, OCS-Abfragen, Vorschaubilder) - nur mit App-Passwort.</summary>
    public static bool DavLike(string path) =>
        path.StartsWith("/dav", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/remote.php/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/ocs/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/index.php/core/preview", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/index.php/apps/files/api/v1/thumbnail/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Principal fuer ein Geraet: wie <see cref="Principal"/>, aber immer Rolle "user" und mit Geraete-Anspruch.</summary>
    public static ClaimsPrincipal DevicePrincipal(UserRecord u, string scheme, string appPasswordId = "") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, u.Id),
            new Claim(ClaimTypes.Name, u.Login),
            new Claim(ClaimTypes.Role, "user"),
            new Claim(StampClaim, u.Stamp),
            new Claim(DeviceClaim, "1"),
            new Claim(AppPasswordIdClaim, appPasswordId)
        }, scheme));

    public static void Succeeded(string ip) => Failures.TryRemove(ip, out _);

    /// <summary>Einmal-Code fuer die Ersteinrichtung (nur solange es keinen Benutzer gibt). Beim Start ins Log
    /// geschrieben, damit nicht der erste Fremde, der die frische Instanz erreicht, das Verwaltungskonto anlegt.
    /// Per Umgebungsvariable DMOFFICE_SETUP_CODE fest vorgebbar.</summary>
    public static string? SetupCode { get; private set; }
    public static void EnsureSetupCode(ILogger log)
    {
        if (SetupCode != null) return;
        SetupCode = Environment.GetEnvironmentVariable("DMOFFICE_SETUP_CODE")?.Trim() is { Length: > 0 } fixedCode
            ? fixedCode : Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        log.LogWarning("Ersteinrichtung: Zum Anlegen des Verwaltungskontos diesen Code eingeben: {Code}", SetupCode);
    }

    public record LoginBody(string Login, string Password, string? Code);
    public record CodeBody(string Code);
    public record SetupBody(string Login, string DisplayName, string Password, string? Code = null);
    public record ChangeBody(string Old, string New);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/auth");

        api.MapGet("/state", (HttpContext http, UserStore users) =>
        {
            var loggedIn = http.User.Identity?.IsAuthenticated == true;
            UserRecord? me = loggedIn ? users.ById(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "") : null;
            return new
            {
                setupNeeded = users.Empty,
                setupCode = users.Empty && SetupCode != null,
                loggedIn = me != null,
                user = me == null ? null : new { me.Id, me.Login, me.DisplayName, me.IsAdmin, me.TwoFactor, files = !me.FilesOff }
            };
        });

        api.MapPost("/setup", async (SetupBody body, HttpContext http, UserStore users) =>
        {
            if (!users.Empty) return Results.Conflict(new { error = "Bereits eingerichtet." });
            // Einmal-Code aus dem Server-Log verlangen, falls einer gesetzt ist (konstante Zeit, mit Fehlversuchssperre)
            var ip = IpKey(http.Connection.RemoteIpAddress);
            if (Blocked(ip)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            if (SetupCode is { } code && !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(body.Code?.Trim() ?? ""), System.Text.Encoding.UTF8.GetBytes(code)))
            {
                Failed(ip, "Einrichtungscode");
                await Task.Delay(700);
                return Results.BadRequest(new { error = "Einrichtungscode falsch. Er steht im Server-Protokoll (docker logs)." });
            }
            try
            {
                var u = users.CreateFirstAdmin(body.Login, body.DisplayName, body.Password);
                if (u == null) return Results.Conflict(new { error = "Bereits eingerichtet." });
                SetupCode = null;   // verbraucht
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(u, CookieAuthenticationDefaults.AuthenticationScheme),
                    new AuthenticationProperties { IsPersistent = true });
                return Results.Ok();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/login", async (LoginBody body, HttpContext http, UserStore users) =>
        {
            var ip = Auth.IpKey(http.Connection.RemoteIpAddress);
            var nameKey = NameKey(body.Login ?? "");
            if (Blocked(ip) || Blocked(nameKey)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            var u = users.ByLogin(body.Login ?? "");
            var ok = await Passwords.VerifyLimitedAsync(body.Password ?? "", u?.PasswordHash ?? DummyHash, http.RequestAborted);
            if (ok == null) return Results.Json(new { error = "Der Server ist gerade ausgelastet. Bitte gleich noch einmal versuchen." }, statusCode: 503);
            if (ok == false || u == null)
            {
                Failed(ip); Failed(nameKey);
                await Task.Delay(700);
                return Results.Json(new { error = "Anmeldename oder Passwort falsch." }, statusCode: 401);
            }
            if (u.TwoFactor)
            {
                // Passwort stimmt - ohne Code nur die Frage danach, keine Anmeldung
                if (string.IsNullOrWhiteSpace(body.Code)) return Results.Ok(new { twoFactor = true });
                if (!CheckSecondFactor(users, u, body.Code))
                {
                    Failed(ip); Failed(nameKey);
                    await Task.Delay(700);
                    return Results.Json(new { error = "Code falsch oder abgelaufen.", twoFactor = true }, statusCode: 401);
                }
            }
            Succeeded(ip); Succeeded(nameKey);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(u, CookieAuthenticationDefaults.AuthenticationScheme),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
            return Results.Ok(new { twoFactor = false });
        });

        // ---------------- Zwei-Faktor-Anmeldung ----------------

        api.MapPost("/2fa/begin", (HttpContext http, UserStore users) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            // Laufende 2FA nur ueber "Ausschalten" (mit Passwort und Code) ersetzen
            if (u.TwoFactor) return Results.BadRequest(new { error = "Zwei-Faktor-Anmeldung ist schon an - erst ausschalten." });
            var secret = Totp.NewSecret();
            users.Update(id, x => x.TotpPending = users.Wrap(secret));
            return Results.Ok(new { uri = Totp.Uri(secret, u.Login), secret = Totp.Base32(secret) });
        }).RequireAuthorization();

        api.MapPost("/2fa/confirm", async (CodeBody body, HttpContext http, UserStore users) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (u.TwoFactor) return Results.BadRequest(new { error = "Zwei-Faktor-Anmeldung ist schon an - erst ausschalten." });
            if (u.TotpPending == null) return Results.BadRequest(new { error = "Erst einrichten." });
            var secret = users.Unwrap(u.TotpPending);
            if (Totp.Verify(secret, body.Code, 0) is not long step) return Results.BadRequest(new { error = "Code stimmt nicht - Uhrzeit am Handy prüfen." });
            var codes = Totp.NewRecoveryCodes();
            users.Update(id, x =>
            {
                x.TotpSecret = x.TotpPending;
                x.TotpPending = null;
                x.TotpLastStep = step;
                x.RecoveryCodes = codes.Select(Passwords.TokenHash).ToList();
                x.NewStamp();
            });
            await Reissue(http, users, id);
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireAuthorization();

        api.MapPost("/2fa/recovery", (LoginBody body, HttpContext http, UserStore users) =>
        {
            // Neue Wiederherstellungscodes - mit Passwort bestaetigt
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (!u.TwoFactor) return Results.BadRequest(new { error = "Zwei-Faktor-Anmeldung ist aus." });
            if (!Passwords.Verify(body.Password, u.PasswordHash)) return Results.BadRequest(new { error = "Passwort falsch." });
            var codes = Totp.NewRecoveryCodes();
            users.Update(id, x => x.RecoveryCodes = codes.Select(Passwords.TokenHash).ToList());
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireAuthorization();

        api.MapPost("/2fa/disable", async (LoginBody body, HttpContext http, UserStore users) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (!Passwords.Verify(body.Password, u.PasswordHash)) return Results.BadRequest(new { error = "Passwort falsch." });
            if (u.TwoFactor && !CheckSecondFactor(users, u, body.Code ?? "")) return Results.BadRequest(new { error = "Code falsch." });
            users.Update(id, x => { x.TotpSecret = null; x.TotpPending = null; x.RecoveryCodes.Clear(); x.NewStamp(); });
            await Reissue(http, users, id);
            return Results.Ok();
        }).RequireAuthorization();

        api.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        api.MapPost("/password", async (ChangeBody body, HttpContext http, UserStore users) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (!Passwords.Verify(body.Old, u.PasswordHash)) return Results.BadRequest(new { error = "Bisheriges Passwort falsch." });
            if (body.New.Length < 8) return Results.BadRequest(new { error = "Mindestens 8 Zeichen." });
            // Neuer Stempel: alle anderen Sitzungen sind abgemeldet, diese bekommt ein neues Cookie
            users.Update(id, x => { x.PasswordHash = Passwords.Hash(body.New); x.NewStamp(); });
            await Reissue(http, users, id);
            return Results.Ok();
        }).RequireAuthorization();
    }
}

/// <summary>Basic mit Anmeldename und App-Passwort - fuer /dav, /.well-known und die Web-API der Handy-App
/// (dort eingeschraenkt, siehe Auth.AppPasswordAllowed, und ohne Admin-Rolle).</summary>
public sealed class AppPasswordHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly UserStore _users;
    private readonly Spaces _spaces;

    public AppPasswordHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, UserStore users, Spaces spaces) : base(options, logger, encoder)
    {
        _users = users;
        _spaces = spaces;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Innenteil eines verschluesselten Umschlags (E2e): einmaliges Zeichen, nur von Loopback
        var session = Request.Headers[E2e.SessionHeader].ToString();
        if (session.Length > 0)
        {
            var s = E2e.TakeSession(session, Context.Connection.RemoteIpAddress);
            var su = s == null ? null : _users.ById(s.Value.UserId);
            if (su == null) return Task.FromResult(AuthenticateResult.Fail("session"));
            // Browser-Geraet: wie das Cookie, mit Admin-Rolle; App-Geraet: eingeschraenkt. Leere App-Passwort-Id
            // kennzeichnet "aus dem Umschlag heraus" - so darf sich kein zweites Geraet registrieren.
            var principal = s!.Value.AppPasswordId == null ? Auth.Principal(su, Scheme.Name) : Auth.DevicePrincipal(su, Scheme.Name, s.Value.AppPasswordId);
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(Auth.EnvelopeClaim, "1"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
        // Ticket der Handy-App (grosse Dateien und Streams ausserhalb des Umschlags): gilt nur fuer genau diese Anfrage
        var ticketHeader = Request.Headers[Tickets.Header].ToString();
        if (ticketHeader.Length > 0) return Task.FromResult(Ticket(ticketHeader));
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(AuthenticateResult.NoResult());
        // Ausserhalb der Freigabe zaehlt das App-Passwort nicht - auch nicht als Fehlversuch
        if (!Auth.AppPasswordAllowed(Request.Path.Value ?? "")) return Task.FromResult(AuthenticateResult.NoResult());
        bool dav = Auth.DavLike(Request.Path.Value ?? "");

        var ip = Auth.IpKey(Context.Connection.RemoteIpAddress);
        if (Auth.Blocked(ip)) return Task.FromResult(AuthenticateResult.Fail("blocked"));

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
        catch { return Task.FromResult(AuthenticateResult.Fail("bad header")); }
        var colon = decoded.IndexOf(':');
        if (colon < 0) return Task.FromResult(AuthenticateResult.Fail("bad header"));

        // Keine Sperre je Anmeldename: App-Passwoerter haben 120 Bit Zufall, Raten ist aussichtslos. Eine Namenssperre
        // haette nur erlaubt, mit dem bekannten Namen die DAV- und App-Zugaenge eines Benutzers lahmzulegen.
        // Die Bremse je Adresse (oben) und die Bremse fuer Unangemeldete bleiben.
        var user = _users.ByLogin(decoded[..colon]);
        if (user != null)
        {
            UserSpace space;
            // Datenbank wartet noch auf ihre Datenaenderung (Platz, Freigabe): kein Zugang, aber auch kein Fehlversuch
            try { space = _spaces.Get(user.Id); }
            catch (UpdatePendingException) { return Task.FromResult(AuthenticateResult.Fail("update pending")); }
            if (AppPasswords.Check(space.Db, decoded[(colon + 1)..]) is { } appPasswordId)
            {
                var scope = AppPasswords.ScopeOf(space.Db, appPasswordId);
                // notizi-Zugang: nur WebDAV (dort weiter eingegrenzt auf den Abgleichordner), keine Web-API
                if (scope != null && !dav) return Task.FromResult(AuthenticateResult.Fail("scope"));
                Auth.Succeeded(ip);
                var principal = dav ? Auth.Principal(user, Scheme.Name) : Auth.DevicePrincipal(user, Scheme.Name, appPasswordId);
                if (scope != null) ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(Auth.ScopeClaim, scope));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
            }
        }
        Auth.Failed(ip, "App-Passwort");
        return Task.FromResult(AuthenticateResult.Fail("wrong"));
    }

    /// <summary>Ticket pruefen: echt und nicht abgelaufen, passt zur Anfrage, Benutzer und Stempel unveraendert,
    /// App-Passwort nicht widerrufen, ein schreibendes Ticket noch unbenutzt. Dann ein Geraete-Principal mit Ticket-Anspruch.</summary>
    private AuthenticateResult Ticket(string token)
    {
        var t = Tickets.Read(Context.RequestServices, token, out var expires);
        if (t == null || !Tickets.Matches(t, Request)) return AuthenticateResult.Fail("ticket");
        var user = _users.ById(t.U);
        if (user == null || user.Stamp != t.S) return AuthenticateResult.Fail("ticket");
        if (t.A != null)
        {
            UserSpace space;
            try { space = _spaces.Get(user.Id); }
            catch (UpdatePendingException) { return AuthenticateResult.Fail("update pending"); }
            if (!AppPasswords.Exists(space.Db, t.A)) return AuthenticateResult.Fail("ticket");
        }
        if (!Tickets.Reading(t.M) && !Tickets.Consume(t.Id, expires)) return AuthenticateResult.Fail("ticket used");
        var principal = Auth.DevicePrincipal(user, Scheme.Name, t.A ?? "");
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(Tickets.Claim, t.Id));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        // Nur DAV-Programme brauchen die Aufforderung. Unter /api wuerde sie im Browser das graue
        // Anmeldefenster oeffnen und die Anmeldeseite blockieren - die Handy-App schickt Basic ohnehin vorab.
        if (Auth.BasicChallenge(Request.Path.Value ?? ""))
            Response.Headers.WWWAuthenticate = "Basic realm=\"dmOffice\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}

public static class AppPasswords
{
    /// <summary>Bereich "notizi": nur der Abgleichordner von notizi ueber WebDAV, sonst nichts.</summary>
    public const string ScopeNotizi = "notizi";

    /// <summary>Bereich "dav": nur DAV/Dateien (CalDAV, CardDAV, WebDAV, Nextcloud-Clients), keine Web-API.
    /// App-Passwoerter aus dem Nextcloud-Login-Flow bekommen diesen Bereich - so gibt ein per Phishing
    /// erschlichenes Passwort keinen Zugriff auf Mail, Kalender und Notizen ueber die Web-API.</summary>
    public const string ScopeDav = "dav";

    public record Entry(string Id, string Name, string Created, string? LastUsed, string? Scope = null);

    public static List<Entry> List(UserDb db) =>
        db.Query("SELECT id, name, created, last_used, scope FROM app_passwords ORDER BY created",
            r => new Entry(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));

    public static string Create(UserDb db, string name, string? scope = null)
    {
        var token = Passwords.NewToken();
        db.Exec("INSERT INTO app_passwords(id,name,hash,created,scope) VALUES($i,$n,$h,$c,$s)",
            ("$i", Guid.NewGuid().ToString("N")), ("$n", string.IsNullOrWhiteSpace(name) ? "Gerät" : name.Trim()),
            ("$h", Passwords.TokenHash(token)), ("$c", DateTime.UtcNow.ToString("o")), ("$s", (object?)scope ?? DBNull.Value));
        return token;
    }

    /// <summary>Hat sich ein eigener notizi-Zugang schon einmal angemeldet? Erst dann sehen alle anderen Zugaenge den
    /// Abgleichordner nicht mehr - sonst sperrte schon der Knopf "notizi verbinden" die bisherige Verbindung aus,
    /// bevor notizi umgestellt ist.</summary>
    public static bool HasNotiziScope(UserDb db) =>
        Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM app_passwords WHERE scope=$s AND last_used IS NOT NULL", ("$s", ScopeNotizi))) > 0;

    public static string? ScopeOf(UserDb db, string id) => db.Scalar("SELECT scope FROM app_passwords WHERE id=$i", ("$i", id)) as string;

    public static bool Exists(UserDb db, string id) => db.Scalar("SELECT 1 FROM app_passwords WHERE id=$i", ("$i", id)) != null;

    public static void Delete(UserDb db, string id)
    {
        E2e.DeleteForAppPassword(db, id);
        db.Exec("DELETE FROM app_passwords WHERE id=$i", ("$i", id));
    }

    private static readonly ConcurrentDictionary<string, DateTime> Touched = new();

    /// <summary>Id des passenden App-Passworts, null wenn keins passt.</summary>
    public static string? Check(UserDb db, string token)
    {
        var hash = Passwords.TokenHash(token);
        var id = db.Scalar("SELECT id FROM app_passwords WHERE hash=$h", ("$h", hash)) as string;
        if (id == null) return null;
        // Zuletzt benutzt - hoechstens einmal je Stunde schreiben
        if (!Touched.TryGetValue(id, out var t) || DateTime.UtcNow - t > TimeSpan.FromHours(1))
        {
            Touched[id] = DateTime.UtcNow;
            db.Exec("UPDATE app_passwords SET last_used=$t WHERE id=$i", ("$t", DateTime.UtcNow.ToString("o")), ("$i", id));
        }
        return id;
    }
}
