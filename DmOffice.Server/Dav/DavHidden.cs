using System.Collections.Concurrent;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Ordner, die die Person für WebDAV und Nextcloud-Clients ausgeblendet hat (Kontextmenü der Dateiablage). Gemerkt wird die
/// Dateinummer - sie bleibt beim Umbenennen und Verschieben gleich. Ausgeblendet ist der Ordner samt Inhalt; in der
/// Weboberfläche und der App bleibt alles sichtbar. Der Tresor-Ordner (dmVAULT) folgt der Einstellung des Tresors.
/// </summary>
public static class DavHidden
{
    private const string Key = "files.dav-hidden";
    /// <summary>Pfade kurz zwischenhalten: die Prüfung läuft für jeden Eintrag einer WebDAV-Liste.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<UserDb, (DateTime At, string[] Paths)> Cache = new();

    public static HashSet<long> Ids(UserDb db) =>
        db.Get(Key) is { } json ? JsonSerializer.Deserialize<HashSet<long>>(json) ?? [] : [];

    /// <summary>Ein- oder ausblenden; gibt zurück, ob sich etwas geändert hat.</summary>
    public static bool Set(UserDb db, long fileId, bool hidden)
    {
        var ids = Ids(db);
        var changed = hidden ? ids.Add(fileId) : ids.Remove(fileId);
        if (changed) db.Set(Key, ids.Count == 0 ? null : JsonSerializer.Serialize(ids));
        Cache.TryRemove(db, out _);
        return changed;
    }

    /// <summary>Liegt der Pfad in einem ausgeblendeten Ordner (oder ist er es selbst)?</summary>
    public static bool Covers(UserDb db, string path)
    {
        var paths = PathsOf(db);
        return paths.Length > 0 && paths.Any(p => FileStore.InRoot(path, p));
    }

    private static string[] PathsOf(UserDb db)
    {
        if (Cache.TryGetValue(db, out var c) && DateTime.UtcNow - c.At < Fresh) return c.Paths;
        var ids = Ids(db);
        var paths = ids.Count == 0 ? [] : db.Query("SELECT path FROM files WHERE is_dir = 1 AND fileid IN (" + string.Join(",", ids) + ")", r => r.GetString(0)).ToArray();
        Cache[db] = (DateTime.UtcNow, paths);
        return paths;
    }
}
