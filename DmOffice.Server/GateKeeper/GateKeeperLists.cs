using System.Collections.Concurrent;
using System.Globalization;
using DmOffice.Core;

namespace DmOffice.GateKeeper;

/// <summary>
/// Filterlisten von GateKeeper: laden (beim Start, alle N Stunden, auf Knopfdruck), als Dateien unter &lt;Daten&gt;/gatekeeper/
/// ablegen und zu einer Menge zusammenführen, die der Tunnel prüft. Eine Liste, die nicht geladen werden kann, behält ihren
/// letzten Stand.
/// </summary>
public sealed class GateKeeperLists(GateKeeperStore store, DataPaths paths, IHttpClientFactory http, ILogger<GateKeeperLists> log) : BackgroundService
{
    /// <summary>Größte Liste in Bytes.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private volatile GkListEntries _current = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Die zusammengeführten Einträge aller eingeschalteten Listen.</summary>
    public GkListEntries Current => _current;

    public string Folder => Path.Combine(paths.Root, "gatekeeper");
    private string FileOf(long id) => Path.Combine(Folder, "list-" + id.ToString(CultureInfo.InvariantCulture) + ".txt");

    /// <summary>Gespeicherte Listen neu zusammenführen (nach Ein-/Ausschalten oder Löschen).</summary>
    public void Rebuild()
    {
        var all = new GkListEntries();
        foreach (var l in store.Lists().Where(l => l.Enabled))
        {
            var f = FileOf(l.Id);
            if (!File.Exists(f)) continue;
            using var reader = new StreamReader(f);
            GateKeeperFilter.Parse(reader, all);
        }
        _current = all;
    }

    /// <summary>Datei einer gelöschten Liste entfernen.</summary>
    public void Remove(long id)
    {
        try { File.Delete(FileOf(id)); } catch (IOException) { }
        Rebuild();
    }

    /// <summary>Eine oder alle Listen laden; danach neu zusammenführen.</summary>
    public async Task RefreshAsync(long? only, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var l in store.Lists().Where(l => only == null ? l.Enabled : l.Id == only))
                await FetchAsync(l, ct);
            Rebuild();
        }
        finally { _gate.Release(); }
    }

    private async Task FetchAsync(GkList list, CancellationToken ct)
    {
        var tmp = FileOf(list.Id) + ".part";
        try
        {
            using var client = http.CreateClient("gatekeeper");
            client.Timeout = TimeSpan.FromMinutes(2);
            using var resp = await client.GetAsync(list.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) { store.ListFetched(list.Id, "HTTP " + (int)resp.StatusCode, null); return; }
            if (resp.Content.Headers.ContentLength > MaxBytes) { store.ListFetched(list.Id, "zu groß", null); return; }
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) { store.ListFetched(list.Id, "zu groß", null); return; }
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            GkListEntries parsed;
            using (var reader = new StreamReader(tmp)) parsed = GateKeeperFilter.Parse(reader);
            if (parsed.Count == 0) { store.ListFetched(list.Id, "keine Einträge erkannt", null); return; }
            File.Move(tmp, FileOf(list.Id), overwrite: true);
            store.ListFetched(list.Id, "ok", parsed.Count);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("GateKeeper: Liste {Id} nicht geladen: {Error}", list.Id, e.Message);
            store.ListFetched(list.Id, e is TaskCanceledException ? "Zeitüberschreitung" : "nicht erreichbar", null);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Features.InRelease(Features.GateKeeper)) return;
        try { Rebuild(); } catch (Exception e) when (e is IOException or InvalidOperationException) { log.LogWarning("GateKeeper: Listen nicht gelesen: {Error}", e.Message); }
        // IPv6 beim Start prüfen (danach höchstens alle 10 Minuten bei Bedarf)
        log.LogInformation("GateKeeper: Ziele über IPv6 {State}", GateKeeperIpv6.Available ? "erreichbar" : "nicht erreichbar, die Apps bekommen nur IPv4-Adressen");
        // Kurz nach dem Start laden, was fällig ist; dann stündlich nachsehen
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Features.On(Features.GateKeeper))
                {
                    var due = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - store.RefreshHours * 3600L;
                    if (store.Lists().Any(l => l.Enabled && (l.Fetched ?? 0) < due)) await RefreshAsync(null, ct);
                    store.Prune();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "GateKeeper: Laden der Listen fehlgeschlagen"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>
/// Zähler von GateKeeper im Speicher, alle 30 Sekunden in die Datenbank übernommen. Nur Zahlen und gesperrte Domains,
/// keine erlaubten Ziele und keine Inhalte.
/// </summary>
public sealed class GateKeeperStats(GateKeeperStore store, ILogger<GateKeeperStats> log) : BackgroundService
{
    private sealed class Bucket
    {
        public long Conns, Dns, Blocked, Up, Down;
        public readonly long[] Sizes = new long[GkSizes.Count];
        public readonly ConcurrentDictionary<string, (long Hits, long Last)> Hits = new(StringComparer.Ordinal);
    }

    private ConcurrentDictionary<string, Bucket> _open = new();

    private Bucket Of(string user) => _open.GetOrAdd(user, _ => new Bucket());

    public void Connection(string user) => Interlocked.Increment(ref Of(user).Conns);
    public void Dns(string user) => Interlocked.Increment(ref Of(user).Dns);

    /// <summary>Eine Verbindung ist zu Ende: durchgereichte Bytes je Richtung.</summary>
    public void Traffic(string user, long up, long down)
    {
        var b = Of(user);
        Interlocked.Add(ref b.Up, up);
        Interlocked.Add(ref b.Down, down);
        Interlocked.Increment(ref b.Sizes[GkSizes.Bucket(up + down)]);
    }

    public void Blocked(string user, string domain)
    {
        var b = Of(user);
        Interlocked.Increment(ref b.Blocked);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        b.Hits.AddOrUpdate(domain, _ => (1, now), (_, v) => (v.Hits + 1, now));
    }

    /// <summary>Gesammeltes übernehmen (auch beim Beenden und für Tests).</summary>
    public void Flush()
    {
        var taken = Interlocked.Exchange(ref _open, new ConcurrentDictionary<string, Bucket>());
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var (user, b) in taken)
        {
            try { store.AddCounts(user, day, b.Conns, b.Dns, b.Blocked, b.Hits.Select(h => (h.Key, h.Value.Hits, h.Value.Last)),
                b.Up, b.Down, b.Sizes.Any(n => n > 0) ? b.Sizes : null); }
            catch (Exception e) { log.LogWarning("GateKeeper: Zähler nicht gespeichert: {Error}", e.Message); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                Flush();
            }
        }
        catch (OperationCanceledException) { }
        finally { Flush(); }
    }
}
