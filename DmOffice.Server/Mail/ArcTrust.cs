using System.Collections.Concurrent;
using DnsClient;
using MimeKit;
using MimeKit.Cryptography;

namespace DmOffice.Mail;

/// <summary>
/// Weitergeleitete Mails (Gmail → Outlook → eigener Mailserver): Die Weiterleitung verändert die Mail, DKIM und damit DMARC
/// scheitern beim eigenen Mailserver, obwohl der Absender echt ist. Prüft der eigene Mailserver die ARC-Kette nicht selbst
/// (kein „arc=“ im Prüfvermerk), prüfen wir sie hier: Ist die Kette kryptografisch intakt und hat ein großer Postfachanbieter
/// das letzte Glied versiegelt, gilt dessen Prüfvermerk (ARC-Authentication-Results). Eigene Kettenglieder beliebiger
/// Domains zählen nicht - die kann jeder Absender mit eigener Domain gültig signieren.
/// </summary>
internal static class ArcTrust
{
    /// <summary>Versiegelnde Domains großer Anbieter, deren Prüfvermerk wir übernehmen (registrierbare Domain aus ARC-Seal d=).</summary>
    private static readonly HashSet<string> Sealers =
    [
        "google.com", "microsoft.com", "outlook.com", "icloud.com", "me.com", "yahoo.com", "yahoodns.net", "gmx.net", "web.de",
        "mailbox.org", "posteo.de", "messagingengine.com", "fastmail.com", "zoho.com", "zohomail.eu", "protonmail.ch", "proton.me",
    ];

    /// <summary>Schlüsselabfrage per DNS; Tests setzen eine eigene.</summary>
    internal static IDnsResolver Dns = Resolver.Instance;

    /// <summary>Prüfvermerk des letzten Kettenglieds, wenn die Kette gültig ist und ein bekannter Anbieter versiegelt hat; sonst null.</summary>
    internal static PhishCheck.AuthResult? Of(MimeMessage m)
    {
        try
        {
            // Höchstes Kettenglied: Siegel und Prüfvermerk mit derselben Nummer
            int top = 0; string? sealer = null;
            foreach (var h in m.Headers.Where(h => h.Id == HeaderId.ArcSeal))
            {
                var tags = Tags(h.Value);
                if (tags.TryGetValue("i", out var i) && int.TryParse(i, out var n) && n > top && tags.TryGetValue("d", out var d))
                { top = n; sealer = d; }
            }
            if (top == 0 || sealer == null || PhishCheck.Ascii(sealer) is not { } s || !Sealers.Contains(PhishCheck.Registrable(s))) return null;
            var aar = m.Headers.Where(h => h.Id == HeaderId.ArcAuthenticationResults)
                .Select(h => AuthenticationResults.TryParse(h.RawValue, out var r) ? r : null)
                .FirstOrDefault(r => r?.Instance == top);
            if (aar == null) return null;
            // Erst prüfen, was ohne Netz geht; DNS nur, wenn der Prüfvermerk überhaupt etwas bestätigt
            string? Result(string method) => aar.Results.FirstOrDefault(r => r.Method.Equals(method, StringComparison.OrdinalIgnoreCase))?.Result?.ToLowerInvariant();
            var dkim = aar.Results.Where(r => r.Method.Equals("dkim", StringComparison.OrdinalIgnoreCase) && r.Result.Equals("pass", StringComparison.OrdinalIgnoreCase)).ToList();
            if (Result("dmarc") != "pass" && dkim.Count == 0) return null;
            var verifier = new ArcVerifier(Dns);
            if (verifier.Verify(m).Chain != ArcSignatureValidationResult.Pass) return null;
            var signers = dkim.SelectMany(r => r.Properties).Where(x => x.PropertyType.Equals("header", StringComparison.OrdinalIgnoreCase)
                    && (x.Property.Equals("d", StringComparison.OrdinalIgnoreCase) || x.Property.Equals("i", StringComparison.OrdinalIgnoreCase)))
                .Select(x => PhishCheck.Ascii(x.Value.TrimStart('@').Split('@')[^1])).OfType<string>().Select(PhishCheck.Registrable).Distinct().ToList();
            return new PhishCheck.AuthResult(Result("dmarc"), Result("spf"), dkim.Count > 0, "pass", signers);
        }
        catch { return null; }
    }

    private static Dictionary<string, string> Tags(string value)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) tags[part[..eq].Trim()] = string.Concat(part[(eq + 1)..].Where(c => !char.IsWhiteSpace(c)));
        }
        return tags;
    }

    /// <summary>TXT-Abfragen für die öffentlichen Schlüssel (selector._domainkey.domain), eine Stunde zwischengespeichert.</summary>
    private sealed class Resolver : IDnsResolver
    {
        public static readonly Resolver Instance = new();
        private static readonly LookupClient Client = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(3), Retries = 1, UseCache = true });
        private static readonly ConcurrentDictionary<string, (DateTime At, string[] Records)> Cache = new();

        public DnsTxtResponse QueryTxt(string name, CancellationToken cancellationToken = default)
        {
            if (Cache.TryGetValue(name, out var c) && c.At > DateTime.UtcNow.AddHours(-1)) return new DnsTxtResponse(c.Records);
            // Ein TXT-Eintrag kann aus mehreren Zeichenketten bestehen: zusammensetzen
            var records = Client.Query(name, QueryType.TXT).Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToArray();
            if (Cache.Count > 1000) Cache.Clear();
            Cache[name] = (DateTime.UtcNow, records);
            return new DnsTxtResponse(records);
        }

        public Task<DnsTxtResponse> QueryTxtAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(QueryTxt(name, cancellationToken));
    }
}
