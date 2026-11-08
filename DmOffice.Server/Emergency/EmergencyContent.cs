using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Emergency;

/// <summary>
/// Verschlüsselter Inhalt der Notfallpakete: Notfalltext und Referenzkopien unter data/emergency/&lt;Paket&gt;/. Schreiben
/// geht immer (öffentlicher Schlüssel des Pakets), Lesen nur mit dem privaten - für den Besitzer aus dem Tresor, für die
/// Vertrauensperson aus Notfallcode und Hälfte des Servers nach Ablauf der Frist.
/// </summary>
public sealed class EmergencyContent(DataPaths paths, EmergencyStore store, Spaces spaces)
{
    /// <summary>Größte Datei für eine Referenzkopie (sie wird zum Verschlüsseln ganz gelesen).</summary>
    public const long MaxFile = 256L * 1024 * 1024;
    /// <summary>Notfalltext höchstens so groß (Bilder stehen darin als data:-Adressen).</summary>
    public const int MaxText = 32 * 1024 * 1024;
    /// <summary>Im Tresor: Paketschlüssel und Notfallcode des Besitzers (kv „emergency.&lt;Paket&gt;“).</summary>
    public static string TresorKey(string package) => "emergency." + package;

    private string Dir(string package) => Path.Combine(paths.Root, "emergency", package);

    // ---------------- Schlüssel ----------------

    /// <summary>Schlüssel anlegen (einmal je Paket) - Paketschlüssel und Notfallcode kommen in den Tresor des Besitzers.</summary>
    public string CreateKeys(TresorDb tresor, string owner, string package)
    {
        var p = store.Package(package);
        if (p == null || p.Owner != owner) throw new KeyNotFoundException("Notfallpaket nicht gefunden.");
        if (p.PublicKey != null) throw new InvalidOperationException("Das Paket hat schon einen Notfallcode.");
        var k = EmergencyVault.Create();
        tresor.Exec("INSERT OR REPLACE INTO kv(key, value) VALUES($k, $v)", ("$k", TresorKey(package)),
            ("$v", System.Text.Json.JsonSerializer.Serialize(new OwnerKey(Convert.ToBase64String(k.PackageKey), k.Code))));
        store.Change(owner, package, x =>
        {
            x.PublicKey = Convert.ToBase64String(k.PublicKey); x.SealedPrivate = Convert.ToBase64String(k.SealedPrivate);
            x.ServerHalf = Convert.ToBase64String(k.ServerHalf); x.Salt = Convert.ToBase64String(k.Salt);
        }, "Notfallcode erzeugt");
        return EmergencyVault.Format(k.Code);
    }

    public sealed record OwnerKey(string Key, string Code);

    /// <summary>Was der Besitzer im Tresor hat - null: kein Schlüssel (oder im Tresor nicht mehr da).</summary>
    public static OwnerKey? OwnerKeyOf(TresorDb tresor, string package)
    {
        var v = tresor.Query("SELECT value FROM kv WHERE key = $k", r => r.GetString(0), ("$k", TresorKey(package))).FirstOrDefault();
        return v == null ? null : System.Text.Json.JsonSerializer.Deserialize<OwnerKey>(v);
    }

    /// <summary>Privater Schlüssel für den Besitzer (Tresor offen).</summary>
    public byte[] PrivateForOwner(TresorDb tresor, EmergencyPackage p)
    {
        if (p.SealedPrivate == null || OwnerKeyOf(tresor, p.Id) is not { } k) throw new InvalidOperationException("Für dieses Paket ist im Tresor kein Schlüssel hinterlegt.");
        return EmergencyVault.PrivateKey(Convert.FromBase64String(k.Key), Convert.FromBase64String(p.SealedPrivate));
    }

    /// <summary>Privater Schlüssel aus dem Notfallcode - null: Code falsch.</summary>
    public static byte[]? PrivateFromCode(EmergencyPackage p, string code)
    {
        if (p.SealedPrivate == null || p.ServerHalf == null || p.Salt == null) return null;
        var key = EmergencyVault.PackageKey(code, Convert.FromBase64String(p.ServerHalf), Convert.FromBase64String(p.Salt));
        try { return EmergencyVault.PrivateKey(key, Convert.FromBase64String(p.SealedPrivate)); }
        catch (CryptographicException) { return null; }
    }

    // ---------------- Notfalltext ----------------

    public void WriteText(string owner, EmergencyPackage p, string html)
    {
        if (p.PublicKey == null) throw new InvalidOperationException("Das Paket hat noch keinen Notfallcode.");
        var bytes = Encoding.UTF8.GetBytes(html ?? "");
        if (bytes.Length > MaxText) throw new ArgumentException("Der Notfalltext ist zu groß (Bilder verkleinern).");
        var has = (html ?? "").Trim().Length > 0;
        Directory.CreateDirectory(Dir(p.Id));
        var path = Path.Combine(Dir(p.Id), "text.bin");
        if (has) Atomic(path, EmergencyVault.Seal(Convert.FromBase64String(p.PublicKey), bytes)); else File.Delete(path);
        store.Change(owner, p.Id, x => { x.HasText = has; x.TextChanged = DateTime.UtcNow; }, "Notfalltext gespeichert");
    }

    public string ReadText(EmergencyPackage p, byte[] privateKey)
    {
        var path = Path.Combine(Dir(p.Id), "text.bin");
        return File.Exists(path) ? Encoding.UTF8.GetString(EmergencyVault.Open(privateKey, File.ReadAllBytes(path))) : "";
    }

    // ---------------- Referenzkopien ----------------

    /// <summary>Datei aus der Ablage des Besitzers als Kopie ins Paket.</summary>
    public EmergencyFile AddFile(string owner, string package, long fileId)
    {
        var p = store.Package(package) ?? throw new KeyNotFoundException("Notfallpaket nicht gefunden.");
        if (p.Owner != owner) throw new KeyNotFoundException("Notfallpaket nicht gefunden.");
        if (p.PublicKey == null) throw new InvalidOperationException("Das Paket hat noch keinen Notfallcode.");
        if (p.Files.Any(f => f.Source == fileId)) throw new InvalidOperationException("Diese Datei ist schon im Paket.");
        var db = spaces.Get(owner).Db;
        var e = FileStore.ById(db, fileId);
        if (e == null || e.IsDir || e.Path.StartsWith(FileStore.SystemVault, StringComparison.Ordinal)) throw new KeyNotFoundException("Diese Datei gibt es nicht.");
        var f = new EmergencyFile { Id = Guid.NewGuid().ToString("N")[..12], Source = fileId };
        Copy(p, db, e, f);
        store.Change(owner, package, x => x.Files.Add(f), $"Datei „{f.Name}“ aufgenommen");
        return f;
    }

    public void RemoveFile(string owner, string package, string id)
    {
        var p = store.Change(owner, package, x => x.Files.RemoveAll(f => f.Id == id), "Datei entfernt");
        File.Delete(Path.Combine(Dir(package), "files", id + ".bin"));
    }

    /// <summary>
    /// Kopien auf den Stand der Herkunft bringen: geändert (anderes ETag) oder ersetzt (gleicher Pfad, neue fileid) - neu
    /// kopieren; gelöscht oder verschoben - die letzte Kopie bleibt, das Paket zeigt einen Hinweis.
    /// </summary>
    public EmergencyPackage Refresh(EmergencyPackage p)
    {
        if (p.PublicKey == null || p.Files.Count == 0) return p;
        var db = spaces.Get(p.Owner).Db;
        var changed = new List<EmergencyFile>();
        foreach (var f in p.Files)
        {
            var e = FileStore.ById(db, f.Source) is { IsDir: false } byId ? byId : FileStore.Get(db, f.Path) is { IsDir: false } byPath ? byPath : null;
            if (e == null) { if (!f.Missing) { f.Missing = true; changed.Add(f); } continue; }
            if (e.FileId == f.Source && e.Etag == f.Etag && !f.Missing) continue;
            try { Copy(p, db, e, f); f.Source = e.FileId; changed.Add(f); }
            catch (Exception ex) when (ex is IOException or ArgumentException) { /* beim nächsten Mal */ }
        }
        if (changed.Count == 0) return p;
        return store.Change(p.Owner, p.Id, x =>
        {
            foreach (var c in changed) if (x.Files.FindIndex(y => y.Id == c.Id) is var i and >= 0) x.Files[i] = c;
        }, changed.All(c => c.Missing) ? null : "Kopie erneuert: " + string.Join(", ", changed.Where(c => !c.Missing).Select(c => c.Name)));
    }

    public byte[] ReadFile(EmergencyPackage p, string id, byte[] privateKey)
    {
        if (!p.Files.Any(f => f.Id == id)) throw new KeyNotFoundException("Diese Datei gibt es im Paket nicht.");
        return EmergencyVault.Open(privateKey, File.ReadAllBytes(Path.Combine(Dir(p.Id), "files", id + ".bin")));
    }

    /// <summary>Paket gelöscht: der verschlüsselte Inhalt auch.</summary>
    public void Forget(string package)
    {
        try { if (Directory.Exists(Dir(package))) Directory.Delete(Dir(package), true); } catch (IOException) { }
    }

    private void Copy(EmergencyPackage p, UserDb db, FileEntry e, EmergencyFile f)
    {
        if (e.Size > MaxFile) throw new ArgumentException($"„{e.Name}“ ist zu groß für eine Kopie im Notfallpaket (höchstens {MaxFile / 1024 / 1024} MB).");
        byte[] data;
        using (var s = FileStore.OpenRead(db, e.Path)) { var m = new MemoryStream(); s.CopyTo(m); data = m.ToArray(); }
        var dir = Path.Combine(Dir(p.Id), "files");
        Directory.CreateDirectory(dir);
        Atomic(Path.Combine(dir, f.Id + ".bin"), EmergencyVault.Seal(Convert.FromBase64String(p.PublicKey!), data));
        f.Path = e.Path; f.Name = e.Name; f.Size = e.Size; f.Etag = e.Etag; f.Copied = DateTime.UtcNow; f.Missing = false;
    }

    private static void Atomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}
