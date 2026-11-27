using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace DmOffice.Mail;

/// <summary>
/// Tracker-Schutz in der Lese-Ansicht (Schalter mailtrackers): Zählpixel - Bilder, die nur melden, dass und wann eine Mail geöffnet
/// wurde - fallen weg, auch wenn entfernte Bilder erlaubt sind; aus Links fallen Angaben zur Nachverfolgung (utm_*, fbclid, gclid …).
/// Erkannt wird nach bekannten Mustern (Größe 1×1 oder unsichtbar, typische Pfade der Versanddienste); Weiterleitungs-Links der
/// Versanddienste (click.…) lassen sich so nicht bereinigen, ohne sie aufzurufen - die bleiben.
/// </summary>
public static partial class MailTrackers
{
    /// <summary>Angaben zur Nachverfolgung in Links (Name exakt oder mit Präfix).</summary>
    private static readonly HashSet<string> Params = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "gclsrc", "dclid", "gbraid", "wbraid", "msclkid", "yclid", "twclid", "ttclid", "igshid", "li_fat_id",
        "mc_cid", "mc_eid", "_hsenc", "_hsmi", "__hssc", "__hstc", "__hsfp", "hsctatracking", "mkt_tok", "oly_anon_id", "oly_enc_id",
        "vero_conv", "vero_id", "wickedid", "rb_clickid", "s_cid", "ml_subscriber", "ml_subscriber_hash", "sc_cid", "ecid", "_bta_tid", "_bta_c",
        "trk", "trkcampaign", "spm", "srsltid", "epik", "pk_campaign", "pk_kwd", "pk_source", "pk_medium", "mtm_campaign", "mtm_source", "mtm_medium",
    };

    private static readonly string[] ParamPrefixes = ["utm_", "pk_", "mtm_", "hsa_", "vero_"];

    /// <summary>Ist die Angabe eine zur Nachverfolgung?</summary>
    public static bool IsTrackingParam(string name) =>
        Params.Contains(name) || ParamPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Link ohne Angaben zur Nachverfolgung; null, wenn nichts zu entfernen war oder es kein http(s)-Link ist.</summary>
    public static string? CleanLink(string href)
    {
        var h = href.Trim();
        if (!h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        var q = h.IndexOf('?');
        if (q < 0) return null;
        var hash = h.IndexOf('#', q);
        var query = hash < 0 ? h[(q + 1)..] : h[(q + 1)..hash];
        var parts = query.Split('&');
        var keep = parts.Where(p => p.Length > 0 && !IsTrackingParam(Uri.UnescapeDataString(p.Split('=')[0]))).ToList();
        if (keep.Count == parts.Count(p => p.Length > 0)) return null;
        return h[..q] + (keep.Count > 0 ? "?" + string.Join("&", keep) : "") + (hash < 0 ? "" : h[hash..]);
    }

    /// <summary>Ist das entfernte Bild ein Zählpixel?</summary>
    public static bool IsPixel(IElement img, string src)
    {
        // Sehr große Angaben gelten als groß (kein Zählpixel)
        static int? Px(string? v) => v != null && SizeRx().Match(v) is { Success: true } m
            ? (int.TryParse(m.Groups[1].Value, out var n) ? n : int.MaxValue) : null;
        var style = (img.GetAttribute("style") ?? "").ToLowerInvariant();
        var w = Px(img.GetAttribute("width")) ?? (StyleSize("width", style) is { } sw ? Px(sw) : null);
        var h = Px(img.GetAttribute("height")) ?? (StyleSize("height", style) is { } sh ? Px(sh) : null);
        if (w is <= 2 && h is <= 2) return true;
        if ((w is 0 || h is 0) || style.Replace(" ", "").Contains("display:none") || style.Replace(" ", "").Contains("visibility:hidden")) return true;
        return PathRx().IsMatch(src);
    }

    private static string? StyleSize(string prop, string style) =>
        Regex.Match(style, @"(?:^|;|\s)" + prop + @"\s*:\s*([0-9.]+)px", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^\s*([0-9]+)(?:\.[0-9]+)?\s*(?:px)?\s*$")]
    private static partial Regex SizeRx();

    /// <summary>Pfade, unter denen Versanddienste Öffnungen zählen (Mailchimp, SendGrid, Brevo, HubSpot, Mailjet, Salesforce, CleverReach …).</summary>
    [GeneratedRegex(@"(/track/open|/open\.php|/open\.aspx|/wf/open|/tr/op/|/trk/open|/email/open|/mail/open|/openrate|/beacon|/pixel(\.gif|\.png|/|\?|$)|/o\.gif|/t\.gif|/imp\?|/emimp/|/ss/o/)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PathRx();
}
