using DmOffice.Storage;

namespace DmOffice.Audio;

/// <summary>
/// Datei eines externen Ordners als Stream mit Spulen: liest in Blöcken über <see cref="IStorageDriver.ReadRangeAsync"/>.
/// Für WebDAV, wo <see cref="IStorageDriver.OpenReadAsync"/> nur vorwärts liest - die Tags am Ende (ID3v1, APE, M4A mit
/// „moov“ hinten) und das Spulen beim Abspielen brauchen beliebige Stellen.
/// </summary>
public sealed class DriverStream(IStorageDriver driver, string path, long length, int block = 256 * 1024) : Stream
{
    private long _pos;
    private long _bufAt = -1;
    private byte[] _buf = [];

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, length); }

    /// <summary>Letzter Lesefehler - ATL fängt Ausnahmen selbst ab und liefert dann leere Tags; so sieht man, dass es am Speicher lag.</summary>
    public Exception? Error { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult(); }
        catch (Exception e) { Error = e; throw; }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pos >= length || buffer.Length == 0) return 0;
        if (_bufAt < 0 || _pos < _bufAt || _pos >= _bufAt + _buf.Length)
        {
            // Große Lesewünsche (Abspielen) in einem Stück, kleine (Tags) in Blöcken
            var want = (int)Math.Min(Math.Max(block, buffer.Length), length - _pos);
            _buf = await driver.ReadRangeAsync(path, _pos, want, ct);
            _bufAt = _pos;
            if (_buf.Length == 0) return 0;
        }
        var from = (int)(_pos - _bufAt);
        var n = Math.Min(buffer.Length, _buf.Length - from);
        _buf.AsMemory(from, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _pos + offset,
        _ => length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
