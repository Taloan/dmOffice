using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DmOffice.Core;

/// <summary>Ein geschriebener Blob: Name (null bei leerem Inhalt - dafuer gibt es keine Datei), Groesse, SHA-256 des Klartexts.</summary>
public sealed record BlobInfo(string? Id, long Size, byte[] Sha256);

/// <summary>
/// Inhalte der Dateiablage als einzelne verschluesselte Dateien neben der Benutzerdatenbank, je Benutzer ein Ordner.
/// Kein Klartext auf der Platte: jede Datei hat einen eigenen Schluessel (HKDF aus dem Datenbankschluessel, ihrem
/// Namen und einem Zufallswert im Kopf) und wird in Bloecken von 64 KB mit AES-GCM verschluesselt - so laesst sie
/// sich als Stream schreiben und an beliebiger Stelle lesen (Range, Video spulen), ohne sie ganz zu laden.
/// Aufbau: "DMB1", 16 Byte Zufall, dann die Bloecke (Geheimtext + 16 Byte Tag). Nonce = Blocknummer; der letzte Block
/// ist im Nonce markiert, Abschneiden faellt also auf. Welche Zeilen auf einen Blob zeigen, steht in der Datenbank.
/// </summary>
public sealed class BlobStore
{
    public const int ChunkSize = 64 * 1024;
    internal const int TagSize = 16, SaltSize = 16;
    internal static readonly byte[] Magic = "DMB1"u8.ToArray();
    internal const int HeaderSize = 4 + SaltSize;

    private readonly byte[] _key;
    public string Dir { get; }

    public BlobStore(string dir, byte[] dbKey)
    {
        Dir = Path.GetFullPath(dir);
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, dbKey, 32, info: "dmoffice blobs v1"u8.ToArray());
    }

    private string TempDir => Path.Combine(Dir, "tmp");

    public static bool ValidId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    public string PathOf(string id)
    {
        if (!ValidId(id)) throw new ArgumentException("Ungültiger Blob-Name.");
        return Path.Combine(Dir, id[..2], id);
    }

    internal byte[] FileKey(string id, ReadOnlySpan<byte> salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt.ToArray(), Encoding.ASCII.GetBytes(id));

    internal static void Nonce(Span<byte> nonce, long chunk, bool final)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce, chunk);
        nonce[11] = final ? (byte)1 : (byte)0;
    }

    /// <summary>Neuer Blob zum Hineinschreiben; erst <see cref="BlobWriter.Complete"/> legt ihn an seinen Platz.</summary>
    /// <param name="maxLength">Mehr Bytes: die Ausnahme von <paramref name="tooLarge"/> (Speicher voll).</param>
    public BlobWriter Create(long maxLength = long.MaxValue, Func<Exception>? tooLarge = null)
    {
        Directory.CreateDirectory(TempDir);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new BlobWriter(this, id, Path.Combine(TempDir, id + ".part"), maxLength, tooLarge);
    }

    public BlobInfo Write(ReadOnlySpan<byte> data)
    {
        using var w = Create();
        w.Write(data);
        return w.Complete();
    }

    public async Task<BlobInfo> WriteAsync(Stream source, long maxLength = long.MaxValue, Func<Exception>? tooLarge = null, CancellationToken ct = default)
    {
        await using var w = Create(maxLength, tooLarge);
        await source.CopyToAsync(w, ChunkSize, ct);
        return w.Complete();
    }

    /// <summary>Entschluesselnder Stream mit Laenge und Suchen. Datei fehlt: FileNotFoundException.</summary>
    public Stream OpenRead(string id) => new BlobReadStream(this, id, PathOf(id));

    public byte[] ReadAll(string id)
    {
        using var s = OpenRead(id);
        var data = new byte[s.Length];
        s.ReadExactly(data);
        return data;
    }

    public bool Exists(string id) => File.Exists(PathOf(id));

    public void Delete(string id)
    {
        try { File.Delete(PathOf(id)); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Alle Blobs auf der Platte mit Aenderungszeit (fuer das Aufraeumen nach Abstuerzen).</summary>
    public IEnumerable<(string Id, DateTime Modified)> List()
    {
        if (!Directory.Exists(Dir)) yield break;
        foreach (var sub in Directory.EnumerateDirectories(Dir))
        {
            if (Path.GetFileName(sub) is not { Length: 2 } prefix || !prefix.All(Uri.IsHexDigit)) continue;
            foreach (var f in Directory.EnumerateFiles(sub))
            {
                var name = Path.GetFileName(f);
                if (ValidId(name)) yield return (name, File.GetLastWriteTimeUtc(f));
            }
        }
    }

    /// <summary>Halbfertiges von abgebrochenen Uploads, aelter als <paramref name="age"/>.</summary>
    public int CleanTemp(TimeSpan age)
    {
        if (!Directory.Exists(TempDir)) return 0;
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(TempDir))
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow - age)
                try { File.Delete(f); n++; } catch (IOException) { }
        return n;
    }

    /// <summary>Den ganzen Ordner loeschen (Benutzer geloescht).</summary>
    public void DeleteAll()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }
}

/// <summary>Schreibt einen Blob blockweise verschluesselt in eine temporaere Datei; Complete verschiebt sie an ihren Platz.
/// Ohne Complete (Abbruch, Ausnahme) wird sie beim Schliessen geloescht.</summary>
public sealed class BlobWriter : Stream
{
    private readonly BlobStore _store;
    private readonly string _temp;
    private readonly long _max;
    private readonly Func<Exception>? _tooLarge;
    private readonly FileStream _file;
    private readonly AesGcm _aes;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _plain = new byte[BlobStore.ChunkSize];
    private readonly byte[] _cipher = new byte[BlobStore.ChunkSize + BlobStore.TagSize];
    private int _fill;
    private long _chunk, _length;
    private bool _done;

    public string Id { get; }

    internal BlobWriter(BlobStore store, string id, string temp, long max, Func<Exception>? tooLarge)
    {
        _store = store; Id = id; _temp = temp; _max = max; _tooLarge = tooLarge;
        _file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None);
        try
        {
            var salt = RandomNumberGenerator.GetBytes(BlobStore.SaltSize);
            _file.Write(BlobStore.Magic);
            _file.Write(salt);
            _aes = new AesGcm(store.FileKey(id, salt), BlobStore.TagSize);
        }
        catch { _file.Dispose(); File.Delete(temp); throw; }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() { }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_done) throw new InvalidOperationException("Blob ist abgeschlossen.");
        if (_length + buffer.Length > _max) throw _tooLarge?.Invoke() ?? new IOException("Zu groß.");
        _hash.AppendData(buffer);
        _length += buffer.Length;
        while (buffer.Length > 0)
        {
            // Voller Block geht erst raus, wenn mehr kommt - der letzte muss als letzter markiert werden
            if (_fill == _plain.Length) Seal(final: false);
            int n = Math.Min(buffer.Length, _plain.Length - _fill);
            buffer[..n].CopyTo(_plain.AsSpan(_fill));
            _fill += n;
            buffer = buffer[n..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    private void Seal(bool final)
    {
        Span<byte> nonce = stackalloc byte[12];
        BlobStore.Nonce(nonce, _chunk++, final);
        _aes.Encrypt(nonce, _plain.AsSpan(0, _fill), _cipher.AsSpan(0, _fill), _cipher.AsSpan(_fill, BlobStore.TagSize));
        _file.Write(_cipher, 0, _fill + BlobStore.TagSize);
        _fill = 0;
    }

    /// <summary>Letzten Block schreiben, auf die Platte bringen, an den endgueltigen Platz legen.
    /// Leerer Inhalt: keine Datei, Id null.</summary>
    public BlobInfo Complete()
    {
        if (_done) throw new InvalidOperationException("Blob ist abgeschlossen.");
        _done = true;
        var sha = _hash.GetHashAndReset();
        if (_length == 0)
        {
            _file.Dispose();
            File.Delete(_temp);
            return new BlobInfo(null, 0, sha);
        }
        Seal(final: true);
        _file.Flush(flushToDisk: true);
        _file.Dispose();
        var target = _store.PathOf(Id);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(_temp, target);
        return new BlobInfo(Id, _length, sha);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
            _aes.Dispose();
            _hash.Dispose();
            if (File.Exists(_temp)) try { File.Delete(_temp); } catch (IOException) { }
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Liest einen Blob entschluesselt, mit Suchen: es wird nur der Block entschluesselt, in dem gelesen wird.
/// Ein veraenderter oder abgeschnittener Blob wirft CryptographicException.</summary>
internal sealed class BlobReadStream : Stream
{
    private readonly FileStream _file;
    private readonly AesGcm _aes;
    private readonly long _chunks, _length;
    private readonly byte[] _plain = new byte[BlobStore.ChunkSize];
    private readonly byte[] _cipher = new byte[BlobStore.ChunkSize + BlobStore.TagSize];
    private long _loaded = -1;
    private int _loadedLength;
    private long _pos;

    public BlobReadStream(BlobStore store, string id, string path)
    {
        // Delete erlaubt: Aufraeumen darf einen Blob entfernen, waehrend er noch ausgeliefert wird
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess);
        try
        {
            Span<byte> head = stackalloc byte[BlobStore.HeaderSize];
            if (_file.Length < BlobStore.HeaderSize + BlobStore.TagSize) throw new CryptographicException("Blob beschädigt.");
            _file.ReadExactly(head);
            if (!head[..4].SequenceEqual(BlobStore.Magic)) throw new CryptographicException("Kein Blob.");
            _aes = new AesGcm(store.FileKey(id, head[4..]), BlobStore.TagSize);
            long body = _file.Length - BlobStore.HeaderSize, block = BlobStore.ChunkSize + BlobStore.TagSize;
            _chunks = (body + block - 1) / block;
            long last = body - (_chunks - 1) * block - BlobStore.TagSize;
            if (last < 0) throw new CryptographicException("Blob beschädigt.");
            _length = (_chunks - 1) * BlobStore.ChunkSize + last;
        }
        catch { _file.Dispose(); throw; }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _pos;
        set { ArgumentOutOfRangeException.ThrowIfNegative(value); _pos = value; }
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _pos + offset,
        _ => _length + offset,
    };

    private void Load(long chunk)
    {
        if (_loaded == chunk) return;
        long block = BlobStore.ChunkSize + BlobStore.TagSize;
        bool final = chunk == _chunks - 1;
        int plain = final ? (int)(_length - chunk * BlobStore.ChunkSize) : BlobStore.ChunkSize;
        _file.Position = BlobStore.HeaderSize + chunk * block;
        _file.ReadExactly(_cipher, 0, plain + BlobStore.TagSize);
        Span<byte> nonce = stackalloc byte[12];
        BlobStore.Nonce(nonce, chunk, final);
        _loaded = -1;
        _aes.Decrypt(nonce, _cipher.AsSpan(0, plain), _cipher.AsSpan(plain, BlobStore.TagSize), _plain.AsSpan(0, plain));
        _loaded = chunk;
        _loadedLength = plain;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _pos < _length)
        {
            long chunk = _pos / BlobStore.ChunkSize;
            Load(chunk);
            int at = (int)(_pos - chunk * BlobStore.ChunkSize);
            int n = Math.Min(buffer.Length, _loadedLength - at);
            _plain.AsSpan(at, n).CopyTo(buffer);
            buffer = buffer[n..];
            _pos += n;
            total += n;
        }
        return total;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer, offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _file.Dispose(); _aes.Dispose(); }
        base.Dispose(disposing);
    }
}
