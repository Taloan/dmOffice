using System.Text;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>Die WebDAV-Dateien in der Weboberflaeche: ansehen, hochladen, ordnen, Papierkorb, Versionen, Freigaben.</summary>
public static class FilesApi
{
    public record PathBody(string Path);
    public record PathsBody(string? Path, string[]? Paths);
    public record MoveBody(string From, string To, bool Copy, string? Conflict);
    public record IdsBody(long[] Ids);
    public record FavoriteBody(string Path, bool On);
    public record SaveBody(string Path, string Text, string? Etag, bool Create);
    public record VersionBody(string Path, long Id);
    public record HashItem(string Sha256, long Size);
    public record HashesBody(HashItem[] Items, string? Dir = null);
    public record TrashSettings(int Days);
    /// <summary>Hoechstwerte der Verwaltung: Ablauf spaetestens nach so vielen Tagen, hoechstens so viele Abrufe.</summary>
    private static (DateTime? Expires, int? MaxDownloads) LinkLimits(LinkBody body)
    {
        var l = FileRetention.LimitsOf();
        var expires = body.Expires;
        if (l.LinkDays > 0)
        {
            var latest = DateTime.UtcNow.AddDays(l.LinkDays);
            if (expires == null || expires.Value.ToUniversalTime() > latest) expires = latest;
        }
        return (expires, l.LinkMaxDownloads > 0 ? FileRetention.Cap(body.MaxDownloads ?? 0, l.LinkMaxDownloads) : body.MaxDownloads);
    }

    public record LinkBody(string? Path, string? Mode, DateTime? Expires, string? Password, int? MaxDownloads = null);

    public record ShareBody(string Path, string[] Members, string[]? ReadOnly);
    public record LeaveBody(int Id);

    /// <summary>
    /// Pfad aus der Anfrage aufloesen: eigener Ordner, Ordner in einem mit mir geteilten Ordner (dann in der Ablage des Besitzers)
    /// oder „Mit mir geteilt“ selbst. Der Abgleichordner von notizi ist ein geschuetzter Bereich: in der Dateiablage unsichtbar
    /// und unveraenderlich - nur notizi selbst (ueber /dav) und die Notizen von dmOffice greifen darauf zu.
    /// </summary>
    private static FileLoc Loc(HttpContext http, string? path)
    {
        var loc = SharedFiles.Resolve(http, FileStore.Normalize(path ?? ""));
        if (loc.Protected(loc.Path)) throw new KeyNotFoundException("Ordner nicht gefunden.");
        return loc;
    }

    /// <summary>Wie <see cref="Loc"/>, fuer Loeschen und Verschieben: ein Ordner, in dem der notizi-Ordner liegt, wuerde ihn mitnehmen.</summary>
    private static FileLoc LocTree(HttpContext http, string? path)
    {
        var loc = Loc(http, path);
        if (FileStore.Contains(loc.Path, loc.Space.Settings.Notizi.Root) && !loc.Virtual)
            throw new InvalidOperationException("Dieser Ordner enthält den geschützten notizi-Abgleichordner und lässt sich nicht verschieben oder löschen.");
        return loc;
    }

    /// <summary>Datei oder Ordner am Ort, sonst 404.</summary>
    private static FileEntry Entry(HttpContext http, FileLoc loc) =>
        loc.Virtual ? SharedFiles.RootEntry(SharedFiles.Mounts(http)) : FileStore.Get(loc.Db, loc.Path) ?? throw new KeyNotFoundException("Nicht gefunden.");

    /// <summary>Kennzeichen fuer die Oberflaeche: Favoriten, Links, Personenfreigaben - nur in der eigenen Ablage.</summary>
    private sealed record Marks(HashSet<long> Favorites, HashSet<long> Links, HashSet<long> People)
    {
        public static readonly Marks None = new([], [], []);
    }

    private static Marks MarksOf(HttpContext http, FileLoc loc)
    {
        if (loc.Shared || loc.Virtual) return Marks.None;
        var me = http.Space();
        return new Marks(FileStore.FavoriteIds(me.Db), FileStore.SharedIds(me.Db),
            http.RequestServices.GetRequiredService<FolderShareStore>().All().Where(s => s.Owner == me.User.Id).Select(s => s.Folder).ToHashSet());
    }

    /// <summary>Was die Oberflaeche fuer eine Datei braucht. f: wie der Benutzer sie sieht (Pfad und fileid umgerechnet).</summary>
    private static object Item(FileEntry f, Marks? marks = null, FileMount? mount = null) => new
    {
        f.Path, f.Name, f.IsDir, f.Size, f.Mime, f.Modified, f.Created, f.FileId, f.Etag,
        favorite = marks?.Favorites.Contains(f.FileId) == true,
        shared = marks?.Links.Contains(f.FileId) == true,
        // Mit Personen geteilt (Besitzer) bzw. ein mit mir geteilter Ordner in „Mit mir geteilt“
        sharedWith = marks?.People.Contains(f.FileId) == true,
        owner = mount?.Owner.User.DisplayName,
        canWrite = mount?.CanWrite,
    };

    /// <summary>Eintraege eines Ordners, wie der Benutzer sie sieht.</summary>
    private static IEnumerable<object> List(HttpContext http, FileLoc loc)
    {
        if (loc.Virtual) return SharedFiles.Mounts(http).Select(m => Item(SharedFiles.MountEntry(m), null, m));
        var marks = MarksOf(http, loc);
        var items = FileStore.Children(loc.Db, loc.Path).Where(f => !loc.Protected(f.Path) && !(!loc.Shared && SharedFiles.Hidden(http, f.Path)))
            .Select(f => Item(loc.Show(f), marks));
        // In der Wurzel steht "Mit mir geteilt" wie ein Ordner (Programme und die Handy-App zeigen es so ohne Aenderung), solange jemand etwas teilt
        if (!loc.Shared && loc.Path.Length == 0 && SharedFiles.Mounts(http) is { Count: > 0 } mounts)
            items = items.Append(Item(SharedFiles.RootEntry(mounts)));
        return items;
    }

    /// <summary>Datei oder Ordner am Ort (nicht „Mit mir geteilt“ selbst), sonst 404.</summary>
    private static FileEntry FileAt(FileLoc loc) =>
        (loc.Virtual ? null : FileStore.Get(loc.Db, loc.Path)) ?? throw new KeyNotFoundException("Nicht gefunden.");

    /// <summary>Ein Ort in der eigenen Ablage - Teilen und Links sind Sache des Besitzers.</summary>
    private static FileLoc Own(HttpContext http, string path)
    {
        var loc = Loc(http, path);
        return loc.Shared || loc.Virtual ? throw new UnauthorizedAccessException("Teilen kann nur der Besitzer.") : loc;
    }

    /// <summary>In der eigenen Ablage unsichtbar: der notizi-Ordner und ein eigener Eintrag, den „Mit mir geteilt“ verdeckt.</summary>
    private static bool Protected(HttpContext http, string path) =>
        FileStore.InRoot(path, http.Space().Settings.Notizi.Root) || SharedFiles.Under(http, path);

    /// <summary>Mit wem ein eigener Ordner geteilt ist.</summary>
    private static IEnumerable<object> People(HttpContext http, long folder) =>
        http.RequestServices.GetRequiredService<FolderShareStore>().Of(http.Space().User.Id, folder) is { } s ? People(http, s) : [];

    private static IEnumerable<object> People(HttpContext http, FolderShare s)
    {
        var users = http.RequestServices.GetRequiredService<UserStore>();
        return s.Members.Select(m => users.ById(m)).OfType<UserRecord>()
            .Select(u => (object)new { u.Id, u.Login, u.DisplayName, readOnly = s.ReadOnly.Contains(u.Id) }).ToList();
    }


    /// <summary>Nur Bilder, PDF, Ton und Video im Browser zeigen - nie HTML oder SVG aus der Ablage unter dieser Adresse.</summary>
    public static bool Inline(string mime) =>
        mime.StartsWith("image/") && mime != "image/svg+xml" || mime == "application/pdf" || mime.StartsWith("audio/") || mime.StartsWith("video/");

    /// <summary>Datei ausliefern: aus der Datenbank gestreamt, mit Bereichen (Video spulen), ETag und Aenderungszeit.</summary>
    public static IResult Send(HttpContext http, UserDb db, FileEntry f, bool inline)
    {
        var mime = f.Mime is { Length: > 0 } m && m != "application/octet-stream" ? m : DavServer.Mime(f.Path);
        bool show = inline && Inline(mime);
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Bild, Ton, Video direkt geoeffnet: als Dokument ohne Skripte und ohne Zugriff auf die Seite
        if (show && mime != "application/pdf") http.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'";
        http.Response.Headers.CacheControl = "private, no-cache";
        return Results.Stream(FileStore.OpenRead(db, f.Path), show ? mime : "application/octet-stream", show ? null : f.Name,
            new DateTimeOffset(DateTime.SpecifyKind(f.Modified, DateTimeKind.Utc)), new Microsoft.Net.Http.Headers.EntityTagHeaderValue(f.Etag.StartsWith('"') ? f.Etag : $"\"{f.Etag}\""),
            enableRangeProcessing: true);
    }

    /// <summary>Vorschaubild ausliefern; mit ?v=ETag darf der Browser es behalten.</summary>
    public static async Task<IResult> SendThumb(HttpContext http, UserDb db, FileEntry f, int? size, string? v)
    {
        var data = await Thumbs.Get(db, f, size ?? Thumbs.Small, http.RequestAborted);
        if (data == null) return Results.NotFound();
        http.Response.Headers.CacheControl = v == f.Etag ? "private, max-age=31536000, immutable" : "private, no-cache";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Bytes(data, "image/webp");
    }

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
            var loc = Loc(http, path);
            var dir = Entry(http, loc);
            if (!dir.IsDir) throw new ArgumentException("Kein Ordner.");
            return new
            {
                path = loc.Show(loc.Path),
                usage = FileStore.Usage(db),
                trash = FileStore.TrashUsage(db),
                versions = FileStore.VersionUsage(db),
                quota = FileStore.Quota(db),
                // alles, was der Benutzer belegt (auch Mails, Kalender, Kontakte, Notizen) - das Kontingent gilt dafuer
                used = UserStorage.Used(db),
                favorite = !loc.Shared && !loc.Virtual && FileStore.FavoriteIds(db).Contains(dir.FileId),
                // In einem geteilten Ordner: wem er gehoert, ob ich darin schreiben darf, wo er anfaengt
                canWrite = loc.CanWrite,
                owner = loc.Mount?.Owner.User.DisplayName,
                mount = loc.Mount?.Path,
                items = List(http, loc)
            };
        });

        // Details: Groesse samt Inhalt, Anzahl, Versionen, Links, Personen
        // Ohne path (die Oberflaeche laesst den leeren Pfad weg): "Meine Dateien"
        api.MapGet("/info", (string? path, HttpContext http) =>
        {
            var loc = Loc(http, path);
            var db = loc.Db;
            // "Meine Dateien" hat keine eigene Zeile: Ordner mit der juengsten Aenderung darin
            var f = loc.Virtual ? Entry(http, loc) : FileStore.Get(db, loc.Path) ?? (loc.Path.Length == 0
                ? new FileEntry("", true, 0, "", null, DateTime.MinValue,
                    db.Scalar("SELECT MAX(modified) FROM files") is string m ? DateTime.Parse(m, null, System.Globalization.DateTimeStyles.RoundtripKind) : DateTime.UtcNow)
                : throw new KeyNotFoundException("Nicht gefunden."));
            var inside = loc.Virtual
                ? SharedFiles.Mounts(http).SelectMany(x => new[] { x.Dir }.Concat(FileStore.Descendants(x.Owner.Db, x.Dir.Path))).ToList()
                : f.IsDir ? FileStore.Descendants(db, loc.Path).Where(x => !loc.Protected(x.Path) && !(!loc.Shared && SharedFiles.Under(http, x.Path))).ToList() : [];
            var own = !loc.Shared && !loc.Virtual;
            return new
            {
                item = Item(loc.Show(f), MarksOf(http, loc), loc.IsMountRoot ? loc.Mount : null),
                sha256 = f.IsDir || loc.Virtual ? null : FileHashes.Of(db, loc.Path),
                size = f.IsDir ? inside.Sum(x => x.Size) : f.Size,
                files = inside.Count(x => !x.IsDir),
                folders = inside.Count(x => x.IsDir),
                versions = f.IsDir || loc.Virtual ? [] : FileStore.Versions(db, f.FileId),
                links = own ? Links.List(db, f.FileId) : [],
                people = own && f.IsDir ? People(http, f.FileId) : [],
                canWrite = loc.CanWrite,
                owner = loc.Mount?.Owner.User.DisplayName,
            };
        });

        api.MapGet("/download", (string path, bool? inline, long? version, HttpContext http) =>
        {
            var loc = Loc(http, path);
            var f = FileAt(loc);
            if (f.IsDir) throw new ArgumentException("Ordner lassen sich nicht herunterladen.");
            if (version is { } v)
            {
                var data = FileStore.OpenVersion(loc.Db, f.FileId, v) ?? throw new KeyNotFoundException("Version nicht gefunden.");
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.Stream(data, "application/octet-stream", f.Name, enableRangeProcessing: true);
            }
            // "Zuletzt verwendet" ist eigen - die Liste des Besitzers bleibt unberuehrt
            if (!loc.Shared) FileStore.Used(loc.Db, f.FileId);
            return Send(http, loc.Db, f, inline == true);
        });

        api.MapGet("/thumb", async (string path, int? size, string? v, HttpContext http) =>
        {
            var loc = Loc(http, path);
            return await SendThumb(http, loc.Db, FileAt(loc), size, v);
        });

        // Standbild eines Videos aus dem Browser (der Server hat keinen Video-Decoder) - ein Vorschaubild, auch beim Lesen
        api.MapPut("/thumb", async (string path, HttpRequest request, HttpContext http) =>
        {
            // Ein Vorschaubild ist klein: nicht erst bis zu 512 MB in den Speicher lesen und dann ablehnen
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 8L * 1024 * 1024;
            var loc = Loc(http, path).Writable();
            var f = FileAt(loc);
            if (Thumbs.KindOf(f.Name, f.Mime) != Thumbs.Kind.Video) throw new ArgumentException("Nur für Videos.");
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, http.RequestAborted);
            if (ms.Length > 8L * 1024 * 1024) return Results.BadRequest(new { error = "Bild zu groß." });
            return Thumbs.Accept(loc.Db, f, ms.ToArray()) ? Results.Ok() : Results.BadRequest(new { error = "Kein Bild." });
        });

        // conflict: replace (Vorgabe, wie bisher), rename (Nummer anhaengen), skip, fail (409);
        // mtime: Aenderungszeit der Datei in ms seit 1970 (automatisches Hochladen der Handy-App), sonst jetzt
        // In einen geteilten Ordner: in die Ablage des Besitzers, sein Kontingent zaehlt
        api.MapPost("/upload", async (string? path, string? conflict, long? mtime, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            DateTime? modified = mtime is > 0 and < 253402300800000 ? DateTime.UnixEpoch.AddMilliseconds(mtime.Value) : null;
            var dir = FileStore.Normalize(path ?? "");
            var written = new List<object>();
            // Teil fuer Teil direkt in den BlobStore - kein Puffern des Formulars (das laege sonst unverschluesselt in /tmp)
            await foreach (var file in MultipartUpload.Files(request, ct))
            {
                // Mit Unterordnern (Ordner hochladen): "a/b/c.txt"
                var rel = FileStore.Normalize(file.FileName);
                if (rel.Length == 0) throw new ArgumentException("Dateiname fehlt.");
                var loc = Loc(http, dir.Length == 0 ? rel : dir + "/" + rel).Creatable();
                var db = loc.Db;
                var full = loc.Path;
                if (FileStore.Parent(full) is { Length: > 0 } parent) FileStore.EnsureDir(db, parent);
                if (FileStore.Get(db, full) is { } existing)
                {
                    if (existing.IsDir || conflict == "rename") full = FileStore.FreeName(db, full);
                    else if (conflict == "skip") { written.Add(new { path = loc.Show(full), skipped = true, sha256 = FileHashes.Of(db, full) }); continue; }
                    else if (conflict == "fail") return Results.Conflict(new { error = "Gibt es schon.", conflict = true, path = loc.Show(full) });
                }
                try { await FileStore.PutAsync(db, full, file.Body, string.IsNullOrEmpty(file.ContentType) || file.ContentType == "application/octet-stream" ? DavServer.Mime(full) : file.ContentType, mtime: modified, ct: ct); }
                catch (QuotaExceededException ex) { return Results.Json(new { error = loc.Shared ? $"Beim Besitzer ist kein Platz mehr frei. {ex.Message}" : ex.Message }, statusCode: 507); }
                // Pruefsumme des Geschriebenen: die App vergleicht sie mit ihrer eigenen, bevor sie die Datei als gesichert zaehlt
                written.Add(new { path = loc.Show(full), skipped = false, sha256 = FileHashes.Of(db, full) });
                // Vorschaubild gleich im Hintergrund erzeugen - sonst wartet die Kachelansicht beim ersten Oeffnen
                if (FileStore.Get(db, full) is { } added && Thumbs.KindOf(added.Name, added.Mime) is Thumbs.Kind.Image or Thumbs.Kind.Pdf)
                    _ = Task.Run(async () => { try { await Thumbs.Get(db, added, Thumbs.Small, CancellationToken.None); } catch (Exception) { /* beim Ansehen erneut */ } });
            }
            return Results.Ok(new { files = written });
        }).DisableAntiforgery();

        // Welche dieser Pfade gibt es schon (vor dem Hochladen fragen)
        // Liegen diese Inhalte (Pruefsumme + Groesse) schon irgendwo in der Ablage? Fuer die App: "schon gesichert"
        // unabhaengig von Name und Ordner, und Gegenprobe vor dem Freigeben von Platz auf dem Geraet.
        // pending: Dateien ohne Pruefsumme (noch nicht nachgerechnet) - was dort liegt, kann hier noch fehlen.
        // dir: nur direkt in diesem Ordner ("Duplikate ueberspringen" aus). Papierkorb, Versionen und notizi zaehlen nie.
        api.MapPost("/hashes", (HashesBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            if (body.Items is not { } items) throw new ArgumentException("Keine Prüfsummen.");
            if (items.Length > FileHashes.MaxLookup) throw new ArgumentException($"Höchstens {FileHashes.MaxLookup} Prüfsummen auf einmal.");
            var found = FileHashes.Lookup(db, items.Where(i => i.Sha256 != null).Select(i => (i.Sha256, i.Size)),
                body.Dir is { } d ? FileStore.Normalize(d) : null);
            return new
            {
                found = found.Select(x => new { sha256 = x.Key.Sha256, size = x.Key.Size, path = x.Value }),
                pending = FileHashes.PendingFiles(db),
            };
        });

        api.MapPost("/exists", (PathsBody body, HttpContext http) =>
        {
            var existing = new List<string>();
            foreach (var p in (body.Paths ?? []).Select(FileStore.Normalize).Distinct())
            {
                try { if (Loc(http, p) is var loc && (loc.Virtual || FileStore.Get(loc.Db, loc.Path) != null)) existing.Add(p); }
                catch (KeyNotFoundException) { /* geschuetzt oder kein solcher geteilter Ordner */ }
            }
            return new { existing };
        });

        api.MapPost("/mkdir", (PathBody body, HttpContext http) =>
        {
            var loc = Loc(http, body.Path).Creatable();
            FileStore.Mkcol(loc.Db, loc.Path);
            return Results.Ok(new { path = loc.Show(loc.Path) });
        });

        // Textdatei speichern (Bearbeiten im Browser, neue Textdatei); mit ETag: nur, wenn sie sich nicht geaendert hat
        api.MapPost("/save", (SaveBody body, HttpContext http) =>
        {
            var loc = Loc(http, body.Path).Creatable();
            var db = loc.Db;
            var data = Encoding.UTF8.GetBytes(body.Text ?? "");
            if (data.Length > 20 * 1024 * 1024) throw new ArgumentException("Zu groß für den Texteditor.");
            try
            {
                var (etag, _) = FileStore.Put(db, loc.Path, data, DavServer.Mime(loc.Path), ifMatch: body.Create ? null : body.Etag, ifNoneMatch: body.Create ? "*" : null);
                if (!loc.Shared) FileStore.Used(db, FileStore.Get(db, loc.Path)?.FileId ?? 0);
                return Results.Ok(new { path = loc.Show(loc.Path), etag });
            }
            catch (Pim.PreconditionFailedException)
            {
                return Results.Conflict(new { error = body.Create ? "Gibt es schon." : "Die Datei wurde inzwischen geändert.", conflict = true });
            }
            catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message }, statusCode: 507); }
        });

        // In den Papierkorb (in einem geteilten Ordner: in den des Besitzers); Antwort: die eigenen Eintraege dort (fuer Rueckgaengig)
        api.MapPost("/delete", (PathsBody body, HttpContext http) =>
        {
            var locs = (body.Paths ?? (body.Path != null ? [body.Path] : [])).Select(p => LocTree(http, p).Changeable()).ToList();
            var trash = new List<long>();
            foreach (var loc in locs)
                if (FileStore.Delete(loc.Db, loc.Path, out var id) && id > 0 && !loc.Shared) trash.Add(id);
            return Results.Ok(new { trash });
        });

        // conflict: fail (Vorgabe, 409), replace (Ziel in den Papierkorb), rename (Nummer anhaengen), skip.
        // Aus einem geteilten Ordner heraus wird kopiert - das Original bleibt beim Besitzer (Antwort copied: true).
        // Eigenes in einen geteilten Ordner verschieben: es wird dorthin kopiert und verschwindet bei mir.
        api.MapPost("/move", async (MoveBody body, HttpContext http) =>
        {
            var from = LocTree(http, body.From);
            var to = Loc(http, body.To).Creatable();
            if (from.Virtual) throw new UnauthorizedAccessException("„Mit mir geteilt“ bleibt, wo es ist.");
            bool sameArea = from.Db == to.Db && from.Mount?.Share.Id == to.Mount?.Share.Id;
            bool copy = body.Copy || (from.Shared && !sameArea);
            if (!copy) from.Changeable();
            bool overwrite = false;
            if (FileStore.Get(to.Db, to.Path) != null)
            {
                switch (body.Conflict)
                {
                    case "rename": to = to with { Path = FileStore.FreeName(to.Db, to.Path) }; break;
                    case "skip": return Results.Ok(new { path = to.Show(to.Path), skipped = true });
                    case "replace": LocTree(http, body.To).Changeable(); overwrite = true; break;
                    default: return Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = to.Show(to.Path) });
                }
            }
            try
            {
                long replaced = 0;
                if (from.Db == to.Db) FileStore.MoveOrCopy(to.Db, from.Path, to.Path, overwrite, copy, out replaced);
                else
                {
                    await SharedFiles.CopyAcross(from, to, overwrite, http.RequestAborted);
                    if (!copy) FileStore.Delete(from.Db, from.Path);
                }
                return Results.Ok(new { path = to.Show(to.Path), skipped = false, replaced = replaced > 0 && !to.Shared ? replaced : (long?)null, copied = copy && !body.Copy });
            }
            catch (DavConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (DavForbiddenException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (QuotaExceededException ex) { return Results.Json(new { error = to.Shared ? $"Beim Besitzer ist kein Platz mehr frei. {ex.Message}" : ex.Message }, statusCode: 507); }
            catch (Pim.PreconditionFailedException) { return Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = to.Show(to.Path) }); }
        });

        // Mehrere Dateien/Ordner als ZIP. POST als Formular (lange Auswahl), GET fuer einzelne Ordner.
        api.MapMethods("/zip", ["GET", "POST"], async (HttpContext http) =>
        {
            var src = http.Request.HasFormContentType ? (await http.Request.ReadFormAsync()).ToDictionary(x => x.Key, x => x.Value)
                : http.Request.Query.ToDictionary(x => x.Key, x => x.Value);
            var locs = src.TryGetValue("paths", out var ps) ? ps.Select(p => Loc(http, p)).ToList() : [];
            var name = src.TryGetValue("name", out var n) && n.ToString() is { Length: > 0 } nn ? nn : "Dateien";
            if (locs.Count == 0) throw new ArgumentException("Nichts ausgewählt.");
            if (locs.Any(l => l.Virtual)) throw new ArgumentException("„Mit mir geteilt“ lässt sich nicht als Ganzes herunterladen.");
            if (locs.Select(l => l.Db).Distinct().Count() > 1) throw new ArgumentException("Bitte nur aus einem Ordner zugleich herunterladen.");
            var first = locs[0];
            // Namen im ZIP relativ zum aktuellen Ordner; liegt der woanders (geteilte Ordner in „Mit mir geteilt“), relativ zu ihrem Ordner beim Besitzer
            var b = SharedFiles.Resolve(http, FileStore.Normalize(src.TryGetValue("base", out var bb) ? bb.ToString() : ""));
            var baseDir = b.Db == first.Db && !b.Virtual ? b.Path : FileStore.Parent(first.Path) ?? "";
            await Zip.Write(http, first.Db, baseDir, locs.Select(l => l.Path), first.Protected, Path.GetFileNameWithoutExtension(name) + ".zip");
        }).DisableAntiforgery();

        // Suchen nach Namen: unter einem Ordner (Vorgabe: ueberall, samt der mit mir geteilten Ordner), optional nur eine Art
        api.MapGet("/search", (string? q, string? path, string? type, HttpContext http) =>
        {
            var under = Loc(http, path);
            var areas = under.Virtual || under.Path.Length == 0 && !under.Shared
                ? SharedFiles.Mounts(http).Select(m => new FileLoc(m.Owner, m.Dir.Path, m)).ToList() : [];
            if (!under.Virtual) areas.Insert(0, under);
            // Nach Art (Ansicht Fotos): alle Bilder bis zu einer grossen Grenze, sonst die ersten Treffer
            var limit = string.IsNullOrEmpty(type) ? 500 : 5000;
            var hits = areas.SelectMany(a =>
            {
                var marks = MarksOf(http, a);
                return FileStore.Search(a.Db, a.Path, q ?? "", limit, f => !a.Protected(f.Path) && !(!a.Shared && SharedFiles.Under(http, f.Path))
                        && (string.IsNullOrEmpty(type) || FileKinds.Of(f) == type))
                    .Select(f => Item(a.Show(f), marks));
            });
            return new { items = hits.Take(limit) };
        });

        api.MapGet("/recent", (HttpContext http) =>
        {
            var db = http.Space().Db;
            var marks = MarksOf(http, new FileLoc(http.Space(), "", null));
            return new { items = FileStore.Recent(db).Where(x => !Protected(http, x.File.Path)).Select(x => new { item = Item(x.File, marks), used = x.Used }) };
        });

        api.MapGet("/favorites", (HttpContext http) =>
        {
            var db = http.Space().Db;
            var marks = MarksOf(http, new FileLoc(http.Space(), "", null));
            return new { items = FileStore.Favorites(db).Where(f => !Protected(http, f.Path)).Select(f => Item(f, marks)) };
        });

        api.MapPost("/favorite", (FavoriteBody body, HttpContext http) =>
        {
            var loc = Loc(http, body.Path);
            if (loc.Shared || loc.Virtual) throw new ArgumentException("Favoriten gibt es nur in der eigenen Ablage.");
            if (loc.Path.Length == 0) throw new ArgumentException("„Meine Dateien“ ist immer da.");
            var f = FileStore.Get(loc.Db, loc.Path) ?? throw new KeyNotFoundException("Nicht gefunden.");
            FileStore.Favorite(loc.Db, f.FileId, body.On);
            return Results.Ok();
        });

        // ---------------- Papierkorb ----------------

        api.MapGet("/trash", (HttpContext http) =>
        {
            var db = http.Space().Db;
            return new
            {
                days = TrashDays(db),
                usage = FileStore.TrashUsage(db),
                items = FileStore.Trash(db).Where(t => !Protected(http, t.Path))
                    .Select(t => new { t.Id, t.Path, t.Name, t.IsDir, t.Size, t.Deleted, t.Mime })
            };
        });

        api.MapPost("/trash/restore", (IdsBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            var restored = new List<string>();
            foreach (var id in body.Ids)
            {
                try { restored.Add(FileStore.Restore(db, id)); }
                catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message, restored }, statusCode: 507); }
            }
            return Results.Ok(new { restored });
        });

        api.MapPost("/trash/delete", (IdsBody body, HttpContext http) => Results.Ok(new { deleted = FileStore.PurgeTrash(http.Space().Db, body.Ids) }));

        api.MapPost("/trash/empty", (HttpContext http) => Results.Ok(new { deleted = FileStore.PurgeTrash(http.Space().Db, null) }));

        api.MapPut("/trash/settings", (TrashSettings body, HttpContext http) =>
        {
            if (body.Days is < 0 or > 3650) throw new ArgumentException("0 bis 3650 Tage.");
            http.Space().Db.Set(TrashDaysKey, body.Days.ToString());
            return Results.Ok();
        });

        // ---------------- Aufbewahrung (Einstellungen -> Dateien) ----------------
        api.MapGet("/settings", (HttpContext http) =>
        {
            var db = http.Space().Db;
            return new { settings = FileRetention.Load(db), limits = FileRetention.LimitsOf(), usage = FileStore.Usage(db), trash = FileStore.TrashUsage(db), versions = FileStore.VersionUsage(db), quota = FileStore.Quota(db), used = UserStorage.Used(db) };
        });
        api.MapPut("/settings", (FileRetention.Settings body, HttpContext http) =>
        {
            var db = http.Space().Db;
            FileRetention.Save(db, body);
            // Gleich anwenden, damit der Platz sofort frei wird
            FileRetention.Apply(db, DateTime.UtcNow);
            return Results.Ok(new { settings = FileRetention.Load(db), trash = FileStore.TrashUsage(db), versions = FileStore.VersionUsage(db) });
        });
        api.MapPost("/versions/purge", (HttpContext http) =>
        {
            var db = http.Space().Db;
            var n = db.Exec("DELETE FROM file_versions");
            FileStore.CollectBlobs(db);
            return Results.Ok(new { deleted = n });
        });

        // ---------------- Fruehere Versionen (in einem geteilten Ordner: die des Besitzers) ----------------

        api.MapGet("/versions", (string path, HttpContext http) =>
        {
            var loc = Loc(http, path);
            return new { items = FileStore.Versions(loc.Db, FileAt(loc).FileId) };
        });

        api.MapPost("/versions/restore", (VersionBody body, HttpContext http) =>
        {
            var loc = Loc(http, body.Path).Writable();
            try { return Results.Ok(new { etag = FileStore.RestoreVersion(loc.Db, loc.Path, body.Id) }); }
            catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message }, statusCode: 507); }
        });

        // ---------------- Oeffentliche Links (nur in der eigenen Ablage) ----------------

        api.MapGet("/links", (string? path, HttpContext http) =>
        {
            var db = http.Space().Db;
            long? fileId = null;
            if (path != null)
            {
                var loc = Loc(http, path);
                if (loc.Shared || loc.Virtual) return new { items = Enumerable.Empty<object>() };
                fileId = FileAt(loc).FileId;
            }
            var now = DateTime.UtcNow;
            return new
            {
                items = Links.List(db, fileId).Select(l => (Link: l, File: FileStore.ById(db, l.FileId)))
                    .Where(x => x.File == null || !Protected(http, x.File.Path))
                    .Select(x => (object)new
                    {
                        x.Link.Id, x.Link.Token, x.Link.Mode, x.Link.Expires, x.Link.HasPassword, x.Link.Created, x.Link.Downloads, x.Link.MaxDownloads,
                        expired = x.Link.Expired(now) || x.Link.UsedUp,
                        // Datei im Papierkorb: Link ruht, bis sie zurueck ist
                        item = x.File == null ? null : Item(x.File),
                    })
            };
        });

        api.MapPost("/links", (LinkBody body, HttpContext http) =>
        {
            var space = http.Space();
            var loc = Loc(http, body.Path ?? throw new ArgumentException("Pfad fehlt."));
            if (loc.Shared || loc.Virtual) throw new UnauthorizedAccessException("Links zu einem geteilten Ordner legt nur der Besitzer an.");
            var f = FileAt(loc);
            if (!f.IsDir && body.Mode != "read") throw new ArgumentException("Hochladen geht nur in Ordner.");
            var (expires, maxDl) = LinkLimits(body);
            var link = Links.Create(space.Db, f.FileId, body.Mode ?? "read", expires, body.Password, maxDl);
            http.RequestServices.GetRequiredService<LinkIndex>().Add(space.User.Id, link.Id, link.Token);
            return Results.Ok(link);
        });

        api.MapPut("/links/{id}", (string id, LinkBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            var link = Links.Get(db, id) ?? throw new KeyNotFoundException("Link nicht gefunden.");
            if (FileStore.ById(db, link.FileId) is { IsDir: false } && body.Mode != "read") throw new ArgumentException("Hochladen geht nur in Ordner.");
            var (expires, maxDl) = LinkLimits(body);
            Links.Update(db, id, body.Mode, expires, body.Password, maxDl);
            return Results.Ok(Links.Get(db, id));
        });

        api.MapDelete("/links/{id}", (string id, HttpContext http) =>
        {
            var space = http.Space();
            if (!Links.Delete(space.Db, id)) throw new KeyNotFoundException("Link nicht gefunden.");
            http.RequestServices.GetRequiredService<LinkIndex>().Remove(space.User.Id, id);
            return Results.Ok();
        });

        // ---------------- Mit Personen teilen (nur Ordner) ----------------

        // Meine geteilten Ordner mit ihren Mitgliedern - fuer die Ansicht "Freigegeben"; mit path: nur dieser Ordner
        api.MapGet("/shares", (string? path, HttpContext http, FolderShareStore shares) =>
        {
            var me = http.Space();
            long? only = path == null ? null : FileAt(Own(http, path)).FileId;
            var marks = MarksOf(http, new FileLoc(me, "", null));
            return new
            {
                items = shares.All().Where(s => s.Owner == me.User.Id && (only == null || s.Folder == only))
                    .Select(s => (Share: s, Dir: FileStore.ById(me.Db, s.Folder)))
                    // Ordner im Papierkorb: die Freigabe ruht, bis er zurueck ist
                    .Where(x => x.Dir != null && !Protected(http, x.Dir.Path))
                    .Select(x => new { x.Share.Id, item = Item(x.Dir!, marks), members = People(http, x.Share) })
            };
        });

        // Einen eigenen Ordner teilen (readOnly: wer davon nur lesen darf); leere Mitglieder: Freigabe aufheben
        api.MapPut("/shares", (ShareBody body, HttpContext http, FolderShareStore shares, UserStore users) =>
        {
            var me = http.Space();
            var f = FileAt(Own(http, body.Path));
            if (!f.IsDir) throw new ArgumentException("Mit Personen teilen geht nur mit Ordnern – einzelne Dateien teilst du per Link.");
            if (!SharedFiles.Shareable(me.Db, f)) throw new ArgumentException("Dieser Ordner lässt sich nicht teilen: Er ist der geschützte notizi-Abgleichordner oder enthält ihn.");
            var members = body.Members.Where(m => m != me.User.Id && users.ById(m) != null).ToList();
            shares.Set(me.User.Id, f.FileId, members, body.ReadOnly);
            return Results.Ok(new { members = People(http, f.FileId) });
        });

        // Mit mir geteilte Ordner ("Mit mir geteilt")
        api.MapGet("/shared", (HttpContext http) => new
        {
            items = SharedFiles.Mounts(http).Select(m => new
            {
                m.Share.Id, m.Path, m.Name, folder = m.Dir.Name, owner = m.Owner.User.DisplayName, m.CanWrite, fileId = m.Id(m.Dir.FileId),
            })
        });

        // Mit mir geteilten Ordner nicht mehr anzeigen (der Besitzer behaelt ihn)
        api.MapPost("/shared/leave", (LeaveBody body, HttpContext http, FolderShareStore shares) =>
        {
            shares.Leave(body.Id, http.Space().User.Id);
            return Results.Ok();
        });
    }

    public const string TrashDaysKey = "files.trashDays";

    /// <summary>Wirksame Aufbewahrung in Tagen (Einstellung des Benutzers, begrenzt durch die Verwaltung).</summary>
    public static int TrashDays(UserDb db) => FileRetention.Load(db).TrashDays;
}

/// <summary>Art einer Datei fuer Filter und Symbole - dieselben Namen wie in der Oberflaeche (files/kinds.ts).</summary>
public static class FileKinds
{
    public static string Of(FileEntry f)
    {
        if (f.IsDir) return "folder";
        var ext = Path.GetExtension(f.Name).ToLowerInvariant();
        var mime = f.Mime ?? "";
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" or ".heic" or ".heif" or ".tif" or ".tiff" or ".ico" or ".jfif" or ".avif" => "image",
            ".pdf" => "pdf",
            ".doc" or ".docx" or ".odt" or ".rtf" or ".pages" => "doc",
            ".xls" or ".xlsx" or ".ods" or ".csv" or ".numbers" => "sheet",
            ".ppt" or ".pptx" or ".odp" or ".key" => "slides",
            ".mp3" or ".m4a" or ".wav" or ".ogg" or ".oga" or ".opus" or ".flac" or ".aac" or ".wma" => "audio",
            ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv" or ".avi" or ".wmv" or ".ogv" => "video",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" => "archive",
            ".txt" or ".md" or ".markdown" or ".log" or ".json" or ".xml" or ".yaml" or ".yml" or ".ini" or ".conf" or ".cfg" or ".toml"
                or ".cs" or ".js" or ".ts" or ".tsx" or ".jsx" or ".py" or ".java" or ".kt" or ".c" or ".h" or ".cpp" or ".go" or ".rs" or ".sh"
                or ".ps1" or ".bat" or ".sql" or ".css" or ".scss" or ".html" or ".htm" or ".php" or ".rb" or ".swift" or ".ics" or ".vcf" => "text",
            _ => mime.StartsWith("image/") ? "image" : mime.StartsWith("audio/") ? "audio" : mime.StartsWith("video/") ? "video"
                : mime.StartsWith("text/") ? "text" : "other",
        };
    }
}
