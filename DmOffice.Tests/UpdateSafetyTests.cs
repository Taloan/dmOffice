using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>
/// Update-Sicherheit: Datenaenderungen (Schemaschritte, Auslagerung der Dateiinhalte) pruefen vorher den Platz, sichern vorher die
/// Datenbank, fragen bei grossen Umbauten die Verwaltung und lassen sich jederzeit abbrechen und wiederaufnehmen. Die Datenbanken
/// in Fixtures/ hat der echte Code von v1.0.0 und v1.1.0 angelegt (Schemaversion 9 und 10), mit Dateien, Mail, Notiz, Kalender.
/// </summary>
public class UpdateSafetyTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
    private const long MB = 1024 * 1024;

    private sealed class Env
    {
        public DataPaths Paths = null!;
        public UserStore Users = null!;
        public ServerConfig Config = null!;
        public UpdateCenter Update = null!;
        public Spaces Spaces = null!;
        public UserRecord User = null!;
        /// <summary>Freier Platz, den die Pruefung sieht - veraenderlich.</summary>
        public long Free = 100 * 1024 * MB;
        public string Db => Paths.UserDb(User.Id);

        public static Env Make(string? fixture = "legacy-v1.0.0.dmdb", long? free = null)
        {
            var dir = Directory.CreateTempSubdirectory("dmupdate").FullName;
            var paths = new DataPaths(dir);
            var users = new UserStore(paths, new Vault(paths, "test-master-key-" + Guid.NewGuid().ToString("N")));
            var user = users.Create("alice", "Alice", "password123", true);
            // Die Fixtures sind mit diesem Schluessel angelegt
            users.Update(user.Id, u => u.WrappedKey = users.Wrap(Key));
            user = users.ById(user.Id)!;
            if (fixture != null) File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), paths.UserDb(user.Id));
            var config = new ServerConfig(paths);
            var env = new Env { Paths = paths, Users = users, Config = config, User = user };
            if (free != null) env.Free = free.Value;
            env.Update = new UpdateCenter(paths, users, config)
            {
                FreeBytes = _ => env.Free,
                Reserve = 1 * MB,
                AppVersion = "9.9.9",
            };
            env.Spaces = new Spaces(users, paths, env.Update);
            return env;
        }

        public UserDb.Inspection Inspect() => UserDb.Inspect(Db, Key);
        public bool BackupExists => Directory.Exists(PreUpdateBackup.Root(Paths)) && Directory.EnumerateDirectories(PreUpdateBackup.Root(Paths)).Any();
    }

    /// <summary>Datei lesen, auch wenn SQLite sie noch offen hat (Windows).</summary>
    private static byte[] ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var m = new MemoryStream();
        f.CopyTo(m);
        return m.ToArray();
    }

    private static string Text(UserDb db, string path) => Encoding.UTF8.GetString(FileStore.Data(db, path)!);

    // ---------------- Schrittfolge von v1.0 / v1.1 bis heute ----------------

    [Theory]
    [InlineData("legacy-v1.0.0.dmdb", 9)]
    [InlineData("legacy-v1.1.0.dmdb", 10)]
    public void Von_den_alten_Versionen_bis_heute_ohne_Datenverlust(string fixture, int fromVersion)
    {
        var env = Env.Make(fixture);
        Assert.Equal(fromVersion, env.Inspect().Version);
        var big = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ReadShared(env.Db)));   // nur zum Vergleich unten

        var plan = env.Update.Plan(env.Users.ById(env.User.Id)!);
        Assert.True(plan.Pending);
        Assert.Equal("ready", plan.State);
        Assert.Equal(fromVersion, plan.From);
        Assert.Equal(UserDb.LatestVersion, plan.To);
        Assert.Equal(UserDb.LatestVersion - fromVersion, plan.Steps.Count);
        Assert.Contains(plan.Steps, s => s.Version == 11 || fromVersion >= 11);

        var space = env.Spaces.Get(env.User.Id);
        var db = space.Db;
        Assert.Equal(UserDb.LatestVersion, env.Inspect().Version);
        Assert.False(env.Update.Plan(env.Users.ById(env.User.Id)!).Pending);

        // Alles da: Dateien (Inhalt noch in der Datenbank, wird ausgelagert), Mail, Notiz, Kalender, App-Passwort
        Assert.Equal("Hallo aus Version 1", Text(db, "Dokumente/klein.txt"));
        Assert.Equal(150_000, FileStore.Data(db, "Dokumente/gross.bin")!.Length);
        Assert.Equal(70_000, FileStore.Data(db, "foto.jpg")!.Length);
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM mail"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM notes"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM collections"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM app_passwords"));
        Assert.Equal("{\"theme\":\"dark\"}", db.Get("settings"));
        // Dateinummern aus Schritt 11 sind vergeben
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM files WHERE fileid IS NULL"));
        var before = FileStore.Data(db, "Dokumente/gross.bin")!;

        // Auslagerung der Inhalte in Blobs, danach Pruefsummen - alles lesbar, nichts verloren
        var pending = FileStore.PendingBlobs(db);
        Assert.True(pending >= 3);
        var job = new BlobMigration(env.Spaces, env.Update, NullLogger<BlobMigration>.Instance);
        Assert.False(job.Run(space, default));
        Assert.Equal(0, FileStore.PendingBlobs(db));
        Assert.Equal(before, FileStore.Data(db, "Dokumente/gross.bin"));
        Assert.Equal("Hallo aus Version 1", Text(db, "Dokumente/klein.txt"));
        FileHashes.Compute(db);
        Assert.Equal(0L, FileHashes.Pending(db));
        Assert.NotNull(FileHashes.Of(db, "Dokumente/klein.txt"));
        Assert.NotEqual("", big);
    }

    // ---------------- Vorabpruefung des Platzes ----------------

    [Fact]
    public void Ohne_genug_Platz_wird_nicht_migriert_und_nichts_veraendert()
    {
        var env = Env.Make(free: 1 * MB);   // weniger als die 32 MB Mindestbedarf plus Reserve
        var bytesBefore = ReadShared(env.Db);

        var ex = Assert.Throws<UpdatePendingException>(() => env.Spaces.Get(env.User.Id));
        Assert.Equal("space", ex.Reason);
        Assert.Contains("es fehlen etwa", ex.Message);
        // Datenbank unangetastet, keine Sicherung, nichts halb fertig
        Assert.Equal(9, env.Inspect().Version);
        Assert.Equal(bytesBefore, ReadShared(env.Db));
        Assert.False(env.BackupExists);

        var plan = env.Update.Plan(env.Users.ById(env.User.Id)!);
        Assert.Equal("space", plan.State);
        Assert.True(plan.MissingBytes >= 32 * MB, $"fehlt {plan.MissingBytes}");
        Assert.Equal(1 * MB, plan.FreeBytes);

        // Die Anwendung laeuft weiter: andere Dienste ueberspringen den Benutzer, die Verwaltung sieht die Meldung
        Assert.Empty(env.Spaces.All());
        Assert.NotNull(env.Update.Describe());

        // Platz geschaffen -> beim naechsten Versuch geht es los
        env.Free = 1000 * MB;
        var space = env.Spaces.Get(env.User.Id);
        Assert.Equal(UserDb.LatestVersion, env.Inspect().Version);
        Assert.Equal("Hallo aus Version 1", Text(space.Db, "Dokumente/klein.txt"));
    }

    [Fact]
    public void Platzbedarf_waechst_mit_dem_Dateiinhalt_in_der_Datenbank()
    {
        var env = Env.Make();
        var plan = env.Update.Plan(env.Users.ById(env.User.Id)!);
        // v1.0: Schritt 11 schreibt jede Datei samt Inhalt neu (rund 220 KB) - der Mindestbedarf von 32 MB ueberwiegt hier,
        // der Inhalt zaehlt aber mit und die Auslagerung kommt hinzu
        Assert.True(plan.MigrationBytes >= 32 * MB);
        Assert.True(plan.ContentBytes >= 220_000);
        Assert.Contains(plan.Steps, s => s is { Version: 11, Heavy: true });
        Assert.True(plan.BackupBytes >= 400_000);
    }

    // ---------------- Sicherung vor dem Umbau ----------------

    [Fact]
    public void Vor_dem_Umbau_entsteht_eine_lesbare_Sicherung_samt_Dateien_des_Datenordners()
    {
        var env = Env.Make();
        env.Config.Update(s => s.DefaultFileQuotaMb = 5);                       // legt server.json an
        File.WriteAllText(Path.Combine(env.Paths.Root, "shares.json"), "[]");  // Freigaben
        Directory.CreateDirectory(env.Paths.Keys);
        File.WriteAllText(Path.Combine(env.Paths.Keys, "key-1.xml"), "<key/>");

        env.Spaces.Get(env.User.Id);

        var info = Assert.Single(PreUpdateBackup.List(env.Paths));
        Assert.Equal("9.9.9", info.AppVersion);
        Assert.StartsWith(DateTime.UtcNow.ToString("yyyy-MM-dd") + "-9.9.9", info.Name);
        var entry = Assert.Single(info.Users);
        Assert.Equal(env.User.Id, entry.Id);
        Assert.Equal(9, entry.Schema);
        Assert.True(info.Bytes > 400_000);
        foreach (var f in new[] { "users.json", "server.json", "shares.json", "manifest.json", Path.Combine("keys", "key-1.xml"), Path.Combine("users", env.User.Id + ".db") })
            Assert.True(File.Exists(Path.Combine(info.Path, f)), f);
        Assert.Empty(Directory.EnumerateFiles(info.Path, "*.part", SearchOption.AllDirectories));

        // Die Kopie ist die Datenbank von vorher: verschluesselt, mit demselben Schluessel lesbar, noch Version 9, alle Inhalte
        var copy = Path.Combine(info.Path, "users", env.User.Id + ".db");
        Assert.Equal(9, UserDb.Inspect(copy, Key).Version);
        using (var conn = new SqliteConnection(UserDb.ConnectionStringOf(copy, Key)))
        {
            conn.Open();
            Assert.Equal(5L, UserDb.Query(conn, "SELECT COUNT(*) FROM files", r => r.GetInt64(0)).Single());
            Assert.Equal(1L, UserDb.Query(conn, "SELECT COUNT(*) FROM notes", r => r.GetInt64(0)).Single());
            Assert.Equal("ok", UserDb.Query(conn, "PRAGMA integrity_check", r => r.GetString(0)).Single());
        }
        Assert.ThrowsAny<SqliteException>(() => UserDb.Inspect(copy, new byte[32]));
        Assert.DoesNotContain("Hallo aus Version 1", Encoding.Latin1.GetString(ReadShared(copy)));

        // Und die Kopie laesst sich zurueckspielen: ein Neustart baut sie wieder um
        Assert.Equal(UserDb.LatestVersion, env.Inspect().Version);
        var restored = Path.Combine(Directory.CreateTempSubdirectory("dmrestore").FullName, "u.db");
        File.Copy(copy, restored);
        var db = new UserDb(restored, Key, "restored-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("Hallo aus Version 1", Text(db, "Dokumente/klein.txt"));

        // Zweiter Start desselben Tages: keine zweite Sicherung, wenn nichts mehr umzubauen ist
        Assert.Single(PreUpdateBackup.List(env.Paths));
    }

    [Fact]
    public void Wiederholter_Versuch_legt_nicht_noch_eine_Sicherung_an()
    {
        var env = Env.Make();
        UserDb.StepHooks[env.User.Id] = v => { if (v == 13) throw new IOException("kaputt"); };
        try { Assert.Throws<UpdatePendingException>(() => env.Spaces.Get(env.User.Id)); }
        finally { UserDb.StepHooks.TryRemove(env.User.Id, out _); }
        var first = Assert.Single(PreUpdateBackup.List(env.Paths));
        env.Update.RetryAfter = TimeSpan.Zero;
        env.Spaces.Get(env.User.Id);
        var second = Assert.Single(PreUpdateBackup.List(env.Paths));
        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.Users.Single().Created, second.Users.Single().Created);
    }

    [Fact]
    public void Reicht_der_Platz_nur_fuer_den_Umbau_ist_die_Sicherung_Sache_der_Verwaltung()
    {
        // 32 MB Mindestbedarf + Reserve + ein Hauch - die Sicherung (rund 0,5 MB) passt nicht mehr dazu
        var env = Env.Make(free: 33 * MB + 400_000);

        var ex = Assert.Throws<UpdatePendingException>(() => env.Spaces.Get(env.User.Id));
        Assert.Equal("backup-space", ex.Reason);
        Assert.Equal(9, env.Inspect().Version);
        Assert.False(env.BackupExists);
        var plan = env.Update.Plan(env.Users.ById(env.User.Id)!);
        Assert.Equal(0, plan.MissingBytes);
        Assert.True(plan.BackupMissingBytes > 0);
        Assert.True(plan.WillBackup);

        // Ausdruecklich ohne Sicherung fortfahren
        env.Update.Start(withoutBackup: true);
        Assert.True(env.Update.WithoutBackupApproved);
        env.Spaces.Get(env.User.Id);
        Assert.Equal(UserDb.LatestVersion, env.Inspect().Version);
        Assert.False(env.BackupExists);
    }

    [Fact]
    public void Auch_mit_Bestaetigung_ohne_Sicherung_braucht_der_Umbau_selbst_Platz()
    {
        var env = Env.Make(free: 2 * MB);
        env.Update.Start(withoutBackup: true);
        var ex = Assert.Throws<UpdatePendingException>(() => env.Spaces.Get(env.User.Id));
        Assert.Equal("space", ex.Reason);
        Assert.Equal(9, env.Inspect().Version);
    }

    [Fact]
    public void Grosse_Umbauten_starten_erst_nach_Jetzt_starten()
    {
        var env = Env.Make();
        env.Update.BigBytes = 1 * MB;   // alles ab 1 MB gilt als gross

        var ex = Assert.Throws<UpdatePendingException>(() => env.Spaces.Get(env.User.Id));
        Assert.Equal("consent", ex.Reason);
        Assert.Equal(9, env.Inspect().Version);
        Assert.False(env.BackupExists);
        Assert.Equal("consent", env.Update.Plan(env.Users.ById(env.User.Id)!).State);

        env.Update.Start(withoutBackup: false);
        env.Spaces.Get(env.User.Id);
        Assert.Equal(UserDb.LatestVersion, env.Inspect().Version);
        Assert.True(env.BackupExists);

        // Die Freigabe galt nur fuer diese Aenderung
        env.Update.ResetApprovals(migrationDone: true, blobsDone: false);
        Assert.False(env.Update.MigrationApproved);
        Assert.False(env.Update.WithoutBackupApproved);
    }

    [Fact]
    public void Freigaben_ueberstehen_einen_Neustart()
    {
        var env = Env.Make();
        env.Update.Start(withoutBackup: true);
        env.Update.SetPaused(true);
        var again = new UpdateCenter(env.Paths, env.Users, env.Config);
        Assert.True(again.MigrationApproved);
        Assert.True(again.BlobsApproved);
        Assert.True(again.WithoutBackupApproved);
        Assert.True(again.Paused);
    }

    // ---------------- Abbruch und Wiederaufnahme ----------------

    [Fact]
    public void Abbruch_mitten_im_Umbau_rollt_den_Schritt_zurueck_und_der_naechste_Start_macht_weiter()
    {
        var env = Env.Make();
        var id = env.User.Id;
        UserDb.StepHooks[id] = v => { if (v == 13) throw new IOException("Platte voll"); };
        try
        {
            var ex = Assert.Throws<UpdatePendingException>(() => env.Spaces.Get(id));
            Assert.Equal("error", ex.Reason);
            Assert.Contains("unverändert", ex.Message);
        }
        finally { UserDb.StepHooks.TryRemove(id, out _); }

        // Schritte 11 und 12 sind festgeschrieben, 13 ganz zurueckgerollt (auch user_version)
        Assert.Equal(12, env.Inspect().Version);
        Assert.Equal("ok", Integrity(env));
        Assert.NotNull(Activity.Get(id, Activity.Migration)?.Error);

        // Gescheitert: eine Weile nicht von selbst erneut, der Benutzer sieht den Fehler
        var plan = env.Update.Plan(env.Users.ById(id)!);
        Assert.Equal("error", plan.State);
        Assert.Equal(12, plan.From);
        Assert.Throws<UpdatePendingException>(() => env.Spaces.Get(id));

        // "Jetzt starten"/"Erneut pruefen" versucht es sofort noch einmal
        env.Update.Start(withoutBackup: false, approve: false);
        var space = env.Spaces.Get(id);
        Assert.Equal(UserDb.LatestVersion, env.Inspect().Version);
        Assert.Equal("Hallo aus Version 1", Text(space.Db, "Dokumente/klein.txt"));
        Assert.Equal(150_000, FileStore.Data(space.Db, "Dokumente/gross.bin")!.Length);
        Assert.Equal("ok", Integrity(env));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(16)]
    [InlineData(18)]
    public void Abbruch_in_jedem_Schritt_laesst_die_Datenbank_heil(int failAt)
    {
        var env = Env.Make("legacy-v1.0.0.dmdb");
        var id = env.User.Id;
        UserDb.StepHooks[id] = v => { if (v == failAt) throw new IOException("Strom weg"); };
        try { Assert.Throws<UpdatePendingException>(() => env.Spaces.Get(id)); }
        finally { UserDb.StepHooks.TryRemove(id, out _); }
        Assert.Equal(failAt - 1, env.Inspect().Version);
        Assert.Equal("ok", Integrity(env));
        // Neu starten (anderer Prozess: neue Objekte, nur die Dateien bleiben)
        var restarted = new Spaces(env.Users, env.Paths, new UpdateCenter(env.Paths, env.Users, env.Config) { FreeBytes = _ => env.Free, Reserve = MB });
        var space = restarted.Get(id);
        Assert.Equal(UserDb.LatestVersion, env.Inspect().Version);
        Assert.Equal("Hallo aus Version 1", Text(space.Db, "Dokumente/klein.txt"));
    }

    private static string Integrity(Env env)
    {
        using var conn = new SqliteConnection(UserDb.ConnectionStringOf(env.Db, Key));
        conn.Open();
        return UserDb.Query(conn, "PRAGMA integrity_check", r => r.GetString(0)).Single();
    }

    [Fact]
    public void Abbruch_beim_Auslagern_macht_nach_dem_Neustart_dort_weiter()
    {
        var env = Env.Make("legacy-v1.1.0.dmdb");
        var space = env.Spaces.Get(env.User.Id);
        var expected = FileStore.Data(space.Db, "foto.jpg")!;
        var total = FileStore.PendingBlobs(space.Db);

        // Eine Zeile ausgelagert, dann "Absturz"
        Assert.Equal(1, FileStore.MigrateBlobs(space.Db, default, max: 1));
        Assert.Equal(total - 1, FileStore.PendingBlobs(space.Db));
        var blobsAfterOne = space.Db.Blobs.List().Count();

        // Neustart: neue Objekte auf denselben Dateien
        var db = new UserDb(env.Db, Key, "reopen-" + Guid.NewGuid().ToString("N"), env.Paths.UserBlobs(env.User.Id));
        Assert.Equal(total - 1, FileStore.PendingBlobs(db));
        var restarted = new Spaces(env.Users, env.Paths, env.Update);
        var job = new BlobMigration(restarted, env.Update, NullLogger<BlobMigration>.Instance);
        Assert.False(job.Run(restarted.Get(env.User.Id), default));
        Assert.Equal(0, FileStore.PendingBlobs(db));
        Assert.Equal(expected, FileStore.Data(db, "foto.jpg"));
        Assert.Equal("Hallo aus Version 1", Text(db, "Dokumente/klein.txt"));
        // Wiederholen aendert nichts mehr
        Assert.Equal(0, FileStore.MigrateBlobs(db));
        Assert.True(db.Blobs.List().Count() >= blobsAfterOne);
    }

    // ---------------- Auslagern: Platz, Freigabe, Pause ----------------

    [Fact]
    public void Auslagern_wartet_bei_zu_wenig_Platz_am_Blob_Ort_und_nennt_die_Zahl()
    {
        var env = Env.Make("legacy-v1.1.0.dmdb");
        var space = env.Spaces.Get(env.User.Id);
        var pending = FileStore.PendingBlobs(space.Db);
        var bytes = FileStore.PendingBlobBytes(space.Db);
        Assert.True(bytes >= 220_000);

        // Daten haben Platz, der Blob-Ort nicht (eigenes Volume)
        env.Update.FreeBytes = path => path.StartsWith(env.Paths.Blobs, StringComparison.OrdinalIgnoreCase) ? 100_000 : 100 * 1024 * MB;
        env.Update.Reserve = 10 * MB;
        var job = new BlobMigration(env.Spaces, env.Update, NullLogger<BlobMigration>.Instance);
        Assert.True(job.Run(space, default));   // true: noch offen
        Assert.Equal(pending, FileStore.PendingBlobs(space.Db));   // nichts angefasst
        Assert.Empty(space.Db.Blobs.List());
        var report = env.Update.BlobsOf(env.User.Id)!;
        Assert.Equal("space", report.State);
        Assert.True(report.MissingBytes >= bytes + 10 * MB - 100_000 - 1);
        Assert.Equal(100_000, report.FreeBytes);

        // Platz da -> laeuft
        env.Update.FreeBytes = _ => 100 * 1024 * MB;
        Assert.False(job.Run(space, default));
        Assert.Equal(0, FileStore.PendingBlobs(space.Db));
    }

    [Fact]
    public void Auslagern_grosser_Mengen_braucht_Freigabe_und_laesst_sich_pausieren()
    {
        var env = Env.Make("legacy-v1.1.0.dmdb");
        var space = env.Spaces.Get(env.User.Id);
        var job = new BlobMigration(env.Spaces, env.Update, NullLogger<BlobMigration>.Instance);
        env.Update.BigBytes = 100_000;

        Assert.True(job.Run(space, default));
        Assert.Equal("consent", env.Update.BlobsOf(env.User.Id)!.State);
        Assert.True(FileStore.PendingBlobs(space.Db) > 0);

        // Freigegeben, aber pausiert
        env.Update.Start(withoutBackup: false);
        env.Update.SetPaused(true);
        Assert.True(job.Run(space, default));
        Assert.Equal("paused", env.Update.BlobsOf(env.User.Id)!.State);
        Assert.True(FileStore.PendingBlobs(space.Db) > 0);

        // Fortsetzen
        env.Update.SetPaused(false);
        Assert.False(job.Run(space, default));
        Assert.Equal(0, FileStore.PendingBlobs(space.Db));
        // Danach gilt die Freigabe nicht mehr
        job.Pass(default);
        Assert.False(env.Update.BlobsApproved);
    }

    [Fact]
    public void Kleine_Mengen_werden_ohne_Rueckfrage_ausgelagert()
    {
        var env = Env.Make("legacy-v1.1.0.dmdb");
        var space = env.Spaces.Get(env.User.Id);
        var job = new BlobMigration(env.Spaces, env.Update, NullLogger<BlobMigration>.Instance);
        Assert.False(job.Run(space, default));
        Assert.Equal(0, FileStore.PendingBlobs(space.Db));
    }

    // ---------------- Aufraeumen alter Sicherungen ----------------

    [Fact]
    public void Alte_Sicherungen_raeumt_dmOffice_nach_der_eingestellten_Zeit_weg()
    {
        var env = Env.Make();
        env.Spaces.Get(env.User.Id);   // heutige Sicherung
        Assert.Equal(14, env.Update.KeepDays);   // Vorgabe
        var user = env.Users.ById(env.User.Id)!;
        var now = DateTime.UtcNow;
        PreUpdateBackup.Create(env.Paths, user, Key, 9, 18, "0.9.0", now.AddDays(-20));
        PreUpdateBackup.Create(env.Paths, user, Key, 9, 18, "0.9.1", now.AddDays(-10));
        Assert.Equal(3, PreUpdateBackup.List(env.Paths).Count);
        Assert.True(PreUpdateBackup.TotalBytes(env.Paths) > 3 * 400_000);

        // 0: nie loeschen
        env.Update.SetKeepDays(0);
        Assert.Equal(0, env.Update.CleanupBackups());
        Assert.Equal(3, PreUpdateBackup.List(env.Paths).Count);

        env.Update.SetKeepDays(14);
        Assert.Equal(1, env.Update.CleanupBackups());
        Assert.Equal(new[] { "0.9.1", "9.9.9" }.Order(), PreUpdateBackup.List(env.Paths).Select(b => b.AppVersion).Order());
        // Einstellung steht in server.json und gilt nach einem Neustart
        env.Update.SetKeepDays(7);
        Assert.Equal(7, new ServerConfig(env.Paths).Current.PreUpdateKeepDays);
        Assert.Equal(1, env.Update.CleanupBackups());
        Assert.Single(PreUpdateBackup.List(env.Paths));
        Assert.False(PreUpdateBackup.Delete(env.Paths, "../etc"));
        Assert.True(PreUpdateBackup.Delete(env.Paths, PreUpdateBackup.List(env.Paths).Single().Name));
        Assert.Empty(PreUpdateBackup.List(env.Paths));
    }

    [Fact]
    public void Wird_ein_Benutzer_geloescht_verschwinden_seine_Kopien_aus_den_Sicherungen()
    {
        var env = Env.Make();
        env.Spaces.Get(env.User.Id);   // heutige Sicherung
        var user = env.Users.ById(env.User.Id)!;
        PreUpdateBackup.Create(env.Paths, user, Key, 9, 18, "0.9.0", DateTime.UtcNow.AddDays(-5));
        Assert.Equal(2, Directory.EnumerateFiles(PreUpdateBackup.Root(env.Paths), env.User.Id + ".db", SearchOption.AllDirectories).Count());

        Assert.Equal(2, PreUpdateBackup.ForgetUser(env.Paths, env.User.Id));

        Assert.Empty(Directory.EnumerateFiles(PreUpdateBackup.Root(env.Paths), env.User.Id + ".db", SearchOption.AllDirectories));
        Assert.All(PreUpdateBackup.List(env.Paths), b => Assert.DoesNotContain(b.Users, u => u.Id == env.User.Id));
    }

    [Fact]
    public void Sicherungen_bleiben_solange_ein_Umbau_noch_aussteht()
    {
        var env = Env.Make();
        var user = env.Users.ById(env.User.Id)!;
        PreUpdateBackup.Create(env.Paths, user, Key, 9, 18, "0.9.0", DateTime.UtcNow.AddDays(-100));
        env.Free = 1 * MB;   // der Umbau wartet auf Platz
        Assert.Equal(0, env.Update.CleanupBackups());
        Assert.Single(PreUpdateBackup.List(env.Paths));
    }

    [Fact]
    public void Abgebrochene_Sicherung_hinterlaesst_nichts_Halbes()
    {
        var env = Env.Make();
        var user = env.Users.ById(env.User.Id)!;
        // Falscher Schluessel: VACUUM INTO scheitert
        Assert.ThrowsAny<Exception>(() => PreUpdateBackup.Create(env.Paths, user, new byte[32], 9, 18, "9.9.9", DateTime.UtcNow));
        var dir = PreUpdateBackup.List(env.Paths).Single();
        Assert.Empty(dir.Users);
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.part", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(dir.Path, "users", user.Id + ".db")));
        Assert.False(PreUpdateBackup.Has(env.Paths, user.Id, 9));
        // Danach klappt es mit dem richtigen
        PreUpdateBackup.Create(env.Paths, user, Key, 9, 18, "9.9.9", DateTime.UtcNow);
        Assert.True(PreUpdateBackup.Has(env.Paths, user.Id, 9));
    }

    // ---------------- Verdichten ----------------

    [Fact]
    public async Task Verdichten_startet_nur_mit_Platz_fuer_eine_zweite_Kopie()
    {
        var env = Env.Make("legacy-v1.1.0.dmdb");
        var space = env.Spaces.Get(env.User.Id);
        FileStore.MigrateBlobs(space.Db);
        var (file, wal, _) = space.Db.SpaceInfo();

        env.Free = 100_000;
        var job = SettingsApi.StartCompact(space, NullLogger.Instance, env.Paths, env.Update);
        Assert.False(job.Running);
        Assert.Null(job.Started);
        Assert.True(job.Missing > 0);
        Assert.Equal(file + wal + env.Update.Reserve, job.Need);
        Assert.Equal(100_000, job.Free);
        Assert.NotNull(job.Error);
        Assert.Equal(file, space.Db.SpaceInfo().File);   // nichts passiert

        env.Free = 1000 * MB;
        job = SettingsApi.StartCompact(space, NullLogger.Instance, env.Paths, env.Update);
        Assert.Equal(0, job.Missing);
        for (var i = 0; i < 200 && job.Running; i++) await Task.Delay(50);
        Assert.False(job.Running);
        Assert.Null(job.Error);
        Assert.True(space.Db.SpaceInfo().File < file);
        Assert.Equal("Hallo aus Version 1", Text(space.Db, "Dokumente/klein.txt"));
    }

    // ---------------- Echter Platzmangel ----------------

    [Fact]
    public void Platzabfrage_liefert_Zahlen_fuer_vorhandene_und_fehlende_Ordner()
    {
        var dir = Directory.CreateTempSubdirectory("dmfree").FullName;
        Assert.True(DiskSpace.Free(dir) > 0);
        Assert.True(DiskSpace.Free(Path.Combine(dir, "gibt", "es", "nicht")) > 0);   // geht zum naechsten vorhandenen Ordner hoch
        Assert.Equal("1.5 MB", DiskSpace.Format((long)(1.5 * MB)));
    }
}
