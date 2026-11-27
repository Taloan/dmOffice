using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DmOffice.Core;

/// <summary>
/// Datenbank des Tresors (users/&lt;id&gt;.tresor.db): SQLCipher wie die Benutzerdatenbank, aber mit einem Schlüssel aus dem
/// Tresor-Schlüssel statt aus dem Hauptschlüssel des Servers, und mit eigener Versionierung (nicht die Schemanummern der
/// Benutzerdatenbank). Dateien darin liegen im eigenen <see cref="BlobStore"/> unter blobs/&lt;id&gt;/tresor.
/// </summary>
public sealed class TresorDb : ISqlStore, IDisposable
{
    private readonly string _connectionString;
    public BlobStore Blobs { get; }
    /// <summary>Die Tresor-Dateien (Verzeichnis in der Tabelle files, Inhalt im Speicher dahinter).</summary>
    public DmOffice.TresorFileStore Files { get; }

    static TresorDb() => SQLitePCL.Batteries_V2.Init();

    /// <param name="storage">Speicher der Tresor-Dateien; ohne Angabe der BlobStore (Tresore von vor dem Cryptomator-Format).</param>
    public TresorDb(string path, byte[] tresorKey, string blobDir, Func<TresorDb, DmOffice.ITresorStorage>? storage = null)
    {
        var dbKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, tresorKey, 32, [], Encoding.ASCII.GetBytes("tresor-db"));
        Blobs = new BlobStore(blobDir, HKDF.DeriveKey(HashAlgorithmName.SHA256, tresorKey, 32, [], Encoding.ASCII.GetBytes("tresor-blobs")));
        _connectionString = UserDb.ConnectionStringOf(path, dbKey);
        Migrate();
        Files = new DmOffice.TresorFileStore(this, storage?.Invoke(this) ?? new DmOffice.BlobTresorStorage(Blobs));
    }

    /// <summary>Schritte der Tresor-Datenbank; die Nummer steht in user_version. Nur anhängen, nie ändern.</summary>
    private static readonly string[] Steps =
    [
        // 1: Grundlage
        "CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL)",
        // 2: Dateien
        TresorFiles.Schema,
        // 3: Platzhalter (Schema einer späteren Version - die Nummer bleibt belegt, bestehende Tresore haben den Schritt schon)
        "SELECT 1",
        // 4: Abgleich-Ordner der notizi-Datenbanken im Tresor (Inhalt; Namen und ETags stehen in der Benutzerdatenbank)
        DmOffice.Notes.TresorNotizi.Schema,
        // 5: DMS - Dokumente, Text und Gelerntes der Dokumente im Tresor (die Listen liegen in der Benutzerdatenbank)
        DmOffice.Dms.Dms.Schema,
        // [feature:journal]
        // 6: Tagebücher im Tresor - Einträge und Medien
        DmOffice.Journal.TresorJournals.Schema,
        // [feature:journal:else]
        // // 6: Platzhalter (Schema einer späteren Version - die Nummer bleibt belegt, bestehende Tresore haben den Schritt schon)
        // "SELECT 1",
        // [/feature:journal]
    ];

    public static int Version => Steps.Length;

    private void Migrate()
    {
        using var conn = Open();
        var v = Convert.ToInt32(Scalar(conn, "PRAGMA user_version") ?? 0);
        for (var i = v; i < Steps.Length; i++)
        {
            using var tx = conn.BeginTransaction();
            UserDb.Exec(conn, Steps[i]);
            UserDb.Exec(conn, $"PRAGMA user_version = {i + 1}");
            tx.Commit();
        }
        // Nachhol-Schritte (docs/plugins-konzept.md 7.7): Ein Release ohne eine Funktion ersetzt ihren Schritt durch einen
        // Platzhalter. Ein Tresor, der darunter angelegt oder aktualisiert wurde, steht danach schon auf der Nummer, und der echte
        // Schritt läuft später nicht mehr - darum die Tabellen solcher Funktionen hier bei jedem Öffnen mit IF NOT EXISTS anlegen.
        // [feature:journal]
        UserDb.Exec(conn, IfMissing(DmOffice.Journal.TresorJournals.Schema));
        // [/feature:journal]
        PluginSteps?.Invoke(conn);
    }

    /// <summary>Schema so umschreiben, dass es auf einer Datenbank mit (Teilen) davon nichts ändert.</summary>
    internal static string IfMissing(string sql) => sql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
        .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ").Replace("INSERT INTO ", "INSERT OR IGNORE INTO ");

    /// <summary>Nach den Kern-Schritten: Schemaschritte der Plugins in der Tresor-Datenbank (Plugins/PluginSchemas). Null: keine.</summary>
    public static Action<SqliteConnection>? PluginSteps { get; set; }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        return UserDb.Exec(conn, sql, args);
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        return UserDb.Query(conn, sql, read, args);
    }

    /// <summary>Stimmige Kopie für die Sicherung (VACUUM INTO) - bleibt mit demselben Schlüssel verschlüsselt.</summary>
    public void SnapshotTo(string target) => S3Backup.Vacuum(_connectionString, target);

    public void Dispose()
    {
        // Cryptomator-Speicher: Masterkey aus dem Speicher löschen
        (Files.Storage as IDisposable)?.Dispose();
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }
}
