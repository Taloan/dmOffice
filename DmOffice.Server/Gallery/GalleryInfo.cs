using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;

namespace DmOffice.Gallery;

/// <summary>
/// Titel, Beschreibung (Bildunterschrift), Bewertung, Stichwörter und Farbe aus der Galerie in die Fotos schreiben - über
/// <see cref="PhotoMeta"/>: JPEG in die Datei, RAW in die Sidecar-Datei daneben. Der Index bekommt den neuen Stand sofort, die Datei
/// schreibt der Hintergrund. Die Markierung (P/X/U) bleibt wie bei Lightroom Classic nur in dmOffice (<see cref="PhotoMarks"/>).
/// </summary>
public static class GalleryInfo
{
    /// <summary>Was sich ändern soll; null = bleibt. Leerer Text und Bewertung 0 entfernen die Angabe.</summary>
    /// <summary>Pick: Markierung (1 ausgewählt, -1 abgelehnt, 0 ohne); Label: Farbe (red, yellow, green, blue, purple; leer = keine).</summary>
    public sealed record Body(List<string> Items, string? Title, string? Description, int? Rating, List<string>? AddKeywords = null, List<string>? RemoveKeywords = null,
        double? Lat = null, double? Lon = null, int? Pick = null, string? Label = null);
    public sealed record Failure(string Id, string Name, string Error);

    /// <summary>Je Anfrage höchstens so viele - die Web-Galerie schickt größere Auswahlen in Teilen (mit Fortschritt).</summary>
    public const int MaxItems = 50;

    public static async Task<List<Failure>> WriteAsync(UserSpace space, ServerConfig config, Body body, CancellationToken ct)
    {
        if (body.Items is not { Count: > 0 and <= MaxItems }) throw new ArgumentException($"Ein bis {MaxItems} Fotos je Anfrage.");
        // Ort von Hand (Atlas): ersetzt einen vorhandenen
        if ((body.Lat == null) != (body.Lon == null) || body.Lat is < -90 or > 90 || body.Lon is < -180 or > 180) throw new ArgumentException("Ungültige Koordinaten.");
        var fields = new PhotoMeta.Fields(body.Lat is { } la ? Math.Round(la, 6) : null, body.Lon is { } lo ? Math.Round(lo, 6) : null, body.Rating, body.Title?.Trim(),
            body.Description?.Trim(), body.AddKeywords, body.RemoveKeywords, body.Pick is int p ? Math.Sign(p) : null, body.Label?.Trim().ToLowerInvariant());
        if (fields.Label != null && !PhotoMeta.IsLabel(fields.Label)) throw new ArgumentException("Unbekannte Farbe.");
        if (!fields.HasXmpOnly && !fields.HasGps) throw new ArgumentException("Nichts zu ändern.");
        if ((body.AddKeywords?.Count ?? 0) + (body.RemoveKeywords?.Count ?? 0) > 200 || (body.AddKeywords ?? []).Any(k => k.Length > 200))
            throw new ArgumentException("Zu viele oder zu lange Stichwörter.");
        if (body.Rating is < 0 or > 5) throw new ArgumentException("Bewertung 0 bis 5.");
        if (body.Title?.Length > 500 || body.Description?.Length > 4000) throw new ArgumentException("Der Text ist zu lang.");
        var failed = new List<Failure>();
        var items = body.Items.Distinct().ToList();
        var db = space.Db;

        // Markierung (P/X/U) nur in dmOffice - wie Lightroom Classic, das sie nur im Katalog führt, nicht in Datei oder XMP
        if (fields.Pick != null)
            foreach (var id in items) PhotoMarks.Set(db, id, new PhotoMeta.Fields(Pick: fields.Pick), null);
        var rest = fields with { Pick = null };
        // Angaben nur in der Datenbank (Einstellungen → Galerie); der Ort geht weiter in die Datei (Verorten)
        if (!space.Settings.PhotoWriteXmp && rest.HasXmpOnly)
        {
            foreach (var id in items) PhotoMarks.Set(db, id, rest with { Lat = null, Lon = null }, null);
            rest = new PhotoMeta.Fields(rest.Lat, rest.Lon);
        }
        if (!rest.HasXmpOnly && !rest.HasGps) return failed;

        // Sofort in den Index (Galerie und Details zeigen es gleich), die Datei schreibt der Hintergrund nacheinander - ein großes
        // JPEG neu zu schreiben dauert Sekunden, schnell hintereinander gesetzte Angaben würden sich sonst gegenseitig überholen
        var write = new List<string>();
        foreach (var id in items)
        {
            var name = id[(id.LastIndexOf('/') + 1)..];
            try
            {
                if (!PhotoMeta.CanWrite(name)) throw new NotSupportedException("In dieses Dateiformat schreibt dmOffice nichts (nur JPEG und RAW).");
                if (!id.StartsWith("ext:", StringComparison.Ordinal) && (id == SharedFiles.Folder || id.StartsWith(SharedFiles.Folder + "/", StringComparison.Ordinal)))
                    throw new UnauthorizedAccessException("Fotos anderer lassen sich hier nicht ändern.");
                // RAW im Galerie-Ordner ohne XMP-Dateien: die Angaben stünden nirgends, wo die Galerie sie liest
                if (id.StartsWith("ext:", StringComparison.Ordinal) && PhotoMeta.UsesSidecar(name) && !GalleryFolders.Resolve(space, config, id[4..]).Source.Sidecars)
                    throw new InvalidOperationException("XMP-Dateien sind für diesen Galerie-Ordner ausgeschaltet - bei RAW-Fotos stehen die Angaben dort.");
                IndexNow(space, config, id, rest);
                write.Add(id);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add(new Failure(id, name, e.Message));
            }
        }
        if (write.Count > 0) Later(space, () => WriteFilesAsync(space, config, write, rest));
        return failed;
    }

    // ---------------- Index sofort, Dateien im Hintergrund ----------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> Chains = new();

    /// <summary>Was für diesen Benutzer noch in Dateien geschrieben wird (Tests warten darauf).</summary>
    public static Task Pending(string userId) => Chains.TryGetValue(userId, out var t) ? t : Task.CompletedTask;
    public static Task AllPending() => Task.WhenAll(Chains.Values);

    private static void Later(UserSpace space, Func<Task> work)
    {
        lock (Chains)
        {
            var prev = Chains.TryGetValue(space.User.Id, out var t) ? t : Task.CompletedTask;
            Chains[space.User.Id] = prev.ContinueWith(_ => work(), TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Neuen Stand gleich in den Index: eigene Fotos in file_meta, Galerie-Ordner in gallery_ext.</summary>
    private static void IndexNow(UserSpace space, ServerConfig config, string id, PhotoMeta.Fields f)
    {
        if (id.StartsWith("ext:", StringComparison.Ordinal))
        {
            var (src, rel) = GalleryFolders.Resolve(space, config, id[4..]);
            GalleryFolders.SetInfo(space.Db, src.Id, rel, f);
            if (f.HasGps) GalleryFolders.SetGps(space.Db, src.Id, rel, f.Lat, f.Lon);
            return;
        }
        var db = space.Db;
        FileMeta.Ensure(db);
        string? keywords = null;
        if (f.HasKeywords)
        {
            var old = db.Scalar("SELECT keywords FROM file_meta WHERE path = $p", ("$p", id)) as string;
            var list = f.KeywordsFrom((old ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
            keywords = list.Count > 0 ? string.Join("\n", list) : null;
        }
        db.Exec(@"UPDATE file_meta SET rating = CASE WHEN $sr THEN $r ELSE rating END, title = CASE WHEN $st THEN $t ELSE title END,
                description = CASE WHEN $sd THEN $d ELSE description END, keywords = CASE WHEN $sk THEN $k ELSE keywords END,
                label = CASE WHEN $sl THEN $l ELSE label END, lat = CASE WHEN $sg THEN $la ELSE lat END, lon = CASE WHEN $sg THEN $lo ELSE lon END
                WHERE path = $p",
            ("$sr", f.Rating != null), ("$r", f.Rating is > 0 ? f.Rating : null), ("$st", f.Title != null), ("$t", string.IsNullOrWhiteSpace(f.Title) ? null : f.Title.Trim()),
            ("$sd", f.Description != null), ("$d", string.IsNullOrWhiteSpace(f.Description) ? null : f.Description.Trim()), ("$sk", f.HasKeywords), ("$k", keywords),
            ("$sl", f.Label != null), ("$l", string.IsNullOrEmpty(f.Label) ? null : f.Label), ("$sg", f.HasGps), ("$la", f.Lat), ("$lo", f.Lon), ("$p", id));
    }

    private static async Task WriteFilesAsync(UserSpace space, ServerConfig config, List<string> ids, PhotoMeta.Fields fields)
    {
        var drivers = new Dictionary<string, (IStorageDriver Driver, bool ReadOnly)>();
        try
        {
            foreach (var id in ids)
            {
                var name = id[(id.LastIndexOf('/') + 1)..];
                try
                {
                    if (id.StartsWith("ext:", StringComparison.Ordinal)) await ExtAsync(space, config, drivers, id, fields, CancellationToken.None);
                    else Own(space, id, fields);
                }
                catch (Exception e)
                {
                    // In der Aktivität sichtbar; der Index zeigt den neuen Stand schon, die Datei hat ihn nicht
                    Activity.Start(space.User.Id, Activity.PhotoWrite, name).Fail(name + ": " + (e is Pim.PreconditionFailedException ? "Die Datei wurde inzwischen geändert." : e.Message));
                }
            }
        }
        finally { foreach (var d in drivers.Values) d.Driver.Dispose(); }
    }

    /// <summary>Eigenes Foto in der Ablage (mit ETag: hat es sich inzwischen geändert, wird nichts überschrieben).</summary>
    private static void Own(UserSpace space, string path, PhotoMeta.Fields fields)
    {
        var db = space.Db;
        if (path == SharedFiles.Folder || path.StartsWith(SharedFiles.Folder + "/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Fotos anderer lassen sich hier nicht ändern.");
        var f = FileStore.Get(db, path) is { IsDir: false } x ? x : throw new KeyNotFoundException("Die Datei gibt es nicht mehr.");
        if (PhotoMeta.IsJpeg(f.Name))
        {
            var data = FileStore.Data(db, f.Path) ?? throw new KeyNotFoundException("Die Datei gibt es nicht mehr.");
            FileStore.Put(db, f.Path, PhotoMeta.WriteJpeg(data, fields), f.Mime, ifMatch: f.Etag);
        }
        else
        {
            var sc = FileMeta.SidecarOf(db, f.Path);
            var old = sc != null ? FileStore.Get(db, sc) : null;
            if (old is { Size: > Sidecar.MaxBytes }) throw new IOException("Die Sidecar-Datei ist ungewöhnlich groß.");
            var text = old != null && FileStore.Data(db, old.Path) is { } b ? Encoding.UTF8.GetString(b) : null;
            var dir = FileStore.Parent(f.Path) ?? "";
            var target = sc ?? (dir.Length == 0 ? "" : dir + "/") + Path.GetFileNameWithoutExtension(f.Name) + ".xmp";
            FileStore.Put(db, target, Encoding.UTF8.GetBytes(PhotoMeta.WriteXmp(text, fields, packet: text == null)), "application/rdf+xml",
                ifMatch: old?.Etag, ifNoneMatch: old == null ? "*" : null);
        }
        FileMeta.Refresh(space, f.Path);
    }

    /// <summary>
    /// Foto im Galerie-Ordner. Gibt es eine Sidecar-Datei, die die Galerie liest (RAW immer, JPEG wenn eine da ist), kommt alles dort
    /// hinein - sonst ins JPEG selbst (im Hintergrund geschrieben wie aus dem Editor).
    /// </summary>
    private static async Task ExtAsync(UserSpace space, ServerConfig config, Dictionary<string, (IStorageDriver Driver, bool ReadOnly)> drivers,
        string id, PhotoMeta.Fields fields, CancellationToken ct)
    {
        var (src, rel) = GalleryFolders.Resolve(space, config, id[4..]);
        if (!drivers.TryGetValue(src.Id, out var o)) drivers[src.Id] = o = ExternalFolders.Open(space, config, src.Mount);
        if (o.ReadOnly) throw new UnauthorizedAccessException("Dieser Ordner ist nur zum Lesen eingebunden.");
        var driver = o.Driver;
        var dir = StoragePath.Parent(rel);
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        var raw = PhotoMeta.UsesSidecar(name);
        if (raw && !src.Sidecars) throw new InvalidOperationException("XMP-Dateien sind für diesen Galerie-Ordner ausgeschaltet - bei RAW-Fotos stehen die Angaben dort.");

        var entries = await driver.ListAsync(dir, ct);
        var names = entries.Where(e => !e.IsDir).Select(e => e.Name).ToList();
        var have = src.Sidecars ? Sidecar.For(name, names.Where(Sidecar.Is).GroupBy(Sidecar.Key).ToDictionary(g => g.Key, g => g.First())) : null;
        if (raw || have != null)
        {
            var scName = have ?? PhotoMeta.SidecarName(name, names);
            var path = StoragePath.Join(dir, scName);
            string? text = null;
            if (entries.FirstOrDefault(e => !e.IsDir && e.Name == scName) is { } old)
            {
                if (old.Size > Sidecar.MaxBytes) throw new IOException("Die Sidecar-Datei ist ungewöhnlich groß.");
                await using var s = await driver.OpenReadAsync(path, ct);
                using var r = new StreamReader(s, Encoding.UTF8);
                text = await r.ReadToEndAsync(ct);
            }
            var result = Encoding.UTF8.GetBytes(PhotoMeta.WriteXmp(text, fields, packet: text == null));
            await driver.WriteAsync(path, new MemoryStream(result), ct);
            // Mit dem neuen Stand der Sidecar-Datei eintragen - das nächste Einlesen überspringt sie dann
            var st = await driver.StatAsync(path, ct);
            var stamp = st is { IsDir: false } && st.Size == result.Length ? GalleryFolders.SidecarStamp(st with { Name = scName }) : -1;
            GalleryFolders.ApplySidecar(space.Db, src.Id, rel, Sidecar.Read(result), stamp);
            return;
        }

        var data = await PhotoWork.GetAsync(space, config, id, ct);
        var jpeg = PhotoMeta.WriteJpeg(data, fields);
        if (PhotoWork.Ready) PhotoWork.WriteLater(space, src.Id, rel, rel, jpeg);
        else
        {
            await driver.WriteAsync(rel, new MemoryStream(jpeg), ct);
            var st = await driver.StatAsync(rel, ct);
            GalleryFolders.Written(space, config, src, rel, jpeg, st is { IsDir: false } s1 && s1.Size == jpeg.Length ? s1.Modified : DateTime.UtcNow);
        }
        GalleryFolders.SetInfo(space.Db, src.Id, rel, fields);
        if (fields.HasGps) GalleryFolders.SetGps(space.Db, src.Id, rel, fields.Lat, fields.Lon);
    }
}
