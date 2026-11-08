using DmOffice.Core;
using SkiaSharp;

namespace DmOffice.Dav;

/// <summary>
/// Vorschaubilder der Dateiablage: Bilder (JPEG, PNG, GIF, WebP, BMP, ICO) mit SkiaSharp, die erste Seite von PDFs
/// mit PDFium (PDFtoImage). Videos haben keinen Decoder auf dem Server - dort schickt der Browser ein Standbild,
/// das hier neu kodiert wird. Ergebnis als WebP, verschluesselt im BlobStore (Verweis in file_thumbs);
/// gilt, solange der ETag der Datei stimmt. Misslingt es, merkt sich ein leerer Eintrag das (kein neuer Versuch).
/// </summary>
public static class Thumbs
{
    /// <summary>
    /// Kachel/Liste und Betrachter. Groß wie die vorab erzeugten Vorschauen der Galerie-Ordner (GalleryProxies.Size), damit beides
    /// gleich scharf ist. Früher 1600 px: solche Vorschauen bleiben als Rückfall, solange das Original nur im Storage Extender liegt.
    /// </summary>
    /// Medium: Kacheln auf Bildschirmen mit Skalierung (eine Kachel von 240 Punkten braucht dort 300 bis 480 Pixel) - aus der
    /// großen Vorschau gerechnet, wenn es sie schon gibt.
    public const int Small = 256, Medium = 512, Large = 2048, LegacyLarge = 1600;

    public static int Snap(int size) => size <= Small ? Small : size <= Medium ? Medium : Large;

    /// <summary>Hoechstens so viele Bilder gleichzeitig - Dekodieren kostet Speicher und Rechenzeit.</summary>
    private static readonly SemaphoreSlim Gate = new(2);
    /// <summary>PDFium ist nicht threadsicher.</summary>
    /// <summary>PDFium verträgt keine gleichzeitigen Aufrufe - auch DMS (Seitenbilder) nimmt diese Sperre.</summary>
    internal static readonly object PdfLock = new();

    private const long MaxImageBytes = 80L * 1024 * 1024, MaxPdfBytes = 100L * 1024 * 1024;
    /// <summary>Groesser dekodiert wird nicht (Pixel nach dem verkleinerten Dekodieren) - gegen Dekompressionsbomben.</summary>
    private const long MaxPixels = 60_000_000;

    private static readonly HashSet<string> ImageExt = [".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".bmp", ".ico"];
    /// <summary>RAW-Fotos: Vorschaubild aus der eingebetteten JPEG-Vorschau (<see cref="EmbeddedJpeg"/>).</summary>
    public static readonly HashSet<string> RawExt = new(StringComparer.OrdinalIgnoreCase) { ".arw", ".cr2", ".cr3", ".nef", ".nrw", ".orf", ".rw2", ".raf", ".pef", ".dng", ".srw", ".3fr", ".erf" };
    public static bool IsRaw(string name) => RawExt.Contains(Path.GetExtension(name));

    public enum Kind { None, Image, Pdf, Video }

    public static Kind KindOf(string name, string? mime)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ImageExt.Contains(ext) || RawExt.Contains(ext)) return Kind.Image;
        if (ext == ".pdf" || mime == "application/pdf") return Kind.Pdf;
        if (mime?.StartsWith("video/") == true || ext is ".mp4" or ".m4v" or ".webm" or ".mov" or ".ogv" or ".mkv") return Kind.Video;
        if (mime is "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/bmp") return Kind.Image;
        return Kind.None;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Retried = new();

    /// <summary>
    /// Fruehere Versionen merkten sich auch voruebergehende Lesefehler als "kein Vorschaubild" - fuer immer. Einmal je
    /// Datenbank diese leeren Eintraege wegraeumen, damit die Bilder beim naechsten Ansehen neu entstehen.
    /// </summary>
    private static void RetryFailedOnce(UserDb db)
    {
        if (!Retried.TryAdd(db.UserId, 0) || db.Get("thumbs.retry") == "1") return;
        try
        {
            db.Exec("DELETE FROM file_thumbs WHERE blob IS NULL AND length(data)=0");
            db.Set("thumbs.retry", "1");
        }
        catch (Exception) { Retried.TryRemove(db.UserId, out _); }
    }

    private static (string Etag, byte[] Data)? Cached(UserDb db, long fileId, int size)
    {
        var row = db.Query("SELECT etag, blob, data FROM file_thumbs WHERE fileid=$f AND size=$s",
            r => (Etag: r.GetString(0), Blob: r.IsDBNull(1) ? null : r.GetString(1), Data: (byte[])r.GetValue(2)), ("$f", fileId), ("$s", size));
        if (row.Count == 0) return null;
        if (row[0].Blob is not { } blob) return (row[0].Etag, row[0].Data);
        try { return (row[0].Etag, db.Blobs.ReadAll(blob)); }
        catch (IOException) { return null; }   // eben ersetzt - neu erzeugen
    }

    /// <summary>Schon erzeugtes Vorschaubild einer Dateinummer, ohne etwas zu erzeugen (Papierkorb: der Inhalt liegt nicht mehr unter einem Pfad).</summary>
    public static byte[]? Stored(UserDb db, long fileId, int size) =>
        Cached(db, fileId, Snap(size)) is { } c && c.Data.Length > 0 ? c.Data : size > Small && Cached(db, fileId, Small) is { } s && s.Data.Length > 0 ? s.Data : null;

    /// <summary>Liegt der Inhalt der Datei hier (nicht nur im Storage Extender)?</summary>
    private static bool ContentHere(UserDb db, long fileId)
    {
        var blob = db.Scalar("SELECT blob FROM files WHERE fileid=$f", ("$f", fileId)) as string;
        return blob == null || db.Blobs.Exists(blob);
    }

    /// <summary>Als Blob wie die Dateien selbst; leer (misslungen) ohne Blob.</summary>
    private static void Store(UserDb db, long fileId, int size, string etag, byte[] data)
    {
        var blob = data.Length > 0 ? db.Blobs.Write(data).Id : null;
        try
        {
            db.Exec("INSERT INTO file_thumbs(fileid,size,etag,data,blob) VALUES($f,$s,$e,X'',$b) ON CONFLICT(fileid,size) DO UPDATE SET etag=$e, data=X'', blob=$b",
                ("$f", fileId), ("$s", size), ("$e", etag), ("$b", blob));
        }
        catch { if (blob != null) db.Blobs.Delete(blob); throw; }
        FileStore.CollectBlobs(db);
    }

    /// <summary>
    /// Gibt es (voraussichtlich) ein Vorschaubild? Fuer nc:has-preview im PROPFIND - ohne etwas zu erzeugen:
    /// Bilder und PDFs in den Grenzen, solange kein Versuch fehlgeschlagen ist; Videos nur mit Standbild.
    /// </summary>
    /// <summary>Bekommt dieser Inhalt ein Vorschaubild vom Server (Bild, PDF; Videos liefert der Browser)?</summary>
    public static bool Expected(string name, string? mime, long size)
    {
        var kind = KindOf(name, mime);
        return size > 0 && kind is Kind.Image or Kind.Pdf && size <= (kind == Kind.Pdf ? MaxPdfBytes : MaxImageBytes);
    }

    public static bool Available(UserDb db, FileEntry f)
    {
        if (f.IsDir || f.FileId <= 0 || f.Size == 0) return false;
        var kind = KindOf(f.Name, f.Mime);
        if (kind == Kind.None) return false;
        var c = Cached(db, f.FileId, Small);
        if (kind == Kind.Video) return c is { } v && v.Etag == f.Etag && v.Data.Length > 0;
        if (f.Size > (kind == Kind.Pdf ? MaxPdfBytes : MaxImageBytes)) return false;
        return !(c is { } x && x.Etag == Stamp(f) && x.Data.Length == 0);
    }

    /// <summary>
    /// WebP fuer Programme, die es nicht lesen: die Vorschau im Windows-Explorer (Nextcloud-Client, GDI+) kennt
    /// nur BMP, GIF, JPEG, PNG, TIFF. Mit Durchsichtigkeit als PNG, sonst JPEG.
    /// </summary>
    public static (byte[] Data, string Mime)? Classic(byte[] webp)
    {
        using var bmp = SKBitmap.Decode(webp);
        if (bmp == null) return null;
        bool alpha = false;
        if (bmp.AlphaType != SKAlphaType.Opaque)
            foreach (var px in bmp.Pixels)
                if (px.Alpha != 255) { alpha = true; break; }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(alpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 85);
        return data == null ? null : (data.ToArray(), alpha ? "image/png" : "image/jpeg");
    }

    /// <summary>Merkmal im Zwischenspeicher: der Etag, bei RAW mit Stand der Drehlogik - ändert die sich, wird neu erzeugt.</summary>
    private const string RawStamp = "~raw2";
    private static string Stamp(FileEntry f) => IsRaw(f.Name) ? f.Etag + RawStamp : f.Etag;
    /// <summary>SQL: Vorschaubild t gehoert zum aktuellen Stand der Datei f - RAW-Fotos tragen den Zusatz aus <see cref="Stamp"/>.</summary>
    public const string CurrentSql = "(t.etag = f.etag OR t.etag = f.etag || '" + RawStamp + "')";

    /// <summary>
    /// Vorschaubild (WebP) oder null, wenn es keines gibt - bei Fotos mit Entwicklungseinstellungen (PhotoEdits) die bearbeitete Fassung.
    /// original: immer das Original (der Editor arbeitet darauf).
    /// </summary>
    public static async Task<byte[]?> Get(UserDb db, FileEntry f, int size, CancellationToken ct, bool original = false)
    {
        if (!original && !f.IsDir && f.FileId > 0 && KindOf(f.Name, f.Mime) == Kind.Image && PhotoEdits.Get(db, PhotoEdits.KeyOf(f)) is { } edit)
            return await PhotoEdits.ThumbAsync(db.UserId, PhotoEdits.KeyOf(f), edit, Stamp(f), size, IsRaw(f.Name),
                () => GetOriginal(db, f, Large, ct), () => Task.FromResult(FileStore.Data(db, f.Path) ?? throw new KeyNotFoundException("Datei nicht gefunden.")), null, ct);
        return await GetOriginal(db, f, size, ct);
    }

    private static async Task<byte[]?> GetOriginal(UserDb db, FileEntry f, int size, CancellationToken ct)
    {
        if (f.IsDir || f.FileId <= 0) return null;
        size = Snap(size);
        RetryFailedOnce(db);
        if (Cached(db, f.FileId, size) is { } c && c.Etag == Stamp(f)) return c.Data.Length == 0 ? null : c.Data;
        // Groß noch nicht in 2048 px: liegt das Original nur im Storage Extender, die frühere 1600er-Vorschau zeigen,
        // statt dafür das ganze Original (bei RAW oft 25 MB) zurückzuholen
        if (size == Large && Cached(db, f.FileId, LegacyLarge) is { } old && old.Etag == Stamp(f) && old.Data.Length > 0 && !ContentHere(db, f.FileId)) return old.Data;
        // Mittel: aus der großen Vorschau, wenn es sie schon gibt (schnell, ohne das Original)
        if (size == Medium && Cached(db, f.FileId, Large) is { } big && big.Etag == Stamp(f) && big.Data.Length > 0
            && FromBytes(big.Data, Medium) is { } mid)
        {
            Store(db, f.FileId, Medium, Stamp(f), mid);
            return mid;
        }
        // Original nur im Storage Extender: nicht dafür zurückholen - aus der früheren 1600er-Vorschau, sonst die kleine
        if (size == Medium && !ContentHere(db, f.FileId))
        {
            if (Cached(db, f.FileId, LegacyLarge) is { } l && l.Etag == Stamp(f) && l.Data.Length > 0 && FromBytes(l.Data, Medium) is { } m)
            {
                Store(db, f.FileId, Medium, Stamp(f), m);
                return m;
            }
            if (Cached(db, f.FileId, Small) is { } sm && sm.Etag == Stamp(f) && sm.Data.Length > 0) return sm.Data;
        }
        var kind = KindOf(f.Name, f.Mime);
        // Videos: nur das Standbild aus dem Browser (Accept), auch fuer die grosse Ansicht
        if (kind == Kind.Video)
            return size > Small && Cached(db, f.FileId, Small) is { } v && v.Etag == f.Etag && v.Data.Length > 0 ? v.Data : null;
        if (kind == Kind.None || f.Size == 0 || f.Size > (kind == Kind.Pdf ? MaxPdfBytes : MaxImageBytes)) return null;
        // Kleines Bild ist schon klein genug: die grosse Ansicht braucht keine eigene Version
        await Gate.WaitAsync(ct);
        byte[]? data;
        byte[]? largeToo = null;
        bool failed = false;
        // Kleine Vorschau eines Fotos: die große aus demselben Dekodieren gleich mit - die Großansicht ist dann sofort da
        var both = size == Small && kind == Kind.Image && !IsRaw(f.Name) && !(Cached(db, f.FileId, Large) is { } have && have.Etag == Stamp(f));
        using var activity = Activity.Start(db.UserId, Activity.Thumbs);
        try
        {
            data = await Task.Run(() =>
            {
                Stream stream;
                // Lesen schlug fehl (Speicher kurz nicht erreichbar, Datei eben ersetzt): naechstes Mal noch einmal versuchen
                try { stream = FileStore.OpenRead(db, f.Path); }
                catch (Exception ex) when (ex is not OperationCanceledException) { failed = true; return null; }
                try
                {
                    using (stream)
                    {
                        // RAW: die eingebettete JPEG-Vorschau, gedreht nach den Angaben im RAW selbst
                        if (IsRaw(f.Name)) { var raw = new MemoryStream(); stream.CopyTo(raw); return FromRaw(raw.ToArray(), size); }
                        if (both)
                        {
                            using var big = Decode(stream, Large, out var o) is { } d ? Orient(d, o, Large) : null;
                            if (big == null) return null;
                            largeToo = Encode(big, Large);
                            return Encode(big, Small);
                        }
                        using (var bmp = kind == Kind.Pdf ? RenderPdf(stream, size) : Decode(stream, size, out var origin2) is { } b ? Orient(b, origin2, size) : null)
                            return bmp == null ? null : Encode(bmp, size);
                    }
                }
                // Kaputte oder unlesbare Datei: das merken - ausser es war ein Lesefehler mitten im Strom
                catch (IOException) { failed = true; return null; }
                catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
            }, ct);
        }
        finally { Gate.Release(); }
        // Nur merken, was sicher kein Vorschaubild ergibt - ein voruebergehender Fehler soll es nicht fuer immer verhindern
        if (!failed) Store(db, f.FileId, size, Stamp(f), data ?? []);
        if (largeToo != null) Store(db, f.FileId, Large, Stamp(f), largeToo);
        return data;
    }

    /// <summary>Standbild aus dem Browser (Video): dekodieren, verkleinern, neu kodieren - nie ungeprueft speichern.</summary>
    public static bool Accept(UserDb db, FileEntry f, byte[] image)
    {
        if (f.IsDir || f.FileId <= 0 || image.Length > 8 * 1024 * 1024) return false;
        using var ms = new MemoryStream(image);
        using var bmp = Decode(ms, Small, out var origin);
        if (bmp == null) return false;
        using var oriented = Orient(bmp, origin, Small);
        var data = Encode(oriented, Small);
        if (data == null) return false;
        Store(db, f.FileId, Small, f.Etag, data);
        return true;
    }

    /// <summary>Kleines Vorschaubild (WebP) aus beliebigen Bildbytes, z. B. fuer Notizen. Null, wenn es kein Bild ist.</summary>
    public static byte[]? FromBytes(byte[] image, int size)
    {
        try
        {
            using var stream = new MemoryStream(image);
            using var bmp = Decode(stream, size, out var origin);
            if (bmp == null) return null;
            using var oriented = origin == SKEncodedOrigin.TopLeft ? null : Orient(bmp.Copy(), origin, size);
            return Encode(oriented ?? bmp, size);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Vorschaubild (WebP) aus einem RAW-Foto: eingebettetes JPEG, gedreht nach dessen eigener Angabe, sonst nach der des RAW.
    /// Die Hersteller halten es verschieden: manche drehen die Vorschau schon selbst und geben trotzdem den Winkel an, manche
    /// speichern sie ungedreht mit Winkel. Kamerasensoren sind quer - ist die Vorschau schon hochkant, wird nicht noch einmal
    /// um 90° gedreht. Null: keine Vorschau gefunden.
    /// </summary>
    public static byte[]? FromRaw(byte[] raw, int size) => EmbeddedJpeg(raw) is { } jpg ? FromPreview(jpg, raw, size) : null;

    /// <summary>
    /// Vorschaubild aus einer eingebetteten JPEG-Vorschau (RAW, MPF-Vorschau eines großen JPEG); ohne eigene Drehangabe gilt die
    /// der Datei (<paramref name="file"/>: die ganze Datei oder ihr Kopf mit den EXIF-Angaben).
    /// </summary>
    public static byte[]? FromPreview(byte[] jpg, byte[] file, int size)
    {
        try
        {
            using var bmp = Decode(new MemoryStream(jpg), size, out var origin);
            if (bmp == null) return null;
            if (origin == SKEncodedOrigin.TopLeft) origin = RawOrigin(file);
            if (Quarter(origin) && bmp.Height > bmp.Width) origin = SKEncodedOrigin.TopLeft;
            using var oriented = origin == SKEncodedOrigin.TopLeft ? null : Orient(bmp.Copy(), origin, size);
            return Encode(oriented ?? bmp, size);
        }
        catch (Exception) { return null; }
    }

    private static bool Quarter(SKEncodedOrigin o) => o is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Drehung laut RAW - dieselben Werte 1..8 wie SKEncodedOrigin. Zuerst EXIF IFD0, sonst die erste andere Angabe (Panasonic
    /// RW2 hat ein eigenes IFD0, CR3 steckt sie in einen eigenen Block); die des eingebetteten Vorschaubilds (IFD1) zuletzt.
    /// </summary>
    internal static SKEncodedOrigin RawOrigin(byte[] raw)
    {
        try
        {
            var dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(new MemoryStream(raw))
                .Where(d => d.ContainsTag(0x0112))
                .OrderBy(d => d is MetadataExtractor.Formats.Exif.ExifIfd0Directory ? 0 : d is MetadataExtractor.Formats.Exif.ExifThumbnailDirectory ? 2 : 1);
            foreach (var d in dirs)
                if (MetadataExtractor.DirectoryExtensions.TryGetInt32(d, 0x0112, out var o) && o is >= 1 and <= 8) return (SKEncodedOrigin)o;
        }
        catch (Exception) { }
        return SKEncodedOrigin.TopLeft;
    }

    /// <summary>
    /// Große JPEGs von Kameras (Sony, Fuji, Nikon …) tragen nach dem eigentlichen Bild eine Vorschau (MPF, Multi-Picture Format, etwa
    /// 1616×1080). Wo sie steht, sagt ein APP2-Block im Kopf der Datei - so reicht für das Vorschaubild der Kopf und dieser Ausschnitt
    /// statt der ganzen Datei. Ergebnis: Lage in der Datei (die größte weitere Aufnahme), null wenn keine angegeben ist.
    /// </summary>
    public static (long Offset, int Length)? MpfPreview(byte[] head)
    {
        if (head.Length < 4 || head[0] != 0xFF || head[1] != 0xD8) return null;
        try
        {
            for (var pos = 2; pos + 4 <= head.Length;)
            {
                if (head[pos] != 0xFF) return null;
                var marker = head[pos + 1];
                if (marker == 0xFF) { pos++; continue; }
                if (marker is 0xDA or 0xD9) return null;   // Bilddaten beginnen - kein MPF davor
                var len = head[pos + 2] << 8 | head[pos + 3];
                if (len < 2) return null;
                var data = pos + 4;
                if (marker == 0xE2 && data + 8 <= head.Length && head[data] == 'M' && head[data + 1] == 'P' && head[data + 2] == 'F' && head[data + 3] == 0)
                    return MpfEntries(head, data + 4, Math.Min(head.Length, pos + 2 + len));
                pos += 2 + len;
            }
        }
        catch (IndexOutOfRangeException) { }
        return null;
    }

    private static (long Offset, int Length)? MpfEntries(byte[] b, int tiff, int end)
    {
        var le = b[tiff] == 'I';
        if (!le && b[tiff] != 'M') return null;
        uint U32(int i) => i + 4 > end ? throw new IndexOutOfRangeException() : le ? BitConverter.ToUInt32(b, i) : (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
        int U16(int i) => i + 2 > end ? throw new IndexOutOfRangeException() : le ? b[i] | b[i + 1] << 8 : b[i] << 8 | b[i + 1];
        var ifd = tiff + (int)U32(tiff + 4);
        var count = U16(ifd);
        for (var e = 0; e < count; e++)
        {
            var at = ifd + 2 + e * 12;
            if (U16(at) != 0xB002) continue;   // MP Entry: je Aufnahme 16 Byte (Art, Größe, Lage ab dem MPF-Kopf, Abhängigkeiten)
            var bytes = (int)U32(at + 4);
            var list = tiff + (int)U32(at + 8);
            (long Offset, int Length)? best = null;
            for (var i = 1; i < bytes / 16; i++)
            {
                // Art „unbestimmt“: keine Vorschau, bei Handys die Gain Map für HDR (ein graues Bild)
                if ((U32(list + i * 16) & 0xFFFFFF) == 0) continue;
                var size = U32(list + i * 16 + 4);
                var offset = U32(list + i * 16 + 8);
                if (offset > 0 && size is >= 1024 and <= 16 * 1024 * 1024 && (best == null || size > best.Value.Length))
                    best = (tiff + (long)offset, (int)size);
            }
            return best;
        }
        return null;
    }

    /// <summary>
    /// RAW-Fotos (CR2, NEF, ARW, DNG, CR3 …) tragen fertige JPEG-Vorschauen in sich - die größte, die sich dekodieren
    /// lässt. Null: keine gefunden.
    /// </summary>
    public static byte[]? EmbeddedJpeg(byte[] raw)
    {
        (int Start, long Pixels) best = (-1, 0);
        // Begrenzt: eine präparierte Datei voller FF D8 FF ergäbe sonst Millionen Versuche (Minuten Rechenzeit je Upload)
        var tries = 0;
        var until = DateTime.UtcNow.AddSeconds(5);
        for (var i = 0; i >= 0 && i < raw.Length - 3; i++)
        {
            i = Array.IndexOf(raw, (byte)0xFF, i);
            if (i < 0 || i > raw.Length - 3) break;
            if (raw[i + 1] != 0xD8 || raw[i + 2] != 0xFF) continue;
            if (++tries > 256 || DateTime.UtcNow > until) break;
            try
            {
                using var codec = SKCodec.Create(new MemoryStream(raw, i, raw.Length - i, writable: false));
                if (codec?.EncodedFormat == SKEncodedImageFormat.Jpeg && (long)codec.Info.Width * codec.Info.Height > best.Pixels)
                    best = (i, (long)codec.Info.Width * codec.Info.Height);
            }
            catch (Exception) { }
        }
        return best.Start < 0 ? null : raw[best.Start..];
    }

    /// <summary>Dekodiert verkleinert (JPEG kann 1/2, 1/4, 1/8 direkt), hoechstens etwa doppelt so gross wie noetig.</summary>
    private static SKBitmap? Decode(Stream stream, int size, out SKEncodedOrigin origin)
    {
        origin = SKEncodedOrigin.TopLeft;
        using var codec = SKCodec.Create(stream);
        if (codec == null) return null;
        origin = codec.EncodedOrigin;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;
        // Beim Dekodieren schon verkleinern (JPEG in Achtelschritten), aber nicht unter die Zielgröße - das Feine macht danach das
        // kubische Verkleinern. Früher mit doppeltem Rand: ein 26-MP-Foto wurde für 2048 px ganz dekodiert (Sekunden auf dem Server)
        var max = Math.Max(info.Width, info.Height);
        var dims = codec.GetScaledDimensions(Math.Min(1f, (float)size / max));
        if (Math.Max(dims.Width, dims.Height) < Math.Min(size, max)) dims = info.Size;
        if ((long)dims.Width * dims.Height > MaxPixels) return null;
        var target = new SKImageInfo(dims.Width, dims.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(target);
        var result = codec.GetPixels(target, bmp.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bmp;
        bmp.Dispose();
        return null;
    }

    private static SKBitmap? RenderPdf(Stream stream, int size)
    {
        // Im Container: Poppler als eigener Prozess (Zeit- und Speichergrenze), Absturz oder Hänger trifft nicht den Server.
        // Die PDF nur einmal im Speicher - vorher bis zu dreimal, bei vielen PDFs (DMS-Übernahme) blieb der Speicher zerstückelt
        if (PdfTool.Available)
        {
            byte[] pdf;
            if (stream.CanSeek)
            {
                pdf = new byte[stream.Length];
                stream.Position = 0;
                stream.ReadExactly(pdf);
            }
            else
            {
                using var all = new MemoryStream();
                stream.CopyTo(all);
                pdf = all.ToArray();
            }
            if (PdfTool.Info(pdf) is not { Width: > 0, Height: > 0 } info || info.Height / info.Width > 4 || info.Width / info.Height > 4) return null;
            return PdfTool.Render(pdf, 0, size) is { } jpeg ? SKBitmap.Decode(jpeg) : null;
        }
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            stream = copy;
        }
        // Nicht ewig auf die Sperre warten: haengt PDFium an einer Datei, stehen sonst alle PDF-Vorschauen
        if (!Monitor.TryEnter(PdfLock, TimeSpan.FromSeconds(30))) return null;
        try
        {
            // Extreme Seitenverhaeltnisse (Seite 1×14400 pt) ergaeben bei fester Breite riesige Bilder
            var page = PDFtoImage.Conversion.GetPageSize(stream, 0, leaveOpen: true, password: null);
            stream.Position = 0;
            if (page.Width <= 0 || page.Height <= 0 || page.Height / page.Width > 4 || page.Width / page.Height > 4) return null;
            // Seite 1 in Zielbreite, Hoehe nach Seitenverhaeltnis; weisser Grund (PDF-Seiten sind sonst durchsichtig)
            return PDFtoImage.Conversion.ToImage(stream, 0, leaveOpen: true, password: null,
                new PDFtoImage.RenderOptions { Width = size, WithAspectRatio = true, BackgroundColor = SKColors.White, WithAnnotations = true });
        }
        finally { Monitor.Exit(PdfLock); }
    }

    /// <summary>In die richtige Lage drehen (EXIF-Ausrichtung) und auf hoechstens size×size verkleinern.</summary>
    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin o, int size)
    {
        // Mehr als halbieren: erst in Halbschritten (je 2:1 gemittelt), damit das kubische Verkleinern danach nicht flimmert
        while (Math.Max(src.Width, src.Height) >= 2 * size)
        {
            var half = src.Resize(new SKImageInfo(Math.Max(1, src.Width / 2), Math.Max(1, src.Height / 2), SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (half == null) break;
            src.Dispose();
            src = half;
        }
        bool swap = o is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        float s = Math.Min(1f, (float)size / Math.Max(src.Width, src.Height));
        float w = src.Width * s, h = src.Height * s;
        int dw = Math.Max(1, (int)Math.Round(swap ? h : w)), dh = Math.Max(1, (int)Math.Round(swap ? w : h));
        var dst = new SKBitmap(new SKImageInfo(dw, dh, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var c = new SKCanvas(dst);
        c.Clear(SKColors.Transparent);
        switch (o)
        {
            case SKEncodedOrigin.TopRight: c.Translate(dw, 0); c.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: c.Translate(dw, dh); c.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: c.Translate(0, dh); c.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: c.RotateDegrees(90); c.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: c.Translate(dw, 0); c.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: c.Translate(dw, dh); c.RotateDegrees(90); c.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: c.Translate(0, dh); c.RotateDegrees(270); break;
        }
        c.Scale(s);
        using var img = SKImage.FromBitmap(src);
        // Kubisch (Catmull-Rom) statt linear: Kanten bleiben scharf; das Dekodieren hat schon auf höchstens das Doppelte verkleinert
        c.DrawImage(img, 0, 0, s < 1f ? new SKSamplingOptions(SKCubicResampler.CatmullRom) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        c.Flush();
        if (!ReferenceEquals(src, dst)) src.Dispose();
        return dst;
    }

    private static byte[]? Encode(SKBitmap bmp, int size)
    {
        // PDF-Seite: nach der Breite gerendert, hier noch in das Quadrat einpassen
        SKBitmap fit = bmp;
        if (Math.Max(bmp.Width, bmp.Height) > size) fit = Orient(bmp.Copy(), SKEncodedOrigin.TopLeft, size);
        try
        {
            using var img = SKImage.FromBitmap(fit);
            // Große Vorschau (Galerie, 2048 px) mit 75: im Schnitt 190 statt 300 KB je Foto, am Bildschirm kein sichtbarer Unterschied;
            // die kleinen bleiben bei 85 (dort spart es kaum etwas, wirkt aber schneller weich)
            using var data = img.Encode(SKEncodedImageFormat.Webp, size >= LegacyLarge ? 75 : 85);
            return data?.ToArray();
        }
        finally { if (!ReferenceEquals(fit, bmp)) fit.Dispose(); }
    }
}
