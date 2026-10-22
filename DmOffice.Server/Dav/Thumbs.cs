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
    /// <summary>Kachel/Liste und Betrachter.</summary>
    public const int Small = 256, Large = 1600;

    public static int Snap(int size) => size <= Small ? Small : Large;

    /// <summary>Hoechstens so viele Bilder gleichzeitig - Dekodieren kostet Speicher und Rechenzeit.</summary>
    private static readonly SemaphoreSlim Gate = new(2);
    /// <summary>PDFium ist nicht threadsicher.</summary>
    private static readonly object PdfLock = new();

    private const long MaxImageBytes = 80L * 1024 * 1024, MaxPdfBytes = 100L * 1024 * 1024;
    /// <summary>Groesser dekodiert wird nicht (Pixel nach dem verkleinerten Dekodieren) - gegen Dekompressionsbomben.</summary>
    private const long MaxPixels = 60_000_000;

    private static readonly HashSet<string> ImageExt = [".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".bmp", ".ico"];

    public enum Kind { None, Image, Pdf, Video }

    public static Kind KindOf(string name, string? mime)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ImageExt.Contains(ext)) return Kind.Image;
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
    public static bool Available(UserDb db, FileEntry f)
    {
        if (f.IsDir || f.FileId <= 0 || f.Size == 0) return false;
        var kind = KindOf(f.Name, f.Mime);
        if (kind == Kind.None) return false;
        var c = Cached(db, f.FileId, Small);
        if (kind == Kind.Video) return c is { } v && v.Etag == f.Etag && v.Data.Length > 0;
        if (f.Size > (kind == Kind.Pdf ? MaxPdfBytes : MaxImageBytes)) return false;
        return !(c is { } x && x.Etag == f.Etag && x.Data.Length == 0);
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

    /// <summary>Vorschaubild (WebP) oder null, wenn es keines gibt.</summary>
    public static async Task<byte[]?> Get(UserDb db, FileEntry f, int size, CancellationToken ct)
    {
        if (f.IsDir || f.FileId <= 0) return null;
        size = Snap(size);
        RetryFailedOnce(db);
        if (Cached(db, f.FileId, size) is { } c && c.Etag == f.Etag) return c.Data.Length == 0 ? null : c.Data;
        var kind = KindOf(f.Name, f.Mime);
        // Videos: nur das Standbild aus dem Browser (Accept), auch fuer die grosse Ansicht
        if (kind == Kind.Video)
            return size == Large && Cached(db, f.FileId, Small) is { } v && v.Etag == f.Etag && v.Data.Length > 0 ? v.Data : null;
        if (kind == Kind.None || f.Size == 0 || f.Size > (kind == Kind.Pdf ? MaxPdfBytes : MaxImageBytes)) return null;
        // Kleines Bild ist schon klein genug: die grosse Ansicht braucht keine eigene Version
        await Gate.WaitAsync(ct);
        byte[]? data;
        bool failed = false;
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
                    using (var bmp = kind == Kind.Pdf ? RenderPdf(stream, size) : Decode(stream, size, out var origin2) is { } b ? Orient(b, origin2, size) : null)
                        return bmp == null ? null : Encode(bmp, size);
                }
                // Kaputte oder unlesbare Datei: das merken - ausser es war ein Lesefehler mitten im Strom
                catch (IOException) { failed = true; return null; }
                catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
            }, ct);
        }
        finally { Gate.Release(); }
        // Nur merken, was sicher kein Vorschaubild ergibt - ein voruebergehender Fehler soll es nicht fuer immer verhindern
        if (!failed) Store(db, f.FileId, size, f.Etag, data ?? []);
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

    /// <summary>Dekodiert verkleinert (JPEG kann 1/2, 1/4, 1/8 direkt), hoechstens etwa doppelt so gross wie noetig.</summary>
    private static SKBitmap? Decode(Stream stream, int size, out SKEncodedOrigin origin)
    {
        origin = SKEncodedOrigin.TopLeft;
        using var codec = SKCodec.Create(stream);
        if (codec == null) return null;
        origin = codec.EncodedOrigin;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;
        float scale = Math.Min(1f, 2f * size / Math.Max(info.Width, info.Height));
        var dims = codec.GetScaledDimensions(scale);
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
        c.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
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
            using var data = img.Encode(SKEncodedImageFormat.Webp, size == Small ? 78 : 85);
            return data?.ToArray();
        }
        finally { if (!ReferenceEquals(fit, bmp)) fit.Dispose(); }
    }
}
