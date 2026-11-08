using Microsoft.Extensions.DependencyInjection;
using System.Text;
using DmOffice.Storage;

namespace DmOffice.Tests;

/// <summary>Speicher-Treiber: Ordner auf dem Server und WebDAV (gegen dmOffices eigenen WebDAV-Server).</summary>
[Collection("server")]
public class StorageDriverTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public StorageDriverTests(FilesServerFixture fx) => _fx = fx;

    /// <summary>Was jeder Treiber koennen muss.</summary>
    private static async Task Durchlauf(IStorageDriver d)
    {
        var ct = CancellationToken.None;
        var text = "Hallo aus dem Treiber " + Guid.NewGuid() + new string('x', 5000);
        await d.WriteAsync("Treiber/Unter/a.txt", new MemoryStream(Encoding.UTF8.GetBytes(text)), ct);
        var list = await d.ListAsync("Treiber/Unter", ct);
        var a = Assert.Single(list, e => e.Name == "a.txt");
        Assert.False(a.IsDir);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), a.Size);
        Assert.Contains(await d.ListAsync("Treiber", ct), e => e.Name == "Unter" && e.IsDir);
        using (var r = new StreamReader(await d.OpenReadAsync("Treiber/Unter/a.txt", ct))) Assert.Equal(text, await r.ReadToEndAsync());
        Assert.Equal(Encoding.UTF8.GetBytes(text).AsSpan(100, 50).ToArray(), await d.ReadRangeAsync("Treiber/Unter/a.txt", 100, 50, ct));
        Assert.Equal("a.txt", (await d.StatAsync("Treiber/Unter/a.txt", ct))!.Name);
        Assert.Null(await d.StatAsync("Treiber/fehlt.txt", ct));
        await d.MoveAsync("Treiber/Unter/a.txt", "Treiber/b.txt", ct);
        Assert.Null(await d.StatAsync("Treiber/Unter/a.txt", ct));
        Assert.NotNull(await d.StatAsync("Treiber/b.txt", ct));
        await d.MkdirAsync("Treiber/Leer/Tief", ct);
        Assert.True((await d.StatAsync("Treiber/Leer/Tief", ct))!.IsDir);
        await d.DeleteAsync("Treiber", ct);
        Assert.Null(await d.StatAsync("Treiber", ct));
        await d.DeleteAsync("Treiber", ct);   // schon weg: kein Fehler
        // Nie hinaus
        await Assert.ThrowsAsync<ArgumentException>(() => d.StatAsync("../geheim", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => d.ListAsync("a/../../x", ct));
        await Assert.ThrowsAnyAsync<Exception>(() => d.DeleteAsync("", ct));
    }

    [Fact]
    public async Task Ordner()
    {
        var root = Directory.CreateTempSubdirectory("dmdriver").FullName;
        var outside = Directory.CreateTempSubdirectory("dmdriver-aussen").FullName;
        try
        {
            using var d = new FolderDriver(root);
            await Durchlauf(d);
            var space = await d.SpaceAsync(CancellationToken.None);
            Assert.True(space is { Free: > 0, Used: >= 0 });
            File.WriteAllText(Path.Combine(outside, "geheim.txt"), "nicht zeigen");
            // Verknuepfung nach draussen: nicht auflisten, nicht oeffnen (Windows braucht dafuer Rechte - dann ueberspringen)
            try { Directory.CreateSymbolicLink(Path.Combine(root, "raus"), outside); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            Assert.DoesNotContain(await d.ListAsync("", CancellationToken.None), e => e.Name == "raus");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.OpenReadAsync("raus/geheim.txt", CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task WebDav_gegen_dmOffice_selbst()
    {
        using var http = _fx.Client();
        using var d = new WebDavDriver($"http://localhost/dav/files/{FilesServerFixture.Login}/", FilesServerFixture.Login, _fx.AppPassword, http);
        await Durchlauf(d);
        // Ohne Kontingent meldet dmOffice „unbekannt“ (-3): dann kein Platz, aber auch kein Fehler
        var space = await d.SpaceAsync(CancellationToken.None);
        Assert.True(space == null || space.Free >= 0);
    }

    [Fact]
    public async Task WebDav_falsches_Passwort()
    {
        using var http = _fx.Client(signedIn: false);
        using var d = new WebDavDriver($"http://localhost/dav/files/{FilesServerFixture.Login}/", FilesServerFixture.Login, "falsch", http);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.ListAsync("", CancellationToken.None));
    }
}

/// <summary>Kopie und Sicherung auf WebDAV und Ordner (DriverRemote) statt S3.</summary>
[Collection("server")]
public class StorageTargetTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public StorageTargetTests(FilesServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Sicherung_und_Kopie_auf_WebDav_und_Ordner()
    {
        var services = _fx.Factory.Services;
        var paths = services.GetRequiredService<DmOffice.Core.DataPaths>();
        var vault = services.GetRequiredService<DmOffice.Core.Vault>();
        var users = services.GetRequiredService<DmOffice.Core.UserStore>();
        var config = services.GetRequiredService<DmOffice.Core.ServerConfig>();
        var spaces = services.GetRequiredService<DmOffice.Core.Spaces>();
        DmOffice.Dav.FileStore.Put(_fx.Db, "ziel-probe.txt", Encoding.UTF8.GetBytes("Ziel " + Guid.NewGuid()), "text/plain");
        var folder = Directory.CreateTempSubdirectory("dmziel").FullName;
        using var http = _fx.Client();
        try
        {
            foreach (var remote in new DmOffice.Core.IBlobRemote[]
                     {
                         new DriverRemote(new FolderDriver(folder)),
                         new DriverRemote(new WebDavDriver($"http://localhost/dav/files/{FilesServerFixture.Login}/Sicherungsziel/", FilesServerFixture.Login, _fx.AppPassword, http)),
                     })
            {
                await DmOffice.Core.S3Mirror.ProbeAsync(remote, "", CancellationToken.None);
                // Kopie der Inhalte
                var mirror = new DmOffice.Core.S3Mirror(spaces, config, paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<DmOffice.Core.S3Mirror>.Instance);
                await mirror.PassAsync(remote, CancellationToken.None);
                Assert.Null(mirror.Status.LastError);
                Assert.True(mirror.Status.Mirrored > 0 && mirror.Status.Mirrored == mirror.Status.Local);
                // Sicherung der Datenbanken
                var backup = new DmOffice.Core.S3Backup(paths, vault, users, config, services, Microsoft.Extensions.Logging.Abstractions.NullLogger<DmOffice.Core.S3Backup>.Instance);
                await backup.RunAsync(remote, CancellationToken.None);
                Assert.Null(backup.Status.LastError);
                var stand = Assert.Single(await DmOffice.Core.S3Backup.ListAsync(remote, config.Current.S3Prefix, CancellationToken.None));
                // Zurueck in ein leeres Verzeichnis
                var target = Directory.CreateTempSubdirectory("dmziel-zurueck").FullName;
                try
                {
                    var manifest = await DmOffice.Core.S3Backup.RestoreAsync(new DmOffice.Core.DataPaths(target), vault, remote, stand, CancellationToken.None);
                    Assert.Contains(manifest.Users, u => u.Login == FilesServerFixture.Login);
                }
                finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(target, recursive: true); }
                await remote.DeleteAsync(stand.Key, CancellationToken.None);
                remote.Dispose();
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
