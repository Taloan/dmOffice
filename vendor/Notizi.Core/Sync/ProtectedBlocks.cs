using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Notizi.Sync
{
    /// <summary>
    /// Bloecke, die ein einfacher Editor - die Handy-App - nicht bearbeiten
    /// kann und deshalb unangetastet zurueckgeben muss (docs/konzept-sync.md,
    /// Abschnitt 4, "Bearbeiten auf dem Handy"): Tabellen,
    /// Bilder, Abschnitte. Bearbeitbar sind nur gewoehnliche Absaetze und
    /// Listen.
    ///
    /// Kommt eine Version aus einem solchen Editor, muss jeder geschuetzte
    /// Block der vorigen Version darin unveraendert und in derselben
    /// Reihenfolge wieder vorkommen; ganz fehlen darf nur ein Bild (dort
    /// geloescht). Neu darf nur ein Bild sein. Sonst uebernimmt der Abgleich
    /// sie nicht als Notiz, sondern legt sie als Kopie daneben.
    /// </summary>
    public static class ProtectedBlocks
    {
        /// <summary>Die geschuetzten Bloecke in Reihenfolge, als Text.</summary>
        public static List<string>? Of(string html) => Blocks(html)?.Select(b => Normalized(b)).ToList();

        private static List<XElement>? Blocks(string html)
        {
            XElement root;
            try
            {
                var settings = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore };
                using var reader = XmlReader.Create(new System.IO.StringReader(
                    "<root>" + html.Replace("&nbsp;", "&#160;") + "</root>"), settings);
                root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return null;
            }

            // Inhalt steht in der Huelle &lt;div data-notizi&gt;, sonst direkt.
            var container = root.Elements().Count() == 1
                            && root.Elements().First() is { Name.LocalName: "div" } div
                            && div.Attribute("data-notizi") != null
                ? div : root;

            return container.Elements().Where(IsProtected).ToList();
        }

        /// <summary>
        /// Ob <paramref name="after"/> die geschuetzten Bloecke aus
        /// <paramref name="before"/> unveraendert laesst: Jeder steht in
        /// derselben Reihenfolge wieder da; fehlen darf nur ein Bild, auf dem
        /// Handy geloescht. Neu dazukommen duerfen nur Bilder (eine
        /// Zeichnung, ein eingefuegtes Foto). Eine fehlende oder veraenderte
        /// Tabelle faellt damit auf.
        /// </summary>
        public static bool Kept(string before, string after)
        {
            var oldBlocks = Blocks(before);
            var nowBlocks = Blocks(after);
            if (oldBlocks == null) return true;     // vorher schon unlesbar: nichts zu schuetzen
            if (nowBlocks == null) return false;
            var old = oldBlocks.Select(Normalized).ToList();
            var now = nowBlocks.Select(Normalized).ToList();

            // Alte der Reihe nach in den neuen suchen; fehlen darf nur ein Bild.
            var matched = new bool[now.Count];
            int j = 0;
            for (int i = 0; i < old.Count; i++)
            {
                int k = j;
                while (k < now.Count && now[k] != old[i]) k++;
                if (k == now.Count)
                {
                    if (!IsImageBlock(oldBlocks[i])) return false;
                    continue;
                }
                matched[k] = true;
                j = k + 1;
            }

            // Neu darf nur ein Bild oder ein Codeblock sein - beides legt die
            // App an, beides nimmt nichts weg.
            for (int k = 0; k < now.Count; k++)
                if (!matched[k] && !IsImageBlock(nowBlocks[k]) && !IsNewCodeBlock(nowBlocks[k])) return false;
            return true;
        }

        /// <summary>Ein Codeblock ohne Bild oder Tabelle darin - so legt ihn die App an.</summary>
        private static bool IsNewCodeBlock(XElement e) =>
            IsCodeParagraph(e) && !e.Descendants().Any(d => d.Name.LocalName is "img" or "table");

        /// <summary>Ein Bildblock: Absatz mit Bild (kein Codeblock) oder die
        /// Bildhuelle &lt;div data-block="image"&gt;.</summary>
        private static bool IsImageBlock(XElement e) =>
            e.Descendants().Any(d => d.Name.LocalName == "img")
            && !e.Descendants().Any(d => d.Name.LocalName is "table" or "pre")
            && ((e.Name.LocalName == "p" && !IsCodeParagraph(e))
                || (e.Name.LocalName == "div" && (string?)e.Attribute("data-block") == "image"));

        /// <summary>
        /// Ob ein einzelner Block (Text der obersten Ebene, wie ihn
        /// NoteMerge.Split liefert) geschuetzt ist. Was sich nicht lesen
        /// laesst, gilt als geschuetzt - im Zweifel nicht anfassen.
        /// </summary>
        public static bool IsProtectedBlock(string block)
        {
            if (!block.TrimStart().StartsWith("<", StringComparison.Ordinal)) return false;   // loser Text
            try
            {
                var settings = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore };
                using var reader = XmlReader.Create(new System.IO.StringReader(block.Replace("&nbsp;", "&#160;")), settings);
                return IsProtected(XElement.Load(reader, LoadOptions.PreserveWhitespace));
            }
            catch (XmlException)
            {
                return true;
            }
        }

        private static bool IsProtected(XElement block)
        {
            switch (block.Name.LocalName.ToLowerInvariant())
            {
                case "ul":
                case "ol":
                    // Eine Liste ist bearbeitbar - es sei denn, in ihr steckt
                    // selbst etwas Geschuetztes.
                    return block.Descendants().Any(e => e.Name.LocalName is "table" or "img"
                                                        || IsCodeParagraph(e));
                case "p":
                    // Codebloecke sind reiner Text - die App bearbeitet sie.
                    return block.Descendants().Any(e => e.Name.LocalName == "img");
                default:
                    return true;
            }
        }

        /// <summary>Codeblock: Absatz in fester Schrittweite (siehe
        /// CodeBlockTools in notizi).</summary>
        private static bool IsCodeParagraph(XElement e)
        {
            if (e.Name.LocalName != "p") return false;
            var style = (string?)e.Attribute("style") ?? "";
            return style.Contains("font-family:Consolas", StringComparison.OrdinalIgnoreCase)
                || style.Contains("font-family:Courier", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Der Block als Text, ohne die Bildadressen: Ein anderer Rechner
        /// kodiert dasselbe Bild womoeglich in andere Bytes und damit unter
        /// anderem Namen - das waere kein Verlust. Ein einfacher Editor kann
        /// Bilder ohnehin nicht austauschen, nur verlieren, und das zeigt
        /// sich am fehlenden img.
        /// </summary>
        private static string Normalized(XElement block)
        {
            var copy = new XElement(block);
            foreach (var img in copy.DescendantsAndSelf().Where(e => e.Name.LocalName == "img"))
                img.Attribute("src")?.Remove();
            return copy.ToString(SaveOptions.DisableFormatting);
        }
    }
}
