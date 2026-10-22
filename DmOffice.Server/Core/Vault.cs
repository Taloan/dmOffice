using System.Security.Cryptography;
using System.Text;

namespace DmOffice.Core;

/// <summary>
/// Der Hauptschluessel. Er packt die Schluessel der Benutzerdatenbanken ein;
/// selbst liegt er nicht neben den Daten, sondern kommt aus der Umgebung
/// (DMOFFICE_MASTER_KEY, in Unraid als maskierte Variable). Fehlt sie, legt
/// dmOffice ihn als Datei im Datenverzeichnis an - dann schuetzt die
/// Verschluesselung nur noch Kopien einzelner Datenbanken, nicht das ganze
/// Verzeichnis. Die Verwaltung zeigt das an.
///
/// Wird die Variable spaeter gesetzt, packt der naechste Start alle
/// Benutzerschluessel neu ein und loescht die Datei.
/// </summary>
public sealed class Vault
{
    private const string Salt = "dmOffice.master.v1";

    public byte[] Key { get; }
    public bool KeyInDataDir { get; }
    /// <summary>Der fruehere Schluessel aus der Datei, wenn jetzt die
    /// Variable gilt - fuer das einmalige Umpacken.</summary>
    public byte[]? Previous { get; }
    public string KeyFile { get; }

    public Vault(DataPaths paths, string? environmentKey)
    {
        KeyFile = Path.Combine(paths.Root, "master.key");
        byte[]? fileKey = File.Exists(KeyFile) ? Convert.FromHexString(File.ReadAllText(KeyFile).Trim()) : null;

        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            Key = Rfc2898DeriveBytes.Pbkdf2(environmentKey.Trim(), Encoding.UTF8.GetBytes(Salt), 200_000, HashAlgorithmName.SHA256, 32);
            Previous = fileKey;
            KeyInDataDir = false;
        }
        else
        {
            if (fileKey == null)
            {
                fileKey = RandomNumberGenerator.GetBytes(32);
                File.WriteAllText(KeyFile, Convert.ToHexString(fileKey));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            Key = fileKey;
            KeyInDataDir = true;
        }
    }

    public string Wrap(byte[] secret) => Wrap(Key, secret);

    public byte[] Unwrap(string wrapped) => Unwrap(Key, wrapped);

    public static string Wrap(byte[] key, byte[] secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[secret.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, secret, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(cipher).Concat(tag).ToArray());
    }

    public static byte[] Unwrap(byte[] key, string wrapped)
    {
        var data = Convert.FromBase64String(wrapped);
        var nonce = data[..12];
        var tag = data[^16..];
        var cipher = data[12..^16];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

public sealed class DataPaths
{
    public string Root { get; }

    /// <summary>Inhalte der Dateiablage (BlobStore), je Benutzer ein Unterordner - Vorgabe &lt;Daten&gt;/blobs,
    /// mit DMOFFICE_BLOBS auch anderswo (eigenes Volume auf dem Array).</summary>
    public string Blobs { get; }

    public DataPaths(string root, string? blobs = null)
    {
        Root = Path.GetFullPath(root);
        Blobs = Path.GetFullPath(string.IsNullOrWhiteSpace(blobs) ? Path.Combine(Root, "blobs") : blobs);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Users);
    }
    public string Users => Path.Combine(Root, "users");
    public string UserDb(string id) => Path.Combine(Users, id + ".db");
    public string UserBlobs(string id) => Path.Combine(Blobs, id);
    public string Keys => Path.Combine(Root, "keys");
}
