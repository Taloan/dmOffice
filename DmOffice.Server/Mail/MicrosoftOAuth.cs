using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>
/// Anmeldung bei privaten Microsoft-Konten (outlook.com, hotmail, live, msn): Microsoft nimmt dort
/// fuer IMAP und SMTP kein Passwort mehr an, nur OAuth2 (XOAUTH2).
///
/// Device-Code-Flow gegen /consumers: dmOffice holt einen Code, der Benutzer gibt ihn unter
/// microsoft.com/devicelogin ein und bestaetigt. Keine Redirect-URI, also gleich fuer jede
/// Installation. Vorgabe ist die App-Registrierung des Projekts ("dm Mailsuite", oeffentlicher Client,
/// die ID ist kein Geheimnis); DMOFFICE_MS_CLIENT_ID setzt eine eigene, "off" schaltet die Anmeldung ab.
///
/// Das Refresh-Token liegt im Konto (Einstellungen, SQLCipher-Datenbank des Benutzers). Access-Tokens
/// halten etwa eine Stunde und bleiben nur im Speicher. Microsoft gibt beim Erneuern meist ein neues
/// Refresh-Token aus - TokenRotated schreibt es zurueck.
/// </summary>
public sealed class MicrosoftOAuth
{
    /// <summary>App-Registrierung des Projekts (Entra ID, Konten aller Mandanten und persoenliche Konten).</summary>
    public const string ProjectClientId = "d4a96eec-f5f6-461d-9717-0294c0d9753e";

    /// <summary>Client-ID aus der Umgebung: leer = die des Projekts, "off"/"none" = keine Microsoft-Anmeldung.</summary>
    public static string? ClientIdFrom(string? env) => string.IsNullOrWhiteSpace(env) ? ProjectClientId
        : env.Trim().ToLowerInvariant() is "off" or "none" or "0" or "false" ? null : env.Trim();

    public const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    public const string Scopes = "offline_access https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send";

    /// <summary>Domains privater Microsoft-Konten - dort geht nur OAuth2.</summary>
    public static readonly HashSet<string> Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook.com", "outlook.de", "outlook.at", "outlook.fr", "outlook.it", "outlook.es",
        "hotmail.com", "hotmail.de", "hotmail.at", "hotmail.ch", "hotmail.co.uk", "hotmail.fr", "hotmail.it", "hotmail.es",
        "live.com", "live.de", "live.at", "live.co.uk", "live.fr", "live.it", "live.nl",
        "msn.com", "passport.com",
    };

    public static bool IsConsumerDomain(string email)
    {
        var at = email.LastIndexOf('@');
        return at > 0 && Domains.Contains(email[(at + 1)..].Trim());
    }

    private readonly HttpClient _http;
    public string? ClientId { get; }
    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Neues Refresh-Token: Konto, bisheriges Token (wie im Konto gespeichert), neues Token.</summary>
    public Action<string, string, string>? TokenRotated { get; set; }

    public MicrosoftOAuth(string? clientId, HttpClient http)
    {
        ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
        _http = http;
    }

    // ---------------- Token-Endpunkt ----------------

    private sealed record TokenResponse(string? AccessToken, string? RefreshToken, int ExpiresIn, string? Error, string? Description);

    private async Task<TokenResponse> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = ClientId ?? throw new InvalidOperationException("Microsoft-Anmeldung ist nicht eingerichtet (DMOFFICE_MS_CLIENT_ID).");
        using var res = await _http.PostAsync(Authority + "token", new FormUrlEncodedContent(form), ct);
        var json = await ReadJsonAsync(res, ct);
        string? S(string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var expires = json.TryGetProperty("expires_in", out var e) ? (e.ValueKind == JsonValueKind.Number ? e.GetInt32() : int.TryParse(e.GetString(), out var n) ? n : 3600) : 3600;
        return new TokenResponse(S("access_token"), S("refresh_token"), expires, S("error") ?? (res.IsSuccessStatusCode ? null : "http_" + (int)res.StatusCode), S("error_description"));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        try { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    /// <summary>Microsofts Fehlertext ohne Kennungen am Ende ("Trace ID: ..."), erste Zeile genuegt.</summary>
    private static string Short(string? description, string? error) =>
        (description ?? error ?? "").Split('\n', '\r')[0].Trim() is { Length: > 0 } s ? s : "unbekannter Fehler";

    // ---------------- Access-Token je Konto ----------------

    /// <summary>Eine Kette von Refresh-Tokens desselben Kontos. Gefunden wird sie unter dem aktuellen und dem
    /// vorigen Token - bis das neue in den Einstellungen steht, fragt das Konto noch mit dem alten.</summary>
    private sealed class Chain
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string Refresh = "";
        public string? Prior;
        public string? Access;
        public DateTime Expires;
    }

    private readonly ConcurrentDictionary<string, Chain> _chains = new();

    /// <summary>Gilt ein Access-Token noch so lange, wird es nicht erneuert.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(3);

    /// <summary>Access-Token fuer XOAUTH2, bei Bedarf mit dem Refresh-Token erneuert.</summary>
    public async Task<string> AccessTokenAsync(MailAccount account, CancellationToken ct)
    {
        var refresh = account.OAuthRefreshToken;
        if (string.IsNullOrEmpty(refresh))
            throw new MailKit.Security.AuthenticationException("Nicht bei Microsoft angemeldet – Konto bearbeiten und neu anmelden.");
        if (!Enabled)
            throw new MailKit.Security.AuthenticationException("Microsoft-Anmeldung ist auf diesem Server nicht eingerichtet (DMOFFICE_MS_CLIENT_ID).");

        var chain = _chains.GetOrAdd(refresh, t => new Chain { Refresh = t });
        string? rotated = null;
        await chain.Gate.WaitAsync(ct);
        try
        {
            if (chain.Access != null && chain.Expires - DateTime.UtcNow > Margin) return chain.Access;
            var r = await TokenAsync(new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = chain.Refresh,
                ["scope"] = Scopes,
            }, ct);
            if (r.Error != null || string.IsNullOrEmpty(r.AccessToken))
                throw new MailKit.Security.AuthenticationException(r.Error is "invalid_grant" or "interaction_required"
                    ? "Microsoft-Anmeldung abgelaufen – Konto bearbeiten und neu anmelden."
                    : "Microsoft-Anmeldung: " + Short(r.Description, r.Error));
            chain.Access = r.AccessToken;
            chain.Expires = DateTime.UtcNow.AddSeconds(r.ExpiresIn);
            if (!string.IsNullOrEmpty(r.RefreshToken) && r.RefreshToken != chain.Refresh)
            {
                if (chain.Prior != null) _chains.TryRemove(new KeyValuePair<string, Chain>(chain.Prior, chain));
                chain.Prior = chain.Refresh;
                chain.Refresh = r.RefreshToken;
                _chains[r.RefreshToken] = chain;
                rotated = r.RefreshToken;
            }
            return chain.Access;
        }
        finally
        {
            chain.Gate.Release();
            // Ausserhalb der Sperre: Speichern loest SettingsChanged aus, das darf wieder hierher fuehren
            if (rotated != null) TokenRotated?.Invoke(account.Id, refresh, rotated);
        }
    }

    /// <summary>Neues Refresh-Token ins Konto schreiben - nur, wenn dort noch das bisherige steht
    /// (sonst hat sich der Benutzer inzwischen neu angemeldet).</summary>
    public static bool Persist(IEnumerable<UserSpace> spaces, string accountId, string previous, string fresh)
    {
        foreach (var space in spaces)
        {
            if (!space.Settings.MailAccounts.Any(a => a.Id == accountId)) continue;
            var done = false;
            space.UpdateSettings(s =>
            {
                var a = s.MailAccounts.FirstOrDefault(x => x.Id == accountId);
                if (a == null || a.Auth != MailAuth.Microsoft || a.OAuthRefreshToken != previous) return;
                a.OAuthRefreshToken = fresh;
                done = true;
            });
            return done;
        }
        return false;
    }

    // ---------------- Device-Code-Flow ----------------

    public sealed record DeviceStart(string Flow, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

    private sealed class Flow
    {
        public required string UserId;
        public required string DeviceCode;
        public DateTime Expires;
        public string? Refresh;
    }

    private readonly ConcurrentDictionary<string, Flow> _flows = new();

    /// <summary>Anmeldung beginnen: Code fuer microsoft.com/devicelogin.</summary>
    public async Task<DeviceStart> StartAsync(string userId, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Microsoft-Anmeldung ist nicht eingerichtet (DMOFFICE_MS_CLIENT_ID).");
        foreach (var (key, old) in _flows)
            if (old.Expires < DateTime.UtcNow.AddMinutes(-30)) _flows.TryRemove(key, out _);

        using var res = await _http.PostAsync(Authority + "devicecode", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["scope"] = Scopes,
        }), ct);
        var json = await ReadJsonAsync(res, ct);
        string? S(string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int N(string name, int fallback) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
        if (S("device_code") is not { } device || S("user_code") is not { } code)
            throw new InvalidOperationException("Microsoft-Anmeldung: " + Short(S("error_description"), S("error") ?? "http_" + (int)res.StatusCode));

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var expiresIn = N("expires_in", 900);
        _flows[id] = new Flow { UserId = userId, DeviceCode = device, Expires = DateTime.UtcNow.AddSeconds(expiresIn) };
        return new DeviceStart(id, code, S("verification_uri") ?? "https://www.microsoft.com/link", expiresIn, Math.Max(1, N("interval", 5)));
    }

    /// <summary>Stand der Anmeldung: pending, ok, expired, denied oder ein Fehlertext.</summary>
    public async Task<string> PollAsync(string userId, string flowId, CancellationToken ct)
    {
        if (!_flows.TryGetValue(flowId, out var flow) || flow.UserId != userId) return "expired";
        if (flow.Refresh != null) return "ok";
        if (flow.Expires < DateTime.UtcNow) return "expired";
        var r = await TokenAsync(new()
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = flow.DeviceCode,
        }, ct);
        switch (r.Error)
        {
            case "authorization_pending" or "slow_down": return "pending";
            case "expired_token" or "bad_verification_code": return "expired";
            case "authorization_declined" or "access_denied": return "denied";
            case not null: return "Microsoft-Anmeldung: " + Short(r.Description, r.Error);
        }
        if (string.IsNullOrEmpty(r.RefreshToken)) return "Microsoft-Anmeldung: kein Refresh-Token erhalten (offline_access fehlt?)";
        flow.Refresh = r.RefreshToken;
        // Gleich nutzbar - der Verbindungstest danach braucht keine Erneuerung
        if (!string.IsNullOrEmpty(r.AccessToken))
            _chains[r.RefreshToken] = new Chain { Refresh = r.RefreshToken, Access = r.AccessToken, Expires = DateTime.UtcNow.AddSeconds(r.ExpiresIn) };
        return "ok";
    }

    /// <summary>Refresh-Token einer abgeschlossenen Anmeldung dieses Benutzers, sonst null.</summary>
    public string? Grant(string userId, string? flowId) =>
        flowId != null && _flows.TryGetValue(flowId, out var f) && f.UserId == userId ? f.Refresh : null;

    /// <summary>Nach dem Speichern: die Anmeldung wird nicht mehr gebraucht.</summary>
    public void Forget(string? flowId)
    {
        if (flowId != null) _flows.TryRemove(flowId, out _);
    }
}
