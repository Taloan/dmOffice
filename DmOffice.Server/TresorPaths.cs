using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice;

/// <summary>
/// Der Tresor in der Dateiansicht (docs/tresor-cryptomator-konzept.md, Stufe 3a): Pfade „tresor:“ bzw. „tresor:/Ordner/Datei“
/// über dieselben Wege wie die Dateiablage (/api/files: Liste, Details, Herunterladen, Vorschaubild, Hochladen, Ordner,
/// Löschen, Verschieben und Kopieren auch zwischen Tresor und „Meine Dateien“, ZIP). Nur bei offenem Tresor auf diesem
/// Gerät (sonst 423). Was es im Tresor nicht gibt (Teilen, Favoriten, Versionen, Papierkorb, Suche), bleibt außen vor.
/// </summary>
public static class TresorPaths
{
    public const string Prefix = "tresor:";

    public static bool Is(string? path) => path != null && path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Pfad im Tresor ohne Vorsatz, ohne Schrägstriche am Rand („Ordner/Datei“, Wurzel "").</summary>
    public static string Rel(string path)
    {
        var rel = path[Prefix.Length..].Trim('/');
        if (rel.Length > 0 && rel.Split('/').Any(p => p is "" or "." or "..")) throw new ArgumentException("Ungültiger Pfad.");
        return rel;
    }

    public static string Show(string rel) => rel.Length == 0 ? Prefix : Prefix + "/" + rel;

    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;
    private static string? ParentOf(string rel) => rel.Length == 0 ? null : rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
    private static string NameOf(string rel) => rel[(rel.LastIndexOf('/') + 1)..];

    /// <summary>Kennung zum Pfad (0 = Wurzel), null, wenn es ihn nicht gibt.</summary>
    public static long? IdOf(TresorFileStore files, string rel)
    {
        long id = 0;
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = files.Children(id).FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            id = next.Id;
        }
        return id;
    }

    /// <summary>Beim Abruf über den Pfad: Noch nicht Eingetragenes (von außen gekommen, nach einer Wiederherstellung) abgleichen wie beim Öffnen des Ordners.</summary>
    private static long? SyncedIdOf(TresorFileStore files, string rel)
    {
        if (IdOf(files, rel) is { } id) return id;
        if (ParentOf(rel) is { } parent) files.ReconciledFolder(parent);
        return IdOf(files, rel);
    }

    /// <summary>Kennung einer Tresor-Datei aus „tresor:&lt;Id&gt;“ (Bildbearbeitung) oder „tresor:/Pfad“ (Dateiansicht); sonst null.</summary>
    public static long? IdOfAny(HttpContext http, string? path)
    {
        if (path == null || !Is(path)) return null;
        if (TresorFiles.IdOfPath(path) is { } id) return id;
        var rel = Rel(path);
        return rel.Length == 0 ? null : SyncedIdOf(Files(http), rel) is { } found and > 0 ? found : throw new KeyNotFoundException("Diese Datei gibt es im Tresor nicht.");
    }

    private static TresorFiles.Entry EntryAt(TresorFileStore files, string rel) =>
        SyncedIdOf(files, rel) is { } id and > 0 && files.Find(id) is { } e ? e : throw new KeyNotFoundException("Nicht gefunden.");

    private static FileEntry ToFileEntry(TresorFiles.Entry e, string rel)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(e.Modified).UtcDateTime;
        return new FileEntry(Show(rel), e.IsDir, e.Size, e.Modified.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Mime, at, at, e.Id);
    }

    private static object Item(TresorFiles.Entry e, string rel) => FilesApi.Item(ToFileEntry(e, rel));

    private static TresorFileStore Files(HttpContext http) => Tresor.Db(http).Files;

    // ---------------- Lesen ----------------

    public static object List(HttpContext http, string path)
    {
        var files = Files(http);
        var rel = Rel(path);
        // Was von außen in den Cryptomator-Tresor kam (Cryptomator über Nextcloud, WebDAV), erscheint beim Öffnen des Ordners
        var id = files.ReconciledFolder(rel) ?? (IdOf(files, rel) is { } other && files.Find(other) is { IsDir: false }
            ? throw new ArgumentException("Kein Ordner.") : throw new KeyNotFoundException("Ordner nicht gefunden."));
        var db = http.Space().Db;
        return new
        {
            path = Show(rel),
            usage = FileStore.Usage(db),
            trash = FileStore.TrashUsage(db),
            versions = FileStore.VersionUsage(db),
            quota = FileStore.Quota(db),
            used = UserStorage.Used(db),
            favorite = false,
            canWrite = true,
            owner = (string?)null,
            mount = (string?)null,
            showOnly = false,
            tresor = true,
            items = files.Children(id).Select(c => Item(c, Join(rel, c.Name))),
        };
    }

    public static object Info(HttpContext http, string path)
    {
        var files = Files(http);
        var rel = Rel(path);
        var id = SyncedIdOf(files, rel) ?? throw new KeyNotFoundException("Nicht gefunden.");
        var e = id == 0 ? new TresorFiles.Entry(0, 0, "Tresor", true, 0, null, null, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : files.Find(id)!;
        long size = e.IsDir ? 0 : e.Size;
        int nFiles = 0, nFolders = 0;
        void Walk(long dir)
        {
            foreach (var c in files.Children(dir))
                if (c.IsDir) { nFolders++; Walk(c.Id); } else { nFiles++; size += c.Size; }
        }
        if (e.IsDir) Walk(id);
        return new
        {
            item = Item(e, rel), sha256 = (string?)null, size, files = nFiles, folders = nFolders,
            versions = Array.Empty<object>(), links = Array.Empty<object>(), people = Array.Empty<object>(),
            canWrite = true, showOnly = false, owner = (string?)null, tresor = true,
        };
    }

    public static IResult Download(HttpContext http, string path, bool inline)
    {
        var files = Files(http);
        var e = files.Find(IdOfAny(http, path) ?? 0) ?? throw new KeyNotFoundException("Nicht gefunden.");
        if (e.IsDir) throw new ArgumentException("Ordner lassen sich nicht herunterladen.");
        http.Response.Headers.CacheControl = "no-store";
        var (mime, download) = FilesApi.Shown(http, e.Mime, e.Name, inline);
        return Results.Stream(files.OpenRead(e.Id), mime, download, enableRangeProcessing: true);
    }

    public static IResult Thumb(HttpContext http, string path, int? size)
    {
        var files = Files(http);
        var e = files.Find(IdOfAny(http, path) ?? 0) ?? throw new KeyNotFoundException("Nicht gefunden.");
        if (e.IsDir) return Results.NotFound();
        http.Response.Headers.CacheControl = "private, no-store";
        return TresorFiles.ThumbOf(files, e, size) is { } webp ? Results.Bytes(webp, "image/webp") : Results.NotFound();
    }

    /// <summary>Größe einer Datei oder eines Ordners samt Inhalt.</summary>
    public static long SizeOf(HttpContext http, string path)
    {
        var files = Files(http);
        var id = IdOf(files, Rel(path)) ?? 0;
        long Walk(long dir) => files.Children(dir).Sum(c => c.IsDir ? Walk(c.Id) : c.Size);
        return id == 0 ? Walk(0) : files.Find(id) is { } e ? (e.IsDir ? Walk(e.Id) : e.Size) : 0;
    }

    public static bool Exists(HttpContext http, string path)
    {
        var rel = Rel(path);
        return rel.Length == 0 || IdOf(Files(http), rel) != null;
    }

    // ---------------- Ändern ----------------

    public static IResult Mkdir(HttpContext http, string path)
    {
        var files = Files(http);
        var rel = Rel(path);
        var parent = IdOf(files, ParentOf(rel) ?? throw new ArgumentException("Die Wurzel gibt es schon.")) ?? throw new KeyNotFoundException("Ordner nicht gefunden.");
        if (files.Children(parent).Any(c => string.Equals(c.Name, NameOf(rel), StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new { error = "Diesen Namen gibt es dort schon." });
        var (_, name) = files.CreateFolder(parent, NameOf(rel));
        return Results.Ok(new { path = Show(Join(ParentOf(rel)!, name)) });
    }

    public static void Delete(HttpContext http, IEnumerable<string> paths)
    {
        var files = Files(http);
        foreach (var p in paths)
        {
            var rel = Rel(p);
            if (rel.Length == 0) throw new ArgumentException("Der Tresor selbst lässt sich hier nicht löschen.");
            if (IdOf(files, rel) is { } id and > 0) files.Delete(id);
        }
        UserStorage.Refresh(http.Space().Db);
    }

    /// <summary>
    /// Hochladen in einen Ordner des Tresors. Namen mit „/“ (Ordner hochladen) legen die Unterordner an. conflict: replace
    /// (Vorgabe), rename, skip, fail - wie in der Dateiablage.
    /// </summary>
    public static async Task<IResult> Upload(HttpContext http, string path, string? conflict, HttpRequest request, CancellationToken ct)
    {
        var written = new List<object>();
        await foreach (var file in MultipartUpload.Files(request, ct))
        {
            var (entry, error) = await StoreOne(http, path, file.FileName, file.Body, file.ContentType, conflict, ct);
            if (error != null) return error;
            if (entry != null) written.Add(entry);
        }
        UserStorage.Refresh(http.Space().Db);
        return Results.Ok(new { files = written });
    }

    /// <summary>Eine Datei in einen Ordner des Tresors (Hochladen, Abschluss der Teil-Uploads). Eintrag null: übersprungen.</summary>
    public static async Task<(object? Entry, IResult? Error)> StoreOne(HttpContext http, string dirPath, string fileName, Stream body, string? contentType,
        string? conflict, CancellationToken ct)
    {
        var files = Files(http);
        var space = http.Space();
        var dirRel = Rel(dirPath);
        var name = fileName.Replace('\\', '/').Trim('/');
        var sub = ParentOf(name) ?? "";
        var parent = files.EnsureFolder(Join(dirRel, sub));
        var leaf = TresorFiles.CleanName(NameOf(name));
        var existing = files.Children(parent).FirstOrDefault(c => string.Equals(c.Name, leaf, StringComparison.OrdinalIgnoreCase));
        var quota = FileStore.Quota(space.Db);
        var left = quota > 0 ? Math.Max(0, quota - UserStorage.Counted(space.Db)) : long.MaxValue;
        var mime = contentType is { Length: > 0 } c && !c.StartsWith("application/octet-stream") ? c : DavServer.Mime(leaf);
        TresorFiles.Entry result;
        if (existing is { IsDir: false } && conflict is null or "replace")
        {
            await files.ReplaceAsync(existing.Id, body, mime, null, ct);
            result = files.Find(existing.Id)!;
        }
        else if (existing != null && conflict == "skip") return (null, null);
        else if (existing != null && conflict == "fail")
            return (null, Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = Show(Join(Join(dirRel, sub), leaf)) }));
        else
        {
            var (id, _) = await files.CreateAsync(parent, leaf, body, mime, ct, left, () => new QuotaExceededException(quota));
            result = files.Find(id)!;
        }
        return (Item(result, files.PathOf(result.Id)!), null);
    }

    /// <summary>
    /// Verschieben oder kopieren mit mindestens einer Seite im Tresor. In den Tresor verschoben: das Original wird erst entfernt,
    /// wenn alles im Tresor liegt, und dann endgültig (nicht in den Papierkorb - dort läge es unverschlüsselt).
    /// </summary>
    public static async Task<IResult> Move(HttpContext http, string from, string to, bool copy, string? conflict, CancellationToken ct)
    {
        var fromTresor = Is(from);
        var toTresor = Is(to);
        var files = Files(http);
        var space = http.Space();

        // Ziel klären: Ordner, in den es kommt, und Name
        string toDir, toName;
        if (toTresor)
        {
            var rel = Rel(to);
            toDir = ParentOf(rel) ?? throw new ArgumentException("Ungültiges Ziel.");
            toName = TresorFiles.CleanName(NameOf(rel));
        }
        else
        {
            var target = FilesApi.Loc(http, to).Creatable();
            toDir = FileStore.Parent(target.Path) ?? throw new ArgumentException("Ungültiges Ziel.");
            toName = target.Path[(target.Path.LastIndexOf('/') + 1)..];
        }

        // Ziel schon belegt?
        bool exists = toTresor ? IdOf(files, Join(toDir, toName)) != null : FileStore.Get(FilesApi.Loc(http, to).Db, FilesApi.Loc(http, to).Path) != null;
        if (exists)
        {
            switch (conflict)
            {
                case "skip": return Results.Ok(new { path = to, skipped = true });
                case "rename":
                    if (toTresor) toName = files.FreeName(IdOf(files, toDir) ?? 0, toName);
                    else { var l = FilesApi.Loc(http, to); var free = FileStore.FreeName(l.Db, l.Path); toName = free[(free.LastIndexOf('/') + 1)..]; }
                    break;
                case "replace":
                    if (toTresor) files.Delete(IdOf(files, Join(toDir, toName))!.Value);
                    else { var l = FilesApi.Loc(http, to).Changeable(); FileStore.Delete(l.Db, l.Path); }
                    break;
                default: return Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = to });
            }
        }

        // Innerhalb des Tresors verschieben: nur das Verzeichnis (und der Speicher) ändert sich
        if (fromTresor && toTresor && !copy)
        {
            var e = EntryAt(files, Rel(from));
            var parent = IdOf(files, toDir) ?? throw new KeyNotFoundException("Zielordner nicht gefunden.");
            var name = files.Move(e.Id, parent, toName);
            return Results.Ok(new { path = Show(Join(toDir, name)), skipped = false, replaced = (long?)null, copied = false });
        }

        // Sonst Inhalt für Inhalt kopieren, danach (beim Verschieben) die Quelle entfernen
        string result;
        if (fromTresor)
        {
            var e = EntryAt(files, Rel(from));
            result = toTresor ? Show(await CopyWithin(files, e, toDir, toName, ct)) : await CopyOut(http, files, e, toDir, toName, ct);
            if (!copy) files.Delete(e.Id);
        }
        else
        {
            var src = FilesApi.Loc(http, from);
            if (copy) src.NotShowOnly("kopieren"); else src.Changeable();
            var entry = FileStore.Get(src.Db, src.Path) ?? throw new KeyNotFoundException("Nicht gefunden.");
            result = Show(await CopyIn(files, src.Db, entry, toDir, toName, ct));
            if (!copy)
            {
                // Endgültig, nicht in den Papierkorb: dort läge das Original unverschlüsselt
                if (FileStore.Delete(src.Db, src.Path, out var trash) && trash > 0) FileStore.PurgeTrash(src.Db, [trash]);
            }
        }
        UserStorage.Refresh(space.Db);
        return Results.Ok(new { path = result, skipped = false, replaced = (long?)null, copied = false });
    }

    private static async Task<string> CopyWithin(TresorFileStore files, TresorFiles.Entry e, string toDir, string toName, CancellationToken ct)
    {
        var parent = files.EnsureFolder(toDir);
        if (e.IsDir)
        {
            var (_, name) = files.CreateFolder(parent, toName);
            var at = Join(toDir, name);
            foreach (var c in files.Children(e.Id)) await CopyWithin(files, c, at, c.Name, ct);
            return at;
        }
        await using var s = files.OpenRead(e.Id);
        var (_, n) = await files.CreateAsync(parent, toName, s, e.Mime, ct);
        return Join(toDir, n);
    }

    private static async Task<string> CopyIn(TresorFileStore files, UserDb db, FileEntry e, string toDir, string toName, CancellationToken ct)
    {
        var parent = files.EnsureFolder(toDir);
        if (e.IsDir)
        {
            var (_, name) = files.CreateFolder(parent, toName);
            var at = Join(toDir, name);
            foreach (var c in FileStore.Children(db, e.Path)) await CopyIn(files, db, c, at, c.Path[(c.Path.LastIndexOf('/') + 1)..], ct);
            return at;
        }
        await using var s = FileStore.OpenRead(db, e.Path);
        var (_, n) = await files.CreateAsync(parent, toName, s, e.Mime, ct);
        return Join(toDir, n);
    }

    /// <summary>Aus dem Tresor in die Dateiablage (bewusstes Herausnehmen, die Oberfläche fragt vorher).</summary>
    private static async Task<string> CopyOut(HttpContext http, TresorFileStore files, TresorFiles.Entry e, string toDir, string toName, CancellationToken ct)
    {
        var target = FilesApi.Loc(http, toDir.Length == 0 ? toName : toDir + "/" + toName).Creatable();
        if (e.IsDir)
        {
            FileStore.Mkcol(target.Db, target.Path);
            foreach (var c in files.Children(e.Id)) await CopyOut(http, files, c, target.Path, c.Name, ct);
        }
        else
        {
            await using var s = files.OpenRead(e.Id);
            await FileStore.PutAsync(target.Db, target.Path, s, e.Mime, ct: ct);
        }
        return target.Show(target.Path);
    }

    /// <summary>Mehrere Einträge als ZIP (Inhalte entschlüsselt) - gestreamt wie <see cref="Zip.Write"/>.</summary>
    public static async Task Zip(HttpContext http, IReadOnlyList<string> paths, string fileName)
    {
        var files = Files(http);
        var roots = paths.Select(p => EntryAt(files, Rel(p))).ToList();
        if (roots.Count == 0) throw new KeyNotFoundException("Nichts zum Herunterladen.");
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.CacheControl = "no-store";
        if (http.Features.Get<IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        await http.Response.StartAsync();
        await using var buffered = new BufferedStream(http.Response.Body, 256 * 1024);
        using (var zip = new ZipArchive(buffered, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(TresorFiles.Entry e, string name)
            {
                http.RequestAborted.ThrowIfCancellationRequested();
                if (e.IsDir)
                {
                    zip.CreateEntry(name + "/");
                    foreach (var c in files.Children(e.Id)) Add(c, name + "/" + c.Name);
                    return;
                }
                var level = DmOffice.Dav.Zip.Packed.Contains(Path.GetExtension(e.Name).ToLowerInvariant()) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                var entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = DateTimeOffset.FromUnixTimeMilliseconds(e.Modified);
                using var target = entry.Open();
                using var s = files.OpenRead(e.Id);
                s.CopyTo(target);
            }
            foreach (var r in roots) Add(r, r.Name);
        }
        await buffered.FlushAsync();
    }
}
