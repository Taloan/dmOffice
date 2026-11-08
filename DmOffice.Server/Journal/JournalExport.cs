using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DmOffice.Dav;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace DmOffice.Journal;

/// <summary>
/// Tagebuch exportieren (KOORDINATION Auftrag 6): ZIP mit je Eintrag einer Markdown-Datei (yyyy/MM/yyyy-MM-dd.md, bei mehreren
/// Einträgen am Tag mit Uhrzeit) samt Kopf (Datum, Uhrzeit, Titel, Ort, Wetter, Stimmung, Schlagwörter), den Fotos und Videos
/// im Original daneben (yyyy/MM/media/…) und einer tagebuch.json mit allem - die liest der Import wieder ein, auch in ein
/// anderes dmOffice. Gestreamt wie der ZIP-Download der Dateiablage.
/// </summary>
public static class JournalExport
{
    public const string Format = "dmoffice-journal";
    public const string JsonName = "tagebuch.json";

    public sealed record MediaOut(string? File, string Name, string Mime, long? Size, int? Width, int? Height, DateTime? Taken);
    public sealed record EntryOut(string Uri, DateTime Date, string? Title, string Html, string Text, int? Mood, List<string> Tags, JournalPlace? Place,
        bool Private, string? Weather, bool ShowEvents, DateTime? Created, DateTime? Modified, string? Import, List<MediaOut> Media, List<JournalRef> Refs);
    public sealed record JournalOut(string Name, string? Color);
    public sealed record Document(string Format, int Version, DateTime Exported, JournalOut Journal, List<EntryOut> Entries);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Einträge im Zeitraum (Tage in der Zeitzone des Benutzers, beide einschließlich), älteste zuerst.</summary>
    public static List<JournalEntry> Select(IEnumerable<JournalEntry> entries, TimeZoneInfo zone, DateOnly? from, DateOnly? to) =>
        entries.Where(e =>
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.Date, DateTimeKind.Utc), zone));
            return (from == null || day >= from) && (to == null || day <= to);
        }).OrderBy(e => e.Date).ToList();

    /// <summary>Die ZIP in die Antwort schreiben. media=false: ohne Fotos und Videos (nur Text und tagebuch.json).</summary>
    internal static async Task Write(HttpContext http, JournalApi.Scope scope, TimeZoneInfo zone, List<JournalEntry> entries, bool media)
    {
        var files = scope.Files;
        var fileName = Safe(scope.Coll.Name) + " " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".zip";
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (http.Features.Get<IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        await http.Response.StartAsync();
        await using var buffered = new BufferedStream(http.Response.Body, 256 * 1024);
        using (var zip = new ZipArchive(buffered, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string Unique(string path)
            {
                var dir = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
                var name = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                var p = path;
                for (int i = 2; !used.Add(p); i++) p = dir + name + "-" + i + ext;
                return p;
            }
            var local = entries.ToDictionary(e => e, e => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.Date, DateTimeKind.Utc), zone));
            var perDay = entries.GroupBy(e => local[e].Date).ToDictionary(g => g.Key, g => g.Count());
            var doc = new List<EntryOut>();
            foreach (var e in entries)
            {
                http.RequestAborted.ThrowIfCancellationRequested();
                var at = local[e];
                var folder = at.ToString("yyyy/MM", CultureInfo.InvariantCulture);
                var stem = at.ToString(perDay[at.Date] > 1 ? "yyyy-MM-dd_HHmm" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var mediaOut = new List<MediaOut>();
                foreach (var m in e.Media)
                {
                    string? file = null;
                    var name = Path.GetFileName(m.Path);
                    if (media && files.Info(m.Path) is { } f)
                    {
                        file = Unique(folder + "/media/" + stem + " " + Safe(name));
                        var entry = zip.CreateEntry(file, Zip.Packed.Contains(Path.GetExtension(name).ToLowerInvariant()) ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                        entry.LastWriteTime = f.Modified.Year < 1980 ? DateTime.Now : f.Modified.ToLocalTime();
                        using var target = entry.Open();
                        using var source = files.OpenRead(m.Path);
                        source.CopyTo(target, 256 * 1024);
                    }
                    mediaOut.Add(new MediaOut(file, name, m.Mime, m.Size, m.Width, m.Height, m.Taken));
                }
                var md = Unique(folder + "/" + stem + ".md");
                var mdEntry = zip.CreateEntry(md, CompressionLevel.Optimal);
                mdEntry.LastWriteTime = at.Year < 1980 ? DateTime.Now : at;
                using (var w = new StreamWriter(mdEntry.Open(), new UTF8Encoding(false)))
                    w.Write(Markdown(e, at, mediaOut, folder));
                doc.Add(new EntryOut(e.Uri, e.Date, e.Title, e.Html, e.Text, e.Mood, e.Tags, e.Place, e.Private, e.Weather, e.ShowEvents,
                    e.Created, e.Modified, e.Import, mediaOut, e.Refs));
            }
            var json = zip.CreateEntry(JsonName, CompressionLevel.Optimal);
            using (var s = json.Open())
                JsonSerializer.Serialize(s, new Document(Format, 1, DateTime.UtcNow, new JournalOut(scope.Coll.Name, scope.Coll.Color), doc), Json);
        }
        await buffered.FlushAsync();
    }

    /// <summary>Ein Eintrag als Markdown mit Kopf (YAML wie bei Obsidian und anderen Markdown-Programmen).</summary>
    public static string Markdown(JournalEntry e, DateTime local, List<MediaOut> media, string folder)
    {
        static string Q(string s) => JsonSerializer.Serialize(s, Json);
        var sb = new StringBuilder("---\n");
        sb.Append("date: ").Append(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("time: \"").Append(local.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("\"\n");
        if (!string.IsNullOrWhiteSpace(e.Title)) sb.Append("title: ").Append(Q(e.Title)).Append('\n');
        if (e.Place is { } p)
        {
            if (!string.IsNullOrWhiteSpace(p.Name)) sb.Append("place: ").Append(Q(p.Name)).Append('\n');
            if (p.Lat is double lat && p.Lon is double lon)
                sb.Append("location: [").Append(lat.ToString("0.######", CultureInfo.InvariantCulture)).Append(", ").Append(lon.ToString("0.######", CultureInfo.InvariantCulture)).Append("]\n");
        }
        if (!string.IsNullOrWhiteSpace(e.Weather)) sb.Append("weather: ").Append(Q(e.Weather)).Append('\n');
        if (e.Mood is int mood) sb.Append("mood: ").Append(mood).Append('\n');
        if (e.Tags.Count > 0) sb.Append("tags: [").Append(string.Join(", ", e.Tags.Select(Q))).Append("]\n");
        if (e.Private) sb.Append("private: true\n");
        sb.Append("---\n\n");
        if (!string.IsNullOrWhiteSpace(e.Title)) sb.Append("# ").Append(e.Title.Trim()).Append("\n\n");
        var text = HtmlToMarkdown(e.Html);
        if (text.Length > 0) sb.Append(text).Append("\n\n");
        foreach (var m in media.Where(m => m.File != null))
        {
            var rel = m.File![(folder.Length + 1)..];
            sb.Append(m.Mime.StartsWith("image/", StringComparison.Ordinal) ? "!" : "").Append('[').Append(EscapeText(m.Name)).Append("](<").Append(rel).Append(">)\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------------- HTML aus dem Editor -> Markdown ----------------

    public static string HtmlToMarkdown(string html)
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

    /// <summary>Datei- und Ordnernamen ohne Zeichen, die in ZIP oder auf Windows stören.</summary>
    private static string Safe(string name)
    {
        var s = Regex.Replace(name, @"[\\/:*?""<>|\x00-\x1f]", "_").Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s.Length > 120 ? s[..120] : s;
    }
}
