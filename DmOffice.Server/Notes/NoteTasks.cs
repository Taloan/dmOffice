using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Notizi.Sync;

namespace DmOffice.Notes;

/// <summary>Ein Kaestchen in einer Notiz: Block-Nummer und das wievielte Kaestchen im Block.</summary>
public sealed record NoteTask(long Note, string NoteTitle, string? Folder, int Block, int N, string Text, bool Done, DateTime Modified);

/// <summary>
/// Aufgaben aus notizi: dort gibt es keine eigene Aufgabe, nur ein Kaestchen am Zeilenanfang
/// (☐ offen, ☑ erledigt) in Absatz oder Listenpunkt. Hier gesammelt fuer die Systemliste
/// "Notizi-Aufgaben"; abhaken schreibt das Zeichen in die Notiz zurueck, der Abgleich bringt es zu notizi.
/// </summary>
public static class NoteTasks
{
    public const char Open = '☐', Done = '☑';

    public static List<NoteTask> All(NotesDb db)
    {
        var result = new List<NoteTask>();
        foreach (var note in db.GetAllActive())
        {
            if (note.Html.IndexOf(Open) < 0 && note.Html.IndexOf(Done) < 0) continue;
            if (NoteMerge.Split(note.Html) is not { } split) continue;
            for (int b = 0; b < split.Blocks.Count; b++)
            {
                int n = 0;
                foreach (var pos in Boxes(split.Blocks[b]))
                {
                    var text = TextAfter(split.Blocks[b], pos);
                    if (text.Length > 0)
                        result.Add(new NoteTask(note.Id, note.DisplayTitle, note.Folder, b, n, text, split.Blocks[b][pos] == Done, note.Modified));
                    n++;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Abhaken oder wieder oeffnen. text: wie beim Laden - steht dort inzwischen etwas anderes
    /// (Notiz auf einem anderen Geraet geaendert), wird nichts veraendert.
    /// </summary>
    public static bool Set(NotesDb db, long id, int block, int n, string text, bool done)
    {
        var note = db.GetNote(id) ?? throw new KeyNotFoundException("Notiz nicht gefunden.");
        var split = NoteMerge.Split(note.Html) ?? throw new InvalidOperationException("Diese Notiz lässt sich hier nur lesen.");
        if (block < 0 || block >= split.Blocks.Count) return false;
        var boxes = Boxes(split.Blocks[block]).ToList();
        if (n < 0 || n >= boxes.Count) return false;
        var html = split.Blocks[block];
        var pos = boxes[n];
        if (TextAfter(html, pos) != text) return false;
        if ((html[pos] == Done) == done) return true;

        var sb = new StringBuilder(html) { [pos] = done ? Done : Open };
        split.Blocks[block] = sb.ToString();
        var result = split.Head + string.Concat(split.Blocks) + split.Tail;
        if (!ProtectedBlocks.Kept(note.Html, result)) return false;

        // Im Klartext das gleiche Kaestchen umstellen: das wievielte in der ganzen Notiz
        int global = split.Blocks.Take(block).Sum(x => Boxes(x).Count()) + n;
        db.SaveNote(id, result, Toggle(note.Plain, global, done));
        return true;
    }

    /// <summary>Positionen der Kaestchen im Text (nicht in Tags), jeweils am Anfang einer Zeile.</summary>
    private static IEnumerable<int> Boxes(string html)
    {
        bool inTag = false, lineStart = true;
        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '<')
            {
                inTag = true;
                // Neuer Absatz, Listenpunkt oder Zeilenumbruch: neue Zeile
                if (LineTag.Match(html, i).Success) lineStart = true;
                continue;
            }
            if (inTag) { if (c == '>') inTag = false; continue; }
            if (c == Open || c == Done) { if (lineStart) yield return i; lineStart = false; continue; }
            if (!char.IsWhiteSpace(c) && c != ' ' && !(c == '&' && html.AsSpan(i).StartsWith("&nbsp;"))) lineStart = false;
        }
    }

    private static readonly Regex LineTag = new("\\G</?(p|li|div|h[1-6]|br)\\b", RegexOptions.IgnoreCase);
    private static readonly Regex LineEnd = new("<(/(p|li|div|h[1-6])|br|p|li|ul|ol|div)\\b", RegexOptions.IgnoreCase);

    /// <summary>Text hinter dem Kaestchen bis zum Zeilenende, ohne Tags.</summary>
    private static string TextAfter(string html, int pos)
    {
        var rest = html[(pos + 1)..];
        var m = LineEnd.Match(rest);
        if (m.Success) rest = rest[..m.Index];
        var text = WebUtility.HtmlDecode(Regex.Replace(rest, "<[^>]*>", ""));
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static string Toggle(string plain, int index, bool done)
    {
        int seen = 0;
        for (int i = 0; i < plain.Length; i++)
        {
            if (plain[i] != Open && plain[i] != Done) continue;
            if (seen++ == index) return plain[..i] + (done ? Done : Open) + plain[(i + 1)..];
        }
        return plain;
    }
}
