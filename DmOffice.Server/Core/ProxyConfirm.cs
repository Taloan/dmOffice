using System.Net;
using System.Text.Json;

namespace DmOffice.Core;

/// <summary>
/// Kennung proxyconfirm: X-Forwarded-For/-Proto/-Host gelten nur von bestätigten Reverse-Proxys (Verwaltung → Sicherheit) und von
/// dieser Maschine selbst. Absender mit solchen Kopfzeilen werden festgehalten und dort zum Bestätigen gezeigt. Beim ersten Start
/// läuft eine Übernahmezeit (<see cref="LearningTime"/>): Absender, denen die bisherige Regel (<see cref="ProxyTrust.Networks"/>) glaubte,
/// werden in dieser Zeit „automatisch übernommen“ - bestehende Aufbauten hinter einem Proxy laufen nach dem Update weiter.
/// Liste in &lt;Daten&gt;/proxies.json. Gilt nicht, wenn DMOFFICE_TRUSTED_PROXIES gesetzt ist (dann allein diese Liste).
/// </summary>
public sealed class ProxyConfirm
{
    public static ProxyConfirm? Shared { get; set; }

    public const string Auto = "auto", Confirmed = "confirmed", Seen = "seen";
    /// <summary>So lange nach dem ersten Start übernimmt die bisherige Regel Proxys von selbst.</summary>
    public static readonly TimeSpan LearningTime = TimeSpan.FromHours(24);
    /// <summary>Höchstens so viele unbestätigte Absender merken (die ältesten fallen heraus).</summary>
    public const int MaxSeen = 50;

    /// <summary>Ein Absender mit X-Forwarded-*: Stand auto/confirmed/seen, Host aus X-Forwarded-Host (gekürzt), By: wer bestätigt hat.</summary>
    public sealed record Entry(string Address, string State, DateTime First, DateTime Last, long Hits, string? Host, string? By, DateTime? Since);

    private sealed record Stored(DateTime LearnUntil, List<Entry> Proxies);

    private readonly string _file;
    private readonly IReadOnlyList<IPNetwork> _legacy;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _all = new(StringComparer.Ordinal);
    private DateTime _saved = DateTime.MinValue;

    public DateTime LearnUntil { get; private set; }

    /// <param name="legacy">Netze der bisherigen Regel (nur in der Übernahmezeit).</param>
    public ProxyConfirm(string file, IReadOnlyList<IPNetwork> legacy, DateTime? now = null)
    {
        _file = file;
        _legacy = legacy;
        Stored? s = null;
        try { if (File.Exists(file)) s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file)); }
        catch (JsonException) { }
        if (s == null)
        {
            LearnUntil = (now ?? DateTime.UtcNow) + LearningTime;
            Save(force: true);
            return;
        }
        LearnUntil = s.LearnUntil;
        foreach (var e in s.Proxies ?? []) _all[e.Address] = e;
    }

    public bool Learning(DateTime now) => now < LearnUntil;

    public static IPAddress Normal(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>Hat die Anfrage X-Forwarded-*-Kopfzeilen?</summary>
    public static bool Forwarded(HttpRequest r) =>
        r.Headers.ContainsKey("X-Forwarded-For") || r.Headers.ContainsKey("X-Forwarded-Proto") || r.Headers.ContainsKey("X-Forwarded-Host");

    /// <summary>
    /// Eine Anfrage mit X-Forwarded-* von dieser Adresse: gilt der Absender als Proxy? Hält ihn fest (Zähler, zuletzt, Host);
    /// in der Übernahmezeit wird ein Absender, dem die bisherige Regel glaubte, übernommen.
    /// </summary>
    public bool Trusted(IPAddress ip, string? host, DateTime? at = null)
    {
        ip = Normal(ip);
        if (IPAddress.IsLoopback(ip)) return true;
        var now = at ?? DateTime.UtcNow;
        var key = ip.ToString();
        host = Short(host);
        lock (_lock)
        {
            _all.TryGetValue(key, out var e);
            var trusted = e?.State is Auto or Confirmed;
            var changed = false;
            if (!trusted && Learning(now) && _legacy.Any(n => n.Contains(ip)))
            {
                trusted = true;
                changed = true;
                e = e == null ? new Entry(key, Auto, now, now, 0, host, null, now) : e with { State = Auto, Since = now };
            }
            if (e == null)
            {
                changed = true;
                e = new Entry(key, Seen, now, now, 0, host, null, null);
                var seen = _all.Values.Where(x => x.State == Seen).OrderBy(x => x.Last).ToList();
                foreach (var old in seen.Take(Math.Max(0, seen.Count - MaxSeen + 1))) _all.Remove(old.Address);
            }
            _all[key] = e with { Last = now, Hits = e.Hits + 1, Host = host ?? e.Host };
            Save(changed);
            return trusted;
        }
    }

    public List<Entry> List()
    {
        lock (_lock) return _all.Values.OrderBy(e => e.State == Seen).ThenByDescending(e => e.Last).ToList();
    }

    /// <summary>Einen festgehaltenen Absender als Proxy bestätigen.</summary>
    public void Confirm(string address, string by)
    {
        lock (_lock)
        {
            if (!_all.TryGetValue(Key(address), out var e)) throw new KeyNotFoundException("Diesen Absender gibt es nicht in der Liste.");
            _all[e.Address] = e with { State = Confirmed, By = by, Since = DateTime.UtcNow };
            Save(force: true);
        }
    }

    /// <summary>Aus der Liste nehmen: ein Proxy gilt danach nicht mehr (schickt er weiter, steht er wieder unbestätigt da).</summary>
    public bool Remove(string address)
    {
        lock (_lock)
        {
            var removed = _all.Remove(Key(address));
            if (removed) Save(force: true);
            return removed;
        }
    }

    private static string Key(string address) =>
        IPAddress.TryParse((address ?? "").Trim(), out var ip) ? Normal(ip).ToString() : throw new ArgumentException("Keine gültige Adresse.");

    private static string? Short(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var clean = new string(host.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 100 ? clean[..100] : clean;
    }

    /// <summary>Stand schreiben: sofort bei Änderungen, Zähler höchstens einmal je Minute.</summary>
    private void Save(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _saved < TimeSpan.FromMinutes(1)) return;
        _saved = now;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored(LearnUntil, _all.Values.ToList())));
            File.Move(tmp, _file, true);
        }
        catch (IOException) { }
    }
}
