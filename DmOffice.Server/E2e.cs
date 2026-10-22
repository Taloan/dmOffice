using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DmOffice;

/// <summary>
/// Verschluesselter Umschlag zwischen App (spaeter auch Browser) und dmOffice, zusaetzlich zu TLS:
/// Proxy, Netz und TLS-Endpunkt sehen nur Chiffrat. ECDH P-256 (fuer WebCrypto) zwischen dem
/// dauerhaften Serverschluessel und einem Geraeteschluessel, HKDF-SHA256, AES-256-GCM.
///
///   GET  /api/e2e/key                      oeffentlicher Serverschluessel (auch im QR-Code)
///   POST /api/e2e/register {pubkey}        mit App-Passwort: Geraet anmelden -> {deviceId, serverKey}
///   POST /api/e2e/{userId}/{deviceId}      Umschlag: nonce(12) || AES-GCM(JSON-Anfrage) || tag(16)
///                                          Kopf X-DM-Counter: Zaehler gegen Wiederholung
///   Antwort: nonce(12) || AES-GCM(JSON-Antwort) || tag(16), AAD mit "resp"
///
/// Der entschluesselte Innenteil laeuft als gewoehnliche Anfrage ueber die Loopback-Adresse
/// durch die eigene Pipeline, angemeldet ueber ein einmaliges Sitzungszeichen (X-DM-E2E-Session).
/// </summary>
public static class E2e
{
    public const string Version = "dmoffice-e2e-v1";
    public const string SessionHeader = "X-DM-E2E-Session";
    public const string CounterHeader = "X-DM-Counter";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------- Kryptographie (rein, testbar) ----------------

    /// <summary>Oeffentlicher Schluessel als roher Punkt (0x04 || X || Y, 65 Byte), base64url - so liest WebCrypto "raw".</summary>
    public static string ExportPublic(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false);
        return Base64Url(new byte[] { 4 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray());
    }

    public static ECDiffieHellman ImportPublic(string raw)
    {
        var bytes = FromBase64Url(raw);
        if (bytes.Length != 65 || bytes[0] != 4) throw new ArgumentException("Ungültiger Schlüssel.");
        return ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = bytes[1..33], Y = bytes[33..65] } });
    }

    /// <summary>Gemeinsamer AES-Schluessel: HKDF(ECDH, salt = Geraeteschluessel || Serverschluessel, info = Version).</summary>
    public static byte[] DeriveKey(ECDiffieHellman mine, ECDiffieHellman theirs, string devicePub, string serverPub)
    {
        var shared = mine.DeriveRawSecretAgreement(theirs.PublicKey);
        var salt = FromBase64Url(devicePub).Concat(FromBase64Url(serverPub)).ToArray();
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Encoding.UTF8.GetBytes(Version));
    }

    public static byte[] Aad(string deviceId, long counter, bool response)
    {
        var sb = Version + "|" + deviceId + "|" + counter + (response ? "|resp" : "");
        return Encoding.UTF8.GetBytes(sb);
    }

    public static byte[] Seal(byte[] key, byte[] plain, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, aad);
        return nonce.Concat(cipher).Concat(tag).ToArray();
    }

    public static byte[] Open(byte[] key, byte[] sealed_, byte[] aad)
    {
        if (sealed_.Length < 28) throw new CryptographicException("zu kurz");
        var nonce = sealed_[..12];
        var tag = sealed_[^16..];
        var cipher = sealed_[12..^16];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, aad);
        return plain;
    }

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }

    /// <summary>Zaehlerfenster gegen Wiederholung: hoechster gesehener Wert, dahinter ein Fenster fuer verspaetete parallele Anfragen.</summary>
    public sealed class CounterWindow
    {
        private long _highest;
        private readonly long _floor;
        private readonly HashSet<long> _seen = new();
        private const int Window = 256;
        public long Highest => _highest;

        public CounterWindow(long highest) { _highest = highest; _floor = highest; }

        public bool Accept(long counter)
        {
            lock (_seen)
            {
                if (counter <= 0) return false;
                // Nach Neustart/Cache-Verlust ist _seen leer. Alles bis zum geladenen Stand gilt als schon
                // benutzt - sonst liessen sich die letzten bis zu 256 Umschlaege erneut einspielen.
                if (counter <= _floor) return false;
                if (counter > _highest)
                {
                    _highest = counter;
                    _seen.Add(counter);
                    _seen.RemoveWhere(c => c <= _highest - Window);
                    return true;
                }
                if (counter <= _highest - Window) return false;
                return _seen.Add(counter);
            }
        }
    }

    // ---------------- Serverschluessel ----------------

    public sealed class ServerKey
    {
        public ECDiffieHellman Key { get; }
        public string Public { get; }

        public ServerKey(DataPaths paths, Vault vault)
        {
            Directory.CreateDirectory(paths.Keys);
            var file = Path.Combine(paths.Keys, "e2e.key");
            if (File.Exists(file))
            {
                Key = ECDiffieHellman.Create();
                Key.ImportPkcs8PrivateKey(vault.Unwrap(File.ReadAllText(file).Trim()), out _);
            }
            else
            {
                Key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                File.WriteAllText(file, vault.Wrap(Key.ExportPkcs8PrivateKey()));
            }
            Public = ExportPublic(Key);
            Fingerprint = E2e.Fingerprint(Public);
        }

        /// <summary>Zum Vergleich mit der App (Koppeln ohne QR-Code, Umstieg auf den Umschlag), siehe <see cref="E2e.Fingerprint"/>.</summary>
        public string Fingerprint { get; }
    }

    /// <summary>
    /// Fingerabdruck eines oeffentlichen Schluessels zum Vergleichen mit dem Auge: die ersten 16 Byte von SHA-256 ueber
    /// den rohen Punkt, hex in Vierergruppen ("1A2B 3C4D ..."). Die App rechnet genauso (E2e.kt).
    /// </summary>
    public static string Fingerprint(string publicKey)
    {
        var hash = SHA256.HashData(FromBase64Url(publicKey));
        var hex = Convert.ToHexString(hash, 0, 16);
        return string.Join(" ", Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }

    // ---------------- Geraete und Sitzungen ----------------

    /// <summary>App-Geraet (AppPasswordId gesetzt) oder Browser (Stamp gesetzt, laeuft nach 30 Tagen ab).</summary>
    public sealed record Device(string Id, string? AppPasswordId, string? Stamp, string PubKey, long Counter, DateTime Created)
    {
        public bool Browser => AppPasswordId == null;
    }

    public static Device? FindDevice(UserDb db, string id) =>
        db.Query("SELECT id, app_password_id, stamp, pubkey, counter, created FROM e2e_devices WHERE id=$i",
            r => new Device(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetInt64(4),
                DateTime.TryParse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue), ("$i", id)).FirstOrDefault();

    public static string Register(UserDb db, string? appPasswordId, string? stamp, string pubKey)
    {
        ImportPublic(pubKey).Dispose();   // prueft das Format
        var id = Guid.NewGuid().ToString("N");
        db.Exec("INSERT INTO e2e_devices(id, app_password_id, stamp, pubkey, created, counter) VALUES($i,$a,$s,$p,$c,0)",
            ("$i", id), ("$a", appPasswordId), ("$s", stamp), ("$p", pubKey), ("$c", DateTime.UtcNow.ToString("o")));
        // Abgelaufene Browser-Geraete wegraeumen
        db.Exec("DELETE FROM e2e_devices WHERE app_password_id IS NULL AND created < $t", ("$t", DateTime.UtcNow.AddDays(-30).ToString("o")));
        return id;
    }

    public static void Unregister(UserDb db, string id) => db.Exec("DELETE FROM e2e_devices WHERE id=$i", ("$i", id));

    /// <summary>Mit dem App-Passwort fallen auch seine Geraeteschluessel.</summary>
    public static void DeleteForAppPassword(UserDb db, string appPasswordId)
    {
        db.Exec("DELETE FROM e2e_devices WHERE app_password_id=$a", ("$a", appPasswordId));
        foreach (var k in Keys.Keys.Where(k => k.EndsWith("/" + appPasswordId)).ToArray()) Keys.TryRemove(k, out _);
    }

    // Abgeleitete Schluessel und Zaehlerfenster je Geraet (Schluessel: userId/deviceId/appPasswordId)
    private static readonly ConcurrentDictionary<string, (byte[] Key, CounterWindow Window)> Keys = new();

    // Einmalige Sitzungszeichen fuer den Innenteil
    private sealed record Session(string UserId, string? AppPasswordId, DateTime Expires);
    private static readonly ConcurrentDictionary<string, Session> Sessions = new();

    /// <summary>Fuer den AppPasswordHandler: gilt das Sitzungszeichen (nur von der Loopback-Adresse)?</summary>
    public static (string UserId, string? AppPasswordId)? TakeSession(string token, IPAddress? remote)
    {
        if (remote == null || !IPAddress.IsLoopback(remote)) return null;
        if (!Sessions.TryRemove(token, out var s)) return null;
        if (s.Expires < DateTime.UtcNow) return null;
        return (s.UserId, s.AppPasswordId);
    }

    // ---------------- Endpunkte ----------------

    private sealed record RegisterBody(string PubKey);
    private sealed record Inner(string Method, string Path, Dictionary<string, string>? Headers, string? Body);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/e2e");

        api.MapGet("/key", (ServerKey key) => new { key = key.Public, version = Version }).AllowAnonymous();

        // App (mit App-Passwort) oder Browser (mit Cookie): Browser-Geraete haengen am Stempel der Anmeldung
        api.MapPost("/register", (RegisterBody body, HttpContext http, ServerKey key) =>
        {
            var appPasswordId = http.User.FindFirstValue(Auth.AppPasswordIdClaim);
            if (http.User.HasClaim(c => c.Type == Auth.EnvelopeClaim)) return Results.Json(new { error = "Nicht aus dem Umschlag heraus." }, statusCode: 403);
            var space = http.Space();
            string id;
            try { id = Register(space.Db, appPasswordId, appPasswordId == null ? space.User.Stamp : null, body.PubKey); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok(new { deviceId = id, userId = space.User.Id, serverKey = key.Public, version = Version });
        }).RequireAuthorization();

        // Abmelden im Browser (auch per sendBeacon beim Schliessen des Tabs)
        api.MapPost("/unregister/{deviceId}", (string deviceId, HttpContext http) =>
        {
            var space = http.Space();
            Unregister(space.Db, deviceId);
            // Nur Eintraege des eigenen Benutzers - sonst setzte eine fremde deviceId (sie steht in der URL) das
            // Zaehlerfenster eines anderen Geraets zurueck
            foreach (var k in Keys.Keys.Where(k => k.StartsWith(space.User.Id + "/" + deviceId + "/", StringComparison.Ordinal)).ToArray()) Keys.TryRemove(k, out _);
            return Results.Ok();
        }).RequireAuthorization().DisableAntiforgery();

        api.MapPost("/{userId}/{deviceId}", async (string userId, string deviceId, HttpContext http, ServerKey key, UserStore users, Spaces spaces,
            IServer server, IHttpClientFactory clients, CancellationToken ct) =>
        {
            // Eigener Zaehler fuer den Umschlag: Fehlversuche anderswo (DAV-Programm mit altem Passwort, Link mit falschem Kennwort)
            // sperren sonst die Oberflaeche derselben Adresse mit "429 Too Many Requests"
            var ip = "umschlag:" + Auth.IpKey(http.Connection.RemoteIpAddress);
            if (Auth.Blocked(ip)) return Results.StatusCode(429);
            var user = users.ById(userId);
            var space = user == null ? null : spaces.Get(user.Id);
            var device = space == null ? null : FindDevice(space.Db, deviceId);
            if (user == null || space == null || device == null) { Auth.Failed(ip, "Umschlag: Geraet unbekannt"); return Results.StatusCode(404); }
            // Browser-Geraet: nach Passwortwechsel (neuer Stempel) oder 30 Tagen ungueltig
            if (device.Browser && (device.Stamp != user.Stamp || device.Created < DateTime.UtcNow.AddDays(-30)))
            {
                Unregister(space.Db, deviceId);
                return Results.StatusCode(404);
            }
            if (!long.TryParse(http.Request.Headers[CounterHeader].ToString(), out var counter)) return Results.StatusCode(400);

            var cacheKey = $"{userId}/{deviceId}/{device.AppPasswordId ?? "browser"}";
            var entry = Keys.GetOrAdd(cacheKey, _ =>
            {
                using var pub = ImportPublic(device.PubKey);
                return (DeriveKey(key.Key, pub, device.PubKey, key.Public), new CounterWindow(device.Counter));
            });
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            byte[] plain;
            // Erst entschluesseln (AEAD prueft die Echtheit), dann den Zaehler verbrauchen. Sonst koennte eine
            // unechte Anfrage mit hohem Zaehler echte Anfragen des Geraets als "Replay" aussperren.
            try { plain = Open(entry.Key, ms.ToArray(), Aad(deviceId, counter, false)); }
            catch (CryptographicException) { Auth.Failed(ip, "Umschlag: nicht entschluesselbar"); return Results.StatusCode(400); }
            // Entschluesselt: ein angemeldetes Geraet - zaehlt nicht fuer die Bremse ohne Anmeldung
            http.Items[AnonThrottle.TrustedItem] = true;
            // Ein doppelt eingespielter Umschlag war echt (die Pruefung oben ist bestanden) - das ist kein Angriffsversuch und sperrt nichts
            if (!entry.Window.Accept(counter)) return Results.StatusCode(409);
            // Zaehler im Hintergrund festhalten: ein Schreibzugriff je Anfrage wartete sonst auf den Mail-Abgleich
            // (SQLite: nur ein Schreiber) - jeder Klick in der Oberflaeche dauerte dann Sekunden
            PersistCounter(space, deviceId, entry.Window);
            Auth.Succeeded(ip);

            var inner = JsonSerializer.Deserialize<Inner>(plain, Json) ?? throw new ArgumentException("Leer.");
            if (!inner.Path.StartsWith("/") || inner.Path.Contains("://")) return Results.StatusCode(400);
            // Kein Ausbrechen aus der erlaubten Freigabe ueber "..": weder roh noch kodiert. Der Innenteil liefe
            // sonst gegen einen anderen Pfad als den geprueften (z. B. /api/mail/../auth/...). Nur den Pfadteil
            // pruefen - ".." in einem Query-Wert (etwa ein Dateiname) ist harmlos. Die eigentliche Pruefung
            // laeuft danach auf dem kanonisierten Pfad, genau dem, den der Innenteil zu sehen bekommt.
            var rawPath = inner.Path.Split('?', 2)[0];
            var rawPathLower = rawPath.ToLowerInvariant();
            if (rawPath.Contains('\\') || rawPath.Contains("..") || rawPathLower.Contains("%2e") || rawPathLower.Contains("%2f") || rawPathLower.Contains("%5c"))
                return Results.StatusCode(400);
            var address = LocalAddress(server);
            Uri innerUri;
            try { innerUri = new Uri(new Uri(address), inner.Path); }
            catch (UriFormatException) { return Results.StatusCode(400); }
            // Muss auf dieselbe Loopback-Adresse zeigen (sonst waere der Pfad doch absolut gewesen)
            if (innerUri.Host != new Uri(address).Host || innerUri.Port != new Uri(address).Port) return Results.StatusCode(400);
            // App: nur die Freigabe des App-Passworts; Browser: alles unter /api wie mit dem Cookie
            var innerPath = innerUri.AbsolutePath;
            if (device.Browser ? !innerPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) : !Auth.AppPasswordAllowed(innerPath)) return Results.StatusCode(403);

            // Innenteil ueber Loopback durch die eigene Pipeline, angemeldet per einmaligem Zeichen
            var token = Base64Url(RandomNumberGenerator.GetBytes(24));
            Sessions[token] = new Session(user.Id, device.AppPasswordId, DateTime.UtcNow.AddSeconds(60));
            foreach (var (k, v) in Sessions.ToArray()) if (v.Expires < DateTime.UtcNow) Sessions.TryRemove(k, out _);

            // Gegen den kanonisierten Pfad senden - nicht gegen den rohen inner.Path
            using var req = new HttpRequestMessage(new HttpMethod(inner.Method.ToUpperInvariant()), address + innerUri.PathAndQuery);
            req.Headers.TryAddWithoutValidation(SessionHeader, token);
            // Browser-Geraet: das Anmelde-Cookie mitgeben, damit der Innenteil genau wie bisher laeuft (auth/state, Admin, Antiforgery)
            if (device.Browser && http.Request.Headers.Cookie.Count > 0) req.Headers.TryAddWithoutValidation("Cookie", http.Request.Headers.Cookie.ToString());
            // Kein X-Forwarded-For: der Innenteil muss als Loopback ankommen, sonst gilt das Sitzungszeichen nicht
            // (UseForwardedHeaders wuerde die Adresse sonst durch die des Handys ersetzen).
            // Aber oeffentliche Adresse und Schema weitergeben - sonst baut der Innenteil Links auf
            // http://127.0.0.1:8080 (Geraete & Sync, Ruecklinks in Termine).
            req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", http.Request.Scheme);
            req.Headers.TryAddWithoutValidation("X-Forwarded-Host", http.Request.Host.Value);
            var bodyBytes = inner.Body == null ? null : Convert.FromBase64String(inner.Body);
            if (bodyBytes != null || inner.Method.ToUpperInvariant() is "POST" or "PUT" or "PROPFIND" or "REPORT" or "PROPPATCH" or "MKCOL" or "MKCALENDAR")
                req.Content = new ByteArrayContent(bodyBytes ?? Array.Empty<byte>());
            if (inner.Headers != null)
                foreach (var (k, v) in inner.Headers)
                {
                    if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { if (req.Content != null) req.Content.Headers.TryAddWithoutValidation(k, v); }
                    else if (!k.Equals("Host", StringComparison.OrdinalIgnoreCase) && !k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        && !k.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
                        req.Headers.TryAddWithoutValidation(k, v);
                }
            using var client = clients.CreateClient("e2e");
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            var resBody = await res.Content.ReadAsByteArrayAsync(ct);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in res.Headers.Concat(res.Content.Headers)) headers[h.Key] = string.Join(", ", h.Value);
            headers.Remove("Transfer-Encoding");
            // Kein Set-Cookie durch den Umschlag: ein Cookie des Innenteils (z. B. aus einer Neuausstellung)
            // soll kein vollwertiges Anmelde-Cookie an das Geraet geben.
            headers.Remove("Set-Cookie");
            var outer = JsonSerializer.SerializeToUtf8Bytes(new { status = (int)res.StatusCode, headers, body = Convert.ToBase64String(resBody) }, Json);
            var sealedOut = Seal(entry.Key, outer, Aad(deviceId, counter, true));
            return Results.Bytes(sealedOut, "application/octet-stream");
        }).AllowAnonymous().DisableAntiforgery();
    }

    private static readonly ConcurrentDictionary<string, byte> PendingCounters = new();

    /// <summary>Hoechsten Zaehler eines Geraets gesammelt und ausserhalb der Anfrage speichern. Kurz nach einem Absturz
    /// koennte eine gerade abgefangene Anfrage einmal wiederholt werden - der Zeitraum ist eine halbe Sekunde.</summary>
    private static void PersistCounter(UserSpace space, string deviceId, CounterWindow window)
    {
        if (!PendingCounters.TryAdd(deviceId, 0)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            PendingCounters.TryRemove(deviceId, out _);
            try { space.Db.Exec("UPDATE e2e_devices SET counter=$c WHERE id=$i AND counter<$c", ("$c", window.Highest), ("$i", deviceId)); }
            catch (Exception) { /* naechste Anfrage versucht es wieder */ }
        });
    }

    private static string? _address;
    private static string LocalAddress(IServer server)
    {
        if (_address != null) return _address;
        var urls = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? Array.Empty<string>();
        var http = urls.FirstOrDefault(u => u.StartsWith("http://")) ?? urls.FirstOrDefault() ?? "http://127.0.0.1:8080";
        var uri = new Uri(http.Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1").Replace("://0.0.0.0", "://127.0.0.1").Replace("://[::]", "://127.0.0.1"));
        _address = $"{uri.Scheme}://127.0.0.1:{uri.Port}";
        return _address;
    }
}
