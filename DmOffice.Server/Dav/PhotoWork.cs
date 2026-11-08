using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Arbeitskopien für den Foto-Editor bei Fotos aus Galerie-Ordnern („ext:…“, oft über einen Tunnel eingebunden und langsam zu lesen):
/// <list type="bullet">
/// <item>Original vorab holen: öffnet man ein Foto im Editor, liest der Server es gleich im Hintergrund in den Zwischenspeicher
/// (data/cache/photowork, verschlüsselt). Vorschau, Vorschläge und Speichern nehmen dann diese Kopie - solange Größe und Änderungszeit
/// auf dem Speicher noch passen. Höchstens 4 GB, eine Kopie gilt 6 Stunden.</item>
/// <item>Im Hintergrund schreiben: das Ergebnis liegt sofort in der Galerie (Vorschaubilder aus dem Ergebnis), auf den Speicher
/// geschrieben wird danach - mit Wiederholungen, auch nach einem Neustart (die wartenden liegen mit Ziel im Ordner „pending“).</item>
/// </list>
/// Nichts davon ist in der Sicherung; der Ordner darf jederzeit weg (wartende Schreibvorgänge gingen dann verloren).
/// </summary>
public static class PhotoWork
{
    private const long MaxBytes = 4L * 1024 * 1024 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan[] Retries = [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    private static string? _root;
    private static byte[] _key = [];
    private static ILogger? _log;
    /// <summary>Für Hintergrund-Schreibvorgänge (Benutzer und Speicher nachschlagen).</summary>
    private static Func<string, UserSpace?>? _space;
    private static ServerConfig? _config;
    private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> Loading = new();
    // Wartende Schreibvorgänge je „Quelle/Pfad“ - das Einlesen des Ordners darf sie nicht als verschwunden austragen
    private static readonly ConcurrentDictionary<string, Task> Writing = new();

    /// <summary>Wird diese Datei gerade noch im Hintergrund geschrieben?</summary>
    public static bool IsPending(string source, string path) => Writing.ContainsKey(source + "/" + path);

    /// <summary>Für Tests: warten, bis alle Schreibvorgänge durch sind.</summary>
    internal static Task Drain() => Task.WhenAll(Writing.Values);

    public static bool Ready => _root != null;

    public static void Init(string dataRoot, byte[] master, ServerConfig config, Func<string, UserSpace?> space, ILogger log)
    {
        _root = Path.Combine(dataRoot, "cache", "photowork");
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "dmoffice photo work v1"u8.ToArray());
        _config = config; _space = space; _log = log;
        Directory.CreateDirectory(Path.Combine(_root, "pending"));
        // Liegengebliebene Schreibvorgänge (Neustart) wieder aufnehmen
        foreach (var meta in Directory.GetFiles(Path.Combine(_root, "pending"), "*.json"))
            try { if (JsonSerializer.Deserialize<Pending>(File.ReadAllText(meta)) is { } p) Start(p, Path.ChangeExtension(meta, ".bin")); }
            catch (Exception e) when (e is IOException or JsonException) { log.LogWarning("Wartendes Foto {File}: {Error}", meta, e.Message); }
    }

    // ---------------- Lesen ----------------

    /// <summary>Original aus dem Galerie-Ordner - aus der Arbeitskopie, wenn sie zum Stand auf dem Speicher passt, sonst gelesen und gemerkt.</summary>
    public static Task<byte[]> GetAsync(UserSpace space, ServerConfig config, string id, CancellationToken ct)
    {
        if (_root == null) return ReadAsync(space, config, id, ct);
        var key = space.User.Id + "|" + id;
        // Mehrere gleichzeitig (Vorab-Holen und Vorschau): einmal lesen
        var lazy = Loading.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(() => LoadAsync(space, config, id, key)));
        return lazy.Value.WaitAsync(ct);
    }

    /// <summary>Im Hintergrund holen (Editor geöffnet) - Fehler zählen nicht, beim Bearbeiten wird es dann eben gelesen.</summary>
    public static void Prefetch(UserSpace space, ServerConfig config, string id)
    {
        if (_root == null || !id.StartsWith("ext:", StringComparison.Ordinal)) return;
        _ = GetAsync(space, config, id, CancellationToken.None).ContinueWith(t => _log?.LogInformation("Arbeitskopie {Id}: {Error}", id, t.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private static async Task<byte[]> LoadAsync(UserSpace space, ServerConfig config, string id, string key)
    {
        try
        {
            var (src, rel) = DmOffice.Gallery.GalleryFolders.Resolve(space, config, id[4..]);
            var (driver, _) = DmOffice.Storage.ExternalFolders.Open(space, config, src.Mount);
            using (driver)
            {
                var st = await driver.StatAsync(rel, CancellationToken.None) ?? throw new KeyNotFoundException("Datei nicht gefunden.");
                var stamp = st.Size ^ (new DateTimeOffset(DateTime.SpecifyKind(st.Modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds() << 1);
                var file = FileOf(key);
                if (Load(file, stamp) is { } cached) { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); return cached; }
                await using var s = await driver.OpenReadAsync(rel, CancellationToken.None);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                var data = ms.ToArray();
                Store(file, stamp, data);
                Prune();
                return data;
            }
        }
        finally { _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => Loading.TryRemove(key, out var _)); }
    }

    private static async Task<byte[]> ReadAsync(UserSpace space, ServerConfig config, string id, CancellationToken ct)
    {
        var (src, rel) = DmOffice.Gallery.GalleryFolders.Resolve(space, config, id[4..]);
        var (driver, _) = DmOffice.Storage.ExternalFolders.Open(space, config, src.Mount);
        using (driver)
        {
            await using var s = await driver.OpenReadAsync(rel, ct);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
    }

    // ---------------- Schreiben ----------------

    public sealed record Pending(string User, string Source, string From, string Target, long Created);

    /// <summary>
    /// Ergebnis im Hintergrund auf den Speicher schreiben. Die Galerie zeigt es sofort (Edited mit Größe des Ergebnisses und jetzt als
    /// Zeit); nach dem Schreiben bekommt der Eintrag Größe und Zeit vom Speicher.
    /// </summary>
    public static void WriteLater(UserSpace space, string source, string from, string target, byte[] data)
    {
        if (_root == null) throw new InvalidOperationException("Arbeitskopien sind nicht eingerichtet.");
        var p = new Pending(space.User.Id, source, from, target, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var name = Path.Combine(_root, "pending", Guid.NewGuid().ToString("N"));
        Store(name + ".bin", 0, data);
        File.WriteAllText(name + ".json", JsonSerializer.Serialize(p));
        Start(p, name + ".bin");
    }

    private static void Start(Pending p, string bin)
    {
        var key = p.Source + "/" + p.Target;
        var task = Task.Run(() => WriteLoop(p, bin));
        Writing[key] = task;
        _ = task.ContinueWith(_ => Writing.TryRemove(new KeyValuePair<string, Task>(key, task)));
    }

    private static async Task WriteLoop(Pending p, string bin)
    {
        var data = Load(bin, 0);
        if (data == null || _space?.Invoke(p.User) is not { } space || _config == null) { Drop(bin); return; }
        using var job = Activity.Start(space.User.Id, Activity.PhotoWrite, p.Target);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var src = DmOffice.Gallery.GalleryFolders.Of(space.Db).FirstOrDefault(s => s.Id == p.Source) ?? throw new KeyNotFoundException("Galerie-Ordner fehlt.");
                var (driver, ro) = DmOffice.Storage.ExternalFolders.Open(space, _config, src.Mount);
                using (driver)
                {
                    if (ro) throw new UnauthorizedAccessException("Der Ordner ist nur zum Lesen eingebunden.");
                    await driver.WriteAsync(p.Target, new MemoryStream(data), CancellationToken.None);
                    // Stand vom Speicher übernehmen - meldet er noch den alten (Zwischenspeicher des Netzlaufwerks), gilt der des Ergebnisses
                    var st = await driver.StatAsync(p.Target, CancellationToken.None);
                    DmOffice.Gallery.GalleryFolders.Written(space, _config, src, p.Target, data,
                        st is { IsDir: false } s1 && s1.Size == data.Length ? s1.Modified : DateTime.UtcNow);
                }
                Drop(bin);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or InvalidOperationException)
            {
                if (attempt >= Retries.Length)
                {
                    job.Fail($"{p.Target}: {e.Message}");
                    _log?.LogWarning("Bearbeitetes Foto {Target} ließ sich nicht schreiben: {Error}", p.Target, e.Message);
                    return;   // bleibt in „pending“ - beim nächsten Start neu versucht
                }
                await Task.Delay(Retries[attempt]);
            }
        }
    }

    private static void Drop(string bin)
    {
        try { File.Delete(bin); File.Delete(Path.ChangeExtension(bin, ".json")); } catch (IOException) { }
    }

    // ---------------- Datei: Stand (8), Nonce (12), Tag (16), verschlüsselt ----------------

    private static string FileOf(string key) =>
        Path.Combine(_root!, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..40].ToLowerInvariant() + ".bin");

    private static void Store(string file, long stamp, byte[] data)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        var tmp = file + ".tmp";
        using (var f = File.Create(tmp))
        {
            f.Write(BitConverter.GetBytes(stamp)); f.Write(nonce); f.Write(tag); f.Write(cipher);
        }
        File.Move(tmp, file, true);
    }

    private static byte[]? Load(string file, long stamp)
    {
        byte[] all;
        try { all = File.ReadAllBytes(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (all.Length < 36 || BitConverter.ToInt64(all, 0) != stamp) return null;
        var plain = new byte[all.Length - 36];
        try { using var aes = new AesGcm(_key, 16); aes.Decrypt(all.AsSpan(8, 12), all.AsSpan(36), all.AsSpan(20, 16), plain); }
        catch (CryptographicException) { return null; }
        return plain;
    }

    /// <summary>Alte Kopien weg (älter als 6 Stunden), dann die am längsten unbenutzten, bis höchstens 4 GB übrig sind.</summary>
    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_root!).GetFiles("*.bin").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (DateTime.UtcNow - f.LastWriteTimeUtc < MaxAge && total <= MaxBytes) continue;
                total -= f.Length;
                f.Delete();
            }
        }
        catch (IOException) { }
    }

    /// <summary>Für Tests: Wurzel und Schlüssel setzen, ohne Hintergrund-Schreibvorgänge aufzunehmen.</summary>
    internal static void InitForTests(string root, byte[] master, ServerConfig config, Func<string, UserSpace?> space)
    {
        _root = root; _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "dmoffice photo work v1"u8.ToArray());
        _config = config; _space = space;
        Directory.CreateDirectory(Path.Combine(root, "pending"));
    }
}
