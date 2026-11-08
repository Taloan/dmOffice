using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

namespace DmOffice.Core;

/// <summary>
/// Security Center (docs/security-center-konzept.md): dmOffice ist keine Webseite für beliebige Besucher. Ohne Anmeldung gibt
/// es nur eine feste Menge von Wegen (Oberfläche, Anmelden und Koppeln, Freigabelinks, DAV und Nextcloud mit eigener
/// Anmeldung, Rückrufe) - jeder andere Pfad ohne irgendein Anmeldemerkmal ist ein Abtasten und sperrt die Adresse sofort.
/// Drei Klassen: <b>Angriff</b> (unbekannter Pfad ohne Anmeldemerkmal, Scanner im User-Agent) - sperren; <b>verdächtig</b>
/// (bekannter Weg mit falschen Daten: Anmeldung, App-Passwort, Link-Token) - zählt, sperrt erst bei vielen; <b>normal</b>
/// (Erlaubtes, abgelaufene Sitzung mit Cookie, Adressen mit einer gültigen Anmeldung in letzter Zeit).
/// Sperrliste, Erlaubt-Liste und jedes einzelne Ereignis liegen in data/security.db (SQLCipher, Schlüssel unter keys/).
/// </summary>
public sealed partial class SecurityCenter
{
    public static SecurityCenter? Shared { get; set; }

    public sealed record Block(string Key, string Reason, DateTime Since, DateTime? Until, int Level, long Hits, DateTime Last, string? By, string? Note);
    public sealed record AllowEntry(string Cidr, string? Note, DateTime Added);
    public sealed record Event(long Id, DateTime At, string Key, string Kind, string Method, string Path, string? Agent, string? Detail);
    /// <summary>Abuse*: AbuseIPDB (SecurityAbuse.cs) - prüfen ab Schwelle sperren, Sperrdauer in Tagen, Fallen melden.</summary>
    public sealed record Settings(bool Traps = true, int FirstDays = 7, int SeenDays = 30, int KeepDays = 90,
        bool AbuseCheck = false, int AbuseScore = 90, int AbuseDays = 7, bool AbuseReport = false);

    /// <summary>Immer erlaubt: eigener Rechner, private Netze (Heimnetz, Docker), Tailscale, IPv6 lokal - aus diesen Netzen
    /// kommt kein fremder Scanner, und ein Fehler darin sperrte sonst die eigenen Geräte aus.</summary>
    public static readonly string[] BuiltIn = ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "169.254.0.0/16", "fc00::/7", "fe80::/10"];
    private static readonly IPNetwork[] BuiltInNets = BuiltIn.Select(IPNetwork.Parse).ToArray();

    /// <summary>Verdächtiges: so viele Punkte innerhalb einer Stunde sperren für einen Tag (Anmeldung 2, Link-Token 3).</summary>
    public const int SuspiciousLimit = 30;
    /// <summary>Gesperrte Anfragen: nur die ersten so vielen je Sperre einzeln ins Protokoll, danach nur noch gezählt.</summary>
    private const int HitsLogged = 20;

    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, Block> _blocks = new();
    private readonly ConcurrentDictionary<string, long> _pendingHits = new();
    private readonly ConcurrentDictionary<string, DateTime> _seen = new();
    private readonly ConcurrentDictionary<string, DateTime> _seenWritten = new();
    private readonly ConcurrentDictionary<string, (int Points, DateTime Since)> _suspicious = new();
    private volatile IPNetwork[] _allow = [];
    private volatile Settings _settings = new();

    static SecurityCenter() => SQLitePCL.Batteries_V2.Init();

    public SecurityCenter(DataPaths paths, Vault vault) : this(Path.Combine(paths.Root, "security.db"), LoadKey(paths, vault)) { }

    internal SecurityCenter(string path, byte[] key)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Password = "x'" + Convert.ToHexString(key) + "'", DefaultTimeout = 30 }.ToString();
        using (var conn = Open())
            UserDb.Exec(conn, @"
CREATE TABLE IF NOT EXISTS blocks (key TEXT PRIMARY KEY, reason TEXT NOT NULL, since INTEGER NOT NULL, until INTEGER NULL, level INTEGER NOT NULL,
    hits INTEGER NOT NULL DEFAULT 0, last INTEGER NOT NULL, by TEXT NULL, note TEXT NULL);
CREATE TABLE IF NOT EXISTS allow (cidr TEXT PRIMARY KEY, note TEXT NULL, added INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, key TEXT NOT NULL, kind TEXT NOT NULL,
    method TEXT NOT NULL, path TEXT NOT NULL, agent TEXT NULL, detail TEXT NULL);
CREATE INDEX IF NOT EXISTS events_at ON events(at);
CREATE INDEX IF NOT EXISTS events_key ON events(key);
CREATE TABLE IF NOT EXISTS seen (key TEXT PRIMARY KEY, last INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS reputation (key TEXT PRIMARY KEY, score INTEGER NOT NULL, at INTEGER NOT NULL);");
        Reload();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }

    private static byte[] LoadKey(DataPaths paths, Vault vault)
    {
        Directory.CreateDirectory(paths.Keys);
        var file = Path.Combine(paths.Keys, "security.key");
        if (!File.Exists(file))
        {
            var fresh = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(file, vault.Wrap(fresh));
            return fresh;
        }
        var wrapped = File.ReadAllText(file).Trim();
        try { return vault.Unwrap(wrapped); }
        catch (CryptographicException) when (vault.Previous != null)
        {
            var key = Vault.Unwrap(vault.Previous, wrapped);
            File.WriteAllText(file, vault.Wrap(key));
            return key;
        }
    }

    private static long Unix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();
    private static DateTime Time(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    private void Reload()
    {
        using var conn = Open();
        _blocks.Clear();
        foreach (var b in UserDb.Query(conn, "SELECT key, reason, since, until, level, hits, last, by, note FROM blocks", ReadBlock)) _blocks[b.Key] = b;
        _allow = UserDb.Query(conn, "SELECT cidr FROM allow", r => r.GetString(0)).Select(c => IPNetwork.TryParse(c, out var n) ? (IPNetwork?)n : null)
            .Where(n => n != null).Select(n => n!.Value).ToArray();
        _seen.Clear();
        foreach (var (seenKey, at) in UserDb.Query(conn, "SELECT key, last FROM seen", r => (r.GetString(0), r.GetInt64(1)))) _seen[seenKey] = Time(at);
        var kv = UserDb.Query(conn, "SELECT key, value FROM kv", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
        _settings = new Settings(
            Traps: !kv.TryGetValue("traps", out var tr) || tr != "0",
            FirstDays: kv.TryGetValue("firstDays", out var fd) && int.TryParse(fd, out var f) ? f : 7,
            SeenDays: kv.TryGetValue("seenDays", out var sd) && int.TryParse(sd, out var s) ? s : 30,
            KeepDays: kv.TryGetValue("keepDays", out var kd) && int.TryParse(kd, out var k) ? k : 90,
            AbuseCheck: kv.GetValueOrDefault("abuseCheck") == "1",
            AbuseScore: int.TryParse(kv.GetValueOrDefault("abuseScore"), out var a) ? a : 90,
            AbuseDays: int.TryParse(kv.GetValueOrDefault("abuseDays"), out var ad) ? ad : 7,
            AbuseReport: kv.GetValueOrDefault("abuseReport") == "1");
        LoadAbuse(conn, kv);
    }

    private static Block ReadBlock(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), Time(r.GetInt64(2)), r.IsDBNull(3) ? null : Time(r.GetInt64(3)),
        r.GetInt32(4), r.GetInt64(5), Time(r.GetInt64(6)), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));

    // ---------------- Abfragen ----------------

    public Settings Current => _settings;

    public bool Allowed(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return BuiltInNets.Any(n => n.Contains(ip)) || _allow.Any(n => n.Contains(ip));
    }

    public Block? BlockOf(string key) => _blocks.TryGetValue(key, out var b) && (b.Until == null || b.Until > DateTime.UtcNow) ? b : null;

    /// <summary>Mit dieser Adresse hat sich in letzter Zeit jemand gültig angemeldet - ein ungültiges Merkmal ist dann eher eine
    /// abgelaufene Sitzung als ein Angriff (Haushalt, eigenes Handy): festhalten ja, sperren nein.</summary>
    public bool RecentlyGood(string key) => _seen.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromDays(_settings.SeenDays);

    public List<Block> Blocks() => _blocks.Values.Where(b => b.Until == null || b.Until > DateTime.UtcNow)
        .Select(b => b with { Hits = b.Hits + _pendingHits.GetValueOrDefault(b.Key) }).OrderByDescending(b => b.Last).ToList();

    public List<AllowEntry> AllowList()
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT cidr, note, added FROM allow ORDER BY added", r => new AllowEntry(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), Time(r.GetInt64(2))));
    }

    public List<Event> Events(string? query, int limit = 500, long? before = null)
    {
        using var conn = Open();
        var like = string.IsNullOrWhiteSpace(query) ? null : "%" + query.Trim().Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
        return UserDb.Query(conn, @"SELECT id, at, key, kind, method, path, agent, detail FROM events
            WHERE ($b IS NULL OR id < $b) AND ($q IS NULL OR key LIKE $q ESCAPE '!' OR path LIKE $q ESCAPE '!' OR agent LIKE $q ESCAPE '!' OR detail LIKE $q ESCAPE '!' OR kind LIKE $q ESCAPE '!')
            ORDER BY id DESC LIMIT $n",
            r => new Event(r.GetInt64(0), Time(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)),
            ("$b", before), ("$q", like), ("$n", Math.Clamp(limit, 1, 5000)));
    }

    /// <summary>Ereignisse je Art seit einem Zeitpunkt (Übersicht: 24 Stunden, 7 Tage).</summary>
    public Dictionary<string, int> Counts(DateTime since)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT kind, COUNT(*) FROM events WHERE at >= $s GROUP BY kind", r => (r.GetString(0), r.GetInt32(1)), ("$s", Unix(since)))
            .ToDictionary(x => x.Item1, x => x.Item2);
    }

    /// <summary>Adressen mit den meisten Ereignissen seit einem Zeitpunkt.</summary>
    public List<(string Key, int Count, DateTime Last)> Top(DateTime since, int n = 10)
    {
        using var conn = Open();
        // Nur Versuche von außen - nicht das Freigeben und Sperren in der Verwaltung, nicht alte Geräte
        return UserDb.Query(conn, "SELECT key, COUNT(*), MAX(at) FROM events WHERE at >= $s AND kind NOT IN ('freigegeben', 'von-hand', 'altes-geraet', 'gemeldet') GROUP BY key ORDER BY 2 DESC LIMIT $n",
            r => (r.GetString(0), r.GetInt32(1), Time(r.GetInt64(2))), ("$s", Unix(since)), ("$n", n));
    }

    // ---------------- Ereignisse und Sperren ----------------

    private void Log(string key, string kind, string method, string path, string? agent, string? detail)
    {
        using var conn = Open();
        UserDb.Exec(conn, "INSERT INTO events(at, key, kind, method, path, agent, detail) VALUES($a,$k,$t,$m,$p,$u,$d)",
            ("$a", Unix(DateTime.UtcNow)), ("$k", key), ("$t", kind), ("$m", method), ("$p", Cut(path, 500)), ("$u", agent == null ? null : Cut(agent, 300)), ("$d", detail));
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Angriff (Falle oder Scanner): festhalten und sperren - erste Sperre <see cref="Settings.FirstDays"/>, eine weitere
    /// innerhalb von 90 Tagen 30 Tage, danach dauerhaft. Adressen mit gültiger Anmeldung in letzter Zeit: nur festhalten.</summary>
    public void Attack(string key, string kind, string method, string path, string? agent)
    {
        var good = RecentlyGood(key);
        Log(key, kind, method, path, agent, good ? "nicht gesperrt: Adresse mit gültiger Anmeldung" : null);
        if (good) return;
        BlockNow(key, kind == "scanner" ? $"Scanner ({Cut(agent ?? "", 80)})" : $"Falle: {method} {Cut(path, 120)}", null, null);
        ReportAttack(key, kind, method, path);
    }

    /// <summary>Nur festhalten, ohne Punkte (etwa ein altes Gerät mit gelöschtem App-Passwort).</summary>
    public void Note(string key, string kind, string method, string path, string? agent, string? detail)
    {
        if (key == "?") return;
        // Ein altes Gerät fragt oft im Sekundentakt: höchstens einmal je Stunde und Adresse
        var k = key + "|" + kind + "|" + detail;
        if (_noted.TryGetValue(k, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1)) return;
        _noted[k] = DateTime.UtcNow;
        Log(key, kind, method, path, agent, detail);
    }
    private readonly ConcurrentDictionary<string, DateTime> _noted = new();

    /// <summary>Verdächtig (bekannter Weg mit falschen Daten): Punkte; ab <see cref="SuspiciousLimit"/> in einer Stunde einen Tag gesperrt.</summary>
    public void Suspicious(string key, string kind, int points, string method, string path, string? agent, string? detail)
    {
        if (key == "?") return;
        Log(key, kind, method, path, agent, detail);
        if (RecentlyGood(key) || BlockOf(key) != null) return;
        var now = DateTime.UtcNow;
        var s = _suspicious.AddOrUpdate(key, (points, now), (_, o) => now - o.Since > TimeSpan.FromHours(1) ? (points, now) : (o.Points + points, o.Since));
        if (s.Points >= SuspiciousLimit)
        {
            _suspicious.TryRemove(key, out _);
            BlockNow(key, $"Viele Fehlversuche ({kind})", now.AddDays(1), null);
        }
    }

    /// <summary>Sperren (Erkennung oder von Hand: <paramref name="until"/> null und <paramref name="by"/> gesetzt = dauerhaft).</summary>
    public Block BlockNow(string key, string reason, DateTime? until, string? by, string? note = null)
    {
        var now = DateTime.UtcNow;
        var prev = _blocks.GetValueOrDefault(key);
        // Wiederholungstäter: eine frühere Sperre in den letzten 90 Tagen hebt die Stufe
        var level = prev != null && now - prev.Last < TimeSpan.FromDays(90) ? prev.Level + 1 : 1;
        if (by == null && until == null)
            until = level switch { 1 => now.AddDays(Math.Max(1, _settings.FirstDays)), 2 => now.AddDays(30), _ => null };
        if (prev != null && BlockOf(key) != null && by == null)
            return prev;   // läuft schon
        var b = new Block(key, reason, now, until, level, 0, now, by, note);
        _blocks[key] = b;
        using (var conn = Open())
            UserDb.Exec(conn, "INSERT OR REPLACE INTO blocks(key, reason, since, until, level, hits, last, by, note) VALUES($k,$r,$s,$u,$l,0,$s,$b,$n)",
                ("$k", key), ("$r", reason), ("$s", Unix(now)), ("$u", until == null ? null : Unix(until.Value)), ("$l", level), ("$b", by), ("$n", note));
        SystemLog.Add(Microsoft.Extensions.Logging.LogLevel.Warning, "Sicherheit",
            $"Adresse {key} gesperrt {(until == null ? "dauerhaft" : "bis " + until.Value.ToString("dd.MM.yyyy HH:mm") + " UTC")}: {reason}");
        return b;
    }

    public bool Unblock(string key, string by)
    {
        if (!_blocks.TryRemove(key, out _)) return false;
        _pendingHits.TryRemove(key, out _);
        using var conn = Open();
        UserDb.Exec(conn, "DELETE FROM blocks WHERE key=$k", ("$k", key));
        Log(key, "freigegeben", "-", "-", null, "von " + by);
        return true;
    }

    /// <summary>Von Hand gesperrt (Verwaltung): als Ereignis festhalten.</summary>
    public void Manual(string key, string by, string? note) =>
        Log(key, "von-hand", "-", "-", null, "von " + by + (string.IsNullOrWhiteSpace(note) ? "" : ": " + note));

    /// <summary>Anfrage einer gesperrten Adresse: zählen, die ersten einzeln festhalten.</summary>
    public void Hit(string key, string method, string path, string? agent)
    {
        var n = _pendingHits.AddOrUpdate(key, 1, (_, o) => o + 1) + (_blocks.TryGetValue(key, out var b) ? b.Hits : 0);
        if (n <= HitsLogged) Log(key, "gesperrt", method, path, agent, null);
    }

    /// <summary>Gültig angemeldet von dieser Adresse (höchstens einmal je Stunde gespeichert).</summary>
    public void Good(string key)
    {
        var now = DateTime.UtcNow;
        _seen[key] = now;
        if (_seenWritten.TryGetValue(key, out var w) && now - w < TimeSpan.FromHours(1)) return;
        _seenWritten[key] = now;
        using var conn = Open();
        UserDb.Exec(conn, "INSERT OR REPLACE INTO seen(key, last) VALUES($k,$l)", ("$k", key), ("$l", Unix(now)));
    }

    public void Allow(string cidr, string? note)
    {
        var net = ParseNet(cidr);
        using (var conn = Open())
            UserDb.Exec(conn, "INSERT OR REPLACE INTO allow(cidr, note, added) VALUES($c,$n,$a)", ("$c", net.ToString()), ("$n", note), ("$a", Unix(DateTime.UtcNow)));
        Reload();
    }

    public void Disallow(string cidr)
    {
        using (var conn = Open()) UserDb.Exec(conn, "DELETE FROM allow WHERE cidr=$c", ("$c", cidr));
        Reload();
    }

    /// <summary>„1.2.3.4“ oder „1.2.3.0/24“, IPv6 ebenso.</summary>
    public static IPNetwork ParseNet(string s)
    {
        s = (s ?? "").Trim();
        if (!s.Contains('/') && IPAddress.TryParse(s, out var ip)) return new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return IPNetwork.TryParse(s, out var n) ? n : throw new ArgumentException("Keine gültige Adresse oder kein gültiges Netz (etwa 203.0.113.7 oder 203.0.113.0/24).");
    }

    public void Save(Settings s)
    {
        if (s.FirstDays is < 1 or > 365 || s.SeenDays is < 1 or > 365 || s.KeepDays is < 7 or > 3650 || s.AbuseScore is < 1 or > 100 || s.AbuseDays is < 1 or > 365)
            throw new ArgumentException("Ungültige Werte.");
        using (var conn = Open())
            foreach (var (k, v) in new[] { ("traps", s.Traps ? "1" : "0"), ("firstDays", s.FirstDays.ToString()), ("seenDays", s.SeenDays.ToString()), ("keepDays", s.KeepDays.ToString()),
                ("abuseCheck", s.AbuseCheck ? "1" : "0"), ("abuseScore", s.AbuseScore.ToString()), ("abuseDays", s.AbuseDays.ToString()), ("abuseReport", s.AbuseReport ? "1" : "0") })
                UserDb.Exec(conn, "INSERT OR REPLACE INTO kv(key, value) VALUES($k,$v)", ("$k", k), ("$v", v));
        _settings = s;
    }

    /// <summary>Zähler sichern, Abgelaufenes und Altes wegräumen (Hintergrund, jede Minute).</summary>
    public void Housekeeping()
    {
        var now = DateTime.UtcNow;
        using var conn = Open();
        foreach (var (k, n) in _pendingHits.ToArray())
        {
            if (!_pendingHits.TryRemove(k, out var add)) continue;
            UserDb.Exec(conn, "UPDATE blocks SET hits = hits + $n, last = $l WHERE key=$k", ("$n", add), ("$l", Unix(now)), ("$k", k));
            if (_blocks.TryGetValue(k, out var b)) _blocks[k] = b with { Hits = b.Hits + add, Last = now };
        }
        // Abgelaufene Sperren bleiben 90 Tage stehen (für die Stufe bei Wiederholung), dann weg
        UserDb.Exec(conn, "DELETE FROM blocks WHERE until IS NOT NULL AND until < $t", ("$t", Unix(now.AddDays(-90))));
        UserDb.Exec(conn, "DELETE FROM events WHERE at < $t", ("$t", Unix(now.AddDays(-_settings.KeepDays))));
        UserDb.Exec(conn, "DELETE FROM seen WHERE last < $t", ("$t", Unix(now.AddDays(-_settings.SeenDays))));
        UserDb.Exec(conn, "DELETE FROM reputation WHERE at < $t", ("$t", Unix(now - ReputationValid)));
        foreach (var (k, r) in _reputation.ToArray()) if (now - r.At > ReputationValid) _reputation.TryRemove(k, out _);
        foreach (var (k, at) in _seen.ToArray()) if (now - at > TimeSpan.FromDays(_settings.SeenDays)) _seen.TryRemove(k, out _);
        foreach (var (k, s) in _suspicious.ToArray()) if (now - s.Since > TimeSpan.FromHours(1)) _suspicious.TryRemove(k, out _);
    }

    // ---------------- Einordnen einer Anfrage ----------------

    /// <summary>Werkzeuge zum Abtasten - echte Browser, Apps und DAV-Clients heißen nie so.</summary>
    [GeneratedRegex(@"sqlmap|nikto|nmap|masscan|zgrab|nuclei|wpscan|dirbuster|gobuster|ffuf|feroxbuster|acunetix|nessus|openvas|l9explore|l9tcpid|zmeu|morfeus|jaeles|httpx|naabu|fuzz|xray|whatweb|wfuzz|hydra|medusa|netsparker|qualys|w3af|arachni|joomscan|commix",
        RegexOptions.IgnoreCase)]
    private static partial Regex ScannerAgents();

    public static bool IsScanner(string? agent) => !string.IsNullOrEmpty(agent) && ScannerAgents().IsMatch(agent);

    /// <summary>
    /// Gibt es diesen Weg? Ein Endpunkt (außer dem Rückfall auf die Startseite), eine Seite der Oberfläche oder etwas, das
    /// Browser von selbst anfragen (Symbole, /.well-known). Dateien der Oberfläche liefert die Pipeline vorher aus.
    /// </summary>
    public static bool Known(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (ctx.GetEndpoint() is { } ep && !(ep is RouteEndpoint re && re.RoutePattern.RawText?.StartsWith("{*", StringComparison.Ordinal) == true)) return true;
        return Page(path) || BrowserAsks(path);
    }

    /// <summary>Seiten der Oberfläche - alles andere läuft über #/… hinter „/“.</summary>
    public static bool Page(PathString path)
    {
        var p = Plain(path);
        return p is "/" or "/index.html" or "/login" || path.StartsWithSegments("/s") || path.StartsWithSegments("/a");
    }

    /// <summary>Pfad ohne Unterschied in Groß- und Kleinschreibung und ohne Schrägstrich am Ende (/LOGIN, /login/ = /login).</summary>
    private static string Plain(PathString path)
    {
        var p = (path.Value ?? "/").ToLowerInvariant();
        return p.Length > 1 ? p.TrimEnd('/') is { Length: > 0 } t ? t : "/" : p;
    }

    /// <summary>Was Browser und Geräte ohne Zutun anfragen (auch wenn es fehlt) - kein Abtasten.</summary>
    private static bool BrowserAsks(PathString path)
    {
        var p = Plain(path);
        return path.StartsWithSegments("/assets") || path.StartsWithSegments("/.well-known") || p is "/favicon.ico" or "/favicon.svg" or "/favicon.png" or "/robots.txt"
            or "/sw.js" or "/manifest.webmanifest" or "/browserconfig.xml" or "/apple-app-site-association"
            || p.StartsWith("/apple-touch-icon") || p.StartsWith("/icon-") || p.StartsWith("/pukki");
    }

    /// <summary>Bringt die Anfrage irgendein Anmeldemerkmal mit (gültig oder nicht)? Ohne jedes: ein Fremder.</summary>
    public static bool HasCredential(HttpContext ctx)
    {
        var h = ctx.Request.Headers;
        return ctx.User.Identity?.IsAuthenticated == true || h.ContainsKey("Authorization") || h.ContainsKey(Tickets.Header) || h.ContainsKey(E2e.SessionHeader)
            || ctx.Request.Cookies.ContainsKey("dmoffice") || IsEnvelope(ctx.Request.Path);
    }

    private static bool IsEnvelope(PathString path)
    {
        var p = path.Value ?? "";
        return p.StartsWith("/api/e2e/", StringComparison.OrdinalIgnoreCase) && p.Count(c => c == '/') == 4;
    }

    /// <summary>Freigabelink mit unbekanntem Token (Raten von Links).</summary>
    private static bool IsShareToken(PathString path) =>
        path.StartsWithSegments("/api/public") || path.StartsWithSegments("/api/public-album");

    /// <summary>Hinter UseAuthentication und vor der Bremse: Erlaubtes durch, Gesperrtes ab, Abtasten erkennen.</summary>
    public static void Use(WebApplication app) => app.Use(async (ctx, next) =>
    {
        var sc = Shared;
        var ip = ctx.Connection.RemoteIpAddress;
        var key = ip == null ? null : AnonThrottle.KeyOf(ip);
        if (sc == null || ip == null || key == null || sc.Allowed(ip)) { await next(); return; }
        var method = ctx.Request.Method;
        var path = ctx.Request.Path.Value ?? "";
        string? agent = ctx.Request.Headers.UserAgent;
        if (sc.BlockOf(key) != null)
        {
            // Gesperrt: nur noch wirklich Angemeldete (Haushalt hinter derselben Adresse) - ein vorgetäuschtes Merkmal genügt nicht
            if (!await Authenticated(ctx))
            {
                sc.Hit(key, method, path, agent);
                ctx.Response.StatusCode = 403;
                return;
            }
        }
        // Bei AbuseIPDB als Angreifer bekannt (eingeschaltet, nicht angemeldet, keine Adresse mit gültiger Anmeldung): gleich sperren
        else if (sc.Current.AbuseCheck && sc.AbuseKeySet && !sc.RecentlyGood(key) && !await Authenticated(ctx)
            && await sc.ReputationAsync(key, ip) is int score && score >= sc.Current.AbuseScore)
        {
            sc.ReputationBlock(key, score, method, path, agent);
            ctx.Response.StatusCode = 403;
            return;
        }
        if (sc.BlockOf(key) == null && sc.Current.Traps && !HasCredential(ctx))
        {
            var kind = IsScanner(agent) ? "scanner" : !Known(ctx) ? "falle" : null;
            if (kind != null)
            {
                sc.Attack(key, kind, method, path, agent);
                ctx.Response.StatusCode = 404;
                return;
            }
        }
        await next();
        if (ctx.User.Identity?.IsAuthenticated == true) sc.Good(key);
        else if (ctx.Response.StatusCode == 404 && IsShareToken(ctx.Request.Path))
            sc.Suspicious(key, "link", 3, method, path, agent, "unbekannter Freigabelink");
    });

    /// <summary>Wirklich angemeldet: Cookie oder App-Passwort, Ticket, Umschlag (wie die Bremse prüft).</summary>
    private static async Task<bool> Authenticated(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true || IsEnvelope(ctx.Request.Path)) return true;
        var h = ctx.Request.Headers;
        if (!h.ContainsKey("Authorization") && !h.ContainsKey(Tickets.Header) && !h.ContainsKey(E2e.SessionHeader)) return false;
        return (await ctx.AuthenticateAsync(Auth.AppPasswordScheme)).Succeeded;
    }
}

/// <summary>Zähler sichern und Altes wegräumen, jede Minute.</summary>
public sealed class SecurityHousekeeping(ILogger<SecurityHousekeeping> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
            try { SecurityCenter.Shared?.Housekeeping(); }
            catch (Exception e) when (e is SqliteException or IOException) { log.LogWarning("Security Center: Aufräumen fehlgeschlagen: {Error}", e.Message); }
        }
    }
}
