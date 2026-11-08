using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice;

/// <summary>
/// Speicher hinter den Tresor-Dateien (docs/tresor-cryptomator-konzept.md, 1 und 3.4): Inhalte und - wo der Speicher einen
/// eigenen Baum hat (Cryptomator) - Ordner, Umbenennen und Verschieben. Pfade im Klartext („Ordner/Name“). Die Kennung im Speicher
/// (<c>files.blob</c>) vergibt der Speicher selbst; ein Speicher mit Baum braucht keine und gibt null zurück.
/// </summary>
public interface ITresorStorage
{
    Task<(string? Ref, long Size)> WriteAsync(string path, Stream data, long maxLength, Func<Exception>? tooLarge, CancellationToken ct);
    Stream OpenRead(string path, string? reference);
    /// <summary>Datei oder Ordner (samt Inhalt) entfernen.</summary>
    void Delete(string path, string? reference, bool isDir);
    void CreateFolder(string path);
    void Move(string from, string to, bool isDir);
    /// <summary>Platz für eine Datei, deren Kennung nicht mehr gebraucht wird (nach Ersetzen).</summary>
    void Release(string? reference);
}

/// <summary>Heutiger Speicher: jede Datei ein Blob im BlobStore des Tresors; der Baum steht nur in der Tabelle files.</summary>
public sealed class BlobTresorStorage(BlobStore blobs) : ITresorStorage
{
    public async Task<(string? Ref, long Size)> WriteAsync(string path, Stream data, long maxLength, Func<Exception>? tooLarge, CancellationToken ct)
    {
        var b = await blobs.WriteAsync(data, maxLength, tooLarge, ct);
        return (b.Id, b.Size);
    }

    public Stream OpenRead(string path, string? reference) =>
        reference != null ? blobs.OpenRead(reference) : new MemoryStream([], writable: false);

    public void Delete(string path, string? reference, bool isDir) => Release(reference);
    public void CreateFolder(string path) { }
    public void Move(string from, string to, bool isDir) { }
    public void Release(string? reference) { if (reference != null) blobs.Delete(reference); }
}

/// <summary>
/// Die Tresor-Dateien: Verzeichnis in der Tabelle files der Tresor-Datenbank (Kennung, Ordner, Name, Größe, Stand), Inhalt im
/// <see cref="ITresorStorage"/>. Alles, was Dateien im Tresor anlegt, liest, ersetzt, verschiebt oder löscht, geht hier durch -
/// Dateibrowser, Office, DMS, Bildbearbeitung, „Kopie aus dem Tresor“. Vorschaubilder bleiben im BlobStore des Tresors.
/// </summary>
public sealed class TresorFileStore(TresorDb db, ITresorStorage storage)
{
    public TresorDb Db => db;
    public ITresorStorage Storage => storage;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private const string Cols = "id, parent, name, is_dir, size, mime, blob, thumb_small, thumb_large, modified";

    private static TresorFiles.Entry Read(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3) != 0, r.GetInt64(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetInt64(9));

    public TresorFiles.Entry? Find(long id) =>
        db.Query($"SELECT {Cols} FROM files WHERE id = $i", Read, ("$i", id)).FirstOrDefault();

    public List<TresorFiles.Entry> Children(long parent) =>
        db.Query($"SELECT {Cols} FROM files WHERE parent = $p ORDER BY is_dir DESC, name COLLATE NOCASE", Read, ("$p", parent));

    public List<(long Id, long Parent, string Name)> Folders() =>
        db.Query("SELECT id, parent, name FROM files WHERE is_dir = 1 ORDER BY name COLLATE NOCASE", r => (r.GetInt64(0), r.GetInt64(1), r.GetString(2)));

    /// <summary>Pfad („Ordner/Unterordner/Name“), null, wenn es den Eintrag nicht gibt. Wurzel: "".</summary>
    public string? PathOf(long id)
    {
        if (id == 0) return "";
        var parts = new List<string>();
        for (var e = Find(id); e != null && parts.Count < 64; e = e.Parent == 0 ? null : Find(e.Parent)) parts.Insert(0, e.Name);
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static string Join(string parentPath, string name) => parentPath.Length == 0 ? name : parentPath + "/" + name;

    public void RequireFolder(long id)
    {
        if (id != 0 && Find(id) is not { IsDir: true }) throw new KeyNotFoundException("Diesen Ordner gibt es im Tresor nicht.");
    }

    /// <summary>Freier Name im Ordner: „Name (2).ext“ usw.</summary>
    public string FreeName(long parent, string name, long? self = null)
    {
        bool Taken(string n) => Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM files WHERE parent = $p AND name = $n COLLATE NOCASE AND id IS NOT $s",
            ("$p", parent), ("$n", n), ("$s", self)) ?? 0L) > 0;
        if (!Taken(name)) return name;
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var i = 2; ; i++) if (!Taken($"{stem} ({i}){ext}")) return $"{stem} ({i}){ext}";
    }

    /// <summary>In der obersten Ebene liegen die internen Daten (.dmoffice) - der Name ist dort vergeben.</summary>
    public const string Internal = ".dmoffice";

    private static string Allowed(long parent, string name)
    {
        var clean = TresorFiles.CleanName(name);
        if (parent == 0 && string.Equals(clean, Internal, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Dieser Name ist für interne Daten des Tresors vergeben.");
        return clean;
    }

    /// <summary>
    /// Schreibende Wege und der Abgleich mit dem Cryptomator-Tresor laufen nacheinander: sonst sähe ein Abgleich mitten in einem
    /// Verschieben die Datei im Tresor schon am neuen Ort, in der Tabelle noch am alten.
    /// </summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Ordner unter freiem Namen anlegen; gibt Id und Namen zurück.</summary>
    public (long Id, string Name) CreateFolder(long parent, string name)
    {
        gate.Wait();
        try { return CreateFolderLocked(parent, name); }
        finally { gate.Release(); }
    }

    private (long Id, string Name) CreateFolderLocked(long parent, string name)
    {
        RequireFolder(parent);
        var free = FreeName(parent, Allowed(parent, name));
        storage.CreateFolder(Join(PathOf(parent)!, free));
        var id = Convert.ToInt64(db.Scalar("INSERT INTO files(parent, name, is_dir, modified, created) VALUES($p, $n, 1, $t, $t) RETURNING id",
            ("$p", parent), ("$n", free), ("$t", Now())));
        return (id, free);
    }

    /// <summary>Ordner nach Pfad („Dokumente/Finanzamt/2025“), fehlende werden angelegt. Gibt die Id zurück (0 = Wurzel).</summary>
    public long EnsureFolder(string path)
    {
        long parent = 0;
        foreach (var raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = TresorFiles.CleanName(raw);
            var found = db.Scalar("SELECT id FROM files WHERE parent = $p AND name = $n COLLATE NOCASE AND is_dir = 1", ("$p", parent), ("$n", name));
            parent = found is long id ? id : CreateFolder(parent, name).Id;
        }
        return parent;
    }

    /// <summary>Neue Datei unter freiem Namen; gibt Id und den vergebenen Namen zurück.</summary>
    public async Task<(long Id, string Name)> CreateAsync(long parent, string name, Stream data, string? mime, CancellationToken ct,
        long maxLength = long.MaxValue, Func<Exception>? tooLarge = null, long? stamp = null)
    {
        await gate.WaitAsync(ct);
        try { return await CreateLockedAsync(parent, name, data, mime, ct, maxLength, tooLarge, stamp); }
        finally { gate.Release(); }
    }

    private async Task<(long Id, string Name)> CreateLockedAsync(long parent, string name, Stream data, string? mime, CancellationToken ct,
        long maxLength, Func<Exception>? tooLarge, long? stamp)
    {
        RequireFolder(parent);
        var free = FreeName(parent, Allowed(parent, name));
        var (reference, size) = await storage.WriteAsync(Join(PathOf(parent)!, free), data, maxLength, tooLarge, ct);
        var t = stamp ?? Now();
        var id = Convert.ToInt64(db.Scalar("INSERT INTO files(parent, name, is_dir, size, mime, blob, modified, created) VALUES($p, $n, 0, $s, $m, $b, $t, $t) RETURNING id",
            ("$p", parent), ("$n", free), ("$s", size), ("$m", mime), ("$b", reference), ("$t", t)));
        return (id, free);
    }

    /// <summary>Inhalt einer Datei ersetzen (Office, Bildbearbeitung): neuer Inhalt, Vorschaubilder weg, neuer Stand. Gibt den Stand zurück.</summary>
    public async Task<long> ReplaceAsync(long id, Stream data, string? mime, long? stamp, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await ReplaceLockedAsync(id, data, mime, stamp, ct); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Fixierte Dokumente im Tresor: Solange er offen ist, sperrt dmOffice Ändern und Löschen. Dateien eines Cryptomator-Tresors
    /// lassen sich außerhalb von dmOffice verändern; das kann diese Sperre nicht verhindern.
    /// </summary>
    private void GuardFixed(long id)
    {
        if (DmOffice.Dav.FileStore.InSystemAccess) return;
        if (db.Scalar("SELECT 1 FROM pragma_table_info('dms_docs') WHERE name = 'fixed'") == null) return;
        if (db.Scalar(@"WITH RECURSIVE tree(id) AS (SELECT $i UNION ALL SELECT f.id FROM files f JOIN tree t ON f.parent = t.id)
                SELECT 1 FROM dms_docs d JOIN tree t ON t.id = d.fileid WHERE d.fixed = 1 LIMIT 1", ("$i", id)) != null)
            throw new DmOffice.Dav.DavForbiddenException("Hier liegt ein fixiertes Dokument. Es lässt sich nicht ändern oder löschen, solange die Fixierung besteht.");
    }

    private async Task<long> ReplaceLockedAsync(long id, Stream data, string? mime, long? stamp, CancellationToken ct)
    {
        GuardFixed(id);
        var e = Find(id) is { IsDir: false } f ? f : throw new KeyNotFoundException("Diese Datei gibt es im Tresor nicht.");
        var (reference, size) = await storage.WriteAsync(PathOf(id)!, data, long.MaxValue, null, ct);
        var now = Math.Max(stamp ?? Now(), e.Modified + 1);
        db.Exec("UPDATE files SET blob = $b, size = $s, mime = COALESCE($m, mime), thumb_small = NULL, thumb_large = NULL, modified = $t WHERE id = $i",
            ("$b", reference), ("$s", size), ("$m", mime), ("$t", now), ("$i", id));
        if (e.Blob != reference) storage.Release(e.Blob);
        foreach (var t in new[] { e.ThumbSmall, e.ThumbLarge }) if (t != null) db.Blobs.Delete(t);
        return now;
    }

    public Stream OpenRead(long id) =>
        Find(id) is { IsDir: false } e ? storage.OpenRead(PathOf(id)!, e.Blob) : throw new KeyNotFoundException("Diese Datei gibt es im Tresor nicht.");

    public byte[] ReadAll(long id)
    {
        using var s = OpenRead(id);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>Liegt <paramref name="folder"/> in <paramref name="id"/> (Verschieben in sich selbst verhindern)?</summary>
    private bool Inside(long folder, long id)
    {
        for (var f = folder; f != 0; f = Find(f)?.Parent ?? 0) if (f == id) return true;
        return false;
    }

    /// <summary>Umbenennen und/oder verschieben; der Name wird frei gemacht. Gibt den vergebenen Namen zurück.</summary>
    public string Move(long id, long parent, string? newName)
    {
        gate.Wait();
        try { return MoveLocked(id, parent, newName); }
        finally { gate.Release(); }
    }

    private string MoveLocked(long id, long parent, string? newName)
    {
        var e = Find(id) ?? throw new KeyNotFoundException("Das gibt es im Tresor nicht.");
        RequireFolder(parent);
        if (e.IsDir && Inside(parent, id)) throw new ArgumentException("Ein Ordner kann nicht in sich selbst verschoben werden.");
        var name = FreeName(parent, newName != null ? Allowed(parent, newName) : Allowed(parent, e.Name), id);
        var from = PathOf(id)!;
        var to = Join(PathOf(parent)!, name);
        if (from != to) storage.Move(from, to, e.IsDir);
        db.Exec("UPDATE files SET parent = $p, name = $n, modified = $t WHERE id = $i", ("$p", parent), ("$n", name), ("$t", Now()), ("$i", id));
        return name;
    }

    /// <summary>Verschieben bzw. umbenennen nach Pfad (Ordner werden angelegt); gibt den neuen Pfad zurück.</summary>
    public string MoveTo(long id, string path)
    {
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? 0 : EnsureFolder(path[..slash]);
        Move(id, parent, path[(slash + 1)..]);
        return PathOf(id)!;
    }

    /// <summary>Datei oder Ordner samt Inhalt endgültig löschen (im Tresor gibt es keinen Papierkorb).</summary>
    public void Delete(long id)
    {
        gate.Wait();
        try
        {
            var e = Find(id) ?? throw new KeyNotFoundException("Das gibt es im Tresor nicht.");
            GuardFixed(id);
            storage.Delete(PathOf(id)!, e.Blob, e.IsDir);
            Forget(e);
        }
        finally { gate.Release(); }
    }

    // ---------------- Abgleich mit dem Cryptomator-Tresor ----------------

    /// <summary>
    /// Ordner mit dem Cryptomator-Tresor abgleichen: Was von außen hineinkam (Cryptomator über Nextcloud oder WebDAV), kommt in die
    /// Tabelle; was dort fehlt, fliegt aus der Tabelle (der Inhalt ist im Tresor schon weg); eine geänderte Größe gilt neu, die
    /// Vorschaubilder fallen weg. Die internen Daten (<see cref="Internal"/>) bleiben außen vor. Läuft gerade ein Schreiben, beim
    /// nächsten Mal. Verglichen wird nur die Größe: eine von außen geänderte Datei gleicher Größe behält Stand und Vorschaubilder.
    /// </summary>
    public void Reconcile(long dir)
    {
        if (storage is not CryptomatorTresorStorage cs) return;
        if (!gate.Wait(0)) return;
        try
        {
            if (PathOf(dir) is not { } path) return;
            var vault = cs.Vault;
            List<Core.Cryptomator.CryptomatorVault.Entry> inVault;
            try { inVault = vault.List("/" + path); }
            catch (DirectoryNotFoundException) { return; }
            if (dir == 0) inVault.RemoveAll(e => string.Equals(e.Name, Internal, StringComparison.OrdinalIgnoreCase));
            var names = inVault.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var rows = Children(dir);
            foreach (var r in rows.Where(r => !names.Contains(r.Name))) Forget(r);
            var known = rows.Where(r => names.Contains(r.Name)).ToDictionary(r => r.Name, StringComparer.Ordinal);
            foreach (var e in inVault)
            {
                var at = Join(path, e.Name);
                if (known.TryGetValue(e.Name, out var r))
                {
                    if (!e.IsDir && !r.IsDir && r.Size != e.Size)
                    {
                        db.Exec("UPDATE files SET size = $s, modified = $t, thumb_small = NULL, thumb_large = NULL WHERE id = $i",
                            ("$s", e.Size), ("$t", Math.Max(Millis(e.Modified), r.Modified + 1)), ("$i", r.Id));
                        foreach (var t in new[] { r.ThumbSmall, r.ThumbLarge }) if (t != null) db.Blobs.Delete(t);
                    }
                    continue;
                }
                // Gleicher Name in anderer Schreibweise schon da, oder über den Namen nicht erreichbar (Konfliktkopie): auslassen
                if (known.Keys.Any(k => string.Equals(k, e.Name, StringComparison.OrdinalIgnoreCase))) continue;
                if (e.IsDir ? vault.DirIdOf("/" + at) == null : vault.CipherFileOf("/" + at) == null) continue;
                var id = Convert.ToInt64(db.Scalar("INSERT INTO files(parent, name, is_dir, size, mime, modified, created) VALUES($p, $n, $d, $s, $m, $t, $t) RETURNING id",
                    ("$p", dir), ("$n", e.Name), ("$d", e.IsDir ? 1 : 0), ("$s", e.IsDir ? 0 : e.Size), ("$m", e.IsDir ? null : DavServer.Mime(e.Name)), ("$t", Millis(e.Modified))));
                known[e.Name] = Find(id)!;
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Kennung eines Ordners nach Pfad; auf dem Weg dorthin wird jeder Ordner abgeglichen (<see cref="Reconcile"/>). Null: gibt es nicht.</summary>
    public long? ReconciledFolder(string rel)
    {
        long id = 0;
        Reconcile(0);
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Children(id).FirstOrDefault(c => c.IsDir && string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            id = next.Id;
            Reconcile(id);
        }
        return id;
    }

    private static long Millis(DateTime t) =>
        new DateTimeOffset(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime()).ToUnixTimeMilliseconds();

    /// <summary>Zeilen (und Blobs des Speichers ohne Baum, Vorschaubilder) unterhalb entfernen; der Speicher hat den Baum schon gelöscht.</summary>
    private void Forget(TresorFiles.Entry e)
    {
        if (e.IsDir) foreach (var c in Children(e.Id)) { if (!c.IsDir) storage.Release(c.Blob); Forget(c); }
        foreach (var t in new[] { e.ThumbSmall, e.ThumbLarge }) if (t != null) db.Blobs.Delete(t);
        db.Exec("DELETE FROM files WHERE id = $i", ("$i", e.Id));
    }

    /// <summary>Gespeichertes Vorschaubild (WebP), null, wenn es keins gibt.</summary>
    public byte[]? Thumb(TresorFiles.Entry e, bool small)
    {
        var stored = small ? e.ThumbSmall : e.ThumbLarge;
        return stored != null && db.Blobs.Exists(stored) ? db.Blobs.ReadAll(stored) : null;
    }

    public void SetThumb(long id, bool small, byte[] webp)
    {
        var t = db.Blobs.Write(webp);
        db.Exec($"UPDATE files SET {(small ? "thumb_small" : "thumb_large")} = $b WHERE id = $i", ("$b", t.Id), ("$i", id));
    }
}
