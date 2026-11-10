namespace DmOffice.Storage;

/// <summary>
/// Nur ausgewählte Ordner der obersten Ebene eines fremden Speichers (externe Ordner: „nicht alles davon in dmOffice“).
/// Oben erscheinen nur diese Ordner; alles darunter geht normal. Pfade außerhalb gibt es nicht, und oben lässt sich
/// nichts anlegen, verschieben oder löschen.
/// </summary>
public sealed class SubsetDriver(IStorageDriver inner, IEnumerable<string> include) : IStorageDriver
{
    private readonly HashSet<string> _include = new(include, StringComparer.Ordinal);

    private static string Top(string path) => path.Split('/')[0];

    /// <summary>Pfad liegt in einem der freigegebenen Ordner (oder ist einer davon).</summary>
    private bool Inside(string path) => path.Length > 0 && _include.Contains(Top(path));

    private string Read(string path) => path.Length == 0 || Inside(path) ? path : throw new KeyNotFoundException("Diesen Ordner gibt es hier nicht.");

    /// <summary>Ändern nur unterhalb eines freigegebenen Ordners - nicht oben und nicht an ihm selbst.</summary>
    private string Write(string path) => Inside(path) && path.Contains('/') ? path
        : throw new UnauthorizedAccessException("Hier lassen sich nur die Inhalte der freigegebenen Ordner ändern.");

    public async Task<List<DriverEntry>> ListAsync(string path, CancellationToken ct)
    {
        var list = await inner.ListAsync(Read(path), ct);
        return path.Length == 0 ? list.Where(e => e.IsDir && _include.Contains(e.Name)).ToList() : list;
    }

    public Task<DriverEntry?> StatAsync(string path, CancellationToken ct) => path.Length == 0 || Inside(path) ? inner.StatAsync(path, ct) : Task.FromResult<DriverEntry?>(null);
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(Read(path), ct);
    public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct) => inner.ReadRangeAsync(Read(path), offset, count, ct);
    public Task WriteAsync(string path, Stream data, CancellationToken ct) => inner.WriteAsync(Write(path), data, ct);
    public Task MkdirAsync(string path, CancellationToken ct) => inner.MkdirAsync(Write(path), ct);
    public Task SetModifiedAsync(string path, DateTime utc, CancellationToken ct) => inner.SetModifiedAsync(Write(path), utc, ct);
    public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(Write(path), ct);
    public Task MoveAsync(string from, string to, CancellationToken ct) => inner.MoveAsync(Write(from), Write(to), ct);
    public Task<DriverSpace?> SpaceAsync(CancellationToken ct) => inner.SpaceAsync(ct);
    public void Dispose() => inner.Dispose();
}
