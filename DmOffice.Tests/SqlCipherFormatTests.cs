using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Tests;

/// <summary>
/// Die Datenbanken liegen im Format von SQLCipher 4. Fixtures/sqlcipher4.db hat SQLCipher 4.5.2 geschrieben (roher Schlüssel,
/// 32 × 0x3F, Tabelle t mit „hallo alt“): Der aktuelle Stand muss sie lesen und weiterschreiben können.
/// </summary>
public class SqlCipherFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fmt-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Key = Enumerable.Repeat((byte)0x3F, 32).ToArray();

    public SqlCipherFormatTests() { SQLitePCL.Batteries_V2.Init(); Directory.CreateDirectory(_dir); }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* beim nächsten Mal */ }
    }

    private static string Scalar(string cs, string sql)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    [Fact]
    public void Datei_von_SQLCipher_4_bleibt_lesbar_und_beschreibbar()
    {
        // Sonderzeichen im Pfad: die URI-Form muss sie maskieren
        var sub = Path.Combine(_dir, "a #1 50% ?x");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(sub, "alt.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sqlcipher4.db"), file);
        var cs = UserDb.ConnectionStringOf(file, Key);
        Assert.Equal("hallo alt", Scalar(cs, "SELECT group_concat(x, '|') FROM t"));
        Scalar(cs, "INSERT INTO t VALUES('hallo neu')");
        Assert.Equal("hallo alt|hallo neu", Scalar(cs, "SELECT group_concat(x, '|') FROM t"));
        Assert.Equal(file, UserDb.PathOf(cs));
        // Kein anderer Pfad entstanden
        Assert.Single(Directory.GetFiles(sub, "*.db"));
    }

    [Fact]
    public void Neue_Datei_ist_verschluesselt_und_nur_mit_dem_Schluessel_lesbar()
    {
        var file = Path.Combine(_dir, "neu.db");
        Scalar(UserDb.ConnectionStringOf(file, Key), "CREATE TABLE t(x TEXT); INSERT INTO t VALUES('geheim')");
        SqliteConnection.ClearAllPools();
        var head = File.ReadAllBytes(file).Take(16).ToArray();
        Assert.NotEqual("SQLite format 3\0"u8.ToArray(), head);
        Assert.DoesNotContain("geheim", File.ReadAllText(file));
        Assert.ThrowsAny<SqliteException>(() => Scalar(new SqliteConnectionStringBuilder { DataSource = file }.ToString(), "SELECT x FROM t"));
        Assert.ThrowsAny<SqliteException>(() => Scalar(UserDb.ConnectionStringOf(file, new byte[32]), "SELECT x FROM t"));
        Assert.Equal("geheim", Scalar(UserDb.ConnectionStringOf(file, Key), "SELECT x FROM t"));
        // Andere Datei, gleiches Format
        Assert.Equal(Path.Combine(_dir, "b.db"), UserDb.PathOf(UserDb.WithFile(UserDb.ConnectionStringOf(file, Key), Path.Combine(_dir, "b.db"))));
    }
}
