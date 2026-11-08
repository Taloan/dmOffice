using DmOffice.Core;
using DmOffice.Storage;

namespace DmOffice.Audio;

/// <summary>Audiothek: Ordner einrichten, Titelliste, Cover, Abspielen (Range) und Hörverlauf. Ausgeschaltet: 404.</summary>
public static class AudioApi
{
    public record SourceBody(string Mount, string? Path, bool Watch = true);
    public record WatchBody(bool? Watch);
    public record PlayedBody(string Id, long Ms, string? Device, DateTime? At);
    public record PlaylistBody(string? Name, List<string>? Tracks);
    public record TracksBody(List<string> Tracks);

    /// <summary>Reihenfolge der Felder je Titel in GET /tracks (Arrays statt Objekte: zehntausende Titel).</summary>
    public static readonly string[] Fields = ["id", "title", "artist", "albumArtist", "album", "track", "disc", "year", "genre", "duration", "cover", "modified"];

    public static void Map(WebApplication app)
    {
        // Ein- und ausschalten: Hauptschalter der Module (/api/admin/modules)
        var api = app.MapGroup("/api/audio").RequireAuthorization().AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            return AudioFolders.Enabled(http.Space().User)
                ? await next(ctx)
                : Results.NotFound(new { error = "Die Audiothek ist ausgeschaltet." });
        });

        api.MapGet("/sources", async (HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            var mounts = await ExternalFolders.WithReachAsync(space, config, ExternalFolders.MountsOf(space, config));
            return new
            {
                sources = AudioFolders.Of(space.Db).Select(s => new
                {
                    s.Id, s.Mount, mountName = mounts.FirstOrDefault(m => m.Id == s.Mount)?.Name, s.Path,
                    // Externer Ordner gerade nicht erreichbar: Titel bleiben in der Liste, Abspielen gesperrt
                    available = mounts.FirstOrDefault(m => m.Id == s.Mount)?.Available ?? false,
                    s.Watch, s.LastScan, s.Error, s.Count,
                    scanning = AudioScanJob.IsRunning(space.User.Id, s.Id),
                    pending = AudioFolders.Pending(space.Db, s.Id),
                }),
                mounts,
            };
        });

        api.MapPost("/sources", async (SourceBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            var path = StoragePath.Clean(body.Path);
            var (driver, _) = ExternalFolders.Open(space, config, body.Mount);
            using (driver)
                if (path.Length > 0 && await driver.StatAsync(path, ct) is not { IsDir: true })
                    return Results.BadRequest(new { error = "Diesen Ordner gibt es nicht." });
            var list = AudioFolders.Of(space.Db);
            if (list.Any(s => s.Mount == body.Mount && s.Path == path)) return Results.BadRequest(new { error = "Dieser Ordner ist schon in der Audiothek." });
            var src = new AudioSource { Mount = body.Mount, Path = path, Watch = body.Watch };
            list.Add(src);
            AudioFolders.Save(space.Db, list);
            AudioScanJob.Request(space.User.Id, src.Id);
            return Results.Ok(new { src.Id });
        });

        api.MapPut("/sources/{id}", (string id, WatchBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            var list = AudioFolders.Of(db);
            if (list.FirstOrDefault(s => s.Id == id) is not { } src) return Results.NotFound();
            if (body.Watch is bool w) src.Watch = w;
            AudioFolders.Save(db, list);
            return Results.Ok();
        });

        api.MapDelete("/sources/{id}", (string id, HttpContext http) =>
        {
            var db = http.Space().Db;
            var list = AudioFolders.Of(db);
            if (list.FirstOrDefault(s => s.Id == id) is not { } gone) return Results.NotFound();
            list.Remove(gone);
            AudioFolders.Save(db, list);
            AudioFolders.Forget(db, id);
            return Results.Ok();
        });

        api.MapPost("/sources/{id}/scan", (string id, HttpContext http) =>
        {
            var space = http.Space();
            if (!AudioFolders.Of(space.Db).Any(s => s.Id == id)) return Results.NotFound();
            AudioScanJob.Request(space.User.Id, id);
            return Results.Ok();
        });

        // Alle Titel als Arrays (Reihenfolge in fields), unverändert nur 304
        api.MapGet("/tracks", (HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            var tag = AudioFolders.Version(space, config);
            http.Response.Headers.CacheControl = "private, no-cache";
            if (http.Request.Headers.IfNoneMatch.ToString() == tag) return Results.StatusCode(StatusCodes.Status304NotModified);
            http.Response.Headers.ETag = tag;
            var tracks = AudioFolders.List(space, config).Select(t => new object?[]
                { t.Source + "/" + t.Path, t.Title, t.Artist, t.AlbumArtist, t.Album, t.No, t.Disc, t.Year, t.Genre, t.Duration, t.Cover, t.Modified });
            return Results.Ok(new { fields = Fields, tracks });
        });

        // Hörverlauf: Kennung → [gehört, übersprungen, zuletzt gehört (ms seit 1970)]
        api.MapGet("/stats", (HttpContext http, ServerConfig config) =>
            AudioFolders.Stats(http.Space(), config).ToDictionary(x => x.Key, x => new object?[] { x.Value.Plays, x.Value.Skips, x.Value.Last }));

        api.MapPost("/played", (PlayedBody body, HttpContext http, ServerConfig config) =>
        {
            if (body.Ms < 0) return Results.BadRequest(new { error = "Ungültige Dauer." });
            // Von der App nachgemeldet: Zeitpunkt aus der Vergangenheit, nicht aus der Zukunft
            var at = body.At is { } a && a.ToUniversalTime() < DateTime.UtcNow ? a.ToUniversalTime() : (DateTime?)null;
            var s = AudioFolders.Played(http.Space(), config, body.Id, body.Ms, body.Device, at);
            return Results.Ok(new { plays = s.Plays, skips = s.Skips, last = s.Last });
        });

        // Playlists: eigene (Stufe 2) und Auto-Playlists aus dem Hörverlauf
        api.MapGet("/playlists", (HttpContext http, ServerConfig config) => new
        {
            playlists = AudioPlaylists.List(http.Space(), config),
            auto = AudioPlaylists.AutoKinds,
        });
        api.MapGet("/playlists/auto/{kind}", (string kind, HttpContext http, ServerConfig config) =>
            new { tracks = AudioPlaylists.Auto(http.Space(), config, kind) });
        api.MapGet("/playlists/{id}", (string id, HttpContext http, ServerConfig config) =>
        {
            var (name, tracks) = AudioPlaylists.Get(http.Space(), config, id);
            return new { id, name, tracks };
        });
        api.MapPost("/playlists", (PlaylistBody body, HttpContext http, ServerConfig config) =>
            new { id = AudioPlaylists.Create(http.Space(), config, body.Name, body.Tracks) });
        // Umbenennen und/oder neue Reihenfolge (verschoben, entfernt)
        api.MapPut("/playlists/{id}", (string id, PlaylistBody body, HttpContext http, ServerConfig config) =>
        {
            if (body.Name != null) AudioPlaylists.Rename(http.Space(), id, body.Name);
            if (body.Tracks != null) AudioPlaylists.SetTracks(http.Space(), config, id, body.Tracks);
            return Results.Ok();
        });
        api.MapPost("/playlists/{id}/add", (string id, TracksBody body, HttpContext http, ServerConfig config) =>
            new { added = AudioPlaylists.Add(http.Space(), config, id, body.Tracks ?? []) });
        api.MapDelete("/playlists/{id}", (string id, HttpContext http) =>
        {
            AudioPlaylists.Delete(http.Space(), id);
            return Results.Ok();
        });

        api.MapGet("/cover/{id}", (string id, HttpContext http) =>
        {
            if (AudioFolders.Cover(http.Space().Db, id) is not { } data) return Results.NotFound();
            // Kennung = Prüfsumme: ändert sich nie
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(data, "image/jpeg");
        });

        // Abspielen mit Spulen (Range) - auch bei WebDAV, das liest dann in Ausschnitten
        api.MapGet("/stream", async (string id, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            var (src, path) = AudioFolders.Resolve(space, config, id);
            if (AudioFolders.MimeOf(path) is not { } mime) return Results.NotFound();
            if (!await ExternalFolders.ReachableAsync(space, config, src.Mount))
                return Results.Json(new { error = "Der externe Ordner ist gerade nicht erreichbar." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
            http.Response.RegisterForDispose(driver);
            if (await driver.StatAsync(path, ct) is not { IsDir: false } st) return Results.NotFound();
            http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Stream(new DriverStream(driver, path, st.Size, 1024 * 1024), mime,
                lastModified: new DateTimeOffset(DateTime.SpecifyKind(st.Modified, DateTimeKind.Utc)), enableRangeProcessing: true);
        });
    }
}
