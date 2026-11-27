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
/// server.json, Schluesselring der Anmeldung). Die Datei loescht dmOffice nicht
/// selbst: vollstaendige Sicherungen von vorher brauchen sie noch. Die Verwaltung
/// zeigt einen Hinweis mit Knopf, bis sie weg ist.
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

    /// <param name="readOnly">Nur lesen (Werkzeug neben dem laufenden Server): nichts umpacken, keine Schlüsseldatei anlegen.</param>
    public Vault(DataPaths paths, string? environmentKey, bool readOnly = false)
    {
        KeyFile = Path.Combine(paths.Root, "master.key");
        byte[]? fileKey = File.Exists(KeyFile) ? Convert.FromHexString(File.ReadAllText(KeyFile).Trim()) : null;

        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            Key = Rfc2898DeriveBytes.Pbkdf2(environmentKey.Trim(), Encoding.UTF8.GetBytes(Salt), 200_000, HashAlgorithmName.SHA256, 32);
            Previous = fileKey;
            KeyInDataDir = false;
            // Gleich hier, bevor irgendein anderer Teil die Dateien liest und mit alten Werten zurueckschreibt
            if (fileKey != null && !readOnly)
            {
                try { Migrated = Migrate(paths, fileKey); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException) { MigrationError = e.Message; }
            }
        }
        else
        {
            if (fileKey == null)
            {
                if (readOnly) throw new InvalidOperationException("Kein Hauptschlüssel: DMOFFICE_MASTER_KEY ist nicht gesetzt, und im Datenverzeichnis liegt keine Schlüsseldatei.");
                fileKey = RandomNumberGenerator.GetBytes(32);
                File.WriteAllText(KeyFile, Convert.ToHexString(fileKey));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            Key = fileKey;
            KeyInDataDir = true;
        }
    }

    // [feature:masterkeyreq]
    /// <summary>
    /// Darf der Server mit diesem Datenverzeichnis ohne DMOFFICE_MASTER_KEY starten? Null: ja - die Variable ist gesetzt, oder die
    /// Schlüsseldatei einer bestehenden Installation liegt da (dann bleibt es bei der Warnung). Sonst der Grund für den Abbruch:
    /// Ein neues Datenverzeichnis (keine Schlüsseldatei, keine Benutzer) braucht einen eigenen Schlüssel außerhalb der Daten;
    /// gibt es Benutzer, aber keinen Schlüssel, ließen sich ihre Daten mit einem neu erzeugten ohnehin nicht öffnen.
    /// </summary>
    public static string? StartRefused(DataPaths paths, string? environmentKey)
    {
        if (!string.IsNullOrWhiteSpace(environmentKey) || File.Exists(Path.Combine(paths.Root, "master.key"))) return null;
        if (HasUsers(paths))
            return "dmOffice startet nicht: Im Datenverzeichnis gibt es Benutzer, aber weder DMOFFICE_MASTER_KEY noch die Schlüsseldatei master.key. " +
                   "Ohne den bisherigen Hauptschlüssel lassen sich die Daten nicht öffnen. DMOFFICE_MASTER_KEY mit dem bisherigen Wert setzen " +
                   "oder master.key aus einer Sicherung zurück ins Datenverzeichnis legen und neu starten.";
        return "dmOffice startet nicht: DMOFFICE_MASTER_KEY ist nicht gesetzt. Eine neue Installation braucht einen eigenen Hauptschlüssel als " +
               "Umgebungsvariable - er liegt nicht bei den Daten, so bleiben auch Kopien und Sicherungen des Datenverzeichnisses verschlüsselt. " +
               "Einen starken Wert erzeugt z. B. „openssl rand -hex 32“ (64 Zeichen). Ihn als DMOFFICE_MASTER_KEY in die Container-Vorlage " +
               "bzw. in die .env neben compose.yaml eintragen, sicher aufbewahren (z. B. im Passwortmanager) und den Container neu starten. " +
               "Ohne diesen Wert lassen sich die Daten später nicht mehr öffnen.";
    }

    private static bool HasUsers(DataPaths paths)
    {
        var file = Path.Combine(paths.Root, "users.json");
        if (!File.Exists(file)) return false;
        try { return JsonNode.Parse(File.ReadAllText(file)) is not JsonArray { Count: 0 }; }
        // Unlesbar: lieber als bestehende Installation behandeln
        catch (JsonException) { return true; }
    }
    // [/feature:masterkeyreq]

    /// <summary>
    /// Wirkt der Wert von DMOFFICE_MASTER_KEY wie eine selbst gewählte Passphrase statt einer langen Zufallsfolge? Abgeleitet
    /// wird er mit festem Salt (für alle Installationen gleich, sonst ließe sich der Schlüssel bestehender Daten nicht mehr
    /// bilden) - eine kurze Passphrase ist damit leichter zu erraten. Nur für den Hinweis im Log.
    /// </summary>
    public static bool LooksWeak(string? environmentKey)
    {
        var k = (environmentKey ?? "").Trim();
        if (k.Length == 0) return false;
        if (k.Length < 24) return true;
        // Nur wenige verschiedene Zeichen (z. B. „aaaa…“ oder „1234…“)
        return k.Distinct().Count() < 10;
    }

    /// <summary>Die Variable gilt, aber die alte Schluesseldatei liegt noch im Datenverzeichnis.</summary>
    public bool OldKeyFileLeft => !KeyInDataDir && File.Exists(KeyFile);

    /// <summary>Alte Schluesseldatei loeschen (Knopf in der Verwaltung); nur, wenn die Variable gilt.</summary>
    public void DeleteOldKeyFile()
    {
        if (!KeyInDataDir) File.Delete(KeyFile);
    }

    public string Wrap(byte[] secret) => Wrap(Key, secret);

    /// <summary>
    /// Umstieg von der Schluesseldatei auf DMOFFICE_MASTER_KEY: in allen kleinen Dateien des Datenverzeichnisses und
    /// in keys/ jeden Wert, der sich mit dem alten Schluessel auspacken laesst, mit dem neuen einpacken - in .json und
    /// .xml jeden Textwert, sonst den ganzen Inhalt. Die Datei bleibt liegen; solange sie da ist, laeuft das bei jedem
    /// Start (findet dann nichts mehr, packt aber auch zurueckgespielte alte Werte um). Gibt die Zahl der neu
    /// eingepackten Werte zurueck.
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
        // [feature:cfgseal]
        // Siegel der Einstellungsdateien: was zum alten Schlüssel passte, gilt danach mit dem neuen (auch bei einem Abbruch)
        var sealedBefore = ConfigSeal.ValidUnder(paths.Root, old);
        try
        {
        // [/feature:cfgseal]
        foreach (var (f, t) in writes)
        {
            File.WriteAllText(f + ".rewrap", t);
            File.Move(f + ".rewrap", f, overwrite: true);
        }
        // [feature:cfgseal]
        }
        finally { if (sealedBefore.Count > 0) ConfigSeal.Reseal(paths.Root, Key, sealedBefore); }
        // [/feature:cfgseal]
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
