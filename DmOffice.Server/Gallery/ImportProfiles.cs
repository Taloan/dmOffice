using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace DmOffice.Gallery;

/// <summary>Ein Quellordner: in der eigenen Ablage (Mount leer) oder in einem externen Ordner, immer mit Unterordnern.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ImportSourceJson))]
public sealed record ImportSource(string Mount, string Path);

/// <summary>Quellordner lesen: früher ein Pfad in der eigenen Ablage, jetzt Ordner und Pfad.</summary>
public sealed class ImportSourceJson : System.Text.Json.Serialization.JsonConverter<ImportSource>
{
    public override ImportSource Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.String) return new("", reader.GetString() ?? "");
        using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
        string Prop(string n) => doc.RootElement.EnumerateObject().FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: System.Text.Json.JsonValueKind.String } v
            ? v.GetString() ?? "" : "";
        return new(Prop("mount"), Prop("path"));
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, ImportSource value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("mount", value.Mount);
        writer.WriteString("path", value.Path);
        writer.WriteEndObject();
    }
}

/// <summary>Ein Importprofil: Fotos aus Quellordnern (samt Unterordnern) nach Mustern in einen Zielordner einsortieren.</summary>
public sealed class ImportProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>Quellordner, in der eigenen Ablage oder in externen Ordnern.</summary>
    public List<ImportSource> Sources { get; set; } = [];
    /// <summary>Externer Ordner des Ziels (ExternalFolders); leer = eigene Ablage.</summary>
    public string Mount { get; set; } = "";
    /// <summary>Zielordner darin, mit „/“ getrennt; leer = oberste Ebene.</summary>
    public string Target { get; set; } = "";
    public string Folders { get; set; } = "{exif_year}/{exif_month}/{exif_day}";
    /// <summary>Dateiname ohne Endung.</summary>
    public string Names { get; set; } = "{original_name}";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Kopieren statt verschieben: die Quelle bleibt, das Profil merkt sich, was schon einsortiert ist.</summary>
    public bool Copy { get; set; }
    /// <summary>Alle so viele Minuten prüfen.</summary>
    public int Every { get; set; } = 10;
    /// <summary>Nur Fotos, die an diesem Tag oder später aufgenommen wurden.</summary>
    public DateOnly? Since { get; set; }
    public bool Active { get; set; } = true;
    /// <summary>Sprache der Oberfläche beim Speichern: „de“ oder „en“ (Monatsnamen).</summary>
    public string Language { get; set; } = "de";
    public DateTime? LastRun { get; set; }
    public string? Error { get; set; }
    /// <summary>Beim letzten Lauf einsortiert, insgesamt einsortiert, noch offen.</summary>
    public int LastCount { get; set; }
    public int Total { get; set; }
    public int Pending { get; set; }
}

/// <summary>
/// Importprofile der Galerie: Fotos, die zum Beispiel der Auto-Upload der App in einen Ordner der eigenen Ablage oder eine Freigabe
/// legt, werden nach Mustern für Ordnerstruktur und Dateinamen in einen Zielordner einsortiert. Quelle und Ziel sind die eigene
/// Ablage oder externe Ordner; im selben Speicher wird nur umbenannt, sonst gestreamt. RAW, JPG und XMP-Sidecar mit gleichem Namen
/// bleiben zusammen.
/// Gearbeitet wird eine Datei nach der anderen; von einem Foto liegt höchstens ein JPEG ganz im Speicher (wenn Titel oder
/// Beschreibung hineingeschrieben werden), RAW-Dateien nie.
/// </summary>
public static class ImportProfiles
{
    public const string Key = "gallery.imports";
    /// <summary>Je Lauf und Profil höchstens so viele Fotos - der Rest folgt im nächsten Durchgang, nicht alles auf einmal.</summary>
    public static int MaxPerRun { get; set; } = 200;
    /// <summary>Eine Datei wird erst einsortiert, wenn sie so lange in der Quelle liegt (das Gegenstück eines Paares kommt oft später).</summary>
    public static TimeSpan Settle { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>
    /// Eigene Ablage: Dateien erscheinen erst ganz gespeichert und stoßen das Profil sofort an (<see cref="FolderWatch"/>, nach
    /// <see cref="ImportProfileJob.Quiet"/> ohne weitere Datei) - kurz warten nur, damit RAW, JPG und XMP zusammenkommen.
    /// </summary>
    public static TimeSpan SettleOwn { get; set; } = TimeSpan.FromSeconds(10);
    private const long MaxJpegBytes = 100L * 1024 * 1024;

    public static readonly string[] FolderTokens = ["camera_make", "camera_model", "import_date", "import_year", "import_month", "import_month_name", "import_day",
        "exif_date", "exif_year", "exif_month", "exif_month_name", "exif_day"];
    public static readonly string[] NameTokens = ["original_name", "camera_make", "camera_model", "import_date", "exif_date", "exif_datetime", "exif_time", "sequence"];
    /// <summary>Für Titel und Beschreibung: alle, in der Reihenfolge des CameraImporters.</summary>
    public static readonly string[] AllTokens = ["original_name", "camera_make", "camera_model", "exif_date", "exif_year", "exif_month", "exif_month_name", "exif_day",
        "exif_datetime", "exif_time", "import_date", "import_year", "import_month", "import_month_name", "import_day", "sequence"];

    public static List<ImportProfile> Of(UserDb db) => db.GetJson<List<ImportProfile>>(Key);
    public static void Save(UserDb db, List<ImportProfile> list)
    {
        db.SetJson(Key, list);
        FolderWatch.Forget(db.UserId);
    }

    /// <summary>Profil ändern, ohne gleichzeitige Änderungen (Lauf, Bearbeiten) zu verlieren.</summary>
    private static readonly object SaveLock = new();
    internal static void Update(UserDb db, string id, Action<ImportProfile> change)
    {
        lock (SaveLock)
        {
            var list = Of(db);
            if (list.FirstOrDefault(p => p.Id == id) is not { } p) return;
            change(p);
            Save(db, list);
        }
    }
    internal static void Change(UserDb db, Action<List<ImportProfile>> change)
    {
        lock (SaveLock) { var list = Of(db); change(list); Save(db, list); }
    }

    // ---------------- Muster ----------------

    /// <summary>Angaben zu einem Foto für die Platzhalter.</summary>
    public sealed record Facts(DateTime Taken, bool FromExif, string? Make, string? Model, string OriginalName);

    private static readonly Regex Token = new(@"\{([a-z_]+)\}", RegexOptions.Compiled);

    /// <summary>Unbekannte Platzhalter in einem Muster (für die Prüfung beim Speichern).</summary>
    public static List<string> Unknown(string pattern) =>
        Token.Matches(pattern).Select(m => m.Groups[1].Value).Where(t => !AllTokens.Contains(t)).Distinct().ToList();

    /// <summary>Wert eines Platzhalters; leer, wenn es ihn für dieses Foto nicht gibt.</summary>
    public static string Value(string token, Facts f, DateTime import, int sequence, CultureInfo culture) => token switch
    {
        "camera_make" => f.Make ?? "Unknown",
        "camera_model" => f.Model ?? "Unknown",
        "original_name" => f.OriginalName,
        "sequence" => sequence.ToString("D3", CultureInfo.InvariantCulture),
        "import_date" => import.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        "import_year" => import.ToString("yyyy", CultureInfo.InvariantCulture),
        "import_month" => import.ToString("MM", CultureInfo.InvariantCulture),
        "import_month_name" => culture.DateTimeFormat.GetMonthName(import.Month),
        "import_day" => import.ToString("dd", CultureInfo.InvariantCulture),
        "exif_date" => f.Taken.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        "exif_year" => f.Taken.ToString("yyyy", CultureInfo.InvariantCulture),
        "exif_month" => f.Taken.ToString("MM", CultureInfo.InvariantCulture),
        "exif_month_name" => culture.DateTimeFormat.GetMonthName(f.Taken.Month),
        "exif_day" => f.Taken.ToString("dd", CultureInfo.InvariantCulture),
        "exif_time" => f.Taken.ToString("HHmmss", CultureInfo.InvariantCulture),
        "exif_datetime" => f.Taken.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture),
        _ => "",
    };

    private static readonly char[] Bad = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Steuerzeichen und Zeichen, die die Schreibrichtung umstellen (ein Name sähe sonst wie ein anderer aus).</summary>
    public static bool Invisible(char c) => char.IsControl(c) || c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    /// <summary>Wert dateisystemfest wie im CameraImporter: Steuerzeichen weg, unzulässige Zeichen „_“, kein Punkt oder Leerzeichen am Ende.</summary>
    public static string Safe(string v)
    {
        var s = new string(v.Where(c => !Invisible(c)).Select(c => Bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }

    private static string Fill(string pattern, Facts f, DateTime import, int sequence, CultureInfo culture) =>
        Token.Replace(pattern, m => Safe(Value(m.Groups[1].Value, f, import, sequence, culture)));

    /// <summary>Ordnerstruktur: Platzhalter ersetzen; die Schrägstriche des Musters trennen die Ebenen.</summary>
    public static string RenderFolder(string pattern, Facts f, DateTime import, int sequence, CultureInfo culture)
    {
        var parts = Fill(pattern, f, import, sequence, culture).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().TrimEnd('.')).Where(x => x.Length > 0 && x != "..").Select(x => x.Length > 120 ? x[..120] : x);
        return string.Join('/', parts);
    }

    /// <summary>Dateiname ohne Endung.</summary>
    public static string RenderName(string pattern, Facts f, DateTime import, int sequence, CultureInfo culture)
    {
        var v = Safe(Fill(pattern, f, import, sequence, culture));
        return v.Length > 150 ? v[..150] : v;
    }

    /// <summary>Titel und Beschreibung: Platzhalter ersetzen, sonst unverändert.</summary>
    public static string RenderText(string pattern, Facts f, DateTime import, int sequence, CultureInfo culture) =>
        Token.Replace(pattern, m => Value(m.Groups[1].Value, f, import, sequence, culture)).Trim();

    /// <summary>Monatsnamen in der Sprache der Oberfläche, mit der das Profil gespeichert wurde.</summary>
    public static CultureInfo CultureOf(string? language) =>
        language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("de-DE");

    /// <summary>
    /// Aufnahmezeit (Ortszeit der Kamera), Hersteller und Modell aus dem Kopf der Datei. Ohne EXIF wie im CameraImporter die früheste
    /// Dateizeit: Kopieren setzt Zeiten nur nach vorn, die früheste liegt der Aufnahme am nächsten.
    /// </summary>
    public static Facts Read(Stream stream, string originalName, DateTime modifiedUtc, TimeZoneInfo zone)
    {
        DateTime? taken = null;
        string? make = null, model = null;
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(stream);
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            make = ifd0?.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
            model = ifd0?.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
            foreach (var d in dirs.OfType<ExifSubIfdDirectory>())
                if (d.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var t) || d.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out t))
                { taken = DateTime.SpecifyKind(t, DateTimeKind.Unspecified); break; }
            if (taken == null && dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault() is { } movie
                && movie.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) && created.Year > 1970)
                taken = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(created, DateTimeKind.Utc), zone);
        }
        catch (Exception e) when (e is ImageProcessingException or IOException or MetadataException or ArgumentException or NotSupportedException or IndexOutOfRangeException) { }
        var fromExif = taken != null;
        taken ??= TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc), zone);
        return new Facts(taken.Value, fromExif, string.IsNullOrEmpty(make) ? null : make, string.IsNullOrEmpty(model) ? null : model, originalName);
    }

    /// <summary>Beispiel für die Vorschau, wenn in den Quellordnern noch kein Foto liegt.</summary>
    public static readonly Facts Example = new(new DateTime(2026, 6, 14, 10, 30, 45), true, "FUJIFILM", "X100VI", "DSCF0042");

    // ---------------- Orte: eigene Ablage oder externer Ordner ----------------

    /// <summary>Eine Datei in Quelle oder Ziel; Etag ist bei externen Ordnern Größe und Änderungszeit.</summary>
    public sealed record Item(string Path, long Size, DateTime Modified, DateTime Created, string Etag, string? Mime = null)
    {
        public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    }

    /// <summary>Wo Fotos liegen oder hinkommen: die eigene Ablage oder ein externer Ordner. Alles gestreamt, nichts ganz im Speicher.</summary>
    public abstract class Loc : IDisposable
    {
        public abstract string Mount { get; }
        /// <summary>Alle Dateien unter einem Ordner, mit Unterordnern.</summary>
        public abstract Task<List<Item>> TreeAsync(string root, CancellationToken ct);
        /// <summary>Dateien direkt in einem Ordner, nach Namen; fehlt der Ordner: leer.</summary>
        public abstract Task<Dictionary<string, Item>> FilesAsync(string dir, CancellationToken ct);
        public abstract Task<Stream> OpenAsync(string path, CancellationToken ct);
        /// <summary>Der Anfang einer Datei für die Aufnahmedaten (bei externen Ordnern nur ein Ausschnitt).</summary>
        public abstract Task<Stream> HeadAsync(Item f, CancellationToken ct);
        public abstract Task WriteAsync(string path, Stream data, Item like, CancellationToken ct);
        public abstract Task MkdirAsync(string dir, CancellationToken ct);
        /// <summary>Löschen: in der eigenen Ablage in den Papierkorb, im externen Ordner endgültig.</summary>
        public abstract Task DeleteAsync(string path, CancellationToken ct);
        public abstract Task<long?> SizeAsync(string path, CancellationToken ct);
        public virtual async Task<string?> ShaAsync(string path, CancellationToken ct)
        {
            await using var s = await OpenAsync(path, ct);
            return FileStore.Hex(await SHA256.HashDataAsync(s, ct));
        }
        public virtual void Dispose() { }
    }

    public sealed class OwnLoc(UserDb db) : Loc
    {
        public UserDb Db => db;
        public override string Mount => "";
        private static Item Of(FileEntry f) => new(f.Path, f.Size, f.Modified, f.Created, f.Etag, f.Mime);
        public override Task<List<Item>> TreeAsync(string root, CancellationToken ct) =>
            Task.FromResult(FileStore.Descendants(db, root).Where(f => !f.IsDir).Select(Of).ToList());
        public override Task<Dictionary<string, Item>> FilesAsync(string dir, CancellationToken ct) =>
            Task.FromResult(dir.Length == 0 || FileStore.Get(db, dir) is { IsDir: true }
                ? FileStore.Children(db, dir).Where(f => !f.IsDir).Select(Of).ToDictionary(f => f.Name, StringComparer.Ordinal)
                : new Dictionary<string, Item>(StringComparer.Ordinal));
        public override Task<Stream> OpenAsync(string path, CancellationToken ct) => Task.FromResult(FileStore.OpenRead(db, path));
        public override Task<Stream> HeadAsync(Item f, CancellationToken ct) => OpenAsync(f.Path, ct);
        public override async Task WriteAsync(string path, Stream data, Item like, CancellationToken ct) =>
            await FileStore.PutAsync(db, path, data, like.Mime ?? MimeOf(like.Name), ifNoneMatch: "*", mtime: like.Modified, ct: ct);
        public override Task MkdirAsync(string dir, CancellationToken ct) { if (dir.Length > 0) FileStore.EnsureDir(db, dir); return Task.CompletedTask; }
        public override Task DeleteAsync(string path, CancellationToken ct) { FileStore.Delete(db, path); return Task.CompletedTask; }
        public override Task<long?> SizeAsync(string path, CancellationToken ct) => Task.FromResult(FileStore.Get(db, path) is { IsDir: false } f ? f.Size : (long?)null);
        public override async Task<string?> ShaAsync(string path, CancellationToken ct) =>
            db.Scalar("SELECT sha256 FROM files WHERE path = $p", ("$p", path)) is string s && s.Length == 64 ? s : await base.ShaAsync(path, ct);
    }

    public sealed class ExtLoc(IStorageDriver driver, string mount) : Loc
    {
        public IStorageDriver Driver => driver;
        public override string Mount => mount;
        /// <summary>Höchstens so viele Dateien je Quellordner auflisten.</summary>
        private const int MaxTree = 50_000;
        private const int HeadBytes = 512 * 1024, RawHeadBytes = 6 * 1024 * 1024;
        private static Item Of(string path, DriverEntry e) => new(path, e.Size, e.Modified, e.Modified, $"{e.Size}:{e.Modified.Ticks}", e.ContentType);
        public override async Task<List<Item>> TreeAsync(string root, CancellationToken ct)
        {
            var list = new List<Item>();
            var dirs = new Stack<string>([root]);
            while (dirs.Count > 0 && list.Count < MaxTree)
            {
                ct.ThrowIfCancellationRequested();
                var dir = dirs.Pop();
                foreach (var e in await driver.ListAsync(dir, ct))
                {
                    // Versteckte Ordner und die der NAS (.thumbnails, @eaDir) auslassen
                    if (e.Name.StartsWith('.') || e.Name.StartsWith('@')) continue;
                    var path = StoragePath.Join(dir, e.Name);
                    if (e.IsDir) dirs.Push(path); else list.Add(Of(path, e));
                }
            }
            return list;
        }
        public override async Task<Dictionary<string, Item>> FilesAsync(string dir, CancellationToken ct)
        {
            if (dir.Length > 0 && await driver.StatAsync(dir, ct) is not { IsDir: true }) return new(StringComparer.Ordinal);
            return (await driver.ListAsync(dir, ct)).Where(e => !e.IsDir).ToDictionary(e => e.Name, e => Of(StoragePath.Join(dir, e.Name), e), StringComparer.Ordinal);
        }
        public override Task<Stream> OpenAsync(string path, CancellationToken ct) => driver.OpenReadAsync(path, ct);
        public override async Task<Stream> HeadAsync(Item f, CancellationToken ct) =>
            new MemoryStream(await driver.ReadRangeAsync(f.Path, 0, (int)Math.Min(f.Size, Thumbs.IsRaw(f.Name) ? RawHeadBytes : HeadBytes), ct));
        public override Task WriteAsync(string path, Stream data, Item like, CancellationToken ct) => driver.WriteAsync(path, data, ct);
        public override Task MkdirAsync(string dir, CancellationToken ct) => dir.Length > 0 ? driver.MkdirAsync(dir, ct) : Task.CompletedTask;
        public override Task DeleteAsync(string path, CancellationToken ct) => driver.DeleteAsync(path, ct);
        public override async Task<long?> SizeAsync(string path, CancellationToken ct) => await driver.StatAsync(path, ct) is { IsDir: false } e ? e.Size : null;
        public override void Dispose() => driver.Dispose();
    }

    private static string MimeOf(string name) => PhotoMeta.IsJpeg(name) ? "image/jpeg" : Sidecar.Is(name) ? "application/rdf+xml" : "application/octet-stream";

    // ---------------- Gruppen: RAW + JPG + XMP ----------------

    /// <summary>Ein Foto in der Quelle: alle Dateien gleichen Namens im selben Ordner (RAW, JPG, Video) und die XMP-Sidecar-Datei.</summary>
    public sealed record Group(string Mount, string Dir, string Stem, List<Item> Photos, List<Item> Sidecars)
    {
        public IEnumerable<Item> All => Photos.Concat(Sidecars);
        /// <summary>Für die Aufnahmedaten: das JPEG (schnell gelesen), sonst das erste.</summary>
        public Item Primary => Photos.FirstOrDefault(p => PhotoMeta.IsJpeg(p.Name)) ?? Photos[0];
        /// <summary>Früheste Dateizeit des Fotos (Hochladen, Änderung) - Ersatz, wenn kein EXIF-Datum da ist.</summary>
        public DateTime Earliest => Photos.Min(p => p.Created < p.Modified ? p.Created : p.Modified);
    }

    private static string StemOf(string name) => Path.GetFileNameWithoutExtension(name);

    /// <summary>Dateien eines Orts zu Fotos zusammenfassen. „X.RAF.xmp“ und „X.xmp“ gehören zu „X.RAF“/„X.JPG“; Sidecar ohne Foto bleibt liegen.</summary>
    public static List<Group> Groups(IEnumerable<Item> files, string mount = "")
    {
        var map = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        var sidecars = new List<Item>();
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (Sidecar.Is(f.Name)) { sidecars.Add(f); continue; }
            if (GalleryFolders.KindOf(f.Name) == null) continue;
            var dir = StoragePath.Parent(f.Path);
            var stem = StemOf(f.Name);
            var key = dir + "/" + stem;
            if (!map.TryGetValue(key, out var g)) map[key] = g = new Group(mount, dir, stem, [], []);
            g.Photos.Add(f);
        }
        foreach (var s in sidecars)
        {
            var dir = StoragePath.Parent(s.Path);
            var inner = s.Name[..^4];
            // „X.RAF.xmp“: zu X, wenn „X.RAF“ ein Foto ist; „X.xmp“: zu X
            var stem = map.ContainsKey(dir + "/" + StemOf(inner)) && GalleryFolders.KindOf(inner) != null ? StemOf(inner) : inner;
            if (map.TryGetValue(dir + "/" + stem, out var g)) g.Sidecars.Add(s);
        }
        return [.. map.Values.OrderBy(g => g.Dir, StringComparer.Ordinal).ThenBy(g => g.Stem, StringComparer.Ordinal)];
    }

    /// <summary>Name einer Datei am Ziel: neuer Name, alte Endung; die Sidecar-Datei behält ihren Teil hinter dem alten Namen („.RAF.xmp“).</summary>
    public static string TargetName(Item f, string stem, string name) => name + f.Name[stem.Length..];

    // ---------------- Merkliste ----------------

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UserDb, object> Ensured = new();

    /// <summary>Was ein Profil schon gesehen hat (Ort, Pfad und ETag in der Quelle): einsortiert beim Kopieren, übersprungen wegen des Datums.</summary>
    private static void Ensure(UserDb db)
    {
        if (Ensured.TryGetValue(db, out _)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS gallery_import_seen(profile TEXT NOT NULL, path TEXT NOT NULL, etag TEXT NOT NULL,
            PRIMARY KEY(profile, path)) WITHOUT ROWID");
        Ensured.AddOrUpdate(db, new object());
    }

    /// <summary>Schlüssel einer Quelldatei in der Merkliste: der Pfad, bei externen Ordnern mit dem Ordner davor.</summary>
    private static string SeenKey(string mount, Item f) => mount.Length == 0 ? f.Path : mount + ":" + f.Path;

    private static Dictionary<string, string> Seen(UserDb db, string profile) =>
        db.Query("SELECT path, etag FROM gallery_import_seen WHERE profile = $p", r => (r.GetString(0), r.GetString(1)), ("$p", profile))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

    private static void Remember(UserDb db, string profile, Group g)
    {
        foreach (var f in g.All)
            db.Exec("INSERT OR REPLACE INTO gallery_import_seen(profile, path, etag) VALUES($p, $f, $e)", ("$p", profile), ("$f", SeenKey(g.Mount, f)), ("$e", f.Etag));
    }

    /// <summary>Profil gelöscht: seine Merkliste auch.</summary>
    public static void Forget(UserDb db, string profile)
    {
        Ensure(db);
        db.Exec("DELETE FROM gallery_import_seen WHERE profile = $p", ("$p", profile));
        foreach (var k in Stable.Keys.Where(k => k.StartsWith(profile + "|", StringComparison.Ordinal))) Stable.TryRemove(k, out _);
    }

    /// <summary>
    /// Dateien in externen Ordnern: zuerst gesehen mit dieser Größe und Zeit. Kopieren in eine Freigabe behält oft die alte
    /// Änderungszeit - fertig ist eine Datei erst, wenn sie über <see cref="Settle"/> unverändert geblieben ist.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Etag, DateTime Since)> Stable = new();

    // ---------------- Einsortieren ----------------

    /// <summary>Ziel oder Quelle nicht erreichbar: der Lauf endet, beim nächsten Intervall geht es normal weiter.</summary>
    public sealed class TargetUnavailable(string message) : Exception(message);

    /// <summary>Einen externen Ordner öffnen; null mit Grund, wenn er gerade nicht geht.</summary>
    private static async Task<(ExtLoc? Loc, string? Why)> OpenExtAsync(UserSpace space, ServerConfig config, string mount, bool write)
    {
        try
        {
            if (!await ExternalFolders.ReachableAsync(space, config, mount)) return (null, "gerade nicht erreichbar");
            var (d, ro) = ExternalFolders.Open(space, config, mount);
            if (ro && write) { d.Dispose(); return (null, "nur zum Lesen eingebunden"); }
            return (new ExtLoc(d, mount), null);
        }
        catch (KeyNotFoundException) { return (null, "nicht mehr vorhanden"); }
        // Erreichbarkeit ist kurz zwischengespeichert: die Freigabe kann inzwischen weg sein
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException) { return (null, "gerade nicht erreichbar"); }
    }

    /// <summary>Ein Lauf eines Profils: bis zu <see cref="MaxPerRun"/> Fotos einsortieren. Gibt die Zahl der einsortierten zurück.</summary>
    public static async Task<int> RunAsync(UserSpace space, ServerConfig config, string id, CancellationToken ct, Action<int, int>? progress = null)
    {
        var db = space.Db;
        if (Of(db).FirstOrDefault(x => x.Id == id) is not { } p) return 0;
        Ensure(db);
        var now = DateTime.UtcNow;
        var culture = CultureOf(p.Language);
        var zone = space.Zone;
        var import = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
        var locs = new Dictionary<string, Loc>(StringComparer.Ordinal) { [""] = new OwnLoc(db) };
        int done = 0, handled = 0, total = 0;
        string? error = null, sourceError = null;
        try
        {
            // Quellen auflisten; ein externer Quellordner, der gerade fehlt, wird übersprungen
            var groups = new List<(Loc Loc, Group Group)>();
            var present = new HashSet<string>(StringComparer.Ordinal);
            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var src in p.Sources)
            {
                if (missing.Contains(src.Mount)) continue;
                if (!locs.TryGetValue(src.Mount, out var loc))
                {
                    var (ext, why) = await OpenExtAsync(space, config, src.Mount, write: !p.Copy);
                    if (ext == null) { missing.Add(src.Mount); sourceError ??= $"Ein Quellordner ist {why}."; continue; }
                    locs[src.Mount] = loc = ext;
                }
                List<Item> files;
                try { files = await loc.TreeAsync(src.Path, ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
                {
                    missing.Add(src.Mount);
                    sourceError ??= "Ein Quellordner ist gerade nicht erreichbar.";
                    continue;
                }
                var ready = new List<Item>();
                foreach (var f in files)
                    if (present.Add(SeenKey(src.Mount, f)) && Settled(p.Id, src.Mount, f, now)) ready.Add(f);
                groups.AddRange(Groups(ready, src.Mount).Select(g => (loc, g)));
            }
            var seen = Seen(db, p.Id);
            // Merkliste aufräumen: was nicht mehr in der Quelle liegt (nicht bei Quellen, die gerade fehlen)
            foreach (var gone in seen.Keys.Where(k => !present.Contains(k) && !missing.Any(m => k.StartsWith(m + ":", StringComparison.Ordinal))).ToList())
            {
                db.Exec("DELETE FROM gallery_import_seen WHERE profile = $p AND path = $f", ("$p", p.Id), ("$f", gone));
                seen.Remove(gone);
            }
            groups = groups.Where(x => x.Group.All.Any(f => !seen.TryGetValue(SeenKey(x.Group.Mount, f), out var e) || e != f.Etag)).ToList();
            total = groups.Count;
            if (groups.Count == 0) return 0;

            if (!locs.TryGetValue(p.Mount, out var target))
            {
                var (ext, why) = await OpenExtAsync(space, config, p.Mount, write: true);
                if (ext == null) throw new TargetUnavailable($"Der Zielordner ist {why}.");
                locs[p.Mount] = target = ext;
            }
            // {sequence}: laufende Nummer je Lauf, wie im CameraImporter je Import
            var sequence = 0;
            foreach (var (from, g) in groups)
            {
                if (handled >= MaxPerRun) break;
                ct.ThrowIfCancellationRequested();
                handled++;
                progress?.Invoke(handled, Math.Min(groups.Count, MaxPerRun));
                try
                {
                    Facts facts;
                    await using (var s = await from.HeadAsync(g.Primary, ct)) facts = Read(s, g.Stem, g.Earliest, zone);
                    if (p.Since is { } since && DateOnly.FromDateTime(facts.Taken) < since) { Remember(db, p.Id, g); continue; }
                    var next = sequence + 1;
                    var folder = StoragePath.Join(p.Target, RenderFolder(p.Folders, facts, import, next, culture));
                    var name = RenderName(p.Names, facts, import, next, culture);
                    var title = RenderText(p.Title, facts, import, next, culture);
                    var description = RenderText(p.Description, facts, import, next, culture);
                    var fields = title.Length > 0 || description.Length > 0
                        ? new PhotoMeta.Fields(Title: title.Length > 0 ? title : null, Description: description.Length > 0 ? description : null) : null;
                    // Ein Muster, das zurück in einen Quellordner führt, sortierte dieselben Fotos immer wieder um
                    if (p.Sources.Any(src => src.Mount == p.Mount && (src.Path.Length == 0 || folder == src.Path || FileStore.InRoot(folder, src.Path))))
                        throw new InvalidOperationException("Der Zielordner liegt in einem Quellordner.");
                    // In einen externen Ordner, der Galerie-Ordner ist: gleich mit Bildangaben und Vorschaubild in die Galerie, aus der
                    // Datei, die wir gerade in der Hand haben - sonst läse das Einlesen sie über das Netz noch einmal
                    Func<Item, string, byte[]?, Task>? toGallery = target is ExtLoc toExt ? async (f, dest, data) =>
                    {
                        try
                        {
                            var sources = GalleryFolders.Of(db).Where(x => x.Mount == toExt.Mount && InGallery(dest, x.Path)).ToList();
                            if (sources.Count == 0) return;
                            if (data == null && f.Size <= MaxGalleryBytes) data = await ReadAllAsync(from, f.Path, ct);
                            if (data == null || await toExt.Driver.StatAsync(dest, ct) is not { IsDir: false } st) return;
                            foreach (var x in sources) GalleryFolders.Placed(space, config, x, dest, data, st.Size, st.Modified);
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { } // dann holt es das Einlesen nach
                    } : null;
                    var placed = await PlaceAsync(from, target, g, folder, name, p.Copy, fields, ct, toGallery);
                    if (p.Copy) Remember(db, p.Id, g);
                    if (placed)
                    {
                        sequence = next;
                        done++;
                    }
                    if (target is ExtLoc) ExternalFolders.Changed(space, p.Mount, folder);
                    if (from is ExtLoc && !p.Copy) ExternalFolders.Changed(space, g.Mount, g.Dir);
                }
                catch (Exception e) when (e is IOException or HttpRequestException && (target is ExtLoc || from is ExtLoc))
                {
                    // Freigabe weg: aufhören statt jede Datei einzeln scheitern zu lassen
                    throw new TargetUnavailable(target is ExtLoc ? "Der Zielordner ist gerade nicht erreichbar." : "Ein Quellordner ist gerade nicht erreichbar.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException
                    or DavConflictException or QuotaExceededException or Pim.PreconditionFailedException or InvalidOperationException)
                {
                    error = $"{g.Primary.Name}: {e.Message}";
                    if (e is QuotaExceededException) break;
                }
            }
        }
        finally
        {
            foreach (var l in locs.Values) l.Dispose();
            var pending = total - handled;
            var message = error ?? sourceError;
            Update(db, p.Id, x =>
            {
                x.LastRun = now;
                x.LastCount = done;
                x.Total += done;
                x.Pending = pending;
                x.Error = message;
            });
            if (done > 0) Arrived(db, p);
        }
        return done;
    }

    /// <summary>Größer wird nicht gleich in die Galerie eingetragen (die Datei stünde dafür ganz im Speicher) - das holt das Einlesen nach.</summary>
    private const long MaxGalleryBytes = 100L * 1024 * 1024;

    private static bool InGallery(string path, string root)
    {
        root = StoragePath.Clean(root);
        return root.Length == 0 || path.StartsWith(root + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Neues ist abgelegt: die Galerie gleich auf den neuen Stand bringen - eigene Ablage sofort (Zeitleiste neu bauen),
    /// externes Ziel über den Galerie-Ordner, in dem es liegt (gleich einlesen).
    /// </summary>
    private static void Arrived(UserDb db, ImportProfile p)
    {
        GalleryTimeline.Forget(db.UserId);
        if (p.Mount.Length == 0) return;
        var target = StoragePath.Clean(p.Target);
        foreach (var src in GalleryFolders.Of(db).Where(s => s.Mount == p.Mount))
        {
            var root = StoragePath.Clean(src.Path);
            if (root.Length == 0 || target == root || target.StartsWith(root + "/", StringComparison.Ordinal) || root.StartsWith(target + "/", StringComparison.Ordinal) || target.Length == 0)
                GalleryScanJob.Request(db.UserId, src.Id);
        }
    }

    /// <summary>Lange genug unverändert? Eigene Ablage: seit dem Hochladen; externer Ordner: seit Größe und Zeit gleich geblieben sind.</summary>
    private static bool Settled(string profile, string mount, Item f, DateTime now)
    {
        if (mount.Length == 0) return now - f.Created >= (Settle < SettleOwn ? Settle : SettleOwn);
        if (Settle <= TimeSpan.Zero) return true;
        var st = Stable.AddOrUpdate(profile + "|" + mount + ":" + f.Path, (f.Etag, now), (_, old) => old.Etag == f.Etag ? old : (f.Etag, now));
        return now - st.Since >= Settle;
    }

    /// <summary>
    /// Freie Endung für ein Foto am Ziel: „“, „_2“, „_3“ … - die erste, bei der jede Datei des Fotos frei ist oder dort schon mit
    /// gleichem Inhalt liegt. Null-Endung mit allen Fotos schon da: das Foto ist bereits einsortiert.
    /// </summary>
    /// <param name="same">Liegt am Ziel unter diesem Namen schon eine Datei: gleicher Inhalt wie das Foto (true), anderer (false); frei: null.</param>
    private static async Task<(string Name, bool Duplicate)> FreeAsync(Group g, string name, Func<Item, string, Task<bool?>> same)
    {
        for (var n = 1; n < 1000; n++)
        {
            var candidate = n == 1 ? name : $"{name}_{n}";
            var free = true;
            var allThere = true;
            foreach (var f in g.Photos)
            {
                var r = await same(f, TargetName(f, g.Stem, candidate));
                if (r == false) { free = false; break; }
                if (r == null) allThere = false;
            }
            if (free) return (candidate, allThere);
        }
        throw new InvalidOperationException("Kein freier Name am Ziel.");
    }

    /// <summary>
    /// Ein Foto von <paramref name="from"/> nach <paramref name="to"/>. Innerhalb der eigenen Ablage wird nur umbenannt (keine Kopie der
    /// Daten), innerhalb desselben externen Ordners verschoben; sonst gestreamt und am Ziel geprüft. Die Quelle wird erst gelöscht, wenn
    /// alles am Ziel liegt - in der eigenen Ablage in den Papierkorb, in einem externen Ordner endgültig, deshalb dort erst nach dem
    /// Vergleich der Prüfsummen.
    /// </summary>
    private static async Task<bool> PlaceAsync(Loc from, Loc to, Group g, string folder, string name, bool copy, PhotoMeta.Fields? fields, CancellationToken ct,
        Func<Item, string, byte[]?, Task>? placed = null)
    {
        var listing = await to.FilesAsync(folder, ct);
        var sha = new Dictionary<string, string?>();
        async Task<string?> Sha(Loc l, string path) => sha.TryGetValue(l.Mount + ":" + path, out var h) ? h : sha[l.Mount + ":" + path] = await l.ShaAsync(path, ct);
        var (final, duplicate) = await FreeAsync(g, name, async (src, target) =>
        {
            if (!listing.TryGetValue(target, out var e)) return null;
            return e.Size == src.Size && await Sha(to, e.Path) is { } a && a == await Sha(from, src.Path);
        });
        if (duplicate)
        {
            if (!copy) foreach (var f in g.All) await from.DeleteAsync(f.Path, ct);
            return false;
        }
        await to.MkdirAsync(folder, ct);
        var sameStore = from.Mount == to.Mount;
        var raws = g.Photos.Where(p => PhotoMeta.UsesSidecar(p.Name)).ToList();
        var done = new List<Item>();
        foreach (var f in g.All)
        {
            var target = TargetName(f, g.Stem, final);
            var isSidecar = Sidecar.Is(f.Name);
            // Foto liegt dort schon mit gleichem Inhalt (Teil eines Paares)
            if (!isSidecar && listing.ContainsKey(target)) { done.Add(f); continue; }
            // Sidecar-Datei gleichen Namens am Ziel: nicht überschreiben
            var dest = StoragePath.Join(folder, isSidecar && listing.ContainsKey(target) ? FreeSidecar(listing, target) : target);
            byte[]? data = null;
            if (fields != null && PhotoMeta.IsJpeg(f.Name) && f.Size <= MaxJpegBytes)
                data = PhotoMeta.WriteJpeg(await ReadAllAsync(from, f.Path, ct), fields);
            else if (fields != null && isSidecar && f.Size <= Sidecar.MaxBytes)
                data = Encoding.UTF8.GetBytes(PhotoMeta.WriteXmp(Encoding.UTF8.GetString(await ReadAllAsync(from, f.Path, ct)), fields));
            if (data == null && sameStore && from is OwnLoc own)
            {
                // Verschieben = umbenennen; die Kopie teilt sich den Inhalt mit dem Original
                FileStore.MoveOrCopy(own.Db, f.Path, dest, overwrite: false, copy);
                continue;
            }
            if (data == null && sameStore && !copy && from is ExtLoc ext)
            {
                await ext.Driver.MoveAsync(f.Path, dest, ct);
                continue;
            }
            string hash;
            long expected;
            if (data != null)
            {
                await to.WriteAsync(dest, new MemoryStream(data), f, ct);
                (hash, expected) = (FileStore.Hex(SHA256.HashData(data)), data.Length);
            }
            else
            {
                await using var s = await from.OpenAsync(f.Path, ct);
                await using var hs = new HashingStream(s);
                await to.WriteAsync(dest, hs, f, ct);
                (hash, expected) = (hs.Hex(), hs.Count);
            }
            if (await to.SizeAsync(dest, ct) != expected) throw new InvalidOperationException("Die Datei kam nicht vollständig am Ziel an.");
            // Endgültig gelöscht wird nur, was am Ziel nachweislich gleich angekommen ist
            if (!copy && from is ExtLoc && await to.ShaAsync(dest, ct) != hash) throw new InvalidOperationException("Die Datei kam nicht unverändert am Ziel an.");
            done.Add(f);
            if (placed != null && !isSidecar) await placed(f, dest, data);
        }
        if (fields != null && raws.Count > 0 && g.Sidecars.Count == 0)
        {
            var target = TargetName(raws[0], g.Stem, final) + ".xmp";
            if (!listing.ContainsKey(target))
                await to.WriteAsync(StoragePath.Join(folder, target), new MemoryStream(Encoding.UTF8.GetBytes(PhotoMeta.WriteXmp(null, fields))),
                    raws[0] with { Path = target, Mime = "application/rdf+xml" }, ct);
        }
        // Erst wenn alles am Ziel liegt: die Quelle löschen
        if (!copy) foreach (var f in done) await from.DeleteAsync(f.Path, ct);
        return true;
    }

    private static string FreeSidecar(Dictionary<string, Item> listing, string name)
    {
        var stem = name[..^4];
        for (var n = 2; ; n++) if (!listing.ContainsKey($"{stem} ({n}).xmp")) return $"{stem} ({n}).xmp";
    }

    private static async Task<byte[]> ReadAllAsync(Loc l, string path, CancellationToken ct)
    {
        await using var s = await l.OpenAsync(path, ct);
        var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>Reicht durch und rechnet dabei die Prüfsumme mit - so muss die Quelle nicht zweimal gelesen werden.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Count { get; private set; }
        public string Hex() => FileStore.Hex(_hash.GetHashAndReset());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.CanSeek ? inner.Length : throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Took(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            return Took(buffer.Span[..n]);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        private int Took(ReadOnlySpan<byte> data) { _hash.AppendData(data); Count += data.Length; return data.Length; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}

/// <summary>
/// Hintergrunddienst der Importprofile: prüft jede Minute, welche aktiven Profile fällig sind (Intervall je Profil), und lässt sie
/// nacheinander laufen - nie zwei zugleich. „Jetzt einsortieren“ stößt ihn sofort an, ebenso eine neue Datei in einem Quellordner
/// der eigenen Ablage (<see cref="FolderWatch"/>).
/// </summary>
public sealed class ImportProfileJob(Spaces spaces, ServerConfig config, ILogger<ImportProfileJob> log) : BackgroundService
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Wanted = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Running = new();
    private static readonly SemaphoreSlim Kick = new(0);

    public static void Request(string userId, string profileId) { Wanted[userId + "/" + profileId] = true; Kick.Release(); }
    public static bool IsRunning(string userId, string profileId) => Running.ContainsKey(userId + "/" + profileId) || Wanted.ContainsKey(userId + "/" + profileId);

    /// <summary>Name beim <see cref="FolderWatch"/>.</summary>
    public const string Watch = "gallery-import";
    /// <summary>So lange ohne weitere Datei im Quellordner, dann anstoßen - länger als <see cref="ImportProfiles.SettleOwn"/>.</summary>
    public static TimeSpan Quiet { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Quellordner in der eigenen Ablage sofort einsortieren, wenn dort eine Datei fertig gespeichert ist.</summary>
    public static void WatchFolders() => FolderWatch.Watch(new(Watch, Quiet,
        db => Features.On(Features.ImportProfiles) && Features.On(Features.Gallery)
            ? ImportProfiles.Of(db).Where(p => p.Active).SelectMany(p => p.Sources.Where(s => s.Mount.Length == 0).Select(s => (p.Id, s.Path)))
            : [],
        Request));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        WatchFolders();
        try { await Task.Delay(TimeSpan.FromSeconds(70), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Importprofile: Durchgang fehlgeschlagen"); }
            try { await Kick.WaitAsync(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!Features.On(Features.ImportProfiles) || !Features.On(Features.Gallery)) { Wanted.Clear(); return; }
        foreach (var space in spaces.All())
        {
            ct.ThrowIfCancellationRequested();
            if (FileStore.FilesOff(space.Db)) continue;
            var now = DateTime.UtcNow;
            foreach (var p in ImportProfiles.Of(space.Db))
            {
                var key = space.User.Id + "/" + p.Id;
                var wanted = Wanted.TryRemove(key, out _);
                // Fällig: angestoßen, oder aktiv und das Intervall um (noch offene Fotos vom letzten Lauf: gleich weiter)
                // Die eigene Ablage stößt FolderWatch bei jeder fertig gespeicherten Datei an, externe Ordner melden nichts.
                // Das Intervall gilt für beide: So fällt auch auf, was ohne Anstoß in den Ordner kam.
                var every = Math.Max(5, p.Every);
                var due = p.Active && (p.LastRun == null || p.Pending > 0 || now - p.LastRun.Value >= TimeSpan.FromMinutes(every));
                // Läuft gerade ein Stapel ein, kommt der Anstoß danach (sonst trennte der Lauf vielleicht RAW und JPG)
                if (!wanted && (!due || FolderWatch.IsPending(Watch, space.User.Id, p.Id))) continue;
                Running[key] = true;
                using var job = Activity.Start(space.User.Id, Activity.GalleryImport, p.Name);
                try
                {
                    int n;
                    using (FolderWatch.As(Watch)) n = await ImportProfiles.RunAsync(space, config, p.Id, ct, (d, t) => job.Progress(d, t, p.Name));
                    if (n > 0) log.LogInformation("Importprofil ({User}): {Count} Fotos einsortiert", space.User.Login, n);
                }
                catch (ImportProfiles.TargetUnavailable e)
                {
                    // Bis zum nächsten Intervall warten, nicht jede Minute neu anklopfen
                    ImportProfiles.Update(space.Db, p.Id, x => { x.LastRun = now; x.Error = e.Message; x.Pending = 0; });
                    job.Fail($"{p.Name}: {e.Message}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    ImportProfiles.Update(space.Db, p.Id, x => { x.LastRun = now; x.Error = e.Message; });
                    job.Fail($"{p.Name}: {e.Message}");
                    log.LogWarning(e, "Importprofil ({User})", space.User.Login);
                }
                finally { Running.TryRemove(key, out _); }
            }
        }
    }
}
