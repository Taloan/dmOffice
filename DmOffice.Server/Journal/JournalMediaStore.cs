using System.Globalization;
using DmOffice.Core;
using DmOffice.Dav;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using SkiaSharp;

namespace DmOffice.Journal;

/// <summary>
/// Fotos, Videos und Sprachaufnahmen für das Tagebuch: kommen in die Dateiablage unter „sys-dmoffice-journal/Jahr/Monat“ -
/// ein Systemordner, den Dateiablage, Fotos, Suche und WebDAV nicht zeigen (wie der notizi-Ordner). Fotos werden
/// kleingerechnet (lange Kante 1024 oder 2048 Pixel, einstellbar; „0“: Original) - Aufnahmedatum und Ort werden vorher aus
/// dem Original gelesen (EXIF, GPS).
/// </summary>
public static class JournalMediaStore
{
    /// <summary>Systemordner der Tagebuch-Medien; ein Name, den kaum jemand selbst wählt.</summary>
    public const string Root = "sys-dmoffice-journal";
    /// <summary>Hier lagen die Medien bis Oktober 2026 (sichtbar) - <see cref="JournalMediaMove"/> zieht sie um.</summary>
    internal const string OldRoot = "Tagebuch";

    public const int DefaultSize = 2048;
    public static readonly int[] Sizes = [1024, 2048, 0];
    private const string SizeKey = "journal.imagesize";
    private const long MaxImageBytes = 200L * 1024 * 1024;

    /// <summary>Was aus der Datei gelesen wurde: wann und wo aufgenommen.</summary>
    public sealed record Found(JournalMedia Media, double? Lat, double? Lon);

    public static int ImageSize(UserDb db) => int.TryParse(db.Get(SizeKey), out var s) && Sizes.Contains(s) ? s : DefaultSize;

    public static void SetImageSize(UserDb db, int size)
    {
        if (!Sizes.Contains(size)) throw new ArgumentException("Erlaubt sind 1024, 2048 oder 0 (Originalgröße).");
        db.Set(SizeKey, size.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Neue Datei ins Tagebuch: Bilder kleingerechnet, alles andere wie es ist.</summary>
    public static async Task<Found> Store(JournalFiles files, string name, string? mime, Stream body, long size, CancellationToken ct)
    {
        var space = files.Space;
        name = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "Datei" : name.Trim());
        mime = string.IsNullOrWhiteSpace(mime) || mime == "application/octet-stream" ? MimeOf(name) : mime;
        if (mime.StartsWith("image/") && size <= MaxImageBytes)
        {
            var bytes = await ReadAll(body, ct);
            var (taken, lat, lon) = Meta(new MemoryStream(bytes), space.Zone);
            var max = ImageSize(space.Db);
            var shrunk = max > 0 ? Shrink(bytes, max) : null;
            int? w = null, h = null;
            if (shrunk is { } s) { bytes = s.Data; (w, h) = (s.Width, s.Height); name = Path.GetFileNameWithoutExtension(name) + ".jpg"; mime = "image/jpeg"; }
            else if (Dimensions(bytes) is { } d) (w, h) = d;
            var path = files.Target(name, taken);
            files.Put(path, bytes, mime, taken);
            return new Found(new JournalMedia(path, mime, bytes.LongLength, w, h, taken), lat, lon);
        }
        var target = files.Target(name, null);
        var stored = await files.PutAsync(target, body, mime, ct);
        // Videos: Aufnahmezeit und Ort aus der gespeicherten Datei (QuickTime/MP4), soweit vorhanden
        DateTime? when = null; double? la = null, lo = null;
        if (mime.StartsWith("video/") && !files.Dropping)
            try { await using var read = files.OpenRead(target); if (read.CanSeek) (when, la, lo) = Meta(read, space.Zone); }
            catch (Exception e) when (e is IOException or ImageProcessingException or MetadataException) { }
        return new Found(new JournalMedia(target, mime, stored, Taken: when), la, lo);
    }

    /// <summary>
    /// Bild aus einer fertigen großen Vorschau (Galerie-Ordner) ins Tagebuch - Aufnahmezeit und Ort kommen vom Aufrufer, weil die
    /// Vorschau keine Bildangaben hat. Ist die Vorschau kleiner als eingestellt (ältere mit 1600 px), wird sie so genommen - schneller
    /// als das Original über die Freigabe. Null: Das Tagebuch will die Originalgröße - dann das Original.
    /// </summary>
    public static Found? FromPreview(JournalFiles files, string name, byte[] preview, int previewSize, DateTime? taken, double? lat, double? lon)
    {
        var max = ImageSize(files.Space.Db);
        if (max == 0) return null;
        // Schon klein genug und JPEG (ältere Vorschau): so übernehmen, sonst als JPEG in der eingestellten Größe
        var s = Shrink(preview, Math.Min(max, previewSize));
        if (s == null && Dimensions(preview) is { } d) s = new Shrunk(preview, d.W, d.H);
        if (s == null) return null;
        var path = files.Target(Path.GetFileNameWithoutExtension(Path.GetFileName(name)) + ".jpg", taken);
        files.Put(path, s.Data, "image/jpeg", taken);
        return new Found(new JournalMedia(path, "image/jpeg", s.Data.LongLength, s.Width, s.Height, taken), lat, lon);
    }

    /// <summary>
    /// Datei aus der eigenen Dateiablage (source) anhängen: Bilder als kleine Kopie im Tagebuchordner, alles andere als Verweis.
    /// Gehört das Tagebuch jemand anderem oder liegt es im Tresor (target), kommt auch alles andere als Kopie dorthin - sonst
    /// sähe er es nicht bzw. läge es nicht im Tresor.
    /// </summary>
    public static Found FromFiles(UserSpace source, JournalFiles target, string path)
    {
        var space = source;
        var db = space.Db;
        var copy = target.Tresor != null || target.Dropping || target.Space != source;
        var f = FileStore.Get(db, FileStore.Normalize(path)) ?? throw new KeyNotFoundException("Diese Datei gibt es nicht.");
        if (f.IsDir) throw new ArgumentException("Ordner lassen sich nicht anhängen.");
        var mime = string.IsNullOrEmpty(f.Mime) ? MimeOf(f.Name) : f.Mime;
        if (copy && (!mime.StartsWith("image/") || f.Size > MaxImageBytes || ImageSize(target.Space.Db) == 0))
        {
            using var read = FileStore.OpenRead(db, f.Path);
            var dest = target.Target(f.Name, null);
            target.PutAsync(dest, read, mime, CancellationToken.None).GetAwaiter().GetResult();
            return new Found(new JournalMedia(dest, mime, f.Size), null, null);
        }
        if (!mime.StartsWith("image/") || f.Size > MaxImageBytes || ImageSize(db) == 0)
        {
            DateTime? when = null; double? la = null, lo = null;
            if (mime.StartsWith("image/") || mime.StartsWith("video/"))
                try { using var s = FileStore.OpenRead(db, f.Path); if (s.CanSeek) (when, la, lo) = Meta(s, space.Zone); }
                catch (Exception e) when (e is IOException or ImageProcessingException or MetadataException) { }
            return new Found(new JournalMedia(f.Path, mime, f.Size, Taken: when), la, lo);
        }
        var bytes = FileStore.Data(db, f.Path) ?? throw new KeyNotFoundException("Die Datei lässt sich nicht lesen.");
        var (taken, lat, lon) = Meta(new MemoryStream(bytes), space.Zone);
        var shrunk = Shrink(bytes, ImageSize(target.Space.Db));
        if (shrunk == null && !copy) return new Found(new JournalMedia(f.Path, mime, f.Size, Dimensions(bytes)?.W, Dimensions(bytes)?.H, taken), lat, lon);
        if (shrunk == null)
        {
            var dest = target.Target(f.Name, taken);
            target.Put(dest, bytes, mime, taken);
            return new Found(new JournalMedia(dest, mime, bytes.LongLength, Dimensions(bytes)?.W, Dimensions(bytes)?.H, taken), lat, lon);
        }
        var path2 = target.Target(Path.GetFileNameWithoutExtension(f.Name) + ".jpg", taken);
        target.Put(path2, shrunk.Data, "image/jpeg", taken);
        return new Found(new JournalMedia(path2, "image/jpeg", shrunk.Data.LongLength, shrunk.Width, shrunk.Height, taken), lat, lon);
    }

    /// <summary>Aufnahmezeit (UTC) und Ort aus EXIF/GPS bzw. QuickTime. Ohne Zeitzone im Bild gilt die eigene.</summary>
    public static (DateTime? Taken, double? Lat, double? Lon) Meta(Stream stream, TimeZoneInfo zone)
    {
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try { dirs = ImageMetadataReader.ReadMetadata(stream); }
        catch (Exception e) when (e is ImageProcessingException or IOException or MetadataException or ArgumentException) { return (null, null, null); }
        DateTime? taken = null;
        if (dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault() is { } exif
            && (exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var local) || exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out local)))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            // Versatz zur UTC, wenn die Kamera ihn mitschreibt („+02:00“)
            var offset = exif.GetDescription(ExifDirectoryBase.TagTimeZoneOriginal) ?? exif.GetDescription(ExifDirectoryBase.TagTimeZone);
            taken = offset != null && TimeSpan.TryParse(offset.TrimStart('+'), CultureInfo.InvariantCulture, out var off)
                ? DateTime.SpecifyKind(local - (offset.StartsWith('-') ? -off.Duration() : off), DateTimeKind.Utc)
                : Ics.ToUtc(local, zone);
        }
        else if (dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault() is { } mov
                 && mov.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) && created.Year > 1970)
            taken = DateTime.SpecifyKind(created, DateTimeKind.Utc);
        double? lat = null, lon = null;
        if (dirs.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation() is { } geo && !geo.IsZero)
            (lat, lon) = (Math.Round(geo.Latitude, 6), Math.Round(geo.Longitude, 6));
        return (taken, lat, lon);
    }

    private sealed record Shrunk(byte[] Data, int Width, int Height);

    /// <summary>Lange Kante auf max Pixel, richtig herum (EXIF), als JPEG. Null: kein lesbares Bild oder schon klein genug.</summary>
    private static Shrunk? Shrink(byte[] data, int max)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(data));
            if (codec == null) return null;
            var info = codec.Info;
            var origin = codec.EncodedOrigin;
            var longEdge = Math.Max(info.Width, info.Height);
            if (longEdge <= 0 || (longEdge <= max && origin == SKEncodedOrigin.TopLeft && codec.EncodedFormat == SKEncodedImageFormat.Jpeg)) return null;
            var scale = Math.Min(1f, (float)max / longEdge);
            // JPEG dekodiert direkt verkleinert (1/2, 1/4, 1/8) - höchstens doppelt so groß wie nötig
            var dims = codec.GetScaledDimensions(Math.Min(1f, scale * 2));
            using var decoded = new SKBitmap(new SKImageInfo(dims.Width, dims.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (codec.GetPixels(decoded.Info, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
            int w = Math.Max(1, (int)Math.Round(info.Width * scale)), h = Math.Max(1, (int)Math.Round(info.Height * scale));
            using var resized = decoded.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var oriented = Orient(resized, origin);
            using var img = SKImage.FromBitmap(oriented);
            return new Shrunk(img.Encode(SKEncodedImageFormat.Jpeg, 88).ToArray(), oriented.Width, oriented.Height);
        }
        catch (Exception) { return null; }
    }

    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin o)
    {
        if (o == SKEncodedOrigin.TopLeft) return src.Copy();
        var swap = o is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height);
        using var c = new SKCanvas(dst);
        switch (o)
        {
            case SKEncodedOrigin.TopRight: c.Scale(-1, 1, dst.Width / 2f, 0); break;
            case SKEncodedOrigin.BottomRight: c.RotateDegrees(180, dst.Width / 2f, dst.Height / 2f); break;
            case SKEncodedOrigin.BottomLeft: c.Scale(1, -1, 0, dst.Height / 2f); break;
            case SKEncodedOrigin.LeftTop: c.Translate(dst.Width, 0); c.RotateDegrees(90); c.Scale(1, -1, 0, src.Height / 2f); break;
            case SKEncodedOrigin.RightTop: c.Translate(dst.Width, 0); c.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: c.Translate(0, dst.Height); c.RotateDegrees(270); c.Scale(1, -1, 0, src.Height / 2f); break;
            case SKEncodedOrigin.LeftBottom: c.Translate(0, dst.Height); c.RotateDegrees(270); break;
        }
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    private static (int W, int H)? Dimensions(byte[] data)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(data));
            if (codec == null) return null;
            var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            return swap ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception) { return null; }
    }

    private static async Task<byte[]> ReadAll(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".heic" or ".heif" => "image/heic",
        ".gif" => "image/gif",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };
}

/// <summary>
/// Wohin die Medien eines Tagebuchs kommen: Dateiablage der Person (Systemordner) oder - bei einem Tagebuch im Tresor - die
/// Tresor-Datenbank (<see cref="TresorJournals"/>), bei geschlossenem Tresor über dessen öffentlichen Schlüssel abgelegt (<see cref="Dropping"/>).
/// Aus einem <see cref="UserSpace"/> wird ohne Weiteres die Dateiablage.
/// </summary>
public sealed class JournalFiles(UserSpace space, TresorDb? tresor, bool dropping = false)
{
    public UserSpace Space => space;
    public TresorDb? Tresor => tresor;
    /// <summary>Tagebuch im geschlossenen Tresor: nur ablegen, lesen geht nicht.</summary>
    public bool Dropping => dropping;

    public static implicit operator JournalFiles(UserSpace space) => new(space, null);

    /// <summary>„sys-dmoffice-journal/2026/10/Name.jpg“ (Monat der Aufnahme, sonst heute); freier Name, wenn es ihn schon gibt.</summary>
    public string Target(string name, DateTime? taken)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(taken ?? DateTime.UtcNow, space.Zone);
        var dir = $"{JournalMediaStore.Root}/{local:yyyy}/{local:MM}";
        if (dropping) return TresorJournals.DropPath(dir + "/" + name);
        if (tresor != null) return TresorJournals.FreeName(tresor, dir + "/" + name);
        FileStore.EnsureDir(space.Db, dir);
        return FileStore.FreeName(space.Db, dir + "/" + name);
    }

    public void Put(string path, byte[] data, string mime, DateTime? mtime)
    {
        if (dropping) TresorJournals.Drop(space, path, mime, data, mtime);
        else if (tresor != null) TresorJournals.PutAsync(tresor, path, new MemoryStream(data), mime, mtime, CancellationToken.None).GetAwaiter().GetResult();
        else FileStore.Put(space.Db, path, data, mime, mtime: mtime);
    }

    /// <summary>Zurück: Größe.</summary>
    public async Task<long?> PutAsync(string path, Stream data, string mime, CancellationToken ct)
    {
        if (dropping)
        {
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            TresorJournals.Drop(space, path, mime, ms.ToArray(), null);
            return ms.Length;
        }
        if (tresor != null) return await TresorJournals.PutAsync(tresor, path, data, mime, null, ct);
        await FileStore.PutAsync(space.Db, path, data, mime, ct: ct);
        return FileStore.Get(space.Db, path)?.Size;
    }

    public Stream OpenRead(string path) => dropping ? throw new TresorClosedException() : tresor != null
        ? TresorJournals.Open(tresor, TresorJournals.Media(tresor, path) ?? throw new KeyNotFoundException("Diese Datei gibt es nicht mehr."))
        : FileStore.OpenRead(space.Db, path);

    /// <summary>Größe und Änderungszeit; null: gibt es nicht.</summary>
    public (long Size, DateTime Modified)? Info(string path) => dropping ? null : tresor != null
        ? TresorJournals.Media(tresor, path) is { } m ? (m.Size, m.Modified) : null
        : FileStore.Get(space.Db, path) is { IsDir: false } f ? (f.Size, f.Modified) : null;
}
