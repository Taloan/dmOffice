using System.Collections.Concurrent;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;

namespace DmOffice.Gallery;

/// <summary>
/// Galerie-Ordner neu zuordnen: derselbe Galerie-Ordner (gleiche Kennung) zeigt auf einen anderen externen Ordner oder Pfad -
/// etwa wenn dieselben Fotos statt über eine CIFS-Freigabe jetzt über SFTP kommen. Index, Bildangaben, Sterne, Farben, Markierungen,
/// Alben, Orte, Bearbeitungen, Gesichter und Vorschauen bleiben; nur die Pfade werden umgerechnet (sie stehen relativ zum externen
/// Ordner). Danach sucht ein Abgleich im Hintergrund jede Datei am neuen Ort; was dort fehlt, geht aus dem Index.
/// </summary>
public static class GalleryRelink
{
    private static readonly ConcurrentDictionary<string, bool> Busy = new();

    /// <summary>Wird dieser Ordner gerade umgestellt? Einlesen und Vorschauen warten dann.</summary>
    public static bool IsBusy(string userId, string sourceId) => Busy.ContainsKey(userId + "/" + sourceId);

    /// <param name="Checked">Dateien der Stichprobe</param>
    /// <param name="Found">davon am neuen Ort mit gleichem Pfad und gleicher Größe</param>
    /// <param name="Total">Dateien im Index</param>
    public sealed record Probe(int Checked, int Found, int Total);

    /// <summary>Was gegen das Ziel spricht, sonst null.</summary>
    public static string? Problem(UserSpace space, ServerConfig config, GallerySource src, string mount, string path)
    {
        if (!ExternalFolders.MountsOf(space, config).Any(m => m.Id == mount)) return "Diesen externen Ordner gibt es nicht.";
        if (src.Mount == mount && src.Path == path) return "Der externe Galerie-Ordner zeigt schon auf diesen Ordner.";
        if (GalleryFolders.Of(space.Db).Any(s => s.Id != src.Id && s.Mount == mount && s.Path == path)) return "Dieser Ordner ist schon in der Galerie.";
        return null;
    }

    /// <summary>Stichprobe aus dem Index (höchstens <see cref="FolderRelink.SampleMax"/>): liegen die Dateien am neuen Ort?</summary>
    public static async Task<Probe> ProbeAsync(UserSpace space, ServerConfig config, GallerySource src, string mount, string path, CancellationToken ct)
    {
        var db = space.Db;
        GalleryFolders.Ensure(db);
        var total = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM gallery_ext WHERE source = $s", ("$s", src.Id)) ?? 0L);
        var sample = db.Query($"SELECT path, size FROM gallery_ext WHERE source = $s ORDER BY random() LIMIT {FolderRelink.SampleMax}",
                r => (Path: FolderRelink.Map(src.Path, path, r.GetString(0)), Size: r.GetInt64(1)), ("$s", src.Id))
            .Where(x => x.Path != null).Select(x => (x.Path!, x.Size)).ToList();
        var (driver, _) = ExternalFolders.Open(space, config, mount);
        using (driver)
        {
            if (path.Length > 0 && await driver.StatAsync(path, ct) is not { IsDir: true }) throw new ArgumentException("Diesen Ordner gibt es nicht.");
            var (n, found) = await FolderRelink.ProbeAsync(driver, sample, ct);
            return new Probe(n, found, total);
        }
    }

    /// <summary>
    /// Umstellen. Gibt einen Fehlertext zurück oder null. Danach gleicht der Hintergrund ab (<see cref="CheckAsync"/>).
    /// <paramref name="albums"/>: Alben (Einträge „x:Quelle/Pfad“), in Tests auch ohne.
    /// </summary>
    public static string? Apply(UserSpace space, ServerConfig config, AlbumStore? albums, string id, string mount, string path)
    {
        var user = space.User.Id;
        var key = user + "/" + id;
        var db = space.Db;
        // Erst eintragen, dann nachsehen (wie das Einlesen umgekehrt): so beginnt kein Durchgang mit dem alten Ort
        Busy[key] = true;
        try
        {
            if (GalleryScanJob.IsBusy(user, id)) return "Der Ordner wird gerade eingelesen. Bitte gleich noch einmal versuchen.";
            var list = GalleryFolders.Of(db);
            if (list.FirstOrDefault(s => s.Id == id) is not { } src) throw new KeyNotFoundException("Nicht gefunden.");
            if (Problem(space, config, src, mount, path) is { } problem) return problem;
            GalleryFolders.Ensure(db);
            PhotoMarks.Ensure(db);
            PhotoEdits.Ensure(db);
            var (oldMount, oldRoot) = (src.Mount, src.Path);
            var rows = db.Query("SELECT path, size, modified, thumb IS NOT NULL AND length(thumb) = 0 FROM gallery_ext WHERE source = $s",
                r => (Path: r.GetString(0), Stamp: GalleryProxies.Stamp(r.GetInt64(1), r.GetInt64(2)), InShared: r.GetBoolean(3)), ("$s", id));
            var pairs = rows.Select(r => (From: r.Path, To: FolderRelink.Map(oldRoot, path, r.Path))).Where(x => x.To != null).Select(x => (x.From, To: x.To!)).ToList();
            var newPath = pairs.ToDictionary(x => x.From, x => x.To, StringComparer.Ordinal);

            // Kleine Vorschaubilder und Bildangaben: gemeinsamer Speicher (Server-Ordner) oder in der Zeile (eigene Ordner)
            var oldServer = GalleryFolders.SharedMount(config, oldMount);
            var newServer = GalleryFolders.SharedMount(config, mount);
            // Server-Ordner, auf den die Person keinen Zugriff mehr hat: aus seinem gemeinsamen Bestand wird nichts übernommen,
            // Vorschaubilder und Bildangaben entstehen aus den Dateien am neuen Ort
            var oldDenied = oldServer && !ExternalFolders.MountsOf(space, config).Any(m => m.Id == oldMount);
            var oldShared = oldServer && !oldDenied;
            var shared = GalleryFolders.Shared;
            Dictionary<string, byte[]>? pulled = null;
            if (shared != null && oldShared && newServer) shared.CopyAll(oldMount, mount, pairs);
            else if (shared != null && oldShared) pulled = shared.ThumbsOf(oldMount, rows.Where(r => r.InShared).Select(r => (r.Path, r.Stamp)));

            // Große Vorschauen: gemeinsam je Server-Ordner oder je Benutzer und Galerie-Ordner - umziehen statt neu rechnen
            GalleryProxies?[] stores = [GalleryFolders.Proxies,
                GalleryFolders.Medium, // [feature:galmedium]
            ];
            foreach (var proxies in stores.OfType<GalleryProxies>())
            {
                // Vom eigenen Ordner auf einen Server-Ordner: die eigenen Vorschauen stammen nicht aus dem Server-Ordner, sie gehen nicht
                // in den gemeinsamen Bestand (dort entstehen sie aus den Dateien selbst)
                if ((!oldServer || oldDenied) && newServer) { proxies.RemoveSource(user, id); continue; }
                var from = oldShared ? (A: GalleryProxies.SharedScope, B: oldMount) : (A: user, B: id);
                var to = newServer ? (A: GalleryProxies.SharedScope, B: mount) : (A: user, B: id);
                // Gleicher Bereich: über einen Zwischenbereich, damit ein neuer Pfad keine noch nicht umgezogene Vorschau überschreibt
                var via = from == to ? (A: user, B: id + "~relink") : to;
                foreach (var (a, b) in pairs)
                {
                    // Gemeinsame Vorschauen braucht vielleicht noch jemand: kopieren; eigene verschieben. Ältere je Benutzer dazu.
                    if (!proxies.Relocate(from.A, from.B, a, via.A, via.B, b, move: !oldShared, keep: newServer) && oldShared)
                        proxies.Relocate(user, id, a, via.A, via.B, b, move: true, keep: newServer);
                }
                if (via != to)
                {
                    foreach (var (_, b) in pairs) proxies.Relocate(via.A, via.B, b, to.A, to.B, b, move: true, keep: newServer);
                    proxies.RemoveSource(via.A, via.B);
                }
            }

            using (var conn = db.Open())
            using (var tx = conn.BeginTransaction())
            {
                var (f, t) = (FolderRelink.Prefix(oldRoot), FolderRelink.Prefix(path));
                // Was nicht unter dem alten Ordner liegt (gibt es nicht), käme nie wieder vor
                if (f.Length > 0) UserDb.Exec(conn, "DELETE FROM gallery_ext WHERE source = $s AND substr(path, 1, length($f)) <> $f", ("$s", id), ("$f", f));
                FolderRelink.RenameKeys(conn, "gallery_ext", "path", f, t, "source = $s", ("$s", id));
                FolderRelink.RenameKeys(conn, "gallery_ext_gps", "path", f, t, "source = $s", ("$s", id));
                if (oldDenied)
                    UserDb.Exec(conn, "UPDATE gallery_ext SET thumb = NULL, stage = 0 WHERE source = $s AND thumb IS NOT NULL AND length(thumb) = 0", ("$s", id));
                if (pulled != null)
                {
                    foreach (var (p, thumb) in pulled)
                        UserDb.Exec(conn, "UPDATE gallery_ext SET thumb = $t WHERE source = $s AND path = $p", ("$t", thumb), ("$s", id), ("$p", newPath[p]));
                    // Im gemeinsamen Speicher fehlte es: neu lesen
                    UserDb.Exec(conn, "UPDATE gallery_ext SET thumb = NULL, stage = 0 WHERE source = $s AND thumb IS NOT NULL AND length(thumb) = 0", ("$s", id));
                }
                FolderRelink.RenameKeys(conn, "photo_marks", "key", "ext:" + id + "/" + f, "ext:" + id + "/" + t);
                FolderRelink.RenameKeys(conn, "photo_edits", "key", PhotoEdits.KeyOfExt(id + "/" + f), PhotoEdits.KeyOfExt(id + "/" + t));
                if (FolderRelink.HasTable(conn, "gallery_imagehash")) FolderRelink.RenameKeys(conn, "gallery_imagehash", "id", "ext:" + id + "/" + f, "ext:" + id + "/" + t);
                UserDb.Exec(conn, "UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1");
                tx.Commit();
                albums?.RenameRefs(user, "x:" + id + "/" + f, "x:" + id + "/" + t);
            }

            src.Mount = mount;
            src.Path = path;
            src.Error = null;
            src.Relink = new RelinkState { At = DateTime.UtcNow, Pending = true, Total = rows.Count };
            GalleryFolders.Save(db, list);
            // Neu prüfen. Vom eigenen Ordner auf einen Server-Ordner: die kleinen Vorschaubilder und Bildangaben bleiben in den eigenen
            // Zeilen und gehen nicht in den gemeinsamen Bestand - der füllt sich aus den Dateien des Server-Ordners
            var ownToServer = !oldServer && newServer;
            db.Set("gallery.seed." + id, ownToServer ? "done" : null);
            db.Set(GalleryFolders.CheckedKey(id), ownToServer ? "1" : null);
            GalleryList.Forget(user);
            GalleryTimeline.Forget(user);
            GalleryScanJob.Request(user, id);
            return null;
        }
        finally { Busy.TryRemove(key, out _); }
    }

    /// <summary>Name des Ordners für die Aktivität: externer Ordner › Pfad.</summary>
    private static string Label(UserSpace space, ServerConfig config, GallerySource src) =>
        (ExternalFolders.MountsOf(space, config).FirstOrDefault(m => m.Id == src.Mount)?.Name ?? src.Mount) + (src.Path.Length > 0 ? " › " + src.Path.Replace("/", " › ") : "");

    /// <summary>
    /// Abgleich nach dem Umstellen: jede Datei aus dem Index am neuen Ort suchen. Liegt sie dort, bleibt alles; sonst geht sie aus
    /// dem Index. In der Aktivität steht der Fortschritt, am Ende die Liste der entfernten Dateien. True, wenn er fertig ist.
    /// </summary>
    public static async Task<bool> CheckAsync(UserSpace space, ServerConfig config, GallerySource src, ILogger log, CancellationToken ct)
    {
        var db = space.Db;
        GalleryFolders.Ensure(db);
        var paths = db.Query("SELECT path FROM gallery_ext WHERE source = $s ORDER BY path", r => r.GetString(0), ("$s", src.Id));
        var label = Label(space, config, src);
        var total = paths.Count;
        using var job = Activity.Start(space.User.Id, Activity.GalleryRelink, $"Abgleich {label}", total);
        List<string> missing;
        try
        {
            var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
            using (driver)
                missing = await FolderRelink.MissingAsync(driver, src.Path, paths,
                    (done, found) => job.Progress(done, total, $"Abgleich {label}: {DmOffice.Core.Num.N(found)} von {DmOffice.Core.Num.N(total)} am neuen Ziel gefunden"),
                    () => GalleryFolders.Of(db).FirstOrDefault(x => x.Id == src.Id) is { } c && c.Mount == src.Mount && c.Path == src.Path, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }   // entfernt oder wieder neu zugeordnet
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or ArgumentException)
        {
            Update(db, src, s => s.Error = e.Message);
            job.Fail($"{label}: {e.Message}");
            log.LogWarning("Galerie-Ordner {User}/{Source}: Abgleich nach dem Neuzuordnen: {Error}", space.User.Login, src.Id, e.Message);
            return false;
        }
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            foreach (var p in missing) UserDb.Exec(conn, "DELETE FROM gallery_ext WHERE source = $s AND path = $p", ("$s", src.Id), ("$p", p));
            tx.Commit();
        }
        foreach (var p in missing)
            if (GalleryFolders.SharedMount(config, src.Mount)) GalleryFolders.ForgetShared(src.Mount, p);
            else GalleryFolders.DropLarge(space.User.Id, src.Id, p);
        var found = total - missing.Count;
        Update(db, src, s => { s.Pending = false; s.Error = null; s.Total = total; s.Found = found; s.RemovedCount = missing.Count; s.Removed = missing.Take(FolderRelink.ReportMax).ToList(); });
        if (missing.Count > 0) GalleryList.Forget(space.User.Id);
        job.Finish($"Abgleich {label}: {DmOffice.Core.Num.N(found)} von {DmOffice.Core.Num.N(total)} am neuen Ziel gefunden" + (missing.Count > 0 ? $", {DmOffice.Core.Num.N(missing.Count)} aus dem Index entfernt" : ""),
            missing.Take(FolderRelink.ReportMax).ToList());
        log.LogInformation("Galerie-Ordner {User}/{Source}: Abgleich nach dem Neuzuordnen, {Found} von {Total} gefunden, {Missing} entfernt", space.User.Login, src.Id, found, total, missing.Count);
        return true;
    }

    /// <summary>Stand des Abgleichs am Ordner speichern - nur, wenn er noch auf denselben Ort zeigt.</summary>
    private static void Update(UserDb db, GallerySource src, Action<RelinkState> change)
    {
        var list = GalleryFolders.Of(db);
        if (list.FirstOrDefault(x => x.Id == src.Id) is not { } cur || cur.Mount != src.Mount || cur.Path != src.Path) return;
        cur.Relink ??= new RelinkState { At = DateTime.UtcNow };
        change(cur.Relink);
        GalleryFolders.Save(db, list);
    }
}
