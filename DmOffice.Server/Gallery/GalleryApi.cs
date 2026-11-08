using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;

namespace DmOffice.Gallery;

/// <summary>Galerie-Ordner einrichten und ihre Fotos und Videos ausliefern (Liste, Vorschaubild, Datei).</summary>
public static class GalleryApi
{
    public record SourceBody(string Mount, string? Path, bool Watch = true, bool Sidecars = true, bool Proxies = false);
    public record WatchBody(bool? Watch, bool? Sidecars, bool? Proxies = null);
    public record DeleteBody(List<string> Ids);
    public record GallerySettingsBody(bool WriteXmp);
    public record RefreshBody(string Id);
    public record VisibleBody(List<string>? Ids);
    public record FacesBody(bool Enabled);
    public record FacesNameBody(string Name, string? Group, List<long>? Faces);
    public record FacesIdsBody(List<long> Faces);
    public record FacesPersonBody(string? Name, long? Cover, bool? Hidden);
    public record FacesRejectBody(long Person);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/gallery").RequireAuthorization();

        api.MapGet("/sources", async (HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            var mounts = await ExternalFolders.WithReachAsync(space, config, ExternalFolders.MountsOf(space, config));
            return new
            {
                sources = GalleryFolders.Of(space.Db).Select(s => new
                {
                    s.Id, s.Mount, mountName = mounts.FirstOrDefault(m => m.Id == s.Mount)?.Name, s.Path,
                    // Externer Ordner gerade nicht erreichbar: die Galerie zeigt nur die gespeicherten Vorschaubilder
                    available = mounts.FirstOrDefault(m => m.Id == s.Mount)?.Available ?? false, s.Watch, s.Draining, s.Sidecars, s.Proxies, s.LastScan, s.Error, s.Count,
                    scanning = GalleryScanJob.IsRunning(space.User.Id, s.Id),
                    // Stufen: wie viele noch ohne Bildangaben bzw. ohne Vorschaubild
                    pending = GalleryFolders.Stages(space.Db, s.Id) is var st ? st.Listed + st.NoThumb : 0,
                    // Große Vorschauen dieses Ordners: wie viele da sind und wie viel Platz sie belegen (auch ohne „vorab“, vom Öffnen)
                    proxyCount = GalleryFolders.FolderProxyUsage(space, config, s) is var u ? u.Count : 0,
                    proxyBytes = u.Bytes,
                }),
                mounts,
            };
        });

        api.MapPost("/sources", async (SourceBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            var path = StoragePath.Clean(body.Path);
            // Gibt es den Ordner, und darf ich ihn sehen?
            var (driver, _) = ExternalFolders.Open(space, config, body.Mount);
            using (driver)
                if (path.Length > 0 && await driver.StatAsync(path, ct) is not { IsDir: true })
                    return Results.BadRequest(new { error = "Diesen Ordner gibt es nicht." });
            var list = GalleryFolders.Of(space.Db);
            if (list.Any(s => s.Mount == body.Mount && s.Path == path)) return Results.BadRequest(new { error = "Dieser Ordner ist schon in der Galerie." });
            var src = new GallerySource { Mount = body.Mount, Path = path, Watch = body.Watch, Sidecars = body.Sidecars, Proxies = body.Proxies };
            list.Add(src);
            GalleryFolders.Save(space.Db, list);
            GalleryScanJob.Request(space.User.Id, src.Id);
            return Results.Ok(new { src.Id });
        });

        // Große Vorschauen eines Ordners löschen - nicht, solange ein anderes Konto sie für denselben Ordner vorab erzeugen lässt
        // (sonst rechnete der Server sie gleich wieder); das eigene „vorab“ geht dabei aus
        api.MapDelete("/sources/{id}/proxies", (string id, HttpContext http, ServerConfig config, Spaces spaces) =>
        {
            var space = http.Space();
            var list = GalleryFolders.Of(space.Db);
            if (list.FirstOrDefault(s => s.Id == id) is not { } src) return Results.NotFound();
            if (GalleryFolders.ProxiesWantedElsewhere(spaces.All(), space, config, src))
                return Results.BadRequest(new { error = "Ein anderes Konto lässt für diesen Ordner große Vorschauen vorab erzeugen. Gelöscht würden sie gleich wieder erzeugt." });
            src.Proxies = false;
            GalleryFolders.Save(space.Db, list);
            return Results.Ok(new { removed = GalleryFolders.DeleteFolderProxies(space, config, src) });
        });

        api.MapPut("/sources/{id}", (string id, WatchBody body, HttpContext http, ServerConfig config) =>
        {
            var db = http.Space().Db;
            var list = GalleryFolders.Of(db);
            if (list.FirstOrDefault(s => s.Id == id) is not { } src) return Results.NotFound();
            if (body.Watch is bool w) src.Watch = w;
            var rescan = body.Sidecars is bool sc && sc != src.Sidecars;
            if (body.Sidecars is bool sc2) src.Sidecars = sc2;
            // Große Vorschauen: an - gleich anfangen; aus - den Platz wieder freigeben
            if (body.Proxies is bool px && px != src.Proxies)
            {
                src.Proxies = px;
                if (px) GalleryProxyJob.Request(); else GalleryFolders.DropProxies(http.Space(), config, src);
            }
            GalleryFolders.Save(db, list);
            // Sidecars an oder aus: gleich neu einlesen
            if (rescan) GalleryScanJob.Request(http.Space().User.Id, src.Id);
            return Results.Ok();
        });

        api.MapDelete("/sources/{id}", (string id, HttpContext http, ServerConfig config) =>
        {
            var db = http.Space().Db;
            var list = GalleryFolders.Of(db);
            if (list.FirstOrDefault(s => s.Id == id) is not { } gone) return Results.NotFound();
            list.Remove(gone);
            // Server-Ordner: eigene ältere große Vorschauen erst in den gemeinsamen Bereich - andere brauchen sie vielleicht
            if (GalleryFolders.SharedMount(config, gone.Mount)) GalleryFolders.Proxies?.Adopt(http.Space().User.Id, gone.Id, gone.Mount);
            GalleryFolders.Save(db, list);
            GalleryFolders.Forget(db, id, http.Space().User.Id);
            return Results.Ok();
        });

        api.MapPost("/sources/{id}/scan", (string id, HttpContext http) =>
        {
            var space = http.Space();
            if (!GalleryFolders.Of(space.Db).Any(s => s.Id == id)) return Results.NotFound();
            GalleryScanJob.Request(space.User.Id, id);
            return Results.Ok();
        });

        // Alle Fotos und Videos der Galerie-Ordner, mit Aufnahmedaten (wie /api/files/meta) - fertig gepackt aus dem Speicher
        // (GalleryList), unverändert nur 304. Diese Form liest die Handy-App; die Web-Galerie nimmt die kompakte darunter.
        api.MapGet("/external", (HttpContext http, ServerConfig config) => GalleryList.SendAsync(http, config, "f2", entries =>
        {
            var edits = PhotoEdits.Fingerprints(http.Space().Db);
            return new
            {
                items = entries.Select(e => new
                {
                    id = e.Source + "/" + e.Path, name = e.Path[(e.Path.LastIndexOf('/') + 1)..], e.Size, e.Mime,
                    modified = DateTimeOffset.FromUnixTimeMilliseconds(e.Modified).UtcDateTime,
                    etag = PhotoEdits.Tag(e.Size.ToString("x") + "-" + e.Modified.ToString("x"), edits.GetValueOrDefault(PhotoEdits.KeyOfExt(e.Source + "/" + e.Path))),
                }),
                meta = entries.Where(e => e.Meta?.Taken != null || e.Meta?.Camera != null || e.Meta?.Width != null || e.Meta?.Title != null || e.Meta?.Keywords != null || e.Meta?.Rating != null || e.Meta?.Label != null || e.Meta?.Pick != null || e.Meta?.Lat != null || e.Description != null).Select(e => new
                {
                    key = "ext:" + e.Source + "/" + e.Path, e.Meta!.Taken, e.Meta.Camera, e.Meta.Lens, e.Meta.Focal, e.Meta.Aperture, e.Meta.Exposure, e.Meta.Iso,
                    e.Meta.Width, e.Meta.Height, e.Meta.Duration, e.Meta.Rating, e.Meta.Title, e.Meta.Keywords, e.Meta.Label, e.Description, e.Meta.Lat, e.Meta.Lon, e.Meta.Pick,
                }),
            };
        }));
        // Löschen im externen Ordner selbst (samt Sidecar) - nur wenn er beschreibbar eingebunden ist
        api.MapPost("/external/delete", async (DeleteBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            try
            {
                var error = await GalleryFolders.DeleteAsync(http.Space(), config, body.Ids, ct);
                return error == null ? Results.Ok() : Results.Json(new { error }, statusCode: 403);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Nicht gefunden." }); }
        });
        // Titel, Beschreibung und Bewertung in eigene Fotos und Fotos der Galerie-Ordner schreiben (GalleryInfo) - Fehler je Foto
        api.MapPost("/info", async (GalleryInfo.Body body, HttpContext http, ServerConfig config, CancellationToken ct) =>
            Results.Ok(new { failed = await GalleryInfo.WriteAsync(http.Space(), config, body, ct) }));
        // Kompakte Form (Ordnerpfade und Texte nur einmal, Zeilen ohne Feldnamen) für die Web-Galerie
        api.MapGet("/external/compact", (HttpContext http, ServerConfig config) => GalleryList.SendAsync(http, config, "f3", GalleryList.Compact));

        // ---------------- Personen (GalleryFaces): Gesichter über die Photo-Engine, je Benutzer einschaltbar ----------------
        // Gesichtsdaten liegen im Tresor: ohne Tresor keine Personen, bei geschlossenem Tresor antworten die Daten mit 423
        // Stand und Übersicht; stößt das Einlesen neuer Fotos an (höchstens alle paar Minuten)
        // Ortssuche fürs Verorten in den Details (Open-Meteo, wie beim Briefing von Pukki): Name → Koordinaten
        api.MapGet("/places", async (string q, IHttpClientFactory f, CancellationToken ct) => await DmOffice.Chat.PukkiBriefing.PlacesAsync(f.CreateClient(), q, ct));

        // Einstellungen → Galerie: Angaben in die Fotos schreiben (XMP) oder nur in die eigene Datenbank
        api.MapGet("/settings", (HttpContext http) => Results.Ok(new { writeXmp = http.Space().Settings.PhotoWriteXmp }));
        api.MapPut("/settings", (GallerySettingsBody body, HttpContext http) =>
        {
            http.Space().UpdateSettings(s => s.PhotoWriteXmp = body.WriteXmp);
            return Results.Ok(new { writeXmp = body.WriteXmp });
        });

        api.MapGet("/faces", async (HttpContext http, ServerConfig config, ILogger<FacesBody> log) =>
        {
            var space = http.Space();
            var model = await GalleryFaces.Model(http.RequestAborted);
            var on = space.Settings.Faces;
            if (on && model != null) await GalleryFaces.KickAsync(http, config, log);
            var open = Tresor.IsOpen(http, touch: false);
            return Results.Ok(new
            {
                available = model != null, enabled = on, running = GalleryFaces.IsRunning(space.User.Id),
                // Wartet, bis die Galerie-Ordner fertig ausgewertet sind
                waiting = on && !GalleryFaces.IsRunning(space.User.Id) && GalleryFaces.WaitingForGallery(space),
                tresor = new { exists = Tresor.Exists(space.User.Id), open },
                overview = on && open && Tresor.DbOf(space.User.Id) is { } t ? GalleryFaces.Overview(t, GalleryFaces.Scanned(space.Db), await GalleryFaces.VisibleAsync(http, config)) : null,
            });
        });
        // Einschalten (nur mit Engine) liest ein; Ausschalten löscht alle Gesichtsdaten und Namen
        api.MapPut("/faces", async (FacesBody body, HttpContext http, ServerConfig config, ILogger<FacesBody> log) =>
        {
            var space = http.Space();
            if (body.Enabled)
            {
                if (await GalleryFaces.Model(http.RequestAborted) == null) return Results.Json(new { error = "Die Photo-Engine bietet keine Gesichtererkennung an." }, statusCode: 503);
                if (!Tresor.Exists(space.User.Id)) return Results.BadRequest(new { error = "Personen liegen im Tresor. Bitte zuerst den Tresor einrichten." });
                space.UpdateSettings(s => s.Faces = true);
                GalleryFaces.Enable(space.Db);
                await GalleryFaces.KickAsync(http, config, log, force: true);
            }
            else
            {
                // Die Gesichtsdaten im Tresor gehen mit - dafür muss er offen sein
                var t = Tresor.Exists(space.User.Id) ? Tresor.Db(http) : null;
                space.UpdateSettings(s => s.Faces = false);
                GalleryFaces.Disable(space.Db, t);
            }
            return Results.Ok(new { enabled = body.Enabled });
        });
        api.MapGet("/faces/persons/{id:long}", async (long id, HttpContext http, ServerConfig config) =>
            Need(http) ?? Results.Bytes(GalleryFaces.Photos(Tresor.Db(http), await GalleryFaces.VisibleAsync(http, config), id, null), "application/json"));
        api.MapGet("/faces/groups/{id}", async (string id, HttpContext http, ServerConfig config) =>
            Need(http) ?? Results.Bytes(GalleryFaces.Photos(Tresor.Db(http), await GalleryFaces.VisibleAsync(http, config), null, id), "application/json"));
        // Gruppe oder einzelne Gesichter benennen (gleicher Name: dieselbe Person)
        api.MapPost("/faces/name", (FacesNameBody body, HttpContext http) =>
        {
            if (Need(http) is { } no) return no;
            var db = Tresor.Db(http);
            var faces = body.Faces ?? [];
            if (body.Group != null)
                faces = GalleryFaces.Groups(GalleryFaces.Load(db)).TryGetValue(body.Group, out var ids) ? ids
                    : throw new KeyNotFoundException("Diese Gruppe gibt es nicht mehr - die Ansicht wird neu geladen.");
            if (faces.Count == 0) throw new ArgumentException("Keine Gesichter angegeben.");
            return Results.Ok(new { person = GalleryFaces.Name(db, faces, body.Name) });
        });
        // Schnell zuordnen in der Übersicht: Gruppe ausblenden (spielt keine Rolle), wieder zeigen, Vorschlag ablehnen
        api.MapPost("/faces/groups/{id}/ignore", (string id, HttpContext http) =>
        {
            if (Need(http) is { } no) return no;
            GalleryFaces.Ignore(Tresor.Db(http), id);
            return Results.Ok();
        });
        api.MapDelete("/faces/ignored", (string? group, HttpContext http) =>
        {
            if (Need(http) is { } no) return no;
            GalleryFaces.Unignore(Tresor.Db(http), group);
            return Results.Ok();
        });
        api.MapPost("/faces/groups/{id}/reject", (string id, FacesRejectBody body, HttpContext http) =>
        {
            if (Need(http) is { } no) return no;
            GalleryFaces.RejectGroup(Tresor.Db(http), id, body.Person);
            return Results.Ok();
        });
        api.MapPost("/faces/persons/{id:long}/confirm", (long id, FacesIdsBody body, HttpContext http) =>
        {
            if (Need(http) is { } no) return no;
            GalleryFaces.Confirm(Tresor.Db(http), id, body.Faces);
            return Results.Ok();
        });
        api.MapPost("/faces/persons/{id:long}/reject", (long id, FacesIdsBody body, HttpContext http) =>
        {
            if (Need(http) is { } no) return no;
            GalleryFaces.Reject(Tresor.Db(http), id, body.Faces);
            return Results.Ok();
        });
        api.MapPut("/faces/persons/{id:long}", (long id, FacesPersonBody body, HttpContext http) =>
            Need(http) ?? Results.Ok(new { person = GalleryFaces.Change(Tresor.Db(http), id, body.Name, body.Cover, body.Hidden) }));
        api.MapDelete("/faces/persons/{id:long}", (long id, HttpContext http) =>
        {
            if (Need(http) is { } no) return no;
            GalleryFaces.Remove(Tresor.Db(http), id);
            return Results.Ok();
        });
        // Ausschnitt eines Gesichts; mit passendem v lange gültig
        api.MapGet("/faces/crop/{id:long}", async (long id, int? size, string? v, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            if (Need(http) is { } no) return no;
            if (await GalleryFaces.CropAsync(http, config, id, size ?? 128, ct) is not { } crop) return Results.NotFound();
            http.Response.Headers.CacheControl = v == crop.Stamp ? "private, max-age=31536000, immutable" : "private, no-cache";
            return Results.Bytes(crop.Data, "image/webp");
        });

        // ---------------- Zeitleiste in Monatsabschnitten (GalleryTimeline) ----------------
        // Erst die Monate mit Anzahl (klein), dann die Fotos der Monate im Bild; Filterleiste und Atlas rechnet der Server
        api.MapGet("/timeline/buckets", async (string? sort, bool? desc, bool? stack, string? ext, string? filter, string? group, HttpContext http, ServerConfig config) =>
        {
            var v = await GalleryTimeline.ViewAsync(http, config, GalleryTimeline.ParseQuery(sort, desc, stack, ext, filter, group));
            var body = GalleryTimeline.BucketsJson(v);
            return GalleryTimeline.Send(http, GalleryTimeline.EtagOf(body), () => body);
        });
        api.MapGet("/timeline/bucket", async (string k, string? sort, bool? desc, bool? stack, string? ext, string? filter, string? group, HttpContext http, ServerConfig config) =>
        {
            var v = await GalleryTimeline.ViewAsync(http, config, GalleryTimeline.ParseQuery(sort, desc, stack, ext, filter, group));
            if (!v.ByKey.TryGetValue(k, out var b)) return Results.Ok(new { k, items = Array.Empty<object>() });
            return GalleryTimeline.Send(http, b.Hash, () => GalleryTimeline.BucketJson(b));
        });
        // Handy-App: alle Fotos auf einmal, kompakt (statt Suche + Galerie-Ordner + Bildangaben, zusammen dutzende MB)
        // Eigene Adresse je Foto (#/gallery/photo/…, wie bei Immich): in welchem Monat es liegt - die Seite lädt dann diesen Monat
        api.MapGet("/timeline/locate", async (string id, string? sort, bool? desc, bool? stack, string? group, HttpContext http, ServerConfig config) =>
        {
            var v = await GalleryTimeline.ViewAsync(http, config, GalleryTimeline.ParseQuery(sort, desc, stack, null, null, group));
            var b = v.Buckets.FirstOrDefault(b => b.Items.Any(e => e.Id == id || e.Stack?.Any(x => x.Id == id) == true));
            return b == null ? Results.NotFound(new { error = "Dieses Foto gibt es nicht mehr." }) : Results.Ok(new { k = b.Key });
        });
        api.MapGet("/timeline/all", async (bool? stack, HttpContext http, ServerConfig config) =>
        {
            var v = await GalleryTimeline.ViewAsync(http, config, GalleryTimeline.ParseQuery("taken", true, stack ?? false, null, null));
            return GalleryTimeline.Send(http, GalleryTimeline.EtagOf(GalleryTimeline.BucketsJson(v)), () => GalleryTimeline.AllJson(v));
        });
        // Handy-App: Gruppen nach Quelle, Kategorie oder Tag - je Gruppe Name und Fotos (die App gruppiert nach Datum selbst)
        api.MapGet("/timeline/groups", async (string group, HttpContext http, ServerConfig config) =>
        {
            var v = await GalleryTimeline.ViewAsync(http, config, GalleryTimeline.ParseQuery("taken", true, false, null, null, group));
            var groups = new List<(string L, List<string> I)>();
            foreach (var b in v.Buckets)
            {
                var l = b.Label ?? "";
                if (groups.Count == 0 || groups[^1].L != l) groups.Add((l, []));
                groups[^1].I.AddRange(b.Items.Select(e => e.Id));
            }
            return Results.Ok(new { groups = groups.Select(g => new { l = g.L, i = g.I }) });
        });
        // Suche in der Galerie: eigene Fotos und Galerie-Ordner, auch nach Titel und Stichwörtern
        api.MapGet("/timeline/search", async (string q, bool? stack, string? ext, HttpContext http, ServerConfig config) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new { items = Array.Empty<object>() });
            var v = await GalleryTimeline.ViewAsync(http, config, GalleryTimeline.ParseQuery("taken", true, stack, ext, null));
            return Results.Bytes(GalleryTimeline.SearchJson(v, q.Trim(), persons: GalleryFaces.NamesByKey(http.Space(), Tresor.IsOpen(http, touch: false) ? Tresor.DbOf(http.Space().User.Id) : null),
                labelled: DmOffice.Dav.FileCategories.Matching(http.Space().Db, q.Trim())), "application/json");
        });
        api.MapGet("/timeline/facets", async (string cols, string? ext, string? filter, HttpContext http, ServerConfig config) =>
        {
            var list = cols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToArray();
            var body = await GalleryTimeline.FacetsAsync(http, config, GalleryTimeline.ParseQuery(null, null, false, ext, filter), list);
            return GalleryTimeline.Send(http, GalleryTimeline.EtagOf(body), () => body);
        });
        api.MapGet("/timeline/geo", async (bool? stack, string? ext, string? filter, HttpContext http, ServerConfig config) =>
        {
            var v = await GalleryTimeline.ViewAsync(http, config, GalleryTimeline.ParseQuery(null, null, stack, ext, filter));
            var body = GalleryTimeline.GeoJson(v);
            return GalleryTimeline.Send(http, GalleryTimeline.EtagOf(body), () => body);
        });

        // Alle Angaben einer Bilddatei aus einem Galerie-Ordner (Kopf der Datei, bei RAW mehr)
        api.MapGet("/external/exif", async (string id, HttpContext http, ServerConfig config, CancellationToken ct) =>
            Results.Ok((await GalleryFolders.AllTagsAsync(http.Space(), config, id, ct)).Select(x => new { group = x.Group, name = x.Name, value = x.Value })));

        // Abgleich beim Ansehen: das gezeigte Foto (liest den Kopf der Datei neu), die sichtbaren Kacheln (je Ordner einmal auflisten,
        // wenige noch nicht eingelesene lesen). Speicher nicht erreichbar: 200 mit „unverändert“
        api.MapPost("/external/refresh", async (RefreshBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
            Results.Ok(SeenJson(await GalleryFolders.RefreshAsync(http.Space(), config, body.Id, ct))));
        api.MapPost("/external/visible", async (VisibleBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var r = await GalleryFolders.VisibleAsync(http.Space(), config, body.Ids ?? [], ct);
            return Results.Ok(new { items = r.Items.Select(SeenJson), open = r.Open, reachable = r.Reachable });
        });

        // ready=1 (Kacheln): die große Vorschau nur, wenn sie schon da ist, sonst das kleine Vorschaubild - Blättern holt keine
        // Originale über die Freigabe (vorher entstand so für jede große Kachel eine große Vorschau aus dem ganzen Original)
        api.MapGet("/external/thumb", async (string id, int? size, string? orig, string? ready, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            if (ready is "1" or "true" && (size ?? Thumbs.Small) > Thumbs.Small && GalleryFolders.ReadyLarge(http.Space(), config, id) is { } r)
            {
                http.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.File(r.Image, "image/webp");
            }
            var data = await GalleryFolders.ThumbAsync(http.Space(), config, id, ready is "1" or "true" ? Thumbs.Small : size ?? Thumbs.Small, ct, orig is "1" or "true");
            if (data == null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(data, "image/webp");
        });

        api.MapPut("/external/thumb", async (string id, HttpRequest request, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            using var m = new MemoryStream();
            await request.Body.CopyToAsync(m, ct);
            if (m.Length > 5_000_000) return Results.BadRequest();
            GalleryFolders.StoreThumb(http.Space(), config, id, m.ToArray());
            return Results.Ok();
        }).DisableAntiforgery();

        // Das Original - im Browser nur Bilder und Videos (kein SVG, kein HTML), sonst als Download; Spulen in Videos (Range)
        api.MapGet("/external/file", (string id, bool? inline, HttpContext http, ServerConfig config, CancellationToken ct) =>
            SendExternal(http, http.Space(), config, id, inline == true, ct));
    }

    /// <summary>Abgleich eines Eintrags in der Form, die die Oberfläche kennt (Bildangaben wie bei /external).</summary>
    private static object SeenJson(GalleryFolders.Seen s) => new
    {
        id = s.Id, key = "ext:" + s.Id, s.Reachable, s.Changed, s.FileChanged, s.Gone, s.Etag, s.HasThumb, size = s.Entry?.Size, pending = s.Entry?.Pending,
        modified = s.Entry is { } f ? DateTimeOffset.FromUnixTimeMilliseconds(f.Modified).UtcDateTime : (DateTime?)null,
        meta = s.Entry is { Meta: { } m } e ? new
        {
            key = "ext:" + s.Id, m.Taken, m.Camera, m.Lens, m.Focal, m.Aperture, m.Exposure, m.Iso, m.Width, m.Height, m.Duration, m.Rating, m.Title, m.Keywords, m.Label,
            e.Description, m.Lat, m.Lon, m.Pick,
        } : null,
    };

    /// <summary>Original aus einem Galerie-Ordner (auch für Alben anderer): im Browser nur Bilder und Videos, sonst als Download.</summary>
    public static async Task<IResult> SendExternal(HttpContext http, UserSpace space, ServerConfig config, string id, bool inline, CancellationToken ct)
    {
        {
            var (src, path) = GalleryFolders.Resolve(space, config, id);
            // Externer Ordner gerade nicht erreichbar: gleich antworten statt an der Freigabe zu hängen
            if (!await ExternalFolders.ReachableAsync(space, config, src.Mount))
                return Results.Json(new { error = "Der externe Ordner ist gerade nicht erreichbar." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
            var mime = DavServer.Mime(path);
            var show = inline && GalleryFolders.KindOf(path) != null && mime is not "image/svg+xml" && (mime.StartsWith("image/") || mime.StartsWith("video/"));
            var stream = await driver.OpenReadAsync(path, ct);
            http.Response.RegisterForDispose(driver);
            http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            var name = path[(path.LastIndexOf('/') + 1)..];
            return show
                ? Results.Stream(stream, mime, enableRangeProcessing: stream.CanSeek)
                : Results.Stream(stream, "application/octet-stream", name, enableRangeProcessing: stream.CanSeek);
        }
    }

    /// <summary>Personen ausgeschaltet: 404 (die Ansicht fragt dann den Stand neu ab).</summary>
    private static IResult? Need(HttpContext http) => http.Space().Settings.Faces ? null : Results.NotFound(new { error = "Personen sind ausgeschaltet." });
}
