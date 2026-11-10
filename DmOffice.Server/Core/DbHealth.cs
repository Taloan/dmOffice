using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DmOffice.Core;

/// <summary>
/// Nächtliche Prüfung aller Datenbanken (je Benutzer, Chat, Alben, Push) mit PRAGMA integrity_check - findet beschädigte Seiten und
/// Indizes (auch Index gegen Tabelle, das quick_check auslässt), bevor sie jemandem auffallen oder in die Sicherung wandern. Kaputte Indizes (der häufigste Befund) baut sie gleich
/// neu auf (REINDEX, die Daten selbst bleiben unberührt); alles andere meldet sie in der Verwaltung und der Aktivität der Admins.
/// Der letzte Bericht liegt in data/health.json (nur Namen und Befunde, keine Inhalte).
/// </summary>
public sealed class DbHealth(DataPaths paths, UserStore users, IServiceProvider services, ILogger<DbHealth> log) : BackgroundService
{
    public sealed record Result(string Name, string Label, bool Ok, string? Problem, bool Repaired, long Ms);
    public sealed record Report(DateTime At, List<Result> Items);

    private const string File = "health.json";
    private int _busy;   // 1, solange eine Prüfung läuft (Knopf und Nachtlauf nie gleichzeitig)
    private Report? _last;
    public bool Running { get; private set; }

    public Report? Last => _last ??= Load();

    private Report? Load()
    {
        try { var f = Path.Combine(paths.Root, File); return System.IO.File.Exists(f) ? JsonSerializer.Deserialize<Report>(System.IO.File.ReadAllText(f)) : null; }
        catch { return null; }
    }

    /// <summary>Gleich prüfen (Knopf in der Verwaltung), im Hintergrund - Running gilt sofort, damit die Seite den Lauf sieht.</summary>
    public void RunNow()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        Running = true;
        _ = Task.Run(() => { try { CheckAll(); } catch (Exception e) { log.LogError(e, "Datenbankprüfung"); } finally { Volatile.Write(ref _busy, 0); } });
    }

    /// <summary>
    /// Eine Datenbank prüfen. Befunde nur in Indizes: REINDEX und noch einmal prüfen. Lässt sich die Datei gar nicht öffnen
    /// (falscher Schlüssel, keine Datenbank), ist das ebenfalls ein Befund.
    /// </summary>
    public static (bool Ok, string? Problem, bool Repaired) Check(string connectionString)
    {
        try
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            UserDb.Exec(conn, "PRAGMA busy_timeout=30000");
            var found = IntegrityCheck(conn);
            if (found.Count == 0) return (true, null, false);
            if (found.All(IndexOnly))
            {
                UserDb.Exec(conn, "REINDEX");
                var again = IntegrityCheck(conn);
                if (again.Count == 0) return (true, Summary(found), true);
                found = again;
            }
            return (false, Summary(found), false);
        }
        catch (Exception e) { return (false, e.Message, false); }
    }

    private static List<string> IntegrityCheck(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "PRAGMA integrity_check(20)";
        var rows = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows is ["ok"] ? [] : rows;
    }

    // "wrong # of entries in index x", "row 5 missing from index x", "non-unique entry in index x" - Daten heil, nur der Index nicht
    private static bool IndexOnly(string line) => line.Contains(" index ", StringComparison.OrdinalIgnoreCase);
    private static string Summary(List<string> lines) => string.Join("; ", lines.Take(3)) + (lines.Count > 3 ? $" (+{lines.Count - 3})" : "");

    /// <summary>Alle Datenbanken prüfen - eine nach der anderen, damit der Server nebenher bedienbar bleibt.</summary>
    public Report CheckAll(CancellationToken ct = default)
    {
        Running = true;
        var items = new List<Result>();
        try
        {
            var dbs = new List<(string Name, string Label, Func<string> Cs)>();
            foreach (var u in users.All())
                if (System.IO.File.Exists(paths.UserDb(u.Id)))
                    dbs.Add(("user:" + u.Id, u.Login, () => UserDb.ConnectionStringOf(paths.UserDb(u.Id), users.DbKey(u))));
            dbs.Add(("chat", "Chat", () => services.GetRequiredService<Chat.ChatStore>().ConnectionString));
            dbs.Add(("albums", "Alben", () => services.GetRequiredService<Gallery.AlbumStore>().ConnectionString));
            dbs.Add(("push", "Push", () => services.GetRequiredService<WebPush>().ConnectionString));
            foreach (var (name, label, cs) in dbs)
            {
                if (ct.IsCancellationRequested) break;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (ok, problem, repaired) = Check(cs());
                items.Add(new Result(name, label, ok, problem, repaired, sw.ElapsedMilliseconds));
                if (!ok) log.LogError("Datenbank {Label} beschädigt: {Problem}", label, problem);
                else if (repaired) log.LogWarning("Datenbank {Label}: Index neu aufgebaut ({Problem})", label, problem);
            }
            var report = new Report(DateTime.UtcNow, items);
            _last = report;
            try { System.IO.File.WriteAllText(Path.Combine(paths.Root, File), JsonSerializer.Serialize(report)); } catch (Exception e) { log.LogWarning(e, "health.json nicht geschrieben"); }
            return report;
        }
        finally { Running = false; }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Erst nach dem Start in Ruhe, dann jede Nacht gegen 4 Uhr (nach dem Core-Backup um 3) - oder wenn der letzte Lauf über einen Tag her ist
        try { await Task.Delay(TimeSpan.FromMinutes(10), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            var last = Last?.At;
            var now = DateTime.Now;
            if ((last == null || DateTime.UtcNow - last > TimeSpan.FromHours(36) || (now.Hour == 4 && DateTime.UtcNow - last > TimeSpan.FromHours(20)))
                && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
                try { await Task.Run(() => CheckAll(stop), stop); } catch (OperationCanceledException) { break; } catch (Exception e) { log.LogError(e, "Datenbankprüfung"); }
                finally { Volatile.Write(ref _busy, 0); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stop); } catch (OperationCanceledException) { break; }
        }
    }
}
