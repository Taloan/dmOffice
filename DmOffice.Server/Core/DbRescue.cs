using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Core;

/// <summary>
/// Hilfe für eine beschädigte Datenbank (Verwaltung → System → Datenbanken), immer nur für diese eine - alle anderen bleiben, wie sie sind:
/// <list type="bullet">
/// <item><b>Reparieren versuchen:</b> alles Lesbare in eine neue Datenbank übernehmen (Aufbau, Zeilen, Version), prüfen, dann tauschen.</item>
/// <item><b>Aus Sicherung ersetzen:</b> nur diese Datenbank aus einem Core-Stand holen, prüfen, dann tauschen. Bei einer Person werden
/// Dateien, die nach dem Stand dazukamen, nicht verloren: Sie kommen ohne Namen nach „Nach Wiederherstellung gefunden“.</item>
/// </list>
/// Die beschädigte Datei bleibt in beiden Fällen als Kopie liegen (&lt;name&gt;.beschaedigt-&lt;Zeit&gt;).
/// </summary>
public sealed class DbRescue(DataPaths paths, UserStore users, Spaces spaces, ServerConfig config, DbHealth health, IServiceProvider services, ILogger<DbRescue> log)
{
    public sealed record Outcome(bool Ok, string Message, int Tables = 0, int TablesIncomplete = 0, long Rows = 0, int Rescued = 0);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Pfad und Verbindung einer Datenbank aus der Prüfung („user:&lt;id&gt;“, chat, albums, push).</summary>
    private (string Path, string ConnectionString, UserRecord? User) Target(string name)
    {
        if (name.StartsWith("user:", StringComparison.Ordinal))
        {
            var user = users.ById(name[5..]) ?? throw new KeyNotFoundException("Diesen Benutzer gibt es nicht.");
            var path = paths.UserDb(user.Id);
            return (path, UserDb.ConnectionStringOf(path, users.DbKey(user)), user);
        }
        var cs = name switch
        {
            "chat" => services.GetRequiredService<Chat.ChatStore>().ConnectionString,
            "albums" => services.GetRequiredService<Gallery.AlbumStore>().ConnectionString,
            "push" => services.GetRequiredService<WebPush>().ConnectionString,
            _ => throw new KeyNotFoundException("Diese Datenbank gibt es nicht."),
        };
        return (UserDb.PathOf(cs), cs, null);
    }

    private static string WithFile(string connectionString, string file, bool readOnly = false)
    {
        var b = new SqliteConnectionStringBuilder(UserDb.WithFile(connectionString, file)) { Pooling = false };
        if (readOnly) b.Mode = SqliteOpenMode.ReadOnly;
        return b.ToString();
    }

    // ---------------- Reparieren versuchen ----------------

    public async Task<Outcome> RepairAsync(string name, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var (path, cs, user) = Target(name);
            if (!File.Exists(path)) return new(false, "Die Datei gibt es nicht.");
            var fresh = path + ".rettung";
            DeleteDb(fresh);
            var (tables, incomplete, rows) = Copy(WithFile(cs, path, readOnly: true), WithFile(cs, fresh));
            var (ok, problem, _) = DbHealth.Check(WithFile(cs, fresh));
            if (!ok) { DeleteDb(fresh); return new(false, "Auch die neue Datenbank ist nicht in Ordnung: " + problem); }
            var kept = Swap(path, fresh, user);
            health.RunNow();
            log.LogWarning("Datenbank {Name} repariert: {Tables} Tabellen, {Incomplete} unvollständig, {Rows} Zeilen; beschädigte Datei: {Kept}", name, tables, incomplete, rows, kept);
            return new(true, incomplete == 0
                ? $"Repariert: {tables} Tabellen und {DmOffice.Core.Num.N(rows)} Einträge übernommen. Die beschädigte Datei liegt als {Path.GetFileName(kept)} daneben."
                : $"Repariert, aber {incomplete} von {tables} Tabellen ließen sich nicht ganz lesen; {DmOffice.Core.Num.N(rows)} Einträge übernommen. Fehlt etwas Wichtiges, die Datenbank aus der Sicherung ersetzen. Die beschädigte Datei liegt als {Path.GetFileName(kept)} daneben.",
                tables, incomplete, rows);
        }
        catch (Exception e) when (e is SqliteException or IOException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return new(false, "Reparieren ging nicht: " + e.Message);
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Aufbau und alles Lesbare übernehmen: Tabellen zuerst (Zeile für Zeile - eine unlesbare Stelle beendet nur diese Tabelle),
    /// dann Indizes, Trigger und Sichten, zuletzt die Version (user_version), damit keine Migration neu läuft.
    /// </summary>
    internal static (int Tables, int Incomplete, long Rows) Copy(string fromCs, string toCs)
    {
        using var from = new SqliteConnection(fromCs);
        from.Open();
        using var to = new SqliteConnection(toCs);
        to.Open();
        var schema = new List<(string Type, string Name, string Sql)>();
        using (var cmd = from.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) schema.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        var version = Convert.ToInt64(new SqliteCommand("PRAGMA user_version", from).ExecuteScalar());
        UserDb.Exec(to, "PRAGMA foreign_keys=OFF");
        int tables = 0, incomplete = 0;
        long rows = 0;
        // Virtuelle Tabellen (Volltext) zuerst: Sie legen ihre Hilfstabellen selbst an, deren Inhalt danach übernommen wird
        var virtuals = schema.Where(x => x.Type == "table" && x.Sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var v in virtuals) UserDb.Exec(to, v.Sql);
        bool Shadow(string t) => virtuals.Any(v => t.StartsWith(v.Name + "_", StringComparison.Ordinal));
        foreach (var (_, table, sql) in schema.Where(x => x.Type == "table" && !x.Sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase)))
        {
            if (Shadow(table)) UserDb.Exec(to, $"DELETE FROM \"{table.Replace("\"", "\"\"")}\"");
            else UserDb.Exec(to, sql);
            tables++;
            using var tx = to.BeginTransaction();
            try
            {
                using var read = from.CreateCommand();
                read.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\"";
                using var r = read.ExecuteReader();
                var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
                using var ins = to.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = $"INSERT OR IGNORE INTO \"{table.Replace("\"", "\"\"")}\" ({string.Join(",", cols.Select(c => "\"" + c.Replace("\"", "\"\"") + "\""))}) VALUES ({string.Join(",", cols.Select((_, i) => "$p" + i))})";
                var ps = cols.Select((_, i) => ins.Parameters.Add(new SqliteParameter("$p" + i, null))).ToList();
                while (true)
                {
                    try { if (!r.Read()) break; }
                    catch (SqliteException) { incomplete++; break; }   // unlesbare Seite: der Rest dieser Tabelle fehlt
                    for (var i = 0; i < cols.Count; i++) ps[i].Value = r.IsDBNull(i) ? DBNull.Value : r.GetValue(i);
                    ins.ExecuteNonQuery();
                    rows++;
                }
            }
            catch (SqliteException) { incomplete++; }
            tx.Commit();
        }
        foreach (var (_, _, sql) in schema.Where(x => x.Type is "index" or "trigger" or "view"))
            try { UserDb.Exec(to, sql); } catch (SqliteException) { }
        UserDb.Exec(to, $"PRAGMA user_version={version}");
        return (tables, incomplete, rows);
    }

    // ---------------- Aus Sicherung ersetzen ----------------

    /// <summary>Die Stände am Ziel des Core-Backups, neueste zuerst - null: kein Core-Backup eingerichtet.</summary>
    public async Task<List<S3Backup.Stand>?> StandsAsync(CancellationToken ct)
    {
        if (!S3Backup.TargetConfigured(config)) return null;
        using var remote = S3Backup.RemoteOf(config);
        return (await S3Backup.ListAsync(remote, S3Backup.PrefixOf(config.Current), ct)).OrderByDescending(s => s.Created).ToList();
    }

    public async Task<Outcome> ReplaceAsync(string name, string standName, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        var temp = Path.Combine(Path.GetTempPath(), "dmoffice-dbrescue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (path, cs, user) = Target(name);
            if (!S3Backup.TargetConfigured(config)) return new(false, "Es ist kein Core-Backup eingerichtet.");
            Directory.CreateDirectory(temp);
            string file;
            using (var remote = S3Backup.RemoteOf(config))
            {
                var stand = (await S3Backup.ListAsync(remote, S3Backup.PrefixOf(config.Current), ct)).FirstOrDefault(s => s.Name == standName)
                    ?? throw new KeyNotFoundException("Diesen Stand gibt es am Ziel nicht.");
                file = Path.Combine(temp, stand.Name + ".dmb");
                await remote.DownloadAsync(stand.Key, file, ct);
            }
            var key = Environment.GetEnvironmentVariable("DMOFFICE_MASTER_KEY");
            using var reader = StandReader.Open(file, key, string.IsNullOrWhiteSpace(key) ? Path.Combine(paths.Root, "master.key") : null);
            // Dieselbe Datei im Stand: gleicher Platz relativ zum Datenverzeichnis
            var inStand = Path.Combine(reader.Paths.Root, Path.GetRelativePath(paths.Root, path));
            if (!File.Exists(inStand)) return new(false, "Diese Datenbank ist im Stand nicht enthalten.");
            var fresh = path + ".sicherung";
            DeleteDb(fresh);
            // Über eine Verbindung kopieren (nimmt ein Journal mit), nicht als Datei
            using (var src = new SqliteConnection(WithFile(cs, inStand, readOnly: true)))
            {
                src.Open();
                using var dst = new SqliteConnection(WithFile(cs, fresh));
                dst.Open();
                src.BackupDatabase(dst);
            }
            var (ok, problem, _) = DbHealth.Check(WithFile(cs, fresh));
            if (!ok) { DeleteDb(fresh); return new(false, "Die Datenbank im Stand lässt sich nicht öffnen oder ist nicht in Ordnung: " + problem); }
            var kept = Swap(path, fresh, user);
            var rescued = user != null ? await RescueOrphansAsync(user, ct) : 0;
            health.RunNow();
            log.LogWarning("Datenbank {Name} aus dem Stand {Stand} ersetzt; {Rescued} Dateien gerettet; beschädigte Datei: {Kept}", name, standName, rescued, kept);
            return new(true, $"Aus dem Stand ersetzt. Die bisherige Datei liegt als {Path.GetFileName(kept)} daneben."
                + (rescued > 0 ? $" {rescued} Dateien, die nach dem Stand dazukamen, liegen im Ordner „{S3Mirror.RescueFolder}“." : ""), Rescued: rescued);
        }
        catch (Exception e) when (e is SqliteException or IOException or KeyNotFoundException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or HttpRequestException or Amazon.Runtime.AmazonServiceException)
        {
            return new(false, "Ersetzen ging nicht: " + (e is IOException or HttpRequestException or Amazon.Runtime.AmazonServiceException ? S3Remote.Describe(e) : e.Message));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { }
            Gate.Release();
        }
    }

    /// <summary>
    /// Nach dem Ersetzen der Datenbank einer Person: Dateiinhalte, auf die nichts mehr zeigt (nach dem Stand hochgeladen), als Dateien
    /// ohne Namen retten - hier liegende sofort, nur im Storage Extender liegende beim nächsten Abgleich (Liste wie nach einer Wiederherstellung).
    /// </summary>
    private async Task<int> RescueOrphansAsync(UserRecord user, CancellationToken ct)
    {
        var space = spaces.Get(user.Id);
        var needed = S3Mirror.Referenced(space.Db);
        var n = 0;
        foreach (var (id, _) in space.Db.Blobs.List().ToList())
        {
            if (needed.Contains(id)) continue;
            try { if (await S3Mirror.RescueOne(space, id, n + 1, ct)) n++; }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException) { log.LogWarning("Retten nach dem Ersetzen ({Id}): {Error}", id, e.Message); }
        }
        // Was nur im Storage Extender liegt: dieselbe Liste wie nach einer Wiederherstellung - der Abgleich rettet, was nicht darin steht
        if (config.Current.S3Mirror)
        {
            var dir = Path.Combine(paths.Root, S3Mirror.KnownFolder);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, user.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(S3Mirror.Referenced(space.Db).ToList()), ct);
        }
        return n;
    }

    // ---------------- Tauschen ----------------

    /// <summary>Die neue Datei an die Stelle der alten; die alte bleibt als Kopie. Gibt den Namen der Kopie zurück.</summary>
    private string Swap(string path, string fresh, UserRecord? user)
    {
        if (user != null) spaces.Forget(user.Id);
        SqliteConnection.ClearAllPools();
        var kept = path + ".beschaedigt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        // zwei Eingriffe in derselben Sekunde: die erste Kopie bleibt
        for (var n = 2; File.Exists(kept); n++) kept = path + ".beschaedigt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + n;
        File.Move(path, kept);
        foreach (var x in new[] { "-wal", "-shm" })
            if (File.Exists(path + x)) File.Move(path + x, kept + x);
        File.Move(fresh, path);
        return kept;
    }

    private static void DeleteDb(string file)
    {
        foreach (var x in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(file + x)) File.Delete(file + x);
    }
}
