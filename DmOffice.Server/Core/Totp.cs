using System.Security.Cryptography;
using System.Text;

namespace DmOffice.Core;

/// <summary>
/// Einmalcodes nach RFC 6238 (TOTP, SHA-1, 6 Stellen, 30 Sekunden) - das,
/// was jede Authenticator-App erzeugt. Angenommen wird der Code des
/// aktuellen Zeitfensters und je eines davor und danach (Uhren gehen nicht
/// gleich); derselbe Code gilt nur einmal.
/// </summary>
public static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static string Uri(byte[] secret, string account, string issuer = "dmOffice") =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        int offset = hash[^1] & 0x0F;
        int value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }

    public static long Step(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds() / 30;

    /// <summary>Das Zeitfenster des passenden Codes, oder null. lastStep: zuletzt
    /// angenommenes Fenster - aeltere und dasselbe gelten nicht noch einmal.</summary>
    public static long? Verify(byte[] secret, string code, long lastStep)
    {
        code = new string(code.Where(char.IsDigit).ToArray());
        if (code.Length != 6) return null;
        var now = Step(DateTime.UtcNow);
        for (long s = now - 1; s <= now + 1; s++)
        {
            if (s <= lastStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, s)), Encoding.ASCII.GetBytes(code)))
                return s;
        }
        return null;
    }

    public static List<string> NewRecoveryCodes(int count = 10)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return Enumerable.Range(0, count).Select(_ =>
        {
            var b = RandomNumberGenerator.GetBytes(10);
            var s = new string(b.Select(x => alphabet[x % alphabet.Length]).ToArray());
            return s[..5] + "-" + s[5..];
        }).ToList();
    }
}
