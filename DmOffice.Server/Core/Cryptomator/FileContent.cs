using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DmOffice.Core.Cryptomator;

/// <summary>
/// Inhalte im Vault-Format 8 (SIV_GCM): Kopf mit 68 Byte (Nonce 12 | AES-GCM über 8 Byte 0xFF und den 32-Byte-Inhaltsschlüssel,
/// mit dem Verschlüsselungs-Masterkey | Prüfsumme 16), danach Blöcke mit 32 KiB Klartext: Nonce 12 | Geheimtext | Prüfsumme 16.
/// Zugehörige Daten je Block: Blocknummer (8 Byte, Big Endian) || Kopf-Nonce. Eine leere Datei ist nur der Kopf.
/// </summary>
public static class FileContent
{
    public const int NonceSize = 12, TagSize = 16, HeaderSize = NonceSize + 8 + 32 + TagSize;
    public const int ChunkPlain = 32 * 1024, ChunkCipher = NonceSize + ChunkPlain + TagSize;

    public sealed record Header(byte[] Nonce, byte[] ContentKey);

    public static long CleartextSize(long ciphertextSize)
    {
        if (ciphertextSize < HeaderSize) throw new FormatException("Datei kürzer als ihr Kopf.");
        var body = ciphertextSize - HeaderSize;
        var full = body / ChunkCipher;
        var rest = body % ChunkCipher;
        if (rest is > 0 and <= NonceSize + TagSize) throw new FormatException("Datei hat einen unvollständigen Block.");
        return full * ChunkPlain + (rest == 0 ? 0 : rest - NonceSize - TagSize);
    }

    public static long CiphertextSize(long cleartextSize)
    {
        var full = cleartextSize / ChunkPlain;
        var rest = cleartextSize % ChunkPlain;
        return HeaderSize + full * ChunkCipher + (rest == 0 ? 0 : rest + NonceSize + TagSize);
    }

    public static (Header Header, byte[] Bytes) NewHeader(Masterkey key)
    {
        var h = new Header(RandomNumberGenerator.GetBytes(NonceSize), RandomNumberGenerator.GetBytes(32));
        var plain = new byte[40];
        plain.AsSpan(0, 8).Fill(0xFF);
        h.ContentKey.CopyTo(plain, 8);
        var bytes = new byte[HeaderSize];
        h.Nonce.CopyTo(bytes, 0);
        using (var gcm = new AesGcm(key.EncKey, TagSize))
            gcm.Encrypt(h.Nonce, plain, bytes.AsSpan(NonceSize, 40), bytes.AsSpan(NonceSize + 40, TagSize));
        CryptographicOperations.ZeroMemory(plain);
        return (h, bytes);
    }

    public static Header ReadHeader(Masterkey key, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize) throw new FormatException("Datei kürzer als ihr Kopf.");
        var plain = new byte[40];
        try
        {
            using var gcm = new AesGcm(key.EncKey, TagSize);
            gcm.Decrypt(bytes[..NonceSize], bytes.Slice(NonceSize, 40), bytes.Slice(NonceSize + 40, TagSize), plain);
        }
        catch (CryptographicException) { throw new CryptographicException("Kopf der Datei passt nicht zum Masterkey oder wurde verändert."); }
        return new Header(bytes[..NonceSize].ToArray(), plain[8..]);
    }

    private static void Aad(Span<byte> aad, long chunk, byte[] headerNonce)
    {
        BinaryPrimitives.WriteInt64BigEndian(aad, chunk);
        headerNonce.CopyTo(aad[8..]);
    }

    public static void EncryptChunk(Header h, long chunk, ReadOnlySpan<byte> plain, Span<byte> output)
    {
        Span<byte> aad = stackalloc byte[8 + NonceSize];
        Aad(aad, chunk, h.Nonce);
        var nonce = output[..NonceSize];
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(h.ContentKey, TagSize);
        gcm.Encrypt(nonce, plain, output.Slice(NonceSize, plain.Length), output.Slice(NonceSize + plain.Length, TagSize), aad);
    }

    public static void DecryptChunk(Header h, long chunk, ReadOnlySpan<byte> cipher, Span<byte> output)
    {
        if (cipher.Length <= NonceSize + TagSize) throw new FormatException("Block zu kurz.");
        Span<byte> aad = stackalloc byte[8 + NonceSize];
        Aad(aad, chunk, h.Nonce);
        var n = cipher.Length - NonceSize - TagSize;
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
        var cipher = new byte[ChunkCipher];
        long chunk = 0;
        while (true)
        {
            var n = await source.ReadAtLeastAsync(plain, ChunkPlain, throwOnEndOfStream: false, ct);
            if (n == 0) break;
            EncryptChunk(h, chunk++, plain.AsSpan(0, n), cipher);
            await target.WriteAsync(cipher.AsMemory(0, n + NonceSize + TagSize), ct);
            if (n < ChunkPlain) break;
        }
        CryptographicOperations.ZeroMemory(plain);
    }

    public static byte[] Encrypt(Masterkey key, ReadOnlySpan<byte> plain)
    {
        var (h, head) = NewHeader(key);
        var result = new byte[CiphertextSize(plain.Length)];
        head.CopyTo(result, 0);
        var pos = HeaderSize;
        for (long chunk = 0, off = 0; off < plain.Length; chunk++, off += ChunkPlain)
        {
            var n = (int)Math.Min(ChunkPlain, plain.Length - off);
            EncryptChunk(h, chunk, plain.Slice((int)off, n), result.AsSpan(pos, n + NonceSize + TagSize));
            pos += n + NonceSize + TagSize;
        }
        return result;
    }

    public static byte[] Decrypt(Masterkey key, ReadOnlySpan<byte> cipher)
    {
        var h = ReadHeader(key, cipher);
        var result = new byte[CleartextSize(cipher.Length)];
        var pos = HeaderSize;
        for (long chunk = 0; pos < cipher.Length; chunk++)
        {
            var n = Math.Min(ChunkCipher, cipher.Length - pos);
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
    private readonly byte[] _buf = new byte[FileContent.ChunkCipher];
    private long _loaded = -1;
    private int _loadedLength;
    private long _pos;
    private readonly bool _leaveOpen;

    public DecryptingStream(Masterkey key, Stream cipher, bool leaveOpen = false)
    {
        if (!cipher.CanSeek) throw new ArgumentException("Der verschlüsselte Stream muss suchen können.");
        _cipher = cipher;
        _leaveOpen = leaveOpen;
        var head = new byte[FileContent.HeaderSize];
        cipher.Position = 0;
        cipher.ReadExactly(head);
        _header = FileContent.ReadHeader(key, head);
        Length = FileContent.CleartextSize(cipher.Length);
    }

    public override long Length { get; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Position { get => _pos; set => _pos = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }

    private void Load(long chunk)
    {
        if (_loaded == chunk) return;
        _cipher.Position = FileContent.HeaderSize + chunk * FileContent.ChunkCipher;
        var n = _cipher.ReadAtLeast(_buf, FileContent.ChunkCipher, throwOnEndOfStream: false);
        FileContent.DecryptChunk(_header, chunk, _buf.AsSpan(0, n), _plain);
        _loaded = chunk;
        _loadedLength = n - FileContent.NonceSize - FileContent.TagSize;
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
