using System.Text;
using DmOffice.Core;
using DmOffice.Search;

namespace DmOffice.Dav;

/// <summary>Die WebDAV-Dateien in der Weboberflaeche: ansehen, hochladen, ordnen, Papierkorb, Versionen, Freigaben.</summary>
public static partial class FilesApi
{
    public record PathBody(string Path);
    public record PathsBody(string? Path, string[]? Paths);
    public record MoveBody(string From, string To, bool Copy, string? Conflict);
    public record VaultUnlockBody(string Path, string Password);
    public record IdsBody(long[] Ids);
    public record FavoriteBody(string Path, bool On);
    public record DavHiddenBody(string Path, bool Hidden);
    public record SaveBody(string Path, string Text, string? Etag, bool Create);
    /// <summary>Foto drehen (0/90/180/270) und zuschneiden; Overwrite: ersetzen (in der Ablage bleibt die vorige Fassung als Version), sonst Kopie „…_bearbeitet.jpg“.</summary>
    public record PhotoEditBody(string Path, int Rotate, PhotoEdit.Crop? Crop, bool Overwrite, PhotoAdjust.Values? Adjust = null);
    public record PhotoAdjustBody(string Path, PhotoAdjust.Values? Adjust);

    /// <summary>Inhalt eines Fotos: eigene Ablage oder Galerie-Ordner („ext:…“).</summary>
    private static async Task<byte[]> PhotoBytesAsync(HttpContext http, ServerConfig config, string path, CancellationToken ct)
    {
        // Galerie-Ordner: über die Arbeitskopie (vorab geholt, sobald der Editor aufgeht)
        if (path.StartsWith("ext:", StringComparison.Ordinal)) return await PhotoWork.GetAsync(http.Space(), config, path, ct);
        // Tresor (nur mit offenem Tresor auf diesem Gerät)
        if (TresorPaths.IdOfAny(http, path) is { } tresorId) return Tresor.Db(http).Files.ReadAll(tresorId);
        var loc = Loc(http, path);
        return FileStore.Data(loc.Db, loc.Path) ?? throw new KeyNotFoundException("Datei nicht gefunden.");
    }

    /// <summary>Verkleinerte Fassungen für die Vorschau der Regler (je Benutzer und Foto, die letzten acht) - nicht jedes Mal das Original lesen.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, byte[] Small)> PreviewCache = new();

    private static async Task<SkiaSharp.SKBitmap> PreviewSourceAsync(HttpContext http, ServerConfig config, string path, CancellationToken ct)
    {
        // Tresor: erst prüfen, ob er auf diesem Gerät offen ist - sonst käme die gemerkte Fassung auch geschlossen heraus
        if (TresorPaths.Is(path)) Tresor.Db(http);
        var key = http.Space().User.Id + "|" + path;
        if (!PreviewCache.TryGetValue(key, out var hit) || DateTime.UtcNow - hit.At > TimeSpan.FromMinutes(15))
        {
            using var small = PhotoAdjust.Small(await PhotoBytesAsync(http, config, path, ct), 1400);
            hit = (DateTime.UtcNow, PhotoAdjust.Jpeg(small, 95));
            PreviewCache[key] = hit;
            foreach (var old in PreviewCache.OrderByDescending(x => x.Value.At).Skip(8).Select(x => x.Key).ToList()) PreviewCache.TryRemove(old, out _);
        }
        return SkiaSharp.SKBitmap.Decode(hit.Small);
    }

    /// <summary>
    /// Drehen, Zuschnitt und Regler anwenden: mit Photo-Engine rechnet sie die Regler (darauf sind ihre Werte abgestimmt, Metadaten
    /// bleiben), Drehen und Zuschnitt danach hier; ohne Engine (oder wenn sie nicht antwortet) alles hier.
    /// </summary>
    private static async Task<byte[]> EditAsync(byte[] data, PhotoEditBody body, ILogger log, CancellationToken ct, bool raw = false)
    {
        // RAW entwickelt nur die Engine (auch ohne Regler) - ein JPG in voller Größe, aufrecht, mit den Aufnahmedaten
        if (raw)
            return PhotoEdit.Apply(await PhotoEngine.DevelopAsync(data, (body.Adjust ?? Neutral).Clamped(), null, PhotoEdit.Quality, log, ct)
                ?? throw new InvalidOperationException(RawNeedsEngine), body.Rotate, body.Crop);
        if (body.Adjust is { IsNeutral: false } a && await PhotoEngine.DevelopAsync(data, a.Clamped(), null, PhotoEdit.Quality, log, ct) is { } developed)
            // Die Engine liefert das Bild ohne Metadaten - Aufnahmezeit, Kamera, Ort usw. aus dem Original übernehmen
            return PhotoEdit.Apply(developed, body.Rotate, body.Crop, meta: data);
        return PhotoEdit.Apply(data, body.Rotate, body.Crop, body.Adjust);
    }

    private static readonly PhotoAdjust.Values Neutral = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private const string RawNeedsEngine = "RAW-Dateien lassen sich nur mit der Photo-Engine bearbeiten - sie antwortet gerade nicht.";
    /// <summary>RAW (.raf, .arw …): entwickelt die Engine; gespeichert wird immer als neues JPG daneben.</summary>
    private static bool IsRaw(string path) => Thumbs.IsRaw(path);
    private static long MaxPhoto(string path) => (IsRaw(path) ? 200L : 80L) * 1024 * 1024;

    /// <summary>Vorschlag der Photo-Engine: RAW ganz (sie liest die Aufnahmedaten selbst), sonst verkleinert mit den Aufnahmedaten.</summary>
    private static async Task<(PhotoAdjust.Values Values, string? Analysis)?> EngineValuesAsync(byte[] bytes, string path, TimeZoneInfo zone, ILogger log, CancellationToken ct)
    {
        if (IsRaw(path)) return await PhotoEngine.AnalyzeAsync(bytes, null, log, ct);
        using var small = PhotoAdjust.Small(bytes, 1600);
        var meta = FileMeta.Read(path, new MemoryStream(bytes), zone);
        var exif = meta == null ? null : System.Text.Json.JsonSerializer.Serialize(new
        {
            iso = meta.Iso, exposure_time = meta.Exposure, f_number = meta.Aperture, focal_length = meta.Focal, model = meta.Camera,
            // Ortszeit der Aufnahme (Abendlicht: die Engine lässt dann die Wärme)
            datetime = meta.Taken is long ms ? TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, zone).ToString("yyyy:MM:dd HH:mm:ss") : null,
        });
        return await PhotoEngine.AnalyzeAsync(PhotoAdjust.Jpeg(small, 92), exif, log, ct);
    }

    /// <summary>Vorschlag der KI: Vorschau, Aufnahmedaten und Kennzahlen hin, Reglerwerte zurück (RAW: die neutrale Entwicklung der Engine als Bild).</summary>
    private static async Task<(PhotoAdjust.Values Values, string? Analysis)> AiValuesAsync(ServerConfig config, byte[] bytes, string path, TimeZoneInfo zone, ILogger log, CancellationToken ct)
    {
        if (IsRaw(path)) bytes = await PhotoEngine.DevelopAsync(bytes, Neutral, 1500, 92, log, ct) ?? throw new InvalidOperationException(RawNeedsEngine);
        using var small = PhotoAdjust.Small(bytes, 1500);
        var st = PhotoAdjust.Measure(small);
        var meta = FileMeta.Read(path, new MemoryStream(bytes), zone);
        var exif = meta == null ? "unbekannt" : string.Join(", ", new string?[] { meta.Camera, meta.Exposure is double ex ? (ex < 1 ? "1/" + Math.Round(1 / ex) : ex.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)) + " s" : null, meta.Aperture is double ap ? "f/" + ap.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : null,
            meta.Iso is int iso ? "ISO " + iso : null, meta.Focal is double fo ? fo.ToString("0") + " mm" : null }.Where(x => !string.IsNullOrEmpty(x)));
        var prompt = AdjustPrompt + "\n\nAufnahmedaten: " + exif + "\nKennzahlen (Helligkeit 0–255): Mittelwert " + st.Mean + ", Median " + st.Median +
            $", P1={st.P1}, P5={st.P5}, P95={st.P95}, P99={st.P99}, Anteil ≥250: {st.ClipHigh} %, Anteil ≤5: {st.ClipLow} %, Mittelwert Bildmitte: {st.CenterMean}";
        var answer = await DmOffice.Chat.PukkiAi.See(config, prompt, PhotoAdjust.Jpeg(small, 85), "image/jpeg", log, ct)
            ?? throw new InvalidOperationException("Die KI konnte das Foto gerade nicht beurteilen.");
        var m = System.Text.RegularExpressions.Regex.Match(answer, @"\{[\s\S]*\}");
        if (!m.Success) throw new InvalidOperationException("Die KI hat keine Werte geliefert.");
        using var doc = System.Text.Json.JsonDocument.Parse(m.Value);
        double V(string n) => doc.RootElement.TryGetProperty(n, out var e) && e.TryGetDouble(out var d) ? d : 0;
        var values = new PhotoAdjust.Values(V("exposure"), V("contrast"), V("highlights"), V("shadows"), V("whites"), V("blacks"),
            V("temperature"), V("tint"), V("vibrance"), V("saturation")).Clamped();
        var analysis = doc.RootElement.TryGetProperty("analysis", out var an) && an.ValueKind == System.Text.Json.JsonValueKind.String ? an.GetString() : null;
        return (values, analysis);
    }

    public record PhotoAutoBody(List<string> Paths, string Source);
    public record PhotoEditsBody(string Path, int Rotate, PhotoEdit.Crop? Crop, PhotoAdjust.Values? Adjust, string? Origin);

    /// <summary>
    /// Wo die Einstellungen eines Fotos liegen: eigene Ablage (auch geteilte Ordner) in der Datenbank des Besitzers an der Datei-Kennung,
    /// Galerie-Ordner in der eigenen an Quelle und Pfad. Setzen braucht bei geteilten Ordnern das Recht zu ändern.
    /// </summary>
    private static (UserDb Db, string Key) EditKey(HttpContext http, ServerConfig config, string path, bool write)
    {
        // Im Tresor gibt es keine gemerkten Einstellungen in der Benutzerdatenbank - dort wird direkt gespeichert
        if (TresorPaths.Is(path)) throw new ArgumentException("Im Tresor wird die Bearbeitung direkt in die Datei gespeichert.");
        if (path.StartsWith("ext:", StringComparison.Ordinal))
        {
            DmOffice.Gallery.GalleryFolders.Resolve(http.Space(), config, path[4..]);   // gibt es den Ordner (noch)?
            return (http.Space().Db, PhotoEdits.KeyOfExt(path[4..]));
        }
        var loc = write ? Loc(http, path).Writable() : Loc(http, path);
        var f = FileStore.Get(loc.Db, loc.Path) ?? throw new KeyNotFoundException("Datei nicht gefunden.");
        return (loc.Db, PhotoEdits.KeyOf(f));
    }

    /// <summary>
    /// Der Auftrag zu „automatisch verbessern“: je Foto Vorschlag holen und als Entwicklungseinstellungen merken (PhotoEdits) - das Original
    /// bleibt, rückgängig jederzeit. Ein Fehler bei einem Foto hält die anderen nicht auf.
    /// </summary>
    private static async Task AutoAsync(UserSpace space, ServerConfig config, List<(string Path, FileLoc? Loc)> jobs, string source, ILogger log)
    {
        using var job = Activity.Start(space.User.Id, Activity.PhotoAuto, source == "ai" ? "KI" : "Engine", jobs.Count);
        int done = 0, changed = 0, failed = 0;
        string? lastError = null;
        foreach (var (path, loc) in jobs)
        {
            try
            {
                var bytes = loc == null ? await PhotoWork.GetAsync(space, config, path, CancellationToken.None)
                    : FileStore.Data(loc.Db, loc.Path) ?? throw new KeyNotFoundException("Datei nicht gefunden.");
                if (bytes.Length > MaxPhoto(path)) throw new ArgumentException("Das Foto ist zu groß.");
                var values = source == "ai" ? (await AiValuesAsync(config, bytes, path, space.Zone, log, CancellationToken.None)).Values
                    : (await EngineValuesAsync(bytes, path, space.Zone, log, CancellationToken.None) ?? throw new InvalidOperationException("Die Photo-Engine antwortet gerade nicht.")).Values;
                // Nur Einstellungen (nicht-destruktiv): Drehen und Ausschnitt einer früheren Bearbeitung bleiben, die Regler kommen neu
                var (db, key) = loc == null ? (space.Db, PhotoEdits.KeyOfExt(path[4..]))
                    : (loc.Db, PhotoEdits.KeyOf(FileStore.Get(loc.Db, loc.Path) ?? throw new KeyNotFoundException("Datei nicht gefunden.")));
                var before = PhotoEdits.Get(db, key);
                if (values.IsNeutral && before == null) { job.Progress(++done, jobs.Count); continue; }
                PhotoEdits.Set(db, key, new PhotoEdits.Edit(before?.Rotate ?? 0, before?.Crop, values, source, 0));
                changed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException or InvalidOperationException
                or HttpRequestException or QuotaExceededException or System.Text.Json.JsonException)
            {
                failed++; lastError = path.Split('/').Last() + ": " + e.Message;
                log.LogWarning("Automatisch verbessern {Path}: {Error}", path, e.Message);
            }
            job.Progress(++done, jobs.Count, $"{changed} verbessert" + (failed > 0 ? $", {failed} mit Fehler" : ""));
        }
        if (failed > 0) job.Fail($"{failed} von {jobs.Count} Fotos nicht verbessert - zuletzt {lastError}");
    }

    /// <summary>Anweisung für den Vorschlag der KI (nach Vorlage): natürlich, eher zu wenig als zu viel, nur JSON.</summary>
    private const string AdjustPrompt =
        "Du bist ein erfahrener Fotograf und Bildbearbeiter. Du bekommst ein JPG (verkleinerte Vorschau), dessen Aufnahmedaten und gemessene " +
        "Helligkeits-Kennzahlen. Bestimme Korrekturwerte, die das Foto natürlich und ausgewogen wirken lassen - wie eine sorgfältige manuelle " +
        "Bearbeitung, nicht wie ein Filter. Grundsätze: Natürlichkeit vor Effekt, die Stimmung (Tageszeit, Licht) bleibt; kein HDR-Look, keine " +
        "übersättigten Farben. Das Hauptmotiv muss gut erkennbar sein, Hauttöne natürlich. Lichter nicht ausbrennen, Schatten nicht absaufen lassen - " +
        "richte dich dafür nach den Kennzahlen, nicht nur nach dem Eindruck. Es ist ein JPG: Schatten nur maßvoll aufhellen. Lieber zu wenig als " +
        "zu viel; ein gutes Foto bekommt nur minimale oder keine Korrekturen. Antworte NUR mit einem JSON-Objekt mit diesen Feldern: " +
        "\"analysis\" (ein kurzer Satz auf Deutsch: Motiv, Licht, wichtigstes Problem), \"exposure\" (Blenden, -1 bis 1), \"contrast\", \"highlights\", " +
        "\"shadows\" (je -60 bis 60), \"whites\", \"blacks\", \"temperature\", \"tint\", \"vibrance\", \"saturation\" (je -50 bis 50). 0 heißt unverändert.";

    /// <summary>Freier Name für die Kopie: „foto_bearbeitet.jpg“, dann „foto_bearbeitet 2.jpg“ …</summary>
    private static async Task<string> FreeNameAsync(string path, Func<string, Task<bool>> exists)
    {
        var dir = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = dir + name + "_bearbeitet" + (i > 1 ? " " + i : "") + ext;
            if (!await exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Kein freier Name gefunden.");
    }
    public record VersionBody(string Path, long Id);
    public record HashItem(string Sha256, long Size);
    public record HashesBody(HashItem[] Items, string? Dir = null);
    public record TrashSettings(int Days);
    /// <summary>Hoechstwerte der Verwaltung: Ablauf spaetestens nach so vielen Tagen, hoechstens so viele Abrufe.</summary>
    private static (DateTime? Expires, int? MaxDownloads) LinkLimits(LinkBody body)
    {
        var l = FileRetention.LimitsOf();
        var expires = body.Expires;
        if (l.LinkDays > 0)
        {
            var latest = DateTime.UtcNow.AddDays(l.LinkDays);
            if (expires == null || expires.Value.ToUniversalTime() > latest) expires = latest;
        }
        return (expires, l.LinkMaxDownloads > 0 ? FileRetention.Cap(body.MaxDownloads ?? 0, l.LinkMaxDownloads) : body.MaxDownloads);
    }

    public record LinkBody(string? Path, string? Mode, DateTime? Expires, string? Password, int? MaxDownloads = null);

    public record ShareBody(string Path, string[] Members, string[]? ReadOnly, string[]? ShowOnly = null);
    public record LeaveBody(int Id);

    /// <summary>
    /// Pfad aus der Anfrage aufloesen: eigener Ordner, Ordner in einem mit mir geteilten Ordner (dann in der Ablage des Besitzers)
    /// oder „Mit mir geteilt“ selbst. Der Abgleichordner von notizi ist ein geschuetzter Bereich: in der Dateiablage unsichtbar
    /// und unveraenderlich - nur notizi selbst (ueber /dav) und die Notizen von dmOffice greifen darauf zu.
    /// </summary>
    /// <summary>Grenze für ZIP-Downloads in Byte (0 = keine).</summary>
    internal static long ZipLimit(ServerConfig config) => ZipLimitOf?.Invoke() ?? config.Current.FileLimits.ZipMb * 1024L * 1024;
    /// <summary>Für Tests: Grenze in Byte statt aus der Verwaltung.</summary>
    internal static Func<long>? ZipLimitOf;

    /// <summary>Größe der Auswahl für ein ZIP (Inhalte, nicht gepackt) - Tresor, eigener Cryptomator-Tresor oder Ablage.</summary>
    private static long ZipSize(HttpContext http, List<string> paths)
    {
        long n = 0;
        foreach (var p in paths)
        {
            if (TresorPaths.Is(p)) n += TresorPaths.SizeOf(http, p);
            else if (CryptomatorPaths.Is(p)) n += CryptomatorPaths.SizeOf(http, p);
            else if (Loc(http, p) is { Virtual: false } l && FileStore.Get(l.Db, l.Path) is { } f) n += FileStore.TreeSize(l.Db, f);
        }
        return n;
    }

    internal static FileLoc Loc(HttpContext http, string? path)
    {
        var loc = SharedFiles.Resolve(http, FileStore.Normalize(path ?? ""));
        if (loc.Protected(loc.Path)) throw new KeyNotFoundException("Ordner nicht gefunden.");
        return loc;
    }

    /// <summary>Wie <see cref="Loc"/>, fuer Loeschen und Verschieben: ein Ordner, in dem der notizi-Ordner liegt, wuerde ihn mitnehmen.</summary>
    private static FileLoc LocTree(HttpContext http, string? path)
    {
        var loc = Loc(http, path);
        if (FileStore.Contains(loc.Path, loc.Space.Settings.Notizi.Root) && !loc.Virtual)
            throw new InvalidOperationException("Dieser Ordner enthält den geschützten notizi-Abgleichordner und lässt sich nicht verschieben oder löschen.");
        return loc;
    }

    /// <summary>Datei oder Ordner am Ort, sonst 404.</summary>
    private static FileEntry Entry(HttpContext http, FileLoc loc) =>
        loc.Virtual ? SharedFiles.RootEntry(SharedFiles.Mounts(http)) : FileStore.Get(loc.Db, loc.Path) ?? throw new KeyNotFoundException("Nicht gefunden.");

    /// <summary>Kennzeichen fuer die Oberflaeche: Favoriten, Links, Personenfreigaben - nur in der eigenen Ablage.</summary>
    /// <param name="Edits">Bearbeitete Fotos (PhotoEdits): Schlüssel → Fingerabdruck - hängt an den Etag, damit die Vorschaubilder neu kommen</param>
    /// <param name="DavHidden">Für WebDAV und Nextcloud ausgeblendete Ordner (samt dmVAULT, wenn der Tresor dort nicht bereitsteht)</param>
    internal sealed record Marks(HashSet<long> Favorites, HashSet<long> Links, HashSet<long> People, Dictionary<string, string>? Edits = null, HashSet<long>? DavHidden = null)
    {
        public static readonly Marks None = new([], [], []);
    }

    internal static Marks MarksOf(HttpContext http, FileLoc loc)
    {
        if (loc.Shared || loc.Virtual) return Marks.None;
        var me = http.Space();
        var hidden = DmOffice.Dav.DavHidden.Ids(me.Db);
        if (!Tresor.DavEnabled(me.User.Id) && FileStore.Get(me.Db, FileStore.SystemVault) is { } vault) hidden.Add(vault.FileId);
        return new Marks(FileStore.FavoriteIds(me.Db), FileStore.SharedIds(me.Db),
            http.RequestServices.GetRequiredService<FolderShareStore>().All().Where(s => s.Owner == me.User.Id).Select(s => s.Folder).ToHashSet(),
            PhotoEdits.Fingerprints(me.Db), hidden);
    }

    /// <summary>Was die Oberflaeche fuer eine Datei braucht. f: wie der Benutzer sie sieht (Pfad und fileid umgerechnet).</summary>
    internal static object Item(FileEntry f, Marks? marks = null, FileMount? mount = null, string? snippet = null) => new
    {
        f.Path, f.Name, f.IsDir, f.Size, f.Mime, f.Modified, f.Created, f.FileId, Etag = PhotoEdits.Tag(f.Etag, marks?.Edits?.GetValueOrDefault(PhotoEdits.KeyOf(f))),
        favorite = marks?.Favorites.Contains(f.FileId) == true,
        shared = marks?.Links.Contains(f.FileId) == true,
        // Mit Personen geteilt (Besitzer) bzw. ein mit mir geteilter Ordner in „Mit mir geteilt“
        sharedWith = marks?.People.Contains(f.FileId) == true,
        // Für WebDAV und Nextcloud ausgeblendet (nur der markierte Ordner selbst)
        davHidden = f.IsDir && marks?.DavHidden?.Contains(f.FileId) == true ? true : (bool?)null,
        owner = mount?.Owner.User.DisplayName,
        canWrite = mount?.CanWrite,
        showOnly = mount?.ShowOnly == true ? true : (bool?)null,
        // Suche: im Inhalt gefunden (Volltextsuche), Ausschnitt mit U+E000/U+E001 um die Treffer
        snippet,
    };

    /// <summary>Eintraege eines Ordners, wie der Benutzer sie sieht.</summary>
    private static IEnumerable<object> List(HttpContext http, FileLoc loc)
    {
        if (loc.Virtual) return SharedFiles.Mounts(http).Select(m => Item(SharedFiles.MountEntry(m), null, m));
        var marks = MarksOf(http, loc);
        var items = FileStore.Children(loc.Db, loc.Path).Where(f => !loc.Protected(f.Path) && !(!loc.Shared && SharedFiles.Hidden(http, f.Path)))
            .Select(f => Item(loc.Show(f), marks));
        // In der Wurzel steht "Mit mir geteilt" wie ein Ordner (Programme und die Handy-App zeigen es so ohne Aenderung), solange jemand etwas teilt
        if (!loc.Shared && loc.Path.Length == 0 && SharedFiles.Mounts(http) is { Count: > 0 } mounts)
            items = items.Append(Item(SharedFiles.RootEntry(mounts)));
        return items;
    }

    /// <summary>Datei oder Ordner am Ort (nicht „Mit mir geteilt“ selbst), sonst 404.</summary>
    private static FileEntry FileAt(FileLoc loc) =>
        (loc.Virtual ? null : FileStore.Get(loc.Db, loc.Path)) ?? throw new KeyNotFoundException("Nicht gefunden.");

    /// <summary>Ein Ort in der eigenen Ablage - Teilen und Links sind Sache des Besitzers.</summary>
    private static FileLoc Own(HttpContext http, string path)
    {
        var loc = Loc(http, path);
        return loc.Shared || loc.Virtual ? throw new UnauthorizedAccessException("Teilen kann nur der Besitzer.") : loc;
    }

    /// <summary>In der eigenen Ablage unsichtbar: der notizi-Ordner, der Systemordner des Tagebuchs und ein eigener Eintrag, den „Mit mir geteilt“ verdeckt.</summary>
    private static bool Protected(HttpContext http, string path) =>
        FileStore.InRoot(path, http.Space().Settings.Notizi.Root) || FileStore.InRoot(path, DmOffice.Journal.JournalMediaStore.Root) || FileStore.InRoot(path, FileStore.SystemVault) || SharedFiles.Under(http, path);

    /// <summary>Mit wem ein eigener Ordner geteilt ist.</summary>
    private static IEnumerable<object> People(HttpContext http, long folder) =>
        http.RequestServices.GetRequiredService<FolderShareStore>().Of(http.Space().User.Id, folder) is { } s ? People(http, s) : [];

    private static IEnumerable<object> People(HttpContext http, FolderShare s)
    {
        var users = http.RequestServices.GetRequiredService<UserStore>();
        return s.Members.Select(m => users.ById(m)).OfType<UserRecord>()
            .Select(u => (object)new { u.Id, u.Login, u.DisplayName, readOnly = s.ReadOnly.Contains(u.Id), level = s.LevelOf(u.Id) }).ToList();
    }


    /// <summary>Nur Bilder, PDF, Ton und Video im Browser zeigen - nie HTML oder SVG aus der Ablage unter dieser Adresse.</summary>
    public static bool Inline(string mime) =>
        mime.StartsWith("image/") && mime != "image/svg+xml" || mime == "application/pdf" || mime.StartsWith("audio/") || mime.StartsWith("video/");

    /// <summary>Gezeigte Inhalte als Dokument: ohne Skripte und ohne Zugriff auf die Seite.</summary>
    public const string SandboxCsp = "sandbox; default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'";

    /// <summary>
    /// Typ und Download-Name für eine ausgelieferte Datei, deren Typ von außen kommen kann (Upload, Mail-Anhang, Tresor):
    /// gezeigt nur Bild, PDF, Ton, Video oder reiner Text, mit Sandbox; alles andere (HTML, SVG, Skript …) als Datei zum Speichern.
    /// </summary>
    public static (string Mime, string? Download) Shown(HttpContext http, string? mime, string name, bool inline)
    {
        var m = mime is { Length: > 0 } x && x != "application/octet-stream" ? x : DavServer.Mime(name);
        var show = inline && (Inline(m) || m.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase));
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (show && m != "application/pdf") http.Response.Headers["Content-Security-Policy"] = SandboxCsp;
        return show ? (m, null) : ("application/octet-stream", name);
    }

    public static string MimeOf(FileEntry f) => f.Mime is { Length: > 0 } m && m != "application/octet-stream" ? m : DavServer.Mime(f.Path);

    /// <summary>
    /// Nur gezeigt (Version 2): ausliefern nur, was der Browser selbst anzeigt (Bild, PDF, Ton, Video) oder Text -
    /// alles andere kaeme als Datei zum Speichern heraus. "Nur zeigen" ist kein Kopierschutz, aber kein Download-Knopf.
    /// </summary>
    public static bool Viewable(FileEntry f) { var m = MimeOf(f); return Inline(m) || (m.StartsWith("text/") && m != "text/html"); }

    /// <summary>Datei ausliefern: aus der Datenbank gestreamt, mit Bereichen (Video spulen), ETag und Aenderungszeit.</summary>
    public static IResult Send(HttpContext http, UserDb db, FileEntry f, bool inline)
    {
        var mime = f.Mime is { Length: > 0 } m && m != "application/octet-stream" ? m : DavServer.Mime(f.Path);
        bool show = inline && Inline(mime);
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Bild, Ton, Video direkt geoeffnet: als Dokument ohne Skripte und ohne Zugriff auf die Seite
        if (show && mime != "application/pdf") http.Response.Headers["Content-Security-Policy"] = SandboxCsp;
        http.Response.Headers.CacheControl = "private, no-cache";
        return Results.Stream(FileStore.OpenRead(db, f.Path), show ? mime : "application/octet-stream", show ? null : f.Name,
            new DateTimeOffset(DateTime.SpecifyKind(f.Modified, DateTimeKind.Utc)), new Microsoft.Net.Http.Headers.EntityTagHeaderValue(f.Etag.StartsWith('"') ? f.Etag : $"\"{f.Etag}\""),
            enableRangeProcessing: true);
    }

    /// <summary>Vorschaubild ausliefern; mit ?v=ETag darf der Browser es behalten.</summary>
    public static async Task<IResult> SendThumb(HttpContext http, UserDb db, FileEntry f, int? size, string? v, bool original = false)
    {
        var data = await Thumbs.Get(db, f, size ?? Thumbs.Small, http.RequestAborted, original);
        if (data == null) return Results.NotFound();
        // Lange gültig nur, wenn der Etag (samt Fingerabdruck der Bearbeitung) zum Stand passt
        var edit = original ? null : PhotoEdits.Get(db, PhotoEdits.KeyOf(f));
        var tag = PhotoEdits.Tag(f.Etag, edit == null ? null : PhotoEdits.Fingerprint(edit));
        http.Response.Headers.CacheControl = v == tag || original && v == f.Etag ? "private, max-age=31536000, immutable" : "private, no-cache";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Bytes(data, "image/webp");
    }

    /// <summary>
    /// Eine hochgeladene Datei ablegen (auch fuer das Hochladen in Stuecken, Version 2): Unterordner anlegen, Namenskonflikt
    /// nach conflict, in einen geteilten Ordner beim Besitzer, Vorschaubild anstossen. Zurueck: Eintrag fuer die Antwort oder Fehler.
    /// </summary>
    private static async Task<(object? Entry, IResult? Error)> StoreOne(HttpContext http, string dir, string fileName, Stream body, string? contentType,
        string? conflict, DateTime? modified, CancellationToken ct)
    {
        // Mit Unterordnern (Ordner hochladen): "a/b/c.txt"
        var rel = FileStore.Normalize(fileName);
        if (rel.Length == 0) throw new ArgumentException("Dateiname fehlt.");
        var loc = Loc(http, dir.Length == 0 ? rel : dir + "/" + rel).Creatable();
        var db = loc.Db;
        var full = loc.Path;
        if (FileStore.Parent(full) is { Length: > 0 } parent) FileStore.EnsureDir(db, parent);
        if (FileStore.Get(db, full) is { } existing)
        {
            if (existing.IsDir || conflict == "rename") full = FileStore.FreeName(db, full);
            else if (conflict == "skip") return (new { path = loc.Show(full), skipped = true, sha256 = FileHashes.Of(db, full) }, null);
            else if (conflict == "fail") return (null, Results.Conflict(new { error = "Gibt es schon.", conflict = true, path = loc.Show(full) }));
        }
        try { await FileStore.PutAsync(db, full, body, string.IsNullOrEmpty(contentType) || contentType == "application/octet-stream" ? DavServer.Mime(full) : contentType, mtime: modified, ct: ct); }
        catch (QuotaExceededException ex) { return (null, Results.Json(new { error = loc.Shared ? $"Beim Besitzer ist kein Platz mehr frei. {ex.Message}" : ex.Message }, statusCode: 507)); }
        // Vorschaubild gleich im Hintergrund erzeugen - sonst wartet die Kachelansicht beim ersten Oeffnen
        if (FileStore.Get(db, full) is { } added && Thumbs.KindOf(added.Name, added.Mime) is Thumbs.Kind.Image or Thumbs.Kind.Pdf)
            _ = Task.Run(async () => { try { await Thumbs.Get(db, added, Thumbs.Small, CancellationToken.None); } catch (Exception) { /* beim Ansehen erneut */ } });
        // Pruefsumme des Geschriebenen: die App vergleicht sie mit ihrer eigenen, bevor sie die Datei als gesichert zaehlt
        return (new { path = loc.Show(full), skipped = false, sha256 = FileHashes.Of(db, full) }, null);
    }

    /// <summary>
    /// Kopie am zweiten Ort und naechtliche Sicherung fuer die Speicherplatz-Seite der Benutzer - ohne Adressen, Zugaenge
    /// und Fehlertexte (die stehen nur in der Verwaltung).
    /// </summary>
    private static object? CopyInfo(ServerConfig config, S3Mirror mirror, S3Backup backup)
    {
        var s = config.Current;
        if (!s.S3Mirror) return null;
        var m = mirror.Status;
        var b = backup.Status;
        var cache = S3Mirror.CacheMode(s) ? m.Cache : null;
        return new
        {
            target = s.StorageTarget,
            lastRun = m.LastRun,
            ok = m.LastError == null,
            cache = cache == null ? null : new { local = cache.Bytes, target = cache.Limit, max = cache.Max },
            backup = s.S3Backup ? new { lastRun = b.LastRun, ok = b.LastError == null, keepCount = BackupRules.KeepCount(s) } : null,
        };
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/files").RequireAuthorization();
        // Version 2: Hochladen in Stuecken (fortsetzbar, ohne Groessengrenze je Datei)
        MapChunked(api);
        // Vom Admin abgeschaltet: der ganze Bereich (der notizi-Abgleich laeuft ueber WebDAV weiter)
        api.AddEndpointFilter(async (ctx, next) =>
            FileStore.FilesOff(ctx.HttpContext.Space().Db)
                ? Results.Json(new { error = "Die Dateiablage ist für dieses Konto abgeschaltet." }, statusCode: 403)
                : await next(ctx));
        // Archive wie Ordner öffnen und ZIPs ändern (ArchiveApi.cs)
        MapArchive(api);

        api.MapGet("/", (string? path, HttpContext http) =>
        {
            // Tresor in der Dateiansicht (TresorPaths): „tresor:“ und darunter
            if (TresorPaths.Is(path)) return TresorPaths.List(http, path!);
            if (CryptomatorPaths.Is(path)) return CryptomatorPaths.List(http, path!);
            var db = http.Space().Db;
            var loc = Loc(http, path);
            var dir = Entry(http, loc);
            if (!dir.IsDir) throw new ArgumentException("Kein Ordner.");
            return new
            {
                path = loc.Show(loc.Path),
                usage = FileStore.Usage(db),
                trash = FileStore.TrashUsage(db),
                versions = FileStore.VersionUsage(db),
                quota = FileStore.Quota(db),
                // alles, was der Benutzer belegt (auch Mails, Kalender, Kontakte, Notizen) - das Kontingent gilt dafuer
                used = UserStorage.Used(db),
                favorite = !loc.Shared && !loc.Virtual && FileStore.FavoriteIds(db).Contains(dir.FileId),
                // In einem geteilten Ordner: wem er gehoert, ob ich darin schreiben darf, wo er anfaengt
                canWrite = loc.CanWrite,
                owner = loc.Mount?.Owner.User.DisplayName,
                mount = loc.Mount?.Path,
                showOnly = loc.ShowOnly,
                items = List(http, loc),
                // Eigene Cryptomator-Tresore (Ordner mit vault.cryptomator): darunter und ob dieser Ordner selbst einer ist
                vaults = loc.Shared || loc.Virtual ? [] : FileStore.Children(loc.Db, loc.Path).Where(f => f.IsDir && CryptomatorPaths.IsVault(loc.Db, f.Path)).Select(f => f.Path).ToArray(),
                // … und welche davon gerade offen sind (zum Schließen im Kontextmenü)
                openVaults = loc.Shared || loc.Virtual ? [] : FileStore.Children(loc.Db, loc.Path)
                    .Where(f => f.IsDir && CryptomatorPaths.IsVault(loc.Db, f.Path) && CryptomatorPaths.IsUnlocked(http, f.Path)).Select(f => f.Path).ToArray(),
                vault = !loc.Shared && !loc.Virtual && loc.Path.Length > 0 && CryptomatorPaths.IsVault(loc.Db, loc.Path)
                    ? new { unlocked = CryptomatorPaths.IsUnlocked(http, loc.Path) } : null,
            };
        });

        // Details: Groesse samt Inhalt, Anzahl, Versionen, Links, Personen
        // Ohne path (die Oberflaeche laesst den leeren Pfad weg): "Meine Dateien"
        api.MapGet("/info", (string? path, HttpContext http) =>
        {
            if (TresorPaths.Is(path)) return TresorPaths.Info(http, path!);
            if (CryptomatorPaths.Is(path)) return CryptomatorPaths.Info(http, path!);
            var loc = Loc(http, path);
            var db = loc.Db;
            // "Meine Dateien" hat keine eigene Zeile: Ordner mit der juengsten Aenderung darin
            var f = loc.Virtual ? Entry(http, loc) : FileStore.Get(db, loc.Path) ?? (loc.Path.Length == 0
                ? new FileEntry("", true, 0, "", null, DateTime.MinValue,
                    db.Scalar("SELECT MAX(modified) FROM files") is string m ? DateTime.Parse(m, null, System.Globalization.DateTimeStyles.RoundtripKind) : DateTime.UtcNow)
                : throw new KeyNotFoundException("Nicht gefunden."));
            var inside = loc.Virtual
                ? SharedFiles.Mounts(http).SelectMany(x => new[] { x.Dir }.Concat(FileStore.Descendants(x.Owner.Db, x.Dir.Path))).ToList()
                : f.IsDir ? FileStore.Descendants(db, loc.Path).Where(x => !loc.Protected(x.Path) && !(!loc.Shared && SharedFiles.Under(http, x.Path))).ToList() : [];
            var own = !loc.Shared && !loc.Virtual;
            return new
            {
                item = Item(loc.Show(f), MarksOf(http, loc), loc.IsMountRoot ? loc.Mount : null),
                sha256 = f.IsDir || loc.Virtual ? null : FileHashes.Of(db, loc.Path),
                size = f.IsDir ? inside.Sum(x => x.Size) : f.Size,
                files = inside.Count(x => !x.IsDir),
                folders = inside.Count(x => x.IsDir),
                versions = f.IsDir || loc.Virtual ? [] : FileStore.Versions(db, f.FileId),
                links = own ? Links.List(db, f.FileId) : [],
                people = own && f.IsDir ? People(http, f.FileId) : [],
                canWrite = loc.CanWrite,
                showOnly = loc.ShowOnly,
                owner = loc.Mount?.Owner.User.DisplayName,
            };
        });

        api.MapGet("/download", (string path, bool? inline, long? version, HttpContext http) =>
        {
            if (TresorPaths.Is(path)) return TresorPaths.Download(http, path, inline == true);
            if (CryptomatorPaths.Is(path)) return CryptomatorPaths.Download(http, path, inline == true);
            var loc = Loc(http, path);
            var f = FileAt(loc);
            if (f.IsDir) throw new ArgumentException("Ordner lassen sich nicht herunterladen.");
            // Nur gezeigt: ansehen im Browser ja, als Datei herunterladen und fruehere Staende nein
            if (inline != true || version != null || !Viewable(f)) loc.NotShowOnly();
            if (version is { } v)
            {
                var data = FileStore.OpenVersion(loc.Db, f.FileId, v) ?? throw new KeyNotFoundException("Version nicht gefunden.");
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.Stream(data, "application/octet-stream", f.Name, enableRangeProcessing: true);
            }
            // "Zuletzt verwendet": nur echtes Herunterladen - Anzeigen im Browser (Vorschau, Standbild eines Videos, Vorladen
            // im Betrachter) zählt nicht; was der Betrachter wirklich zeigt, meldet er über /used. Die Liste ist eigen,
            // die des Besitzers bleibt unberührt
            if (!loc.Shared && inline != true) FileStore.Used(loc.Db, f.FileId);
            return Send(http, loc.Db, f, inline == true);
        });

        // Im Betrachter angesehen (eine Weile auf dem Bildschirm) - für „Zuletzt verwendet“
        api.MapPost("/used", (PathBody body, HttpContext http) =>
        {
            if (TresorPaths.Is(body.Path) || CryptomatorPaths.Is(body.Path)) return Results.Ok();
            var loc = Loc(http, body.Path);
            if (!loc.Shared && FileStore.Get(loc.Db, loc.Path) is { IsDir: false } f) FileStore.Used(loc.Db, f.FileId);
            return Results.Ok();
        });

        // Alle Angaben in einer Bilddatei (Detailbereich → „Alle Angaben der Datei“)
        api.MapGet("/exif", (string path, HttpContext http) =>
        {
            var db = http.Space().Db;
            var f = FileStore.Get(db, FileStore.Normalize(path)) ?? throw new KeyNotFoundException("Nicht gefunden.");
            if (f.IsDir || Protected(http, f.Path)) throw new KeyNotFoundException("Nicht gefunden.");
            using var s = FileStore.OpenRead(db, f.Path);
            return Results.Ok(FileMeta.AllTags(s).Select(x => new { group = x.Group, name = x.Name, value = x.Value }));
        });

        // orig=1: das Original, auch wenn das Foto bearbeitet ist (der Editor arbeitet darauf)
        api.MapGet("/thumb", async (string path, int? size, string? v, string? orig, HttpContext http) =>
        {
            if (TresorPaths.Is(path)) return TresorPaths.Thumb(http, path, size);
            if (CryptomatorPaths.Is(path)) return CryptomatorPaths.Thumb(http, path, size);
            var loc = Loc(http, path);
            return await SendThumb(http, loc.Db, FileAt(loc), size, v, orig is "1" or "true");
        });

        // Standbild eines Videos aus dem Browser (der Server hat keinen Video-Decoder) - ein Vorschaubild, auch beim Lesen
        api.MapPut("/thumb", async (string path, HttpRequest request, HttpContext http) =>
        {
            // Ein Vorschaubild ist klein: nicht erst bis zu 512 MB in den Speicher lesen und dann ablehnen
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 8L * 1024 * 1024;
            var loc = Loc(http, path).Writable();
            var f = FileAt(loc);
            if (Thumbs.KindOf(f.Name, f.Mime) != Thumbs.Kind.Video) throw new ArgumentException("Nur für Videos.");
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, http.RequestAborted);
            if (ms.Length > 8L * 1024 * 1024) return Results.BadRequest(new { error = "Bild zu groß." });
            return Thumbs.Accept(loc.Db, f, ms.ToArray()) ? Results.Ok() : Results.BadRequest(new { error = "Kein Bild." });
        });

        // conflict: replace (Vorgabe, wie bisher), rename (Nummer anhaengen), skip, fail (409);
        // mtime: Aenderungszeit der Datei in ms seit 1970 (automatisches Hochladen der Handy-App), sonst jetzt
        // In einen geteilten Ordner: in die Ablage des Besitzers, sein Kontingent zaehlt
        api.MapPost("/upload", async (string? path, string? conflict, long? mtime, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            if (TresorPaths.Is(path)) return await TresorPaths.Upload(http, path!, conflict, request, ct);
            if (CryptomatorPaths.Is(path)) return await CryptomatorPaths.Upload(http, path!, conflict, request, ct);
            DateTime? modified = mtime is > 0 and < 253402300800000 ? DateTime.UnixEpoch.AddMilliseconds(mtime.Value) : null;
            var dir = FileStore.Normalize(path ?? "");
            var written = new List<object>();
            // Teil fuer Teil direkt in den BlobStore - kein Puffern des Formulars (das laege sonst unverschluesselt in /tmp)
            await foreach (var file in MultipartUpload.Files(request, ct))
            {
                var (entry, error) = await StoreOne(http, dir, file.FileName, file.Body, file.ContentType, conflict, modified, ct);
                if (error != null) return error;
                written.Add(entry!);
            }
            return Results.Ok(new { files = written });
        }).DisableAntiforgery();

        // Welche dieser Pfade gibt es schon (vor dem Hochladen fragen)
        // Liegen diese Inhalte (Pruefsumme + Groesse) schon irgendwo in der Ablage? Fuer die App: "schon gesichert"
        // unabhaengig von Name und Ordner, und Gegenprobe vor dem Freigeben von Platz auf dem Geraet.
        // pending: Dateien ohne Pruefsumme (noch nicht nachgerechnet) - was dort liegt, kann hier noch fehlen.
        // dir: nur direkt in diesem Ordner ("Duplikate ueberspringen" aus). Papierkorb, Versionen und notizi zaehlen nie.
        api.MapPost("/hashes", (HashesBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            if (body.Items is not { } items) throw new ArgumentException("Keine Prüfsummen.");
            if (items.Length > FileHashes.MaxLookup) throw new ArgumentException($"Höchstens {FileHashes.MaxLookup} Prüfsummen auf einmal.");
            var found = FileHashes.Lookup(db, items.Where(i => i.Sha256 != null).Select(i => (i.Sha256, i.Size)),
                body.Dir is { } d ? FileStore.Normalize(d) : null);
            return new
            {
                found = found.Select(x => new { sha256 = x.Key.Sha256, size = x.Key.Size, path = x.Value }),
                pending = FileHashes.PendingFiles(db),
            };
        });

        api.MapPost("/exists", (PathsBody body, HttpContext http) =>
        {
            var existing = new List<string>();
            foreach (var p in (body.Paths ?? []).Where(TresorPaths.Is).Distinct())
                if (TresorPaths.Exists(http, p)) existing.Add(p);
            foreach (var p in (body.Paths ?? []).Where(CryptomatorPaths.Is).Distinct())
                if (CryptomatorPaths.Exists(http, p)) existing.Add(p);
            foreach (var p in (body.Paths ?? []).Where(p => !TresorPaths.Is(p) && !CryptomatorPaths.Is(p)).Select(FileStore.Normalize).Distinct())
            {
                try { if (Loc(http, p) is var loc && (loc.Virtual || FileStore.Get(loc.Db, loc.Path) != null)) existing.Add(p); }
                catch (KeyNotFoundException) { /* geschuetzt oder kein solcher geteilter Ordner */ }
            }
            return new { existing };
        });

        // Eigene Cryptomator-Tresore (Stufe 3b): entsperren mit dem Passwort, sperren
        api.MapPost("/vault/unlock", (VaultUnlockBody body, HttpContext http) => CryptomatorPaths.Unlock(http, body.Path, body.Password));
        api.MapPost("/vault/lock", (PathBody body, HttpContext http) => { CryptomatorPaths.Lock(http, body.Path); return Results.Ok(); });

        api.MapPost("/mkdir", (PathBody body, HttpContext http) =>
        {
            if (TresorPaths.Is(body.Path)) return TresorPaths.Mkdir(http, body.Path);
            if (CryptomatorPaths.Is(body.Path)) return CryptomatorPaths.Mkdir(http, body.Path);
            var loc = Loc(http, body.Path).Creatable();
            FileStore.Mkcol(loc.Db, loc.Path);
            return Results.Ok(new { path = loc.Show(loc.Path) });
        });

        // Foto drehen und zuschneiden - Metadaten bleiben (PhotoEdit); eigene Ablage oder ein beschreibbarer Galerie-Ordner („ext:…“)
        api.MapPost("/photo-edit", async (PhotoEditBody body, HttpContext http, ServerConfig config, ILogger<PhotoEditBody> log, CancellationToken ct) =>
        {
            var max = MaxPhoto(body.Path);
            var raw = IsRaw(body.Path);
            if (raw && !PhotoEngine.Configured) return Results.Json(new { error = RawNeedsEngine }, statusCode: 503);
            // Tresor: bearbeitet zurück in den Tresor (überschreibend oder daneben)
            if (TresorPaths.IdOfAny(http, body.Path) is { } tresorId)
            {
                var tdb = Tresor.Db(http);
                var entry = tdb.Files.Find(tresorId) ?? throw new KeyNotFoundException("Diese Datei gibt es im Tresor nicht.");
                if (entry.Size > MaxPhoto(entry.Name)) throw new ArgumentException("Das Foto ist zu groß.");
                var result = await EditAsync(tdb.Files.ReadAll(tresorId), body, log, ct, IsRaw(entry.Name));
                return Results.Ok(new { path = TresorFiles.PathPrefix + await TresorFiles.SaveEditedAsync(tdb.Files, tresorId, result, body.Overwrite, ct) });
            }
            if (body.Path.StartsWith("ext:", StringComparison.Ordinal))
            {
                var space = http.Space();
                var (src, rel) = DmOffice.Gallery.GalleryFolders.Resolve(space, config, body.Path[4..]);
                var (driver, ro) = DmOffice.Storage.ExternalFolders.Open(space, config, src.Mount);
                using (driver)
                {
                    if (ro) return Results.Json(new { error = "Dieser Ordner ist nur zum Lesen eingebunden - das Foto lässt sich dort nicht speichern." }, statusCode: 403);
                    // Original aus der Arbeitskopie (meist schon da, seit der Editor aufging)
                    var data = await PhotoWork.GetAsync(space, config, body.Path, ct);
                    if (data.Length > max) throw new ArgumentException("Das Foto ist zu groß.");
                    var result = await EditAsync(data, body, log, ct, raw);
                    // RAW: immer als neues JPG daneben („DSCF1513_bearbeitet.jpg“)
                    var target = body.Overwrite && !raw ? rel : await FreeNameAsync(raw ? Path.ChangeExtension(rel, ".jpg") : rel, async p => await driver.StatAsync(p, ct) != null);
                    // Sofort in der Galerie, auf den Speicher im Hintergrund - das Warten auf den Tunnel entfällt
                    if (PhotoWork.Ready)
                    {
                        DmOffice.Gallery.GalleryFolders.Edited(space, config, src, rel, target, result, result.Length, DateTime.UtcNow);
                        PhotoWork.WriteLater(space, src.Id, rel, target, result);
                        return Results.Ok(new { path = "ext:" + src.Id + "/" + target, pending = true });
                    }
                    await driver.WriteAsync(target, new MemoryStream(result), ct);
                    // Gleich in die Galerie (Angaben vom Original, Vorschaubild aus dem Ergebnis); ohne Angaben vom Speicher den Ordner neu einlesen
                    // Meldet der Speicher noch den alten Stand (Zwischenspeicher eines Netzlaufwerks), gelten Größe und Zeit des Ergebnisses
                    if (await driver.StatAsync(target, ct) is { IsDir: false } st)
                        DmOffice.Gallery.GalleryFolders.Edited(space, config, src, rel, target, result, result.Length, st.Size == result.Length ? st.Modified : DateTime.UtcNow);
                    else DmOffice.Gallery.GalleryScanJob.Request(space.User.Id, src.Id);
                    return Results.Ok(new { path = "ext:" + src.Id + "/" + target });
                }
            }
            var loc = Loc(http, body.Path).Creatable();
            var db = loc.Db;
            var original = FileStore.Get(db, loc.Path) ?? throw new KeyNotFoundException("Datei nicht gefunden.");
            if (original.Size > max) throw new ArgumentException("Das Foto ist zu groß.");
            var bytes = FileStore.Data(db, loc.Path) ?? throw new KeyNotFoundException("Datei nicht gefunden.");
            var edited = await EditAsync(bytes, body, log, ct, raw);
            var to = body.Overwrite && !raw ? loc.Path : await FreeNameAsync(raw ? Path.ChangeExtension(loc.Path, ".jpg") : loc.Path, p => Task.FromResult(FileStore.Get(db, p) != null));
            try { FileStore.Put(db, to, edited, "image/jpeg"); }
            catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message }, statusCode: 507); }
            return Results.Ok(new { path = loc.Show(to) });
        });

        // Regler: Vorschau (verkleinert, aufrecht, ungedreht und ungeschnitten - Drehen und Ausschnitt zeigt der Editor selbst)
        // Entwicklungseinstellungen (nicht-destruktiv): lesen, setzen, entfernen - das Original bleibt
        api.MapGet("/photo-edits", (string path, HttpContext http, ServerConfig config) =>
        {
            if (TresorPaths.Is(path)) return Results.Ok(new { edit = (object?)null });
            var (db, key) = EditKey(http, config, path, write: false);
            return Results.Ok(new { edit = PhotoEdits.Get(db, key) });
        });
        api.MapPut("/photo-edits", (PhotoEditsBody body, HttpContext http, ServerConfig config) =>
        {
            var (db, key) = EditKey(http, config, body.Path, write: true);
            PhotoEdits.Set(db, key, new PhotoEdits.Edit(body.Rotate, body.Crop, body.Adjust, body.Origin is "engine" or "ai" ? body.Origin : "hand", 0));
            var edit = PhotoEdits.Get(db, key);
            return Results.Ok(new { edit, fingerprint = edit == null ? null : PhotoEdits.Fingerprint(edit) });
        });
        api.MapDelete("/photo-edits", (string path, HttpContext http, ServerConfig config) =>
        {
            var (db, key) = EditKey(http, config, path, write: true);
            PhotoEdits.Clear(db, key);
            return Results.Ok();
        });

        // Editor geöffnet: das Original schon einmal holen (Galerie-Ordner) - Vorschau, Vorschläge und Speichern warten dann nicht darauf
        api.MapPost("/photo-edit/prefetch", (PhotoAdjustBody body, HttpContext http, ServerConfig config) =>
        {
            PhotoWork.Prefetch(http.Space(), config, body.Path);
            return Results.Ok();
        });

        api.MapPost("/photo-adjust/preview", async (PhotoAdjustBody body, HttpContext http, ServerConfig config, ILogger<PhotoAdjustBody> log, CancellationToken ct) =>
        {
            // RAW: die Engine entwickelt (auch ohne Regler - sonst sähe man nur das eingebettete Kamera-JPG)
            if (IsRaw(body.Path))
                return await PhotoEngine.DevelopAsync(await PhotoBytesAsync(http, config, body.Path, ct), (body.Adjust ?? Neutral).Clamped(), 1400, 85, log, ct) is { } rawDev
                    ? Results.File(rawDev, "image/jpeg") : Results.Json(new { error = RawNeedsEngine }, statusCode: 503);
            using var src = await PreviewSourceAsync(http, config, body.Path, ct);
            // Mit Photo-Engine: ihre Umsetzung (die verkleinerte Fassung ist schon aufrecht, ohne EXIF)
            if (body.Adjust is { IsNeutral: false } ea && PhotoEngine.Configured
                && await PhotoEngine.DevelopAsync(PhotoAdjust.Jpeg(src, 95), ea.Clamped(), 1400, 85, log, ct) is { } dev)
                return Results.File(dev, "image/jpeg");
            using var done = body.Adjust is { IsNeutral: false } a ? PhotoAdjust.Apply(src, a.Clamped()) : null;
            return Results.File(PhotoAdjust.Jpeg(done ?? src, 85), "image/jpeg");
        });
        // Vorschlag der KI (nur, wenn für einen freigeschaltet): Vorschau, Aufnahmedaten und Kennzahlen hin, Reglerwerte zurück
        api.MapGet("/photo-adjust/ai", (HttpContext http, ServerConfig config, UserStore users) =>
            new { available = DmOffice.Chat.PukkiAi.Allowed(config, users.ById(http.Space().User.Id)), engine = PhotoEngine.Configured, raw = PhotoEngine.Configured });
        // Vorschlag der Photo-Engine (ohne KI): verkleinert und aufrecht hin, dazu die Aufnahmedaten
        api.MapPost("/photo-adjust/engine", async (PhotoAdjustBody body, HttpContext http, ServerConfig config, ILogger<PhotoAdjustBody> log, CancellationToken ct) =>
        {
            if (!PhotoEngine.Configured) return Results.NotFound(new { error = "Die Photo-Engine ist nicht eingerichtet." });
            var r = await EngineValuesAsync(await PhotoBytesAsync(http, config, body.Path, ct), body.Path, http.Space().Zone, log, ct);
            return r is { } ok ? Results.Ok(new { values = ok.Values, analysis = ok.Analysis })
                : Results.Json(new { error = "Die Photo-Engine antwortet gerade nicht." }, statusCode: 503);
        });
        api.MapPost("/photo-adjust/suggest", async (PhotoAdjustBody body, HttpContext http, ServerConfig config, UserStore users, ILogger<PhotoAdjustBody> log, CancellationToken ct) =>
        {
            if (!DmOffice.Chat.PukkiAi.Allowed(config, users.ById(http.Space().User.Id))) throw new UnauthorizedAccessException("Für dich ist keine KI freigeschaltet.");
            // Grundsatz: die KI hat keinen Zugriff auf Inhalte im Tresor
            if (TresorPaths.Is(body.Path)) throw new UnauthorizedAccessException("Die KI hat keinen Zugriff auf den Tresor.");
            var (values, analysis) = await AiValuesAsync(config, await PhotoBytesAsync(http, config, body.Path, ct), body.Path, http.Space().Zone, log, ct);
            return Results.Ok(new { values, analysis });
        });

        // Mehrere Fotos automatisch verbessern (Galerie, markierte): Vorschlag der Engine oder der KI je Foto, gemerkt als Einstellungen -
        // im Hintergrund, Fortschritt in der Aktivitätsanzeige. Fotos ohne nötige Korrektur bleiben, wie sie sind.
        api.MapPost("/photo-auto", (PhotoAutoBody body, HttpContext http, ServerConfig config, UserStore users, ILogger<PhotoAutoBody> log) =>
        {
            var space = http.Space();
            if (body.Source == "engine" && !PhotoEngine.Configured) return Results.Json(new { error = "Die Photo-Engine ist nicht eingerichtet." }, statusCode: 503);
            if (body.Source == "ai" && !DmOffice.Chat.PukkiAi.Allowed(config, users.ById(space.User.Id))) return Results.Json(new { error = "Für dich ist keine KI freigeschaltet." }, statusCode: 403);
            if (body.Source is not ("engine" or "ai")) throw new ArgumentException("Unbekannte Art des Vorschlags.");
            // Ziele jetzt auflösen (Rechte, geteilte Ordner) - der Auftrag läuft ohne die Anfrage weiter
            var jobs = new List<(string Path, FileLoc? Loc)>();
            foreach (var path in body.Paths.Distinct().Take(500))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"\.(jpe?g)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && !IsRaw(path)) continue;
                if (IsRaw(path) && !PhotoEngine.Configured) continue;
                jobs.Add(path.StartsWith("ext:", StringComparison.Ordinal) ? (path, null) : (path, Loc(http, path).Creatable()));
            }
            if (jobs.Count == 0) return Results.BadRequest(new { error = "Keine bearbeitbaren Fotos (JPG, RAW mit der Photo-Engine) markiert." });
            _ = Task.Run(() => AutoAsync(space, config, jobs, body.Source, log));
            return Results.Ok(new { started = jobs.Count });
        });

        // Textdatei speichern (Bearbeiten im Browser, neue Textdatei); mit ETag: nur, wenn sie sich nicht geaendert hat
        api.MapPost("/save", (SaveBody body, HttpContext http) =>
        {
            var loc = Loc(http, body.Path).Creatable();
            var db = loc.Db;
            var data = Encoding.UTF8.GetBytes(body.Text ?? "");
            if (data.Length > 20 * 1024 * 1024) throw new ArgumentException("Zu groß für den Texteditor.");
            try
            {
                var (etag, _) = FileStore.Put(db, loc.Path, data, DavServer.Mime(loc.Path), ifMatch: body.Create ? null : body.Etag, ifNoneMatch: body.Create ? "*" : null);
                if (!loc.Shared) FileStore.Used(db, FileStore.Get(db, loc.Path)?.FileId ?? 0);
                return Results.Ok(new { path = loc.Show(loc.Path), etag });
            }
            catch (Pim.PreconditionFailedException)
            {
                return Results.Conflict(new { error = body.Create ? "Gibt es schon." : "Die Datei wurde inzwischen geändert.", conflict = true });
            }
            catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message }, statusCode: 507); }
        });

        // In den Papierkorb (in einem geteilten Ordner: in den des Besitzers); Antwort: die eigenen Eintraege dort (fuer Rueckgaengig)
        api.MapPost("/delete", (PathsBody body, HttpContext http) =>
        {
            var all = body.Paths ?? (body.Path != null ? [body.Path] : []);
            // Im Tresor endgültig (kein Papierkorb)
            if (all.Any(TresorPaths.Is)) TresorPaths.Delete(http, all.Where(TresorPaths.Is));
            // Im entsperrten Cryptomator-Tresor ebenso endgültig
            if (all.Any(CryptomatorPaths.Is)) CryptomatorPaths.Delete(http, all.Where(CryptomatorPaths.Is));
            var locs = all.Where(p => !TresorPaths.Is(p) && !CryptomatorPaths.Is(p)).Select(p => LocTree(http, p).Changeable()).ToList();
            var trash = new List<long>();
            foreach (var loc in locs)
                if (FileStore.Delete(loc.Db, loc.Path, out var id) && id > 0 && !loc.Shared) trash.Add(id);
            return Results.Ok(new { trash });
        });

        // conflict: fail (Vorgabe, 409), replace (Ziel in den Papierkorb), rename (Nummer anhaengen), skip.
        // Aus einem geteilten Ordner heraus wird kopiert - das Original bleibt beim Besitzer (Antwort copied: true).
        // Eigenes in einen geteilten Ordner verschieben: es wird dorthin kopiert und verschwindet bei mir.
        api.MapPost("/move", async (MoveBody body, HttpContext http) =>
        {
            // Mindestens eine Seite im Tresor (auch zwischen Tresor und „Meine Dateien“)
            if (CryptomatorPaths.Is(body.From) || CryptomatorPaths.Is(body.To))
                return await CryptomatorPaths.Move(http, body.From, body.To, body.Copy, body.Conflict, http.RequestAborted);
            if (TresorPaths.Is(body.From) || TresorPaths.Is(body.To))
                return await TresorPaths.Move(http, body.From, body.To, body.Copy, body.Conflict, http.RequestAborted);
            var from = LocTree(http, body.From);
            var to = Loc(http, body.To).Creatable();
            if (from.Virtual) throw new UnauthorizedAccessException("„Mit mir geteilt“ bleibt, wo es ist.");
            bool sameArea = from.Db == to.Db && from.Mount?.Share.Id == to.Mount?.Share.Id;
            bool copy = body.Copy || (from.Shared && !sameArea);
            // Aus einem nur gezeigten Ordner laesst sich nichts herauskopieren
            if (copy) from.NotShowOnly("kopieren");
            if (!copy) from.Changeable();
            bool overwrite = false;
            if (FileStore.Get(to.Db, to.Path) != null)
            {
                switch (body.Conflict)
                {
                    case "rename": to = to with { Path = FileStore.FreeName(to.Db, to.Path) }; break;
                    case "skip": return Results.Ok(new { path = to.Show(to.Path), skipped = true });
                    case "replace": LocTree(http, body.To).Changeable(); overwrite = true; break;
                    default: return Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = to.Show(to.Path) });
                }
            }
            try
            {
                long replaced = 0;
                if (from.Db == to.Db) FileStore.MoveOrCopy(to.Db, from.Path, to.Path, overwrite, copy, out replaced);
                else
                {
                    await SharedFiles.CopyAcross(from, to, overwrite, http.RequestAborted);
                    if (!copy) FileStore.Delete(from.Db, from.Path);
                }
                return Results.Ok(new { path = to.Show(to.Path), skipped = false, replaced = replaced > 0 && !to.Shared ? replaced : (long?)null, copied = copy && !body.Copy });
            }
            catch (DavConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (DavForbiddenException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (QuotaExceededException ex) { return Results.Json(new { error = to.Shared ? $"Beim Besitzer ist kein Platz mehr frei. {ex.Message}" : ex.Message }, statusCode: 507); }
            catch (Pim.PreconditionFailedException) { return Results.Conflict(new { error = "Am Ziel gibt es das schon.", conflict = true, path = to.Show(to.Path) }); }
        });

        // Mehrere Dateien/Ordner als ZIP. POST als Formular (lange Auswahl), GET fuer einzelne Ordner.
        // Grenze für ZIP-Downloads (Verwaltung → Dateien; 0 = keine) - die Oberfläche zeigt sie vorher an
        api.MapGet("/zip-limit", (ServerConfig config) => new { limit = ZipLimit(config) });

        api.MapMethods("/zip", ["GET", "POST"], async (HttpContext http, ServerConfig config) =>
        {
            var src = http.Request.HasFormContentType ? (await http.Request.ReadFormAsync()).ToDictionary(x => x.Key, x => x.Value)
                : http.Request.Query.ToDictionary(x => x.Key, x => x.Value);
            // Größer als erlaubt: nicht erst anfangen
            if (ZipLimit(config) is > 0 and var zipMax && src.TryGetValue("paths", out var all) && ZipSize(http, all.Select(p => p!).ToList()) > zipMax)
                return Results.Json(new { error = $"Größer als die Grenze für ZIP-Downloads ({zipMax / 1024 / 1024} MB)." }, statusCode: 413);
            var name = src.TryGetValue("name", out var n) && n.ToString() is { Length: > 0 } nn ? nn : "Dateien";
            if (src.TryGetValue("paths", out var cp) && cp.Count > 0 && cp.All(p => CryptomatorPaths.Is(p)))
            {
                await CryptomatorPaths.Zip(http, cp.Select(p => p!).ToList(), Path.GetFileNameWithoutExtension(name) + ".zip");
                return Results.Empty;
            }
            if (src.TryGetValue("paths", out var tp) && tp.Count > 0 && tp.All(p => TresorPaths.Is(p)))
            {
                await TresorPaths.Zip(http, tp.Select(p => p!).ToList(), Path.GetFileNameWithoutExtension(name) + ".zip");
                return Results.Empty;
            }
            var locs = src.TryGetValue("paths", out var ps) ? ps.Select(p => Loc(http, p)).ToList() : [];
            if (locs.Count == 0) throw new ArgumentException("Nichts ausgewählt.");
            if (locs.Any(l => l.Virtual)) throw new ArgumentException("„Mit mir geteilt“ lässt sich nicht als Ganzes herunterladen.");
            foreach (var l in locs) l.NotShowOnly();
            if (locs.Select(l => l.Db).Distinct().Count() > 1) throw new ArgumentException("Bitte nur aus einem Ordner zugleich herunterladen.");
            var first = locs[0];
            // Namen im ZIP relativ zum aktuellen Ordner; liegt der woanders (geteilte Ordner in „Mit mir geteilt“), relativ zu ihrem Ordner beim Besitzer
            var b = SharedFiles.Resolve(http, FileStore.Normalize(src.TryGetValue("base", out var bb) ? bb.ToString() : ""));
            var baseDir = b.Db == first.Db && !b.Virtual ? b.Path : FileStore.Parent(first.Path) ?? "";
            await Zip.Write(http, first.Db, baseDir, locs.Select(l => l.Path), first.Protected, Path.GetFileNameWithoutExtension(name) + ".zip");
            return Results.Empty;
        }).DisableAntiforgery();

        // Suchen nach Namen: unter einem Ordner (Vorgabe: ueberall, samt der mit mir geteilten Ordner), optional nur eine Art.
        // content=true: danach die Dateien, in deren Text die Woerter vorkommen (Volltextsuche, wenn eingeschaltet) - mit Ausschnitt
        api.MapGet("/search", (string? q, string? path, string? type, bool? photos, bool? content, HttpContext http) =>
        {
            var under = Loc(http, path);
            // Ansicht Fotos: ausgeschlossene Ordner (Cover-Bilder der Musik, App-Daten ...) weglassen
            var excluded = photos == true ? http.Space().Settings.PhotoExcludes : [];
            var areas = under.Virtual || under.Path.Length == 0 && !under.Shared
                ? SharedFiles.Mounts(http).Select(m => new FileLoc(m.Owner, m.Dir.Path, m)).ToList() : [];
            if (!under.Virtual) areas.Insert(0, under);
            // Nach Art (Ansicht Fotos): alle Bilder bis zu einer grossen Grenze, sonst die ersten Treffer
            var limit = string.IsNullOrEmpty(type) ? 500 : 5000;
            bool OfType(FileEntry f) => string.IsNullOrEmpty(type) || FileKinds.Of(f) == type || type == "media" && FileKinds.Of(f) is "image" or "video";
            var named = areas.SelectMany(a =>
            {
                var marks = MarksOf(http, a);
                return FileStore.Search(a.Db, a.Path, q ?? "", limit, f => !a.Protected(f.Path) && !(!a.Shared && SharedFiles.Under(http, f.Path)) && OfType(f))
                    .Select(a.Show).Where(f => excluded.Count == 0 || !FileKinds.UnderAny(excluded, f.Path))
                    .Select(f => (File: f, Marks: marks));
            }).Take(limit).ToList();
            var items = named.Select(x => Item(x.File, x.Marks)).ToList();
            // Auch über Kategorien und Tags (eigene Dateien)
            if (!string.IsNullOrWhiteSpace(q) && areas.FirstOrDefault(a => !a.Shared) is { } own && FileCategories.Matching(own.Db, q.Trim()) is { Count: > 0 } labelled)
            {
                var seen = named.Select(x => x.File.Path).ToHashSet();
                var marks = MarksOf(http, own);
                foreach (var f in labelled.Chunk(400).SelectMany(ch => FileStore.ByFileIds(own.Db, $"SELECT fileid FROM files WHERE fileid IN ({string.Join(",", ch)})")))
                {
                    if (items.Count >= limit) break;
                    var inside = own.Path.Length == 0 || f.Path.StartsWith(own.Path + "/", StringComparison.Ordinal);
                    if (f.IsDir || !inside || own.Protected(f.Path) || SharedFiles.Under(http, f.Path) || !OfType(f)) continue;
                    if (excluded.Count > 0 && FileKinds.UnderAny(excluded, f.Path)) continue;
                    var shown = own.Show(f);
                    if (seen.Add(shown.Path)) items.Add(Item(shown, marks));
                }
            }
            if (content == true && !string.IsNullOrWhiteSpace(q))
            {
                // Im Inhalt gefunden - im Index des Besitzers (geteilte Ordner nur, wenn er die Inhaltssuche eingeschaltet hat)
                var seen = named.Select(x => x.File.Path).ToHashSet();
                foreach (var a in areas.Where(a => a.Space.Settings.TextIndex.Enabled))
                {
                    var marks = MarksOf(http, a);
                    foreach (var h in TextIndex.SearchFiles(a.Space, q, a.Path, 100))
                    {
                        if (a.Protected(h.Path) || !a.Shared && SharedFiles.Under(http, h.Path)) continue;
                        if (FileStore.Get(a.Db, h.Path) is not { IsDir: false } f || !OfType(f)) continue;
                        var shown = a.Show(f);
                        if (seen.Add(shown.Path)) items.Add(Item(shown, marks, snippet: h.Snippet));
                    }
                }
            }
            return new { items };
        });

        // Ordnerkacheln: je Unterordner die neuesten vier Bilder (auch aus tieferen Ordnern) als Collage
        api.MapGet("/covers", (string? path, HttpContext http) =>
        {
            var loc = Loc(http, path);
            if (loc.Virtual || loc.ShowOnly) return Results.Ok(new Dictionary<string, object[]>());
            var result = new Dictionary<string, object[]>();
            foreach (var d in FileStore.Children(loc.Db, loc.Path).Where(f => f.IsDir && !loc.Protected(f.Path)).Take(200))
            {
                var imgs = FileStore.CoverImages(loc.Db, d.Path).Where(f => !loc.Protected(f.Path)).Select(f => Item(loc.Show(f))).ToArray();
                if (imgs.Length > 0) result[loc.Show(d).Path] = imgs;
            }
            return Results.Ok(result);
        });

        // Aufnahmedaten (FileMeta): ohne Pfad alle eigenen Fotos und Videos (Galerie, Schlüssel ist der Pfad), mit Pfad die
        // direkt in diesem Ordner (Detailansicht, Schlüssel ist der Name) - dazu, wie viele noch nicht gelesen sind
        api.MapGet("/meta", (string? path, HttpContext http) =>
        {
            static object Of(FileMeta.Row m, string key) => new { key, m.Taken, m.Camera, m.Lens, m.Focal, m.Aperture, m.Exposure, m.Iso, m.Width, m.Height, m.Duration, m.Rating, m.Title, m.Keywords, m.Lat, m.Lon, m.Label, m.Pick };
            if (path == null)
            {
                var db = http.Space().Db;
                return Results.Ok(new { items = FileMeta.List(db, null).Select(m => Of(m, m.Path)), pending = FileMeta.Pending(db) });
            }
            var loc = Loc(http, path);
            if (loc.Virtual || loc.ShowOnly) return Results.Ok(new { items = Array.Empty<object>(), pending = 0L });
            return Results.Ok(new { items = FileMeta.List(loc.Db, loc.Path).Where(m => !loc.Protected(m.Path)).Select(m => Of(m, m.Path[(m.Path.LastIndexOf('/') + 1)..])), pending = 0L });
        });

        api.MapGet("/photo-excludes", (HttpContext http) => http.Space().Settings.PhotoExcludes);
        api.MapPut("/photo-excludes", (PhotoExcludesBody body, HttpContext http) =>
        {
            var list = body.Folders.Select(FileStore.Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            http.Space().UpdateSettings(s => s.PhotoExcludes = list);
            return Results.Ok(list);
        });

        api.MapGet("/recent", (HttpContext http) =>
        {
            var db = http.Space().Db;
            var marks = MarksOf(http, new FileLoc(http.Space(), "", null));
            return new { items = FileStore.Recent(db).Where(x => !Protected(http, x.File.Path)).Select(x => new { item = Item(x.File, marks), used = x.Used }) };
        });

        api.MapGet("/favorites", (HttpContext http) =>
        {
            var db = http.Space().Db;
            var marks = MarksOf(http, new FileLoc(http.Space(), "", null));
            return new { items = FileStore.Favorites(db).Where(f => !Protected(http, f.Path)).Select(f => Item(f, marks)) };
        });

        api.MapPost("/favorite", (FavoriteBody body, HttpContext http) =>
        {
            var loc = Loc(http, body.Path);
            if (loc.Shared || loc.Virtual) throw new ArgumentException("Favoriten gibt es nur in der eigenen Ablage.");
            if (loc.Path.Length == 0) throw new ArgumentException("„Meine Dateien“ ist immer da.");
            var f = FileStore.Get(loc.Db, loc.Path) ?? throw new KeyNotFoundException("Nicht gefunden.");
            FileStore.Favorite(loc.Db, f.FileId, body.On);
            return Results.Ok();
        });

        // Ordner für WebDAV und Nextcloud aus- oder einblenden; dmVAULT ist die Einstellung „bereitstellen“ des Tresors
        api.MapPost("/dav-hidden", (DavHiddenBody body, HttpContext http) =>
        {
            var space = http.Space();
            // Der Tresor (Ordner dmVAULT, in der Ablage nicht als Ordner zu sehen): seine Einstellung „bereitstellen“
            if (FileStore.Normalize(body.Path) == FileStore.SystemVault)
            {
                if (!Tresor.Exists(space.User.Id)) throw new ArgumentException("Es ist kein Tresor eingerichtet.");
                Tresor.ChangeSettings(space.User.Id, null, null, null, dav: !body.Hidden);
                SecurityNotice.Own(http.RequestServices, space.User.Id, body.Hidden ? "Tresor für WebDAV und Nextcloud ausgeblendet" : "Tresor für WebDAV und Nextcloud eingeblendet");
                return Results.Ok();
            }
            var loc = Loc(http, body.Path);
            if (loc.Shared || loc.Virtual) throw new ArgumentException("Das geht nur in der eigenen Ablage.");
            if (loc.Path.Length == 0) throw new ArgumentException("„Meine Dateien“ lässt sich nicht ausblenden.");
            var f = FileStore.Get(loc.Db, loc.Path) ?? throw new KeyNotFoundException("Nicht gefunden.");
            if (!f.IsDir) throw new ArgumentException("Ausblenden geht nur für Ordner.");
            DmOffice.Dav.DavHidden.Set(loc.Db, f.FileId, body.Hidden);
            SecurityNotice.Own(http.RequestServices, space.User.Id, (body.Hidden ? "Für WebDAV und Nextcloud ausgeblendet: " : "Für WebDAV und Nextcloud eingeblendet: ") + f.Path);
            return Results.Ok();
        });

        // ---------------- Papierkorb ----------------

        // q: suchen - im Namen und Ort des Gelöschten und in den Namen in gelöschten Ordnern (matches: Treffer darin)
        api.MapGet("/trash", (string? q, HttpContext http) =>
        {
            var db = http.Space().Db;
            var hits = string.IsNullOrWhiteSpace(q) ? null : FileStore.TrashSearch(db, q.Trim());
            return new
            {
                days = TrashDays(db),
                usage = FileStore.TrashUsage(db),
                items = FileStore.Trash(db).Where(t => !Protected(http, t.Path) && (hits == null || hits.ContainsKey(t.Id)))
                    .Select(t => new { t.Id, t.Path, t.Name, t.IsDir, t.Size, t.Deleted, t.Mime, matches = hits?.GetValueOrDefault(t.Id) })
            };
        });

        // Vorschaubild eines gelöschten Fotos für die Kachelansicht des Papierkorbs
        api.MapGet("/trash/{id:long}/thumb", (long id, int? size, HttpContext http) =>
        {
            var db = http.Space().Db;
            var t = FileStore.Trash(db).FirstOrDefault(x => x.Id == id);
            if (t == null || t.IsDir || Protected(http, t.Path)) return Results.NotFound();
            var fileId = db.Scalar("SELECT fileid FROM file_trash_items WHERE trash_id=$t AND rel=''", ("$t", id)) is long f ? f : 0;
            if (fileId <= 0 || Thumbs.Stored(db, fileId, size ?? Thumbs.Small) is not { } data) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(data, "image/webp");
        });

        api.MapPost("/trash/restore", (IdsBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            var restored = new List<string>();
            foreach (var id in body.Ids)
            {
                try { restored.Add(FileStore.Restore(db, id)); }
                catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message, restored }, statusCode: 507); }
            }
            return Results.Ok(new { restored });
        });

        api.MapPost("/trash/delete", (IdsBody body, HttpContext http) => Results.Ok(new { deleted = FileStore.PurgeTrash(http.Space().Db, body.Ids) }));

        api.MapPost("/trash/empty", (HttpContext http) => Results.Ok(new { deleted = FileStore.PurgeTrash(http.Space().Db, null) }));

        api.MapPut("/trash/settings", (TrashSettings body, HttpContext http) =>
        {
            if (body.Days is < 0 or > 3650) throw new ArgumentException("0 bis 3650 Tage.");
            http.Space().Db.Set(TrashDaysKey, body.Days.ToString());
            return Results.Ok();
        });

        // ---------------- Aufbewahrung (Einstellungen -> Dateien) ----------------
        api.MapGet("/settings", (HttpContext http, ServerConfig config, S3Mirror mirror, S3Backup backup) =>
        {
            var db = http.Space().Db;
            return new { settings = FileRetention.Load(db), limits = FileRetention.LimitsOf(), usage = FileStore.Usage(db), trash = FileStore.TrashUsage(db), versions = FileStore.VersionUsage(db), quota = FileStore.Quota(db), used = UserStorage.Used(db), copy = CopyInfo(config, mirror, backup) };
        });
        api.MapPut("/settings", (FileRetention.Settings body, HttpContext http) =>
        {
            var db = http.Space().Db;
            FileRetention.Save(db, body);
            // Gleich anwenden, damit der Platz sofort frei wird
            FileRetention.Apply(db, DateTime.UtcNow);
            return Results.Ok(new { settings = FileRetention.Load(db), trash = FileStore.TrashUsage(db), versions = FileStore.VersionUsage(db) });
        });
        api.MapPost("/versions/purge", (HttpContext http) =>
        {
            var db = http.Space().Db;
            var n = db.Exec("DELETE FROM file_versions");
            FileStore.CollectBlobs(db);
            return Results.Ok(new { deleted = n });
        });

        // ---------------- Fruehere Versionen (in einem geteilten Ordner: die des Besitzers) ----------------

        api.MapGet("/versions", (string path, HttpContext http) =>
        {
            var loc = Loc(http, path);
            return new { items = FileStore.Versions(loc.Db, FileAt(loc).FileId) };
        });

        api.MapPost("/versions/restore", (VersionBody body, HttpContext http) =>
        {
            var loc = Loc(http, body.Path).Writable();
            try { return Results.Ok(new { etag = FileStore.RestoreVersion(loc.Db, loc.Path, body.Id) }); }
            catch (QuotaExceededException ex) { return Results.Json(new { error = ex.Message }, statusCode: 507); }
        });

        // ---------------- Oeffentliche Links (nur in der eigenen Ablage) ----------------

        api.MapGet("/links", (string? path, HttpContext http) =>
        {
            var db = http.Space().Db;
            long? fileId = null;
            if (path != null)
            {
                var loc = Loc(http, path);
                if (loc.Shared || loc.Virtual) return new { items = Enumerable.Empty<object>() };
                fileId = FileAt(loc).FileId;
            }
            var now = DateTime.UtcNow;
            return new
            {
                items = Links.List(db, fileId).Select(l => (Link: l, File: FileStore.ById(db, l.FileId)))
                    .Where(x => x.File == null || !Protected(http, x.File.Path))
                    .Select(x => (object)new
                    {
                        x.Link.Id, x.Link.Token, x.Link.Mode, x.Link.Expires, x.Link.HasPassword, x.Link.Created, x.Link.Downloads, x.Link.MaxDownloads,
                        expired = x.Link.Expired(now) || x.Link.UsedUp,
                        // Datei im Papierkorb: Link ruht, bis sie zurueck ist
                        item = x.File == null ? null : Item(x.File),
                    })
            };
        });

        api.MapPost("/links", (LinkBody body, HttpContext http) =>
        {
            var space = http.Space();
            var loc = Loc(http, body.Path ?? throw new ArgumentException("Pfad fehlt."));
            if (loc.Shared || loc.Virtual) throw new UnauthorizedAccessException("Links zu einem geteilten Ordner legt nur der Besitzer an.");
            var f = FileAt(loc);
            if (!f.IsDir && body.Mode != "read") throw new ArgumentException("Hochladen geht nur in Ordner.");
            var (expires, maxDl) = LinkLimits(body);
            var link = Links.Create(space.Db, f.FileId, body.Mode ?? "read", expires, body.Password, maxDl);
            http.RequestServices.GetRequiredService<LinkIndex>().Add(space.User.Id, link.Id, link.Token);
            // Aus der App (App-Passwort): der Link endet mit dem Widerruf dieses App-Passworts
            if (http.User.FindFirst(Auth.AppPasswordIdClaim)?.Value is { Length: > 0 } device) Links.ByDevice(space.Db, link.Id, device);
            return Results.Ok(link);
        });

        api.MapPut("/links/{id}", (string id, LinkBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            var link = Links.Get(db, id) ?? throw new KeyNotFoundException("Link nicht gefunden.");
            if (FileStore.ById(db, link.FileId) is { IsDir: false } && body.Mode != "read") throw new ArgumentException("Hochladen geht nur in Ordner.");
            var (expires, maxDl) = LinkLimits(body);
            Links.Update(db, id, body.Mode, expires, body.Password, maxDl);
            return Results.Ok(Links.Get(db, id));
        });

        api.MapDelete("/links/{id}", (string id, HttpContext http) =>
        {
            var space = http.Space();
            if (!Links.Delete(space.Db, id)) throw new KeyNotFoundException("Link nicht gefunden.");
            http.RequestServices.GetRequiredService<LinkIndex>().Remove(space.User.Id, id);
            return Results.Ok();
        });

        // ---------------- Mit Personen teilen (Ordner und einzelne Dateien) ----------------

        // Meine geteilten Ordner mit ihren Mitgliedern - fuer die Ansicht "Freigegeben"; mit path: nur dieser Ordner
        api.MapGet("/shares", (string? path, HttpContext http, FolderShareStore shares) =>
        {
            var me = http.Space();
            long? only = path == null ? null : FileAt(Own(http, path)).FileId;
            var marks = MarksOf(http, new FileLoc(me, "", null));
            return new
            {
                items = shares.All().Where(s => s.Owner == me.User.Id && (only == null || s.Folder == only))
                    .Select(s => (Share: s, Dir: FileStore.ById(me.Db, s.Folder)))
                    // Ordner im Papierkorb: die Freigabe ruht, bis er zurueck ist
                    .Where(x => x.Dir != null && !Protected(http, x.Dir.Path))
                    .Select(x => new { x.Share.Id, item = Item(x.Dir!, marks), members = People(http, x.Share) })
            };
        });

        // Einen eigenen Ordner teilen (readOnly: wer davon nur lesen darf); leere Mitglieder: Freigabe aufheben
        api.MapPut("/shares", (ShareBody body, HttpContext http, FolderShareStore shares, UserStore users) =>
        {
            var me = http.Space();
            var f = FileAt(Own(http, body.Path));
            if (!SharedFiles.ShareableEntry(me.Db, f)) throw new ArgumentException(f.IsDir
                ? "Dieser Ordner lässt sich nicht teilen: Er ist der geschützte notizi-Abgleichordner oder enthält ihn."
                : "Diese Datei lässt sich nicht teilen: Sie liegt im geschützten notizi-Abgleichordner.");
            var members = body.Members.Where(m => m != me.User.Id && users.ById(m) != null).ToList();
            shares.Set(me.User.Id, f.FileId, members, body.ReadOnly, body.ShowOnly);
            return Results.Ok(new { members = People(http, f.FileId) });
        });

        // Mit mir geteilte Ordner und Dateien ("Mit mir geteilt")
        api.MapGet("/shared", (HttpContext http) => new
        {
            items = SharedFiles.Mounts(http).Select(m => new
            {
                m.Share.Id, m.Path, m.Name, folder = m.Dir.Name, owner = m.Owner.User.DisplayName, m.CanWrite, m.ShowOnly, fileId = m.Id(m.Dir.FileId), m.Dir.IsDir,
            })
        });

        // Mit mir geteilten Ordner nicht mehr anzeigen (der Besitzer behaelt ihn)
        api.MapPost("/shared/leave", (LeaveBody body, HttpContext http, FolderShareStore shares) =>
        {
            shares.Leave(body.Id, http.Space().User.Id);
            return Results.Ok();
        });
    }

    public const string TrashDaysKey = "files.trashDays";

    /// <summary>Wirksame Aufbewahrung in Tagen (Einstellung des Benutzers, begrenzt durch die Verwaltung).</summary>
    public static int TrashDays(UserDb db) => FileRetention.Load(db).TrashDays;
}

/// <summary>Art einer Datei fuer Filter und Symbole - dieselben Namen wie in der Oberflaeche (files/kinds.ts).</summary>
public sealed record PhotoExcludesBody(List<string> Folders);

public static class FileKinds
{
    /// <summary>Liegt der Pfad in einem der Ordner (oder ist er einer)?</summary>
    public static bool UnderAny(IEnumerable<string> folders, string path) =>
        folders.Any(e => path.Equals(e, StringComparison.OrdinalIgnoreCase) || path.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase));

    public static string Of(FileEntry f)
    {
        if (f.IsDir) return "folder";
        var ext = Path.GetExtension(f.Name).ToLowerInvariant();
        var mime = f.Mime ?? "";
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" or ".heic" or ".heif" or ".tif" or ".tiff" or ".ico" or ".jfif" or ".avif" => "image",
            _ when Thumbs.IsRaw(f.Name) => "image",
            ".pdf" => "pdf",
            ".doc" or ".docx" or ".odt" or ".rtf" or ".pages" => "doc",
            ".xls" or ".xlsx" or ".ods" or ".csv" or ".numbers" => "sheet",
            ".ppt" or ".pptx" or ".odp" or ".key" => "slides",
            ".mp3" or ".m4a" or ".wav" or ".ogg" or ".oga" or ".opus" or ".flac" or ".aac" or ".wma" => "audio",
            ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv" or ".avi" or ".wmv" or ".ogv" => "video",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" => "archive",
            ".txt" or ".md" or ".markdown" or ".log" or ".json" or ".xml" or ".yaml" or ".yml" or ".ini" or ".conf" or ".cfg" or ".toml"
                or ".cs" or ".js" or ".ts" or ".tsx" or ".jsx" or ".py" or ".java" or ".kt" or ".c" or ".h" or ".cpp" or ".go" or ".rs" or ".sh"
                or ".ps1" or ".bat" or ".sql" or ".css" or ".scss" or ".html" or ".htm" or ".php" or ".rb" or ".swift" or ".ics" or ".vcf" => "text",
            _ => mime.StartsWith("image/") ? "image" : mime.StartsWith("audio/") ? "audio" : mime.StartsWith("video/") ? "video"
                : mime.StartsWith("text/") ? "text" : "other",
        };
    }
}
