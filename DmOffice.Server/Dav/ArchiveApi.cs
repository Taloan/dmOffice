using System.Collections.Concurrent;
using System.Text;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Archive im Dateibrowser (/api/files/archive…): ZIP, 7z, RAR, TAR, TAR.GZ wie einen Ordner durchblättern, Einträge öffnen
/// und entpacken; ZIPs auch ändern (hinzufügen, umbenennen, bearbeiten, entfernen, Ordner anlegen). Änderungen sammeln sich
/// (<see cref="ArchiveEdits"/>), bis gespeichert oder verworfen wird. Nur in der eigenen Ablage und in mit einem geteilten
/// Ordnern - nicht im Tresor und nicht in externen Ordnern. Ändern nur mit Schreibrecht auf den Ordner.
/// </summary>
public static partial class FilesApi
{
    public record ArchivePathBody(string Path);
    public record ArchiveUnlockBody(string Path, string Password);
    public record ArchiveEntryBody(string Path, string Entry);
    public record ArchiveMoveBody(string Path, string From, string To);
    public record ArchiveEntriesBody(string Path, string[] Entries);
    public record ArchiveTextBody(string Path, string Entry, string Text, bool Create);
    public record ArchiveSaveBody(string Path, bool Copy);
    public record ArchiveExtractBody(string Path, string? Dir, string[]? Entries, string Target);

    /// <summary>Ein geöffnetes Archiv: Ort, Datei, Verzeichnis, offene Änderungen des Benutzers und ob er sie speichern darf.</summary>
    private sealed record Arc(FileLoc Loc, FileEntry File, ArchiveListing Listing, ArchiveEdits.State? Edit, UserSpace Me, string? Password)
    {
        public string Shown => Loc.Show(Loc.Path);
        /// <summary>Die Datei hat sich geändert, seit die Änderungen begonnen haben - Speichern geht dann nur als Kopie.</summary>
        public bool Stale => Edit != null && Edit.Etag != File.Etag;
    }

    private static Arc OpenArc(HttpContext http, string? path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Pfad fehlt.");
        if (path.StartsWith("ext:", StringComparison.Ordinal) || TresorFiles.IdOfPath(path) != null)
            throw new ArgumentException("Archive lassen sich vorerst nur in der eigenen Dateiablage öffnen.");
        var loc = Loc(http, path);
        var f = FileAt(loc);
        if (f.IsDir || Archives.FormatOf(f.Name) == null) throw new ArgumentException("Kein unterstütztes Archiv (ZIP, 7z, RAR, TAR, TAR.GZ).");
        var me = http.Space();
        var password = Archives.PasswordOf(me.User.Id, loc.Db, loc.Path, f.Etag);
        var listing = Archives.List(loc.Db, loc.Path, f.Etag, password);
        return new Arc(loc, f, listing, ArchiveEdits.Get(me.Db, loc.Db.UserId, loc.Path), me, password);
    }

    /// <summary>Warum sich das Archiv nicht ändern lässt - null, wenn es geht.</summary>
    private static string? ReadOnlyReason(Arc a)
    {
        var l = a.Listing;
        if (l.Format != "zip") return Archives.Label(l.Format) + "e lassen sich nur lesen und entpacken.";
        if (l.Unsafe > 0) return "Das Archiv enthält Einträge mit unzulässigen Pfaden (etwa „../“) und lässt sich nur lesen.";
        if (l.Bomb) return "Die angegebenen Größen sehen nach einer ZIP-Bombe aus. Das Archiv wird nicht ausgepackt.";
        if (a.File.Size > Archives.MaxEditBytes || l.Entries.Count > Archives.MaxEditEntries)
            return "Das Archiv ist zu groß zum Ändern (über 2 GB oder 20.000 Einträge). Lesen und Entpacken gehen.";
        if (a.Loc.ShowOnly) return "Nur gezeigt.";
        if (!a.Loc.CanWrite) return "Nur lesbar – der Besitzer hat keine Bearbeitung erlaubt.";
        return null;
    }

    /// <summary>Offene Änderungen zum Weiterarbeiten - beginnt sie, wenn es noch keine gibt. Wirft, wenn Ändern nicht geht.</summary>
    private static ArchiveEdits.State Editable(Arc a)
    {
        if (ReadOnlyReason(a) is { } why) throw new UnauthorizedAccessException(why);
        a.Loc.Writable();
        // Verschlüsselt: ändern erst nach dem Entsperren (gespeichert wird mit demselben Passwort)
        if (a.Listing.Encrypted && a.Password == null) throw new ArchivePasswordException(false);
        return a.Edit ?? ArchiveEdits.Start(a.Listing, a.Loc.Db.UserId, a.Loc.Path, a.Shown, a.File.Etag);
    }

    /// <summary>Ein Eintrag für die Oberfläche (Name und Art ergeben sich aus dem Pfad).</summary>
    private sealed record ArcRow(string Path, bool IsDir, long Size, long Packed, DateTime? Modified, bool Added, bool Changed = false)
    {
        public string Name => Path[(Path.LastIndexOf('/') + 1)..];
        public string? Mime => IsDir ? null : DavServer.Mime(Path);
    }

    /// <summary>Einträge direkt in einem Ordner des Archivs - im geänderten Stand, wenn es offene Änderungen gibt.</summary>
    private static List<ArcRow> ArcChildren(Arc a, string dir)
    {
        static bool Direct(string p, string dir) => ArchiveEdits.Parent(p) == dir;
        if (a.Edit is { } s)
        {
            if (dir.Length > 0 && !s.Dirs.Contains(dir)) throw new KeyNotFoundException("Diesen Ordner gibt es im Archiv nicht.");
            return s.Dirs.Where(d => Direct(d, dir)).Select(d => new ArcRow(d, true, 0, 0, null, false))
                .Concat(s.Files.Where(x => Direct(x.Key, dir)).Select(x => new ArcRow(x.Key, false, x.Value.S,
                    x.Value.E != null ? a.Listing.Find(x.Value.E)?.Packed ?? 0 : 0, x.Value.M, x.Value.B != null && !x.Value.R, x.Value.R))).ToList();
        }
        var dirs = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        var files = new Dictionary<string, ArcRow>(StringComparer.Ordinal);
        bool found = dir.Length == 0;
        foreach (var e in a.Listing.Entries)
        {
            if (e.Path == dir && e.IsDir) found = true;
            if (!(dir.Length == 0 || e.Path.StartsWith(dir + "/", StringComparison.Ordinal))) continue;
            found = true;
            var rest = dir.Length == 0 ? e.Path : e.Path[(dir.Length + 1)..];
            var slash = rest.IndexOf('/');
            // Ordner auch dann, wenn es im Archiv nur Dateien darin gibt (ZIPs ohne eigene Ordnereinträge)
            if (slash >= 0) dirs.TryAdd(dir.Length == 0 ? rest[..slash] : dir + "/" + rest[..slash], null);
            else if (e.IsDir) dirs[e.Path] = e.Modified;
            // Bei gleichen Namen gilt der letzte Eintrag (wie beim Auspacken)
            else files[e.Path] = new ArcRow(e.Path, false, e.Size, e.Packed, e.Modified, false);
        }
        if (!found) throw new KeyNotFoundException("Diesen Ordner gibt es im Archiv nicht.");
        // Eine Datei und ein Ordner mit gleichem Namen: der Ordner
        return dirs.Select(d => new ArcRow(d.Key, true, 0, 0, d.Value, false)).Concat(files.Values.Where(f => !dirs.ContainsKey(f.Path))).ToList();
    }

    /// <summary>Vorschaubilder von Bildern im Archiv: klein und kurz im Speicher (je Benutzer, Archiv-Stand und Eintrag).</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, byte[] Data)> ArcThumbs = new();
    private const long MaxArcThumbSource = 40L * 1024 * 1024;

    private static async Task<byte[]> ReadUpTo(Stream s, long max, CancellationToken ct)
    {
        using var m = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0)
        {
            if (m.Length + n > max) throw new ArgumentException("Zu groß für die Vorschau.");
            m.Write(buffer, 0, n);
        }
        return m.ToArray();
    }

    private static void MapArchive(RouteGroupBuilder api)
    {
        var arc = api.MapGroup("/archive");
        // Passwort fehlt oder falsch: eigene Antwort, damit die Oberfläche danach fragt
        arc.AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (ArchivePasswordException ex) { return Results.Json(new { error = ex.Message, password = true, wrong = ex.Wrong }, statusCode: 403); }
            catch (VirusFoundException ex) { return Results.Json(new { error = ex.Message }, statusCode: 422); }
            catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message }, statusCode: 507); }
            catch (DavConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // Inhalt eines Ordners im Archiv, dazu Format, ob es sich ändern lässt und offene Änderungen
        arc.MapGet("", (string path, string? dir, HttpContext http) =>
        {
            var a = OpenArc(http, path);
            var inner = Archives.Inner(dir);
            var s = a.Edit;
            return Results.Ok(new
            {
                path = a.Shown, name = a.File.Name, format = a.Listing.Format, label = Archives.Label(a.Listing.Format),
                encrypted = a.Listing.Encrypted, unlocked = a.Password != null, readOnly = ReadOnlyReason(a),
                bomb = a.Listing.Bomb, @unsafe = a.Listing.Unsafe, total = a.Listing.Total, count = a.Listing.Entries.Count,
                pending = s == null ? null : new { s.Changes, s.Started, s.Touched, expires = Min(s.Touched + ArchiveEdits.Idle, s.Started + ArchiveEdits.MaxAge), stale = a.Stale },
                dir = inner,
                items = ArcChildren(a, inner).Select(r => new { r.Path, r.Name, r.IsDir, r.Size, r.Packed, r.Modified, r.Added, r.Changed, r.Mime }),
            });
        });

        // Verschlüsseltes Archiv entsperren: Passwort prüfen und nur im Speicher merken
        arc.MapPost("/unlock", (ArchiveUnlockBody body, HttpContext http) =>
        {
            var loc = Loc(http, body.Path);
            var f = FileAt(loc);
            if (string.IsNullOrEmpty(body.Password)) throw new ArgumentException("Passwort fehlt.");
            Archives.CheckPassword(loc.Db, loc.Path, f.Etag, body.Password);
            Archives.Remember(http.Space().User.Id, loc.Db, loc.Path, f.Etag, body.Password);
            return Results.Ok();
        });

        // Einen Eintrag ausliefern: ansehen (Bild, PDF, Ton, Video, Text) oder herunterladen - entpackt beim Senden
        arc.MapGet("/entry", (string path, string entry, bool? inline, HttpContext http) =>
        {
            var a = OpenArc(http, path);
            var inner = Archives.Inner(entry);
            var mime = DavServer.Mime(inner);
            bool show = inline == true && Inline(mime);
            if (!show && !(inline == true && mime.StartsWith("text/") && mime != "text/html")) a.Loc.NotShowOnly();
            var stream = a.Edit is { } s ? ArchiveEdits.Open(a.Me.Db, a.Loc.Db, s, a.Listing, inner, a.Password)
                : Archives.OpenEntry(a.Loc.Db, a.Loc.Path, a.Listing, inner, a.Password);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (show && mime != "application/pdf") http.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'";
            http.Response.Headers.CacheControl = "private, no-cache";
            return Results.Stream(stream, show ? mime : "application/octet-stream", show ? null : inner[(inner.LastIndexOf('/') + 1)..]);
        });

        // Vorschaubild eines Bilds im Archiv
        arc.MapGet("/thumb", async (string path, string entry, int? size, HttpContext http) =>
        {
            var a = OpenArc(http, path);
            var inner = Archives.Inner(entry);
            if (Thumbs.KindOf(inner, DavServer.Mime(inner)) != Thumbs.Kind.Image || Thumbs.IsRaw(inner)) return Results.NotFound();
            var px = Thumbs.Snap(size ?? Thumbs.Small);
            var item = a.Edit?.Files.GetValueOrDefault(inner);
            var key = $"{a.Me.User.Id}|{a.Loc.Db.UserId}|{a.Loc.Path}|{a.File.Etag}|{item?.B}|{inner}|{px}";
            if (!ArcThumbs.TryGetValue(key, out var hit))
            {
                var declared = item?.S ?? a.Listing.Find(inner)?.Size ?? 0;
                if (declared > MaxArcThumbSource) return Results.NotFound();
                byte[] data;
                await using (var s = a.Edit is { } st ? ArchiveEdits.Open(a.Me.Db, a.Loc.Db, st, a.Listing, inner, a.Password)
                           : Archives.OpenEntry(a.Loc.Db, a.Loc.Path, a.Listing, inner, a.Password))
                    data = await ReadUpTo(s, MaxArcThumbSource, http.RequestAborted);
                if (Thumbs.FromBytes(data, px) is not { } thumb) return Results.NotFound();
                hit = (DateTime.UtcNow, thumb);
                ArcThumbs[key] = hit;
                foreach (var old in ArcThumbs.OrderByDescending(x => x.Value.At).Skip(300).Select(x => x.Key).ToList()) ArcThumbs.TryRemove(old, out _);
            }
            http.Response.Headers.CacheControl = "private, max-age=600";
            return Results.File(hit.Data, "image/webp");
        });

        // Hinzufügen (Hochladen, Ziehen, auch mit Unterordnern): conflict replace ersetzt, sonst 409 mit den vorhandenen Namen
        arc.MapPost("/upload", async (string path, string? dir, string? conflict, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            var a = OpenArc(http, path);
            var s = Editable(a);
            var baseDir = Archives.Inner(dir);
            var added = new List<string>();
            try
            {
                await foreach (var file in MultipartUpload.Files(request, ct))
                {
                    var inner = Archives.Inner(baseDir.Length == 0 ? file.FileName : baseDir + "/" + file.FileName);
                    if (s.Files.ContainsKey(inner) && conflict != "replace")
                        return Results.Conflict(new { error = "Gibt es im Archiv schon.", conflict = true, entry = inner });
                    var room = Archives.MaxEditBytes * 2 - s.Files.Values.Sum(f => f.S);
                    var blob = await a.Me.Db.Blobs.WriteAsync(file.Body, room, () => new ArchiveException("Das Archiv würde zu groß zum Ändern."), ct);
                    try
                    {
                        // Virenprüfung wie beim Hochladen in die Ablage
                        VirusScan.CheckBlob(a.Me.Db, blob, a.Shown + "/" + inner);
                        ArchiveEdits.SetFile(a.Me.Db, s, inner, blob);
                    }
                    catch { if (blob.Id != null) a.Me.Db.Blobs.Delete(blob.Id); throw; }
                    added.Add(inner);
                }
            }
            finally { if (added.Count > 0) ArchiveEdits.Put(a.Me.Db, s); }
            return Results.Ok(new { added });
        }).DisableAntiforgery();

        arc.MapPost("/mkdir", (ArchiveEntryBody body, HttpContext http) =>
        {
            var a = OpenArc(http, body.Path);
            var s = Editable(a);
            ArchiveEdits.Mkdir(s, Archives.Inner(body.Entry));
            ArchiveEdits.Put(a.Me.Db, s);
            return Results.Ok();
        });

        arc.MapPost("/move", (ArchiveMoveBody body, HttpContext http) =>
        {
            var a = OpenArc(http, body.Path);
            var s = Editable(a);
            ArchiveEdits.Move(s, Archives.Inner(body.From), Archives.Inner(body.To));
            ArchiveEdits.Put(a.Me.Db, s);
            return Results.Ok();
        });

        arc.MapPost("/delete", (ArchiveEntriesBody body, HttpContext http) =>
        {
            var a = OpenArc(http, body.Path);
            var s = Editable(a);
            foreach (var e in body.Entries) ArchiveEdits.Remove(a.Me.Db, s, Archives.Inner(e));
            ArchiveEdits.Put(a.Me.Db, s);
            return Results.Ok();
        });

        // Textdatei im Archiv bearbeiten oder neu anlegen
        arc.MapPost("/text", (ArchiveTextBody body, HttpContext http) =>
        {
            var a = OpenArc(http, body.Path);
            var s = Editable(a);
            var inner = Archives.Inner(body.Entry);
            if (body.Create && s.Exists(inner)) return Results.Conflict(new { error = "Gibt es im Archiv schon.", conflict = true });
            var data = Encoding.UTF8.GetBytes(body.Text ?? "");
            if (data.Length > 20 * 1024 * 1024) throw new ArgumentException("Zu groß für den Texteditor.");
            var blob = a.Me.Db.Blobs.Write(data);
            try
            {
                VirusScan.CheckBlob(a.Me.Db, blob, a.Shown + "/" + inner);
                ArchiveEdits.SetFile(a.Me.Db, s, inner, blob);
            }
            catch { if (blob.Id != null) a.Me.Db.Blobs.Delete(blob.Id); throw; }
            ArchiveEdits.Put(a.Me.Db, s);
            var row = new ArcRow(inner, false, blob.Size, 0, DateTime.UtcNow, !s.Files[inner].R, s.Files[inner].R);
            return Results.Ok(new { row.Path, row.Name, row.IsDir, row.Size, row.Packed, row.Modified, row.Added, row.Changed, row.Mime });
        });

        // Speichern: neues ZIP an die Stelle des alten (das alte bleibt als Version) oder als Kopie daneben
        arc.MapPost("/save", async (ArchiveSaveBody body, HttpContext http, CancellationToken ct) =>
        {
            var a = OpenArc(http, body.Path);
            if (a.Edit is not { } s) return Results.Ok(new { path = a.Shown, saved = false });
            if (ReadOnlyReason(a) is { } why) throw new UnauthorizedAccessException(why);
            var loc = a.Loc.Writable();
            // Passwort nach einem Neustart vergessen: erst wieder entsperren
            if (a.Listing.Encrypted && a.Password == null) throw new ArchivePasswordException(false);
            if (a.Stale && !body.Copy)
                return Results.Conflict(new { error = "Das Archiv wurde inzwischen anderweitig geändert. Die Änderungen lassen sich als Kopie speichern.", conflict = true });
            var target = body.Copy
                ? FileStore.FreeName(loc.Db, (FileStore.Parent(loc.Path) is { Length: > 0 } p ? p + "/" : "") + Path.GetFileNameWithoutExtension(a.File.Name) + " (geändert).zip")
                : loc.Path;
            if (body.Copy) (loc with { Path = target }).Creatable();
            try { await ArchiveEdits.SaveAsync(a.Me.Db, loc.Db, s, a.Listing, a.Password, target, ct); }
            catch (Pim.PreconditionFailedException)
            {
                return Results.Conflict(new { error = "Das Archiv wurde inzwischen anderweitig geändert. Die Änderungen lassen sich als Kopie speichern.", conflict = true });
            }
            ArchiveEdits.Drop(a.Me.Db, s.Owner, s.Path, s);
            return Results.Ok(new { path = loc.Show(target), saved = true });
        });

        arc.MapPost("/discard", (ArchivePathBody body, HttpContext http) =>
        {
            var loc = Loc(http, body.Path);
            var db = http.Space().Db;
            ArchiveEdits.Drop(db, loc.Db.UserId, loc.Path, ArchiveEdits.Get(db, loc.Db.UserId, loc.Path));
            return Results.Ok();
        });

        // Offene Änderungen in allen Archiven (Hinweis in der Dateiablage, damit nichts vergessen wird)
        arc.MapGet("/pending", (HttpContext http) => Results.Ok(new
        {
            items = ArchiveEdits.All(http.Space().Db).Select(s => new
            {
                path = s.Shown, s.Changes, s.Touched, expires = Min(s.Touched + ArchiveEdits.Idle, s.Started + ArchiveEdits.MaxAge),
            }),
        }));

        // Entpacken in die Ablage: markierte Einträge (Ordner samt Inhalt) oder alles im aktuellen Ordner des Archivs.
        // Vorhandenes wird nicht überschrieben (Nummer dran). Jede Datei geht durch PutAsync: Kontingent und Virenprüfung wie beim Hochladen.
        arc.MapPost("/extract", async (ArchiveExtractBody body, HttpContext http, CancellationToken ct) =>
        {
            var a = OpenArc(http, body.Path);
            a.Loc.NotShowOnly("entpacken");
            if (a.Listing.Bomb) throw new ArchiveException("Das Archiv wird nicht ausgepackt: Die angegebenen Größen sehen nach einer ZIP-Bombe aus.");
            var dir = Archives.Inner(body.Dir);
            var picked = (body.Entries is { Length: > 0 } es ? es.Select(Archives.Inner) : [dir]).ToList();
            bool Wanted(string p) => picked.Any(x => x.Length == 0 || p == x || p.StartsWith(x + "/", StringComparison.Ordinal));
            string Rel(string p) => dir.Length == 0 ? p : p.StartsWith(dir + "/", StringComparison.Ordinal) ? p[(dir.Length + 1)..] : p[(p.LastIndexOf('/') + 1)..];
            var target = Loc(http, body.Target).Creatable();
            if (target.Path.Length > 0) FileStore.EnsureDir(target.Db, target.Path);
            // Was entsteht: (Pfad im Archiv, Größe); Ordner auch leer
            var files = a.Edit is { } s ? s.Files.Where(x => Wanted(x.Key)).Select(x => (Path: x.Key, Size: x.Value.S)).ToList()
                : a.Listing.Entries.Where(e => !e.IsDir && Wanted(e.Path)).Select(e => (e.Path, e.Size)).ToList();
            var dirs = a.Edit is { } s2 ? s2.Dirs.Where(Wanted).ToList() : a.Listing.Entries.Where(e => e.IsDir && Wanted(e.Path)).Select(e => e.Path).ToList();
            if (files.Sum(f => f.Size) > Archives.MaxTotal) throw new ArchiveException("Zu viel auf einmal zum Entpacken.");
            int done = 0;
            var failed = new List<object>();
            string Dest(string rel) => target.Path.Length == 0 ? rel : target.Path + "/" + rel;
            foreach (var d in dirs.Select(Rel).Where(r => r.Length > 0).Distinct()) FileStore.EnsureDir(target.Db, Dest(d));
            async Task Store(string p, Stream data)
            {
                var dest = Dest(Rel(p));
                try
                {
                    if (FileStore.Parent(dest) is { Length: > 0 } parent) FileStore.EnsureDir(target.Db, parent);
                    if (FileStore.Get(target.Db, dest) != null) dest = FileStore.FreeName(target.Db, dest);
                    await FileStore.PutAsync(target.Db, dest, data, DavServer.Mime(dest), ct: ct);
                    done++;
                }
                // Einzelne Datei abgelehnt (Virus, kaputt, Name belegt): weiter mit den anderen; voll oder falsches Passwort: Schluss
                catch (Exception ex) when (ex is VirusFoundException or ArchiveException or DavConflictException or ArgumentException)
                {
                    failed.Add(new { entry = p, error = ex.Message });
                }
            }
            var wanted = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            if (a.Edit is { } ed)
            {
                // Hinzugefügte aus dem Blob, die übrigen in einem Durchgang aus dem alten Archiv (unter ihrem neuen Namen)
                var fromOld = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in wanted)
                {
                    var item = ed.Files[p];
                    if (item.B is { } blob) { await using var data = a.Me.Db.Blobs.OpenRead(blob); await Store(p, data); }
                    else fromOld[item.E!] = p;
                }
                if (fromOld.Count > 0)
                    await Archives.ForEach(a.Loc.Db, a.Loc.Path, a.Listing, a.Password, fromOld.ContainsKey, (e, data) => Store(fromOld[e.Path], data), ct);
            }
            else await Archives.ForEach(a.Loc.Db, a.Loc.Path, a.Listing, a.Password, wanted.Contains, (e, data) => Store(e.Path, data), ct);
            return Results.Ok(new { extracted = done, failed, target = target.Show(target.Path) });
        });
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
