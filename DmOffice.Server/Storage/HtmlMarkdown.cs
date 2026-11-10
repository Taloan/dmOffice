using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace DmOffice.Storage;

/// <summary>HTML aus dem Editor (Notizen) als Markdown - für den Datenexport.</summary>
public static class HtmlMarkdown
{
    public static string From(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var doc = new HtmlParser().ParseDocument("<body>" + html + "</body>");
        var sb = new StringBuilder();
        Blocks(doc.Body!, sb, "");
        var md = Regex.Replace(sb.ToString(), @"[ \t]+\n", "\n");
        return Regex.Replace(md, @"\n{3,}", "\n\n").Trim();
    }

    private static readonly HashSet<string> BlockTags = ["P", "DIV", "H1", "H2", "H3", "UL", "OL", "BLOCKQUOTE", "HR", "LI"];

    /// <summary>Kinder eines Blocks: Fließtext sammeln, Blöcke einzeln. prefix: vor jede Zeile (Einrückung, „> “).</summary>
    private static void Blocks(INode parent, StringBuilder sb, string prefix)
    {
        var line = new StringBuilder();
        void Flush()
        {
            var t = line.ToString().Trim();
            if (t.Length > 0) sb.Append(prefix).Append(t.Replace("\n", "\n" + prefix)).Append("\n\n");
            line.Clear();
        }
        foreach (var n in parent.ChildNodes)
        {
            if (n is IElement el && BlockTags.Contains(el.TagName))
            {
                Flush();
                switch (el.TagName)
                {
                    case "H1": case "H2": case "H3":
                        sb.Append(prefix).Append(new string('#', el.TagName[1] - '0')).Append(' ').Append(Inline(el).Trim()).Append("\n\n");
                        break;
                    case "UL": case "OL":
                        List(el, sb, prefix);
                        sb.Append('\n');
                        break;
                    case "BLOCKQUOTE":
                        Blocks(el, sb, prefix + "> ");
                        break;
                    case "HR":
                        sb.Append(prefix).Append("---\n\n");
                        break;
                    default:
                        Blocks(el, sb, prefix);
                        break;
                }
            }
            else line.Append(Inline(n));
        }
        Flush();
    }

    private static void List(IElement list, StringBuilder sb, string prefix)
    {
        var check = list.HasAttribute("data-check");
        var n = 1;
        foreach (var li in list.Children.Where(c => c.TagName == "LI"))
        {
            var mark = check ? (li.GetAttribute("data-done") == "1" ? "- [x] " : "- [ ] ") : list.TagName == "OL" ? n++ + ". " : "- ";
            var text = new StringBuilder();
            var nested = new List<IElement>();
            foreach (var c in li.ChildNodes)
                if (c is IElement e && e.TagName is "UL" or "OL") nested.Add(e);
                else text.Append(c is IElement b && BlockTags.Contains(b.TagName) ? Inline(b) + " " : Inline(c));
            sb.Append(prefix).Append(mark).Append(text.ToString().Trim().Replace("\n", "\n" + prefix + "  ")).Append('\n');
            foreach (var e in nested) List(e, sb, prefix + "  ");
        }
    }

    private static string Inline(INode n)
    {
        if (n is IText t) return EscapeText(Regex.Replace(t.Data.Replace(' ', ' '), @"\s+", " "));
        if (n is not IElement el) return "";
        string Kids() => string.Concat(el.ChildNodes.Select(Inline));
        string Wrap(string mark) { var k = Kids(); return k.Trim().Length == 0 ? k : mark + k.Trim() + mark + (k.EndsWith(' ') ? " " : ""); }
        return el.TagName switch
        {
            "BR" => "\n",
            "B" or "STRONG" => Wrap("**"),
            "I" or "EM" => Wrap("*"),
            "S" or "STRIKE" or "DEL" => Wrap("~~"),
            "U" => "<u>" + Kids() + "</u>",
            "MARK" => Wrap("=="),
            "A" => el.GetAttribute("href") is { Length: > 0 } href ? "[" + Kids().Trim() + "](<" + href + ">)" : Kids(),
            _ => Kids(),
        };
    }

    /// <summary>Zeichen, die Markdown sonst als Formatierung läse.</summary>
    private static string EscapeText(string s) => Regex.Replace(s, @"([\\`*_\[\]<>])", @"\$1");
}
