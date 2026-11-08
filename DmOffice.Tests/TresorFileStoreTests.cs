using System.Security.Cryptography;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>
/// Schnittstelle „Tresor-Dateien“ (docs/tresor-cryptomator-konzept.md, 3.4): <see cref="TresorFileStore"/> gibt Ordner, Inhalte,
/// Umbenennen, Verschieben und Löschen mit Klartext-Pfaden an den Speicher weiter - Grundlage für den Cryptomator-Speicher.
/// </summary>
public class TresorFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tfs-" + Guid.NewGuid().ToString("N"));

    /// <summary>Speicher mit eigenem Baum (wie Cryptomator): merkt sich Inhalte nach Pfad und alle Aufrufe.</summary>
    private sealed class TreeStorage : ITresorStorage
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly HashSet<string> Dirs = [""];
        public readonly List<string> Calls = [];

        public async Task<(string? Ref, long Size)> WriteAsync(string path, Stream data, long maxLength, Func<Exception>? tooLarge, CancellationToken ct)
        {
            var m = new MemoryStream();
            await data.CopyToAsync(m, ct);
            if (m.Length > maxLength) throw tooLarge?.Invoke() ?? new IOException();
            Files[path] = m.ToArray();
            Calls.Add("write " + path);
            return (null, m.Length);
        }

        public Stream OpenRead(string path, string? reference) => new MemoryStream(Files[path]);

        public void Delete(string path, string? reference, bool isDir)
        {
            Calls.Add("delete " + path);
            if (!isDir) { Files.Remove(path); return; }
            foreach (var k in Files.Keys.Where(k => k.StartsWith(path + "/")).ToList()) Files.Remove(k);
            Dirs.RemoveWhere(d => d == path || d.StartsWith(path + "/"));
        }

        public void CreateFolder(string path) { Calls.Add("mkdir " + path); Dirs.Add(path); }

        public void Move(string from, string to, bool isDir)
        {
            Calls.Add($"move {from} -> {to}");
            foreach (var k in Files.Keys.Where(k => k == from || k.StartsWith(from + "/")).ToList())
            {
                Files[to + k[from.Length..]] = Files[k];
                Files.Remove(k);
            }
        }

        public void Release(string? reference) { }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private (TresorDb Db, TresorFileStore Files, TreeStorage Storage) Open()
    {
        Directory.CreateDirectory(_dir);
        var db = new TresorDb(Path.Combine(_dir, "t.db"), RandomNumberGenerator.GetBytes(32), Path.Combine(_dir, "blobs"));
        var storage = new TreeStorage();
        return (db, new TresorFileStore(db, storage), storage);
    }

    private static MemoryStream Text(string s) => new(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Baum_und_Inhalt_gehen_mit_Klartext_Pfaden_an_den_Speicher()
    {
        var (db, files, storage) = Open();
        using var _ = db;
        var a = files.EnsureFolder("Dokumente/Steuer");
        var (id, name) = await files.CreateAsync(a, "Bescheid.pdf", Text("eins"), "application/pdf", default);
        Assert.Equal("Bescheid.pdf", name);
        Assert.Equal("eins", new StreamReader(files.OpenRead(id)).ReadToEnd());
        // Gleicher Name: freier Name
        var (id2, name2) = await files.CreateAsync(a, "Bescheid.pdf", Text("zwei"), null, default);
        Assert.Equal("Bescheid (2).pdf", name2);
        Assert.Contains("write Dokumente/Steuer/Bescheid (2).pdf", storage.Calls);

        // Ersetzen: neuer Stand, gleicher Ort
        var before = files.Find(id)!.Modified;
        var stamp = await files.ReplaceAsync(id, Text("neu"), null, null, default);
        Assert.True(stamp > before);
        Assert.Equal("neu", System.Text.Encoding.UTF8.GetString(storage.Files["Dokumente/Steuer/Bescheid.pdf"]));

        // Umbenennen und Verschieben eines Ordners: der Speicher zieht den Baum mit
        var archive = files.CreateFolder(0, "Archiv").Id;
        files.Move(a, archive, "Steuer 2025");
        Assert.Equal("Archiv/Steuer 2025/Bescheid.pdf", files.PathOf(id));
        Assert.Contains("move Dokumente/Steuer -> Archiv/Steuer 2025", storage.Calls);
        Assert.Equal("neu", new StreamReader(files.OpenRead(id)).ReadToEnd());
        Assert.Throws<ArgumentException>(() => files.Move(archive, files.Find(a)!.Id, null));

        // Löschen eines Ordners: einmal an den Speicher, alle Zeilen weg
        files.Delete(archive);
        Assert.Contains("delete Archiv", storage.Calls);
        Assert.Null(files.Find(id));
        Assert.Null(files.Find(id2));
        Assert.DoesNotContain(storage.Files.Keys, k => k.StartsWith("Archiv/"));
    }

    [Fact]
    public async Task Heutiger_Speicher_raeumt_Blobs_beim_Ersetzen_und_Loeschen()
    {
        Directory.CreateDirectory(_dir);
        using var db = new TresorDb(Path.Combine(_dir, "b.db"), RandomNumberGenerator.GetBytes(32), Path.Combine(_dir, "blobs"));
        var files = db.Files;
        var (id, _) = await files.CreateAsync(0, "a.txt", Text("alt"), null, default);
        var oldBlob = files.Find(id)!.Blob!;
        await files.ReplaceAsync(id, Text("neu"), null, null, default);
        Assert.False(db.Blobs.Exists(oldBlob));
        var newBlob = files.Find(id)!.Blob!;
        files.Delete(id);
        Assert.False(db.Blobs.Exists(newBlob));
    }
}
