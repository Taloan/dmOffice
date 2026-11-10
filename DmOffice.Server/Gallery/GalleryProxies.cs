using System.Security.Cryptography;
using DmOffice.Core;

namespace DmOffice.Gallery;

/// <summary>
/// Große Vorschauen (2048 px) der Galerie-Ordner als Zwischenspeicher: verschlüsselte Dateien unter data/cache/gallery/&lt;Benutzer&gt;/
/// &lt;Quelle&gt;/ - bei Server-Ordnern (vom Admin freigegeben, für alle dieselben Dateien) einmal für alle unter
/// data/cache/gallery/_shared/&lt;Ordner&gt;/ (der Pfad ist dann relativ zum Server-Ordner, also für alle gleich). Nicht in der Datenbank und nicht in der Sicherung - alles lässt sich aus den Originalen neu machen, der Ordner darf
/// jederzeit weg. Damit öffnet ein Foto sofort, auch wenn das Original langsam (Tunnel, WebDAV) oder gerade nicht erreichbar ist.
/// Datei: Stand (8 Byte, Größe ^ Änderungszeit des Originals - ändert es sich, gilt die Vorschau nicht mehr), Nonce (12), Tag (16),
/// verschlüsseltes WebP (AES-GCM mit einem aus dem Hauptschlüssel abgeleiteten Schlüssel).
/// </summary>
public sealed class GalleryProxies
{
    public const int Size = 2048;
    /// <summary>Erste Ebene für Server-Ordner statt der Benutzerkennung.</summary>
    public const string SharedScope = "_shared";
    private readonly string _root;
    private readonly byte[] _key;

    public GalleryProxies(DataPaths paths, Vault vault) : this(Path.Combine(paths.Root, "cache", "gallery"), vault.Key) { }

    internal GalleryProxies(string root, byte[] master)
    {
        _root = root;
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "dmoffice gallery proxy v1"u8.ToArray());
    }

    /// <summary>Stand des Originals: Größe und Änderungszeit in einer Zahl.</summary>
    public static long Stamp(long size, long modified) => size ^ (modified << 1);

    private string FileOf(string user, string source, string path) =>
        Path.Combine(_root, user, source, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..40].ToLowerInvariant() + ".bin");

    /// <summary>Gibt es eine gültige Vorschau (gleicher Stand)? Liest nur die ersten 8 Byte.</summary>
    public bool Has(string user, string source, string path, long stamp)
    {
        var f = FileOf(user, source, path);
        if (!File.Exists(f)) return false;
        try
        {
            using var s = File.OpenRead(f);
            Span<byte> head = stackalloc byte[8];
            return s.Read(head) == 8 && BitConverter.ToInt64(head) == stamp;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Die Vorschau, wenn sie zum Stand passt und sich entschlüsseln lässt - sonst null.</summary>
    public byte[]? Get(string user, string source, string path, long stamp)
    {
        var f = FileOf(user, source, path);
        byte[] all;
        try { all = File.ReadAllBytes(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (all.Length < 8 + 12 + 16 || BitConverter.ToInt64(all, 0) != stamp) return null;
        var plain = new byte[all.Length - 36];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(all.AsSpan(8, 12), all.AsSpan(36), all.AsSpan(20, 16), plain, all.AsSpan(0, 8));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Ablegen (über eine Zwischendatei - halb geschriebene Dateien gibt es nicht).</summary>
    public void Put(string user, string source, string path, long stamp, byte[] webp)
    {
        var f = FileOf(user, source, path);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var all = new byte[36 + webp.Length];
        BitConverter.TryWriteBytes(all.AsSpan(0, 8), stamp);
        RandomNumberGenerator.Fill(all.AsSpan(8, 12));
        using (var aes = new AesGcm(_key, 16))
            aes.Encrypt(all.AsSpan(8, 12), webp, all.AsSpan(36), all.AsSpan(20, 16), all.AsSpan(0, 8));
        var tmp = f + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(tmp, all);
        File.Move(tmp, f, overwrite: true);
    }

    /// <summary>Größe der abgelegten Vorschau in Byte, 0 wenn keine da ist.</summary>
    public long SizeOf(string user, string source, string path)
    {
        try { var f = new FileInfo(FileOf(user, source, path)); return f.Exists ? f.Length : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public void Remove(string user, string source, string path)
    {
        try { File.Delete(FileOf(user, source, path)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Galerie-Ordner entfernt oder Vorschauen abgeschaltet: alle seine Vorschauen weg.</summary>
    public void RemoveSource(string user, string source)
    {
        try { var d = Path.Combine(_root, user, source); if (Directory.Exists(d)) Directory.Delete(d, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Vorschauen, die früher je Benutzer lagen, in den gemeinsamen Bereich eines Server-Ordners übernehmen (umbenennen, nichts
    /// neu rechnen); was dort schon liegt, bleibt. Danach ist der alte Ordner weg.
    /// </summary>
    public int Adopt(string user, string source, string mount)
    {
        var from = Path.Combine(_root, user, source);
        if (!Directory.Exists(from)) return 0;
        var to = Path.Combine(_root, SharedScope, mount);
        var moved = 0;
        try
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.EnumerateFiles(from, "*.bin"))
            {
                var dest = Path.Combine(to, Path.GetFileName(f));
                try { if (!File.Exists(dest)) { File.Move(f, dest); moved++; } }
                catch (IOException) { }
            }
            Directory.Delete(from, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return moved;
    }

    /// <summary>Server-Ordner mit gemeinsamen Vorschauen (Ordnernamen unter _shared).</summary>
    public List<string> SharedMounts()
    {
        var d = Path.Combine(_root, SharedScope);
        return Directory.Exists(d) ? Directory.GetDirectories(d).Select(x => Path.GetFileName(x)!).ToList() : [];
    }

    /// <summary>Server-Ordner nicht mehr freigegeben: seine gemeinsamen Vorschauen weg.</summary>
    public void RemoveShared(string mount) => RemoveSource(SharedScope, mount);

    /// <summary>Belegter Platz eines Galerie-Ordners (für die Einstellungen).</summary>
    public (int Count, long Bytes) Usage(string user, string source)
    {
        var d = Path.Combine(_root, user, source);
        if (!Directory.Exists(d)) return (0, 0);
        var files = new DirectoryInfo(d).GetFiles("*.bin");
        return (files.Length, files.Sum(x => x.Length));
    }
}
