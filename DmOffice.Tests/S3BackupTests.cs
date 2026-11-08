using DmOffice.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Json;

namespace DmOffice.Tests;

/// <summary>Sicherung nach S3 und zurueck in ein leeres Datenverzeichnis: verschluesselt, ohne Hauptschluessel,
/// mit allen Benutzerdatenbanken; ein anderer Hauptschluessel oeffnet sie nicht.</summary>
[Collection("server")]
public class S3BackupTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public S3BackupTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Sichern_und_in_leeres_Verzeichnis_zurueckspielen()
    {
        var services = _fx.Factory.Services;
        var paths = services.GetRequiredService<DataPaths>();
        var vault = services.GetRequiredService<Vault>();
        var users = services.GetRequiredService<UserStore>();
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("anna");
        var marker = "Sicherungsprobe " + Guid.NewGuid();
        // Tresor von Ben mit einer Datei; geschlossen gesichert
        Tresor.ArgonMemory = 1024; Tresor.ScryptCost = 1024; Tresor.ArgonIterations = 1;
        var benId = _fx.Id("ben");
        Tresor.Delete(benId);
        var ben = _fx.As("ben");
        (await ben.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" })).EnsureSuccessStatusCode();
        (await ben.PostAsync("/api/tresor/files/upload?parent=0&name=geheim.txt", new StringContent("Tresorprobe 0815"))).EnsureSuccessStatusCode();
        (await ben.PostAsync("/api/tresor/close", null)).EnsureSuccessStatusCode();
        DmOffice.Dav.FileStore.Put(space.Db, "s3-sicherung-probe.txt", System.Text.Encoding.UTF8.GetBytes(marker), "text/plain");

        // Firebase-Schlüssel (Push an die App): kommt mit, die Sicherung ist als Ganzes verschlüsselt
        Directory.CreateDirectory(paths.Keys);
        var fcm = Path.Combine(paths.Keys, "fcm.json");
        var hadFcm = File.Exists(fcm);
        if (!hadFcm) File.WriteAllText(fcm, """{"type":"service_account"}""");
        var backup = new S3Backup(paths, vault, users, config, services, NullLogger<S3Backup>.Instance);
        var root = Directory.CreateTempSubdirectory("dms3b").FullName;
        var remote = new S3MirrorTests.FolderRemote(root);
        var target = Directory.CreateTempSubdirectory("dmrestore").FullName;
        try
        {
            await backup.RunAsync(remote, CancellationToken.None);
            Assert.Null(backup.Status.LastError);
            var stands = await S3Backup.ListAsync(remote, config.Current.S3Prefix, CancellationToken.None);
            var stand = Assert.Single(stands);

            // In S3 steht nichts im Klartext: weder Anmeldenamen noch die Benutzerliste
            var bytes = File.ReadAllBytes(Directory.EnumerateFiles(root, "*.dmb", SearchOption.AllDirectories).Single());
            Assert.DoesNotContain("users.json", System.Text.Encoding.ASCII.GetString(bytes));
            Assert.DoesNotContain("anna", System.Text.Encoding.ASCII.GetString(bytes));

            // Zurueck in ein leeres Datenverzeichnis, mit demselben Hauptschluessel
            var empty = new DataPaths(target);
            var manifest = await S3Backup.RestoreAsync(empty, vault, remote, stand, CancellationToken.None);
            // Erst beim Neustart an seinen Platz (vorher wartet er)
            Assert.False(File.Exists(Path.Combine(target, "users.json")));
            Assert.True(S3Backup.ApplyPending(empty) > 0);
            // Finanzen stellt danach nichts aus, bis die Nummern bestätigt sind
            Assert.True(File.Exists(Path.Combine(target, DmOffice.Finance.FinanceApi.RestoreStampFile)));
            Assert.Contains(manifest.Users, u => u.Login == "anna");
            Assert.True(File.Exists(Path.Combine(target, "users.json")));
            Assert.False(File.Exists(Path.Combine(target, "master.key")));
            Assert.True(File.Exists(Path.Combine(target, "keys", "fcm.json")));
            Assert.DoesNotContain("service_account", System.Text.Encoding.ASCII.GetString(bytes));
            Assert.True(File.Exists(Path.Combine(target, S3Mirror.RestoreFlag)));

            // Tresor: zurück an seinem Platz, im Stand nichts im Klartext, öffnen nur mit dem Tresor-Passwort
            Assert.DoesNotContain("Tresorprobe", System.Text.Encoding.ASCII.GetString(bytes));
            Assert.True(File.Exists(Path.Combine(target, "users", benId + ".tresor.json")));
            // Die Tresor-Dateien liegen als Cryptomator-Tresor in Bens Dateiablage und kommen mit ihr zurück
            var before = Tresor.Paths;
            var beforeDb = Tresor.UserDbOf;
            var benDb = new UserDb(Path.Combine(target, "users", benId + ".db"), users.DbKey(users.ById(benId)!), benId, empty.UserBlobs(benId));
            Tresor.Paths = empty;
            Tresor.UserDbOf = _ => benDb;
            try
            {
                Assert.NotNull(DmOffice.Dav.FileStore.Get(benDb, DmOffice.FileStoreVaultFiles.DefaultRoot + "/vault.cryptomator"));
                Assert.Null(await Tresor.KeyFromPasswordAsync(benId, "falsch-falsch"));
                var key = await Tresor.KeyFromPasswordAsync(benId, "tresor-pass-1");
                Assert.NotNull(key);
                // Geschlossen gesichert: Tresor-Datenbank und Dateien liegen im Cryptomator-Tresor der Ablage - deren Verzeichnis ist da,
                // den Inhalt hält wie bei allen Dateien der Storage Extender (die Blocksicherung nimmt nur das Verzeichnis der Ablage mit)
                Assert.Contains(DmOffice.Dav.FileStore.Descendants(benDb, DmOffice.FileStoreVaultFiles.DefaultRoot), f => !f.IsDir && f.Path.EndsWith(".c9r") && f.Size > 68);
            }
            finally { Tresor.Paths = before; Tresor.UserDbOf = beforeDb; }
            var db = Path.Combine(target, "users", space.User.Id + ".db");
            using (var conn = new SqliteConnection(UserDb.ConnectionStringOf(db, users.DbKey(users.ById(space.User.Id)!))))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM files WHERE path='s3-sicherung-probe.txt'";
                Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
            }

            // Ein anderer Hauptschluessel oeffnet die Sicherung nicht
            var other = Directory.CreateTempSubdirectory("dmrestore2").FullName;
            try
            {
                var foreign = new Vault(new DataPaths(other), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
                await Assert.ThrowsAsync<InvalidOperationException>(() => S3Backup.RestoreAsync(new DataPaths(other), foreign, remote, stand, CancellationToken.None));
                Assert.False(File.Exists(Path.Combine(other, "users.json")));
            }
            finally { Directory.Delete(other, recursive: true); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); Directory.Delete(target, recursive: true); if (!hadFcm) File.Delete(fcm); }
    }
}
