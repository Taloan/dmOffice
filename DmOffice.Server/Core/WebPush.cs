using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DmOffice.Core;

/// <summary>
/// Web Push (2.0): Benachrichtigungen im Browser bzw. in der installierten Web-App auch bei geschlossenem Fenster -
/// für Chat, neue Mail und Termin-Erinnerungen (siehe docs/web-push.md). Ohne fremdes Paket:
/// <list type="bullet">
/// <item>VAPID-Schlüssel (P-256) in data/keys/vapid.json, der private eingepackt mit dem Hauptschlüssel.</item>
/// <item>Abos je Benutzer in einer eigenen, verschlüsselten data/push.db - die Benutzerdatenbanken bleiben unberührt.</item>
/// <item>Inhalt verschlüsselt nach RFC 8291 (aes128gcm) - der Push-Dienst sieht ihn nicht; Absender nach RFC 8292 (VAPID).</item>
/// </list>
/// Gesendet wird nur an bekannte Push-Dienste (Google, Mozilla, Microsoft, Apple) - der Server ruft sonst keine
/// Adressen auf, die ein Browser ihm nennt.
/// </summary>
public sealed class WebPush
{
    public sealed record Subscription(string User, string Endpoint, string P256dh, string Auth, string Created, string? Agent);

    private static readonly string[] Services =
        ["fcm.googleapis.com", "updates.push.services.mozilla.com", ".notify.windows.com", ".push.apple.com"];

    private readonly string _connectionString;
    private readonly ECDsa _vapid;
    private readonly HttpClient _http;
    private readonly ILogger<WebPush>? _log;

    /// <summary>Öffentlicher VAPID-Schlüssel (unkomprimierter Punkt, Base64url) - für pushManager.subscribe.</summary>
    public string PublicKey { get; }

    public WebPush(DataPaths paths, Vault vault, IHttpClientFactory http, ILogger<WebPush> log)
        : this(Path.Combine(paths.Root, "push.db"), LoadKey(paths, vault), LoadVapid(paths, vault), http.CreateClient("webpush"), log) { }

    /// <summary>Für Tests: Datei, Schlüssel und Client direkt.</summary>
    public WebPush(string path, byte[] key, ECDsa vapid, HttpClient http, ILogger<WebPush>? log = null)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Password = "x'" + Convert.ToHexString(key) + "'", DefaultTimeout = 30 }.ToString();
        _vapid = vapid;
        _http = http;
        _log = log;
        PublicKey = B64(Point(vapid.ExportParameters(false)));
        using var conn = Open();
        Exec(conn, @"CREATE TABLE IF NOT EXISTS subs (endpoint TEXT PRIMARY KEY, user TEXT NOT NULL, p256dh TEXT NOT NULL, auth TEXT NOT NULL,
                     created TEXT NOT NULL, agent TEXT); CREATE INDEX IF NOT EXISTS subs_user ON subs(user);
                     CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
    }

    // ---------------- Schlüssel ----------------

    private static byte[] LoadKey(DataPaths paths, Vault vault)
    {
        Directory.CreateDirectory(paths.Keys);
        var file = Path.Combine(paths.Keys, "push.key");
        if (!File.Exists(file))
        {
            var fresh = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(file, vault.Wrap(fresh));
            return fresh;
        }
        return Unwrap(vault, file);
    }

    private sealed record VapidFile(string PublicKey, string PrivateKey);

    private static ECDsa LoadVapid(DataPaths paths, Vault vault)
    {
        Directory.CreateDirectory(paths.Keys);
        var file = Path.Combine(paths.Keys, "vapid.json");
        if (File.Exists(file))
        {
            var v = JsonSerializer.Deserialize<VapidFile>(File.ReadAllText(file))!;
            var pub = Unb64(v.PublicKey);
            return ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256, D = vault.Unwrap(v.PrivateKey),
                Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
            });
        }
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        File.WriteAllText(file, JsonSerializer.Serialize(new VapidFile(B64(Point(p)), vault.Wrap(p.D!))));
        return key;
    }

    private static byte[] Unwrap(Vault vault, string file)
    {
        var wrapped = File.ReadAllText(file).Trim();
        try { return vault.Unwrap(wrapped); }
        catch (CryptographicException) when (vault.Previous != null)
        {
            var key = Vault.Unwrap(vault.Previous, wrapped);
            File.WriteAllText(file, vault.Wrap(key));
            return key;
        }
    }

    // ---------------- Abos ----------------

    /// <summary>Konsistente, gleich verschluesselte Kopie (S3-Sicherung).</summary>
    public void SnapshotTo(string file) => S3Backup.Vacuum(_connectionString, file);
    internal string ConnectionString => _connectionString;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;");
        return conn;
    }

    private static int Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Nur bekannte Push-Dienste über HTTPS.</summary>
    public static bool Allowed(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort &&
        Services.Any(s => s.StartsWith('.') ? u.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase) : u.Host.Equals(s, StringComparison.OrdinalIgnoreCase));

    public void Subscribe(string user, string endpoint, string p256dh, string auth, string? agent)
    {
        if (!Allowed(endpoint)) throw new ArgumentException("Unbekannter Push-Dienst.");
        if (Unb64(p256dh).Length != 65 || Unb64(auth).Length != 16) throw new ArgumentException("Ungültiges Abo.");
        using var conn = Open();
        // Hoechstens 20 Geraete je Benutzer - die aeltesten fallen heraus
        Exec(conn, "DELETE FROM subs WHERE user=$u AND endpoint NOT IN (SELECT endpoint FROM subs WHERE user=$u ORDER BY created DESC LIMIT 19) AND endpoint<>$e",
            ("$u", user), ("$e", endpoint));
        Exec(conn, "INSERT INTO subs(endpoint, user, p256dh, auth, created, agent) VALUES($e,$u,$p,$a,$c,$g) " +
                   "ON CONFLICT(endpoint) DO UPDATE SET user=excluded.user, p256dh=excluded.p256dh, auth=excluded.auth, agent=excluded.agent",
            ("$e", endpoint), ("$u", user), ("$p", p256dh), ("$a", auth), ("$c", DateTime.UtcNow.ToString("o")), ("$g", agent?.Length > 300 ? agent[..300] : agent));
    }

    public void Unsubscribe(string user, string endpoint)
    {
        using var conn = Open();
        Exec(conn, "DELETE FROM subs WHERE endpoint=$e AND user=$u", ("$e", endpoint), ("$u", user));
    }

    /// <summary>Benutzer gelöscht: seine Abos weg.</summary>
    public void Forget(string user)
    {
        using var conn = Open();
        Exec(conn, "DELETE FROM subs WHERE user=$u", ("$u", user));
    }

    public List<Subscription> Of(string user)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user, endpoint, p256dh, auth, created, agent FROM subs WHERE user=$u";
        cmd.Parameters.AddWithValue("$u", user);
        using var r = cmd.ExecuteReader();
        var list = new List<Subscription>();
        while (r.Read()) list.Add(new Subscription(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    /// <summary>Wer überhaupt ein Abo hat - nur für die braucht es Arbeit.</summary>
    public HashSet<string> Users()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT user FROM subs";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>Absender-Adresse für VAPID (sub): die Adresse, unter der zuletzt jemand ein Abo angelegt hat.</summary>
    public string? Contact
    {
        get
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM kv WHERE key='origin'";
            return cmd.ExecuteScalar() as string;
        }
        set
        {
            if (value == null) return;
            using var conn = Open();
            Exec(conn, "INSERT INTO kv(key, value) VALUES('origin',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$v", value));
        }
    }

    // ---------------- Senden ----------------

    /// <summary>Benachrichtigung an alle Abos eines Benutzers: { title, body, url, tag }. Verschwundene Abos fallen weg.</summary>
    public async Task<int> Send(string user, string title, string body, string url, string tag, bool urgent = false, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { title, body = body.Length > 300 ? body[..300] + "…" : body, url, tag });
        var sent = 0;
        foreach (var s in Of(user))
        {
            try
            {
                using var req = Request(s, payload, urgent);
                using var res = await _http.SendAsync(req, ct);
                if ((int)res.StatusCode is 404 or 410) Unsubscribe(s.User, s.Endpoint);
                else if (res.IsSuccessStatusCode) sent++;
                else _log?.LogWarning("Web-Push {Host}: {Status}", new Uri(s.Endpoint).Host, (int)res.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log?.LogWarning("Web-Push {Host}: {Error}", new Uri(s.Endpoint).Host, ex.Message);
            }
        }
        return sent;
    }

    public HttpRequestMessage Request(Subscription s, byte[] payload, bool urgent)
    {
        var endpoint = new Uri(s.Endpoint);
        var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(Encrypt(payload, Unb64(s.P256dh), Unb64(s.Auth))) };
        req.Content.Headers.ContentType = new("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("aes128gcm");
        req.Headers.TryAddWithoutValidation("TTL", "86400");
        req.Headers.TryAddWithoutValidation("Urgency", urgent ? "high" : "normal");
        req.Headers.TryAddWithoutValidation("Authorization", $"vapid t={Jwt(endpoint.GetLeftPart(UriPartial.Authority))}, k={PublicKey}");
        return req;
    }

    /// <summary>VAPID (RFC 8292): JWT mit ES256, gültig 12 Stunden.</summary>
    private string Jwt(string audience)
    {
        var contact = Contact is { } c && c.StartsWith("https://") ? c : "mailto:dmoffice@localhost";
        var header = B64(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(), sub = contact }));
        var sig = _vapid.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256);   // IEEE P1363 (r||s) wie JWS
        return header + "." + claims + "." + B64(sig);
    }

    /// <summary>Inhalt verschlüsseln nach RFC 8291 (aes128gcm, ein Datensatz).</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? serverKey = null, byte[]? salt = null)
    {
        using var server = serverKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var asPublic = Point(server.ExportParameters(false));
        using var ua = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] } });
        var ecdhSecret = server.DeriveRawSecretAgreement(ua.PublicKey);
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        salt ??= RandomNumberGenerator.GetBytes(16);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var padded = Concat(plaintext, [2]);   // letzter Datensatz
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(cek, 16)) gcm.Encrypt(nonce, padded, cipher, tag);
        var rs = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rs, 4096);
        return Concat(salt, rs, [(byte)asPublic.Length], asPublic, cipher, tag);
    }

    private static byte[] Point(ECParameters p) => Concat([4], p.Q.X!, p.Q.Y!);
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Unb64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
        catch (FormatException) { return []; }
    }

    // ---------------- Endpunkte ----------------

    private sealed record SubscribeBody(string Endpoint, SubscribeKeys? Keys);
    private sealed record SubscribeKeys(string? P256dh, string? Auth);
    private sealed record EndpointBody(string Endpoint);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/push/web").RequireAuthorization();
        api.MapGet("/key", (WebPush push) => new { key = push.PublicKey });
        // Body wie PushSubscription.toJSON(): { endpoint, keys: { p256dh, auth } }
        api.MapPost("/subscribe", (SubscribeBody body, HttpContext http, WebPush push) =>
        {
            push.Subscribe(http.Space().User.Id, body.Endpoint, body.Keys?.P256dh ?? "", body.Keys?.Auth ?? "", http.Request.Headers.UserAgent.ToString());
            // Absender fuer VAPID: nur beim ersten Abo festlegen - nicht von jedem beliebigen Host-Kopf ueberschreiben lassen
            if (http.Request.IsHttps && push.Contact == null) push.Contact = $"https://{http.Request.Host}";
            return Results.Ok();
        });
        api.MapPost("/unsubscribe", (EndpointBody body, HttpContext http, WebPush push) =>
        {
            push.Unsubscribe(http.Space().User.Id, body.Endpoint);
            return Results.Ok();
        });
        api.MapPost("/test", async (HttpContext http, WebPush push, CancellationToken ct) =>
            new { sent = await push.Send(http.Space().User.Id, "dmOffice", "So sieht eine Benachrichtigung aus.", "/#/settings/notifications", "test", ct: ct) });
    }
}
