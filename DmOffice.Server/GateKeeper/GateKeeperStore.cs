using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DmOffice.Plugins;
using DmOffice.Sdk;

namespace DmOffice.GateKeeper;

/// <summary>Filterliste, vom Admin eingetragen. Status: ok, Fehlertext oder null (noch nie geladen).</summary>
public sealed record GkList(long Id, string Url, string Name, bool Enabled, long? Fetched, string? Status, int Entries);

/// <summary>Eigene Regel eines Benutzers: block oder allow, je samt Unterdomains.</summary>
public sealed record GkRule(string Domain, string Action, long Created);

/// <summary>Gesperrte Domain mit Zahl der Treffer und letztem Treffer (Unix-Sekunden).</summary>
public sealed record GkHit(string Domain, long Hits, long Last);

/// <summary>
/// Zähler eines Tages: geprüfte Verbindungen, DNS-Anfragen, gesperrt, durchgereichte Bytes (von der App / zur App) und die
/// Größen der Verbindungen als Verteilung (<see cref="GkSizes"/>, für den Median).
/// </summary>
public sealed record GkDay(string Day, long Connections, long Dns, long Blocked, long Up = 0, long Down = 0,
    [property: System.Text.Json.Serialization.JsonIgnore] long[]? Sizes = null);

/// <summary>
/// Verteilung der Verbindungsgrößen in Zweierpotenz-Stufen: Stufe 0 = 0 Bytes, Stufe k = 2^(k-1) bis unter 2^k Bytes.
/// Der Median daraus schätzt, was eine gesperrte Verbindung gekostet hätte; einzelne große Übertragungen (Videos,
/// Downloads) verschieben ihn kaum.
/// </summary>
public static class GkSizes
{
    public const int Count = 40;

    public static int Bucket(long bytes) => bytes <= 0 ? 0 : Math.Min(Count - 1, 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)bytes));

    public static long[] Add(long[]? a, long[]? b)
    {
        var r = new long[Count];
        for (int i = 0; i < Count; i++) r[i] = (a != null && i < a.Length ? a[i] : 0) + (b != null && i < b.Length ? b[i] : 0);
        return r;
    }

    /// <summary>Median in Bytes (Mitte der Stufe, geometrisch); 0 ohne Verbindungen.</summary>
    public static long Median(long[]? s)
    {
        if (s == null) return 0;
        long total = s.Sum(), seen = 0;
        if (total == 0) return 0;
        for (int i = 0; i < s.Length; i++)
        {
            seen += s[i];
            if (seen * 2 >= total) return i == 0 ? 0 : (long)Math.Round(Math.Pow(2, i - 1) * Math.Sqrt(2));
        }
        return 0;
    }

    public static string ToJson(long[] s) => JsonSerializer.Serialize(s);
    public static long[]? FromJson(string? json) { try { return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<long[]>(json); } catch (JsonException) { return null; } }
}

/// <summary>Eigene Regeln eines Benutzers als Mengen (für die Prüfung im Tunnel).</summary>
public sealed record GkUserRules(HashSet<string> Allow, HashSet<string> Block)
{
    public static readonly GkUserRules Empty = new([], []);
}

/// <summary>
/// Daten von GateKeeper (Kennung gatekeeper, docs/gatekeeper-konzept.md) in der serverweiten Datenbank
/// &lt;Daten&gt;/plugins/gatekeeper.db (eigener Schlüssel, wie der Haushalt). Keine Tabellen in den Benutzerdatenbanken.
/// Kein Inhaltsprotokoll: gespeichert werden nur Zähler und gesperrte Adressen. Welche Apps ein Handy hat und welche über
/// GateKeeper laufen, speichert nur die App auf dem Handy (Datensparsamkeit, Entscheidung 25.11.2026).
/// </summary>
public sealed class GateKeeperStore
{
    public const string Key = "gatekeeper";
    public const string Block = "block", Allow = "allow";
    /// <summary>So viele gesperrte Domains je Benutzer bleiben (die ältesten fallen heraus).</summary>
    public const int MaxHits = 2000;
    /// <summary>So viele Tage Zähler bleiben.</summary>
    public const int KeepDays = 90;
    /// <summary>Meldung bei einer Eingabe, die keine Adresse ist (eigene Regel, Adresse prüfen).</summary>
    public const string BadAddress = "Bitte eine Adresse wie werbung.beispiel.de angeben.";

    internal static readonly SchemaStep[] Schema =
    [
        new(1, """
            CREATE TABLE gk_setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE gk_list (id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, name TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1,
                fetched INTEGER NULL, status TEXT NULL, entries INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE gk_user (user TEXT PRIMARY KEY, enabled INTEGER NOT NULL DEFAULT 0, packages TEXT NOT NULL DEFAULT '[]');
            CREATE TABLE gk_app (user TEXT NOT NULL, device TEXT NOT NULL, package TEXT NOT NULL, label TEXT NOT NULL, seen INTEGER NOT NULL,
                PRIMARY KEY (user, device, package));
            CREATE TABLE gk_rule (user TEXT NOT NULL, domain TEXT NOT NULL, action TEXT NOT NULL CHECK(action IN ('block','allow')), created INTEGER NOT NULL,
                PRIMARY KEY (user, domain));
            CREATE TABLE gk_hit (user TEXT NOT NULL, domain TEXT NOT NULL, hits INTEGER NOT NULL, last INTEGER NOT NULL, PRIMARY KEY (user, domain));
            CREATE INDEX gk_hit_last ON gk_hit(user, last);
            CREATE TABLE gk_day (user TEXT NOT NULL, day TEXT NOT NULL, conns INTEGER NOT NULL DEFAULT 0, dns INTEGER NOT NULL DEFAULT 0,
                blocked INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (user, day));
            """),
        // Datenverkehr: Bytes je Richtung und Verteilung der Verbindungsgrößen (JSON, GkSizes)
        new(2, """
            ALTER TABLE gk_day ADD COLUMN up INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE gk_day ADD COLUMN down INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE gk_day ADD COLUMN sizes TEXT NULL;
            """),
        // Datensparsamkeit (25.11.2026): App-Listen, Gerätenamen, App-Auswahl und Ein/Aus bleiben auf dem Handy. Die Inhalte
        // löscht vorher Purge (mit secure_delete); hier fallen nur noch die Tabellen einer neu angelegten Datenbank weg.
        new(3, """
            DROP TABLE IF EXISTS gk_app;
            DROP TABLE IF EXISTS gk_user;
            """),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IServerData _db;
    private readonly ConcurrentDictionary<string, GkUserRules> _rules = new();

    public GateKeeperStore(ServerDataStore data)
    {
        var db = data.Open(Key);
        Purge(db);
        db.Apply(Key, Schema);
        _db = db;
    }

    /// <summary>Für Tests: auf einer gegebenen Datenbank (Schema wird angelegt).</summary>
    internal GateKeeperStore(ServerData db)
    {
        Purge(db);
        db.Apply(Key, Schema);
        _db = db;
    }

    /// <summary>
    /// Früher gemeldete App-Listen, Gerätenamen und App-Auswahl (Tabellen gk_app und gk_user bis 25.11.2026) löschen.
    /// secure_delete überschreibt die frei gewordenen Stellen der Datei mit Nullen, der Checkpoint leert das Write-Ahead-Log.
    /// Läuft bei jedem Start; ohne die Tabellen geschieht nichts.
    /// </summary>
    private static void Purge(ServerData db)
    {
        if (Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('gk_app','gk_user')")) == 0) return;
        db.Transaction(t =>
        {
            // Jede Anweisung als eigener Befehl (ein PRAGMA liefert eine Ergebniszeile)
            t.Execute("PRAGMA secure_delete = ON");
            t.Execute("DROP TABLE IF EXISTS gk_app");
            t.Execute("DROP TABLE IF EXISTS gk_user");
            return 0;
        });
        db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ------------------------------------------------------------ Einstellungen des Servers

    public string? Setting(string key) => _db.Scalar("SELECT value FROM gk_setting WHERE key=$k", ("$k", key)) as string;

    public void SetSetting(string key, string? value)
    {
        if (value == null) _db.Execute("DELETE FROM gk_setting WHERE key=$k", ("$k", key));
        else _db.Execute("INSERT INTO gk_setting (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
    }

    /// <summary>Abstand zwischen zwei Ladevorgängen der Listen in Stunden (1–168, Vorgabe 24).</summary>
    public int RefreshHours
    {
        get => int.TryParse(Setting("refreshHours"), out var h) ? Math.Clamp(h, 1, 168) : 24;
        set => SetSetting("refreshHours", Math.Clamp(value, 1, 168).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Dürfen Verbindungen über GateKeeper Adressen im eigenen Netz des Servers erreichen (Vorgabe nein)?</summary>
    public bool AllowPrivate
    {
        get => Setting("allowPrivate") == "1";
        set => SetSetting("allowPrivate", value ? "1" : null);
    }

    // ------------------------------------------------------------ Listen

    private static GkList ReadList(System.Data.IDataRecord r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0,
        r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), (int)r.GetInt64(6));

    public List<GkList> Lists() => _db.Query("SELECT id, url, name, enabled, fetched, status, entries FROM gk_list ORDER BY id", ReadList);

    public GkList? List(long id) => _db.Query("SELECT id, url, name, enabled, fetched, status, entries FROM gk_list WHERE id=$i", ReadList, ("$i", id)).FirstOrDefault();

    public static string CheckUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new ArgumentException("Bitte eine Adresse mit http:// oder https:// angeben.");
        return u.ToString();
    }

    public GkList AddList(string url, string? name)
    {
        url = CheckUrl(url);
        if (Lists().Any(l => l.Url == url)) throw new ArgumentException("Diese Liste ist schon eingetragen.");
        var n = string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : name.Trim();
        if (n.Length > 200) n = n[..200];
        var id = Convert.ToInt64(_db.Scalar("INSERT INTO gk_list (url, name) VALUES ($u, $n) RETURNING id", ("$u", url), ("$n", n)));
        return List(id)!;
    }

    public void UpdateList(long id, string? name, bool? enabled, string? url = null)
    {
        var l = List(id) ?? throw new KeyNotFoundException("Liste nicht gefunden.");
        var newUrl = url == null ? l.Url : CheckUrl(url);
        var newName = string.IsNullOrWhiteSpace(name) ? l.Name : name.Trim();
        _db.Execute("UPDATE gk_list SET name=$n, enabled=$e, url=$u WHERE id=$i", ("$n", newName.Length > 200 ? newName[..200] : newName),
            ("$e", (enabled ?? l.Enabled) ? 1 : 0), ("$u", newUrl), ("$i", id));
        if (newUrl != l.Url) _db.Execute("UPDATE gk_list SET fetched=NULL, status=NULL, entries=0 WHERE id=$i", ("$i", id));
    }

    public void DeleteList(long id) => _db.Execute("DELETE FROM gk_list WHERE id=$i", ("$i", id));

    public void ListFetched(long id, string status, int? entries)
    {
        if (entries is { } n) _db.Execute("UPDATE gk_list SET fetched=$f, status=$s, entries=$c WHERE id=$i", ("$f", Now), ("$s", status), ("$c", n), ("$i", id));
        else _db.Execute("UPDATE gk_list SET status=$s WHERE id=$i", ("$s", status), ("$i", id));
    }

    // ------------------------------------------------------------ Eigene Regeln

    public List<GkRule> Rules(string user) => _db.Query("SELECT domain, action, created FROM gk_rule WHERE user=$u ORDER BY domain",
        r => new GkRule(r.GetString(0), r.GetString(1), r.GetInt64(2)), ("$u", user));

    public GkRule SetRule(string user, string domain, string action)
    {
        var d = GateKeeperFilter.Normalize(domain) ?? throw new ArgumentException(BadAddress);
        if (action is not (Block or Allow)) throw new ArgumentException("Unbekannte Aktion.");
        if (Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM gk_rule WHERE user=$u", ("$u", user))) >= 5000) throw new ArgumentException("Höchstens 5000 eigene Regeln.");
        _db.Execute("INSERT INTO gk_rule (user, domain, action, created) VALUES ($u, $d, $a, $c) ON CONFLICT(user, domain) DO UPDATE SET action=excluded.action",
            ("$u", user), ("$d", d), ("$a", action), ("$c", Now));
        _rules.TryRemove(user, out _);
        return Rules(user).First(r => r.Domain == d);
    }

    public void DeleteRule(string user, string domain)
    {
        _db.Execute("DELETE FROM gk_rule WHERE user=$u AND domain=$d", ("$u", user), ("$d", GateKeeperFilter.Normalize(domain) ?? domain));
        _rules.TryRemove(user, out _);
    }

    /// <summary>Eigene Regeln als Mengen (zwischengespeichert, nach jeder Änderung neu).</summary>
    public GkUserRules RulesOf(string user) => _rules.GetOrAdd(user, u =>
    {
        var all = Rules(u);
        return new GkUserRules(all.Where(r => r.Action == Allow).Select(r => r.Domain).ToHashSet(StringComparer.Ordinal),
            all.Where(r => r.Action == Block).Select(r => r.Domain).ToHashSet(StringComparer.Ordinal));
    });

    // ------------------------------------------------------------ Statistik

    /// <summary>Gesammelte Zähler übernehmen (aus <see cref="GateKeeperStats"/>).</summary>
    public void AddCounts(string user, string day, long conns, long dns, long blocked, IEnumerable<(string Domain, long Hits, long Last)> hits,
        long up = 0, long down = 0, long[]? sizes = null)
    {
        _db.Transaction(t =>
        {
            var old = sizes == null ? null : GkSizes.FromJson(t.Scalar("SELECT sizes FROM gk_day WHERE user=$u AND day=$d", ("$u", user), ("$d", day)) as string);
            t.Execute("""
                INSERT INTO gk_day (user, day, conns, dns, blocked, up, down, sizes) VALUES ($u, $d, $c, $n, $b, $up, $dn, $s)
                ON CONFLICT(user, day) DO UPDATE SET conns=conns+excluded.conns, dns=dns+excluded.dns, blocked=blocked+excluded.blocked,
                    up=up+excluded.up, down=down+excluded.down, sizes=coalesce(excluded.sizes, sizes)
                """, ("$u", user), ("$d", day), ("$c", conns), ("$n", dns), ("$b", blocked), ("$up", up), ("$dn", down),
                ("$s", sizes == null ? null : GkSizes.ToJson(GkSizes.Add(old, sizes))));
            foreach (var (domain, n, last) in hits)
                t.Execute("""
                    INSERT INTO gk_hit (user, domain, hits, last) VALUES ($u, $d, $h, $l)
                    ON CONFLICT(user, domain) DO UPDATE SET hits=hits+excluded.hits, last=max(last, excluded.last)
                    """, ("$u", user), ("$d", domain), ("$h", n), ("$l", last));
            return 0;
        });
    }

    /// <summary>Aufräumen: alte Tage und überzählige Adressen.</summary>
    public void Prune()
    {
        var cut = DateTime.UtcNow.AddDays(-KeepDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _db.Execute("DELETE FROM gk_day WHERE day < $c", ("$c", cut));
        _db.Execute("""
            DELETE FROM gk_hit WHERE rowid IN (SELECT rowid FROM (SELECT rowid, ROW_NUMBER() OVER (PARTITION BY user ORDER BY last DESC) AS n FROM gk_hit) WHERE n > $m)
            """, ("$m", MaxHits));
    }

    public List<GkHit> Hits(string user, string order, int limit) => _db.Query(
        "SELECT domain, hits, last FROM gk_hit WHERE user=$u ORDER BY " + (order == "hits" ? "hits DESC, last DESC" : "last DESC") + " LIMIT $l",
        r => new GkHit(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("$u", user), ("$l", Math.Clamp(limit, 1, MaxHits)));

    public List<GkDay> Days(string user, int days)
    {
        var cut = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return _db.Query("SELECT day, conns, dns, blocked, up, down, sizes FROM gk_day WHERE user=$u AND day >= $c ORDER BY day",
            r => new GkDay(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                GkSizes.FromJson(r.IsDBNull(6) ? null : r.GetString(6))), ("$u", user), ("$c", cut));
    }

    /// <summary>Zahl der Personen mit Verkehr in den letzten Tagen.</summary>
    public int People(int days) => Convert.ToInt32(_db.Scalar("SELECT COUNT(DISTINCT user) FROM gk_day WHERE day >= $c AND conns + dns + up + down > 0",
        ("$c", DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));

    /// <summary>Alle Personen zusammen je Tag (für die Verwaltung; ohne Domains), dazu die Zahl der Personen mit Verkehr.</summary>
    public List<(GkDay Day, int People)> AllDays(int days)
    {
        var cut = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = _db.Query("SELECT day, conns, dns, blocked, up, down, sizes FROM gk_day WHERE day >= $c",
            r => new GkDay(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                GkSizes.FromJson(r.IsDBNull(6) ? null : r.GetString(6))), ("$c", cut));
        return rows.GroupBy(d => d.Day).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (new GkDay(g.Key, g.Sum(d => d.Connections),
            g.Sum(d => d.Dns), g.Sum(d => d.Blocked), g.Sum(d => d.Up), g.Sum(d => d.Down), g.Aggregate((long[]?)null, (a, d) => GkSizes.Add(a, d.Sizes))),
            g.Count(d => d.Connections + d.Dns + d.Up + d.Down > 0))).ToList();
    }

    public void ClearStats(string user)
    {
        _db.Execute("DELETE FROM gk_hit WHERE user=$u", ("$u", user));
        _db.Execute("DELETE FROM gk_day WHERE user=$u", ("$u", user));
    }

    /// <summary>Alles eines Benutzers entfernen (Konto gelöscht).</summary>
    public void Forget(string user)
    {
        foreach (var t in new[] { "gk_rule", "gk_hit", "gk_day" }) _db.Execute($"DELETE FROM {t} WHERE user=$u", ("$u", user));
        _rules.TryRemove(user, out _);
    }
}
