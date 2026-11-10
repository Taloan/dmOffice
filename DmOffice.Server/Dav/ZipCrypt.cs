using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DmOffice.Dav;

/// <summary>Verschlüsselung eines ZIP: keine, ZipCrypto (alt, öffnet auch der Windows-Explorer) oder AES nach WinZip (AE-2).</summary>
public enum ZipEncryption { None, ZipCrypto, Aes128, Aes192, Aes256 }

/// <summary>
/// ZIP schreiben mit Passwort - System.IO.Compression und SharpCompress können das nicht. Jeder Eintrag wird erst gepackt
/// (Deflate) in einen Zwischenspeicher geschrieben (temp; in dmOffice ein verschlüsselter Blob), dann mit CRC und Größen im
/// Kopf verschlüsselt ausgegeben: ZipCrypto (PKWARE APPNOTE 6.1) oder AES-CTR mit PBKDF2-HMAC-SHA1 und HMAC-SHA1 (WinZip AE-2,
/// Methode 99, Zusatzfeld 0x9901). Keine Datenbeschreibung nach dem Eintrag: verschlüsselte ZIPs mit einer solchen liest
/// SharpCompress nicht. Ohne ZIP64: über 4 GB oder 65535 Einträge bricht es ab (geändert werden ohnehin nur Archive bis
/// <see cref="Archives.MaxEditBytes"/>).
/// </summary>
/// <param name="temp">Zwischenspeicher je Eintrag; ohne: im Arbeitsspeicher (Tests).</param>
public sealed class ZipCryptWriter(Stream output, string? password, ZipEncryption encryption, Func<ZipCryptWriter.ITemp>? temp = null)
{
    /// <summary>Zwischenspeicher für einen gepackten Eintrag: erst schreiben, dann einmal lesen.</summary>
    public interface ITemp : IDisposable
    {
        Stream Writer { get; }
        Stream Read();
    }

    private sealed class MemoryTemp : ITemp
    {
        private readonly MemoryStream _m = new();
        public Stream Writer => _m;
        public Stream Read() => new MemoryStream(_m.GetBuffer(), 0, (int)_m.Length, false);
        public void Dispose() => _m.Dispose();
    }

    private sealed record Central(byte[] Name, ushort Version, ushort Flags, ushort Method, uint DosTime, uint Crc, long Packed, long Size, long Offset, byte[] Extra, bool Dir);

    private readonly List<Central> _central = [];
    // ZipCrypto kennt keine Zeichenkodierung für das Passwort: 7-Zip lässt dort nur ASCII zu, SharpCompress liest Umlaute falsch
    private readonly bool _checked = encryption != ZipEncryption.ZipCrypto || (password ?? "").All(char.IsAscii)
        ? true : throw new ArchiveException("Das Archiv nutzt die alte ZIP-Verschlüsselung (ZipCrypto). Mit Umlauten oder Sonderzeichen im Passwort lässt sie sich nicht zuverlässig schreiben – Lesen und Entpacken gehen.");
    private long _pos;
    private bool Encrypted => encryption != ZipEncryption.None;

    private void Out(ReadOnlySpan<byte> data) { output.Write(data); _pos += data.Length; }

    private static uint DosTime(DateTime t)
    {
        t = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        if (t.Year > 2107) t = new DateTime(2107, 12, 31);
        return (uint)((t.Year - 1980) << 25 | t.Month << 21 | t.Day << 16 | t.Hour << 11 | t.Minute << 5 | t.Second / 2);
    }

    private int AesBits => encryption switch { ZipEncryption.Aes128 => 128, ZipEncryption.Aes192 => 192, _ => 256 };

    private void Header(byte[] name, ushort version, ushort flags, ushort method, uint time, uint crc, long packed, long size, byte[] extra)
    {
        Span<byte> h = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], version);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], method);
        BinaryPrimitives.WriteUInt32LittleEndian(h[10..], time);
        BinaryPrimitives.WriteUInt32LittleEndian(h[14..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[18..], (uint)packed);
        BinaryPrimitives.WriteUInt32LittleEndian(h[22..], (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)extra.Length);
        Out(h);
        Out(name);
        Out(extra);
    }

    private static byte[] NameOf(string name) => Encoding.UTF8.GetBytes(name);

    private void Check()
    {
        if (_central.Count >= 65535 || _pos > uint.MaxValue) throw new ArchiveException("Das verschlüsselte Archiv würde zu groß (über 4 GB oder 65535 Einträge).");
    }

    public void AddDirectory(string name, DateTime modified)
    {
        Check();
        var n = NameOf(name.TrimEnd('/') + "/");
        var offset = _pos;
        var time = DosTime(modified);
        Header(n, 20, 0x0800, 0, time, 0, 0, 0, []);
        _central.Add(new Central(n, 20, 0x0800, 0, time, 0, 0, 0, offset, [], true));
    }

    /// <param name="fast">Schon gepackte Inhalte (Fotos, Videos …): nicht noch einmal packen.</param>
    public void AddFile(string name, Stream data, DateTime modified, bool fast, CancellationToken ct = default)
    {
        Check();
        var n = NameOf(name);
        var time = DosTime(modified);
        bool aes = encryption is ZipEncryption.Aes128 or ZipEncryption.Aes192 or ZipEncryption.Aes256;
        // Bit 0: verschlüsselt, Bit 11: Name in UTF-8
        ushort flags = (ushort)(0x0800 | (Encrypted ? 1 : 0));
        ushort method = aes ? (ushort)99 : (ushort)8;
        ushort version = aes ? (ushort)51 : (ushort)20;
        // AE-2, Hersteller „AE“, Stärke, eigentliche Methode Deflate
        byte[] extra = aes ? [0x01, 0x99, 7, 0, 2, 0, (byte)'A', (byte)'E', (byte)(AesBits / 64 - 1), 8, 0] : [];

        using var buffer = temp?.Invoke() ?? new MemoryTemp();
        // 1. Packen, dabei CRC und Länge des Inhalts
        var hashing = new CrcCounting(data);
        using (var deflate = new DeflateStream(buffer.Writer, fast ? CompressionLevel.NoCompression : CompressionLevel.Optimal, leaveOpen: true))
        {
            var chunk = new byte[256 * 1024];
            int read;
            while ((read = hashing.Read(chunk)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                deflate.Write(chunk, 0, read);
            }
        }
        var size = hashing.Length;
        using var packedData = buffer.Read();
        // 2. Kopf mit den Größen, dann verschlüsselt ausgeben
        int keyLen = AesBits / 8;
        long packed = packedData.Length + (encryption == ZipEncryption.ZipCrypto ? 12 : aes ? keyLen / 2 + 2 + 10 : 0);
        if (packed > uint.MaxValue || size > uint.MaxValue) throw new ArchiveException("Ein Eintrag ist zu groß für ein verschlüsseltes Archiv (über 4 GB).");
        // AE-2: keine CRC (die Prüfung macht der HMAC)
        var crc = aes ? 0 : hashing.Crc;
        var offset = _pos;
        Header(n, version, flags, method, time, crc, packed, size, extra);
        var counted = new Counting(output);
        Stream target = counted;
        AesCtrStream? ac = null;
        if (encryption == ZipEncryption.ZipCrypto)
        {
            var zc = new ZipCryptoStream(counted, password ?? "");
            // 12 Bytes Vorspann, das letzte zum Prüfen des Passworts: das obere Byte der CRC
            var head = RandomNumberGenerator.GetBytes(12);
            head[11] = (byte)(hashing.Crc >> 24);
            zc.Write(head);
            target = zc;
        }
        else if (aes) target = ac = new AesCtrStream(counted, password ?? "", AesBits);
        packedData.CopyTo(target, 256 * 1024);
        ac?.Finish();
        _pos += counted.Written;
        if (counted.Written != packed) throw new ArchiveException("Interner Fehler beim Verschlüsseln.");
        _central.Add(new Central(n, version, flags, method, time, crc, packed, size, offset, extra, false));
    }

    /// <summary>Zentralverzeichnis und Abschluss schreiben.</summary>
    public void Finish()
    {
        Check();
        var start = _pos;
        foreach (var c in _central)
        {
            Span<byte> h = stackalloc byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(h, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 63);            // erstellt mit: MS-DOS, Version 6.3
            BinaryPrimitives.WriteUInt16LittleEndian(h[6..], c.Version);
            BinaryPrimitives.WriteUInt16LittleEndian(h[8..], c.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(h[10..], c.Method);
            BinaryPrimitives.WriteUInt32LittleEndian(h[12..], c.DosTime);
            BinaryPrimitives.WriteUInt32LittleEndian(h[16..], c.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)c.Packed);
            BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)c.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)c.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(h[30..], (ushort)c.Extra.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h[38..], c.Dir ? 0x10u : 0x20u);   // Ordner bzw. Archiv-Attribut
            BinaryPrimitives.WriteUInt32LittleEndian(h[42..], (uint)c.Offset);
            Out(h);
            Out(c.Name);
            Out(c.Extra);
        }
        var length = _pos - start;
        if (_pos > uint.MaxValue) throw new ArchiveException("Das verschlüsselte Archiv würde zu groß (über 4 GB).");
        Span<byte> e = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(e, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(e[8..], (ushort)_central.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(e[10..], (ushort)_central.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(e[16..], (uint)start);
        Out(e);
        output.Flush();
    }

    /// <summary>
    /// Wie ist das ZIP verschlüsselt? Aus dem Zentralverzeichnis: der erste verschlüsselte Eintrag gibt es vor (AES mit Stärke aus
    /// dem Zusatzfeld 0x9901, sonst ZipCrypto). Unverschlüsselt oder nicht lesbar: None.
    /// </summary>
    public static ZipEncryption Inspect(Stream zip)
    {
        if (!zip.CanSeek || zip.Length < 22) return ZipEncryption.None;
        var tail = (int)Math.Min(zip.Length, 22 + 65535);
        var buf = new byte[tail];
        zip.Position = zip.Length - tail;
        zip.ReadExactly(buf);
        int eocd = -1;
        for (int i = tail - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) return ZipEncryption.None;
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(eocd + 16));
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(eocd + 12));
        // ZIP64: Ort des Verzeichnisses im ZIP64-Abschluss
        if (cdOffset == uint.MaxValue && eocd >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(eocd - 20)) == 0x07064b50)
        {
            var z64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(eocd - 12));
            var rec = new byte[56];
            zip.Position = z64;
            zip.ReadExactly(rec);
            if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != 0x06064b50) return ZipEncryption.None;
            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(40));
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
        }
        if (cdOffset < 0 || cdOffset + cdSize > zip.Length) return ZipEncryption.None;
        zip.Position = cdOffset;
        var reader = new BufferedStream(zip, 64 * 1024);
        var h = new byte[46];
        for (long read = 0; read + 46 <= cdSize;)
        {
            reader.ReadExactly(h);
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x02014b50) break;
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(10));
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(28)), extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(30)),
                commentLen = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(32));
            var rest = new byte[nameLen + extraLen + commentLen];
            reader.ReadExactly(rest);
            read += 46 + rest.Length;
            if ((flags & 1) == 0) continue;
            if (method != 99) return ZipEncryption.ZipCrypto;
            var extra = rest.AsSpan(nameLen, extraLen);
            for (int i = 0; i + 4 <= extra.Length;)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[i..]);
                var len = BinaryPrimitives.ReadUInt16LittleEndian(extra[(i + 2)..]);
                if (id == 0x9901 && len >= 7 && i + 4 + len <= extra.Length)
                    return extra[i + 8] switch { 1 => ZipEncryption.Aes128, 2 => ZipEncryption.Aes192, _ => ZipEncryption.Aes256 };
                i += 4 + len;
            }
            return ZipEncryption.Aes256;
        }
        return ZipEncryption.None;
    }

    // ---------------- Hilfsströme ----------------

    internal static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(uint crc, byte b) => CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);

    /// <summary>Liest durch und rechnet CRC-32 und Länge mit.</summary>
    private sealed class CrcCounting(Stream inner)
    {
        private uint _crc = 0xffffffff;
        public long Length { get; private set; }
        public uint Crc => _crc ^ 0xffffffff;
        public int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            foreach (var b in buffer[..n]) _crc = ZipCryptWriter.Crc(_crc, b);
            Length += n;
            return n;
        }
    }

    /// <summary>Zählt, was geschrieben wird.</summary>
    private sealed class Counting(Stream inner) : WriteOnly
    {
        public long Written { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); Written += buffer.Length; }
    }

    /// <summary>ZipCrypto (PKWARE): drei Schlüssel aus dem Passwort, jedes Byte mit dem Klartext fortgeschrieben.</summary>
    internal sealed class ZipCryptoStream : WriteOnly
    {
        private readonly Stream _inner;
        private uint _k0 = 0x12345678, _k1 = 0x23456789, _k2 = 0x34567890;
        public ZipCryptoStream(Stream inner, string password)
        {
            _inner = inner;
            foreach (var b in Encoding.ASCII.GetBytes(password)) Update(b);
        }
        private void Update(byte b)
        {
            _k0 = Crc(_k0, b);
            _k1 = (_k1 + (_k0 & 0xff)) * 134775813 + 1;
            _k2 = Crc(_k2, (byte)(_k1 >> 24));
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var o = new byte[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
            {
                var t = (ushort)(_k2 | 2);
                o[i] = (byte)(buffer[i] ^ (byte)((t * (t ^ 1)) >> 8));
                Update(buffer[i]);
            }
            _inner.Write(o);
        }
    }

    /// <summary>AES-CTR nach WinZip: Salz und Prüfwert vorn, Zähler ab 1 (little endian), HMAC-SHA1 über das Verschlüsselte (10 Bytes) hinten.</summary>
    internal sealed class AesCtrStream : WriteOnly
    {
        private readonly Stream _inner;
        private readonly ICryptoTransform _ecb;
        private readonly IncrementalHash _mac;
        private readonly byte[] _counter = new byte[16], _block = new byte[16];
        private int _used = 16;

        public AesCtrStream(Stream inner, string password, int bits)
        {
            _inner = inner;
            int keyLen = bits / 8;
            var salt = RandomNumberGenerator.GetBytes(keyLen / 2);
            var keys = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 1000, HashAlgorithmName.SHA1, keyLen * 2 + 2);
            using var aes = Aes.Create();
            aes.Key = keys[..keyLen];
            // Der Zähler wird blockweise verschlüsselt (ECB), das Ergebnis mit dem Inhalt verknüpft
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            _ecb = aes.CreateEncryptor();
            _mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, keys[keyLen..(keyLen * 2)]);
            inner.Write(salt);
            inner.Write(keys.AsSpan(keyLen * 2, 2));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var o = new byte[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
            {
                if (_used == 16)
                {
                    for (int k = 0; k < 16 && ++_counter[k] == 0; k++) { }
                    _ecb.TransformBlock(_counter, 0, 16, _block, 0);
                    _used = 0;
                }
                o[i] = (byte)(buffer[i] ^ _block[_used++]);
            }
            _mac.AppendData(o);
            _inner.Write(o);
        }

        public void Finish()
        {
            _inner.Write(_mac.GetHashAndReset().AsSpan(0, 10));
            _ecb.Dispose();
            _mac.Dispose();
        }
    }

    internal abstract class WriteOnly : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public abstract override void Write(ReadOnlySpan<byte> buffer);
    }
}
