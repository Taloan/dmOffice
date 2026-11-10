using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Globale Spamlisten (Version 2, Einstellungen → Junk-E-Mail): DNS-Sperrlisten für den Server, der die Mail eingeliefert hat
/// (Received), und für Absenderdomain und Links. Ein Treffer geht als Merkmal "rbl:&lt;liste&gt;" in den lernenden Filter
/// (SpamFilter lernt, wie viel jede Liste taugt); je Liste kann ein Treffer die Mail auch gleich in Junk schieben.
/// Gefragt wird nur nach IP-Adressen und Domains, nie nach Inhalten. Ergebnisse je Mail in spam_rbl, damit späteres
/// „Junk“/„Kein Junk“ dieselben Merkmale lernt; Antworten eine Stunde im Speicher.
/// </summary>
public static partial class Blocklists
{
    public enum Kind { Ip, Domain }

    /// <summary>Eine Liste. Dqs: Zone über den Spamhaus-Zugangsschlüssel (Data Query Service), {0} = Schlüssel.</summary>
    public sealed record List(string Id, string Name, Kind Kind, string Zone, string? Dqs = null);

    public static readonly List[] All =
    [
        new("zen", "Spamhaus ZEN", Kind.Ip, "zen.spamhaus.org", "{0}.zen.dq.spamhaus.net"),
        new("dbl", "Spamhaus DBL", Kind.Domain, "dbl.spamhaus.org", "{0}.dbl.dq.spamhaus.net"),
        new("spamcop", "SpamCop", Kind.Ip, "bl.spamcop.net"),
        new("psbl", "PSBL", Kind.Ip, "psbl.surriel.com"),
        new("surbl", "SURBL", Kind.Domain, "multi.surbl.org"),
        new("uribl", "URIBL", Kind.Domain, "multi.uribl.com"),
    ];

    private const int MaxDomains = 12;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3), Keep = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, (DateTime At, bool Listed)> Cache = new();

    /// <summary>Zum Testen: statt DNS (Name → Antwort-Adressen, leer = nicht gelistet).</summary>
    internal static Func<string, CancellationToken, Task<IPAddress[]>>? ResolveOverride;

    private static void Ensure(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS spam_rbl (key TEXT PRIMARY KEY, hits TEXT NOT NULL, at INTEGER NOT NULL)");

    /// <summary>Fragt die eingeschalteten Listen und merkt sich die Treffer zur Mail. Gibt die Kennungen der Treffer zurück.</summary>
    public static async Task<List<string>> CheckAsync(UserDb db, MimeMessage m, IEnumerable<string> lists, string? spamhausKey, CancellationToken ct)
    {
        var on = All.Where(l => lists.Contains(l.Id)).ToList();
        if (on.Count == 0) return [];
        var ip = SenderIp(m);
        var domains = Domains(m);
        var hits = new List<string>();
        foreach (var l in on)
        {
            var zone = l.Dqs != null && !string.IsNullOrEmpty(spamhausKey) ? string.Format(l.Dqs, spamhausKey) : l.Zone;
            List<string> names = l.Kind == Kind.Ip
                ? ip == null ? [] : [Reverse(ip) + "." + zone]
                : domains.Select(d => d + "." + zone).ToList();
            foreach (var name in names)
                if (await Listed(name, ct)) { hits.Add(l.Id); break; }
        }
        Ensure(db);
        db.Exec("INSERT OR REPLACE INTO spam_rbl(key, hits, at) VALUES($k,$h,$t)",
            ("$k", SpamFilter.KeyOf(m)), ("$h", string.Join(",", hits)), ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return hits;
    }

    /// <summary>Gemerkte Treffer einer Mail als Merkmale für den Filter.</summary>
    public static IEnumerable<string> Tokens(UserDb db, MimeMessage m)
    {
        Ensure(db);
        return db.Scalar("SELECT hits FROM spam_rbl WHERE key=$k", ("$k", SpamFilter.KeyOf(m))) is string h && h.Length > 0
            ? h.Split(',').Select(x => "rbl:" + x) : [];
    }

    private static async Task<bool> Listed(string name, CancellationToken ct)
    {
        if (Cache.TryGetValue(name, out var c) && DateTime.UtcNow - c.At < Keep) return c.Listed;
        bool listed;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var answer = ResolveOverride != null ? await ResolveOverride(name, cts.Token) : await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, cts.Token);
            listed = answer.Any(IsListing);
        }
        catch (SocketException) { listed = false; }                                  // NXDOMAIN: nicht gelistet
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; } // zu langsam: nicht merken
        Cache[name] = (DateTime.UtcNow, listed);
        if (Cache.Count > 20_000) foreach (var old in Cache.Where(x => DateTime.UtcNow - x.Value.At > Keep).Select(x => x.Key).ToList()) Cache.TryRemove(old, out _);
        return listed;
    }

    /// <summary>
    /// Treffer sind Adressen 127.x.y.z. Keine Treffer: 127.0.0.1 (URIBL/SURBL: Anfrage abgelehnt, meist über öffentliche
    /// DNS-Server) und 127.255.255.x (Spamhaus: Fehler, öffentlicher Resolver, kein Schlüssel).
    /// </summary>
    internal static bool IsListing(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length == 4 && b[0] == 127 && !(b[1] == 0 && b[2] == 0 && b[3] == 1) && !(b[1] == 255 && b[2] == 255);
    }

    private static string Reverse(IPAddress ip) => string.Join(".", ip.GetAddressBytes().Reverse());

    [GeneratedRegex(@"\[(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\]")]
    private static partial Regex BracketIp();

    /// <summary>
    /// Der Server, der die Mail beim Anbieter eingeliefert hat: die oberste öffentliche IPv4-Adresse in einem Received-Kopf
    /// (darüber stehen nur die internen Stationen des Anbieters, meist ohne oder mit privater Adresse).
    /// </summary>
    internal static IPAddress? SenderIp(MimeMessage m)
    {
        foreach (var h in m.Headers.Where(h => h.Id == HeaderId.Received))
        {
            var from = h.Value;
            var by = from.IndexOf(" by ", StringComparison.OrdinalIgnoreCase);
            if (by > 0) from = from[..by];
            foreach (Match x in BracketIp().Matches(from))
                if (IPAddress.TryParse(x.Groups[1].Value, out var ip) && IsPublic(ip)) return ip;
        }
        return null;
    }

    private static bool IsPublic(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return !(b[0] is 10 or 127 or 0 || b[0] >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }

    [GeneratedRegex(@"https?://([a-z0-9.-]+\.[a-z]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex LinkHost();

    private static readonly HashSet<string> TwoLevel = ["co.uk", "org.uk", "ac.uk", "com.au", "net.au", "co.at", "or.at", "co.nz", "com.br", "co.jp", "com.tr", "com.cn"];

    /// <summary>Absenderdomain und die Domains der Links (registrierbarer Teil), höchstens zwölf.</summary>
    internal static List<string> Domains(MimeMessage m)
    {
        var hosts = new List<string>();
        foreach (var f in m.From.Mailboxes) if (f.Address.IndexOf('@') is > 0 and var at) hosts.Add(f.Address[(at + 1)..]);
        foreach (Match x in LinkHost().Matches((m.HtmlBody ?? "") + " " + (m.TextBody ?? ""))) hosts.Add(x.Groups[1].Value);
        return hosts.Select(Registrable).Where(d => d != null).Select(d => d!).Distinct().Take(MaxDomains).ToList();
    }

    private static string? Registrable(string host)
    {
        var parts = host.Trim('.').ToLowerInvariant().Split('.');
        if (parts.Length < 2) return null;
        var two = string.Join(".", parts[^2..]);
        return TwoLevel.Contains(two) && parts.Length >= 3 ? string.Join(".", parts[^3..]) : two;
    }
}
