using System.Globalization;
using System.Text.RegularExpressions;
using DmOffice.Search;

namespace DmOffice.Dav;

/// <summary>
/// PDF-Seiten über Poppler (pdfinfo, pdftoppm) als eigener Prozess mit Speichergrenze und Zeitgrenze - ein Hänger oder Absturz
/// an einer präparierten Datei trifft so nur das Werkzeug, nicht den Server. Nur unter Linux mit beiden Programmen (im Image);
/// sonst (lokal unter Windows) null, dann rendert PDFium im Serverprozess.
/// </summary>
public static partial class PdfTool
{
    private static readonly Lazy<(string Info, string Render)?> Found = new(Detect);

    public static bool Available => Found.Value != null;

    /// <summary>Höchstens zwei Seiten zugleich (jede Anfrage ein eigener Prozess).</summary>
    private static readonly SemaphoreSlim Gate = new(2);

    /// <summary>Beide Programme da und eine kleine Probeseite klappt - sonst PDFium wie bisher.</summary>
    private static (string Info, string Render)? Detect()
    {
        if (!OperatingSystem.IsLinux() || Ocr.Find("pdfinfo") is not { } i || Ocr.Find("pdftoppm") is not { } r) return null;
        var probe = System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");
        var ok = Info(probe, 0, (i, r)) is { Pages: 1, Width: > 0 } && Render(probe, 0, 100, (i, r)) is { Length: > 0 };
        Console.WriteLine(ok ? "PDF-Vorschau: Poppler als eigener Prozess." : "PDF-Vorschau: Poppler-Probe fehlgeschlagen, PDFium im Serverprozess.");
        return ok ? (i, r) : null;
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Seitenzahl und Größe (pt) einer Seite (ab 0); null, wenn Poppler fehlt oder die Datei nicht liest.</summary>
    public static (int Pages, double Width, double Height)? Info(byte[] pdf, int page = 0) =>
        Found.Value is { } t ? Info(pdf, page, t) : null;

    private static (int Pages, double Width, double Height)? Info(byte[] pdf, int page, (string Info, string Render) t)
    {
        if (!Gate.Wait(Timeout)) return null;
        var dir = Ocr.TempDir();
        try
        {
            var file = Path.Combine(dir, "doc.pdf");
            File.WriteAllBytes(file, pdf);
            var p = (page + 1).ToString(CultureInfo.InvariantCulture);
            var (code, output, _) = Ocr.Run(t.Info, new[] { "-f", p, "-l", p, file }, null, Timeout);
            if (code != 0) return null;
            var pages = PagesLine().Match(output);
            var size = SizeLine().Match(output);
            if (!pages.Success) return null;
            var count = int.Parse(pages.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!size.Success) return (count, 0, 0);
            return (count, double.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        catch (Exception) { return null; }
        finally { Ocr.Clean(dir); Gate.Release(); }
    }

    /// <summary>Seite (ab 0) in der Breite als JPEG (weißer Grund); null bei Fehler.</summary>
    public static byte[]? Render(byte[] pdf, int page, int width) =>
        Found.Value is { } t ? Render(pdf, page, width, t) : null;

    private static byte[]? Render(byte[] pdf, int page, int width, (string Info, string Render) t)
    {
        if (!Gate.Wait(Timeout)) return null;
        var dir = Ocr.TempDir();
        try
        {
            var file = Path.Combine(dir, "doc.pdf");
            File.WriteAllBytes(file, pdf);
            var prefix = Path.Combine(dir, "seite");
            var p = (page + 1).ToString(CultureInfo.InvariantCulture);
            var (code, _, _) = Ocr.Run(t.Render, new[] { "-f", p, "-l", p, "-singlefile", "-jpeg", "-jpegopt", "quality=85",
                "-scale-to-x", width.ToString(CultureInfo.InvariantCulture), "-scale-to-y", "-1", file, prefix }, null, Timeout);
            var jpg = prefix + ".jpg";
            return code == 0 && File.Exists(jpg) ? File.ReadAllBytes(jpg) : null;
        }
        catch (Exception) { return null; }
        finally { Ocr.Clean(dir); Gate.Release(); }
    }

    [GeneratedRegex(@"^Pages:\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex PagesLine();

    [GeneratedRegex(@"^Page\s+\d+\s+size:\s+([\d.]+)\s+x\s+([\d.]+)", RegexOptions.Multiline)]
    private static partial Regex SizeLine();
}
