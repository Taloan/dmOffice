using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Journal;

/// <summary>
/// Bei jedem Start: Tagebuch-Medien aus dem sichtbaren Ordner „Tagebuch/Jahr/Monat“ (bis Oktober 2026) in den Systemordner
/// <see cref="JournalMediaStore.Root"/> umziehen und die Einträge darauf umstellen. Verschoben wird nur, worauf ein Eintrag
/// zeigt - eigene Dateien in einem Ordner „Tagebuch“ bleiben, wo sie sind; leer gewordene Ordner darunter fallen weg.
/// </summary>
public sealed class JournalMediaMove(Spaces spaces, ILogger<JournalMediaMove> log) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Release-Liste: in dieser Version nicht freigeschaltet - nichts im Hintergrund
        if (!DmOffice.Core.Features.On(DmOffice.Core.Features.Journal)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        await Task.Run(() =>
        {
            foreach (var space in spaces.All())
            {
                if (ct.IsCancellationRequested) return;
                // Als Warnung, damit es im Protokoll steht (Informationen blendet der Server aus)
                try { if (Run(space) is var n and > 0) log.LogWarning("Tagebuch {User}: {Count} Medien in den Systemordner verschoben", space.User.Login, n); }
                catch (Exception e) { log.LogWarning("Tagebuch {User}: Medien nicht verschoben: {Error}", space.User.Login, e.Message); }
            }
        }, CancellationToken.None);
    }

    /// <summary>Umzug für einen Benutzer; zurück: wie viele Dateien verschoben wurden.</summary>
    public static int Run(UserSpace space)
    {
        var db = space.Db;
        // Kein Merker „erledigt“: findet nichts mehr unter „Tagebuch/“, kostet ein Durchgang nur das Lesen der Einträge
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Map(string path)
        {
            if (moved.TryGetValue(path, out var done)) return done;
            if (!FileStore.InRoot(path, JournalMediaStore.OldRoot)) return null;
            var to = JournalMediaStore.Root + path[JournalMediaStore.OldRoot.Length..];
            var f = FileStore.Get(db, path);
            if (f == null || f.IsDir) return FileStore.Get(db, to) != null ? moved[path] = to : null;
            FileStore.EnsureDir(db, to[..to.LastIndexOf('/')]);
            if (FileStore.Get(db, to) != null) to = FileStore.FreeName(db, to);
            FileStore.MoveOrCopy(db, path, to, overwrite: false, copy: false);
            return moved[path] = to;
        }
        foreach (var journal in JournalStore.Journals(db)) JournalStore.RewriteMedia(db, journal, Map);
        Tidy(db);
        return moved.Count;
    }

    /// <summary>
    /// Leere Jahres- und Monatsordner unter „Tagebuch“ (2026/10) wegräumen, „Tagebuch“ selbst nur, wenn danach ganz leer -
    /// auch wenn gerade nichts verschoben wurde (etwa nachdem ein übrig gebliebenes Foto gelöscht wurde). Eigenes bleibt.
    /// </summary>
    private static void Tidy(UserDb db)
    {
        var root = JournalMediaStore.OldRoot;
        if (FileStore.Get(db, root) is not { IsDir: true }) return;
        static bool Year(string n) => n.Length == 4 && n.All(char.IsDigit);
        static bool Month(string n) => n.Length == 2 && n.All(char.IsDigit);
        var removed = false;
        foreach (var y in FileStore.Children(db, root).Where(f => f.IsDir && Year(f.Name)).ToList())
        {
            foreach (var m in FileStore.Children(db, y.Path).Where(f => f.IsDir && Month(f.Name)).ToList())
                if (!FileStore.Children(db, m.Path).Any()) removed |= FileStore.Delete(db, m.Path);
            if (!FileStore.Children(db, y.Path).Any()) removed |= FileStore.Delete(db, y.Path);
        }
        // Ein selbst angelegter leerer Ordner „Tagebuch“ bleibt - weg nur, wenn hier gerade Reste des Tagebuchs verschwunden sind
        if (removed && !FileStore.Children(db, root).Any()) FileStore.Delete(db, root);
    }
}
