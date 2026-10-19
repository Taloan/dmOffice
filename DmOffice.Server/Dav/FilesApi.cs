using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>Die WebDAV-Dateien in der Weboberflaeche: ansehen, hochladen, ordnen.</summary>
public static class FilesApi
{
    public record PathBody(string Path);
    public record MoveBody(string From, string To, bool Copy);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/files").RequireAuthorization();
        // Vom Admin abgeschaltet: der ganze Bereich (der notizi-Abgleich laeuft ueber WebDAV weiter)
        api.AddEndpointFilter(async (ctx, next) =>
            FileStore.FilesOff(ctx.HttpContext.Space().Db)
                ? Results.Json(new { error = "Die Dateiablage ist für dieses Konto abgeschaltet." }, statusCode: 403)
                : await next(ctx));

        api.MapGet("/", (string? path, HttpContext http) =>
        {
            var db = http.Space().Db;
            var p = FileStore.Normalize(path ?? "");
            var dir = FileStore.Get(db, p) ?? throw new KeyNotFoundException("Ordner nicht gefunden.");
            if (!dir.IsDir) throw new ArgumentException("Kein Ordner.");
            return new
            {
                path = p,
                usage = FileStore.Usage(db),
                quota = FileStore.Quota(db),
                items = FileStore.Children(db, p).Select(f => new { f.Path, f.Name, f.IsDir, f.Size, f.Mime, f.Modified })
            };
        });

        api.MapGet("/download", (string path, bool? inline, HttpContext http) =>
        {
            var db = http.Space().Db;
            var p = FileStore.Normalize(path);
            var f = FileStore.Get(db, p) ?? throw new KeyNotFoundException();
            if (f.IsDir) throw new ArgumentException("Ordner lassen sich nicht herunterladen.");
            var mime = f.Mime ?? DavServer.Mime(p);
            // Inline nur fuer Bilder und PDF - kein HTML aus der Ablage unter dieser Adresse
            bool show = inline == true && (mime.StartsWith("image/") && mime != "image/svg+xml" || mime == "application/pdf");
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(FileStore.Data(db, p) ?? Array.Empty<byte>(), show ? mime : "application/octet-stream", show ? null : f.Name);
        });

        api.MapPost("/upload", async (string? path, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            var db = http.Space().Db;
            var dir = FileStore.Normalize(path ?? "");
            var form = await request.ReadFormAsync(ct);
            foreach (var file in form.Files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                // Mit Unterordnern (Ordner hochladen): "a/b/c.txt"
                var rel = FileStore.Normalize(file.FileName);
                var full = dir.Length == 0 ? rel : dir + "/" + rel;
                if (FileStore.Parent(full) is { Length: > 0 } parent) FileStore.EnsureDir(db, parent);
                try { FileStore.Put(db, full, ms.ToArray(), string.IsNullOrEmpty(file.ContentType) ? DavServer.Mime(full) : file.ContentType); }
                catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message }, statusCode: 507); }
            }
            return Results.Ok();
        }).DisableAntiforgery();

        api.MapPost("/mkdir", (PathBody body, HttpContext http) =>
        {
            FileStore.Mkcol(http.Space().Db, FileStore.Normalize(body.Path));
            return Results.Ok();
        });

        api.MapPost("/delete", (PathBody body, HttpContext http) =>
        {
            FileStore.Delete(http.Space().Db, FileStore.Normalize(body.Path));
            return Results.Ok();
        });

        api.MapPost("/move", (MoveBody body, HttpContext http) =>
        {
            try { FileStore.MoveOrCopy(http.Space().Db, FileStore.Normalize(body.From), FileStore.Normalize(body.To), false, body.Copy); }
            catch (DavConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (DavForbiddenException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message }, statusCode: 507); }
            catch (Pim.PreconditionFailedException) { return Results.Conflict(new { error = "Am Ziel gibt es das schon." }); }
            return Results.Ok();
        });
    }
}
