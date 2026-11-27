using DmOffice.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>Alte Daten am bisherigen Ziel: merken beim Ortswechsel, nie löschen, was noch genutzt wird, nur unter dem Präfix löschen.</summary>
public class FormerTargetsTests
{
    [Fact]
    public void Nur_ein_anderer_Ort_wird_gemerkt_und_Ueberschneidungen_zaehlen_als_genutzt()
    {
        var dir = Directory.CreateTempSubdirectory("dmformer");
        var config = new ServerConfig(new DataPaths(Path.Combine(dir.FullName, "data")));
        config.Update(s => { s.StorageTarget = "s3"; s.S3Endpoint = "https://s3.example.org"; s.S3Bucket = "dm"; s.S3Prefix = "a"; s.S3AccessKey = "k"; s.S3Mirror = true; });
        // Nur neue Zugangsdaten, gleicher Ort: nichts gemerkt
        var before = config.Current;
        config.Update(s => s.S3AccessKey = "k2");
        FormerTargets.Track(config, "storage", before);
        Assert.Empty(config.Current.FormerTargets);
        // Anderer Bucket: gemerkt
        before = config.Current;
        config.Update(s => s.S3Bucket = "neu");
        FormerTargets.Track(config, "storage", before);
        var f = Assert.Single(config.Current.FormerTargets);
        Assert.Equal(("storage", "dm", "a", "k2"), (f.Purpose, f.Bucket, f.Prefix, f.AccessKey));
        Assert.False(FormerTargets.StillUsed(f, config.Current));
        // Das Core-Backup nutzt jetzt den alten Bucket mit Unterordner des alten Präfixes: überschneidet sich → genutzt
        config.Update(s => { s.BackupOwnTarget = true; s.BackupTarget = "s3"; s.BackupS3Endpoint = "https://s3.example.org/"; s.BackupS3Bucket = "DM"; s.BackupS3Prefix = "a/backup"; });
        Assert.True(FormerTargets.StillUsed(f, config.Current));
        config.Update(s => s.BackupS3Prefix = "b");
        Assert.False(FormerTargets.StillUsed(f, config.Current));
    }

    [Fact]
    public async Task Loeschen_entfernt_nur_den_Praefix_und_vergisst_das_Ziel()
    {
        var dir = Directory.CreateTempSubdirectory("dmformer");
        var config = new ServerConfig(new DataPaths(Path.Combine(dir.FullName, "data")));
        var a = Directory.CreateDirectory(Path.Combine(dir.FullName, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(dir.FullName, "b")).FullName;
        Directory.CreateDirectory(Path.Combine(a, "dm", "blobs", "x"));
        File.WriteAllText(Path.Combine(a, "dm", "blobs", "x", "1"), "eins");
        File.WriteAllText(Path.Combine(a, "dm", "core.dmb"), "zwei");
        Directory.CreateDirectory(Path.Combine(a, "dm2"));
        File.WriteAllText(Path.Combine(a, "dm2", "fremd"), "bleibt");
        File.WriteAllText(Path.Combine(a, "fremd.txt"), "bleibt");
        config.Update(s => { s.StorageTarget = "folder"; s.StorageFolder = a; s.S3Prefix = "dm"; s.S3Mirror = true; });
        var before = config.Current;
        config.Update(s => s.StorageFolder = b);
        FormerTargets.Track(config, "storage", before);
        var f = Assert.Single(config.Current.FormerTargets);
        var (count, bytes) = await FormerTargets.CountAsync(config, f, default);
        Assert.Equal((2L, 8L), (count, bytes));

        Assert.True(FormerTargets.StartDelete(config, f, "admin", NullLogger.Instance));
        for (var i = 0; i < 100 && FormerTargets.RunOf(f.Id) is { Busy: true }; i++) await Task.Delay(50);
        Assert.Null(FormerTargets.RunOf(f.Id)!.Error);
        Assert.False(Directory.Exists(Path.Combine(a, "dm", "blobs")));
        Assert.False(File.Exists(Path.Combine(a, "dm", "core.dmb")));
        Assert.True(File.Exists(Path.Combine(a, "dm2", "fremd")));
        Assert.True(File.Exists(Path.Combine(a, "fremd.txt")));
        Assert.Empty(config.Current.FormerTargets);
    }

    [Fact]
    public void Wird_der_Ort_wieder_genutzt_lehnt_Loeschen_ab()
    {
        var dir = Directory.CreateTempSubdirectory("dmformer");
        var config = new ServerConfig(new DataPaths(Path.Combine(dir.FullName, "data")));
        config.Update(s => { s.StorageTarget = "folder"; s.StorageFolder = "/x/a"; s.S3Mirror = true; });
        var before = config.Current;
        config.Update(s => s.StorageFolder = "/x/b");
        FormerTargets.Track(config, "storage", before);
        var f = Assert.Single(config.Current.FormerTargets);
        config.Update(s => s.StorageFolder = "/x/a");
        Assert.Throws<InvalidOperationException>(() => FormerTargets.StartDelete(config, f, "admin", NullLogger.Instance));
    }

    [Fact]
    public async Task Ein_nie_genutztes_Ziel_ist_kein_Altbestand()
    {
        var dir = Directory.CreateTempSubdirectory("dmformer");
        var config = new ServerConfig(new DataPaths(Path.Combine(dir.FullName, "data")));
        // Ordner, Storage Extender nie eingeschaltet: nichts gemerkt
        config.Update(s => { s.StorageTarget = "folder"; s.StorageFolder = dir.FullName; });
        var before = config.Current;
        config.Update(s => s.StorageFolder = "/x/b");
        FormerTargets.Track(config, "storage", before);
        // S3, das nie erreichbar war: nichts gemerkt (Nachzählen scheitert)
        config.Update(s => { s.StorageTarget = "s3"; s.S3Endpoint = "http://127.0.0.1:9"; s.S3Bucket = "dm"; s.S3AccessKey = "k"; });
        before = config.Current;
        config.Update(s => s.S3Bucket = "neu");
        FormerTargets.Track(config, "storage", before);
        await Task.Delay(3000);
        Assert.Empty(config.Current.FormerTargets);
    }
}
