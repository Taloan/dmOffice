using System.Text.RegularExpressions;

namespace DmOffice.Mail;

/// <summary>
/// Alias-Dienste, die Mails an eine Tarnadresse weiterleiten und dabei den Absender durch eine eigene Antwortadresse ersetzen
/// (SimpleLogin/Proton Pass: „shipment-tracking_at_amazon_de_molvhu@simplelogin.co“, DuckDuckGo, addy.io). Für jede Mail ist
/// der Absender damit scheinbar neu und fremd. Aus der Antwortadresse (oder dem Namen) lässt sich der eigentliche Absender
/// ablesen - geglaubt wird das nur, wenn der eigene Mailserver bestätigt hat, dass die Mail wirklich vom Dienst kommt. Bestätigt
/// ist der eigentliche Absender damit nicht; er dient darum nur zum Einordnen (Marke, Linkziel, Anzeige), nie als Vertrauen.
/// </summary>
public static partial class Forwarders
{
    private static readonly Dictionary<string, string> Services = new(StringComparer.OrdinalIgnoreCase)
    {
        ["simplelogin.co"] = "SimpleLogin", ["simplelogin.com"] = "SimpleLogin", ["simplelogin.fr"] = "SimpleLogin", ["slmail.me"] = "SimpleLogin",
        ["aleeas.com"] = "SimpleLogin", ["slmails.com"] = "SimpleLogin", ["silomails.com"] = "SimpleLogin", ["8alias.com"] = "SimpleLogin",
        ["8shield.net"] = "SimpleLogin", ["dralias.com"] = "SimpleLogin",
        ["passinbox.com"] = "Proton Pass", ["passmail.net"] = "Proton Pass", ["passmail.com"] = "Proton Pass", ["passfwd.com"] = "Proton Pass",
        ["duck.com"] = "DuckDuckGo", ["addy.io"] = "addy.io", ["anonaddy.me"] = "addy.io", ["anonaddy.com"] = "addy.io",
        ["mozmail.com"] = "Firefox Relay",
    };

    /// <param name="Service">Name des Dienstes zum Anzeigen</param>
    /// <param name="Original">Eigentlicher Absender (klein)</param>
    /// <param name="Domain">Dessen Domain (ASCII)</param>
    /// <param name="PerSender">Eigene Antwortadresse je Absender (SimpleLogin …). Firefox Relay schreibt dagegen die eigene
    /// Tarnadresse als Absender - alle Mails darüber haben dieselbe, „schon mal geschrieben“ sagt darum nichts.</param>
    public sealed record Info(string Service, string Original, string Domain, bool PerSender = true);

    /// <summary>Gehört die Domain (registrierbar) zu einem Alias-Dienst?</summary>
    public static string? ServiceOf(string? registrable) => registrable != null && Services.TryGetValue(registrable, out var s) ? s : null;

    /// <summary>Eigentlichen Absender aus Antwortadresse oder Namen ablesen; null, wenn die Adresse nicht von einem Alias-Dienst stammt.</summary>
    public static Info? Decode(string? address, string? name)
    {
        address = address?.Trim().ToLowerInvariant() ?? "";
        var at = address.LastIndexOf('@');
        if (at <= 0 || PhishCheck.Ascii(address[(at + 1)..]) is not { } host || ServiceOf(PhishCheck.Registrable(host)) is not { } service) return null;
        var local = address[..at];
        string? original = null;
        // Firefox Relay: „info@shop.de [via Relay]“ <eigene-maske@mozmail.com>
        if (service == "Firefox Relay")
        {
            if (name == null || RelayName().Match(name) is not { Success: true } r) return null;
            original = r.Groups[1].Value.ToLowerInvariant();
            if (!Plausible(original)) return null;
            var d = PhishCheck.Ascii(original[(original.LastIndexOf('@') + 1)..])!;
            return ServiceOf(PhishCheck.Registrable(d)) != null ? null : new Info(service, original, d, PerSender: false);
        }
        // addy.io: alias+absender=domain.de@addy.io
        if (service == "addy.io" && local.IndexOf('+') is var plus and > 0 && local.LastIndexOf('=') is var eq && eq > plus)
            original = local[(plus + 1)..eq] + "@" + local[(eq + 1)..];
        // SimpleLogin, Proton Pass, DuckDuckGo: absender_at_domain_de_zufall - die Domain mit „_“ statt „.“ (Domains haben kein „_“)
        else if (local.LastIndexOf("_at_", StringComparison.Ordinal) is var i and > 0)
        {
            var rest = local[(i + 4)..];
            var cut = rest.LastIndexOf('_');
            if (cut > 0) original = local[..i] + "@" + rest[..cut].Replace('_', '.');
        }
        // Sonst steht er oft im Namen: „Amazon.de - shipment-tracking at amazon.de“
        if ((original == null || !Plausible(original)) && name != null && NameAddress().Match(name) is { Success: true } n)
            original = n.Groups[1].Value.ToLowerInvariant() + "@" + n.Groups[2].Value.ToLowerInvariant();
        if (original == null || !Plausible(original)) return null;
        var domain = PhishCheck.Ascii(original[(original.LastIndexOf('@') + 1)..])!;
        // Ein Alias-Dienst, der an einen Alias-Dienst weiterleitet, verrät nichts
        if (ServiceOf(PhishCheck.Registrable(domain)) != null) return null;
        return new Info(service, original, domain);
    }

    private static bool Plausible(string address) =>
        address.LastIndexOf('@') is var at and > 0 && PhishCheck.Ascii(address[(at + 1)..]) is { } d && DomainRx().IsMatch(d);

    [GeneratedRegex(@"^\s*""?([\w.+-]+@[\w-]+(?:\.[\w-]+)+)""?\s*\[via Relay\]", RegexOptions.IgnoreCase)]
    private static partial Regex RelayName();

    [GeneratedRegex(@"([\w.+-]+) at ([\w-]+(?:\.[\w-]+)+)\s*$")]
    private static partial Regex NameAddress();

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z][a-z0-9-]+$")]
    private static partial Regex DomainRx();
}
