using System.Globalization;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Search;

namespace DmOffice.Dms;

/// <summary>API vom DMS unter /api/dms (Web und App). Aus, wenn die Kennung „dms“ nicht freigeschaltet ist (Features).</summary>
public static class DmsApi
{
    /// <summary>Seitenbilder, Seitenzahl, Text im Rahmen: höchstens so viele PDFs zugleich (Poppler als eigener Prozess).</summary>
    private static readonly SemaphoreSlim PageGate = new(2);

    private static IResult TileResult(HttpContext http, DmsStore store, byte[] jpeg)
    {
        http.Response.Headers.CacheControl = store.Tresor ? "no-store" : "private, max-age=600";
        return Results.File(jpeg, "image/jpeg");
    }

    public record EntryBody(string? Kind, string? Name, string? Parent, string? Color, string? Match, string? MatchText, bool? CaseSensitive);
    public record DocBody(string? Title, string? DocDate, string? Correspondent, string? Doctype, List<string>? Tags, List<string>? AddTags,
        List<string>? RemoveTags, bool? Confirm, string? Role = null, string? Amount = null);
    public record FixedBody(bool Fixed);
    public record BatchBody(List<long> Ids, DocBody Change);
    public record IdsBody(List<long> Ids);
    public record ImportFilesBody(List<string> Paths, string? Profile, bool? Move);
    public record MoveAreaBody(List<long> Ids, string To, string? Profile);
    public record PauseBody(bool Paused);
    public record PaperlessBody(string? Mount, string? Folder, List<string>? Skip, double? Share, string? Profile);

    public static void Map(WebApplication app)
    {
        DmsHost.Current = new DmsHost(app.Services.GetRequiredService<ServerConfig>());
        // Dokumente führen ihren Text selbst: die Inhaltssuche der Dateiablage liest sie nicht doppelt, findet sie aber über das DMS
        TextIndex.SkipFile = (space, path) => Features.On(Features.Dms) && Dms.IsDocPath(space.Db, path);
        TextIndex.ExtraFileHits = (space, query, under, limit) => Features.On(Features.Dms) ? Search(space, query, under, limit) : [];
        // Endgültig gelöscht heißt auch im DMS sofort weg; das stündliche Aufräumen bleibt als Netz
        FileStore.TrashPurged = db => DmsImport.ForgetGone(db);
        // Fixierte Dokumente: Die Sperre sitzt in der Dateiablage, damit sie für Web, App, WebDAV, Office und alle anderen Wege gilt
        FileStore.HasFixed = Dms.HasFixed;

        var api = app.MapGroup("/api/dms").RequireAuthorization();
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var space = ctx.HttpContext.Space();
            if (FileStore.FilesOff(space.Db)) return Results.Json(new { error = "Die Dateiablage ist für dieses Konto abgeschaltet." }, statusCode: 403);
            return await next(ctx);
        });
        DmsHost Host() => DmsHost.Current!;
        // Bereich der Anfrage: area=tresor → Tresor (nur offen auf diesem Gerät, sonst 423), sonst der offene Bereich
        DmsStore Store(HttpContext http, string? area) =>
            area == "tresor" ? DmsStore.ForTresor(http.Space().Db, Core.Tresor.Db(http)) : http.Space().Db;
        // Dokumente, die bei geschlossenem Tresor abgelegt wurden, übernimmt der Tresor beim Öffnen
        var spaces = app.Services.GetRequiredService<Spaces>();
        DmsImport.SpaceOf = id => spaces.Get(id);
        Core.Tresor.Absorbers["dms"] = DmsImport.Absorb;

        // Stand: Texterkennung, PDF/A, KI, Posteingang
        // Abzeichen in der linken Leiste: Summe der Inboxen (Dateiablage, Tresor nur offen auf diesem Gerät, dazu was vor dem Tresor wartet); legt nichts an
        api.MapGet("/inbox-count", (HttpContext http) =>
        {
            var space = http.Space();
            static bool Used(DmsStore s) => s.Scalar("SELECT 1 FROM sqlite_master WHERE name = 'dms_docs'") != null;
            DmsStore files = space.Db;
            var n = Used(files) ? Dms.InboxCount(files) : 0;
            if (Core.Tresor.IsOpen(http, touch: false) && Core.Tresor.DbOf(space.User.Id) is { } tdb && DmsStore.ForTresor(space.Db, tdb) is var t && Used(t))
                n += Dms.InboxCount(t);
            // Vor dem geschlossenen Tresor Wartendes zählt mit (es landet nach dem Öffnen in der Tresor-Inbox)
            if (Core.Tresor.Exists(space.User.Id)) n += Core.Tresor.Pending(space.User.Id, "dms");
            return new { count = n };
        });

        // Wer eine Rolle gerade innehat (Hinweis „Ersetzt das bisherige Dokument …“ beim Zuordnen); im geschlossenen Tresor nur, dass es dort liegt
        api.MapGet("/roles/holder", (string role, HttpContext http) =>
        {
            var space = http.Space();
            if (!Dms.Roles(space.Db).TryGetValue(role, out var h)) return Results.Ok(new { holder = (object?)null });
            DmsStore? store = !h.Tresor ? space.Db
                : Core.Tresor.IsOpen(http, touch: false) && Core.Tresor.DbOf(space.User.Id) is { } tdb ? DmsStore.ForTresor(space.Db, tdb) : null;
            if (store == null) return Results.Ok(new { holder = (object?)new { id = h.Doc, tresor = true, title = (string?)null, docDate = (string?)null } });
            var d = Dms.ByIds(store, [h.Doc]).FirstOrDefault();
            // Im Papierkorb: wird nicht ersetzt, die Rolle ist gerade frei
            return Results.Ok(new { holder = d == null || d.Trashed ? null : (object?)new { id = d.Id, tresor = h.Tresor, title = d.Title, docDate = d.DocDate ?? d.Imported.ToString("yyyy-MM-dd") } });
        });

        api.MapGet("/state", (HttpContext http) =>
        {
            var space = http.Space();
            Dms.Ensure(space.Db);
            var host = Host();
            var settings = DmsImport.Settings(space.Db);
            return new
            {
                paused = settings.Paused,
                ocr = host.OcrAvailable,
                ocrReason = Ocr.Current.Reason,
                pdfa = host.PdfAAvailable,
                aiAllowed = host.Allowed(space),
                aiService = host.Allowed(space) ? host.Service : null,
                ai = settings.Ai,
                inbox = Dms.InboxCount(space.Db),
                all = Dms.Docs(space.Db, new Dms.Filter(), 0, 1).Total,
                queued = Dms.QueuedCount(space.Db),
                // Tresor: eingerichtet, offen auf diesem Gerät, Posteingang darin, noch nicht übernommene Ablagen
                tresor = Core.Tresor.Exists(space.User.Id),
                tresorOpen = Core.Tresor.IsOpen(http, touch: false),
                tresorInbox = Core.Tresor.IsOpen(http, touch: false) && Core.Tresor.DbOf(space.User.Id) is { } tdb ? Dms.InboxCount(DmsStore.ForTresor(space.Db, tdb)) : 0,
                tresorQueued = Core.Tresor.IsOpen(http, touch: false) && Core.Tresor.DbOf(space.User.Id) is { } tdb2
                    ? Dms.QueuedCount(DmsStore.ForTresor(space.Db, tdb2)) : 0,
                tresorAll = Core.Tresor.IsOpen(http, touch: false) && Core.Tresor.DbOf(space.User.Id) is { } tdb3
                    ? Dms.Docs(DmsStore.ForTresor(space.Db, tdb3), new Dms.Filter(), 0, 1).Total : 0,
                // Bei geschlossenem Tresor abgelegt (Ablageprofil mit Ziel Tresor): wartet, bis er geöffnet wird
                tresorWaiting = Core.Tresor.Exists(space.User.Id) ? Core.Tresor.Pending(space.User.Id, "dms") : 0,
            };
        });

        api.MapPut("/settings", (DmsSettings body, HttpContext http) =>
        {
            // Anhalten gehört zur Warteschlange (eigener Aufruf), nicht zu den Einstellungen
            body.Paused = DmsImport.Settings(http.Space().Db).Paused;
            DmsImport.SaveSettings(http.Space().Db, body);
            return Results.Ok(body);
        });

        // Warteschlange anhalten bzw. fortsetzen
        api.MapPost("/queue/pause", (PauseBody b, HttpContext http) =>
        {
            var db = http.Space().Db;
            var s = DmsImport.Settings(db);
            s.Paused = b.Paused;
            DmsImport.SaveSettings(db, s);
            if (!b.Paused) DmsJob.Kick();
            return new { paused = s.Paused };
        });

        // ---------------- Listen ----------------

        api.MapGet("/lists", (HttpContext http) => Dms.EntriesWithCounts(http.Space().Db));

        api.MapPost("/lists", (EntryBody b, HttpContext http) =>
        {
            var e = Dms.Create(http.Space().Db, b.Kind ?? "", b.Name, string.IsNullOrEmpty(b.Parent) ? null : b.Parent, b.Color, b.Match, b.MatchText, b.CaseSensitive ?? false);
            Reassess(http.Space());
            return e;
        });

        api.MapPut("/lists/{id}", (string id, EntryBody b, HttpContext http) =>
        {
            var e = Dms.Update(http.Space().Db, id, b.Name, b.Parent, b.Color, b.Match, b.MatchText, b.CaseSensitive);
            Reassess(http.Space());
            return e;
        });

        api.MapDelete("/lists/{id}", (string id, HttpContext http) =>
        {
            var space = http.Space();
            foreach (var d in Dms.Delete(space.Db, id)) DmsImport.Refile(space, Host(), d);
            return Results.NoContent();
        });

        api.MapPost("/lists/suggestion", (string? lang, HttpContext http) => new { created = Dms.CreateSuggestion(http.Space().Db, lang) });

        // ---------------- Dokumente ----------------

        api.MapGet("/docs", (HttpContext http, string? q, string? correspondent, string? doctype, string? tags, string? docFrom, string? docTo,
            string? importedFrom, string? importedTo, bool? inbox, string? profile, string? mime, bool? noCorrespondent, bool? noDoctype, bool? noTags,
            bool? paperlessDiffers, bool? queued, string? sort, bool? desc, string? area, int? offset, int? limit, string? role, bool? noRole, bool? @fixed) =>
        {
            var space = http.Space();
            var store = Store(http, area);
            long? Day(string? d, bool end) => d != null && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var x)
                ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(x.ToDateTime(end ? new TimeOnly(23, 59, 59) : TimeOnly.MinValue), space.Zone)).ToUnixTimeSeconds() : null;
            var filter = new Dms.Filter(q, correspondent, doctype, tags?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(), docFrom, docTo,
                Day(importedFrom, false), Day(importedTo, true), inbox, profile, mime, noCorrespondent, noDoctype, noTags, paperlessDiffers, queued, sort, desc ?? false, role, noRole, @fixed);
            var (items, total) = Dms.Docs(store, filter, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 100, 1, 500));
            return new { items, total };
        });

        api.MapGet("/docs/{id:long}", (long id, string? area, HttpContext http) => Dms.Get(Store(http, area), id));

        // Plausible Daten aus dem Text zur Auswahl (nicht nach dem Importtag)
        api.MapGet("/docs/{id:long}/dates", (long id, string? area, HttpContext http) =>
        {
            var store = Store(http, area);
            var doc = Dms.Get(store, id);
            var imported = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(doc.Imported, DateTimeKind.Utc), http.Space().Zone));
            return DmsAssign.FindDates(Dms.Text(store, id) ?? "", imported).Take(20)
                .Select(c => new { date = c.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), count = c.Count, context = c.Context, labeled = c.Labeled });
        });

        api.MapGet("/docs/{id:long}/text", (long id, string? area, HttpContext http) => new { text = Dms.Text(Store(http, area), id) ?? "" });

        api.MapPut("/docs/{id:long}", (long id, string? area, DocBody b, HttpContext http) =>
        {
            var store = Store(http, area);
            Change(http.Space(), [id], b, store);
            return Dms.Get(store, id);
        });

        api.MapPost("/docs/batch", (BatchBody b, string? area, HttpContext http) =>
        {
            var store = Store(http, area);
            Change(http.Space(), b.Ids, b.Change, store);
            return Results.Ok(Dms.ByIds(store, b.Ids));
        });

        // Vorschlag der KI für einen neuen Korrespondenten übernehmen
        api.MapPost("/docs/{id:long}/adopt-correspondent", (long id, HttpContext http) =>
        {
            var space = http.Space();
            var doc = Dms.Get(space.Db, id);
            if (doc.AiSuggestion is not { Length: > 0 } name) throw new ArgumentException("Kein Vorschlag vorhanden.");
            var existing = Dms.FindByName(Dms.Entries(space.Db, Dms.Correspondent), Dms.Correspondent, name)
                ?? Dms.Create(space.Db, Dms.Correspondent, name, null, null, Dms.Auto, null, false);
            space.Db.Exec("UPDATE dms_docs SET ai_suggestion = NULL WHERE id = $d", ("$d", id));
            // Zuordnen; Change ordnet danach die übrigen Dokumente neu ein (dieselbe Empfehlung an anderen Dokumenten)
            Change(space, [id], new DocBody(null, null, existing.Id, null, null, null, null, null));
            return Dms.Get(space.Db, id);
        });

        // Nach Vorgabe ablegen (auch „von Hand abgelegte“); nur Bestätigte - Unbestätigte legt „Bestätigen“ nach dem Ordnermuster ab
        api.MapPost("/docs/refile", (IdsBody b, string? area, HttpContext http) =>
        {
            var space = http.Space();
            var store = Store(http, area);
            foreach (var d in Dms.ByIds(store, b.Ids).Where(d => !d.Inbox)) DmsImport.Refile(space, Host(), d.Id, force: true, store);
            return Results.Ok(Dms.ByIds(store, b.Ids));
        });

        // Zwischen offenem Bereich und Tresor verschieben (Tresor offen auf diesem Gerät): Angaben, Text und Original kommen mit
        api.MapPost("/docs/move-area", async (MoveAreaBody b, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var tresor = DmsStore.ForTresor(space.Db, Core.Tresor.Db(http));
            var (from, to) = b.To == "tresor" ? ((DmsStore)space.Db, tresor) : (tresor, (DmsStore)space.Db);
            int moved = 0, notPlaced = 0, already = 0, merged = 0;
            var failed = new List<object>();
            foreach (var id in b.Ids)
            {
                // Ein Dokument mit Fehler hält die übrigen nicht auf; es bleibt, wo es war (ein halb fertiges führt der nächste Versuch zusammen)
                try
                {
                    switch (await DmsImport.MoveAreaAsync(space, Host(), from, to, id, b.Profile, ct))
                    {
                        case DmsImport.Moved.Done: moved++; break;
                        case DmsImport.Moved.NotPlaced: notPlaced++; break;
                        case DmsImport.Moved.Merged: merged++; break;
                        default: already++; break;
                    }
                }
                catch (Exception e) when (e is IOException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException
                    or System.Security.Cryptography.CryptographicException or HttpRequestException)
                {
                    var title = Dms.ByIds(from, [id]).FirstOrDefault()?.Title ?? "#" + id;
                    SystemLog.Add(Microsoft.Extensions.Logging.LogLevel.Warning, "DMS", $"Verschieben {(b.To == "tresor" ? "in den Tresor" : "aus dem Tresor")}, Dokument {id}: {e.GetType().Name}: {e.Message}");
                    failed.Add(new { id, title, error = e is IOException && (e.Message.Contains('/') || e.Message.Contains('\\')) ? "Speicher- oder Verbindungsfehler." : e.Message });
                }
            }
            // Was nicht verschoben wurde, mit Grund - sonst stand da nur „0 Dokumente verschoben“
            return new { moved, notPlaced, already, merged, failed };
        });

        // Löschen: Datei in den Papierkorb (dort bleibt das Dokument samt Angaben wiederherstellbar)
        api.MapPost("/docs/delete", async (IdsBody b, string? area, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var store = Store(http, area);
            // Vor dem ersten Löschen prüfen: Sonst wäre von einer Auswahl nur ein Teil gelöscht
            if (Dms.ByIds(store, b.Ids).Any(d => d.Fixed)) throw new DavForbiddenException("Fixierte Dokumente lassen sich nicht löschen. Bitte zuerst die Fixierung aufheben.");
            foreach (var d in Dms.ByIds(store, b.Ids))
            {
                // Noch nicht abgelegt (wartend oder mit Fehler): es gibt nur Original und Eintrag
                if (d.FileId == 0 && d.Mount == null) { DmsImport.ForgetUnplaced(store, d.Id); continue; }
                // Im Tresor und im externen Ordner gibt es keinen Papierkorb: Angaben gleich mit weg
                if (await DmsImport.RemoveFileAsync(space, Host(), store, d, purge: false, ct))
                    DmsImport.Forget(store, d.Id, store.Scalar("SELECT original_blob FROM dms_docs WHERE id = $d", ("$d", d.Id)) as string);
            }
            return Results.NoContent();
        });

        // Fixieren oder die Fixierung bewusst aufheben
        api.MapPost("/docs/{id:long}/fixed", (long id, string? area, FixedBody b, HttpContext http) =>
        {
            var store = Store(http, area);
            Dms.SetFixed(store, id, b.Fixed);
            return Dms.Get(store, id);
        });

        // Erneut verarbeiten (nach einem Fehler)
        api.MapPost("/docs/{id:long}/retry", (long id, string? area, HttpContext http) =>
        {
            var db = Store(http, area);
            if (db.Exec("UPDATE dms_docs SET state = 'queued', tries = 0, error = NULL WHERE id = $d AND state = 'error' AND original_blob IS NOT NULL", ("$d", id)) == 0)
                throw new ArgumentException("Dieses Dokument lässt sich nicht erneut verarbeiten.");
            DmsJob.Kick();
            return Results.NoContent();
        });

        api.MapGet("/docs/{id:long}/original", (long id, string? area, HttpContext http) =>
        {
            var s = DmsImport.OpenOriginal(http.Space(), Host(), id, out var name, out var mime, Store(http, area));
            return Results.File(s, mime ?? "application/octet-stream", name, enableRangeProcessing: true);
        });

        // Herunterladen unter dem Titel des Dokuments, mehrere als ZIP - auch aus dem Tresor, solange er offen ist
        api.MapPost("/docs/download", async (HttpContext http, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var store = Store(http, form["area"].ToString() == "tresor" ? "tresor" : null);
            var ids = form["ids"].Select(x => long.TryParse(x, out var v) ? v : 0).Where(v => v > 0).Distinct().Take(MaxDownload).ToList();
            if (ids.Count == 0) return Results.BadRequest(new { error = "Nichts ausgewählt." });
            async Task<byte[]> Read(DmsDoc d)
            {
                if (DmsImport.IsPlaced(d)) return await DmsImport.ReadFileAsync(http.Space(), Host(), store, d, ct);
                using var original = DmsImport.OpenOriginal(http.Space(), Host(), d.Id, out _, out _, store);
                using var copy = new MemoryStream();
                await original.CopyToAsync(copy, ct);
                return copy.ToArray();
            }
            var docs = ids.Select(id => Dms.Get(store, id)).ToList();
            if (docs.Count == 1)
            {
                try { return Results.File(await Read(docs[0]), "application/octet-stream", DownloadName(docs[0])); }
                catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException)
                { return Results.Json(new { error = "Die Datei lässt sich gerade nicht lesen." }, statusCode: 503); }
            }
            http.Response.ContentType = "application/zip";
            http.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = "Dokumente.zip" }.ToString();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // ZipArchive schreibt synchron; die Antwort kommt trotzdem stückweise beim Browser an
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
            await http.Response.StartAsync(ct);
            await using var buffered = new BufferedStream(http.Response.Body, 256 * 1024);
            using (var zip = new System.IO.Compression.ZipArchive(buffered, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<string>();
                foreach (var d in docs)
                {
                    var name = DownloadName(d);
                    var stem = Path.GetFileNameWithoutExtension(name);
                    var ext = Path.GetExtension(name);
                    for (var n = 2; !names.Add(name); n++) name = $"{stem} ({n}){ext}";
                    try
                    {
                        var data = await Read(d);
                        await using var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Fastest).Open();
                        await entry.WriteAsync(data, ct);
                    }
                    catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or KeyNotFoundException) { missing.Add(name); }
                }
                if (missing.Count > 0)
                {
                    await using var note = zip.CreateEntry("Nicht enthalten.txt").Open();
                    await note.WriteAsync(System.Text.Encoding.UTF8.GetBytes("Diese Dokumente ließen sich nicht lesen:\r\n" + string.Join("\r\n", missing)), ct);
                }
            }
            await buffered.FlushAsync(ct);
            return Results.Empty;
        }).DisableAntiforgery();

        // Inhalt der abgelegten Datei zum Ansehen (nötig für Dokumente im externen Ordner; geht für alle)
        api.MapGet("/docs/{id:long}/content", async (long id, string? area, HttpContext http, CancellationToken ct) =>
        {
            var store = Store(http, area);
            var doc = Dms.Get(store, id);
            if (!DmsImport.IsPlaced(doc)) return Results.NotFound();
            var name = Path.GetFileName(doc.Path ?? doc.OriginalName);
            byte[] data;
            try { data = await DmsImport.ReadFileAsync(http.Space(), Host(), store, doc, ct); }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException)
            {
                if (doc.Mount == null) return Results.Json(new { error = "Die Datei lässt sich gerade nicht lesen." }, statusCode: 503);
                // Ordner erreichbar, nur die Datei fehlt: das sagen
                bool missing;
                try { missing = await Host().StatAsync(http.Space(), doc.Mount, doc.Path!, ct) == null; }
                catch (Exception s) when (s is IOException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or KeyNotFoundException) { missing = false; }
                return missing ? Results.Json(new { error = "Diese Datei liegt nicht mehr im externen Ordner." }, statusCode: 404)
                    : Results.Json(new { error = "Der externe Ordner ist gerade nicht erreichbar." }, statusCode: 503);
            }
            // Typ kann aus einem Mail-Anhang stammen: gezeigt nur, was sicher ist
            var (mime, download) = Dav.FilesApi.Shown(http, doc.Mime, name, inline: true);
            if (download == null) http.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { Inline = true, FileName = name }.ToString();
            return Results.File(data, mime, download, enableRangeProcessing: true);
        });

        // Vorschau einer PDF als Seitenbilder (vom Server gerendert): der PDF-Betrachter des Browsers lässt sich im Rahmen nicht überall
        // einbetten (Vivaldi blockiert ihn unter der CSP der Seite), Bilder gehen überall
        api.MapGet("/docs/{id:long}/pages", async (long id, string? area, HttpContext http, CancellationToken ct) =>
        {
            var store = Store(http, area);
            var doc = Dms.Get(store, id);
            if (!DmsImport.IsPlaced(doc)) return Results.NotFound();
            await PageGate.WaitAsync(ct);
            try
            {
                var data = await DmsImport.ReadFileAsync(http.Space(), Host(), store, doc, ct);
                return Results.Ok(new { count = await Task.Run(() => Host().PdfPageCount(data), ct) });
            }
            finally { PageGate.Release(); }
        });
        api.MapGet("/docs/{id:long}/page/{n:int}", async (long id, int n, int? width, string? area, bool? tile, HttpContext http, CancellationToken ct) =>
        {
            var store = Store(http, area);
            var doc = Dms.Get(store, id);
            if (!DmsImport.IsPlaced(doc)) return Results.NotFound();
            // Kachel (Liste, Inbox): einmal gerechnet und im Bereich gespeichert (Tresor: in der Tresor-Datenbank) - sonst las jedes
            // Öffnen der Inbox jede PDF ganz (auch aus dem externen Ordner) und rechnete sie neu; 419 Kacheln hielten den Server an
            var tileWidth = (width ?? 360) <= 360 ? 360 : 720;
            var stamp = $"{doc.Mount}|{doc.Path}|{doc.FileId}|{doc.Size}";
            if (tile == true && n == 0)
            {
                var hit = store.Query("SELECT data FROM dms_tiles WHERE doc = $d AND width = $w AND stamp = $s", r => (byte[])r.GetValue(0),
                    ("$d", id), ("$w", tileWidth), ("$s", stamp)).FirstOrDefault();
                if (hit != null) return hit.Length == 0 ? Results.NotFound() : TileResult(http, store, hit);
            }
            // Wenige zugleich und nicht auf den Arbeits-Threads des Servers rechnen - die übrigen Anfragen warten nicht mit
            byte[]? jpeg;
            await PageGate.WaitAsync(ct);
            try
            {
                var data = await DmsImport.ReadFileAsync(http.Space(), Host(), store, doc, ct);
                var w = tile == true && n == 0 ? tileWidth : Math.Clamp(width ?? 1000, 200, 2000);
                jpeg = await Task.Run(() => Host().PdfPage(data, Math.Max(0, n), w), ct);
            }
            finally { PageGate.Release(); }
            if (tile == true && n == 0)
                store.Exec("INSERT INTO dms_tiles(doc, width, stamp, data) VALUES($d, $w, $s, $x) ON CONFLICT(doc, width) DO UPDATE SET stamp = $s, data = $x",
                    ("$d", id), ("$w", tileWidth), ("$s", stamp), ("$x", jpeg ?? []));
            if (jpeg == null) return Results.NotFound();
            // Kurz im Browser zwischenspeichern (privat); im Tresor nicht
            http.Response.Headers.CacheControl = store.Tresor ? "no-store" : "private, max-age=600";
            return Results.File(jpeg, "image/jpeg");
        });

        // Text in einem Rahmen auf einer Seite (Feld aus dem Dokument übernehmen): Textschicht der PDF, sonst Texterkennung des Ausschnitts
        api.MapPost("/docs/{id:long}/region", async (long id, DmsRegion.Area body, string? area, HttpContext http, CancellationToken ct) =>
        {
            var store = Store(http, area);
            var doc = Dms.Get(store, id);
            if (!DmsImport.IsPlaced(doc)) return Results.NotFound();
            var data = await DmsImport.ReadFileAsync(http.Space(), Host(), store, doc, ct);
            await PageGate.WaitAsync(ct);
            try { return Results.Ok(await Task.Run(() => DmsRegion.TextIn(data, body, Host(), ct), ct)); }
            finally { PageGate.Release(); }
        });

        // Angaben zu einer Datei der Ablage (Details in Dateien)
        api.MapGet("/of", (long fileId, HttpContext http) => Dms.OfFile(http.Space().Db, fileId) is { } d ? Results.Ok(d) : Results.NoContent());

        // ---------------- Import ----------------

        api.MapPost("/upload", async (string? profile, string? area, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            // Hochladen in der Ansicht „Tresor“: ohne Angabe das (Standard-)Ablageprofil mit Ziel Tresor
            if (profile == null && area == "tresor")
                profile = (DmsFiling.Of(space).Where(p => p.Target == "tresor").OrderByDescending(p => p.Default).FirstOrDefault()
                    ?? throw new ArgumentException("Es gibt kein Ablageprofil mit Ziel Tresor.")).Id;
            var results = new List<object>();
            await foreach (var file in MultipartUpload.Files(request, ct))
            {
                var r = await DmsImport.ImportAsync(space, file.Body, file.FileName, file.ContentType, new DmsImport.Info("upload", Profile: profile), ct);
                results.Add(new { name = file.FileName, id = r.Id, duplicate = r.Duplicate, dropped = r.Dropped });
            }
            return Results.Ok(new { files = results });
        }).DisableAntiforgery();

        // „Ins DMS aufnehmen“ aus der Dateiablage: Inhalt übernehmen, Datei danach in den Papierkorb (move, Vorgabe)
        api.MapPost("/import-files", async (ImportFilesBody b, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var results = new List<object>();
            foreach (var raw in b.Paths)
            {
                var path = FileStore.Normalize(raw);
                var f = FileStore.Get(space.Db, path);
                // Nur eigene Dateien; was nicht geht, zählt die Antwort mit, statt es wegzulassen
                if (f == null || f.IsDir || Host().Protected(space, path)) { results.Add(new { name = Path.GetFileName(path), skipped = true }); continue; }
                if (Dms.IsDocPath(space.Db, path)) { results.Add(new { name = f.Name, duplicate = true }); continue; }
                DmsImport.Result r;
                await using (var s = FileStore.OpenRead(space.Db, path))
                    r = await DmsImport.ImportAsync(space, s, f.Name, f.Mime, new DmsImport.Info("files", path, Profile: b.Profile, FileDate: f.Modified, Freed: b.Move != false ? f.Size : 0), ct);
                if (b.Move != false) FileStore.Delete(space.Db, path);
                results.Add(new { name = f.Name, id = r.Id, duplicate = r.Duplicate });
            }
            return Results.Ok(new { files = results });
        });

        // ---------------- Ablageprofile ----------------

        api.MapGet("/profiles", (string? lang, HttpContext http) =>
        {
            DmsFiling.FirstLanguageOverride = lang;
            try { return DmsFiling.Of(http.Space()); } finally { DmsFiling.FirstLanguageOverride = null; }
        });
        api.MapPost("/profiles", (DmsProfile p, HttpContext http) => { p.Id = Guid.NewGuid().ToString("N")[..12]; return DmsFiling.Save(http.Space(), p, Host(), Host()); });
        api.MapPut("/profiles/{id}", (string id, DmsProfile p, HttpContext http) =>
        {
            if (DmsFiling.Find(http.Space(), id) == null) throw new KeyNotFoundException("Ablageprofil nicht gefunden.");
            p.Id = id;
            return DmsFiling.Save(http.Space(), p, Host(), Host());
        });
        api.MapDelete("/profiles/{id}", (string id, HttpContext http) => { DmsFiling.Delete(http.Space(), id); return Results.NoContent(); });

        // Vorschau eines Musters mit einem Beispiel
        api.MapPost("/profiles/preview", (DmsProfile p, string? lang, HttpContext http) =>
        {
            foreach (var pattern in new[] { p.Folders ?? "", p.Names ?? "" })
                if (DmsFiling.Unknown(pattern) is { Count: > 0 } bad) return Results.Ok(new { error = "Unbekannter Platzhalter: {" + string.Join("}, {", bad) + "}" });
            p.Folders ??= "";
            p.Names ??= "";
            p.Unknown = string.IsNullOrWhiteSpace(p.Unknown) ? "Unbekannt" : p.Unknown;
            var example = DmsFiling.Example(lang ?? p.Language);
            return Results.Ok(new { path = DmsFiling.PathFor(p, example, ".pdf"), inbox = DmsFiling.PathFor(p, example, ".pdf", inbox: true) });
        });

        // Alle Dokumente eines Profils nach Vorgabe neu ablegen
        api.MapPost("/profiles/{id}/refile", (string id, HttpContext http) =>
        {
            var space = http.Space();
            var ids = space.Db.Query("SELECT id FROM dms_docs WHERE profile = $p AND state = 'done'", r => r.GetInt64(0), ("$p", id));
            var moved = ids.Count(d => DmsImport.Refile(space, Host(), d, force: true) != null);
            return new { count = moved };
        });

        // ---------------- Quellen ----------------

        api.MapGet("/sources", (HttpContext http) => DmsSources.Of(http.Space().Db));
        api.MapGet("/mounts", (HttpContext http) => Host().Mounts(http.Space()).Select(m => new { id = m.Id, name = m.Name,
            readOnly = Host().MountInfo(http.Space(), m.Id)?.ReadOnly ?? true }));
        api.MapPost("/sources", (DmsSource s, HttpContext http) =>
        {
            s.Id = Guid.NewGuid().ToString("N")[..12];
            var saved = DmsSources.Save(http.Space(), s, Host());
            DmsJob.Request(http.Space().User.Id, saved.Id);
            return saved;
        });
        api.MapPut("/sources/{id}", (string id, DmsSource s, HttpContext http) =>
        {
            if (DmsSources.Of(http.Space().Db).All(x => x.Id != id)) throw new KeyNotFoundException("Quelle nicht gefunden.");
            s.Id = id;
            return DmsSources.Save(http.Space(), s, Host());
        });
        api.MapDelete("/sources/{id}", (string id, HttpContext http) => { DmsSources.Delete(http.Space().Db, id); return Results.NoContent(); });
        api.MapPost("/sources/{id}/run", (string id, HttpContext http) => { DmsJob.Request(http.Space().User.Id, id); return Results.NoContent(); });

        // ---------------- Übernahme aus paperless-ngx ----------------

        api.MapPost("/paperless/read", async (PaperlessBody b, HttpContext http, CancellationToken ct) =>
        {
            var export = await DmsPaperless.ReadAsync(http.Space(), Host(), Mount(b), b.Folder ?? "", ct);
            return new
            {
                documents = export.Docs.Count, withFile = export.Docs.Count(d => d.Original != null), withArchive = export.Docs.Count(d => d.Archive != null),
                missing = export.Missing, entries = export.Entries,
            };
        });

        api.MapPost("/paperless/lists", async (PaperlessBody b, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var export = await DmsPaperless.ReadAsync(space, Host(), Mount(b), b.Folder ?? "", ct);
            return new { mapped = DmsPaperless.ApplyLists(space, export, (b.Skip ?? []).ToHashSet()).Count };
        });

        api.MapPost("/paperless/trial", async (PaperlessBody b, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var export = await DmsPaperless.ReadAsync(space, Host(), Mount(b), b.Folder ?? "", ct);
            return DmsPaperless.RunTrial(space, export, b.Share ?? 0.7);
        });

        api.MapPost("/paperless/import", (PaperlessBody b, HttpContext http) =>
        {
            var space = http.Space();
            if (!DmsPaperless.Start(space, Host(), Mount(b), b.Folder ?? "", b.Profile, app.Lifetime.ApplicationStopping))
                throw new InvalidOperationException("Die Übernahme läuft schon.");
            return Results.NoContent();
        });

        api.MapGet("/paperless/status", (HttpContext http) =>
        {
            var db = http.Space().Db;
            Dms.Ensure(db);
            return new
            {
                progress = DmsPaperless.StatusOf(http.Space().User.Id),
                differing = Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM dms_docs d WHERE {Dms.PlDiffers} AND d.inbox = 1") ?? 0),
                same = DmsPaperless.SameIds(db).Count,
                compared = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dms_docs WHERE pl = 1") ?? 0),
            };
        });

        // Gleiche bestätigen (lernt mit)
        api.MapPost("/paperless/confirm-same", (HttpContext http) =>
        {
            var space = http.Space();
            var ids = DmsPaperless.SameIds(space.Db);
            Change(space, ids, new DocBody(null, null, null, null, null, null, null, true));
            return new { count = ids.Count };
        });

        // Für die gewählten Dokumente die Angaben aus paperless übernehmen (als bestätigt)
        api.MapPost("/paperless/take", (IdsBody b, HttpContext http) =>
        {
            var space = http.Space();
            foreach (var d in Dms.ByIds(space.Db, b.Ids))
                foreach (var id in Dms.Apply(space.Db, [d.Id], DmsPaperless.TakePaperless(d))) DmsImport.Refile(space, Host(), id);
            return Results.Ok(Dms.ByIds(space.Db, b.Ids));
        });

        api.MapPost("/paperless/discard", (HttpContext http) => new { count = DmsPaperless.Discard(http.Space().Db) });

        // ---------------- Mailordner als Quelle ----------------

        api.MapGet("/mail-sources", (HttpContext http) => DmsMailSources.Of(http.Space().Db));
        api.MapGet("/mail-accounts", (HttpContext http) =>
        {
            if (DmsHost.Mail is not { } mail) return [];
            var space = http.Space();
            return mail.Accounts(space).Select(a => new { id = a.Id, name = a.Name, folders = mail.Folders(space, a.Id).Select(f => new { path = f.Path, name = f.Name }) }).ToList<object>();
        });
        api.MapPost("/mail-sources", (DmsMailSource s, HttpContext http) =>
        {
            if (DmsHost.Mail is not { } mail) throw new InvalidOperationException("Ohne Mail gibt es keine Mailordner als Quelle.");
            s.Id = Guid.NewGuid().ToString("N")[..12];
            var saved = DmsMailSources.Save(http.Space(), s, mail);
            DmsJob.Request(http.Space().User.Id, saved.Id);
            return saved;
        });
        api.MapPut("/mail-sources/{id}", (string id, DmsMailSource s, HttpContext http) =>
        {
            if (DmsHost.Mail is not { } mail) throw new InvalidOperationException("Ohne Mail gibt es keine Mailordner als Quelle.");
            if (DmsMailSources.Of(http.Space().Db).All(x => x.Id != id)) throw new KeyNotFoundException("Quelle nicht gefunden.");
            s.Id = id;
            return DmsMailSources.Save(http.Space(), s, mail);
        });
        api.MapDelete("/mail-sources/{id}", (string id, HttpContext http) => { DmsMailSources.Delete(http.Space().Db, id); return Results.NoContent(); });
        api.MapPost("/mail-sources/{id}/run", (string id, HttpContext http) => { DmsJob.Request(http.Space().User.Id, id); return Results.NoContent(); });

        // Dokumente zu einer Herkunft (etwa „Konto|Message-ID“ einer Mail): „schon im DMS abgelegt“
        api.MapGet("/by-ref", (string @ref, HttpContext http) => Dms.ByRef(http.Space().Db, @ref));

        // ---------------- Virtuelle Bäume ----------------

        api.MapGet("/tree", (string levels, string? area, HttpContext http) =>
        {
            var space = http.Space();
            var profiles = DmsFiling.Of(space).ToDictionary(p => p.Id, p => p.Name);
            return Dms.Tree(space, levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), id => profiles.GetValueOrDefault(id) ?? "",
                Store(http, area));
        });
    }

    private const int MaxDownload = 500;

    /// <summary>Dateiname zum Herunterladen: Titel des Dokuments mit der Endung der Datei.</summary>
    private static string DownloadName(DmsDoc d)
    {
        var file = Path.GetFileName(d.Path ?? d.OriginalName);
        var clean = new string((d.Title ?? "").Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c) && c != '/' && c != '\\').ToArray()).Trim().Trim('.');
        if (clean.Length == 0) return file;
        if (clean.Length > 120) clean = clean[..120].TrimEnd();
        return clean + Path.GetExtension(file);
    }

    private static string? Mount(PaperlessBody b) => string.IsNullOrEmpty(b.Mount) ? null : b.Mount;

    /// <summary>Angaben ändern und - wo das Profil es will - neu ablegen.</summary>
    /// <summary>Nach einer Änderung an den Listen: offene Inbox-Dokumente neu einordnen und ablegen (offener Bereich; der Tresor beim nächsten Ändern dort).</summary>
    private static void Reassess(UserSpace space)
    {
        foreach (var id in Dms.Reassess(space.Db)) DmsImport.Refile(space, DmsHost.Current!, id, false, space.Db);
    }

    private static void Change(UserSpace space, IEnumerable<long> ids, DocBody b, DmsStore? store = null)
    {
        store ??= space.Db;
        var changed = Dms.Apply(store, ids, new Dms.Change(b.Title, b.DocDate, b.Correspondent, b.Doctype, b.Tags, b.AddTags, b.RemoveTags, b.Confirm, b.Role, b.Amount));
        // Die übrigen offenen Dokumente der Inbox reagieren auf die Zuordnung (Gelerntes, neue Einträge)
        foreach (var id in Dms.Reassess(store)) if (!changed.Contains(id)) changed.Add(id);
        foreach (var id in changed)
        {
            DmsImport.Refile(space, DmsHost.Current!, id, false, store);
            // Externer Ordner: Angaben im Hintergrund neu in die PDF schreiben, wenn das Profil es will
            if (!store.Tresor && Dms.Get(store, id) is { Mount: not null, PdfA: true } d && DmsFiling.Find(space, d.Profile) is { WriteMeta: true })
            {
                store.Exec("UPDATE dms_docs SET meta_dirty = 1 WHERE id = $d", ("$d", id));
                DmsJob.Kick();
            }
        }
    }

    /// <summary>Treffer im DMS als Treffer der Inhaltssuche der Dateiablage.</summary>
    private static List<FileHit> Search(UserSpace space, string query, string under, int limit)
    {
        var db = space.Db;
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE name = 'dms_fts'") == null) return [];
        var match = TextIndex.FtsQuery(query);
        if (match.Length == 0) return [];
        return db.Query($@"SELECT f.path, d.title, f.size, f.mime, f.modified, t.method,
                    snippet(dms_fts, 1, '{TextIndex.MarkStart}', '{TextIndex.MarkEnd}', '…', 14)
                FROM dms_fts JOIN dms_docs d ON d.id = dms_fts.rowid JOIN files f ON f.fileid = d.fileid LEFT JOIN dms_text t ON t.doc = d.id
                WHERE dms_fts MATCH $q AND ($u = '' OR substr(f.path, 1, length($u) + 1) = $u || '/')
                ORDER BY bm25(dms_fts, 4.0, 1.0) LIMIT $l",
            r => new FileHit(r.GetString(0), Path.GetFileName(r.GetString(0)), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
                DateTime.Parse(r.GetString(4), null, DateTimeStyles.RoundtripKind), r.IsDBNull(5) ? "text" : r.GetString(5), r.GetString(6)),
            ("$q", match), ("$u", under), ("$l", limit));
    }
}
