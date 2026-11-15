using DmOffice.Core;

namespace DmOffice.Storage;

/// <summary>
/// Liegt ein Ordner auf dem Server im Datenverzeichnis, darüber oder ist er es selbst? Nicht nur nach dem Pfadtext:
/// Verknüpfungen und Junctions werden aufgelöst, und dasselbe Verzeichnis unter einer anderen Einbindung
/// (z. B. /mnt/user als /shares, Daten als /data) wird an seiner server.id erkannt.
/// </summary>
public static class DataDirGuard
{
    private static readonly char Sep = Path.DirectorySeparatorChar;
    private static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool Touches(string dir, DataPaths paths)
    {
        var full = Real(dir);
        if (Overlaps(full, Real(paths.Root)) || Overlaps(full, Real(paths.Blobs))) return true;
        string id;
        try { id = S3Mirror.ServerId(paths); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        bool Same(string candidate)
        {
            try { var f = Path.Combine(candidate, S3Mirror.ServerIdFile); return File.Exists(f) && File.ReadAllText(f).Trim() == id; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
        // Darin: der Ordner selbst oder ein Ordner darüber ist das Datenverzeichnis
        for (var p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (Same(p)) return true;
        // Darunter: jedes Ende des Pfads, unter dem das Dateisystem das Datenverzeichnis kennt
        foreach (var fs in FsPaths(Real(paths.Root)))
        {
            var parts = fs[(Path.GetPathRoot(fs)?.Length ?? 0)..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                if (Same(Path.Combine([full, .. parts[i..]]))) return true;
        }
        return false;
    }

    /// <summary>Voller Pfad mit aufgelösten Verknüpfungen und Junctions (Teil für Teil).</summary>
    public static string Real(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var cur = root;
        foreach (var part in full[root.Length..].Split(Sep, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(cur, part);
            try
            {
                var info = new DirectoryInfo(next);
                if (info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) next = Path.GetFullPath(target.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            cur = next;
        }
        var trimmed = cur.TrimEnd(Sep);
        return trimmed.Length == 0 || trimmed.Length < root.TrimEnd(Sep).Length ? root : trimmed;
    }

    private static bool Overlaps(string a, string b)
    {
        if (a.Equals(b, Cmp)) return true;
        string A = a.EndsWith(Sep) ? a : a + Sep, B = b.EndsWith(Sep) ? b : b + Sep;
        return a.StartsWith(B, Cmp) || b.StartsWith(A, Cmp);
    }

    /// <summary>Der Pfad selbst und (Linux) der Pfad innerhalb seines Dateisystems laut /proc/self/mountinfo.</summary>
    private static IEnumerable<string> FsPaths(string real)
    {
        yield return real;
        if (!OperatingSystem.IsLinux()) yield break;
        string[] lines;
        try { lines = File.ReadAllLines("/proc/self/mountinfo"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        string? mount = null, fsRoot = null;
        foreach (var line in lines)
        {
            // Feld 4: Wurzel im Dateisystem, Feld 5: Einhängepunkt
            var f = line.Split(' ');
            if (f.Length < 5) continue;
            var mp = Unescape(f[4]);
            var mpDir = mp.TrimEnd('/');
            if ((real == mp || real.StartsWith(mpDir + "/", StringComparison.Ordinal)) && (mount == null || mp.Length >= mount.Length))
            {
                mount = mp;
                fsRoot = Unescape(f[3]);
            }
        }
        if (mount != null && fsRoot != null)
            yield return fsRoot.TrimEnd('/') + "/" + real[mount.TrimEnd('/').Length..].TrimStart('/');
    }

    private static string Unescape(string s) => s.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
}
