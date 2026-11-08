using System.Net.Http.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>Kopie der Dateiinhalte nach S3: Fehlendes kommt hoch, lokal Verschwundenes wird erst nach der Frist geloescht,
/// und fehlt lokal fast alles, wird gar nichts geloescht.</summary>
[Collection("server")]
public class S3MirrorTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public S3MirrorTests(ChatFixture fx) => _fx = fx;

    /// <summary>Ein Ordner statt S3.</summary>
    internal sealed class FolderRemote(string root) : IBlobRemote
    {
        public List<string> Deleted { get; } = new();
        private string PathOf(string key) => Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
        public async Task<List<(string Key, long Size)>> ListSizesAsync(string prefix, CancellationToken ct) =>
            (await ListAsync(prefix, ct)).Select(k => (k, new FileInfo(PathOf(k)).Length)).ToList();
        public Task<HashSet<string>> ListAsync(string prefix, CancellationToken ct) => Task.FromResult(Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Where(k => k.StartsWith(prefix)).ToHashSet()
            : new HashSet<string>());
        public Task UploadAsync(string file, string key, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(key))!);
            File.Copy(file, PathOf(key), overwrite: true);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken ct) { File.Delete(PathOf(key)); Deleted.Add(key); return Task.CompletedTask; }
        public void Dispose() { }
        public Task DownloadAsync(string key, string file, CancellationToken ct) { File.Copy(PathOf(key), file, overwrite: true); return Task.CompletedTask; }
        public int Ranges;
        public Task<long> SizeAsync(string key, CancellationToken ct) => Task.FromResult(File.Exists(PathOf(key)) ? new FileInfo(PathOf(key)).Length : -1);
        public Task<byte[]> ReadRangeAsync(string key, long offset, int count, CancellationToken ct)
        {
            Ranges++;
            using var f = File.OpenRead(PathOf(key));
            f.Position = offset;
            var buf = new byte[Math.Min(count, f.Length - offset)];
            f.ReadExactly(buf);
            return Task.FromResult(buf);
        }
    }

    [Theory]
    [InlineData(100, 50, long.MaxValue, long.MaxValue, true)]    // keine Begrenzung
    [InlineData(100, 50, 200, long.MaxValue, true)]              // passt in die Begrenzung
    [InlineData(100, 50, 149, long.MaxValue, false)]             // passt nicht in die Begrenzung
    [InlineData(100, 50, long.MaxValue, 50, true)]               // passt genau bis zur Reserve
    [InlineData(100, 50, long.MaxValue, 49, false)]              // Reserve wäre unterschritten
    [InlineData(100, 50, long.MaxValue, -5, false)]
    [InlineData(300, 50, 200, long.MaxValue, false)]             // schon über der Begrenzung
    public void Alles_passt_in_die_Grenzen(long here, long away, long target, long headroom, bool fits) =>
        Assert.Equal(fits, S3Mirror.Fits(here, away, target, headroom));

    [Fact]
    public void Frei_zu_lassender_Platz_in_GB()
    {
        const long gb = 1024L * 1024 * 1024;
        Assert.Equal(50 * gb, S3Mirror.ReserveOf(new ServerSettings { S3MinFreeGb = 50 }, 1000 * gb));
        // Bleibt derselbe Wert, egal wie gross das Volume ist
        Assert.Equal(50 * gb, S3Mirror.ReserveOf(new ServerSettings { S3MinFreeGb = 50 }, 100 * gb));
        // Aeltere Einstellung in Prozent
        Assert.Equal(100 * gb, S3Mirror.ReserveOf(new ServerSettings { S3MinFreePercent = 10 }, 1000 * gb));
        Assert.Equal(50, S3Mirror.DefaultMinFreeGb(1000 * gb));
        Assert.Equal(20, S3Mirror.DefaultMinFreeGb(100 * gb));
        Assert.Equal(1, S3Mirror.DefaultMinFreeGb(2 * gb));
    }

    [Fact]
    public void Alte_Betriebsart_wird_zur_Begrenzung()
    {
        Assert.False(S3Mirror.Limited(new ServerSettings { S3Mode = S3Mirror.ModeMirror }));
        Assert.True(S3Mirror.Limited(new ServerSettings { S3Mode = S3Mirror.ModeCache, S3CacheTargetGb = 5 }));
        Assert.True(S3Mirror.Limited(new ServerSettings { S3Mode = S3Mirror.ModeMirror, S3CacheLimited = true, S3CacheTargetGb = 5 }));
        Assert.False(S3Mirror.Limited(new ServerSettings { S3Mode = S3Mirror.ModeCache, S3CacheLimited = false }));
        Assert.Equal(long.MaxValue, S3Mirror.Target(new ServerSettings { S3Mode = S3Mirror.ModeMirror }));
    }

    [Fact]
    public async Task Auffuellen_holt_die_neuesten_Dateien_bis_zur_Grenze()
    {
        const double gb = 1024.0 * 1024 * 1024;
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var mirror = new S3Mirror(services.GetRequiredService<Spaces>(), config, services.GetRequiredService<DataPaths>(), NullLogger<S3Mirror>.Instance);
        var space = _fx.Space("ben");
        foreach (var n in new[] { "fuell-alt", "fuell-neu" }) DmOffice.Dav.FileStore.Put(space.Db, n + ".bin", System.Text.Encoding.UTF8.GetBytes(n + Guid.NewGuid() + new string('z', 8000)), "application/octet-stream");
        string BlobOf(string n) => space.Db.Query($"SELECT blob FROM files WHERE path='{n}.bin'", r => r.GetString(0)).Single();
        space.Db.Exec("UPDATE files SET modified = '2020-01-01T00:00:00.0000000Z' WHERE path = 'fuell-alt.bin'");
        space.Db.Exec("UPDATE files SET modified = '2030-01-01T00:00:00.0000000Z' WHERE path = 'fuell-neu.bin'");
        var root = Directory.CreateTempSubdirectory("dms3f").FullName;
        var remote = new FolderRemote(root);
        var before = config.Current;
        try
        {
            S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 500_000_000_000);
            config.Update(s => { s.S3Mirror = true; s.S3CacheLimited = true; s.S3CacheTargetGb = 100; s.S3MinFreeGb = 1; });
            await mirror.PassAsync(remote, CancellationToken.None);
            var all = mirror.Status.Cache!.Bytes;
            Assert.Equal(0, mirror.Status.Cache.Remote);
            // Der lokale Speicherpool zählt nur Dateien: ein Vorschaubild ändert nichts daran, es steht für sich
            var thumb = space.Db.Blobs.Write(System.Text.Encoding.UTF8.GetBytes(new string('t', 5000))).Id;
            space.Db.Exec("INSERT INTO file_thumbs(fileid, size, etag, data, blob) SELECT fileid, 256, etag, X'', $b FROM files WHERE path = 'fuell-alt.bin'", ("$b", thumb));
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Equal(all, mirror.Status.Cache!.Bytes);
            Assert.True(mirror.Status.Cache.ThumbBytes > 0);
            // Beide liegen nur noch am Ziel; die Grenze fasst danach nur eine von beiden
            space.Db.Blobs.Delete(BlobOf("fuell-alt"));
            space.Db.Blobs.Delete(BlobOf("fuell-neu"));
            config.Update(s => s.S3CacheTargetGb = (all - 100) / gb);
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Null(mirror.Status.Restore);
            Assert.Equal(1, await mirror.PrefillAsync(remote, TimeSpan.FromMinutes(1), CancellationToken.None));
            Assert.Null(mirror.Prefill);   // danach nichts mehr in der Aktivitaet
            Assert.True(space.Db.Blobs.Exists(BlobOf("fuell-neu")));
            Assert.False(space.Db.Blobs.Exists(BlobOf("fuell-alt")));
            // Nichts mehr, was passt: Es bleibt dabei, und bereinigt wird auch nichts
            Assert.Equal(0, await mirror.PrefillAsync(remote, TimeSpan.FromMinutes(1), CancellationToken.None));
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Equal(0, mirror.Status.Cache!.Evicted);
            Assert.True(space.Db.Blobs.Exists(BlobOf("fuell-neu")));
        }
        finally
        {
            S3Mirror.VolumeOverride = null;
            config.Update(s => { s.S3Mirror = before.S3Mirror; s.S3CacheLimited = before.S3CacheLimited; s.S3CacheTargetGb = before.S3CacheTargetGb; s.S3MinFreeGb = before.S3MinFreeGb; });
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Kopie_holt_Fehlendes_von_selbst_zurueck()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var mirror = new S3Mirror(services.GetRequiredService<Spaces>(), config, services.GetRequiredService<DataPaths>(), NullLogger<S3Mirror>.Instance);
        var space = _fx.Space("ben");
        DmOffice.Dav.FileStore.Put(space.Db, "selbst-zurueck.txt", System.Text.Encoding.UTF8.GetBytes("Zurueck " + Guid.NewGuid()), "text/plain");
        var blob = space.Db.Query("SELECT blob FROM files WHERE path='selbst-zurueck.txt'", r => r.GetString(0)).Single();
        var root = Directory.CreateTempSubdirectory("dms3auto").FullName;
        var remote = new FolderRemote(root);
        var before = config.Current;
        try
        {
            config.Update(s => s.S3Mirror = true);
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Null(mirror.Status.Restore);
            // Platte verliert den Inhalt: der naechste Durchgang merkt es und stoesst das Zurueckholen an - ohne Knopf
            File.Delete(space.Db.Blobs.PathOf(blob));
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.True(mirror.Status.Restore?.Running);
        }
        finally { config.Update(s => s.S3Mirror = before.S3Mirror); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Ausschalten_haelt_den_laufenden_Abgleich_an()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var mirror = new S3Mirror(services.GetRequiredService<Spaces>(), config, services.GetRequiredService<DataPaths>(), NullLogger<S3Mirror>.Instance);
        var blobs = _fx.Space("ben").Db.Blobs;
        for (var i = 0; i < 5; i++) blobs.Write(System.Text.Encoding.UTF8.GetBytes("Abbruch " + i + Guid.NewGuid()));
        var root = Directory.CreateTempSubdirectory("dms3off").FullName;
        var before = config.Current;
        try
        {
            config.Update(s => s.S3Mirror = true);
            // Gleich beim ersten Hochladen schaltet jemand den Storage Extender aus
            var remote = new SwitchOffRemote(new FolderRemote(root), () => config.Update(s => s.S3Mirror = false));
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Equal(1, remote.Uploads);
            Assert.False(mirror.Status.Running);
        }
        finally { config.Update(s => s.S3Mirror = before.S3Mirror); Directory.Delete(root, recursive: true); }
    }

    private sealed class SwitchOffRemote(IBlobRemote inner, Action onUpload) : IBlobRemote
    {
        public int Uploads;
        public Task<HashSet<string>> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);
        public Task<List<(string Key, long Size)>> ListSizesAsync(string prefix, CancellationToken ct) => inner.ListSizesAsync(prefix, ct);
        public async Task UploadAsync(string file, string key, CancellationToken ct) { Uploads++; await inner.UploadAsync(file, key, ct); onUpload(); }
        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);
        public Task DownloadAsync(string key, string file, CancellationToken ct) => inner.DownloadAsync(key, file, ct);
        public Task<long> SizeAsync(string key, CancellationToken ct) => inner.SizeAsync(key, ct);
        public Task<byte[]> ReadRangeAsync(string key, long offset, int count, CancellationToken ct) => inner.ReadRangeAsync(key, offset, count, ct);
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task Hochladen_und_verzoegert_loeschen()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var mirror = new S3Mirror(services.GetRequiredService<Spaces>(), config, services.GetRequiredService<DataPaths>(), NullLogger<S3Mirror>.Instance);
        var space = _fx.Space("anna");
        var blobs = space.Db.Blobs;
        var ids = Enumerable.Range(0, 4).Select(i => blobs.Write(System.Text.Encoding.UTF8.GetBytes("Inhalt " + i + Guid.NewGuid())).Id!).ToList();
        var root = Directory.CreateTempSubdirectory("dms3").FullName;
        var remote = new FolderRemote(root);
        try
        {
            await mirror.PassAsync(remote, CancellationToken.None);
            var prefix = S3Mirror.Prefix(config.Current.S3Prefix) + space.User.Id + "/";
            var there = await remote.ListAsync(prefix, CancellationToken.None);
            Assert.All(ids, id => Assert.Contains(prefix + id[..2] + "/" + id, there));
            // Hochgeladen wird der verschluesselte Blob, kein Klartext
            Assert.Equal(File.ReadAllBytes(blobs.PathOf(ids[0])), File.ReadAllBytes(Path.Combine(root, (prefix + ids[0][..2] + "/" + ids[0]).Replace('/', Path.DirectorySeparatorChar))));

            // Lokal geloescht: erst vormerken, nicht sofort in S3 loeschen
            blobs.Delete(ids[0]);
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Empty(remote.Deleted);
            var gone = Path.Combine(blobs.Dir, ".s3-gone.json");
            Assert.True(File.Exists(gone));
            // Frist abgelaufen: jetzt weg
            File.WriteAllText(gone, File.ReadAllText(gone).Replace(DateTime.UtcNow.Year.ToString(), (DateTime.UtcNow.Year - 1).ToString()));
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Contains(remote.Deleted, k => k.EndsWith(ids[0]));

            // Fehlt lokal fast alles (Datenverzeichnis nicht eingehaengt?): nichts vormerken
            foreach (var id in ids.Skip(1)) blobs.Delete(id);
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.False(File.Exists(gone) && File.ReadAllText(gone).Contains(ids[1]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Anderer_Server_am_selben_Ziel_dann_loescht_dort_keiner()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var mirror = new S3Mirror(services.GetRequiredService<Spaces>(), config, services.GetRequiredService<DataPaths>(), NullLogger<S3Mirror>.Instance);
        var space = _fx.Space("cleo");
        var blobs = space.Db.Blobs;
        var ids = Enumerable.Range(0, 4).Select(i => blobs.Write(System.Text.Encoding.UTF8.GetBytes("Anderer " + i + Guid.NewGuid())).Id!).ToList();
        var root = Directory.CreateTempSubdirectory("dms3").FullName;
        var remote = new FolderRemote(root);
        var marker = Path.GetTempFileName();
        void Other(DateTime at) { File.WriteAllText(marker, at.ToString("o")); remote.UploadAsync(marker, S3Mirror.Prefix(config.Current.S3Prefix) + S3Mirror.ServersFolder + new string('f', 32), default).Wait(); }
        var gone = Path.Combine(blobs.Dir, ".s3-gone.json");
        void Age() { if (File.Exists(gone)) File.WriteAllText(gone, File.ReadAllText(gone).Replace(DateTime.UtcNow.Year.ToString(), (DateTime.UtcNow.Year - 1).ToString())); }
        try
        {
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Null(mirror.Status.OtherServer);
            // Ein zweiter Server (etwa aus einer Sicherung aufgesetzt) benutzt dasselbe Ziel: was diesem fehlt, kann jenem gehören
            Other(DateTime.UtcNow);
            blobs.Delete(ids[0]);
            await mirror.PassAsync(remote, CancellationToken.None);
            Age();
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Empty(remote.Deleted);
            Assert.NotNull(mirror.Status.OtherServer);
            // Seit über der Frist nicht mehr da: aufgeräumt wird wieder - mit frisch laufender Frist
            Other(DateTime.UtcNow.AddYears(-2));
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Null(mirror.Status.OtherServer);
            Assert.Empty(remote.Deleted);
            Age();
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Contains(remote.Deleted, k => k.EndsWith(ids[0]));
        }
        finally { Directory.Delete(root, recursive: true); File.Delete(marker); }
    }

    [Fact]
    public async Task Fehlendes_aus_S3_holen()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var mirror = new S3Mirror(services.GetRequiredService<Spaces>(), config, services.GetRequiredService<DataPaths>(), NullLogger<S3Mirror>.Instance);
        var space = _fx.Space("ben");
        var text = "Rücksicherung " + Guid.NewGuid();
        DmOffice.Dav.FileStore.Put(space.Db, "s3-ruecksicherung.txt", System.Text.Encoding.UTF8.GetBytes(text), "text/plain");
        var blob = space.Db.Query("SELECT blob FROM files WHERE path='s3-ruecksicherung.txt'", r => r.GetString(0)).Single();
        var root = Directory.CreateTempSubdirectory("dms3r").FullName;
        var remote = new FolderRemote(root);
        try
        {
            await mirror.PassAsync(remote, CancellationToken.None);
            // Platte kaputt: der Inhalt fehlt lokal, die Datenbank kennt ihn noch
            File.Delete(space.Db.Blobs.PathOf(blob));
            await mirror.RestoreAsync(remote, CancellationToken.None);
            Assert.True(space.Db.Blobs.Exists(blob));
            using (var r = new StreamReader(DmOffice.Dav.FileStore.OpenRead(space.Db, "s3-ruecksicherung.txt")))
                Assert.Equal(text, r.ReadToEnd());
            Assert.True(mirror.Status.Restore!.Restored >= 1);

            // Beschaedigt in S3: wird nicht abgelegt
            File.Delete(space.Db.Blobs.PathOf(blob));
            var key = Directory.EnumerateFiles(root, blob, SearchOption.AllDirectories).Single();
            var bytes = File.ReadAllBytes(key); bytes[^5] ^= 0xFF; File.WriteAllBytes(key, bytes);
            await mirror.RestoreAsync(remote, CancellationToken.None);
            Assert.False(space.Db.Blobs.Exists(blob));
            Assert.True(mirror.Status.Restore!.Failed >= 1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>SSBS mit S3 (Ersatz-Ziel) und mit einem echten Ordner als Ziel (Speicher-Treiber wie bei WebDAV).</summary>
    [Theory]
    [InlineData("s3")]
    [InlineData("folder")]
    public async Task SSBS_verdraengt_holt_beim_Oeffnen_und_loescht_am_Ziel_nichts(string target)
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var mirror = new S3Mirror(services.GetRequiredService<Spaces>(), config, services.GetRequiredService<DataPaths>(), NullLogger<S3Mirror>.Instance);
        var space = _fx.Space(target == "folder" ? "cleo" : "dora");
        var texts = Enumerable.Range(0, 3).Select(i => $"Zwischenspeicher {i} " + Guid.NewGuid() + new string('x', 4000)).ToList();
        for (int i = 0; i < 3; i++) DmOffice.Dav.FileStore.Put(space.Db, $"cache-{i}.txt", System.Text.Encoding.UTF8.GetBytes(texts[i]), "text/plain");
        string BlobOf(int i) => space.Db.Query($"SELECT blob FROM files WHERE path='cache-{i}.txt'", r => r.GetString(0)).Single();
        var root = Directory.CreateTempSubdirectory("dms3c").FullName;
        IBlobRemote remote = target == "folder" ? new DmOffice.Storage.DriverRemote(new DmOffice.Storage.FolderDriver(root)) : new FolderRemote(root);
        var before = config.Current;
        try
        {
            config.Update(s => { s.S3Mirror = true; s.S3Mode = S3Mirror.ModeCache; s.S3CacheTargetGb = 0.000001; s.S3MinFreePercent = 10; s.S3Endpoint = "http://test"; s.S3Bucket = "b"; s.S3AccessKey = "k"; s.StorageTarget = target; s.StorageFolder = root; });
            config.SetS3SecretKey("geheim");
            S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 500_000_000_000);
            // Unverarbeitetes wird nie verdraengt: die drei als fertig gelesen markieren (Volltext-Warteschlange leer)
            space.Db.Exec("DELETE FROM text_queue WHERE kind = 'file' AND ref LIKE 'cache-%'");
            await mirror.PassAsync(remote, CancellationToken.None);   // erst alles nach S3
            // Alt machen (Frisches wird nie verdraengt); cache-0 zuletzt gelesen, cache-2 am laengsten nicht
            for (int i = 0; i < 3; i++)
            {
                var p = space.Db.Blobs.PathOf(BlobOf(i));
                if (!File.Exists(p)) continue;   // schon beim ersten Abgleich verdraengt (fertig verarbeitet, gesichert)
                File.SetLastAccessTimeUtc(p, DateTime.UtcNow.AddHours(-10 * (i + 1)));
            }
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.False(space.Db.Blobs.Exists(BlobOf(2)));
            Assert.True(mirror.Status.Cache!.Remote >= 1);
            // Solange Inhalte nur im Storage Extender liegen: nicht ausschalten, keinen Schlüssel löschen, kein anderes Ziel - es ändert sich nichts
            var cfg = config.Current;
            // Die Verwaltung braucht eine Anmeldung im Browser (App-Passwörter dürfen dort nicht)
            var admin = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
            Assert.True((await admin.PostAsJsonAsync("/api/auth/login", new { login = "anna", password = "geheim-123456" })).IsSuccessStatusCode);
            async Task Refused(object body)
            {
                var res = await admin.PutAsJsonAsync("/api/admin/storage", body);
                Assert.Equal(System.Net.HttpStatusCode.Conflict, res.StatusCode);
                Assert.Contains("nur im Storage Extender", await res.Content.ReadAsStringAsync());
                Assert.True(config.Current.S3Mirror);
                Assert.Equal("geheim", config.S3SecretKey());
            }
            await Refused(new { mirror = false, target = cfg.StorageTarget, endpoint = cfg.S3Endpoint, bucket = cfg.S3Bucket, prefix = cfg.S3Prefix, accessKey = cfg.S3AccessKey, folder = cfg.StorageFolder });
            if (target == "s3") await Refused(new { mirror = true, target = cfg.StorageTarget, endpoint = cfg.S3Endpoint, bucket = cfg.S3Bucket, prefix = cfg.S3Prefix, accessKey = cfg.S3AccessKey, folder = cfg.StorageFolder, secretKey = "" });
            await Refused(new { mirror = true, target = "webdav", webDavUrl = "https://example.org/dav/" });
            // Anderer Präfix: dort liegt nichts von dem, was hier fehlt
            await Refused(new { mirror = true, target = cfg.StorageTarget, endpoint = cfg.S3Endpoint, bucket = cfg.S3Bucket, prefix = "woanders", accessKey = cfg.S3AccessKey, folder = cfg.StorageFolder });
            // Frisch hochgeladenes Foto ohne Vorschaubild (noch nicht fertig verarbeitet): bleibt hier, auch ueber dem Zielwert
            // An der Reserve (die maximale Nutzung des Speichervolumens) wird das Hochladen abgewiesen - mit klarer Meldung
            S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 100_000_000_000 + 2000);   // 10 % Reserve, darueber nur noch 2000 Bytes
            var full = Assert.Throws<DmOffice.Dav.QuotaExceededException>(() => DmOffice.Dav.FileStore.Put(space.Db, "cache-voll.txt", new byte[8000], "application/octet-stream"));
            Assert.Contains("frei zu lassende", full.Message);
            S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 500_000_000_000);
            DmOffice.Dav.FileStore.Put(space.Db, "cache-neu.jpg", System.Text.Encoding.UTF8.GetBytes("Neu " + Guid.NewGuid() + new string('y', 4000)), "image/jpeg");
            await mirror.PassAsync(remote, CancellationToken.None);
            var neu = space.Db.Query("SELECT blob FROM files WHERE path='cache-neu.jpg'", r => r.GetString(0)).Single();
            Assert.True(space.Db.Blobs.Exists(neu));
            Assert.True(mirror.Status.Cache!.Waiting > 0);
            // Vorschaubild da (hier: als nicht darstellbar vermerkt): beim naechsten Abgleich darf er gehen
            space.Db.Exec("INSERT INTO file_thumbs(fileid, size, etag, data) SELECT fileid, " + DmOffice.Dav.Thumbs.Small + ", etag, X'' FROM files WHERE path = 'cache-neu.jpg'");
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.False(space.Db.Blobs.Exists(neu));
            S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 100_000_000_000 + 1000);   // 10 % Reserve, darueber nur noch 1000 Bytes
            // In S3 nichts zum Loeschen vorgemerkt - die Datenbank braucht den Inhalt noch
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.Contains(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), f => f.EndsWith(BlobOf(2)));
            var gone = Path.Combine(space.Db.Blobs.Dir, ".s3-gone.json");
            Assert.False(File.Exists(gone) && File.ReadAllText(gone).Contains(BlobOf(2)));

            // Hintergrundarbeiten holen nichts
            using (BlobStore.WithoutFetch())
                Assert.ThrowsAny<IOException>(() => DmOffice.Dav.FileStore.OpenRead(space.Db, "cache-2.txt").Dispose());
            Assert.False(space.Db.Blobs.Exists(BlobOf(2)));

            // Oeffnen holt ihn aus S3
            mirror.RemoteOverride = remote;
            var fetch = BlobStore.Fetch;
            BlobStore.Fetch = (b, id) => mirror.FetchOne(b, id);
            try
            {
                // Bis zur Reserve zu wenig Platz: direkt aus S3 durchgereicht, nicht abgelegt
                using (var r = new StreamReader(DmOffice.Dav.FileStore.OpenRead(space.Db, "cache-2.txt"))) Assert.Equal(texts[2], r.ReadToEnd());
                Assert.False(space.Db.Blobs.Exists(BlobOf(2)));
                if (remote is FolderRemote counted) Assert.True(counted.Ranges > 0);
                // Mitten hinein springen (Video spulen): nur der Ausschnitt
                using (var st = DmOffice.Dav.FileStore.OpenRead(space.Db, "cache-2.txt"))
                {
                    st.Position = 100;
                    var part = new byte[20];
                    st.ReadExactly(part);
                    Assert.Equal(System.Text.Encoding.UTF8.GetBytes(texts[2]).AsSpan(100, 20).ToArray(), part);
                }
                // Genug Platz im Zwischenspeicher: abgelegt
                config.Update(s => s.S3CacheTargetGb = 1);
                S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 500_000_000_000);
                using (var r = new StreamReader(DmOffice.Dav.FileStore.OpenRead(space.Db, "cache-2.txt"))) Assert.Equal(texts[2], r.ReadToEnd());
                Assert.True(space.Db.Blobs.Exists(BlobOf(2)));
                // Auch in S3 nicht da: „fehlt“, nicht „S3 nicht erreichbar“
                space.Db.Blobs.Delete(BlobOf(1));
                foreach (var f in Directory.EnumerateFiles(root, BlobOf(1), SearchOption.AllDirectories)) File.Delete(f);
                var ex = Assert.Throws<BlobMissingException>(() => DmOffice.Dav.FileStore.OpenRead(space.Db, "cache-1.txt").Dispose());
                Assert.Contains("fehlt", ex.Message);
            }
            finally { BlobStore.Fetch = fetch; }
        }
        finally
        {
            config.Update(s => { s.S3Mirror = before.S3Mirror; s.S3Mode = S3Mirror.ModeMirror; s.S3CacheTargetGb = 100; s.S3Endpoint = null; s.S3Bucket = null; s.S3AccessKey = null; });
            config.SetS3SecretKey(null);
            S3Mirror.VolumeOverride = null;
            Directory.Delete(root, recursive: true);
        }
    }
}
