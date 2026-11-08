using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace DmOffice.Core;

/// <summary>Ein entfernter Ablageort fuer Blobs - heute S3-kompatibel (Garage, MinIO, Hetzner, Backblaze ...), im Test ein Ordner.</summary>
public interface IBlobRemote : IDisposable
{
    /// <summary>Alle Schluessel unter dem Praefix.</summary>
    Task<HashSet<string>> ListAsync(string prefix, CancellationToken ct);
    /// <summary>Alle Schluessel unter dem Praefix mit Groesse.</summary>
    Task<List<(string Key, long Size)>> ListSizesAsync(string prefix, CancellationToken ct);
    Task UploadAsync(string file, string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    /// <summary>Objekt in eine Datei laden.</summary>
    Task DownloadAsync(string key, string file, CancellationToken ct);
    /// <summary>Groesse eines Objekts in Bytes; -1: gibt es nicht.</summary>
    Task<long> SizeAsync(string key, CancellationToken ct);
    /// <summary>Ein Ausschnitt eines Objekts.</summary>
    Task<byte[]> ReadRangeAsync(string key, long offset, int count, CancellationToken ct);
    /// <summary>Kopie im selben Speicher, ohne die Bytes zu holen; false: kann dieser Speicher nicht oder nicht für dieses Objekt.</summary>
    Task<bool> TryCopyAsync(string from, string to, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>S3-kompatibler Speicher ueber das AWS-SDK: pfadbasierte Adressen (Garage, MinIO), Prüfsummen nur wenn noetig.</summary>
public sealed class S3Remote(AmazonS3Client client, string bucket) : IBlobRemote, IDisposable
{
    public static S3Remote Create(string endpoint, string region, string bucket, string accessKey, string secretKey) => new(new AmazonS3Client(
        new BasicAWSCredentials(accessKey, secretKey),
        new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region.Trim(),
            ForcePathStyle = true,
            // SDK 4 schickt sonst zusaetzliche Pruefsummen (aws-chunked), die nicht jeder S3-kompatible Server kennt
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromMinutes(10),
        }), bucket);

    public async Task<HashSet<string>> ListAsync(string prefix, CancellationToken ct) =>
        (await ListSizesAsync(prefix, ct)).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

    public async Task<List<(string Key, long Size)>> ListSizesAsync(string prefix, CancellationToken ct)
    {
        var keys = new List<(string, long)>();
        var req = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response res;
        do
        {
            res = await client.ListObjectsV2Async(req, ct);
            foreach (var o in res.S3Objects ?? []) keys.Add((o.Key, o.Size ?? 0));
            req.ContinuationToken = res.NextContinuationToken;
        } while (res.IsTruncated == true);
        return keys;
    }

    public async Task UploadAsync(string file, string key, CancellationToken ct)
    {
        // Ab 16 MB in Teilen (Videos, grosse Archive) - das erledigt TransferUtility
        using var transfer = new TransferUtility(client);
        await transfer.UploadAsync(new TransferUtilityUploadRequest { BucketName = bucket, Key = key, FilePath = file }, ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct) => client.DeleteObjectAsync(bucket, key, ct);

    public async Task<long> SizeAsync(string key, CancellationToken ct)
    {
        try { return (await client.GetObjectMetadataAsync(bucket, key, ct)).ContentLength; }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return -1; }
    }

    public async Task<byte[]> ReadRangeAsync(string key, long offset, int count, CancellationToken ct)
    {
        using var res = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ByteRange = new ByteRange(offset, offset + count - 1) }, ct);
        using var ms = new MemoryStream(count);
        await res.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task DownloadAsync(string key, string file, CancellationToken ct)
    {
        using var res = await client.GetObjectAsync(bucket, key, ct);
        await using var f = File.Create(file);
        await res.ResponseStream.CopyToAsync(f, ct);
    }

    public async Task<bool> TryCopyAsync(string from, string to, CancellationToken ct)
    {
        try { await client.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = from, DestinationBucket = bucket, DestinationKey = to }, ct); return true; }
        catch (AmazonS3Exception) { return false; }   // fehlt dort, zu groß oder nicht unterstützt: der Aufrufer überträgt selbst
    }

    /// <summary>Fehler fuer Menschen: bei S3 Statuscode und Fehlercode („403 AccessDenied: …“) samt Hinweis, sonst die innerste Meldung.</summary>
    public static string Describe(Exception e)
    {
        if (e is AggregateException a && a.InnerException != null) e = a.InnerException;
        if (e is AmazonS3Exception s3)
        {
            var hint = s3.ErrorCode switch
            {
                "AccessDenied" => " – hat der Schlüssel Lese- und Schreibrechte auf den Bucket?",
                "InvalidAccessKeyId" => " – den Zugangsschlüssel gibt es dort nicht.",
                "SignatureDoesNotMatch" => " – passt der geheime Schlüssel?",
                "NoSuchBucket" => " – den Bucket gibt es nicht.",
                "AuthorizationHeaderMalformed" => " – stimmt die Region?",
                _ => "",
            };
            return $"{(int)s3.StatusCode} {s3.ErrorCode}: {s3.Message}{hint}";
        }
        return e.GetBaseException() is System.Security.Cryptography.CryptographicException ? Unreadable : e.GetBaseException().Message;
    }

    /// <summary>Authentisierte Verschlüsselung kann beides nicht unterscheiden - also beides nennen.</summary>
    public const string Unreadable = "Der Inhalt lässt sich nicht entschlüsseln: Er ist beschädigt oder gehört zu einem anderen Hauptschlüssel.";

    public void Dispose() => client.Dispose();
}

/// <summary>
/// Kopie der Dateiinhalte in einen S3-Speicher (Storage Extender): gelesen wird weiter lokal,
/// jeder Blob geht zusaetzlich nach S3 - so liegt alles auch an einem zweiten Ort. Die Blobs sind schon verschluesselt
/// (BlobStore) und tragen Zufallsnamen; der Anbieter sieht weder Inhalte noch Dateinamen.
/// Schluessel: {Praefix}{Benutzer}/{xx}/{Blob}. Was lokal verschwunden ist (Papierkorb geleert, Version abgelaufen),
/// wird in S3 erst nach einer Frist geloescht - ein Versehen oder ein halb wiederhergestelltes Datenverzeichnis kostet so nichts.
/// </summary>
public sealed class S3Mirror(Spaces spaces, ServerConfig config, DataPaths paths, ILogger<S3Mirror> log) : BackgroundService
{
    public static readonly TimeSpan DeleteAfter = TimeSpan.FromDays(7);
    /// <summary>Liegt im Datenverzeichnis nach einer Wiederherstellung aus S3: beim Start alle Inhalte holen.</summary>
    public const string RestoreFlag = "restore-from-s3.flag";

    /// <summary>
    /// So lange bleibt am Ziel, was hier endgueltig geloescht wurde: 7 Tage, mit Core-Backup so lange wie seine Staende reichen.
    /// Bis die erste Kopie des Files-Backups auf diesem Ziel vollstaendig ist, gilt noch dessen Haltedauer (TimeSpan.MaxValue: unbegrenzt).
    /// </summary>
    public static TimeSpan KeepDeleted(ServerSettings s)
    {
        if (!s.S3Backup) return DeleteAfter;
        if (BackupRules.FilesOn(s) && !s.FilesBackupOwnTarget && !s.FilesBackupCopied)
            return BackupRules.HoldDays(s) is int days ? TimeSpan.FromDays(days) : TimeSpan.MaxValue;
        return TimeSpan.FromDays(Math.Max(7, BackupRules.MinHoldDays(s)));
    }
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _kick = new(0);

    public sealed record State(bool Running, DateTime? LastRun, string? LastError, long Local, long Mirrored, long Uploaded, long Deleted, long Pending,
        RestoreState? Restore = null, CacheState? Cache = null, DateTime? OtherServer = null, bool LowSpace = false);
    /// <summary>Zwischenspeicher: belegt hier, Grenze, verdraengt beim letzten Durchgang, seit dem Start aus S3 geholt.</summary>
    public sealed record CacheState(long Bytes, long Limit, long Evicted, long Fetched, long Remote, long Streamed = 0, long Max = 0, long Waiting = 0,
        long Both = 0, long BothBytes = 0, long RemoteBytes = 0, long FixedBytes = 0, long PendingCount = 0, long PendingBytes = 0,
        long ThumbBytes = 0, IReadOnlyList<BusyPart>? Busy = null);
    /// <summary>Noch nicht Verarbeitetes je Grund (siehe <see cref="UnprocessedWhy"/>), die groessten zuerst.</summary>
    public sealed record BusyPart(string Reason, string? Ext, long Count, long Bytes);
    /// <summary>„Fehlendes aus S3 holen“: lokal fehlend, geholt, in S3 auch nicht da, gescheitert.</summary>
    public sealed record RestoreState(bool Running, DateTime? Finished, long Missing, long Restored, long NotThere, long Failed, string? Error);
    private State _state = new(false, null, null, 0, 0, 0, 0, 0);
    private volatile bool _restoreWanted;
    public State Status => _state;

    /// <summary>Sofort einen Durchgang (nach dem Einrichten oder auf Knopfdruck).</summary>
    public void Kick() => _kick.Release();

    private readonly List<TaskCompletionSource> _passWaiters = new();

    /// <summary>
    /// Auf einen vollstaendigen Abgleich warten, der erst nach diesem Aufruf beginnt - das Core-Backup sichert danach die
    /// Datenbanken und kennt so nur Inhalte, die schon am Ziel liegen.
    /// </summary>
    public Task NextPassAsync(CancellationToken ct)
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_passWaiters) _passWaiters.Add(t);
        Kick();
        return t.Task.WaitAsync(ct);
    }

    /// <summary>Welche Inhalte dieses Benutzers beim letzten Abgleich am Ziel lagen - null: noch kein Abgleich mit diesem Ziel.</summary>
    public IReadOnlyCollection<string>? AtTarget(string userId) =>
        _inS3Where == LocationOf(config.Current) && _inS3.TryGetValue(userId, out var d) ? d.Keys.ToList() : null;

    /// <summary>Nur noch fuer alte Einstellungen und Tests: frueher die Betriebsart.</summary>
    public const string ModeMirror = "mirror", ModeCache = "cache";
    /// <summary>
    /// Der Storage Extender kennt nur ein System: Am Ziel liegen alle Dateien, hier die zuletzt genutzten. Passen alle in die Grenzen
    /// (Begrenzung, maximale Nutzung des Volumes), liegen alle auch hier, und Fehlendes kommt von selbst zurueck.
    /// </summary>
    public static bool CacheMode(ServerSettings s) => s.S3Mirror;
    /// <summary>Begrenzt? Alte Einstellungen: Betriebsart cache hiess begrenzt, mirror nicht.</summary>
    public static bool Limited(ServerSettings s) => (s.S3CacheLimited ?? s.S3Mode == ModeCache) && s.S3CacheTargetGb > 0;
    private const long GB = 1024L * 1024 * 1024;
    /// <summary>Zielwert: darauf verdraengt der Abgleich in Ruhe.</summary>
    public static long Target(ServerSettings s) => Limited(s) ? (long)(s.S3CacheTargetGb * GB) : long.MaxValue;
    /// <summary>Nur fuer Tests: Groesse und freier Platz des lokalen Speichervolumens.</summary>
    internal static Func<string, (long Total, long Free)>? VolumeOverride;

    /// <summary>
    /// Groesse und freier Platz des Volumes, auf dem <paramref name="dir"/> liegt (Array, Pool, Platte) - unter Linux genau
    /// dieses Verzeichnisses, nicht der Wurzel: im Container waere das das Dateisystem des Images, nicht das eingehaengte /data.
    /// </summary>
    public static (long Total, long Free) VolumeOf(string dir)
    {
        if (VolumeOverride != null) return VolumeOverride(dir);
        try
        {
            Directory.CreateDirectory(dir);
            var full = Path.GetFullPath(dir);
            var d = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full);
            return (d.TotalSize, d.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return (0, long.MaxValue); }
    }

    /// <summary>der frei zu lassende Speicherplatz in Bytes: so viel bleibt auf dem lokalen Volume immer frei - die Datenbanken brauchen Luft.</summary>
    public long Reserve() => ReserveOf(config.Current, VolumeOf(paths.Blobs).Total);

    /// <summary>Frei zu lassen in Bytes: der eingestellte Wert in GB, aus aelteren Einstellungen der Anteil am Volume.</summary>
    public static long ReserveOf(ServerSettings s, long total)
    {
        if (s.S3MinFreeGb is double gb) return (long)(gb * GB);
        return total <= 0 ? GB : (long)(total / 100.0 * Math.Clamp(s.S3MinFreePercent, 1, 50));
    }

    /// <summary>Vorgabe beim ersten Einschalten: 50 GB, hoechstens ein Fuenftel des Volumes, mindestens 1 GB.</summary>
    public static double DefaultMinFreeGb(long total) =>
        total <= 0 ? 50 : Math.Max(1, Math.Round(Math.Min(50, total / 5.0 / GB)));

    /// <summary>Das lokale Speichervolumen, auf dem die Dateiinhalte liegen.</summary>
    public (long Total, long Free) LocalVolume() => VolumeOf(paths.Blobs);

    /// <summary>So viel darf lokal noch geschrieben werden, bis die Reserve erreicht ist (negativ: schon darunter).</summary>
    public long Headroom()
    {
        var (_, free) = VolumeOf(paths.Blobs);
        return free == long.MaxValue ? long.MaxValue : free - Reserve();
    }

    /// <summary>
    /// Noch nicht fertig verarbeitet - darf nicht verdraengt werden, sonst muesste die Verarbeitung es wieder aus S3 holen:
    /// ohne Pruefsumme, in der Warteschlange der Volltextsuche, oder (Bild, PDF) ohne Vorschaubild.
    /// </summary>
    public static HashSet<string> Unprocessed(UserDb db, bool text = true) => UnprocessedWhy(db, text).Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Was noch nicht fertig verarbeitet ist und warum (der erste passende Grund): hash = Pruefsumme fehlt, hashOld = dasselbe bei
    /// Versionen und Papierkorb, text = wartet auf die Volltextsuche, edited = bearbeitetes Foto ohne kleines Vorschaubild,
    /// thumb = Vorschaubild fehlt; bei den beiden letzten mit Dateiendung.
    /// </summary>
    internal static Dictionary<string, (string Reason, string? Ext)> UnprocessedWhy(UserDb db, bool text = true)
    {
        var why = new Dictionary<string, (string Reason, string? Ext)>(StringComparer.Ordinal);
        foreach (var table in UserDb.HashTables)
            foreach (var blob in db.Query($"SELECT blob FROM {table} WHERE blob IS NOT NULL AND sha256 IS NULL", r => r.GetString(0)))
                why.TryAdd(blob, (table == "files" ? "hash" : "hashOld", null));
        if (text) try
            {
                foreach (var blob in db.Query("SELECT f.blob FROM text_queue q JOIN files f ON q.kind = 'file' AND f.path = q.ref WHERE f.blob IS NOT NULL", r => r.GetString(0)))
                    why.TryAdd(blob, ("text", null));
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { }   // Volltextsuche nie eingeschaltet
        HashSet<string>? edited = null;
        foreach (var (id, name, mime, size, blob) in db.Query(
                     "SELECT f.fileid, f.path, f.mime, f.size, f.blob FROM files f WHERE f.is_dir = 0 AND f.blob IS NOT NULL AND NOT EXISTS " +
                     "(SELECT 1 FROM file_thumbs t WHERE t.fileid = f.fileid AND t.size = " + Dav.Thumbs.Small + " AND " + Dav.Thumbs.CurrentSql + ")",
                     r => (r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3), r.GetString(4))))
            if (Dav.Thumbs.Expected(name, mime, size))
            {
                edited ??= Dav.PhotoEdits.Fingerprints(db).Keys.ToHashSet(StringComparer.Ordinal);
                why.TryAdd(blob, (edited.Contains("f:" + id) ? "edited" : "thumb", Path.GetExtension(name).ToLowerInvariant()));
            }
        return why;
    }

    /// <summary>
    /// Fuer FileStore.CacheRoom: so viel darf lokal noch abgelegt werden, bis der frei zu lassende Speicherplatz erreicht ist - nur mit
    /// Storage Extender. Knapp geworden, wird sofort Verarbeitetes bereinigt, auch unter die Zielgroesse.
    /// </summary>
    public long CacheRoom()
    {
        var s = config.Current;
        if (!s.S3Mirror) return long.MaxValue;
        var room = Headroom();
        if (CacheMode(s) && room < GB) { FreeUp(GB); room = Headroom(); }
        return Math.Max(0, room);
    }

    /// <summary>Den Cache so weit bereinigen, dass <paramref name="wanted"/> Bytes bis zur Reserve Platz haben.</summary>
    private void FreeUp(long wanted)
    {
        var shortBy = wanted - Headroom();
        if (shortBy > 0) MakeRoom(Math.Max(0, Interlocked.Read(ref _cacheBytes) - shortBy), 0);
    }

    /// <summary>Fuer BlobStore.Written: neu Abgelegtes zaehlen; ueber dem Zielwert den naechsten Abgleich anstossen.</summary>
    public void Wrote(long bytes)
    {
        if (!CacheMode(config.Current)) return;
        if (Interlocked.Add(ref _cacheBytes, bytes) > Target(config.Current)) Kick();
    }

    /// <summary>
    /// Dateiinhalte, auf die dmOffice zeigt, die aber nicht hier liegen (verdrängt, Zurückholen noch nicht fertig) -
    /// Anzahl und Größe. Solange das nicht 0 ist, darf nichts den Weg zum Storage Extender abschneiden: nicht ausschalten,
    /// keine Zugangsdaten löschen, kein anderes Ziel.
    /// </summary>
    public (long Count, long Bytes) NotHome()
    {
        long count = 0, bytes = 0;
        foreach (var space in spaces.All())
        {
            var db = space.Db;
            var here = db.Blobs.List().Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            var missing = Referenced(db).Where(id => !here.Contains(id)).ToHashSet(StringComparer.Ordinal);
            if (missing.Count == 0) continue;
            count += missing.Count;
            // Größe aus der Datenbank (je Inhalt einmal)
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in new[] { "files", "file_versions", "file_trash_items" })
                foreach (var (blob, size) in db.Query($"SELECT blob, size FROM {t} WHERE blob IS NOT NULL", r => (r.GetString(0), r.IsDBNull(1) ? 0L : r.GetInt64(1))))
                    if (missing.Contains(blob) && seen.Add(blob)) bytes += size;
        }
        return (count, bytes);
    }

    /// <summary>Freier Platz dort, wo die Dateiinhalte hier liegen.</summary>
    public long FreeHere() => VolumeOf(paths.Blobs).Free;

    /// <summary>Auf diese Inhalte zeigt die Datenbank (Dateien, Versionen, Papierkorb, Vorschaubilder).</summary>
    public static HashSet<string> Referenced(UserDb db) =>
        db.Query(string.Join(" UNION ", UserDb.BlobTables.Select(t => $"SELECT blob FROM {t} WHERE blob IS NOT NULL")), r => r.GetString(0))
            .Where(BlobStore.ValidId).ToHashSet(StringComparer.Ordinal);

    /// <summary>Vorschaubilder bleiben immer hier - die Ordner- und Fotoansicht soll nie auf S3 warten.</summary>
    private static HashSet<string> Thumbs(UserDb db) =>
        db.Query("SELECT blob FROM file_thumbs WHERE blob IS NOT NULL", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _fetching = new();
    private IBlobRemote? _fetchRemote;
    private string? _fetchConfig;

    /// <summary>
    /// Rueckruf fuer BlobStore.Fetch: einen verdraengten Inhalt beim Oeffnen aus S3 holen (Web, App, WebDAV, Office ...).
    /// Wer gleichzeitig denselben Inhalt will, wartet auf den ersten. Klappt es nicht, bleibt er fehlend (FileNotFound).
    /// </summary>
    public BlobStore.FetchResult? FetchOne(BlobStore blobs, string id)
    {
        var s = config.Current;
        if (!s.S3Mirror || !Configured(s, config.S3SecretKey())) return null;
        var key = Prefix(s.S3Prefix) + blobs.Owner + "/" + id[..2] + "/" + id;
        IBlobRemote remote = RemoteOverride ?? Remote();
        long size;
        try { size = remote.SizeAsync(key, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Aus dem Storage Extender holen ({Id}): {Error}", id, S3Remote.Describe(e)); return new(false); }
        if (size < 0) return new(false, Missing: true);
        BlobStore.FetchResult Stream()
        {
            Interlocked.Increment(ref _streamed);
            return new(true, new RangeReadStream((at, n) => remote.ReadRangeAsync(key, at, n, CancellationToken.None).GetAwaiter().GetResult(), size));
        }
        // Gross im Verhaeltnis zum Cache: nicht ablegen, direkt durchreichen (der Download beginnt sofort, Videos lassen sich spulen)
        if (CacheMode(s) && size > Math.Max(Target(s) / 4, GB / 4)) return Stream();
        // Nur einmal gelesen (Volltext): durchreichen, den Zwischenspeicher nicht damit fuellen
        if (!BlobStore.KeepFetched) return Stream();
        var gate = _fetching.GetOrAdd(blobs.Owner + "/" + id, _ => new SemaphoreSlim(1));
        gate.Wait();
        try
        {
            if (blobs.Exists(id)) return new(true);
            // Die Reserve ehrlich halten: vorher bereinigen, nicht erst beim naechsten Abgleich - reicht es nicht
            // (alles noch in Verarbeitung), durchreichen statt ablegen
            if (CacheMode(s)) FreeUp(size);
            if (Headroom() < size) return Stream();
            var raw = blobs.RawTemp();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                remote.DownloadAsync(key, raw, cts.Token).GetAwaiter().GetResult();
                blobs.Adopt(id, raw);
                Interlocked.Increment(ref _fetched);
                Interlocked.Add(ref _cacheBytes, size);
                return new(true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Aus dem Storage Extender holen ({Id}): {Error}", id, S3Remote.Describe(e));
                if (File.Exists(raw)) File.Delete(raw);
                return new(false);
            }
        }
        finally { gate.Release(); _fetching.TryRemove(blobs.Owner + "/" + id, out _); }
    }

    /// <summary>Hier belegt (Stand des letzten Abgleichs plus seither Geholtes) - fuer das sofortige Verdraengen.</summary>
    private long _cacheBytes;
    private long _streamed;
    /// <summary>Seit dem letzten Abgleich sicher in S3 (je Benutzer) - nur das darf verdraengt werden.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, long>> _inS3 = new();
    /// <summary>An welchem Ziel <see cref="_inS3"/> gesehen wurde - nach einem Zielwechsel zaehlt das alte nicht mehr.</summary>
    private volatile string? _inS3Where;
    private readonly object _evictLock = new();

    /// <summary>
    /// Platz fuer <paramref name="incoming"/> Bytes schaffen: die am laengsten nicht gelesenen, sicher in S3 liegenden und
    /// fertig verarbeiteten Inhalte hier loeschen, bis alles unter die Grenze passt (nie Vorschaubilder).
    /// </summary>
    private void MakeRoom(long limit, long incoming)
    {
        if (Interlocked.Read(ref _cacheBytes) + incoming <= limit) return;
        lock (_evictLock)
        {
            var candidates = new List<(BlobStore Blobs, string Id, long Size, DateTime Used)>();
            long bytes = 0;
            foreach (var space in spaces.All())
            {
                var blobs = space.Db.Blobs;
                var thumbs = Thumbs(space.Db);
                var busy = Unprocessed(space.Db, space.Settings.TextIndex.Enabled);
                Dictionary<string, long>? safe = null;
                if (_inS3Where == LocationOf(config.Current)) _inS3.TryGetValue(space.User.Id, out safe);
                foreach (var (id, _) in blobs.List())
                {
                    var fi = new FileInfo(blobs.PathOf(id));
                    if (!fi.Exists) continue;
                    // Der lokale Speicherpool zaehlt nur Dateien, Vorschaubilder liegen zusaetzlich auf dem Volume
                    if (!thumbs.Contains(id)) bytes += fi.Length;
                    // Nur was am Ziel vollstaendig liegt (gleiche Groesse), fertig verarbeitet ist und kein Vorschaubild
                    if (safe == null || !safe.TryGetValue(id, out var atTarget) || atTarget != fi.Length || thumbs.Contains(id) || busy.Contains(id)) continue;
                    candidates.Add((blobs, id, fi.Length, fi.LastAccessTimeUtc > fi.LastWriteTimeUtc ? fi.LastAccessTimeUtc : fi.LastWriteTimeUtc));
                }
            }
            foreach (var c in candidates.OrderBy(c => c.Used))
            {
                if (bytes + incoming <= limit) break;
                try { c.Blobs.Delete(c.Id); bytes -= c.Size; } catch (IOException) { }
            }
            Interlocked.Exchange(ref _cacheBytes, bytes);
        }
    }
    private long _fetched;
    /// <summary>Nur fuer Tests: statt des S3-Speichers aus den Einstellungen.</summary>
    internal IBlobRemote? RemoteOverride;

    private IBlobRemote Remote()
    {
        var s = config.Current;
        var key = $"{s.StorageTarget}|{s.S3Endpoint}|{s.S3Region}|{s.S3Bucket}|{s.S3AccessKey}|{s.S3SecretKey}|{s.StorageWebDavUrl}|{s.StorageWebDavUser}|{s.StorageWebDavPassword}|{s.StorageFolder}";
        lock (_fetching)
        {
            if (_fetchRemote == null || _fetchConfig != key) { _fetchRemote?.Dispose(); _fetchRemote = RemoteOf(config); _fetchConfig = key; }
            return _fetchRemote;
        }
    }

    /// <summary>Im naechsten Durchgang fehlende Inhalte aus S3 holen (auch wenn die Kopie gerade aus ist).</summary>
    public void RequestRestore()
    {
        _restoreWanted = true;
        _state = _state with { Restore = new RestoreState(true, null, 0, 0, 0, 0, null) };
        Kick();
    }

    /// <param name="secret">Nur bei S3: der geheime Schluessel.</param>
    public static bool Configured(ServerSettings s, string? secret) => s.StorageTarget switch
    {
        "webdav" => !string.IsNullOrWhiteSpace(s.StorageWebDavUrl),
        "folder" => !string.IsNullOrWhiteSpace(s.StorageFolder),
        _ => !string.IsNullOrWhiteSpace(s.S3Endpoint) && !string.IsNullOrWhiteSpace(s.S3Bucket) && !string.IsNullOrWhiteSpace(s.S3AccessKey) && !string.IsNullOrEmpty(secret),
    };

    /// <summary>Das eingestellte Speicherziel: S3, WebDAV oder ein Ordner.</summary>
    public static IBlobRemote RemoteOf(ServerConfig config)
    {
        var s = config.Current;
        return RemoteOf(s.StorageTarget, s.S3Endpoint, s.S3Region, s.S3Bucket, s.S3AccessKey, config.S3SecretKey(), s.StorageWebDavUrl, s.StorageWebDavUser, config.StorageWebDavPassword(), s.StorageFolder);
    }

    /// <summary>Ein Speicherziel aus einzelnen Angaben (etwa noch nicht gespeicherten, zum Pruefen).</summary>
    public static IBlobRemote RemoteOf(string? target, string? endpoint, string? region, string? bucket, string? accessKey, string? secret,
        string? webDavUrl, string? webDavUser, string? webDavPassword, string? folder) => target switch
    {
        "webdav" => new DmOffice.Storage.DriverRemote(new DmOffice.Storage.WebDavDriver(webDavUrl!.Trim(), webDavUser, webDavPassword)),
        "folder" => new DmOffice.Storage.DriverRemote(new DmOffice.Storage.FolderDriver(folder!.Trim())),
        _ => S3Remote.Create(endpoint!.Trim(), region ?? "", bucket!.Trim(), accessKey!.Trim(), secret!),
    };

    /// <summary>Wo der Storage Extender liegt (Art, Adresse, Bucket, Ordner, Praefix) - ohne Zugangsdaten.</summary>
    public static string LocationOf(ServerSettings s) =>
        $"{s.StorageTarget ?? "s3"}|{s.S3Endpoint}|{s.S3Bucket}|{s.StorageWebDavUrl}|{s.StorageFolder}|{Prefix(s.S3Prefix)}";

    /// <summary>Kennung dieses Servers - bleibt im Datenverzeichnis, kommt nicht in die Sicherung: ein daraus aufgesetzter Server ist ein anderer.</summary>
    public const string ServerIdFile = "server.id";
    public static string ServerId(DataPaths paths)
    {
        var f = Path.Combine(paths.Root, ServerIdFile);
        try { if (File.Exists(f) && File.ReadAllText(f).Trim() is { Length: 32 } id) return id; } catch (IOException) { }
        var made = Guid.NewGuid().ToString("N");
        File.WriteAllText(f, made);
        return made;
    }

    public const string ServersFolder = "dmoffice-servers/";

    /// <summary>
    /// Am Ziel vermerken, dass dieser Server es benutzt, und nachsehen, ob innerhalb der Frist auch ein anderer dmOffice-Server
    /// dort war (etwa ein Testserver, aus einer Sicherung aufgesetzt). Dessen letzter Zeitpunkt, sonst null. Solange das so
    /// ist, loescht keiner am Ziel etwas: was dem einen fehlt, kann dem anderen gehoeren.
    /// </summary>
    public static async Task<DateTime?> OtherServerAsync(IBlobRemote remote, string? prefix, string self, TimeSpan within, CancellationToken ct)
    {
        var folder = Prefix(prefix) + ServersFolder;
        var file = Path.GetTempFileName();
        try { File.WriteAllText(file, DateTime.UtcNow.ToString("o")); await remote.UploadAsync(file, folder + self, ct); }
        finally { File.Delete(file); }
        DateTime? latest = null;
        foreach (var (key, size) in await remote.ListSizesAsync(folder, ct))
        {
            if (key.EndsWith("/" + self, StringComparison.Ordinal) || size is <= 0 or > 100) continue;
            var text = System.Text.Encoding.UTF8.GetString(await remote.ReadRangeAsync(key, 0, (int)size, ct));
            if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                && DateTime.UtcNow - at.ToUniversalTime() < within && (latest == null || at.ToUniversalTime() > latest)) latest = at.ToUniversalTime();
        }
        return latest;
    }

    /// <summary>
    /// Vermerke anderer Server am Ziel entfernen - wenn der „andere“ dieser Server vor einem Umzug war (neues Datenverzeichnis,
    /// neue Kennung). Nur auf ausdrücklichen Wunsch des Admins; danach räumt der Abgleich wieder auf. Gibt die Zahl zurück.
    /// </summary>
    public async Task<int> ForgetOthersAsync(CancellationToken ct)
    {
        using var remote = RemoteOf(config);
        var folder = Prefix(config.Current.S3Prefix) + ServersFolder;
        var self = ServerId(paths);
        int n = 0;
        foreach (var (key, _) in await remote.ListSizesAsync(folder, ct))
            if (!key.EndsWith("/" + self, StringComparison.Ordinal)) { await remote.DeleteAsync(key, ct); n++; }
        _state = _state with { OtherServer = null };
        Kick();
        return n;
    }

    /// <summary>
    /// Wie viele der Dateiinhalte, die nur am Ziel liegen, unter anderen Angaben (neue Adresse, neue Zugangsdaten) NICHT zu
    /// finden sind - 0: der Wechsel verliert nichts.
    /// </summary>
    public async Task<long> MissingAtAsync(IBlobRemote remote, string? prefix, CancellationToken ct)
    {
        long missing = 0;
        foreach (var space in spaces.All())
        {
            var db = space.Db;
            var here = db.Blobs.List().Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            var away = Referenced(db).Where(id => !here.Contains(id)).ToList();
            if (away.Count == 0) continue;
            var userPrefix = Prefix(prefix) + space.User.Id + "/";
            var there = await remote.ListAsync(userPrefix, ct);
            missing += away.Count(id => !there.Contains(userPrefix + id[..2] + "/" + id));
        }
        return missing;
    }

    private (DateTime At, long Count, long Bytes) _notHome;
    /// <summary><see cref="NotHome"/> fuer die Anzeige - hoechstens alle 30 Sekunden neu gezaehlt.</summary>
    public (long Count, long Bytes) NotHomeRecent()
    {
        var n = _notHome;
        if (DateTime.UtcNow - n.At > TimeSpan.FromSeconds(30)) { var (c, b) = NotHome(); _notHome = n = (DateTime.UtcNow, c, b); }
        return (n.Count, n.Bytes);
    }

    /// <summary>Speicherziel pruefen: schreiben, lesen, loeschen.</summary>
    public static async Task ProbeAsync(IBlobRemote remote, string? prefix, CancellationToken ct)
    {
        var folder = Prefix(prefix) + "dmoffice-probe";
        var key = folder + "/" + Guid.NewGuid().ToString("N");
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "dmOffice");
            await remote.UploadAsync(file, key, ct);
            if (System.Text.Encoding.UTF8.GetString(await remote.ReadRangeAsync(key, 0, 8, ct)) != "dmOffice") throw new InvalidOperationException("Gelesen wurde etwas anderes als geschrieben.");
            await remote.DeleteAsync(key, ct);
            try { await remote.DeleteAsync(folder, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
        }
        finally { File.Delete(file); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        // Gerade aus einer S3-Sicherung wiederhergestellt: die Dateiinhalte fehlen noch alle
        var flag = Path.Combine(paths.Root, RestoreFlag);
        // Eingeschaltet entscheidet der erste Abgleich: Passt alles in die Grenzen, holt er alles zurueck
        if (File.Exists(flag)) { File.Delete(flag); if (!config.Current.S3Mirror) RequestRestore(); }
        while (!ct.IsCancellationRequested)
        {
            if (Configured(config.Current, config.S3SecretKey()) && (config.Current.S3Mirror || _restoreWanted))
            {
                // Ziel nicht erreichbar (z. B. USB-Platte nicht eingehaengt): melden und spaeter wieder versuchen - nie den Server beenden
                try
                {
                    using var remote = RemoteOf(config);
                    if (_restoreWanted) { _restoreWanted = false; await RestoreAsync(remote, ct); }
                    // Nach einer Wiederherstellung: was nach dem Core-Backup ans Ziel kam, als Datei retten statt wegzuraeumen
                    if (Directory.Exists(Path.Combine(paths.Root, KnownFolder))) await RescueAsync(remote, ct);
                    if (config.Current.S3Mirror) await PassAsync(remote, ct);
                    if (_restoreWanted) continue;   // Abgleich hat fuer „Fehlendes holen“ Platz gemacht
                    if (config.Current.S3Mirror) await PrefillAsync(remote, Every, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    log.LogWarning("Storage Extender: {Error}", e.Message);
                    _state = _state with { Running = false, LastError = "Storage Extender nicht erreichbar: " + e.Message };
                }
            }
            else if (_restoreWanted) { _restoreWanted = false; _state = _state with { Restore = new RestoreState(false, DateTime.UtcNow, 0, 0, 0, 0, "Der S3-Speicher ist nicht eingerichtet.") }; }
            try { await _kick.WaitAsync(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Ein Durchgang ueber alle Benutzer: fehlende Blobs hochladen, lange Verschwundenes loeschen.</summary>
    public async Task PassAsync(IBlobRemote remote, CancellationToken ct)
    {
        // Wer vor diesem Durchgang gewartet hat (Core-Backup), ist danach dran - auch wenn er vorzeitig endet
        List<TaskCompletionSource> waiting;
        lock (_passWaiters) { waiting = _passWaiters.ToList(); _passWaiters.Clear(); }
        try { await PassCoreAsync(remote, ct); }
        finally { foreach (var w in waiting) w.TrySetResult(); }
    }

    private async Task PassCoreAsync(IBlobRemote remote, CancellationToken ct)
    {
        var prefix = Prefix(config.Current.S3Prefix);
        long local = 0, mirrored = 0, uploaded = 0, deleted = 0, pending = 0;
        string? error = null;
        // Fortschritt fuer die Aktivitaet: vorab zaehlen, wie viele Inhalte zu pruefen sind; Local/Mirrored zeigen waehrend des
        // Durchgangs „so viele von so vielen am Ziel“ (danach wie bisher das Ergebnis)
        long total = 0;
        try { foreach (var sp in spaces.All()) total += sp.Db.Blobs.List().Count(); } catch (IOException) { }
        _state = _state with { Running = true, Local = total, Mirrored = 0, Uploaded = 0 };
        // Fuer den Zwischenspeicher: was hier liegt und schon sicher in S3 ist (nur das darf verdraengt werden)
        var cache = new List<(BlobStore Blobs, string Id, long Size, DateTime Used)>();
        long cacheBytes = 0, remoteOnly = 0, waiting = 0, both = 0, bothBytes = 0, remoteBytes = 0, awayBytes = 0, fixedBytes = 0, pendingCount = 0, pendingBytes = 0, thumbBytes = 0;
        var busyParts = new Dictionary<(string Reason, string? Ext), (long Count, long Bytes)>();
        var caching = CacheMode(config.Current);
        // Wird der Storage Extender waehrend des Durchgangs ausgeschaltet, hoert er sofort auf (nicht erst nach dem ganzen Bestand)
        var wasOn = config.Current.S3Mirror;
        var missingHere = false;
        var where = LocationOf(config.Current);
        if (_inS3Where != where) { _inS3.Clear(); _inS3Where = where; }
        DateTime? other = null;
        try
        {
            other = await OtherServerAsync(remote, config.Current.S3Prefix, ServerId(paths), KeepDeleted(config.Current), ct);
            if (other != null) log.LogWarning("Storage Extender: am selben Ziel arbeitet auch ein anderer dmOffice-Server (zuletzt {At:u}) - dort wird nichts gelöscht", other);
            foreach (var space in spaces.All())
            {
                ct.ThrowIfCancellationRequested();
                var blobs = space.Db.Blobs;
                var userPrefix = prefix + space.User.Id + "/";
                var sizes = (await remote.ListSizesAsync(userPrefix, ct)).ToDictionary(x => x.Key, x => x.Size, StringComparer.Ordinal);
                var there = sizes.Keys.ToHashSet(StringComparer.Ordinal);
                var here = blobs.List().Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
                // Gebraucht ist, worauf die Datenbank zeigt - auch was im Zwischenspeicher-Betrieb hier gerade fehlt
                var needed = Referenced(space.Db);
                local += here.Count;
                foreach (var id in here)
                {
                    ct.ThrowIfCancellationRequested();
                    // „Fehlendes holen“ angefordert: der Abgleich macht Platz und setzt danach fort
                    if (_restoreWanted) throw new RestoreFirst();
                    if (wasOn && !config.Current.S3Mirror) throw new SwitchedOff();
                    var key = userPrefix + id[..2] + "/" + id;
                    if (there.Contains(key)) { mirrored++; _state = _state with { Mirrored = mirrored }; continue; }
                    try { await remote.UploadAsync(blobs.PathOf(id), key, ct); uploaded++; mirrored++; there.Add(key); sizes[key] = new FileInfo(blobs.PathOf(id)).Length; _state = _state with { Uploaded = uploaded, Mirrored = mirrored }; }
                    // Gerade geloescht (der Bestand wurde am Anfang gelesen): kein Fehler - S3 meldet das als ArgumentException, nicht als FileNotFoundException
                    catch (Exception e) when (e is not OperationCanceledException && !File.Exists(blobs.PathOf(id))) { }
                    catch (Exception e) when (e is not OperationCanceledException) { pending++; error ??= S3Remote.Describe(e); }
                }
                // Lokal verschwunden: Zeitpunkt merken, nach der Frist auch in S3 loeschen
                var sweep = await SweepAsync(remote, blobs.Dir, GoneFile, there, here, needed, KeepDeleted(config.Current), other != null, S3Backup.HoldDeletes(config.Current, paths, DateTime.UtcNow), space.User.Login, log, ct);
                deleted += sweep.Deleted;
                error ??= sweep.Error;
                _inS3[space.User.Id] = sizes.ToDictionary(x => x.Key[(x.Key.LastIndexOf('/') + 1)..], x => x.Value, StringComparer.Ordinal);
                // Fehlt hier etwas, das am Ziel liegt (verdraengt, Plattenfehler, nach einer Wiederherstellung): siehe unten, ob es zurueckkommt
                foreach (var id in needed)
                    if (!here.Contains(id) && sizes.TryGetValue(userPrefix + id[..2] + "/" + id, out var absent)) { missingHere = true; awayBytes += absent; }
                if (caching)
                {
                    var thumbs = Thumbs(space.Db);
                    var why = UnprocessedWhy(space.Db, space.Settings.TextIndex.Enabled);
                    // Fuer die Anzeige zaehlen Dateien, nicht ihre Vorschaubilder (die bleiben immer hier)
                    foreach (var id in needed)
                        if (!here.Contains(id) && !thumbs.Contains(id) && sizes.TryGetValue(userPrefix + id[..2] + "/" + id, out var away)) { remoteOnly++; remoteBytes += away; }
                    foreach (var id in here)
                    {
                        var fi = new FileInfo(blobs.PathOf(id));
                        if (!fi.Exists) continue;
                        // Der lokale Speicherpool zaehlt nur Dateien, Vorschaubilder liegen zusaetzlich auf dem Volume
                        if (!thumbs.Contains(id)) cacheBytes += fi.Length;
                        if (!thumbs.Contains(id) && sizes.TryGetValue(userPrefix + id[..2] + "/" + id, out var atBoth)) { both++; bothBytes += atBoth; }
                        else if (!thumbs.Contains(id)) { pendingCount++; pendingBytes += fi.Length; }
                        // Wird (noch) nicht bereinigt: noch nicht Verarbeitetes
                        if (thumbs.Contains(id)) thumbBytes += fi.Length;
                        if (!thumbs.Contains(id) && why.ContainsKey(id)) fixedBytes += fi.Length;
                        // Nur was sicher in S3 liegt und fertig verarbeitet ist, kein Vorschaubild
                        if (why.TryGetValue(id, out var cause))
                        {
                            waiting += fi.Length;
                            busyParts.TryGetValue(cause, out var part);
                            busyParts[cause] = (part.Count + 1, part.Bytes + fi.Length);
                            continue;
                        }
                        // Nur was am Ziel vollstaendig liegt (gleiche Groesse) - ein halb geschriebenes Objekt zaehlt nicht
                        if (thumbs.Contains(id) || !sizes.TryGetValue(userPrefix + id[..2] + "/" + id, out var atTarget) || atTarget != fi.Length) continue;
                        cache.Add((blobs, id, fi.Length, fi.LastAccessTimeUtc > fi.LastWriteTimeUtc ? fi.LastAccessTimeUtc : fi.LastWriteTimeUtc));
                    }
                }
            }
            // Ueber dem Zielwert: in Ruhe die am laengsten nicht gelesenen, fertig verarbeiteten Inhalte hier loeschen
            // (in S3 bleiben sie), bis der Zielwert erreicht ist
            if (caching)
            {
                var limit = Target(config.Current);
                // Reserve unterschritten: auch unter die Zielgroesse bereinigen
                var deficit = -Headroom();
                if (deficit > 0) limit = Math.Max(0, Math.Min(limit, cacheBytes - deficit));
                long evicted = 0;
                if (cacheBytes > limit)
                    foreach (var c in cache.OrderBy(c => c.Used))
                    {
                        if (cacheBytes <= limit) break;
                        try { lock (_evictLock) c.Blobs.Delete(c.Id); cacheBytes -= c.Size; evicted++; remoteOnly++; remoteBytes += c.Size; awayBytes += c.Size; both--; bothBytes -= c.Size; }
                        catch (IOException) { }   // gerade geoeffnet - beim naechsten Mal
                    }
                Interlocked.Exchange(ref _cacheBytes, cacheBytes);
                // Passt alles in die Grenzen, liegt alles auch hier: von selbst zurueckholen. Sonst bleibt es beim Nachladen beim Oeffnen.
                if (missingHere && !Fits(cacheBytes, awayBytes, Target(config.Current), Headroom())) missingHere = false;
                _state = _state with { Cache = new CacheState(cacheBytes, limit, evicted, Interlocked.Read(ref _fetched), remoteOnly, Interlocked.Read(ref _streamed), Headroom() is var room && room == long.MaxValue ? long.MaxValue : cacheBytes + Math.Max(0, room), waiting, both, bothBytes, remoteBytes, fixedBytes, pendingCount, pendingBytes,
                    thumbBytes, busyParts.Select(x => new BusyPart(x.Key.Reason, x.Key.Ext, x.Value.Count, x.Value.Bytes)).OrderByDescending(x => x.Bytes).Take(12).ToList()) };
            }
            else _state = _state with { Cache = null };
        }
        catch (OperationCanceledException) { _state = _state with { Running = false }; throw; }
        catch (RestoreFirst) { _state = _state with { Running = false }; return; }
        catch (SwitchedOff) { _state = _state with { Running = false }; log.LogInformation("Storage Extender ausgeschaltet - Abgleich angehalten"); return; }
        catch (Exception e) { error = S3Remote.Describe(e); }
        if (error != null) log.LogWarning("S3-Kopie: {Error}", error);
        if (missingHere && !_restoreWanted && _state.Restore is not { Running: true }) { log.LogWarning("Storage Extender: hier fehlen Dateiinhalte - werden vom Ziel zurückgeholt"); RequestRestore(); }
        _state = new State(false, DateTime.UtcNow, error, local, mirrored, uploaded, deleted, pending, _state.Restore, _state.Cache, other, Headroom() < 0);
        if (_state.LowSpace) log.LogWarning("Storage Extender: der frei zu lassende Speicherplatz ist erreicht – lokal sind nur noch {Free} frei", FreeHere());
    }

    public const string GoneFile = ".s3-gone.json";

    /// <summary>Passt, was hier liegt, samt dem, was nur am Ziel liegt, in die Begrenzung und in den Platz bis zur Reserve?</summary>
    internal static bool Fits(long here, long away, long target, long headroom) =>
        away <= target - Math.Min(here, target) && (headroom == long.MaxValue || away <= Math.Max(0, headroom));

    /// <summary>
    /// Am Ziel aufraeumen, was hier verschwunden ist: Zeitpunkt merken, nach der Frist loeschen. Fehlt mehr als die Haelfte, stimmt
    /// eher mit dem Datenverzeichnis etwas nicht (nicht eingehaengt, halb zurueckgespielt): nichts vormerken. Arbeitet ein anderer
    /// Server am selben Ziel: ebenso. Scheitert das Core-Backup oder ist es ueberfaellig (hold): vormerken, aber nicht loeschen.
    /// </summary>
    internal static async Task<(long Deleted, string? Error)> SweepAsync(IBlobRemote remote, string dir, string goneFile, HashSet<string> there, HashSet<string> here,
        HashSet<string> needed, TimeSpan keep, bool other, bool hold, string login, ILogger log, CancellationToken ct)
    {
        long deleted = 0;
        string? error = null;
        var gone = Gone.Load(dir, goneFile);
        var missing = there.Where(k => k[(k.LastIndexOf('/') + 1)..] is var id && !here.Contains(id) && !needed.Contains(id)).ToHashSet(StringComparer.Ordinal);
        if (missing.Count > 0 && missing.Count * 2 > there.Count)
        {
            log.LogWarning("Backup-Ziel {User}: {Missing} von {There} Inhalten fehlen lokal - lösche dort vorsichtshalber nichts", login, missing.Count, there.Count);
            missing.Clear();
        }
        if (other) missing.Clear();
        foreach (var k in gone.Keys.Where(k => !missing.Contains(k)).ToList()) gone.Remove(k);
        foreach (var k in missing)
        {
            if (!gone.TryGetValue(k, out var since)) { gone[k] = DateTime.UtcNow; continue; }
            if (hold || DateTime.UtcNow - since < keep) continue;
            try { await remote.DeleteAsync(k, ct); gone.Remove(k); deleted++; }
            catch (Exception e) when (e is not OperationCanceledException) { error ??= S3Remote.Describe(e); }
        }
        Gone.Save(dir, goneFile, gone);
        return (deleted, error);
    }

    /// <summary>Groesse und freier Platz des Ziels - nur bei einem Ordner bekannt, S3 und WebDAV melden das nicht verlaesslich.</summary>
    public (long Total, long Free)? TargetSpace()
    {
        var s = config.Current;
        if (s.StorageTarget != "folder" || string.IsNullOrWhiteSpace(s.StorageFolder)) return null;
        var (total, free) = VolumeOf(s.StorageFolder);
        return total > 0 ? (total, free) : null;
    }

    /// <summary>Belegt am Storage Extender beim letzten Abgleich: Anzahl und Bytes.</summary>
    public (long Count, long Bytes) AtTargetTotal() =>
        (_inS3.Values.Sum(d => (long)d.Count), _inS3.Values.Sum(d => d.Values.Sum()));

    /// <summary>
    /// Was die Datenbanken kennen, lokal aber fehlt (Plattenschaden, halb zurueckgespielte Sicherung), aus S3 holen.
    /// Jeder Inhalt wird vor dem Ablegen ganz entschluesselt und so geprueft.
    /// </summary>
    public async Task RestoreAsync(IBlobRemote remote, CancellationToken ct)
    {
        var prefix = Prefix(config.Current.S3Prefix);
        long missing = 0, restored = 0, notThere = 0, failed = 0;
        string? error = null;
        var full = false;
        void Show(bool running) => _state = _state with { Restore = new RestoreState(running, running ? null : DateTime.UtcNow, missing, restored, notThere, failed, error) };
        Show(true);
        try
        {
            foreach (var space in spaces.All())
            {
                if (full) break;
                ct.ThrowIfCancellationRequested();
                var blobs = space.Db.Blobs;
                var wanted = Referenced(space.Db).Where(id => !blobs.Exists(id)).ToList();
                if (wanted.Count == 0) continue;
                missing += wanted.Count;
                var userPrefix = prefix + space.User.Id + "/";
                var there = (await remote.ListSizesAsync(userPrefix, ct)).ToDictionary(x => x.Key, x => x.Size, StringComparer.Ordinal);
                foreach (var id in wanted)
                {
                    ct.ThrowIfCancellationRequested();
                    var key = userPrefix + id[..2] + "/" + id;
                    if (!there.TryGetValue(key, out var size)) { notThere++; continue; }
                    // Liefe die Platte voll, koennten die Datenbanken nicht mehr schreiben - lieber anhalten, beim naechsten Abgleich geht es weiter
                    if (Headroom() < size) { error = "Auf dem lokalen Speichervolumen ist nur noch der frei zu lassende Speicherplatz verfügbar – das Zurückholen ist angehalten und geht weiter, sobald wieder Platz ist."; full = true; break; }
                    var raw = blobs.RawTemp();
                    try { await remote.DownloadAsync(key, raw, ct); blobs.Adopt(id, raw); restored++; }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        failed++;
                        error ??= (space.Db.Query("SELECT path FROM files WHERE blob = $b", r => r.GetString(0), ("$b", id)).FirstOrDefault() is { } file ? $"{space.User.Login}: {file}: " : "") + S3Remote.Describe(e);
                        if (File.Exists(raw)) File.Delete(raw);
                    }
                    if ((restored + failed) % 20 == 0) Show(true);
                }
            }
        }
        catch (OperationCanceledException) { Show(false); throw; }
        catch (Exception e) { error = S3Remote.Describe(e); }
        if (error != null) log.LogWarning("Aus dem Storage Extender holen: {Error}", error);
        Show(false);
    }

    /// <summary>
    /// Bleibt unter den Grenzen Platz, die neuesten Dateien vom Ziel holen. Passt eine nicht mehr, kommt die naechste, die noch passt.
    /// Fuellt nur bis zur Grenze, loest also nie ein Bereinigen aus. Nach der Zeit hoert es auf, der naechste Durchgang setzt fort.
    /// </summary>
    public sealed record PrefillState(bool Running, long Done, long Total);
    /// <summary>Stand des Auffuellens fuer die Aktivitaet - null: gerade nichts zu tun.</summary>
    public PrefillState? Prefill { get; private set; }

    internal async Task<int> PrefillAsync(IBlobRemote remote, TimeSpan budget, CancellationToken ct)
    {
        var s = config.Current;
        if (!s.S3Mirror || _inS3Where != LocationOf(s)) return 0;
        var wanted = new List<(UserSpace Space, string Id, long Size, string Modified)>();
        foreach (var space in spaces.All())
        {
            if (!_inS3.TryGetValue(space.User.Id, out var atTarget)) continue;
            foreach (var (id, modified) in space.Db.Query("SELECT blob, MAX(modified) FROM files WHERE is_dir = 0 AND blob IS NOT NULL GROUP BY blob", r => (r.GetString(0), r.GetString(1))))
                if (BlobStore.ValidId(id) && !space.Db.Blobs.Exists(id) && atTarget.TryGetValue(id, out var size))
                    wanted.Add((space, id, size, modified));
        }
        var prefix = Prefix(s.S3Prefix);
        var stop = DateTime.UtcNow + budget;
        var fetched = 0;
        // Vorab auswaehlen, was passt - so kann die Aktivitaet „x von y“ zeigen
        var left = Math.Min(Target(s) - Interlocked.Read(ref _cacheBytes), Headroom());
        var plan = new List<(UserSpace Space, string Id, long Size, string Modified)>();
        foreach (var x in wanted.OrderByDescending(x => x.Modified, StringComparer.Ordinal))
            if (x.Size <= left) { plan.Add(x); left -= x.Size; }
        if (plan.Count == 0) { Prefill = null; return 0; }
        Prefill = new PrefillState(true, 0, plan.Count);
        try
        {
        foreach (var x in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (_restoreWanted || !config.Current.S3Mirror || DateTime.UtcNow > stop) break;
            var room = Math.Min(Target(config.Current) - Interlocked.Read(ref _cacheBytes), Headroom());
            if (x.Size > room) continue;
            var blobs = x.Space.Db.Blobs;
            var raw = blobs.RawTemp();
            try
            {
                await remote.DownloadAsync(prefix + x.Space.User.Id + "/" + x.Id[..2] + "/" + x.Id, raw, ct);
                blobs.Adopt(x.Id, raw);
                Interlocked.Add(ref _cacheBytes, x.Size);
                fetched++;
                Prefill = new PrefillState(true, fetched, plan.Count);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Lokalen Speicherpool auffuellen: {Error}", S3Remote.Describe(e)); if (File.Exists(raw)) File.Delete(raw); }
        }
        }
        finally { Prefill = null; }
        // Die Anzeige auf den neuen Stand bringen
        if (fetched > 0) Kick();
        return fetched;
    }

    private sealed class RestoreFirst : Exception;
    private sealed class SwitchedOff : Exception;

    public static string Prefix(string? p)
    {
        var s = (p ?? "").Trim().Trim('/');
        return s.Length == 0 ? "" : s + "/";
    }

    /// <summary>Lokal verschwundene Blobs mit dem Zeitpunkt, seit dem sie fehlen - neben den Blobs, damit es Neustarts uebersteht.</summary>
    /// <summary>Im Core-Backup: welche Inhalte je Benutzer zu seinem Zeitpunkt am Ziel lagen (s3-known/&lt;Benutzer&gt;.json).</summary>
    public const string KnownFolder = "s3-known";
    /// <summary>Hierhin kommen gerettete Inhalte - ohne Namen und Ordner, die stehen nur in der Datenbank.</summary>
    public const string RescueFolder = "Nach Wiederherstellung gefunden";

    public sealed record RescueState(DateTime Finished, int Rescued);
    /// <summary>Letztes Retten (fuer die Anzeige) - null: keins.</summary>
    public RescueState? Rescued { get; private set; }

    /// <summary>
    /// Nach einer Wiederherstellung aus dem Core-Backup: Inhalte am Ziel, die weder die Datenbank kennt noch das Backup zu
    /// seinem Zeitpunkt am Ziel gesehen hat, sind danach entstanden (hochgeladen nach dem letzten Core-Backup). Ihre Namen
    /// sind verloren, die Inhalte nicht: sie kommen als Dateien nach „Nach Wiederherstellung gefunden“, mit erkanntem Typ.
    /// Vor dem Backup Geloeschtes kommt so nicht zurueck - das stand in der Liste des Backups.
    /// </summary>
    public async Task RescueAsync(IBlobRemote remote, CancellationToken ct)
    {
        var dir = Path.Combine(paths.Root, KnownFolder);
        var prefix = Prefix(config.Current.S3Prefix);
        var count = 0;
        foreach (var space in spaces.All())
        {
            var file = Path.Combine(dir, space.User.Id + ".json");
            if (!File.Exists(file)) continue;
            HashSet<string> known;
            try { known = (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? new()).ToHashSet(StringComparer.Ordinal); }
            catch (JsonException) { File.Delete(file); continue; }
            var needed = Referenced(space.Db);
            var userPrefix = prefix + space.User.Id + "/";
            var blobs = space.Db.Blobs;
            foreach (var (key, size) in await remote.ListSizesAsync(userPrefix, ct))
            {
                ct.ThrowIfCancellationRequested();
                var id = key[(key.LastIndexOf('/') + 1)..];
                if (!BlobStore.ValidId(id) || known.Contains(id) || needed.Contains(id)) continue;
                // Kein Platz bis zur Reserve: spaeter weiter (die Liste bleibt liegen)
                if (Headroom() < 2 * size) { log.LogWarning("Retten nach der Wiederherstellung angehalten – lokal ist der frei zu lassende Speicherplatz erreicht"); return; }
                var raw = blobs.RawTemp();
                try
                {
                    await remote.DownloadAsync(key, raw, ct);
                    blobs.Adopt(id, raw);   // ganz entschluesselt und so geprueft
                    if (await RescueOne(space, id, ++count, ct)) continue;
                    count--;
                }
                catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or InvalidDataException or IOException or Dav.QuotaExceededException)
                {
                    count--;
                    log.LogWarning("Retten nach der Wiederherstellung ({Id}): {Error}", id, e.Message);
                }
                finally { if (blobs.Exists(id) && !needed.Contains(id)) blobs.Delete(id); }
            }
            File.Delete(file);
        }
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        Rescued = new RescueState(DateTime.UtcNow, count);
        if (count > 0) log.LogInformation("Nach der Wiederherstellung {Count} Inhalte ohne Namen gerettet ({Folder})", count, RescueFolder);
    }

    /// <summary>Einen geretteten Inhalt als Datei ablegen - Vorschaubilder (WebP in deren Groessen) nicht. true: abgelegt.</summary>
    private static async Task<bool> RescueOne(UserSpace space, string id, int n, CancellationToken ct)
    {
        var blobs = space.Db.Blobs;
        var head = new byte[64];
        int got;
        using (BlobStore.WithoutFetch())
        await using (var s = blobs.OpenRead(id)) got = await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        var (ext, mime) = Sniff(head[..got]);
        if (ext == "webp")
        {
            using (BlobStore.WithoutFetch())
            await using (var s = blobs.OpenRead(id))
            using (var codec = SkiaSharp.SKCodec.Create(s))
                if (codec != null && Math.Max(codec.Info.Width, codec.Info.Height) is Dav.Thumbs.Small or Dav.Thumbs.Medium or Dav.Thumbs.Large or Dav.Thumbs.LegacyLarge) return false;
        }
        if (ext == "zip")
            using (BlobStore.WithoutFetch())
            await using (var s = blobs.OpenRead(id))
            {
                var copy = new MemoryStream();
                await s.CopyToAsync(copy, ct);
                try { (ext, mime) = Office(new System.IO.Compression.ZipArchive(copy)); } catch (InvalidDataException) { }
            }
        Dav.FileStore.EnsureDir(space.Db, RescueFolder);
        using (BlobStore.WithoutFetch())
        await using (var s = blobs.OpenRead(id))
        {
            // Nie ueber Gerettetes von frueher schreiben
            var path = $"{RescueFolder}/Gefunden {n:0000}.{ext}";
            for (var k = n + 1; Dav.FileStore.Get(space.Db, path) != null; k++) path = $"{RescueFolder}/Gefunden {k:0000}.{ext}";
            await Dav.FileStore.PutAsync(space.Db, path, s, mime, ct: ct);
        }
        return true;
    }

    /// <summary>Dateityp am Anfang des Inhalts erkennen (die Namen sind verloren).</summary>
    internal static (string Ext, string Mime) Sniff(byte[] b)
    {
        bool At(int i, string s) => b.Length >= i + s.Length && System.Text.Encoding.ASCII.GetString(b, i, s.Length) == s;
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ("jpg", "image/jpeg");
        if (b.Length >= 4 && b[0] == 0x89 && At(1, "PNG")) return ("png", "image/png");
        if (At(0, "GIF8")) return ("gif", "image/gif");
        if (At(0, "RIFF") && At(8, "WEBP")) return ("webp", "image/webp");
        if (At(0, "RIFF") && At(8, "WAVE")) return ("wav", "audio/wav");
        if (At(0, "%PDF")) return ("pdf", "application/pdf");
        if (At(4, "ftyp")) return At(8, "heic") || At(8, "heix") || At(8, "mif1") ? ("heic", "image/heic") : At(8, "qt  ") ? ("mov", "video/quicktime") : ("mp4", "video/mp4");
        if (At(0, "PK\u0003\u0004")) return ("zip", "application/zip");
        if (At(0, "ID3") || (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0)) return ("mp3", "audio/mpeg");
        if (At(0, "OggS")) return ("ogg", "audio/ogg");
        if (At(0, "fLaC")) return ("flac", "audio/flac");
        if (LooksLikeText(b)) return ("txt", "text/plain");
        return ("bin", "application/octet-stream");
    }

    /// <summary>Gültiges UTF-8 ohne Steuerzeichen; der Anfang kann ein Zeichen mitten durchschneiden. Sonst zählt es als binär (auch Verschlüsseltes).</summary>
    private static bool LooksLikeText(byte[] b)
    {
        if (b.Length == 0) return false;
        var decoder = new System.Text.UTF8Encoding(false, true).GetDecoder();
        var chars = new char[b.Length + 2];
        try
        {
            var n = decoder.GetChars(b, 0, b.Length, chars, 0, flush: false);
            return chars.Take(n).All(c => !char.IsControl(c) || c is '\t' or '\r' or '\n' or '\f');
        }
        catch (System.Text.DecoderFallbackException) { return false; }
    }

    /// <summary>ZIP genauer: Word, Excel, PowerPoint, OpenDocument.</summary>
    private static (string Ext, string Mime) Office(System.IO.Compression.ZipArchive zip)
    {
        bool Has(string n) => zip.GetEntry(n) != null;
        if (Has("word/document.xml")) return ("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        if (Has("xl/workbook.xml")) return ("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        if (Has("ppt/presentation.xml")) return ("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation");
        if (zip.GetEntry("mimetype") is { } m)
        {
            using var r = new StreamReader(m.Open());
            var type = r.ReadToEnd().Trim();
            if (type.EndsWith(".text")) return ("odt", type);
            if (type.EndsWith(".spreadsheet")) return ("ods", type);
            if (type.EndsWith(".presentation")) return ("odp", type);
        }
        return ("zip", "application/zip");
    }

    private static class Gone
    {
        public static Dictionary<string, DateTime> Load(string dir, string file)
        {
            var path = Path.Combine(dir, file);
            try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path)) ?? new() : new(); }
            catch (Exception e) when (e is IOException or JsonException) { return new(); }
        }
        public static void Save(string dir, string file, Dictionary<string, DateTime> gone)
        {
            var path = Path.Combine(dir, file);
            if (gone.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(gone));
        }
    }
}

/// <summary>
/// Zwischenspeicher-Betrieb: Vorschaubilder gleich nach dem Hochladen machen, solange der Inhalt noch hier liegt -
/// sonst muesste die Ordneransicht ihn spaeter dafuer aus S3 holen. Erst danach darf er verdraengt werden.
/// </summary>
public sealed class ThumbAhead(Spaces spaces, ServerConfig config, ILogger<ThumbAhead> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            int made = 0;
            if (S3Mirror.CacheMode(config.Current))
                foreach (var space in spaces.All())
                {
                    try
                    {
                        foreach (var x in Todo(space.Db))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (Dav.FileStore.Get(space.Db, x.Path) is not { } f || !space.Db.Blobs.Exists(BlobOf(space.Db, x.Path) ?? "")) continue;
                            // Nur was hier liegt - nichts dafuer aus S3 holen
                            using (BlobStore.WithoutFetch()) await Dav.Thumbs.Get(space.Db, f, Dav.Thumbs.Small, ct);
                            made++;
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) { log.LogWarning("Vorschaubilder vorab {User}: {Error}", space.User.Login, e.Message); }
                }
            try { await Task.Delay(made > 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Die neuesten Dateien, denen ein Vorschaubild fehlt. Die Begrenzung erst nach dem Filter: Dateien ohne Vorschaubild (Text, Video ...)
    /// bleiben immer in der Abfrage - stuenden sie vorn, kaemen aeltere Fotos sonst nie an die Reihe.
    /// </summary>
    internal static List<(string Path, string? Mime, long Size)> Todo(UserDb db, int max = 400) =>
        db.Query(
            "SELECT f.path, f.mime, f.size FROM files f WHERE f.is_dir = 0 AND f.blob IS NOT NULL AND NOT EXISTS " +
            "(SELECT 1 FROM file_thumbs t WHERE t.fileid = f.fileid AND t.size = " + Dav.Thumbs.Small + " AND " + Dav.Thumbs.CurrentSql + ") ORDER BY f.modified DESC",
            r => (Path: r.GetString(0), Mime: r.IsDBNull(1) ? null : r.GetString(1), Size: r.GetInt64(2)))
            .Where(x => Dav.Thumbs.Expected(x.Path, x.Mime, x.Size)).Take(max).ToList();

    private static string? BlobOf(UserDb db, string path) =>
        db.Query("SELECT blob FROM files WHERE path = $p", r => r.IsDBNull(0) ? null : r.GetString(0), ("$p", path)).FirstOrDefault();
}
