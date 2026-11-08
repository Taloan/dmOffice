using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Tests;

/// <summary>
/// Sicherungen ziehen eine stimmige Kopie per VACUUM INTO (S3Backup, PreUpdateBackup, Tresor). Die Kopie muss mit demselben
/// Schlüssel verschlüsselt bleiben - eine Klartext-Kopie einer SQLCipher-Datenbank läge sonst lesbar in der Sicherung.
/// </summary>
public class VacuumEncryptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vac-" + Guid.NewGuid().ToString("N"));

    public VacuumEncryptionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* beim nächsten Mal */ }
    }

    [Fact]
    public void Kopie_per_VACUUM_INTO_bleibt_verschluesselt()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var source = Path.Combine(_dir, "quelle.db");
        var copy = Path.Combine(_dir, "kopie.db");
        var cs = UserDb.ConnectionStringOf(source, key);
        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('Geheimer Klartext 4711')";
            cmd.ExecuteNonQuery();
        }

        S3Backup.Vacuum(cs, copy);
        SqliteConnection.ClearAllPools();

        // Weder SQLite-Kopf noch Inhalt im Klartext
        var bytes = File.ReadAllBytes(copy);
        Assert.False(bytes.AsSpan(0, 16).SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")));
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Geheimer Klartext")) < 0);

        // Mit dem Schlüssel lesbar, ohne bzw. mit falschem nicht
        using (var conn = new SqliteConnection(UserDb.ConnectionStringOf(copy, key)))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM t";
            Assert.Equal("Geheimer Klartext 4711", cmd.ExecuteScalar());
        }
        foreach (var other in new[] { new SqliteConnectionStringBuilder { DataSource = copy }.ToString(), UserDb.ConnectionStringOf(copy, RandomNumberGenerator.GetBytes(32)) })
        {
            using var conn = new SqliteConnection(other);
            Assert.ThrowsAny<SqliteException>(() =>
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT v FROM t";
                cmd.ExecuteScalar();
            });
        }
    }
}
