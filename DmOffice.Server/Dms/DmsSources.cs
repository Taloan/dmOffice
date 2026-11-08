using DmOffice.Core;

namespace DmOffice.Dms;

/// <summary>Eine Quelle: ein überwachter Ordner in der eigenen Ablage oder in einem externen Ordner.</summary>
public sealed class DmsSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>Externer Ordner (ExternalFolders); leer = eigene Ablage.</summary>
    public string Mount { get; set; } = "";
    /// <summary>Ordner darin, mit „/“ getrennt; immer mit Unterordnern.</summary>
    public string Folder { get; set; } = "";
    public string? Profile { get; set; }
    public string? Correspondent { get; set; }
    public string? Doctype { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>Alle so viele Minuten prüfen.</summary>
    public int Every { get; set; } = 5;
    public bool Active { get; set; } = true;
    public DateTime? LastRun { get; set; }
    public string? Error { get; set; }
    /// <summary>Beim letzten Lauf übernommen, davon schon vorhanden, insgesamt übernommen.</summary>
    public int LastCount { get; set; }
    public int LastDuplicates { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// Überwachte Ordner vom DMS (docs/dms-konzept.md, „Quellen“), Muster: Importprofile der Galerie. Gelesen wird aus der eigenen Ablage
/// oder einem externen Ordner, übernommen aus der eigenen Ablage sofort, aus externen Ordnern erst nach <see cref="Settle"/> ohne
/// Änderung; danach wird die Datei in der Quelle entfernt
/// (eigene Ablage: Papierkorb). Bei externen Ordnern nur zum Lesen merkt sich die Quelle, was schon übernommen ist.
/// </summary>
public static class DmsSources
{
    public const string Key = "dms.sources";
    public static TimeSpan Settle { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Eigene Ablage: eine Datei erscheint erst, wenn sie ganz gespeichert ist - kein Warten nötig.</summary>
    public static TimeSpan SettleOwn { get; set; } = TimeSpan.Zero;
    /// <summary>Je Lauf höchstens so viele Dateien - der Rest folgt im nächsten.</summary>
    public static int MaxPerRun { get; set; } = 50;

    private static readonly object SaveLock = new();

    public static List<DmsSource> Of(UserDb db) => db.GetJson<List<DmsSource>>(Key);

    internal static void Update(UserDb db, string id, Action<DmsSource> change)
    {
        lock (SaveLock)
        {
            var list = Of(db);
            if (list.FirstOrDefault(s => s.Id == id) is not { } s) return;
            change(s);
            db.SetJson(Key, list);
        }
    }

    private static bool SameTarget(string? mount, DmsProfile p) =>
        string.IsNullOrEmpty(mount) ? p.Target == "files" : p.Target == "external" && p.Mount == mount;

    private static bool Within(string path, string root) =>
        root.Length == 0 || path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Liegen Quellordner und Zielordner des Profils am selben Ort ineinander?</summary>
    public static bool Overlap(string? mount, string folder, DmsProfile p) =>
        SameTarget(mount, p) && (Within(folder, p.Folder) || Within(p.Folder, folder));

    public static DmsSource Save(UserSpace space, DmsSource s, IDmsSources sources)
    {
        s.Name = (s.Name ?? "").Trim();
        if (s.Name.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        s.Mount ??= "";
        s.Folder = s.Mount.Length == 0 ? Dav.FileStore.Normalize(s.Folder ?? "") : Storage.StoragePath.Clean(s.Folder);
        if (s.Mount.Length == 0 && s.Folder.Length == 0) throw new ArgumentException("Bitte einen Ordner wählen.");
        if (s.Mount.Length > 0 && sources.Mounts(space).All(m => m.Id != s.Mount)) throw new KeyNotFoundException("Diesen externen Ordner gibt es nicht.");
        var profile = DmsFiling.Find(space, s.Profile) ?? DmsFiling.Default(space);
        s.Profile = profile.Id;
        if (s.Mount.Length == 0 && Dav.FileStore.IsSystem(space.Db, s.Folder)) throw new ArgumentException("Aus diesem Ordner kann das DMS nichts übernehmen.");
        // Quellen lesen nur eigene Ordner; geteilte liegen nicht dort
        if (s.Mount.Length == 0 && Dav.FileStore.Get(space.Db, s.Folder) is not { IsDir: true }) throw new ArgumentException("Diesen Ordner gibt es in deiner Ablage nicht.");
        // Ein Ablageordner im Quellordner übernähme die abgelegten Dokumente immer wieder und nähme sie dort weg
        if (DmsFiling.Of(space).Any(p => Overlap(s.Mount, s.Folder, p)))
            throw new ArgumentException("Quellordner und Zielordner des Ablageprofils dürfen nicht ineinander liegen.");
        s.Every = Math.Clamp(s.Every, 1, 24 * 60);
        lock (SaveLock)
        {
            var list = Of(space.Db);
            var i = list.FindIndex(x => x.Id == s.Id);
            if (i >= 0)
            {
                var old = list[i];
                (s.LastRun, s.Error, s.LastCount, s.LastDuplicates, s.Total) = (old.LastRun, old.Error, old.LastCount, old.LastDuplicates, old.Total);
                list[i] = s;
            }
            else list.Add(s);
            space.Db.SetJson(Key, list);
        }
        Dav.FolderWatch.Forget(space.Db.UserId);
        return s;
    }

    public static void Delete(UserDb db, string id)
    {
        lock (SaveLock)
        {
            var list = Of(db);
            if (list.RemoveAll(s => s.Id == id) == 0) throw new KeyNotFoundException("Quelle nicht gefunden.");
            db.SetJson(Key, list);
        }
        Dav.FolderWatch.Forget(db.UserId);
        Dms.Ensure(db);
        db.Exec("DELETE FROM dms_seen WHERE source = $s", ("$s", id));
    }

    private static string FileError(Exception e) =>
        e is IOException or ArgumentException or KeyNotFoundException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
            or Dav.DavForbiddenException or Dav.QuotaExceededException or Dav.DavConflictException
            ? e.Message : Logged(e, "Die Datei ließ sich nicht übernehmen.");

    private static string Logged(Exception e, string message)
    {
        SystemLog.Add(Microsoft.Extensions.Logging.LogLevel.Warning, "DMS", $"Quelle: {e.GetType().Name}: {e.Message}");
        return message;
    }

    /// <summary>Ein Lauf einer Quelle. Gibt (übernommen, schon vorhanden) zurück.</summary>
    public static async Task<(int Done, int Duplicates)> RunAsync(UserSpace space, DmsHost host, string id, CancellationToken ct)
    {
        var db = space.Db;
        Dms.Ensure(db);
        if (Of(db).FirstOrDefault(x => x.Id == id) is not { } s) return (0, 0);
        var now = DateTime.UtcNow;
        int done = 0, dup = 0;
        string? error = null;
        try
        {
            var mount = s.Mount.Length == 0 ? null : s.Mount;
            if (mount == null && Dav.FileStore.Get(db, s.Folder) is not { IsDir: true }) throw new KeyNotFoundException("Ordner fehlt.");
            var (files, readOnly) = await host.ListAsync(space, mount, s.Folder, ct);
            var seen = readOnly
                ? db.Query("SELECT path, etag FROM dms_seen WHERE source = $s", r => (r.GetString(0), r.GetString(1)), ("$s", s.Id)).ToDictionary(x => x.Item1, x => x.Item2)
                : [];
            var profile = DmsFiling.Find(space, s.Profile) ?? DmsFiling.Default(space);
            var presets = new DmsAssign.Presets(s.Correspondent, s.Doctype, s.Tags);
            var targets = DmsFiling.Of(space).Where(p => SameTarget(mount, p)).Select(p => p.Folder).ToList();
            var settle = mount == null ? TimeSpan.FromTicks(Math.Min(Settle.Ticks, SettleOwn.Ticks)) : Settle;
            foreach (var f in files.Where(f => now - f.Modified >= settle && !(seen.TryGetValue(f.Path, out var e) && e == f.Etag))
                         .OrderBy(f => f.Modified).Take(MaxPerRun))
            {
                ct.ThrowIfCancellationRequested();
                // Teilweise hochgeladene Dateien und Ablage-Ordner nie übernehmen
                if (f.Name.EndsWith(".dmoffice-part") || f.Name.StartsWith('.')) continue;
                if (targets.Any(t => Within(f.Path, t)) || Dms.IsDocAt(db, mount, f.Path) || (mount == null && host.Protected(space, f.Path))) continue;
                try
                {
                    DmsImport.Result r;
                    await using (var data = await host.OpenReadAsync(space, mount, f.Path, ct))
                        r = await DmsImport.ImportAsync(space, data, f.Name, Dav.DavServer.Mime(f.Name),
                            new DmsImport.Info("folder", s.Id, Profile: profile.Id, Presets: presets, FileDate: f.Modified, Freed: mount == null && !readOnly ? f.Size : 0), ct);
                    if (r.Duplicate) dup++; else done++;
                    if (readOnly)
                        db.Exec("INSERT OR REPLACE INTO dms_seen(source, path, etag) VALUES($s, $p, $e)", ("$s", s.Id), ("$p", f.Path), ("$e", f.Etag));
                    else await host.DeleteAsync(space, mount, f.Path, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                // Jeder Fehler steht an der Quelle; die übrigen Dateien, Quellen und die Warteschlange laufen weiter
                catch (Exception e)
                {
                    error = $"{f.Name}: {FileError(e)}";
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            error = e is KeyNotFoundException ? "Den Ordner gibt es nicht mehr."
                : e is IOException or UnauthorizedAccessException or HttpRequestException ? "Der Ordner ist gerade nicht erreichbar."
                : Logged(e, "Diese Quelle ließ sich nicht prüfen.");
        }
        Update(db, s.Id, x => { x.LastRun = now; x.LastCount = done; x.LastDuplicates = dup; x.Total += done; x.Error = error; });
        return (done, dup);
    }
}
