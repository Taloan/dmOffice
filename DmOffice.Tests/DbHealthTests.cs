using System.Security.Cryptography;
using DmOffice.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Datenbankprüfung: heile Datei ok, kaputter Index wird neu aufgebaut, fremder Schlüssel ist ein Befund;
/// der Lauf über den ganzen Server prüft alle Datenbanken und schreibt health.json.</summary>
[Collection("server")]
public class DbHealthTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public DbHealthTests(ChatFixture fx) => _fx = fx;

    private static (string Path, string Cs) NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), "dmo-health-" + Guid.NewGuid().ToString("N") + ".db");
        return (path, UserDb.ConnectionStringOf(path, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void Heile_Datenbank_ist_ok()
    {
        var (_, cs) = NewDb();
        using (var c = new SqliteConnection(cs)) { c.Open(); UserDb.Exec(c, "CREATE TABLE t(x); CREATE INDEX i ON t(x); INSERT INTO t VALUES (1),(2),(3)"); }
        Assert.Equal((true, null, false), DbHealth.Check(cs));
    }

    [Fact]
    public void Kaputter_Index_wird_neu_aufgebaut()
    {
        var (_, cs) = NewDb();
        using (var c = new SqliteConnection(cs))
        {
            c.Open();
            // Index einer leeren Tabelle der vollen unterschieben: „wrong # of entries in index i“, die Daten selbst sind heil
            UserDb.Exec(c, "CREATE TABLE t(x); INSERT INTO t VALUES (1),(2),(3); CREATE TABLE u(x); CREATE INDEX i ON u(x);");
            UserDb.Exec(c, "PRAGMA writable_schema=ON; UPDATE sqlite_master SET tbl_name='t', sql='CREATE INDEX i ON t(x)' WHERE name='i'; PRAGMA writable_schema=OFF;");
        }
        SqliteConnection.ClearAllPools();
        var (ok, problem, repaired) = DbHealth.Check(cs);
        Assert.True(ok, problem);
        Assert.True(repaired);
        Assert.Contains("index", problem!);
        Assert.Equal((true, null, false), DbHealth.Check(cs));
    }

    [Fact]
    public void Falscher_Schluessel_ist_ein_Befund()
    {
        var (path, cs) = NewDb();
        using (var c = new SqliteConnection(cs)) { c.Open(); UserDb.Exec(c, "CREATE TABLE t(x)"); }
        SqliteConnection.ClearAllPools();
        var (ok, problem, _) = DbHealth.Check(UserDb.ConnectionStringOf(path, RandomNumberGenerator.GetBytes(32)));
        Assert.False(ok);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Alle_Datenbanken_des_Servers()
    {
        _fx.Space("anna");
        var services = _fx.Factory.Services;
        var health = services.GetRequiredService<DbHealth>();
        var report = health.CheckAll();
        Assert.Contains(report.Items, i => i.Name == "chat");
        Assert.Contains(report.Items, i => i.Name.StartsWith("user:"));
        Assert.All(report.Items, i => Assert.True(i.Ok, i.Label + ": " + i.Problem));
        Assert.True(File.Exists(Path.Combine(services.GetRequiredService<DataPaths>().Root, "health.json")));
    }

    [Fact]
    public async Task Jetzt_pruefen_startet_sofort_und_schreibt_neuen_Stand()
    {
        _fx.Space("anna");
        var health = _fx.Factory.Services.GetRequiredService<DbHealth>();
        var before = health.CheckAll().At;
        await Task.Delay(20);
        health.RunNow();
        Assert.True(health.Running);
        for (var i = 0; i < 200 && health.Running; i++) await Task.Delay(50);
        Assert.False(health.Running);
        Assert.True(health.Last!.At > before);
    }
}
