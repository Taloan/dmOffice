using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>Files-Backup: Regeln der Haltedauer, Lauf mit eigenem Ziel, Löschpause und das Werkzeug restore-tool.</summary>
[Collection("server")]
public class FilesBackupTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public FilesBackupTests(ChatFixture fx) => _fx = fx;

    private static ServerSettings Core(int count, int every) => new() { S3Backup = true, S3BackupKeepCount = count, S3BackupEveryHours = every };

    [Theory]
    [InlineData(10, 168, 77)]   // 10 Stände, wöchentlich: 11 Wochen
    [InlineData(5, BackupRules.Monthly, 186)]   // 5 Stände, monatlich: 6 Monate zu 31 Tagen
    [InlineData(1, 1, 1)]       // nie unter einem Tag
    [InlineData(24, 1, 2)]      // 25 Stunden, auf ganze Tage aufgerundet
    public void Mindesthaltedauer(int count, int every, int days) => Assert.Equal(days, BackupRules.MinHoldDays(Core(count, every)));

    [Fact]
    public void Fruehere_Tage_werden_zur_Anzahl()
    {
        Assert.Equal(30, BackupRules.KeepCount(new ServerSettings { S3BackupKeepDays = 30, S3BackupEveryHours = 24 }));
        Assert.Equal(10, BackupRules.KeepCount(new ServerSettings { S3BackupKeepDays = 70, S3BackupEveryHours = 168 }));
        // Mindestens drei, wie die frühere Regel „die drei neuesten bleiben“
        Assert.Equal(3, BackupRules.KeepCount(new ServerSettings { S3BackupKeepDays = 7, S3BackupEveryHours = 168 }));
        Assert.Equal(1, BackupRules.KeepCount(new ServerSettings { S3BackupKeepCount = 1, S3BackupKeepDays = 90 }));
    }

    [Fact]
    public void Frist_der_Spiegelung()
    {
        Assert.Equal(TimeSpan.FromDays(7), S3Mirror.KeepDeleted(new ServerSettings { S3Backup = false, FilesBackup = true, FilesBackupDays = 400 }));
        Assert.Equal(TimeSpan.FromDays(77), S3Mirror.KeepDeleted(Core(10, 168)));
        // Files-Backup am selben Ziel, erste Kopie noch nicht vollständig: seine Haltedauer, nie unter dem Mindestwert
        var s = Core(10, 168); s.FilesBackup = true; s.FilesBackupDays = 365;
        Assert.Equal(TimeSpan.FromDays(365), S3Mirror.KeepDeleted(s));
        s.FilesBackupDays = 3;
        Assert.Equal(TimeSpan.FromDays(77), S3Mirror.KeepDeleted(s));
        s.FilesBackupForever = true;
        Assert.Equal(TimeSpan.MaxValue, S3Mirror.KeepDeleted(s));
        // Kopie vollständig: Der Storage Extender hat seine eigene Frist
        s.FilesBackupCopied = true;
        Assert.Equal(TimeSpan.FromDays(77), S3Mirror.KeepDeleted(s));
        // Eigenes Ziel: Die Spiegelung bleibt, wie sie ist
        s.FilesBackupCopied = false; s.FilesBackupOwnTarget = true;
        Assert.Equal(TimeSpan.FromDays(77), S3Mirror.KeepDeleted(s));
    }

    [Fact]
    public void Werkzeug_liest_Staende_ab_2_0()
    {
        Assert.False(StandReader.ToolReads(null));
        Assert.False(StandReader.ToolReads("1.5"));
        Assert.True(StandReader.ToolReads("2.0"));
        Assert.True(StandReader.ToolReads("2.1"));
    }

    private static void SetCoreState(DataPaths paths, DateTime? lastRun, string? error) =>
        File.WriteAllText(Path.Combine(paths.Root, "core-backup.json"), JsonSerializer.Serialize(new { LastRun = lastRun, LastName = "x", LastBytes = 1L, LastError = error }));

    [Fact]
    public void Loeschpause_solange_das_Core_Backup_scheitert_oder_ueberfaellig_ist()
    {
        var paths = _fx.Factory.Services.GetRequiredService<DataPaths>();
        var file = Path.Combine(paths.Root, "core-backup.json");
        var saved = File.Exists(file) ? File.ReadAllText(file) : null;
        var now = DateTime.UtcNow;
        try
        {
            var s = Core(10, 168);
            Assert.False(S3Backup.HoldDeletes(new ServerSettings { S3Backup = false }, paths, now));
            if (File.Exists(file)) File.Delete(file);
            Assert.True(S3Backup.HoldDeletes(s, paths, now));   // noch kein Lauf
            SetCoreState(paths, now.AddDays(-1), null);
            Assert.False(S3Backup.HoldDeletes(s, paths, now));
            SetCoreState(paths, now.AddDays(-1), "Ziel nicht erreichbar");
            Assert.True(S3Backup.HoldDeletes(s, paths, now));
            SetCoreState(paths, now.AddDays(-11), null);        // überfällig: mehr als anderthalb Abstände
            Assert.True(S3Backup.HoldDeletes(s, paths, now));
        }
        finally { if (saved != null) File.WriteAllText(file, saved); else if (File.Exists(file)) File.Delete(file); }
    }

    /// <summary>Eigener Dienst mit Ordner als Ziel; stellt danach die Einstellungen wieder her.</summary>
    private async Task WithFiles(string root, Func<FilesBackup, S3MirrorTests.FolderRemote, ServerConfig, DataPaths, Task> run)
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var paths = services.GetRequiredService<DataPaths>();
        var spaces = services.GetRequiredService<Spaces>();
        var mirror = new S3Mirror(spaces, config, paths, NullLogger<S3Mirror>.Instance);
        var files = new FilesBackup(spaces, config, paths, mirror, NullLogger<FilesBackup>.Instance);
        var before = config.Current;
        var coreFile = Path.Combine(paths.Root, "core-backup.json");
        var coreSaved = File.Exists(coreFile) ? File.ReadAllText(coreFile) : null;
        try
        {
            config.Update(s => { s.S3Backup = true; s.S3BackupKeepCount = 1; s.S3BackupEveryHours = 24; s.FilesBackup = true; s.FilesBackupOwnTarget = true; s.FilesBackupTarget = "folder"; s.FilesBackupFolder = root; s.FilesBackupS3Prefix = "fb"; s.FilesBackupDays = 10; s.FilesBackupForever = false; });
            SetCoreState(paths, DateTime.UtcNow, null);
            await run(files, new S3MirrorTests.FolderRemote(root), config, paths);
        }
        finally
        {
            config.Update(s =>
            {
                s.S3Backup = before.S3Backup; s.S3BackupKeepCount = before.S3BackupKeepCount; s.S3BackupEveryHours = before.S3BackupEveryHours; s.FilesBackup = before.FilesBackup;
                s.FilesBackupOwnTarget = before.FilesBackupOwnTarget; s.FilesBackupTarget = before.FilesBackupTarget; s.FilesBackupFolder = before.FilesBackupFolder; s.FilesBackupS3Prefix = before.FilesBackupS3Prefix; s.FilesBackupDays = before.FilesBackupDays;
            });
            if (coreSaved != null) File.WriteAllText(coreFile, coreSaved); else if (File.Exists(coreFile)) File.Delete(coreFile);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Lauf_uebertraegt_nur_Fehlendes_und_loescht_nach_der_Haltedauer()
    {
        var space = _fx.Space("ben");
        var db = space.Db;
        // Das Aufräumen greift nur, wenn nicht mehr als die Hälfte fehlt: Bestand anlegen
        for (var i = 0; i < 4; i++) DmOffice.Dav.FileStore.Put(db, $"fb-bleibt-{i}.txt", System.Text.Encoding.UTF8.GetBytes("bleibt " + i + Guid.NewGuid()), "text/plain");
        DmOffice.Dav.FileStore.Put(db, "fb-weg.txt", System.Text.Encoding.UTF8.GetBytes("weg " + Guid.NewGuid()), "text/plain");
        var blob = db.Query("SELECT blob FROM files WHERE path='fb-weg.txt'", r => r.GetString(0)).Single();
        var root = Directory.CreateTempSubdirectory("dmfb").FullName;
        await WithFiles(root, async (files, remote, config, paths) =>
        {
            await files.PassAsync(remote, CancellationToken.None);
            Assert.Null(files.Status.LastError);
            Assert.NotNull(files.Status.CompleteSince);
            Assert.True(files.Status.Uploaded > 0);
            var key = "fb/" + space.User.Id + "/" + blob[..2] + "/" + blob;
            Assert.True(await remote.SizeAsync(key, CancellationToken.None) > 0);

            // Zweiter Lauf ohne Änderung: nichts übertragen
            await files.PassAsync(remote, CancellationToken.None);
            Assert.Equal(0, files.Status.Uploaded);

            // Endgültig gelöscht: erst nur vorgemerkt
            db.Exec("DELETE FROM files WHERE path='fb-weg.txt'");
            db.Blobs.Delete(blob);
            await files.PassAsync(remote, CancellationToken.None);
            Assert.True(await remote.SizeAsync(key, CancellationToken.None) > 0);

            // Frist abgelaufen, aber Core-Backup gescheitert: bleibt
            var gone = Path.Combine(db.Blobs.Dir, ".files-backup-gone.json");
            File.WriteAllText(gone, JsonSerializer.Serialize(new Dictionary<string, DateTime> { [key] = DateTime.UtcNow.AddDays(-30) }));
            SetCoreState(paths, DateTime.UtcNow, "kaputt");
            await files.PassAsync(remote, CancellationToken.None);
            Assert.True(await remote.SizeAsync(key, CancellationToken.None) > 0);

            // Core-Backup läuft wieder: jetzt weg
            SetCoreState(paths, DateTime.UtcNow, null);
            await files.PassAsync(remote, CancellationToken.None);
            Assert.Equal(-1, await remote.SizeAsync(key, CancellationToken.None));
        });
    }

    [Fact]
    public async Task Vollstaendig_haengt_an_Alter_und_erstem_Lauf()
    {
        var root = Directory.CreateTempSubdirectory("dmfbc").FullName;
        await WithFiles(root, async (files, remote, config, _) =>
        {
            var now = DateTime.UtcNow;
            Assert.False(files.Complete(now, now));                       // noch kein fertiger Lauf
            await files.PassAsync(remote, CancellationToken.None);
            Assert.True(files.Complete(DateTime.UtcNow, DateTime.UtcNow));
            Assert.False(files.Complete(now.AddDays(-1), now));           // vor dem ersten fertigen Lauf
            var later = DateTime.UtcNow;
            Assert.True(files.Complete(later, later.AddDays(9)));
            Assert.False(files.Complete(later, later.AddDays(11)));       // älter als die Haltedauer
            config.Update(s => s.FilesBackupForever = true);
            Assert.True(files.Complete(later, later.AddDays(5000)));
            config.Update(s => { s.FilesBackupForever = false; s.FilesBackup = false; });
            Assert.Null(files.Complete(later, later));
        });
    }

    [Fact]
    public async Task Werkzeug_holt_aktuelle_und_geloeschte_Dateien_und_aendert_am_Ziel_nichts()
    {
        var services = _fx.Factory.Services;
        var space = _fx.Space("anna");
        var db = space.Db;
        var text = "Werkzeugprobe " + Guid.NewGuid();
        DmOffice.Dav.FileStore.EnsureDir(db, "werkzeug");
        DmOffice.Dav.FileStore.Put(db, "werkzeug/probe.txt",System.Text.Encoding.UTF8.GetBytes(text), "text/plain");
        var root = Directory.CreateTempSubdirectory("dmfbt").FullName;
        var coreRoot = Directory.CreateTempSubdirectory("dmfbs").FullName;
        var outDir = Directory.CreateTempSubdirectory("dmfbo").FullName;
        var paths = services.GetRequiredService<DataPaths>();
        await WithFiles(root, async (files, remote, config, _) =>
        {
            await files.PassAsync(remote, CancellationToken.None);
            // Core-Stand am eigenen Ort; das Files-Backup lief eben von Hand
            config.Update(s => s.FilesBackup = false);
            var backup = new S3Backup(paths, services.GetRequiredService<Vault>(), services.GetRequiredService<UserStore>(), config, services, NullLogger<S3Backup>.Instance);
            var coreRemote = new S3MirrorTests.FolderRemote(coreRoot);
            await backup.RunAsync(coreRemote, CancellationToken.None);
            Assert.Null(backup.Status.LastError);
            var stand = Directory.EnumerateFiles(coreRoot, "*.dmb", SearchOption.AllDirectories).Single();

            RestoreTool.KeyFileForTests = Path.Combine(paths.Root, "master.key");
            try
            {
                var before = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).OrderBy(x => x.f).ToList();
                var o = new StringWriter(); var e = new StringWriter();
                Assert.Equal(0, await RestoreTool.RunAsync(["users", "--stand", stand], o, e, CancellationToken.None));
                Assert.Contains("anna", o.ToString());

                o = new StringWriter();
                Assert.Equal(0, await RestoreTool.RunAsync(["ls", "--stand", stand, "--user", "anna", "--path", "werkzeug"], o, e, CancellationToken.None));
                Assert.Contains("werkzeug/probe.txt", o.ToString());

                o = new StringWriter();
                Assert.Equal(0, await RestoreTool.RunAsync(["find", "--stand", stand, "--user", "anna", "probe"], o, e, CancellationToken.None));
                Assert.Contains("werkzeug/probe.txt", o.ToString());

                o = new StringWriter();
                var code = await RestoreTool.RunAsync(["get", "--stand", stand, "--user", "anna", "--out", outDir, "--files", root, "--prefix", "fb", "werkzeug/probe.txt"], o, e, CancellationToken.None);
                Assert.True(code == 0, e.ToString());
                Assert.Equal(text, File.ReadAllText(Path.Combine(outDir, "probe.txt")));

                // Nichts überschreiben
                var again = new StringWriter();
                Assert.Equal(3, await RestoreTool.RunAsync(["get", "--stand", stand, "--user", "anna", "--out", outDir, "--files", root, "--prefix", "fb", "werkzeug/probe.txt"], again, e, CancellationToken.None));
                Assert.Equal(text, File.ReadAllText(Path.Combine(outDir, "probe.txt")));

                // Inhalt fehlt am Ziel: wird genannt
                var empty = Directory.CreateTempSubdirectory("dmfbe").FullName;
                var err = new StringWriter();
                Assert.Equal(3, await RestoreTool.RunAsync(["get", "--stand", stand, "--user", "anna", "--out", Path.Combine(outDir, "b"), "--files", empty, "--prefix", "fb", "werkzeug/probe.txt"], new StringWriter(), err, CancellationToken.None));
                Assert.Contains("Inhalt fehlt am Ziel", err.ToString());
                Directory.Delete(empty, recursive: true);

                // Am Ziel unverändert
                var after = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).OrderBy(x => x.f).ToList();
                Assert.Equal(before, after);
            }
            finally
            {
                RestoreTool.KeyFileForTests = null;
                try { Directory.Delete(coreRoot, recursive: true); Directory.Delete(outDir, recursive: true); } catch (IOException) { }
            }
        });
    }

    /// <summary>Files-Backup auf dem Ziel des Storage Extender (hier ein Ordner); stellt danach die Einstellungen wieder her.</summary>
    private async Task WithSameTarget(string root, Func<FilesBackup, S3MirrorTests.FolderRemote, ServerConfig, DataPaths, Task> run)
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var paths = services.GetRequiredService<DataPaths>();
        var spaces = services.GetRequiredService<Spaces>();
        var mirror = new S3Mirror(spaces, config, paths, NullLogger<S3Mirror>.Instance);
        var files = new FilesBackup(spaces, config, paths, mirror, NullLogger<FilesBackup>.Instance);
        var before = config.Current;
        var coreFile = Path.Combine(paths.Root, "core-backup.json");
        var coreSaved = File.Exists(coreFile) ? File.ReadAllText(coreFile) : null;
        var wait = S3Backup.PassWait;
        try
        {
            S3Backup.PassWait = TimeSpan.FromSeconds(10);
            config.Update(s =>
            {
                s.S3Backup = true; s.S3BackupKeepCount = 1; s.S3BackupEveryHours = 24; s.FilesBackup = true; s.FilesBackupOwnTarget = false; s.FilesBackupCopied = false; s.FilesBackupDays = 60; s.FilesBackupForever = false;
                s.StorageTarget = "folder"; s.StorageFolder = root; s.S3Prefix = "px"; s.S3Mirror = true;
            });
            SetCoreState(paths, DateTime.UtcNow, null);
            await run(files, new S3MirrorTests.FolderRemote(root), config, paths);
        }
        finally
        {
            S3Backup.PassWait = wait;
            config.Update(s =>
            {
                s.S3Backup = before.S3Backup; s.S3BackupKeepCount = before.S3BackupKeepCount; s.S3BackupEveryHours = before.S3BackupEveryHours; s.FilesBackup = before.FilesBackup; s.FilesBackupOwnTarget = before.FilesBackupOwnTarget; s.FilesBackupCopied = before.FilesBackupCopied; s.FilesBackupDays = before.FilesBackupDays;
                s.StorageTarget = before.StorageTarget; s.StorageFolder = before.StorageFolder; s.S3Prefix = before.S3Prefix; s.S3Mirror = before.S3Mirror;
            });
            if (coreSaved != null) File.WriteAllText(coreFile, coreSaved); else if (File.Exists(coreFile)) File.Delete(coreFile);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Gleiches_Ziel_eigene_Kopie_stellt_56_Tage_alten_Stand_ohne_Storage_Extender_wieder_her()
    {
        var services = _fx.Factory.Services;
        var space = _fx.Space("anna");
        var db = space.Db;
        var marker = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 4; i++) DmOffice.Dav.FileStore.Put(db, $"sz-{marker}-bleibt-{i}.txt", System.Text.Encoding.UTF8.GetBytes("bleibt " + i + Guid.NewGuid()), "text/plain");
        var text = "Gelöscht vor 56 Tagen " + Guid.NewGuid();
        var name = $"sz-{marker}-weg.txt";
        DmOffice.Dav.FileStore.Put(db, name, System.Text.Encoding.UTF8.GetBytes(text), "text/plain");
        DmOffice.Dav.FileStore.Put(db, $"sz-{marker}-lokal-entfernt.txt", System.Text.Encoding.UTF8.GetBytes("nur im Extender " + Guid.NewGuid()), "text/plain");
        var blob = db.Query($"SELECT blob FROM files WHERE path='{name}'", r => r.GetString(0)).Single();
        var evicted = db.Query($"SELECT blob FROM files WHERE path='sz-{marker}-lokal-entfernt.txt'", r => r.GetString(0)).Single();
        var root = Directory.CreateTempSubdirectory("dmfbg").FullName;
        var coreRoot = Directory.CreateTempSubdirectory("dmfbh").FullName;
        var outDir = Directory.CreateTempSubdirectory("dmfbi").FullName;
        var user = space.User.Id;
        string Work(string id) => $"px/{user}/{id[..2]}/{id}";
        string Copy(string id) => $"px/files-backup/{user}/{id[..2]}/{id}";
        await WithSameTarget(root, async (files, remote, config, paths) =>
        {
            var ct = CancellationToken.None;
            // Im Arbeitsbereich: beide Inhalte und einer, den nur noch die Frist hält. Lokal fehlt einer.
            await remote.UploadAsync(db.Blobs.PathOf(blob), Work(blob), ct);
            await remote.UploadAsync(db.Blobs.PathOf(evicted), Work(evicted), ct);
            var heldId = Guid.NewGuid().ToString("N");
            await remote.UploadAsync(db.Blobs.PathOf(blob), Work(heldId), ct);
            var evictedFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            File.Copy(db.Blobs.PathOf(evicted), evictedFile);
            db.Blobs.Delete(evicted);

            await files.PassAsync(remote, ct);
            Assert.Null(files.Status.LastError);
            Assert.NotNull(files.Status.CompleteSince);
            Assert.True(config.Current.FilesBackupCopied);
            foreach (var id in new[] { blob, evicted, heldId }) Assert.True(await remote.SizeAsync(Copy(id), ct) > 0, id);
            Assert.True(await remote.SizeAsync(Work(blob), ct) > 0);   // Arbeitsbereich unverändert
            Assert.Equal(TimeSpan.FromDays(7), S3Mirror.KeepDeleted(config.Current));

            // Zweiter Lauf: nichts mehr zu übertragen
            await files.PassAsync(remote, ct);
            Assert.Equal(0, files.Status.Uploaded);

            // Core-Stand, dann Datei endgültig löschen und den Arbeitsbereich dieses Benutzers verlieren
            var backup = new S3Backup(paths, services.GetRequiredService<Vault>(), services.GetRequiredService<UserStore>(), config, services, NullLogger<S3Backup>.Instance);
            await backup.RunAsync(new S3MirrorTests.FolderRemote(coreRoot), ct);
            Assert.Null(backup.Status.LastError);
            var stand = Directory.EnumerateFiles(coreRoot, "*.dmb", SearchOption.AllDirectories).Single();
            db.Exec($"DELETE FROM files WHERE path='{name}'");
            db.Blobs.Delete(blob);
            SetCoreState(paths, DateTime.UtcNow, null);
            await files.PassAsync(remote, ct);
            Directory.Delete(Path.Combine(root, "px", user), recursive: true);

            // 56 Tage später, Haltedauer 60: Die Kopie hält den Inhalt noch
            var gone = Path.Combine(db.Blobs.Dir, ".files-backup-gone.json");
            File.WriteAllText(gone, JsonSerializer.Serialize(new Dictionary<string, DateTime> { [Copy(blob)] = DateTime.UtcNow.AddDays(-56) }));
            await files.PassAsync(remote, ct);
            Assert.True(await remote.SizeAsync(Copy(blob), ct) > 0);

            // Das Werkzeug holt die Datei aus dem alten Stand, der Arbeitsbereich ist leer
            RestoreTool.KeyFileForTests = Path.Combine(paths.Root, "master.key");
            try
            {
                var err = new StringWriter();
                var code = await RestoreTool.RunAsync(["get", "--stand", stand, "--user", "anna", "--out", outDir, name], new StringWriter(), err, ct);
                Assert.True(code == 0, err.ToString());
                Assert.Equal(text, File.ReadAllText(Path.Combine(outDir, name)));
            }
            finally { RestoreTool.KeyFileForTests = null; }

            // Wiederherstellen auf dem Server: fehlender Inhalt kommt aus der Kopie
            await files.RestoreAsync(remote, ct);
            Assert.True(db.Blobs.Exists(evicted));
            Assert.Equal(File.ReadAllBytes(evictedFile), File.ReadAllBytes(db.Blobs.PathOf(evicted)));
            File.Delete(evictedFile);

            // Haltedauer abgelaufen: aus der Kopie gelöscht
            File.WriteAllText(gone, JsonSerializer.Serialize(new Dictionary<string, DateTime> { [Copy(blob)] = DateTime.UtcNow.AddDays(-61) }));
            await files.PassAsync(remote, ct);
            Assert.Equal(-1, await remote.SizeAsync(Copy(blob), ct));
        });
        try { Directory.Delete(coreRoot, recursive: true); Directory.Delete(outDir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Speicher, der im selben Speicher kopieren kann: zählt, was direkt kopiert und was übertragen wurde.</summary>
    private sealed class CopyingRemote(IBlobRemote inner) : IBlobRemote
    {
        public int Copies, Uploads;
        public Task<HashSet<string>> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);
        public Task<List<(string Key, long Size)>> ListSizesAsync(string prefix, CancellationToken ct) => inner.ListSizesAsync(prefix, ct);
        public Task UploadAsync(string file, string key, CancellationToken ct) { Uploads++; return inner.UploadAsync(file, key, ct); }
        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);
        public Task DownloadAsync(string key, string file, CancellationToken ct) => inner.DownloadAsync(key, file, ct);
        public Task<long> SizeAsync(string key, CancellationToken ct) => inner.SizeAsync(key, ct);
        public Task<byte[]> ReadRangeAsync(string key, long offset, int count, CancellationToken ct) => inner.ReadRangeAsync(key, offset, count, ct);
        public async Task<bool> TryCopyAsync(string from, string to, CancellationToken ct)
        {
            if (await inner.SizeAsync(from, ct) < 0) return false;
            var temp = Path.GetTempFileName();
            try { await inner.DownloadAsync(from, temp, ct); await inner.UploadAsync(temp, to, ct); Copies++; return true; }
            finally { File.Delete(temp); }
        }
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task Gleiches_Ziel_kopiert_direkt_im_Speicher_wenn_der_Anbieter_das_kann()
    {
        var space = _fx.Space("ben");
        var db = space.Db;
        var path = $"sz-direkt-{Guid.NewGuid():N}.txt";
        DmOffice.Dav.FileStore.Put(db, path, System.Text.Encoding.UTF8.GetBytes("direkt " + Guid.NewGuid()), "text/plain");
        var blob = db.Query($"SELECT blob FROM files WHERE path='{path}'", r => r.GetString(0)).Single();
        var root = Directory.CreateTempSubdirectory("dmfbd").FullName;
        await WithSameTarget(root, async (files, folder, config, _) =>
        {
            var ct = CancellationToken.None;
            await folder.UploadAsync(db.Blobs.PathOf(blob), $"px/{space.User.Id}/{blob[..2]}/{blob}", ct);
            var remote = new CopyingRemote(folder);
            await files.PassAsync(remote, ct);
            Assert.Null(files.Status.LastError);
            Assert.True(remote.Copies >= 1);
            Assert.True(await folder.SizeAsync($"px/files-backup/{space.User.Id}/{blob[..2]}/{blob}", ct) > 0);
        });
    }

    /// <summary>Ziel, das nicht erreichbar ist.</summary>
    private sealed class DownRemote : IBlobRemote
    {
        private static Exception Down() => new IOException("nicht erreichbar");
        public Task<HashSet<string>> ListAsync(string prefix, CancellationToken ct) => throw Down();
        public Task<List<(string Key, long Size)>> ListSizesAsync(string prefix, CancellationToken ct) => throw Down();
        public Task UploadAsync(string file, string key, CancellationToken ct) => throw Down();
        public Task DeleteAsync(string key, CancellationToken ct) => throw Down();
        public Task DownloadAsync(string key, string file, CancellationToken ct) => throw Down();
        public Task<long> SizeAsync(string key, CancellationToken ct) => throw Down();
        public Task<byte[]> ReadRangeAsync(string key, long offset, int count, CancellationToken ct) => throw Down();
        public void Dispose() { }
    }

    [Fact]
    public async Task Geloeschtes_Konto_verschwindet_sofort_am_Ziel_und_andere_Konten_bleiben()
    {
        var root = Directory.CreateTempSubdirectory("dmfbu").FullName;
        var target = Directory.CreateTempSubdirectory("dmfbv").FullName;
        try
        {
            var paths = new DataPaths(root);
            var remote = new S3MirrorTests.FolderRemote(target);
            var ct = CancellationToken.None;
            string Id() => Guid.NewGuid().ToString("N");
            async Task<string> Put(string key)
            {
                var f = Path.GetTempFileName();
                File.WriteAllText(f, "x");
                await remote.UploadAsync(f, key, ct);
                File.Delete(f);
                return key;
            }
            var erased = Id();
            var other = Id();
            // Eine Kennung, die mit der gelöschten beginnt: darf nicht mitgehen
            var similar = erased[..31] + (erased[31] == 'a' ? 'b' : 'a');
            var inWork = await Put($"px/{erased}/ab/{Id()}");
            var inCopy = await Put($"px/files-backup/{erased}/ab/{Id()}");
            var keep = new[] { await Put($"px/{other}/ab/{Id()}"), await Put($"px/files-backup/{other}/ab/{Id()}"), await Put($"px/{similar}/ab/{Id()}"), await Put("px/backups/stand.dmb") };
            var folders = new List<ErasedUsers.Target> { new(remote, "px"), new(remote, "px/files-backup") };
            var marks = new List<ErasedUsers.Target> { new(remote, "px") };

            // Ziel nicht erreichbar: bleibt vorgemerkt
            ErasedUsers.Note(paths, erased);
            var down = new List<ErasedUsers.Target> { new(new DownRemote(), "px") };
            Assert.NotNull(await ErasedUsers.PassAsync(paths, down, down, ct));
            Assert.Contains(erased, ErasedUsers.Open(paths));

            // Sofort, ohne Frist: nur dieser Ordner, auch in der Kopie
            Assert.Null(await ErasedUsers.PassAsync(paths, folders, marks, ct));
            Assert.Equal(-1, await remote.SizeAsync(inWork, ct));
            Assert.Equal(-1, await remote.SizeAsync(inCopy, ct));
            foreach (var key in keep) Assert.True(await remote.SizeAsync(key, ct) > 0, key);
            Assert.True(await remote.SizeAsync("px/" + ErasedUsers.MarkFolder + erased, ct) > 0);

            // Erledigt erst, wenn ein Durchgang nichts mehr findet: Ein laufender Abgleich könnte nachgeschrieben haben
            Assert.Contains(erased, ErasedUsers.Open(paths));
            var late = await Put($"px/{erased}/cd/{Id()}");
            await ErasedUsers.PassAsync(paths, folders, marks, ct);
            Assert.Equal(-1, await remote.SizeAsync(late, ct));
            await ErasedUsers.PassAsync(paths, folders, marks, ct);
            Assert.Empty(ErasedUsers.Open(paths));
            Assert.False(File.Exists(Path.Combine(root, "erased-users.json")));

            // Eingespieltes älteres Core-Backup: nur das Konto mit Vermerk geht wieder, einmal
            var removed = new List<string>();
            await ErasedUsers.AfterRestoreAsync(paths, marks, new[] { erased, other }, removed.Add, ct);
            Assert.Empty(removed);   // keine Marke: nichts zu tun
            var flag = Path.Combine(root, ErasedUsers.CheckFlag);
            File.WriteAllText(flag, "x");
            await Assert.ThrowsAsync<IOException>(() => ErasedUsers.AfterRestoreAsync(paths, down, new[] { erased, other }, removed.Add, ct));
            Assert.True(File.Exists(flag));   // nicht erreichbar: später noch einmal
            await ErasedUsers.AfterRestoreAsync(paths, marks, new[] { erased, other }, removed.Add, ct);
            Assert.Equal(new[] { erased }, removed);
            Assert.False(File.Exists(flag));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task Eigenes_Konto_loeschen_verlangt_Passwort_und_laesst_andere_unberuehrt()
    {
        var services = _fx.Factory.Services;
        var users = services.GetRequiredService<UserStore>();
        var spaces = services.GetRequiredService<Spaces>();
        var paths = services.GetRequiredService<DataPaths>();
        string Make(string login)
        {
            var u = users.Create(login, login, "geheim-123456", isAdmin: false);
            _fx.Users[login] = (u.Id, AppPasswords.Create(spaces.Get(u.Id).Db, "Test"));
            return u.Id;
        }
        var goneLogin = "szg" + Guid.NewGuid().ToString("N")[..6];
        var stayLogin = "szb" + Guid.NewGuid().ToString("N")[..6];
        var goneId = Make(goneLogin);
        var stayId = Make(stayLogin);
        var gone = _fx.As(goneLogin);
        var stay = _fx.As(stayLogin);
        var text = "Bleibt " + Guid.NewGuid();
        DmOffice.Dav.FileStore.Put(spaces.Get(stayId).Db, "eigen.txt", System.Text.Encoding.UTF8.GetBytes(text), "text/plain");
        DmOffice.Dav.FileStore.EnsureDir(spaces.Get(goneId).Db, "Gemeinsam");
        (await gone.PutAsJsonAsync("/api/files/shares", new { path = "Gemeinsam", members = new[] { stayId }, readOnly = Array.Empty<string>() })).EnsureSuccessStatusCode();
        // Auch der Bleibende teilt etwas mit dem, der geht
        DmOffice.Dav.FileStore.EnsureDir(spaces.Get(stayId).Db, "Seins");
        (await stay.PutAsJsonAsync("/api/files/shares", new { path = "Seins", members = new[] { goneId }, readOnly = Array.Empty<string>() })).EnsureSuccessStatusCode();

        // Vorab: was das für andere bedeutet
        var impact = await (await gone.GetAsync("/api/auth/delete-account/impact")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(impact.GetProperty("lastAdmin").GetBoolean());
        Assert.Equal(1, impact.GetProperty("shares").GetInt32());
        Assert.Contains(stayLogin, impact.GetProperty("members").EnumerateArray().Select(x => x.GetString()));

        // Falsches Passwort: nichts passiert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await gone.PostAsJsonAsync("/api/auth/delete-account", new { password = "falsch" })).StatusCode);
        Assert.NotNull(users.ById(goneId));

        // Der letzte Admin kann nicht
        var last = await _fx.As("anna").PostAsJsonAsync("/api/auth/delete-account", new { password = "geheim-123456" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, last.StatusCode);
        Assert.Contains("einzige Admin", await last.Content.ReadAsStringAsync());

        // Richtiges Passwort: sofort weg
        var done = await gone.PostAsJsonAsync("/api/auth/delete-account", new { password = "geheim-123456" });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        Assert.Null(users.ById(goneId));
        Assert.False(File.Exists(paths.UserDb(goneId)));
        Assert.False(Directory.Exists(paths.UserBlobs(goneId)));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await gone.GetAsync("/api/files/")).StatusCode);

        // Der andere arbeitet weiter: eigene Datei da, geteilter Ordner verschwunden, ohne Fehler
        Assert.Equal(text, await stay.GetStringAsync("/api/files/download?path=eigen.txt"));
        var folder = await stay.GetAsync("/api/files/?path=" + Uri.EscapeDataString("Mit mir geteilt"));
        Assert.True(folder.IsSuccessStatusCode || folder.StatusCode == System.Net.HttpStatusCode.NotFound, folder.StatusCode.ToString());
        Assert.DoesNotContain("Gemeinsam", await folder.Content.ReadAsStringAsync());
        Assert.True((await stay.GetAsync("/api/files/?path=Seins")).IsSuccessStatusCode);
    }

    [Fact]
    public void Einspielen_entfernt_Datenbanken_der_Ersteinrichtung_ohne_Stand()
    {
        var root = Directory.CreateTempSubdirectory("dmfbp").FullName;
        try
        {
            var paths = new DataPaths(root);
            var pending = Directory.CreateDirectory(Path.Combine(root, S3Backup.PendingFolder)).FullName;
            File.WriteAllText(Path.Combine(pending, "backup.json"), "{}");
            File.WriteAllText(Path.Combine(pending, "chat.db"), "aus dem Stand");
            File.WriteAllText(Path.Combine(root, "chat.db"), "leer");
            File.WriteAllText(Path.Combine(root, "junkprints.db"), "leer");
            File.WriteAllText(Path.Combine(root, "junkprints.db-wal"), "leer");
            File.WriteAllText(Path.Combine(root, "security.db"), "leer");
            Assert.True(S3Backup.ApplyPending(paths) > 0);
            Assert.Equal("aus dem Stand", File.ReadAllText(Path.Combine(root, "chat.db")));
            Assert.False(File.Exists(Path.Combine(root, "junkprints.db")));
            Assert.False(File.Exists(Path.Combine(root, "junkprints.db-wal")));
            Assert.False(File.Exists(Path.Combine(root, "security.db")));
            Assert.True(File.Exists(Path.Combine(root, FilesBackup.RestoreFlag)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Core_Backup_behaelt_so_viele_Staende_wie_eingestellt()
    {
        var services = _fx.Factory.Services;
        var paths = services.GetRequiredService<DataPaths>();
        var config = services.GetRequiredService<ServerConfig>();
        var root = Directory.CreateTempSubdirectory("dmfbk").FullName;
        var before = config.Current;
        try
        {
            config.Update(s => { s.S3BackupKeepCount = 1; s.S3Mirror = false; });
            var backup = new S3Backup(paths, services.GetRequiredService<Vault>(), services.GetRequiredService<UserStore>(), config, services, NullLogger<S3Backup>.Instance);
            var remote = new S3MirrorTests.FolderRemote(root);
            await backup.RunAsync(remote, CancellationToken.None);
            await Task.Delay(1100);   // Stände tragen die Sekunde im Namen
            await backup.RunAsync(remote, CancellationToken.None);
            Assert.Null(backup.Status.LastError);
            Assert.Single(await S3Backup.ListAsync(remote, config.Current.S3Prefix, CancellationToken.None));
        }
        finally { config.Update(s => { s.S3BackupKeepCount = before.S3BackupKeepCount; s.S3Mirror = before.S3Mirror; }); Directory.Delete(root, recursive: true); }
    }
}
