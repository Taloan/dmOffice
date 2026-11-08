using System.Net;
using System.Text.RegularExpressions;

namespace DmOffice.Journal;

/// <summary>
/// Text eines Tagebucheintrags: HTML aus dem Editor (Überschriften, fett/kursiv/unterstrichen/durchgestrichen, Farbe, Listen,
/// Checklisten als &lt;ul data-check&gt; mit &lt;li data-done="1"&gt;, Zitat, Links) - bereinigt; dazu reiner Text für andere
/// Programme und die Suche.
/// </summary>
public static class JournalHtml
{
    private static readonly string[] Tags =
        ["p", "br", "div", "span", "b", "strong", "i", "em", "u", "s", "strike", "del", "h1", "h2", "h3", "ul", "ol", "li", "blockquote", "a", "font", "mark", "hr"];

    public static string Clean(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var sanitizer = new Ganss.Xss.HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var t in Tags) sanitizer.AllowedTags.Add(t);
        sanitizer.AllowedAttributes.Clear();
        foreach (var a in new[] { "href", "data-check", "data-done", "color", "style" }) sanitizer.AllowedAttributes.Add(a);
        sanitizer.AllowedCssProperties.Clear();
        foreach (var c in new[] { "color", "background-color", "text-align", "font-weight", "font-style", "text-decoration" }) sanitizer.AllowedCssProperties.Add(c);
        sanitizer.AllowedSchemes.Clear();
        foreach (var s in new[] { "http", "https", "mailto" }) sanitizer.AllowedSchemes.Add(s);
        var clean = sanitizer.Sanitize(html).Trim();
        // Leer gelassener Editor
        return Regex.IsMatch(clean, @"^(?:<(?:p|div)>(?:\s|<br>)*</(?:p|div)>|<br>|\s)*$") ? "" : clean;
    }

    /// <summary>Reiner Text: Absätze und Listenpunkte als Zeilen, Checklisten mit ☐/☑.</summary>
    public static string Plain(string html)
    {
        if (html.Length == 0) return "";
        var t = Regex.Replace(html, @"<li\b[^>]*data-done=""1""[^>]*>", "\n☑ ", RegexOptions.IgnoreCase);
        // Punkte einer Checkliste ohne Haken
        t = Regex.Replace(t, @"<ul\b[^>]*data-check[^>]*>(.*?)</ul>", m => Regex.Replace(m.Value, @"<li\b(?![^>]*data-done=""1"")[^>]*>", "\n☐ "),
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        t = Regex.Replace(t, @"<li\b[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<br\s*/?>|</(?:p|div|h1|h2|h3|blockquote|ul|ol)>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "<[^>]+>", "");
        t = WebUtility.HtmlDecode(t).Replace(' ', ' ');
        t = Regex.Replace(t, @"[ \t]+\n", "\n");
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        // Listenpunkte direkt unter dem Absatz davor, ohne Leerzeile
        t = Regex.Replace(t, @"\n{2,}(?=[☑☐•] )", "\n");
        return t.Trim();
    }
}
