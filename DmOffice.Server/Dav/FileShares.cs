using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>Ein Ordner der Dateiablage, den sein Besitzer mit anderen Benutzern dieses Servers teilt.</summary>
public sealed class FolderShare
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    /// <summary>fileid des Ordners in der Datenbank des Besitzers - bleibt beim Umbenennen und Verschieben.</summary>
    public long Folder { get; set; }
    public List<string> Members { get; set; } = new();
    /// <summary>Teilmenge von Members, die nur lesen darf.</summary>
    public List<string> ReadOnly { get; set; } = new();
}

/// <summary>
/// Geteilte Ordner - data/file-shares.json (eigene Datei neben shares.json der Kalender und Notizordner).
/// Der Inhalt bleibt in der Ablage des Besitzers; der Server liest und schreibt dort fuer die Mitglieder.
/// </summary>
public sealed class FolderShareStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<FolderShare> _shares;

    public FolderShareStore(DataPaths paths)
    {
        _path = Path.Combine(paths.Root, "file-shares.json");
        _shares = File.Exists(_path) ? JsonSerializer.Deserialize<List<FolderShare>>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public List<FolderShare> All() { lock (_lock) return _shares.Select(Copy).ToList(); }

    public FolderShare? ById(int id) { lock (_lock) return _shares.FirstOrDefault(s => s.Id == id) is { } s ? Copy(s) : null; }

    public FolderShare? Of(string owner, long folder) { lock (_lock) return _shares.FirstOrDefault(s => s.Owner == owner && s.Folder == folder) is { } s ? Copy(s) : null; }

    /// <summary>Mitglieder setzen; leer hebt die Freigabe auf. readOnly: wer davon nur lesen darf.</summary>
    public void Set(string owner, long folder, IEnumerable<string> members, IEnumerable<string>? readOnly = null)
    {
        var list = members.Where(m => m != owner).Distinct().ToList();
        var ro = (readOnly ?? Array.Empty<string>()).Where(list.Contains).Distinct().ToList();
        lock (_lock)
        {
            var s = _shares.FirstOrDefault(x => x.Owner == owner && x.Folder == folder);
            if (list.Count == 0) { if (s != null) _shares.Remove(s); }
            else if (s == null) _shares.Add(new FolderShare { Id = _shares.Count == 0 ? 1 : _shares.Max(x => x.Id) + 1, Owner = owner, Folder = folder, Members = list, ReadOnly = ro });
            else { s.Members = list; s.ReadOnly = ro; }
            Save();
        }
    }

    /// <summary>Ein Mitglied steigt aus.</summary>
    public void Leave(int id, string member)
    {
        lock (_lock)
        {
            var s = _shares.FirstOrDefault(x => x.Id == id);
            if (s == null || !s.Members.Remove(member)) return;
            s.ReadOnly.Remove(member);
            if (s.Members.Count == 0) _shares.Remove(s);
            Save();
        }
    }

    /// <summary>Benutzer geloescht: seine Freigaben und Mitgliedschaften weg.</summary>
    public void Forget(string user)
    {
        lock (_lock)
        {
            int n = _shares.RemoveAll(s => s.Owner == user);
            foreach (var s in _shares) { n += s.Members.Remove(user) ? 1 : 0; s.ReadOnly.Remove(user); }
            n += _shares.RemoveAll(s => s.Members.Count == 0);
            if (n > 0) Save();
        }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_shares, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static FolderShare Copy(FolderShare s) => new() { Id = s.Id, Owner = s.Owner, Folder = s.Folder, Members = s.Members.ToList(), ReadOnly = s.ReadOnly.ToList() };
}

/// <summary>Ein mit mir geteilter Ordner, wie er unter „Mit mir geteilt“ haengt. Dir: der Ordner beim Besitzer (echter Pfad).</summary>
public sealed record FileMount(FolderShare Share, UserSpace Owner, FileEntry Dir, string Name, bool CanWrite)
{
    /// <summary>Pfad beim Mitglied: "Mit mir geteilt/Fotos (von Anna)".</summary>
    public string Path => SharedFiles.Folder + "/" + Name;

    /// <summary>Liegt der Pfad (beim Besitzer) im geteilten Ordner oder ist er es?</summary>
    public bool Contains(string ownerPath) => ownerPath == Dir.Path || ownerPath.StartsWith(Dir.Path + "/", StringComparison.Ordinal);

    /// <summary>fileid beim Mitglied: Freigabe in den oberen Bits - kollidiert nie mit eigenen (Nextcloud-Clients erkennen Dateien daran).</summary>
    public long Id(long fileId) => ((long)Share.Id << SharedFiles.IdShift) + fileId;
}

/// <summary>
/// Ein Ort in der Dateiablage, wie der angemeldete Benutzer ihn anspricht: eigener Pfad, ein Pfad in einem mit ihm geteilten
/// Ordner (dann Space = Besitzer, Path = Pfad beim Besitzer) oder „Mit mir geteilt“ selbst (Virtual).
/// </summary>
public sealed record FileLoc(UserSpace Space, string Path, FileMount? Mount, bool Virtual = false)
{
    public UserDb Db => Space.Db;
    public bool Shared => Mount != null;
    public bool CanWrite => !Virtual && (Mount?.CanWrite ?? true);
    /// <summary>Der geteilte Ordner selbst: umbenennen, verschieben und loeschen kann nur der Besitzer.</summary>
    public bool IsMountRoot => Mount != null && Path == Mount.Dir.Path;

    /// <summary>Aenderungen nur mit Bearbeitungsrecht - sonst 403.</summary>
    public FileLoc Writable() => Virtual
        ? throw new UnauthorizedAccessException("„Mit mir geteilt“ zeigt nur, was andere mit dir teilen – hier lässt sich nichts ablegen.")
        : CanWrite ? this : throw new UnauthorizedAccessException("Nur lesbar – der Besitzer hat dir keine Bearbeitung erlaubt.");

    /// <summary>Wie <see cref="Writable"/>, und nicht der geteilte Ordner selbst.</summary>
    public FileLoc Changeable() => IsMountRoot
        ? throw new UnauthorizedAccessException("Den geteilten Ordner selbst kann nur der Besitzer umbenennen, verschieben oder löschen.")
        : Writable();

    /// <summary>Wie <see cref="Changeable"/>, fuer ein neues Ziel: „Mit mir geteilt“ ist in der Wurzel als Name reserviert.</summary>
    public FileLoc Creatable() => !Shared && Path == SharedFiles.Folder
        ? throw new UnauthorizedAccessException("„Mit mir geteilt“ ist ein reservierter Name – bitte einen anderen wählen.")
        : Changeable();

    /// <summary>Pfad beim Besitzer → Pfad, wie der Benutzer ihn sieht.</summary>
    public string Show(string path) => Mount == null ? path : Mount.Path + path[Mount.Dir.Path.Length..];

    /// <summary>Eintrag beim Besitzer → wie der Benutzer ihn sieht (Pfad und fileid umgerechnet).</summary>
    public FileEntry Show(FileEntry f) => Mount == null ? f : f with { Path = Show(f.Path), FileId = Mount.Id(f.FileId) };

    /// <summary>Pfad darunter (rel ohne fuehrenden Schraegstrich).</summary>
    public string Child(string rel) => rel.Length == 0 ? Path : Path.Length == 0 ? rel : Path + "/" + rel;

    /// <summary>Der geschuetzte notizi-Ordner dessen, dem die Ablage gehoert.</summary>
    public bool Protected(string path) => FileStore.InRoot(path, Space.Settings.Notizi.Root);
}

/// <summary>
/// „Mit mir geteilt“: die mit dem Benutzer geteilten Ordner, als virtueller Ordner in der Wurzel seiner Ablage (Weboberflaeche,
/// WebDAV, Nextcloud-Clients). Die Freigabe wird bei jeder Anfrage frisch geprueft - Widerruf wirkt sofort.
/// </summary>
public static class SharedFiles
{
    public const string Folder = "Mit mir geteilt";

    /// <summary>fileid beim Mitglied = Freigabe · 2^40 + fileid beim Besitzer (bleibt unter 2^53, sicher in JavaScript).</summary>
    public const int IdShift = 40;

    /// <summary>fileid von „Mit mir geteilt“ selbst.</summary>
    public const long RootId = (1L << 52) - 1;

    /// <summary>Darf dieser Ordner geteilt werden? Nicht die Wurzel, nicht der notizi-Ordner, nichts darin und nichts darueber.</summary>
    public static bool Shareable(UserDb db, FileEntry f)
    {
        var root = FileStore.ProtectedRoot(db);
        return f.IsDir && f.Path.Length > 0 && !FileStore.InRoot(f.Path, root) && !FileStore.Contains(f.Path, root);
    }

    /// <summary>Die mit mir geteilten Ordner, die es gerade gibt - je Anfrage einmal berechnet.</summary>
    public static List<FileMount> Mounts(HttpContext http)
    {
        if (http.Items.TryGetValue(typeof(FileMount), out var cached) && cached is List<FileMount> list) return list;
        list = Mounts(http.RequestServices, http.Space().User.Id);
        http.Items[typeof(FileMount)] = list;
        return list;
    }

    public static List<FileMount> Mounts(IServiceProvider services, string userId)
    {
        var spaces = services.GetRequiredService<Spaces>();
        var result = new List<FileMount>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in services.GetRequiredService<FolderShareStore>().All().Where(s => s.Members.Contains(userId)).OrderBy(s => s.Id))
        {
            UserSpace owner;
            try { owner = spaces.Get(s.Owner); } catch (KeyNotFoundException) { continue; }
            if (FileStore.FilesOff(owner.Db)) continue;
            // Im Papierkorb des Besitzers oder weg: ruht (kommt der Ordner zurueck, ist er wieder da)
            if (FileStore.ById(owner.Db, s.Folder) is not { } dir || !Shareable(owner.Db, dir)) continue;
            var first = $"{dir.Name} (von {owner.User.DisplayName.Replace('/', '-').Replace('\\', '-')})";
            var name = first;
            for (int n = 2; !names.Add(name); n++) name = $"{first} ({n})";
            result.Add(new FileMount(s, owner, dir, name, !s.ReadOnly.Contains(userId)));
        }
        return result;
    }

    /// <summary>Liegt der Pfad (beim Benutzer) in „Mit mir geteilt“? Nur, wenn ihm jemand etwas teilt - sonst ist es ein gewoehnlicher Name.</summary>
    public static bool Under(HttpContext http, string path) =>
        (path == Folder || path.StartsWith(Folder + "/", StringComparison.Ordinal)) && Mounts(http).Count > 0;

    /// <summary>Ein eigener Eintrag gleichen Namens in der Wurzel wird verdeckt, solange es „Mit mir geteilt“ gibt.</summary>
    public static bool Hidden(HttpContext http, string ownPath) => ownPath == Folder && Mounts(http).Count > 0;

    /// <summary>Pfad (normalisiert, wie der Benutzer ihn sieht) aufloesen. Unbekannter geteilter Ordner: 404.</summary>
    public static FileLoc Resolve(HttpContext http, string path)
    {
        var me = http.Space();
        if (!Under(http, path)) return new FileLoc(me, path, null);
        if (path == Folder) return new FileLoc(me, path, null, Virtual: true);
        var rest = path[(Folder.Length + 1)..];
        var slash = rest.IndexOf('/');
        var name = slash < 0 ? rest : rest[..slash];
        var mount = Mounts(http).FirstOrDefault(m => m.Name == name) ?? throw new KeyNotFoundException("Nicht gefunden.");
        return new FileLoc(mount.Owner, mount.Dir.Path + (slash < 0 ? "" : rest[slash..]), mount);
    }

    /// <summary>fileid, wie der Benutzer sie sieht → Ort und Eintrag; eigene wie bisher. Ohne Freigabe: null.</summary>
    public static (FileLoc Loc, FileEntry File)? ById(HttpContext http, long id)
    {
        var me = http.Space();
        if (id < 1L << IdShift)
            return FileStore.ById(me.Db, id) is { } own && !Under(http, own.Path) ? (new FileLoc(me, own.Path, null), own) : null;
        var mount = Mounts(http).FirstOrDefault(m => m.Share.Id == id >> IdShift);
        if (mount == null || FileStore.ById(mount.Owner.Db, id & ((1L << IdShift) - 1)) is not { } f || !mount.Contains(f.Path)) return null;
        return (new FileLoc(mount.Owner, f.Path, mount), f);
    }

    /// <summary>Darf der Benutzer diese Datei des Besitzers sehen (und schreiben)? Fuer Office-Tokens, die ueber Stunden gelten.</summary>
    public static (bool See, bool Write) Allows(IServiceProvider services, string userId, string ownerId, FileEntry f)
    {
        var mounts = Mounts(services, userId).Where(m => m.Owner.User.Id == ownerId && m.Contains(f.Path)).ToList();
        return (mounts.Count > 0, mounts.Any(m => m.CanWrite));
    }

    /// <summary>„Mit mir geteilt“ als Eintrag: ETag und Zeit aus den geteilten Ordnern - aendert sich darin etwas, aendert er sich mit.</summary>
    public static FileEntry RootEntry(List<FileMount> mounts)
    {
        var tag = string.Join("|", mounts.Select(m => m.Share.Id + ":" + m.Name + ":" + m.Dir.Etag + ":" + m.CanWrite));
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tag)))[..16].ToLowerInvariant() + "\"";
        var modified = mounts.Count == 0 ? DateTime.UtcNow : mounts.Max(m => m.Dir.Modified);
        var created = mounts.Count == 0 ? DateTime.UtcNow : mounts.Min(m => m.Dir.Created);
        return new FileEntry(Folder, true, 0, etag, null, created, modified, RootId);
    }

    /// <summary>Ein geteilter Ordner, wie er in „Mit mir geteilt“ liegt.</summary>
    public static FileEntry MountEntry(FileMount m) => m.Dir with { Path = m.Path, FileId = m.Id(m.Dir.FileId) };

    /// <summary>
    /// Kopieren zwischen zwei Ablagen (eigene ↔ geteilt, oder zwei Besitzer): Inhalt fuer Inhalt neu geschrieben, das Kontingent
    /// zaehlt beim Ziel. Den geschuetzten notizi-Ordner der Quelle laesst es aus. Gibt zurueck, ob das Ziel neu ist.
    /// </summary>
    public static async Task<bool> CopyAcross(FileLoc from, FileLoc to, bool overwrite, CancellationToken ct = default)
    {
        var src = FileStore.Get(from.Db, from.Path) ?? throw new KeyNotFoundException();
        bool exists = FileStore.Get(to.Db, to.Path) != null;
        if (exists && !overwrite) throw new Pim.PreconditionFailedException();
        if (FileStore.Get(to.Db, FileStore.Parent(to.Path)!) is not { IsDir: true }) throw new DavConflictException("Zielordner fehlt.");
        var items = (src.IsDir ? new[] { src }.Concat(FileStore.Descendants(from.Db, src.Path)) : [src]).Where(f => !from.Protected(f.Path)).ToList();
        var quota = FileStore.Quota(to.Db);
        if (quota > 0 && UserStorage.Counted(to.Db) + items.Sum(f => f.Size) > quota) throw new QuotaExceededException(quota);
        if (exists) FileStore.Delete(to.Db, to.Path);
        foreach (var f in items)
        {
            ct.ThrowIfCancellationRequested();
            var dst = to.Path + f.Path[src.Path.Length..];
            if (f.IsDir) FileStore.Mkcol(to.Db, dst);
            else
            {
                await using var data = FileStore.OpenRead(from.Db, f.Path);
                await FileStore.PutAsync(to.Db, dst, data, f.Mime, mtime: f.Modified, ct: ct);
            }
        }
        return !exists;
    }
}
