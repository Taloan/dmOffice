using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace DmOffice.Core.Cryptomator;

/// <summary>
/// AES-SIV nach RFC 5297, zusammengesetzt aus CMAC und AES-CTR von BouncyCastle (docs/tresor-cryptomator-konzept.md,
/// Entscheidung 5). Schlüssel wie bei Cryptomator (siv-mode): ein Schlüssel für CTR, einer für CMAC - in RFC 5297 ist der
/// Gesamtschlüssel K = MAC-Schlüssel || CTR-Schlüssel. Ausgabe: V (16 Byte) || Geheimtext.
/// </summary>
public static class AesSiv
{
    private const int Block = 16;

    public static byte[] Encrypt(byte[] ctrKey, byte[] macKey, ReadOnlySpan<byte> plain, params byte[][] associated)
    {
        var v = S2V(macKey, plain, associated);
        var result = new byte[Block + plain.Length];
        v.CopyTo(result, 0);
        Ctr(ctrKey, v, plain, result.AsSpan(Block));
        return result;
    }

    /// <summary>Gegenstück zu <see cref="Encrypt"/>; null, wenn Schlüssel, Daten oder zugehörige Daten nicht passen.</summary>
    public static byte[]? Decrypt(byte[] ctrKey, byte[] macKey, ReadOnlySpan<byte> data, params byte[][] associated)
    {
        if (data.Length < Block) return null;
        var v = data[..Block].ToArray();
        var plain = new byte[data.Length - Block];
        Ctr(ctrKey, v, data[Block..], plain);
        var check = S2V(macKey, plain, associated);
        if (CryptographicOperations.FixedTimeEquals(check, v)) return plain;
        CryptographicOperations.ZeroMemory(plain);
        return null;
    }

    private static byte[] Cmac(byte[] key, ReadOnlySpan<byte> data)
    {
        var mac = new CMac(new AesEngine());
        mac.Init(new KeyParameter(key));
        mac.BlockUpdate(data);
        var output = new byte[Block];
        mac.DoFinal(output, 0);
        return output;
    }

    /// <summary>Verdoppeln in GF(2^128) (RFC 5297, 2.3).</summary>
    private static byte[] Dbl(byte[] s)
    {
        var r = new byte[Block];
        for (var i = 0; i < Block; i++) r[i] = (byte)((s[i] << 1) | (i + 1 < Block ? s[i + 1] >> 7 : 0));
        if ((s[0] & 0x80) != 0) r[Block - 1] ^= 0x87;
        return r;
    }

    private static void Xor(Span<byte> target, ReadOnlySpan<byte> other)
    {
        for (var i = 0; i < other.Length; i++) target[i] ^= other[i];
    }

    /// <summary>S2V (RFC 5297, 2.4).</summary>
    private static byte[] S2V(byte[] macKey, ReadOnlySpan<byte> plain, byte[][] associated)
    {
        var d = Cmac(macKey, new byte[Block]);
        foreach (var a in associated)
        {
            d = Dbl(d);
            Xor(d, Cmac(macKey, a));
        }
        byte[] t;
        if (plain.Length >= Block)
        {
            t = plain.ToArray();
            Xor(t.AsSpan(t.Length - Block), d);
        }
        else
        {
            t = Dbl(d);
            var padded = new byte[Block];
            plain.CopyTo(padded);
            padded[plain.Length] = 0x80;
            Xor(t, padded);
        }
        return Cmac(macKey, t);
    }

    /// <summary>AES-CTR mit Q = V, darin die Bits 31 und 63 von rechts gelöscht (RFC 5297, 2.5).</summary>
    private static void Ctr(byte[] ctrKey, byte[] v, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length == 0) return;
        var q = (byte[])v.Clone();
        q[8] &= 0x7f;
        q[12] &= 0x7f;
        var ctr = new SicBlockCipher(new AesEngine());
        ctr.Init(true, new ParametersWithIV(new KeyParameter(ctrKey), q));
        var inBlock = new byte[Block];
        var outBlock = new byte[Block];
        for (var pos = 0; pos < input.Length; pos += Block)
        {
            var n = Math.Min(Block, input.Length - pos);
            input.Slice(pos, n).CopyTo(inBlock);
            ctr.ProcessBlock(inBlock, 0, outBlock, 0);
            outBlock.AsSpan(0, n).CopyTo(output.Slice(pos, n));
        }
    }
}
