using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace DmOffice;

/// <summary>
/// Anmeldung über einen Identitätsanbieter (OpenID Connect, etwa Keycloak, Authentik, Pocket ID) als zusätzliche Anmeldemethode
/// im Web. Ablauf: Autorisierungscode mit PKCE, state und nonce; das ID-Token kommt direkt vom Token-Endpunkt und wird mit den
/// Schlüsseln des Anbieters (JWKS) geprüft. Zugeordnet wird über Anbieter und „sub“ (<see cref="UserRecord.OidcSubject"/>):
/// Bestehende Konten verknüpft man selbst in den Einstellungen (mit Passwort bzw. Code bestätigt, dann frisch beim Anbieter
/// angemeldet), die Verwaltung lädt per Einladungslink ein oder merkt eine (beim Anbieter bestätigte) E-Mail-Adresse vor; neue
/// Konten legt der Server nur an, wenn die Verwaltung das erlaubt. Ist eine Gruppe eingetragen, gilt sie bei jeder Anmeldung.
/// Hat das Konto eine Zwei-Faktor-Anmeldung und bestätigt der Anbieter keine eigene (amr), fragt dmOffice danach den Code ab.
/// </summary>
public static class Oidc
{
    private const string StateCookie = "dm_oidc", PurposeState = "dmoffice.oidc.state", PurposeId = "dmoffice.oidc.id", PurposeCode = "dmoffice.oidc.2fa";
    /// <summary>ID-Token der Anmeldung über den Anbieter - für das Abmelden dort (id_token_hint), gebunden an die Sitzung</summary>
    private const string IdCookie = "dm_oidc_id";
    /// <summary>Zwischen Anbieter und Code der Zwei-Faktor-Anmeldung (5 Minuten)</summary>
    private const string CodeCookie = "dm_oidc_2fa";
    public const string CallbackPath = "/api/auth/oidc/callback";
    private const string InvitePrefix = "invite:";

    public record SettingsBody(bool Enabled, string? Name, string? Issuer, string? ClientId, string? ClientSecret, bool AutoCreate, string? Group, bool AllowUnlink = false, bool LogoutAtProvider = true, bool OnlyOidc = false);
    public record UnlinkBody(string? Password, string? Old = null, string? Code = null);
    public record AdminLinkBody(string? Expected, bool Invite = false);
    public record LinkBody(string? Password, string? Code);
    public record CodeBody(string? Code);

    /// <summary>Konto, das die Verwaltung für eine beim Anbieter bestätigte E-Mail-Adresse vorgemerkt hat. Nur Konten, die noch
    /// nicht verknüpft sind, keine Administratoren, keine Einladungen. Ein Anmeldename beim Anbieter zählt nicht - den kann man
    /// dort je nach Einrichtung selbst wählen.</summary>
    public static UserRecord? Reserved(IEnumerable<UserRecord> users, string? email, bool emailVerified)
    {
        if (!emailVerified || string.IsNullOrWhiteSpace(email)) return null;
        return users.FirstOrDefault(u => u.OidcSubject == null && !u.IsAdmin && u.OidcExpected is { } x && x.Contains('@') && !x.StartsWith(InvitePrefix, StringComparison.Ordinal)
            && string.Equals(x.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Einladung (Einmal-Code der Verwaltung): gespeichert als „invite:Hash:Ablauf“.</summary>
    public static string InviteValue(string token, DateTimeOffset until) => $"{InvitePrefix}{Passwords.TokenHash(token)}:{until.ToUnixTimeSeconds()}";
    public static DateTimeOffset? InviteUntil(UserRecord u) =>
        u.OidcExpected is { } x && x.StartsWith(InvitePrefix, StringComparison.Ordinal) && long.TryParse(x[(x.LastIndexOf(':') + 1)..], out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
    public static UserRecord? Invited(IEnumerable<UserRecord> users, string token, DateTimeOffset now)
    {
        var hash = Passwords.TokenHash(token);
        return users.FirstOrDefault(u => u.OidcSubject == null && !u.IsAdmin && u.OidcExpected is { } x && x.StartsWith(InvitePrefix + hash + ":", StringComparison.Ordinal)
            && InviteUntil(u) is { } until && until > now);
    }

    /// <summary>Inhalt des kurzlebigen, verschlüsselten Cookies zwischen Start und Rückkehr vom Anbieter.</summary>
    /// <param name="Fresh">Frische Anmeldung beim Anbieter verlangt (Verknüpfen, Bestätigen): prompt=login, max_age=0, auth_time geprüft</param>
    private sealed record Pending(string State, string Nonce, string Verifier, string? LinkUser, long Expires, bool Reauth = false, string? Sid = null,
        string? Invite = null, string? Back = null, bool Fresh = false);
    /// <summary>Gerade beim Anbieter neu angemeldet (Bestätigung ohne eigenes Passwort und ohne 2FA): Benutzer → Zeitpunkt und Sitzung</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, string? Sid)> Reauths = new();
    private static readonly TimeSpan ReauthValid = TimeSpan.FromMinutes(10);
    /// <summary>Freigaben zum Verknüpfen (nach Bestätigung mit Passwort/Code): einmaliger Schlüssel → Benutzer, Sitzung, gültig bis</summary>
    private static readonly ConcurrentDictionary<string, (string User, string? Sid, DateTime Until)> LinkTokens = new();
    /// <summary>Zuordnen und Anlegen nacheinander (zwei gleichzeitige Rückkehrer legen sonst zwei Konten mit demselben sub an)</summary>
    private static readonly SemaphoreSlim AccountLock = new(1, 1);
    /// <summary>Wie lange eine Anmeldung beim Anbieter als „frisch“ gilt (auth_time)</summary>
    private static readonly TimeSpan FreshLogin = TimeSpan.FromMinutes(5);

    /// <summary>Angaben des Anbieters (.well-known/openid-configuration) und seine Schlüssel, je Aussteller kurz zwischengespeichert.</summary>
    private sealed record Provider(string Issuer, string Authorize, string Token, string Jwks, bool Basic, DateTime Fetched, string? EndSession = null);
    private static readonly Dictionary<string, Provider> Discovered = new();
    private static readonly Dictionary<string, (JsonElement Keys, DateTime Fetched)> KeySets = new();
    /// <summary>Letzter Fehlschlag je Aussteller - nicht bei jedem Aufruf erneut 15 s warten</summary>
    private static readonly Dictionary<string, DateTime> DiscoverFailed = new();
    /// <summary>Letztes erzwungenes Neuladen der Schlüssel je JWKS-Adresse (höchstens einmal je Minute)</summary>
    private static readonly Dictionary<string, DateTime> KeysForced = new();
    private static readonly object Lock = new();

    public static bool Ready(ServerConfig config) => Features.On(Features.Oidc) && config.Current.Oidc is { Enabled: true, Issuer.Length: > 0, ClientId.Length: > 0 };

    /// <summary>Beschriftung des Knopfs auf der Anmeldeseite, null wenn aus.</summary>
    /// <remarks>Allgemein gehalten („Anmelden mit OIDC“), unabhängig vom Anbieter.</remarks>
    public static string? ButtonName(ServerConfig config) => Ready(config) ? "OIDC" : null;

    /// <summary>Anmeldung nur über den Anbieter eingeschaltet - Anmeldename und Passwort gelten dann nur für Administratoren</summary>
    public static bool OnlyOidc(ServerConfig config) => Ready(config) && config.Current.Oidc.OnlyOidc;
    public static bool PasswordBlocked(ServerConfig config, UserRecord u) => OnlyOidc(config) && !u.IsAdmin;
    public const string OnlyOidcError = "Die Anmeldung ist nur über OIDC möglich.";

    private static string RedirectUri(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{CallbackPath}";

    // ---------------- Adressen ----------------

    /// <summary>Adresse im eigenen Netz (localhost, private Adressen, Namen ohne Punkt oder mit .local/.lan/.home.arpa/.internal):
    /// dort ist http erlaubt, sonst nur https.</summary>
    public static bool LocalHost(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || !host.Contains('.') && !host.Contains(':')) return true;
        if (host.EndsWith(".local") || host.EndsWith(".lan") || host.EndsWith(".home.arpa") || host.EndsWith(".internal") || host.EndsWith(".localhost")) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }
        return ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal;
    }

    /// <summary>Adresse beim Anbieter: https (http nur im eigenen Netz), ohne Benutzerangaben; issuer: zusätzlich ohne ? und #. null = in Ordnung, sonst der Grund.</summary>
    public static string? BadUrl(string? url, bool issuer)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return "Die Adresse ist ungültig.";
        if (u.Scheme != "https" && !(u.Scheme == "http" && LocalHost(u.Host)))
            return "Die Adresse muss mit https:// beginnen (http:// nur für Adressen im eigenen Netz).";
        if (u.UserInfo.Length > 0) return "Die Adresse darf keine Zugangsdaten enthalten.";
        if (u.Fragment.Length > 0 || (issuer && u.Query.Length > 0)) return "Die Aussteller-Adresse darf weder ? noch # enthalten.";
        return null;
    }

    private static async Task<Provider> Discover(IHttpClientFactory factory, string issuer, CancellationToken ct, bool fresh = false)
    {
        var key = issuer.TrimEnd('/');
        lock (Lock)
        {
            if (!fresh && Discovered.TryGetValue(key, out var p) && p.Fetched > DateTime.UtcNow.AddHours(-1)) return p;
            if (!fresh && DiscoverFailed.TryGetValue(key, out var f) && f > DateTime.UtcNow.AddSeconds(-30))
                throw new InvalidOperationException("Der Anbieter war eben nicht erreichbar.");
        }
        if (BadUrl(key, issuer: true) is { } bad) throw new InvalidOperationException(bad);
        try
        {
            using var client = factory.CreateClient("oidc");
            using var doc = JsonDocument.Parse(await client.GetStringAsync(key + "/.well-known/openid-configuration", ct));
            var r = doc.RootElement;
            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s
                : throw new InvalidOperationException($"Der Anbieter nennt kein „{name}“.");
            var basic = !r.TryGetProperty("token_endpoint_auth_methods_supported", out var m) || m.ValueKind != JsonValueKind.Array
                || m.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "client_secret_basic");
            var found = new Provider(S("issuer"), S("authorization_endpoint"), S("token_endpoint"), S("jwks_uri"), basic, DateTime.UtcNow,
                r.TryGetProperty("end_session_endpoint", out var es) && es.ValueKind == JsonValueKind.String ? es.GetString() : null);
            // Der Aussteller muss genau der eingetragene sein (sonst könnte ein fremdes Dokument Tokens eines anderen Ausstellers einschleusen)
            if (found.Issuer.TrimEnd('/') != key) throw new InvalidOperationException("Der Anbieter meldet einen anderen Aussteller als eingetragen (Schreibweise und / am Ende prüfen).");
            // Auch die Endpunkte nur verschlüsselt (bzw. im eigenen Netz)
            foreach (var u in new[] { found.Authorize, found.Token, found.Jwks, found.EndSession })
                if (u != null && BadUrl(u, issuer: false) != null) throw new InvalidOperationException("Der Anbieter nennt Endpunkte ohne https.");
            lock (Lock) { Discovered[key] = found; DiscoverFailed.Remove(key); }
            return found;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            lock (Lock) DiscoverFailed[key] = DateTime.UtcNow;
            throw;
        }
    }

    private static async Task<JsonElement> Keys(IHttpClientFactory factory, Provider p, bool fresh, CancellationToken ct)
    {
        lock (Lock)
        {
            if (KeySets.TryGetValue(p.Jwks, out var k))
            {
                if (!fresh && k.Fetched > DateTime.UtcNow.AddHours(-1)) return k.Keys;
                // Neu laden (unbekannte kid) höchstens einmal je Minute
                if (fresh && KeysForced.TryGetValue(p.Jwks, out var last) && last > DateTime.UtcNow.AddMinutes(-1)) return k.Keys;
            }
            if (fresh) KeysForced[p.Jwks] = DateTime.UtcNow;
        }
        using var client = factory.CreateClient("oidc");
        var keys = JsonDocument.Parse(await client.GetStringAsync(p.Jwks, ct)).RootElement.GetProperty("keys").Clone();
        if (keys.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Der Anbieter liefert keine Schlüsselliste.");
        lock (Lock) KeySets[p.Jwks] = (keys, DateTime.UtcNow);
        return keys;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    /// <summary>Signatur eines JWT mit einem Schlüssel aus dem JWKS prüfen (RS256/384/512, PS256/384/512, ES256/384/512).</summary>
    public static bool VerifySignature(string jwt, JsonElement keys)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            using var header = JsonDocument.Parse(UnB64(parts[0]));
            var alg = header.RootElement.TryGetProperty("alg", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            // Nur signierte Tokens (RS…, PS…, ES…); „none“ und HMAC (HS…, Geheimnis statt Schlüsselpaar) nicht
            if (alg.Length != 5 || alg[0] is not ('R' or 'P' or 'E')) return false;
            var kid = header.RootElement.TryGetProperty("kid", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            var data = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            var sig = UnB64(parts[2]);
            var hash = alg[^3..] switch { "256" => HashAlgorithmName.SHA256, "384" => HashAlgorithmName.SHA384, "512" => HashAlgorithmName.SHA512, _ => default };
            if (hash == default) return false;
            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object) continue;
                if (kid != null && key.TryGetProperty("kid", out var kk) && kk.GetString() != kid) continue;
                if (key.TryGetProperty("use", out var use) && use.GetString() is { } u && u != "sig") continue;
                // Steht am Schlüssel ein Algorithmus, muss er passen
                if (key.TryGetProperty("alg", out var ka) && ka.GetString() is { } kalg && kalg != alg) continue;
                var kty = key.TryGetProperty("kty", out var t) ? t.GetString() : null;
                try
                {
                    if (kty == "RSA" && alg[0] is 'R' or 'P')
                    {
                        using var rsa = RSA.Create(new RSAParameters { Modulus = UnB64(key.GetProperty("n").GetString()!), Exponent = UnB64(key.GetProperty("e").GetString()!) });
                        if (rsa.VerifyData(data, sig, hash, alg[0] == 'P' ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1)) return true;
                    }
                    else if (kty == "EC" && alg[0] == 'E')
                    {
                        var curve = key.GetProperty("crv").GetString() switch
                        {
                            "P-256" => ECCurve.NamedCurves.nistP256, "P-384" => ECCurve.NamedCurves.nistP384, "P-521" => ECCurve.NamedCurves.nistP521, _ => default,
                        };
                        using var ec = ECDsa.Create(new ECParameters { Curve = curve, Q = new ECPoint { X = UnB64(key.GetProperty("x").GetString()!), Y = UnB64(key.GetProperty("y").GetString()!) } });
                        if (ec.VerifyData(data, sig, hash)) return true;
                    }
                }
                catch (Exception e) when (e is CryptographicException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) { }
            }
            return false;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { return false; }
    }

    /// <summary>Angaben aus dem ID-Token, nach Prüfung von Aussteller, Empfänger (aud, azp), Ablauf (exp, nbf, iat) und nonce; null, wenn etwas nicht passt.</summary>
    public static JsonElement? CheckClaims(string jwt, string issuer, string clientId, string nonce, DateTimeOffset now)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return null;
        JsonElement c;
        try { c = JsonDocument.Parse(UnB64(parts[1])).RootElement.Clone(); }
        catch (Exception e) when (e is JsonException or FormatException) { return null; }
        if (c.ValueKind != JsonValueKind.Object) return null;
        string? S(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? N(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
        if (S("iss")?.TrimEnd('/') != issuer.TrimEnd('/')) return null;
        var aud = c.TryGetProperty("aud", out var a) ? a : default;
        var audList = aud.ValueKind == JsonValueKind.String ? [aud.GetString()]
            : aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList() : [];
        if (!audList.Contains(clientId)) return null;
        // Steht die autorisierte Partei (azp) im Token, muss es dmOffice sein - auch wenn dmOffice unter mehreren Empfängern steht
        if (c.TryGetProperty("azp", out _) && S("azp") != clientId) return null;
        var t = now.ToUnixTimeSeconds();
        if (N("exp") is not { } exp || exp < t - 60) return null;
        if (c.TryGetProperty("nbf", out _) && (N("nbf") is not { } nbf || nbf > t + 60)) return null;
        if (c.TryGetProperty("iat", out _) && (N("iat") is not { } iat || iat > t + 60)) return null;
        if (S("nonce") != nonce) return null;
        if (string.IsNullOrEmpty(S("sub"))) return null;
        return c;
    }

    /// <summary>Hat der Anbieter selbst einen zweiten Faktor bestätigt (amr)?</summary>
    public static bool ProviderMfa(JsonElement claims) =>
        claims.TryGetProperty("amr", out var amr) && amr.ValueKind == JsonValueKind.Array
        && amr.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() is "mfa" or "otp" or "hwk" or "totp" or "webauthn" or "fido");

    /// <summary>Gruppen aus dem Token („groups“; Keycloak schreibt sie je nach Einstellung mit führendem Schrägstrich).</summary>
    public static List<string> Groups(JsonElement claims) =>
        claims.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()?.TrimStart('/') ?? "" : "").Where(x => x.Length > 0).ToList() : [];

    /// <summary>Gilt die eingetragene Gruppe (falls eine eingetragen ist) für diese Anmeldung?</summary>
    public static bool InGroup(ServerConfig config, JsonElement claims) =>
        string.IsNullOrWhiteSpace(config.Current.Oidc.Group) || Groups(claims).Contains(config.Current.Oidc.Group.Trim().TrimStart('/'));

    /// <summary>Anmeldename für ein neues Konto: preferred_username, sonst der Teil der Mailadresse vor dem @, gesäubert und frei.</summary>
    public static string NewLogin(UserStore users, string? preferred, string? email)
    {
        var raw = (preferred ?? email?.Split('@')[0] ?? "benutzer").ToLowerInvariant();
        var clean = new string(raw.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-').ToArray()).Trim('-', '.', '_');
        if (clean.Length < 2) clean = "benutzer";
        if (clean.Length > 36) clean = clean[..36];
        var login = clean;
        for (var i = 2; users.ByLogin(login) != null; i++) login = clean + i;
        return login;
    }

    /// <summary>Anzeigename aus dem Token: ohne Steuer- und Formatzeichen (etwa Umkehr der Schreibrichtung), höchstens 80 Zeichen.</summary>
    public static string CleanName(string? name, string fallback)
    {
        var s = new string((name ?? "").Where(ch => char.GetUnicodeCategory(ch) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format)).ToArray()).Trim();
        if (s.Length > 80) s = s[..80].Trim();
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>Cookie-Name: bei https mit dem Präfix __Secure- (der Browser nimmt es dann nur verschlüsselt an).</summary>
    private static string CookieName(HttpContext http, string name) => http.Request.IsHttps ? "__Secure-" + name : name;

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider dp, string purpose) => dp.CreateProtector(purpose).ToTimeLimitedDataProtector();

    /// <summary>Hinweisseite im Aussehen der Anmeldeseite (hell oder dunkel nach Systemeinstellung); other: „Mit anderem Konto beim Anbieter anmelden“ anbieten.</summary>
    private static IResult Page(string title, string text, bool other = false) => Results.Content(
        "<!doctype html><html lang=\"de\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>dmOffice</title><style>"
        + ":root{color-scheme:light dark;--bg:#f3f3f3;--card:#fff;--fg:#242424;--muted:#616161;--line:#d1d1d1;--brand:#6b1fb5}"
        + "@media(prefers-color-scheme:dark){:root{--bg:#1b1b1f;--card:#29292e;--fg:#f0f0f0;--muted:#adadad;--line:#4a4a50}}"
        + "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:var(--bg);color:var(--fg);font-family:'Segoe UI',system-ui,sans-serif}"
        + ".card{background:var(--card);border-radius:8px;padding:28px 32px;max-width:400px;width:calc(100% - 32px);box-sizing:border-box;box-shadow:0 2px 8px rgba(0,0,0,.25)}"
        + ".logo{display:flex;align-items:center;gap:10px;font-size:24px;font-weight:600;margin-bottom:18px}.logo span{width:32px;height:32px;border-radius:7px;background:var(--brand);display:inline-block}"
        + "h1{font-size:18px;margin:0 0 8px}p{color:var(--muted);line-height:1.5;margin:0 0 18px}"
        + "a.b{display:block;text-align:center;padding:7px 12px;border-radius:4px;text-decoration:none;font-weight:600;margin-top:10px;border:1px solid var(--line);color:var(--fg)}"
        + "a.p{background:var(--brand);border-color:var(--brand);color:#fff}</style>"
        + $"<body><div class=\"card\"><div class=\"logo\"><span></span>dmOffice</div><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(text)}</p>"
        + (other ? "<a class=\"b p\" href=\"/api/auth/oidc/start?prompt=login\">Mit anderem Konto beim Anbieter anmelden</a>" : "")
        + $"<a class=\"b{(other ? "" : " p")}\" href=\"/\">Zur Anmeldeseite</a></div></body></html>",
        "text/html; charset=utf-8");

    private const string Unreachable = "Der Identitätsanbieter antwortet nicht wie erwartet. Bitte später noch einmal versuchen oder an die Verwaltung wenden.";

    /// <summary>Frische Bestätigung beim Anbieter verbrauchen (für <see cref="Auth.Confirm"/> bei Konten ohne eigenes Passwort): nur in derselben Sitzung, nur einmal.</summary>
    public static bool ConsumeReauth(string userId, string? sid) =>
        Reauths.TryGetValue(userId, out var r) && r.Sid == sid && DateTime.UtcNow - r.At < ReauthValid && Reauths.TryRemove(userId, out _);

    /// <summary>
    /// Beim Abmelden: War diese Sitzung über den Anbieter angemeldet (ID-Token im Cookie, gebunden an die Sitzung) und soll auch
    /// dort abgemeldet werden, die Adresse dafür (end_session_endpoint mit id_token_hint, zurück zur Anmeldeseite); sonst null.
    /// Das Cookie fällt in jedem Fall weg.
    /// </summary>
    public static async Task<string?> EndSessionUrl(HttpContext http, ServerConfig config, IHttpClientFactory factory, IDataProtectionProvider dp)
    {
        var name = CookieName(http, IdCookie);
        var raw = http.Request.Cookies[name];
        http.Response.Cookies.Delete(name, new CookieOptions { Path = "/api/auth", Secure = http.Request.IsHttps });
        if (raw == null || !Ready(config) || !config.Current.Oidc.LogoutAtProvider) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<IdStored>(Protector(dp, PurposeId).Unprotect(raw));
            // Nur die Sitzung, die sich über den Anbieter angemeldet hat - nicht eine spätere im selben Browser
            if (stored == null || stored.S != CookieSessions.Of(http.User)) return null;
            var p = await Discover(factory, config.Current.Oidc.Issuer!, http.RequestAborted);
            if (p.EndSession == null) return null;
            return p.EndSession + (p.EndSession.Contains('?') ? "&" : "?") + "id_token_hint=" + Uri.EscapeDataString(stored.T)
                + "&post_logout_redirect_uri=" + Uri.EscapeDataString($"{http.Request.Scheme}://{http.Request.Host}/")
                + "&client_id=" + Uri.EscapeDataString(config.Current.Oidc.ClientId!);
        }
        catch (Exception e) when (e is CryptographicException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException or FormatException) { return null; }
    }
    private sealed record IdStored(string T, string? S);
    private sealed record CodeStored(string U, string Subject, string IdToken);

    /// <summary>Anmelden nach geprüftem Token (und ggf. Code): Sitzung, Meldung bei neuem Browser, Cookie fürs Abmelden beim Anbieter.</summary>
    private static async Task SignIn(HttpContext http, UserRecord u, string idToken, IDataProtectionProvider dp, ServerConfig config)
    {
        var sid = await Auth.SignInNew(http, u, new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
        Auth.KnownBrowser(http, u);
        var value = Protector(dp, PurposeId).Protect(JsonSerializer.Serialize(new IdStored(idToken, sid)), TimeSpan.FromDays(30));
        // Sehr große Tokens (viele Gruppen) passen nicht in ein Cookie - dann ohne Abmelden beim Anbieter
        if (value.Length < 3800)
            http.Response.Cookies.Append(CookieName(http, IdCookie), value, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps, Path = "/api/auth", IsEssential = true, MaxAge = TimeSpan.FromDays(30),
            });
        SecurityNotice.Own(http.RequestServices, u.Id, $"angemeldet über {ButtonName(config)}");
    }

    /// <summary>Den Administratoren per Pukki mitteilen (neues Konto über den Anbieter).</summary>
    private static void TellAdmins(IServiceProvider services, string text)
    {
        var users = services.GetRequiredService<UserStore>();
        foreach (var a in users.All().Where(x => x.IsAdmin))
            try
            {
                var chat = services.GetRequiredService<ChatStore>();
                Pukki.Ensure(chat, a.Id);
                var room = Pukki.RoomOf(chat, a.Id) ?? chat.OpenDirect(a.Id, Pukki.Id);
                chat.Post(Pukki.Id, room, text);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { /* Nachricht ist nur ein Hinweis */ }
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/auth/oidc");

        // Start: zum Anbieter. link=Schlüssel (aus POST /link, nach Bestätigung): das eigene Konto verknüpfen; reauth=1: frisch
        // beim Anbieter bestätigen; invite=Code: Einladung der Verwaltung annehmen
        api.MapGet("/start", async (string? link, string? prompt, string? reauth, string? invite, string? back, HttpContext http, ServerConfig config, UserStore users,
            IHttpClientFactory factory, IDataProtectionProvider dp, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!Ready(config)) return Page("Anmeldung nicht verfügbar", "Die Anmeldung über einen Identitätsanbieter ist auf diesem Server nicht eingerichtet.");
            var me = http.User.Identity?.IsAuthenticated == true ? http.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            var sid = CookieSessions.Of(http.User);
            string? linkUser = null, inviteHash = null, backTo = null;
            var re = reauth == "1";
            if (re)
            {
                if (me == null) return Page("Bitte anmelden", "Zum Bestätigen zuerst anmelden.");
                linkUser = me;
                // Zurück zur Seite, von der die Bestätigung kam (nur Pfade innerhalb der App)
                if (back is { Length: > 2 and < 200 } b && b.StartsWith("#/", StringComparison.Ordinal) && b.All(ch => ch > ' ' && ch != '\\')) backTo = b;
            }
            else if (link != null)
            {
                // Nur mit Schlüssel aus der bestätigten Anfrage, nur in derselben Sitzung
                if (me == null || !LinkTokens.TryRemove(link, out var lt) || lt.User != me || lt.Sid != sid || lt.Until < DateTime.UtcNow)
                    return Page("Bitte neu beginnen", "Das Verknüpfen lässt sich nur aus den Einstellungen unter Sicherheit starten.");
                linkUser = me;
            }
            else if (invite != null)
            {
                if (invite.Length > 200 || Invited(users.All(), invite, DateTimeOffset.UtcNow) == null)
                    return Page("Einladung ungültig", "Der Einladungslink ist abgelaufen oder wurde schon verwendet. Bitte bei der Verwaltung einen neuen anfordern.");
                inviteHash = Passwords.TokenHash(invite);
            }
            var fresh = re || linkUser != null;
            Provider p;
            try { p = await Discover(factory, config.Current.Oidc.Issuer!, ct); }
            catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException or NotSupportedException or ArgumentException)
            {
                logs.CreateLogger("DmOffice.Oidc").LogWarning("OIDC: Anbieter nicht erreichbar: {Error}", e.Message);
                return Page("Anbieter nicht erreichbar", Unreachable);
            }
            var pending = new Pending(B64(RandomNumberGenerator.GetBytes(24)), B64(RandomNumberGenerator.GetBytes(24)), B64(RandomNumberGenerator.GetBytes(32)), linkUser,
                DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(), re, sid, inviteHash, backTo, fresh);
            http.Response.Cookies.Append(CookieName(http, StateCookie), Protector(dp, PurposeState).Protect(JsonSerializer.Serialize(pending), TimeSpan.FromMinutes(10)), new CookieOptions
            {
                // Lax: kommt bei der Rückkehr vom Anbieter (Navigation von einer anderen Seite) mit
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps, Path = "/api/auth/oidc", IsEssential = true, MaxAge = TimeSpan.FromMinutes(10),
            });
            var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(pending.Verifier)));
            var extra = new List<KeyValuePair<string, string>>();
            if (prompt == "login" || fresh) extra.Add(new("prompt", "login"));
            // Frische Anmeldung: der Anbieter muss auth_time mitliefern
            if (fresh) extra.Add(new("max_age", "0"));
            var url = p.Authorize + (p.Authorize.Contains('?') ? "&" : "?") + string.Join("&", new Dictionary<string, string>
            {
                ["response_type"] = "code", ["client_id"] = config.Current.Oidc.ClientId!, ["redirect_uri"] = RedirectUri(http),
                ["scope"] = "openid profile email", ["state"] = pending.State, ["nonce"] = pending.Nonce,
                ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            }.Concat(extra).Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
            return Results.Redirect(url);
        });

        api.MapGet("/callback", async (string? code, string? state, string? error, string? error_description, HttpContext http, ServerConfig config, UserStore users,
            IHttpClientFactory factory, IDataProtectionProvider dp, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("DmOffice.Oidc");
            if (!Ready(config)) return Page("Anmeldung nicht verfügbar", "Die Anmeldung über einen Identitätsanbieter ist auf diesem Server nicht eingerichtet.");
            var cookieName = CookieName(http, StateCookie);
            Pending? pending = null;
            try { if (http.Request.Cookies[cookieName] is { } raw) pending = JsonSerializer.Deserialize<Pending>(Protector(dp, PurposeState).Unprotect(raw)); }
            catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { }
            var stateOk = pending != null && pending.Expires >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() && state != null
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(pending.State));
            // Nur die passende Rückkehr beendet den laufenden Vorgang - ein fremder Link löscht ihn nicht
            if (stateOk) http.Response.Cookies.Delete(cookieName, new CookieOptions { Path = "/api/auth/oidc", Secure = http.Request.IsHttps });
            if (error != null)
            {
                // Text des Anbieters nur ins Protokoll (über einen Link ließe sich sonst beliebiger Text auf dieser Seite zeigen)
                log.LogInformation("OIDC: Anbieter meldet {Error}: {Description}", Cut(error, 60), Cut(error_description, 200));
                return Page("Anmeldung abgebrochen", "Die Anmeldung beim Anbieter wurde abgebrochen oder abgelehnt.");
            }
            if (!stateOk || string.IsNullOrEmpty(code)) return Page("Anmeldung abgelaufen", "Bitte die Anmeldung noch einmal von vorn beginnen.");

            var o = config.Current.Oidc;
            JsonElement claims;
            string idToken;
            try
            {
                var p = await Discover(factory, o.Issuer!, ct);
                using var client = factory.CreateClient("oidc");
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri(http), ["code_verifier"] = pending!.Verifier, ["client_id"] = o.ClientId!,
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, p.Token);
                if (config.OidcSecret() is { Length: > 0 } secret)
                {
                    if (p.Basic) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(o.ClientId!) + ":" + Uri.EscapeDataString(secret))));
                    else form["client_secret"] = secret;
                }
                req.Content = new FormUrlEncodedContent(form);
                using var res = await client.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode) { log.LogWarning("OIDC: Token-Endpunkt antwortet {Status}", (int)res.StatusCode); return Page("Anmeldung fehlgeschlagen", "Der Anbieter hat den Code nicht angenommen. Client-ID, Geheimnis und Weiterleitungsadresse prüfen."); }
                using var tok = JsonDocument.Parse(body);
                var it = tok.RootElement.TryGetProperty("id_token", out var itv) && itv.ValueKind == JsonValueKind.String ? itv.GetString() : null;
                if (it == null) return Page("Anmeldung fehlgeschlagen", "Der Anbieter hat kein ID-Token geliefert (Bereich „openid“ fehlt?).");
                var keys = await Keys(factory, p, false, ct);
                if (!VerifySignature(it, keys) && !VerifySignature(it, await Keys(factory, p, true, ct)))
                    return Page("Anmeldung fehlgeschlagen", "Die Signatur des Anbieters ließ sich nicht prüfen.");
                if (CheckClaims(it, p.Issuer, o.ClientId!, pending.Nonce, DateTimeOffset.UtcNow) is not { } c)
                    return Page("Anmeldung fehlgeschlagen", "Das Token des Anbieters passt nicht zu dieser Anmeldung (Aussteller, Empfänger, Gültigkeit).");
                claims = c;
                idToken = it;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException or FormatException
                or KeyNotFoundException or ArgumentException or NotSupportedException)
            {
                log.LogWarning("OIDC: {Error}", e.Message);
                return Page("Anbieter nicht erreichbar", Unreachable);
            }

            string? C(string n) => claims.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            // Verknüpfen und Bestätigen: Der Anbieter muss eine frische Anmeldung bestätigen (auth_time), nicht eine noch offene Sitzung dort
            if (pending.Fresh && !(claims.TryGetProperty("auth_time", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var atv)
                    && DateTimeOffset.FromUnixTimeSeconds(atv) > DateTimeOffset.UtcNow - FreshLogin))
                return Page("Nicht bestätigt", "Der Anbieter hat keine neue Anmeldung bestätigt. Bitte noch einmal versuchen und dort Anmeldedaten eingeben.");
            var subject = o.Issuer!.TrimEnd('/') + "|" + C("sub");
            var current = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var currentSid = CookieSessions.Of(http.User);

            UserRecord? linked;
            await AccountLock.WaitAsync(ct);
            try
            {
                linked = users.All().FirstOrDefault(u => u.OidcSubject == subject);

                // Bestätigung beim Anbieter: nur dasselbe, schon verknüpfte Konto, in derselben Sitzung
                if (pending.Reauth && pending.LinkUser is { } reUser)
                {
                    if (current != reUser || currentSid != pending.Sid || linked?.Id != reUser)
                        return Page("Nicht bestätigt", "Die Anmeldung beim Anbieter passt nicht zu diesem dmOffice-Konto.");
                    Reauths[reUser] = (DateTime.UtcNow, currentSid);
                    return Results.Redirect("/" + (pending.Back ?? "#/settings/security"));
                }
                // Verknüpfen aus den Einstellungen: nur das Konto, das den Vorgang bestätigt und gestartet hat, in derselben Sitzung
                if (pending.LinkUser is { } linkUser)
                {
                    if (current != linkUser || currentSid != pending.Sid) return Page("Bitte anmelden", "Zum Verknüpfen muss dasselbe Konto angemeldet sein.");
                    if (users.ById(linkUser) is not { } lu) return Page("Bitte anmelden", "Zum Verknüpfen muss dasselbe Konto angemeldet sein.");
                    if (lu.OidcSubject != null) return Page("Schon verknüpft", "Dein Konto ist bereits verknüpft. Um ein anderes Konto beim Anbieter zu verwenden, die Verknüpfung zuerst lösen.");
                    if (linked != null) return Page("Schon verknüpft", "Dieses Konto beim Anbieter ist bereits mit einem anderen dmOffice-Konto verknüpft.");
                    if (!InGroup(config, claims)) return Page("Kein Zugang", "Dein Konto beim Anbieter gehört nicht zur Gruppe, die für dmOffice freigegeben ist.");
                    users.Update(linkUser, x => { x.OidcSubject = subject; x.OidcExpected = null; });
                    SecurityNotice.Important(http.RequestServices, linkUser, $"Anmeldung über {ButtonName(config)} verknüpft");
                    return Results.Redirect("/#/settings/security");
                }

                // Einladung der Verwaltung: das eingeladene Konto, einmalig
                if (pending.Invite is { } inviteHash)
                {
                    var invited = users.All().FirstOrDefault(u => u.OidcSubject == null && !u.IsAdmin && u.OidcExpected is { } x
                        && x.StartsWith(InvitePrefix + inviteHash + ":", StringComparison.Ordinal) && InviteUntil(u) > DateTimeOffset.UtcNow);
                    if (invited == null) return Page("Einladung ungültig", "Der Einladungslink ist abgelaufen oder wurde schon verwendet. Bitte bei der Verwaltung einen neuen anfordern.");
                    if (linked != null) return Page("Schon verknüpft", "Dieses Konto beim Anbieter ist bereits mit einem anderen dmOffice-Konto verknüpft.");
                    if (!InGroup(config, claims)) return Page("Kein Zugang", "Dein Konto beim Anbieter gehört nicht zur Gruppe, die für dmOffice freigegeben ist.");
                    users.Update(invited.Id, x => { x.OidcSubject = subject; x.OidcExpected = null; });
                    SecurityNotice.Important(http.RequestServices, invited.Id, $"Anmeldung über {ButtonName(config)} verknüpft (Einladung)");
                    log.LogInformation("OIDC: Einladung für {Login} angenommen", invited.Login);
                    linked = users.ById(invited.Id);
                }

                // Von der Verwaltung für eine beim Anbieter bestätigte E-Mail-Adresse vorgemerkt: jetzt verknüpfen
                if (linked == null && InGroup(config, claims) && Reserved(users.All(), C("email"),
                        claims.TryGetProperty("email_verified", out var ev) && (ev.ValueKind == JsonValueKind.True || (ev.ValueKind == JsonValueKind.String && ev.GetString() == "true"))) is { } reserved)
                {
                    users.Update(reserved.Id, x => { x.OidcSubject = subject; x.OidcExpected = null; });
                    SecurityNotice.Important(http.RequestServices, reserved.Id, $"Anmeldung über {ButtonName(config)} verknüpft");
                    log.LogInformation("OIDC: vorgemerktes Konto {Login} verknüpft", reserved.Login);
                    linked = users.ById(reserved.Id);
                }

                // Eingetragene Gruppe gilt bei jeder Anmeldung - wer dort aus der Gruppe genommen wird, kommt nicht mehr herein
                if (!InGroup(config, claims)) return Page("Kein Zugang", "Dein Konto beim Anbieter gehört nicht zur Gruppe, die für dmOffice freigegeben ist.", other: true);

                if (linked == null)
                {
                    if (!o.AutoCreate) return Page("Kein Konto verknüpft", o.OnlyOidc
                        ? "Mit diesem Konto beim Anbieter ist kein dmOffice-Konto verknüpft. Bitte an einen Administrator wenden."
                        : "Mit diesem Konto beim Anbieter ist noch kein dmOffice-Konto verknüpft. Melde dich mit Anmeldename und Passwort an und verknüpfe es in den Einstellungen unter Sicherheit.", other: true);
                    var login = NewLogin(users, C("preferred_username"), C("email"));
                    // Ein zufälliges Passwort, das niemand kennt: angemeldet wird über den Anbieter; ein eigenes Passwort lässt sich später setzen
                    var created = users.Create(login, CleanName(C("name") ?? C("preferred_username"), login), Passwords.NewToken() + Passwords.NewToken(), isAdmin: false);
                    users.Update(created.Id, x => { x.OidcSubject = subject; x.NoOwnPassword = true; });
                    log.LogInformation("OIDC: neues Konto {Login} angelegt", login);
                    TellAdmins(http.RequestServices, $"👤 Über die Anmeldung mit OIDC wurde ein neues Konto angelegt: „{login}“. Es lässt sich in der Verwaltung unter Benutzer prüfen.");
                    linked = users.ById(created.Id);
                }
            }
            finally { AccountLock.Release(); }

            // Zwei-Faktor-Anmeldung am Konto und der Anbieter bestätigt keinen eigenen zweiten Faktor: Code abfragen
            if (linked!.TwoFactor && !ProviderMfa(claims))
            {
                http.Response.Cookies.Append(CookieName(http, CodeCookie),
                    Protector(dp, PurposeCode).Protect(JsonSerializer.Serialize(new CodeStored(linked.Id, subject, idToken)), TimeSpan.FromMinutes(5)), new CookieOptions
                    {
                        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, Path = "/api/auth/oidc", IsEssential = true, MaxAge = TimeSpan.FromMinutes(5),
                    });
                return Results.Redirect("/?oidc2fa=1");
            }
            await SignIn(http, linked, idToken, dp, config);
            return Results.Redirect("/");
        });

        // Code der Zwei-Faktor-Anmeldung nach der Rückkehr vom Anbieter
        api.MapPost("/2fa", async (CodeBody b, HttpContext http, ServerConfig config, UserStore users, IDataProtectionProvider dp) =>
        {
            if (!Ready(config)) return Results.NotFound();
            var name = CookieName(http, CodeCookie);
            CodeStored? stored = null;
            try { if (http.Request.Cookies[name] is { } raw) stored = JsonSerializer.Deserialize<CodeStored>(Protector(dp, PurposeCode).Unprotect(raw)); }
            catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { }
            if (stored == null || users.ById(stored.U) is not { } u || u.OidcSubject != stored.Subject)
                return Results.Json(new { error = "Die Anmeldung ist abgelaufen. Bitte noch einmal über OIDC anmelden." }, statusCode: 401);
            var key = "oidc2fa:" + u.Id;
            if (Auth.Blocked(key)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            if (!Auth.CheckSecondFactor(users, u, b.Code ?? ""))
            {
                Auth.Failed(key, "Code nach Anmeldung über OIDC");
                await Task.Delay(700);
                return Results.Json(new { error = "Code falsch oder abgelaufen.", twoFactor = true }, statusCode: 401);
            }
            Auth.Succeeded(key);
            http.Response.Cookies.Delete(name, new CookieOptions { Path = "/api/auth/oidc", Secure = http.Request.IsHttps });
            await SignIn(http, u, stored.IdToken, dp, config);
            return Results.Ok();
        });

        // Verknüpfen vorbereiten: mit Passwort und Code (ohne eigenes Passwort: Code bzw. Bestätigung beim Anbieter) bestätigt,
        // dann ein einmaliger Schlüssel für /start - eine übernommene Sitzung allein kann kein fremdes Anbieterkonto anhängen
        api.MapPost("/link", (LinkBody b, HttpContext http, UserStore users, ServerConfig config) =>
        {
            if (!Ready(config)) return Results.NotFound();
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (u.OidcSubject != null) return Results.BadRequest(new { error = "Dein Konto ist bereits verknüpft." });
            if (Auth.Confirm(users, u, b.Password, b.Code, needCode: true, http) is { } refused) return refused;
            foreach (var old in LinkTokens.Where(x => x.Value.Until < DateTime.UtcNow).Select(x => x.Key).ToList()) LinkTokens.TryRemove(old, out _);
            var token = B64(RandomNumberGenerator.GetBytes(24));
            LinkTokens[token] = (id, CookieSessions.Of(http.User), DateTime.UtcNow.AddMinutes(5));
            return Results.Ok(new { url = "/api/auth/oidc/start?link=" + Uri.EscapeDataString(token) });
        }).RequireAuthorization();

        // Verknüpfung lösen - nur, wenn die Verwaltung es erlaubt, und immer mit dem Passwort, das ab jetzt gilt
        api.MapPost("/unlink", async (UnlinkBody b, HttpContext http, UserStore users, ServerConfig config) =>
        {
            if (!config.Current.Oidc.AllowUnlink || OnlyOidc(config)) return Results.Json(new { error = "Die Verknüpfung kann nur die Verwaltung lösen." }, statusCode: 403);
            if ((b.Password ?? "").Length < 8) return Results.BadRequest(new { error = "Das neue Passwort braucht mindestens 8 Zeichen." });
            var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var u = users.ById(id)!;
            if (u.OidcSubject == null) return Results.BadRequest(new { error = "Dein Konto ist nicht verknüpft." });
            // Bestätigen: mit dem bisherigen Passwort (und Code), ohne eigenes Passwort mit dem Code oder einer frischen Anmeldung beim Anbieter
            if (Auth.Confirm(users, u, b.Old, b.Code, needCode: true, http) is { } refused) return refused;
            users.Update(id, x => { x.OidcSubject = null; x.NoOwnPassword = false; x.PasswordHash = Passwords.Hash(b.Password!); x.NewStamp(); });
            CryptomatorPaths.LockAll(id);
            await Auth.Reissue(http, users, id);
            SecurityNotice.Important(http.RequestServices, id, "Anmeldung über den Identitätsanbieter gelöst, eigenes Passwort festgelegt");
            return Results.Ok();
        }).RequireAuthorization();
        api.MapGet("/me", (HttpContext http, UserStore users, ServerConfig config) =>
        {
            var me = users.ById(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var sid = CookieSessions.Of(http.User);
            return Results.Ok(new
            {
                available = Ready(config), name = ButtonName(config), linked = me?.OidcSubject != null,
                allowUnlink = config.Current.Oidc.AllowUnlink && !OnlyOidc(config),
                hasPassword = me is { NoOwnPassword: false },
                // Anmeldung mit Anmeldename und Passwort möglich (eigenes Passwort, nicht „nur OIDC“)
                passwordLogin = me is { NoOwnPassword: false } && !PasswordBlocked(config, me),
                // Wie bestätigt wird (Verknüpfen, Lösen, Passwort, App-Passwörter …): Passwort (+ Code), nur Code oder Anmeldung beim Anbieter
                confirm = me is null ? "password" : Auth.ConfirmMode(me),
                confirmed = me != null && Reauths.TryGetValue(me.Id, out var ra) && ra.Sid == sid && DateTime.UtcNow - ra.At < ReauthValid,
            });
        }).RequireAuthorization();

        // Verwaltung: Anbieter einrichten und Verbindung prüfen; das Geheimnis geht nie zurück an den Browser
        var admin = app.MapGroup("/api/admin/oidc").RequireAuthorization("admin");
        admin.MapGet("", (HttpContext http, ServerConfig config) =>
        {
            var o = config.Current.Oidc;
            return Results.Ok(new { o.Enabled, o.Name, o.Issuer, o.ClientId, hasSecret = !string.IsNullOrEmpty(o.ClientSecret), o.AutoCreate, o.Group, o.AllowUnlink, o.LogoutAtProvider, o.OnlyOidc,
                redirectUri = RedirectUri(http), logoutUri = $"{http.Request.Scheme}://{http.Request.Host}/" });
        });
        admin.MapPut("", (SettingsBody b, ServerConfig config) =>
        {
            if (b.Enabled && (string.IsNullOrWhiteSpace(b.Issuer) || string.IsNullOrWhiteSpace(b.ClientId)))
                return Results.BadRequest(new { error = "Bitte Aussteller-Adresse und Client-ID angeben." });
            if (!string.IsNullOrWhiteSpace(b.Issuer) && BadUrl(b.Issuer, issuer: true) is { } bad) return Results.BadRequest(new { error = bad });
            config.Update(s =>
            {
                s.Oidc.Enabled = b.Enabled; s.Oidc.Name = b.Name?.Trim(); s.Oidc.Issuer = b.Issuer?.Trim().TrimEnd('/'); s.Oidc.ClientId = b.ClientId?.Trim();
                s.Oidc.AutoCreate = b.AutoCreate; s.Oidc.Group = string.IsNullOrWhiteSpace(b.Group) ? null : b.Group.Trim(); s.Oidc.AllowUnlink = b.AllowUnlink; s.Oidc.LogoutAtProvider = b.LogoutAtProvider; s.Oidc.OnlyOidc = b.OnlyOidc;
            });
            // Leer gelassen: bisheriges Geheimnis bleibt; „-“ löscht es
            if (b.ClientSecret == "-") config.SetOidcSecret(null);
            else if (!string.IsNullOrEmpty(b.ClientSecret)) config.SetOidcSecret(b.ClientSecret);
            lock (Lock) { Discovered.Clear(); KeySets.Clear(); DiscoverFailed.Clear(); KeysForced.Clear(); }
            return Results.NoContent();
        });
        // Für ein Konto vormerken: bestätigte E-Mail-Adresse beim Anbieter, oder Einladungslink (7 Tage, einmalig); leer: Vormerkung entfernen.
        // Administratorkonten verknüpfen sich nur selbst (mit Passwort und Code bestätigt).
        admin.MapPost("/users/{id}/link", (string id, AdminLinkBody b, UserStore users, HttpContext http) =>
        {
            var u = users.ById(id);
            if (u == null) return Results.NotFound();
            if (u.OidcSubject != null) return Results.BadRequest(new { error = "Das Konto ist bereits verknüpft." });
            var by = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            var expected = string.IsNullOrWhiteSpace(b.Expected) ? null : b.Expected.Trim();
            if ((b.Invite || expected != null) && u.IsAdmin)
                return Results.BadRequest(new { error = "Administratorkonten verknüpft die Person selbst unter Einstellungen → Sicherheit." });
            if (b.Invite)
            {
                var token = Passwords.NewToken();
                var until = DateTimeOffset.UtcNow.AddDays(7);
                users.Update(id, x => x.OidcExpected = InviteValue(token, until));
                SecurityNotice.ByAdmin(http.RequestServices, id, by, "Einladungslink für die Anmeldung über OIDC erstellt");
                return Results.Ok(new { url = $"{http.Request.Scheme}://{http.Request.Host}/api/auth/oidc/start?invite={Uri.EscapeDataString(token)}", until });
            }
            if (expected != null && (!expected.Contains('@') || expected.Length > 254 || expected.StartsWith(InvitePrefix, StringComparison.Ordinal)))
                return Results.BadRequest(new { error = "Bitte eine E-Mail-Adresse angeben. Für einen Anmeldenamen einen Einladungslink erstellen." });
            if (expected != null && users.All().Any(x => x.Id != id && x.OidcSubject == null && string.Equals(x.OidcExpected, expected, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = "Diese Adresse ist bereits für ein anderes Konto vorgemerkt." });
            users.Update(id, x => x.OidcExpected = expected);
            SecurityNotice.ByAdmin(http.RequestServices, id, by,
                expected == null ? "Vormerkung für die Anmeldung über OIDC entfernt" : $"Anmeldung über OIDC vorgemerkt für „{expected}“");
            return Results.Ok(new { url = (string?)null, until = (DateTimeOffset?)null });
        });
        // Verknüpfung lösen. Ohne eigenes Passwort (über den Anbieter angelegt) legt die Verwaltung dabei eins fest.
        admin.MapPost("/users/{id}/unlink", (string id, UnlinkBody b, UserStore users, HttpContext http) =>
        {
            var u = users.ById(id);
            if (u == null) return Results.NotFound();
            if (u.OidcSubject == null) return Results.BadRequest(new { error = "Das Konto ist nicht verknüpft." });
            var pw = b.Password ?? "";
            if (u.NoOwnPassword && pw.Length == 0) return Results.BadRequest(new { error = "Das Konto hat noch kein eigenes Passwort. Bitte ein Passwort festlegen." });
            if (pw.Length is > 0 and < 8) return Results.BadRequest(new { error = "Das Passwort braucht mindestens 8 Zeichen." });
            users.Update(id, x =>
            {
                x.OidcSubject = null; x.OidcExpected = null;
                if (pw.Length > 0) { x.PasswordHash = Passwords.Hash(pw); x.NoOwnPassword = false; }
                x.NewStamp();
            });
            CookieSessions.EndAll(http.RequestServices, id);
            if (pw.Length > 0) { Tresor.AdminReset(id); CryptomatorPaths.LockAll(id); }
            SecurityNotice.ByAdmin(http.RequestServices, id, http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                pw.Length > 0 ? "Anmeldung über OIDC gelöst, Passwort neu gesetzt" : "Anmeldung über OIDC gelöst");
            return Results.Ok();
        });
        // Verbindung prüfen - nur Adressen, die auch gespeichert werden dürften, und nur allgemeine Fehlertexte (kein Ausforschen des Netzes)
        admin.MapPost("/test", async (SettingsBody b, IHttpClientFactory factory, ILoggerFactory logs, CancellationToken ct) =>
        {
            var issuer = (b.Issuer ?? "").Trim().TrimEnd('/');
            if (BadUrl(issuer, issuer: true) is { } bad) return Results.Ok(new { ok = false, error = bad });
            try
            {
                var p = await Discover(factory, issuer, ct, fresh: true);
                var keys = await Keys(factory, p, true, ct);
                return Results.Ok(new { ok = true, issuer = p.Issuer, keys = keys.GetArrayLength() });
            }
            catch (InvalidOperationException e) when (e.Message.StartsWith("Der Anbieter", StringComparison.Ordinal) || e.Message.StartsWith("Die Adresse", StringComparison.Ordinal))
            {
                return Results.Ok(new { ok = false, error = e.Message });
            }
            catch (Exception e) when (e is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                logs.CreateLogger("DmOffice.Oidc").LogInformation("OIDC-Test {Issuer}: {Error}", issuer, e.Message);
                return Results.Ok(new { ok = false, error = "Unter dieser Adresse antwortet kein OIDC-Anbieter (.well-known/openid-configuration und Schlüssel nicht lesbar)." });
            }
        });
    }

    private static string Cut(string? s, int n) => s == null ? "" : (s.Length > n ? s[..n] : s).ReplaceLineEndings(" ");
}
