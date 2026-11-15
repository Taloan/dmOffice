using DmOffice.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>Hilfe für eine beschädigte Datenbank: nur diese eine reparieren oder aus einem Core-Stand ersetzen - alle anderen bleiben.</summary>
[Collection("server")]
public class DbRescueTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public DbRescueTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public void Uebernimmt_Aufbau_Zeilen_Indizes_und_Version()
    {
        var dir = Directory.CreateTempSubdirectory("dmdbr").FullName;
        try
        {
            var from = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "a.db"), Pooling = false }.ToString();
            var to = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "b.db"), Pooling = false }.ToString();
            using (var c = new SqliteConnection(from))
            {
                c.Open();
                UserDb.Exec(c, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT, data BLOB); CREATE INDEX t_name ON t(name); PRAGMA user_version=42;");
                for (var i = 0; i < 50; i++) UserDb.Exec(c, "INSERT INTO t(name, data) VALUES($n, $d)", ("$n", "Zeile " + i), ("$d", new byte[] { (byte)i }));
            }
            var (tables, incomplete, rows) = DbRescue.Copy(from, to);
            Assert.Equal((1, 0, 50L), (tables, incomplete, rows));
            using var check = new SqliteConnection(to);
            check.Open();
            Assert.Equal(42L, new SqliteCommand("PRAGMA user_version", check).ExecuteScalar());
            Assert.Equal(50L, new SqliteCommand("SELECT COUNT(*) FROM t", check).ExecuteScalar());
            Assert.Equal(1L, new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 't_name'", check).ExecuteScalar());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Reparieren_und_aus_Sicherung_ersetzen_nur_fuer_diese_Person()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var rescue = services.GetRequiredService<DbRescue>();
        var paths = services.GetRequiredService<DataPaths>();
        var ben = _fx.Space("ben");
        var dora = _fx.Space("dora");
        var stamp = Guid.NewGuid().ToString("N")[..8];
        DmOffice.Dav.FileStore.Put(ben.Db, $"vorher-{stamp}.txt", System.Text.Encoding.UTF8.GetBytes("vorher " + stamp), "text/plain");
        var core = Directory.CreateTempSubdirectory("dmdbc").FullName;
        var before = config.Current;
        try
        {
            // Reparieren: alles bleibt, die alte Datei liegt daneben
            var repaired = await rescue.RepairAsync("user:" + ben.User.Id, CancellationToken.None);
            Assert.True(repaired.Ok, repaired.Message);
            Assert.Equal(0, repaired.TablesIncomplete);
            ben = _fx.Space("ben");
            Assert.NotNull(DmOffice.Dav.FileStore.Get(ben.Db, $"vorher-{stamp}.txt"));
            Assert.Contains(Directory.EnumerateFiles(Path.GetDirectoryName(paths.UserDb(ben.User.Id))!), f => f.Contains(".beschaedigt-"));

            // Core-Stand in einen Ordner, danach kommt eine Datei dazu - bei Ben und bei Dora
            config.Update(s => { s.BackupOwnTarget = true; s.BackupTarget = "folder"; s.BackupFolder = core; s.BackupS3Prefix = null; });
            var backup = new S3Backup(paths, services.GetRequiredService<Vault>(), services.GetRequiredService<UserStore>(), config, services, NullLogger<S3Backup>.Instance);
            await backup.RunAsync(S3Backup.RemoteOf(config), CancellationToken.None);
            Assert.Null(backup.Status.LastError);
            DmOffice.Dav.FileStore.Put(ben.Db, $"nachher-{stamp}.txt", System.Text.Encoding.UTF8.GetBytes("nachher " + stamp), "text/plain");
            DmOffice.Dav.FileStore.Put(dora.Db, $"dora-{stamp}.txt", System.Text.Encoding.UTF8.GetBytes("dora " + stamp), "text/plain");

            var stands = await rescue.StandsAsync(CancellationToken.None);
            Assert.NotNull(stands);
            var replaced = await rescue.ReplaceAsync("user:" + ben.User.Id, stands![0].Name, CancellationToken.None);
            Assert.True(replaced.Ok, replaced.Message);

            ben = _fx.Space("ben");
            Assert.NotNull(DmOffice.Dav.FileStore.Get(ben.Db, $"vorher-{stamp}.txt"));
            Assert.Null(DmOffice.Dav.FileStore.Get(ben.Db, $"nachher-{stamp}.txt"));
            // Die spätere Datei ist nicht verloren: ohne Namen im Ordner für Gerettetes
            Assert.True(replaced.Rescued >= 1);
            var found = ben.Db.Query("SELECT path FROM files WHERE path LIKE $p", r => r.GetString(0), ("$p", S3Mirror.RescueFolder + "/%"));
            Assert.Contains(found, p => DmOffice.Dav.FileStore.Get(ben.Db, p) is { } f && System.Text.Encoding.UTF8.GetString(ReadAll(ben.Db, p)) == "nachher " + stamp);
            // Dora bleibt, wie sie ist
            Assert.NotNull(DmOffice.Dav.FileStore.Get(_fx.Space("dora").Db, $"dora-{stamp}.txt"));

            // Unbekannter Stand: klare Meldung, nichts geändert
            var wrong = await rescue.ReplaceAsync("user:" + ben.User.Id, "gibt-es-nicht", CancellationToken.None);
            Assert.False(wrong.Ok);
            Assert.NotNull(DmOffice.Dav.FileStore.Get(_fx.Space("ben").Db, $"vorher-{stamp}.txt"));
        }
        finally
        {
            config.Update(s => { s.BackupOwnTarget = before.BackupOwnTarget; s.BackupTarget = before.BackupTarget; s.BackupFolder = before.BackupFolder; s.BackupS3Prefix = before.BackupS3Prefix; });
            try { Directory.Delete(core, recursive: true); } catch (IOException) { }
        }
    }

    private static byte[] ReadAll(UserDb db, string path)
    {
        using var s = DmOffice.Dav.FileStore.OpenRead(db, path);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}
