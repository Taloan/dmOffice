using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Search;
using DmOffice.Storage;

namespace DmOffice.Dms;

// Schnittstellen vom DMS zum Rest von dmOffice (docs/dms-konzept.md, „Herauslösbarkeit als eigene App“). Das DMS greift auf
// Dateiablage, Texterkennung, KI und externe Ordner nur hierüber zu; eine eigene DMS-App setzt sie mit eigenen Mitteln um.

/// <summary>Dokumentdateien in der Ablage einer Person.</summary>
public interface IDmsFiles
{
    /// <summary>Neue Datei unter freiem Namen anlegen (fehlende Ordner davor auch); gibt Pfad, Datei-Kennung und ETag zurück.</summary>
    Task<(string Path, long FileId, string Etag)> CreateAsync(UserSpace space, DmsStore store, string path, Stream data, string? mime, CancellationToken ct);
    /// <summary>Aktueller Pfad und ETag einer Datei; null, wenn sie nicht (mehr) in der Ablage liegt.</summary>
    (string Path, string Etag)? Find(UserSpace space, DmsStore store, long fileId);
    /// <summary>Liegt die Datei im Papierkorb? (Der Tresor hat keinen.)</summary>
    bool InTrash(UserSpace space, DmsStore store, long fileId);
    /// <summary>Verschieben bzw. umbenennen unter freiem Namen; gibt den neuen Pfad zurück.</summary>
    string Move(UserSpace space, DmsStore store, long fileId, string to);
    /// <summary>Offener Bereich: in den Papierkorb (purge: endgültig); Tresor: endgültig.</summary>
    void Delete(UserSpace space, DmsStore store, long fileId, bool purge = false);
    Stream OpenRead(UserSpace space, DmsStore store, long fileId);
    /// <summary>Ordner aus der Galerie ausnehmen (Ablageordner vom DMS).</summary>
    void ExcludeFromGallery(UserSpace space, string folder);
    /// <summary>Ordner, deren Inhalt geschützt ist (Abgleichordner von notizi) - dort legt das DMS nichts ab.</summary>
    bool Protected(UserSpace space, string path);
}

/// <summary>Text und Archivfassung.</summary>
public interface IDmsText
{
    bool OcrAvailable { get; }
    bool PdfAAvailable { get; }
    /// <summary>Text ohne Texterkennung; WantsOcr: gescannt bzw. Bild.</summary>
    Extracted Extract(byte[] data, string name, string? mime, CancellationToken ct);
    /// <summary>Durchsuchbare PDF und erkannter Text zu einem Scan oder Bild.</summary>
    DmsArchive.Searchable MakeSearchable(byte[] data, string name, string? mime, CancellationToken ct);
    /// <summary>PDF nach PDF/A-2b, mit den Angaben in den Metadaten; null, wenn es nicht ging.</summary>
    byte[]? ToPdfA(byte[] pdf, DmsArchive.PdfInfo? info, CancellationToken ct);
    /// <summary>Ganze Mail (.eml) als PDF; null, wenn es nicht ging.</summary>
    byte[]? MailPdf(byte[] eml, CancellationToken ct);
    /// <summary>Seitenzahl einer PDF; 0, wenn sie sich nicht lesen lässt.</summary>
    int PdfPageCount(byte[] pdf);
    /// <summary>Eine Seite als JPEG in der Breite <paramref name="width"/>; null, wenn es nicht ging.</summary>
    byte[]? PdfPage(byte[] pdf, int page, int width);
}

/// <summary>KI beim Import.</summary>
public interface IDmsAi
{
    bool Allowed(UserSpace space);
    /// <summary>Name des eingerichteten Dienstes (für den Hinweis in den Einstellungen).</summary>
    string? Service { get; }
    Task<string?> WriteAsync(string system, string prompt, CancellationToken ct);
}

/// <summary>Ein Eintrag in einem überwachten Ordner.</summary>
public sealed record DmsSourceFile(string Path, string Name, long Size, string Etag, DateTime Modified);

/// <summary>Überwachte Ordner: eigene Ablage oder externer Ordner.</summary>
public interface IDmsSources
{
    /// <summary>Dateien unter dem Ordner (mit Unterordnern). ReadOnly: externer Ordner nur zum Lesen.</summary>
    Task<(List<DmsSourceFile> Files, bool ReadOnly)> ListAsync(UserSpace space, string? mount, string folder, CancellationToken ct);
    Task<Stream> OpenReadAsync(UserSpace space, string? mount, string path, CancellationToken ct);
    Task DeleteAsync(UserSpace space, string? mount, string path, CancellationToken ct);
    /// <summary>Externe Ordner, die die Person sieht: Kennung und Name.</summary>
    List<(string Id, string Name)> Mounts(UserSpace space);
}

/// <summary>
/// Externer Ordner als Speicherort (docs/dms-konzept.md, „Externer Ordner als Speicherort“): Dateien dort anlegen, lesen, ersetzen,
/// verschieben, löschen. ETag ist Größe und Änderungszeit (wie bei den überwachten Ordnern).
/// </summary>
public interface IDmsExternal
{
    /// <summary>Der Ordner, wenn die Person ihn sieht: Name, nur zum Lesen.</summary>
    (string Name, bool ReadOnly)? MountInfo(UserSpace space, string mount);
    /// <summary>Neue Datei unter freiem Namen (fehlende Ordner davor auch); gibt Pfad und ETag zurück.</summary>
    Task<(string Path, string Etag)> CreateAsync(UserSpace space, string mount, string path, byte[] data, CancellationToken ct);
    /// <summary>Inhalt ersetzen; gibt den neuen ETag zurück.</summary>
    Task<string> ReplaceAsync(UserSpace space, string mount, string path, byte[] data, CancellationToken ct);
    Task<byte[]> ReadAsync(UserSpace space, string mount, string path, CancellationToken ct);
    /// <summary>Verschieben bzw. umbenennen unter freiem Namen; gibt den neuen Pfad zurück.</summary>
    Task<string> MoveAsync(UserSpace space, string mount, string from, string to, CancellationToken ct);
    /// <summary>Größe und ETag, null: gibt es nicht.</summary>
    Task<(long Size, string Etag)?> StatAsync(UserSpace space, string mount, string path, CancellationToken ct);
    /// <summary>Endgültig löschen (einen Papierkorb gibt es draußen nicht).</summary>
    Task RemoveAsync(UserSpace space, string mount, string path, CancellationToken ct);
    /// <summary>Gibt es den Ordner?</summary>
    Task<bool> FolderExistsAsync(UserSpace space, string mount, string path, CancellationToken ct);
}

/// <summary>Eine Mail in einem Ordner (für Mailordner als Quelle).</summary>
public sealed record DmsMailItem(string Folder, uint Uid, string? MessageId, DateTimeOffset Date);

/// <summary>
/// Mailkonten der Person (Mailordner als Quelle). Setzt das Mail-Modul (Mail/MailToDms.cs) - das DMS kennt keine Mail-Klassen, nur
/// die rohe Mail (.eml) und diese Aufrufe.
/// </summary>
public interface IDmsMail
{
    /// <summary>Konten: Kennung, Name, Adresse.</summary>
    List<(string Id, string Name)> Accounts(UserSpace space);
    /// <summary>Ordner eines Kontos: Pfad und Name.</summary>
    List<(string Path, string Name)> Folders(UserSpace space, string account);
    /// <summary>Mails eines Ordners seit einem Zeitpunkt, älteste zuerst.</summary>
    List<DmsMailItem> List(UserSpace space, string account, string folder, DateTimeOffset since);
    /// <summary>Die Mail roh (.eml).</summary>
    Task<byte[]> RawAsync(UserSpace space, string account, string folder, uint uid, CancellationToken ct);
    /// <summary>Danach mit der Mail: seen, flag, move (target), delete (in den Papierkorb).</summary>
    void After(UserSpace space, string account, string folder, uint uid, string action, string? target);
}

/// <summary>Umsetzung der Schnittstellen in dmOffice.</summary>
public sealed class DmsHost(ServerConfig config) : IDmsFiles, IDmsText, IDmsAi, IDmsSources, IDmsExternal
{
    /// <summary>Für Hintergrund und API gesetzt in <see cref="DmsApi.Map"/>.</summary>
    public static DmsHost? Current { get; set; }

    /// <summary>Mailkonten - gesetzt vom Mail-Modul (Mail/MailToDms.cs); null ohne Mail.</summary>
    public static IDmsMail? Mail { get; set; }

    // ---------------- Dateien ----------------

    public async Task<(string Path, long FileId, string Etag)> CreateAsync(UserSpace space, DmsStore store, string path, Stream data, string? mime, CancellationToken ct)
    {
        if (store.Tresor)
        {
            var files = store.TresorDb.Files;
            var slash = path.LastIndexOf('/');
            var parent = slash < 0 ? 0 : files.EnsureFolder(path[..slash]);
            var (id, _) = await files.CreateAsync(parent, path[(slash + 1)..], data, mime, ct);
            return (files.PathOf(id)!, id, files.Find(id)!.Modified.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var db = space.Db;
        var dir = FileStore.Parent(path) ?? "";
        if (dir.Length > 0) FileStore.EnsureDir(db, dir);
        var at = await FileStore.PutNewAsync(db, path, data, mime, ct);
        var e = FileStore.Get(db, at) ?? throw new IOException("Die Datei wurde nicht angelegt.");
        return (at, e.FileId, e.Etag);
    }

    public (string Path, string Etag)? Find(UserSpace space, DmsStore store, long fileId)
    {
        if (store.Tresor)
            return store.TresorDb.Files.Find(fileId) is { IsDir: false } t
                ? (store.TresorDb.Files.PathOf(fileId)!, t.Modified.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
        return FileStore.ById(space.Db, fileId) is { IsDir: false } e ? (e.Path, e.Etag) : null;
    }

    public bool InTrash(UserSpace space, DmsStore store, long fileId) =>
        !store.Tresor && space.Db.Scalar("SELECT 1 FROM file_trash_items WHERE fileid = $f", ("$f", fileId)) != null;

    public string Move(UserSpace space, DmsStore store, long fileId, string to)
    {
        if (store.Tresor) return store.TresorDb.Files.MoveTo(fileId, to);
        var db = space.Db;
        var e = FileStore.ById(db, fileId) ?? throw new KeyNotFoundException("Die Datei gibt es nicht mehr.");
        if (e.Path == to) return to;
        var dir = FileStore.Parent(to) ?? "";
        if (dir.Length > 0) FileStore.EnsureDir(db, dir);
        to = FileStore.FreeName(db, to);
        FileStore.MoveOrCopy(db, e.Path, to, overwrite: false, copy: false);
        return to;
    }

    public void Delete(UserSpace space, DmsStore store, long fileId, bool purge = false)
    {
        if (store.Tresor) { if (store.TresorDb.Files.Find(fileId) is { IsDir: false }) store.TresorDb.Files.Delete(fileId); return; }
        if (FileStore.ById(space.Db, fileId) is not { } e) return;
        FileStore.Delete(space.Db, e.Path, out var trash);
        // Verschoben in den Tresor: im offenen Bereich bleibt nichts zurück
        if (purge && trash != 0) FileStore.PurgeTrash(space.Db, [trash]);
    }

    public Stream OpenRead(UserSpace space, DmsStore store, long fileId)
    {
        if (store.Tresor)
            return store.TresorDb.Files.Find(fileId) is { IsDir: false } ? store.TresorDb.Files.OpenRead(fileId)
                : throw new KeyNotFoundException("Die Datei gibt es nicht mehr.");
        return FileStore.OpenRead(space.Db, (FileStore.ById(space.Db, fileId) ?? throw new KeyNotFoundException("Die Datei gibt es nicht mehr.")).Path);
    }

    public void ExcludeFromGallery(UserSpace space, string folder)
    {
        folder = FileStore.Normalize(folder);
        if (folder.Length == 0 || space.Settings.PhotoExcludes.Any(x => x.Equals(folder, StringComparison.OrdinalIgnoreCase))) return;
        space.UpdateSettings(s => s.PhotoExcludes = s.PhotoExcludes.Append(folder).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public bool Protected(UserSpace space, string path) => FileStore.IsSystem(space.Db, path);

    // ---------------- Text ----------------

    public bool OcrAvailable => Ocr.Available;
    public bool PdfAAvailable => DmsArchive.PdfAAvailable;

    private static readonly ExtractLimits Limits = new() { MaxBytes = 200L * 1024 * 1024, MaxPages = 1000, MaxChars = 1_000_000 };

    public Extracted Extract(byte[] data, string name, string? mime, CancellationToken ct) => TextExtract.Extract(data, name, mime, ocr: false, Limits, ct);

    public DmsArchive.Searchable MakeSearchable(byte[] data, string name, string? mime, CancellationToken ct) =>
        DmsArchive.MakeSearchable(data, TextExtract.Ext(name, mime), ct);

    public byte[]? ToPdfA(byte[] pdf, DmsArchive.PdfInfo? info, CancellationToken ct) => DmsArchive.ToPdfA(pdf, ct, info);

    /// <summary>HTML einer Mail als Text (Absätze, Listen, Links).</summary>
    public string HtmlToText(string html) => DmOffice.Mail.MailHtml.ToText(html);

    public int PdfPageCount(byte[] pdf) => Dav.PdfPages.Count(pdf);

    // Wie in der Dateiablage, mit derselben Grenze für sehr hohe Seiten
    public byte[]? PdfPage(byte[] pdf, int page, int width) => Dav.PdfPages.Page(pdf, page, width);

    public byte[]? MailPdf(byte[] eml, CancellationToken ct)
    {
        try { return DmsMailPdf.Make(eml, HtmlToText, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    // ---------------- KI ----------------

    public bool Allowed(UserSpace space) => Chat.PukkiAi.Allowed(config, space.User);

    public string? Service => config.Current.AiProvider == Chat.PukkiAi.OpenAi ? config.Current.AiEndpoint : "Claude (Anthropic)";

    public Task<string?> WriteAsync(string system, string prompt, CancellationToken ct) => Chat.PukkiAi.Write(config, system, prompt, null, ct, search: false);

    // ---------------- Überwachte Ordner ----------------

    public async Task<(List<DmsSourceFile> Files, bool ReadOnly)> ListAsync(UserSpace space, string? mount, string folder, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mount))
            return (FileStore.Descendants(space.Db, FileStore.Normalize(folder)).Where(f => !f.IsDir && !Protected(space, f.Path))
                .Select(f => new DmsSourceFile(f.Path, f.Name, f.Size, f.Etag, f.Created > f.Modified ? f.Created : f.Modified)).ToList(), false);
        var (driver, ro) = ExternalFolders.Open(space, config, mount);
        using (driver)
        {
            var list = new List<DmsSourceFile>();
            var dirs = new Queue<(string Path, int Depth)>();
            dirs.Enqueue((StoragePath.Clean(folder), 0));
            while (dirs.Count > 0 && list.Count < 5000)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = dirs.Dequeue();
                foreach (var e in await driver.ListAsync(dir, ct))
                {
                    var p = StoragePath.Join(dir, e.Name);
                    if (e.IsDir) { if (depth < 8) dirs.Enqueue((p, depth + 1)); }
                    else list.Add(new DmsSourceFile(p, e.Name, e.Size, $"{e.Size}-{e.Modified.Ticks}", e.Modified));
                }
            }
            return (list, ro);
        }
    }

    public async Task<Stream> OpenReadAsync(UserSpace space, string? mount, string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mount)) return FileStore.OpenRead(space.Db, path);
        var (driver, _) = ExternalFolders.Open(space, config, mount);
        // Ganz lesen, dann den Treiber schließen (Dokumente sind klein genug)
        using (driver)
        {
            await using var s = await driver.OpenReadAsync(path, ct);
            var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms;
        }
    }

    public async Task DeleteAsync(UserSpace space, string? mount, string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mount)) { FileStore.Delete(space.Db, path); return; }
        var (driver, ro) = ExternalFolders.Open(space, config, mount);
        using (driver)
        {
            if (ro) return;
            await driver.DeleteAsync(path, ct);
            ExternalFolders.Changed(space, mount, FileStore.Parent(path) ?? "");
        }
    }

    public List<(string Id, string Name)> Mounts(UserSpace space) =>
        ExternalFolders.MountsOf(space, config).Select(m => (m.Id, m.Name)).ToList();

    // ---------------- Externer Ordner als Speicherort ----------------

    public (string Name, bool ReadOnly)? MountInfo(UserSpace space, string mount) =>
        ExternalFolders.MountsOf(space, config).FirstOrDefault(m => m.Id == mount) is { } m ? (m.Name, m.ReadOnly) : null;

    private static string Etag(DriverEntry e) => $"{e.Size}-{e.Modified.Ticks}";

    /// <summary>Schreibbarer Treiber; wirft bei Ordnern nur zum Lesen.</summary>
    private IStorageDriver Writable(UserSpace space, string mount)
    {
        var (driver, ro) = ExternalFolders.Open(space, config, mount);
        if (!ro) return driver;
        driver.Dispose();
        throw new InvalidOperationException("Der externe Ordner ist nur zum Lesen freigegeben.");
    }

    private static async Task<string> FreeAsync(IStorageDriver d, string path, CancellationToken ct)
    {
        if (await d.StatAsync(path, ct) == null) return path;
        var dir = StoragePath.Parent(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = StoragePath.Join(dir, $"{name} ({i}){ext}");
            if (await d.StatAsync(p, ct) == null) return p;
        }
    }

    public async Task<(string Path, string Etag)> CreateAsync(UserSpace space, string mount, string path, byte[] data, CancellationToken ct)
    {
        using var d = Writable(space, mount);
        path = await FreeAsync(d, StoragePath.Clean(path), ct);
        await d.WriteAsync(path, new MemoryStream(data), ct);
        ExternalFolders.Changed(space, mount, StoragePath.Parent(path));
        return (path, await d.StatAsync(path, ct) is { } e ? Etag(e) : "");
    }

    public async Task<string> ReplaceAsync(UserSpace space, string mount, string path, byte[] data, CancellationToken ct)
    {
        using var d = Writable(space, mount);
        await d.WriteAsync(StoragePath.Clean(path), new MemoryStream(data), ct);
        ExternalFolders.Changed(space, mount, StoragePath.Parent(path));
        return await d.StatAsync(path, ct) is { } e ? Etag(e) : "";
    }

    public async Task<byte[]> ReadAsync(UserSpace space, string mount, string path, CancellationToken ct)
    {
        var (d, _) = ExternalFolders.Open(space, config, mount);
        using (d)
        {
            await using var s = await d.OpenReadAsync(StoragePath.Clean(path), ct);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
    }

    public async Task<string> MoveAsync(UserSpace space, string mount, string from, string to, CancellationToken ct)
    {
        using var d = Writable(space, mount);
        to = StoragePath.Clean(to);
        if (to == from) return to;
        to = await FreeAsync(d, to, ct);
        if (StoragePath.Parent(to) is { Length: > 0 } dir) await d.MkdirAsync(dir, ct);
        await d.MoveAsync(from, to, ct);
        ExternalFolders.Changed(space, mount, StoragePath.Parent(from));
        ExternalFolders.Changed(space, mount, StoragePath.Parent(to));
        return to;
    }

    public async Task<(long Size, string Etag)?> StatAsync(UserSpace space, string mount, string path, CancellationToken ct)
    {
        var (d, _) = ExternalFolders.Open(space, config, mount);
        using (d) return await d.StatAsync(StoragePath.Clean(path), ct) is { IsDir: false } e ? (e.Size, Etag(e)) : null;
    }

    public async Task<bool> FolderExistsAsync(UserSpace space, string mount, string path, CancellationToken ct)
    {
        path = StoragePath.Clean(path);
        if (path.Length == 0) return true;
        var (d, _) = ExternalFolders.Open(space, config, mount);
        using (d) return await d.StatAsync(path, ct) is { IsDir: true };
    }

    public async Task RemoveAsync(UserSpace space, string mount, string path, CancellationToken ct)
    {
        using var d = Writable(space, mount);
        await d.DeleteAsync(StoragePath.Clean(path), ct);
        ExternalFolders.Changed(space, mount, StoragePath.Parent(path));
    }
}
