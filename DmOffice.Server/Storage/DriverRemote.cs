using DmOffice.Core;

namespace DmOffice.Storage;

/// <summary>
/// Ein Speicher-Treiber (WebDAV, Ordner) als Ziel fuer Kopie und Sicherung (IBlobRemote): die Schluessel der Kopie
/// („dmoffice/&lt;Benutzer&gt;/ab/&lt;Blob&gt;“, „dmoffice/backups/…“) werden zu Pfaden darunter.
/// </summary>
public sealed class DriverRemote(IStorageDriver driver) : IBlobRemote
{
    public async Task<HashSet<string>> ListAsync(string prefix, CancellationToken ct) =>
        (await ListSizesAsync(prefix, ct)).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Alles unter dem Praefix, Ordner fuer Ordner (WebDAV-Server erlauben selten „Tiefe unendlich“).</summary>
    public async Task<List<(string Key, long Size)>> ListSizesAsync(string prefix, CancellationToken ct)
    {
        var result = new List<(string, long)>();
        var root = StoragePath.Clean(prefix);
        if (await driver.StatAsync(root, ct) is not { IsDir: true } && root.Length > 0) return result;
        var todo = new Stack<string>([root]);
        while (todo.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = todo.Pop();
            List<DriverEntry> entries;
            try { entries = await driver.ListAsync(dir, ct); }
            catch (KeyNotFoundException) { continue; }
            foreach (var e in entries)
            {
                var path = StoragePath.Join(dir, e.Name);
                if (e.IsDir) todo.Push(path);
                else result.Add((path, e.Size));
            }
        }
        return result;
    }

    public async Task UploadAsync(string file, string key, CancellationToken ct)
    {
        await using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await driver.WriteAsync(key, f, ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct) => driver.DeleteAsync(key, ct);

    public async Task DownloadAsync(string key, string file, CancellationToken ct)
    {
        await using var s = await driver.OpenReadAsync(key, ct);
        await using var f = File.Create(file);
        await s.CopyToAsync(f, ct);
    }

    public async Task<long> SizeAsync(string key, CancellationToken ct) => await driver.StatAsync(key, ct) is { IsDir: false } e ? e.Size : -1;

    public Task<byte[]> ReadRangeAsync(string key, long offset, int count, CancellationToken ct) => driver.ReadRangeAsync(key, offset, count, ct);

    public void Dispose() => driver.Dispose();
}
