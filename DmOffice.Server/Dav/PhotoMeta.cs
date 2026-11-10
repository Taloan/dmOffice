using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace DmOffice.Dav;

/// <summary>
/// Metadaten in Fotos schreiben, ohne exiftool - an einer Stelle für alle (Geotagging aus Aktivitäten, später Bewertung, Titel und
/// Beschreibung aus der Galerie). Angelehnt an den CameraImporter (xmp_jpeg, xmp_sidecar):
/// <list type="bullet">
/// <item>JPEG: nur die APP1-Abschnitte (EXIF, XMP) werden ersetzt oder ergänzt; alles andere - Tabellen, Bilddaten ab SOS, IPTC,
/// Farbprofil - bleibt Byte für Byte. GPS kommt ins EXIF (GPS-IFD), Bewertung, Titel und Beschreibung ins XMP.</item>
/// <item>EXIF: nichts wird verschoben. Das GPS-IFD (und, wenn der Zeiger darauf fehlt, eine Kopie von IFD0 mit Zeiger) kommt ans Ende
/// der TIFF-Daten; alle vorhandenen Versätze (Herstellerdaten, Vorschaubild) bleiben gültig, das alte IFD0 bleibt als unbenutzte Bytes.</item>
/// <item>XMP: ein vorhandenes Paket wird gelesen und nur unsere Felder werden ersetzt - fremde Angaben (Lightroom-Entwicklung,
/// darktable-Verlauf, Gesichtsregionen …) bleiben samt ihren Präfixen. Dieselbe Feldlogik für Sidecar und eingebettetes XMP.</item>
/// <item>RAW: nie die Datei selbst, sondern die Sidecar-Datei daneben (vorhandene „foto.arw.xmp“ bzw. „foto.xmp“, sonst neu „foto.xmp“).</item>
/// <item>Nach dem Schreiben wird das Ergebnis gelesen und geprüft (GPS wie gewollt, Aufnahmezeit, Kamera und Drehangabe wie vorher,
/// Bilddaten gleich) - sonst Fehler statt einer veränderten Datei.</item>
/// </list>
/// </summary>
public static class PhotoMeta
{
    /// <summary>
    /// Was geschrieben werden soll; null = bleibt, wie es ist. Bewertung 0 und leerer Text entfernen die Angabe. Stichwörter kommen
    /// hinzu bzw. gehen weg (die übrigen bleiben) - so lassen sich mehrere Fotos auf einmal ändern, ohne ihre eigenen zu verlieren.
    /// </summary>
    /// <param name="Pick">Markierung wie Lightroom (xmpDM:pick): 1 ausgewählt, -1 abgelehnt, 0 ohne.</param>
    /// <param name="Label">Farbmarkierung als Schlüssel (red, yellow, green, blue, purple); leer = keine.</param>
    public sealed record Fields(double? Lat = null, double? Lon = null, int? Rating = null, string? Title = null, string? Description = null,
        IReadOnlyList<string>? AddKeywords = null, IReadOnlyList<string>? RemoveKeywords = null, int? Pick = null, string? Label = null)
    {
        public bool HasGps => Lat != null && Lon != null;
        public bool HasKeywords => AddKeywords is { Count: > 0 } || RemoveKeywords is { Count: > 0 };
        public bool HasXmpOnly => Rating != null || Title != null || Description != null || HasKeywords || Pick != null || Label != null;

        /// <summary>Neue Stichwortliste aus der bisherigen (Groß-/Kleinschreibung egal, Reihenfolge bleibt).</summary>
        public List<string> KeywordsFrom(IEnumerable<string> old)
        {
            var remove = (RemoveKeywords ?? []).Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = old.Select(k => k.Trim()).Where(k => k.Length > 0 && !remove.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var k in (AddKeywords ?? []).Select(k => k.Trim()).Where(k => k.Length > 0))
                if (!list.Contains(k, StringComparer.OrdinalIgnoreCase)) list.Add(k);
            return list;
        }
    }

    public static bool IsJpeg(string name) => Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg";

    /// <summary>RAW-Fotos bekommen eine Sidecar-Datei, die Datei selbst bleibt unangetastet.</summary>
    public static bool UsesSidecar(string name) => Thumbs.IsRaw(name);

    /// <summary>Schreibbar: JPEG (in die Datei) und RAW (Sidecar). HEIC, PNG, Videos nicht.</summary>
    public static bool CanWrite(string name) => IsJpeg(name) || UsesSidecar(name);

    // ---------------- Sidecar ----------------

    /// <summary>
    /// Name der Sidecar-Datei zu einem Foto: eine vorhandene („foto.arw.xmp“ wie darktable, sonst „foto.xmp“ wie Lightroom, gleich
    /// welche Schreibweise), sonst neu „foto.xmp“. <paramref name="names"/> sind die Dateinamen im Ordner des Fotos.
    /// </summary>
    public static string SidecarName(string photo, IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.FirstOrDefault(n => n.Equals(photo + ".xmp", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(n => n.Equals(Path.GetFileNameWithoutExtension(photo) + ".xmp", StringComparison.OrdinalIgnoreCase))
            ?? Path.GetFileNameWithoutExtension(photo) + ".xmp";
    }

    // ---------------- Lesen ----------------

    /// <summary>GPS aus dem Foto (EXIF, auch RAW) - liest nur den Kopf; null, wenn keins da ist.</summary>
    public static (double Lat, double Lon)? GpsOf(Stream head)
    {
        try
        {
            var geo = ImageMetadataReader.ReadMetadata(head).OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation();
            return geo is { IsZero: false } g ? (g.Latitude, g.Longitude) : null;
        }
        catch (Exception e) when (e is ImageProcessingException or IOException or MetadataException or ArgumentException or NotSupportedException or IndexOutOfRangeException) { return null; }
    }

    // ---------------- JPEG ----------------

    private static readonly byte[] ExifId = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpId = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private const int MaxSegment = 65535 - 2;

    private sealed record Seg(byte Marker, int Start, int End)
    {
        public int Payload => Start + 4;
    }

    private static bool Is(byte[] b, Seg s, byte[] id) => s.Marker == 0xE1 && s.End - s.Payload >= id.Length && b.AsSpan(s.Payload, id.Length).SequenceEqual(id);

    /// <summary>Die Abschnitte vor den Bilddaten mit ihrer Lage; Ende = wo SOS beginnt. Kaputter Aufbau: Fehler.</summary>
    private static (List<Seg> Segs, int Sos) Scan(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) throw new InvalidDataException("Keine JPEG-Datei.");
        var list = new List<Seg>();
        var pos = 2;
        while (true)
        {
            if (pos + 4 > b.Length || b[pos] != 0xFF) throw new InvalidDataException("Der Aufbau der JPEG-Datei ist ungewöhnlich - sie wird nicht verändert.");
            var m = b[pos + 1];
            if (m == 0xFF) { pos++; continue; }
            if (m == 0xDA) return (list, pos);
            if (m is 0x01 or >= 0xD0 and <= 0xD7) { pos += 2; continue; }
            var len = b[pos + 2] << 8 | b[pos + 3];
            if (len < 2 || pos + 2 + len > b.Length) throw new InvalidDataException("Der Aufbau der JPEG-Datei ist ungewöhnlich - sie wird nicht verändert.");
            list.Add(new Seg(m, pos, pos + 2 + len));
            pos += 2 + len;
        }
    }

    /// <summary>
    /// Felder in eine JPEG schreiben: GPS ins EXIF, Bewertung, Titel und Beschreibung ins XMP (steht dort schon GPS, wird es mit
    /// angepasst). Nur die betroffenen APP1-Abschnitte ändern sich; geprüft wird vor der Rückgabe.
    /// </summary>
    public static byte[] WriteJpeg(byte[] jpeg, Fields f)
    {
        var (segs, sos) = Scan(jpeg);
        var exifIdx = segs.FindIndex(s => Is(jpeg, s, ExifId));
        var xmpIdx = segs.FindIndex(s => Is(jpeg, s, XmpId));
        byte[]? exif = null, xmp = null;
        if (f.HasGps)
        {
            var tiff = exifIdx >= 0 ? jpeg[(segs[exifIdx].Payload + ExifId.Length)..segs[exifIdx].End] : EmptyTiff();
            byte[] withGps = AddGps(tiff, f.Lat!.Value, f.Lon!.Value);
            // Voll bis zur Höchstgröße (Fujifilm füllt den EXIF-Abschnitt mit Nullen auf): das Neue in die Füllung statt dahinter
            if (ExifId.Length + withGps.Length > MaxSegment && WithoutPadding(tiff) is { } trimmed)
                withGps = AddGps(trimmed, f.Lat!.Value, f.Lon!.Value);
            exif = Segment([.. ExifId, .. withGps]);
        }
        var oldXmp = xmpIdx >= 0 ? Encoding.UTF8.GetString(jpeg, segs[xmpIdx].Payload + XmpId.Length, segs[xmpIdx].End - segs[xmpIdx].Payload - XmpId.Length) : null;
        var xmpFields = oldXmp != null && oldXmp.Contains("GPSLatitude", StringComparison.Ordinal) ? f : f with { Lat = null, Lon = null };
        // Stichwörter nur im IPTC (ältere Programme): als Ausgangsliste nehmen - steht dc:subject einmal im XMP, gilt nur noch das
        List<string>? seed = null;
        if (f.HasKeywords && (oldXmp == null || !oldXmp.Contains("subject", StringComparison.Ordinal)))
            try { seed = ImageMetadataReader.ReadMetadata(new MemoryStream(jpeg)).OfType<MetadataExtractor.Formats.Iptc.IptcDirectory>().FirstOrDefault()?.GetStringArray(MetadataExtractor.Formats.Iptc.IptcDirectory.TagKeywords)?.ToList(); }
            catch (Exception e) when (e is ImageProcessingException or IOException or MetadataException) { }
        if (xmpFields.HasXmpOnly || xmpFields.HasGps)
            xmp = Segment([.. XmpId, .. Encoding.UTF8.GetBytes(WriteXmp(oldXmp, xmpFields, packet: true, seedKeywords: seed))]);

        // Neue Abschnitte: EXIF gleich nach SOI (hinter JFIF-APP0, das dort stehen muss), XMP hinter EXIF
        var exifAt = exifIdx >= 0 ? -1 : segs.Count > 0 && segs[0].Marker == 0xE0 ? 1 : 0;
        var xmpAt = xmpIdx >= 0 ? -1 : exifIdx >= 0 ? exifIdx + 1 : exifAt >= 0 ? exifAt : 0;
        using var ms = new MemoryStream(jpeg.Length + 4096);
        ms.Write(jpeg, 0, 2);
        for (var i = 0; i <= segs.Count; i++)
        {
            if (i == exifAt && exif != null) ms.Write(exif);
            if (i == xmpAt && xmp != null) ms.Write(xmp);
            if (i == segs.Count) break;
            var s = segs[i];
            if (i == exifIdx && exif != null) ms.Write(exif);
            else if (i == xmpIdx && xmp != null) ms.Write(xmp);
            else ms.Write(jpeg, s.Start, s.End - s.Start);
        }
        // Zwischen dem letzten Abschnitt und SOS (Füllbytes) und ab SOS: unverändert
        var tail = segs.Count > 0 ? segs[^1].End : 2;
        ms.Write(jpeg, tail, jpeg.Length - tail);
        var result = ms.ToArray();
        Verify(jpeg, result, f, jpeg.Length - sos);
        return result;
    }

    private static byte[] Segment(byte[] payload)
    {
        if (payload.Length > MaxSegment) throw new InvalidDataException("Die Metadaten sind zu groß für einen JPEG-Abschnitt.");
        return [0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];
    }

    /// <summary>Ergebnis prüfen: Bilddaten gleich, GPS wie gewollt, Aufnahmezeit, Kamera und Drehangabe wie vorher.</summary>
    private static void Verify(byte[] before, byte[] after, Fields f, int imageBytes)
    {
        if (!before.AsSpan(before.Length - imageBytes).SequenceEqual(after.AsSpan(after.Length - imageBytes)))
            throw new InvalidDataException("Prüfung nach dem Schreiben: Bilddaten verändert.");
        var a = Read(before);
        var b = Read(after);
        if (a.Taken != b.Taken || a.Make != b.Make || a.Model != b.Model || a.Orientation != b.Orientation)
            throw new InvalidDataException("Prüfung nach dem Schreiben: Aufnahmedaten verändert.");
        if (f.HasGps && (b.Gps is not { } g || Math.Abs(g.Lat - f.Lat!.Value) > 1e-6 || Math.Abs(g.Lon - f.Lon!.Value) > 1e-6))
            throw new InvalidDataException("Prüfung nach dem Schreiben: GPS nicht lesbar.");

        static (string? Taken, string? Make, string? Model, string? Orientation, (double Lat, double Lon)? Gps) Read(byte[] x)
        {
            var dirs = ImageMetadataReader.ReadMetadata(new MemoryStream(x));
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var geo = dirs.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation();
            return (sub?.GetString(ExifDirectoryBase.TagDateTimeOriginal), ifd0?.GetString(ExifDirectoryBase.TagMake), ifd0?.GetString(ExifDirectoryBase.TagModel),
                ifd0?.GetString(ExifDirectoryBase.TagOrientation), geo is { IsZero: false } g ? (g.Latitude, g.Longitude) : null);
        }
    }

    // ---------------- EXIF: GPS-IFD anhängen ----------------

    /// <summary>TIFF ohne Einträge (für JPEGs ohne EXIF): „II“, 42, IFD0 bei 8 mit 0 Einträgen.</summary>
    private static byte[] EmptyTiff() => [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// GPS-IFD mit Version, Breite und Länge (Grad, Minuten, Sekunden) ans Ende der TIFF-Daten. Schon vorhandene GPS-Angaben ohne
    /// Ort (Zeit, Höhe …) werden übernommen. Zeigt IFD0 schon auf ein GPS-IFD, wird nur dieser Zeiger umgestellt; sonst kommt eine
    /// Kopie von IFD0 mit zusätzlichem Zeiger ans Ende (dahinter eine Kopie von IFD1) und der TIFF-Kopf zeigt darauf.
    /// </summary>
    internal static byte[] AddGps(byte[] tiff, double lat, double lon)
    {
        if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180) throw new ArgumentException("Ungültige Koordinaten.");
        if (tiff.Length < 8) throw new InvalidDataException("EXIF-Daten unvollständig.");
        var le = tiff[0] == 'I' && tiff[1] == 'I';
        if (!le && !(tiff[0] == 'M' && tiff[1] == 'M')) throw new InvalidDataException("EXIF-Daten unbekannter Art.");
        if (R16(tiff, 2, le) != 42) throw new InvalidDataException("EXIF-Daten unbekannter Art.");
        var ifd0 = (int)R32(tiff, 4, le);
        var entries = Entries(tiff, ifd0, le) ?? throw new InvalidDataException("EXIF-Daten beschädigt.");
        var next = R32(tiff, ifd0 + 2 + entries.Count * 12, le);
        var gpsIdx = entries.FindIndex(e => R16(e, 0, le) == 0x8825);

        // Vorhandenes GPS-IFD: alles außer Version und Ort übernehmen (Versätze darin bleiben gültig - es wird nichts verschoben)
        var kept = new List<byte[]>();
        if (gpsIdx >= 0 && Entries(tiff, (int)R32(entries[gpsIdx], 8, le), le) is { } old)
            kept.AddRange(old.Where(e => R16(e, 0, le) > 4));

        var body = new List<byte>(tiff);
        if (body.Count % 2 == 1) body.Add(0);
        var gpsAt = body.Count;
        var ours = new List<byte[]>
        {
            Entry(0x0000, 1, 4, [2, 3, 0, 0], le),
            Entry(0x0001, 2, 2, [(byte)(lat >= 0 ? 'N' : 'S'), 0, 0, 0], le),
            Entry(0x0002, 5, 3, null, le),
            Entry(0x0003, 2, 2, [(byte)(lon >= 0 ? 'E' : 'W'), 0, 0, 0], le),
            Entry(0x0004, 5, 3, null, le),
        };
        var all = ours.Concat(kept).OrderBy(e => R16(e, 0, le)).ToList();
        var data = gpsAt + 2 + all.Count * 12 + 4;
        W32(ours[2], 8, (uint)data, le);
        W32(ours[4], 8, (uint)(data + 24), le);
        Add16(body, all.Count, le);
        foreach (var e in all) body.AddRange(e);
        Add32(body, 0, le);
        foreach (var v in Dms(Math.Abs(lat)).Concat(Dms(Math.Abs(lon)))) { Add32(body, v.Num, le); Add32(body, v.Den, le); }

        var result = body.ToArray();
        if (gpsIdx >= 0)
            W32(result, ifd0 + 2 + gpsIdx * 12 + 8, (uint)gpsAt, le);
        else
        {
            if (result.Length % 2 == 1) result = [.. result, 0];
            var newIfd0 = result.Length;
            var copy = entries.Append(Entry(0x8825, 4, 1, null, le)).OrderBy(e => R16(e, 0, le)).ToList();
            W32(copy.First(e => R16(e, 0, le) == 0x8825), 8, (uint)gpsAt, le);
            var tail = new List<byte>();
            Add16(tail, copy.Count, le);
            foreach (var e in copy) tail.AddRange(e);
            Add32(tail, next, le);
            result = [.. result, .. tail];
            W32(result, 4, (uint)newIfd0, le);
            // IFD1 (Vorschaubild) auch dahinter: manche Leser folgen keinem Zeiger, der zurück an den Anfang zeigt, und verlören es
            if (next != 0 && Entries(result, (int)next, le) is { } ifd1)
            {
                var ifd1At = result.Length;
                var t1 = new List<byte>();
                Add16(t1, ifd1.Count, le);
                foreach (var e in ifd1) t1.AddRange(e);
                Add32(t1, R32(result, (int)next + 2 + ifd1.Count * 12, le), le);
                result = [.. result, .. t1];
                W32(result, newIfd0 + 2 + copy.Count * 12, (uint)ifd1At, le);
            }
        }
        return result;
    }

    /// <summary>
    /// TIFF-Daten ohne die Füllung am Ende: bis zum letzten Byte, auf das ein IFD, ein Wert, das Vorschaubild oder die MakerNote
    /// zeigt. Nur wenn danach reine Füllung steht: Nullen oder ein Muster, das sich bis zum Ende wiederholt (Fujifilm: alle 256 Byte) -
    /// sonst null (dann bleibt alles, wie es ist).
    /// </summary>
    internal static byte[]? WithoutPadding(byte[] t)
    {
        if (t.Length < 8) return null;
        var le = t[0] == 'I' && t[1] == 'I';
        long used = 8;
        var seen = new HashSet<int>();
        var todo = new Stack<int>();
        todo.Push((int)R32(t, 4, le));
        while (todo.Count > 0)
        {
            var at = todo.Pop();
            if (at == 0 || !seen.Add(at)) continue;
            var entries = Entries(t, at, le);
            if (entries == null) return null;
            used = Math.Max(used, at + 2 + entries.Count * 12 + 4);
            long? thumbAt = null, thumbLen = null;
            foreach (var e in entries)
            {
                int tag = R16(e, 0, le), type = R16(e, 2, le);
                var count = (long)R32(e, 4, le);
                var value = R32(e, 8, le);
                var size = count * (type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 1 });
                if (size > 4) used = Math.Max(used, value + size);
                if (tag is 0x8769 or 0x8825 or 0xA005 && type is 4 or 13) todo.Push((int)value);
                if (tag == 0x0201) thumbAt = value;
                if (tag == 0x0202) thumbLen = value;
            }
            if (thumbAt is { } ta && thumbLen is { } tl) used = Math.Max(used, ta + tl);
            todo.Push((int)R32(t, at + 2 + entries.Count * 12, le));
        }
        if (used > t.Length) return null;
        return used < t.Length && IsFiller(t.AsSpan((int)used)) ? t[..(int)used] : null;
    }

    /// <summary>Füllung: nur Nullen, oder ein Muster von höchstens 256 Byte, das sich mindestens viermal und bis zum Ende wiederholt.</summary>
    private static bool IsFiller(ReadOnlySpan<byte> r)
    {
        if (r.IndexOfAnyExcept((byte)0) < 0) return true;
        for (var period = 1; period <= 256 && period * 4 <= r.Length; period++)
            if (r[period..].SequenceEqual(r[..^period])) return true;
        return false;
    }

    /// <summary>Die Einträge (je 12 Byte) eines IFD; null, wenn es außerhalb der Daten läge.</summary>
    private static List<byte[]>? Entries(byte[] t, int at, bool le)
    {
        if (at < 8 || at + 2 > t.Length) return null;
        var n = R16(t, at, le);
        if (at + 2 + n * 12 + 4 > t.Length) return null;
        return Enumerable.Range(0, n).Select(i => t[(at + 2 + i * 12)..(at + 14 + i * 12)]).ToList();
    }

    private static byte[] Entry(int tag, int type, int count, byte[]? inline, bool le)
    {
        var e = new byte[12];
        W16(e, 0, (ushort)tag, le); W16(e, 2, (ushort)type, le); W32(e, 4, (uint)count, le);
        inline?.CopyTo(e, 8);
        return e;
    }

    /// <summary>Grad, Minuten, Sekunden als Brüche (Sekunden auf 1/10000).</summary>
    private static IEnumerable<(uint Num, uint Den)> Dms(double v)
    {
        var deg = Math.Floor(v);
        var min = Math.Floor((v - deg) * 60);
        var sec = Math.Min(Math.Round(((v - deg) * 60 - min) * 60 * 10000), 599_999);
        return [((uint)deg, 1), ((uint)min, 1), ((uint)sec, 10000)];
    }

    private static int R16(byte[] b, int i, bool le) => le ? b[i] | b[i + 1] << 8 : b[i] << 8 | b[i + 1];
    private static uint R32(byte[] b, int i, bool le) => le ? (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24) : (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
    private static void W16(byte[] b, int i, ushort v, bool le) { if (le) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); } else { b[i] = (byte)(v >> 8); b[i + 1] = (byte)v; } }
    private static void W32(byte[] b, int i, uint v, bool le)
    {
        if (le) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); b[i + 2] = (byte)(v >> 16); b[i + 3] = (byte)(v >> 24); }
        else { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }
    }
    private static void Add16(List<byte> l, int v, bool le) { var b = new byte[2]; W16(b, 0, (ushort)v, le); l.AddRange(b); }
    private static void Add32(List<byte> l, uint v, bool le) { var b = new byte[4]; W32(b, 0, v, le); l.AddRange(b); }

    // ---------------- XMP ----------------

    private static readonly XNamespace X = "adobe:ns:meta/", Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#", Xmp = "http://ns.adobe.com/xap/1.0/",
        Dc = "http://purl.org/dc/elements/1.1/", Exif = "http://ns.adobe.com/exif/1.0/", Lr = "http://ns.adobe.com/lightroom/1.0/",
        XmpDM = "http://ns.adobe.com/xmp/1.0/DynamicMedia/", Photoshop = "http://ns.adobe.com/photoshop/1.0/";

    // Farbmarkierung wie Lightroom und der CameraImporter: xmp:Label als Wort (wir schreiben Deutsch), photoshop:LabelColor technisch
    private static readonly Dictionary<string, string> LabelWord = new() { ["red"] = "Rot", ["yellow"] = "Gelb", ["green"] = "Grün", ["blue"] = "Blau", ["purple"] = "Violett" };
    public static bool IsLabel(string? key) => key != null && (key.Length == 0 || LabelWord.ContainsKey(key));

    /// <summary>
    /// Felder in ein XMP-Paket schreiben (Sidecar-Datei oder eingebettet): ein vorhandenes wird gelesen, nur unsere Felder ersetzt
    /// (an allen Stellen, als Attribut oder Element, in jedem rdf:Description), alles andere bleibt samt Formatierung. Ohne
    /// vorhandenes entsteht ein kleines neues. Ein unlesbares vorhandenes Paket: Fehler (es wird nicht überschrieben).
    /// </summary>
    public static string WriteXmp(string? existing, Fields f, bool packet = true, IReadOnlyList<string>? seedKeywords = null)
    {
        XDocument doc;
        var text = existing?.TrimStart('﻿', ' ', '\r', '\n', '\t').TrimEnd('\0', ' ', '\r', '\n', '\t');
        if (string.IsNullOrEmpty(text)) doc = NewXmp();
        else
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };
                using var reader = XmlReader.Create(new StringReader(text), settings);
                doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException e) { throw new InvalidDataException("Die vorhandenen XMP-Daten sind nicht lesbar - sie werden nicht überschrieben (" + e.Message + ")."); }
        }
        var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault() ?? throw new InvalidDataException("Die vorhandenen XMP-Daten haben kein rdf:RDF - sie werden nicht überschrieben.");
        var descs = rdf.Elements(Rdf + "Description").ToList();
        if (descs.Count == 0) { var d = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")); rdf.Add(d); descs.Add(d); }
        var desc = descs[0];

        void Clear(XName n, XElement? keep = null)
        {
            foreach (var d in descs)
            {
                d.Attribute(n)?.Remove();
                foreach (var e in d.Elements(n).Where(e => e != keep).ToList()) e.Remove();
            }
        }
        void Simple(XName n, string? value, string prefix)
        {
            Clear(n);
            if (string.IsNullOrEmpty(value)) return;
            Declare(desc, prefix, n.Namespace);
            desc.SetAttributeValue(n, value);
        }

        if (f.Rating is int r) Simple(Xmp + "Rating", r == 0 ? null : r.ToString(CultureInfo.InvariantCulture), "xmp");
        if (f.HasGps)
        {
            Simple(Exif + "GPSLatitude", AdobeGps(f.Lat!.Value, true), "exif");
            Simple(Exif + "GPSLongitude", AdobeGps(f.Lon!.Value, false), "exif");
        }
        if (f.Pick is int pk) Simple(XmpDM + "pick", pk == 0 ? null : pk > 0 ? "1" : "-1", "xmpDM");
        if (f.Label != null)
        {
            var key = f.Label.Length == 0 ? null : f.Label;
            Simple(Xmp + "Label", key != null && LabelWord.TryGetValue(key, out var word) ? word : null, "xmp");
            Simple(Photoshop + "LabelColor", key, "photoshop");
        }
        if (f.Title != null) Alt(Dc + "title", f.Title);
        if (f.Description != null) Alt(Dc + "description", f.Description);
        if (f.HasKeywords) Keywords();

        // Stichwörter (dc:subject, rdf:Bag). Lightrooms hierarchische Stichwörter (lr:hierarchicalSubject) bleiben - nur wer hier
        // entfernt wird, geht dort mit weg (sonst käme er in Lightroom zurück)
        void Keywords()
        {
            var old = descs.SelectMany(d => d.Elements(Dc + "subject")).SelectMany(e => e.Descendants(Rdf + "li")).Select(x => x.Value).ToList();
            if (old.Count == 0 && seedKeywords != null) old = [.. seedKeywords];
            var list = f.KeywordsFrom(old);
            Clear(Dc + "subject");
            if (list.Count > 0)
            {
                Declare(desc, "dc", Dc);
                desc.Add(new XElement(Dc + "subject", new XElement(Rdf + "Bag", list.Select(k => new XElement(Rdf + "li", k)))));
            }
            var gone = (f.RemoveKeywords ?? []).Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (gone.Count == 0) return;
            foreach (var li in descs.SelectMany(d => d.Elements(Lr + "hierarchicalSubject")).SelectMany(e => e.Descendants(Rdf + "li")).ToList())
                if (gone.Contains(li.Value.Split('|')[^1].Trim())) li.Remove();
        }

        // Sprachvarianten (rdf:Alt): x-default ersetzen, andere Sprachen bleiben; leer = Angabe weg
        void Alt(XName n, string value)
        {
            var el = descs.SelectMany(d => d.Elements(n)).FirstOrDefault();
            if (value.Length == 0) { Clear(n); return; }
            Clear(n, el);
            if (el == null)
            {
                Declare(desc, "dc", Dc);
                el = new XElement(n, new XElement(Rdf + "Alt"));
                desc.Add(el);
            }
            var alt = el.Element(Rdf + "Alt") ?? new XElement(Rdf + "Alt");
            if (alt.Parent == null) { el.RemoveNodes(); el.Add(alt); }
            var li = alt.Elements(Rdf + "li").FirstOrDefault(x => (string?)x.Attribute(XNamespace.Xml + "lang") == "x-default")
                ?? alt.Elements(Rdf + "li").FirstOrDefault();
            if (li == null) { li = new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default")); alt.Add(li); }
            li.Value = value;
        }

        if (packet && !doc.Nodes().OfType<XProcessingInstruction>().Any(p => p.Target == "xpacket"))
        {
            doc.AddFirst(new XProcessingInstruction("xpacket", "begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\""));
            doc.Add(new XProcessingInstruction("xpacket", "end=\"w\""));
        }
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = string.IsNullOrEmpty(text), NewLineHandling = NewLineHandling.None }))
            doc.Save(w);
        return sb.ToString();
    }

    private static XDocument NewXmp() => new(new XElement(X + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", X), new XAttribute(X + "xmptk", "dmOffice"),
        new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf),
            new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")))));

    /// <summary>Präfix bekannt machen, wenn der Namensraum noch keinen hat (sonst schriebe XDocument „p1:“).</summary>
    private static void Declare(XElement desc, string prefix, XNamespace ns)
    {
        if (desc.GetPrefixOfNamespace(ns) != null) return;
        var p = prefix;
        for (var i = 2; desc.GetNamespaceOfPrefix(p) is { } taken && taken != ns; i++) p = prefix + i;
        desc.SetAttributeValue(XNamespace.Xmlns + p, ns.NamespaceName);
    }

    /// <summary>Adobe-Schreibweise wie Lightroom: „48,14.531220N“ (Grad, Dezimalminuten, Richtung).</summary>
    public static string AdobeGps(double v, bool lat)
    {
        var a = Math.Abs(v);
        var deg = Math.Floor(a);
        var min = (a - deg) * 60;
        return deg.ToString("0", CultureInfo.InvariantCulture) + "," + min.ToString("0.000000", CultureInfo.InvariantCulture) + (lat ? v >= 0 ? "N" : "S" : v >= 0 ? "E" : "W");
    }

    /// <summary>„48,14.531220N“ oder „48,14,31.87N“ → Dezimalgrad; null, wenn unlesbar.</summary>
    public static double? ParseAdobeGps(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var sign = 1.0;
        if (s[^1] is 'N' or 'S' or 'E' or 'W' or 'n' or 's' or 'e' or 'w') { if (char.ToUpperInvariant(s[^1]) is 'S' or 'W') sign = -1; s = s[..^1]; }
        var parts = s.Split(',');
        var v = 0.0;
        for (var i = 0; i < parts.Length && i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return null;
            v += p / Math.Pow(60, i);
        }
        return v <= 180 ? sign * v : null;
    }
}
