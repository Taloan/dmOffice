using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Gallery;

/// <summary>
/// Alben (Version 2): feste und smarte, teilbar mit Benutzern dieses Servers. Bei festen Alben dürfen Mitglieder eigene Fotos
/// dazulegen und nur diese wieder herausnehmen; smarte sind für Mitglieder nur zum Ansehen. Vorschau und Original kommen
/// immer über das Album (Rechte: Besitzer oder Mitglied, Foto gehört dazu) aus der Ablage dessen, dem das Foto gehört.
/// </summary>
public static class AlbumApi
{
    public record CreateBody(string Name, string? Kind, string? Rule);
    public record UpdateBody(string? Name, string? Rule, List<string>? Members);
    public record PathsBody(List<string> Paths);
    public record KeysBody(List<string> Keys);
    /// <summary>Schnellsammlung: In true/false legt fest, ob hinein oder heraus; ohne: alle drin → heraus, sonst hinein.</summary>
    public record QuickBody(List<string> Paths, bool? In);

    /// <summary>Ein Foto, wie ein Album es zeigt. Ref: "f:&lt;fileid&gt;" oder "x:&lt;quelle&gt;/&lt;pfad&gt;".</summary>
    public sealed record Photo(string Owner, string Ref, string Name, long Size, string? Mime, DateTime Modified, string Etag,
        FileMeta.Row? Meta, string? Description, string? Path, string? Ext)
    {
        public string Key => Owner + "|" + Ref;
    }

    // ---------------- Fotos eines Benutzers ----------------

    /// <summary>Alle Fotos und Videos eines Benutzers wie in seiner Galerie: eigene Ablage (ohne ausgeschlossene Ordner) und Galerie-Ordner.</summary>
    public static List<Photo> Media(UserSpace space, ServerConfig config)
    {
        var db = space.Db;
        var excluded = space.Settings.PhotoExcludes;
        var meta = FileMeta.List(db, null).ToDictionary(m => m.Path, StringComparer.Ordinal);
        var system = FileStore.SystemFilter(db);
        var own = FileStore.Search(db, "", "", 1_000_000, f => !f.IsDir && FileKinds.Of(f) is "image" or "video" && (excluded.Count == 0 || !FileKinds.UnderAny(excluded, f.Path))
                && !system(f.Path))
            .Select(f => new Photo(space.User.Id, "f:" + f.FileId, f.Name, f.Size, f.Mime, f.Modified, f.Etag, meta.GetValueOrDefault(f.Path), null, f.Path, null));
        var ext = GalleryFolders.List(space, config).Select(e => new Photo(space.User.Id, "x:" + e.Source + "/" + e.Path, e.Path[(e.Path.LastIndexOf('/') + 1)..], e.Size, e.Mime,
            DateTimeOffset.FromUnixTimeMilliseconds(e.Modified).UtcDateTime, e.Size.ToString("x") + "-" + e.Modified.ToString("x"), e.Meta, e.Description, null, "ext:" + e.Source + "/" + e.Path));
        return own.Concat(ext).ToList();
    }

    /// <summary>Ein Foto eines Benutzers nach seinem Verweis; null, wenn es nicht mehr da ist oder inzwischen in einem Systemordner liegt.</summary>
    /// <param name="system">Systemordner-Filter (FileStore.SystemFilter); fehlt er, wird er gelesen</param>
    public static Photo? Resolve(UserSpace space, ServerConfig config, string reference, Func<string, bool>? system = null)
    {
        if (reference.StartsWith("f:") && long.TryParse(reference[2..], out var id))
        {
            if (FileStore.ById(space.Db, id) is not { IsDir: false } f) return null;
            if ((system ?? FileStore.SystemFilter(space.Db))(f.Path)) return null;
            var meta = FileMeta.List(space.Db, f.Path.Contains('/') ? f.Path[..f.Path.LastIndexOf('/')] : "").FirstOrDefault(m => m.Path == f.Path);
            return new Photo(space.User.Id, reference, f.Name, f.Size, f.Mime, f.Modified, f.Etag, meta, null, f.Path, null);
        }
        if (reference.StartsWith("x:"))
        {
            var ext = reference[2..];
            var e = GalleryFolders.List(space, config).FirstOrDefault(x => x.Source + "/" + x.Path == ext);
            return e == null ? null : new Photo(space.User.Id, reference, e.Path[(e.Path.LastIndexOf('/') + 1)..], e.Size, e.Mime,
                DateTimeOffset.FromUnixTimeMilliseconds(e.Modified).UtcDateTime, e.Size.ToString("x") + "-" + e.Modified.ToString("x"), e.Meta, e.Description, null, "ext:" + ext);
        }
        return null;
    }

    // ---------------- Smart-Alben: die Filterleiste auf dem Server ----------------

    internal sealed record Rule(List<string> Cols, List<List<string>> Sel);

    internal static Rule? ParseRule(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Rule>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Werte einer Eigenschaft wie in der Filterleiste (web/src/files/PhotoFilter.tsx, ATTRS); '' heißt unbekannt.</summary>
    internal static string[] Values(string attr, Photo p, TimeZoneInfo zone) => Values(attr, p.Name, p.Modified, p.Meta, zone, p.Ext, p.Description);

    /// <summary>Wie oben, aus den einzelnen Angaben (auch für die Zeitleiste der Galerie, GalleryTimeline).</summary>
    /// <param name="id">Kennung in der Galerie („ext:&lt;Ordner&gt;/…“ für Galerie-Ordner, sonst der Pfad in der Ablage) - für die Quelle.</param>
    internal static string[] Values(string attr, string name, DateTime modified, FileMeta.Row? m, TimeZoneInfo zone, string? id = null, string? description = null)
    {
        DateTime Taken() => TimeZoneInfo.ConvertTimeFromUtc(m?.Taken is long t ? DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime : DateTime.SpecifyKind(modified, DateTimeKind.Utc), zone);
        return attr switch
        {
            "year" => [Taken().Year.ToString(CultureInfo.InvariantCulture)],
            "month" => [Taken().ToString("yyyy-MM", CultureInfo.InvariantCulture)],
            "camera" => [m?.Camera ?? ""],
            "lens" => [m?.Lens ?? ""],
            "focal" => [m?.Focal is double f && f != 0 ? Num(f) : ""],
            "aperture" => [m?.Aperture is double a && a != 0 ? Num(a) : ""],
            "exposure" => [m?.Exposure is double x && x != 0 ? Num(x) : ""],
            "iso" => [m?.Iso is int i && i != 0 ? i.ToString(CultureInfo.InvariantCulture) : ""],
            "keyword" => string.IsNullOrEmpty(m?.Keywords) ? [""] : m.Keywords.Split('\n'),
            "title" => [m?.Title?.Trim() ?? ""],
            "description" => [description?.Trim() ?? ""],
            "rating" => [(m?.Rating ?? 0).ToString(CultureInfo.InvariantCulture)],
            "label" => [m?.Label ?? ""],
            // Markierung wie Lightroom: P ausgewählt, X abgelehnt, U ohne
            "pick" => [m?.Pick is 1 ? "P" : m?.Pick is -1 ? "X" : "U"],
            // Quelle: die eigene Ablage („own“) oder ein Galerie-Ordner („ext:&lt;Kennung&gt;“)
            "source" => [SourceOf(id)],
            "type" => [name.LastIndexOf('.') is > 0 and var d ? name[(d + 1)..].ToUpperInvariant() : ""],
            "orientation" => [m?.Width is not int w || m.Height is not int h || w == 0 || h == 0 ? "" : w > h ? "quer" : w < h ? "hoch" : "quadrat"],
            _ => [""],
        };
    }

    internal static string SourceOf(string? id) => id is { } x && x.StartsWith("ext:", StringComparison.Ordinal) && x.IndexOf('/', 4) is > 4 and var slash ? x[..slash] : "own";

    internal static bool Matches(string? ruleJson, Photo p, TimeZoneInfo zone)
    {
        var rule = ParseRule(ruleJson);
        if (rule == null) return false;
        for (var k = 0; k < rule.Cols.Count && k < rule.Sel.Count; k++)
        {
            if (rule.Sel[k].Count == 0) continue;
            if (!Values(rule.Cols[k], p, zone).Any(rule.Sel[k].Contains)) return false;
        }
        return true;
    }

    // ---------------- Inhalt eines Albums ----------------

    /// <summary>Smarte Alben: die Trefferliste, höchstens einmal gleichzeitig berechnet (Lazy) - jede Vorschau und jede Anfrage braucht sie,
    /// und bei zehntausenden Fotos rechnete sonst jede gleichzeitige Anfrage die ganze Regel selbst: die Vorschaubilder kamen kaum an.</summary>
    private static readonly ConcurrentDictionary<long, (DateTime At, string? Rule, Lazy<List<Photo>> List)> SmartCache = new();
    private static readonly TimeSpan SmartTtl = TimeSpan.FromMinutes(2);

    public static List<Photo> ItemsOf(Album album, Spaces spaces, ServerConfig config, AlbumStore store)
    {
        if (album.Kind == Album.Smart)
        {
            var entry = SmartCache.AddOrUpdate(album.Id,
                _ => (DateTime.UtcNow, album.Rule, Build()),
                (_, old) => old.Rule == album.Rule && DateTime.UtcNow - old.At < SmartTtl ? old : (DateTime.UtcNow, album.Rule, Build()));
            try { return entry.List.Value; }
            catch { SmartCache.TryRemove(album.Id, out _); throw; }
            Lazy<List<Photo>> Build() => new(() =>
            {
                var owner = spaces.Get(album.Owner);
                return Media(owner, config).Where(p => Matches(album.Rule, p, owner.Zone)).ToList();
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
        var result = new List<Photo>();
        foreach (var g in store.Items(album.Id).GroupBy(i => i.Owner))
        {
            UserSpace space;
            try { space = spaces.Get(g.Key); } catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { continue; }
            var system = FileStore.SystemFilter(space.Db);
            foreach (var i in g) if (Resolve(space, config, i.Ref, system) is { } p) result.Add(p);
        }
        return result;
    }

    private static object Dto(Photo p, string me, UserStore users) => new
    {
        key = p.Key, owner = p.Owner, ownerName = users.ById(p.Owner)?.DisplayName, mine = p.Owner == me,
        name = p.Name, size = p.Size, mime = p.Mime, modified = p.Modified, etag = p.Etag,
        // Eigene Fotos: wo sie liegen (für Aktionen in der eigenen Ablage)
        path = p.Owner == me ? p.Path ?? p.Ext : null,
        meta = p.Meta == null && p.Description == null ? null : new
        {
            p.Meta?.Taken, p.Meta?.Camera, p.Meta?.Lens, p.Meta?.Focal, p.Meta?.Aperture, p.Meta?.Exposure, p.Meta?.Iso,
            p.Meta?.Width, p.Meta?.Height, p.Meta?.Duration, p.Meta?.Rating, p.Meta?.Title, p.Meta?.Keywords, p.Meta?.Label, p.Meta?.Pick, description = p.Description,
        },
    };

    private static Album Visible(AlbumStore store, long id, string me) =>
        store.Get(id) is { } a && a.Sees(me) && (a.Kind != Album.Quick || Features.On(Features.QuickCollection)) ? a : throw new KeyNotFoundException("Album nicht gefunden.");

    /// <summary>
    /// Verweis auf ein eigenes Foto: Pfad in der Ablage („f:&lt;fileid&gt;“), ext:-Kennung eines Galerie-Ordners („x:…“) oder
    /// ein eigenes Foto aus einem Album („alb:&lt;Album&gt;/&lt;Besitzer&gt;|&lt;Verweis&gt;“); null, wenn es keins ist.
    /// </summary>
    private static string? RefOf(UserSpace space, ServerConfig config, string path, Func<string, bool> system)
    {
        if (path.StartsWith("alb:", StringComparison.Ordinal))
        {
            var key = path.IndexOf('/') is > 4 and var s ? path[(s + 1)..] : "";
            var bar = key.IndexOf('|');
            return bar > 0 && key[..bar] == space.User.Id && key[(bar + 1)..] is { } r && (r.StartsWith("f:") || r.StartsWith("x:")) ? r : null;
        }
        // Systemordner (Tagebuch-Medien, notizi, System-Tresor) gehören wie in der Galerie nicht in Alben
        if (path.StartsWith("ext:")) { GalleryFolders.Resolve(space, config, path[4..]); return "x:" + path[4..]; }
        return FileStore.Get(space.Db, path) is { IsDir: false } f && FileKinds.Of(f) is "image" or "video" && !system(f.Path) ? "f:" + f.FileId : null;
    }

    /// <summary>Kennung in der Galerie zu einem Verweis (Pfad in der Ablage oder ext:-Kennung); null, wenn die Datei weg ist.</summary>
    private static string? GalleryIdOf(UserSpace space, string reference) =>
        reference.StartsWith("x:") ? "ext:" + reference[2..]
        : reference.StartsWith("f:") && long.TryParse(reference[2..], out var id) && FileStore.ById(space.Db, id) is { IsDir: false } f ? f.Path : null;

    /// <summary>Darf me dieses Foto über das Album sehen?</summary>
    private static Photo Allowed(Album album, string key, Spaces spaces, ServerConfig config, AlbumStore store)
    {
        var bar = key.IndexOf('|');
        if (bar <= 0) throw new KeyNotFoundException("Nicht gefunden.");
        var (owner, reference) = (key[..bar], key[(bar + 1)..]);
        if (album.Kind == Album.Smart)
            return ItemsOf(album, spaces, config, store).FirstOrDefault(p => p.Key == key) ?? throw new KeyNotFoundException("Nicht gefunden.");
        if (!store.Contains(album.Id, owner, reference)) throw new KeyNotFoundException("Nicht gefunden.");
        return Resolve(spaces.Get(owner), config, reference) ?? throw new KeyNotFoundException("Nicht gefunden.");
    }

    /// <summary>Vorschaubild eines Fotos aus der Ablage seines Besitzers.</summary>
    public static async Task<IResult> Thumb(HttpContext http, Photo p, Spaces spaces, ServerConfig config, int? size)
    {
        var owner = spaces.Get(p.Owner);
        if (p.Ext != null)
        {
            var data = await GalleryFolders.ThumbAsync(owner, config, p.Ext[4..], size ?? Thumbs.Small, http.RequestAborted);
            if (data == null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(data, "image/webp");
        }
        var f = FileStore.ById(owner.Db, long.Parse(p.Ref[2..], CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("Nicht gefunden.");
        return await FilesApi.SendThumb(http, owner.Db, f, size, null);
    }

    /// <summary>Das Original: im Browser ansehen (inline) oder herunterladen (download false: nur ansehen erlaubt).</summary>
    public static async Task<IResult> File(HttpContext http, Photo p, Spaces spaces, ServerConfig config, bool inline, bool download = true)
    {
        var owner = spaces.Get(p.Owner);
        if (!download) inline = true;
        if (p.Ext != null) return await GalleryApi.SendExternal(http, owner, config, p.Ext[4..], inline, http.RequestAborted);
        var f = FileStore.ById(owner.Db, long.Parse(p.Ref[2..], CultureInfo.InvariantCulture)) ?? throw new KeyNotFoundException("Nicht gefunden.");
        if (!download && !FilesApi.Viewable(f)) throw new UnauthorizedAccessException("Nur ansehen.");
        return FilesApi.Send(http, owner.Db, f, inline);
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/gallery/albums").RequireAuthorization();

        api.MapGet("", (HttpContext http, AlbumStore store, Spaces spaces, ServerConfig config, UserStore users) =>
        {
            var me = http.Space().User.Id;
            // Schnellsammlung: vom System angelegt, steht vorn
            var quick = Features.On(Features.QuickCollection);
            if (quick) store.Quick(me);
            return store.ForUser(me).Where(a => quick || a.Kind != Album.Quick).OrderBy(a => a.Kind == Album.Quick ? 0 : 1).Select(a =>
            {
                var items = a.Holds ? null : ItemsOf(a, spaces, config, store);
                var first = a.Holds ? store.Items(a.Id).FirstOrDefault() : null;
                return new
                {
                    id = a.Id, name = a.Name, kind = a.Kind, rule = a.Owner == me ? a.Rule : null, owner = a.Owner, ownerName = users.ById(a.Owner)?.DisplayName,
                    mine = a.Owner == me, members = a.Owner == me ? a.Members : null, shared = a.Members.Count > 0,
                    count = items?.Count ?? store.Items(a.Id).Count,
                    cover = items?.FirstOrDefault()?.Key ?? (first == null ? null : first.Owner + "|" + first.Ref),
                };
            });
        });

        api.MapPost("", (CreateBody body, HttpContext http, AlbumStore store) =>
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > 80) return Results.BadRequest(new { error = "Name fehlt oder ist zu lang." });
            var kind = body.Kind == Album.Smart ? Album.Smart : Album.Fixed;
            if (kind == Album.Smart && ParseRule(body.Rule) == null) return Results.BadRequest(new { error = "Smart-Album ohne Filter." });
            return Results.Ok(new { id = store.Create(http.Space().User.Id, name, kind, kind == Album.Smart ? body.Rule : null) });
        });

        api.MapPut("/{id:long}", (long id, UpdateBody body, HttpContext http, AlbumStore store, UserStore users) =>
        {
            var me = http.Space().User.Id;
            var a = Visible(store, id, me);
            if (a.Owner != me) throw new UnauthorizedAccessException("Nur der Besitzer ändert das Album.");
            if (a.Kind == Album.Quick) return Results.BadRequest(new { error = "Die Schnellsammlung lässt sich nicht umbenennen oder teilen." });
            var name = body.Name?.Trim();
            if (name is { Length: 0 or > 80 }) return Results.BadRequest(new { error = "Name fehlt oder ist zu lang." });
            if (body.Rule != null && (a.Kind != Album.Smart || ParseRule(body.Rule) == null)) return Results.BadRequest(new { error = "Ungültiger Filter." });
            store.Update(id, name, body.Rule);
            if (body.Members != null) store.SetMembers(id, body.Members.Where(u => u != me && users.ById(u) != null));
            SmartCache.TryRemove(id, out _);
            return Results.Ok();
        });

        // Besitzer: löschen; Mitglied: aussteigen (seine Fotos gehen mit)
        api.MapDelete("/{id:long}", (long id, HttpContext http, AlbumStore store) =>
        {
            var me = http.Space().User.Id;
            var a = Visible(store, id, me);
            if (a.Kind == Album.Quick) return Results.BadRequest(new { error = "Die Schnellsammlung lässt sich nicht löschen." });
            if (a.Owner == me) store.Delete(id); else store.Leave(id, me);
            SmartCache.TryRemove(id, out _);
            return Results.Ok();
        });

        api.MapGet("/{id:long}/items", (long id, HttpContext http, AlbumStore store, Spaces spaces, ServerConfig config, UserStore users) =>
        {
            var me = http.Space().User.Id;
            var a = Visible(store, id, me);
            return new { album = new { a.Id, a.Name, a.Kind, mine = a.Owner == me }, items = ItemsOf(a, spaces, config, store).Select(p => Dto(p, me, users)) };
        });

        // Eigene Fotos (Pfad der Ablage oder ext:-Kennung eines Galerie-Ordners) in ein festes Album legen
        api.MapPost("/{id:long}/add", (long id, PathsBody body, HttpContext http, AlbumStore store, ServerConfig config) =>
        {
            var space = http.Space();
            var a = Visible(store, id, space.User.Id);
            if (!a.Holds) return Results.BadRequest(new { error = "In ein Smart-Album lässt sich nichts legen." });
            var system = FileStore.SystemFilter(space.Db);
            var refs = (body.Paths ?? []).Select(p => RefOf(space, config, p, system)).OfType<string>().ToList();
            return Results.Ok(new { added = store.Add(id, space.User.Id, refs, space.User.Id) });
        });

        // ---------------- Schnellsammlung (Kennung quickcoll) ----------------

        // Was drin ist, als Kennungen der Galerie (Pfad in der Ablage, ext:…) - für den Kreis auf den Kacheln
        api.MapGet("/quick", (HttpContext http, AlbumStore store) =>
        {
            if (!Features.On(Features.QuickCollection)) return Results.NotFound();
            var space = http.Space();
            var a = store.Quick(space.User.Id);
            return Results.Ok(new { id = a.Id, paths = store.Items(a.Id).Select(i => GalleryIdOf(space, i.Ref)).OfType<string>() });
        });

        // Wie Lightroom (Taste B): sind schon alle drin, kommen sie heraus, sonst kommen die fehlenden hinein
        api.MapPost("/quick/toggle", (QuickBody body, HttpContext http, AlbumStore store, ServerConfig config) =>
        {
            if (!Features.On(Features.QuickCollection)) return Results.NotFound();
            var space = http.Space();
            var me = space.User.Id;
            var a = store.Quick(me);
            var system = FileStore.SystemFilter(space.Db);
            var refs = (body.Paths ?? []).Select(p => RefOf(space, config, p, system)).OfType<string>().Distinct().ToList();
            if (refs.Count == 0) return Results.BadRequest(new { error = "Keine Fotos gewählt." });
            var inside = body.In is bool into ? !into : refs.All(r => store.Contains(a.Id, me, r));
            if (inside) store.Remove(a.Id, refs.Select(r => (me, r)));
            else store.Add(a.Id, me, refs, me);
            return Results.Ok(new { @in = !inside, count = store.Items(a.Id).Count });
        });

        // Herausnehmen: der Besitzer alles, Mitglieder nur ihre eigenen Fotos
        api.MapPost("/{id:long}/remove", (long id, KeysBody body, HttpContext http, AlbumStore store) =>
        {
            var me = http.Space().User.Id;
            var a = Visible(store, id, me);
            var items = (body.Keys ?? []).Select(k => k.IndexOf('|') is > 0 and var b ? (k[..b], k[(b + 1)..]) : ("", "")).Where(x => x.Item1.Length > 0).ToList();
            if (a.Owner != me && items.Any(x => x.Item1 != me)) throw new UnauthorizedAccessException("Herausnehmen darfst du nur deine eigenen Fotos.");
            store.Remove(id, items);
            return Results.Ok();
        });

        api.MapGet("/{id:long}/thumb", async (long id, string key, int? size, HttpContext http, AlbumStore store, Spaces spaces, ServerConfig config) =>
            await Thumb(http, Allowed(Visible(store, id, http.Space().User.Id), key, spaces, config, store), spaces, config, size));

        api.MapGet("/{id:long}/file", async (long id, string key, bool? inline, HttpContext http, AlbumStore store, Spaces spaces, ServerConfig config) =>
            await File(http, Allowed(Visible(store, id, http.Space().User.Id), key, spaces, config, store), spaces, config, inline == true));
    }

    /// <summary>Für den Chat (Zeigen): Inhalt und Fotos eines Albums prüfen, ohne Mitglied zu sein.</summary>
    public static Photo PhotoOf(Album album, string key, Spaces spaces, ServerConfig config, AlbumStore store) => Allowed(album, key, spaces, config, store);
}
