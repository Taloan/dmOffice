using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice;

/// <summary>
/// Push an die Handy-App ueber Firebase Cloud Messaging: dmOffice schickt nur ein inhaltsloses
/// Signal ("es gibt Neues"), die App holt die Daten selbst ueber den verschluesselten Umschlag.
///
/// Einrichtung: Dienstkonto-Schluessel (JSON aus der Firebase-Konsole) als data/keys/fcm.json oder
/// Pfad in DMOFFICE_FCM_CREDENTIALS. Ohne Schluessel ist alles still - die App fragt dann wie bisher
/// alle 15 Minuten.
/// </summary>
public static class Push
{
    // ---------------- Tokens je App-Passwort ----------------

    public sealed record Token(string Value, string AppPasswordId, string Created);

    public static void Register(UserDb db, string appPasswordId, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) throw new ArgumentException("Ungültiges Token.");
        db.Exec("INSERT OR REPLACE INTO push_tokens(token, app_password_id, created) VALUES($t,$a,$c)",
            ("$t", token.Trim()), ("$a", appPasswordId), ("$c", DateTime.UtcNow.ToString("o")));
    }

    public static List<Token> Tokens(UserDb db) =>
        db.Query("SELECT token, app_password_id, created FROM push_tokens", r => new Token(r.GetString(0), r.GetString(1), r.GetString(2)));

    public static void Remove(UserDb db, string token) => db.Exec("DELETE FROM push_tokens WHERE token=$t", ("$t", token));

    /// <summary>
    /// Fingerabdruck des Benutzerstands: aendert sich, sobald sich Mail, Kalender/Aufgaben/Kontakte
    /// oder Notizen aendern. Billig genug fuer eine Abfrage alle paar Sekunden.
    /// </summary>
    public static string Fingerprint(UserDb db, long mailVersion)
    {
        var pim = db.Scalar("SELECT COALESCE(SUM(synctoken),0) FROM collections");
        var notes = db.Scalar("SELECT COALESCE(SUM(sync_rev),0) || '/' || COUNT(*) FROM notes");
        return $"{mailVersion}|{pim}|{notes}";
    }

    // ---------------- Endpunkt ----------------

    private sealed record RegisterBody(string Token);

    public static void Map(WebApplication app)
    {
        // Nur fuer die Handy-App (App-Passwort, direkt oder im Umschlag): das Token haengt am App-Passwort
        // und faellt mit dessen Widerruf
        app.MapPost("/api/push/register", (RegisterBody body, HttpContext http) =>
        {
            var appPasswordId = http.User.FindFirstValue(Auth.AppPasswordIdClaim);
            if (string.IsNullOrEmpty(appPasswordId)) return Results.Json(new { error = "Nur für die Handy-App." }, statusCode: 403);
            try { Register(http.Space().Db, appPasswordId, body.Token); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok(new { enabled = http.RequestServices.GetRequiredService<Fcm>().Configured });
        }).RequireAuthorization();
    }

    // ---------------- Waechter ----------------

    /// <summary>Schaut alle paar Sekunden je Benutzer mit Handy nach Aenderungen und schickt dann ein Signal.</summary>
    public sealed class Watcher(Spaces spaces, MailSync mail, Fcm fcm, ILogger<Watcher> log) : BackgroundService
    {
        private readonly ConcurrentDictionary<string, string> _seen = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastSent = new();

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            if (!fcm.Configured) { log.LogInformation("Push: kein FCM-Schluessel, Handy fragt selbst ab"); return; }
            while (!stop.IsCancellationRequested)
            {
                try { await Round(stop); }
                catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogWarning("Push: {Error}", ex.Message); }
                try { await Task.Delay(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { }
            }
        }

        private async Task Round(CancellationToken ct)
        {
            foreach (var space in spaces.All())
            {
                var tokens = Tokens(space.Db);
                if (tokens.Count == 0) continue;
                var id = space.User.Id;
                var fp = Fingerprint(space.Db, mail.Version(id));
                var before = _seen.GetValueOrDefault(id);
                _seen[id] = fp;
                if (before == null || before == fp) continue;   // erste Runde merkt sich nur den Stand
                // Hoechstens ein Signal je 15 Sekunden - die App holt dann ohnehin alles
                if (_lastSent.TryGetValue(id, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(15)) { _seen[id] = before; continue; }
                _lastSent[id] = DateTime.UtcNow;
                foreach (var tok in tokens)
                {
                    var result = await fcm.SendSync(tok.Value, ct);
                    if (result == Fcm.Result.Unregistered) Remove(space.Db, tok.Value);
                }
            }
        }
    }
}

/// <summary>Versand ueber die FCM-HTTP-v1-Schnittstelle mit einem Dienstkonto (OAuth2-JWT, RS256).</summary>
public sealed class Fcm
{
    public enum Result { Sent, Unregistered, Failed }

    private sealed record ServiceAccount(string project_id, string client_email, string private_key, string token_uri);

    private readonly ServiceAccount? _sa;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<Fcm> _log;
    private string? _accessToken;
    private DateTime _accessExpires;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool Configured => _sa != null;

    public Fcm(DataPaths paths, IHttpClientFactory http, ILogger<Fcm> log)
    {
        _http = http;
        _log = log;
        var file = Environment.GetEnvironmentVariable("DMOFFICE_FCM_CREDENTIALS") is { Length: > 0 } p ? p : Path.Combine(paths.Keys, "fcm.json");
        if (!File.Exists(file)) return;
        try
        {
            _sa = JsonSerializer.Deserialize<ServiceAccount>(File.ReadAllText(file));
            if (_sa == null || string.IsNullOrEmpty(_sa.private_key) || string.IsNullOrEmpty(_sa.client_email)) _sa = null;
        }
        catch (Exception ex) { log.LogWarning("FCM-Schluessel {File} unlesbar: {Error}", file, ex.Message); _sa = null; }
    }

    /// <summary>JWT fuer den Google-Token-Endpunkt, signiert mit dem Dienstkonto (RS256). Oeffentlich fuer Tests.</summary>
    public static string BuildJwt(string clientEmail, string privateKeyPem, string tokenUri, DateTime now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var iat = new DateTimeOffset(now).ToUnixTimeSeconds();
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = clientEmail, ["scope"] = "https://www.googleapis.com/auth/firebase.messaging",
            ["aud"] = tokenUri, ["iat"] = iat, ["exp"] = iat + 3600,
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + claims + "." + B64(sig);
    }

    private async Task<string> AccessToken(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _accessExpires) return _accessToken;
            var jwt = BuildJwt(_sa!.client_email, _sa.private_key, _sa.token_uri ?? "https://oauth2.googleapis.com/token", DateTime.UtcNow);
            using var client = _http.CreateClient("fcm");
            using var res = await client.PostAsync(_sa.token_uri ?? "https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = jwt,
            }), ct);
            var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Google-Token: " + json.ToString());
            _accessToken = json.GetProperty("access_token").GetString();
            _accessExpires = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32() - 120);
            return _accessToken!;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Datennachricht "sync" mit hoher Prioritaet - weckt die App, ohne etwas anzuzeigen.</summary>
    public async Task<Result> SendSync(string token, CancellationToken ct)
    {
        if (_sa == null) return Result.Failed;
        try
        {
            var access = await AccessToken(ct);
            using var client = _http.CreateClient("fcm");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{_sa.project_id}/messages:send");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                message = new { token, data = new Dictionary<string, string> { ["t"] = "sync" }, android = new { priority = "high", ttl = "300s" } }
            }), Encoding.UTF8, "application/json");
            using var res = await client.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return Result.Sent;
            var body = await res.Content.ReadAsStringAsync(ct);
            // Token ungueltig (App deinstalliert, Daten geloescht): weg damit
            if ((int)res.StatusCode == 404 || body.Contains("UNREGISTERED") || body.Contains("INVALID_ARGUMENT")) return Result.Unregistered;
            _log.LogWarning("FCM {Status}: {Body}", (int)res.StatusCode, body.Length > 300 ? body[..300] : body);
            return Result.Failed;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("FCM: {Error}", ex.Message);
            return Result.Failed;
        }
    }
}
