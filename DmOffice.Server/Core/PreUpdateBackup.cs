using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DmOffice.Core;

/// <summary>
/// Sicherung vor einer Datenaenderung: von jeder Benutzerdatenbank, die umgebaut wird, eine konsistente Kopie
/// (VACUUM INTO - mit demselben Schluessel verschluesselt), dazu die kleinen Dateien des Datenordners (users.json,
/// server.json, shares.json, master.key, ...) und keys/. Liegt unter &lt;Daten&gt;/pre-update/&lt;Datum&gt;-&lt;Version&gt;/.
/// Die Blob-Dateien (Dateiinhalte) sind nicht dabei: die Auslagerung loescht nichts, was nicht bis zur ausdruecklichen
/// Verdichtung in der Datenbank bliebe - und die Kopie der Datenbank hat die alten Inhalte noch.
/// Jede Datei entsteht als .part und wird erst fertig umbenannt; die Liste (manifest.json) nennt nur Fertiges. Ein
/// Abbruch hinterlaesst also hoechstens Reste, die der naechste Versuch wegraeumt.
/// </summary>
public static class PreUpdateBackup
{
    public const string FolderName = "pre-update";
    private const long MaxStaticFile = 64L * 1024 * 1024;

    /// <param name="Schema">Version, aus der umgebaut wird (so steht die Kopie).</param>
    /// <param name="To">Version, auf die umgebaut werden soll.</param>
    public sealed record Entry(string Id, string Login, int Schema, int To, long Bytes, DateTime Created);

    public sealed class Manifest
    {
        public string Name { get; set; } = "";
        public DateTime Created { get; set; }
        public string AppVersion { get; set; } = "";
        public List<Entry> Users { get; set; } = new();
    }

    public sealed record Info(string Name, DateTime Created, string AppVersion, long Bytes, List<Entry> Users, string Path);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static string Root(DataPaths paths) => Path.Combine(paths.Root, FolderName);

    public static string NameFor(DateTime utc, string appVersion) =>
        utc.ToString("yyyy-MM-dd") + "-" + new string(appVersion.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

    public static bool ValidName(string name) =>
        name.Length is > 0 and <= 80 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && !name.StartsWith('.');

    private static Manifest? ReadManifest(string dir)
    {
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")), Json); }
        catch { return null; }
    }

    private static void WriteManifest(string dir, Manifest m)
    {
        var path = Path.Combine(dir, "manifest.json");
        File.WriteAllText(path + ".part", JsonSerializer.Serialize(m, Json));
        File.Move(path + ".part", path, overwrite: true);
    }

    /// <summary>Benutzer wurde geloescht: seine Datenbankkopien aus allen Sicherungen entfernen (Datenschutz - ein spaeteres
    /// Zurueckspielen darf ihn nicht wieder herbeiholen). Gibt die Zahl der entfernten Kopien zurueck.</summary>
    public static int ForgetUser(DataPaths paths, string userId)
    {
        var root = Root(paths);
        if (!Directory.Exists(root)) return 0;
        int n = 0;
        lock (Gate)
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(dir, "users", userId + ".db");
                if (File.Exists(file)) { File.Delete(file); n++; }
                if (ReadManifest(dir) is { } m && m.Users.RemoveAll(u => u.Id == userId) > 0) WriteManifest(dir, m);
            }
        return n;
    }

    /// <summary>Gibt es schon eine fertige Kopie fuer den laufenden Umbau dieser Datenbank? Die Datenbank steht jetzt auf
    /// <paramref name="schema"/>; die Kopie gilt, wenn sie von dort oder frueher stammt und der Umbau, fuer den sie gemacht
    /// wurde, noch nicht fertig ist (ein abgebrochener Umbau steht irgendwo dazwischen). Eine Kopie aus einem frueheren,
    /// abgeschlossenen Umbau gilt nicht.</summary>
    public static bool Has(DataPaths paths, string userId, int schema)
    {
        var root = Root(paths);
        if (!Directory.Exists(root)) return false;
        foreach (var dir in Directory.EnumerateDirectories(root))
            if (ReadManifest(dir) is { } m && m.Users.Any(u => u.Id == userId && u.Schema <= schema && schema < u.To)
                && File.Exists(Path.Combine(dir, "users", userId + ".db")))
                return true;
        return false;
    }

    /// <summary>Kopie der Datenbank anlegen (und beim ersten Mal die Dateien des Datenordners). Gibt den Ordner zurueck.</summary>
    public static string Create(DataPaths paths, UserRecord user, byte[] key, int schema, int to, string appVersion, DateTime now)
    {
        lock (Gate)
        {
            var dir = Path.Combine(Root(paths), NameFor(now, appVersion));
            Directory.CreateDirectory(Path.Combine(dir, "users"));
            // Reste eines abgebrochenen Versuchs
            foreach (var part in Directory.EnumerateFiles(dir, "*.part", SearchOption.AllDirectories)) File.Delete(part);

            var manifest = ReadManifest(dir);
            if (manifest == null)
            {
                manifest = new Manifest { Name = Path.GetFileName(dir), Created = now, AppVersion = appVersion };
                CopyStatic(paths, dir);
            }

            var source = paths.UserDb(user.Id);
            var target = Path.Combine(dir, "users", user.Id + ".db");
            var tmp = target + ".part";
            try
            {
                if (File.Exists(target)) File.Delete(target);
                using (var conn = new SqliteConnection(UserDb.ConnectionStringOf(source, key)))
                {
                    conn.Open();
                    using var pragma = conn.CreateCommand();
                    pragma.CommandText = "PRAGMA busy_timeout=30000";
                    pragma.ExecuteNonQuery();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandTimeout = 0;
                    cmd.CommandText = "VACUUM INTO $f";
                    cmd.Parameters.AddWithValue("$f", tmp);
                    cmd.ExecuteNonQuery();
                }
                File.Move(tmp, target);
            }
            catch
            {
                try { File.Delete(tmp); } catch { /* egal */ }
                throw;
            }
            manifest.Users.RemoveAll(u => u.Id == user.Id);
            manifest.Users.Add(new Entry(user.Id, user.Login, schema, to, new FileInfo(target).Length, now));
            WriteManifest(dir, manifest);
            return dir;
        }
    }

    /// <summary>Die kleinen Dateien des Datenordners (Benutzerliste, Einstellungen, Freigaben, Hauptschluessel) und keys/.</summary>
    private static void CopyStatic(DataPaths paths, string dir)
    {
        foreach (var f in Directory.EnumerateFiles(paths.Root))
        {
            var info = new FileInfo(f);
            if (info.Length > MaxStaticFile || info.Extension is ".tmp" or ".part") continue;
            // Den Hauptschluessel NICHT mitsichern: sonst oeffnete eine alte Kopie der Sicherung alle aktuellen
            // Benutzerdatenbanken weiter (die DB-Schluessel aendern sich beim Wechsel auf die Umgebung nicht).
            // Fuer eine Ruecksicherung auf demselben System liegt der Schluessel ohnehin vor.
            if (info.Name.Equals("master.key", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(f, Path.Combine(dir, info.Name), overwrite: true);
        }
        if (Directory.Exists(paths.Keys))
            foreach (var f in Directory.EnumerateFiles(paths.Keys, "*", SearchOption.AllDirectories))
            {
                // fcm.json (Firebase-Dienstkonto, privater Schluessel) liegt im Klartext - nicht mitsichern.
                // Der Data-Protection-Schluesselring ist mit dem Hauptschluessel eingepackt und darf mit.
                if (Path.GetFileName(f).Equals("fcm.json", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Path.GetRelativePath(paths.Root, f);
                var to = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, overwrite: true);
            }
    }

    private static long DirBytes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });

    public static List<Info> List(DataPaths paths)
    {
        var root = Root(paths);
        if (!Directory.Exists(root)) return new();
        var result = new List<Info>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var m = ReadManifest(dir);
            var created = m?.Created ?? Directory.GetCreationTimeUtc(dir);
            result.Add(new Info(Path.GetFileName(dir), created, m?.AppVersion ?? "", DirBytes(dir), m?.Users ?? new(), dir));
        }
        return result.OrderByDescending(i => i.Created).ToList();
    }

    /// <summary>Belegter Platz aller Sicherungen.</summary>
    public static long TotalBytes(DataPaths paths) => Directory.Exists(Root(paths)) ? DirBytes(Root(paths)) : 0;

    public static bool Delete(DataPaths paths, string name)
    {
        if (!ValidName(name)) return false;
        var dir = Path.Combine(Root(paths), name);
        if (!Directory.Exists(dir)) return false;
        lock (Gate) Directory.Delete(dir, recursive: true);
        return true;
    }

    /// <summary>Sicherungen loeschen, die aelter sind als <paramref name="keepDays"/> Tage (0: nie). Gibt die Zahl der geloeschten zurueck.</summary>
    public static int Cleanup(DataPaths paths, int keepDays, DateTime now)
    {
        if (keepDays <= 0) return 0;
        int n = 0;
        foreach (var info in List(paths))
            if (now - info.Created > TimeSpan.FromDays(keepDays) && Delete(paths, info.Name)) n++;
        return n;
    }
}
