using System.Security.Cryptography;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Pruefsummen (SHA-256 des Klartexts) der Dateiinhalte, Spalte sha256 in files, file_versions und file_trash_items.
/// Neue Inhalte bekommen sie beim Schreiben (FileStore.Commit, der BlobWriter rechnet mit), ausgelagerte beim Auslagern
/// (FileStore.MigrateBlobs). Fuer alles Fruehere rechnet <see cref="Compute"/> nach. Damit erkennt die App, ob ein Foto
/// schon irgendwo in der Ablage liegt - egal unter welchem Namen und in welchem Ordner (<see cref="Lookup"/>).
/// Spaeter auch Grundlage fuer "Duplikate finden" (docs/duplikate.md).
/// </summary>
public static class FileHashes
{
    /// <summary>Zeilen mit Inhalt, deren Pruefsumme noch fehlt. Inhalte, die noch in der Datenbank liegen (data), rechnet
    /// das Auslagern mit - die bleiben hier aussen vor, sonst wuerden sie zweimal gelesen.</summary>
    private static string Missing(string table) =>
        "sha256 IS NULL AND data IS NULL" + (table == "file_versions" ? "" : " AND is_dir=0");

    /// <summary>Wie viele Dateien (ohne Versionen und Papierkorb) noch keine Pruefsumme haben.</summary>
    public static long PendingFiles(UserDb db) =>
        Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM files WHERE sha256 IS NULL AND is_dir=0") ?? 0L);

    /// <summary>Alle Zeilen ohne Pruefsumme, die sich jetzt nachrechnen lassen.</summary>
    public static long Pending(UserDb db) =>
        UserDb.HashTables.Sum(t => Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM {t} WHERE {Missing(t)}") ?? 0L));

    /// <summary>Pruefsumme einer Datei, null wenn (noch) unbekannt.</summary>
    public static string? Of(UserDb db, string path) =>
        db.Scalar("SELECT sha256 FROM files WHERE path=$p AND is_dir=0", ("$p", path)) as string;

    /// <summary>
    /// Fehlende Pruefsummen nachrechnen, Zeile fuer Zeile: Inhalt als Stream lesen, dann nur eintragen, wenn die Zeile noch
    /// denselben Inhalt hat (gleicher Blob, gleiche Groesse) - sonst hat sie inzwischen jemand neu geschrieben und dabei
    /// ihre eigene Pruefsumme mitgebracht. Abbrechen geht jederzeit; der naechste Aufruf macht bei den uebrigen weiter.
    /// Dateien zuerst, dann Versionen und Papierkorb.
    /// </summary>
    /// <param name="progress">Nach jeder Zeile: so viele eingetragen.</param>
    /// <returns>Eingetragene Pruefsummen.</returns>
    public static int Compute(UserDb db, CancellationToken ct = default, Action<int>? progress = null)
    {
        int n = 0;
        foreach (var table in UserDb.HashTables)
        {
            long last = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var next = db.Query($"SELECT rowid, blob, size FROM {table} WHERE rowid>$last AND {Missing(table)} ORDER BY rowid LIMIT 1",
                    r => (Id: r.GetInt64(0), Blob: r.IsDBNull(1) ? null : r.GetString(1), Size: r.GetInt64(2)), ("$last", last));
                if (next.Count == 0) break;
                var (rowid, blob, size) = next[0];
                last = rowid;
                string? hash;
                // Ohne Blob und ohne data: leerer Inhalt
                try { hash = blob == null ? (size == 0 ? Empty : null) : HashBlob(db, blob, size); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or CryptographicException or InvalidDataException)
                {
                    // Blob fehlt oder ist beschaedigt: keine Pruefsumme - lieber unbekannt als falsch
                    continue;
                }
                if (hash == null) continue;
                var changed = db.Exec($"UPDATE {table} SET sha256=$h WHERE rowid=$r AND sha256 IS NULL AND data IS NULL AND blob IS $b AND size=$s",
                    ("$h", hash), ("$r", rowid), ("$b", blob), ("$s", size));
                if (changed == 0) continue;
                n++;
                progress?.Invoke(n);
            }
        }
        return n;
    }

    /// <summary>Pruefsumme eines Blobs; null, wenn er nicht so lang ist, wie die Zeile sagt.</summary>
    private static string? HashBlob(UserDb db, string blob, long size)
    {
        using var s = db.Blobs.OpenRead(blob);
        var hash = Hash(s, out var length);
        return length == size ? hash : null;
    }

    private static readonly string Empty = FileStore.Hex(SHA256.HashData(ReadOnlySpan<byte>.Empty));

    private static string Hash(Stream s, out long length)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BlobStore.ChunkSize];
        length = 0;
        int read;
        while ((read = s.Read(buffer)) > 0) { sha.AppendData(buffer, 0, read); length += read; }
        return FileStore.Hex(sha.GetHashAndReset());
    }

    public static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    /// <summary>Hoechstens so viele Pruefsummen in einer Anfrage.</summary>
    public const int MaxLookup = 5000;

    /// <summary>
    /// Welche dieser Inhalte (Pruefsumme und Groesse) liegen irgendwo in der Ablage? Je gefundenem ein Pfad als Beleg.
    /// Zaehlt nur, was als Datei sichtbar ist: nicht der Papierkorb und nicht die Versionen (eigene Tabellen), nicht der
    /// geschuetzte notizi-Ordner. Die Groesse muss mit passen - ein Treffer heisst dann wirklich derselbe Inhalt.
    /// Was in den Papierkorb kommt, zaehlt sofort nicht mehr.
    /// </summary>
    /// <param name="dir">Nur direkt in diesem Ordner suchen (App: "Duplikate ueberspringen" aus); null: ueberall.</param>
    public static Dictionary<(string Sha256, long Size), string> Lookup(UserDb db, IEnumerable<(string Sha256, long Size)> items, string? dir = null)
    {
        var root = FileStore.ProtectedRoot(db);
        var found = new Dictionary<(string, long), string>();
        using var conn = db.Open();
        foreach (var (sha, size) in items.Select(i => (i.Sha256.ToLowerInvariant(), i.Size)).Distinct())
        {
            if (!ValidHash(sha) || size < 0) continue;
            var inDir = dir == null ? "" : " AND parent=$d";
            var path = UserDb.Query(conn, $"SELECT path FROM files WHERE sha256=$h AND size=$s AND is_dir=0{inDir} ORDER BY length(path), path",
                r => r.GetString(0), ("$h", sha), ("$s", size), ("$d", dir)).FirstOrDefault(p => !FileStore.InRoot(p, root));
            if (path != null) found[(sha, size)] = path;
        }
        return found;
    }
}

/// <summary>
/// Im Hintergrund fehlende Pruefsummen nachrechnen (<see cref="FileHashes.Compute"/>): kurz nach dem Start, dann stuendlich
/// nachsehen. In Portionen, unterbrechbar und wiederaufnehmbar; meldet sich mit Fortschritt im Aktivitaets-Register.
/// </summary>
public sealed class FileHashJob(Spaces spaces, ILogger<FileHashJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(40), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            await Task.Run(() =>
            {
                foreach (var space in spaces.All())
                {
                    if (ct.IsCancellationRequested) return;
                    try { Run(space, ct); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Activity.Start(space.User.Id, Activity.FilesHashes).Fail(ex.Message); log.LogWarning("Prüfsummen {User}: {Error}", space.User.Login, ex.Message); }
                }
            }, CancellationToken.None);
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void Run(UserSpace space, CancellationToken ct)
    {
        var db = space.Db;
        var pending = FileHashes.Pending(db);
        if (pending == 0) return;
        using var activity = Activity.Start(space.User.Id, Activity.FilesHashes, total: pending);
        log.LogInformation("Prüfsummen {User}: {Count} Inhalte werden nachgerechnet", space.User.Login, pending);
        var done = FileHashes.Compute(db, ct, d => { if (d % 50 == 0) activity.Progress(d); });
        activity.Progress(done);
        log.LogInformation("Prüfsummen {User}: fertig ({Done} nachgerechnet)", space.User.Login, done);
    }
}
