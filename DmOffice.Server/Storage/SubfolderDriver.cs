namespace DmOffice.Storage;

/// <summary>
/// Ein Unterordner eines Speichers als eigene Wurzel (Datensicherung in einen Ordner eines externen Ordners). Pfade gehen
/// nie darüber hinaus; die Wurzel selbst lässt sich nicht löschen oder verschieben. Fehlt der Ordner, wird er beim Schreiben angelegt.
/// </summary>
public sealed class SubfolderDriver(IStorageDriver inner, string root) : IStorageDriver
{
    private readonly string _root = StoragePath.Clean(root);

    private string Full(string path) => StoragePath.Join(_root, StoragePath.Clean(path));

    private string Change(string path) => StoragePath.Clean(path).Length > 0 ? Full(path)
        : throw new UnauthorizedAccessException("Die Wurzel lässt sich nicht ändern.");

    public Task<List<DriverEntry>> ListAsync(string path, CancellationToken ct) => inner.ListAsync(Full(path), ct);
    public Task<DriverEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(Full(path), ct);
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(Full(path), ct);
    public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct) => inner.ReadRangeAsync(Full(path), offset, count, ct);
    public Task WriteAsync(string path, Stream data, CancellationToken ct) => inner.WriteAsync(Change(path), data, ct);
    public Task MkdirAsync(string path, CancellationToken ct) => inner.MkdirAsync(Full(path), ct);
    public Task SetModifiedAsync(string path, DateTime utc, CancellationToken ct) => inner.SetModifiedAsync(Change(path), utc, ct);
    public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(Change(path), ct);
    public Task MoveAsync(string from, string to, CancellationToken ct) => inner.MoveAsync(Change(from), Change(to), ct);
    public Task<DriverSpace?> SpaceAsync(CancellationToken ct) => inner.SpaceAsync(ct);
    public void Dispose() => inner.Dispose();
}
