using DmOffice.Core;

namespace DmOffice.Search;

/// <summary>
/// Hintergrund-Warteschlange der Volltextsuche: arbeitet text_queue aller Benutzer mit eingeschalteter Suche ab -
/// Neues zuerst (die Trigger reihen neue Mails und Dateien sofort ein), den Bestand nach und nach. Begrenzte
/// Parallelitaet (DMOFFICE_INDEX_THREADS, Vorgabe 1 bzw. 2 ab vier Kernen), kurze Pausen zwischen den Eintraegen,
/// OCR in eigenen Prozessen mit niedrigster Prioritaet - Mail, Kalender und Dateien bleiben flott.
/// </summary>
public sealed class TextIndexer(Spaces spaces, ILogger<TextIndexer> log) : BackgroundService
{
    public static int Parallelism { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("DMOFFICE_INDEX_THREADS"), out var n) && n > 0 ? Math.Min(n, 8)
        : Environment.ProcessorCount >= 4 ? 2 : 1;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private DateTime _pruned = DateTime.MinValue;

    /// <summary>Gleich nachsehen statt erst beim naechsten Durchgang (nach Einschalten oder Neuaufbau).</summary>
    public void Kick()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { /* schon geweckt */ }
    }

    /// <summary>Die Warteschlange eines Benutzers abarbeiten, hoechstens so viele Eintraege - auch fuer die Tests.</summary>
    public static async Task<int> RunAsync(UserSpace space, int max, CancellationToken ct, ExtractLimits? limits = null, TimeSpan? pause = null)
    {
        int done = 0;
        while (done < max && !ct.IsCancellationRequested)
        {
            var items = TextIndex.Next(space.Db, Math.Min(Parallelism * 4, max - done));
            if (items.Count == 0) break;
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (item, token) =>
            {
                TextIndex.Process(space, item, limits, token);
                if (pause is { } p && p > TimeSpan.Zero) await Task.Delay(p, token);
            });
            done += items.Count;
            TextIndex.MarkRun(space.Db);
        }
        return done;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            bool busy = false;
            foreach (var space in spaces.All())
            {
                if (stop.IsCancellationRequested) break;
                try
                {
                    var settings = space.Settings.TextIndex;
                    if (!settings.Enabled) continue;
                    // Schalter fuer die Trigger nachziehen (etwa nach Wiederherstellen einer Sicherung)
                    if (space.Db.Get(TextIndex.OnKey) == null) TextIndex.Apply(space, new TextIndexSettings());
                    if (DateTime.UtcNow - _pruned > TimeSpan.FromHours(1)) TextIndex.Prune(space);
                    if (!TextIndex.HasWork(space.Db)) continue;
                    // Je Benutzer ein Happen, dann der naechste - keiner wartet auf den Bestand eines anderen
                    using var activity = Activity.Start(space.User.Id, Activity.TextIndex);
                    var n = await RunAsync(space, 50, stop, pause: TimeSpan.FromMilliseconds(50));
                    busy |= n > 0;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception e) { Activity.Start(space.User.Id, Activity.TextIndex).Fail(e.Message); log.LogWarning("Volltextindex {User}: {Error}", space.User.Id, e.Message); }
            }
            if (DateTime.UtcNow - _pruned > TimeSpan.FromHours(1)) _pruned = DateTime.UtcNow;
            if (busy) continue;
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { break; }
        }
    }
}
