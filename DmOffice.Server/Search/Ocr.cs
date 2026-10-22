using System.Diagnostics;

namespace DmOffice.Search;

/// <summary>
/// Texterkennung ueber Programme im Container, jeweils als eigener Prozess mit niedriger Prioritaet und Zeitgrenze:
/// tesseract (Apache-2.0) erkennt Text in Bildern, pdftoppm (Poppler) rendert Seiten gescannter PDFs als Bild,
/// heif-dec/heif-convert (libheif) macht aus HEIC ein PNG. Absturz oder Haenger eines Werkzeugs trifft so nie den Server.
/// Fehlt tesseract (etwa lokal unter Windows), ist die OCR nicht verfuegbar - der Schalter wird ausgegraut.
/// </summary>
public static class Ocr
{
    public sealed record Tools(string? Tesseract, string? PdfToPpm, string? Heif, string Languages, string? Reason)
    {
        public bool Available => Tesseract != null && Reason == null;
    }

    private static readonly Lazy<Tools> Found = new(Detect);

    /// <summary>Was im Container vorhanden ist (einmal ermittelt).</summary>
    public static Tools Current => Found.Value;

    public static bool Available => Current.Available;

    /// <summary>Gewuenschte Sprachen; nur die vorhandenen werden genutzt.</summary>
    private static readonly string[] Wanted = { "deu", "eng" };

    private static Tools Detect()
    {
        var tesseract = Find("tesseract");
        var pdf = Find("pdftoppm");
        var heif = Find("heif-dec") ?? Find("heif-convert");
        if (tesseract == null) return new(null, pdf, heif, "", "Tesseract ist auf diesem Server nicht installiert.");
        try
        {
            var (code, output, _) = Run(tesseract, new[] { "--list-langs" }, null, TimeSpan.FromSeconds(20));
            var langs = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            var use = Wanted.Where(langs.Contains).ToList();
            if (code != 0 || use.Count == 0) return new(null, pdf, heif, "", "Tesseract hat keine Sprachdaten für Deutsch oder Englisch.");
            return new(tesseract, pdf, heif, string.Join("+", use), null);
        }
        catch (Exception ex) { return new(null, pdf, heif, "", "Tesseract lässt sich nicht starten: " + ex.Message); }
    }

    /// <summary>Programm im PATH suchen (unter Windows auch mit .exe).</summary>
    internal static string? Find(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var candidate in OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : new[] { name })
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), candidate);
                    if (File.Exists(full)) return full;
                }
                catch { /* ungueltiger Eintrag im PATH */ }
            }
        return null;
    }

    /// <summary>Text in einem Bild erkennen (PNG, JPG, TIFF, BMP, GIF, WebP; HEIC ueber libheif).</summary>
    public static string Image(byte[] data, string ext, TimeSpan timeout, CancellationToken ct)
    {
        var tools = Current;
        if (!tools.Available) throw new InvalidOperationException(tools.Reason ?? "Keine Texterkennung.");
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "bild" + ext);
            File.WriteAllBytes(file, data);
            if (ext is ".heic" or ".heif")
            {
                if (tools.Heif == null) throw new NotSupportedException("HEIC lässt sich auf diesem Server nicht lesen.");
                var png = Path.Combine(dir, "bild.png");
                var (code, _, err) = Run(tools.Heif, new[] { file, png }, null, timeout, ct);
                if (code != 0 || !File.Exists(png)) throw new InvalidOperationException("HEIC: " + Short(err));
                file = png;
            }
            return Tesseract(file, timeout, ct);
        }
        finally { Clean(dir); }
    }

    /// <summary>Gescanntes PDF: Seiten als Bild rendern (pdftoppm) und einzeln erkennen.</summary>
    public static string Pdf(byte[] data, int pages, TimeSpan timeoutPerPage, int maxChars, CancellationToken ct)
    {
        var tools = Current;
        if (!tools.Available) throw new InvalidOperationException(tools.Reason ?? "Keine Texterkennung.");
        if (tools.PdfToPpm == null) throw new NotSupportedException("pdftoppm fehlt – gescannte PDFs lassen sich nicht lesen.");
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "doc.pdf");
            File.WriteAllBytes(file, data);
            var text = new System.Text.StringBuilder();
            // Seite fuer Seite: nie mehr als ein Bild auf der Platte, und bei genug Text ist Schluss
            for (int page = 1; page <= pages && text.Length < maxChars; page++)
            {
                ct.ThrowIfCancellationRequested();
                var prefix = Path.Combine(dir, "seite");
                // 200 dpi in Graustufen: gut genug fuer Tesseract, klein genug fuer den Speicher
                var (code, _, err) = Run(tools.PdfToPpm, new[] { "-r", "200", "-gray", "-png", "-f", page.ToString(), "-l", page.ToString(), "-singlefile", file, prefix },
                    null, timeoutPerPage, ct);
                var png = prefix + ".png";
                if (code != 0 || !File.Exists(png))
                {
                    if (page == 1) throw new InvalidOperationException("PDF-Seite rendern: " + Short(err));
                    break;
                }
                text.AppendLine(Tesseract(png, timeoutPerPage, ct));
                File.Delete(png);
            }
            return text.ToString();
        }
        finally { Clean(dir); }
    }

    private static string Tesseract(string file, TimeSpan timeout, CancellationToken ct)
    {
        var tools = Current;
        // Ein Faden je Aufruf: die Warteschlange begrenzt die Parallelitaet selbst
        var env = new Dictionary<string, string> { ["OMP_THREAD_LIMIT"] = "1" };
        var (code, output, err) = Run(tools.Tesseract!, new[] { file, "stdout", "-l", tools.Languages, "--psm", "3" }, env, timeout, ct);
        if (code != 0) throw new InvalidOperationException("Tesseract: " + Short(err));
        return output;
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] : s.Trim();

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dmoffice-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Clean(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* beim naechsten Mal */ }
    }

    private const string PrLimit = "/usr/bin/prlimit";

    /// <summary>Programm ausfuehren: niedrige Prioritaet, Speichergrenze, Ausgabe einsammeln, nach der Zeitgrenze beenden.</summary>
    internal static (int Code, string Output, string Error) Run(string exe, IEnumerable<string> args, IDictionary<string, string>? env,
        TimeSpan timeout, CancellationToken ct = default)
    {
        // Unter Linux mit Speichergrenze (prlimit aus util-linux): die Werkzeuge lesen Dateien von aussen (Mailanhaenge,
        // Uploads). Eine riesige PDF-Seite oder ein praepariertes Bild soll nur das Werkzeug beenden, nicht den Container.
        var limited = OperatingSystem.IsLinux() && File.Exists(PrLimit);
        var psi = new ProcessStartInfo(limited ? PrLimit : exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        if (limited)
        {
            psi.ArgumentList.Add("--as=" + (2L * 1024 * 1024 * 1024));
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(exe);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(exe + " startet nicht.");
        try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { /* nicht ueberall erlaubt */ }
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            p.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* schon weg */ }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(exe)} brauchte länger als {timeout.TotalSeconds:0} s.");
        }
        return (p.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
