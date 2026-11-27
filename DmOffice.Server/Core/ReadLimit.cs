namespace DmOffice.Core;

/// <summary>
/// Einen Strom ganz in den Speicher lesen, aber höchstens <c>max</c> Bytes. Für Inhalte, deren Größe ein fremder Server oder eine
/// veränderbare Datei nennt: die Angabe kann falsch sein, gelesen wird trotzdem nie mehr als die Grenze.
/// </summary>
public static class ReadLimit
{
    /// <summary>Darüber wirft das Lesen (eine <see cref="IOException"/>).</summary>
    public sealed class TooLargeException() : IOException("Die Datei ist größer als erwartet.");

    public static async Task<byte[]> ReadAllAsync(Stream s, long max, CancellationToken ct = default, long expected = 0)
    {
        using var m = new MemoryStream((int)Math.Clamp(expected, 0, Math.Min(max, int.MaxValue)));
        var buffer = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0)
        {
            if (m.Length + n > max) throw new TooLargeException();
            m.Write(buffer, 0, n);
        }
        return m.Length == m.Capacity ? m.GetBuffer() : m.ToArray();
    }

    public static byte[] ReadAll(Stream s, long max, long expected = 0)
    {
        using var m = new MemoryStream((int)Math.Clamp(expected, 0, Math.Min(max, int.MaxValue)));
        var buffer = new byte[81920];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (m.Length + n > max) throw new TooLargeException();
            m.Write(buffer, 0, n);
        }
        return m.Length == m.Capacity ? m.GetBuffer() : m.ToArray();
    }
}
