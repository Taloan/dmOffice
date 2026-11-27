namespace DmOffice.Storage;

/// <summary>Ausschnitt aus einer Antwort lesen: erst <c>skip</c> Bytes überspringen, dann höchstens <c>count</c> Bytes - was der
/// Server darüber hinaus schickt, bleibt ungelesen.</summary>
public static class RangeRead
{
    public static async Task<byte[]> TakeAsync(Stream s, long skip, int count, CancellationToken ct)
    {
        if (count <= 0) return [];
        if (skip > 0)
        {
            var junk = new byte[81920];
            for (long left = skip; left > 0;)
            {
                var n = await s.ReadAsync(junk.AsMemory(0, (int)Math.Min(junk.Length, left)), ct);
                if (n == 0) return [];
                left -= n;
            }
        }
        var buf = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(got, count - got), ct);
            if (n == 0) break;
            got += n;
        }
        return got == count ? buf : buf[..got];
    }
}
