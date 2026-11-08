using SkiaSharp;

namespace DmOffice.Dav;

/// <summary>
/// Fotos drehen und zuschneiden, die Metadaten bleiben: EXIF (Aufnahmezeit, Kamera, Objektiv, Belichtung, GPS), XMP, IPTC und das
/// Farbprofil werden aus dem Original übernommen; angepasst wird nur, was sich ändert - Drehangabe (danach „normal“), Bildmaße,
/// das eingebettete Vorschaubild fällt weg (es zeigte noch das alte Bild). Nur Drehen um 90° ohne Zuschnitt: verlustfrei, nur die
/// Drehangabe ändert sich. Sonst wird neu kodiert (JPEG, Qualität 93).
/// </summary>
public static class PhotoEdit
{
    /// <summary>Ausschnitt in Anteilen (0..1) des Bildes, wie es gezeigt wird - aufrecht und schon gedreht.</summary>
    public sealed record Crop(double X, double Y, double W, double H);

    public const int Quality = 93;

    public static bool IsJpeg(byte[] b) => b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8;

    /// <summary>
    /// Gedreht (0, 90, 180, 270 im Uhrzeigersinn) und zugeschnitten; Metadaten des Originals bleiben. <paramref name="meta"/>: Datei,
    /// aus der die Metadaten kommen, wenn <paramref name="jpeg"/> selbst keine hat (Ergebnis der Photo-Engine, aufrecht) - sonst fehlten
    /// in der gespeicherten Datei Aufnahmezeit, Kamera und Ort, und wer den Ordner neu einliest, sortiert das Foto nach dem Speicherdatum.
    /// </summary>
    public static byte[] Apply(byte[] jpeg, int rotate, Crop? crop, PhotoAdjust.Values? adjust = null, byte[]? meta = null)
    {
        var tuned = adjust != null && !adjust.IsNeutral;
        if (!IsJpeg(jpeg)) throw new ArgumentException("Nur JPEG-Fotos lassen sich hier bearbeiten.");
        rotate = ((rotate % 360) + 360) % 360;
        if (rotate % 90 != 0) throw new ArgumentException("Drehen nur in Vierteldrehungen.");
        var segments = Segments(jpeg);
        var orientation = Orientation(segments);
        var metaSegments = meta != null && IsJpeg(meta) ? Segments(meta) : null;
        var full = crop == null || crop.X <= 0.0001 && crop.Y <= 0.0001 && crop.W >= 0.9999 && crop.H >= 0.9999;
        // Nur drehen: verlustfrei über die Drehangabe (bei gespiegelten Angaben - selten - doch neu kodieren)
        if (full && !tuned && orientation is 1 or 3 or 6 or 8)
        {
            if (rotate == 0) return metaSegments == null ? jpeg : Rebuild(jpeg, segments, metaSegments, orientation, null);
            var target = Compose(orientation, rotate);
            return Rebuild(jpeg, segments, metaSegments, target, null);
        }
        using var codec = SKCodec.Create(new MemoryStream(jpeg)) ?? throw new ArgumentException("Das Foto ließ sich nicht lesen.");
        using var raw = SKBitmap.Decode(codec) ?? throw new ArgumentException("Das Foto ließ sich nicht lesen.");
        using var upright = Orient(raw, (SKEncodedOrigin)orientation);
        using var turned = Turn(upright, rotate);
        var c = crop ?? new Crop(0, 0, 1, 1);
        var x = (int)Math.Round(Math.Clamp(c.X, 0, 1) * turned.Width);
        var y = (int)Math.Round(Math.Clamp(c.Y, 0, 1) * turned.Height);
        var w = Math.Max(1, Math.Min(turned.Width - x, (int)Math.Round(Math.Clamp(c.W, 0, 1) * turned.Width)));
        var h = Math.Max(1, Math.Min(turned.Height - y, (int)Math.Round(Math.Clamp(c.H, 0, 1) * turned.Height)));
        using var cut = new SKBitmap(w, h, turned.ColorType, turned.AlphaType);
        if (!turned.ExtractSubset(cut, new SKRectI(x, y, x + w, y + h))) throw new ArgumentException("Ausschnitt ungültig.");
        using var final = tuned ? PhotoAdjust.Apply(cut, adjust!.Clamped()) : null;
        using var img = SKImage.FromBitmap(final ?? cut);
        var encoded = img.Encode(SKEncodedImageFormat.Jpeg, Quality).ToArray();
        return Rebuild(encoded, Segments(encoded), metaSegments ?? segments, 1, (w, h));
    }

    // ---------------- Drehen ----------------

    /// <summary>EXIF-Drehangabe nach einer weiteren Drehung im Uhrzeigersinn (nur die ungespiegelten 1, 6, 3, 8).</summary>
    public static int Compose(int orientation, int rotate)
    {
        int[] ring = [1, 6, 3, 8];
        var i = Array.IndexOf(ring, orientation);
        return ring[(i + rotate / 90) % 4];
    }

    /// <summary>Aufrecht: dasselbe Bild, wenn es schon aufrecht ist, sonst ein gedrehtes neues.</summary>
    public static SKBitmap Upright(SKBitmap src, SKEncodedOrigin origin) => origin == SKEncodedOrigin.TopLeft ? src : Orient(src, origin);

    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src.Copy();
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(dst.Width, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(dst.Width, dst.Height); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, dst.Height); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Scale(-1, 1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(dst.Width, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(dst.Width, dst.Height); canvas.Scale(-1, 1); canvas.RotateDegrees(-90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, dst.Height); canvas.RotateDegrees(-90); break;
        }
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>Für die Anzeige (bearbeitete Vorschaubilder): aufrechtes Bild drehen und zuschneiden - ohne Metadaten.</summary>
    public static SKBitmap TurnAndCrop(SKBitmap upright, int rotate, Crop? crop)
    {
        rotate = ((rotate % 360) + 360) % 360;
        var turned = Turn(upright, rotate);
        if (crop == null || crop.X <= 0.0001 && crop.Y <= 0.0001 && crop.W >= 0.9999 && crop.H >= 0.9999) return turned;
        var x = (int)Math.Round(Math.Clamp(crop.X, 0, 1) * turned.Width);
        var y = (int)Math.Round(Math.Clamp(crop.Y, 0, 1) * turned.Height);
        var w = Math.Max(1, Math.Min(turned.Width - x, (int)Math.Round(Math.Clamp(crop.W, 0, 1) * turned.Width)));
        var h = Math.Max(1, Math.Min(turned.Height - y, (int)Math.Round(Math.Clamp(crop.H, 0, 1) * turned.Height)));
        var cut = new SKBitmap(w, h, turned.ColorType, turned.AlphaType);
        using (var canvas = new SKCanvas(cut)) canvas.DrawBitmap(turned, new SKRect(x, y, x + w, y + h), new SKRect(0, 0, w, h));
        turned.Dispose();
        return cut;
    }

    private static SKBitmap Turn(SKBitmap src, int rotate) => rotate switch
    {
        90 => Orient(src, SKEncodedOrigin.RightTop),
        180 => Orient(src, SKEncodedOrigin.BottomRight),
        270 => Orient(src, SKEncodedOrigin.LeftBottom),
        _ => src.Copy(),
    };

    // ---------------- JPEG-Abschnitte ----------------

    /// <summary>Ein Abschnitt vor den Bilddaten: Kennung (0xE1 = APP1 …) und die ganzen Bytes samt FF-Kennung und Länge.</summary>
    public sealed record Segment(byte Marker, byte[] Bytes)
    {
        public bool IsExif => Marker == 0xE1 && Bytes.Length > 10 && Bytes[4] == 'E' && Bytes[5] == 'x' && Bytes[6] == 'i' && Bytes[7] == 'f';
        public bool IsXmp => Marker == 0xE1 && !IsExif;
    }

    /// <summary>Die Abschnitte vom Dateianfang bis vor die Bilddaten (SOS).</summary>
    public static List<Segment> Segments(byte[] b)
    {
        var list = new List<Segment>();
        var pos = 2;
        while (pos + 4 <= b.Length && b[pos] == 0xFF)
        {
            var m = b[pos + 1];
            if (m == 0xFF) { pos++; continue; }
            if (m == 0xDA) break;
            var len = b[pos + 2] << 8 | b[pos + 3];
            if (len < 2 || pos + 2 + len > b.Length) break;
            list.Add(new Segment(m, b[pos..(pos + 2 + len)]));
            pos += 2 + len;
        }
        return list;
    }

    /// <summary>
    /// Neu zusammensetzen: SOI, die Metadaten (aus <paramref name="meta"/>, sonst aus der Datei selbst; Drehangabe auf
    /// <paramref name="orientation"/>, Maße auf <paramref name="size"/>), dann Tabellen und Bilddaten von <paramref name="image"/>.
    /// </summary>
    private static byte[] Rebuild(byte[] image, List<Segment> imageSegments, List<Segment>? meta, int orientation, (int W, int H)? size)
    {
        var source = meta ?? imageSegments;
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        foreach (var s in source.Where(s => s.Marker is >= 0xE1 and <= 0xEF))
        {
            var bytes = s.IsExif ? PatchExif(s.Bytes, orientation, size, dropThumb: size != null) : s.IsXmp ? PatchXmp(s.Bytes, orientation) : s.Bytes;
            ms.Write(bytes);
        }
        // Vom Bild: alles außer den APP-Abschnitten (die kommen oben aus dem Original), dann die Bilddaten ab SOS
        var pos = 2;
        foreach (var s in imageSegments)
        {
            if (!(s.Marker is >= 0xE0 and <= 0xEF)) ms.Write(s.Bytes);
            pos += s.Bytes.Length;
        }
        while (pos < image.Length && !(image[pos] == 0xFF && image[pos + 1] == 0xDA)) pos++;
        ms.Write(image, pos, image.Length - pos);
        return ms.ToArray();
    }

    // ---------------- EXIF (TIFF im APP1) ----------------

    private const int TiffStart = 10;   // FF E1, Länge (2), „Exif\0\0“ (6)

    public static int Orientation(List<Segment> segments)
    {
        var exif = segments.FirstOrDefault(s => s.IsExif);
        if (exif == null) return 1;
        int found = 1;
        Walk(exif.Bytes, (tag, valueAt, type, le, b) => { if (tag == 0x0112) found = Read16(b, valueAt, le); });
        return found is >= 1 and <= 8 ? found : 1;
    }

    /// <summary>Drehangabe und (neu kodiert) Bildmaße an Ort und Stelle setzen, das Vorschaubild (IFD1) abhängen.</summary>
    private static byte[] PatchExif(byte[] seg, int orientation, (int W, int H)? size, bool dropThumb)
    {
        var b = (byte[])seg.Clone();
        Walk(b, (tag, valueAt, type, le, x) =>
        {
            if (tag == 0x0112) Write16(x, valueAt, (ushort)orientation, le);
            else if (size is { } sz && tag is 0xA002 or 0xA003 or 0x0100 or 0x0101)
            {
                var v = tag is 0xA002 or 0x0100 ? sz.W : sz.H;
                if (type == 3) Write16(x, valueAt, (ushort)Math.Min(v, ushort.MaxValue), le); else if (type == 4) Write32(x, valueAt, (uint)v, le);
            }
        });
        if (dropThumb && b.Length > TiffStart + 8)
        {
            var le = b[TiffStart] == 'I';
            var ifd0 = TiffStart + (int)Read32(b, TiffStart + 4, le);
            if (ifd0 + 2 <= b.Length)
            {
                var next = ifd0 + 2 + Read16(b, ifd0, le) * 12;
                if (next + 4 <= b.Length) Write32(b, next, 0, le);
            }
        }
        return b;
    }

    /// <summary>Alle Einträge von IFD0 und dem EXIF-IFD; valueAt: wo der Wert steht (bei kurzen Werten im Eintrag selbst).</summary>
    private static void Walk(byte[] b, Action<int, int, int, bool, byte[]> visit)
    {
        if (b.Length < TiffStart + 8) return;
        var le = b[TiffStart] == 'I';
        if (!le && b[TiffStart] != 'M') return;
        void Ifd(int at, bool follow)
        {
            if (at < TiffStart || at + 2 > b.Length) return;
            var n = Read16(b, at, le);
            for (var i = 0; i < n; i++)
            {
                var e = at + 2 + i * 12;
                if (e + 12 > b.Length) return;
                var tag = Read16(b, e, le);
                var type = Read16(b, e + 2, le);
                visit(tag, e + 8, type, le, b);
                if (tag == 0x8769 && follow) Ifd(TiffStart + (int)Read32(b, e + 8, le), false);
            }
        }
        Ifd(TiffStart + (int)Read32(b, TiffStart + 4, le), true);
    }

    private static int Read16(byte[] b, int i, bool le) => le ? b[i] | b[i + 1] << 8 : b[i] << 8 | b[i + 1];
    private static uint Read32(byte[] b, int i, bool le) => le ? (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24) : (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
    private static void Write16(byte[] b, int i, ushort v, bool le) { if (le) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); } else { b[i] = (byte)(v >> 8); b[i + 1] = (byte)v; } }
    private static void Write32(byte[] b, int i, uint v, bool le)
    {
        if (le) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); b[i + 2] = (byte)(v >> 16); b[i + 3] = (byte)(v >> 24); }
        else { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }
    }

    /// <summary>XMP: die Drehangabe (tiff:Orientation) - eine Ziffer, die Länge bleibt gleich.</summary>
    private static byte[] PatchXmp(byte[] seg, int orientation)
    {
        // Latin-1: jedes Byte bleibt ein Zeichen - Länge und Kopf des Abschnitts bleiben unverändert
        var text = System.Text.Encoding.Latin1.GetString(seg);
        var o = (char)('0' + orientation);
        var patched = System.Text.RegularExpressions.Regex.Replace(text, @"(tiff:Orientation\s*=\s*[""'])[1-8]([""'])", m => m.Groups[1].Value + o + m.Groups[2].Value);
        patched = System.Text.RegularExpressions.Regex.Replace(patched, @"(<tiff:Orientation>)[1-8](</tiff:Orientation>)", m => m.Groups[1].Value + o + m.Groups[2].Value);
        var bytes = System.Text.Encoding.Latin1.GetBytes(patched);
        return bytes.Length == seg.Length ? bytes : seg;
    }
}
