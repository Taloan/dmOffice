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
        // Zwei gleichzeitige Anfragen mit demselben Code: nur eine darf ihn verbrauchen (frisch gelesen unter der Sperre)
        lock (SecondFactorGates.GetOrAdd(u.Id, _ => new object()))
        {
            u = users.ById(u.Id) ?? u;
            return CheckSecondFactorLocked(users, u, code);
        }
    }

    private static readonly ConcurrentDictionary<string, object> SecondFactorGates = new();

    private static bool CheckSecondFactorLocked(UserStore users, UserRecord u, string code)
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
    private static readonly ConcurrentDictionary<string, (int Count, DateTime Until, DateTime Last)> Failures = new();
    /// <summary>So lange nach dem letzten Fehlversuch beginnt der Zähler wieder bei null (Sperre klingt ab).</summary>
    private static readonly TimeSpan FailuresForget = TimeSpan.FromHours(1);

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

    /// <summary>Nach einem neuen Stempel: diese Sitzung behalten, alle anderen fallen raus (auch aus der Sitzungsliste).</summary>
    public static Task Reissue(HttpContext http, UserStore users, string id)
    {
        var sid = CookieSessions.Of(http.User);
        var u = users.ById(id)!;
        CookieSessions.EndAll(http.RequestServices, id, sid, u.Stamp);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, WithSession(Principal(u, CookieAuthenticationDefaults.AuthenticationScheme), sid),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    }

    /// <summary>Neue Anmeldung im Browser: mit Eintrag in der Sitzungsliste.</summary>
    public static Task SignInNew(HttpContext http, UserRecord u, AuthenticationProperties props) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            WithSession(Principal(u, CookieAuthenticationDefaults.AuthenticationScheme), CookieSessions.Start(http, u.Id, u.Stamp)), props);

    private static ClaimsPrincipal WithSession(ClaimsPrincipal p, string? sid)
    {
        if (sid != null) ((ClaimsIdentity)p.Identity!).AddClaim(new Claim(CookieSessions.Claim, sid));
        return p;
    }

    /// <summary>Jede Anfrage mit Cookie: gibt es den Benutzer noch, stimmt der Stempel?
    /// Die Rolle kommt immer aus users.json - ein entzogenes Admin-Recht gilt sofort.</summary>
    public static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
        var p = ctx.Principal;
        var u = users.ById(p?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        // Notfallsitzung (Version 2): gilt nur, solange der Notfallzugang gewaehrt ist - Zuruecknehmen wirft sie sofort hinaus
        var emergency = p?.FindFirstValue(DmOffice.Emergency.EmergencyApi.SessionClaim);
        var sid = p == null ? null : CookieSessions.Of(p);
        if (u == null || (p!.FindFirstValue(StampClaim) ?? "") != u.Stamp
            || (sid != null && !CookieSessions.Valid(ctx.HttpContext, u.Id, sid))
            || (emergency != null && !ctx.HttpContext.RequestServices.GetRequiredService<DmOffice.Emergency.EmergencyStore>().MayEnter(emergency, u.Id)))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        if (p!.IsInRole("admin") != u.IsAdmin || p.FindFirstValue(ClaimTypes.Name) != u.Login)
        {
            var fresh = WithSession(Principal(u, CookieAuthenticationDefaults.AuthenticationScheme), sid);
            if (emergency != null) ((ClaimsIdentity)fresh.Identity!).AddClaim(new Claim(DmOffice.Emergency.EmergencyApi.SessionClaim, emergency));
            ctx.ReplacePrincipal(fresh);
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
    private static string Cut(string? s) => s == null ? "" : s.Length > 60 ? s[..60] : s;

    public static string IpKey(System.Net.IPAddress? ip) => ip == null ? "?" : AnonThrottle.KeyOf(ip) ?? ip.ToString();

    public static void Failed(string key, string? why = null)
    {
        // Nicht endlos wachsen lassen: ab 10 000 Eintraegen hoechstens alle 10 s alles wegraeumen, was gerade
        // nicht gesperrt ist (abgelaufene Sperren und Zaehler unter der Schwelle)
        if (Failures.Count > 10_000 && DateTime.UtcNow - _lastFailurePrune > TimeSpan.FromSeconds(10))
        {
            _lastFailurePrune = DateTime.UtcNow;
            // Nur Abgeklungenes: sonst setzte ein Angreifer mit vielen Adressen die Zähler aller Namen zurück
            foreach (var (k, v) in Failures.ToArray())
                if (v.Until < DateTime.UtcNow && DateTime.UtcNow - v.Last > FailuresForget) Failures.TryRemove(k, out _);
        }
        var now = DateTime.UtcNow;
        var count = (Failures.TryGetValue(key, out var f) && now - f.Last <= FailuresForget ? f.Count : 0) + 1;
        Failures[key] = (count, count >= 5 ? now.AddMinutes(Math.Min(30, count)) : DateTime.MinValue, now);
        if (count == 5) Log?.LogWarning("Zu viele Fehlversuche von {Key} ({Why}) - fuer {Minutes} Minuten gesperrt", key, why ?? "Anmeldung", Math.Min(30, count));
    }

    public static string NameKey(string login) => "u:" + UserStore.Normalize(login);

    /// <summary>Anspruch einer Anmeldung mit App-Passwort (Handy-App) - nie mit Admin-Rolle.</summary>
    public const string DeviceClaim = "dm:device";
    /// <summary>Id des benutzten App-Passworts - fuer die Geraete-Registrierung (E2e).</summary>
    public const string AppPasswordIdClaim = "dm:apppw";
    /// <summary>Id des App-Passworts bei CalDAV/CardDAV-Zugriffen (WebDAV der App).</summary>
    public const string DavPasswordClaim = "dm:davpw";

    /// <summary>Id des App-Passworts, mit dem die Anfrage kam (App, Ticket der App, CalDAV) - sonst null (Browser). Der Tresor ist je Gerät offen.</summary>
    public static string? DeviceOf(HttpContext http) =>
        http.User.FindFirstValue(AppPasswordIdClaim) is { Length: > 0 } a ? a
        : http.User.FindFirstValue(DavPasswordClaim) is { Length: > 0 } d ? d : null;
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
        // Eigenes Konto löschen auch aus der App; verlangt zusätzlich das Passwort des Kontos
        if (p.Equals("/api/auth/delete-account", StringComparison.OrdinalIgnoreCase) || p.Equals("/api/auth/delete-account/impact", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var prefix in new[] { "/api/mail", "/api/pim", "/api/notes", "/api/files", "/api/office", "/api/chat", "/api/journal", "/api/emergency", "/api/gallery", "/api/audio", "/api/dashboard", "/api/coach", "/api/tresor", "/api/dms" })
            if (p.Equals(prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var exact in new[] { "/api/activity", "/api/settings/dav", "/api/settings/ui", "/api/settings/categories", "/api/settings/search-folders", "/api/settings/security-log" })
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
            && !p.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase)
            // Verlangt zusätzlich Passwort und Zweitfaktor des Kontos, ein App-Passwort allein genügt nicht
            && !p.Equals("/api/auth/delete-account", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("/api/auth/delete-account/impact", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Anmeldung gelungen: der Name ist frei, die Adresse bekommt nur einen Versuch gutgeschrieben - sonst setzte jemand mit
    /// eigenem Konto die Bremse seiner Adresse zwischen den Versuchen an fremden Konten immer wieder zurück.
    /// </summary>
    public static void SucceededLogin(string ip, string nameKey)
    {
        Failures.TryRemove(nameKey, out _);
        if (Failures.TryGetValue(ip, out var f) && f.Until <= DateTime.UtcNow)
        {
            if (f.Count <= 1) Failures.TryRemove(ip, out _);
            else Failures[ip] = (f.Count - 1, f.Until, f.Last);
        }
    }

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

    /// <summary>Der Code ist verbraucht: Auch die Aufforderung im Systemprotokoll ist damit überholt.</summary>
    private static void SetupCodeUsed()
    {
        if (SetupCode is { } used) SystemLog.Withdraw(used);
        SetupCode = null;
    }

    public record LoginBody(string Login, string Password, string? Code);

    private const string KnownCookie = "dm_known", KnownKey = "auth.known-browsers";

    /// <summary>
    /// Anmeldung aus einem Browser, in dem diese Person sich noch nie angemeldet hat: Meldung per Pukki und Mail. Der Browser
    /// trägt dafür eine Kennung im Cookie (ein Jahr); gespeichert ist nur ihr Hash (höchstens 30 je Person). Die erste
    /// Anmeldung nach Einführung (noch keine Liste) zählt still als bekannt - sonst bekäme jeder beim nächsten Mal eine Meldung.
    /// </summary>
    /// <summary>
    /// Schlüssel für die Fehlversuche aus einem Browser, in dem sich dieses Konto schon angemeldet hat (Cookie aus
    /// <see cref="KnownBrowser"/>), sonst null. Ein solcher Browser zählt eigene Fehlversuche und ist von der Sperre des
    /// Anmeldenamens nicht betroffen.
    /// </summary>
    private static string? DeviceKey(HttpContext http, UserRecord? u)
    {
        if (u == null || http.Request.Cookies[KnownCookie] is not { Length: >= 20 } token) return null;
        try
        {
            var stored = http.RequestServices.GetRequiredService<Spaces>().Get(u.Id).Db.Get(KnownKey);
            if (stored == null) return null;
            var hash = Passwords.TokenHash(token);
            return (System.Text.Json.JsonSerializer.Deserialize<List<string>>(stored) ?? []).Contains(hash) ? "d:" + u.Id + ":" + hash : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    private static void KnownBrowser(HttpContext http, UserRecord u)
    {
        try
        {
            var db = http.RequestServices.GetRequiredService<Spaces>().Get(u.Id).Db;
            var stored = db.Get(KnownKey);
            var list = stored == null ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(stored) ?? [];
            var token = http.Request.Cookies[KnownCookie] is { Length: >= 20 } c ? c : E2e.Base64Url(RandomNumberGenerator.GetBytes(24));
            var hash = Passwords.TokenHash(token);
            if (!list.Contains(hash))
            {
                if (stored != null)
                    SecurityNotice.Important(http.RequestServices, u.Id,
                        $"neuer Browser ({BrowserName(http.Request.Headers.UserAgent.ToString())}, Adresse {http.Connection.RemoteIpAddress})", login: true);
                list.Add(hash);
                if (list.Count > 30) list.RemoveRange(0, list.Count - 30);
                db.Set(KnownKey, System.Text.Json.JsonSerializer.Serialize(list));
            }
            http.Response.Cookies.Append(KnownCookie, token, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, Path = "/", IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddYears(1),
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException) { /* Meldung ist Zusatz - Anmeldung geht trotzdem */ }
    }

    /// <summary>Kurzer Name des Browsers für die Meldung, etwa „Firefox unter Windows“.</summary>
    internal static string BrowserName(string ua)
    {
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : "unbekannter Browser";
        var os = ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" : ua.Contains("Windows") ? "Windows"
            : ua.Contains("Mac OS X") ? "macOS" : ua.Contains("Linux") ? "Linux" : null;
        return os == null ? browser : browser + " unter " + os;
    }
    public record CodeBody(string Code);
    public record PasswordBody(string? Password, string? Code = null);

    /// <summary>
    /// Bestätigung mit dem Anmelde-Passwort (und bei Zwei-Faktor dem Code, wenn needCode) für Heikles aus einer laufenden Sitzung -
    /// mit derselben Fehlversuchsbremse wie die Anmeldung. Null: in Ordnung, sonst die Fehlermeldung.
    /// </summary>
    private static bool LastAdmin(UserStore users, UserRecord u) => u.IsAdmin && users.All().Count(x => x.IsAdmin) <= 1;

    public static IResult? Confirm(UserStore users, UserRecord u, string? password, string? code, bool needCode)
    {
        var key = "confirm:" + u.Id;
        if (Blocked(key)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
        if (string.IsNullOrEmpty(password) || !Passwords.Verify(password, u.PasswordHash))
        {
            Failed(key, "Passwort zur Bestätigung");
            return Results.BadRequest(new { error = "Passwort falsch." });
        }
        if (needCode && u.TwoFactor && !CheckSecondFactor(users, u, code ?? ""))
        {
            Failed(key, "Code zur Bestätigung");
            return Results.BadRequest(new { error = "Code falsch." });
        }
        Succeeded(key);
        return null;
    }
    public record SetupBody(string Login, string DisplayName, string Password, string? Code = null);
    /// <summary>Wiederherstellen aus einer S3-Sicherung statt Neueinrichtung (Version 2).</summary>
    public record RestoreBody(string? Code, string? Endpoint, string? Region, string? Bucket, string? Prefix, string? AccessKey, string? SecretKey, string? Name = null,
        string? Target = null, string? WebDavUrl = null, string? WebDavUser = null, string? WebDavPassword = null, string? Folder = null);
    public record ProfileBody(string? Code, string? Text);
    public record ChangeBody(string Old, string New);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/auth");

        api.MapGet("/state", (HttpContext http, UserStore users, ServerConfig config) =>
        {
            var loggedIn = http.User.Identity?.IsAuthenticated == true;
            UserRecord? me = loggedIn ? users.ById(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "") : null;
            return new
            {
                setupNeeded = users.Empty,
                setupCode = users.Empty && SetupCode != null,
                // Demo-Server: Zugangsdaten für die Anmeldeseite und den Hinweis oben
                demo = Demo.Enabled ? new { login = Demo.Login, password = Demo.Password } : null,
                // Release-Liste: in dieser Version nicht freigeschaltete Funktionen - Web und App blenden sie aus
                featuresOff = Features.Off,
                loggedIn = me != null,
                user = me == null ? null : new { me.Id, me.Login, me.DisplayName, me.IsAdmin, me.TwoFactor, files = !me.FilesOff,
                    // Audiothek: Modul eingeschaltet und nicht ohne Dateiablage
                    audio = DmOffice.Audio.AudioFolders.Enabled(me) }
            };
        });

        // Ersteinrichtung: statt eines neuen Verwaltungskontos alles aus einer S3-Sicherung zurueckholen (Version 2).
        // Nur solange es keinen Benutzer gibt und mit dem Einrichtungscode; der Hauptschluessel muss derselbe sein.
        async Task<IResult?> CodeGate(string? given, HttpContext http, UserStore users)
        {
            if (!users.Empty) return Results.Conflict(new { error = "Bereits eingerichtet." });
            var ip = IpKey(http.Connection.RemoteIpAddress);
            if (Blocked(ip)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            if (SetupCode is { } code && !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(given?.Trim() ?? ""), System.Text.Encoding.UTF8.GetBytes(code)))
            {
                Failed(ip, "Einrichtungscode");
                await Task.Delay(700);
                return Results.BadRequest(new { error = "Einrichtungscode falsch. Er steht im Server-Protokoll (docker logs)." });
            }
            return null;
        }
        async Task<IResult?> RestoreGate(RestoreBody body, HttpContext http, UserStore users)
        {
            if (await CodeGate(body.Code, http, users) is { } stop) return stop;
            if (body.Target == "folder")
            {
                if (string.IsNullOrWhiteSpace(body.Folder) || !Path.IsPathFullyQualified(body.Folder.Trim()) || !Directory.Exists(body.Folder.Trim()))
                    return Results.BadRequest(new { error = "Den Ordner gibt es auf dem Server nicht." });
            }
            else if (!Uri.TryCreate((body.Target == "webdav" ? body.WebDavUrl : body.Endpoint)?.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                return Results.BadRequest(new { error = "Die Adresse muss mit http:// oder https:// beginnen." });
            return null;
        }
        IBlobRemote RemoteFor(RestoreBody b) => b.Target switch
        {
            "webdav" => new DmOffice.Storage.DriverRemote(new DmOffice.Storage.WebDavDriver(b.WebDavUrl!.Trim(), b.WebDavUser, b.WebDavPassword)),
            "folder" => new DmOffice.Storage.DriverRemote(new DmOffice.Storage.FolderDriver(b.Folder!.Trim())),
            _ => S3Remote.Create((b.Endpoint ?? "").Trim().TrimEnd('/'), b.Region ?? "", (b.Bucket ?? "").Trim(), (b.AccessKey ?? "").Trim(), b.SecretKey ?? ""),
        };

        // Zugangsdaten für den Notfall (aus der Verwaltung → Storage) einlesen: mit dem Hauptschlüssel dieses Servers öffnen
        // und die Felder zurückgeben - passt der Schlüssel nicht, merkt man es hier schon, nicht erst an der Sicherung
        api.MapPost("/restore/profile", async (ProfileBody body, HttpContext http, UserStore users, Vault vault) =>
        {
            if (await CodeGate(body.Code, http, users) is { } stop) return stop;
            try
            {
                return StorageProfile.Open(vault, body.Text ?? "") is { } p && (p.Core ?? p.Files) is { } core ? Results.Ok(new { core, files = p.Files, own = p.Core != null })
                    : Results.BadRequest(new { error = "Darin stehen keine Zugangsdaten von dmOffice – die Zeile beginnt mit „dmoffice-zugang:“." });
            }
            catch (CryptographicException)
            {
                return Results.BadRequest(new { error = "Die Zugangsdaten passen nicht zum Hauptschlüssel dieses Servers. In der Container-Vorlage muss derselbe DMOFFICE_MASTER_KEY stehen wie beim alten Server." });
            }
        });
        api.MapPost("/restore/list", async (RestoreBody body, HttpContext http, UserStore users, CancellationToken ct) =>
        {
            if (await RestoreGate(body, http, users) is { } stop) return stop;
            try { using var remote = RemoteFor(body); return Results.Ok(await S3Backup.ListAsync(remote, body.Prefix, ct)); }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.BadRequest(new { error = "Speicherziel: " + S3Remote.Describe(e) }); }
        });
        // Vor dem Wiederherstellen: Wie viele Inhalte des Stands fehlen am Ziel seiner Einstellungen? Rührt am Ziel nichts an.
        api.MapPost("/restore/check", async (RestoreBody body, HttpContext http, UserStore users, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (await RestoreGate(body, http, users) is { } stop) return stop;
            var raw = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(raw);
            try
            {
                using var remote = RemoteFor(body);
                var stand = (await S3Backup.ListAsync(remote, body.Prefix, ct)).FirstOrDefault(s => s.Name == body.Name)
                    ?? throw new KeyNotFoundException("Diesen Stand gibt es dort nicht.");
                var file = Path.Combine(raw, stand.Name + ".dmb");
                await remote.DownloadAsync(stand.Key, file, ct);
                var key = Environment.GetEnvironmentVariable("DMOFFICE_MASTER_KEY");
                if (string.IsNullOrWhiteSpace(key)) return Results.Ok(new { checkedFiles = false, total = 0L, missing = 0L, reason = "Ohne DMOFFICE_MASTER_KEY lässt sich das nicht vorab prüfen." });
                using var reader = StandReader.Open(file, key);
                var (total, missing) = await RestoreCheck.CountAsync(reader, ct);
                return Results.Ok(new { checkedFiles = true, total, missing, reason = (string?)null });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logs.CreateLogger("DmOffice.Restore").LogWarning("Prüfung vor dem Wiederherstellen: {Error}", e.Message);
                return Results.Ok(new { checkedFiles = false, total = 0L, missing = 0L, reason = (string?)S3Remote.Describe(e) });
            }
            finally { try { Directory.Delete(raw, recursive: true); } catch (IOException) { } }
        });
        api.MapPost("/restore/run", async (RestoreBody body, HttpContext http, UserStore users, DataPaths paths, Vault vault,
            IHostApplicationLifetime life, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (await RestoreGate(body, http, users) is { } stop) return stop;
            try
            {
                using var remote = RemoteFor(body);
                var stand = (await S3Backup.ListAsync(remote, body.Prefix, ct)).FirstOrDefault(s => s.Name == body.Name)
                    ?? throw new KeyNotFoundException("Diesen Stand gibt es dort nicht.");
                var manifest = await S3Backup.RestoreAsync(paths, vault, remote, stand, ct);
                logs.CreateLogger("DmOffice.Restore").LogWarning("Aus S3-Sicherung {Name} wiederhergestellt ({Users} Benutzer) - der Server startet neu", stand.Name, manifest.Users.Count);
                SetupCodeUsed();
                // Neu starten, damit Benutzer, Einstellungen und Schluessel aus der Sicherung geladen werden (Docker startet neu)
                _ = Task.Run(async () => { await Task.Delay(1500); life.StopApplication(); });
                return Results.Ok(new { users = manifest.Users.Select(x => x.Login), manifest.AppVersion, created = manifest.Created });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Results.BadRequest(new { error = e is Amazon.Runtime.AmazonServiceException or HttpRequestException ? "Speicherziel: " + S3Remote.Describe(e) : e.Message });
            }
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
                SetupCodeUsed();
                await SignInNew(http, u, new AuthenticationProperties { IsPersistent = true });
                return Results.Ok();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/login", async (LoginBody body, HttpContext http, UserStore users) =>
        {
            var ip = Auth.IpKey(http.Connection.RemoteIpAddress);
            var nameKey = NameKey(body.Login ?? "");
            var u = users.ByLogin(body.Login ?? "");
            // Browser, in dem sich dieses Konto schon angemeldet hat: eigener Zähler statt dem des Namens
            var device = DeviceKey(http, u);
            if (Blocked(ip) || Blocked(device ?? nameKey)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            var ok = await Passwords.VerifyLimitedAsync(body.Password ?? "", u?.PasswordHash ?? DummyHash, http.RequestAborted);
            if (ok == null) return Results.Json(new { error = "Der Server ist gerade ausgelastet. Bitte gleich noch einmal versuchen." }, statusCode: 503);
            if (ok == false || u == null)
            {
                Failed(ip); Failed(device ?? nameKey);
                // Security Center: festhalten, wer es auf welches Konto versucht (verdächtig, sperrt erst bei vielen)
                SecurityCenter.Shared?.Suspicious(ip, "anmeldung", 2, "POST", "/api/auth/login", http.Request.Headers.UserAgent,
                    u == null ? $"unbekannter Anmeldename „{Cut(body.Login)}“" : $"falsches Passwort für „{u.Login}“");
                await Task.Delay(700);
                return Results.Json(new { error = "Anmeldename oder Passwort falsch." }, statusCode: 401);
            }
            if (u.TwoFactor)
            {
                // Passwort stimmt - ohne Code nur die Frage danach, keine Anmeldung
                if (string.IsNullOrWhiteSpace(body.Code)) return Results.Ok(new { twoFactor = true });
                if (!CheckSecondFactor(users, u, body.Code))
                {
                    Failed(ip); Failed(device ?? nameKey);
                    SecurityCenter.Shared?.Suspicious(ip, "anmeldung", 2, "POST", "/api/auth/login", http.Request.Headers.UserAgent, $"falscher Zweitfaktor für „{u.Login}“ (Passwort stimmte)");
                    await Task.Delay(700);
                    return Results.Json(new { error = "Code falsch oder abgelaufen.", twoFactor = true }, statusCode: 401);
                }
            }
            // Aus einem bekannten Browser bleibt der Zähler des Namens stehen
            SucceededLogin(ip, device ?? nameKey);
            await SignInNew(http, u, new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
            KnownBrowser(http, u);
            return Results.Ok(new { twoFactor = false });
        });

        // ---------------- Zwei-Faktor-Anmeldung ----------------

        api.MapPost("/2fa/begin", (PasswordBody body, HttpContext http, UserStore users) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            // Laufende 2FA nur ueber "Ausschalten" (mit Passwort und Code) ersetzen
            if (u.TwoFactor) return Results.BadRequest(new { error = "Zwei-Faktor-Anmeldung ist schon an - erst ausschalten." });
            // Mit dem Passwort bestätigt - sonst könnte eine übernommene Sitzung 2FA mit eigenem Gerät einschalten und aussperren
            if (Confirm(users, u, body.Password, null, needCode: false) is { } refused) return refused;
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
            SecurityNotice.Important(http.RequestServices, id, "Zwei-Faktor-Anmeldung eingeschaltet");
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireAuthorization();

        api.MapPost("/2fa/recovery", (LoginBody body, HttpContext http, UserStore users) =>
        {
            // Neue Wiederherstellungscodes - mit Passwort bestaetigt
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (!u.TwoFactor) return Results.BadRequest(new { error = "Zwei-Faktor-Anmeldung ist aus." });
            // Passwort und Code - neue Codes allein mit dem Passwort umgingen die Zwei-Faktor-Anmeldung dauerhaft
            if (Confirm(users, u, body.Password, body.Code, needCode: true) is { } refused) return refused;
            var codes = Totp.NewRecoveryCodes();
            users.Update(id, x => x.RecoveryCodes = codes.Select(Passwords.TokenHash).ToList());
            SecurityNotice.Important(http.RequestServices, id, "neue Wiederherstellungscodes für die Zwei-Faktor-Anmeldung");
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireAuthorization();

        api.MapPost("/2fa/disable", async (LoginBody body, HttpContext http, UserStore users) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (Confirm(users, u, body.Password, body.Code, needCode: true) is { } refused) return refused;
            users.Update(id, x => { x.TotpSecret = null; x.TotpPending = null; x.RecoveryCodes.Clear(); x.NewStamp(); });
            await Reissue(http, users, id);
            SecurityNotice.Important(http.RequestServices, id, "Zwei-Faktor-Anmeldung ausgeschaltet");
            return Results.Ok();
        }).RequireAuthorization();

        api.MapPost("/logout", async (HttpContext http) =>
        {
            Tresor.Close(http);
            CryptomatorPaths.LockAccess(http);
            if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } me && CookieSessions.Of(http.User) is { } sid) CookieSessions.End(http.RequestServices, me, sid);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        // Sitzungsliste: wo bin ich angemeldet (Browser); einzelne abmelden
        api.MapGet("/sessions", (HttpContext http) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var mine = CookieSessions.Of(http.User);
            var stamp = http.RequestServices.GetRequiredService<UserStore>().ById(id)!.Stamp;
            return Results.Ok(CookieSessions.List(http.RequestServices, id, stamp)
                .Select(e => new { e.Id, e.Created, e.Seen, e.Browser, e.Address, current = e.Id == mine }));
        }).RequireAuthorization();
        api.MapDelete("/sessions/{sid}", (string sid, HttpContext http) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (!CookieSessions.End(http.RequestServices, id, sid)) return Results.NotFound();
            SecurityNotice.Own(http.RequestServices, id, "Browser abgemeldet");
            return Results.Ok();
        }).RequireAuthorization();

        api.MapPost("/password", async (ChangeBody body, HttpContext http, UserStore users) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (Confirm(users, u, body.Old, null, needCode: false) is { } refused) return refused;
            if (body.New.Length < 8) return Results.BadRequest(new { error = "Mindestens 8 Zeichen." });
            // Neuer Stempel: alle anderen Sitzungen sind abgemeldet, diese bekommt ein neues Cookie
            users.Update(id, x => { x.PasswordHash = Passwords.Hash(body.New); x.NewStamp(); });
            CryptomatorPaths.LockAll(id);
            await Reissue(http, users, id);
            SecurityNotice.Important(http.RequestServices, id, "Passwort geändert");
            return Results.Ok();
        }).RequireAuthorization();

        // Eigenes Konto löschen: zuerst, was das für andere bedeutet
        api.MapGet("/delete-account/impact", (HttpContext http, UserStore users, DmOffice.Dav.FolderShareStore files, DmOffice.Pim.ShareStore pim, DmOffice.Notes.NoteShareStore notes) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            var mine = files.All().Where(s => s.Owner == id).Select(s => s.Members)
                .Concat(pim.All().Where(s => s.Owner == id).Select(s => s.Members))
                .Concat(notes.All().Where(s => s.Owner == id).Select(s => s.Members)).Where(m => m.Count > 0).ToList();
            var members = mine.SelectMany(m => m).Distinct().Select(m => users.ById(m)?.DisplayName).OfType<string>().OrderBy(n => n).ToList();
            return new { twoFactor = u.TwoFactor, lastAdmin = LastAdmin(users, u), shares = mine.Count, members, financeDocs = DmOffice.Finance.FinanceStore.IssuedCount(http.Space().Db) };
        }).RequireAuthorization();

        // Eigenes Konto löschen: Passwort und Zweitfaktor, dann sofort; das Aufräumen am Ziel folgt direkt danach (ErasedUsersJob)
        api.MapPost("/delete-account", async (PasswordBody body, HttpContext http, UserStore users, ILoggerFactory logs) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (http.User.FindFirstValue(DmOffice.Emergency.EmergencyApi.SessionClaim) != null)
                return Results.Json(new { error = "Im Notfallzugang lässt sich das Konto nicht löschen." }, statusCode: 403);
            if (LastAdmin(users, u)) return Results.BadRequest(new { error = "Du bist der einzige Admin. Lege zuerst einen weiteren Admin an." });
            if (Confirm(users, u, body.Password, body.Code, needCode: true) is { } refused) return refused;
            AdminApi.DeleteUser(http.RequestServices, id);
            logs.CreateLogger("DmOffice.Auth").LogWarning("Konto {Id} wurde von seinem Inhaber gelöscht", id);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).RequireAuthorization();

        // Überall abmelden: neuer Stempel - alle Sitzungen (Browser, Notfallsitzungen) sind abgemeldet, diese bekommt ein neues Cookie.
        // App-Passwörter bleiben (eigene Liste unter Geräte & Sync), offene Cryptomator-Tresore werden gesperrt.
        api.MapPost("/logout-all", async (HttpContext http, UserStore users) =>
        {
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            users.Update(id, x => x.NewStamp());
            CryptomatorPaths.LockAll(id);
            await Reissue(http, users, id);
            SecurityNotice.Own(http.RequestServices, id, "überall abgemeldet");
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
        // Gesperrte Adresse: ein gültiges App-Passwort kommt trotzdem durch (120 Bit Zufall - Raten ist aussichtslos, die Sperre
        // schützt hier nur vor Last). Sonst sperrte ein altes Gerät mit gelöschtem App-Passwort alle Geräte desselben Anschlusses aus.
        var blocked = Auth.Blocked(ip);

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
                // WebDAV der App: der Tresor ist je Gerät offen - das Gerät ist dieses App-Passwort
                if (dav) ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(Auth.DavPasswordClaim, appPasswordId));
                if (scope != null) ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(Auth.ScopeClaim, scope));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
            }
        }
        if (blocked) return Task.FromResult(AuthenticateResult.Fail("blocked"));
        var agent = Request.Headers.UserAgent.ToString();
        var shortAgent = agent.Length > 60 ? agent[..60] : agent;
        // Früher gültig (gelöscht): ein altes Gerät, kein Raten - festhalten, aber weder Fehlversuch noch Punkte, auch wenn
        // es gerade von einer neuen Adresse kommt (WLAN → Mobilfunk)
        string? old = null;
        if (user != null)
            try { old = AppPasswords.WasValid(_spaces.Get(user.Id).Db, decoded[(colon + 1)..]); }
            catch (UpdatePendingException) { }
        if (old != null)
        {
            SecurityCenter.Shared?.Note(ip, "altes-geraet", Request.Method, Request.Path.Value ?? "", agent, $"gelöschtes App-Passwort „{old}“ von „{user!.Login}“");
            return Task.FromResult(AuthenticateResult.Fail("revoked"));
        }
        // Nie vergeben: Raten oder fremder Name - Fehlversuch und verdächtig
        Auth.Failed(ip, $"App-Passwort für „{decoded[..colon]}“, {shortAgent}");
        SecurityCenter.Shared?.Suspicious(ip, "app-passwort", 2, Request.Method, Request.Path.Value ?? "", agent,
            user == null ? $"unbekannter Anmeldename „{(colon > 60 ? decoded[..60] : decoded[..colon])}“" : $"unbekanntes App-Passwort für „{user.Login}“");
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

    /// <summary>Grabsteine gelöschter App-Passwörter: nur die Prüfsumme und der Name - damit keins ein zweites Mal vergeben wird
    /// und eine Anmeldung damit als „altes Gerät“ erkennbar ist (gnädiger als ein erratenes).</summary>
    private static void EnsureRevoked(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS app_password_revoked (hash TEXT PRIMARY KEY, name TEXT NOT NULL, revoked TEXT NOT NULL)");

    /// <summary>War dieses App-Passwort einmal gültig (gelöscht)? Dann der Name, sonst null.</summary>
    public static string? WasValid(UserDb db, string token)
    {
        EnsureRevoked(db);
        return db.Scalar("SELECT name FROM app_password_revoked WHERE hash=$h", ("$h", Passwords.TokenHash(token))) as string;
    }

    public static string Create(UserDb db, string name, string? scope = null)
    {
        EnsureRevoked(db);
        var token = Passwords.NewToken();
        // Nie ein früheres wieder vergeben (bei 120 Bit Zufall ohnehin kaum möglich)
        while (db.Scalar("SELECT 1 FROM app_password_revoked WHERE hash=$h", ("$h", Passwords.TokenHash(token))) != null) token = Passwords.NewToken();
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

    /// <summary>Umbenennen: Der Name ist nur eine Bezeichnung für die Liste, am Zugang ändert sich nichts.</summary>
    public static void Rename(UserDb db, string id, string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("Bitte einen Namen angeben.");
        if (n.Length > 80) throw new ArgumentException("Der Name darf höchstens 80 Zeichen lang sein.");
        if (db.Exec("UPDATE app_passwords SET name=$n WHERE id=$i", ("$n", n), ("$i", id)) == 0) throw new KeyNotFoundException("App-Passwort nicht gefunden.");
    }

    /// <summary>
    /// Beim Koppeln nennt die Handy-App ihr Gerät (Hersteller und Modell). Steht noch der vorgegebene Name „Handy-App · Datum“ da,
    /// tritt das Gerät an die Stelle des Datums; einen selbst vergebenen Namen lässt das unberührt.
    /// </summary>
    public static void NameDevice(UserDb db, string id, string? device)
    {
        var d = System.Text.RegularExpressions.Regex.Replace(device ?? "", @"\s+", " ").Trim();
        if (d.Length == 0) return;
        if (d.Length > 60) d = d[..60];
        if (db.Scalar("SELECT name FROM app_passwords WHERE id=$i", ("$i", id)) is not string name) return;
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.+) · [\d./\-]+$");
        if (!m.Success) return;
        db.Exec("UPDATE app_passwords SET name=$n WHERE id=$i", ("$n", m.Groups[1].Value + " · " + d), ("$i", id));
    }

    public static void Delete(UserDb db, string id)
    {
        EnsureRevoked(db);
        db.Exec("INSERT OR IGNORE INTO app_password_revoked(hash, name, revoked) SELECT hash, name, $t FROM app_passwords WHERE id=$i",
            ("$t", DateTime.UtcNow.ToString("o")), ("$i", id));
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
        Touch(db, id);
        return id;
    }

    /// <summary>Zuletzt benutzt - hoechstens einmal je Stunde schreiben. Auch fuer die App ueber den Umschlag (E2e): dort
    /// kommt das App-Passwort nicht bei jeder Anfrage mit, sonst stuende sie in der Liste als lange unbenutzt.</summary>
    public static void Touch(UserDb db, string id)
    {
        if (Touched.TryGetValue(id, out var t) && DateTime.UtcNow - t <= TimeSpan.FromHours(1)) return;
        Touched[id] = DateTime.UtcNow;
        db.Exec("UPDATE app_passwords SET last_used=$t WHERE id=$i", ("$t", DateTime.UtcNow.ToString("o")), ("$i", id));
    }
}
