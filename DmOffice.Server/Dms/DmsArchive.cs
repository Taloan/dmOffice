using System.Text;
using DmOffice.Search;

namespace DmOffice.Dms;

/// <summary>
/// Archivfassung vom DMS: Scans und Bilder werden zur durchsuchbaren PDF (Seiten als Bild mit unsichtbarer Textschicht, wie
/// OCRmyPDF), dann mit Ghostscript zu PDF/A-2b. Alles als eigene Prozesse mit niedriger Priorität und Zeitgrenze (<see cref="Ocr.Run"/>):
/// pdftoppm rendert die Seiten (JPEG, 200 dpi, Farbe), tesseract erzeugt je Seite PDF und Text, pdfunite fügt zusammen, gs wandelt um.
/// </summary>
public static class DmsArchive
{
    /// <summary>Ergebnis: durchsuchbare PDF (oder null, wenn es nicht ging) und erkannter Text; Note: Grund, wenn etwas fehlte.</summary>
    public sealed record Searchable(byte[]? Pdf, string Text, string? Note);

    /// <summary>Höchstens so viele Seiten bekommen eine Textschicht.</summary>
    public static int MaxPages { get; set; } = 200;
    private static readonly TimeSpan PageTimeout = TimeSpan.FromMinutes(3);

    private static readonly Lazy<string?> Gs = new(() => Ocr.Find("gs"));
    private static readonly Lazy<string?> PdfUnite = new(() => Ocr.Find("pdfunite"));

    /// <summary>Farbprofil für die OutputIntent der PDF/A (Ghostscript bringt eines mit).</summary>
    private static readonly Lazy<string?> Icc = new(() => new[]
    {
        "/usr/share/color/icc/ghostscript/srgb.icc", "/usr/share/color/icc/ghostscript/default_rgb.icc",
        "/usr/share/ghostscript/iccprofiles/srgb.icc", "/usr/share/color/icc/sRGB.icc",
    }.FirstOrDefault(File.Exists));

    public static bool PdfAAvailable => Gs.Value != null && Icc.Value != null;

    private static readonly string[] Images = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".webp", ".heic", ".heif"];

    public static bool CanMakeSearchable(string ext) => ext == ".pdf" || Images.Contains(ext);

    /// <summary>Scan (PDF ohne Text) oder Bild zur durchsuchbaren PDF. Wirft nie - Gründe stehen in Note.</summary>
    public static Searchable MakeSearchable(byte[] data, string ext, CancellationToken ct)
    {
        var tools = Ocr.Current;
        if (!tools.Available) return new(null, "", tools.Reason ?? "Keine Texterkennung.");
        if (!CanMakeSearchable(ext)) return new(null, "", "Dateityp ohne Texterkennung");
        var dir = Path.Combine(Path.GetTempPath(), "dmoffice-dms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pagesPdf = new List<string>();
            var text = new StringBuilder();
            var input = Path.Combine(dir, "in" + ext);
            File.WriteAllBytes(input, data);
            if (ext == ".pdf")
            {
                if (tools.PdfToPpm == null) return new(null, "", "pdftoppm fehlt – gescannte PDFs lassen sich nicht lesen.");
                for (int page = 1; page <= MaxPages; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    var prefix = Path.Combine(dir, "p" + page);
                    var (code, _, err) = Ocr.Run(tools.PdfToPpm, ["-r", "200", "-jpeg", "-jpegopt", "quality=80", "-f", page.ToString(), "-l", page.ToString(),
                        "-singlefile", input, prefix], null, PageTimeout, ct);
                    var jpg = prefix + ".jpg";
                    if (code != 0 || !File.Exists(jpg))
                    {
                        if (page == 1) return new(null, "", "PDF-Seite rendern: " + Short(err));
                        break;
                    }
                    Page(jpg, prefix);
                    File.Delete(jpg);
                }
                // Mehr Seiten als die Grenze: keine gekürzte Archivfassung ablegen, sondern das Original; der Text der ersten Seiten bleibt suchbar
                if (pagesPdf.Count == MaxPages)
                {
                    var next = Path.Combine(dir, "next");
                    var (nextCode, _, _) = Ocr.Run(tools.PdfToPpm, ["-r", "10", "-jpeg", "-f", (MaxPages + 1).ToString(), "-l", (MaxPages + 1).ToString(), "-singlefile", input, next], null, PageTimeout, ct);
                    if (nextCode == 0 && File.Exists(next + ".jpg"))
                        return new(null, text.ToString(), $"Mehr als {MaxPages} Seiten: Text nur aus den ersten {MaxPages} Seiten, abgelegt ist das Original.");
                }
            }
            else
            {
                var image = input;
                if (ext is ".heic" or ".heif")
                {
                    if (tools.Heif == null) return new(null, "", "HEIC lässt sich auf diesem Server nicht lesen.");
                    image = Path.Combine(dir, "in.jpg");
                    var (code, _, err) = Ocr.Run(tools.Heif, [input, image], null, PageTimeout, ct);
                    if (code != 0 || !File.Exists(image)) return new(null, "", "HEIC: " + Short(err));
                }
                // Mehrseitiges TIFF erkennt tesseract selbst
                Page(image, Path.Combine(dir, "p1"));
            }
            if (pagesPdf.Count == 0) return new(null, text.ToString(), "Keine Seite erkannt.");
            string combined;
            if (pagesPdf.Count == 1) combined = pagesPdf[0];
            else
            {
                if (PdfUnite.Value == null) return new(null, text.ToString(), "pdfunite fehlt.");
                combined = Path.Combine(dir, "all.pdf");
                var (code, _, err) = Ocr.Run(PdfUnite.Value, [.. pagesPdf, combined], null, PageTimeout, ct);
                if (code != 0 || !File.Exists(combined)) return new(null, text.ToString(), "Seiten zusammenfügen: " + Short(err));
            }
            return new(File.ReadAllBytes(combined), text.ToString(), null);

            void Page(string image, string prefix)
            {
                var env = new Dictionary<string, string> { ["OMP_THREAD_LIMIT"] = "1" };
                // Unsichtbare Textschicht über dem Bild (pdf) und der Text dazu (txt) in einem Lauf
                var (code, _, err) = Ocr.Run(tools.Tesseract!, [image, prefix, "-l", tools.Languages, "--psm", "3", "pdf", "txt"], env, PageTimeout, ct);
                if (code != 0 || !File.Exists(prefix + ".pdf")) throw new InvalidOperationException("Tesseract: " + Short(err));
                pagesPdf.Add(prefix + ".pdf");
                if (File.Exists(prefix + ".txt")) text.AppendLine(File.ReadAllText(prefix + ".txt"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new(null, "", Short(e.Message)); }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* beim nächsten Mal */ }
        }
    }

    /// <summary>Mehrere PDFs zu einem (pdfunite); null, wenn pdfunite fehlt oder scheitert (etwa ein kaputter Anhang).</summary>
    public static byte[]? Unite(List<byte[]> pdfs, CancellationToken ct)
    {
        if (PdfUnite.Value == null) return null;
        var dir = Path.Combine(Path.GetTempPath(), "dmoffice-dms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = pdfs.Select((p, i) => { var f = Path.Combine(dir, $"{i}.pdf"); File.WriteAllBytes(f, p); return f; }).ToList();
            var output = Path.Combine(dir, "all.pdf");
            var (code, _, _) = Ocr.Run(PdfUnite.Value, [.. files, output], null, PageTimeout, ct);
            return code == 0 && File.Exists(output) ? File.ReadAllBytes(output) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* beim nächsten Mal */ }
        }
    }

    /// <summary>Angaben für die Metadaten der PDF: Titel, Korrespondent (Autor), Dokumenttyp und Dokumentdatum (Betreff), Tags (Schlagwörter).</summary>
    public sealed record PdfInfo(string Title, string? Author, string? Subject, string? Keywords);

    /// <summary>Text als PostScript-Zeichenkette in UTF-16 (Umlaute bleiben erhalten): &lt;FEFF…&gt;.</summary>
    internal static string PsText(string s) =>
        "<FEFF" + Convert.ToHexString(System.Text.Encoding.BigEndianUnicode.GetBytes(s.Length > 1000 ? s[..1000] : s)) + ">";

    /// <summary>DOCINFO-pdfmark; Ghostscript schreibt daraus auch die XMP-Metadaten der PDF/A.</summary>
    internal static string DocInfo(PdfInfo i)
    {
        var parts = new List<string> { "/Title " + PsText(i.Title) };
        if (!string.IsNullOrWhiteSpace(i.Author)) parts.Add("/Author " + PsText(i.Author));
        if (!string.IsNullOrWhiteSpace(i.Subject)) parts.Add("/Subject " + PsText(i.Subject));
        if (!string.IsNullOrWhiteSpace(i.Keywords)) parts.Add("/Keywords " + PsText(i.Keywords));
        return "[ " + string.Join(" ", parts) + " /DOCINFO pdfmark";
    }

    /// <summary>PDF nach PDF/A-2b (Ghostscript, Farbprofil sRGB), mit den Angaben in DOCINFO und XMP; null, wenn Ghostscript fehlt oder die
    /// Umwandlung scheitert.</summary>
    public static byte[]? ToPdfA(byte[] pdf, CancellationToken ct, PdfInfo? info = null)
    {
        if (!PdfAAvailable) return null;
        var dir = Path.Combine(Path.GetTempPath(), "dmoffice-pdfa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var input = Path.Combine(dir, "in.pdf");
            var output = Path.Combine(dir, "out.pdf");
            var def = Path.Combine(dir, "PDFA_def.ps");
            File.WriteAllBytes(input, pdf);
            // OutputIntent mit dem Farbprofil (Vorlage PDFA_def.ps aus Ghostscript)
            File.WriteAllText(def, $$"""
                %!
                /ICCProfile ({{Icc.Value!.Replace("\\", "/")}}) def
                [/_objdef {icc_PDFA} /type /stream /OBJ pdfmark
                [{icc_PDFA} << /N 3 >> /PUT pdfmark
                [{icc_PDFA} ICCProfile (r) file /PUT pdfmark
                [/_objdef {OutputIntent_PDFA} /type /dict /OBJ pdfmark
                [{OutputIntent_PDFA} << /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile {icc_PDFA} /OutputConditionIdentifier (sRGB) >> /PUT pdfmark
                [{Catalog} << /OutputIntents [ {OutputIntent_PDFA} ] >> /PUT pdfmark
                """ + (info != null ? "\n" + DocInfo(info) + "\n" : ""));
            var (code, _, _) = Ocr.Run(Gs.Value!, ["-dPDFA=2", "-dBATCH", "-dNOPAUSE", "-dSAFER", "-dQUIET", $"--permit-file-read={Icc.Value}",
                "-sColorConversionStrategy=RGB", "-sDEVICE=pdfwrite", "-dPDFACompatibilityPolicy=1", $"-sOutputFile={output}", def, input],
                null, TimeSpan.FromMinutes(10), ct);
            if (code != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) return null;
            return File.ReadAllBytes(output);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* beim nächsten Mal */ }
        }
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] : s.Trim();
}
