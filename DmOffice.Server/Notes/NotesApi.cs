using System.Text;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Pim;
using Notizi.Sync;

namespace DmOffice.Notes;

/// <summary>
/// Notizen in der Weboberflaeche. Bearbeitet wird wie auf dem Handy:
/// Absaetze und Listen frei, Tabellen, Bilder und Codebloecke gesperrt und
/// zeichengenau zurueck (ProtectedBlocks) - notizi am PC nimmt die Version
/// sonst nicht an.
/// </summary>
public static class NotesApi
{
    public record Meta(string? CustomTitle, string? Color, bool? Important, string? Folder, bool ClearCustomTitle = false, List<string>? Tags = null);
    public record NewNote(string? Folder);
    public record TaskSet(long Note, int Block, int N, string Text, bool Done);
    public record FolderBody(string Name, string? Parent);
    public record FolderMove(string? Parent);
    public record DatabaseBody(string Folder);
    public record ShareBody(List<string> Members, List<string>? ReadOnly, List<string>? ShowOnly = null);
    public record RecipeIds(List<string> Ids);
    public record RecipeLink(string Url, string? Folder);
    public record RecipeScanSettings(bool Ai);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/notes").RequireAuthorization();
        MapOpen(api);
        MapNotes(api);
        // Eine notizi-Datenbank im Tresor (Tresor auf diesem Gerät offen): dieselben Wege für Notizen und Ordner, ohne
        // Freigaben, Rezepte und Aufgaben - Suche, Pukki und Widgets lesen sie nie
        MapNotes(app.MapGroup("/api/notes/vault/{vault}").RequireAuthorization());
    }

    /// <summary>Nur im offenen Bereich: Datenbanken wählen und in den Tresor verschieben, Freigaben, Aufgaben, Rezepte.</summary>
    private static void MapOpen(RouteGroupBuilder api)
    {
        // Datenbanken unter der Wurzel - wie die Auswahl "Datenbank" in notizi; dazu die im Tresor (Name nur bei offenem Tresor)
        api.MapGet("/databases", async (HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var n = space.Settings.Notizi;
            var open = Tresor.IsOpen(http, touch: false);
            var tresor = new List<object>();
            foreach (var f in TresorNotizi.Folders(space))
                tresor.Add(new { folder = f, name = open ? await NotesSync.TresorNameAsync(space, f, ct) : null });
            return new
            {
                current = n.DbFolder, list = string.IsNullOrEmpty(n.E2eePassword) ? null : await NotesSync.ScanAsync(space, ct),
                tresor, tresorOpen = open, tresorExists = Tresor.Exists(space.User.Id)
            };
        });

        // In den Tresor verschieben (Tresor auf diesem Gerät offen): die gewählte Datenbank vorher vollständig abgleichen,
        // danach ihre Notizen hier überschrieben löschen - sie stehen dann nur noch im Tresor
        api.MapPost("/databases/{folder}/tresor", async (string folder, HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            _ = Tresor.Db(http);
            folder = Notizi.Sync.SyncCatalog.FolderName(folder);
            var current = space.Settings.Notizi.DbFolder == folder;
            if (current)
            {
                await sync.RunAsync(space, ct);
                if (NotesDb.Of(space).PendingCount() > 0)
                    return Results.BadRequest(new { error = "Nicht alles ist abgeglichen - erst den Fehler beim Abgleich beheben, sonst ginge etwas verloren." });
            }
            TresorNotizi.MoveIn(space, folder);
            if (current)
            {
                NotesDb.Of(space).Clear(secure: true);
                space.UpdateSettings(s => { s.Notizi.DbFolder = null; s.Notizi.SwitchDb(); });
                NotesSync.StateOf(space).Store = null;
            }
            SecurityNotice.Own(http.RequestServices, space.User.Id, "Tresor: notizi-Datenbank in den Tresor verschoben");
            await sync.RunTresorAsync(space, folder, ct);
            return Results.Ok();
        });

        // Aus dem Tresor holen (Tresor auf diesem Gerät offen): wieder im offenen Bereich, zum Wählen wie jede andere
        api.MapPost("/databases/{folder}/out", async (string folder, HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            _ = Tresor.Db(http);
            if (TresorNotes.Of(space, folder) is { } db)
            {
                await sync.RunTresorAsync(space, folder, ct);
                if (db.PendingCount() > 0)
                    return Results.BadRequest(new { error = "Nicht alles ist abgeglichen - erst den Fehler beim Abgleich beheben, sonst ginge etwas verloren." });
                NotesSync.StateOf(space, folder).Store = null;
            }
            TresorNotizi.MoveOut(space, folder);
            SecurityNotice.Own(http.RequestServices, space.User.Id, "Tresor: notizi-Datenbank aus dem Tresor geholt");
            return Results.Ok();
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
                s.Notizi.SwitchDb();
            });
            await sync.RunAsync(space, ct);
            return Results.Ok(NotesSync.Status(space));
        });

        // Mit mir geteilte Ordner: je Freigabe der oberste Ordner und alle darunter (der oberste ohne Eltern)
        api.MapGet("/shared", (HttpContext http, ShareStore shares, UserStore users) =>
            SharedNotes.WithMe(http).Select(sc =>
            {
                var root = sc.Share!.Folder!;
                var all = sc.Db.AllFolders().Where(f => sc.Tree!.Contains(f.SyncId)).ToList();
                return new
                {
                    share = sc.Share.Id, root, owner = sc.OwnerName, name = all.First(f => f.SyncId == root).Name, canWrite = sc.CanWrite, showOnly = sc.ShowOnly,
                    sharedWith = Members(sc.Share, users, http.Space().User.Id),
                    folders = all.Select(f => new { f.SyncId, Parent = f.SyncId == root ? null : f.Parent, f.Name })
                };
            }).ToList());

        // Wer auf diesem Server ist - zum Teilen steht /api/pim/people bereit
        // Einen eigenen Ordner samt Unterordnern teilen (ReadOnly: wer davon nur lesen darf); leere Mitglieder: Freigabe aufheben
        api.MapPut("/shares/{folder}", (string folder, ShareBody body, HttpContext http, ShareStore shares, UserStore users) =>
        {
            var space = http.Space();
            _ = NotesDb.Of(space).GetFolder(folder) ?? throw new KeyNotFoundException();
            shares.SetFolder(space.User.Id, folder, body.Members.Where(m => m != space.User.Id && users.ById(m) != null).ToList(), body.ReadOnly, body.ShowOnly);
            return Results.Ok();
        });

        // Einen mit mir geteilten Ordner nicht mehr sehen
        api.MapPost("/shares/{id:int}/leave", (int id, HttpContext http, ShareStore shares) =>
        {
            shares.Leave(id, http.Space().User.Id);
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

        MapRecipes(api);
    }

    /// <summary>Notizen und Ordner - der eigenen Datenbank, geteilter Ordner und (unter /vault/{vault}) einer Datenbank im Tresor.</summary>
    private static void MapNotes(RouteGroupBuilder api)
    {
        api.MapGet("/status", (HttpContext http) => NotesSync.Status(http.Space(), SharedNotes.Own(http).Vault));

        api.MapPost("/sync", async (HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            var scope = SharedNotes.Own(http);
            if (scope.Vault != null) await sync.RunTresorAsync(scope.Space, scope.Vault, ct);
            else await sync.RunAsync(scope.Space, ct);
            return NotesSync.Status(scope.Space, scope.Vault);
        });

        api.MapGet("/folders", (HttpContext http, ShareStore shares, UserStore users) =>
        {
            var scope = SharedNotes.Own(http);
            return scope.Db.AllFolders().Select(f => new
            {
                f.SyncId, f.Parent, f.Name, f.SortOrder,
                sharedWith = scope.Vault == null && shares.OfFolder(scope.Space.User.Id, f.SyncId) is { } sh ? Members(sh, users) : null
            });
        });

        api.MapPost("/folders", (FolderBody body, HttpContext http, NotesSync sync) =>
        {
            // Unterordner in einem geteilten Ordner (mit Bearbeitungsrecht): in der Datenbank des Besitzers
            var scope = SharedNotes.ForFolder(http, body.Parent);
            if (scope.IsShared) scope.Writable();
            var id = scope.Db.CreateFolder(body.Name, body.Parent);
            sync.Kick(scope.Space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok(new { id });
        });

        api.MapPut("/folders/{id}", (string id, FolderBody body, HttpContext http, NotesSync sync) =>
        {
            var scope = SharedNotes.Own(http);
            scope.Db.RenameFolder(id, body.Name);
            sync.Kick(scope.Space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        // Ordner verschieben (Ziehen im Ordnerbereich, "Verschieben nach"); Parent leer: oberste Ebene
        api.MapPost("/folders/{id}/move", (string id, FolderMove body, HttpContext http, NotesSync sync) =>
        {
            var scope = SharedNotes.Own(http);
            scope.Db.MoveFolder(id, string.IsNullOrEmpty(body.Parent) ? null : body.Parent);
            sync.Kick(scope.Space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        api.MapDelete("/folders/{id}", (string id, HttpContext http, NotesSync sync) =>
        {
            var scope = SharedNotes.Own(http);
            if (!scope.Db.DeleteFolder(id)) return Results.BadRequest(new { error = "Der Ordner ist nicht leer." });
            if (scope.Vault == null) http.RequestServices.GetRequiredService<ShareStore>().ForgetFolder(scope.Space.User.Id, id);
            sync.Kick(scope.Space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        // Alle Schlagwoerter der eigenen Notizen mit Anzahl - fuer die Seitenleiste und die Vorschlaege beim Eintippen
        api.MapGet("/tags", (HttpContext http) => SharedNotes.Own(http).Db.GetAllActive()
            .SelectMany(n => n.Tags ?? []).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { tag = g.First(), count = g.Count() }).OrderBy(x => x.tag, StringComparer.CurrentCultureIgnoreCase).ToList());

        api.MapGet("/", (string? folder, string? view, string? q, string? tag, HttpContext http) =>
        {
            // Ein mit mir geteilter Ordner: Notizen aus der Datenbank des Besitzers
            if (string.IsNullOrWhiteSpace(q) && view == null && !string.IsNullOrEmpty(folder) && SharedNotes.TryForFolder(http, folder) is { IsShared: true } sc)
                return new
                {
                    archived = 0, trashed = 0,
                    items = sc.Db.GetNotes(folder).Select(n => Item(n, sc)).ToList()
                };
            var own = SharedNotes.Own(http);
            var db = own.Db;
            List<NoteRow> notes = !string.IsNullOrWhiteSpace(q) ? db.Search(q)
                : view switch
                {
                    "archive" => db.GetArchived(),
                    "trash" => db.GetTrashed(),
                    "all" => db.GetAllActive(),
                    "important" => db.GetAllActive().Where(n => n.Important).ToList(),
                    "tag" => db.GetAllActive().Where(n => n.Tags?.Contains(tag ?? "", StringComparer.OrdinalIgnoreCase) == true).ToList(),
                    _ => db.GetNotes(string.IsNullOrEmpty(folder) ? null : folder)
                };
            var counts = db.CountArchiveAndTrash();
            var items = notes.Select(n => Item(n, own)).ToList();
            // Die Suche geht auch durch die mit mir geteilten Ordner
            if (!string.IsNullOrWhiteSpace(q))
                foreach (var shared in SharedNotes.WithMe(http))
                    items.AddRange(shared.Db.Search(q).Where(n => n.Archived == null && shared.Covers(n)).Select(n => Item(n, shared)));
            return new { archived = counts.Archived, trashed = counts.Trashed, items = items.OrderByDescending(i => i.modified).ToList() };
        });

        MapNoteItems(api);
    }

    private static void MapRecipes(RouteGroupBuilder api)
    {
        // Rezepte (Notizen in Rezeptgestalt) - fuer den Essensplan
        api.MapGet("/recipes", async (string? q, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var folders = space.Db.Query("SELECT sync_id, parent, name, sort_order FROM note_folders",
                r => new NoteFolder { SyncId = r.GetString(0), Parent = r.IsDBNull(1) ? null : r.GetString(1), Name = r.GetString(2), SortOrder = r.GetDouble(3) });
            // Die geladene Datenbank und dazu die anderen, deren Passwort passt
            var all = Recipes.All(NotesDb.Of(space), folders, q);
            all.AddRange(await RemoteRecipes.AllAsync(space, q, ct));
            return all.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        });

        // Rezepte im Internet suchen (DuckDuckGo, auf Wunsch zusätzlich die KI mit Websuche) - Treffer mit Rezept, Bild und Nährwerten
        api.MapGet("/recipes/search/state", (HttpContext http, ServerConfig config, UserStore users) =>
            new { ai = PukkiAi.CanSearch(config) && PukkiAi.Allowed(config, users.ById(http.Space().User.Id)) });
        api.MapGet("/recipes/search", async (string q, bool? ai, HttpContext http, ServerConfig config, UserStore users, DmOffice.Coach.CoachFoods foods,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var space = http.Space();
            var allowed = ai == true && PukkiAi.Allowed(config, users.ById(space.User.Id));
            var (hits, usedAi) = await RecipeSearch.SearchAsync(space, foods, config, allowed, q, logs.CreateLogger("RecipeSearch"), ct);
            return new { hits, ai = usedAi };
        });
        api.MapGet("/recipes/search/image", async (string u, HttpContext http, CancellationToken ct) =>
        {
            var data = await RecipeSearch.ImageAsync(u, ct);
            if (data == null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(data, "image/webp");
        });

        // Rezept per Link: schema.org-Rezept der Seite als Notiz in der Gestalt der notizi-Vorlage (Coach Stufe 2)
        api.MapPost("/recipes/import", async (RecipeLink body, HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            var folder = string.IsNullOrEmpty(body.Folder) ? null : body.Folder;
            var scope = SharedNotes.ForFolder(http, folder);
            if (scope.IsShared) scope.Writable();
            try
            {
                var id = await RecipeImport.ImportAsync(scope.Space, scope.Db, body.Url, folder, ct);
                sync.Kick(scope.Space.User.Id, TimeSpan.FromSeconds(10));
                return Results.Ok(new { id = scope.Wire(id) });
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                return Results.BadRequest(new { error = e is HttpRequestException ? "Die Seite ist nicht erreichbar." : e is TaskCanceledException ? "Die Seite antwortet nicht." : e.Message });
            }
        });

        // Rezept-Scan: Kochbuchseiten erkennen (Entwurf fuer die Pruefansicht), dann als Rezept-Notiz anlegen (docs/rezept-scan-konzept.md)
        api.MapGet("/recipes/scan/state", (HttpContext http, ServerConfig config, UserStore users) =>
        {
            var space = http.Space();
            var aiAllowed = PukkiAi.Allowed(config, users.ById(space.User.Id));
            return new { ocr = DmOffice.Search.Ocr.Available, reason = DmOffice.Search.Ocr.Current.Reason, aiAllowed, ai = aiAllowed && space.Settings.RecipeScanAi };
        });
        api.MapPut("/recipes/scan/settings", (RecipeScanSettings body, HttpContext http) =>
        {
            http.Space().UpdateSettings(s => s.RecipeScanAi = body.Ai);
            return Results.Ok();
        });
        api.MapPost("/recipes/scan", async (HttpRequest request, HttpContext http, ServerConfig config, UserStore users, ILoggerFactory logs, CancellationToken ct) =>
        {
            var space = http.Space();
            if (!DmOffice.Search.Ocr.Available) return Results.BadRequest(new { error = DmOffice.Search.Ocr.Current.Reason ?? "Keine Texterkennung." });
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = RecipeScan.MaxPages * RecipeScan.MaxPageBytes;
            var form = await request.ReadFormAsync(ct);
            var files = new List<(string, byte[])>();
            foreach (var f in form.Files.Take(RecipeScan.MaxPages))
            {
                if (f.Length > RecipeScan.MaxPageBytes) return Results.BadRequest(new { error = $"„{f.FileName}“ ist größer als 15 MB." });
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ct);
                files.Add((f.FileName, ms.ToArray()));
            }
            if (files.Count == 0) return Results.BadRequest(new { error = "Keine Seite übergeben." });
            try
            {
                var result = await RecipeScan.ScanAsync(files, ct);
                var aiAllowed = PukkiAi.Allowed(config, users.ById(space.User.Id));
                var usedAi = false;
                // Regeln haben Titel, Zutaten oder Schritte nicht gefunden: die KI liest die Seiten (Bild und Text), wenn eingeschaltet
                var missing = result.Drafts.Any(d => d.Title.Length == 0 || d.Ingredients.Length == 0 || d.Steps.Length == 0);
                if (missing && aiAllowed && space.Settings.RecipeScanAi
                    && await RecipeScan.AiAsync(config, result.Text, result.Images, logs.CreateLogger("RecipeScan"), ct) is { } byAi)
                {
                    result = result with { Drafts = byAi, Hints = result.Hints.Where(h => h.StartsWith("Seite") || h.StartsWith("Mehr als")).ToList() };
                    usedAi = true;
                }
                return Results.Ok(new { drafts = result.Drafts, text = result.Text, hints = result.Hints, ai = usedAi, aiAllowed });
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or TimeoutException)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        }).DisableAntiforgery();
        // Knopf „Mit KI erkennen“: Formular mit text (schon erkannt) und page (die Seitenbilder)
        api.MapPost("/recipes/scan/ai", async (HttpRequest request, HttpContext http, ServerConfig config, UserStore users, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!PukkiAi.Allowed(config, users.ById(http.Space().User.Id))) return Results.BadRequest(new { error = "KI ist für dieses Konto nicht freigegeben." });
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = RecipeScan.MaxPages * RecipeScan.MaxPageBytes;
            var form = await request.ReadFormAsync(ct);
            var images = new List<byte[]>();
            foreach (var f in form.Files.GetFiles("page").Take(RecipeScan.MaxPages))
            {
                if (f.Length is 0 or > RecipeScan.MaxPageBytes) continue;
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ct);
                // PDF-Seiten gehen nur als Text an die KI
                if (RecipeScan.Jpeg(ms.ToArray(), 1600, 80) is { } jpg) images.Add(jpg);
            }
            var drafts = await RecipeScan.AiAsync(config, form["text"].ToString(), images, logs.CreateLogger("RecipeScan"), ct);
            return drafts == null ? Results.BadRequest(new { error = "Die KI hat kein Rezept geliefert." }) : Results.Ok(new { drafts });
        }).DisableAntiforgery();
        // Anlegen: Formular mit draft (JSON), folder und image (Titelbild); je Rezept ein Aufruf
        api.MapPost("/recipes/scan/save", async (HttpRequest request, HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = RecipeScan.MaxPageBytes + 1024 * 1024;
            var form = await request.ReadFormAsync(ct);
            RecipeScan.Draft? draft;
            try { draft = JsonSerializer.Deserialize<RecipeScan.Draft>(form["draft"].ToString(), JsonSerializerOptions.Web); }
            catch (JsonException) { draft = null; }
            if (draft == null) return Results.BadRequest(new { error = "Kein Rezept übergeben." });
            // Fehlende Felder im JSON kommen als null an
            draft = draft with
            {
                Title = draft.Title ?? "", Yield = draft.Yield ?? "", PrepTime = draft.PrepTime ?? "", CookTime = draft.CookTime ?? "", TotalTime = draft.TotalTime ?? "",
                Description = draft.Description ?? "", Ingredients = draft.Ingredients ?? "", Steps = draft.Steps ?? "", Tip = draft.Tip ?? "", Source = draft.Source ?? "",
            };
            var folder = form["folder"].ToString() is { Length: > 0 } fo ? fo : null;
            var scope = SharedNotes.ForFolder(http, folder);
            if (scope.IsShared) scope.Writable();
            async Task<byte[]> Read(IFormFile f)
            {
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ct);
                return ms.ToArray();
            }
            byte[]? image = form.Files.GetFile("image") is { Length: > 0 and <= RecipeScan.MaxPageBytes } img ? await Read(img) : null;
            if (image != null) VirusScan.CheckBytes(image, scope.Space.User.Id, "Notizen/Rezept-Scan");
            var id = await RecipeScan.SaveAsync(scope.Space, scope.Db, scope.Vault, draft, folder, image, ct);
            sync.Kick(scope.Space.User.Id, TimeSpan.FromSeconds(10));
            return Results.Ok(new { id = scope.Wire(id) });
        }).DisableAntiforgery();

        // Zutaten der gewaehlten Rezepte (sync_id) - fuer "Zutaten auf die Einkaufsliste"
        api.MapPost("/recipes/ingredients", async (RecipeIds body, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var wanted = body.Ids.ToHashSet();
            var found = NotesDb.Of(space).GetAllActive().Where(n => wanted.Contains(n.SyncId))
                .Select(n => new { syncId = n.SyncId, title = n.DisplayTitle, items = Recipes.Ingredients(n) }).ToList();
            // Basisrezepte von Coach (stehen im Essensplan wie Rezept-Notizen)
            found.AddRange(wanted.Select(DmOffice.Coach.BaseRecipes.ById).OfType<DmOffice.Coach.BaseRecipes.Recipe>()
                .Select(b => new { syncId = b.Id, title = b.Title, items = b.Lines }));
            // Was hier nicht liegt, steht vielleicht in einer anderen Datenbank
            var missing = wanted.Where(id => found.All(f => f.syncId != id)).ToHashSet();
            if (missing.Count == 0) return found;
            found.AddRange((await RemoteRecipes.IngredientsAsync(space, missing, ct)).Select(r => new { syncId = r.SyncId, title = r.Title, items = r.Items }));
            return found;
        });
    }

    private static void MapNoteItems(RouteGroupBuilder api)
    {
        api.MapGet("/{id:long}", (long id, HttpContext http) =>
        {
            var (scope, nid) = SharedNotes.ForNote(http, id);
            var db = scope.Db;
            var note = db.GetNote(nid) ?? throw new KeyNotFoundException("Notiz nicht gefunden.");
            var split = NoteMerge.Split(note.Html);
            var raw = split?.Blocks ?? new List<string> { note.Html };
            var flags = split == null ? raw.Select(_ => true).ToList() : raw.Select(ProtectedBlocks.IsProtectedBlock).ToList();
            // Beim Ausliefern bereinigen: Inhalt aus dem Abgleich (notizi) laeuft sonst ungefiltert in die Seite
            // bzw. den WebView. Nur der Web-Editor bereinigt beim Speichern - der Abgleich tut es nicht.
            var blocks = raw.Select(Clean).ToList();
            return new
            {
                Id = scope.Wire(note.Id), note.SyncId, note.Folder, note.CustomTitle, title = note.DisplayTitle, note.Color, note.Important, tags = note.Tags ?? [],
                note.Modified, note.Created, note.Archived, note.Deleted,
                blocks, @protected = flags, readOnly = split == null || !scope.CanWrite,
                attachments = note.Attachments.Select(a => new { a.Name, a.Mime, a.Size, a.Blob }),
                pending = !scope.IsShared && db.IsPending(note.Id),
                owner = scope.IsShared ? scope.OwnerName : null, canWrite = scope.CanWrite, showOnly = scope.ShowOnly,
                // Eigene Notiz: mit wem einzeln geteilt (Version 2); bei geteilten: einzeln oder im Ordner
                sharedWith = scope.IsShared || scope.Vault != null ? null : http.RequestServices.GetRequiredService<NoteShareStore>().Of(scope.Space.User.Id, note.SyncId)?.Members.Count,
                single = scope.Only != null,
                version = Version(note.Html)
            };
        });

        api.MapPost("/", (NewNote body, HttpContext http, NotesSync sync) =>
        {
            var folder = string.IsNullOrEmpty(body.Folder) ? null : body.Folder;
            var scope = SharedNotes.ForFolder(http, folder);
            if (scope.IsShared) { scope.Writable(); sync.Kick(scope.Space.User.Id, TimeSpan.FromSeconds(10)); }
            return Results.Ok(new { id = scope.Wire(scope.Db.CreateNote(folder)) });
        });

        // Inhalt aus dem Editor: unveraenderte und gesperrte Bloecke als Nummer, alles andere als XHTML.
        // Die Nummern gelten fuer die Version "version" - hat der Abgleich die Notiz inzwischen
        // geaendert, passt sie nicht mehr: 409 statt fremde Aenderungen zu ueberschreiben.
        api.MapPut("/{wire:long}/content", async (long wire, HttpRequest request, HttpContext http, NotesSync sync) =>
        {
            var (scope, id) = SharedNotes.ForNote(http, wire);
            scope.Writable();
            var space = scope.Space;
            var db = scope.Db;
            using var doc = await JsonDocument.ParseAsync(request.Body);
            // Pruefen und Speichern am Stueck: zwei Benutzer gleichzeitig duerfen sich nicht ueberschreiben
            lock (db)
            {
            var note = db.GetNote(id) ?? throw new KeyNotFoundException();
            var split = NoteMerge.Split(note.Html) ?? throw new InvalidOperationException("Diese Notiz lässt sich hier nur lesen.");
            if (doc.RootElement.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String
                && ver.GetString() != Version(note.Html))
                return Results.Conflict(new { error = scope.IsShared
                    ? "Die Notiz wurde inzwischen von jemand anderem geändert."
                    : "Die Notiz wurde inzwischen auf einem anderen Gerät geändert." });
            var html = new StringBuilder(split.Head);
            foreach (var block in doc.RootElement.GetProperty("blocks").EnumerateArray())
            {
                if (block.TryGetProperty("i", out var i))
                {
                    var n = i.GetInt32();
                    if (n < 0 || n >= split.Blocks.Count) throw new ArgumentException("Block gibt es nicht.");
                    html.Append(Adjusted(split.Blocks[n], block));
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
            // Neue Version und ihre Bloecke, damit der Editor seine Nummern nachfuehren kann
            var after = NoteMerge.Split(result);
            return Results.Ok(new
            {
                saved = result != note.Html,
                version = Version(result),
                @protected = after?.Blocks.Select(ProtectedBlocks.IsProtectedBlock).ToList()
            });
            }
        });

        api.MapPut("/{wire:long}/meta", async (long wire, Meta body, HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            var (scope, id) = SharedNotes.ForNote(http, wire);
            // Eigene Notiz in einen mit mir geteilten Ordner: wird dorthin kopiert, das Original verschwindet
            if (!scope.IsShared && !string.IsNullOrEmpty(body.Folder) && scope.Db.GetFolder(body.Folder) == null
                && SharedNotes.TryForFolder(http, body.Folder) is { IsShared: true } target)
            {
                var moved = await SharedNotes.MoveIntoShared(scope, id, target, body.Folder, ct);
                sync.Kick(target.Space.User.Id, TimeSpan.FromSeconds(10));
                sync.Kick(scope.Space.User.Id, TimeSpan.FromSeconds(5));
                return Results.Ok(new { id = moved });
            }
            // Einzeln geteilte Notiz (Version 2): in einen Ordner legt sie nur der Besitzer
            if (scope.Only != null && body.Folder != null) throw new UnauthorizedAccessException("In einen anderen Ordner legen darf nur der Besitzer der Notiz.");
            var space = scope.Space;
            var db = scope.Db;
            if (scope.IsShared) scope.Writable();
            // Ziel nur im selben Bereich: eigene Notizen in eigene Ordner, geteilte innerhalb ihres Ordners (nicht heraus)
            if (body.Folder != null)
            {
                if (scope.IsShared ? !scope.Tree!.Contains(body.Folder) : body.Folder.Length > 0 && db.GetFolder(body.Folder) == null)
                {
                    if (scope.IsShared) throw new UnauthorizedAccessException("Aus dem geteilten Ordner herausnehmen darf nur der Besitzer.");
                    throw new KeyNotFoundException("Ordner nicht gefunden.");
                }
            }
            if (body.ClearCustomTitle) db.SetCustomTitle(id, null);
            else if (body.CustomTitle != null) db.SetCustomTitle(id, body.CustomTitle);
            if (body.Color != null) db.SetColor(id, body.Color);
            if (body.Important is bool imp) db.SetImportant(id, imp);
            if (body.Tags != null) db.SetTags(id, body.Tags);
            if (body.Folder != null) db.SetFolder(id, body.Folder.Length == 0 ? null : body.Folder);
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(10));
            return Results.Ok();
        });

        api.MapPost("/{wire:long}/{action}", (long wire, string action, HttpContext http, NotesSync sync) =>
        {
            // Wiederherstellen/Archivieren: nur der Besitzer
            if (wire < 0 && action != "trash") throw new UnauthorizedAccessException("Das darf nur der Besitzer des Ordners.");
            var (scope, id) = SharedNotes.ForNote(http, wire);
            if (scope.Only != null) throw new UnauthorizedAccessException("Das darf nur der Besitzer der Notiz.");
            if (scope.IsShared) scope.Writable();
            var space = scope.Space;
            var db = scope.Db;
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
            if (id < 0) throw new UnauthorizedAccessException("Endgültig löschen darf nur der Besitzer des Ordners.");
            var scope = SharedNotes.Own(http);
            var space = scope.Space;
            scope.Db.DeleteNote(id);
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        api.MapGet("/blob/{name}", (string name, bool? download, string? file, int? thumb, HttpContext http) =>
        {
            var (data, showOnly) = SharedNotes.Blob(http, name) ?? throw new KeyNotFoundException();
            if (showOnly && download == true) throw new UnauthorizedAccessException("Nur gezeigt – herunterladen ist nicht erlaubt.");
            // Vorschau fuer die Notizliste: klein, vom Browser zwischenspeicherbar (der Name haengt am Inhalt)
            if (thumb is > 0 && download != true && DmOffice.Dav.Thumbs.FromBytes(data, Math.Min(thumb.Value, 512)) is { } small)
            {
                http.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.File(small, "image/webp");
            }
            if (download == true) return Results.File(data, "application/octet-stream", file ?? name);
            var mime = Sniff(data);
            // Nur gezeigt: Bilder in der Notiz ja, andere Anhaenge nicht (sie kaemen als Datei an)
            if (showOnly && !mime.StartsWith("image/")) throw new UnauthorizedAccessException("Nur gezeigt – herunterladen ist nicht erlaubt.");
            return Results.File(data, mime.StartsWith("image/") ? mime : "application/octet-stream");
        });

        api.MapPost("/{wire:long}/attachments", async (long wire, HttpRequest request, HttpContext http, NotesSync sync, CancellationToken ct) =>
        {
            var (scope, id) = SharedNotes.ForNote(http, wire);
            scope.Writable();
            var space = scope.Space;
            var form = await request.ReadFormAsync(ct);
            foreach (var file in form.Files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                var data = ms.ToArray();
                VirusScan.CheckBytes(data, space.User.Id, "Notizen/" + file.FileName);
                scope.Db.AddAttachment(id, file.FileName, file.ContentType, data, await NotesSync.BlobNameAsync(space, data, ct, scope.Vault));
            }
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        }).DisableAntiforgery();

        api.MapDelete("/{wire:long}/attachments/{name}", (long wire, string name, HttpContext http, NotesSync sync) =>
        {
            var (scope, id) = SharedNotes.ForNote(http, wire);
            scope.Writable();
            var space = scope.Space;
            scope.Db.RemoveAttachment(id, name);
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(5));
            return Results.Ok();
        });

        // Ein Bild fuer den Editor: Name wie im Abgleichordner, damit notizi es findet
        api.MapPost("/images", async (long? note, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            // Fuer eine geteilte Notiz: das Bild gehoert in die Datenbank des Besitzers (und heisst nach dessen Schluessel)
            var scope = note is long w && w < 0 ? SharedNotes.ForNote(http, w).Scope.Writable() : SharedNotes.Own(http);
            var space = scope.Space;
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault() ?? throw new ArgumentException("Kein Bild.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var data = ms.ToArray();
            VirusScan.CheckBytes(data, space.User.Id, "Notizen/" + file.FileName);
            var name = await NotesSync.BlobNameAsync(space, data, ct, scope.Vault);
            scope.Db.PutBlob(name, data);
            return Results.Ok(new { name });
        }).DisableAntiforgery();
    }

    private static object Members(Share sh, UserStore users, string? except = null) =>
        sh.Members.Where(m => m != except).Select(m => new { id = m, name = users.ById(m)?.DisplayName, readOnly = sh.ReadOnly.Contains(m), level = sh.LevelOf(m) })
            .Where(m => m.name != null).ToList();

    /// <summary>Eine Notiz in der Liste. Geteilte tragen die Kennung der Freigabe und den Namen des Besitzers.</summary>
    private static NoteItemDto Item(NoteRow n, NoteScope scope) => new(
        scope.Wire(n.Id), n.SyncId, n.Folder, n.DisplayTitle, n.Snippet, n.Color, n.Modified, n.Created, n.Important,
        n.Attachments.Count, n.Archived, n.Deleted, n.Match, FirstImage(n), scope.IsShared ? scope.OwnerName : null, n.Tags);

    public record NoteItemDto(long id, string syncId, string? folder, string title, string snippet, string color, DateTime modified, DateTime created,
        bool important, int attachments, DateTime? archived, DateTime? deleted, DmOffice.Search.AttachmentMatch? match, string? image, string? owner, List<string>? tags = null);

    /// <summary>
    /// Ein Block als Nummer, mit dem, was der Editor an ihm geaendert hat (wie notizi am Handy):
    /// "swaps" tauscht einen Bildverweis aus und setzt dessen Groesse - nur in genau diesem img -,
    /// "align" setzt die Ausrichtung am Block. Sonst bleibt er Zeichen fuer Zeichen.
    /// </summary>
    private static string Adjusted(string original, JsonElement block)
    {
        if (block.TryGetProperty("swaps", out var swaps) && swaps.ValueKind == JsonValueKind.Array)
            foreach (var swap in swaps.EnumerateArray())
            {
                if (swap.ValueKind != JsonValueKind.Array || swap.GetArrayLength() < 2) continue;
                // Nur Blobnamen, wie der Abgleich sie vergibt - nichts, was aus dem Attribut ausbricht
                if (!BlobName(swap[0].GetString()) || !BlobName(swap[1].GetString())) continue;
                var to = "blob:" + swap[1].GetString();
                original = original.Replace("blob:" + swap[0].GetString(), to);
                if (swap.GetArrayLength() >= 4 && swap[2].GetString() is { Length: > 0 } w && swap[3].GetString() is { Length: > 0 } h
                    && int.TryParse(w, out _) && int.TryParse(h, out _))
                    original = System.Text.RegularExpressions.Regex.Replace(original,
                        @"<img\b[^>]*" + System.Text.RegularExpressions.Regex.Escape(to) + "[^>]*>",
                        m => SetAttr(SetAttr(m.Value, "width", w), "height", h));
            }
        if (block.TryGetProperty("align", out var align) && align.ValueKind == JsonValueKind.String
            && align.GetString() is "left" or "center" or "right")
            original = WithAlign(original, align.GetString()!);
        return original;
    }

    private static bool BlobName(string? name) =>
        !string.IsNullOrEmpty(name) && System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9._-]+$");

    /// <summary>Setzt text-align im ersten Tag eines Blocks; "left" nimmt es heraus.</summary>
    private static string WithAlign(string block, string align)
    {
        var m = System.Text.RegularExpressions.Regex.Match(block, @"^(\s*<\w+)([^>]*?)(/?>)");
        if (!m.Success) return block;
        var attrs = m.Groups[2].Value;
        var style = System.Text.RegularExpressions.Regex.Match(attrs, @"\sstyle=""([^""]*)""");
        var rest = style.Success
            ? System.Text.RegularExpressions.Regex.Replace(style.Groups[1].Value, @"text-align\s*:[^;]*;?", "").Trim().TrimEnd(';').Trim()
            : "";
        var css = align == "left" ? rest : (rest.Length > 0 ? rest + ";" : "") + "text-align:" + align;
        if (style.Success) attrs = attrs.Remove(style.Index, style.Length);
        if (css.Length > 0) attrs += " style=\"" + css + "\"";
        return m.Groups[1].Value + attrs + m.Groups[3].Value + block[m.Length..];
    }

    /// <summary>Setzt ein Attribut in einem einzelnen Tag (oder fuegt es an).</summary>
    private static string SetAttr(string tag, string name, string value)
    {
        var rx = new System.Text.RegularExpressions.Regex(@"\b" + name + "=\"[^\"]*\"");
        return rx.IsMatch(tag)
            ? rx.Replace(tag, name + "=\"" + value + "\"", 1)
            : tag.Replace("<img", "<img " + name + "=\"" + value + "\"");
    }

    /// <summary>Kurzer Fingerabdruck der Version (SHA-256, hex, gekuerzt) - fuer die Pruefung beim Speichern.</summary>
    private static string Version(string html) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(html)))[..16].ToLowerInvariant();

    /// <summary>Ein Block aus dem Editor: wohlgeformt, ohne Skript und Ereignisse - notizi liest XHTML.</summary>
    private static string Clean(string html)
    {
        var sanitizer = new Ganss.Xss.HtmlSanitizer();
        sanitizer.AllowedSchemes.Add("blob");
        sanitizer.AllowedSchemes.Add("data");
        // Bildhuelle <div data-block="image"> aus dem Editor der App - ohne das Merkmal
        // haelt ProtectedBlocks.Kept ein neues Bild fuer eine fremde Aenderung
        sanitizer.AllowedAttributes.Add("data-block");
        var clean = sanitizer.Sanitize(html);
        // XHTML: leere Elemente schliessen, &nbsp; als Zeichen
        clean = System.Text.RegularExpressions.Regex.Replace(clean, "<(br|img|hr)([^>]*?)(?<!/)>", "<$1$2 />");
        return clean.Replace("&nbsp;", "&#160;");
    }

    private static readonly System.Text.RegularExpressions.Regex InlineImage = new("""<img[^>]*?\ssrc\s*=\s*["']blob:([^"']+)["']""", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Name des ersten Bildes einer Notiz fuer die Liste: zuerst im Text, sonst das erste Bild unter den Anhaengen.</summary>
    private static string? FirstImage(NoteRow n)
    {
        // Die Namen der Bilder im Text stehen schon in Images (beim Speichern aus "blob:<name>" gelesen)
        if (n.Images.FirstOrDefault(i => !string.IsNullOrEmpty(i)) is { } first) return first;
        var m = InlineImage.Match(n.Html);
        if (m.Success) return m.Groups[1].Value;
        return n.Attachments.FirstOrDefault(a => a.Mime?.StartsWith("image/") == true && !string.IsNullOrEmpty(a.Blob))?.Blob;
    }

    private static string Sniff(byte[] d) =>
        d.Length > 4 && d[0] == 0x89 && d[1] == 0x50 ? "image/png" :
        d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 ? "image/jpeg" :
        d.Length > 4 && d[0] == 0x47 && d[1] == 0x49 ? "image/gif" :
        d.Length > 12 && d[8] == 0x57 && d[9] == 0x45 ? "image/webp" :
        d.Length > 2 && d[0] == 0x42 && d[1] == 0x4D ? "image/bmp" : "application/octet-stream";
}
