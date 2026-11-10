using System.Globalization;
using MimeKit;
using SkiaSharp;

namespace DmOffice.Dms;

/// <summary>
/// Archivfassung einer ganzen Mail (docs/dms-konzept.md, „Original und Archivfassung“): PDF mit Kopf (Von, An, Cc, Datum, Betreff),
/// Text der Mail und Liste der Anhänge, gesetzt mit SkiaSharp (Text bleibt Text, also durchsuchbar). HTML-Mails als Text mit
/// Absätzen und Links, nicht im Originallayout; eingebettete Bilder nicht. PDF-Anhänge werden hinten angefügt (pdfunite), danach
/// macht <see cref="DmsArchive.ToPdfA"/> daraus PDF/A. Liest die .eml mit MimeKit (Bibliothek) - keine Mail-Klassen von dmOffice.
/// </summary>
public static class DmsMailPdf
{
    private const float PageWidth = 595, PageHeight = 842, Margin = 50, Body = 10.5f, Head = 10, Title = 15, Line = 1.35f;
    /// <summary>So viele Zeichen Text höchstens (sehr lange Mails werden abgeschnitten).</summary>
    private const int MaxChars = 400_000;

    /// <summary>Schriften: DejaVu Sans (Paket fonts-dejavu-core im Image), sonst was das System hat.</summary>
    internal static readonly Lazy<(SKTypeface Regular, SKTypeface Bold)> Fonts = new(() =>
    {
        SKTypeface? Load(params string[] paths) => paths.Where(File.Exists).Select(SKTypeface.FromFile).FirstOrDefault(t => t != null);
        var regular = Load("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\arial.ttf")
            ?? SKFontManager.Default.MatchFamily("DejaVu Sans") ?? SKTypeface.Default;
        var bold = Load("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", @"C:\Windows\Fonts\segoeuib.ttf", @"C:\Windows\Fonts\arialbd.ttf")
            ?? SKFontManager.Default.MatchFamily("DejaVu Sans", SKFontStyle.Bold) ?? regular;
        return (regular, bold);
    });

    /// <summary>PDF aus einer .eml; htmlToText wandelt HTML in Text (Host). Wirft bei kaputter Mail.</summary>
    public static byte[] Make(byte[] eml, Func<string, string> htmlToText, CancellationToken ct)
    {
        var message = MimeMessage.Load(new MemoryStream(eml));
        var body = message.TextBody ?? (message.HtmlBody is { } html ? htmlToText(html) : "");
        if (body.Length > MaxChars) body = body[..MaxChars] + "\n…";
        var attachments = message.Attachments.Select(a => a is MimePart p ? (p.FileName ?? p.ContentType.Name ?? "Anhang", p) : (a.ContentType.Name ?? "Nachricht.eml", (MimePart?)null)).ToList();

        var main = Render(message, body, attachments.Select(a => a.Item1).ToList());
        // PDF-Anhänge hinten anfügen
        var pdfs = attachments.Where(a => a.Item2 != null && (a.Item2.ContentType.MimeType == "application/pdf" || a.Item1.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            .Select(a => { using var ms = new MemoryStream(); a.Item2!.Content?.DecodeTo(ms, ct); return ms.ToArray(); })
            .Where(b => b.Length > 4 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F').ToList();
        return pdfs.Count == 0 ? main : DmsArchive.Unite([main, .. pdfs], ct) ?? main;
    }

    private static byte[] Render(MimeMessage m, string body, List<string> attachmentNames)
    {
        var (regular, bold) = Fonts.Value;
        using var fBody = new SKFont(regular, Body);
        using var fHead = new SKFont(regular, Head);
        using var fLabel = new SKFont(bold, Head);
        using var fTitle = new SKFont(bold, Title);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var grey = new SKPaint { Color = new SKColor(0x55, 0x55, 0x55), IsAntialias = true };
        using var rule = new SKPaint { Color = new SKColor(0xBB, 0xBB, 0xBB), StrokeWidth = 0.7f };

        using var output = new MemoryStream();
        var meta = new SKDocumentPdfMetadata { Title = m.Subject ?? "", Author = m.From.ToString(), Creator = "dmOffice DMS", Producer = "dmOffice",
            Creation = DateTime.Now, Modified = DateTime.Now, PdfA = true };
        using (var doc = SKDocument.CreatePdf(output, meta))
        {
            SKCanvas? canvas = null;
            float y = 0;
            var width = PageWidth - 2 * Margin;
            void NewPage()
            {
                if (canvas != null) doc.EndPage();
                canvas = doc.BeginPage(PageWidth, PageHeight);
                y = Margin;
            }
            void Ensure(float h) { if (canvas == null || y + h > PageHeight - Margin) NewPage(); }
            void Text(string text, SKFont font, SKPaint paint, float indent = 0)
            {
                var h = font.Size * Line;
                foreach (var line in Wrap(text, font, width - indent))
                {
                    Ensure(h);
                    y += font.Size;
                    canvas!.DrawText(line, Margin + indent, y, SKTextAlign.Left, font, paint);
                    y += h - font.Size;
                }
            }
            NewPage();
            Text(string.IsNullOrWhiteSpace(m.Subject) ? "(ohne Betreff)" : m.Subject, fTitle, ink);
            y += 6;
            void Field(string label, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var lw = 60f;
                var h = Head * Line;
                var lines = Wrap(value, fHead, width - lw).ToList();
                for (var i = 0; i < lines.Count; i++)
                {
                    Ensure(h);
                    y += Head;
                    if (i == 0) canvas!.DrawText(label, Margin, y, SKTextAlign.Left, fLabel, grey);
                    canvas!.DrawText(lines[i], Margin + lw, y, SKTextAlign.Left, fHead, ink);
                    y += h - Head;
                }
            }
            Field("Von", m.From.ToString());
            Field("An", m.To.ToString());
            Field("Cc", m.Cc.ToString());
            Field("Datum", m.Date.ToLocalTime().ToString("dddd, d. MMMM yyyy HH:mm", CultureInfo.GetCultureInfo("de-DE")));
            if (attachmentNames.Count > 0) Field("Anhänge", string.Join(", ", attachmentNames));
            y += 6;
            Ensure(10);
            canvas!.DrawLine(Margin, y, PageWidth - Margin, y, rule);
            y += 12;
            foreach (var para in body.Replace("\r\n", "\n").Split('\n'))
            {
                if (para.Trim().Length == 0) { y += Body * 0.6f; continue; }
                Text(para.TrimEnd(), fBody, ink);
            }
            doc.EndPage();
            doc.Close();
        }
        return output.ToArray();
    }

    /// <summary>Zeilen umbrechen: an Leerzeichen, überlange Wörter hart.</summary>
    internal static IEnumerable<string> Wrap(string text, SKFont font, float width)
    {
        var line = "";
        foreach (var raw in text.Replace('\t', ' ').Split(' '))
        {
            var word = raw;
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureText(candidate) <= width) { line = candidate; continue; }
            if (line.Length > 0) { yield return line; line = ""; }
            while (font.MeasureText(word) > width && word.Length > 1)
            {
                var n = word.Length;
                while (n > 1 && font.MeasureText(word[..n]) > width) n--;
                yield return word[..n];
                word = word[n..];
            }
            line = word;
        }
        if (line.Length > 0) yield return line;
    }
}
