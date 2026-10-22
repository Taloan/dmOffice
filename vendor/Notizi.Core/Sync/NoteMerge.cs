using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Notizi.Sync
{
    /// <summary>
    /// Dreiseitiges Zusammenfuehren: Ausgangsstand (zuletzt abgeglichen),
    /// die hiesige und die fremde Version. Was nur eine Seite geaendert hat,
    /// gilt; was beide gleich geaendert haben, auch. Wie bei OneNote gibt es
    /// keine zweite Notiz: Was beide an derselben Stelle verschieden
    /// geaendert haben, steht in beiden Versionen untereinander.
    ///
    ///     Felder (Ordner, Archiv, Farbe …)  einzeln; beide verschieden:
    ///                                       die spaetere Version
    ///     Text                              Block fuer Block (Absatz,
    ///                                       Liste, Tabelle), verglichen
    ///                                       ohne Formatierungs-Rauschen;
    ///                                       beide am selben Absatz: Wort
    ///                                       fuer Wort; ueberschneidet es
    ///                                       sich: beide Versionen, die von
    ///                                       dort mit Hinweis
    ///     Anhaenge, Quellen                 Hinzufuegen und Entfernen
    ///                                       beider Seiten
    /// </summary>
    public static class NoteMerge
    {
        public static NoteItem? Merge(NoteItem origin, NoteItem mine, NoteItem theirs) =>
            Merge(origin, mine, theirs, out _);

        /// <summary>Null nur, wenn sich das HTML nicht zerlegen laesst - dann
        /// bleibt es bei Notiz und Konfliktkopie. <paramref name="marked"/>:
        /// Im Text stehen zwei Versionen untereinander.</summary>
        public static NoteItem? Merge(NoteItem origin, NoteItem mine, NoteItem theirs, out bool marked)
        {
            bool theirsLater = theirs.Modified >= mine.Modified;
            T Pick<T>(Func<NoteItem, T> field) => Field(field(origin), field(mine), field(theirs), theirsLater);

            var html = MergeHtml(origin.Html, mine.Html, theirs.Html,
                string.IsNullOrEmpty(theirs.Device) ? "anderem Geraet" : theirs.Device, out marked);
            if (html == null) return null;

            var merged = (NoteItem)SyncItem.FromBytes(theirs.ToBytes());
            merged.Html = html;
            merged.Plain = html == theirs.Html ? theirs.Plain : html == mine.Html ? mine.Plain : "";
            merged.Title = html == mine.Html && html != theirs.Html ? mine.Title : theirs.Title;
            merged.Folder = Pick(n => n.Folder);
            merged.SortOrder = Pick(n => n.SortOrder);
            merged.Deleted = Pick(n => n.Deleted);
            merged.Archived = Pick(n => n.Archived);
            merged.CustomTitle = Pick(n => n.CustomTitle);
            merged.Color = Pick(n => n.Color);
            merged.Important = Pick(n => n.Important);
            merged.Alarm = Pick(n => n.Alarm);
            merged.AlarmActive = Pick(n => n.AlarmActive);
            merged.Images = mine.Images.Union(theirs.Images).Where(i => html.Contains("blob:" + i)).ToList();
            merged.Attachments = MergeSet(origin.Attachments, mine.Attachments, theirs.Attachments, a => a.Sha256 + "/" + a.Name);
            merged.Sources = MergeSet(origin.Sources, mine.Sources, theirs.Sources, s => s.Url + "/" + s.Title + "/" + s.App);
            if (mine.Tags != null || theirs.Tags != null)
                merged.Tags = MergeSet(origin.Tags ?? new List<string>(), mine.Tags ?? new List<string>(), theirs.Tags ?? new List<string>(),
                                       t => t.ToLowerInvariant());
            merged.Rev = Math.Max(mine.Rev, theirs.Rev) + 1;
            merged.Modified = theirsLater ? theirs.Modified : mine.Modified;
            merged.Editor = null;
            return merged;
        }

        public static FolderItem MergeFolder(FolderItem origin, FolderItem mine, FolderItem theirs)
        {
            bool theirsLater = theirs.Modified >= mine.Modified;
            var merged = (FolderItem)SyncItem.FromBytes(theirs.ToBytes());
            merged.Name = Field(origin.Name, mine.Name, theirs.Name, theirsLater);
            merged.Parent = Field(origin.Parent, mine.Parent, theirs.Parent, theirsLater);
            merged.SortOrder = Field(origin.SortOrder, mine.SortOrder, theirs.SortOrder, theirsLater);
            merged.Rev = Math.Max(mine.Rev, theirs.Rev) + 1;
            merged.Modified = theirsLater ? theirs.Modified : mine.Modified;
            return merged;
        }

        private static T Field<T>(T origin, T mine, T theirs, bool theirsLater)
        {
            var eq = EqualityComparer<T>.Default;
            if (eq.Equals(mine, origin)) return theirs;
            if (eq.Equals(theirs, origin)) return mine;
            if (eq.Equals(mine, theirs)) return mine;
            return theirsLater ? theirs : mine;
        }

        private static List<T> MergeSet<T>(List<T> origin, List<T> mine, List<T> theirs, Func<T, string> key)
        {
            var o = origin.Select(key).ToHashSet();
            var m = mine.Select(key).ToHashSet();
            var t = theirs.Select(key).ToHashSet();
            var result = new List<T>();
            var seen = new HashSet<string>();
            foreach (var item in mine.Concat(theirs))
            {
                var k = key(item);
                if (!seen.Add(k)) continue;
                bool removedHere = o.Contains(k) && !m.Contains(k);
                bool removedThere = o.Contains(k) && !t.Contains(k);
                if (!removedHere && !removedThere) result.Add(item);
            }
            return result;
        }

        // ==================================================================
        //  Text
        // ==================================================================

        /// <summary>Wie MergeHtml mit Hinweis - Kurzform fuer Pruefungen.</summary>
        public static string? MergeHtml(string origin, string mine, string theirs) =>
            MergeHtml(origin, mine, theirs, "anderem Geraet", out _);

        /// <summary>
        /// Text dreiseitig zusammenfuehren. Verglichen wird ohne Formatierungs-
        /// Rauschen (<see cref="Canon"/>); ein Absatz, den beide Seiten
        /// geaendert haben, wird wortweise zusammengefuehrt. Geht auch das
        /// nicht, stehen beide Versionen untereinander, die von dort mit einem
        /// Hinweis davor (<paramref name="marked"/>) - keine zweite Notiz.
        /// Null nur, wenn sich das HTML nicht zerlegen laesst.
        /// </summary>
        /// <param name="theirLabel">Woher die andere Version kommt, fuer den Hinweis.</param>
        public static string? MergeHtml(string origin, string mine, string theirs, string theirLabel, out bool marked)
        {
            marked = false;
            if (mine == theirs || theirs == origin) return mine;
            if (mine == origin) return theirs;

            var o = Split(origin);
            var m = Split(mine);
            var t = Split(theirs);
            if (o == null || m == null || t == null) return null;

            // Kopf (die Huelle mit den Dokumentformaten) wie ein Feld; beide
            // verschieden: der hiesige.
            string head = m.Value.Head == o.Value.Head ? t.Value.Head : m.Value.Head;

            bool any = false;
            var blocks = Diff3(o.Value.Blocks, m.Value.Blocks, t.Value.Blocks, Canon, (co, cm, ct) =>
            {
                var resolved = ResolveBlocks(co, cm, ct, theirLabel, out bool mark);
                any |= mark;
                return resolved;
            })!;
            marked = any;
            return head + string.Concat(blocks) + o.Value.Tail;
        }

        /// <summary>
        /// Eine Stelle, die beide Seiten geaendert haben. Gleich viele Bloecke:
        /// Block fuer Block, wortweise. Hat eine Seite nur eingefuegt, kommt
        /// das Eingefuegte dazu. Sonst die hiesigen Bloecke, danach mit
        /// Hinweis, was nur dort steht - nichts geht verloren.
        /// </summary>
        private static List<string> ResolveBlocks(List<string> co, List<string> cm, List<string> ct, string label, out bool marked)
        {
            marked = false;
            if (co.Count == cm.Count && co.Count == ct.Count)
            {
                var result = new List<string>();
                for (int i = 0; i < co.Count; i++)
                {
                    var (bo, bm, bt) = (co[i], cm[i], ct[i]);
                    if (Canon(bm) == Canon(bo)) result.Add(bt);
                    else if (Canon(bt) == Canon(bo) || Canon(bt) == Canon(bm)) result.Add(bm);
                    else if (MergeWords(bo, bm, bt) is { } words) result.Add(words);
                    else
                    {
                        result.Add(bm);
                        result.Add(Marker(label));
                        result.Add(bt);
                        marked = true;
                    }
                }
                return result;
            }

            // Eine Seite hat nur eingefuegt, die andere nur an Ort und Stelle
            // geaendert: beides.
            if (cm.Count == co.Count && OnlyInserted(co, ct, out var tPos))
                return Interleave(ct, tPos, cm);
            if (ct.Count == co.Count && OnlyInserted(co, cm, out var mPos))
                return Interleave(cm, mPos, ct);

            var known = co.Concat(cm).Select(Canon).ToHashSet();
            var extra = ct.Where(b => !known.Contains(Canon(b))).ToList();
            var rest = new List<string>(cm);
            if (extra.Count > 0)
            {
                rest.Add(Marker(label));
                rest.AddRange(extra);
                marked = true;
            }
            return rest;
        }

        /// <summary>Ob <paramref name="changed"/> die Bloecke von
        /// <paramref name="origin"/> in derselben Reihenfolge enthaelt, nur mit
        /// Eingefuegtem dazwischen. <paramref name="positions"/>: wo.</summary>
        private static bool OnlyInserted(List<string> origin, List<string> changed, out int[] positions)
        {
            positions = new int[origin.Count];
            int j = 0;
            for (int i = 0; i < origin.Count; i++)
            {
                var key = Canon(origin[i]);
                while (j < changed.Count && Canon(changed[j]) != key) j++;
                if (j == changed.Count) return false;
                positions[i] = j++;
            }
            return true;
        }

        /// <summary>Die Version mit Einfuegungen, ihre urspruenglichen Bloecke
        /// ersetzt durch die der anderen Seite (dort an Ort und Stelle geaendert).</summary>
        private static List<string> Interleave(List<string> withInserts, int[] positions, List<string> edited)
        {
            var result = new List<string>(withInserts);
            for (int i = 0; i < positions.Length; i++) result[positions[i]] = edited[i];
            return result;
        }

        /// <summary>Hinweis vor der anderen Version eines Absatzes.</summary>
        private static string Marker(string label) =>
            "<p style=\"color:#C00000\">⚠ Gleichzeitig geändert – Version von " + System.Security.SecurityElement.Escape(label) + ":</p>";

        // ------------------------------------------------------------------
        //  Wortweise
        // ------------------------------------------------------------------

        private static readonly Regex OneElement = new(@"^<(?<tag>[A-Za-z0-9]+)(?<attrs>[^>]*)>(?<inner>.*)</\k<tag>>$", RegexOptions.Singleline);
        private static readonly Regex Token = new(@"<[^>]+>|\s+|[^\s<]+", RegexOptions.Singleline);

        /// <summary>
        /// Einen Absatz, den beide Seiten geaendert haben, Wort fuer Wort
        /// zusammenfuehren - etwa vorn hier, hinten dort. Null, wenn sich die
        /// Aenderungen ueberschneiden oder das Ergebnis kein gueltiger Block ist.
        /// </summary>
        public static string? MergeWords(string origin, string mine, string theirs)
        {
            var o = OneElement.Match(origin);
            var m = OneElement.Match(mine);
            var t = OneElement.Match(theirs);
            if (!o.Success || !m.Success || !t.Success) return null;
            var tag = m.Groups["tag"].Value;
            if (o.Groups["tag"].Value != tag || t.Groups["tag"].Value != tag) return null;

            static List<string> Tokens(Match x) => Token.Matches(x.Groups["inner"].Value).Select(k => k.Value).ToList();
            var merged = Diff3(Tokens(o), Tokens(m), Tokens(t), s => s, null);
            if (merged == null) return null;

            // Die Huelle (Formate des Absatzes) wie ein Feld.
            string Start(Match x) => "<" + tag + x.Groups["attrs"].Value + ">";
            var start = Canon(Start(m) + "</" + tag + ">") == Canon(Start(o) + "</" + tag + ">") ? Start(t) : Start(m);
            var result = start + string.Concat(merged) + "</" + tag + ">";
            return Parse(result) != null ? result : null;
        }

        // ------------------------------------------------------------------
        //  Vergleich ohne Formatierungs-Rauschen
        // ------------------------------------------------------------------

        private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
            { "p", "li", "ul", "ol", "div", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre" };

        /// <summary>
        /// Ein Block zum Vergleichen, ohne Rauschen: Was notizi am PC jedem
        /// Absatz mitgibt (Schrift, Groesse, Farbe, Abstaende, Ausrichtung
        /// links), zaehlt nicht - ein Absatz vom Handy ohne diese Angaben und
        /// derselbe, am PC gespeichert, sind gleich. Echte Auszeichnung zaehlt:
        /// fett, kursiv, Farbe an einzelnen Worten, Code (feste Schrift, grauer
        /// Grund). Was sich nicht lesen laesst, bleibt, wie es ist.
        /// </summary>
        public static string Canon(string block)
        {
            if (_canon.TryGetValue(block, out var cached)) return cached;
            var root = Parse(block);
            string result;
            if (root == null) result = block;
            else
            {
                foreach (var e in root.Descendants().ToList())
                {
                    if (e.Attribute("style") is { } style)
                    {
                        var kept = CleanStyle(style.Value, BlockTags.Contains(e.Name.LocalName));
                        if (kept.Length == 0) style.Remove(); else style.Value = kept;
                    }
                    e.Attribute("data-span")?.Remove();
                    // Attribute in fester Reihenfolge
                    var attrs = e.Attributes().OrderBy(a => a.Name.LocalName, StringComparer.Ordinal).ToList();
                    e.RemoveAttributes();
                    foreach (var a in attrs) e.Add(a);
                }
                // Spannen ohne Formate sind keine.
                foreach (var span in root.Descendants().Where(e => e.Name.LocalName == "span" && !e.HasAttributes).ToList())
                    span.ReplaceWith(span.Nodes());
                foreach (var text in root.DescendantNodes().OfType<XText>())
                    text.Value = text.Value.Replace(' ', ' ');
                result = string.Concat(root.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));
            }
            if (_canon.Count > 5000) _canon.Clear();
            _canon[block] = result;
            return result;
        }

        private static readonly Dictionary<string, string> _canon = new();

        private static string CleanStyle(string style, bool block)
        {
            var kept = new List<string>();
            foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.IndexOf(':');
                if (colon < 0) continue;
                var name = part[..colon].Trim().ToLowerInvariant();
                var value = part[(colon + 1)..].Trim();
                if (block)
                {
                    bool mono = name == "font-family" && (value.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                                                          || value.Contains("Courier", StringComparison.OrdinalIgnoreCase)
                                                          || value.Contains("mono", StringComparison.OrdinalIgnoreCase));
                    if (name is "font-family" && !mono) continue;
                    if (name is "font-size" or "color" or "line-height" or "text-indent") continue;
                    if (name.StartsWith("margin") || name.StartsWith("padding")) continue;
                    if (name == "text-align" && value is "left" or "start" or "justify") continue;
                    if (mono) value = "monospace";
                }
                kept.Add(name + ":" + value.ToLowerInvariant());
            }
            kept.Sort(StringComparer.Ordinal);
            return string.Join(";", kept);
        }

        private static XElement? Parse(string html)
        {
            try
            {
                var settings = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore };
                using var reader = XmlReader.Create(new System.IO.StringReader("<root>" + html.Replace("&nbsp;", "&#160;") + "</root>"), settings);
                return XElement.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return null;
            }
        }

        /// <summary>
        /// Zerlegt das HTML in Huelle und Bloecke der obersten Ebene, als
        /// Ausschnitte des Originaltexts - nichts wird neu geschrieben, so
        /// bleibt ein unveraenderter Block zeichengenau derselbe.
        /// </summary>
        public static (string Head, List<string> Blocks, string Tail)? Split(string html)
        {
            const string open = "<div data-notizi=";
            if (!html.StartsWith(open, StringComparison.Ordinal) || !html.EndsWith("</div>", StringComparison.Ordinal))
                return null;

            int headEnd = html.IndexOf('>') + 1;
            if (headEnd <= 0) return null;
            int bodyEnd = html.Length - "</div>".Length;
            if (html[headEnd - 2] == '/') return (html[..headEnd], new List<string>(), "");   // <div …/>

            var blocks = new List<string>();
            int i = headEnd;
            while (i < bodyEnd)
            {
                int start = i;
                if (html[i] != '<')
                {
                    // Text auf oberster Ebene (fremdes HTML): bis zum naechsten Tag.
                    int next = html.IndexOf('<', i);
                    i = next < 0 || next > bodyEnd ? bodyEnd : next;
                    blocks.Add(html[start..i]);
                    continue;
                }

                int depth = 0;
                do
                {
                    int close = html.IndexOf('>', i);
                    if (close < 0 || close >= bodyEnd + 1) return null;
                    bool end = html[i + 1] == '/';
                    bool self = html[close - 1] == '/';
                    if (end) depth--;
                    else if (!self) depth++;
                    i = close + 1;

                    // Text bis zum naechsten Tag gehoert zum Block.
                    if (depth > 0)
                    {
                        int next = html.IndexOf('<', i);
                        if (next < 0) return null;
                        i = next;
                    }
                } while (depth > 0 && i < bodyEnd);

                if (depth != 0) return null;
                blocks.Add(html[start..i]);
            }

            return (html[..headEnd], blocks, "</div>");
        }

        /// <summary>
        /// Dreiseitiger Vergleich von Blocklisten: Stellen, an denen alle drei
        /// uebereinstimmen, sind fest; dazwischen gilt, was sich geaendert hat.
        /// Haben beide Seiten dieselbe Stelle verschieden geaendert: null.
        /// </summary>
        public static List<string>? Diff3(List<string> origin, List<string> mine, List<string> theirs) =>
            Diff3(origin, mine, theirs, s => s, null);

        /// <summary>
        /// Wie oben, verglichen ueber <paramref name="key"/>. Feste Stellen
        /// kommen in der hiesigen Version heraus. Eine Stelle, die beide Seiten
        /// verschieden geaendert haben, loest <paramref name="conflict"/> -
        /// ohne: null.
        /// </summary>
        private static List<T>? Diff3<T>(List<T> origin, List<T> mine, List<T> theirs, Func<T, string> key,
                                         Func<List<T>, List<T>, List<T>, List<T>?>? conflict)
        {
            var ko = origin.Select(key).ToList();
            var km = mine.Select(key).ToList();
            var kt = theirs.Select(key).ToList();
            var toMine = Match(ko, km);
            var toTheirs = Match(ko, kt);
            var result = new List<T>();

            int o = 0, m = 0, t = 0;
            while (true)
            {
                int n = o;
                while (n < origin.Count && (toMine[n] < 0 || toTheirs[n] < 0)) n++;

                int mEnd = n < origin.Count ? toMine[n] : mine.Count;
                int tEnd = n < origin.Count ? toTheirs[n] : theirs.Count;

                var keyO = ko.GetRange(o, n - o);
                var keyM = km.GetRange(m, mEnd - m);
                var keyT = kt.GetRange(t, tEnd - t);

                if (keyM.SequenceEqual(keyO)) result.AddRange(theirs.GetRange(t, tEnd - t));
                else if (keyT.SequenceEqual(keyO) || keyT.SequenceEqual(keyM)) result.AddRange(mine.GetRange(m, mEnd - m));
                else
                {
                    var solved = conflict?.Invoke(origin.GetRange(o, n - o), mine.GetRange(m, mEnd - m), theirs.GetRange(t, tEnd - t));
                    if (solved == null) return null;
                    result.AddRange(solved);
                }

                if (n >= origin.Count) break;
                result.Add(mine[mEnd]);
                o = n + 1;
                m = mEnd + 1;
                t = tEnd + 1;
            }
            return result;
        }

        /// <summary>Laengste gemeinsame Teilfolge: fuer jeden Block des
        /// Ausgangs seine Stelle in der anderen Version, oder -1.</summary>
        private static int[] Match(List<string> a, List<string> b)
        {
            var lcs = new int[a.Count + 1, b.Count + 1];
            for (int i = a.Count - 1; i >= 0; i--)
                for (int j = b.Count - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            var result = Enumerable.Repeat(-1, a.Count).ToArray();
            int x = 0, y = 0;
            while (x < a.Count && y < b.Count)
            {
                if (a[x] == b[y]) { result[x] = y; x++; y++; }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) x++;
                else y++;
            }
            return result;
        }
    }
}
