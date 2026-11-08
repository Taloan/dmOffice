using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Große Vorschauen (2048 px) der Fotos in der Dateiablage vorab erzeugen - wie bei den Galerie-Ordnern im Storage Extender.
/// Sonst rechnet der Server sie erst beim Öffnen in der Großansicht (bei 20 MB großen JPEGs Sekunden). Neue Fotos bekommen
/// sie schon mit der kleinen (Thumbs.GetOriginal); das hier holt die bestehenden nach. Immer nur eins und mit Pause, damit
/// Anfragen aus der Oberfläche nicht dahinter warten. Nur was hier liegt: nichts dafür aus dem Storage Extender holen.
/// </summary>
public sealed class PreviewAhead(Spaces spaces, ILogger<PreviewAhead> log) : BackgroundService
{
    /// <summary>Gerade nicht zu machen (Inhalt nur im Storage Extender, Lesefehler): bis zum Neustart nicht wieder versuchen.</summary>
    private readonly HashSet<string> _skip = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            int made = 0;
            foreach (var space in spaces.All())
            {
                try
                {
                    var todo = space.Db.Query(
                        // Vorauswahl nach Endung bzw. Typ, sonst verstopften Dokumente ohne Vorschau die ersten 200
                        "SELECT f.path, f.mime, f.size FROM files f WHERE f.is_dir = 0 AND f.fileid > 0 AND f.size > 0 " +
                        "AND (f.mime LIKE 'image/%' OR lower(f.path) LIKE '%.jpg' OR lower(f.path) LIKE '%.jpeg' OR lower(f.path) LIKE '%.png' OR lower(f.path) LIKE '%.webp') AND NOT EXISTS " +
                        "(SELECT 1 FROM file_thumbs t WHERE t.fileid = f.fileid AND t.size = " + Thumbs.Large + " AND " + Thumbs.CurrentSql + ") " +
                        "ORDER BY f.modified DESC LIMIT 200",
                        r => (Path: r.GetString(0), Mime: r.IsDBNull(1) ? null : r.GetString(1), Size: r.GetInt64(2)))
                        .Where(x => Thumbs.KindOf(x.Path, x.Mime) == Thumbs.Kind.Image && Thumbs.Expected(x.Path, x.Mime, x.Size) && !_skip.Contains(space.User.Id + "|" + x.Path))
                        .Take(50).ToList();
                    foreach (var x in todo)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (FileStore.Get(space.Db, x.Path) is not { } f) continue;
                        using (BlobStore.WithoutFetch()) await Thumbs.Get(space.Db, f, Thumbs.Large, ct, original: true);
                        if (space.Db.Scalar("SELECT 1 FROM file_thumbs WHERE fileid = $f AND size = " + Thumbs.Large, ("$f", f.FileId)) == null)
                            _skip.Add(space.User.Id + "|" + x.Path);
                        else made++;
                        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { log.LogWarning("Große Vorschauen vorab {User}: {Error}", space.User.Login, e.Message); }
            }
            try { await Task.Delay(made > 0 ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
