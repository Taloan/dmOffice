using System.Text;

namespace DmOffice.Core.Cryptomator;

/// <summary>
/// Cryptomator-Tresor (Vault-Format 8) über <see cref="IVaultFiles"/> - ein Ordner auf der Platte oder der System-Tresor in der
/// Dateiablage (docs/tresor-cryptomator-konzept.md). Pfade im Klartext mit „/“, Wurzel = "".
/// </summary>
public sealed class CryptomatorVault : IDisposable
{
    public IVaultFiles Store { get; }
    public Masterkey Key { get; }
    public int ShorteningThreshold { get; }
    private readonly bool _ownsKey;

    private CryptomatorVault(IVaultFiles store, Masterkey key, int threshold, bool ownsKey)
    {
        Store = store;
        Key = key;
        ShorteningThreshold = threshold;
        _ownsKey = ownsKey;
    }

    public sealed record Entry(string Name, bool IsDir, long Size, DateTime Modified);

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Neuen Tresor anlegen: vault.cryptomator, masterkey.cryptomator (mit Passwort), Wurzelordner samt dirid.c9r.</summary>
    /// <param name="password">Ohne: noch kein masterkey.cryptomator (dmOffice hat den Masterkey selbst; die Datei kommt mit
    /// <see cref="SetPassword"/>, sobald das Passwort bekannt ist)</param>
    public static CryptomatorVault Create(IVaultFiles store, Masterkey key, string? password, int? scryptCost = null, bool ownsKey = true)
    {
        if (store.FileExists(VaultConfig.FileName)) throw new InvalidOperationException("Dort liegt schon ein Tresor.");
        if (password != null) WriteSync(store, MasterkeyFile.FileName, MasterkeyFile.Write(key, password, scryptCost));
        WriteSync(store, VaultConfig.FileName, Encoding.ASCII.GetBytes(VaultConfig.Write(key)));
        var v = new CryptomatorVault(store, key, VaultConfig.DefaultShorteningThreshold, ownsKey);
        var rootDir = VaultNames.DirPath(key, VaultNames.RootDirId);
        store.CreateDir(rootDir);
        // Auch die Wurzel bekommt dirid.c9r (leere Kennung), wie bei cryptofs
        WriteSync(store, rootDir + "/" + VaultNames.DirIdBackup, FileContent.Encrypt(key, []));
        return v;
    }

    /// <summary>Öffnen mit dem Passwort; null, wenn es nicht stimmt.</summary>
    public static CryptomatorVault? Open(IVaultFiles store, string password)
    {
        var key = MasterkeyFile.Read(store.ReadAll(MasterkeyFile.FileName), password);
        return key == null ? null : Open(store, key, ownsKey: true);
    }

    /// <summary>Öffnen mit bekanntem Masterkey (dmOffice verwahrt ihn selbst).</summary>
    public static CryptomatorVault Open(IVaultFiles store, Masterkey key, bool ownsKey = false)
    {
        var cfg = VaultConfig.Read(Encoding.ASCII.GetString(store.ReadAll(VaultConfig.FileName)), key);
        return new CryptomatorVault(store, key, cfg.ShorteningThreshold, ownsKey);
    }

    /// <summary>masterkey.cryptomator mit neuem Passwort schreiben (Tresor-Passwort geändert).</summary>
    public void SetPassword(string password, int? scryptCost = null) => WriteSync(Store, MasterkeyFile.FileName, MasterkeyFile.Write(Key, password, scryptCost));

    private static void WriteSync(IVaultFiles store, string rel, byte[] data) => store.WriteAsync(rel, new MemoryStream(data)).GetAwaiter().GetResult();

    private static string[] Split(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => p is "." or ".." ? throw new ArgumentException("Ungültiger Pfad.") : p).ToArray();

    /// <summary>Ort eines Eintrags im Ordner der Kennung: Ordner bzw. Datei mit .c9r oder (lang) .c9s.</summary>
    private (string Node, string Encrypted, bool Short) NodeOf(string parentDirId, string name)
    {
        var enc = VaultNames.Encrypt(Key, name, parentDirId);
        var shortName = VaultNames.Shorten(enc, ShorteningThreshold);
        return (VaultNames.DirPath(Key, parentDirId) + "/" + (shortName ?? enc), enc, shortName != null);
    }

    private string ReadDirId(string node) => Encoding.UTF8.GetString(Store.ReadAll(node + "/" + VaultNames.DirFile)).Trim();

    /// <summary>Kennung eines Ordners; null, wenn es ihn nicht gibt.</summary>
    public string? DirIdOf(string path)
    {
        var id = VaultNames.RootDirId;
        foreach (var part in Split(path))
        {
            var (node, _, _) = NodeOf(id, part);
            if (!Store.FileExists(node + "/" + VaultNames.DirFile)) return null;
            id = ReadDirId(node);
        }
        return id;
    }

    private (string ParentId, string Name) Parent(string path)
    {
        var parts = Split(path);
        if (parts.Length == 0) throw new ArgumentException("Die Wurzel hat keinen Namen.");
        var parentId = DirIdOf(string.Join('/', parts[..^1])) ?? throw new DirectoryNotFoundException("Den Ordner gibt es im Tresor nicht.");
        return (parentId, parts[^1]);
    }

    /// <summary>Verschlüsselte Datei einer Klartext-Datei (bei langen Namen contents.c9r im .c9s-Ordner).</summary>
    /// <summary>Der verschlüsselte Eintrag (Datei oder Ordner) dieses Pfads im Tresor, relativ zur Tresor-Wurzel.</summary>
    public string NodeFor(string path)
    {
        var (parentId, name) = Parent(path);
        return NodeOf(parentId, name).Node;
    }

    public string? CipherFileOf(string path)
    {
        var (parentId, name) = Parent(path);
        var (node, _, isShort) = NodeOf(parentId, name);
        var file = isShort ? node + "/" + VaultNames.ContentsFile : node;
        return Store.FileExists(file) ? file : null;
    }

    public bool IsDirectory(string path) => Split(path).Length == 0 || DirIdOf(path) != null;

    public List<Entry> List(string path)
    {
        var dirId = DirIdOf(path) ?? throw new DirectoryNotFoundException("Den Ordner gibt es im Tresor nicht.");
        var dir = VaultNames.DirPath(Key, dirId);
        var result = new List<Entry>();
        foreach (var (nodeName, isDirNode, size, modified) in Store.List(dir))
        {
            if (nodeName == VaultNames.DirIdBackup) continue;
            var node = dir + "/" + nodeName;
            string encrypted;
            var cipherFile = node;
            long cipherSize = size;
            if (isDirNode && nodeName.EndsWith(VaultNames.ShortSuffix, StringComparison.Ordinal))
            {
                if (!Store.FileExists(node + "/" + VaultNames.NameFile)) continue;
                encrypted = Encoding.UTF8.GetString(Store.ReadAll(node + "/" + VaultNames.NameFile)).Trim();
                cipherFile = node + "/" + VaultNames.ContentsFile;
                cipherSize = -1;
            }
            else if (nodeName.EndsWith(VaultNames.Suffix, StringComparison.Ordinal)) encrypted = nodeName;
            else continue;   // Fremdes und Zwischendateien
            if (VaultNames.Decrypt(Key, encrypted, dirId) is not { } dec) continue;
            var name = dec.Extra == null ? dec.Name : ConflictName(dec.Name);
            if (isDirNode && Store.FileExists(node + "/" + VaultNames.DirFile)) result.Add(new Entry(name, true, 0, modified));
            else if (!isDirNode || Store.FileExists(cipherFile))
            {
                if (cipherSize < 0) cipherSize = Store.List(node).FirstOrDefault(x => x.Name == VaultNames.ContentsFile).Size;
                result.Add(new Entry(name, false, FileContent.CleartextSize(cipherSize), modified));
            }
        }
        return result;
    }

    /// <summary>
    /// Verschlüsselte Dateien, die eine Offline-Kopie von <paramref name="path"/> braucht (Stufe 7b, App ohne Netz): Tresor-Dateien,
    /// auf dem Weg dorthin je der Eintrag des nächsten Ordners, darunter alles. In der Wurzel ohne <paramref name="skipAtRoot"/>.
    /// Null, wenn es den Ordner nicht gibt.
    /// </summary>
    public List<string>? OfflineFiles(string path, string? skipAtRoot = null)
    {
        var files = new List<string> { MasterkeyFile.FileName, VaultConfig.FileName };
        void Node(string node)
        {
            foreach (var (n, isDir, _, _) in Store.List(node)) if (!isDir) files.Add(node + "/" + n);
        }
        var id = VaultNames.RootDirId;
        foreach (var part in Split(path))
        {
            var (node, _, _) = NodeOf(id, part);
            if (!Store.FileExists(node + "/" + VaultNames.DirFile)) return null;
            Node(node);
            id = ReadDirId(node);
        }
        var skip = skipAtRoot != null && Split(path).Length == 0 ? NodeOf(VaultNames.RootDirId, skipAtRoot).Node : null;
        void Tree(string dirId)
        {
            var dir = VaultNames.DirPath(Key, dirId);
            foreach (var (n, isDir, _, _) in Store.List(dir))
            {
                var node = dir + "/" + n;
                if (node == skip || n == VaultNames.DirIdBackup) continue;
                if (!isDir) { files.Add(node); continue; }
                Node(node);
                if (Store.FileExists(node + "/" + VaultNames.DirFile)) Tree(ReadDirId(node));
            }
        }
        Tree(id);
        return files;
    }

    /// <summary>Konfliktkopie: „Name (Konflikt).ext“, wie bei Office im Tresor.</summary>
    private static string ConflictName(string name)
    {
        var ext = Path.GetExtension(name);
        return name[..^ext.Length] + " (Konflikt)" + ext;
    }

    private bool NodeExists(string node) => Store.FileExists(node) || Store.DirExists(node);

    public void CreateDirectory(string path)
    {
        var (parentId, name) = Parent(path);
        var (node, enc, isShort) = NodeOf(parentId, name);
        if (NodeExists(node)) throw new IOException("Diesen Namen gibt es dort schon.");
        var dirId = Guid.NewGuid().ToString();
        Store.CreateDir(node);
        if (isShort) WriteSync(Store, node + "/" + VaultNames.NameFile, Utf8.GetBytes(enc));
        WriteSync(Store, node + "/" + VaultNames.DirFile, Utf8.GetBytes(dirId));
        var content = VaultNames.DirPath(Key, dirId);
        Store.CreateDir(content);
        WriteSync(Store, content + "/" + VaultNames.DirIdBackup, FileContent.Encrypt(Key, Encoding.UTF8.GetBytes(dirId)));
    }

    /// <summary>Datei schreiben (neu oder ersetzen), gestreamt; der Speicher legt sie erst ganz an ihren Platz.</summary>
    public async Task WriteAsync(string path, Stream content, CancellationToken ct = default)
    {
        var (parentId, name) = Parent(path);
        var (node, enc, isShort) = NodeOf(parentId, name);
        string target;
        if (isShort)
        {
            if (Store.FileExists(node + "/" + VaultNames.DirFile)) throw new IOException("Dort liegt ein Ordner.");
            if (!Store.DirExists(node)) Store.CreateDir(node);
            if (!Store.FileExists(node + "/" + VaultNames.NameFile)) WriteSync(Store, node + "/" + VaultNames.NameFile, Utf8.GetBytes(enc));
            target = node + "/" + VaultNames.ContentsFile;
        }
        else
        {
            if (Store.DirExists(node)) throw new IOException("Dort liegt ein Ordner.");
            target = node;
        }
        await using var encrypting = new EncryptingStream(Key, content);
        await Store.WriteAsync(target, encrypting, ct);
    }

    public Stream OpenRead(string path)
    {
        var file = CipherFileOf(path) ?? throw new FileNotFoundException("Diese Datei gibt es im Tresor nicht.");
        return new DecryptingStream(Key, Store.OpenRead(file));
    }

    /// <summary>Datei oder Ordner samt Inhalt endgültig löschen.</summary>
    public void Delete(string path)
    {
        var (parentId, name) = Parent(path);
        var (node, _, _) = NodeOf(parentId, name);
        if (Store.FileExists(node + "/" + VaultNames.DirFile)) DeleteContent(ReadDirId(node));
        Store.Delete(node);
    }

    /// <summary>Inhalt eines Ordners (nach Kennung) samt Unterordnern entfernen.</summary>
    private void DeleteContent(string dirId)
    {
        var dir = VaultNames.DirPath(Key, dirId);
        foreach (var (nodeName, isDirNode, _, _) in Store.List(dir))
            if (isDirNode && Store.FileExists(dir + "/" + nodeName + "/" + VaultNames.DirFile)) DeleteContent(ReadDirId(dir + "/" + nodeName));
        Store.Delete(dir);
    }

    /// <summary>Umbenennen oder verschieben. Der Inhalt bleibt, nur der verschlüsselte Name wird neu (ein Ordner behält seine Kennung).</summary>
    public void Move(string from, string to)
    {
        var (fromParent, fromName) = Parent(from);
        var (toParent, toName) = Parent(to);
        var (oldNode, _, oldShort) = NodeOf(fromParent, fromName);
        var (newNode, newEnc, newShort) = NodeOf(toParent, toName);
        if (oldNode == newNode) return;
        if (NodeExists(newNode)) throw new IOException("Diesen Namen gibt es dort schon.");
        var isDir = Store.FileExists(oldNode + "/" + VaultNames.DirFile);
        if (!oldShort && !newShort) { Store.Move(oldNode, newNode); return; }
        if (oldShort && newShort)
        {
            Store.Move(oldNode, newNode);
            WriteSync(Store, newNode + "/" + VaultNames.NameFile, Utf8.GetBytes(newEnc));
            return;
        }
        // Zwischen kurzer und langer Form: Nutzlast (dir.c9r bzw. Inhalt) umhängen
        var payloadOld = isDir ? oldNode + "/" + VaultNames.DirFile : oldShort ? oldNode + "/" + VaultNames.ContentsFile : oldNode;
        if (newShort)
        {
            Store.CreateDir(newNode);
            WriteSync(Store, newNode + "/" + VaultNames.NameFile, Utf8.GetBytes(newEnc));
            Store.Move(payloadOld, newNode + "/" + (isDir ? VaultNames.DirFile : VaultNames.ContentsFile));
        }
        else if (isDir)
        {
            Store.CreateDir(newNode);
            Store.Move(payloadOld, newNode + "/" + VaultNames.DirFile);
        }
        else Store.Move(payloadOld, newNode);
        Store.Delete(oldNode);
    }

    public void Dispose() { if (_ownsKey) Key.Dispose(); }
}

/// <summary>Kurzform für einen Tresor in einem Ordner auf der Platte.</summary>
public static class LocalVault
{
    public static CryptomatorVault Create(string root, Masterkey key, string password, int? scryptCost = null)
    {
        Directory.CreateDirectory(root);
        return CryptomatorVault.Create(new DiskVaultFiles(root), key, password, scryptCost);
    }

    public static CryptomatorVault? Open(string root, string password) => CryptomatorVault.Open(new DiskVaultFiles(root), password);
}
