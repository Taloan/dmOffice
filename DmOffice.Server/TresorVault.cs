using DmOffice.Core;
using DmOffice.Core.Cryptomator;
using DmOffice.Dav;

namespace DmOffice;

/// <summary>
/// Verschlüsselte Dateien des System-Tresors in der Dateiablage der Person (docs/tresor-cryptomator-konzept.md, 2.1):
/// <c>/dmVAULT/vault</c>. Kontingent, Blocksicherung und Spiegelung gelten wie für jede Datei; Versionen und
/// Papierkorb nicht (<see cref="FileStore.SystemVault"/>).
/// </summary>
public sealed class FileStoreVaultFiles(UserDb db, string root) : IVaultFiles
{
    public const string DefaultRoot = FileStore.SystemVault + "/vault";

    private string P(string rel) => rel.Length == 0 ? root : root + "/" + rel;

    public bool FileExists(string rel) => FileStore.Get(db, P(rel)) is { IsDir: false };
    public bool DirExists(string rel) => FileStore.Get(db, P(rel)) is { IsDir: true };
    public byte[] ReadAll(string rel) => FileStore.Data(db, P(rel)) ?? throw new FileNotFoundException("Diese Datei gibt es im Tresor nicht.");

    public Stream OpenRead(string rel)
    {
        var s = FileStore.OpenRead(db, P(rel));
        if (s.CanSeek) return s;
        // Zum Entschlüsseln muss gesucht werden können (z. B. Inhalt gerade erst aus dem Storage Extender geholt)
        var m = new MemoryStream();
        using (s) s.CopyTo(m);
        m.Position = 0;
        return m;
    }

    public async Task WriteAsync(string rel, Stream data, CancellationToken ct = default)
    {
        var path = P(rel);
        if (FileStore.Parent(path) is { Length: > 0 } dir) FileStore.EnsureDir(db, dir);
        using (FileStore.SystemAccess()) await FileStore.PutAsync(db, path, data, "application/octet-stream", ct: ct);
    }

    public void CreateDir(string rel) => FileStore.EnsureDir(db, P(rel));

    public List<(string Name, bool IsDir, long Size, DateTime Modified)> List(string rel) =>
        FileStore.Children(db, P(rel)).Select(e => (e.Path[(e.Path.LastIndexOf('/') + 1)..], e.IsDir, e.Size, e.Modified)).ToList();

    public void Delete(string rel) => FileStore.Delete(db, P(rel));

    public void Move(string from, string to)
    {
        var target = P(to);
        if (FileStore.Parent(target) is { Length: > 0 } dir) FileStore.EnsureDir(db, dir);
        FileStore.MoveOrCopy(db, P(from), target, overwrite: false, copy: false);
    }
}

/// <summary>Tresor-Dateien im Cryptomator-Tresor: Baum und Inhalt liegen dort, die Tabelle files ist das Verzeichnis dazu.</summary>
public sealed class CryptomatorTresorStorage(CryptomatorVault vault) : ITresorStorage, IDisposable
{
    public void Dispose() => vault.Dispose();
    public CryptomatorVault Vault => vault;

    private static string Abs(string path) => "/" + path;

    public async Task<(string? Ref, long Size)> WriteAsync(string path, Stream data, long maxLength, Func<Exception>? tooLarge, CancellationToken ct)
    {
        var counted = new CountingStream(data, maxLength, tooLarge);
        await vault.WriteAsync(Abs(path), counted, ct);
        return (null, counted.Count);
    }

    public Stream OpenRead(string path, string? reference) => vault.OpenRead(Abs(path));
    public void Delete(string path, string? reference, bool isDir) => vault.Delete(Abs(path));
    public void CreateFolder(string path) => vault.CreateDirectory(Abs(path));
    public void Move(string from, string to, bool isDir) => vault.Move(Abs(from), Abs(to));
    public void Release(string? reference) { }

    /// <summary>Zählt den Klartext beim Schreiben (Größe für die Tabelle) und bricht über der Grenze ab.</summary>
    private sealed class CountingStream(Stream inner, long max, Func<Exception>? tooLarge) : Stream
    {
        public long Count { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            Count += n;
            if (Count > max) throw tooLarge?.Invoke() ?? new IOException("Die Datei ist zu groß.");
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            Count += n;
            if (Count > max) throw tooLarge?.Invoke() ?? new IOException("Die Datei ist zu groß.");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
