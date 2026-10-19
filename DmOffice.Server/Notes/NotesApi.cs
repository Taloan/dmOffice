using System.Text;
using System.Text.Json;
using DmOffice.Core;
using Notizi.Sync;

namespace DmOffice.Notes;

/// <summary>
/// Notizen in der Weboberflaeche. Bearbeitet wird wie auf dem Handy:
/// Absaetze und Listen frei, Tabellen, Bilder und Codebloecke gesperrt und
/// zeichengenau zurueck (ProtectedBlocks) - notizi am PC nimmt die Fassung
/// sonst nicht an.
/// </summary>
public static class NotesApi
{
    public record Meta(string? CustomTitle, string? Color, bool? Important, string? Folder, bool ClearCustomTitle = false);
    public record NewNote(string? Folder);
    public record TaskSet(long Note, int Block, int N, string Text, bool Done);
    public record FolderBody(string Name, string? Parent);
    public record DatabaseBody(string Folder);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/notes").RequireAuthorization();

        api.MapGet("/status", (HttpContext http) => NotesSync.Status(http.Space()));

        api.MapPost("/sync", async (HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            await sync.RunAsync(http.Space(), ct);
            return NotesSync.Status(http.Space());
        });

        // Datenbanken unter der Wurzel - wie die Auswahl "Datenbank" in notizi
        api.MapGet("/databases", async (HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var n = space.Settings.Notizi;
            return new { current = n.DbFolder, list = string.IsNullOrEmpty(n.E2eePassword) ? null : await NotesSync.ScanAsync(space, ct) };
        });

        // Wechseln: erst die bisherige abgleichen (nichts geht verloren), dann die neue laden. Hier liegt nur
        // ein Zwischenspeicher - die Notizen selbst stehen verschluesselt im Abgleichordner.
        api.MapPost("/database", async (DatabaseBody body, HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var folder = Notizi.Sync.SyncCatalog.FolderName(body.Folder);
            if (string.IsNullOrEmpty(space.Settings.Notizi.E2eePassword))
                return Results.BadRequest(new { error = "Erst das E2EE-Passwort unter Einstellungen → Notizen eintragen." });
            if (folder == space.Settings.Notizi.DbFolder) return Results.Ok(NotesSync.Status(space));

            var db = NotesDb.Of(space);
            if (space.Settings.Notizi.Enabled && !string.IsNullOrEmpty(space.Settings.Notizi.DbFolder))
            {
                await sync.RunAsync(space, ct);
                if (db.PendingCount() > 0)
                    return Results.BadRequest(new { error = "Nicht alles ist abgeglichen - erst den Fehler beim Abgleich beheben, sonst ginge etwas verloren." });
                db.Clear();
            }
            // Ohne bisherigen Abgleich bleiben die Notizen hier und gehen beim ersten Durchgang in die gewaehlte Datenbank
            space.UpdateSettings(s =>
            {
                s.Notizi.Enabled = true;
                s.Notizi.DbFolder = folder;
                s.Notizi.DbId = null;
            });
            await sync.RunAsync(space, ct);
            return Results.Ok(NotesSync.Status(space));
        });

        api.MapGet("/folders", (HttpContext http) =>
            http.Space().Db.Query("SELECT sync_id, parent, name, sort_order FROM note_folders ORDER BY sort_order, name",
                r => new NoteFolder { SyncId = r.GetString(0), Parent = r.IsDBNull(1) ? null : r.GetString(1), Name = r.GetString(2), SortOrder = r.GetDouble(3) }));

        api.MapPost("/folders", (FolderBody body, HttpContext http, NotesSync sync) =>
        {
            var space = http.Space();
            var id = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow.ToString("o");
            space.Db.Exec("INSERT INTO note_folders(sync_id,sync_rev,parent,name,sort_order,created,modified) VALUES($s,1,$p,$n,(SELECT COALESCE(MAX(sort_order),0)+1 FROM note_folders),$m,$m)",
                ("$s", id), ("$p", body.Parent), ("$n", body.Name.Trim().Length > 0 ? body.Name.Trim() : "Neuer Ordner"), ("$m", now));
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok(new { id });
        });

        api.MapPut("/folders/{id}", (string id, FolderBody body, HttpContext http, NotesSync sync) =>
        {
            var space = http.Space();
            space.Db.Exec("UPDATE note_folders SET name=$n, modified=$m, sync_rev=sync_rev+1 WHERE sync_id=$s",
                ("$n", body.Name.Trim()), ("$m", DateTime.UtcNow.ToString("o")), ("$s", id));
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        api.MapDelete("/folders/{id}", (string id, HttpContext http, NotesSync sync) =>
        {
            var space = http.Space();
            var db = space.Db;
            if (Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM notes WHERE folder=$f AND deleted IS NULL", ("$f", id))) > 0
                || Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM note_folders WHERE parent=$f", ("$f", id))) > 0)
                return Results.BadRequest(new { error = "Der Ordner ist nicht leer." });
            db.Exec("DELETE FROM note_folders WHERE sync_id=$s", ("$s", id));
            db.Exec("INSERT OR REPLACE INTO note_tombstones(sync_id, kind) VALUES($s, 'folder')", ("$s", id));
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        // Systemliste "Notizi-Aufgaben": alle Kaestchen aus den Notizen
        api.MapGet("/tasks", (HttpContext http) => NoteTasks.All(NotesDb.Of(http.Space())));

        api.MapPost("/tasks/set", (TaskSet body, HttpContext http, NotesSync sync) =>
        {
            var space = http.Space();
            if (!NoteTasks.Set(NotesDb.Of(space), body.Note, body.Block, body.N, body.Text, body.Done))
                return Results.Conflict(new { error = "Die Notiz wurde inzwischen geändert – bitte neu laden." });
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(10));
            return Results.Ok();
        });

        api.MapGet("/", (string? folder, string? view, string? q, HttpContext http) =>
        {
            var db = NotesDb.Of(http.Space());
            List<NoteRow> notes = !string.IsNullOrWhiteSpace(q) ? db.Search(q)
                : view switch
                {
                    "archive" => db.GetArchived(),
                    "trash" => db.GetTrashed(),
                    "all" => db.GetAllActive(),
                    "important" => db.GetAllActive().Where(n => n.Important).ToList(),
                    _ => db.GetNotes(string.IsNullOrEmpty(folder) ? null : folder)
                };
            var counts = db.CountArchiveAndTrash();
            return new
            {
                archived = counts.Archived, trashed = counts.Trashed,
                items = notes.Select(n => new
                {
                    n.Id, n.SyncId, n.Folder, title = n.DisplayTitle, n.Snippet, n.Color, n.Modified, n.Created, n.Important,
                    attachments = n.Attachments.Count, n.Archived, n.Deleted
                })
            };
        });

        api.MapGet("/{id:long}", (long id, HttpContext http) =>
        {
            var space = http.Space();
            var db = NotesDb.Of(space);
            var note = db.GetNote(id) ?? throw new KeyNotFoundException("Notiz nicht gefunden.");
            var split = NoteMerge.Split(note.Html);
            var blocks = split?.Blocks ?? new List<string> { note.Html };
            var flags = split == null ? blocks.Select(_ => true).ToList() : blocks.Select(ProtectedBlocks.IsProtectedBlock).ToList();
            return new
            {
                note.Id, note.SyncId, note.Folder, note.CustomTitle, title = note.DisplayTitle, note.Color, note.Important,
                note.Modified, note.Created, note.Archived, note.Deleted,
                blocks, @protected = flags, readOnly = split == null,
                attachments = note.Attachments.Select(a => new { a.Name, a.Mime, a.Size, a.Blob }),
                pending = db.IsPending(id),
                version = Version(note.Html)
            };
        });

        api.MapPost("/", (NewNote body, HttpContext http) =>
            Results.Ok(new { id = NotesDb.Of(http.Space()).CreateNote(string.IsNullOrEmpty(body.Folder) ? null : body.Folder) }));

        // Inhalt aus dem Editor: unveraenderte und gesperrte Bloecke als Nummer, alles andere als XHTML.
        // Die Nummern gelten fuer die Fassung "version" - hat der Abgleich die Notiz inzwischen
        // geaendert, passt sie nicht mehr: 409 statt fremde Aenderungen zu ueberschreiben.
        api.MapPut("/{id:long}/content", async (long id, HttpRequest request, HttpContext http, NotesSync sync) =>
        {
            var space = http.Space();
            var db = NotesDb.Of(space);
            var note = db.GetNote(id) ?? throw new KeyNotFoundException();
            var split = NoteMerge.Split(note.Html) ?? throw new InvalidOperationException("Diese Notiz lässt sich hier nur lesen.");

            using var doc = await JsonDocument.ParseAsync(request.Body);
            if (doc.RootElement.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String
                && ver.GetString() != Version(note.Html))
                return Results.Conflict(new { error = "Die Notiz wurde inzwischen auf einem anderen Gerät geändert." });
            var html = new StringBuilder(split.Head);
            foreach (var block in doc.RootElement.GetProperty("blocks").EnumerateArray())
            {
                if (block.TryGetProperty("i", out var i))
                {
                    var n = i.GetInt32();
                    if (n < 0 || n >= split.Blocks.Count) throw new ArgumentException("Block gibt es nicht.");
                    html.Append(split.Blocks[n]);
                }
                else html.Append(Clean(block.GetProperty("html").GetString() ?? ""));
            }
            html.Append(split.Tail);
            var plain = doc.RootElement.GetProperty("plain").GetString() ?? "";

            var result = html.ToString();
            if (!ProtectedBlocks.Kept(note.Html, result))
                return Results.BadRequest(new { error = "Gesperrte Blöcke wurden verändert - nicht gespeichert." });
            if (result != note.Html)
            {
                db.SaveNote(id, result, plain.Replace("\r", "").Replace("\n", "\r\n"));
                sync.Kick(space.User.Id, TimeSpan.FromSeconds(10));
            }
            // Neue Fassung und ihre Bloecke, damit der Editor seine Nummern nachfuehren kann
            var after = NoteMerge.Split(result);
            return Results.Ok(new
            {
                saved = result != note.Html,
                version = Version(result),
                @protected = after?.Blocks.Select(ProtectedBlocks.IsProtectedBlock).ToList()
            });
        });

        api.MapPut("/{id:long}/meta", (long id, Meta body, HttpContext http, NotesSync sync) =>
        {
            var space = http.Space();
            var db = NotesDb.Of(space);
            if (body.ClearCustomTitle) db.SetCustomTitle(id, null);
            else if (body.CustomTitle != null) db.SetCustomTitle(id, body.CustomTitle);
            if (body.Color != null) db.SetColor(id, body.Color);
            if (body.Important is bool imp) db.SetImportant(id, imp);
            if (body.Folder != null)
                space.Db.Exec("UPDATE notes SET folder=$f, modified=$m, sync_rev=sync_rev+1 WHERE id=$id",
                    ("$f", body.Folder.Length == 0 ? null : body.Folder), ("$m", DateTime.UtcNow.ToString("o")), ("$id", id));
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(10));
            return Results.Ok();
        });

        api.MapPost("/{id:long}/{action}", (long id, string action, HttpContext http, NotesSync sync) =>
        {
            var space = http.Space();
            var db = NotesDb.Of(space);
            switch (action)
            {
                case "archive": db.ArchiveNote(id); break;
                case "restore": db.RestoreNote(id); break;
                case "trash": db.TrashNote(id); break;
                default: return Results.NotFound();
            }
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        api.MapDelete("/{id:long}", (long id, HttpContext http, NotesSync sync) =>
        {
            var space = http.Space();
            NotesDb.Of(space).DeleteNote(id);
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        api.MapGet("/blob/{name}", (string name, bool? download, string? file, HttpContext http) =>
        {
            var data = NotesDb.Of(http.Space()).GetBlob(name) ?? throw new KeyNotFoundException();
            if (download == true) return Results.File(data, "application/octet-stream", file ?? name);
            var mime = Sniff(data);
            return Results.File(data, mime.StartsWith("image/") ? mime : "application/octet-stream");
        });

        api.MapPost("/{id:long}/attachments", async (long id, HttpRequest request, HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var form = await request.ReadFormAsync(ct);
            foreach (var file in form.Files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                var data = ms.ToArray();
                NotesDb.Of(space).AddAttachment(id, file.FileName, file.ContentType, data, await NotesSync.BlobNameAsync(space, data, ct));
            }
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        }).DisableAntiforgery();

        api.MapDelete("/{id:long}/attachments/{name}", (long id, string name, HttpContext http, NotesSync sync) =>
        {
            var space = http.Space();
            NotesDb.Of(space).RemoveAttachment(id, name);
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        // Ein Bild fuer den Editor: Name wie im Abgleichordner, damit notizi es findet
        api.MapPost("/images", async (HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault() ?? throw new ArgumentException("Kein Bild.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var data = ms.ToArray();
            var name = await NotesSync.BlobNameAsync(space, data, ct);
            NotesDb.Of(space).PutBlob(name, data);
            return Results.Ok(new { name });
        }).DisableAntiforgery();
    }

    /// <summary>Kurzer Fingerabdruck der Fassung (SHA-256, hex, gekuerzt) - fuer die Pruefung beim Speichern.</summary>
    private static string Version(string html) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(html)))[..16].ToLowerInvariant();

    /// <summary>Ein Block aus dem Editor: wohlgeformt, ohne Skript und Ereignisse - notizi liest XHTML.</summary>
    private static string Clean(string html)
    {
        var sanitizer = new Ganss.Xss.HtmlSanitizer();
        sanitizer.AllowedSchemes.Add("blob");
        sanitizer.AllowedSchemes.Add("data");
        var clean = sanitizer.Sanitize(html);
        // XHTML: leere Elemente schliessen, &nbsp; als Zeichen
        clean = System.Text.RegularExpressions.Regex.Replace(clean, "<(br|img|hr)([^>]*?)(?<!/)>", "<$1$2 />");
        return clean.Replace("&nbsp;", "&#160;");
    }

    private static string Sniff(byte[] d) =>
        d.Length > 4 && d[0] == 0x89 && d[1] == 0x50 ? "image/png" :
        d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 ? "image/jpeg" :
        d.Length > 4 && d[0] == 0x47 && d[1] == 0x49 ? "image/gif" :
        d.Length > 12 && d[8] == 0x57 && d[9] == 0x45 ? "image/webp" :
        d.Length > 2 && d[0] == 0x42 && d[1] == 0x4D ? "image/bmp" : "application/octet-stream";
}
