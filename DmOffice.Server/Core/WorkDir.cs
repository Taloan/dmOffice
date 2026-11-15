namespace DmOffice.Core;

/// <summary>
/// Arbeitsordner für Zwischendateien der Werkzeuge (Texterkennung, PDF-Seiten, PDF/A, PDFs zusammenfügen): unter dem
/// Datenverzeichnis (&lt;Daten&gt;/tmp) statt im Temp-Ordner des Systems. Nach Gebrauch werden die Dateien überschrieben und
/// gelöscht; was ein Abbruch liegen lässt, räumt der nächste Start weg. Nicht Teil der Sicherungen.
/// </summary>
public static class WorkDir
{
    public const string FolderName = "tmp";
    private static string? _root;

    /// <summary>Wurzel der Arbeitsordner; vor <see cref="Init"/> (Werkzeuge, Tests ohne Server) im Temp-Ordner des Systems.</summary>
    public static string Root => _root ?? Path.Combine(Path.GetTempPath(), "dmoffice-" + FolderName);

    /// <summary>Beim Start: Wurzel unter dem Datenverzeichnis festlegen und Reste früherer Läufe sicher löschen.</summary>
    public static void Init(string dataRoot)
    {
        _root = Path.Combine(dataRoot, FolderName);
        Directory.CreateDirectory(_root);
        Restrict(_root);
        foreach (var dir in Directory.EnumerateDirectories(_root)) Delete(dir);
        foreach (var file in Directory.EnumerateFiles(_root)) Wipe(file);
    }

    /// <summary>Neuer, leerer Arbeitsordner nur für den Server-Benutzer.</summary>
    public static string Create(string prefix)
    {
        var dir = Path.Combine(Root, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Restrict(dir);
        return dir;
    }

    /// <summary>Arbeitsordner samt Inhalt sicher löschen: jede Datei vorher mit Nullen überschreiben. Wirft nie.</summary>
    public static void Delete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) Wipe(file);
            Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* beim nächsten Start */ }
    }

    /// <summary>Datei mit Nullen überschreiben und löschen (Verknüpfungen nur löschen). Wirft nie.</summary>
    internal static void Wipe(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.LinkTarget == null && info.Length > 0)
            {
                using var f = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None);
                var zeros = new byte[81920];
                for (long left = f.Length; left > 0; left -= zeros.Length) f.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
                f.Flush(flushToDisk: true);
            }
            info.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* beim nächsten Start */ }
    }

    private static void Restrict(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
