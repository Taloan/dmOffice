using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using SkiaSharp;

namespace DmOffice.Dav;

/// <summary>
/// Nicht-destruktive Fotobearbeitung (docs/foto-bearbeitung-konzept.md): je Foto nur Entwicklungseinstellungen - Drehen, Ausschnitt,
/// Reglerwerte - in der Datenbank (Tabelle photo_edits); das Original bleibt unberührt. Vorschaubilder entstehen aus der großen Vorschau
/// des Originals plus Einstellungen (RAW mit der Engine aus dem RAW selbst) und liegen verschlüsselt unter data/cache/edited.
/// Schlüssel: „f:&lt;file_id&gt;“ in der Datenbank des Besitzers (übersteht Umbenennen und Verschieben), „x:&lt;Quelle&gt;/&lt;Pfad&gt;“ für
/// Galerie-Ordner. Damit Browser und App die neue Fassung holen, hängt an den Etags der Listen „~e&lt;Fingerabdruck&gt;“ (<see cref="Tag"/>).
/// </summary>
public static class PhotoEdits
{
    public sealed record Edit(int Rotate, PhotoEdit.Crop? Crop, PhotoAdjust.Values? Adjust, string Origin, long Modified)
    {
        /// <summary>Ändert nichts (dann gibt es keinen Datensatz).</summary>
        public bool IsEmpty => Rotate % 360 == 0 && (Crop == null || Crop.X <= 0.0001 && Crop.Y <= 0.0001 && Crop.W >= 0.9999 && Crop.H >= 0.9999) && (Adjust == null || Adjust.IsNeutral);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int HistoryMax = 10;

    /// <summary>Zwischenspeicher der bearbeiteten Vorschaubilder (wie die großen Vorschauen der Galerie, eigener Ordner).</summary>
    public static DmOffice.Gallery.GalleryProxies? Cache { get; set; }

    public static void Ensure(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS photo_edits (key TEXT PRIMARY KEY, edit TEXT NOT NULL, history TEXT, modified INTEGER NOT NULL)");

    public static string KeyOf(FileEntry f) => "f:" + f.FileId;
    public static string KeyOfExt(string id) => "x:" + id;

    public static Edit? Get(UserDb db, string key)
    {
        Ensure(db);
        return db.Scalar("SELECT edit FROM photo_edits WHERE key = $k", ("$k", key)) is string s ? JsonSerializer.Deserialize<Edit>(s, Json) : null;
    }

    /// <summary>Einstellungen setzen (leer: entfernen); der vorige Stand kommt in den Verlauf.</summary>
    public static void Set(UserDb db, string key, Edit edit)
    {
        Ensure(db);
        if (edit.IsEmpty) { Clear(db, key); return; }
        edit = edit with { Rotate = ((edit.Rotate % 360) + 360) % 360, Adjust = edit.Adjust?.Clamped(), Modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        var before = db.Query("SELECT edit, history FROM photo_edits WHERE key = $k", r => (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)), ("$k", key)).FirstOrDefault();
        var history = before.Item2 is { } h ? JsonSerializer.Deserialize<List<Edit>>(h, Json) ?? [] : [];
        if (before.Item1 is { } prev && JsonSerializer.Deserialize<Edit>(prev, Json) is { } p) history.Insert(0, p);
        db.Exec("INSERT INTO photo_edits(key, edit, history, modified) VALUES($k,$e,$h,$m) ON CONFLICT(key) DO UPDATE SET edit=$e, history=$h, modified=$m",
            ("$k", key), ("$e", JsonSerializer.Serialize(edit, Json)), ("$h", JsonSerializer.Serialize(history.Take(HistoryMax), Json)), ("$m", edit.Modified));
        Changed(db);
    }

    public static void Clear(UserDb db, string key)
    {
        Ensure(db);
        if (db.Exec("DELETE FROM photo_edits WHERE key = $k", ("$k", key)) > 0) Changed(db);
    }

    /// <summary>Alle bearbeiteten Fotos einer Datenbank: Schlüssel → Fingerabdruck (für die Etags der Listen).</summary>
    public static Dictionary<string, string> Fingerprints(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT key, edit FROM photo_edits", r => (r.GetString(0), Fingerprint(r.GetString(1)))).ToDictionary(x => x.Item1, x => x.Item2);
    }

    public static string Fingerprint(Edit e) => Fingerprint(JsonSerializer.Serialize(e with { Modified = 0, Origin = "" }, Json));
    private static string Fingerprint(string json)
    {
        // Herkunft und Zeit ändern das Bild nicht
        var e = JsonSerializer.Deserialize<Edit>(json, Json)!;
        var core = JsonSerializer.Serialize(new { e.Rotate, e.Crop, e.Adjust }, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(core)))[..10].ToLowerInvariant();
    }

    /// <summary>Etag mit Fingerabdruck der Einstellungen (ohne Bearbeitung unverändert).</summary>
    public static string Tag(string etag, string? fingerprint) => fingerprint == null ? etag : etag + "~e" + fingerprint;

    /// <summary>Galerie-Listen neu bauen lassen (ihre Zähler) - die Etags darin ändern sich.</summary>
    private static void Changed(UserDb db)
    {
        try { db.Exec("UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1"); } catch (Microsoft.Data.Sqlite.SqliteException) { }
        try { db.Exec("UPDATE gallery_own_ver SET v = v + 1 WHERE id = 1"); } catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    // ---------------- Vorschaubilder ----------------

    private static readonly SemaphoreSlim Gate = new(2, 2);

    /// <summary>
    /// Bearbeitetes Vorschaubild (WebP). Groß zuerst (2048 px, gemerkt), kleinere daraus. Grundlage: die große Vorschau des Originals
    /// (schon aufrecht); RAW mit eingerichteter Engine aus dem RAW selbst entwickelt (sonst sähe man nur das eingebettete Kamera-JPG).
    /// </summary>
    /// <param name="scope">Erste Ebene im Zwischenspeicher (Benutzer bzw. Datenbank)</param>
    /// <param name="stamp">Stand des Originals (Etag) - ändert es sich, gilt das Vorschaubild nicht mehr</param>
    public static async Task<byte[]?> ThumbAsync(string scope, string key, Edit edit, string stamp, int size, bool raw,
        Func<Task<byte[]?>> large, Func<Task<byte[]>> original, ILogger? log, CancellationToken ct)
    {
        var big = Thumbs.Large;
        var st = StampOf(stamp, edit);
        if (Cache?.Get(scope, "edit", key + "|" + size, st) is { } hit) return hit;
        byte[]? largeEdited = Cache?.Get(scope, "edit", key + "|" + big, st);
        if (largeEdited == null)
        {
            await Gate.WaitAsync(ct);
            try
            {
                // Inzwischen von einer gleichzeitigen Anfrage (andere Größe) gerechnet?
                largeEdited = Cache?.Get(scope, "edit", key + "|" + big, st);
                if (largeEdited == null)
                {
                    using var bmp = await DevelopAsync(edit, raw, large, original, log, ct);
                    if (bmp == null) return null;
                    largeEdited = Encode(bmp, big);
                    if (largeEdited == null) return null;
                    Put(scope, key + "|" + big, st, largeEdited);
                }
            }
            finally { Gate.Release(); }
        }
        if (size >= big) return largeEdited;
        var small = Thumbs.FromBytes(largeEdited, size);
        if (small != null) Put(scope, key + "|" + size, st, small);
        return small;
    }

    /// <summary>Merken; scheitert das (Windows: Datei gerade von einer anderen Anfrage gelesen), kommt das Bild trotzdem.</summary>
    private static void Put(string scope, string path, long stamp, byte[] data)
    {
        try { Cache?.Put(scope, "edit", path, stamp, data); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static long StampOf(string stamp, Edit edit) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(stamp + "|" + Fingerprint(edit))), 0);

    /// <summary>Aufrechtes Bild mit Reglern, gedreht und zugeschnitten.</summary>
    private static async Task<SKBitmap?> DevelopAsync(Edit edit, bool raw, Func<Task<byte[]?>> large, Func<Task<byte[]>> original, ILogger? log, CancellationToken ct)
    {
        var values = edit.Adjust ?? new PhotoAdjust.Values(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        byte[]? jpeg = null;
        // RAW: die Engine entwickelt mit den Reglern (wie beim Speichern); ohne Engine das eingebettete Bild, Regler hier
        if (raw && PhotoEngine.Configured) jpeg = await PhotoEngine.DevelopAsync(await original(), values.Clamped(), Thumbs.Large, 90, log, ct);
        var tuned = jpeg != null;
        SKBitmap? bmp;
        if (jpeg != null) bmp = SKBitmap.Decode(jpeg);
        else
        {
            if (await large() is not { } webp) return null;
            bmp = SKBitmap.Decode(webp);
        }
        if (bmp == null) return null;
        if (!tuned && !values.IsNeutral)
        {
            // Mit Engine: ihre Umsetzung (wie beim Speichern); sonst die eigene
            SKBitmap? adjusted = null;
            if (PhotoEngine.Configured && await PhotoEngine.DevelopAsync(PhotoAdjust.Jpeg(bmp, 92), values.Clamped(), null, 90, log, ct) is { } dev) adjusted = SKBitmap.Decode(dev);
            adjusted ??= PhotoAdjust.Apply(bmp, values.Clamped());
            bmp.Dispose();
            bmp = adjusted;
        }
        var done = PhotoEdit.TurnAndCrop(bmp, edit.Rotate, edit.Crop);
        if (!ReferenceEquals(done, bmp)) bmp.Dispose();
        return done;
    }

    private static byte[]? Encode(SKBitmap bmp, int size)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data == null ? null : Thumbs.FromBytes(data.ToArray(), size);
    }
}
