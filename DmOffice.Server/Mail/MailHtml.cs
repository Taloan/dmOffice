using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace DmOffice.Mail;

/// <summary>
/// HTML einer Mail fuer die Anzeige: ohne Skripte und Formulare, Links in
/// neuem Fenster, entfernte Bilder auf Wunsch gesperrt (Zaehlpixel). Die
/// Oberflaeche zeigt das Ergebnis zusaetzlich in einem iframe ohne Skripte.
/// </summary>
public static partial class MailHtml
{
    public sealed record Result(string Html, bool HasRemoteImages);

    public static Result Sanitize(string html, bool allowRemote, Func<string, string?> cidUrl)
    {
        bool remote = false;
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Add("style");
        sanitizer.AllowedTags.Add("center");
        sanitizer.AllowedTags.Add("font");
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("bgcolor");
        sanitizer.AllowedAttributes.Add("color");
        sanitizer.AllowedAttributes.Add("face");
        sanitizer.AllowedAttributes.Add("data-remote-src");
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("tel");
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        sanitizer.AllowDataAttributes = false;
        sanitizer.KeepChildNodes = true;
        // Inhalt von Skripten (auch JSON-LD aus Buchungsmails), Titel und Vorlagen ist kein
        // lesbarer Text - diese Elemente samt Inhalt entfernen statt nur das Tag.
        sanitizer.RemovingTag += (_, e) =>
        {
            if (e.Tag.LocalName is "script" or "style" or "title" or "template" or "xml" or "head")
            {
                e.Tag.Remove();
                e.Cancel = true;
            }
        };

        // cid: vor dem Bereinigen auf die eigene Adresse umschreiben - die
        // Bereinigung kennt das Schema nicht und wuerde die Bilder entfernen.
        html = CidRx().Replace(html, m =>
        {
            var url = cidUrl(m.Groups[2].Value);
            return url == null ? m.Value : m.Groups[1].Value + url;
        });

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement el) return;
            if (el.LocalName == "a")
            {
                el.SetAttribute("target", "_blank");
                el.SetAttribute("rel", "noopener noreferrer");
            }
            else if (el.LocalName == "img")
            {
                var src = el.GetAttribute("src") ?? "";
                if (src.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                    src.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("//"))
                {
                    remote = true;
                    if (!allowRemote)
                    {
                        el.SetAttribute("data-remote-src", src);
                        el.RemoveAttribute("src");
                        el.RemoveAttribute("srcset");
                    }
                }
            }
            if (!allowRemote && el.GetAttribute("style") is { } style && RemoteUrlRx().IsMatch(style))
            {
                remote = true;
                el.SetAttribute("style", RemoteUrlRx().Replace(style, "none"));
            }
            if (!allowRemote && el.GetAttribute("background") is { Length: > 0 })
            {
                remote = true;
                el.RemoveAttribute("background");
            }
        };

        var clean = sanitizer.Sanitize(html);
        if (!allowRemote && RemoteUrlRx().IsMatch(clean))
        {
            // url(...) in <style>-Bloecken
            remote = true;
            clean = RemoteUrlRx().Replace(clean, "none");
        }
        return new Result(clean, remote);
    }

    /// <summary>Text aus HTML - fuer die Textfassung einer gesendeten Mail.</summary>
    public static string ToText(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        foreach (var br in doc.QuerySelectorAll("br").ToList()) br.Replace(doc.CreateTextNode("\n"));
        foreach (var block in doc.QuerySelectorAll("p,div,li,tr,h1,h2,h3,h4,blockquote").ToList())
            block.Append(doc.CreateTextNode("\n"));
        var text = doc.Body?.TextContent ?? "";
        return MultiBlankRx().Replace(text.Replace("\r", ""), "\n\n").Trim();
    }

    /// <summary>Klartext als HTML, Links anklickbar.</summary>
    public static string FromText(string text)
    {
        var enc = System.Net.WebUtility.HtmlEncode(text);
        enc = LinkRx().Replace(enc, m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        return "<div style=\"white-space:pre-wrap;font-family:inherit\">" + enc + "</div>";
    }

    [GeneratedRegex("(src\\s*=\\s*[\"']?)cid:([^\"'\\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidRx();

    [GeneratedRegex("url\\(\\s*['\"]?(https?:)?//[^)]*\\)", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteUrlRx();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex MultiBlankRx();

    [GeneratedRegex("https?://[^\\s<>\"]+")]
    private static partial Regex LinkRx();
}
