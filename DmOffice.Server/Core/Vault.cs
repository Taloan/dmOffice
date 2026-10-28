using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace DmOffice.Core;

/// <summary>
/// Der Hauptschluessel. Er packt die Schluessel der Benutzerdatenbanken ein;
/// selbst liegt er nicht neben den Daten, sondern kommt aus der Umgebung
/// (DMOFFICE_MASTER_KEY, in Unraid als maskierte Variable). Fehlt sie, legt
/// dmOffice ihn als Datei im Datenverzeichnis an - dann schuetzt die
/// Verschluesselung nur noch Kopien einzelner Datenbanken, nicht das ganze
/// Verzeichnis. Die Verwaltung zeigt das an.
///
/// Wird die Variable spaeter gesetzt, packt der naechste Start alles neu ein,
/// was mit dem alten Schluessel eingepackt ist (Benutzerschluessel, keys/,
/// server.json, Schluesselring der Anmeldung), und loescht erst dann die Datei.
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
    /// <summary>Der Umstieg auf die Variable ist gescheitert (die Datei bleibt liegen, beide Schluessel gelten weiter).</summary>
    public string? MigrationError { get; }
    /// <summary>So viele Werte hat der Umstieg neu eingepackt.</summary>
    public int Migrated { get; }

    public Vault(DataPaths paths, string? environmentKey)
    {
        KeyFile = Path.Combine(paths.Root, "master.key");
        byte[]? fileKey = File.Exists(KeyFile) ? Convert.FromHexString(File.ReadAllText(KeyFile).Trim()) : null;

        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            Key = Rfc2898DeriveBytes.Pbkdf2(environmentKey.Trim(), Encoding.UTF8.GetBytes(Salt), 200_000, HashAlgorithmName.SHA256, 32);
            Previous = fileKey;
            KeyInDataDir = false;
            // Gleich hier, bevor irgendein anderer Teil die Dateien liest und mit alten Werten zurueckschreibt
            if (fileKey != null)
            {
                try { Migrated = Migrate(paths, fileKey); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException) { MigrationError = e.Message; }
            }
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

    /// <summary>
    /// Umstieg von der Schluesseldatei auf DMOFFICE_MASTER_KEY: in allen kleinen Dateien des Datenverzeichnisses und
    /// in keys/ jeden Wert, der sich mit dem alten Schluessel auspacken laesst, mit dem neuen einpacken - in .json und
    /// .xml jeden Textwert, sonst den ganzen Inhalt. Erst wenn alles geschrieben ist, wird die Datei geloescht; bis
    /// dahin bleibt sie liegen (ein Abbruch kostet nichts). Gibt die Zahl der neu eingepackten Werte zurueck.
    /// </summary>
    private int Migrate(DataPaths paths, byte[] old)
    {
        var files = Directory.EnumerateFiles(paths.Root).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (Directory.Exists(paths.Keys)) files.AddRange(Directory.EnumerateFiles(paths.Keys, "*", SearchOption.AllDirectories));
        var writes = new List<(string File, string Text)>();
        int n = 0;
        foreach (var f in files)
        {
            if (new FileInfo(f).Length > 16 * 1024 * 1024) continue;
            var text = File.ReadAllText(f);
            int count = 0;
            string? updated;
            if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (JsonNode.Parse(text) is not { } node) continue;
                count = RewrapJson(node, old);
                updated = count > 0 ? node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : null;
            }
            else if (f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var doc = XDocument.Parse(text);
                foreach (var e in doc.Descendants().Where(e => !e.HasElements).ToList())
                    if (TryRewrap(e.Value, old) is { } w) { e.Value = w; count++; }
                updated = count > 0 ? doc.ToString() : null;
            }
            else
            {
                updated = TryRewrap(text.Trim(), old);
                count = updated != null ? 1 : 0;
            }
            if (updated != null) { writes.Add((f, updated)); n += count; }
        }
        foreach (var (f, t) in writes)
        {
            File.WriteAllText(f + ".rewrap", t);
            File.Move(f + ".rewrap", f, overwrite: true);
        }
        File.Delete(KeyFile);
        return n;
    }

    private int RewrapJson(JsonNode node, byte[] old)
    {
        int n = 0;
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o.ToList())
                {
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && TryRewrap(s, old) is { } w) { o[k] = w; n++; }
                    else if (v != null) n += RewrapJson(v, old);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue jv && jv.TryGetValue<string>(out var s) && TryRewrap(s, old) is { } w) { a[i] = w; n++; }
                    else if (a[i] is { } c) n += RewrapJson(c, old);
                }
                break;
        }
        return n;
    }

    /// <summary>Mit dem alten Schluessel eingepackt (und nicht schon mit dem neuen)? Dann neu eingepackt, sonst null.</summary>
    private string? TryRewrap(string value, byte[] old)
    {
        if (value.Length < 40) return null;
        byte[] data;
        try { data = Convert.FromBase64String(value); } catch (FormatException) { return null; }
        if (data.Length < 12 + 16) return null;
        try { Unwrap(Key, value); return null; } catch (CryptographicException) { }
        try { return Wrap(Key, Unwrap(old, value)); } catch (CryptographicException) { return null; }
    }

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
