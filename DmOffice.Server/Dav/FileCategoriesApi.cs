using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>Kategorien und Tags der eigenen Dateien (docs/datei-kategorien-konzept.md) - für Web und App, nicht über WebDAV.</summary>
public static class FileCategoriesApi
{
    public record CategoryBody(string? Name, string? Parent, string? Color, bool? Learn, bool? Auto);
    public record FoldersBody(List<string>? Folders);
    public record LabelsBody(List<string> Paths, List<string>? Add, List<string>? Remove, List<string>? AddTags, List<string>? RemoveTags);
    public record PathsBody(List<string> Paths);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/files").RequireAuthorization();
        api.AddEndpointFilter(async (ctx, next) =>
            FileStore.FilesOff(ctx.HttpContext.Space().Db)
                ? Results.Json(new { error = "Die Dateiablage ist für dieses Konto abgeschaltet." }, statusCode: 403)
                : await next(ctx));

        // Baum mit Anzahl (direkt zugeordnet, davon noch nicht bestaetigt) und alle Tags
        api.MapGet("/categories", (HttpContext http) => Tree(http.Space().Db));

        api.MapPost("/categories", (CategoryBody body, HttpContext http) =>
        {
            var c = FileCategories.Create(http.Space().Db, body.Name, string.IsNullOrEmpty(body.Parent) ? null : body.Parent, body.Color, body.Learn ?? true, body.Auto ?? true);
            return Results.Ok(c);
        });

        // Vorschlag für den Anfang übernehmen (nur bei leerer Liste)
        api.MapPost("/categories/suggestion", (string? lang, HttpContext http) =>
        {
            FileCategories.CreateSuggestion(http.Space().Db, lang);
            return Tree(http.Space().Db);
        });
        // Vorhandene Dateien ohne Kategorie nachträglich einordnen (der Hintergrundjob arbeitet sie ab)
        api.MapPost("/categories/classify-existing", (FoldersBody? body, HttpContext http) => new { queued = FileCategories.QueueExisting(http.Space(), body?.Folders) });
        // Vorher: was betroffen wäre und welche Kategorien schon zuordnen können
        api.MapGet("/categories/classify-existing", (HttpContext http) =>
        {
            var (files, ready, learning) = FileCategories.ExistingPreview(http.Space());
            var folders = FileCategories.UncategorizedByFolder(http.Space()).Select(f => new { path = f.Folder, count = f.Count });
            return new { files, ready, learning, folders };
        });
        // Probelauf: so würden einige der Dateien eingeordnet
        api.MapPost("/categories/classify-existing/sample", (FoldersBody? body, HttpContext http) =>
            FileCategories.Sample(http.Space(), body?.Folders).Select(x => new { x.Path, cats = x.Cats }));
        // Unbestätigte Zuordnungen wieder entfernen (cat leer: alle Kategorien)
        api.MapPost("/categories/unconfirmed/remove", (string? cat, HttpContext http) =>
            new { removed = FileCategories.RemoveUnconfirmed(http.Space().Db, string.IsNullOrEmpty(cat) ? null : cat) });

        api.MapPut("/categories/{id}", (string id, CategoryBody body, HttpContext http) =>
            FileCategories.Update(http.Space().Db, id, body.Name, body.Parent, body.Color, body.Learn, body.Auto));

        api.MapDelete("/categories/{id}", (string id, HttpContext http) =>
        {
            FileCategories.Delete(http.Space().Db, id);
            return Results.NoContent();
        });

        // Virtueller Ordner: die Dateien der Kategorie und aller darunter, je Datei ihre Kategorien (mit „automatisch“)
        api.MapGet("/categories/{id}/items", (string id, HttpContext http) =>
        {
            var loc = FilesApi.Loc(http, "");
            var files = FileCategories.FilesIn(loc.Db, id).Where(f => !loc.Protected(f.Path)).ToList();
            return Listing(http, loc, files);
        });

        api.MapGet("/tags/items", (string tag, HttpContext http) =>
        {
            var loc = FilesApi.Loc(http, "");
            var files = FileCategories.FilesTagged(loc.Db, tag).Where(f => !loc.Protected(f.Path)).ToList();
            return Listing(http, loc, files);
        });

        // Kategorien und Tags ausgewählter Dateien (Details, Dialog)
        api.MapPost("/labels/of", (PathsBody body, HttpContext http) =>
        {
            var files = Own(http, body.Paths);
            var of = FileCategories.Of(http.Space().Db, files.Select(f => f.FileId));
            return Results.Ok(new
            {
                items = files.ToDictionary(f => f.Path, f => new { cats = of[f.FileId].Cats, tags = of[f.FileId].Tags }),
            });
        });

        api.MapPost("/labels", (LabelsBody body, HttpContext http) =>
        {
            var files = Own(http, body.Paths);
            if (files.Count == 0) throw new ArgumentException("Kategorien und Tags gibt es nur für eigene Dateien.");
            FileCategories.Set(http.Space(), files, body.Add, body.Remove, body.AddTags, body.RemoveTags);
            return Results.NoContent();
        });
    }

    private static object Tree(UserDb db)
    {
        var counts = FileCategories.Counts(db);
        return new
        {
            cats = FileCategories.All(db).Select(c => new
            {
                c.Id, c.Parent, c.Name, c.Color, c.Learn, c.Auto,
                count = counts.TryGetValue(c.Id, out var n) ? n.Count : 0,
                unconfirmed = counts.TryGetValue(c.Id, out var m) ? m.Auto : 0,
            }),
            tags = FileCategories.Tags(db).Select(t => new { tag = t.Tag, count = t.Count }),
        };
    }

    private static IResult Listing(HttpContext http, FileLoc loc, List<FileEntry> files)
    {
        var marks = FilesApi.MarksOf(http, loc);
        var of = FileCategories.Of(loc.Db, files.Select(f => f.FileId));
        return Results.Ok(new
        {
            items = files.Select(f => FilesApi.Item(f, marks)),
            labels = files.ToDictionary(f => f.Path, f => new { cats = of[f.FileId].Cats, tags = of[f.FileId].Tags }),
        });
    }

    /// <summary>Nur eigene Dateien (nicht „Mit mir geteilt“, nicht der notizi-Ordner), keine Ordner.</summary>
    private static List<FileEntry> Own(HttpContext http, List<string>? paths)
    {
        var list = new List<FileEntry>();
        foreach (var p in (paths ?? []).Distinct().Take(5000))
        {
            FileLoc loc;
            try { loc = FilesApi.Loc(http, p); } catch (KeyNotFoundException) { continue; }
            if (loc.Shared || loc.Virtual) continue;
            if (FileStore.Get(loc.Db, loc.Path) is { IsDir: false, FileId: > 0 } f) list.Add(f);
        }
        return list;
    }
}
