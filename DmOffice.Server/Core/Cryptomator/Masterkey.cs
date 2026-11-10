using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace DmOffice.Core.Cryptomator;

/// <summary>Masterkey eines Cryptomator-Tresors: Schlüssel für Verschlüsselung und MAC, je 256 Bit.</summary>
public sealed class Masterkey : IDisposable
{
    public const int KeyLength = 32;
    public byte[] EncKey { get; }
    public byte[] MacKey { get; }

    public Masterkey(byte[] encKey, byte[] macKey)
    {
        if (encKey.Length != KeyLength || macKey.Length != KeyLength) throw new ArgumentException("Ein Masterkey hat zweimal 32 Byte.");
        EncKey = encKey;
        MacKey = macKey;
    }

    public static Masterkey Generate() => new(RandomNumberGenerator.GetBytes(KeyLength), RandomNumberGenerator.GetBytes(KeyLength));

    /// <summary>Verschlüsselung || MAC, wie cryptolib (<c>Masterkey.getEncoded</c>) - Schlüssel für die Signatur von vault.cryptomator.</summary>
    public byte[] Raw() => [.. EncKey, .. MacKey];

    public static Masterkey FromRaw(ReadOnlySpan<byte> raw) =>
        raw.Length == 2 * KeyLength ? new(raw[..KeyLength].ToArray(), raw[KeyLength..].ToArray()) : throw new ArgumentException("Ein Masterkey hat 64 Byte.");

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(EncKey);
        CryptographicOperations.ZeroMemory(MacKey);
    }
}

/// <summary>
/// masterkey.cryptomator: beide Masterkeys mit AES-Key-Wrap (RFC 3394), der Schlüssel dazu per scrypt aus dem Passwort.
/// versionMac ist ein HMAC-SHA256 über die Fassung (4 Byte, Big Endian). Seit Vault-Format 8 steht die Fassung fest auf 999,
/// das Format selbst in vault.cryptomator.
/// </summary>
public static class MasterkeyFile
{
    public const string FileName = "masterkey.cryptomator";
    public const int Version = 999;
    /// <summary>Vorgaben wie in cryptolib (scrypt N = 2^15, r = 8, p = 1). Tests nehmen weniger.</summary>
    public static int DefaultCost { get; set; } = 32768;
    public const int DefaultBlockSize = 8;

    private sealed class Model
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("scryptSalt")] public string ScryptSalt { get; set; } = "";
        [JsonPropertyName("scryptCostParam")] public int ScryptCostParam { get; set; }
        [JsonPropertyName("scryptBlockSize")] public int ScryptBlockSize { get; set; }
        [JsonPropertyName("primaryMasterKey")] public string PrimaryMasterKey { get; set; } = "";
        [JsonPropertyName("hmacMasterKey")] public string HmacMasterKey { get; set; } = "";
        [JsonPropertyName("versionMac")] public string VersionMac { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Kennwort wie in cryptolib: UTF-8, vorher NFC (Cryptomator normalisiert die Eingabe ebenso).</summary>
    private static byte[] Kek(string password, byte[] salt, int cost, int blockSize) =>
        SCrypt.Generate(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)), salt, cost, blockSize, 1, 32);

    private static byte[] VersionMac(byte[] macKey, int version)
    {
        Span<byte> v = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(v, version);
        return HMACSHA256.HashData(macKey, v);
    }

    public static byte[] Write(Masterkey key, string password, int? cost = null)
    {
        var salt = RandomNumberGenerator.GetBytes(8);
        var n = cost ?? DefaultCost;
        var kek = Kek(password, salt, n, DefaultBlockSize);
        try
        {
            var wrap = new Rfc3394WrapEngine(new AesEngine());
            wrap.Init(true, new KeyParameter(kek));
            var m = new Model
            {
                Version = Version,
                ScryptSalt = Convert.ToBase64String(salt),
                ScryptCostParam = n,
                ScryptBlockSize = DefaultBlockSize,
                PrimaryMasterKey = Convert.ToBase64String(wrap.Wrap(key.EncKey, 0, key.EncKey.Length)),
                HmacMasterKey = Convert.ToBase64String(wrap.Wrap(key.MacKey, 0, key.MacKey.Length)),
                VersionMac = Convert.ToBase64String(VersionMac(key.MacKey, Version)),
            };
            return JsonSerializer.SerializeToUtf8Bytes(m, Indented);
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Masterkey aus der Datei; null, wenn das Passwort nicht stimmt. Kaputte Datei: <see cref="FormatException"/>.</summary>
    public static Masterkey? Read(ReadOnlySpan<byte> json, string password)
    {
        var m = JsonSerializer.Deserialize<Model>(json) ?? throw new FormatException("masterkey.cryptomator ist leer.");
        // Obergrenzen (Cryptomator nimmt 2^15/8): ein präparierter Tresor soll den Server nicht mit Speicher oder Rechenzeit lahmlegen
        if (m.ScryptCostParam < 2 || m.ScryptBlockSize < 1 || m.ScryptCostParam > 1 << 20 || m.ScryptBlockSize > 16
            || (m.ScryptCostParam & (m.ScryptCostParam - 1)) != 0)
            throw new FormatException("masterkey.cryptomator hat ungültige scrypt-Werte.");
        var kek = Kek(password, Convert.FromBase64String(m.ScryptSalt), m.ScryptCostParam, m.ScryptBlockSize);
        try
        {
            var unwrap = new Rfc3394WrapEngine(new AesEngine());
            unwrap.Init(false, new KeyParameter(kek));
            byte[] enc, mac;
            try
            {
                var p = Convert.FromBase64String(m.PrimaryMasterKey);
                var h = Convert.FromBase64String(m.HmacMasterKey);
                enc = unwrap.Unwrap(p, 0, p.Length);
                mac = unwrap.Unwrap(h, 0, h.Length);
            }
            catch (Org.BouncyCastle.Crypto.InvalidCipherTextException) { return null; }
            if (!CryptographicOperations.FixedTimeEquals(VersionMac(mac, m.Version), Convert.FromBase64String(m.VersionMac)))
                throw new FormatException("masterkey.cryptomator: Die Fassung passt nicht zu ihrer Prüfsumme.");
            return new Masterkey(enc, mac);
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }
}

/// <summary>
/// vault.cryptomator: JWT (HS256, signiert mit Verschlüsselungs- und MAC-Masterkey) mit Format 8, Grenze für Namenskürzung
/// und SIV_GCM. Der Kopf nennt in <c>kid</c>, wo der Masterkey liegt.
/// </summary>
public static class VaultConfig
{
    public const string FileName = "vault.cryptomator";
    public const int Format = 8;
    public const string CipherCombo = "SIV_GCM";
    public const string MasterkeyKid = "masterkeyfile:" + MasterkeyFile.FileName;
    public const int DefaultShorteningThreshold = 220;

    public sealed record Config(int Format, int ShorteningThreshold, string CipherCombo, string Jti, string Kid);

    private static string B64Url(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }

    public static string Write(Masterkey key, int shorteningThreshold = DefaultShorteningThreshold)
    {
        var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["kid"] = MasterkeyKid, ["typ"] = "JWT", ["alg"] = "HS256" }));
        var payload = B64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["format"] = Format, ["shorteningThreshold"] = shorteningThreshold, ["jti"] = Guid.NewGuid().ToString(), ["cipherCombo"] = CipherCombo,
        }));
        var raw = key.Raw();
        try { return header + "." + payload + "." + B64Url(HMACSHA256.HashData(raw, Encoding.ASCII.GetBytes(header + "." + payload))); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    /// <summary>Kopf ohne Prüfung - welcher Schlüssel (kid) gebraucht wird.</summary>
    public static string KidOf(string jwt)
    {
        var parts = jwt.Trim().Split('.');
        if (parts.Length != 3) throw new FormatException("vault.cryptomator ist kein JWT.");
        using var doc = JsonDocument.Parse(FromB64Url(parts[0]));
        return doc.RootElement.TryGetProperty("kid", out var kid) ? kid.GetString() ?? "" : "";
    }

    /// <summary>Prüft Signatur, Format und Verfahren. Falscher Schlüssel oder fremdes Format: <see cref="FormatException"/>.</summary>
    public static Config Read(string jwt, Masterkey key)
    {
        var parts = jwt.Trim().Split('.');
        if (parts.Length != 3) throw new FormatException("vault.cryptomator ist kein JWT.");
        using var head = JsonDocument.Parse(FromB64Url(parts[0]));
        var alg = head.RootElement.TryGetProperty("alg", out var a) ? a.GetString() : null;
        var data = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var raw = key.Raw();
        byte[] sig;
        try
        {
            sig = alg switch
            {
                "HS256" => HMACSHA256.HashData(raw, data),
                "HS384" => HMACSHA384.HashData(raw, data),
                "HS512" => HMACSHA512.HashData(raw, data),
                _ => throw new FormatException($"vault.cryptomator: Verfahren {alg} wird nicht unterstützt."),
            };
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
        if (!CryptographicOperations.FixedTimeEquals(sig, FromB64Url(parts[2]))) throw new FormatException("vault.cryptomator passt nicht zum Masterkey.");
        using var body = JsonDocument.Parse(FromB64Url(parts[1]));
        var r = body.RootElement;
        var cfg = new Config(r.GetProperty("format").GetInt32(), r.TryGetProperty("shorteningThreshold", out var st) ? st.GetInt32() : DefaultShorteningThreshold,
            r.GetProperty("cipherCombo").GetString() ?? "", r.TryGetProperty("jti", out var j) ? j.GetString() ?? "" : "",
            head.RootElement.TryGetProperty("kid", out var k) ? k.GetString() ?? "" : "");
        if (cfg.Format != Format) throw new FormatException($"Vault-Format {cfg.Format} wird nicht unterstützt (nur {Format}).");
        if (cfg.CipherCombo != CipherCombo) throw new FormatException($"Verfahren {cfg.CipherCombo} wird nicht unterstützt (nur {CipherCombo}).");
        return cfg;
    }
}
