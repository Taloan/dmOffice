using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Storage;

namespace DmOffice.Gallery;

/// <summary>
/// Mittlere Vorschau (1280 px, Kennung galmedium) der Fotos in Galerie-Ordnern: ohne „Vorschaubilder vorab erzeugen“ gab es bisher
/// nur das kleine Vorschaubild (256 px) - die Großansicht musste das ganze Original über die Freigabe holen (CIFS über das Internet:
/// mehrere Sekunden). Sie entsteht beim Einlesen aus den Bytes, die ohnehin gelesen werden, beim Ablegen in einen Galerie-Ordner aus
/// der lokalen Datei und für den Bestand nachts im Hintergrund (<see cref="GalleryMediumJob"/>). Ablage wie die großen Vorschauen
/// (verschlüsselt, Stand = Größe und Änderungszeit des Originals, Server-Ordner gemeinsam) unter data/cache/gallery-medium.
/// Mit eingeschalteten großen Vorschauen (2048 px) entsteht keine mittlere - die große reicht.
/// </summary>
public static partial class GalleryFolders
{
    public const int MediumSize = 1280;

    /// <summary>Ablage der mittleren Vorschauen (beim Start gesetzt; in Tests ohne oder eigene).</summary>
    public static GalleryProxies? Medium { get; set; }

    /// <summary>Soll für diesen Galerie-Ordner eine mittlere Vorschau entstehen?</summary>
    internal static bool MediumWanted(GallerySource src) => Medium != null && !src.Proxies && Features.On(Features.GalleryMedium);

    /// <summary>Eingebettete Vorschau eines großen JPEG (MPF) erst ab dieser Kantenlänge - kleinere wären unscharf; dann lieber wie bisher aus dem Original.</summary>
    private const int MinPreviewEdge = 1200;

    /// <summary>Mittlere Vorschau aus den gelesenen Bytes ablegen (RAW: eingebettetes JPEG; TIFF: <paramref name="img"/> als JPEG). Fehler: keine.</summary>
    /// <param name="head">nur der Kopf der Datei: das eingebettete JPEG eines RAW muss darin vollständig liegen (sonst unten grau)</param>
    internal static void PutMedium((string A, string B) scope, string rel, long stamp, byte[] bytes, byte[] img, bool raw, bool head = false)
    {
        if (Medium == null) return;
        try
        {
            byte[]? m;
            // RAW: das eingebettete JPEG, wie bei der großen Vorschau (aus dem Original wird es nicht besser)
            if (raw && head) m = Thumbs.EmbeddedJpeg(bytes) is { } jpg && Complete(jpg) ? Thumbs.FromPreview(jpg, bytes, MediumSize) : null;
            else m = raw ? Thumbs.FromRaw(bytes, MediumSize) : Thumbs.FromBytes(img, MediumSize);
            if (m != null) Medium.Put(scope.A, scope.B, rel, stamp, m);
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
    }

    /// <summary>Lässt sich das JPEG ganz dekodieren (nicht abgeschnitten)? Verkleinert auf ein Achtel - geht schnell.</summary>
    private static bool Complete(byte[] jpg)
    {
        using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(jpg));
        if (codec == null) return false;
        var dims = codec.GetScaledDimensions(0.125f);
        var info = new SkiaSharp.SKImageInfo(dims.Width, dims.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using var bmp = new SkiaSharp.SKBitmap(info);
        return codec.GetPixels(info, bmp.GetPixels()) == SkiaSharp.SKCodecResult.Success;
    }

    /// <summary>Mittlere Vorschau aus einer eingebetteten JPEG-Vorschau, wenn sie groß genug ist (<paramref name="file"/>: Datei oder Kopf für die Drehung).</summary>
    internal static void PutMediumPreview((string A, string B) scope, string rel, long stamp, byte[] jpg, byte[] file)
    {
        if (Medium == null) return;
        try
        {
            using (var codec = SkiaSharp.SKCodec.Create(new MemoryStream(jpg)))
                if (codec == null || Math.Max(codec.Info.Width, codec.Info.Height) < MinPreviewEdge) return;
            if (Thumbs.FromPreview(jpg, file, MediumSize) is { } m) Medium.Put(scope.A, scope.B, rel, stamp, m);
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
    }

    /// <summary>Vorhandene mittlere Vorschau, nur wenn der Schalter an ist.</summary>
    internal static byte[]? MediumOf((string A, string B) scope, string rel, long stamp) =>
        Features.On(Features.GalleryMedium) ? Medium?.Get(scope.A, scope.B, rel, stamp) : null;

    /// <summary>Fotos eines Galerie-Ordners ohne große und ohne mittlere Vorschau (neueste zuerst).</summary>
    public static List<(string Path, long Stamp)> MissingMedium(UserSpace space, ServerConfig config, GallerySource src)
    {
        Ensure(space.Db);
        if (Medium == null) return [];
        var scope = ProxyScope(space, config, src);
        return space.Db.Query("SELECT path, size, modified FROM gallery_ext WHERE source = $s AND stage = 2 AND big IS NULL ORDER BY modified DESC",
                r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("$s", src.Id))
            .Where(x => KindOf(x.Item1) == "image" && x.Item2 > 0 && x.Item2 <= MaxImageBytes)
            .Select(x => (x.Item1, GalleryProxies.Stamp(x.Item2, x.Item3)))
            .Where(x => !Medium.Has(scope.A, scope.B, x.Item1, x.Item2)
                && Proxies?.Has(scope.A, scope.B, x.Item1, x.Item2) != true
                && !(scope.A == GalleryProxies.SharedScope && Proxies?.Has(space.User.Id, src.Id, x.Item1, x.Item2) == true)
                && !PhotoWork.IsPending(src.Id, x.Item1)).ToList();
    }

    /// <summary>
    /// Mittlere Vorschauen eines Galerie-Ordners nachholen, eine nach der anderen mit Pause dazwischen (die Freigabe soll für die
    /// Person nutzbar bleiben). Höchstens <paramref name="max"/> Fotos und bis <paramref name="until"/>; hält an der Prüfstelle der
    /// Jobsteuerung (Anhalten, Überspringen, Abbrechen). Gibt die Zahl der gelesenen Fotos zurück (auch fehlgeschlagene - sie zählen
    /// gegen die Grenze der Nacht).
    /// </summary>
    public static async Task<int> MakeMediumsAsync(UserSpace space, ServerConfig config, GallerySource src, int max, DateTime until, Activity.Job job, CancellationToken ct, TimeSpan? pause = null)
    {
        if (max <= 0 || !MediumWanted(src)) return 0;
        var missing = MissingMedium(space, config, src);
        if (missing.Count == 0) return 0;
        var scope = ProxyScope(space, config, src);
        var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
        using (driver)
        {
            var total = Math.Min(missing.Count, max);
            var done = 0;
            job.Progress(0, total, src.Path);
            foreach (var (path, stamp) in missing.Take(total))
            {
                await JobControl.CheckAsync(space.User.Id, Activity.GalleryMedium, ct);
                if (DateTime.UtcNow >= until) break;
                // Ordner inzwischen anders eingestellt (große Vorschauen an, neu zugeordnet, entfernt) oder Einlesen läuft: aufhören
                if (done % 25 == 0 && (Of(space.Db).FirstOrDefault(s => s.Id == src.Id) is not { } cur || !MediumWanted(cur) || cur.Mount != src.Mount || cur.Path != src.Path
                    || GalleryScanJob.IsBusy(space.User.Id, src.Id) || GalleryRelink.IsBusy(space.User.Id, src.Id))) break;
                // Inzwischen beim Öffnen entstanden
                if (Medium!.Has(scope.A, scope.B, path, stamp) || Proxies?.Has(scope.A, scope.B, path, stamp) == true) { job.Progress(++done, total, src.Path); continue; }
                try
                {
                    var bytes = await ReadAllAsync(driver, path, ct);
                    var img = Thumbs.IsTiff(path) ? await Thumbs.TiffJpegAsync(bytes, ct) ?? [] : bytes;
                    PutMedium(scope, path, stamp, bytes, img, Thumbs.IsRaw(path));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or ArgumentException or InvalidOperationException) { }
                job.Progress(++done, total, src.Path);
                if (pause is { } p && p > TimeSpan.Zero) await Task.Delay(p, ct);
            }
            return done;
        }
    }

    /// <summary>Server-Ordner nicht mehr freigegeben: gemeinsame mittlere Vorschauen weg.</summary>
    private static void PruneMedium(HashSet<string> freed)
    {
        if (Medium != null) foreach (var m in Medium.SharedMounts().Where(m => !freed.Contains(m))) Medium.RemoveShared(m);
    }
}

/// <summary>
/// Holt nachts die mittleren Vorschauen für den Bestand nach (Galerie-Ordner ohne große Vorschauen): zwischen 1 und 6 Uhr, je Konto
/// höchstens <see cref="PerNight"/> Fotos je Nacht, eines nach dem anderen. In der Aktivität mit Fortschritt; anhalten, überspringen
/// und abbrechen wie das Einlesen. Beim Öffnen eines Fotos ohne Vorschau entsteht sie weiterhin sofort (große Vorschau).
/// </summary>
public sealed class GalleryMediumJob(Spaces spaces, ServerConfig config, ILogger<GalleryMediumJob> log) : BackgroundService
{
    /// <summary>Höchstens so viele Fotos je Konto und Nacht.</summary>
    public static int PerNight { get; set; } = 3000;
    /// <summary>Pause nach jedem Foto - die Freigabe bleibt für anderes nutzbar.</summary>
    public static TimeSpan Pause { get; set; } = TimeSpan.FromMilliseconds(300);
    public const int NightStart = 1, NightEnd = 6;
    private const string CountKey = "gallery.medium.night";

    /// <summary>Liegt dieser Zeitpunkt (Ortszeit des Servers) im nächtlichen Fenster?</summary>
    public static bool Night(DateTime local) => local.Hour >= NightStart && local.Hour < NightEnd;

    /// <summary>Beginn des nächsten Fensters (UTC) - bis dahin ruht der Job nach „Überspringen“.</summary>
    public static DateTime NextNight(DateTime nowLocal)
    {
        var start = nowLocal.Date.AddHours(NightStart);
        if (nowLocal >= start) start = start.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), TimeZoneInfo.Local);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Night(DateTime.Now)) await RunOnceAsync(DateTime.Now, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning(e, "Mittlere Vorschauen: Durchgang fehlgeschlagen"); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Ein Durchgang über alle Konten und Ordner; <paramref name="nowLocal"/> bestimmt Nacht und Zähler.</summary>
    public async Task<int> RunOnceAsync(DateTime nowLocal, CancellationToken ct)
    {
        if (GalleryFolders.Medium == null || !Features.On(Features.Gallery) || !Features.On(Features.GalleryMedium)) return 0;
        // Ende des Fensters in UTC (Ortszeit des Servers)
        var left = nowLocal.Date.AddHours(NightEnd) - nowLocal;
        var until = DateTime.UtcNow + (left > TimeSpan.Zero ? left : TimeSpan.FromHours(1));
        var night = (nowLocal.Hour < NightStart ? nowLocal.Date.AddDays(-1) : nowLocal.Date).ToString("yyyy-MM-dd");
        var all = 0;
        foreach (var space in spaces.All())
        {
            var user = space.User.Id;
            if (JobControl.Blocked(user, Activity.GalleryMedium)) continue;
            var sources = GalleryFolders.Of(space.Db).Where(s => s.LastScan != null && GalleryFolders.MediumWanted(s)).ToList();
            if (sources.Count == 0) continue;
            var used = Used(space.Db, night);
            if (used >= PerNight) continue;
            using var run = JobControl.Run(user, Activity.GalleryMedium);
            using var job = Activity.Start(user, Activity.GalleryMedium);
            try
            {
                foreach (var src in sources)
                {
                    if (used >= PerNight || DateTime.UtcNow >= until) break;
                    if (GalleryScanJob.IsRunning(user, src.Id) || GalleryRelink.IsBusy(user, src.Id)) continue;
                    try
                    {
                        var n = await GalleryFolders.MakeMediumsAsync(space, config, src, PerNight - used, until, job, ct, Pause);
                        used += n; all += n;
                        space.Db.Set(CountKey, night + ":" + used);
                        if (n > 0) log.LogInformation("Mittlere Vorschauen {User}/{Source}: {Count} Fotos gelesen", space.User.Login, src.Id, n);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or ArgumentException)
                    {
                        job.Fail($"{src.Path}: {e.Message}");
                    }
                }
            }
            catch (JobControl.Stopped) { }
        }
        return all;
    }

    /// <summary>In dieser Nacht schon gelesene Fotos des Kontos.</summary>
    private static int Used(UserDb db, string night) =>
        db.Get(CountKey) is { } v && v.StartsWith(night + ":", StringComparison.Ordinal) && int.TryParse(v[(night.Length + 1)..], out var n) ? n : 0;
}
