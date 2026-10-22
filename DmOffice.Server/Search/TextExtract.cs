using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MimeKit;
using UglyToad.PdfPig;

namespace DmOffice.Search;

/// <summary>Grenzen je Datei - damit ein riesiges oder boesartiges Dokument den Server nicht lahmlegt.</summary>
public sealed record ExtractLimits
{
    /// <summary>Groessere Dateien werden nicht gelesen.</summary>
    public long MaxBytes { get; init; } = 50L * 1024 * 1024;
    /// <summary>Text: so viele PDF-Seiten.</summary>
    public int MaxPages { get; init; } = 300;
    /// <summary>OCR: so viele Seiten eines gescannten PDFs.</summary>
    public int MaxOcrPages { get; init; } = 30;
    /// <summary>So viel Text kommt in den Index, der Rest wird abgeschnitten.</summary>
    public int MaxChars { get; init; } = 500_000;
    /// <summary>Bilder unter dieser Groesse sind meist Logos und Symbole - keine OCR.</summary>
    public int MinImageBytes { get; init; } = 12 * 1024;
    /// <summary>Zeitgrenze fuer ein Bild bzw. eine PDF-Seite in der OCR.</summary>
    public TimeSpan OcrTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public static readonly ExtractLimits Default = new();
}

/// <summary>Ergebnis: Text (oder leer), Art der Erkennung und Zustand.</summary>
/// <param name="Method">text | ocr | none</param>
/// <param name="Status">ok - Text gefunden; empty - gelesen, aber ohne Text; skipped - Format/Groesse nicht unterstuetzt
/// oder Texterkennung noetig; error - Fehler beim Lesen</param>
/// <param name="WantsOcr">Mit Texterkennung kaeme (mehr) Text heraus - wird beim Einschalten der OCR neu gelesen.</param>
public sealed record Extracted(string Text, string Method, string Status, string? Note = null, bool WantsOcr = false)
{
    public static Extracted Skip(string note) => new("", "none", "skipped", note);
    public static Extracted NeedsOcr(string note) => new("", "none", "skipped", note, true);
}

/// <summary>
/// Text aus Dateien und Mail-Anhaengen fuer den Suchindex: PDF mit Textebene (PdfPig), Word/Excel/PowerPoint
/// (Open XML, als Strom gelesen - auch ein aufgeblasenes Dokument kommt nur bis zur Textgrenze), Text, CSV,
/// Markdown, HTML und angehaengte Mails. Bilder und gescannte PDFs ueber <see cref="Ocr"/>, wenn erlaubt.
/// </summary>
public static class TextExtract
{
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".csv", ".tsv", ".md", ".markdown", ".log", ".json", ".xml", ".ini", ".yaml", ".yml", ".vcf", ".ics" };
    private static readonly HashSet<string> HtmlExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml" };
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".webp", ".heic", ".heif" };

    /// <summary>Endung zum Dateinamen, sonst aus dem MIME-Typ.</summary>
    public static string Ext(string name, string? mime)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext.Length > 1) return ext;
        return (mime ?? "").ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "message/rfc822" => ".eml",
            "text/html" => ".html",
            "text/csv" => ".csv",
            "text/markdown" => ".md",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/tiff" => ".tif",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/heic" or "image/heif" => ".heic",
            var m when m.StartsWith("text/") => ".txt",
            _ => ext
        };
    }

    /// <summary>Kann diese Datei ueberhaupt Text liefern? Sonst gar nicht erst einreihen bzw. lesen.</summary>
    public static bool Supported(string name, string? mime)
    {
        var ext = Ext(name, mime);
        return ext is ".pdf" or ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".pptm" or ".eml"
               || TextExt.Contains(ext) || HtmlExt.Contains(ext) || ImageExt.Contains(ext);
    }

    public static bool IsImage(string name, string? mime) => ImageExt.Contains(Ext(name, mime));

    /// <summary>Endung nach den ersten Bytes (eingebettete Bilder in Notizen haben keinen Namen); "" wenn unbekannt.</summary>
    public static string SniffImage(byte[] d)
    {
        bool At(int i, params byte[] b) => d.Length >= i + b.Length && b.Select((x, k) => d[i + k] == x).All(x => x);
        if (At(0, 0x89, 0x50, 0x4E, 0x47)) return ".png";
        if (At(0, 0xFF, 0xD8, 0xFF)) return ".jpg";
        if (At(0, 0x47, 0x49, 0x46, 0x38)) return ".gif";
        if (At(0, 0x52, 0x49, 0x46, 0x46) && At(8, 0x57, 0x45, 0x42, 0x50)) return ".webp";
        if (At(0, 0x49, 0x49, 0x2A, 0x00) || At(0, 0x4D, 0x4D, 0x00, 0x2A)) return ".tif";
        if (At(0, 0x42, 0x4D)) return ".bmp";
        if (At(4, 0x66, 0x74, 0x79, 0x70) && (At(8, 0x68, 0x65, 0x69, 0x63) || At(8, 0x6D, 0x69, 0x66, 0x31) || At(8, 0x68, 0x65, 0x69, 0x78))) return ".heic";
        if (At(0, 0x25, 0x50, 0x44, 0x46)) return ".pdf";
        return "";
    }

    /// <summary>Text aus einer Datei holen. Wirft nie - Fehler stehen im Ergebnis.</summary>
    /// <param name="ocr">Texterkennung fuer Bilder und gescannte PDFs erlaubt (Schalter des Benutzers).</param>
    public static Extracted Extract(byte[] data, string name, string? mime, bool ocr, ExtractLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= ExtractLimits.Default;
        var ext = Ext(name, mime);
        if (!Supported(name, mime)) return Extracted.Skip("Dateityp ohne Text");
        if (data.LongLength > limits.MaxBytes) return Extracted.Skip($"größer als {limits.MaxBytes / 1024 / 1024} MB");
        try
        {
            if (ext == ".pdf") return Pdf(data, ocr, limits, ct);
            if (ext is ".docx" or ".docm") return Done(Word(data, limits.MaxChars), limits);
            if (ext is ".xlsx" or ".xlsm") return Done(Excel(data, limits.MaxChars), limits);
            if (ext is ".pptx" or ".pptm") return Done(PowerPoint(data, limits.MaxChars), limits);
            if (ext == ".eml") return Done(Eml(data, ocr, limits, ct), limits);
            if (HtmlExt.Contains(ext)) return Done(Mail.MailHtml.ToText(Decode(data)), limits);
            if (TextExt.Contains(ext)) return Done(Decode(data), limits);
            if (ImageExt.Contains(ext))
            {
                if (data.Length < limits.MinImageBytes) return Extracted.Skip("Bild zu klein");
                if (!ocr) return Extracted.NeedsOcr("Bild – Texterkennung ist aus");
                if (!Ocr.Available) return Extracted.NeedsOcr(Ocr.Current.Reason ?? "keine Texterkennung");
                return Done(Ocr.Image(data, ext, limits.OcrTimeout, ct), limits, "ocr");
            }
            return Extracted.Skip("Dateityp ohne Text");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new("", "none", "error", Short(ex.Message)); }
    }

    private static Extracted Done(string text, ExtractLimits limits, string method = "text")
    {
        text = Clean(text, limits.MaxChars);
        return new(text, method, text.Length == 0 ? "empty" : "ok");
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] : s;

    /// <summary>Leerraum zusammenfassen, Steuerzeichen raus, auf die Grenze kuerzen.</summary>
    internal static string Clean(string text, int max)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max));
        bool space = false, newline = false;
        foreach (var c in text)
        {
            if (sb.Length >= max) break;
            if (c == '\n') { newline = true; continue; }
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
            if (sb.Length > 0 && (newline || space)) sb.Append(newline ? '\n' : ' ');
            newline = space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>UTF-8 (mit oder ohne BOM), sonst Windows-1252 - so sind alte deutsche Textdateien lesbar.</summary>
    internal static string Decode(byte[] data)
    {
        if (data.Length >= 2 && (data[0] == 0xFF && data[1] == 0xFE || data[0] == 0xFE && data[1] == 0xFF))
            return new StreamReader(new MemoryStream(data), true).ReadToEnd();
        try { return new UTF8Encoding(false, true).GetString(data).TrimStart((char)0xFEFF); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(data); }
    }

    // ---------------- PDF ----------------

    private static Extracted Pdf(byte[] data, bool ocr, ExtractLimits limits, CancellationToken ct)
    {
        int pages;
        var sb = new StringBuilder();
        using (var doc = PdfDocument.Open(data, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true }))
        {
            pages = doc.NumberOfPages;
            for (int i = 1; i <= Math.Min(pages, limits.MaxPages) && sb.Length < limits.MaxChars; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.GetPage(i);
                foreach (var word in page.GetWords())
                {
                    sb.Append(word.Text).Append(' ');
                    if (sb.Length >= limits.MaxChars) break;
                }
                sb.Append('\n');
            }
        }
        var text = Clean(sb.ToString(), limits.MaxChars);
        // Kaum Text je Seite: eine gescannte Seite (oft nur eine Kopf- oder Fusszeile als Text)
        if (text.Count(char.IsLetterOrDigit) >= Math.Min(pages, limits.MaxPages) * 25) return new(text, "text", "ok");
        if (!ocr) return text.Length > 0 ? new(text, "text", "ok", "gescannt – Texterkennung ist aus", true) : Extracted.NeedsOcr("gescanntes PDF – Texterkennung ist aus");
        if (!Ocr.Available) return text.Length > 0 ? new(text, "text", "ok", Ocr.Current.Reason, true) : Extracted.NeedsOcr(Ocr.Current.Reason ?? "keine Texterkennung");
        var scanned = Ocr.Pdf(data, Math.Min(pages, limits.MaxOcrPages), limits.OcrTimeout, limits.MaxChars, ct);
        var all = Clean(text + "\n" + scanned, limits.MaxChars);
        return new(all, "ocr", all.Length == 0 ? "empty" : "ok");
    }

    // ---------------- Office (Open XML, als Strom) ----------------

    private static string Word(byte[] data, int max)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(data), false, OxSettings);
        var sb = new StringBuilder();
        var main = doc.MainDocumentPart;
        if (main == null) return "";
        var parts = new List<OpenXmlPart> { main };
        parts.AddRange(main.HeaderParts);
        parts.AddRange(main.FooterParts);
        if (main.FootnotesPart != null) parts.Add(main.FootnotesPart);
        if (main.EndnotesPart != null) parts.Add(main.EndnotesPart);
        foreach (var part in parts)
        {
            using var reader = OpenXmlReader.Create(part);
            while (reader.Read() && sb.Length < max)
            {
                if (!reader.IsStartElement) { if (reader.LocalName == "p") sb.Append('\n'); continue; }
                if (reader.LocalName == "t") sb.Append(reader.GetText());
                else if (reader.LocalName is "tab") sb.Append(' ');
                else if (reader.LocalName is "br" or "cr") sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string Excel(byte[] data, int max)
    {
        using var doc = SpreadsheetDocument.Open(new MemoryStream(data), false, OxSettings);
        var wb = doc.WorkbookPart;
        if (wb == null) return "";
        // Geteilte Zeichenketten der Reihe nach (Zellen verweisen per Nummer)
        var shared = new List<string>();
        if (wb.SharedStringTablePart is { } sst)
        {
            using var reader = OpenXmlReader.Create(sst);
            var current = new StringBuilder();
            long total = 0;
            while (reader.Read())
            {
                if (reader.LocalName == "si")
                {
                    if (reader.IsStartElement) current.Clear();
                    else { shared.Add(current.ToString()); total += current.Length; if (total > max) break; }
                }
                else if (reader.LocalName == "t" && reader.IsStartElement) current.Append(reader.GetText());
            }
        }
        var sb = new StringBuilder();
        foreach (var sheet in wb.WorksheetParts)
        {
            using var reader = OpenXmlReader.Create(sheet);
            string? type = null;
            while (reader.Read() && sb.Length < max)
            {
                if (reader.LocalName == "c" && reader.IsStartElement)
                    type = reader.Attributes.FirstOrDefault(a => a.LocalName == "t").Value;
                else if (reader.LocalName == "v" && reader.IsStartElement)
                {
                    var v = reader.GetText();
                    if (type == "s" && int.TryParse(v, out var i) && i >= 0 && i < shared.Count) sb.Append(shared[i]);
                    else sb.Append(v);
                    sb.Append(' ');
                }
                else if (reader.LocalName == "t" && reader.IsStartElement && type == "inlineStr") sb.Append(reader.GetText()).Append(' ');
                else if (reader.LocalName == "row" && !reader.IsStartElement) sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string PowerPoint(byte[] data, int max)
    {
        using var doc = PresentationDocument.Open(new MemoryStream(data), false, OxSettings);
        var sb = new StringBuilder();
        var pres = doc.PresentationPart;
        if (pres == null) return "";
        var parts = new List<OpenXmlPart>();
        foreach (var slide in pres.SlideParts)
        {
            parts.Add(slide);
            if (slide.NotesSlidePart != null) parts.Add(slide.NotesSlidePart);
        }
        foreach (var part in parts)
        {
            using var reader = OpenXmlReader.Create(part);
            while (reader.Read() && sb.Length < max)
            {
                if (reader.LocalName == "t" && reader.IsStartElement) sb.Append(reader.GetText()).Append(' ');
                else if (reader.LocalName == "p" && !reader.IsStartElement) sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Office-Dateien: ein einzelner XML-Teil hoechstens 20 Mio. Zeichen. Ohne Grenze liest OpenXml auch
    /// gepackte Teile beliebiger Groesse (Zip-Bombe: wenige MB werden Gigabyte im Speicher).</summary>
    private static readonly DocumentFormat.OpenXml.Packaging.OpenSettings OxSettings = new() { MaxCharactersInPart = 20_000_000 };

    // ---------------- Angehaengte Mail ----------------

    private static string Eml(byte[] data, bool ocr, ExtractLimits limits, CancellationToken ct)
    {
        var m = MimeMessage.Load(new MemoryStream(data));
        return MessageText(m, ocr, limits, ct, depth: 0);
    }

    /// <summary>Kopf, Text und (eine Ebene tief) die Anhaenge einer Mail.</summary>
    internal static string MessageText(MimeMessage m, bool ocr, ExtractLimits limits, CancellationToken ct, int depth)
    {
        var sb = new StringBuilder();
        sb.AppendLine(m.Subject);
        sb.AppendLine(m.From.ToString());
        sb.AppendLine(m.To.ToString());
        sb.AppendLine(Mail.MailStore.BodyText(m));
        if (depth > 0) return sb.ToString();
        foreach (var a in m.Attachments)
        {
            if (sb.Length >= limits.MaxChars) break;
            ct.ThrowIfCancellationRequested();
            if (a is MessagePart mp && mp.Message != null) sb.AppendLine(MessageText(mp.Message, ocr, limits, ct, depth + 1));
            else if (a is MimePart p)
            {
                var name = p.FileName ?? p.ContentType.Name ?? "";
                if (!Supported(name, p.ContentType.MimeType)) continue;
                // Angehaengte Mail als Datei (*.eml): wie message/rfc822 nur eine Ebene tief - Kopf und Text,
                // nie deren Anhaenge. Ueber Extract → Eml begann sonst jede Ebene wieder bei Tiefe 0, und eine
                // Mail mit tausendfach verschachtelten .eml-Anhaengen liess den Stack ueberlaufen (Prozessende).
                if (Ext(name, p.ContentType.MimeType) == ".eml")
                {
                    try { sb.AppendLine(MessageText(MimeMessage.Load(new MemoryStream(Mail.MailCompose.Decode(p))), ocr, limits, ct, depth + 1)); }
                    catch (FormatException) { /* keine lesbare Mail */ }
                    continue;
                }
                var r = Extract(Mail.MailCompose.Decode(p), name, p.ContentType.MimeType, ocr, limits with { MaxChars = limits.MaxChars - sb.Length }, ct);
                if (r.Text.Length > 0) sb.AppendLine(r.Text);
            }
        }
        return sb.ToString();
    }
}
