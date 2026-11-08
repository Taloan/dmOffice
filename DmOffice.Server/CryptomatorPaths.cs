using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using DmOffice.Core;
using DmOffice.Core.Cryptomator;
using DmOffice.Dav;

namespace DmOffice;

/// <summary>
/// Eigene Cryptomator-Tresore in der Dateiablage öffnen (docs/tresor-cryptomator-konzept.md, Stufe 3b): ein Ordner mit
/// vault.cryptomator. Entsperrt mit dem Passwort, der Masterkey liegt nur im Arbeitsspeicher - je Person und Zugang (App-Gerät
/// bzw. Browser), bis „Sperren“, Abmelden oder <see cref="Idle"/> ohne Nutzung. Kein Übernehmen (Entscheidung 8): die Dateien
/// bleiben, wo und wie sie sind, Cryptomator-Apps können sie weiter öffnen. Pfade: „cmv:/Ordner/Tresor/Unterordner/Datei“.
/// </summary>
public static class CryptomatorPaths
{
    public const string Prefix = "cmv:";
    public static TimeSpan Idle { get; set; } = TimeSpan.FromMinutes(30);

    public static bool Is(string? path) => path != null && path.StartsWith(Prefix, StringComparison.Ordinal);

    private sealed class Open
    {
        public required string User;
        public required string Root;
        public required CryptomatorVault Vault;
        public DateTime Used;
        /// <summary>Treiber des externen Ordners (Tresor dort), wird mit dem Tresor geschlossen.</summary>
        public IDisposable? Owned;
        /// <summary>Externer Ordner nur zum Lesen eingebunden: auch im Tresor darin nichts schreiben.</summary>
        public bool ReadOnly;
        public void Close() { Vault.Dispose(); Owned?.Dispose(); }
    }

    /// <summary>Tresor in einem externen Ordner: Wurzel „x:Kennung/Pfad“ (sonst ein Pfad der eigenen Ablage).</summary>
    public const string ExtPrefix = "x:";
    public static string ExtRoot(string mount, string path) => ExtPrefix + mount + (path.Length == 0 ? "" : "/" + path);
    private static (string Mount, string Path)? ParseExt(string root)
    {
        if (!root.StartsWith(ExtPrefix, StringComparison.Ordinal)) return null;
        var rest = root[ExtPrefix.Length..];
        var i = rest.IndexOf('/');
        return i < 0 ? (rest, "") : (rest[..i], rest[(i + 1)..]);
    }
    private static string Norm(string root) => ParseExt(root) is { } x ? ExtRoot(x.Mount, Storage.StoragePath.Clean(x.Path)) : FileStore.Normalize(root);

    private static readonly ConcurrentDictionary<string, Open> Unlocked = new();

    /// <summary>Zugang: App-Gerät oder dieser Browser (Kennung im Cookie wie beim Tresor) - nicht alle Browser der Person.</summary>
    private static string Access(HttpContext http, bool create = false)
    {
        var user = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
        if (Auth.DeviceOf(http) is { } d) return user + "|a:" + d;
        return user + "|c:" + (Tresor.BrowserToken(http, create) ?? "-");
    }

    private static string Key(HttpContext http, string root) => Access(http) + "|" + root;

    private static void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var (k, o) in Unlocked.ToArray())
            if (now - o.Used > Idle && Unlocked.TryRemove(k, out var gone)) gone.Close();
    }

    /// <summary>Ist dieser Ordner der eigenen Ablage ein Cryptomator-Tresor (vault.cryptomator und d/ darin)?</summary>
    public static bool IsVault(UserDb db, string path) =>
        FileStore.Get(db, Join(path, VaultConfig.FileName)) is { IsDir: false } && FileStore.Get(db, Join(path, VaultNames.DataDir)) is { IsDir: true };

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;

    /// <summary>Gerade für diesen Zugang entsperrt?</summary>
    public static bool IsUnlocked(HttpContext http, string root) { Sweep(); return Unlocked.ContainsKey(Key(http, root)); }

    /// <summary>Liegt in diesem Ordner (Einträge eines externen Ordners) ein Cryptomator-Tresor?</summary>
    public static bool LooksLikeVault(IEnumerable<(string Name, bool IsDir)> entries) =>
        entries.Any(e => !e.IsDir && e.Name == VaultConfig.FileName) && entries.Any(e => e.IsDir && e.Name == VaultNames.DataDir);

    public static IResult Unlock(HttpContext http, string root, string password)
    {
        var db = http.Space().Db;
        root = Norm(root);
        // Der System-Tresor hat seinen eigenen Weg mit Geräteliste und „Auf allen Geräten schließen“ (/api/tresor/open)
        if (FileStore.InRoot(root, FileStore.SystemVault) || FileStore.InRoot(root, FileStore.OldSystemVault))
            return Results.BadRequest(new { error = "Den Tresor öffnest du über „Tresor“ in den Dateien." });
        var failKey = "cmv:" + http.Space().User.Id;
        if (Auth.Blocked(failKey)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
        IVaultFiles files;
        IDisposable? owned = null;
        var readOnly = false;
        if (ParseExt(root) is { } ext)
        {
            // Externer Ordner: über den Treiber des Ziels; ohne vault.cryptomator und d/ kein Tresor
            var (driver, ro) = Storage.ExternalFolders.Open(http.Space(), http.RequestServices.GetRequiredService<ServerConfig>(), ext.Mount);
            readOnly = ro;
            var df = new Storage.DriverVaultFiles(driver, ext.Path);
            if (!df.FileExists(VaultConfig.FileName) || !df.DirExists(VaultNames.DataDir)) { df.Dispose(); throw new KeyNotFoundException("Dort liegt kein Cryptomator-Tresor."); }
            files = df; owned = df;
        }
        else
        {
            if (!IsVault(db, root)) throw new KeyNotFoundException("Dort liegt kein Cryptomator-Tresor.");
            files = new FileStoreVaultFiles(db, root);
        }
        var kid = VaultConfig.KidOf(System.Text.Encoding.ASCII.GetString(files.ReadAll(VaultConfig.FileName)));
        if (kid != VaultConfig.MasterkeyKid)
        {
            owned?.Dispose();
            return Results.BadRequest(new { error = "Dieser Tresor wird über Cryptomator Hub geöffnet - das geht in dmOffice nicht." });
        }
        CryptomatorVault? vault;
        try { vault = CryptomatorVault.Open(files, password); }
        catch (FormatException ex) { owned?.Dispose(); return Results.BadRequest(new { error = ex.Message }); }
        if (vault == null)
        {
            owned?.Dispose();
            Auth.Failed(failKey, "Passwort eines Cryptomator-Tresors");
            return Results.BadRequest(new { error = "Das Passwort stimmt nicht." });
        }
        Auth.Succeeded(failKey);
        Sweep();
        var key = Access(http, create: true) + "|" + root;
        if (Unlocked.TryRemove(key, out var old)) old.Close();
        Unlocked[key] = new Open { User = Access(http), Root = root, Vault = vault, Used = DateTime.UtcNow, Owned = owned, ReadOnly = readOnly };
        return Results.Ok(new { path = Show(root, "") });
    }

    public static void Lock(HttpContext http, string root)
    {
        if (Unlocked.TryRemove(Key(http, Norm(root)), out var o)) o.Close();
    }

    /// <summary>Alle entsperrten Tresore dieses Zugangs sperren (Abmelden in diesem Browser).</summary>
    public static void LockAccess(HttpContext http)
    {
        if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) is null) return;
        var prefix = Access(http) + "|";
        foreach (var (k, o) in Unlocked.ToArray())
            if (k.StartsWith(prefix, StringComparison.Ordinal) && Unlocked.TryRemove(k, out var gone)) gone.Close();
    }

    /// <summary>Schreiben: nicht in einem Tresor, dessen externer Ordner nur zum Lesen eingebunden ist.</summary>
    private static (Open Open, string Inner) Writable(HttpContext http, string path)
    {
        var r = Resolve(http, path);
        if (r.Open.ReadOnly) throw new UnauthorizedAccessException("Der Ordner ist nur zum Lesen eingebunden.");
        return r;
    }

    /// <summary>Alle entsperrten Tresore einer Person sperren (Abmelden, Konto gesperrt).</summary>
    public static void LockAll(string userId)
    {
        foreach (var (k, o) in Unlocked.ToArray())
            if (k.StartsWith(userId + "|", StringComparison.Ordinal) && Unlocked.TryRemove(k, out var gone)) gone.Close();
    }

    public static string Show(string root, string inner) => Prefix + "/" + root + (inner.Length == 0 ? "" : "/" + inner);

    /// <summary>Entsperrter Tresor zum Pfad und der Pfad darin ("" = Wurzel des Tresors). Gesperrt: 423.</summary>
    private static (Open Open, string Inner) Resolve(HttpContext http, string path)
    {
        Sweep();
        var rel = path[Prefix.Length..].Trim('/');
        if (rel.Split('/').Any(p => p is "" or "." or "..")) throw new ArgumentException("Ungültiger Pfad.");
        var access = Access(http) + "|";
        // Längster passender Tresor (ein Tresor kann in einem Ordner eines anderen liegen - dann gilt der innere)
        var hit = Unlocked.Where(x => x.Key.StartsWith(access, StringComparison.Ordinal) && (rel == x.Value.Root || rel.StartsWith(x.Value.Root + "/", StringComparison.Ordinal)))
            .OrderByDescending(x => x.Value.Root.Length).Select(x => x.Value).FirstOrDefault()
            ?? throw new VaultLockedException();
        hit.Used = DateTime.UtcNow;
        return (hit, rel == hit.Root ? "" : rel[(hit.Root.Length + 1)..]);
    }

    private static string Abs(string inner) => "/" + inner;

    private static object Item(Open o, string inner, CryptomatorVault.Entry e)
    {
        var at = e.Modified;
        var path = Show(o.Root, inner);
        return FilesApi.Item(new FileEntry(path, e.IsDir, e.Size, at.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            e.IsDir ? null : DavServer.Mime(e.Name), at, at));
    }

    private static CryptomatorVault.Entry EntryAt(Open o, string inner)
    {
        if (inner.Length == 0) return new CryptomatorVault.Entry(o.Root[(o.Root.LastIndexOf('/') + 1)..], true, 0, DateTime.UtcNow);
        var parent = inner.Contains('/') ? inner[..inner.LastIndexOf('/')] : "";
        var name = inner[(inner.LastIndexOf('/') + 1)..];
        return o.Vault.List(Abs(parent)).FirstOrDefault(e => e.Name == name) ?? throw new KeyNotFoundException("Nicht gefunden.");
    }

    // ---------------- Lesen ----------------

    public static object List(HttpContext http, string path)
    {
        var (o, inner) = Resolve(http, path);
        var db = http.Space().Db;
        return new
        {
            path = Show(o.Root, inner),
            usage = FileStore.Usage(db), trash = FileStore.TrashUsage(db), versions = FileStore.VersionUsage(db), quota = FileStore.Quota(db), used = UserStorage.Used(db),
            favorite = false, canWrite = true, owner = (string?)null, mount = (string?)null, showOnly = false,
            vaultRoot = o.Root,
            items = o.Vault.List(Abs(inner)).Select(e => Item(o, Join(inner, e.Name), e)),
        };
    }

    public static object Info(HttpContext http, string path)
    {
        var (o, inner) = Resolve(http, path);
        var e = EntryAt(o, inner);
        long size = e.IsDir ? 0 : e.Size;
        int nFiles = 0, nFolders = 0;
        void Walk(string dir)
        {
            foreach (var c in o.Vault.List(Abs(dir)))
                if (c.IsDir) { nFolders++; Walk(Join(dir, c.Name)); } else { nFiles++; size += c.Size; }
        }
        if (e.IsDir) Walk(inner);
        return new
        {
            item = Item(o, inner, e), sha256 = (string?)null, size, files = nFiles, folders = nFolders,
            versions = Array.Empty<object>(), links = Array.Empty<object>(), people = Array.Empty<object>(),
            canWrite = true, showOnly = false, owner = (string?)null, vaultRoot = o.Root,
        };
    }

    public static IResult Download(HttpContext http, string path, bool inline)
    {
        var (o, inner) = Resolve(http, path);
        var e = EntryAt(o, inner);
        if (e.IsDir) throw new ArgumentException("Ordner lassen sich nicht herunterladen.");
        http.Response.Headers.CacheControl = "no-store";
        var (mime, download) = FilesApi.Shown(http, null, e.Name, inline);
        return Results.Stream(o.Vault.OpenRead(Abs(inner)), mime, download, enableRangeProcessing: true);
    }

    public static IResult Thumb(HttpContext http, string path, int? size)
    {
        var (o, inner) = Resolve(http, path);
        var e = EntryAt(o, inner);
        if (e.IsDir || Thumbs.KindOf(e.Name, DavServer.Mime(e.Name)) == Thumbs.Kind.None) return Results.NotFound();
        using var s = o.Vault.OpenRead(Abs(inner));
        using var m = new MemoryStream();
        s.CopyTo(m);
        var px = Thumbs.Snap(size ?? Thumbs.Small);
        var webp = Thumbs.IsRaw(e.Name) ? Thumbs.FromRaw(m.ToArray(), px) : Thumbs.FromBytes(m.ToArray(), px);
        http.Response.Headers.CacheControl = "private, no-store";
        return webp == null ? Results.NotFound() : Results.Bytes(webp, "image/webp");
    }

    public static long SizeOf(HttpContext http, string path)
    {
        var (o, inner) = Resolve(http, path);
        var e = EntryAt(o, inner);
        long Walk(string dir) => o.Vault.List(Abs(dir)).Sum(c => c.IsDir ? Walk(Join(dir, c.Name)) : c.Size);
        return e.IsDir ? Walk(inner) : e.Size;
    }

    public static bool Exists(HttpContext http, string path)
    {
        try { var (o, inner) = Resolve(http, path); EntryAt(o, inner); return true; }
        catch (KeyNotFoundException) { return false; }
    }

    // ---------------- Ändern ----------------

    public static IResult Mkdir(HttpContext http, string path)
    {
        var (o, inner) = Writable(http, path);
        if (inner.Length == 0) return Results.Conflict(new { error = "Diesen Namen gibt es dort schon." });
        try { o.Vault.CreateDirectory(Abs(inner)); }
        catch (IOException) { return Results.Conflict(new { error = "Diesen Namen gibt es dort schon." }); }
        return Results.Ok(new { path = Show(o.Root, inner) });
    }

    public static void Delete(HttpContext http, IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var (o, inner) = Writable(http, p);
            if (inner.Length == 0) throw new ArgumentException("Den Tresor selbst bitte in „Meine Dateien“ löschen.");
            o.Vault.Delete(Abs(inner));
        }
        UserStorage.Refresh(http.Space().Db);
    }

    public static async Task<(object? Entry, IResult? Error)> StoreOne(HttpContext http, string dirPath, string fileName, Stream body, string? conflict, CancellationToken ct)
    {
        var (o, dir) = Writable(http, dirPath);
        var name = fileName.Replace('\\', '/').Trim('/');
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var sub in parts[..^1])
        {
            dir = Join(dir, TresorFiles.CleanName(sub));
            if (o.Vault.DirIdOf(Abs(dir)) == null) o.Vault.CreateDirectory(Abs(dir));
        }
        var leaf = TresorFiles.CleanName(parts[^1]);
        var exists = o.Vault.List(Abs(dir)).Any(e => e.Name == leaf);
        if (exists && conflict == "skip") return (null, null);
        if (exists && conflict == "fail") return (null, Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = Show(o.Root, Join(dir, leaf)) }));
        if (exists && conflict == "rename") leaf = FreeName(o, dir, leaf);
        await o.Vault.WriteAsync(Abs(Join(dir, leaf)), body, ct);
        return (Item(o, Join(dir, leaf), EntryAt(o, Join(dir, leaf))), null);
    }

    public static async Task<IResult> Upload(HttpContext http, string path, string? conflict, HttpRequest request, CancellationToken ct)
    {
        var written = new List<object>();
        await foreach (var file in MultipartUpload.Files(request, ct))
        {
            var (entry, error) = await StoreOne(http, path, file.FileName, file.Body, conflict, ct);
            if (error != null) return error;
            if (entry != null) written.Add(entry);
        }
        UserStorage.Refresh(http.Space().Db);
        return Results.Ok(new { files = written });
    }

    private static string FreeName(Open o, string dir, string name)
    {
        var taken = o.Vault.List(Abs(dir)).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var i = 2; ; i++) if (!taken.Contains($"{stem} ({i}){ext}")) return $"{stem} ({i}){ext}";
    }

    /// <summary>Verschieben/Kopieren mit mindestens einer Seite in einem entsperrten Tresor; die andere Seite: derselbe Tresor oder die eigene Ablage.</summary>
    public static async Task<IResult> Move(HttpContext http, string from, string to, bool copy, string? conflict, CancellationToken ct)
    {
        if (TresorPaths.Is(from) || TresorPaths.Is(to))
            return Results.BadRequest(new { error = "Zwischen Tresor und Cryptomator-Tresor bitte über „Meine Dateien“." });
        if (Is(from) && Is(to))
        {
            var (a, ai) = copy ? Resolve(http, from) : Writable(http, from);
            var (b, bi) = Writable(http, to);
            if (a != b) return Results.BadRequest(new { error = "Zwischen zwei Cryptomator-Tresoren bitte über „Meine Dateien“." });
            var dir = bi.Contains('/') ? bi[..bi.LastIndexOf('/')] : "";
            var name = bi[(bi.LastIndexOf('/') + 1)..];
            if (o_Exists(a, bi))
            {
                if (conflict == "skip") return Results.Ok(new { path = to, skipped = true });
                if (conflict == "rename") name = FreeName(a, dir, name);
                else if (conflict == "replace") a.Vault.Delete(Abs(bi));
                else return Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = to });
            }
            var target = Join(dir, name);
            if (copy) await CopyWithin(a, ai, target, ct); else a.Vault.Move(Abs(ai), Abs(target));
            return Results.Ok(new { path = Show(a.Root, target), skipped = false, replaced = (long?)null, copied = false });
        }
        if (Is(from))
        {
            // Herausnehmen in die Ablage
            var (o, inner) = copy ? Resolve(http, from) : Writable(http, from);
            var loc = FilesApi.Loc(http, to).Creatable();
            var at = loc.Path;
            if (FileStore.Get(loc.Db, at) != null)
            {
                if (conflict == "skip") return Results.Ok(new { path = to, skipped = true });
                if (conflict == "rename") at = FileStore.FreeName(loc.Db, at);
                else if (conflict == "replace") FileStore.Delete(loc.Db, at);
                else return Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = to });
            }
            await CopyOut(o, inner, loc.Db, at, ct);
            if (!copy) o.Vault.Delete(Abs(inner));
            UserStorage.Refresh(http.Space().Db);
            return Results.Ok(new { path = loc.Show(at), skipped = false, replaced = (long?)null, copied = false });
        }
        {
            // Aus der Ablage hinein: das Original geht beim Verschieben endgültig (nicht in den Papierkorb)
            var (o, inner) = Writable(http, to);
            var src = FilesApi.Loc(http, from);
            if (copy) src.NotShowOnly("kopieren"); else src.Changeable();
            var entry = FileStore.Get(src.Db, src.Path) ?? throw new KeyNotFoundException("Nicht gefunden.");
            var dir = inner.Contains('/') ? inner[..inner.LastIndexOf('/')] : "";
            var name = inner[(inner.LastIndexOf('/') + 1)..];
            if (o_Exists(o, inner))
            {
                if (conflict == "skip") return Results.Ok(new { path = to, skipped = true });
                if (conflict == "rename") name = FreeName(o, dir, name);
                else if (conflict == "replace") o.Vault.Delete(Abs(inner));
                else return Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = to });
            }
            var target = Join(dir, name);
            await CopyIn(o, src.Db, entry, target, ct);
            if (!copy && FileStore.Delete(src.Db, src.Path, out var trash) && trash > 0) FileStore.PurgeTrash(src.Db, [trash]);
            UserStorage.Refresh(http.Space().Db);
            return Results.Ok(new { path = Show(o.Root, target), skipped = false, replaced = (long?)null, copied = false });
        }
    }

    private static bool o_Exists(Open o, string inner)
    {
        if (inner.Length == 0) return true;
        var parent = inner.Contains('/') ? inner[..inner.LastIndexOf('/')] : "";
        var name = inner[(inner.LastIndexOf('/') + 1)..];
        return o.Vault.List(Abs(parent)).Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task CopyWithin(Open o, string from, string to, CancellationToken ct)
    {
        var e = EntryAt(o, from);
        if (e.IsDir)
        {
            o.Vault.CreateDirectory(Abs(to));
            foreach (var c in o.Vault.List(Abs(from))) await CopyWithin(o, Join(from, c.Name), Join(to, c.Name), ct);
            return;
        }
        await using var s = o.Vault.OpenRead(Abs(from));
        await o.Vault.WriteAsync(Abs(to), s, ct);
    }

    private static async Task CopyOut(Open o, string inner, UserDb db, string target, CancellationToken ct)
    {
        var e = EntryAt(o, inner);
        if (e.IsDir)
        {
            FileStore.Mkcol(db, target);
            foreach (var c in o.Vault.List(Abs(inner))) await CopyOut(o, Join(inner, c.Name), db, target + "/" + c.Name, ct);
            return;
        }
        await using var s = o.Vault.OpenRead(Abs(inner));
        await FileStore.PutAsync(db, target, s, DavServer.Mime(e.Name), ct: ct);
    }

    private static async Task CopyIn(Open o, UserDb db, FileEntry e, string target, CancellationToken ct)
    {
        if (e.IsDir)
        {
            o.Vault.CreateDirectory(Abs(target));
            foreach (var c in FileStore.Children(db, e.Path)) await CopyIn(o, db, c, Join(target, c.Path[(c.Path.LastIndexOf('/') + 1)..]), ct);
            return;
        }
        await using var s = FileStore.OpenRead(db, e.Path);
        await o.Vault.WriteAsync(Abs(target), s, ct);
    }

    public static async Task Zip(HttpContext http, IReadOnlyList<string> paths, string fileName)
    {
        var resolved = paths.Select(p => Resolve(http, p)).ToList();
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.CacheControl = "no-store";
        if (http.Features.Get<IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        await http.Response.StartAsync();
        await using var buffered = new BufferedStream(http.Response.Body, 256 * 1024);
        using (var zip = new ZipArchive(buffered, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(Open o, string inner, string name)
            {
                http.RequestAborted.ThrowIfCancellationRequested();
                var e = EntryAt(o, inner);
                if (e.IsDir)
                {
                    zip.CreateEntry(name + "/");
                    foreach (var c in o.Vault.List(Abs(inner))) Add(o, Join(inner, c.Name), name + "/" + c.Name);
                    return;
                }
                var level = DmOffice.Dav.Zip.Packed.Contains(Path.GetExtension(e.Name).ToLowerInvariant()) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                var entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = e.Modified;
                using var target = entry.Open();
                using var s = o.Vault.OpenRead(Abs(inner));
                s.CopyTo(target);
            }
            foreach (var (o, inner) in resolved) Add(o, inner, EntryAt(o, inner).Name);
        }
        await buffered.FlushAsync();
    }
}

/// <summary>Cryptomator-Tresor gesperrt (oder nie entsperrt) - 423 { locked: "vault" }.</summary>
public sealed class VaultLockedException() : Exception("Der Cryptomator-Tresor ist gesperrt.");
