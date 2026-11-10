using System.Collections.Concurrent;
using DmOffice.Dav;

namespace DmOffice.Core;

/// <summary>
/// Was ein Benutzer insgesamt belegt: die Datenbank (Mails, Kalender, Kontakte, Notizen) und seine Dateien samt Papierkorb und
/// frueheren Versionen. Das Kontingent der Verwaltung gilt fuer alles zusammen. Zaehlt die Datenbank ohne ihre freien Seiten -
/// geloeschte Mails machen also gleich wieder Platz, auch wenn die Datei erst beim Verdichten schrumpft. Der Tresor zaehlt mit,
/// ebenso Originale und wartende Inhalte des DMS.
/// </summary>
public static class UserStorage
{
    private static readonly ConcurrentDictionary<string, (DateTime At, long Bytes)> Cache = new();
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(15);

    /// <summary>Belegter Platz der Datenbank (Datei und Journal ohne die freien Seiten); kurz zwischengespeichert.</summary>
    public static long Database(UserDb db)
    {
        var key = db.UserId;
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < Fresh) return hit.Bytes;
        var (file, wal, free) = db.SpaceInfo();
        var bytes = Math.Max(0, file + wal - free);
        Cache[key] = (DateTime.UtcNow, bytes);
        return bytes;
    }

    /// <summary>Beim naechsten Zugriff neu messen (nach grossen Aenderungen).</summary>
    public static void Refresh(UserDb db)
    {
        Cache.TryRemove(db.UserId, out _);
        DmsCache.Remove(db);
    }

    private sealed record DmsBytes(DateTime At, long Bytes);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UserDb, DmsBytes> DmsCache = new();
    private static readonly AsyncLocal<long> PlacingBytes = new();

    /// <summary>Was das DMS neben den Dateien aufbewahrt: Originale zu Archivfassungen und noch nicht abgelegte Inhalte; kurz zwischengespeichert.</summary>
    public static long DmsHeld(UserDb db)
    {
        if (DmsCache.TryGetValue(db, out var hit) && DateTime.UtcNow - hit.At < Fresh) return hit.Bytes;
        long bytes = 0;
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'dms_docs'") != null)
            foreach (var (blob, size, done) in db.Query("SELECT original_blob, size, state = 'done' FROM dms_docs WHERE original_blob IS NOT NULL",
                r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2) == 1)))
            {
                // Bei abgelegten Dokumenten nennt size die Archivfassung, das Original kann deutlich groesser sein
                var file = done && BlobStore.ValidId(blob) ? new FileInfo(db.Blobs.PathOf(blob)) : null;
                bytes += file is { Exists: true } ? file.Length : size;
            }
        DmsCache.AddOrUpdate(db, new DmsBytes(DateTime.UtcNow, bytes));
        return bytes;
    }

    /// <summary>Beim Ablegen eines wartenden Dokuments: sein Inhalt zaehlt schon, darf also nicht ein zweites Mal gegen die Grenze laufen.</summary>
    public static IDisposable Placing(long bytes)
    {
        var before = PlacingBytes.Value;
        PlacingBytes.Value = bytes;
        return new PlacingScope(before);
    }
    private sealed class PlacingScope(long before) : IDisposable { public void Dispose() => PlacingBytes.Value = before; }

    /// <summary>Was das DMS beim Schreiben einer Datei gegen die Grenze belegt.</summary>
    public static long DmsAgainstFiles(UserDb db) => Math.Max(0, DmsHeld(db) - PlacingBytes.Value);

    /// <summary>Alles, was der Benutzer belegt - zur Anzeige.</summary>
    public static long Used(UserDb db) => Database(db) + FileStore.UsageTotal(db) + Tresor.SizeOf(db.UserId) + DmsHeld(db);

    /// <summary>Was gegen die Grenze zaehlt: Datenbank und Dateien; Papierkorb und fruehere Versionen weichen bei Bedarf (siehe FileStore).</summary>
    public static long Counted(UserDb db) => Database(db) + FileStore.Usage(db) + Tresor.SizeOf(db.UserId) + DmsHeld(db);

    /// <summary>Hat der Benutzer seine Grenze ausgeschoepft? Dann ruft der Mail-Abgleich nichts Neues ab und Dateien lassen sich nicht mehr hinzufuegen.</summary>
    public static bool Exceeded(UserDb db)
    {
        var quota = FileStore.Quota(db);
        return quota > 0 && Counted(db) > quota;
    }
}
