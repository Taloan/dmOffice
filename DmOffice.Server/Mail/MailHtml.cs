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
    /// <param name="Trackers">Entfernte Zählpixel (nur mit Tracker-Schutz, sonst 0).</param>
    /// <param name="CleanedLinks">Links, aus denen Angaben zur Nachverfolgung entfernt wurden.</param>
    public sealed record Result(string Html, bool HasRemoteImages, int Trackers = 0, int CleanedLinks = 0);

    /// <param name="junk">Junk: auch eingebettete Bilder (cid:, data:) sperren und Links nicht anklickbar machen -
    /// wie in Outlook ohne Ausnahme; freigegeben wird erst, wenn die Mail als "Kein Junk" im Posteingang liegt.</param>
    /// <param name="trackers">Tracker-Schutz (<see cref="MailTrackers"/>): Zählpixel entfernen, Links bereinigen.</param>
    public static Result Sanitize(string html, bool allowRemote, Func<string, string?> cidUrl, bool junk = false, bool trackers = false)
    {
        bool lockAll = junk;
        int pixels = 0, links = 0;
        if (junk) allowRemote = false;
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
        // Keine Formulare: absenden laesst sich zwar nirgends (Rahmen ohne allow-forms, App-WebView), aber ein
        // echt aussehendes Anmeldeformular mit Passwortfeld taugt als Phishing-Kulisse. Beschriftungen
        // (Knopftext) bleiben als Text, die Eingabefelder selbst fallen weg.
        foreach (var tag in new[] { "form", "input", "button", "select", "option", "optgroup", "textarea", "datalist", "output", "keygen" })
            sanitizer.AllowedTags.Remove(tag);
        // Inhalt von Skripten (auch JSON-LD aus Buchungsmails), Titel und Vorlagen ist kein
        // lesbarer Text - diese Elemente samt Inhalt entfernen statt nur das Tag. Ebenso Auswahl- und
        // Textfelder (ihr Inhalt waere nur vorbelegter Formulartext).
        sanitizer.RemovingTag += (_, e) =>
        {
            if (e.Tag.LocalName is "script" or "style" or "title" or "template" or "xml" or "head"
                or "select" or "textarea" or "datalist")
            {
                e.Tag.Remove();
                e.Cancel = true;
            }
        };

        // cid: vor dem Bereinigen auf die eigene Adresse umschreiben - die
        // Bereinigung kennt das Schema nicht und wuerde die Bilder entfernen.
        // Die so erzeugten Adressen sind die einzigen eigenen, die ein Bild laden darf.
        var own = new HashSet<string>(StringComparer.Ordinal);
        html = CidRx().Replace(html, m =>
        {
            var url = cidUrl(m.Groups[2].Value);
            if (url == null) return m.Value;
            own.Add(url);
            return m.Groups[1].Value + url.Replace("&", "&amp;");
        });

        // Adresse in url(...) eines Stils: eingebettet bleibt, aus dem Netz nur wenn erlaubt, alles andere
        // (eigene Server-Adressen, fremde Schemata) lädt nichts.
        string Css(string css) => CssUrlRx().Replace(css, m =>
        {
            var target = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            switch (Kind(target, own))
            {
                case Source.Embedded: return m.Value;
                case Source.Remote:
                    remote = true;
                    return allowRemote ? m.Value : "none";
                default: return "none";
            }
        });

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement el) return;
            if (el.LocalName == "a")
            {
                el.SetAttribute("target", "_blank");
                el.SetAttribute("rel", "noopener noreferrer");
                // data:-Links sind nur fuer Bilder da - als Link koennten sie eine fremde Seite aufmachen
                if (el.GetAttribute("href") is { } target && target.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    el.RemoveAttribute("href");
                if (trackers && el.GetAttribute("href") is { Length: > 0 } link && MailTrackers.CleanLink(link) is { } cleaned)
                {
                    el.SetAttribute("href", cleaned);
                    links++;
                }
                // Junk: Ziel nur als Hinweis zeigen, nicht klickbar
                if (lockAll && el.GetAttribute("href") is { Length: > 0 } href)
                {
                    el.RemoveAttribute("href");
                    el.SetAttribute("title", href);
                    remote = true;
                }
            }
            else if (el.LocalName == "img")
            {
                // Adresse so lesen, wie der Browser sie liest: Tab, CR und LF fallen überall weg,
                // Steuerzeichen und Leerzeichen am Anfang und Ende ebenso
                var raw = el.GetAttribute("src");
                var src = NormalizeUrl(raw);
                if (raw != null && raw != src) el.SetAttribute("src", src);
                if (lockAll && src.Length > 0)
                {
                    // Junk: kein Bild, auch kein eingebettetes - ein Bild des Absenders ist oft der eigentliche Koeder
                    remote = true;
                    el.RemoveAttribute("src");
                    el.RemoveAttribute("srcset");
                    el.SetAttribute("alt", "[Bild gesperrt]");
                }
                else if (Kind(src, own) == Source.Other)
                {
                    // Eigene Server-Adressen (relativ) und fremde Schemata: kein Bild - sonst ruft die Mail
                    // Adressen dieses Servers mit der Anmeldung des Lesers auf
                    el.RemoveAttribute("src");
                }
                else if (Kind(src, own) == Source.Remote)
                {
                    // Zählpixel: fällt ganz weg, auch wenn entfernte Bilder erlaubt sind (zählt nicht als Bild zum Nachladen)
                    if (trackers && MailTrackers.IsPixel(el, src))
                    {
                        pixels++;
                        el.RemoveAttribute("src");
                        el.RemoveAttribute("srcset");
                        el.SetAttribute("alt", "");
                        el.SetAttribute("style", "display:none");
                        return;
                    }
                    remote = true;
                    if (!allowRemote)
                    {
                        el.SetAttribute("data-remote-src", src);
                        el.RemoveAttribute("src");
                        el.RemoveAttribute("srcset");
                    }
                }
            }
            // Bildauswahl je Auflösung: lässt sich nicht einzeln prüfen
            el.RemoveAttribute("srcset");
            if (el.GetAttribute("style") is { } style)
            {
                var cleaned = Css(style);
                if (cleaned != style) el.SetAttribute("style", cleaned);
            }
            if (el.GetAttribute("background") is { Length: > 0 } background)
            {
                var kind = Kind(background, own);
                if (kind == Source.Remote) remote = true;
                if (kind == Source.Other || (kind == Source.Remote && !allowRemote)) el.RemoveAttribute("background");
            }
        };

        var clean = sanitizer.Sanitize(html);
        // url(...) in <style>-Bloecken
        clean = StyleBlockRx().Replace(clean, m => m.Groups[1].Value + Css(m.Groups[2].Value) + m.Groups[3].Value);
        return new Result(clean, remote, pixels, links);
    }

    private enum Source { Embedded, Remote, Other }

    /// <summary>Woher ein Bild käme: eingebettet (data:, Teil der Mail), aus dem Netz, oder anders (relativ = dieser Server, fremdes Schema).</summary>
    private static Source Kind(string? url, HashSet<string> own)
    {
        var s = NormalizeUrl(url);
        if (s.Length == 0 || own.Contains(s) || s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return Source.Embedded;
        // Der Browser liest \ wie / ; "https:/host" und "https:host" führen ebenfalls ins Netz
        var n = s.Replace('\\', '/');
        if (n.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || n.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || n.StartsWith("//"))
            return Source.Remote;
        return Source.Other;
    }

    /// <summary>URL wie im Browser vor dem Auswerten: Tab, CR, LF entfernt, Steuer- und Leerzeichen an den Rändern abgeschnitten.</summary>
    internal static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var s = url.Replace("\t", "").Replace("\r", "").Replace("\n", "");
        return s.Trim().Trim(Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray());
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

    /// <summary>Klartext für eine ausgehende HTML-Mail: Zeilen mit &lt;br&gt;, Links anklickbar (etwa Bestätigungs- und Statuslinks).</summary>
    public static string TextLines(string text) =>
        LinkRx().Replace(string.Join("<br>", text.Replace("\r", "").Split('\n').Select(System.Net.WebUtility.HtmlEncode)),
            m => $"<a href=\"{m.Value}\">{m.Value}</a>");

    [GeneratedRegex("(src\\s*=\\s*[\"']?)cid:([^\"'\\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidRx();

    [GeneratedRegex("url\\(\\s*(?:\"([^\"]*)\"|'([^']*)'|([^)]*?))\\s*\\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlRx();

    [GeneratedRegex("(<style\\b[^>]*>)(.*?)(</style>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlockRx();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex MultiBlankRx();

    [GeneratedRegex("https?://[^\\s<>\"]+")]
    private static partial Regex LinkRx();
}
