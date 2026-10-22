using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using DmOffice.Core;
using MimeKit;
using MimeKit.Cryptography;

namespace DmOffice.Mail;

/// <summary>
/// Warnmerkmale fuer Phishing, ganz ohne Dienst von aussen: Absenderpruefung des eigenen Mailservers fehlgeschlagen,
/// Linktext und Linkziel passen nicht, aehnlich aussehende Domain, erstmaliger Absender mit Anmeldelink.
/// Gerufen beim Anzeigen einer Mail; das Ergebnis ist nur ein Hinweis, verschoben oder gesperrt wird nichts.
/// Die Saetze fuer den Benutzer baut die Oberflaeche aus Art und Domains (Deutsch und Englisch).
/// </summary>
public static partial class PhishCheck
{
    /// <summary>Ein Merkmal.
    /// Kind "auth": Domain = Absenderdomain, Detail "dmarc" oder "spf".
    /// Kind "link": Shown = angezeigte Domain, Target = tatsaechliches Ziel.
    /// Kind "lookalike": Domain = verdaechtige Domain, Like = bekannte Domain, der sie gleicht (bei "mixed" leer), Detail "sender", "link" oder "mixed".
    /// Kind "newsender": Domain = Absenderadresse, Target = Ziel des Anmeldelinks.
    /// Strong: allein schon Grund zur Warnung; sonst erst zusammen mit einem zweiten Merkmal.</summary>
    public sealed record Finding(string Kind, bool Strong, string? Domain = null, string? Shown = null, string? Target = null,
        string? Like = null, string? Detail = null);

    /// <summary>Was die Pruefung ausser der Mail braucht - im Test von Hand gefuellt.</summary>
    public sealed class Context
    {
        /// <summary>Absender steht im Adressbuch oder bekam schon Post von uns.</summary>
        public bool KnownSender { get; init; }
        /// <summary>Registrierbare Domains, die man kennt (ASCII): Adressbuch, gesendete Mails, eigene Konten, eingebaute Marken.</summary>
        public IReadOnlySet<string> KnownDomains { get; init; } = new HashSet<string>();
        /// <summary>Hosts des eigenen Mailservers (IMAP-Server, Domain der eigenen Adresse): nur deren Authentication-Results zaehlen.</summary>
        public IReadOnlyCollection<string> OwnHosts { get; init; } = [];
        /// <summary>Kam vom Absender schon frueher Post? Nur bei Bedarf gefragt (Datenbank).</summary>
        public Func<bool> WroteBefore { get; init; } = () => false;
    }

    private const int MaxPerKind = 3;

    // ---------------- Einstieg vom Server ----------------

    public static List<Finding> Check(UserSpace space, MailAccount account, MimeMessage m)
    {
        try
        {
            var known = SafeSenders.Known(space);
            var from = m.From.Mailboxes.FirstOrDefault()?.Address?.ToLowerInvariant() ?? "";
            var domains = new HashSet<string>(KnownDomainsOf(known));
            foreach (var a in space.Settings.MailAccounts)
                if (Ascii(DomainOf(a.Email)) is { } own) domains.Add(Registrable(own));
            var ctx = new Context
            {
                KnownSender = from.Length > 0 && known.Contains(from),
                KnownDomains = domains,
                OwnHosts = [account.ImapHost, DomainOf(account.Email)],
                // Die angezeigte Mail liegt selbst schon in der Datenbank - "frueher" heisst also eine zweite
                WroteBefore = () => from.Length > 0 && space.Db.Query(
                    "SELECT COUNT(*) FROM (SELECT 1 FROM mail WHERE from_addr = $a COLLATE NOCASE LIMIT 2)", r => r.GetInt64(0), ("$a", from))[0] > 1,
            };
            return Analyze(m, ctx);
        }
        catch (Exception)
        {
            // Ein Hinweis darf die Anzeige der Mail nie verhindern
            return [];
        }
    }

    /// <summary>Domains der sicheren Absender, je Fassung der Menge einmal gerechnet (SafeSenders haelt sie 10 Minuten).</summary>
    private static readonly ConditionalWeakTable<HashSet<string>, HashSet<string>> DomainCache = new();

    private static HashSet<string> KnownDomainsOf(HashSet<string> addresses) => DomainCache.GetValue(addresses, set =>
    {
        var result = new HashSet<string>(Brands);
        foreach (var a in set)
            if (Ascii(DomainOf(a)) is { } d) result.Add(Registrable(d));
        return result;
    });

    // ---------------- Pruefung ----------------

    public static List<Finding> Analyze(MimeMessage m, Context ctx)
    {
        var found = new List<Finding>();
        var from = m.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        var fromHost = Ascii(DomainOf(from));

        // 1. Absenderpruefung des eigenen Mailservers
        var auth = Auth(m, ctx.OwnHosts);
        // ARC bestanden: ein Verteiler oder eine Weiterleitung hat die Mail veraendert, DMARC scheitert dann zwangslaeufig
        if (auth.Dmarc == "fail" && auth.Arc != "pass") found.Add(new("auth", true, Domain: fromHost, Detail: "dmarc"));
        else if (auth.Dmarc == null && auth.Spf == "fail" && !auth.DkimPass) found.Add(new("auth", false, Domain: fromHost, Detail: "spf"));

        var links = Links(m);

        // 2. Linktext zeigt eine andere Domain als das Ziel. Newsletter leiten ueber Zaehl-Domains um - stammt die Mail
        // nachweislich vom Absender (DMARC) und ist er bekannt oder ein Verteiler, ist es nur ein Hinweis.
        bool vouched = auth.Dmarc == "pass" && (ctx.KnownSender || m.Headers.Contains(HeaderId.ListUnsubscribe));
        var seen = new HashSet<(string, string)>();
        foreach (var l in links)
        {
            if (ShownHost(l.Text) is not { } shown) continue;
            if (Registrable(shown) == Registrable(l.Host) || !seen.Add((shown, l.Host))) continue;
            found.Add(new("link", !vouched, Shown: shown, Target: l.Host));
            if (seen.Count >= MaxPerKind) break;
        }

        // 3. Aehnlich aussehende Domains: zuerst der Absender, dann die Linkziele
        int alike = 0;
        var checkedHosts = new HashSet<string>();
        foreach (var (host, what) in new[] { (fromHost, "sender") }.Concat(links.Select(l => ((string?)l.Host, "link"))))
        {
            if (host == null || !checkedHosts.Add(host)) continue;
            if (MixedScripts(host))
                found.Add(new("lookalike", true, Domain: Unicode(host), Detail: "mixed"));
            else if (LooksLike(host, ctx.KnownDomains) is { } like)
                found.Add(new("lookalike", true, Domain: Unicode(host), Like: Unicode(like), Detail: what));
            else continue;
            if (++alike >= MaxPerKind) break;
        }

        // 4. Erstmaliger Absender mit einem Link zum Anmelden, Passwort, Konto bestaetigen ...
        if (!ctx.KnownSender && from.Length > 0
            && links.FirstOrDefault(l => LoginRx().IsMatch(l.Text) || LoginRx().IsMatch(l.Host + l.Path)) is { } login
            && !ctx.WroteBefore())
            found.Add(new("newsender", false, Domain: from.ToLowerInvariant(), Target: login.Host));

        return found;
    }

    // ---------------- Authentication-Results ----------------

    private sealed record AuthResult(string? Dmarc, string? Spf, bool DkimPass, string? Arc);

    /// <summary>Ergebnis der obersten Authentication-Results-Zeile des eigenen Mailservers. Zeilen mit fremder Kennung
    /// (authserv-id) kann jeder Absender selbst hineinschreiben - sie zaehlen nicht.</summary>
    private static AuthResult Auth(MimeMessage m, IEnumerable<string> ownHosts)
    {
        var trusted = ownHosts.Select(Ascii).OfType<string>().Select(Registrable).ToHashSet();
        // Dazu die ersten Empfangsstationen ("Received: from … by mx.google.com"): sie stehen oben, darunter kann der Absender schreiben
        foreach (var r in m.Headers.Where(h => h.Id == HeaderId.Received).Take(2))
            if (ByRx().Match(r.Value) is { Success: true } x && Ascii(x.Groups[1].Value) is { } by) trusted.Add(Registrable(by));
        // Exchange Online schreibt keine Kennung
        bool microsoft = trusted.Any(t => t is "office365.com" or "outlook.com");

        foreach (var h in m.Headers.Where(h => h.Id == HeaderId.AuthenticationResults))
        {
            if (!AuthenticationResults.TryParse(h.RawValue, out var ar)) continue;
            var id = ar.AuthenticationServiceIdentifier;
            if (string.IsNullOrEmpty(id) ? !microsoft : Ascii(id) is not { } ascii || !trusted.Contains(Registrable(ascii))) continue;
            string? Result(string method) => ar.Results.FirstOrDefault(r => r.Method.Equals(method, StringComparison.OrdinalIgnoreCase))?.Result?.ToLowerInvariant();
            return new AuthResult(Result("dmarc"), Result("spf"),
                ar.Results.Any(r => r.Method.Equals("dkim", StringComparison.OrdinalIgnoreCase) && r.Result.Equals("pass", StringComparison.OrdinalIgnoreCase)),
                Result("arc"));
        }
        return new AuthResult(null, null, false, null);
    }

    // ---------------- Links ----------------

    private sealed record Link(string Host, string Path, string Text);

    private static List<Link> Links(MimeMessage m)
    {
        var list = new List<Link>();
        if (m.HtmlBody is { } html)
        {
            var doc = new HtmlParser().ParseDocument(html);
            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                if (Target(a.GetAttribute("href")) is not { } uri) continue;
                list.Add(new Link(uri.Host, uri.AbsolutePath, SpaceRx().Replace(a.TextContent, " ").Trim()));
                if (list.Count >= 500) break;
            }
        }
        else if (m.TextBody is { } text)
            foreach (Match x in UrlRx().Matches(text))
                if (Target(x.Value) is { } uri) list.Add(new Link(uri.Host, uri.AbsolutePath, ""));
        return list;
    }

    /// <summary>Ziel eines Links mit Host in ASCII (Punycode), klein; nur http und https.</summary>
    private static (string Host, string AbsolutePath)? Target(string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');
        return host.Length == 0 ? null : (host, uri.AbsolutePath);
    }

    /// <summary>Domain, die ein Linktext als Adresse zeigt ("www.sparkasse.de", "https://paypal.com/login"), sonst null.
    /// Ohne "http" oder "www." muss die Endung wie eine Top-Level-Domain aussehen - "bericht.pdf" ist keine Adresse.</summary>
    private static string? ShownHost(string text)
    {
        var x = ShownRx().Match(text);
        if (!x.Success) return null;
        var host = Ascii(x.Groups["host"].Value);
        if (host == null) return null;
        bool explicitAddress = x.Groups["scheme"].Success || host.StartsWith("www.");
        var tld = host[(host.LastIndexOf('.') + 1)..];
        if (!explicitAddress && tld.Length != 2 && !CommonTlds.Contains(tld)) return null;
        return host;
    }

    private static readonly HashSet<string> CommonTlds =
        ["com", "net", "org", "info", "biz", "online", "shop", "store", "app", "dev", "site", "xyz", "top", "club", "live", "cloud", "email", "bank", "gov", "edu"];

    // ---------------- Aehnliche Domains ----------------

    /// <summary>Bekannte Domain, der die (unbekannte) Domain des Hosts zum Verwechseln aehnlich sieht, sonst null.</summary>
    private static string? LooksLike(string host, IReadOnlySet<string> known)
    {
        var reg = Registrable(host);
        if (known.Contains(reg)) return null;
        // Bekannte Domain vorgeschoben: paypal.com.konto-pruefen.example
        foreach (var k in known)
            if (host.StartsWith(k + ".", StringComparison.Ordinal) || host.Contains("." + k + ".", StringComparison.Ordinal)) return k;
        var skeleton = Skeleton(Unicode(reg));
        foreach (var k in known)
            if (Skeleton(Unicode(k)) == skeleton || OneEdit(reg, k)) return k;
        return null;
    }

    /// <summary>Ein Zeichen Unterschied (ersetzt, eingefuegt, weggelassen, vertauscht) im Namen vor der Endung,
    /// bei gleicher Endung und Namen ab 6 Zeichen - kuerzere Namen gleichen einander zu oft zufaellig.</summary>
    private static bool OneEdit(string a, string b)
    {
        int da = a.IndexOf('.'), db = b.IndexOf('.');
        if (da < 6 || db < 6 || a[da..] != b[db..]) return false;
        string x = a[..da], y = b[..db];
        if (x == y || Math.Abs(x.Length - y.Length) > 1) return false;
        int i = 0;
        while (i < Math.Min(x.Length, y.Length) && x[i] == y[i]) i++;
        if (x.Length == y.Length)
            return x[(i + 1)..] == y[(i + 1)..]
                || (i + 1 < x.Length && x[i] == y[i + 1] && x[i + 1] == y[i] && x[(i + 2)..] == y[(i + 2)..]);
        return x.Length > y.Length ? x[(i + 1)..] == y[i..] : x[i..] == y[(i + 1)..];
    }

    /// <summary>Form zum Vergleichen: verwechselbare Zeichen auf ein lateinisches Vorbild gebracht, Akzente weg.</summary>
    private static string Skeleton(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant().Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(Confusables.TryGetValue(c, out var r) ? r : c);
        }
        return sb.ToString().Replace("rn", "m").Replace("vv", "w");
    }

    private static readonly Dictionary<char, char> Confusables = new()
    {
        ['0'] = 'o', ['1'] = 'l', ['ı'] = 'i',
        // Kyrillisch
        ['а'] = 'a', ['в'] = 'b', ['е'] = 'e', ['ё'] = 'e', ['к'] = 'k', ['м'] = 'm', ['н'] = 'h', ['о'] = 'o', ['р'] = 'p',
        ['с'] = 'c', ['т'] = 't', ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's', ['ԁ'] = 'd', ['ӏ'] = 'l',
        ['һ'] = 'h', ['ԛ'] = 'q', ['ԝ'] = 'w', ['ɡ'] = 'g',
        // Griechisch
        ['α'] = 'a', ['ο'] = 'o', ['ρ'] = 'p', ['ι'] = 'i', ['κ'] = 'k', ['ν'] = 'v', ['τ'] = 't', ['υ'] = 'u', ['χ'] = 'x',
        ['ε'] = 'e', ['η'] = 'n', ['β'] = 'b',
    };

    /// <summary>Lateinische und kyrillische oder griechische Buchstaben in einem Namen - so baut man bekannte Namen nach.</summary>
    private static bool MixedScripts(string host)
    {
        if (!host.Contains("xn--")) return false;
        foreach (var label in Unicode(host).Split('.'))
        {
            bool latin = false, other = false;
            foreach (var c in label)
            {
                if (c is >= 'a' and <= 'z') latin = true;
                else if (c is >= 'Ͱ' and <= 'Ͽ' or >= 'Ѐ' and <= 'ԯ') other = true;
            }
            if (latin && other) return true;
        }
        return false;
    }

    /// <summary>Oft nachgeahmte Absender - gelten auch ohne Eintrag im Adressbuch als bekannt.</summary>
    private static readonly string[] Brands =
    [
        "paypal.com", "paypal.de", "amazon.de", "amazon.com", "amazon.at", "apple.com", "icloud.com", "microsoft.com", "office.com",
        "outlook.com", "live.com", "google.com", "gmail.com", "dhl.de", "dhl.com", "dpd.de", "hermesworld.com", "ups.com", "post.at",
        "deutschepost.de", "sparkasse.de", "volksbank.de", "commerzbank.de", "deutsche-bank.de", "postbank.de", "ing.de", "dkb.de",
        "comdirect.de", "consorsbank.de", "n26.com", "raiffeisen.at", "sparkasse.at", "erstebank.at", "bankaustria.at", "bawag.at",
        "ubs.com", "postfinance.ch", "telekom.de", "vodafone.de", "o2online.de", "a1.net", "magenta.at", "swisscom.ch", "gmx.de",
        "gmx.net", "web.de", "t-online.de", "netflix.com", "spotify.com", "ebay.de", "ebay.com", "facebook.com", "instagram.com",
        "whatsapp.com", "linkedin.com", "dropbox.com", "docusign.com", "docusign.net", "adobe.com", "elster.de", "bmf.gv.at",
        "oesterreich.gv.at", "bund.de", "arbeitsagentur.de", "booking.com", "airbnb.com", "zalando.de", "otto.de",
    ];

    // ---------------- Domains ----------------

    private static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? "" : address[(at + 1)..];
    }

    /// <summary>Host in ASCII (Punycode), klein, ohne Punkt am Ende; null, wenn er sich nicht umwandeln laesst.</summary>
    private static string? Ascii(string? host)
    {
        host = host?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(host)) return null;
        try { return new IdnMapping().GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
    }

    private static string Unicode(string host)
    {
        try { return new IdnMapping().GetUnicode(host); }
        catch (ArgumentException) { return host; }
    }

    /// <summary>Registrierbare Domain ohne die vollstaendige Public Suffix List: die letzten zwei Teile, bei Zweitebenen
    /// unter Laenderendungen ("co.uk", "gv.at", "com.au") drei. Fuer Warnungen genau genug.</summary>
    public static string Registrable(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        bool secondLevel = labels[^1].Length == 2 && SecondLevels.Contains(labels[^2]);
        return string.Join('.', labels[(secondLevel ? ^3 : ^2)..]);
    }

    private static readonly HashSet<string> SecondLevels = ["co", "com", "net", "org", "gov", "gv", "ac", "edu", "or", "ne", "go", "priv", "ltd", "plc", "me"];

    [GeneratedRegex(@"^(?<scheme>https?://)?(?<host>(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?\.)+\p{L}{2,})(?:[:/?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ShownRx();

    [GeneratedRegex(@"\bby\s+([a-z0-9.-]+\.[a-z]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex ByRx();

    [GeneratedRegex(@"https?://[^\s<>""]+")]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRx();

    [GeneratedRegex(@"anmeld|einlogg|log-?in|log-?on|sign-?in|signon|passwor|kennwort|verifizier|verify|verification|best(ä|ae)tig|confirm|entsperr|unlock|reaktivier|webscr|konto|account", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRx();
}
