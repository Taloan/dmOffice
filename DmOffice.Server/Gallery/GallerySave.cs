using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;

namespace DmOffice.Gallery;

/// <summary>
/// „In Ablage sichern“ (Schalter ablagesave): Fotos und Videos aus Galerie-Ordnern als Kopie in die Ablage, nach den Vorgaben der
/// Person (Einstellungen › Galerie): Zielordner (Vorgabe „Uploads“), darunter Herkunft/Jahr/Monat oder eine andere Ordnung.
/// Die Quelle bleibt unverändert. Was schon in der Ablage liegt - gleiche Datei oder dasselbe Bild mit anderen Bildangaben - wird
/// übersprungen. Läuft im Hintergrund (Aktivität „In Ablage sichern“); App und Web rufen denselben Befehl auf.
/// </summary>
public static class GallerySave
{
    /// <summary>Ordnung unter dem Zielordner.</summary>
    public const string SourceMonth = "source-month", Month = "month", Day = "day", Structure = "structure";
    public static readonly string[] Orders = [SourceMonth, Month, Day, Structure];
    public const string DefaultFolder = "Uploads";

    public record SettingsBody(string? Folder, string? Order, bool SkipKnown = true, bool HintSeen = false);
    public record SaveBody(List<string> Ids);

    private static readonly ConcurrentDictionary<string, bool> Running = new();
    // Noch nicht abgearbeitete Fotos je Person ("ext:..."): App und Web zeigen dafür ein Zwischensymbol, bis die Kopie da ist
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Pending = new();
    // Erfolgreich gesichert (oder schon in der Ablage) je Person: Der Betrachter der App zeigt dafür die Wolke „in dmOffice gespeichert“
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Saved = new();

    public static object SettingsOf(UserSettings s) => new
    {
        folder = string.IsNullOrWhiteSpace(s.SaveFolder) ? DefaultFolder : s.SaveFolder,
        order = Orders.Contains(s.SaveOrder) ? s.SaveOrder : SourceMonth,
        skipKnown = s.SaveSkipKnown,
        hintSeen = s.SaveHintSeen,
    };

    /// <summary>Name der Herkunft eines Galerie-Ordners: letzter Teil seines Pfads, sonst der Name des externen Ordners.</summary>
    public static string SourceName(GallerySource src, IEnumerable<ExternalFolders.Mount> mounts)
    {
        var last = src.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var name = last ?? mounts.FirstOrDefault(m => m.Id == src.Mount)?.Name ?? src.Mount;
        return Safe(name);
    }

    private static string Clean(string s) =>
        new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim(' ', '.');

    private static string Safe(string s)
    {
        var clean = Clean(s);
        return clean.Length == 0 ? "Galerie" : clean;
    }

    /// <summary>Zielordner aus der Eingabe: Teile aus nur Punkten oder Leerzeichen („..“, „ “) fallen weg,
    /// bleibt nichts übrig, gilt der Vorgabeordner.</summary>
    private static string FolderOf(string? input)
    {
        var parts = (input ?? "").Split('/', '\\').Select(Clean).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? DefaultFolder : string.Join('/', parts);
    }

    /// <summary>Höchstlänge eines Ordnernamens im Zielordner (Dateisysteme erlauben 255 Bytes je Name).</summary>
    public const int MaxFolderPart = 100;

    /// <summary>Zielordner in der Ablage für ein Foto (ohne Dateinamen).</summary>
    public static string TargetDir(string folder, string order, string source, string relPath, DateTime taken)
    {
        var root = FolderOf(folder);
        var y = taken.ToString("yyyy", CultureInfo.InvariantCulture);
        var m = taken.ToString("MM", CultureInfo.InvariantCulture);
        var relDir = relPath.Contains('/') ? relPath[..relPath.LastIndexOf('/')] : "";
        return order switch
        {
            Month => $"{root}/{y}/{m}",
            Day => $"{root}/{y}/{m}/{taken.ToString("dd", CultureInfo.InvariantCulture)}",
            Structure => relDir.Length == 0 ? $"{root}/{source}" : $"{root}/{source}/{relDir}",
            _ => $"{root}/{source}/{y}/{m}",
        };
    }

    private sealed record Plan(string Id, GallerySource Source, string Path, string Dir, string Name, long Size, long Modified, string? Mime,
        List<string> Candidates);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/save/settings", (HttpContext http) => Results.Ok(SettingsOf(http.Space().Settings)));
        // Was gerade noch gesichert wird (leer: nichts läuft)
        api.MapGet("/save/pending", (HttpContext http) =>
            Results.Ok(new
            {
                ids = Pending.TryGetValue(http.Space().User.Id, out var p) ? p.Keys.ToList() : new List<string>(),
                saved = Saved.TryGetValue(http.Space().User.Id, out var d) ? d.Keys.ToList() : new List<string>(),
            }));
        api.MapPut("/save/settings", (SettingsBody b, HttpContext http) =>
        {
            if (b.Order != null && !Orders.Contains(b.Order)) throw new ArgumentException("Unbekannte Ordnung.");
            var folder = FolderOf(b.Folder);
            if (folder.Split('/').Any(p => p.Length > MaxFolderPart))
                throw new ArgumentException("Ein Ordnername im Zielordner ist zu lang (höchstens 100 Zeichen).");
            http.Space().UpdateSettings(s =>
            {
                s.SaveFolder = folder;
                s.SaveOrder = b.Order ?? SourceMonth;
                s.SaveSkipKnown = b.SkipKnown;
                s.SaveHintSeen = b.HintSeen || s.SaveHintSeen;
            });
            return Results.Ok(SettingsOf(http.Space().Settings));
        });

        // Ausgewählte Fotos aus Galerie-Ordnern in die Ablage kopieren (im Hintergrund)
        api.MapPost("/save", async (SaveBody b, HttpContext http, ServerConfig config, ILoggerFactory logs) =>
        {
            var space = http.Space();
            var ids = b.Ids.Select(i => i.StartsWith("ext:", StringComparison.Ordinal) ? i[4..] : i).Where(i => i.Contains('/')).Distinct().ToList();
            if (ids.Count == 0) throw new ArgumentException("Bitte Fotos aus einem externen Galerie-Ordner auswählen - was schon in der Ablage liegt, muss nicht gesichert werden.");
            if (ids.Count > 5000) throw new ArgumentException("Höchstens 5000 Fotos auf einmal.");
            var s = space.Settings;
            var folder = string.IsNullOrWhiteSpace(s.SaveFolder) ? DefaultFolder : s.SaveFolder;
            var order = Orders.Contains(s.SaveOrder) ? s.SaveOrder : SourceMonth;
            var mounts = ExternalFolders.MountsOf(space, config).ToList();
            var entries = (await GalleryTimeline.AllAsync(http, config)).ToDictionary(e => e.Id, StringComparer.Ordinal);
            // Eigene Fotos mit denselben Bildangaben: Kandidaten für „dasselbe Bild, andere Datei“
            var own = entries.Values.Where(e => e.Owner == null && !e.Id.StartsWith("ext:", StringComparison.Ordinal))
                .Select(e => (Key: GalleryDupes.CandidateKey(e), e.Id)).Where(x => x.Key != null).ToLookup(x => x.Key!, x => x.Id);
            var plan = new List<Plan>();
            foreach (var id in ids)
            {
                GallerySource src; string path;
                try { (src, path) = GalleryFolders.Resolve(space, config, id); }
                catch (KeyNotFoundException) { continue; }
                var e = entries.GetValueOrDefault("ext:" + id);
                var takenMs = e?.Meta?.Taken ?? e?.Modified ?? 0;
                var taken = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, takenMs)).UtcDateTime, space.Zone);
                var name = path[(path.LastIndexOf('/') + 1)..];
                var key = e != null ? GalleryDupes.CandidateKey(e) : null;
                plan.Add(new Plan(id, src, path, TargetDir(folder, order, SourceName(src, mounts), path, taken), name, e?.Size ?? 0, e?.Modified ?? 0, e?.Mime,
                    key != null ? own[key].ToList() : []));
            }
            if (plan.Count == 0) return Results.NotFound(new { error = "Diese Fotos gibt es nicht mehr." });
            if (!Running.TryAdd(space.User.Id, true)) return Results.Conflict(new { error = "Es werden gerade schon Fotos in die Ablage gesichert. Bitte warten, bis das fertig ist." });
            var log = logs.CreateLogger("DmOffice.Gallery.Save");
            var skipKnown = s.SaveSkipKnown;
            var pending = new ConcurrentDictionary<string, byte>(plan.Select(p => new KeyValuePair<string, byte>("ext:" + p.Id, 0)));
            Pending[space.User.Id] = pending;
            var savedNow = Saved.GetOrAdd(space.User.Id, _ => new ConcurrentDictionary<string, byte>());
            if (savedNow.Count > 5000) savedNow.Clear();
            _ = Task.Run(async () =>
            {
                using var job = Activity.Start(space.User.Id, Activity.GallerySave, $"{plan.Count} Fotos", plan.Sum(p => p.Size));
                try { await RunAsync(space, config, plan, skipKnown, job, CancellationToken.None, (id, ok) => { if (ok) savedNow["ext:" + id] = 0; pending.TryRemove("ext:" + id, out _); }); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    log.LogWarning("In Ablage sichern für {User}: {Error}", space.User.Login, e.Message);
                    job.Fail(e is QuotaExceededException ? "Der Speicher ist voll." : e.Message);
                }
                finally { GalleryTimeline.Forget(space.User.Id); Pending.TryRemove(space.User.Id, out _); Running.TryRemove(space.User.Id, out _); }
            });
            return Results.Accepted(value: new { started = true, count = plan.Count, folder, order });
        });
    }

    private static async Task RunAsync(UserSpace space, ServerConfig config, List<Plan> plan, bool skipKnown, Activity.Job job, CancellationToken ct,
        Action<string, bool>? finished = null)
    {
        var db = space.Db;
        var drivers = new Dictionary<string, IStorageDriver>();
        long total = plan.Sum(p => p.Size), done = 0;
        int saved = 0, skipped = 0, failed = 0;
        try
        {
            foreach (var p in plan)
            {
                var tmp = Path.Combine(Path.GetTempPath(), "dmo-save-" + Guid.NewGuid().ToString("N"));
                var ok = false;
                try
                {
                    if (!drivers.TryGetValue(p.Source.Mount, out var driver))
                    {
                        if (!await ExternalFolders.ReachableAsync(space, config, p.Source.Mount)) { failed++; done += p.Size; continue; }
                        driver = drivers[p.Source.Mount] = ExternalFolders.Open(space, config, p.Source.Mount).Driver;
                    }
                    // Einmal lesen: in eine Zwischendatei, dabei die Prüfsumme bilden
                    string sha;
                    long size;
                    await using (var input = await driver.OpenReadAsync(p.Path, ct))
                    await using (var file = File.Create(tmp))
                    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = await input.ReadAsync(buf, ct)) > 0) { hash.AppendData(buf, 0, n); await file.WriteAsync(buf.AsMemory(0, n), ct); }
                        sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                        size = file.Length;
                    }
                    done += p.Size;
                    if (skipKnown && await KnownAsync(db, tmp, sha, size, p.Candidates, ct)) { skipped++; ok = true; job.Progress(done, total, p.Name); continue; }
                    FileStore.EnsureDir(db, p.Dir);
                    var target = p.Dir + "/" + p.Name;
                    if (FileStore.Get(db, target) != null) target = FileStore.FreeName(db, target);
                    await using (var data = File.OpenRead(tmp))
                        await FileStore.PutAsync(db, target, data, p.Mime ?? DavServer.Mime(target),
                            mtime: p.Modified > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.Modified).UtcDateTime : null, ct: ct);
                    saved++;
                    ok = true;
                    job.Progress(done, total, p.Name);
                }
                catch (QuotaExceededException) { throw; }
                catch (Exception e) when (e is IOException or KeyNotFoundException or UnauthorizedAccessException or DavConflictException) { failed++; }
                finally { try { File.Delete(tmp); } catch (IOException) { } finished?.Invoke(p.Id, ok); }
            }
        }
        finally { foreach (var d in drivers.Values) d.Dispose(); }
        var parts = new List<string> { $"{saved} gesichert" };
        if (skipped > 0) parts.Add($"{skipped} lagen schon in der Ablage");
        if (failed > 0) parts.Add($"{failed} nicht lesbar");
        job.Progress(total, total, string.Join(", ", parts));
    }

    /// <summary>Liegt das schon in der Ablage? Gleiche Datei (Prüfsumme) oder dasselbe Bild mit anderen Bildangaben (JPEG ohne Kopfdaten).</summary>
    private static async Task<bool> KnownAsync(UserDb db, string tmp, string sha, long size, List<string> candidates, CancellationToken ct)
    {
        if (FileHashes.Lookup(db, [(sha, size)]).Count > 0) return true;
        if (candidates.Count == 0) return false;
        string? mine;
        await using (var s = File.OpenRead(tmp)) mine = await GalleryDupes.ImageHashAsync(s, ct);
        if (mine == null) return false;
        foreach (var c in candidates)
        {
            if (FileStore.Get(db, c) == null) continue;
            await using var s = FileStore.OpenRead(db, c);
            if (await GalleryDupes.ImageHashAsync(s, ct) == mine) return true;
        }
        return false;
    }
}
