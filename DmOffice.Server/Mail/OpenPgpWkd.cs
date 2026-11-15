using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace DmOffice.Mail;

/// <summary>
/// Web Key Directory (draft-koch-openpgp-webkey-service): der öffentliche Schlüssel einer Adresse beim Mailanbieter
/// selbst - zuerst unter openpgpkey.&lt;domain&gt; („advanced“), sonst unter &lt;domain&gt; („direct“). Nur Adressen im
/// Internet (wie beim Rezept-Import), höchstens 256 KB, kurze Zeitgrenze.
/// </summary>
public static class OpenPgpWkd
{
    private const int MaxBytes = 256 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true, MaxAutomaticRedirections = 3,
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addrs.FirstOrDefault(Notes.RecipeImport.IsPublic) ?? throw new HttpRequestException("Diese Adresse ist nicht erlaubt.");
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    }) { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Die beiden Adressen für eine Mailadresse (advanced, direct); null bei ungültiger Adresse.</summary>
    public static (Uri Advanced, Uri Direct)? Urls(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return null;
        var local = email[..at];
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        if (!domain.Contains('.') || !domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) return null;
        var hash = ZBase32(SHA1.HashData(Encoding.UTF8.GetBytes(local.ToLowerInvariant())));
        var l = Uri.EscapeDataString(local);
        return (new Uri($"https://openpgpkey.{domain}/.well-known/openpgpkey/{domain}/hu/{hash}?l={l}"),
                new Uri($"https://{domain}/.well-known/openpgpkey/hu/{hash}?l={l}"));
    }

    /// <summary>z-base-32 (RFC 6189), wie WKD es für den Hash des lokalen Teils verlangt.</summary>
    internal static string ZBase32(byte[] data)
    {
        const string alphabet = "ybndrfg8ejkmcpqxot1uwisza345h769";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Schlüssel der Adresse holen; nur Schlüssel, in denen genau diese Adresse steht. Fehler: leere Liste.</summary>
    public static async Task<List<PgpPublicKeyRing>> FetchAsync(string email, CancellationToken ct)
    {
        email = email.Trim().ToLowerInvariant();
        if (Urls(email) is not { } urls) return [];
        foreach (var url in new[] { urls.Advanced, urls.Direct })
        {
            try
            {
                using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode) continue;
                if (res.Content.Headers.ContentLength > MaxBytes) continue;
                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                var ms = new MemoryStream();
                var buf = new byte[16384];
                int n;
                while ((n = await stream.ReadAsync(buf, ct)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxBytes) break;
                }
                if (ms.Length is 0 or > MaxBytes) continue;
                var rings = OpenPgpKeys.ReadPublic(ms.ToArray()).Where(r => OpenPgpKeys.Emails(r).Contains(email)).ToList();
                if (rings.Count > 0) return rings;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or ArgumentException && !ct.IsCancellationRequested) { }
        }
        return [];
    }
}
