using System.Buffers.Binary;
using System.Text;

namespace DmOffice.GateKeeper;

/// <summary>Einträge einer Filterliste: Exact nur genau dieser Name (Hosts-Datei), Tree samt Unterdomains (Domainliste,
/// AdBlock „||name^“), Allow erlaubt samt Unterdomains (AdBlock „@@||name^“).</summary>
public sealed class GkListEntries
{
    public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Tree { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Allow { get; } = new(StringComparer.Ordinal);
    public int Count => Exact.Count + Tree.Count + Allow.Count;
}

/// <summary>Ergebnis einer Prüfung: gesperrt oder nicht, und wodurch (user-allow, user-block, list-allow, list, none).</summary>
public readonly record struct GkVerdict(bool Blocked, string Source)
{
    public static readonly GkVerdict Pass = new(false, "none");
}

/// <summary>
/// Filterlogik von GateKeeper (docs/gatekeeper-konzept.md): Listen lesen (Hosts, Domainliste, AdBlock-Domainregeln) und Namen
/// prüfen. Reihenfolge: eigene Erlaubnis, eigene Sperre, Erlaubnis der Listen, Sperre der Listen. Eigene Regeln gelten samt
/// Unterdomains.
/// </summary>
public static class GateKeeperFilter
{
    /// <summary>Höchstens so viele Einträge je Liste (Schutz vor riesigen oder falschen Dateien).</summary>
    public const int MaxEntries = 2_000_000;

    /// <summary>Name vereinheitlichen: klein, ohne Punkt am Ende, ohne Port; null, wenn es kein gültiger Rechnername ist.</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (n.StartsWith("*.")) n = n[2..];
        if (n.Length is 0 or > 253) return null;
        foreach (var c in n)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_')) return null;
        if (n.StartsWith('.') || n.Contains("..")) return null;
        return n;
    }

    /// <summary>Eine Liste lesen. Unbekannte oder nicht unterstützte Zeilen (Pfade, Optionen, kosmetische Regeln) zählen nicht.</summary>
    public static GkListEntries Parse(TextReader reader, GkListEntries? into = null)
    {
        var e = into ?? new GkListEntries();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (e.Count >= MaxEntries) break;
            ParseLine(line, e);
        }
        return e;
    }

    public static GkListEntries Parse(string text) => Parse(new StringReader(text));

    internal static void ParseLine(string raw, GkListEntries e)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] is '#' or '!' or '[') return;
        // Kosmetische Regeln und Ausnahmen davon
        if (line.Contains("##") || line.Contains("#@#") || line.Contains("#?#") || line.Contains("#$#")) return;
        // Hosts-Datei: „0.0.0.0 name [name …] [# Kommentar]“
        var hash = line.IndexOf(" #", StringComparison.Ordinal);
        if (hash < 0) hash = line.IndexOf("\t#", StringComparison.Ordinal);
        if (hash >= 0) line = line[..hash].Trim();
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && System.Net.IPAddress.TryParse(parts[0], out _))
        {
            foreach (var p in parts.Skip(1))
                if (Normalize(p) is { } h && h is not ("localhost" or "localhost.localdomain" or "local" or "broadcasthost" or "ip6-localhost" or "ip6-loopback" or "0.0.0.0")
                    && h.Contains('.')) e.Exact.Add(h);
            return;
        }
        if (parts.Length != 1) return;
        var rule = parts[0];
        bool allow = false;
        if (rule.StartsWith("@@")) { allow = true; rule = rule[2..]; }
        if (rule.StartsWith("||"))
        {
            rule = rule[2..];
            // Nur reine Domainregeln: „name^“ oder „name^|“; mit Optionen ($…) oder Pfad nicht
            if (rule.EndsWith("^|")) rule = rule[..^2];
            else if (rule.EndsWith('^')) rule = rule[..^1];
            else if (rule.Contains('/') || rule.Contains('$') || rule.Contains('*')) return;
            if (rule.Contains('/') || rule.Contains('$') || rule.Contains('*') || rule.Contains('^')) return;
            if (Normalize(rule) is { } d && d.Contains('.')) (allow ? e.Allow : e.Tree).Add(d);
            return;
        }
        if (allow) return;
        // Reine Domainliste: eine Domain je Zeile (gilt samt Unterdomains)
        if (rule.Contains('/') || rule.Contains('$') || rule.Contains('*') || rule.Contains('^') || rule.Contains('|')) return;
        if (Normalize(rule) is { } name && name.Contains('.') && !System.Net.IPAddress.TryParse(name, out _)) e.Tree.Add(name);
    }

    /// <summary>Der Name und alle übergeordneten Domains (a.b.c → a.b.c, b.c, c).</summary>
    public static IEnumerable<string> Suffixes(string name)
    {
        var s = name;
        while (true)
        {
            yield return s;
            var dot = s.IndexOf('.');
            if (dot < 0) yield break;
            s = s[(dot + 1)..];
        }
    }

    /// <summary>Gilt eine Regel (samt Unterdomains) aus der Menge für den Namen?</summary>
    public static bool InTree(string name, IReadOnlySet<string> tree)
    {
        if (tree.Count == 0) return false;
        foreach (var s in Suffixes(name)) if (tree.Contains(s)) return true;
        return false;
    }

    /// <summary>Namen prüfen: eigene Regeln (userAllow, userBlock, je samt Unterdomains), dann die Listen.</summary>
    public static GkVerdict Check(string? host, GkListEntries lists, IReadOnlySet<string> userAllow, IReadOnlySet<string> userBlock)
    {
        if (Normalize(host) is not { } name) return GkVerdict.Pass;
        if (InTree(name, userAllow)) return new(false, "user-allow");
        if (InTree(name, userBlock)) return new(true, "user-block");
        if (InTree(name, lists.Allow)) return new(false, "list-allow");
        if (lists.Exact.Contains(name) || InTree(name, lists.Tree)) return new(true, "list");
        return GkVerdict.Pass;
    }

    // ---------------------------------------------------------------- TLS ClientHello (SNI)

    public enum ParseState { NeedMore, NotMatching, Done }

    /// <summary>Höchstens so viel vom Anfang einer Verbindung wird gelesen, um SNI oder Host zu finden.</summary>
    public const int MaxPeek = 16 * 1024;

    /// <summary>
    /// SNI aus dem Anfang einer TLS-Verbindung lesen. NeedMore: der ClientHello ist noch nicht vollständig; NotMatching: kein
    /// TLS-ClientHello; Done: gelesen (name ist null, wenn der ClientHello keinen SNI enthält).
    /// Ein ClientHello kann über mehrere TLS-Records verteilt sein (große Schlüssel), die Records werden zusammengesetzt.
    /// </summary>
    public static ParseState ReadSni(ReadOnlySpan<byte> data, out string? name)
    {
        name = null;
        // Handshake-Bytes aus den Records sammeln
        var hs = new List<byte>();
        int pos = 0;
        int need = -1;
        while (true)
        {
            if (data.Length - pos < 5) return data.Length >= MaxPeek ? ParseState.NotMatching : ParseState.NeedMore;
            if (data[pos] != 0x16 || data[pos + 1] != 0x03) return ParseState.NotMatching;
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 3, 2));
            if (len == 0 || len > 16384 + 2048) return ParseState.NotMatching;
            if (data.Length - pos - 5 < len)
            {
                // Teil des Records reicht schon, wenn der ClientHello darin vollständig ist
                var part = data.Slice(pos + 5);
                foreach (var b in part) hs.Add(b);
                if (need < 0 && hs.Count >= 4) need = 4 + (hs[1] << 16 | hs[2] << 8 | hs[3]);
                if (need > 0 && hs.Count >= need) break;
                return data.Length >= MaxPeek ? ParseState.NotMatching : ParseState.NeedMore;
            }
            foreach (var b in data.Slice(pos + 5, len)) hs.Add(b);
            pos += 5 + len;
            if (need < 0 && hs.Count >= 4)
            {
                if (hs[0] != 0x01) return ParseState.NotMatching;
                need = 4 + (hs[1] << 16 | hs[2] << 8 | hs[3]);
                if (need > MaxPeek * 4) return ParseState.NotMatching;
            }
            if (need > 0 && hs.Count >= need) break;
        }
        if (hs[0] != 0x01) return ParseState.NotMatching;
        var h = hs.ToArray().AsSpan(4, need - 4);
        try
        {
            int p = 2 + 32; // Version, Zufall
            p += 1 + h[p]; // Session-ID
            p += 2 + BinaryPrimitives.ReadUInt16BigEndian(h.Slice(p, 2)); // Cipher-Suites
            p += 1 + h[p]; // Kompression
            if (p + 2 > h.Length) return ParseState.Done; // ohne Erweiterungen
            int extEnd = p + 2 + BinaryPrimitives.ReadUInt16BigEndian(h.Slice(p, 2));
            p += 2;
            if (extEnd > h.Length) return ParseState.NotMatching;
            while (p + 4 <= extEnd)
            {
                int type = BinaryPrimitives.ReadUInt16BigEndian(h.Slice(p, 2));
                int len = BinaryPrimitives.ReadUInt16BigEndian(h.Slice(p + 2, 2));
                p += 4;
                if (p + len > extEnd) return ParseState.NotMatching;
                if (type == 0)
                {
                    var ext = h.Slice(p, len);
                    int q = 2; // Länge der Liste
                    while (q + 3 <= ext.Length)
                    {
                        int nameType = ext[q];
                        int nameLen = BinaryPrimitives.ReadUInt16BigEndian(ext.Slice(q + 1, 2));
                        q += 3;
                        if (q + nameLen > ext.Length) return ParseState.NotMatching;
                        if (nameType == 0)
                        {
                            name = Encoding.ASCII.GetString(ext.Slice(q, nameLen));
                            return ParseState.Done;
                        }
                        q += nameLen;
                    }
                    return ParseState.Done;
                }
                p += len;
            }
            return ParseState.Done;
        }
        catch (ArgumentOutOfRangeException) { return ParseState.NotMatching; }
        catch (IndexOutOfRangeException) { return ParseState.NotMatching; }
    }

    // ---------------------------------------------------------------- HTTP (Host)

    /// <summary>Host aus dem Kopf einer HTTP-Anfrage (ohne Port). NotMatching: keine HTTP-Anfrage.</summary>
    public static ParseState ReadHttpHost(ReadOnlySpan<byte> data, out string? host)
    {
        host = null;
        // Erste Zeile: Methode in Großbuchstaben, dann Leerzeichen
        int i = 0;
        while (i < data.Length && i < 16 && data[i] is >= (byte)'A' and <= (byte)'Z') i++;
        if (i < data.Length && (i == 0 || i >= 16 || data[i] != (byte)' ')) return ParseState.NotMatching;
        var text = Encoding.Latin1.GetString(data[..Math.Min(data.Length, MaxPeek)]);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = end >= 0 ? text[..end] : text;
        foreach (var line in head.Split("\r\n").Skip(1))
        {
            if (!line.StartsWith("host:", StringComparison.OrdinalIgnoreCase)) continue;
            var v = line[5..].Trim();
            if (v.StartsWith('['))
            {
                var close = v.IndexOf(']');
                host = close > 0 ? v[1..close] : v;
            }
            else
            {
                var colon = v.LastIndexOf(':');
                host = colon > 0 ? v[..colon] : v;
            }
            return ParseState.Done;
        }
        if (end >= 0) return ParseState.Done;
        return data.Length >= MaxPeek ? ParseState.NotMatching : ParseState.NeedMore;
    }

    // ---------------------------------------------------------------- DNS

    public const int TypeA = 1, TypeAaaa = 28;

    /// <summary>Frage einer DNS-Anfrage (erste Frage): Name, Typ und das Ende der Frage im Paket; null bei kaputten Anfragen.</summary>
    public static (string Name, int Type, int End)? ReadDnsQuestion(ReadOnlySpan<byte> q)
    {
        if (q.Length < 12) return null;
        if ((q[2] & 0x80) != 0) return null; // Antwort statt Frage
        if (BinaryPrimitives.ReadUInt16BigEndian(q.Slice(4, 2)) < 1) return null;
        var sb = new StringBuilder();
        int p = 12;
        while (true)
        {
            if (p >= q.Length) return null;
            int len = q[p++];
            if (len == 0) break;
            if (len > 63 || p + len > q.Length) return null; // keine Zeiger in Fragen
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(q.Slice(p, len)));
            p += len;
            if (sb.Length > 253) return null;
        }
        if (p + 4 > q.Length) return null;
        int type = BinaryPrimitives.ReadUInt16BigEndian(q.Slice(p, 2));
        return (sb.ToString(), type, p + 4);
    }

    /// <summary>Antwort für einen gesperrten Namen: A → 0.0.0.0, AAAA → ::, sonst NXDOMAIN. Nur die erste Frage zählt.</summary>
    public static byte[] BlockedDnsAnswer(ReadOnlySpan<byte> query)
    {
        var question = ReadDnsQuestion(query) ?? throw new ArgumentException("Keine DNS-Anfrage.");
        var qLen = question.End - 12;
        bool address = question.Type is TypeA or TypeAaaa;
        int rdLen = question.Type == TypeA ? 4 : 16;
        var outBuf = new byte[12 + qLen + (address ? 12 + rdLen : 0)];
        query[..2].CopyTo(outBuf); // Kennung
        outBuf[2] = (byte)(0x80 | (query[2] & 0x79)); // QR, Opcode, RD
        outBuf[3] = (byte)(0x80 | (address ? 0 : 3)); // RA, RCODE
        outBuf[5] = 1; // eine Frage
        if (address) outBuf[7] = 1; // eine Antwort
        query.Slice(12, qLen).CopyTo(outBuf.AsSpan(12));
        if (address)
        {
            var a = outBuf.AsSpan(12 + qLen);
            a[0] = 0xC0; a[1] = 0x0C; // Zeiger auf den Namen der Frage
            BinaryPrimitives.WriteUInt16BigEndian(a[2..], (ushort)question.Type);
            BinaryPrimitives.WriteUInt16BigEndian(a[4..], 1); // IN
            BinaryPrimitives.WriteUInt32BigEndian(a[6..], 60); // TTL
            BinaryPrimitives.WriteUInt16BigEndian(a[10..], (ushort)rdLen);
            // Adresse bleibt 0
        }
        return outBuf;
    }

    /// <summary>Antwort ohne Adresse (NOERROR, keine Einträge): der Name gibt es, nur nicht in dieser Art (z. B. AAAA ohne IPv6).</summary>
    public static byte[] EmptyDnsAnswer(ReadOnlySpan<byte> query)
    {
        var a = FailedDnsAnswer(query);
        a[3] = 0x80; // RA, NOERROR
        return a;
    }

    /// <summary>Antwort mit Fehler (SERVFAIL), wenn der Resolver nicht antwortet.</summary>
    public static byte[] FailedDnsAnswer(ReadOnlySpan<byte> query)
    {
        var question = ReadDnsQuestion(query) ?? throw new ArgumentException("Keine DNS-Anfrage.");
        var outBuf = new byte[question.End];
        query[..question.End].CopyTo(outBuf);
        outBuf[2] = (byte)(0x80 | (query[2] & 0x79));
        outBuf[3] = 0x82; // RA, SERVFAIL
        outBuf[4] = 0; outBuf[5] = 1;
        for (int i = 6; i < 12; i++) outBuf[i] = 0;
        return outBuf;
    }
}
