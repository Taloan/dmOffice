using System.Security.Cryptography;
using System.Text;

namespace DmOffice.Emergency;

/// <summary>
/// Schlüssel eines Notfallpakets (docs/tresor-konzept.md, „Notfallpaket“). Jedes Paket hat ein eigenes Schlüsselpaar:
/// mit dem öffentlichen Schlüssel verschlüsselt der Server Notfalltext und Referenzkopien jederzeit (auch bei
/// geschlossenem Tresor, etwa wenn eine Datei in der Ablage ersetzt wurde). Den privaten Schlüssel gibt es nur verpackt
/// mit dem Paketschlüssel - und der entsteht aus zwei Hälften: dem Notfallcode (hat die Vertrauensperson) und der Hälfte
/// des Servers (gibt er erst nach Ablauf der Frist heraus). Der Besitzer hat den Paketschlüssel im Tresor.
/// </summary>
public static class EmergencyVault
{
    /// <summary>Lesbare Zeichen ohne Verwechslungen (kein 0/O, 1/I/L).</summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeChars = 20;

    public sealed record Keys(string Code, byte[] ServerHalf, byte[] Salt, byte[] PublicKey, byte[] SealedPrivate, byte[] PackageKey);

    /// <summary>Neues Schlüsselpaar mit Notfallcode (20 Zeichen, gut 99 Bit) und Hälfte des Servers.</summary>
    public static Keys Create()
    {
        var code = new string(Enumerable.Range(0, CodeChars).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var half = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var key = PackageKey(code, half, salt);
        return new Keys(code, half, salt, ec.ExportSubjectPublicKeyInfo(), Encrypt(key, ec.ExportPkcs8PrivateKey()), key);
    }

    /// <summary>Notfallcode in Gruppen zu vier Zeichen (zum Ausdrucken).</summary>
    public static string Format(string code) => string.Join("-", Enumerable.Range(0, code.Length / 4).Select(i => code.Substring(i * 4, 4)));

    /// <summary>Eingabe vereinheitlichen: groß, ohne Bindestriche und Leerzeichen.</summary>
    public static string Normalize(string input) => new(input.ToUpperInvariant().Where(c => Alphabet.Contains(c)).ToArray());

    public static byte[] PackageKey(string code, byte[] serverHalf, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, [.. Encoding.ASCII.GetBytes(Normalize(code)), .. serverHalf], 32, salt, "dmoffice-notfallpaket-1"u8.ToArray());

    /// <summary>Privaten Schlüssel auspacken - falscher Paketschlüssel: <see cref="CryptographicException"/>.</summary>
    public static byte[] PrivateKey(byte[] packageKey, byte[] sealedPrivate) => Decrypt(packageKey, sealedPrivate);

    /// <summary>Für das Paket verschlüsseln: flüchtiges Schlüsselpaar, ECDH mit dem öffentlichen Schlüssel, AES-GCM.</summary>
    public static byte[] Seal(byte[] publicKey, byte[] data)
    {
        using var to = ECDiffieHellman.Create();
        to.ImportSubjectPublicKeyInfo(publicKey, out _);
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var epk = eph.ExportSubjectPublicKeyInfo();
        var key = eph.DeriveKeyFromHash(to.PublicKey, HashAlgorithmName.SHA256, null, epk);
        var box = Encrypt(key, data);
        var outp = new byte[2 + epk.Length + box.Length];
        outp[0] = (byte)(epk.Length >> 8); outp[1] = (byte)epk.Length;
        epk.CopyTo(outp, 2);
        box.CopyTo(outp, 2 + epk.Length);
        return outp;
    }

    public static byte[] Open(byte[] privateKey, byte[] sealedData)
    {
        var n = (sealedData[0] << 8) | sealedData[1];
        var epk = sealedData.AsSpan(2, n).ToArray();
        using var me = ECDiffieHellman.Create();
        me.ImportPkcs8PrivateKey(privateKey, out _);
        using var from = ECDiffieHellman.Create();
        from.ImportSubjectPublicKeyInfo(epk, out _);
        var key = me.DeriveKeyFromHash(from.PublicKey, HashAlgorithmName.SHA256, null, epk);
        return Decrypt(key, sealedData.AsSpan(2 + n).ToArray());
    }

    // Nonce (12) | Tag (16) | Inhalt
    private static byte[] Encrypt(byte[] key, byte[] data)
    {
        var outp = new byte[28 + data.Length];
        RandomNumberGenerator.Fill(outp.AsSpan(0, 12));
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(outp.AsSpan(0, 12), data, outp.AsSpan(28), outp.AsSpan(12, 16));
        return outp;
    }

    private static byte[] Decrypt(byte[] key, byte[] box)
    {
        if (box.Length < 28) throw new CryptographicException("Zu kurz.");
        var outp = new byte[box.Length - 28];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(box.AsSpan(0, 12), box.AsSpan(28), box.AsSpan(12, 16), outp);
        return outp;
    }
}
