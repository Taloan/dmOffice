using System.Collections.Concurrent;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Storage;

/// <summary>
/// Kopieren und Verschieben zwischen der eigenen Ablage und externen Ordnern (Server-Ordner, WebDAV, OneDrive, pCloud) und
/// zwischen zwei externen Ordnern. Läuft im Hintergrund mit Fortschritt (Aktivität „Dateien übertragen“) - ein Fotoordner kann
/// viele Gigabyte haben. Verschieben löscht erst, wenn alles angekommen ist: in der Ablage in den Papierkorb, extern endgültig.
/// </summary>
public static class Transfer
{
    /// <summary>Ort: Mount null oder leer = eigene Ablage, sonst die Kennung des externen Ordners.</summary>
    public sealed record Place(string? Mount, string Path);
    /// <param name="Conflict">Gleichnamiges im Ziel: null = nachfragen (409), "skip" = Ordner zusammenführen, vorhandene Dateien
    /// lassen; "replace" = zusammenführen, vorhandene Dateien ersetzen; "rename" = beides behalten (neuer Name „… (2)“).</param>
    public sealed record Body(string? FromMount, List<string> Paths, string? ToMount, string To, bool Move, string? Conflict = null);

    /// <param name="Modified">Änderungszeit an der Quelle - das Ziel bekommt sie auch (wie beim Kopieren im Explorer oder mit Syncthing).</param>
    private sealed record Item(string Rel, bool IsDir, long Size, DateTime? Modified = null);

    private static readonly ConcurrentDictionary<string, bool> Running = new();

    public static void Map(RouteGroupBuilder api)
    {
        // Vorschau für die Rückfrage: was übertragen wird (Dateien, Ordner, Bytes) und was im Ziel schon gleich heißt
        api.MapPost("/transfer/preview", async (Body body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            using var src = Side.Open(space, config, string.IsNullOrEmpty(body.FromMount) ? null : body.FromMount);
            using var dst = Side.Open(space, config, string.IsNullOrEmpty(body.ToMount) ? null : body.ToMount);
            var target = dst.Clean(body.To);
            long files = 0, dirs = 0, bytes = 0;
            var conflicts = new List<object>();
            foreach (var p in body.Paths)
            {
                var s = src.Clean(p);
                var name = s[(s.LastIndexOf('/') + 1)..];
                foreach (var i in await src.WalkAsync(s, ct)) { if (i.IsDir) dirs++; else { files++; bytes += i.Size; } }
                if (await dst.StatAsync(Join(target, name), ct) is { } there) conflicts.Add(new { name, folder = there.IsDir });
            }
            return Results.Ok(new { files, dirs, bytes, conflicts });
        });
        MapRun(api);
    }

    private static void MapRun(RouteGroupBuilder api) =>
        api.MapPost("/transfer", async (Body body, HttpContext http, ServerConfig config, ILoggerFactory logs, CancellationToken ct) =>
        {
            var space = http.Space();
            var from = string.IsNullOrEmpty(body.FromMount) ? null : body.FromMount;
            var to = string.IsNullOrEmpty(body.ToMount) ? null : body.ToMount;
            if (from == null && to == null) throw new ArgumentException("Innerhalb der Ablage bitte das normale Kopieren oder Verschieben nehmen.");
            if (from != null && from == to) throw new ArgumentException("Innerhalb eines externen Ordners bitte das normale Kopieren oder Verschieben nehmen.");
            if (body.Paths.Count == 0) throw new ArgumentException("Nichts ausgewählt.");

            // Vorab prüfen, was sofort scheitern würde: Schreibschutz, Systemordner, gleichnamiges Ziel
            using (var src = Side.Open(space, config, from))
            using (var dst = Side.Open(space, config, to))
            {
                if (dst.ReadOnly) return Results.Json(new { error = "Das Ziel ist nur zum Lesen eingebunden." }, statusCode: 403);
                if (body.Move && src.ReadOnly) return Results.Json(new { error = "Aus einem nur lesend eingebundenen Ordner lässt sich nur kopieren." }, statusCode: 403);
                var target = dst.Clean(body.To);
                if (!await dst.IsDirAsync(target, ct)) return Results.BadRequest(new { error = "Das Ziel ist kein Ordner." });
                foreach (var p in body.Paths)
                {
                    var s = src.Clean(p);
                    if (s.Length == 0) return Results.BadRequest(new { error = "Ein ganzer Ordner-Stamm lässt sich so nicht übertragen." });
                    if (await src.StatAsync(s, ct) == null) return Results.NotFound(new { error = $"„{p}“ gibt es nicht mehr." });
                    var name = s[(s.LastIndexOf('/') + 1)..];
                    if (body.Conflict is null && await dst.StatAsync(Join(target, name), ct) is { } there)
                        return Results.Json(new { error = $"Im Ziel gibt es „{name}“ schon.", conflict = true, name, folder = there.IsDir }, statusCode: 409);
                }
            }
            if (!Running.TryAdd(space.User.Id, true)) return Results.Conflict(new { error = "Es läuft schon eine Übertragung. Bitte warten, bis sie fertig ist." });

            var log = logs.CreateLogger("DmOffice.Storage.Transfer");
            _ = Task.Run(async () =>
            {
                using var job = Activity.Start(space.User.Id, Activity.FilesTransfer, body.Move ? "Verschieben" : "Kopieren");
                try { await RunAsync(space, config, from, to, body, job, CancellationToken.None); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    log.LogWarning("Übertragung für {User}: {Error}", space.User.Login, e.Message);
                    job.Fail(e is QuotaExceededException ? "Der Speicher ist voll." : e.Message);
                }
                finally { Running.TryRemove(space.User.Id, out _); }
            });
            return Results.Accepted(value: new { started = true });
        });

    private static async Task RunAsync(UserSpace space, ServerConfig config, string? from, string? to, Body body, Activity.Job job, CancellationToken ct)
    {
        using var src = Side.Open(space, config, from);
        using var dst = Side.Open(space, config, to);
        var target = dst.Clean(body.To);
        // Erst alles auflisten (für den Fortschritt in Bytes), dann übertragen
        var plan = new List<(string Src, string Dst, Item Item)>();
        foreach (var p in body.Paths)
        {
            var s = src.Clean(p);
            var name = s[(s.LastIndexOf('/') + 1)..];
            // „Beide behalten“: der Eintrag selbst bekommt einen freien Namen; sonst wird ein gleichnamiger Ordner zusammengeführt
            if (body.Conflict == "rename") name = await FreeName(dst, target, name, ct);
            foreach (var i in await src.WalkAsync(s, ct)) plan.Add((Join(s, i.Rel), Join(Join(target, name), i.Rel), i));
        }
        var total = plan.Sum(x => x.Item.Size);
        long done = 0;
        int skipped = 0;
        var moved = new List<string>();
        job.Progress(0, total, $"{plan.Count(x => !x.Item.IsDir)} Dateien");
        foreach (var (s, d, i) in plan)
        {
            var there = await dst.StatAsync(d, ct);
            if (i.IsDir)
            {
                if (there == null) await dst.MkdirAsync(d, ct);
                else if (!there.Value.IsDir) throw new IOException($"Im Ziel ist „{d}“ eine Datei, kein Ordner.");
                continue;
            }
            var dest = d;
            if (there != null)
            {
                if (there.Value.IsDir) throw new IOException($"Im Ziel ist „{d}“ ein Ordner, keine Datei.");
                if (body.Conflict == "skip") { skipped++; done += i.Size; continue; }
                if (body.Conflict == "rename") dest = Join(Parent(d), await FreeName(dst, Parent(d), Path.GetFileName(d), ct));
                // "replace": überschreiben
            }
            // Foto oder Video in einen Galerie-Ordner: der Inhalt ist hier ohnehin zur Hand - Vorschau und Bildangaben gleich daraus,
            // statt dass das Einlesen die Datei danach noch einmal vom Speicher holt (über das Netz zum NAS teuer)
            var gallery = dst.GalleryFor(dest);
            if (gallery != null && i.Size <= MaxPlaced)
            {
                byte[] bytes;
                await using (var stream = await src.OpenReadAsync(s, ct)) { var m = new MemoryStream((int)i.Size); await stream.CopyToAsync(m, ct); bytes = m.ToArray(); }
                await dst.WriteAsync(dest, new MemoryStream(bytes), ct, i.Modified);
                try { await dst.PlacedAsync(gallery.Value.Source, gallery.Value.Rel, bytes, ct); }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { /* dann holt das Einlesen es nach */ }
            }
            else
                await using (var stream = await src.OpenReadAsync(s, ct)) await dst.WriteAsync(dest, stream, ct, i.Modified);
            // Angekommen? Beim Verschieben wird die Quelle endgültig gelöscht - nur, wenn das Ziel die ganze Datei hat
            if (body.Move && (await dst.StatAsync(dest, ct) is not { IsDir: false, Size: var size } || size != i.Size))
                throw new IOException($"„{Path.GetFileName(d)}“ ist im Ziel nicht vollständig angekommen – an der Quelle bleibt sie.");
            moved.Add(s);
            done += i.Size;
            job.Progress(done, total, Path.GetFileName(d));
            // Galerie-Ordner im Ziel: alle 25 Dateien neu einlesen lassen - die Vorschauen entstehen so während der Übertragung
            if (moved.Count % 25 == 0) dst.Touched(Parent(dest));
        }
        if (skipped > 0) job.Progress(done, total, $"{skipped} übersprungen (gab es schon)");
        dst.Touched(target);
        if (!body.Move) return;
        // Verschieben: nur, was angekommen ist, an der Quelle löschen - Übersprungenes bleibt dort; danach leere Ordner (tiefste zuerst).
        // Endgültig, nicht in den Papierkorb: die Dateien sind ja nicht weg, sondern liegen im Ziel (wie beim Verschieben im Explorer)
        foreach (var s in moved) await src.DeleteAsync(s, ct, permanent: true);
        foreach (var (s, _, _) in plan.Where(x => x.Item.IsDir).OrderByDescending(x => x.Src.Length))
            if (await src.IsEmptyDirAsync(s, ct)) await src.DeleteAsync(s, ct, permanent: true);
        src.Touched(src.Clean(body.Paths[0]));
    }

    /// <summary>Bis zu dieser Größe wird eine Datei für die Vorschau im Speicher gehalten (Fotos, kurze Videos).</summary>
    private const long MaxPlaced = 200L * 1024 * 1024;

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    /// <summary>„Name (2).ext“, „Name (3).ext“ … - der erste, den es im Ordner noch nicht gibt.</summary>
    private static async Task<string> FreeName(Side side, string dir, string name, CancellationToken ct)
    {
        if (await side.StatAsync(Join(dir, name), ct) == null) return name;
        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 && ext.Length < name.Length ? name[..^ext.Length] : name;
        if (ext.Length == name.Length) ext = "";
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (await side.StatAsync(Join(dir, candidate), ct) == null) return candidate;
        }
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "/" + b;

    /// <summary>Eine Seite der Übertragung: die eigene Ablage oder ein externer Ordner (Treiber).</summary>
    private sealed class Side : IDisposable
    {
        private readonly UserSpace _space;
        private readonly IStorageDriver? _driver;
        private readonly string? _mount;
        private readonly ServerConfig _config;
        public bool ReadOnly { get; }

        private Side(UserSpace space, ServerConfig config, IStorageDriver? driver, string? mount, bool readOnly) { _space = space; _config = config; _driver = driver; _mount = mount; ReadOnly = readOnly; }

        public static Side Open(UserSpace space, ServerConfig config, string? mount)
        {
            if (mount == null)
            {
                if (FileStore.FilesOff(space.Db)) throw new UnauthorizedAccessException("Die Dateiablage ist für dieses Konto abgeschaltet.");
                return new Side(space, config, null, null, false);
            }
            var (d, ro) = ExternalFolders.Open(space, config, mount);
            return new Side(space, config, d, mount, ro);
        }

        public string Clean(string path)
        {
            if (_driver != null) return StoragePath.Clean(path);
            var p = FileStore.Normalize(path);
            // Systemordner (notizi, reservierter Ordner, Tresor) weder lesen noch beschreiben
            if (p.Length > 0 && FileStore.IsSystem(_space.Db, p)) throw new UnauthorizedAccessException("Dieser Ordner ist geschützt.");
            return p;
        }

        public async Task<(bool IsDir, long Size, DateTime? Modified)?> StatAsync(string path, CancellationToken ct)
        {
            if (_driver != null) return await _driver.StatAsync(path, ct) is { } e ? (e.IsDir, e.Size, e.Modified) : null;
            return FileStore.Get(_space.Db, path) is { } f ? (f.IsDir, f.Size, f.Modified) : null;
        }

        public async Task<bool> IsDirAsync(string path, CancellationToken ct) =>
            path.Length == 0 || (await StatAsync(path, ct))?.IsDir == true;

        /// <summary>Alles unter path, relativ dazu - "" ist path selbst. Ordner vor ihrem Inhalt.</summary>
        public async Task<List<Item>> WalkAsync(string path, CancellationToken ct)
        {
            var list = new List<Item>();
            var top = await StatAsync(path, ct) ?? throw new KeyNotFoundException("„" + path + "“ gibt es nicht mehr.");
            list.Add(new Item("", top.IsDir, top.IsDir ? 0 : top.Size, top.Modified));
            if (!top.IsDir) return list;
            var queue = new Queue<string>([""]);
            while (queue.Count > 0)
            {
                var rel = queue.Dequeue();
                var full = Join(path, rel);
                if (_driver != null)
                    foreach (var e in await _driver.ListAsync(full, ct))
                    {
                        if (e.Name.EndsWith(".dmoffice-part", StringComparison.Ordinal)) continue;
                        list.Add(new Item(Join(rel, e.Name), e.IsDir, e.IsDir ? 0 : e.Size, e.Modified));
                        if (e.IsDir) queue.Enqueue(Join(rel, e.Name));
                    }
                else
                    foreach (var f in FileStore.Children(_space.Db, full))
                    {
                        if (FileStore.IsSystem(_space.Db, f.Path)) continue;
                        list.Add(new Item(Join(rel, f.Name), f.IsDir, f.IsDir ? 0 : f.Size, f.Modified));
                        if (f.IsDir) queue.Enqueue(Join(rel, f.Name));
                    }
            }
            return list;
        }

        public async Task<bool> IsEmptyDirAsync(string path, CancellationToken ct)
        {
            if (_driver != null) return (await _driver.StatAsync(path, ct))?.IsDir == true && (await _driver.ListAsync(path, ct)).Count == 0;
            return FileStore.Get(_space.Db, path) is { IsDir: true } && FileStore.Children(_space.Db, path).Count == 0;
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
            _driver != null ? _driver.OpenReadAsync(path, ct) : Task.FromResult(FileStore.OpenRead(_space.Db, path));

        public async Task WriteAsync(string path, Stream data, CancellationToken ct, DateTime? modified = null)
        {
            if (_driver != null)
            {
                PCloudDriver.UploadTime.Value = modified;
                await _driver.WriteAsync(path, data, ct);
                // Zeit der Quelle übernehmen. Kann der Speicher es nicht, bleibt die Zeit des Hochladens - die Datei ist trotzdem da
                if (modified is { } m)
                    try { await _driver.SetModifiedAsync(path, m, ct); }
                    catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested) { }
                return;
            }
            if (FileStore.Parent(path) is { Length: > 0 } parent) FileStore.EnsureDir(_space.Db, parent);
            await FileStore.PutAsync(_space.Db, path, data, DavServer.Mime(path), mtime: modified, ct: ct);
        }

        public Task MkdirAsync(string path, CancellationToken ct)
        {
            if (_driver != null) return _driver.MkdirAsync(path, ct);
            FileStore.EnsureDir(_space.Db, path);
            return Task.CompletedTask;
        }

        /// <param name="permanent">Ablage: nicht in den Papierkorb (Verschieben - der Inhalt liegt im Ziel)</param>
        public Task DeleteAsync(string path, CancellationToken ct, bool permanent = false)
        {
            if (_driver != null) return _driver.DeleteAsync(path, ct);
            if (FileStore.Delete(_space.Db, path, out var trashId) && permanent && trashId > 0) FileStore.PurgeTrash(_space.Db, [trashId]);
            return Task.CompletedTask;
        }

        /// <summary>Galerie-Ordner, in dem dieser Pfad liegt (mit dem Pfad relativ zu ihm) - nur für Fotos und Videos.</summary>
        public (DmOffice.Gallery.GallerySource Source, string Rel)? GalleryFor(string path)
        {
            if (_mount == null || DmOffice.Gallery.GalleryFolders.KindOf(path) is null) return null;
            foreach (var g in DmOffice.Gallery.GalleryFolders.Of(_space.Db) ?? [])
            {
                if (g.Mount != _mount) continue;
                if (g.Path.Length == 0) return (g, path);
                if (FileStore.InRoot(path, g.Path) && path.Length > g.Path.Length) return (g, path[(g.Path.Length + 1)..]);
            }
            return null;
        }

        /// <summary>Gerade geschrieben: Eintrag, Bildangaben und Vorschau aus den Bytes (Größe und Zeit vom Speicher).</summary>
        public async Task PlacedAsync(DmOffice.Gallery.GallerySource g, string rel, byte[] bytes, CancellationToken ct)
        {
            var full = g.Path.Length == 0 ? rel : g.Path + "/" + rel;
            var e = await _driver!.StatAsync(full, ct);
            if (e == null) return;
            DmOffice.Gallery.GalleryFolders.Placed(_space, _config, g, rel, bytes, e.Size, e.Modified);
        }

        /// <summary>Externer Ordner: Liste und Suche neu einlesen lassen, Galerie-Ordner darin gleich einlesen (nicht erst beim
        /// nächsten regelmäßigen Durchgang - sonst fehlten die Vorschauen der übertragenen Fotos lange).</summary>
        public void Touched(string path)
        {
            if (_mount == null) return;
            ExternalFolders.Changed(_space, _mount, path);
            foreach (var g in DmOffice.Gallery.GalleryFolders.Of(_space.Db) ?? [])
                if (g.Mount == _mount && (g.Path.Length == 0 || FileStore.InRoot(path, g.Path) || FileStore.InRoot(g.Path, path)))
                    DmOffice.Gallery.GalleryScanJob.Request(_space.User.Id, g.Id);
        }

        public void Dispose() => _driver?.Dispose();
    }
}
