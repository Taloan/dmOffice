using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice;

/// <summary>
/// Dateien im Tresor (docs/tresor-konzept.md, „Tresor-Dateibrowser“): Ordner, Hochladen, Ansehen, Vorschaubilder, Umbenennen,
/// Verschieben, Löschen und „Kopie aus dem Tresor“ in die normale Dateiablage. Verzeichnis und Inhalt verwaltet
/// <see cref="TresorFileStore"/> (<see cref="TresorDb.Files"/>); Vorschaubilder entstehen beim ersten Ansehen und liegen ebenfalls
/// verschlüsselt im Tresor. Keine Suche, keine Texterkennung, kein Teilen - Galerie, Suche, Pukki und KI lesen den Tresor nie.
/// </summary>
public static class TresorFiles
{
    public sealed record FolderBody(long Parent, string Name);
    public sealed record ChangeBody(string? Name, long? Parent);
    public sealed record CopyOutBody(List<long> Ids, string Folder);

    /// <summary>Tabelle der Dateien (Schritt 2 der Tresor-Datenbank). Wurzel: parent = 0. blob: Kennung im Speicher.</summary>
    public const string Schema =
        "CREATE TABLE files (id INTEGER PRIMARY KEY, parent INTEGER NOT NULL DEFAULT 0, name TEXT NOT NULL, is_dir INTEGER NOT NULL, " +
        "size INTEGER NOT NULL DEFAULT 0, mime TEXT, blob TEXT, thumb_small TEXT, thumb_large TEXT, modified INTEGER NOT NULL, created INTEGER NOT NULL);" +
        "CREATE UNIQUE INDEX files_name ON files(parent, name COLLATE NOCASE);";

    public sealed record Entry(long Id, long Parent, string Name, bool IsDir, long Size, string? Mime, string? Blob, string? ThumbSmall, string? ThumbLarge, long Modified);

    private static object Dto(Entry e) => new { id = e.Id, name = e.Name, isDir = e.IsDir, size = e.Size, mime = e.Mime, modified = e.Modified };

    /// <summary>Name prüfen: nicht leer, keine Schrägstriche, nicht . oder ..</summary>
    public static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 255 || n is "." or ".." || n.IndexOfAny(['/', '\\', '\0']) >= 0) throw new ArgumentException("Ungültiger Name.");
        return n;
    }

    /// <summary>Pfad von der Wurzel bis zum Ordner (für die Brotkrumen).</summary>
    private static List<object> Trail(TresorFileStore files, long id)
    {
        var list = new List<object>();
        for (var e = id == 0 ? null : files.Find(id); e != null; e = e.Parent == 0 ? null : files.Find(e.Parent))
        {
            list.Insert(0, new { id = e.Id, name = e.Name });
            if (list.Count > 64) break;
        }
        return list;
    }

    /// <summary>Pfadform für die Bildbearbeitung (/api/files/photo-*): „tresor:&lt;id&gt;“.</summary>
    public const string PathPrefix = "tresor:";

    public static long? IdOfPath(string path) =>
        path.StartsWith(PathPrefix, StringComparison.Ordinal) && long.TryParse(path[PathPrefix.Length..], out var id) ? id : null;

    /// <summary>
    /// Bearbeitetes Foto zurück in den Tresor: überschreibend (neuer Inhalt, alte Vorschaubilder weg) oder als neue Datei daneben
    /// (RAW immer als JPG). Gibt die Id zurück.
    /// </summary>
    public static async Task<long> SaveEditedAsync(TresorFileStore files, long id, byte[] jpeg, bool overwrite, CancellationToken ct = default)
    {
        var e = files.Find(id) ?? throw new KeyNotFoundException("Diese Datei gibt es im Tresor nicht.");
        if (overwrite && !Thumbs.IsRaw(e.Name))
        {
            await files.ReplaceAsync(id, new MemoryStream(jpeg), "image/jpeg", null, ct);
            return id;
        }
        var name = Thumbs.IsRaw(e.Name) ? Path.ChangeExtension(e.Name, ".jpg") : e.Name;
        return (await files.CreateAsync(e.Parent, name, new MemoryStream(jpeg), "image/jpeg", ct)).Id;
    }

    /// <summary>Vorschaubild (WebP) einer Datei im Tresor: gespeichert oder neu erzeugt und gespeichert; null, wenn es keins gibt.</summary>
    public static byte[]? ThumbOf(TresorFileStore files, Entry e, int? size)
    {
        var px = Thumbs.Snap(size ?? Thumbs.Small);
        if (files.Thumb(e, px == Thumbs.Small) is { } stored) return stored;
        if (Thumbs.KindOf(e.Name, e.Mime) == Thumbs.Kind.None) return null;
        var data = files.ReadAll(e.Id);
        var webp = Thumbs.IsRaw(e.Name) ? Thumbs.FromRaw(data, px) : Thumbs.FromBytes(data, px);
        if (webp != null) files.SetThumb(e.Id, px == Thumbs.Small, webp);
        return webp;
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/tresor/files").RequireAuthorization();

        api.MapGet("/", (long? dir, HttpContext http) =>
        {
            var files = Tresor.Db(http).Files;
            var d = dir ?? 0;
            files.RequireFolder(d);
            return Results.Ok(new { dir = d, trail = Trail(files, d), items = files.Children(d).Select(Dto) });
        });

        // Alle Ordner (für „Verschieben nach“)
        api.MapGet("/folders", (HttpContext http) =>
            Results.Ok(Tresor.Db(http).Files.Folders().Select(f => new { id = f.Id, parent = f.Parent, name = f.Name })));

        api.MapPost("/folder", (FolderBody body, HttpContext http) =>
        {
            var (id, name) = Tresor.Db(http).Files.CreateFolder(body.Parent, body.Name);
            return Results.Ok(new { id, name });
        });

        // Hochladen: Inhalt als Rumpf. Zählt zum Kontingent der Person.
        api.MapPost("/upload", async (long parent, string name, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            var files = Tresor.Db(http).Files;
            files.RequireFolder(parent);
            var clean = CleanName(name);
            var space = http.Space();
            var quota = FileStore.Quota(space.Db);
            var left = quota > 0 ? Math.Max(0, quota - UserStorage.Counted(space.Db)) : long.MaxValue;
            var mime = request.ContentType is { Length: > 0 } c && !c.StartsWith("application/octet-stream") ? c.Split(';')[0].Trim() : DavServer.Mime(clean);
            var (id, final) = await files.CreateAsync(parent, clean, request.Body, mime, ct, left, () => new InvalidOperationException("Der Speicherplatz ist voll."));
            UserStorage.Refresh(space.Db);
            return Results.Ok(new { id, name = final });
        });

        // Inhalt: zum Ansehen (inline) oder Herunterladen. Nur das eigene Gerät mit offenem Tresor.
        api.MapGet("/{id:long}/content", (long id, bool? inline, HttpContext http) =>
        {
            var files = Tresor.Db(http).Files;
            if (files.Find(id) is not { IsDir: false } e) return Results.NotFound(new { error = "Diese Datei gibt es im Tresor nicht." });
            http.Response.Headers.CacheControl = "no-store";
            var (mime, download) = Dav.FilesApi.Shown(http, e.Mime, e.Name, inline == true);
            return Results.Stream(files.OpenRead(id), mime, download, enableRangeProcessing: true);
        });

        // Vorschaubild (WebP): entsteht beim ersten Ansehen und liegt dann verschlüsselt im Tresor
        api.MapGet("/{id:long}/thumb", (long id, int? size, HttpContext http) =>
        {
            var files = Tresor.Db(http).Files;
            if (files.Find(id) is not { IsDir: false } e) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, no-store";
            return ThumbOf(files, e, size) is { } webp ? Results.Bytes(webp, "image/webp") : Results.NotFound();
        });

        // Umbenennen und Verschieben
        api.MapPut("/{id:long}", (long id, ChangeBody body, HttpContext http) =>
        {
            var files = Tresor.Db(http).Files;
            var e = files.Find(id) ?? throw new KeyNotFoundException("Das gibt es im Tresor nicht.");
            var name = files.Move(id, body.Parent ?? e.Parent, body.Name);
            return Results.Ok(new { id, name });
        });

        api.MapDelete("/{id:long}", (long id, HttpContext http) =>
        {
            Tresor.Db(http).Files.Delete(id);
            UserStorage.Refresh(http.Space().Db);
            return Results.Ok();
        });

        // „Kopie aus dem Tresor“: bewusst in die normale Dateiablage - der Tresor behält sein Original
        api.MapPost("/copy-out", async (CopyOutBody body, HttpContext http, CancellationToken ct) =>
        {
            var files = Tresor.Db(http).Files;
            var space = http.Space();
            var folder = FileStore.Normalize(body.Folder ?? "");
            // Zielordner bei Bedarf anlegen (Vorgabe der Oberfläche: „Aus dem Tresor“)
            if (folder.Length > 0) FileStore.EnsureDir(space.Db, folder);
            var copied = new List<string>();
            async Task Copy(Entry e, string into)
            {
                var target = FileStore.FreeName(space.Db, (into.Length > 0 ? into + "/" : "") + e.Name);
                if (e.IsDir)
                {
                    FileStore.Mkcol(space.Db, target);
                    foreach (var c in files.Children(e.Id)) await Copy(c, target);
                }
                else
                {
                    await using var s = files.OpenRead(e.Id);
                    await FileStore.PutAsync(space.Db, target, s, e.Mime, ct: ct);
                }
                copied.Add(target);
            }
            foreach (var id in body.Ids)
                if (files.Find(id) is { } e) await Copy(e, folder);
            return Results.Ok(new { copied });
        });
    }
}
