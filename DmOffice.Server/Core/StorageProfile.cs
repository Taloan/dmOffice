using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DmOffice.Core;

/// <summary>
/// Zugangsdaten des Storage Extenders für den Notfall - wie die Wiederherstellungscodes bei 2FA: eine Zeile Text
/// („dmoffice-zugang:1:…“), mit dem Hauptschlüssel verschlüsselt. Ohne den Schlüssel nutzlos; mit ihm trägt ein neu
/// aufgesetzter Server beim Wiederherstellen alles selbst ein. Gehört neben DMOFFICE_MASTER_KEY in den Passwortmanager.
/// </summary>
public static class StorageProfile
{
    public const string Marker = "dmoffice-zugang:1:";

    public sealed record Data(string Target, string? Endpoint, string? Region, string? Bucket, string? Prefix, string? AccessKey, string? SecretKey,
        string? WebDavUrl, string? WebDavUser, string? WebDavPassword, string? Folder);
    /// <summary>Wo die Dateiinhalte liegen (Storage Extender) und wo das Core-Backup - null: wie der Storage Extender.</summary>
    public sealed record Places(Data? Files, Data? Core);

    private static byte[] KeyOf(byte[] master) => HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "dmoffice storage profile v1"u8.ToArray());

    /// <summary>Die Zugangsdaten so, wie sie gerade eingestellt sind - Storage Extender und, falls eigenes, Core-Backup-Ziel.</summary>
    public static Places Current(ServerConfig config)
    {
        var s = config.Current;
        Data? files = !S3Mirror.Configured(s, config.S3SecretKey()) ? null : s.StorageTarget switch
        {
            "webdav" => new("webdav", null, null, null, s.S3Prefix, null, null, s.StorageWebDavUrl, s.StorageWebDavUser, config.StorageWebDavPassword(), null),
            "folder" => new("folder", null, null, null, s.S3Prefix, null, null, null, null, null, s.StorageFolder),
            _ => new("s3", s.S3Endpoint, s.S3Region, s.S3Bucket, s.S3Prefix, s.S3AccessKey, config.S3SecretKey(), null, null, null, null),
        };
        Data? core = !s.BackupOwnTarget ? null : s.BackupTarget switch
        {
            "webdav" => new("webdav", null, null, null, s.BackupS3Prefix, null, null, s.BackupWebDavUrl, s.BackupWebDavUser, config.BackupWebDavPassword(), null),
            "folder" => new("folder", null, null, null, s.BackupS3Prefix, null, null, null, null, null, s.BackupFolder),
            _ => new("s3", s.BackupS3Endpoint, s.BackupS3Region, s.BackupS3Bucket, s.BackupS3Prefix, s.BackupS3AccessKey, config.BackupS3SecretKey(), null, null, null, null),
        };
        return new(files, core);
    }

    /// <summary>Fingerabdruck der Zugangsdaten - ändert er sich, ist eine gesicherte Zeile veraltet.</summary>
    public static string Stamp(ServerConfig config) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Current(config)))))[..16];

    public static string Seal(Vault vault, Places data) =>
        Marker + Vault.Wrap(KeyOf(vault.Key), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)));

    /// <summary>
    /// Die Zeile aus eingefügtem Text oder einer gespeicherten Datei herausfischen und öffnen - mit dem Hauptschlüssel
    /// dieses Servers (oder dem vorigen). Null: keine Zeile gefunden; CryptographicException: passt nicht zum Schlüssel.
    /// </summary>
    public static Places? Open(Vault vault, string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
        if (line == null) return null;
        CryptographicException? last = null;
        foreach (var master in new[] { vault.Key, vault.Previous })
        {
            if (master == null) continue;
            try { return JsonSerializer.Deserialize<Places>(Vault.Unwrap(KeyOf(master), line[Marker.Length..])); }
            catch (CryptographicException e) { last = e; }
            catch (Exception e) when (e is FormatException or ArgumentException or JsonException) { return null; }   // abgeschnitten, verändert
        }
        throw last ?? new CryptographicException();
    }
}
