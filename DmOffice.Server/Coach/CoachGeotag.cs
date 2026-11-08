using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using DmOffice.Journal;
using DmOffice.Storage;

namespace DmOffice.Coach;

/// <summary>
/// Fotos ohne Ort aus einer aufgezeichneten Aktivität verorten: die Aufnahmezeit (mit Zeitzone aus dem Bild, sonst der eigenen,
/// dazu ein Versatz für eine falsch gehende Kamera-Uhr) wird auf die Strecke abgebildet - zwischen den beiden umgebenden
/// Punkten linear, vor dem Beginn und nach dem Ende (bis <see cref="Slack"/>) der erste bzw. letzte Punkt. Nur Fotos ohne GPS.
/// Erst eine Vorschau (<see cref="PreviewAsync"/>), geschrieben wird nach dem Bestätigen im Hintergrund (<see cref="Start"/>):
/// JPEG ins EXIF der Datei, RAW in die Sidecar-Datei (<see cref="PhotoMeta"/>) - in der Ablage mit Version, in Galerie-Ordnern nur,
/// wenn sie beschreibbar eingebunden sind. RAW und JPEG mit gleichem Namen daneben (Kamera nimmt beides auf) werden mit verortet.
/// </summary>
public static class CoachGeotag
{
    /// <summary>So lange vor dem ersten und nach dem letzten Punkt gilt ein Foto noch als „während der Aktivität“.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(5);
    /// <summary>Liegen die umgebenden Punkte weiter auseinander (Aufzeichnung unterbrochen), ist die Position nur ungefähr.</summary>
    public const double RoughGapS = 300;
    /// <summary>So viel vom Anfang einer Datei reicht für die EXIF-Angaben (RAW: Vorschau-JPEG vorn).</summary>
    private const int HeadBytes = 512 * 1024;

    public sealed record Spot(double Lat, double Lon, bool Rough);

    /// <summary>Punkte mit Zeit ([lat, lon, Unix-Sekunden]), nach Zeit sortiert.</summary>
    public static List<double[]> Timed(IEnumerable<double[]> track) => track.Where(p => p.Length >= 3).OrderBy(p => p[2]).ToList();

    /// <summary>Position zur Zeit <paramref name="t"/> (Unix-Sekunden) auf einer Strecke aus <see cref="Timed"/>; null außerhalb.</summary>
    public static Spot? Locate(IReadOnlyList<double[]> pts, double t)
    {
        if (pts.Count == 0) return null;
        if (t < pts[0][2]) return pts[0][2] - t <= Slack.TotalSeconds ? new Spot(pts[0][0], pts[0][1], false) : null;
        if (t > pts[^1][2]) return t - pts[^1][2] <= Slack.TotalSeconds ? new Spot(pts[^1][0], pts[^1][1], false) : null;
        int lo = 0, hi = pts.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (pts[mid][2] <= t) lo = mid; else hi = mid;
        }
        var a = pts[lo];
        var b = pts[hi];
        var span = b[2] - a[2];
        if (span <= 0) return new Spot(a[0], a[1], false);
        var k = Math.Clamp((t - a[2]) / span, 0, 1);
        return new Spot(Math.Round(a[0] + (b[0] - a[0]) * k, 6), Math.Round(a[1] + (b[1] - a[1]) * k, 6), span > RoughGapS);
    }

    // ---------------- Vorschau ----------------

    /// <summary>
    /// Ein Foto der Vorschau. State: ok (wird verortet), outside (außerhalb der Strecke), readonly (Galerie-Ordner nur zum Lesen),
    /// unsupported (Art, die hier nicht beschrieben wird - HEIC, PNG, Video). Target: file (ins Foto) oder sidecar.
    /// </summary>
    public sealed record Item(string Path, string Name, string? Mime, string? Etag, long? Taken, string State, double? Lat, double? Lon, bool Rough, string? Target);

    public sealed record Preview(bool Timed, string? TrackError, int Photos, int Located, List<Item> Items);

    /// <summary>Fotos der Aktivität (mit Versatz der Kamera-Uhr in Sekunden): Zeitraum in UTC mit zehn Minuten davor und danach.</summary>
    internal static List<JournalApi.DayPhoto> PhotosDuring(UserSpace space, ServerConfig config, CoachGarmin.GarminActivity a, double offsetS = 0)
    {
        if (!DateTime.TryParseExact(a.Start, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return [];
        var from = new DateTimeOffset(Ics.ToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), space.Zone), TimeSpan.Zero).ToUnixTimeMilliseconds() - 600_000;
        var to = from + 1_200_000 + (long)((a.DurationS ?? 0) * 1000);
        var shift = (long)(offsetS * 1000);
        return JournalApi.PhotosOf(space, config, a.Day).Where(p => p.Taken + shift >= from && p.Taken + shift <= to).ToList();
    }

    public static async Task<Preview> PreviewAsync(UserSpace space, ServerConfig config, CoachGarmin.GarminActivity a, List<double[]> track, string? trackError,
        double offsetS, CancellationToken ct)
    {
        var pts = Timed(track);
        var photos = PhotosDuring(space, config, a, offsetS);
        var items = new List<Item>();
        var located = 0;
        var ext = new ExtCache(space, config);
        try
        {
            foreach (var p in WithPartners(space, config, photos))
            {
                ct.ThrowIfCancellationRequested();
                bool has;
                // Galerie-Ordner nicht mehr erreichbar oder nicht mehr freigegeben: dieses Foto auslassen
                try { has = p.Kind == "ext" ? await ext.HasGpsAsync(p, ct) : p.Lat != null; }
                catch (Exception e) when (e is KeyNotFoundException or UnauthorizedAccessException or IOException or HttpRequestException) { continue; }
                if (has) { located++; continue; }
                var target = PhotoMeta.IsJpeg(p.Name) ? "file" : PhotoMeta.UsesSidecar(p.Name) ? "sidecar" : null;
                var spot = p.Taken is long tk ? Locate(pts, tk / 1000.0 + offsetS) : null;
                var state = target == null ? "unsupported" : spot == null ? "outside" : p.Kind == "ext" && ext.ReadOnly(p) ? "readonly" : "ok";
                items.Add(new Item(p.Path, p.Name, p.Mime, p.Etag, p.Taken, state, spot?.Lat, spot?.Lon, spot?.Rough ?? false, target));
            }
        }
        finally { ext.Dispose(); }
        return new Preview(pts.Count > 1, trackError, photos.Count, located, items);
    }

    /// <summary>
    /// Die Fotos samt RAW/JPEG-Partnern mit gleichem Namen im selben Ordner (die Liste der Aktivität zeigt von solchen Paaren nur
    /// eins). Partner übernehmen die Aufnahmezeit, wenn sie selbst keine haben.
    /// </summary>
    private static List<JournalApi.DayPhoto> WithPartners(UserSpace space, ServerConfig config, List<JournalApi.DayPhoto> photos)
    {
        var list = new List<JournalApi.DayPhoto>(photos);
        var seen = photos.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        static string Key(string dir, string name) => dir + "\n" + Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        var want = photos.Select(p => Key(Dir(p.Path), p.Name)).ToHashSet(StringComparer.Ordinal);
        var taken = photos.GroupBy(p => Key(Dir(p.Path), p.Name)).ToDictionary(g => g.Key, g => g.First().Taken, StringComparer.Ordinal);

        // Eigene Ablage: die Ordner der Fotos durchsehen
        var meta = FileMeta.List(space.Db, null).ToDictionary(r => r.Path, StringComparer.Ordinal);
        foreach (var dir in photos.Where(p => p.Kind == "file").Select(p => Dir(p.Path)).Distinct())
            foreach (var f in FileStore.Children(space.Db, dir).Where(f => !f.IsDir && PhotoMeta.CanWrite(f.Name) && !seen.Contains(f.Path)))
                if (want.Contains(Key(dir, f.Name)) && seen.Add(f.Path))
                {
                    var m = meta.GetValueOrDefault(f.Path);
                    list.Add(new JournalApi.DayPhoto("file", f.Path, f.Name, f.Mime, f.Etag, m?.Taken ?? taken[Key(dir, f.Name)], m?.Lat, m?.Lon));
                }
        // Galerie-Ordner
        if (photos.Any(p => p.Kind == "ext"))
            foreach (var e in GalleryFolders.List(space, config))
            {
                var path = "ext:" + e.Source + "/" + e.Path;
                var name = e.Path[(e.Path.LastIndexOf('/') + 1)..];
                if (seen.Contains(path) || !PhotoMeta.CanWrite(name) || !want.Contains(Key(Dir(path), name))) continue;
                seen.Add(path);
                list.Add(new JournalApi.DayPhoto("ext", path, name, e.Mime, e.Size.ToString("x") + "-" + e.Modified.ToString("x"), e.Meta?.Taken ?? taken[Key(Dir(path), name)]));
            }
        return list;
    }

    /// <summary>Galerie-Ordner: Treiber je Quelle (einmal geöffnet), Ordnerinhalte, Ort aus Foto und Sidecar - gemerkt in gallery_ext_gps.</summary>
    private sealed class ExtCache(UserSpace space, ServerConfig config) : IDisposable
    {
        private readonly Dictionary<string, (IStorageDriver Driver, bool ReadOnly, GallerySource Src)> _open = [];
        private readonly Dictionary<string, List<DriverEntry>> _dirs = [];

        public (IStorageDriver Driver, bool ReadOnly, GallerySource Src, string Rel) Open(string path)
        {
            var (src, rel) = GalleryFolders.Resolve(space, config, path[4..]);
            if (!_open.TryGetValue(src.Id, out var o))
            {
                var (driver, ro) = ExternalFolders.Open(space, config, src.Mount);
                _open[src.Id] = o = (driver, ro, src);
            }
            return (o.Driver, o.ReadOnly, o.Src, rel);
        }

        public bool ReadOnly(JournalApi.DayPhoto p) => Open(p.Path).ReadOnly;

        public async Task<List<DriverEntry>> ListAsync(IStorageDriver driver, string source, string dir, CancellationToken ct)
        {
            if (!_dirs.TryGetValue(source + "/" + dir, out var l)) _dirs[source + "/" + dir] = l = await driver.ListAsync(dir, ct);
            return l;
        }

        /// <summary>Hat das Foto einen Ort? Bekannt aus der Tabelle, sonst aus Kopf und Sidecar gelesen (und gemerkt).</summary>
        public async Task<bool> HasGpsAsync(JournalApi.DayPhoto p, CancellationToken ct)
        {
            var (driver, _, src, rel) = Open(p.Path);
            if (GalleryFolders.Gps(space.Db, src.Id, rel) is { Known: true } k) return k.Lat != null;
            (double Lat, double Lon)? gps = null;
            try
            {
                var head = await driver.ReadRangeAsync(rel, 0, HeadBytes, ct);
                gps = PhotoMeta.GpsOf(new MemoryStream(head));
                if (gps == null && PhotoMeta.UsesSidecar(p.Name) && await SidecarAsync(driver, src, rel, ct) is { Text: { } x } && Sidecar.Read(Encoding.UTF8.GetBytes(x)) is { Lat: { } la, Lon: { } lo })
                    gps = (la, lo);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or KeyNotFoundException) { return false; }
            GalleryFolders.SetGps(space.Db, src.Id, rel, gps?.Lat, gps?.Lon);
            return gps != null;
        }

        /// <summary>Sidecar-Datei eines Fotos im Galerie-Ordner: Pfad (vorhanden oder neu) und Inhalt (null, wenn es keine gibt).</summary>
        public async Task<(string Path, string? Text)> SidecarAsync(IStorageDriver driver, GallerySource src, string rel, CancellationToken ct)
        {
            var dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
            var name = rel[(rel.LastIndexOf('/') + 1)..];
            var entries = await ListAsync(driver, src.Id, dir, ct);
            var sc = PhotoMeta.SidecarName(name, entries.Where(e => !e.IsDir).Select(e => e.Name));
            var path = StoragePath.Join(dir, sc);
            var have = entries.FirstOrDefault(e => !e.IsDir && e.Name == sc);
            if (have == null) return (path, null);
            if (have.Size > Sidecar.MaxBytes) throw new IOException("Die Sidecar-Datei ist ungewöhnlich groß.");
            await using var s = await driver.OpenReadAsync(path, ct);
            using var r = new StreamReader(s, Encoding.UTF8);
            return (path, await r.ReadToEndAsync(ct));
        }

        public void Dispose() { foreach (var o in _open.Values) o.Driver.Dispose(); }
    }

    // ---------------- Schreiben ----------------

    public sealed record Failure(string Path, string Name, string Error);

    /// <summary>Stand des Schreibens je Benutzer und Aktivität (nur im Speicher).</summary>
    public sealed class State
    {
        public int Total { get; init; }
        public int Done;
        public bool Running = true;
        public readonly ConcurrentQueue<Failure> Failed = new();
    }

    private static readonly ConcurrentDictionary<string, State> States = new();

    public static State? StateOf(UserSpace space, long activity) => States.GetValueOrDefault(space.User.Id + "|" + activity);

    /// <summary>Die bestätigten Fotos im Hintergrund verorten; false, wenn für diese Aktivität schon geschrieben wird.</summary>
    public static bool Start(UserSpace space, ServerConfig config, long activity, List<Item> items)
    {
        var key = space.User.Id + "|" + activity;
        var state = new State { Total = items.Count };
        if (States.TryGetValue(key, out var old) && old.Running) return false;
        States[key] = state;
        _ = Task.Run(async () =>
        {
            using var job = Activity.Start(space.User.Id, Activity.PhotoWrite, "Fotos verorten", items.Count);
            var ext = new ExtCache(space, config);
            try
            {
                foreach (var it in items)
                {
                    try { await WriteAsync(space, config, ext, it); }
                    // Im Hintergrund: jeder Fehler bleibt bei seinem Foto (geändert inzwischen, Speicher voll, Ordner weg …), die anderen gehen weiter
                    catch (Exception e)
                    { state.Failed.Enqueue(new Failure(it.Path, it.Name, e is Pim.PreconditionFailedException ? "Die Datei wurde inzwischen geändert." : e.Message)); }
                    Interlocked.Increment(ref state.Done);
                    job.Progress(state.Done);
                }
            }
            finally { ext.Dispose(); state.Running = false; }
        });
        return true;
    }

    /// <summary>Ein Foto verorten (Stelle aus der Vorschau, vom Server neu berechnet).</summary>
    private static async Task WriteAsync(UserSpace space, ServerConfig config, ExtCache ext, Item it)
    {
        var fields = new PhotoMeta.Fields(it.Lat, it.Lon);
        if (!it.Path.StartsWith("ext:", StringComparison.Ordinal))
        {
            var db = space.Db;
            var f = FileStore.Get(db, it.Path) ?? throw new KeyNotFoundException("Die Datei gibt es nicht mehr.");
            if (PhotoMeta.IsJpeg(f.Name))
            {
                var data = FileStore.Data(db, f.Path) ?? throw new KeyNotFoundException("Die Datei gibt es nicht mehr.");
                if (PhotoMeta.GpsOf(new MemoryStream(data)) != null) throw new InvalidOperationException("Das Foto hat schon einen Ort.");
                // Mit ETag: hat sich die Datei inzwischen geändert, wird nichts überschrieben
                FileStore.Put(db, f.Path, PhotoMeta.WriteJpeg(data, fields), f.Mime, ifMatch: f.Etag);
            }
            else
            {
                var sc = FileMeta.SidecarOf(db, f.Path);
                var old = sc != null ? FileStore.Get(db, sc) : null;
                if (old is { Size: > Sidecar.MaxBytes }) throw new IOException("Die Sidecar-Datei ist ungewöhnlich groß.");
                var text = old != null && FileStore.Data(db, old.Path) is { } x ? Encoding.UTF8.GetString(x) : null;
                var dir = FileStore.Parent(f.Path) ?? "";
                var path = sc ?? (dir.Length == 0 ? "" : dir + "/") + Path.GetFileNameWithoutExtension(f.Name) + ".xmp";
                FileStore.Put(db, path, Encoding.UTF8.GetBytes(PhotoMeta.WriteXmp(text, fields, packet: text == null)), "application/rdf+xml",
                    ifMatch: old?.Etag, ifNoneMatch: old == null ? "*" : null);
            }
            FileMeta.Refresh(space, f.Path);
            return;
        }

        var (driver, ro, src, rel) = ext.Open(it.Path);
        if (ro) throw new UnauthorizedAccessException("Dieser Ordner ist nur zum Lesen eingebunden.");
        if (PhotoMeta.IsJpeg(rel))
        {
            // Original aus der Arbeitskopie (geprüft gegen Größe und Zeit auf dem Speicher), geschrieben im Hintergrund mit Wiederholungen
            var data = await PhotoWork.GetAsync(space, config, it.Path, CancellationToken.None);
            if (PhotoMeta.GpsOf(new MemoryStream(data)) is { } has)
            {
                GalleryFolders.SetGps(space.Db, src.Id, rel, has.Lat, has.Lon);
                throw new InvalidOperationException("Das Foto hat schon einen Ort.");
            }
            var result = PhotoMeta.WriteJpeg(data, fields);
            if (PhotoWork.Ready) PhotoWork.WriteLater(space, src.Id, rel, rel, result);
            else
            {
                await driver.WriteAsync(rel, new MemoryStream(result), CancellationToken.None);
                var st = await driver.StatAsync(rel, CancellationToken.None);
                GalleryFolders.Written(space, config, src, rel, result, st is { IsDir: false } s1 && s1.Size == result.Length ? s1.Modified : DateTime.UtcNow);
            }
        }
        else
        {
            var (path, text) = await ext.SidecarAsync(driver, src, rel, CancellationToken.None);
            await driver.WriteAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(PhotoMeta.WriteXmp(text, fields, packet: text == null))), CancellationToken.None);
        }
        GalleryFolders.SetGps(space.Db, src.Id, rel, it.Lat, it.Lon);
    }
}
