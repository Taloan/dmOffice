using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>
/// Belegung des Storage Extender (Minimal, Optimal, Maximal) und Speicherbegrenzung: Insgesamt stehen lokaler Speicherpool und
/// Storage Extender zur Verfügung; Kopien entstehen je nach Stufe, entfernt wird nur, was auch lokal liegt.
/// </summary>
[Collection("server")]
public class S3MirrorUsageTests : IClassFixture<ChatFixture>, IDisposable
{
    private const double Gb = 1024.0 * 1024 * 1024;
    private readonly ChatFixture _fx;
    private readonly List<string> _featuresBefore = Features.InReleaseOff.ToList();

    public S3MirrorUsageTests(ChatFixture fx)
    {
        _fx = fx;
        Features.Set(_featuresBefore.Where(x => x != Features.ExtenderUsage));
    }

    public void Dispose() => Features.Set(_featuresBefore);

    private (ServerConfig Config, S3Mirror Mirror) Setup()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        return (config, new S3Mirror(services.GetRequiredService<Spaces>(), config, services.GetRequiredService<DataPaths>(), NullLogger<S3Mirror>.Instance));
    }

    private static List<string> PutOld(UserSpace space, string name, int n)
    {
        var ids = new List<string>();
        for (var i = 0; i < n; i++)
        {
            DmOffice.Dav.FileStore.Put(space.Db, $"{name}-{i}.bin", System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid() + new string('u', 6000)), "application/octet-stream");
            var id = space.Db.Query($"SELECT blob FROM files WHERE path='{name}-{i}.bin'", r => r.GetString(0)).Single();
            File.SetLastAccessTimeUtc(space.Db.Blobs.PathOf(id), DateTime.UtcNow.AddDays(-30 - i));
            File.SetLastWriteTimeUtc(space.Db.Blobs.PathOf(id), DateTime.UtcNow.AddDays(-30 - i));
            ids.Add(id);
        }
        space.Db.Exec($"DELETE FROM text_queue WHERE kind = 'file' AND ref LIKE '{name}-%'");
        return ids;
    }

    private static bool AtTarget(string root, string id) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(f => f.EndsWith(id));

    [Fact]
    public void Ohne_Schalter_wie_bisher()
    {
        Features.Set(_featuresBefore.Append(Features.ExtenderUsage));
        var s = new ServerSettings { S3Usage = S3Mirror.UsageMin, S3SizeGb = 5 };
        Assert.Equal(S3Mirror.UsageMax, S3Mirror.UsageOf(s));
        Assert.Equal(long.MaxValue, S3Mirror.SizeLimit(s));
        Features.Set(_featuresBefore.Where(x => x != Features.ExtenderUsage));
        Assert.Equal(S3Mirror.UsageMin, S3Mirror.UsageOf(s));
        Assert.Equal(5L * 1024 * 1024 * 1024, S3Mirror.SizeLimit(s));
        // Nicht gesetzt: Maximal und unbegrenzt - so verhielt sich der Storage Extender bisher
        Assert.Equal(S3Mirror.UsageMax, S3Mirror.UsageOf(new ServerSettings()));
        Assert.Equal(long.MaxValue, S3Mirror.SizeLimit(new ServerSettings()));
    }

    [Fact]
    public async Task Optimal_uebertraegt_erst_beim_Bereinigen_Minimal_entfernt_Kopien_nach_der_Frist()
    {
        var (config, mirror) = Setup();
        var space = _fx.Space("ben");
        var ids = PutOld(space, "opt", 3);
        var root = Directory.CreateTempSubdirectory("dms3u").FullName;
        var remote = new S3MirrorTests.FolderRemote(root);
        var before = config.Current;
        try
        {
            S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 500_000_000_000);
            config.Update(s => { s.S3Mirror = true; s.S3Usage = S3Mirror.UsageOpt; s.S3SizeGb = null; s.S3CacheLimited = false; s.S3MinFreeGb = 1; });
            // Lokal ist genug Platz: Optimal überträgt nichts
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.All(ids, id => Assert.False(AtTarget(root, id)));
            Assert.All(ids, id => Assert.True(space.Db.Blobs.Exists(id)));

            // Der lokale Speicherpool wird klein: Die ältesten Dateien werden übertragen und danach lokal bereinigt
            config.Update(s => { s.S3CacheLimited = true; s.S3CacheTargetGb = 0.000001; });
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.All(ids, id => Assert.True(AtTarget(root, id)));
            Assert.All(ids, id => Assert.False(space.Db.Blobs.Exists(id)));
            Assert.True(mirror.Status.Cache!.RemoteBytes > 0);

            // Wieder Platz: Auffüllen holt sie zurück, die Kopien bleiben (kein Hin und Her)
            config.Update(s => s.S3CacheLimited = false);
            await mirror.PrefillAsync(remote, TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.All(ids, id => Assert.True(space.Db.Blobs.Exists(id)));
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.All(ids, id => Assert.True(AtTarget(root, id)));
            Assert.Equal(0, mirror.Status.Cache!.ToRemove);

            // Minimal: Kopien von Dateien, die wieder lokal liegen, werden vorgemerkt und erst nach der Frist entfernt
            config.Update(s => s.S3Usage = S3Mirror.UsageMin);
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.All(ids, id => Assert.True(AtTarget(root, id)));
            Assert.True(mirror.Status.Cache!.ToRemove > 0);
            var surplus = Path.Combine(space.Db.Blobs.Dir, S3Mirror.SurplusFile);
            var marked = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(surplus))!;
            Assert.All(ids, id => Assert.Contains(marked.Keys, k => k.EndsWith(id)));
            File.WriteAllText(surplus, System.Text.Json.JsonSerializer.Serialize(marked.ToDictionary(x => x.Key, _ => DateTime.UtcNow.AddDays(-8))));
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.All(ids, id => Assert.False(AtTarget(root, id)));
            Assert.All(ids, id => Assert.True(space.Db.Blobs.Exists(id)));

            // Zurück auf Optimal: Vorgemerktes verfällt, nichts mehr zu entfernen
            config.Update(s => s.S3Usage = S3Mirror.UsageOpt);
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.False(File.Exists(surplus));
        }
        finally
        {
            S3Mirror.VolumeOverride = null;
            config.Update(s => { s.S3Mirror = before.S3Mirror; s.S3Usage = before.S3Usage; s.S3SizeGb = before.S3SizeGb; s.S3CacheLimited = before.S3CacheLimited; s.S3CacheTargetGb = before.S3CacheTargetGb; s.S3MinFreeGb = before.S3MinFreeGb; });
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Strikt_entfernt_Kopien_sofort()
    {
        var (config, mirror) = Setup();
        var space = _fx.Space("ben");
        var ids = PutOld(space, "strict", 3);
        var root = Directory.CreateTempSubdirectory("dms3s").FullName;
        var remote = new S3MirrorTests.FolderRemote(root);
        var before = config.Current;
        try
        {
            S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 500_000_000_000);
            // Maximal: alles liegt an beiden Orten
            config.Update(s => { s.S3Mirror = true; s.S3Usage = S3Mirror.UsageMax; s.S3SizeGb = null; s.S3CacheLimited = false; s.S3MinFreeGb = 1; });
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.All(ids, id => Assert.True(AtTarget(root, id)));

            // Minimal (strict): Kopien von Dateien, die hier liegen, sind nach dem nächsten Abgleich weg - ohne Frist
            config.Update(s => s.S3Usage = S3Mirror.UsageStrict);
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.All(ids, id => Assert.False(AtTarget(root, id)));
            Assert.All(ids, id => Assert.True(space.Db.Blobs.Exists(id)));
            Assert.Equal(S3Mirror.UsageStrict, S3Mirror.UsageOf(config.Current));
        }
        finally
        {
            S3Mirror.VolumeOverride = null;
            config.Update(s => { s.S3Mirror = before.S3Mirror; s.S3Usage = before.S3Usage; s.S3SizeGb = before.S3SizeGb; s.S3CacheLimited = before.S3CacheLimited; s.S3CacheTargetGb = before.S3CacheTargetGb; s.S3MinFreeGb = before.S3MinFreeGb; });
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Maximal_spiegelt_bis_zur_Speicherbegrenzung_und_haelt_sie_ein()
    {
        var (config, mirror) = Setup();
        var space = _fx.Space("cleo");
        var ids = PutOld(space, "max", 4);
        var root = Directory.CreateTempSubdirectory("dms3m").FullName;
        var remote = new S3MirrorTests.FolderRemote(root);
        var before = config.Current;
        try
        {
            S3Mirror.VolumeOverride = _ => (1_000_000_000_000, 500_000_000_000);
            config.Update(s => { s.S3Mirror = true; s.S3Usage = S3Mirror.UsageMax; s.S3SizeGb = null; s.S3CacheLimited = false; s.S3MinFreeGb = 1; });
            // Unbegrenzt: alles wird übertragen (wie bisher)
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.All(ids, id => Assert.True(AtTarget(root, id)));
            var all = mirror.Status.Cache!.UsedRemote;
            Assert.False(mirror.Status.Cache.Full);

            // Verkleinert: Kopien von Dateien, die lokal liegen, werden entfernt, bis die Begrenzung eingehalten ist; lokal bleibt alles
            var cap = all / 2;
            config.Update(s => s.S3SizeGb = cap / Gb);
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.True(mirror.Status.Cache!.UsedRemote <= cap);
            Assert.True(mirror.Status.Cache.Cap <= cap + 1);
            Assert.All(ids, id => Assert.True(space.Db.Blobs.Exists(id)));
            // Neue Datei: Bei voller Begrenzung bekommt sie keine Kopie auf Kosten einer anderen
            var neu = PutOld(space, "max-neu", 1)[0];
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.True(mirror.Status.Cache!.UsedRemote <= cap);

            // Lokal wird es eng: Was keine Kopie hat, wird übertragen; dafür weicht die Kopie einer zuletzt verwendeten Datei
            config.Update(s => { s.S3CacheLimited = true; s.S3CacheTargetGb = (mirror.Status.Cache!.Bytes - 1) / Gb; });
            await mirror.PassAsync(remote, CancellationToken.None);
            Assert.True(mirror.Status.Cache!.Evicted >= 1);
            Assert.True(mirror.Status.Cache.UsedRemote <= cap);
            Assert.True(mirror.Status.Cache.RemoteBytes > 0);
            _ = neu;
        }
        finally
        {
            S3Mirror.VolumeOverride = null;
            config.Update(s => { s.S3Mirror = before.S3Mirror; s.S3Usage = before.S3Usage; s.S3SizeGb = before.S3SizeGb; s.S3CacheLimited = before.S3CacheLimited; s.S3CacheTargetGb = before.S3CacheTargetGb; s.S3MinFreeGb = before.S3MinFreeGb; });
            Directory.Delete(root, recursive: true);
        }
    }
}
