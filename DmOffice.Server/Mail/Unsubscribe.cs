using System.Net;
using System.Net.Sockets;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Von Newslettern abmelden ueber die Kopfzeilen List-Unsubscribe (RFC 2369) und List-Unsubscribe-Post (RFC 8058).
/// Reihenfolge wie bei Gmail/Outlook: One-Click per HTTPS-POST, sonst eine Mail an die mailto-Adresse, sonst bleibt
/// nur der Link - den oeffnet der Browser bzw. die App, damit kein Server unbemerkt fremde Seiten aufruft.
/// </summary>
public static class Unsubscribe
{
    public sealed record Info(string? Url, string? Mailto, bool OneClick);

    /// <summary>Abmeldewege einer Mail; null, wenn sie keine anbietet.</summary>
    public static Info? Of(MimeMessage m)
    {
        var header = m.Headers[HeaderId.ListUnsubscribe];
        if (string.IsNullOrWhiteSpace(header)) return null;
        string? url = null, mailto = null;
        // "<mailto:x@y?subject=unsubscribe>, <https://...>" - Teile in spitzen Klammern
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var v = part.Trim().TrimStart('<').TrimEnd('>').Trim();
            if (v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) mailto ??= v;
            else if (v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url ??= v;
        }
        if (url == null && mailto == null) return null;
        var post = m.Headers["List-Unsubscribe-Post"] ?? "";
        var oneClick = url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && post.Contains("List-Unsubscribe=One-Click", StringComparison.OrdinalIgnoreCase);
        return new Info(url, mailto, oneClick);
    }

    /// <summary>One-Click-Abmeldung (RFC 8058). Nur HTTPS und nur zu oeffentlichen Adressen - eine Mail darf den Server
    /// nicht dazu bringen, Geraete im eigenen Netz anzusprechen.</summary>
    public static async Task<bool> OneClickAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!await IsPublicAsync(uri.Host, ct)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("List-Unsubscribe", "One-Click") }),
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var res = await http.SendAsync(req, cts.Token);
        return res.IsSuccessStatusCode;
    }

    /// <summary>Abmelde-Mail fuer eine mailto-Adresse ("mailto:a@b?subject=x&amp;body=y").</summary>
    public static MimeMessage MailFor(MailAccount account, string mailto)
    {
        var rest = mailto["mailto:".Length..];
        var q = rest.IndexOf('?');
        var to = Uri.UnescapeDataString(q < 0 ? rest : rest[..q]);
        string subject = "unsubscribe", body = "unsubscribe";
        if (q >= 0)
            foreach (var kv in rest[(q + 1)..].Split('&'))
            {
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                var key = kv[..eq].ToLowerInvariant();
                var val = Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
                if (key == "subject" && val.Length > 0) subject = val;
                else if (key == "body" && val.Length > 0) body = val;
            }
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(string.IsNullOrEmpty(account.DisplayName) ? null : account.DisplayName, account.Email));
        m.To.Add(MailboxAddress.Parse(to));
        m.Subject = subject;
        m.Body = new TextPart("plain") { Text = body };
        return m;
    }

    private static async Task<bool> IsPublicAsync(string host, CancellationToken ct)
    {
        IPAddress[] addrs;
        try { addrs = IPAddress.TryParse(host, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(host, ct); }
        catch (SocketException) { return false; }
        return addrs.Length > 0 && addrs.All(IsPublic);
    }

    private static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224);
        }
        var v6 = a.GetAddressBytes();
        return (v6[0] & 0xFE) != 0xFC;   // fc00::/7 (Unique Local)
    }
}
