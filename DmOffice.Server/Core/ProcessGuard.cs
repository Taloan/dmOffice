using System.Runtime.InteropServices;

namespace DmOffice.Core;

/// <summary>Schützt den Speicher des Servers vor den Werkzeugen, die er startet (Ghostscript, Poppler, Tesseract, libheif).
/// Sie laufen unter demselben Benutzer und lesen fremde Dateien. Ohne Schutz könnte ein Werkzeug mit Lücke über
/// /proc/&lt;pid&gt;/environ oder /proc/&lt;pid&gt;/mem den Hauptschlüssel aus dem Server lesen. Mit PR_SET_DUMPABLE = 0 gehören
/// diese Einträge root, und andere Prozesse desselben Benutzers kommen nicht mehr heran (Kennung tooliso).</summary>
public static class ProcessGuard
{
    private const int PrSetDumpable = 4, PrGetDumpable = 3;

    [DllImport("libc", SetLastError = true)]
#pragma warning disable SYSLIB1054 // LibraryImport bräuchte unsicheren Code im ganzen Projekt
    private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
#pragma warning restore SYSLIB1054

    /// <summary>Beim Start einmal aufrufen. Liefert true, wenn der Schutz greift; außerhalb von Linux false.</summary>
    public static bool Seal()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return prctl(PrSetDumpable, 0, 0, 0, 0) == 0 && prctl(PrGetDumpable, 0, 0, 0, 0) == 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
