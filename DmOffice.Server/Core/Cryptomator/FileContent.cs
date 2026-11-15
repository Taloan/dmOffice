using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DmOffice.Core.Cryptomator;

/// <summary>
/// Inhalte im Vault-Format 8. SIV_GCM (Standard, neue Tresore): Kopf mit 68 Byte (Nonce 12 | AES-GCM über 8 Byte 0xFF und den
/// 32-Byte-Inhaltsschlüssel, mit dem Verschlüsselungs-Masterkey | Prüfsumme 16), danach Blöcke mit 32 KiB Klartext: Nonce 12 |
/// Geheimtext | Prüfsumme 16; zugehörige Daten je Block: Blocknummer (8 Byte, Big Endian) || Kopf-Nonce.
/// SIV_CTRMAC (ältere Tresore): Kopf mit 88 Byte (Nonce 16 | AES-CTR über dieselben 40 Byte | HMAC-SHA256 mit dem MAC-Masterkey über
/// Nonce || Geheimtext), Blöcke: Nonce 16 | AES-CTR mit dem Inhaltsschlüssel | HMAC-SHA256 über Kopf-Nonce || Blocknummer (8 Byte,
/// Big Endian) || Block-Nonce || Geheimtext. Eine leere Datei ist nur der Kopf.
/// </summary>
public static class FileContent
{
    public const int NonceSize = 12, TagSize = 16, HeaderSize = NonceSize + 8 + 32 + TagSize;
    public const int ChunkPlain = 32 * 1024, ChunkCipher = NonceSize + ChunkPlain + TagSize;
    // SIV_CTRMAC
    public const int CtrNonceSize = 16, MacSize = 32, CtrHeaderSize = CtrNonceSize + 8 + 32 + MacSize;
    public const int CtrChunkCipher = CtrNonceSize + ChunkPlain + MacSize;

    /// <summary>MacKey gesetzt: SIV_CTRMAC (Blöcke tragen eine HMAC mit diesem Schlüssel), sonst SIV_GCM.</summary>
    public sealed record Header(byte[] Nonce, byte[] ContentKey, byte[]? MacKey = null)
    {
        public bool Ctr => MacKey != null;
        /// <summary>Zuschlag je Block: Nonce und Prüfsumme.</summary>
        public int Overhead => Ctr ? CtrNonceSize + MacSize : NonceSize + TagSize;
        public int ChunkCipherSize => ChunkPlain + Overhead;
        public int HeaderLength => Ctr ? CtrHeaderSize : HeaderSize;
    }

    private static (int Head, int Overhead) Layout(bool ctr) => ctr ? (CtrHeaderSize, CtrNonceSize + MacSize) : (HeaderSize, NonceSize + TagSize);

    public static long CleartextSize(long ciphertextSize) => CleartextSize(ciphertextSize, false);
    public static long CleartextSize(Masterkey key, long ciphertextSize) => CleartextSize(ciphertextSize, key.CtrMac);

    public static long CleartextSize(long ciphertextSize, bool ctr)
    {
        var (head, over) = Layout(ctr);
        if (ciphertextSize < head) throw new FormatException("Datei kürzer als ihr Kopf.");
        var body = ciphertextSize - head;
        var full = body / (ChunkPlain + over);
        var rest = body % (ChunkPlain + over);
        if (rest > 0 && rest <= over) throw new FormatException("Datei hat einen unvollständigen Block.");
        return full * ChunkPlain + (rest == 0 ? 0 : rest - over);
    }

    public static long CiphertextSize(long cleartextSize) => CiphertextSize(cleartextSize, false);

    public static long CiphertextSize(long cleartextSize, bool ctr)
    {
        var (head, over) = Layout(ctr);
        var full = cleartextSize / ChunkPlain;
        var rest = cleartextSize % ChunkPlain;
        return head + full * (ChunkPlain + over) + (rest == 0 ? 0 : rest + over);
    }

    /// <summary>Länge des Kopfs für diesen Schlüssel (nach dem Verfahren des Tresors).</summary>
    public static int HeaderLength(Masterkey key) => key.CtrMac ? CtrHeaderSize : HeaderSize;

    public static (Header Header, byte[] Bytes) NewHeader(Masterkey key)
    {
        var plain = new byte[40];
        plain.AsSpan(0, 8).Fill(0xFF);
        var contentKey = RandomNumberGenerator.GetBytes(32);
        contentKey.CopyTo(plain, 8);
        try
        {
            if (key.CtrMac)
            {
                var h = new Header(RandomNumberGenerator.GetBytes(CtrNonceSize), contentKey, key.MacKey);
                var bytes = new byte[CtrHeaderSize];
                h.Nonce.CopyTo(bytes, 0);
                Ctr(key.EncKey, h.Nonce, plain, bytes.AsSpan(CtrNonceSize, 40));
                HMACSHA256.HashData(key.MacKey, bytes.AsSpan(0, CtrNonceSize + 40), bytes.AsSpan(CtrNonceSize + 40, MacSize));
                return (h, bytes);
            }
            var g = new Header(RandomNumberGenerator.GetBytes(NonceSize), contentKey);
            var gb = new byte[HeaderSize];
            g.Nonce.CopyTo(gb, 0);
            using (var gcm = new AesGcm(key.EncKey, TagSize))
                gcm.Encrypt(g.Nonce, plain, gb.AsSpan(NonceSize, 40), gb.AsSpan(NonceSize + 40, TagSize));
            return (g, gb);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static Header ReadHeader(Masterkey key, ReadOnlySpan<byte> bytes)
    {
        var plain = new byte[40];
        if (key.CtrMac)
        {
            if (bytes.Length < CtrHeaderSize) throw new FormatException("Datei kürzer als ihr Kopf.");
            Span<byte> mac = stackalloc byte[MacSize];
            HMACSHA256.HashData(key.MacKey, bytes[..(CtrNonceSize + 40)], mac);
            if (!CryptographicOperations.FixedTimeEquals(mac, bytes.Slice(CtrNonceSize + 40, MacSize)))
                throw new CryptographicException("Kopf der Datei passt nicht zum Masterkey oder wurde verändert.");
            var nonce = bytes[..CtrNonceSize].ToArray();
            Ctr(key.EncKey, nonce, bytes.Slice(CtrNonceSize, 40), plain);
            return new Header(nonce, plain[8..], key.MacKey);
        }
        if (bytes.Length < HeaderSize) throw new FormatException("Datei kürzer als ihr Kopf.");
        try
        {
            using var gcm = new AesGcm(key.EncKey, TagSize);
            gcm.Decrypt(bytes[..NonceSize], bytes.Slice(NonceSize, 40), bytes.Slice(NonceSize + 40, TagSize), plain);
        }
        catch (CryptographicException) { throw new CryptographicException("Kopf der Datei passt nicht zum Masterkey oder wurde verändert."); }
        return new Header(bytes[..NonceSize].ToArray(), plain[8..]);
    }

    /// <summary>AES im Zählermodus wie Javas „AES/CTR/NoPadding“: Der 16-Byte-Zähler beginnt bei der Nonce und zählt als
    /// 128-Bit-Zahl (Big Endian) hoch.</summary>
    internal static void Ctr(byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input, Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        const int batch = 256;
        var counters = new byte[16 * batch];
        var stream = new byte[16 * batch];
        Span<byte> counter = stackalloc byte[16];
        iv.CopyTo(counter);
        for (var pos = 0; pos < input.Length; pos += stream.Length)
        {
            var n = Math.Min(stream.Length, input.Length - pos);
            var blocks = (n + 15) / 16;
            for (var b = 0; b < blocks; b++)
            {
                counter.CopyTo(counters.AsSpan(b * 16, 16));
                for (var i = 15; i >= 0 && ++counter[i] == 0; i--) { }
            }
            aes.EncryptEcb(counters.AsSpan(0, blocks * 16), stream.AsSpan(0, blocks * 16), PaddingMode.None);
            for (var i = 0; i < n; i++) output[pos + i] = (byte)(input[pos + i] ^ stream[i]);
        }
        CryptographicOperations.ZeroMemory(stream);
    }

    private static void ChunkMac(Header h, long chunk, ReadOnlySpan<byte> nonceAndCipher, Span<byte> mac)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, h.MacKey!);
        Span<byte> num = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(num, chunk);
        hmac.AppendData(h.Nonce);
        hmac.AppendData(num);
        hmac.AppendData(nonceAndCipher);
        hmac.GetHashAndReset(mac);
    }

    private static void Aad(Span<byte> aad, long chunk, byte[] headerNonce)
    {
        BinaryPrimitives.WriteInt64BigEndian(aad, chunk);
        headerNonce.CopyTo(aad[8..]);
    }

    public static void EncryptChunk(Header h, long chunk, ReadOnlySpan<byte> plain, Span<byte> output)
    {
        if (h.Ctr)
        {
            var cnonce = output[..CtrNonceSize];
            RandomNumberGenerator.Fill(cnonce);
            Ctr(h.ContentKey, cnonce, plain, output.Slice(CtrNonceSize, plain.Length));
            ChunkMac(h, chunk, output[..(CtrNonceSize + plain.Length)], output.Slice(CtrNonceSize + plain.Length, MacSize));
            return;
        }
        Span<byte> aad = stackalloc byte[8 + NonceSize];
        Aad(aad, chunk, h.Nonce);
        var nonce = output[..NonceSize];
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(h.ContentKey, TagSize);
        gcm.Encrypt(nonce, plain, output.Slice(NonceSize, plain.Length), output.Slice(NonceSize + plain.Length, TagSize), aad);
    }

    public static void DecryptChunk(Header h, long chunk, ReadOnlySpan<byte> cipher, Span<byte> output)
    {
        if (cipher.Length <= h.Overhead) throw new FormatException("Block zu kurz.");
        var n = cipher.Length - h.Overhead;
        if (h.Ctr)
        {
            Span<byte> mac = stackalloc byte[MacSize];
            ChunkMac(h, chunk, cipher[..(CtrNonceSize + n)], mac);
            if (!CryptographicOperations.FixedTimeEquals(mac, cipher.Slice(CtrNonceSize + n, MacSize)))
                throw new CryptographicException($"Block {chunk} wurde verändert oder passt nicht zur Datei.");
            Ctr(h.ContentKey, cipher[..CtrNonceSize], cipher.Slice(CtrNonceSize, n), output[..n]);
            return;
        }
        Span<byte> aad = stackalloc byte[8 + NonceSize];
        Aad(aad, chunk, h.Nonce);
        try
        {
            using var gcm = new AesGcm(h.ContentKey, TagSize);
            gcm.Decrypt(cipher[..NonceSize], cipher.Slice(NonceSize, n), cipher.Slice(NonceSize + n, TagSize), output[..n], aad);
        }
        catch (CryptographicException) { throw new CryptographicException($"Block {chunk} wurde verändert oder passt nicht zur Datei."); }
    }

    /// <summary>Klartext aus <paramref name="source"/> verschlüsselt nach <paramref name="target"/> schreiben, Block für Block.</summary>
    public static async Task EncryptAsync(Masterkey key, Stream source, Stream target, CancellationToken ct = default)
    {
        var (h, head) = NewHeader(key);
        await target.WriteAsync(head, ct);
        var plain = new byte[ChunkPlain];
        var cipher = new byte[h.ChunkCipherSize];
        long chunk = 0;
        while (true)
        {
            var n = await source.ReadAtLeastAsync(plain, ChunkPlain, throwOnEndOfStream: false, ct);
            if (n == 0) break;
            EncryptChunk(h, chunk++, plain.AsSpan(0, n), cipher);
            await target.WriteAsync(cipher.AsMemory(0, n + h.Overhead), ct);
            if (n < ChunkPlain) break;
        }
        CryptographicOperations.ZeroMemory(plain);
    }

    public static byte[] Encrypt(Masterkey key, ReadOnlySpan<byte> plain)
    {
        var (h, head) = NewHeader(key);
        var result = new byte[CiphertextSize(plain.Length, h.Ctr)];
        head.CopyTo(result, 0);
        var pos = head.Length;
        for (long chunk = 0, off = 0; off < plain.Length; chunk++, off += ChunkPlain)
        {
            var n = (int)Math.Min(ChunkPlain, plain.Length - off);
            EncryptChunk(h, chunk, plain.Slice((int)off, n), result.AsSpan(pos, n + h.Overhead));
            pos += n + h.Overhead;
        }
        return result;
    }

    public static byte[] Decrypt(Masterkey key, ReadOnlySpan<byte> cipher)
    {
        var h = ReadHeader(key, cipher);
        var result = new byte[CleartextSize(cipher.Length, h.Ctr)];
        var pos = h.HeaderLength;
        for (long chunk = 0; pos < cipher.Length; chunk++)
        {
            var n = Math.Min(h.ChunkCipherSize, cipher.Length - pos);
            DecryptChunk(h, chunk, cipher.Slice(pos, n), result.AsSpan((int)(chunk * ChunkPlain)));
            pos += n;
        }
        return result;
    }
}

/// <summary>Entschlüsselnder Stream mit Länge und Suchen; liest nur die Blöcke, die gebraucht werden.</summary>
public sealed class DecryptingStream : Stream
{
    private readonly Stream _cipher;
    private readonly FileContent.Header _header;
    private readonly byte[] _plain = new byte[FileContent.ChunkPlain];
    private readonly byte[] _buf;
    private long _loaded = -1;
    private int _loadedLength;
    private long _pos;
    private readonly bool _leaveOpen;

    public DecryptingStream(Masterkey key, Stream cipher, bool leaveOpen = false)
    {
        if (!cipher.CanSeek) throw new ArgumentException("Der verschlüsselte Stream muss suchen können.");
        _cipher = cipher;
        _leaveOpen = leaveOpen;
        var head = new byte[FileContent.HeaderLength(key)];
        cipher.Position = 0;
        cipher.ReadExactly(head);
        _header = FileContent.ReadHeader(key, head);
        _buf = new byte[_header.ChunkCipherSize];
        Length = FileContent.CleartextSize(cipher.Length, _header.Ctr);
    }

    public override long Length { get; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Position { get => _pos; set => _pos = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }

    private void Load(long chunk)
    {
        if (_loaded == chunk) return;
        _cipher.Position = _header.HeaderLength + chunk * _header.ChunkCipherSize;
        var n = _cipher.ReadAtLeast(_buf, _header.ChunkCipherSize, throwOnEndOfStream: false);
        FileContent.DecryptChunk(_header, chunk, _buf.AsSpan(0, n), _plain);
        _loaded = chunk;
        _loadedLength = n - _header.Overhead;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _pos < Length)
        {
            var chunk = _pos / FileContent.ChunkPlain;
            Load(chunk);
            var inChunk = (int)(_pos - chunk * FileContent.ChunkPlain);
            var n = Math.Min(buffer.Length, _loadedLength - inChunk);
            _plain.AsSpan(inChunk, n).CopyTo(buffer);
            buffer = buffer[n..];
            _pos += n;
            total += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CryptographicOperations.ZeroMemory(_plain);
            CryptographicOperations.ZeroMemory(_header.ContentKey);
            if (!_leaveOpen) _cipher.Dispose();
        }
        base.Dispose(disposing);
    }
}
