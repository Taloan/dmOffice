using DmOffice.Core.Cryptomator;

namespace DmOffice.Storage;

/// <summary>
/// Cryptomator-Tresor in einem externen Ordner (WebDAV, OneDrive, pCloud, Serverordner): die Dateien des Tresors über den Treiber
/// des Ziels. Gelesen wird in Bereichen (der Tresor sucht in den Blöcken), nicht die ganze Datei.
/// </summary>
public sealed class DriverVaultFiles(IStorageDriver driver, string root) : IVaultFiles, IDisposable
{
    private string P(string rel) => StoragePath.Join(root, rel);

    private static T Run<T>(Task<T> t) => t.GetAwaiter().GetResult();
    private static void Run(Task t) => t.GetAwaiter().GetResult();

    public bool FileExists(string rel) => Run(driver.StatAsync(P(rel), default)) is { IsDir: false };
    public bool DirExists(string rel) => Run(driver.StatAsync(P(rel), default)) is { IsDir: true };

    public byte[] ReadAll(string rel)
    {
        using var s = Run(driver.OpenReadAsync(P(rel), default));
        var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    public Stream OpenRead(string rel)
    {
        var path = P(rel);
        var stat = Run(driver.StatAsync(path, default)) ?? throw new FileNotFoundException("Diese Datei gibt es im Tresor nicht.");
        return new RangeStream(driver, path, stat.Size);
    }

    public Task WriteAsync(string rel, Stream data, CancellationToken ct = default) => driver.WriteAsync(P(rel), data, ct);
    public void CreateDir(string rel) => Run(driver.MkdirAsync(P(rel), default));

    public List<(string Name, bool IsDir, long Size, DateTime Modified)> List(string rel)
    {
        try { return Run(driver.ListAsync(P(rel), default)).Select(e => (e.Name, e.IsDir, e.Size, e.Modified)).ToList(); }
        catch (KeyNotFoundException) { return []; }
    }

    public void Delete(string rel) => Run(driver.DeleteAsync(P(rel), default));
    public void Move(string from, string to) => Run(driver.MoveAsync(P(from), P(to), default));

    public void Dispose() => driver.Dispose();

    /// <summary>Lesen mit Suchen über Bereichsanfragen, mit kleinem Puffer (ein Cryptomator-Block ist 32 KB plus Kopf).</summary>
    private sealed class RangeStream(IStorageDriver driver, string path, long length) : Stream
    {
        private const int Block = 256 * 1024;
        private byte[] _buf = [];
        private long _bufAt = -1;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= length || count == 0) return 0;
            if (_bufAt < 0 || Position < _bufAt || Position >= _bufAt + _buf.Length)
            {
                _bufAt = Position;
                _buf = driver.ReadRangeAsync(path, Position, (int)Math.Min(Block, length - Position), default).GetAwaiter().GetResult();
                if (_buf.Length == 0) return 0;
            }
            var at = (int)(Position - _bufAt);
            var n = Math.Min(count, _buf.Length - at);
            Array.Copy(_buf, at, buffer, offset, n);
            Position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
