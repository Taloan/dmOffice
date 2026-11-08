using System.Collections.Concurrent;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Sofort verarbeiten statt im Zeitabstand: <see cref="FileStore"/> meldet jede fertig gespeicherte Datei (Hochladen, WebDAV-PUT,
/// fertiger Chunk-Upload, Verschieben, Kopieren, Wiederherstellen). Liegt sie unter einem überwachten Ordner der eigenen Ablage
/// (DMS-Quellen, Importprofile der Galerie), wird dessen Profil angestoßen - entprellt: erst nach <see cref="Watcher.Quiet"/>
/// ohne weitere Datei, damit ein Stapel einen einzigen Lauf ergibt, spätestens nach <see cref="MaxDelay"/>.
/// Was ein Dienst selbst speichert (<see cref="As"/>), stößt ihn nicht wieder an.
/// </summary>
public static class FolderWatch
{
    /// <summary>Ein Dienst: liefert je Person die überwachten Ordner (Schlüssel, Ordner) und stößt einen Schlüssel an.</summary>
    public sealed record Watcher(string Name, TimeSpan Quiet, Func<UserDb, IEnumerable<(string Key, string Folder)>> Folders, Action<string, string> Fire);

    /// <summary>Bei ständig neuen Dateien trotzdem spätestens nach dieser Zeit anstoßen.</summary>
    public static TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, Watcher> Watchers = new();
    /// <summary>Überwachte Ordner je Dienst und Person, kurz zwischengespeichert (eine Abfrage je Datei wäre bei großen Abgleichen zu viel).</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, List<(string Key, string Folder)> Folders)> Cache = new();
    private static readonly ConcurrentDictionary<string, Pending> Waiting = new();
    private static readonly AsyncLocal<string?> Origin = new();
    /// <summary>Tests: angestoßen (Dienst, Person, Schlüssel).</summary>
    internal static event Action<string, string, string>? Fired;

    private sealed class Pending
    {
        public required Timer Timer;
        public DateTime First;
    }

    public static void Watch(Watcher w) => Watchers[w.Name] = w;

    /// <summary>Profile geändert: die Ordner der Person neu lesen.</summary>
    public static void Forget(string userId)
    {
        foreach (var k in Cache.Keys.Where(k => k.EndsWith("|" + userId, StringComparison.Ordinal))) Cache.TryRemove(k, out _);
    }

    /// <summary>Für die Dauer von using: Speichern dieses Dienstes stößt ihn nicht selbst an (keine Endlosschleife).</summary>
    public static IDisposable As(string name)
    {
        var before = Origin.Value;
        Origin.Value = name;
        return new Restore(before);
    }

    private sealed class Restore(string? before) : IDisposable
    {
        public void Dispose() => Origin.Value = before;
    }

    /// <summary>Wartet für diesen Schlüssel ein Anstoß? Dann läuft gerade ein Stapel ein - der Zeitabstand wartet mit.</summary>
    public static bool IsPending(string name, string userId, string key) => Waiting.ContainsKey(name + "|" + userId + "|" + key);

    /// <summary>Eine Datei oder ein Ordner ist fertig gespeichert (aus <see cref="FileStore"/>).</summary>
    internal static void Saved(UserDb db, string path)
    {
        if (Watchers.IsEmpty) return;
        var origin = Origin.Value;
        foreach (var w in Watchers.Values)
        {
            if (w.Name == origin) continue;
            try
            {
                foreach (var (key, folder) in FoldersOf(w, db))
                    if (folder.Length > 0 && (path == folder || FileStore.InRoot(path, folder))) Schedule(w, db.UserId, key);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Das Speichern selbst ist gelungen; der Zeitabstand holt es nach
            }
        }
    }

    private static List<(string Key, string Folder)> FoldersOf(Watcher w, UserDb db)
    {
        var ck = w.Name + "|" + db.UserId;
        var now = DateTime.UtcNow;
        if (Cache.TryGetValue(ck, out var c) && now - c.At < TimeSpan.FromSeconds(30)) return c.Folders;
        var list = w.Folders(db).ToList();
        Cache[ck] = (now, list);
        return list;
    }

    private static void Schedule(Watcher w, string userId, string key)
    {
        var id = w.Name + "|" + userId + "|" + key;
        var now = DateTime.UtcNow;
        while (true)
        {
            if (Waiting.TryGetValue(id, out var p))
            {
                lock (p)
                {
                    // Nach MaxDelay nicht weiter aufschieben; ist der Anstoß schon fort, unten neu anlegen
                    if (!Waiting.ContainsKey(id)) continue;
                    if (now - p.First < MaxDelay) p.Timer.Change(w.Quiet, Timeout.InfiniteTimeSpan);
                    return;
                }
            }
            var n = new Pending { Timer = new Timer(_ => Fire(w, userId, key, id)), First = now };
            if (Waiting.TryAdd(id, n)) { n.Timer.Change(w.Quiet, Timeout.InfiniteTimeSpan); return; }
            n.Timer.Dispose();
        }
    }

    private static void Fire(Watcher w, string userId, string key, string id)
    {
        if (!Waiting.TryGetValue(id, out var p)) return;
        lock (p) Waiting.TryRemove(id, out _);
        p.Timer.Dispose();
        try { Fired?.Invoke(w.Name, userId, key); w.Fire(userId, key); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }
}
