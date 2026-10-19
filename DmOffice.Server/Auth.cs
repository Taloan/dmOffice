using System.Collections.Concurrent;
using System.Security.Claims;
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
    private static readonly string DummyHash = Passwords.Hash(Guid.NewGuid().ToString());

    /// <summary>Bremse gegen Durchprobieren - je Adresse und je Anmeldename
    /// (gegen wechselnde Adressen). Schluessel fuer Namen: NameKey(login).</summary>
    public static bool Blocked(string key) => Failures.TryGetValue(key, out var f) && f.Until > DateTime.UtcNow;

    public static void Failed(string key)
    {
        // Nicht endlos wachsen lassen: alte Eintraege wegraeumen
        if (Failures.Count > 10_000)
            foreach (var (k, v) in Failures.ToArray())
                if (v.Until < DateTime.UtcNow.AddHours(-1)) Failures.TryRemove(k, out _);
        var count = (Failures.TryGetValue(key, out var f) ? f.Count : 0) + 1;
        Failures[key] = (count, count >= 5 ? DateTime.UtcNow.AddMinutes(Math.Min(30, count)) : DateTime.MinValue);
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
        if (p.StartsWith("/dav", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase)) return true;
        if (!p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.Equals("/api/auth/state", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Equals("/api/e2e/register", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/api/e2e/unregister/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Equals("/api/push/register", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var prefix in new[] { "/api/mail", "/api/pim", "/api/notes", "/api/files" })
            if (p.Equals(prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var exact in new[] { "/api/settings/dav", "/api/settings/ui", "/api/settings/categories", "/api/settings/search-folders" })
            if (p.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>WWW-Authenticate: Basic nur fuer DAV - im Browser loest der Kopf sonst ein Anmeldefenster aus.</summary>
    public static bool BasicChallenge(string path) =>
        path.StartsWith("/dav", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase);

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

    public record LoginBody(string Login, string Password, string? Code);
    public record CodeBody(string Code);
    public record SetupBody(string Login, string DisplayName, string Password);
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
                loggedIn = me != null,
                user = me == null ? null : new { me.Id, me.Login, me.DisplayName, me.IsAdmin, me.TwoFactor, files = !me.FilesOff }
            };
        });

        api.MapPost("/setup", async (SetupBody body, HttpContext http, UserStore users) =>
        {
            if (!users.Empty) return Results.Conflict(new { error = "Bereits eingerichtet." });
            try
            {
                var u = users.Create(body.Login, body.DisplayName, body.Password, isAdmin: true);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(u, CookieAuthenticationDefaults.AuthenticationScheme),
                    new AuthenticationProperties { IsPersistent = true });
                return Results.Ok();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/login", async (LoginBody body, HttpContext http, UserStore users) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "?";
            var nameKey = NameKey(body.Login ?? "");
            if (Blocked(ip) || Blocked(nameKey)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            var u = users.ByLogin(body.Login ?? "");
            if (!Passwords.Verify(body.Password ?? "", u?.PasswordHash ?? DummyHash) || u == null)
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
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(AuthenticateResult.NoResult());
        // Ausserhalb der Freigabe zaehlt das App-Passwort nicht - auch nicht als Fehlversuch
        if (!Auth.AppPasswordAllowed(Request.Path.Value ?? "")) return Task.FromResult(AuthenticateResult.NoResult());
        bool dav = (Request.Path.Value ?? "").StartsWith("/dav", StringComparison.OrdinalIgnoreCase) || (Request.Path.Value ?? "").StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase);

        var ip = Context.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (Auth.Blocked(ip)) return Task.FromResult(AuthenticateResult.Fail("blocked"));

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
        catch { return Task.FromResult(AuthenticateResult.Fail("bad header")); }
        var colon = decoded.IndexOf(':');
        if (colon < 0) return Task.FromResult(AuthenticateResult.Fail("bad header"));

        var nameKey = Auth.NameKey(decoded[..colon]);
        if (Auth.Blocked(nameKey)) return Task.FromResult(AuthenticateResult.Fail("blocked"));
        var user = _users.ByLogin(decoded[..colon]);
        if (user != null)
        {
            var space = _spaces.Get(user.Id);
            if (AppPasswords.Check(space.Db, decoded[(colon + 1)..]) is { } appPasswordId)
            {
                Auth.Succeeded(ip); Auth.Succeeded(nameKey);
                var principal = dav ? Auth.Principal(user, Scheme.Name) : Auth.DevicePrincipal(user, Scheme.Name, appPasswordId);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
            }
        }
        Auth.Failed(ip); Auth.Failed(nameKey);
        return Task.FromResult(AuthenticateResult.Fail("wrong"));
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
    public record Entry(string Id, string Name, string Created, string? LastUsed);

    public static List<Entry> List(UserDb db) =>
        db.Query("SELECT id, name, created, last_used FROM app_passwords ORDER BY created",
            r => new Entry(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));

    public static string Create(UserDb db, string name)
    {
        var token = Passwords.NewToken();
        db.Exec("INSERT INTO app_passwords(id,name,hash,created) VALUES($i,$n,$h,$c)",
            ("$i", Guid.NewGuid().ToString("N")), ("$n", string.IsNullOrWhiteSpace(name) ? "Gerät" : name.Trim()),
            ("$h", Passwords.TokenHash(token)), ("$c", DateTime.UtcNow.ToString("o")));
        return token;
    }

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
