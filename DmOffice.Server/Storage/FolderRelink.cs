using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Storage;

/// <summary>
/// Stand einer Neuzuordnung (Galerie- oder Audiothek-Ordner zeigt jetzt auf einen anderen Ort): wann umgestellt wurde und
/// das Ergebnis des Abgleichs im Hintergrund. Am Ordner gespeichert, damit ein Neustart mittendrin weitermacht.
/// </summary>
public sealed class RelinkState
{
    public DateTime At { get; set; }
    /// <summary>Abgleich steht noch aus oder läuft.</summary>
    public bool Pending { get; set; }
    public int Total { get; set; }
    public int Found { get; set; }
    public int RemovedCount { get; set; }
    /// <summary>Entfernte Dateien (Pfad im Ordner), höchstens <see cref="FolderRelink.ReportMax"/>.</summary>
    public List<string> Removed { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// Gemeinsames für „Ordner neu zuordnen“ (Galerie und Audiothek): Pfade umrechnen, Stichprobe am neuen Ort, Abgleich aller
/// Einträge. Im Index steht der Pfad relativ zum externen Ordner - der Teil unterhalb des Galerie- bzw. Audiothek-Ordners
/// bleibt, der Anfang wird getauscht.
/// </summary>
public static class FolderRelink
{
    public const int SampleMax = 200, ReportMax = 1000;
    /// <summary>Pause je Ordner beim Abgleich (in Ruhe; Tests: null).</summary>
    public static TimeSpan Pause { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>Präfix eines Pfads unter <paramref name="root"/> („“ für die Wurzel, sonst „root/“).</summary>
    public static string Prefix(string root) => root.Length == 0 ? "" : root + "/";

    /// <summary>Pfad unter dem alten Ordner auf den neuen umrechnen; null, wenn er nicht darunter liegt.</summary>
    public static string? Map(string oldRoot, string newRoot, string path)
    {
        var from = Prefix(oldRoot);
        if (!path.StartsWith(from, StringComparison.Ordinal)) return null;
        return Prefix(newRoot) + path[from.Length..];
    }

    /// <summary>
    /// Schlüssel mit Präfix <paramref name="from"/> auf <paramref name="to"/> umschreiben (zwei Schritte, damit sich alte und neue
    /// Schlüssel unterwegs nicht in die Quere kommen; Steuerzeichen kommen in Pfaden nicht vor). length/substr zählen in SQLite
    /// Zeichen - für beide Präfixe gleich gerechnet.
    /// </summary>
    public static int RenameKeys(SqliteConnection conn, string table, string col, string from, string to, string? where = null, params (string, object?)[] args)
    {
        var w = where == null ? "" : " AND " + where;
        var n = UserDb.Exec(conn, $"UPDATE {table} SET {col} = char(1) || $to || substr({col}, length($from) + 1) WHERE substr({col}, 1, length($from)) = $from{w}",
            [("$from", from), ("$to", to), .. args]);
        UserDb.Exec(conn, $"UPDATE {table} SET {col} = substr({col}, 2) WHERE substr({col}, 1, 1) = char(1){w}", args);
        return n;
    }

    public static bool HasTable(SqliteConnection conn, string name) =>
        UserDb.Query(conn, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n", r => 1, ("$n", name)).Count > 0;

    /// <summary>
    /// Stichprobe: liegt am neuen Ort eine Datei mit demselben Pfad und derselben Größe? Je Ordner einmal auflisten.
    /// Gibt (geprüft, gefunden) zurück.
    /// </summary>
    public static async Task<(int Checked, int Found)> ProbeAsync(IStorageDriver driver, IReadOnlyList<(string Path, long Size)> sample, CancellationToken ct)
    {
        var found = 0;
        foreach (var dir in sample.GroupBy(x => StoragePath.Parent(x.Path)))
        {
            var there = await ListOrEmptyAsync(driver, dir.Key, ct);
            foreach (var (path, size) in dir)
                if (there.TryGetValue(path[(path.LastIndexOf('/') + 1)..], out var e) && e.Size == size) found++;
        }
        return (sample.Count, found);
    }

    /// <summary>Dateien eines Ordners nach Name; ein fehlender Ordner ist leer, andere Fehler gehen durch (Speicher antwortet nicht).</summary>
    public static async Task<Dictionary<string, DriverEntry>> ListOrEmptyAsync(IStorageDriver driver, string dir, CancellationToken ct)
    {
        try
        {
            return (await driver.ListAsync(dir, ct)).Where(x => !x.IsDir).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or KeyNotFoundException or HttpRequestException or UnauthorizedAccessException && e is not OperationCanceledException)
        {
            // Gibt es den Ordner nicht, ist er leer; sonst (Netz weg) weiterreichen
            if (dir.Length > 0 && await driver.StatAsync(dir, ct) == null) return [];
            throw;
        }
    }

    /// <summary>
    /// Abgleich aller Einträge am neuen Ort: je Ordner auflisten; was dort nicht liegt, kommt in die Liste der fehlenden.
    /// <paramref name="progress"/>: (geprüft, gefunden). Wirft, wenn der Speicher nicht antwortet - dann bleibt alles, wie es ist.
    /// </summary>
    public static async Task<List<string>> MissingAsync(IStorageDriver driver, string root, IReadOnlyList<string> paths, Action<int, int> progress,
        Func<bool> stillWanted, CancellationToken ct)
    {
        // Wurzel nicht da oder leer: eher ist der Speicher weg als alle Dateien - nichts entfernen
        if (root.Length > 0 && (await driver.StatAsync(root, ct))?.IsDir != true || (await driver.ListAsync(root, ct)).Count == 0)
            throw new IOException("Der neue Ordner meldet keine einzige Datei – nichts entfernt.");
        var missing = new List<string>();
        int done = 0, found = 0;
        foreach (var dir in paths.GroupBy(StoragePath.Parent))
        {
            ct.ThrowIfCancellationRequested();
            if (!stillWanted()) throw new OperationCanceledException();
            var there = await ListOrEmptyAsync(driver, dir.Key, ct);
            foreach (var p in dir)
            {
                done++;
                if (there.ContainsKey(p[(p.LastIndexOf('/') + 1)..])) found++; else missing.Add(p);
            }
            progress(done, found);
            if (Pause > TimeSpan.Zero) await Task.Delay(Pause, ct);
        }
        return missing;
    }

    /// <summary>Änderungszeit gleich? Nach einer Neuzuordnung auf ganze Sekunden (SFTP liefert Sekunden, CIFS Bruchteile).</summary>
    public static bool SameTime(long known, long now, bool coarse) =>
        known == now || coarse && Math.Floor(known / 1000.0) == Math.Floor(now / 1000.0);
}
