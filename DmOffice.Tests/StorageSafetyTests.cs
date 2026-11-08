using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>
/// Storage Extender: die maximale Nutzung des Speichervolumens auch bei BSBS, das Core-Backup wartet auf den Abgleich der Dateien, und nach
/// einer Wiederherstellung wird gerettet, was nach dem Core-Backup ans Ziel kam - aber nichts, was davor geloescht war.
/// </summary>
[Collection("server")]
public class StorageSafetyTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public StorageSafetyTests(ChatFixture fx) => _fx = fx;

    private S3Mirror NewMirror() => new(_fx.Factory.Services.GetRequiredService<Spaces>(), _fx.Factory.Services.GetRequiredService<ServerConfig>(),
        _fx.Factory.Services.GetRequiredService<DataPaths>(), NullLogger<S3Mirror>.Instance);

    [Fact]
    public void Reserve_gilt_auch_bei_BSBS_und_nur_mit_externem_Speicher()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("cleo");
        var before = config.Current;
        try
        {
            // 10 % Reserve, darueber nur noch 2000 Bytes
            S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 100_000_002_000);
            config.Update(s => { s.S3Mirror = false; s.S3MinFreePercent = 10; });
            DmOffice.Dav.FileStore.Put(space.Db, "reserve-aus.bin", new byte[8000], "application/octet-stream");   // ohne Storage Extender: wie bisher
            config.Update(s => { s.S3Mirror = true; s.S3Mode = S3Mirror.ModeMirror; });
            var ex = Assert.Throws<DmOffice.Dav.QuotaExceededException>(() => DmOffice.Dav.FileStore.Put(space.Db, "reserve-an.bin", new byte[8000], "application/octet-stream"));
            Assert.Contains("frei zu lassende", ex.Message);
            DmOffice.Dav.FileStore.Put(space.Db, "reserve-klein.bin", new byte[500], "application/octet-stream");   // passt noch
        }
        finally
        {
            S3Mirror.VolumeOverride = null;
            config.Update(s => { s.S3Mirror = before.S3Mirror; s.S3Mode = before.S3Mode; s.S3MinFreePercent = before.S3MinFreePercent; });
        }
    }

    [Fact]
    public async Task Warten_auf_den_naechsten_Abgleich()
    {
        var mirror = NewMirror();
        var root = Directory.CreateTempSubdirectory("dmwait").FullName;
        try
        {
            var wait = mirror.NextPassAsync(CancellationToken.None);
            Assert.False(wait.IsCompleted);
            await mirror.PassAsync(new S3MirrorTests.FolderRemote(root), CancellationToken.None);
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Nach_der_Wiederherstellung_wird_Neues_gerettet_Geloeschtes_nicht()
    {
        var services = _fx.Factory.Services;
        var paths = services.GetRequiredService<DataPaths>();
        var space = _fx.Space("dora");
        var db = space.Db;
        var mirror = NewMirror();
        var root = Directory.CreateTempSubdirectory("dmrescue").FullName;
        var remote = new S3MirrorTests.FolderRemote(root);
        var known = Path.Combine(paths.Root, S3Mirror.KnownFolder);
        string BlobOf(string p) => db.Query($"SELECT blob FROM files WHERE path='{p}'", r => r.GetString(0)).Single();
        void Forget(string p)
        {
            // Wie nach dem Zurueckspielen: die Datenbank kennt die Datei nicht, lokal liegt sie nicht
            var blob = BlobOf(p);
            db.Exec($"DELETE FROM files WHERE path='{p}'");
            db.Blobs.Delete(blob);
        }
        try
        {
            var stamp = Guid.NewGuid().ToString("N");
            // Vor dem Core-Backup geloescht, am Ziel aber noch da (Frist): darf nicht zurueckkommen
            DmOffice.Dav.FileStore.Put(db, "rescue-alt.txt", System.Text.Encoding.UTF8.GetBytes("Alt " + stamp), "text/plain");
            await mirror.PassAsync(remote, CancellationToken.None);
            Forget("rescue-alt.txt");
            // Core-Backup: merkt sich, was jetzt am Ziel liegt
            Directory.CreateDirectory(known);
            File.WriteAllText(Path.Combine(known, space.User.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(mirror.AtTarget(space.User.Id)));
            // Nach dem Core-Backup hochgeladen, dann faellt der Server aus
            DmOffice.Dav.FileStore.Put(db, "rescue-neu.txt", System.Text.Encoding.UTF8.GetBytes("Neu " + stamp), "text/plain");
            await mirror.PassAsync(remote, CancellationToken.None);
            Forget("rescue-neu.txt");

            await mirror.RescueAsync(remote, CancellationToken.None);
            var found = DmOffice.Dav.FileStore.Children(db, S3Mirror.RescueFolder).Where(f => !f.IsDir).ToList();
            var texts = found.Select(f => { using var r = new StreamReader(DmOffice.Dav.FileStore.OpenRead(db, f.Path)); return r.ReadToEnd(); }).ToList();
            Assert.Contains("Neu " + stamp, texts);
            Assert.DoesNotContain("Alt " + stamp, texts);
            Assert.All(found, f => Assert.EndsWith(".txt", f.Path));
            Assert.False(File.Exists(Path.Combine(known, space.User.Id + ".json")));
            Assert.Equal(found.Count, mirror.Rescued!.Rescued);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(known)) Directory.Delete(known, recursive: true);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, "png")]
    [InlineData(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' }, "pdf")]
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, "heic")]
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }, "mp4")]
    [InlineData(new byte[] { (byte)'H', (byte)'a', (byte)'l', (byte)'l', (byte)'o' }, "txt")]
    [InlineData(new byte[] { 1, 0, 2, 3 }, "bin")]
    public void Dateityp_erkennen(byte[] head, string ext) => Assert.Equal(ext, S3Mirror.Sniff(head).Ext);

    [Fact]
    public void Zufallsbytes_ohne_Nullbyte_gelten_nicht_als_Text()
    {
        var head = new byte[64];
        new Random(5).NextBytes(head);
        for (var i = 0; i < head.Length; i++) if (head[i] == 0) head[i] = 0xC3;
        head[0] = 0xC3; head[1] = 0x28;
        Assert.Equal("bin", S3Mirror.Sniff(head).Ext);
        Assert.Equal("txt", S3Mirror.Sniff("Grüße aus dem Büro\r\n"u8.ToArray()).Ext);
    }
}
