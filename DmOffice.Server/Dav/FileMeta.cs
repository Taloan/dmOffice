using System.Globalization;
using DmOffice.Core;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.WebP;
using MetadataExtractor.Formats.Xmp;

namespace DmOffice.Dav;

/// <summary>
/// Aufnahmedaten von Fotos und Videos in der Dateiablage (Version 2): Aufnahmezeit, Kamera, Objektiv, Brennweite, Blende,
/// Belichtungszeit, ISO, Ort, Abmessungen, Dauer, Bewertung, Stichwörter, Titel - für Spalten der Detailansicht, Sortieren
/// nach Aufnahmedatum und die Filterleiste der Galerie. Liegt je Benutzer in der Tabelle file_meta (beim ersten Zugriff
/// angelegt, ohne neue Schema-Nummer - eine ältere Version lässt sie einfach liegen); <see cref="FileMetaJob"/> liest im
/// Hintergrund nach, Verschieben und Löschen ziehen Trigger nach.
/// </summary>
public static class FileMeta
{
    public sealed record Row(string Path, long? Taken, string? Camera, string? Lens, double? Focal, double? Aperture, double? Exposure, int? Iso,
        double? Lat, double? Lon, int? Width, int? Height, double? Duration, int? Rating, string? Title, string? Keywords, string? Label = null, string? Description = null,
        int? Pick = null);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UserDb, object> Ensured = new();

    private const string Media = @"(lower(f.mime) LIKE 'image/%' OR lower(f.mime) LIKE 'video/%'
        OR lower(f.path) GLOB '*.[cC][rR][23]' OR lower(f.path) LIKE '%.nef' OR lower(f.path) LIKE '%.arw' OR lower(f.path) LIKE '%.orf'
        OR lower(f.path) LIKE '%.rw2' OR lower(f.path) LIKE '%.raf' OR lower(f.path) LIKE '%.pef' OR lower(f.path) LIKE '%.dng'
        OR lower(f.path) LIKE '%.srw' OR lower(f.path) LIKE '%.heic' OR lower(f.path) LIKE '%.heif')";

    /// <summary>SQL: Pfad endet auf eine RAW-Endung.</summary>
    internal static string RawPaths(string col) => string.Join(" OR ", new[] { "nef", "nrw", "arw", "srf", "sr2", "cr2", "cr3", "crw", "orf", "rw2", "raf", "pef", "dng", "srw", "rwl", "3fr", "iiq", "erf", "kdc", "mrw", "x3f" }
        .Select(e => $"lower({col}) LIKE '%.{e}'"));

    public static void Ensure(UserDb db)
    {
        if (Ensured.TryGetValue(db, out _)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS file_meta (
                path TEXT PRIMARY KEY, etag TEXT, ok INTEGER NOT NULL DEFAULT 1,
                taken INTEGER, camera TEXT, lens TEXT, focal REAL, aperture REAL, exposure REAL, iso INTEGER,
                lat REAL, lon REAL, width INTEGER, height INTEGER, duration REAL, rating INTEGER, title TEXT, keywords TEXT, label TEXT
            ) WITHOUT ROWID;
            CREATE TRIGGER IF NOT EXISTS file_meta_ad AFTER DELETE ON files BEGIN
                DELETE FROM file_meta WHERE path = old.path;
            END;
            CREATE TRIGGER IF NOT EXISTS file_meta_mv AFTER UPDATE OF path ON files WHEN new.path IS NOT old.path BEGIN
                UPDATE OR REPLACE file_meta SET path = new.path WHERE path = old.path;
            END;");
        // Farbmarkierung kam später dazu: Spalte anlegen und die Fotos einmal neu lesen (nicht, was nur im Storage Extender liegt)
        if (!db.Query("PRAGMA table_info(file_meta)", r => r.GetString(1)).Contains("label", StringComparer.OrdinalIgnoreCase))
        {
            db.Exec("ALTER TABLE file_meta ADD COLUMN label TEXT");
            db.Exec("UPDATE file_meta SET etag = NULL WHERE etag IS NOT NULL AND etag NOT LIKE 'away:%'");
        }
        // Geänderte Sidecar-Datei (etwa Sterne aus Lightroom über Nextcloud): genau ihr Foto neu lesen - „foto.arw.xmp“ gehört zu
        // „foto.arw“, „foto.xmp“ zu „foto.*“ im selben Ordner. Das Foto selbst hat sich nicht geändert, sein ETag allein merkte es nicht.
        const string stale = @"UPDATE file_meta SET etag = NULL WHERE etag IS NOT NULL AND (lower(path) = lower(substr(X.path, 1, length(X.path) - 4))
                OR (lower(path) LIKE lower(substr(X.path, 1, length(X.path) - 4)) || '.%' AND instr(substr(path, length(X.path) - 3), '/') = 0))";
        db.Exec($@"CREATE TRIGGER IF NOT EXISTS file_meta_xmp_i AFTER INSERT ON files WHEN lower(new.path) LIKE '%.xmp' BEGIN {stale.Replace("X.", "new.")}; END;
            CREATE TRIGGER IF NOT EXISTS file_meta_xmp_u AFTER UPDATE OF etag ON files WHEN lower(new.path) LIKE '%.xmp' BEGIN {stale.Replace("X.", "new.")}; END;
            CREATE TRIGGER IF NOT EXISTS file_meta_xmp_d AFTER DELETE ON files WHEN lower(old.path) LIKE '%.xmp' BEGIN {stale.Replace("X.", "old.")}; END;");
        // Markierung wie Lightroom (xmpDM:pick) kam später dazu: Spalte anlegen, Fotos einmal neu lesen
        if (!db.Query("PRAGMA table_info(file_meta)", r => r.GetString(1)).Contains("pick", StringComparer.OrdinalIgnoreCase))
        {
            db.Exec("ALTER TABLE file_meta ADD COLUMN pick INTEGER");
            db.Exec("UPDATE file_meta SET etag = NULL WHERE etag IS NOT NULL AND etag NOT LIKE 'away:%'");
        }
        // Beschreibung (Bildunterschrift) kam später dazu: Spalte anlegen, Fotos einmal neu lesen
        if (!db.Query("PRAGMA table_info(file_meta)", r => r.GetString(1)).Contains("description", StringComparer.OrdinalIgnoreCase))
        {
            db.Exec("ALTER TABLE file_meta ADD COLUMN description TEXT");
            db.Exec("UPDATE file_meta SET etag = NULL WHERE etag IS NOT NULL AND etag NOT LIKE 'away:%'");
        }
        // RAW-Fotos ohne Aufnahmezeit einmal neu lesen: früher nahm das Lesen das falsche Unterverzeichnis (NEF zeigte die Kopierzeit)
        if (db.Get("file_meta.raw1") != "1")
        {
            db.Exec($"UPDATE file_meta SET etag = NULL WHERE taken IS NULL AND etag IS NOT NULL AND etag NOT LIKE 'away:%' AND ({RawPaths("path")})");
            db.Set("file_meta.raw1", "1");
        }
        // Früher als application/octet-stream abgelegt (WebDAV-Hochladen): einmal nach der Endung bestimmen, damit Fotos als Bild
        // gelten und ihre Kameradaten gelesen werden
        if (db.Get("files.mime1") != "1") { FixMimes(db); db.Set("files.mime1", "1"); }
        Ensured.AddOrUpdate(db, new object());
    }

    /// <summary>Dateien ohne Typ oder mit application/octet-stream nach der Endung bestimmen; gibt die Anzahl zurück.</summary>
    internal static int FixMimes(UserDb db)
    {
        var n = 0;
        foreach (var path in db.Query("SELECT path FROM files WHERE is_dir = 0 AND (mime IS NULL OR mime = '' OR mime = 'application/octet-stream')", r => r.GetString(0)))
            if (DavServer.Mime(path) is var m && m != "application/octet-stream")
            {
                db.Exec("UPDATE files SET mime = $m WHERE path = $p", ("$m", m), ("$p", path));
                n++;
            }
        return n;
    }

    /// <summary>
    /// Noch zu lesen: ohne Eintrag oder geändert - nicht, was nur im Storage Extender liegt (Merker „away:“ vor dem ETag):
    /// das holt der Job nicht extra zurück, er liest es, sobald der Inhalt wieder hier ist (<see cref="Compute"/>).
    /// </summary>
    private const string Open = "(m.path IS NULL OR (m.etag IS NOT f.etag AND m.etag IS NOT ('away:' || f.etag)))";

    /// <summary>Fotos und Videos ohne (aktuelle) Aufnahmedaten.</summary>
    public static long Pending(UserDb db)
    {
        Ensure(db);
        return Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM files f LEFT JOIN file_meta m ON m.path = f.path WHERE f.is_dir = 0 AND {Media} AND {Open}") ?? 0L);
    }

    /// <summary>Nachlesen, in Portionen; zurück: wie viele gelesen wurden.</summary>
    public static int Compute(UserSpace space, CancellationToken ct, Action<int>? progress = null)
    {
        var db = space.Db;
        Ensure(db);
        var done = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = db.Query($"SELECT f.path, f.etag, f.blob FROM files f LEFT JOIN file_meta m ON m.path = f.path WHERE f.is_dir = 0 AND {Media} AND {Open} LIMIT 50",
                r => (Path: r.GetString(0), Etag: r.IsDBNull(1) ? null : r.GetString(1), Blob: r.IsDBNull(2) ? null : r.GetString(2))).ToList();
            if (batch.Count == 0) break;
            foreach (var (path, etag, blob) in batch)
            {
                if (ct.IsCancellationRequested) break;
                // Inhalt nur im Storage Extender: merken statt leer abhaken - gelesen wird, wenn er wieder hier ist
                if (blob != null && !db.Blobs.Exists(blob)) { MarkAway(db, path, etag); done++; continue; }
                Row? row = null;
                try { using var s = FileStore.OpenRead(db, path); row = WithSidecar(db, path, Read(path, s, space.Zone)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }   // fehlt oder liegt nur im Storage Extender
                Store(db, path, etag, row);
                done++;
                progress?.Invoke(done);
            }
        }
        // Wieder hier (geöffnet, zurückgeholt): jetzt lesen
        var back = db.Query($"SELECT f.path, f.etag, f.blob FROM files f JOIN file_meta m ON m.path = f.path WHERE f.is_dir = 0 AND m.etag = ('away:' || f.etag) AND f.blob IS NOT NULL",
            r => (Path: r.GetString(0), Etag: r.GetString(1), Blob: r.GetString(2))).Where(x => db.Blobs.Exists(x.Blob)).ToList();
        foreach (var (path, etag, _) in back)
        {
            if (ct.IsCancellationRequested) break;
            Row? row = null;
            try { using var s = FileStore.OpenRead(db, path); row = WithSidecar(db, path, Read(path, s, space.Zone)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { continue; }
            Store(db, path, etag, row);
            done++;
            progress?.Invoke(done);
        }
        return done;
    }

    /// <summary>
    /// Inhalt nur im Storage Extender: nur den Merker setzen. Schon gelesene Angaben bleiben stehen - sonst löschte ein
    /// erneutes Lesen (etwa nach einer neuen Spalte) die Aufnahmedaten aller ausgelagerten Fotos.
    /// </summary>
    private static void MarkAway(UserDb db, string path, string? etag)
    {
        if (db.Exec("UPDATE file_meta SET etag = $e WHERE path = $p AND ok = 1", ("$e", "away:" + etag), ("$p", path)) == 0)
            Store(db, path, "away:" + etag, null);
    }

    /// <summary>Gleich neu lesen (nach dem Schreiben von Metadaten), statt auf den Hintergrund-Job zu warten.</summary>
    public static void Refresh(UserSpace space, string path)
    {
        Ensure(space.Db);
        if (FileStore.Get(space.Db, path) is not { IsDir: false } f) return;
        Row? row = null;
        try { using var s = FileStore.OpenRead(space.Db, path); row = WithSidecar(space.Db, path, Read(path, s, space.Zone)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return; }
        Store(space.Db, path, f.Etag, row);
    }

    /// <summary>
    /// RAW mit Sidecar-Datei daneben (foto.arw.xmp, sonst foto.xmp): Titel, Beschreibung, Stichwörter, Bewertung und Farbmarkierung von
    /// dort (sie gehen vor - dort schreiben Lightroom und die Galerie hin), ebenso der Ort (Geotagging, von Hand im Atlas gesetzt).
    /// </summary>
    private static Row? WithSidecar(UserDb db, string path, Row? row)
    {
        if (!Thumbs.IsRaw(path) || SidecarOf(db, path) is not { } sc) return row;
        Gallery.Sidecar.Data? d = null;
        try { if (FileStore.Get(db, sc) is { Size: <= Gallery.Sidecar.MaxBytes } && FileStore.Data(db, sc) is { } x) d = Gallery.Sidecar.Read(x); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        if (d == null) return row;
        row ??= new Row(path, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        if (d.Lat != null) row = row with { Lat = d.Lat, Lon = d.Lon };
        // Nur ein Ort in der Sidecar-Datei: der Rest bleibt aus dem Foto
        if (d is { Title: null, Description: null, Rating: null, Label: null, Pick: null, Keywords.Count: 0 }) return row;
        return row with { Title = d.Title, Description = d.Description, Rating = d.Rating, Label = d.Label, Pick = d.Pick, Keywords = d.Keywords.Count > 0 ? string.Join("\n", d.Keywords) : null };
    }

    /// <summary>Pfad der Sidecar-Datei eines Fotos in der Ablage („foto.arw.xmp“ vor „foto.xmp“, Groß-/Kleinschreibung egal) oder null.</summary>
    public static string? SidecarOf(UserDb db, string path)
    {
        var parent = FileStore.Parent(path) ?? "";
        var dir = parent.Length == 0 ? "" : parent + "/";
        var name = path[(path.LastIndexOf('/') + 1)..];
        var a = (dir + name + ".xmp").ToLowerInvariant();
        var b = (dir + Path.GetFileNameWithoutExtension(name) + ".xmp").ToLowerInvariant();
        return db.Query("SELECT path FROM files WHERE parent = $d AND is_dir = 0 AND (lower(path) = $a OR lower(path) = $b)", r => r.GetString(0), ("$d", parent), ("$a", a), ("$b", b))
            .OrderByDescending(p => p.Length).FirstOrDefault();
    }

    private static void Store(UserDb db, string path, string? etag, Row? r) =>
        db.Exec(@"INSERT OR REPLACE INTO file_meta(path, etag, ok, taken, camera, lens, focal, aperture, exposure, iso, lat, lon, width, height, duration, rating, title, keywords, label, description, pick)
                  VALUES($p, $e, $ok, $t, $c, $l, $f, $a, $x, $i, $la, $lo, $w, $h, $d, $r, $ti, $k, $lb, $de, $pk)",
            ("$p", path), ("$e", etag), ("$ok", r == null ? 0 : 1), ("$t", r?.Taken), ("$c", r?.Camera), ("$l", r?.Lens), ("$f", r?.Focal), ("$a", r?.Aperture),
            ("$x", r?.Exposure), ("$i", r?.Iso), ("$la", r?.Lat), ("$lo", r?.Lon), ("$w", r?.Width), ("$h", r?.Height), ("$d", r?.Duration), ("$r", r?.Rating),
            ("$ti", r?.Title), ("$k", r?.Keywords), ("$lb", r?.Label), ("$de", r?.Description), ("$pk", r?.Pick));

    /// <summary>Gelesene Aufnahmedaten: alle Fotos und Videos (ohne Pfad) oder die direkt in einem Ordner.</summary>
    public static List<Row> List(UserDb db, string? folder)
    {
        Ensure(db);
        // Angaben nur in der Datenbank (PhotoMarks) gehen vor denen aus der Datei
        Gallery.PhotoMarks.Ensure(db);
        string C(string c) => Gallery.PhotoMarks.Col("m", c);
        var cols = $"m.path, m.taken, m.camera, m.lens, m.focal, m.aperture, m.exposure, m.iso, m.lat, m.lon, m.width, m.height, m.duration, {C("rating")}, {C("title")}, {C("keywords")}, {C("label")}, {C("description")}, {C("pick")}";
        var sql = folder == null
            ? $"SELECT {cols} FROM file_meta m LEFT JOIN photo_marks pm ON pm.key = m.path WHERE m.ok = 1"
            : $"SELECT {cols} FROM file_meta m JOIN files f ON f.path = m.path LEFT JOIN photo_marks pm ON pm.key = m.path WHERE m.ok = 1 AND f.parent = $d";
        return db.Query(sql, r => new Row(r.GetString(0), N<long>(r, 1), S(r, 2), S(r, 3), N<double>(r, 4), N<double>(r, 5), N<double>(r, 6), N<int>(r, 7),
            N<double>(r, 8), N<double>(r, 9), N<int>(r, 10), N<int>(r, 11), N<double>(r, 12), N<int>(r, 13), S(r, 14), S(r, 15), S(r, 16), S(r, 17), N<int>(r, 18)),
            ("$d", folder ?? "")).ToList();
    }

    private static string? S(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static T? N<T>(Microsoft.Data.Sqlite.SqliteDataReader r, int i) where T : struct => r.IsDBNull(i) ? null : (T)Convert.ChangeType(r.GetValue(i), typeof(T), CultureInfo.InvariantCulture);

    /// <summary>Aufnahmedaten aus einem Foto oder Video (EXIF, XMP, IPTC, QuickTime); null, wenn nichts lesbar ist.</summary>
    public static Row? Read(string path, Stream stream, TimeZoneInfo zone)
    {
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try { dirs = ImageMetadataReader.ReadMetadata(stream); }
        catch (Exception e) when (e is ImageProcessingException or IOException or MetadataException or ArgumentException or NotSupportedException or IndexOutOfRangeException) { return null; }
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        // RAW (NEF, ARW, CR2 …): mehrere Unterverzeichnisse - das erste ist oft das des Rohbilds ohne Aufnahmezeit und Belichtung
        var subs = dirs.OfType<ExifSubIfdDirectory>().ToList();
        var exif = subs.FirstOrDefault(d => d.ContainsTag(ExifDirectoryBase.TagDateTimeOriginal))
            ?? subs.FirstOrDefault(d => d.ContainsTag(ExifDirectoryBase.TagExposureTime) || d.ContainsTag(ExifDirectoryBase.TagFNumber)) ?? subs.FirstOrDefault();

        // Aufnahmezeit wie im Tagebuch: mit Zeitzone aus dem Bild, sonst die eigene; bei Videos die Erstellzeit (UTC)
        DateTime? taken = null;
        if (exif != null && (exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var local) || exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out local)))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            var offset = exif.GetDescription(ExifDirectoryBase.TagTimeZoneOriginal) ?? exif.GetDescription(ExifDirectoryBase.TagTimeZone);
            taken = offset != null && TimeSpan.TryParse(offset.TrimStart('+'), CultureInfo.InvariantCulture, out var off)
                ? DateTime.SpecifyKind(local - (offset.StartsWith('-') ? -off.Duration() : off), DateTimeKind.Utc)
                : Ics.ToUtc(local, zone);
        }
        var movie = dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
        if (taken == null && movie != null && movie.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) && created.Year > 1970)
            taken = DateTime.SpecifyKind(created, DateTimeKind.Utc);

        // Kamera: das Modell - mit dem Hersteller davor, wenn das Modell ihn nicht schon nennt („Canon EOS R5“ statt „Canon Canon EOS R5“)
        var make = ifd0?.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
        var model = ifd0?.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
        var camera = string.IsNullOrEmpty(model) ? make : string.IsNullOrEmpty(make) || model.StartsWith(make.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? model : $"{Short(make)} {model}";
        var lens = exif?.GetDescription(ExifDirectoryBase.TagLensModel)?.Trim();

        double? Rational(ExifDirectoryBase? d, int tag) => d != null && d.TryGetRational(tag, out var r) && r.Denominator != 0 ? r.ToDouble() : null;
        var focal = Rational(exif, ExifDirectoryBase.TagFocalLength);
        var aperture = Rational(exif, ExifDirectoryBase.TagFNumber);
        var exposure = Rational(exif, ExifDirectoryBase.TagExposureTime);
        int? iso = exif != null && exif.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var isoV) && isoV > 0 ? isoV : null;

        // Abmessungen, richtig herum (Hochformat-Fotos sind oft quer gespeichert und nur gedreht markiert)
        int? width = null, height = null;
        void Size(MetadataExtractor.Directory? d, int w, int h) { if (width == null && d != null && d.TryGetInt32(w, out var a) && d.TryGetInt32(h, out var b) && a > 0 && b > 0) { width = a; height = b; } }
        Size(exif, ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight);
        Size(dirs.OfType<JpegDirectory>().FirstOrDefault(), JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight);
        Size(dirs.OfType<PngDirectory>().FirstOrDefault(), PngDirectory.TagImageWidth, PngDirectory.TagImageHeight);
        Size(dirs.OfType<WebPDirectory>().FirstOrDefault(), WebPDirectory.TagImageWidth, WebPDirectory.TagImageHeight);
        var track = dirs.OfType<QuickTimeTrackHeaderDirectory>().FirstOrDefault(t => t.TryGetInt32(QuickTimeTrackHeaderDirectory.TagWidth, out var tw) && tw > 0);
        Size(track, QuickTimeTrackHeaderDirectory.TagWidth, QuickTimeTrackHeaderDirectory.TagHeight);
        if (ifd0 != null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientation) && orientation is >= 5 and <= 8 && width != null) (width, height) = (height, width);

        double? duration = null;
        if (movie != null && movie.GetObject(QuickTimeMovieHeaderDirectory.TagDuration) is { } dv)
            duration = dv switch { TimeSpan ts => ts.TotalSeconds, IConvertible c => c.ToDouble(CultureInfo.InvariantCulture), _ => null };
        if (duration is <= 0) duration = null;

        double? lat = null, lon = null;
        if (dirs.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation() is { } geo && !geo.IsZero)
            (lat, lon) = (Math.Round(geo.Latitude, 6), Math.Round(geo.Longitude, 6));

        // Bewertung, Titel, Beschreibung, Stichwörter: XMP (Lightroom, Kamera), sonst IPTC
        int? rating = null, pick = null;
        string? title = null, description = null, label = null;
        var keywords = new List<string>();
        if (dirs.OfType<XmpDirectory>().FirstOrDefault()?.GetXmpProperties() is { } xmp)
        {
            if (xmp.TryGetValue("xmp:Rating", out var rv) && int.TryParse(rv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rr) && rr is >= 1 and <= 5) rating = rr;
            title = xmp.Where(p => p.Key.StartsWith("dc:title[", StringComparison.Ordinal)).Select(p => p.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            description = xmp.Where(p => p.Key.StartsWith("dc:description[", StringComparison.Ordinal)).Select(p => p.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            keywords.AddRange(xmp.Where(p => p.Key.StartsWith("dc:subject[", StringComparison.Ordinal)).Select(p => p.Value));
            label = ColorLabel.FromXmp(xmp);
            if (xmp.TryGetValue("xmpDM:pick", out var pv) && int.TryParse(pv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pp) && pp != 0) pick = Math.Sign(pp);
        }
        // Bewertung aus Windows (EXIF „Rating“), wenn XMP keine hat
        if (rating == null && ifd0 != null && ifd0.TryGetInt32(0x4746, out var wr) && wr is >= 1 and <= 5) rating = wr;
        if (dirs.OfType<IptcDirectory>().FirstOrDefault() is { } iptc)
        {
            title ??= iptc.GetDescription(IptcDirectory.TagObjectName);
            description ??= iptc.GetDescription(IptcDirectory.TagCaption);
            // Stichwörter aus dem IPTC nur, wenn das XMP keine hat (es ist das neuere - dort schreiben Lightroom und die Galerie hin)
            if (keywords.Count == 0 && iptc.GetStringArray(IptcDirectory.TagKeywords) is { } kw) keywords.AddRange(kw);
        }
        var kws = keywords.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var any = taken != null || camera != null || width != null || duration != null || lat != null || rating != null || label != null || pick != null || kws.Count > 0
            || !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(description);
        if (!any) return null;
        return new Row(path, taken == null ? null : new DateTimeOffset(taken.Value).ToUnixTimeMilliseconds(), Clean(camera), Clean(lens), Round(focal), Round(aperture, 1),
            exposure, iso, lat, lon, width, height, duration == null ? null : Math.Round(duration.Value, 1), rating, Clean(title), kws.Count > 0 ? string.Join("\n", kws) : null, label, Clean(description), pick);
    }

    /// <summary>
    /// Alle Angaben in einer Bilddatei (EXIF, XMP, IPTC, Hersteller …) zum Ansehen im Detailbereich: je Abschnitt Name und Wert,
    /// ohne eingebettete Vorschaubilder und Tabellen, lange Werte gekürzt.
    /// </summary>
    public static List<(string Group, string Name, string Value)> AllTags(Stream stream)
    {
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try { dirs = ImageMetadataReader.ReadMetadata(stream); }
        catch (Exception e) when (e is ImageProcessingException or IOException or MetadataException or ArgumentException or NotSupportedException or IndexOutOfRangeException) { return []; }
        var skip = new[] { "Thumbnail", "Huffman", "Quantization", "File Type", "File" };
        var list = new List<(string, string, string)>();
        foreach (var d in dirs)
        {
            if (skip.Any(x => d.Name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var t in d.Tags)
            {
                var v = t.Description?.Trim();
                if (string.IsNullOrEmpty(v) || t.Name.StartsWith("Unknown tag", StringComparison.Ordinal)) continue;
                if (v.Length > 300) v = v[..300] + " …";
                list.Add((d.Name, t.Name, v));
                if (list.Count >= 800) return list;
            }
        }
        return list;
    }

    private static double? Round(double? v, int digits = 0) => v == null || v <= 0 ? null : Math.Round(v.Value, digits);
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('\0');
    /// <summary>„NIKON CORPORATION“ → „Nikon“, „OLYMPUS IMAGING CORP.“ → „Olympus“.</summary>
    private static string Short(string make)
    {
        var w = make.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return w.Length > 3 && w.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w;
    }
}

/// <summary>
/// Liest im Hintergrund die Aufnahmedaten fehlender oder geänderter Fotos und Videos: kurz nach dem Start, nach jeder Änderung
/// in einer Ablage (<see cref="Wake"/>, etwa ein über Nextcloud geänderter Titel - wenige Sekunden später) und sonst alle fünf
/// Minuten. Verdrängte Inhalte werden nicht geholt - sie bekommen einen leeren Eintrag und bleiben ohne Daten.
/// </summary>
public sealed class FileMetaJob(Spaces spaces, ILogger<FileMetaJob> log) : BackgroundService
{
    private static readonly SemaphoreSlim Signal = new(0);

    /// <summary>Etwas in einer Ablage hat sich geändert: bald nachsehen (mehrere Änderungen kurz hintereinander zählen einmal).</summary>
    public static void Wake() { if (Signal.CurrentCount == 0) try { Signal.Release(); } catch (SemaphoreFullException) { } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(50), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            await Task.Run(() =>
            {
                foreach (var space in spaces.All())
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        using (BlobStore.WithoutFetch())
                        {
                            var pending = FileMeta.Pending(space.Db);
                            if (pending == 0) continue;
                            using var activity = Activity.Start(space.User.Id, Activity.FilesMeta, total: pending);
                            var done = FileMeta.Compute(space, ct, d => { if (d % 25 == 0) activity.Progress(d); });
                            activity.Progress(done);
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Activity.Start(space.User.Id, Activity.FilesMeta).Fail(ex.Message); log.LogWarning("Aufnahmedaten {User}: {Error}", space.User.Login, ex.Message); }
                }
            }, CancellationToken.None);
            try
            {
                // Bis eine Änderung kommt (höchstens fünf Minuten), dann kurz warten, damit ein Schwung Dateien zusammen gelesen wird
                if (await Signal.WaitAsync(TimeSpan.FromMinutes(5), ct)) await Task.Delay(TimeSpan.FromSeconds(3), ct);
                while (Signal.CurrentCount > 0) await Signal.WaitAsync(ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }
}
