using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;
using SkiaSharp;

namespace DmOffice.Gallery;

/// <summary>
/// Foto-Export (Kennung photoexport, docs/foto-export-konzept.md): markierte Fotos der Galerie - aus der Ablage, aus geteilten Ordnern
/// und aus Galerie-Ordnern, JPG wie RAW - als neue Dateien in einen Ordner in Meine Dateien. Format (JPG mit Qualität, PNG), Größe
/// (Original, lange Kante, Druckgröße in cm mit dpi - eingepasst, nie vergrößert), bearbeitet oder Original, Ortsangaben entfernen,
/// Ablage wie bei den Importprofilen (dieselben Muster und Platzhalter, <see cref="ImportProfiles.Patterns"/>): Ordnerstruktur
/// (Unterordner im Zielordner), Dateiname, Titel und Beschreibung (in die Datei geschrieben). Läuft als Aktivität „Fotos exportieren“: Fortschritt, abbrechbar,
/// Fehler je Foto statt Abbruch, am Ende ein Verweis auf den Zielordner. Nie überschreiben: belegte Namen bekommen „ (2)“.
/// </summary>
public static class PhotoExport
{
    public const string Jpg = "jpg", Png = "png";
    public const string SizeOriginal = "original", SizeLong = "long", SizePrint = "print";
    public const string DefaultFolder = "Export", DefaultPattern = "{exif_date}_{sequence}";
    public const int MaxPhotos = 2000;
    private const string Key = "gallery.export";

    /// <summary>Einstellungen eines Exports (auch als Vorgabe gespeichert).</summary>
    public sealed record Options
    {
        public string Format { get; init; } = Jpg;
        public int Quality { get; init; } = 90;
        public string Size { get; init; } = SizeOriginal;
        public int LongEdge { get; init; } = 3000;
        public double WidthCm { get; init; } = 42.0;
        public double HeightCm { get; init; } = 29.7;
        public int Dpi { get; init; } = 300;
        /// <summary>Druckgröße: Hoch- und Querformat folgen dem Foto (sonst bleibt das Maß wie eingegeben).</summary>
        public bool AutoOrient { get; init; } = true;
        /// <summary>Bearbeitet (Drehen, Ausschnitt, Regler) oder das Original.</summary>
        public bool Edited { get; init; } = true;
        public bool RemoveGps { get; init; } = true;
        public string Folder { get; init; } = DefaultFolder;
        /// <summary>Dateinamen-Muster ohne Endung.</summary>
        public string Pattern { get; init; } = DefaultPattern;
        /// <summary>Ordnerstruktur-Muster: Unterordner im Zielordner; leer = alle Dateien direkt in den Zielordner.</summary>
        public string Folders { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";

        [System.Text.Json.Serialization.JsonIgnore]
        public ImportProfiles.Patterns Patterns => new(Folders, Pattern, Title, Description);

        public Options Clean() => this with
        {
            Format = Format == Png ? Png : Jpg,
            Quality = Math.Clamp(Quality, 10, 100),
            Size = Size is SizeLong or SizePrint ? Size : SizeOriginal,
            LongEdge = Math.Clamp(LongEdge, 16, 30000),
            WidthCm = Math.Clamp(double.IsFinite(WidthCm) ? WidthCm : 10, 0.5, 500),
            HeightCm = Math.Clamp(double.IsFinite(HeightCm) ? HeightCm : 10, 0.5, 500),
            Dpi = Math.Clamp(Dpi, 30, 2400),
            Folder = CleanFolder(Folder),
            Pattern = string.IsNullOrWhiteSpace(Pattern) ? DefaultPattern : Pattern.Trim(),
            Folders = (Folders ?? "").Trim(),
            Title = (Title ?? "").Trim(),
            Description = (Description ?? "").Trim(),
        };
    }

    public sealed record Preset(string Name, Options Options);

    public sealed class Stored
    {
        public List<Preset> Presets { get; set; } = [];
        public Options? Last { get; set; }
    }

    public record ExportBody(List<string> Ids, Options? Options, string? Lang);
    public record PresetBody(string Name, Options Options);

    public static Stored Of(UserDb db) => db.GetJson<Stored>(Key);

    /// <summary>Ordner in Meine Dateien: Teile dateisystemfest, leer → „Export“.</summary>
    public static string CleanFolder(string? folder)
    {
        var parts = (folder ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => ImportProfiles.Safe(p)).Where(p => p is not ("." or ".." or "_"));
        var s = string.Join('/', parts);
        return s.Length == 0 ? DefaultFolder : s;
    }

    // ---------------- Größe ----------------

    /// <summary>Druckmaß in Pixeln: cm / 2,54 × dpi.</summary>
    public static int Pixels(double cm, int dpi) => Math.Max(1, (int)Math.Round(cm / 2.54 * dpi));

    /// <summary>
    /// Zielmaße für ein (aufrechtes, bearbeitetes) Foto von <paramref name="w"/>×<paramref name="h"/>. Eingepasst, nie beschnitten,
    /// nie vergrößert. Short: das Foto hat weniger Pixel, als die gewünschte Größe braucht (es bleibt dann so groß, wie es ist).
    /// </summary>
    public static (int W, int H, bool Short) Target(int w, int h, Options o)
    {
        if (w <= 0 || h <= 0) return (w, h, false);
        double scale;
        switch (o.Size)
        {
            case SizeLong:
                scale = (double)o.LongEdge / Math.Max(w, h);
                break;
            case SizePrint:
                int bw = Pixels(o.WidthCm, o.Dpi), bh = Pixels(o.HeightCm, o.Dpi);
                if (o.AutoOrient && w != h && bw != bh && (w > h) != (bw > bh)) (bw, bh) = (bh, bw);
                scale = Math.Min((double)bw / w, (double)bh / h);
                break;
            default: return (w, h, false);
        }
        // Rundung: erst ab einem ganzen Pixel zu wenig gilt das Foto als zu klein
        var isShort = scale * Math.Max(w, h) > Math.Max(w, h) + 0.5;
        if (scale >= 1) return (w, h, isShort);
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)), false);
    }

    // ---------------- Dateinamen ----------------

    public static string Extension(Options o) => o.Format == Png ? ".png" : ".jpg";

    /// <summary>Dateiname nach Muster (Platzhalter der Importprofile; {sequence} = Platz in der Auswahl, ab 1).</summary>
    public static string NameFor(string pattern, ImportProfiles.Facts facts, int sequence, DateTime now, CultureInfo culture, Options o) =>
        ImportProfiles.RenderName(pattern, facts, now, sequence, culture) + Extension(o);

    /// <summary>Ordner der Datei: Zielordner und die aufgelöste Ordnerstruktur darunter.</summary>
    public static string DirFor(string dir, ImportProfiles.Placement p) =>
        p.Folder.Length == 0 ? dir : dir.Length == 0 ? p.Folder : dir + "/" + p.Folder;

    /// <summary>Freier Pfad im Zielordner: belegt → „Name (2).jpg“; nie überschreiben.</summary>
    public static string FreePath(UserDb db, string dir, string name)
    {
        var path = dir.Length == 0 ? name : dir + "/" + name;
        return FileStore.Get(db, path) != null ? FileStore.FreeName(db, path) : path;
    }

    // ---------------- Bild ----------------

    /// <summary>Fehler an einem Foto (kommt in die Liste am Ende, der Export läuft weiter).</summary>
    public sealed class PhotoFailed(string message) : Exception(message);
    private const long MaxSourceBytes = 250L * 1024 * 1024;

    public sealed record Rendered(byte[] Data, int Width, int Height, bool Short);

    private static readonly PhotoAdjust.Values Neutral = new();

    /// <summary>
    /// Ein Foto rechnen: entwickeln (RAW nur mit der Photo-Engine), bearbeiten (Drehen, Ausschnitt, Regler) oder nicht, verkleinern,
    /// kodieren, Metadaten setzen (Aufnahmedaten bleiben, Drehangabe 1, neue Maße, dpi, Ortsangaben auf Wunsch weg).
    /// </summary>
    /// <param name="text">Titel und Beschreibung (XMP); null = keine.</param>
    public static async Task<Rendered> RenderAsync(byte[] src, string name, PhotoEdits.Edit? edit, Options o, ILogger? log, CancellationToken ct,
        PhotoMeta.Fields? text = null, string? lensUser = null)
    {
        var e = o.Edited ? edit : null;
        PhotoAdjust.Values? adjust = e?.Adjust is { IsNeutral: false } a ? a.Clamped() : null;
        var metaSource = src;
        var dropIcc = false;
        byte[]? developed = null;
        // [feature:lenscorr]
        // Objektivkorrektur wie gemerkt (Brennweite und Blende aus dem Original); rechnen kann sie nur die Engine
        var lens = e?.Lens is { Off: false } && PhotoEngine.Configured ? LensProfiles.Block(e.Lens, LensProfiles.MetaOf(src), lensUser) : null;
        if (Thumbs.IsRaw(name))
        {
            developed = await PhotoEngine.DevelopAsync(src, adjust ?? Neutral, null, 95, log, ct, lens)
                ?? throw new PhotoFailed("RAW lässt sich nur mit der Photo-Engine entwickeln - sie antwortet gerade nicht.");
            metaSource = developed;
            adjust = null;
        }
        else if ((adjust != null || lens != null) && PhotoEngine.Configured && PhotoEdit.IsJpeg(src)
            && await PhotoEngine.DevelopAsync(src, adjust ?? Neutral, null, 95, log, ct, lens) is { } dev)
        // [feature:lenscorr:else]
        // if (Thumbs.IsRaw(name))
        // {
        //     developed = await PhotoEngine.DevelopAsync(src, adjust ?? Neutral, null, 95, log, ct)
        //         ?? throw new PhotoFailed("RAW lässt sich nur mit der Photo-Engine entwickeln - sie antwortet gerade nicht.");
        //     metaSource = developed;
        //     adjust = null;
        // }
        // else if (adjust != null && PhotoEngine.Configured && PhotoEdit.IsJpeg(src)
        //     && await PhotoEngine.DevelopAsync(src, adjust, null, 95, log, ct) is { } dev)
        // [/feature:lenscorr]
        {
            // Die Engine liefert aufrecht und ohne Metadaten: die kommen aus dem Original, dessen Farbprofil passt dann nicht mehr
            developed = dev;
            adjust = null;
            dropIcc = true;
        }

        var bmp = Decode(developed ?? src);
        try
        {
            if (e != null && (e.Rotate % 360 != 0 || e.Crop != null)) Swap(ref bmp, PhotoEdit.TurnAndCrop(bmp, e.Rotate, e.Crop));
            if (adjust != null) Swap(ref bmp, PhotoAdjust.Apply(bmp, adjust));
            var (w, h, isShort) = Target(bmp.Width, bmp.Height, o);
            if (w != bmp.Width || h != bmp.Height)
                Swap(ref bmp, bmp.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
                    ?? throw new PhotoFailed("Das Foto ließ sich nicht verkleinern."));
            using var img = SKImage.FromBitmap(bmp);
            var segments = PhotoEdit.IsJpeg(metaSource) ? PhotoEdit.Segments(metaSource) : [];
            byte[] data;
            if (o.Format == Png)
            {
                using var png = img.Encode(SKEncodedImageFormat.Png, 100) ?? throw new PhotoFailed("PNG ließ sich nicht schreiben.");
                var exif = segments.FirstOrDefault(s => s.IsExif) is { } x ? PatchExif(x.Bytes, w, h, o.Dpi, o.RemoveGps) : null;
                data = PngWithMeta(png.ToArray(), o.Dpi, exif, text != null ? PhotoMeta.WriteXmp(null, text, packet: true) : null);
            }
            else
            {
                using var jpg = img.Encode(SKEncodedImageFormat.Jpeg, o.Quality) ?? throw new PhotoFailed("JPG ließ sich nicht schreiben.");
                data = JpegWithMeta(jpg.ToArray(), segments, w, h, o.Dpi, o.RemoveGps, dropIcc);
                // Titel und Beschreibung ins XMP wie bei den Importprofilen (vorhandenes XMP der Quelle bleibt, nur diese Angaben ändern sich)
                if (text != null) data = PhotoMeta.WriteJpeg(data, text);
            }
            return new Rendered(data, w, h, isShort);
        }
        finally { bmp.Dispose(); }
    }

    private static void Swap(ref SKBitmap bmp, SKBitmap next)
    {
        if (ReferenceEquals(bmp, next)) return;
        bmp.Dispose();
        bmp = next;
    }

    /// <summary>Lesen und aufrecht stellen (EXIF-Drehangabe).</summary>
    private static SKBitmap Decode(byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data)) ?? throw new PhotoFailed("Das Format lässt sich nicht lesen.");
        var origin = codec.EncodedOrigin;
        var raw = SKBitmap.Decode(codec) ?? throw new PhotoFailed("Das Foto ließ sich nicht lesen.");
        var up = PhotoEdit.Upright(raw, origin);
        if (!ReferenceEquals(up, raw)) raw.Dispose();
        return up;
    }

    // ---------------- Metadaten: JPG ----------------

    /// <summary>
    /// SOI, JFIF mit dpi, die APP-Abschnitte der Quelle (EXIF angepasst, XMP ohne Ortsangaben auf Wunsch, Farbprofil und IPTC
    /// unverändert), dann Tabellen und Bilddaten des neu kodierten Bildes.
    /// </summary>
    public static byte[] JpegWithMeta(byte[] encoded, List<PhotoEdit.Segment> source, int w, int h, int dpi, bool removeGps, bool dropIcc = false)
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write(Jfif(dpi));
        foreach (var s in source.Where(s => s.Marker is >= 0xE1 and <= 0xEF))
        {
            if (s.IsExif) { ms.Write(PatchExif(s.Bytes, w, h, dpi, removeGps)); continue; }
            if (s.IsXmp)
            {
                if (removeGps && IsExtendedXmp(s.Bytes)) continue;
                ms.Write(removeGps ? XmpWithoutGps(s.Bytes) : s.Bytes);
                continue;
            }
            if (dropIcc && s.Marker == 0xE2) continue;
            ms.Write(s.Bytes);
        }
        var pos = 2;
        foreach (var s in PhotoEdit.Segments(encoded))
        {
            if (!(s.Marker is >= 0xE0 and <= 0xEF)) ms.Write(s.Bytes);
            pos += s.Bytes.Length;
        }
        while (pos + 1 < encoded.Length && !(encoded[pos] == 0xFF && encoded[pos + 1] == 0xDA)) pos++;
        ms.Write(encoded, pos, encoded.Length - pos);
        return ms.ToArray();
    }

    /// <summary>JFIF-Kopf (APP0) mit Auflösung in dpi.</summary>
    public static byte[] Jfif(int dpi)
    {
        var d = (ushort)Math.Clamp(dpi, 1, ushort.MaxValue);
        return [0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x01,
            (byte)(d >> 8), (byte)d, (byte)(d >> 8), (byte)d, 0x00, 0x00];
    }

    private const int TiffStart = 10; // FF E1, Länge, „Exif\0\0“

    /// <summary>
    /// EXIF-Abschnitt anpassen (Kopie): Drehangabe 1, Bildmaße, Auflösung (dpi, Einheit Zoll), Vorschaubild (IFD1) abhängen und
    /// löschen; auf Wunsch das GPS-IFD samt Werten löschen und den Verweis darauf entfernen.
    /// </summary>
    public static byte[] PatchExif(byte[] seg, int w, int h, int dpi, bool removeGps)
    {
        var b = (byte[])seg.Clone();
        if (b.Length < TiffStart + 8) return b;
        var le = b[TiffStart] == 'I';
        if (!le && b[TiffStart] != 'M') return b;
        var ifd0 = TiffStart + (int)Read32(b, TiffStart + 4, le);
        if (ifd0 + 2 > b.Length) return b;

        void Visit(int at, bool follow)
        {
            if (at < TiffStart || at + 2 > b.Length) return;
            var n = Read16(b, at, le);
            for (var i = 0; i < n; i++)
            {
                var e = at + 2 + i * 12;
                if (e + 12 > b.Length) return;
                var tag = Read16(b, e, le);
                var type = Read16(b, e + 2, le);
                switch (tag)
                {
                    case 0x0112 when type == 3: Write16(b, e + 8, 1, le); break;
                    case 0xA002 or 0x0100 or 0xA003 or 0x0101:
                        var v = tag is 0xA002 or 0x0100 ? w : h;
                        if (type == 3) Write16(b, e + 8, (ushort)Math.Min(v, ushort.MaxValue), le); else if (type == 4) Write32(b, e + 8, (uint)v, le);
                        break;
                    case 0x011A or 0x011B when type == 5:
                        var off = TiffStart + (int)Read32(b, e + 8, le);
                        if (off + 8 <= b.Length) { Write32(b, off, (uint)dpi, le); Write32(b, off + 4, 1, le); }
                        break;
                    case 0x0128 when type == 3: Write16(b, e + 8, 2, le); break;
                    case 0x8769 when follow: Visit(TiffStart + (int)Read32(b, e + 8, le), false); break;
                }
            }
        }
        Visit(ifd0, true);

        if (removeGps) RemoveGps(b, ifd0, le);

        // Vorschaubild: IFD1 samt Bild löschen, Verweis auf 0 - es zeigte sonst das ganze, unbearbeitete Foto
        var count = Read16(b, ifd0, le);
        var next = ifd0 + 2 + count * 12;
        if (next + 4 <= b.Length)
        {
            var ifd1 = TiffStart + (int)Read32(b, next, le);
            if (Read32(b, next, le) != 0 && ifd1 + 2 <= b.Length)
            {
                int thumbAt = -1, thumbLen = 0;
                var n1 = Read16(b, ifd1, le);
                for (var i = 0; i < n1; i++)
                {
                    var e = ifd1 + 2 + i * 12;
                    if (e + 12 > b.Length) break;
                    var tag = Read16(b, e, le);
                    if (tag == 0x0201) thumbAt = TiffStart + (int)Read32(b, e + 8, le);
                    if (tag == 0x0202) thumbLen = (int)Read32(b, e + 8, le);
                }
                if (thumbAt >= TiffStart && thumbLen > 0 && thumbAt + thumbLen <= b.Length) Array.Clear(b, thumbAt, thumbLen);
                ClearIfd(b, ifd1, le);
            }
            Write32(b, next, 0, le);
        }
        return b;
    }

    /// <summary>GPS-IFD (Verweis 0x8825 in IFD0) mit seinen Werten nullen und den Verweis aus IFD0 nehmen.</summary>
    private static void RemoveGps(byte[] b, int ifd0, bool le)
    {
        var n = Read16(b, ifd0, le);
        for (var i = 0; i < n; i++)
        {
            var e = ifd0 + 2 + i * 12;
            if (e + 12 > b.Length) return;
            if (Read16(b, e, le) != 0x8825) continue;
            var gps = TiffStart + (int)Read32(b, e + 8, le);
            if (gps >= TiffStart && gps + 2 <= b.Length) ClearIfd(b, gps, le);
            // Einträge danach und den Verweis auf IFD1 um einen Eintrag nach vorn, Zahl der Einträge eins weniger
            var end = ifd0 + 2 + n * 12 + 4;
            if (end > b.Length) return;
            Buffer.BlockCopy(b, e + 12, b, e, end - e - 12);
            Array.Clear(b, end - 12, 12);
            Write16(b, ifd0, (ushort)(n - 1), le);
            return;
        }
    }

    private static readonly int[] TypeSize = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8];

    /// <summary>Ein IFD samt seinen außerhalb stehenden Werten mit Nullen überschreiben.</summary>
    private static void ClearIfd(byte[] b, int at, bool le)
    {
        var n = Read16(b, at, le);
        for (var i = 0; i < n; i++)
        {
            var e = at + 2 + i * 12;
            if (e + 12 > b.Length) break;
            var type = Read16(b, e + 2, le);
            var size = (long)(type < TypeSize.Length ? TypeSize[type] : 1) * Read32(b, e + 4, le);
            if (size > 4)
            {
                var off = TiffStart + (long)Read32(b, e + 8, le);
                if (off >= TiffStart && off + size <= b.Length) Array.Clear(b, (int)off, (int)size);
            }
        }
        var len = Math.Min(2 + n * 12 + 4, b.Length - at);
        Array.Clear(b, at, len);
    }

    private static bool IsExtendedXmp(byte[] seg) =>
        seg.Length > 40 && Encoding.Latin1.GetString(seg, 4, Math.Min(36, seg.Length - 4)).StartsWith("http://ns.adobe.com/xmp/extension/", StringComparison.Ordinal);

    private static readonly Regex GpsAttr = new(@"\s(exif|exifEX):GPS\w+\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.Compiled);
    private static readonly Regex GpsElem = new(@"<(exif|exifEX):(GPS\w+)\b[^>]*?(/>|>.*?</\1:\2\s*>)", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>XMP ohne exif:GPS…-Angaben (Attribute und Elemente); die Länge im Kopf des Abschnitts neu.</summary>
    public static byte[] XmpWithoutGps(byte[] seg)
    {
        var text = Encoding.Latin1.GetString(seg, 4, seg.Length - 4);
        var clean = GpsElem.Replace(GpsAttr.Replace(text, ""), "");
        if (clean.Length == text.Length) return seg;
        var payload = Encoding.Latin1.GetBytes(clean);
        var len = payload.Length + 2;
        if (len > 0xFFFF) return seg;
        return [0xFF, seg[1], (byte)(len >> 8), (byte)len, .. payload];
    }

    private static int Read16(byte[] b, int i, bool le) => le ? b[i] | b[i + 1] << 8 : b[i] << 8 | b[i + 1];
    private static uint Read32(byte[] b, int i, bool le) => le ? (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24) : (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
    private static void Write16(byte[] b, int i, ushort v, bool le) { if (le) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); } else { b[i] = (byte)(v >> 8); b[i + 1] = (byte)v; } }
    private static void Write32(byte[] b, int i, uint v, bool le)
    {
        if (le) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); b[i + 2] = (byte)(v >> 16); b[i + 3] = (byte)(v >> 24); }
        else { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }
    }

    // ---------------- Metadaten: PNG ----------------

    /// <summary>PNG mit pHYs (dpi), falls vorhanden eXIf (die angepassten EXIF-Daten) und XMP (iTXt „XML:com.adobe.xmp“) gleich hinter IHDR.</summary>
    public static byte[] PngWithMeta(byte[] png, int dpi, byte[]? exifSegment, string? xmp = null)
    {
        const int afterIhdr = 8 + 4 + 4 + 13 + 4;
        if (png.Length < afterIhdr) return png;
        var ppm = (uint)Math.Round(dpi / 0.0254);
        using var ms = new MemoryStream();
        ms.Write(png, 0, afterIhdr);
        var phys = new byte[9];
        WriteBE(phys, 0, ppm); WriteBE(phys, 4, ppm); phys[8] = 1;
        Chunk(ms, "pHYs", phys);
        if (exifSegment != null && exifSegment.Length > TiffStart + 8) Chunk(ms, "eXIf", exifSegment[TiffStart..]);
        // iTXt: Schlüsselwort, 0, unkomprimiert (0, 0), Sprache leer, übersetztes Schlüsselwort leer, Text in UTF-8
        if (xmp != null) Chunk(ms, "iTXt", [.. "XML:com.adobe.xmp"u8, 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(xmp)]);
        // Ein pHYs des Kodierers selbst entfällt (es darf nur eines geben)
        var pos = afterIhdr;
        while (pos + 12 <= png.Length)
        {
            var len = (int)((uint)png[pos] << 24 | (uint)png[pos + 1] << 16 | (uint)png[pos + 2] << 8 | png[pos + 3]);
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var total = 12 + len;
            if (pos + total > png.Length) { ms.Write(png, pos, png.Length - pos); break; }
            if (type is not ("pHYs" or "eXIf") && !(xmp != null && type == "iTXt" && IsXmpChunk(png, pos))) ms.Write(png, pos, total);
            pos += total;
        }
        return ms.ToArray();
    }

    private static bool IsXmpChunk(byte[] png, int pos) =>
        pos + 8 + 17 <= png.Length && Encoding.ASCII.GetString(png, pos + 8, 17) == "XML:com.adobe.xmp";

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        WriteBE(head, 0, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        s.Write(head);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFF, head.AsSpan(4, 4)), data) ^ 0xFFFFFFFF;
        var tail = new byte[4];
        WriteBE(tail, 0, crc);
        s.Write(tail);
    }

    private static void WriteBE(byte[] b, int i, uint v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var x in data) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    // ---------------- Lauf ----------------

    /// <summary>Ein Foto im Auftrag: Name der Quelle, Dateizeit (ms), Bearbeitung, Laden der Bytes; Titel und Beschreibung aus der Galerie.</summary>
    public sealed record Item(string Name, long Modified, PhotoEdits.Edit? Edit, Func<CancellationToken, Task<byte[]>> Load, string? Title = null, string? Description = null,
        string? LensUser = null);

    /// <summary>Titel und Beschreibung der Galerie haben Vorrang vor denen in der Datei (in dmOffice bearbeitet).</summary>
    private static ImportProfiles.Facts WithGallery(ImportProfiles.Facts f, string? title, string? description) => f with
    {
        Title = string.IsNullOrWhiteSpace(title) ? f.Title : title,
        Description = string.IsNullOrWhiteSpace(description) ? f.Description : description,
    };

    public sealed record Result(int Written, int Failed, int Short, List<string> Errors, List<string> Paths, bool Stopped);

    /// <summary>
    /// Alle Fotos der Reihe nach rechnen und in <paramref name="dir"/> ablegen. Fehler je Foto kommen in die Liste, der Lauf geht
    /// weiter; nur ein voller Speicher und „Abbrechen“ beenden ihn.
    /// </summary>
    public static async Task<Result> RunAsync(UserDb db, string user, List<Item> items, Options o, string dir, TimeZoneInfo zone, CultureInfo culture,
        Activity.Job? job, ILogger? log, CancellationToken ct)
    {
        FileStore.EnsureDir(db, dir);
        var errors = new List<string>();
        var paths = new List<string>();
        int written = 0, failed = 0, small = 0;
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var stopped = false;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            try { await JobControl.CheckAsync(user, Activity.PhotoExport, ct); }
            catch (JobControl.Stopped) { stopped = true; break; }
            job?.Progress(i, items.Count, it.Name);
            try
            {
                var src = await it.Load(ct);
                var modified = it.Modified > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(it.Modified).UtcDateTime : DateTime.UtcNow;
                var facts = WithGallery(ImportProfiles.Read(new MemoryStream(src, false), Path.GetFileNameWithoutExtension(it.Name), modified, zone), it.Title, it.Description);
                // {sequence} = Platz in der Auswahl, ab 1
                var placement = o.Patterns.Render(facts, now, i + 1, culture);
                var folder = DirFor(dir, placement);
                if (FileStore.IsSystem(db, folder)) throw new PhotoFailed("Der Zielordner ist ein Systemordner.");
                var r = await RenderAsync(src, it.Name, it.Edit, o, log, ct, placement.Fields, it.LensUser);
                if (folder != dir) FileStore.EnsureDir(db, folder);
                var path = FreePath(db, folder, placement.Name + Extension(o));
                FileStore.Put(db, path, r.Data, o.Format == Png ? "image/png" : "image/jpeg");
                paths.Add(path);
                written++;
                if (r.Short) small++;
            }
            catch (QuotaExceededException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failed++;
                errors.Add($"{it.Name}: {Reason(e)}");
                log?.LogInformation("Foto-Export {Name}: {Error}", it.Name, e.Message);
            }
        }
        job?.Progress(written + failed, items.Count);
        return new Result(written, failed, small, errors, paths, stopped);
    }

    private static string Reason(Exception e) => e switch
    {
        PhotoFailed or ArgumentException or InvalidDataException => e.Message,
        KeyNotFoundException => "nicht gefunden",
        IOException or UnauthorizedAccessException => "nicht lesbar",
        _ => "ließ sich nicht exportieren",
    };

    /// <summary>Zusammenfassung für die Aktivität, in der Sprache der Oberfläche beim Start (en, sonst deutsch).</summary>
    public static string Summary(Result r, int total, string? lang = null)
    {
        var en = lang == "en";
        var parts = new List<string> { en
            ? (r.Stopped ? $"Cancelled: {r.Written} of {total} exported" : $"{r.Written} of {total} exported")
            : (r.Stopped ? $"Abgebrochen: {r.Written} von {total} exportiert" : $"{r.Written} von {total} exportiert") };
        if (r.Failed > 0) parts.Add(en ? $"{r.Failed} with errors" : $"{r.Failed} mit Fehler");
        if (r.Short > 0) parts.Add(en ? $"{r.Short} smaller than requested (not enlarged)" : $"{r.Short} kleiner als gewünscht (nicht vergrößert)");
        return string.Join(", ", parts);
    }

    // ---------------- Schnittstelle ----------------

    private static readonly ConcurrentDictionary<string, bool> Running = new();

    /// <summary>Ein Foto der Auswahl, aufgelöst: woher es kommt und wie es zu laden ist (ohne zu laden).</summary>
    private sealed record Source(string Id, string Name, long Modified, long Size, int? Width, int? Height, PhotoEdits.Edit? Edit,
        FileLoc? Own, GallerySource? Ext, string? ExtPath, string? Title = null, string? Description = null);

    private static List<Source> Resolve(HttpContext http, ServerConfig config, List<string> ids, Dictionary<string, GalleryTimeline.Entry> entries, List<string> missing)
    {
        var space = http.Space();
        var list = new List<Source>();
        foreach (var id in ids.Distinct())
        {
            try
            {
                var e = entries.GetValueOrDefault(id);
                if (id.StartsWith("ext:", StringComparison.Ordinal))
                {
                    var (src, path) = GalleryFolders.Resolve(space, config, id[4..]);
                    var name = path[(path.LastIndexOf('/') + 1)..];
                    list.Add(new Source(id, name, e?.Modified ?? 0, e?.Size ?? 0, e?.Meta?.Width, e?.Meta?.Height,
                        PhotoEdits.Get(space.Db, PhotoEdits.KeyOfExt(id[4..])), null, src, path, e?.Meta?.Title, e?.Description ?? e?.Meta?.Description));
                }
                else
                {
                    if (id.StartsWith("tresor:", StringComparison.Ordinal)) throw new KeyNotFoundException();
                    var loc = FilesApi.Loc(http, id).NotShowOnly("exportieren");
                    var f = FileStore.Get(loc.Db, loc.Path);
                    if (f == null || f.IsDir) throw new KeyNotFoundException();
                    list.Add(new Source(id, f.Name, new DateTimeOffset(DateTime.SpecifyKind(f.Modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), f.Size,
                        e?.Meta?.Width, e?.Meta?.Height, PhotoEdits.Get(loc.Db, PhotoEdits.KeyOf(f)), loc, null, null, e?.Meta?.Title, e?.Description ?? e?.Meta?.Description));
                }
            }
            catch (Exception x) when (x is KeyNotFoundException or UnauthorizedAccessException or ArgumentException) { missing.Add(id[(id.LastIndexOf('/') + 1)..]); }
        }
        return list;
    }

    /// <summary>Zielordner prüfen: nur Meine Dateien - kein Tresor, kein Galerie- oder externer Ordner, kein geteilter Ordner.</summary>
    private static string TargetDir(HttpContext http, string folder)
    {
        if (folder.StartsWith("tresor:", StringComparison.Ordinal) || folder.StartsWith("ext:", StringComparison.Ordinal) || folder.Contains(':'))
            throw new ArgumentException("Zielordner nur in Meine Dateien.");
        var loc = FilesApi.Loc(http, folder);
        if (loc.Shared || loc.Virtual || loc.Space.User.Id != http.Space().User.Id) throw new ArgumentException("Zielordner nur in Meine Dateien.");
        if (FileStore.Get(loc.Db, loc.Path) is { IsDir: false }) throw new ArgumentException("Am Zielort liegt eine Datei mit diesem Namen.");
        return loc.Path;
    }

    private static Item ItemOf(Source s, UserSpace space, ServerConfig config, Dictionary<string, IStorageDriver> drivers)
    {
        if (s.Own is { } loc)
            return new Item(s.Name, s.Modified, s.Edit, _ =>
                Task.FromResult(FileStore.Data(loc.Db, loc.Path) ?? throw new KeyNotFoundException()), s.Title, s.Description, loc.Db.UserId);
        return new Item(s.Name, s.Modified, s.Edit, async ct =>
        {
            var mount = s.Ext!.Mount;
            if (!drivers.TryGetValue(mount, out var driver))
            {
                if (!await ExternalFolders.ReachableAsync(space, config, mount)) throw new PhotoFailed("Galerie-Ordner nicht erreichbar.");
                driver = drivers[mount] = ExternalFolders.Open(space, config, mount).Driver;
            }
            await using var input = await driver.OpenReadAsync(s.ExtPath!, ct);
            // Die Größe nennt das Ziel - gelesen wird höchstens so viel wie für Vorschaubilder (große Handy- und RAW-Fotos passen)
            try { return await DmOffice.Core.ReadLimit.ReadAllAsync(input, MaxSourceBytes, ct); }
            catch (DmOffice.Core.ReadLimit.TooLargeException) { throw new PhotoFailed("Das Foto ist zu groß."); }
        }, s.Title, s.Description, space.User.Id);
    }

    /// <summary>Grobe Größe einer exportierten Datei (für die Platzprüfung vorab).</summary>
    public static long Estimate(int w, int h, Options o)
    {
        var px = (double)w * h;
        return (long)(o.Format == Png ? px * 3 : px * (0.08 + 0.006 * o.Quality));
    }

    private static object Settings(UserDb db)
    {
        var s = Of(db);
        // Platzhalter wie bei den Importprofilen
        return new
        {
            presets = s.Presets, last = s.Last ?? new Options(), defaults = new Options(),
            folderTokens = ImportProfiles.FolderTokens, nameTokens = ImportProfiles.NameTokens, textTokens = ImportProfiles.AllTokens,
        };
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/export/settings", (HttpContext http) => Results.Ok(Settings(http.Space().Db)));

        // Vorgabe speichern (gleicher Name: ersetzen) und löschen
        api.MapPut("/export/presets", (PresetBody b, HttpContext http) =>
        {
            var name = (b.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > 80) throw new ArgumentException("Bitte einen Namen angeben (höchstens 80 Zeichen).");
            if ((b.Options ?? new Options()).Clean().Patterns.Error() is { } bad) throw new ArgumentException(bad);
            var db = http.Space().Db;
            var s = Of(db);
            s.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (s.Presets.Count >= 50) throw new ArgumentException("Höchstens 50 Vorgaben.");
            s.Presets.Add(new Preset(name, b.Options.Clean()));
            s.Presets.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
            db.SetJson(Key, s);
            return Results.Ok(Settings(db));
        });
        api.MapDelete("/export/presets", (string name, HttpContext http) =>
        {
            var db = http.Space().Db;
            var s = Of(db);
            s.Presets.RemoveAll(p => p.Name == name);
            db.SetJson(Key, s);
            return Results.Ok(Settings(db));
        });

        // Vorab prüfen: erster Dateiname, wie viele Fotos zu klein sind, grob der Platzbedarf
        api.MapPost("/export/check", async (ExportBody b, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var o = (b.Options ?? new Options()).Clean();
            var space = http.Space();
            var patterns = o.Patterns;
            var unknown = new[] { patterns.Folders, patterns.Names, patterns.Title, patterns.Description }.SelectMany(ImportProfiles.Unknown).Distinct().ToList();
            var patternError = patterns.Error();
            var entries = (await GalleryTimeline.AllAsync(http, config)).ToDictionary(e => e.Id, StringComparer.Ordinal);
            var missing = new List<string>();
            var sources = Resolve(http, config, (b.Ids ?? []).Take(MaxPhotos).ToList(), entries, missing);
            var (need, small) = Need(sources, o);
            string? preview = null;
            ImportProfiles.Placement? first0 = null;
            if (sources.Count > 0 && patternError == null)
            {
                var first = sources[0];
                ImportProfiles.Facts facts;
                try
                {
                    var drivers = new Dictionary<string, IStorageDriver>();
                    try
                    {
                        var bytes = first.Own != null ? null : await ItemOf(first, space, config, drivers).Load(ct);
                        await using var stream = first.Own is { } loc ? FileStore.OpenRead(loc.Db, loc.Path) : new MemoryStream(bytes!, false);
                        facts = ImportProfiles.Read(stream, Path.GetFileNameWithoutExtension(first.Name),
                            DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, first.Modified)).UtcDateTime, space.Zone);
                    }
                    finally { foreach (var d in drivers.Values) d.Dispose(); }
                }
                catch (Exception e) when (e is IOException or KeyNotFoundException or PhotoFailed or UnauthorizedAccessException)
                {
                    facts = ImportProfiles.Example with { OriginalName = Path.GetFileNameWithoutExtension(first.Name), Title = null, Description = null };
                }
                facts = WithGallery(facts, first.Title, first.Description);
                first0 = patterns.Render(facts, TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone), 1, ImportProfiles.CultureOf(b.Lang));
                // Ganzer Pfad samt Unterordnern: „Export/2026/11/20261119_001.jpg“
                preview = DirFor(o.Folder, first0) + "/" + first0.Name + Extension(o);
            }
            string? folderError = null;
            try { TargetDir(http, o.Folder); }
            catch (Exception e) when (e is ArgumentException or KeyNotFoundException or UnauthorizedAccessException) { folderError = e.Message; }
            var free = FileStore.FreeRoom(space.Db);
            return Results.Ok(new
            {
                count = sources.Count, missing, preview, unknown, small, need, patternError,
                // Vorschau je Muster für die Eingabefelder
                folders = first0?.Folder, name = first0 == null ? null : first0.Name + Extension(o), title = first0?.Title, description = first0?.Description,
                free = free == long.MaxValue ? (long?)null : free,
                enough = free == long.MaxValue || need <= free,
                raw = sources.Count(s => Thumbs.IsRaw(s.Name)), engine = PhotoEngine.Configured,
                folder = o.Folder, folderError,
            });
        });

        api.MapPost("/export", async (ExportBody b, HttpContext http, ServerConfig config, ILoggerFactory logs) =>
        {
            var space = http.Space();
            var o = (b.Options ?? new Options()).Clean();
            if (o.Patterns.Error() is { } bad) throw new ArgumentException(bad);
            var ids = (b.Ids ?? []).Distinct().ToList();
            if (ids.Count == 0) throw new ArgumentException("Bitte Fotos auswählen.");
            if (ids.Count > MaxPhotos) throw new ArgumentException($"Höchstens {MaxPhotos} Fotos auf einmal.");
            var dir = TargetDir(http, o.Folder);
            var entries = (await GalleryTimeline.AllAsync(http, config)).ToDictionary(e => e.Id, StringComparer.Ordinal);
            var missing = new List<string>();
            var sources = Resolve(http, config, ids, entries, missing);
            if (sources.Count == 0) return Results.NotFound(new { error = "Diese Fotos gibt es nicht mehr." });
            // Grobe Platzprüfung: ohne Grenze immer, sonst nur, wenn es nach der Schätzung passt
            var free = FileStore.FreeRoom(space.Db);
            if (free != long.MaxValue)
            {
                if (Need(sources, o).Bytes > free) return Results.Conflict(new { error = "Dafür reicht der freie Speicherplatz voraussichtlich nicht." });
            }
            if (!Running.TryAdd(space.User.Id, true)) return Results.Conflict(new { error = "Es läuft schon ein Export. Bitte warten, bis er fertig ist." });
            var db = space.Db;
            var s0 = Of(db);
            s0.Last = o;
            db.SetJson(Key, s0);
            var log = logs.CreateLogger("DmOffice.Gallery.Export");
            var culture = ImportProfiles.CultureOf(b.Lang);
            var drivers = new Dictionary<string, IStorageDriver>();
            var items = sources.Select(s => ItemOf(s, space, config, drivers)).ToList();
            var errors0 = missing.Select(m => $"{m}: nicht gefunden").ToList();
            _ = Task.Run(async () =>
            {
                using var control = JobControl.Run(space.User.Id, Activity.PhotoExport);
                using var job = Activity.Start(space.User.Id, Activity.PhotoExport, null, items.Count);
                try
                {
                    var r = await RunAsync(db, space.User.Id, items, o, dir, space.Zone, culture, job, log, CancellationToken.None);
                    job.Finish(Summary(r with { Failed = r.Failed + errors0.Count }, items.Count + errors0.Count, b.Lang), [.. errors0, .. r.Errors], dir);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    log.LogWarning("Foto-Export für {User}: {Error}", space.User.Login, e.Message);
                    job.Fail(e is QuotaExceededException ? "Der Speicher ist voll." : e.Message);
                }
                finally
                {
                    foreach (var d in drivers.Values) d.Dispose();
                    Running.TryRemove(space.User.Id, out _);
                }
            });
            return Results.Accepted(value: new { started = true, count = items.Count, folder = dir });
        });
    }

    /// <summary>Grob: Platzbedarf und wie viele Fotos kleiner sind als gewünscht (aus den Maßen im Index, ohne die Fotos zu lesen).</summary>
    private static (long Bytes, int Short) Need(List<Source> sources, Options o)
    {
        long need = 0;
        var small = 0;
        foreach (var s in sources)
        {
            if (s.Width is not { } w0 || s.Height is not { } h0 || w0 <= 0 || h0 <= 0) { need += o.Format == Png ? s.Size * 3 : s.Size; continue; }
            // Ausschnitt verkleinert das Foto; Drehen tauscht Breite und Höhe
            var (w, h) = s.Edit is { } ed && o.Edited ? Cut(w0, h0, ed) : (w0, h0);
            var t = Target(w, h, o);
            if (t.Short) small++;
            need += Estimate(t.W, t.H, o);
        }
        return (need, small);
    }

    /// <summary>Maße nach Drehen und Ausschnitt (für die Schätzung vorab).</summary>
    private static (int W, int H) Cut(int w, int h, PhotoEdits.Edit e)
    {
        if (((e.Rotate % 180) + 180) % 180 == 90) (w, h) = (h, w);
        if (e.Crop is { } c) { w = Math.Max(1, (int)Math.Round(w * Math.Clamp(c.W, 0, 1))); h = Math.Max(1, (int)Math.Round(h * Math.Clamp(c.H, 0, 1))); }
        return (w, h);
    }
}
