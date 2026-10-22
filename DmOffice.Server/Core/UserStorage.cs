using System.Collections.Concurrent;
using DmOffice.Dav;

namespace DmOffice.Core;

/// <summary>
/// Was ein Benutzer insgesamt belegt: die Datenbank (Mails, Kalender, Kontakte, Notizen) und seine Dateien samt Papierkorb und
/// frueheren Versionen. Das Kontingent der Verwaltung gilt fuer alles zusammen. Zaehlt die Datenbank ohne ihre freien Seiten -
/// geloeschte Mails machen also gleich wieder Platz, auch wenn die Datei erst beim Verdichten schrumpft.
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
    public static void Refresh(UserDb db) => Cache.TryRemove(db.UserId, out _);

    /// <summary>Alles, was der Benutzer belegt - zur Anzeige.</summary>
    public static long Used(UserDb db) => Database(db) + FileStore.UsageTotal(db);

    /// <summary>Was gegen die Grenze zaehlt: Datenbank und Dateien; Papierkorb und fruehere Versionen weichen bei Bedarf (siehe FileStore).</summary>
    public static long Counted(UserDb db) => Database(db) + FileStore.Usage(db);

    /// <summary>Hat der Benutzer seine Grenze ausgeschoepft? Dann ruft der Mail-Abgleich nichts Neues ab und Dateien lassen sich nicht mehr hinzufuegen.</summary>
    public static bool Exceeded(UserDb db)
    {
        var quota = FileStore.Quota(db);
        return quota > 0 && Counted(db) > quota;
    }
}
