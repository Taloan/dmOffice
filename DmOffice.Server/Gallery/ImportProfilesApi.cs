using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;

namespace DmOffice.Gallery;

/// <summary>Importprofile der Galerie anlegen, bearbeiten, löschen, anstoßen; Vorschau der Muster.</summary>
public static class ImportProfilesApi
{
    public record Body(string? Name, List<ImportSource>? Sources, string? Mount, string? Target, string? Folders, string? Names, string? Title, string? Description,
        bool Copy = false, int Every = 10, DateOnly? Since = null, bool Active = true, string? Language = null);
    public record PreviewBody(List<ImportSource>? Sources, string? Folders, string? Names, string? Title, string? Description, string? Language = null);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/gallery/imports").RequireAuthorization();

        api.MapGet("/", async (HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            var mounts = await ExternalFolders.WithReachAsync(space, config, ExternalFolders.MountsOf(space, config));
            return new
            {
                profiles = ImportProfiles.Of(space.Db).Select(p => new
                {
                    p.Id, p.Name, p.Sources, p.Mount, mountName = mounts.FirstOrDefault(m => m.Id == p.Mount)?.Name, p.Target, p.Folders, p.Names, p.Title,
                    p.Description, p.Copy, p.Every, p.Since, p.Active, p.LastRun, p.Error, p.LastCount, p.Total, p.Pending,
                    running = ImportProfileJob.IsRunning(space.User.Id, p.Id),
                }),
                // Als Quelle alle (nur zum Lesen eingebundene nur zum Kopieren), als Ziel nur, was sich beschreiben lässt
                mounts = mounts.Select(m => new { m.Id, m.Name, m.ReadOnly, m.Available }),
                folderTokens = ImportProfiles.FolderTokens,
                nameTokens = ImportProfiles.NameTokens,
                textTokens = ImportProfiles.AllTokens,
            };
        });

        api.MapPost("/", async (Body body, HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            var p = new ImportProfile();
            if (await ApplyAsync(space, config, p, body, http.RequestAborted) is { } error) return Results.BadRequest(new { error });
            ImportProfiles.Change(space.Db, list => list.Add(p));
            if (p.Active) ImportProfileJob.Request(space.User.Id, p.Id);
            return Results.Ok(new { p.Id });
        });

        api.MapPut("/{id}", async (string id, Body body, HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            if (ImportProfiles.Of(space.Db).FirstOrDefault(x => x.Id == id) is not { } p) return Results.NotFound();
            if (await ApplyAsync(space, config, p, body, http.RequestAborted) is { } error) return Results.BadRequest(new { error });
            ImportProfiles.Update(space.Db, id, x =>
            {
                (x.Name, x.Sources, x.Mount, x.Target, x.Folders, x.Names) = (p.Name, p.Sources, p.Mount, p.Target, p.Folders, p.Names);
                (x.Title, x.Description, x.Copy, x.Every, x.Since, x.Active, x.Language) = (p.Title, p.Description, p.Copy, p.Every, p.Since, p.Active, p.Language);
                x.Error = null;
            });
            return Results.Ok();
        });

        api.MapDelete("/{id}", (string id, HttpContext http) =>
        {
            var db = http.Space().Db;
            var found = false;
            ImportProfiles.Change(db, list => found = list.RemoveAll(x => x.Id == id) > 0);
            if (!found) return Results.NotFound();
            ImportProfiles.Forget(db, id);
            return Results.Ok();
        });

        api.MapPost("/{id}/run", (string id, HttpContext http) =>
        {
            var space = http.Space();
            if (!ImportProfiles.Of(space.Db).Any(x => x.Id == id)) return Results.NotFound();
            ImportProfileJob.Request(space.User.Id, id);
            return Results.Ok();
        });

        // Vorschau: mit dem neuesten Foto der Quellordner, sonst mit einem Beispiel
        api.MapPost("/preview", async (PreviewBody body, HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            var (facts, sample) = await SampleAsync(space, config, body.Sources ?? [], http.RequestAborted);
            var culture = ImportProfiles.CultureOf(body.Language);
            var import = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone);
            const int seq = 1;
            var ext = sample != null ? Path.GetExtension(sample) : ".JPG";
            return new
            {
                sample,
                folder = ImportProfiles.RenderFolder(body.Folders ?? "", facts, import, seq, culture),
                name = ImportProfiles.RenderName(body.Names ?? "", facts, import, seq, culture) + ext,
                title = ImportProfiles.RenderText(body.Title ?? "", facts, import, seq, culture),
                description = ImportProfiles.RenderText(body.Description ?? "", facts, import, seq, culture),
            };
        });
    }

    /// <summary>
    /// Beispielfoto für die Vorschau: das neueste der eigenen Quellordner, sonst eines aus dem ersten externen Quellordner (nur dessen
    /// oberste Ebene, nur der Kopf der Datei - die Vorschau soll nicht die ganze Freigabe durchsuchen).
    /// </summary>
    private static async Task<(ImportProfiles.Facts Facts, string? Sample)> SampleAsync(UserSpace space, ServerConfig config, List<ImportSource> sources, CancellationToken ct)
    {
        var db = space.Db;
        var own = new List<ImportProfiles.Item>();
        foreach (var src in sources.Where(x => (x.Mount ?? "").Length == 0))
        {
            string path;
            try { path = StoragePath.Clean(src.Path); } catch (ArgumentException) { continue; }
            if (path.Length == 0 || FileStore.Get(db, path) is not { IsDir: true }) continue;
            own.AddRange(FileStore.Descendants(db, path).Where(f => !f.IsDir && GalleryFolders.KindOf(f.Name) == "image")
                .Select(f => new ImportProfiles.Item(f.Path, f.Size, f.Modified, f.Created, f.Etag, f.Mime)));
        }
        try
        {
            if (ImportProfiles.Groups(own).OrderByDescending(g => g.Primary.Created).FirstOrDefault() is { } g)
            {
                using var s = FileStore.OpenRead(db, g.Primary.Path);
                return (ImportProfiles.Read(s, g.Stem, g.Earliest, space.Zone), g.Primary.Path);
            }
            if (sources.FirstOrDefault(x => (x.Mount ?? "").Length > 0) is { } ext && await ExternalFolders.ReachableAsync(space, config, ext.Mount))
            {
                using var loc = new ImportProfiles.ExtLoc(ExternalFolders.Open(space, config, ext.Mount).Driver, ext.Mount);
                var files = (await loc.FilesAsync(StoragePath.Clean(ext.Path), ct)).Values.Where(f => GalleryFolders.KindOf(f.Name) == "image");
                if (ImportProfiles.Groups(files, ext.Mount).OrderByDescending(x => x.Primary.Modified).FirstOrDefault() is { } eg)
                {
                    await using var s = await loc.HeadAsync(eg.Primary, ct);
                    return (ImportProfiles.Read(s, eg.Stem, eg.Earliest, space.Zone), eg.Primary.Path);
                }
            }
        }
        catch (Exception e) when (e is KeyNotFoundException or IOException or UnauthorizedAccessException or HttpRequestException or ArgumentException) { }
        return (ImportProfiles.Example, null);
    }

    /// <summary>Angaben prüfen und übernehmen; Fehlertext oder null.</summary>
    internal static async Task<string?> ApplyAsync(UserSpace space, ServerConfig config, ImportProfile p, Body b, CancellationToken ct)
    {
        var name = (b.Name ?? "").Trim();
        if (name.Length is 0 or > 100) return "Das Profil braucht einen Namen (höchstens 100 Zeichen).";
        List<ImportSource> sources;
        string target;
        try
        {
            sources = (b.Sources ?? []).Select(x => new ImportSource((x.Mount ?? "").Trim(), StoragePath.Clean(x.Path)))
                .Where(x => x.Mount.Length > 0 || x.Path.Length > 0).Distinct().ToList();
            target = StoragePath.Clean(b.Target);
        }
        catch (ArgumentException) { return "Ungültiger Pfad."; }
        if (sources.Count is 0 or > 20) return "Das Profil braucht einen bis 20 Quellordner.";
        var mounts = ExternalFolders.MountsOf(space, config);
        foreach (var s in sources)
        {
            if (s.Mount.Length == 0)
            {
                if (FileStore.IsSystem(space.Db, s.Path)) return "Aus diesem Ordner lässt sich nichts einsortieren.";
                if (FileStore.Get(space.Db, s.Path) is not { IsDir: true }) return $"Den Quellordner „{s.Path}“ gibt es nicht.";
                continue;
            }
            if (mounts.FirstOrDefault(m => m.Id == s.Mount) is not { } m) return "Diesen externen Ordner gibt es nicht.";
            // Verschieben löscht in der Quelle: aus einem Ordner nur zum Lesen lässt sich nur kopieren
            if (m.ReadOnly && !b.Copy) return "Aus einem externen Ordner, der nur zum Lesen eingebunden ist, lässt sich nur kopieren.";
            // Den Ordner prüfen, wenn der externe Ordner gerade erreichbar ist (sonst beim Einsortieren)
            if (s.Path.Length > 0 && await ExternalFolders.ReachableAsync(space, config, s.Mount))
            {
                using var d = ExternalFolders.Open(space, config, s.Mount).Driver;
                if (await d.StatAsync(s.Path, ct) is not { IsDir: true }) return $"Den Quellordner „{s.Path}“ gibt es nicht.";
            }
        }
        var mount = (b.Mount ?? "").Trim();
        // Ziel nicht in einem Quellordner, sonst sortierte das Profil seine eigenen Fotos immer wieder um
        if (sources.Any(s => s.Mount == mount && (s.Path.Length == 0 || target == s.Path || FileStore.InRoot(target, s.Path))))
            return "Der Zielordner darf nicht in einem Quellordner liegen.";
        if (mount.Length == 0)
        {
            if (target.Length > 0 && FileStore.IsSystem(space.Db, target)) return "In diesem Ordner lässt sich nichts ablegen.";
            if (target.Length > 0 && FileStore.Get(space.Db, target) is { IsDir: false }) return "Der Zielordner ist eine Datei.";
        }
        else
        {
            if (mounts.FirstOrDefault(m => m.Id == mount) is not { } tm) return "Diesen externen Ordner gibt es nicht.";
            if (tm.ReadOnly) return "Dieser externe Ordner ist nur zum Lesen eingebunden.";
        }
        var folders = (b.Folders ?? "").Trim();
        var names = (b.Names ?? "").Trim();
        var title = (b.Title ?? "").Trim();
        var description = (b.Description ?? "").Trim();
        if (names.Length == 0) return "Das Dateinamen-Muster ist leer.";
        if (names.Contains('/') || names.Contains('\\')) return "Das Dateinamen-Muster darf keinen Schrägstrich enthalten.";
        if (folders.Length > 500 || names.Length > 300 || title.Length > 500 || description.Length > 4000) return "Ein Muster ist zu lang.";
        var unknown = new[] { folders, names, title, description }.SelectMany(ImportProfiles.Unknown).Distinct().ToList();
        if (unknown.Count > 0) return "Unbekannter Platzhalter: " + string.Join(", ", unknown.Select(u => "{" + u + "}"));
        if (b.Every is < 5 or > 1440) return "Das Intervall liegt zwischen 5 und 1440 Minuten.";
        (p.Name, p.Sources, p.Mount, p.Target, p.Folders, p.Names, p.Title, p.Description) = (name, sources, mount, target, folders, names, title, description);
        (p.Copy, p.Every, p.Since, p.Active, p.Language) = (b.Copy, b.Every, b.Since, b.Active, b.Language == "en" ? "en" : "de");
        return null;
    }
}
