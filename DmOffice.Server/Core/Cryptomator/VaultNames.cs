using System.Security.Cryptography;
using System.Text;

namespace DmOffice.Core.Cryptomator;

/// <summary>
/// Namen und Ordner im Vault-Format 8: Ordner-Kennung (Wurzel leer, sonst UUID) → Ordner unter d/ = Base32(SHA-1(AES-SIV(Kennung))),
/// aufgeteilt in 2 + 30 Zeichen. Namen: NFC, AES-SIV mit der Kennung des Elternordners als zugehörigen Daten, Base64url (mit
/// Auffüllung, wie Guava) plus .c9r. Länger als die Grenze (220): Base64url(SHA-1(langer Name)) plus .c9s.
/// </summary>
public static class VaultNames
{
    public const string Suffix = ".c9r", ShortSuffix = ".c9s";
    public const string DirFile = "dir.c9r", ContentsFile = "contents.c9r", SymlinkFile = "symlink.c9r", NameFile = "name.c9s", DirIdBackup = "dirid.c9r";
    public const string DataDir = "d";
    public const string RootDirId = "";

    public static string Base32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        while (sb.Length % 8 != 0) sb.Append('=');
        return sb.ToString();
    }

    public static string Base64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        if (t.Length % 4 != 0) t += new string('=', 4 - t.Length % 4);
        try { return Convert.FromBase64String(t); } catch (FormatException) { return null; }
    }

    /// <summary>Ordner der Kennung, relativ zur Wurzel des Tresors: d/XX/YYYY….</summary>
    public static string DirPath(Masterkey key, string dirId)
    {
        var hash = Base32(SHA1.HashData(AesSiv.Encrypt(key.EncKey, key.MacKey, Encoding.UTF8.GetBytes(dirId))));
        return DataDir + "/" + hash[..2] + "/" + hash[2..32];
    }

    /// <summary>Verschlüsselter Name samt .c9r (noch nicht gekürzt).</summary>
    public static string Encrypt(Masterkey key, string name, string parentDirId) =>
        Base64Url(AesSiv.Encrypt(key.EncKey, key.MacKey, Encoding.UTF8.GetBytes(name.Normalize(NormalizationForm.FormC)), Encoding.UTF8.GetBytes(parentDirId))) + Suffix;

    /// <summary>Gekürzter Name (.c9s), wenn der verschlüsselte länger als die Grenze ist; sonst null.</summary>
    public static string? Shorten(string encrypted, int threshold) =>
        encrypted.Length > threshold ? Base64Url(SHA1.HashData(Encoding.UTF8.GetBytes(encrypted))) + ShortSuffix : null;

    /// <summary>
    /// Klartext-Name aus einem verschlüsselten (mit .c9r). Ein Zusatz vor .c9r - wie ihn Nextcloud bei Konfliktkopien anhängt
    /// („… (conflicted copy …).c9r“) - wird abgeschnitten und zurückgegeben. Null: kein Name dieses Ordners.
    /// </summary>
    public static (string Name, string? Extra)? Decrypt(Masterkey key, string encrypted, string parentDirId)
    {
        if (!encrypted.EndsWith(Suffix, StringComparison.Ordinal)) return null;
        var core = encrypted[..^Suffix.Length];
        string? extra = null;
        // Base64url hat Länge % 4 == 0 und nur [A-Za-z0-9-_=]; alles dahinter ist ein fremder Zusatz
        var end = 0;
        while (end < core.Length && (char.IsAsciiLetterOrDigit(core[end]) || core[end] is '-' or '_' or '=')) end++;
        if (end < core.Length) { extra = core[end..]; core = core[..end]; }
        if (core.Length == 0 || core.Length % 4 != 0 || FromBase64Url(core) is not { } bytes) return null;
        var plain = AesSiv.Decrypt(key.EncKey, key.MacKey, bytes, Encoding.UTF8.GetBytes(parentDirId));
        if (plain == null) return null;
        var name = Encoding.UTF8.GetString(plain);
        // Ein präparierter Tresor (fremder Masterkey-Besitzer) könnte Namen wie „..“ oder „a/b“ verschlüsseln - die gäben
        // beim Herausnehmen, Herunterladen als ZIP oder Abgleich Pfade außerhalb des Ordners. Solche Einträge gibt es nicht.
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains((char)92) || name.Any(char.IsControl)) return null;
        return (name, extra);
    }
}
