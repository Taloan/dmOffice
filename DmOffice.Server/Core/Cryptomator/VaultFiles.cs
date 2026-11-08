namespace DmOffice.Core.Cryptomator;

/// <summary>Die verschlüsselten Dateien eines Cryptomator-Tresors: wo sie liegen (Ordner auf der Platte, Dateiablage).
/// Pfade relativ zur Wurzel des Tresors mit „/“.</summary>
public interface IVaultFiles
{
    bool FileExists(string rel);
    bool DirExists(string rel);
    byte[] ReadAll(string rel);
    /// <summary>Lesen mit Suchen (für <see cref="DecryptingStream"/>).</summary>
    Stream OpenRead(string rel);
    /// <summary>Datei neu schreiben oder ersetzen - erst ganz, dann an ihren Platz.</summary>
    Task WriteAsync(string rel, Stream data, CancellationToken ct = default);
    void CreateDir(string rel);
    /// <summary>Einträge eines Ordners: Name, Ordner?, Größe, Änderungszeit.</summary>
    List<(string Name, bool IsDir, long Size, DateTime Modified)> List(string rel);
    /// <summary>Datei oder Ordner samt Inhalt endgültig entfernen.</summary>
    void Delete(string rel);
    void Move(string from, string to);
}

/// <summary>Tresor in einem Ordner auf der Platte (Formatprüfung, Gegenprobe mit Cryptomator).</summary>
public sealed class DiskVaultFiles(string root) : IVaultFiles
{
    public string Root { get; } = Path.GetFullPath(root);

    private string Full(string rel) => rel.Length == 0 ? Root : Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    public bool FileExists(string rel) => File.Exists(Full(rel));
    public bool DirExists(string rel) => Directory.Exists(Full(rel));
    public byte[] ReadAll(string rel) => File.ReadAllBytes(Full(rel));
    public Stream OpenRead(string rel) => File.OpenRead(Full(rel));

    public async Task WriteAsync(string rel, Stream data, CancellationToken ct = default)
    {
        var target = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var f = File.Create(tmp)) await data.CopyToAsync(f, ct);
            File.Move(tmp, target, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public void CreateDir(string rel) => Directory.CreateDirectory(Full(rel));

    public List<(string Name, bool IsDir, long Size, DateTime Modified)> List(string rel)
    {
        var dir = new DirectoryInfo(Full(rel));
        if (!dir.Exists) return [];
        return dir.EnumerateFileSystemInfos().Where(i => !i.Name.Contains(".tmp-"))
            .Select(i => (i.Name, i is DirectoryInfo, i is FileInfo f ? f.Length : 0L, i.LastWriteTimeUtc)).ToList();
    }

    public void Delete(string rel)
    {
        var p = Full(rel);
        if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
        else if (File.Exists(p)) File.Delete(p);
    }

    public void Move(string from, string to)
    {
        var a = Full(from);
        var b = Full(to);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        if (Directory.Exists(a)) Directory.Move(a, b); else File.Move(a, b);
    }
}

/// <summary>Verschlüsselnder Lese-Stream: Klartext rein, Cryptomator-Inhalt (Kopf und Blöcke) heraus - zum Schreiben ohne Zwischendatei.</summary>
public sealed class EncryptingStream : Stream
{
    private readonly Stream _plain;
    private readonly FileContent.Header _header;
    private readonly byte[] _in = new byte[FileContent.ChunkPlain];
    private byte[] _out;
    private int _outPos, _outLen;
    private long _chunk;
    private bool _done;

    public EncryptingStream(Masterkey key, Stream plain)
    {
        _plain = plain;
        (_header, _out) = FileContent.NewHeader(key);
        _outLen = _out.Length;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_outPos == _outLen && !Next()) return 0;
        var n = Math.Min(buffer.Length, _outLen - _outPos);
        _out.AsSpan(_outPos, n).CopyTo(buffer);
        _outPos += n;
        return n;
    }

    /// <summary>Asynchron, damit auch der Rumpf einer Anfrage (nur asynchron lesbar) als Quelle taugt.</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_outPos == _outLen)
        {
            if (_done) return 0;
            var read = await _plain.ReadAtLeastAsync(_in, FileContent.ChunkPlain, throwOnEndOfStream: false, ct);
            if (!Encrypt(read)) return 0;
        }
        var n = Math.Min(buffer.Length, _outLen - _outPos);
        _out.AsMemory(_outPos, n).CopyTo(buffer);
        _outPos += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private bool Next()
    {
        if (_done) return false;
        return Encrypt(_plain.ReadAtLeast(_in, FileContent.ChunkPlain, throwOnEndOfStream: false));
    }

    private bool Encrypt(int n)
    {
        if (n < FileContent.ChunkPlain) _done = true;
        if (n == 0) return false;
        if (_out.Length < FileContent.ChunkCipher) _out = new byte[FileContent.ChunkCipher];
        FileContent.EncryptChunk(_header, _chunk++, _in.AsSpan(0, n), _out);
        _outPos = 0;
        _outLen = n + FileContent.NonceSize + FileContent.TagSize;
        return true;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_header.ContentKey);
        base.Dispose(disposing);
    }
}
