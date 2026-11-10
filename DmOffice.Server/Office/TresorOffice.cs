using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Office;

/// <summary>
/// Office im Browser für Dateien im Tresor (docs/tresor-konzept.md, „Office im Browser“): Öffnen und Laden nur bei offenem
/// Tresor; Speichern immer - ist der Tresor inzwischen geschlossen, legt dmOffice den neuen Stand mit dem öffentlichen Schlüssel
/// ab (<see cref="Tresor.Drop"/>) und übernimmt ihn beim nächsten Öffnen. Ein offenes Dokument zählt nicht als Nutzung
/// (<see cref="Tresor.DbOf"/> berührt kein Gerät), das automatische Schließen gilt wie eingestellt.
/// Stand einer Datei ist ihre Änderungszeit (ms): der Editor speichert auf den Stand, den er geladen bzw. zuletzt gespeichert
/// hat. Passt er nicht mehr (inzwischen anders geändert), kommt das Gespeicherte als „(Konflikt)“ daneben.
/// </summary>
public static class TresorOffice
{
    public const string DropKind = "office";
    /// <summary>Größer nimmt der Tresor aus dem Office-Server nichts an (der Stand liegt beim Ablegen ganz im Speicher).</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    private sealed record Header(long Id, long? Base, long Stamp);

    /// <summary>Beim Start: Übernahme beim Öffnen.</summary>
    public static void Register() => Tresor.Absorbers[DropKind] = (db, payload) =>
    {
        var nl = Array.IndexOf(payload, (byte)'\n');
        if (nl <= 0 || JsonSerializer.Deserialize<Header>(payload.AsSpan(0, nl)) is not { } h) return;
        Write(db, h.Id, payload[(nl + 1)..], h.Base, h.Stamp);
    };

    /// <summary>Datei im Tresor als FileEntry für die Office-Anbindung (ETag = Stand) - null bei geschlossenem Tresor.</summary>
    public static FileEntry? Entry(string userId, long id)
    {
        if (Tresor.DbOf(userId) is not { } db || db.Files.Find(id) is not { IsDir: false } e) return null;
        return Entry(e);
    }

    public static FileEntry Entry(TresorFiles.Entry e)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(e.Modified).UtcDateTime;
        // Pfad „tresor:<id>/<Name>“: der Name ergibt sich wie in der Ablage aus dem letzten Teil
        return new FileEntry(TresorFiles.PathPrefix + e.Id + "/" + e.Name, false, e.Size, e.Modified.ToString(), e.Mime ?? DavServer.Mime(e.Name), at, at, e.Id);
    }

    /// <summary>Inhalt lesen - nur bei offenem Tresor.</summary>
    public static Stream OpenRead(string userId, long id)
    {
        var db = Tresor.DbOf(userId) ?? throw new TresorClosedException();
        return db.Files.Find(id) is { IsDir: false } ? db.Files.OpenRead(id) : throw new KeyNotFoundException("Diese Datei gibt es im Tresor nicht.");
    }

    /// <summary>
    /// Gespeicherten Stand annehmen. baseStamp: Stand, auf dem der Editor gearbeitet hat (null: unbekannt, dann überschreiben).
    /// Gibt den neuen Stand zurück. Offen: gleich in den Tresor, sonst ablegen und beim Öffnen übernehmen.
    /// </summary>
    public static long Save(string userId, long id, byte[] content, long? baseStamp)
    {
        if (Tresor.DbOf(userId) is { } db) return Write(db, id, content, baseStamp, null);
        // Neuer Stand: mindestens eins über dem alten, damit der nächste Speichervorgang des Editors darauf aufbaut
        var stamp = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (baseStamp ?? 0) + 1);
        var head = JsonSerializer.SerializeToUtf8Bytes(new Header(id, baseStamp, stamp));
        Tresor.Drop(userId, DropKind, [.. head, (byte)'\n', .. content]);
        return stamp;
    }

    /// <summary>In die Tresor-Dateien schreiben: überschreiben, wenn der Stand passt, sonst „Name (Konflikt).ext“ daneben.</summary>
    private static long Write(TresorDb db, long id, byte[] content, long? baseStamp, long? stamp)
    {
        var now = stamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var files = db.Files;
        var e = files.Find(id);
        // Synchron: Übernehmen beim Öffnen und WOPI rufen ohne await; der Inhalt liegt schon ganz im Speicher
        if (e is { IsDir: false } && (baseStamp == null || baseStamp == e.Modified))
            return files.ReplaceAsync(id, new MemoryStream(content), null, now, CancellationToken.None).GetAwaiter().GetResult();
        // Inzwischen geändert oder gelöscht: nichts überschreiben, das Gespeicherte daneben (bzw. in die oberste Ebene)
        var name = e?.Name ?? "Office";
        var ext = Path.GetExtension(name);
        var parent = e != null && (e.Parent == 0 || files.Find(e.Parent) is { IsDir: true }) ? e.Parent : 0;
        files.CreateAsync(parent, name[..^ext.Length] + " (Konflikt)" + ext, new MemoryStream(content), e?.Mime ?? DavServer.Mime(name), CancellationToken.None, stamp: now)
            .GetAwaiter().GetResult();
        return now;
    }

    /// <summary>Stand aus einem Zeitstempel des Editors (ISO, wie LastModifiedTime) - null, wenn keiner oder unlesbar.</summary>
    public static long? StampOf(string? iso) =>
        !string.IsNullOrEmpty(iso) && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null;

    /// <summary>Inhalt bis <see cref="MaxBytes"/> lesen.</summary>
    public static async Task<byte[]> ReadBody(Stream s, CancellationToken ct)
    {
        var m = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (m.Length + n > MaxBytes) throw new InvalidOperationException("Das Dokument ist zu groß für den Tresor.");
            m.Write(buf, 0, n);
        }
        return m.ToArray();
    }
}
