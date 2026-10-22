using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Search;

/// <summary>Volltextsuche: Schalter und Zustand unter Einstellungen -> Suchen, Inhaltssuche in der Dateiablage.</summary>
public static class TextIndexApi
{
    public record SwitchBody(bool Enabled, bool Ocr);

    public static void Map(WebApplication app)
    {
        var settings = app.MapGroup("/api/settings/textindex").RequireAuthorization();

        settings.MapGet("/", (HttpContext http) => TextIndex.State(http.Space()));

        settings.MapPut("/", (SwitchBody body, HttpContext http, TextIndexer indexer) =>
        {
            var space = http.Space();
            var before = space.Settings.TextIndex;
            // OCR nur, wenn der Container sie kann - und nur zusammen mit der Suche
            space.UpdateSettings(s => s.TextIndex = new TextIndexSettings { Enabled = body.Enabled, Ocr = body.Enabled && body.Ocr && Ocr.Available });
            TextIndex.Apply(space, before);
            indexer.Kick();
            return TextIndex.State(space);
        });

        settings.MapPost("/rebuild", (HttpContext http, TextIndexer indexer) =>
        {
            var space = http.Space();
            TextIndex.Rebuild(space);
            indexer.Kick();
            return TextIndex.State(space);
        });

        // Inhaltssuche der Dateiablage (auch fuer die Handy-App): Treffer mit Pfad und Ausschnitt; im Ausschnitt
        // stehen U+E000 und U+E001 um die gefundenen Woerter. Der notizi-Ordner kommt nie vor.
        app.MapGet("/api/files/search/content", (string q, string? path, int? limit, HttpContext http) =>
        {
            var space = http.Space();
            if (FileStore.FilesOff(space.Db))
                return Results.Json(new { error = "Die Dateiablage ist für dieses Konto abgeschaltet." }, statusCode: 403);
            var under = SharedFiles.Resolve(http, FileStore.Normalize(path ?? ""));
            if (under.Protected(under.Path)) throw new KeyNotFoundException("Ordner nicht gefunden.");
            var max = Math.Clamp(limit ?? 100, 1, 500);
            // Mit mir geteilte Ordner: im Index des Besitzers (wenn er die Inhaltssuche eingeschaltet hat), Pfade wie ich sie sehe
            var areas = under.Virtual || under.Path.Length == 0 && !under.Shared
                ? SharedFiles.Mounts(http).Select(m => new FileLoc(m.Owner, m.Dir.Path, m)).ToList() : [];
            if (!under.Virtual) areas.Insert(0, under);
            return Results.Ok(new
            {
                enabled = space.Settings.TextIndex.Enabled,
                items = areas.SelectMany(a => TextIndex.SearchFiles(a.Space, q, a.Path, max)
                        .Where(h => a.Shared || !SharedFiles.Under(http, h.Path)).Select(h => h with { Path = a.Show(h.Path) }))
                    .Take(max).ToList()
            });
        }).RequireAuthorization();
    }
}
