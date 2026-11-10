using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>
/// Kontoeinrichtung aus der Mailadresse, wie Thunderbird: Server, Ports und Verschluesselung herausfinden.
/// Reihenfolge: bekannte Anbieter, Mozilla-Datenbank (ISPDB), Autoconfig der eigenen Domain,
/// MX-Eintrag (Domain bei Google, Microsoft, IONOS ...) und zuletzt Raten mit Verbindungsprobe.
/// Abgerufen wird nur per HTTPS und nur zu oeffentlichen Adressen.
/// </summary>
public static class MailDiscovery
{
    public sealed record Result(string Source, string ImapHost, int ImapPort, Security ImapSecurity,
        string SmtpHost, int SmtpPort, Security SmtpSecurity, string UserName, bool SaveSentCopy);

    private sealed record Server(string Host, int Port, Security Security, string User);

    // Anbieter, deren ISPDB-Eintrag fehlt oder bei denen wir es besser wissen
    private static readonly Dictionary<string, (string Imap, string Smtp, int SmtpPort, Security SmtpSec, bool SaveSent)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = ("imap.gmail.com", "smtp.gmail.com", 465, Security.Ssl, false),
        ["googlemail.com"] = ("imap.gmail.com", "smtp.gmail.com", 465, Security.Ssl, false),
        ["outlook.com"] = ("outlook.office365.com", "smtp.office365.com", 587, Security.StartTls, true),
        ["hotmail.com"] = ("outlook.office365.com", "smtp.office365.com", 587, Security.StartTls, true),
        ["hotmail.de"] = ("outlook.office365.com", "smtp.office365.com", 587, Security.StartTls, true),
        ["live.de"] = ("outlook.office365.com", "smtp.office365.com", 587, Security.StartTls, true),
        ["live.com"] = ("outlook.office365.com", "smtp.office365.com", 587, Security.StartTls, true),
        ["outlook.de"] = ("outlook.office365.com", "smtp.office365.com", 587, Security.StartTls, true),
        ["gmx.de"] = ("imap.gmx.net", "mail.gmx.net", 587, Security.StartTls, true),
        ["gmx.net"] = ("imap.gmx.net", "mail.gmx.net", 587, Security.StartTls, true),
        ["gmx.at"] = ("imap.gmx.net", "mail.gmx.net", 587, Security.StartTls, true),
        ["web.de"] = ("imap.web.de", "smtp.web.de", 587, Security.StartTls, true),
        ["t-online.de"] = ("secureimap.t-online.de", "securesmtp.t-online.de", 465, Security.Ssl, true),
        ["posteo.de"] = ("posteo.de", "posteo.de", 587, Security.StartTls, true),
        ["mailbox.org"] = ("imap.mailbox.org", "smtp.mailbox.org", 465, Security.Ssl, true),
        ["icloud.com"] = ("imap.mail.me.com", "smtp.mail.me.com", 587, Security.StartTls, true),
        ["me.com"] = ("imap.mail.me.com", "smtp.mail.me.com", 587, Security.StartTls, true),
        ["yahoo.com"] = ("imap.mail.yahoo.com", "smtp.mail.yahoo.com", 465, Security.Ssl, true),
        ["yahoo.de"] = ("imap.mail.yahoo.com", "smtp.mail.yahoo.com", 465, Security.Ssl, true),
    };

    /// <summary>Anbieter hinter einem MX-Eintrag: dort liegen die Postfaecher fremder Domains.</summary>
    private static readonly (string MxSuffix, string Domain)[] MxProviders =
    {
        ("google.com", "gmail.com"), ("googlemail.com", "gmail.com"), ("outlook.com", "outlook.com"),
        ("protection.outlook.com", "outlook.com"),
    };

    /// <summary>Hosts von Anbietern, die fuer IMAP ein App-Passwort statt des normalen Passworts verlangen.</summary>
    private static readonly string[] AppPasswordHosts = { "imap.gmail.com", "imap.mail.me.com", "imap.mail.yahoo.com" };

    /// <summary>Wie sich das Konto anmeldet: "microsoft" (OAuth2, nur wenn eingerichtet), "app-password" oder "password".</summary>
    public static string AuthFor(string email, Result r, bool microsoftOAuth) =>
        microsoftOAuth && MicrosoftOAuth.IsConsumerDomain(email) ? "microsoft"
        : AppPasswordHosts.Contains(r.ImapHost, StringComparer.OrdinalIgnoreCase) ? "app-password"
        : "password";

    public static async Task<Result?> DiscoverAsync(HttpClient http, string email, CancellationToken ct)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return null;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();

        if (Known.TryGetValue(domain, out var k))
            return new Result("known", k.Imap, 993, Security.Ssl, k.Smtp, k.SmtpPort, k.SmtpSec, email, k.SaveSent);
        // Private Microsoft-Konten (hotmail.co.uk, msn.com ...): alle beim selben Dienst
        if (MicrosoftOAuth.IsConsumerDomain(email) && Known.TryGetValue("outlook.com", out var ms))
            return new Result("known", ms.Imap, 993, Security.Ssl, ms.Smtp, ms.SmtpPort, ms.SmtpSec, email, ms.SaveSent);

        if (await Ispdb(http, domain, email, ct) is { } isp) return isp with { Source = "ispdb" };
        if (await DomainAutoconfig(http, domain, email, ct) is { } own) return own with { Source = "autoconfig" };

        // Eigene Domain bei einem grossen Anbieter: am MX erkennen
        var mxHosts = await Mx(http, domain, ct);
        foreach (var mx in mxHosts)
        {
            var p = MxProviders.FirstOrDefault(x => mx.EndsWith("." + x.MxSuffix) || mx == x.MxSuffix);
            if (p.Domain != null && Known.TryGetValue(p.Domain, out var kp))
                return new Result("mx", kp.Imap, 993, Security.Ssl, kp.Smtp, kp.SmtpPort, kp.SmtpSec, email, kp.SaveSent);
            var mxDomain = BaseDomain(mx);
            if (mxDomain != domain && await Ispdb(http, mxDomain, email, ct) is { } viaMx) return viaMx with { Source = "mx" };
        }

        // Webhoster (All-Inkl, Hetzner ...): der MX-Rechner spricht meist auch IMAP, mit passendem Zertifikat -
        // imap.<domain> zeigt oft auf denselben Rechner, aber mit einem Zertifikat auf dessen Namen
        return await Guess(domain, email, mxHosts.Take(2), ct);
    }

    private static async Task<Result?> Ispdb(HttpClient http, string domain, string email, CancellationToken ct) =>
        Parse(await Fetch(http, $"https://autoconfig.thunderbird.net/v1.1/{Uri.EscapeDataString(domain)}", ct), email);

    private static async Task<Result?> DomainAutoconfig(HttpClient http, string domain, string email, CancellationToken ct)
    {
        var q = "?emailaddress=" + Uri.EscapeDataString(email);
        return Parse(await Fetch(http, $"https://autoconfig.{domain}/mail/config-v1.1.xml{q}", ct), email)
            ?? Parse(await Fetch(http, $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml{q}", ct), email);
    }

    private static async Task<string?> Fetch(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url);
            if (!await Unsubscribe.IsPublicAsync(uri.Host, ct)) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var res = await http.GetAsync(uri, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            return text.Length > 200_000 ? null : text;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Thunderbird-Autoconfig (config-v1.1.xml) lesen; SSL vor STARTTLS, Klartext nie.</summary>
    internal static Result? Parse(string? xml, string email)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch (Exception) { return null; }
        var provider = doc.Descendants("emailProvider").FirstOrDefault();
        if (provider == null) return null;
        Server? Pick(string element, string type) => provider.Elements(element)
            .Where(e => (string?)e.Attribute("type") == type)
            .Select(e => new Server(
                Expand((string?)e.Element("hostname") ?? "", email),
                int.TryParse((string?)e.Element("port"), out var p) ? p : 0,
                ((string?)e.Element("socketType"))?.ToUpperInvariant() switch { "SSL" => Security.Ssl, "STARTTLS" => Security.StartTls, _ => Security.None },
                Expand((string?)e.Element("username") ?? "%EMAILADDRESS%", email)))
            .Where(s => s.Host.Length > 0 && s.Port > 0 && s.Security != Security.None)
            .OrderBy(s => s.Security == Security.Ssl ? 0 : 1)
            .FirstOrDefault();
        var imap = Pick("incomingServer", "imap");
        var smtp = Pick("outgoingServer", "smtp");
        if (imap == null || smtp == null) return null;
        return new Result("", imap.Host, imap.Port, imap.Security, smtp.Host, smtp.Port, smtp.Security, imap.User, true);
    }

    private static string Expand(string s, string email)
    {
        var at = email.LastIndexOf('@');
        return s.Replace("%EMAILADDRESS%", email).Replace("%EMAILLOCALPART%", email[..at]).Replace("%EMAILDOMAIN%", email[(at + 1)..]).Trim();
    }

    /// <summary>MX-Eintraege ueber DNS-over-HTTPS (.NET hat keinen MX-Resolver), nach Prioritaet.</summary>
    private static async Task<List<string>> Mx(HttpClient http, string domain, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(domain)}&type=MX");
            req.Headers.Accept.ParseAdd("application/dns-json");
            using var res = await http.SendAsync(req, cts.Token);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(cts.Token);
            if (!json.TryGetProperty("Answer", out var answers)) return new();
            return answers.EnumerateArray()
                .Where(a => a.GetProperty("type").GetInt32() == 15)
                .Select(a => a.GetProperty("data").GetString()?.Split(' ', 2) ?? Array.Empty<string>())
                .Where(p => p.Length == 2 && int.TryParse(p[0], out _))
                .OrderBy(p => int.Parse(p[0]))
                .Select(p => p[1].TrimEnd('.').ToLowerInvariant())
                .Distinct().ToList();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>"mx00.ionos.de" -> "ionos.de" (zweistufige Endungen wie co.uk beruecksichtigt).</summary>
    internal static string BaseDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        var twoLevel = parts[^2].Length <= 3 && parts[^1].Length == 2 && parts[^2] is "co" or "com" or "org" or "net" or "ac" or "gv" or "or";
        return string.Join('.', parts[^(twoLevel ? 3 : 2)..]);
    }

    /// <summary>Kein Eintrag gefunden: uebliche Namen durchprobieren (nur Verbindung, keine Anmeldung).</summary>
    private static async Task<Result?> Guess(string domain, string email, IEnumerable<string> mxHosts, CancellationToken ct)
    {
        Server? imap = null, smtp = null;
        foreach (var host in mxHosts.Concat(new[] { $"imap.{domain}", $"mail.{domain}", domain }).Distinct())
        {
            if (!await Unsubscribe.IsPublicAsync(host, ct)) continue;
            if (await Probe(host, 993, Security.Ssl, false, ct)) { imap = new Server(host, 993, Security.Ssl, email); break; }
            if (await Probe(host, 143, Security.StartTls, false, ct)) { imap = new Server(host, 143, Security.StartTls, email); break; }
        }
        if (imap == null) return null;
        foreach (var host in new[] { $"smtp.{domain}", $"mail.{domain}", imap.Host, domain }.Distinct())
        {
            if (!await Unsubscribe.IsPublicAsync(host, ct)) continue;
            if (await Probe(host, 465, Security.Ssl, true, ct)) { smtp = new Server(host, 465, Security.Ssl, email); break; }
            if (await Probe(host, 587, Security.StartTls, true, ct)) { smtp = new Server(host, 587, Security.StartTls, email); break; }
        }
        if (smtp == null) return null;
        return new Result("guess", imap.Host, imap.Port, imap.Security, smtp.Host, smtp.Port, smtp.Security, email, true);
    }

    private static async Task<bool> Probe(string host, int port, Security security, bool isSmtp, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            MailKit.MailService client = isSmtp ? new MailKit.Net.Smtp.SmtpClient() : new MailKit.Net.Imap.ImapClient();
            using (client)
            {
                client.Timeout = 5000;
                await client.ConnectAsync(host, port, ImapPool.Options(security), cts.Token);
                await client.DisconnectAsync(true, cts.Token);
            }
            return true;
        }
        catch (Exception) { return false; }
    }
}
