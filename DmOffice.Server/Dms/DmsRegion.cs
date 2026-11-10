using System.Text;
using DmOffice.Search;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace DmOffice.Dms;

/// <summary>
/// Text in einem Bereich einer Seite (Rahmen im Betrachter, um ein Feld wie Titel oder Dokumentdatum daraus zu übernehmen).
/// Erst aus der Textschicht der PDF (Text-PDF oder Archivfassung mit unsichtbarer Textschicht), sonst Texterkennung nur für den
/// Ausschnitt. Bereich in Anteilen der Seite (0..1), Ursprung oben links.
/// </summary>
public static class DmsRegion
{
    public sealed record Area(int Page, double X0, double Y0, double X1, double Y1);

    /// <summary>Gefundener Text (Zeilen mit \n getrennt) und ob er aus der Texterkennung des Ausschnitts kommt.</summary>
    public sealed record Found(string Text, bool Ocr);

    /// <summary>Bildbreite für die Texterkennung des Ausschnitts (etwa 300 dpi bei A4).</summary>
    private const int OcrWidth = 2480;

    public static Found TextIn(byte[] pdf, Area a, DmsHost host, CancellationToken ct)
    {
        var (x0, x1) = (Math.Clamp(Math.Min(a.X0, a.X1), 0, 1), Math.Clamp(Math.Max(a.X0, a.X1), 0, 1));
        var (y0, y1) = (Math.Clamp(Math.Min(a.Y0, a.Y1), 0, 1), Math.Clamp(Math.Max(a.Y0, a.Y1), 0, 1));
        var text = FromTextLayer(pdf, a.Page, x0, y0, x1, y1);
        if (text.Length > 0 || !Ocr.Available) return new(text, false);
        var page = host.PdfPage(pdf, a.Page, OcrWidth);
        if (page == null) return new("", false);
        return new(Clean(Ocr.Region(Crop(page, x0, y0, x1, y1), TimeSpan.FromSeconds(60), ct)), true);
    }

    /// <summary>Wörter der Textschicht, deren Mitte im Bereich liegt; Zeile für Zeile von oben, in der Zeile von links.</summary>
    internal static string FromTextLayer(byte[] pdf, int pageIndex, double x0, double y0, double x1, double y1)
    {
        try
        {
            using var doc = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return "";
            var page = doc.GetPage(pageIndex + 1);
            // PDF: Ursprung unten links
            double left = x0 * page.Width, right = x1 * page.Width, top = (1 - y0) * page.Height, bottom = (1 - y1) * page.Height;
            // Wörter nach dem Abstand der Buchstaben: Textschichten aus Texterkennung setzen jeden Buchstaben einzeln und oft ohne
            // Leerzeichen - sonst kam „F u j i f i l m 1 8“ statt „Fujifilm 18“ heraus
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).Where(w => !string.IsNullOrWhiteSpace(w.Text)).Where(w =>
            {
                var b = w.BoundingBox;
                var (cx, cy) = ((b.Left + b.Right) / 2, (b.Bottom + b.Top) / 2);
                return cx >= left && cx <= right && cy >= bottom && cy <= top;
            }).ToList();
            return Lines(words);
        }
        catch (Exception) { return ""; }
    }

    private static string Lines(List<Word> words)
    {
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Top))
        {
            var mid = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
            var line = lines.FirstOrDefault(l => Math.Abs((l[0].BoundingBox.Top + l[0].BoundingBox.Bottom) / 2 - mid) < Math.Max(2, l[0].BoundingBox.Height / 2));
            if (line == null) lines.Add([w]); else line.Add(w);
        }
        return string.Join("\n", lines.Select(l => string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
    }

    private static byte[] Crop(byte[] jpeg, double x0, double y0, double x1, double y1)
    {
        using var bmp = SKBitmap.Decode(jpeg);
        var r = SKRectI.Create((int)(x0 * bmp.Width), (int)(y0 * bmp.Height),
            Math.Max(1, (int)((x1 - x0) * bmp.Width)), Math.Max(1, (int)((y1 - y0) * bmp.Height)));
        using var part = new SKBitmap(r.Width, r.Height);
        bmp.ExtractSubset(part, r);
        using var img = SKImage.FromBitmap(part);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Leerzeilen und Leerzeichen am Rand weg.</summary>
    private static string Clean(string s) =>
        string.Join("\n", s.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
}
