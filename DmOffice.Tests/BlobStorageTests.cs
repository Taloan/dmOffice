using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>Dateiinhalte als einzeln verschluesselte Blobs neben der Datenbank (BlobStore): Verschluesselung, Streams,
/// Auslagern alter Inhalte, Aufraeumen ohne Verweis, Kontingent.</summary>
public class BlobStorageTests
{
    private static UserDb NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("dmblobs");
        return new UserDb(Path.Combine(dir.FullName, "u.db"), RandomNumberGenerator.GetBytes(32), "blobs-" + Guid.NewGuid().ToString("N"));
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static List<string> BlobFiles(UserDb db) =>
        db.Blobs.List().Select(b => b.Id).ToList();

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public void Blob_enthaelt_keinen_Klartext_und_Veraenderung_faellt_auf()
    {
        var db = NewDb();
        var secret = B("Kontonummer DE02 1203 0000 0000 2020 51 - streng geheim. ");
        var data = Enumerable.Range(0, 3000).SelectMany(_ => secret).ToArray();   // mehrere Bloecke
        FileStore.Put(db, "geheim.txt", data, "text/plain");
        var id = Assert.Single(BlobFiles(db));
        var onDisk = File.ReadAllBytes(db.Blobs.PathOf(id));
        Assert.False(Contains(onDisk, B("Kontonummer")));
        Assert.False(Contains(onDisk, B("geheim")));
        Assert.True(onDisk.Length > data.Length);
        // Nirgends im Ordner Klartext (auch keine halbfertigen Dateien)
        foreach (var f in Directory.EnumerateFiles(db.Blobs.Dir, "*", SearchOption.AllDirectories))
            Assert.False(Contains(File.ReadAllBytes(f), B("Kontonummer")));
        Assert.Equal(data, FileStore.Data(db, "geheim.txt"));

        // Gleicher Inhalt, andere Datei: anderer Zufall, anderer Geheimtext
        FileStore.Put(db, "kopie.txt", data, "text/plain");
        var other = BlobFiles(db).Single(b => b != id);
        Assert.NotEqual(onDisk.Skip(BlobStore.HeaderSize).Take(64), File.ReadAllBytes(db.Blobs.PathOf(other)).Skip(BlobStore.HeaderSize).Take(64));

        // Ein Bit gekippt: der Block laesst sich nicht mehr lesen
        var tampered = (byte[])onDisk.Clone();
        tampered[BlobStore.HeaderSize + 70_000] ^= 1;
        File.WriteAllBytes(db.Blobs.PathOf(id), tampered);
        Assert.ThrowsAny<CryptographicException>(() => FileStore.Data(db, "geheim.txt"));
        // Abgeschnitten nach einem ganzen Block: der neue letzte Block war nicht als letzter verschluesselt
        File.WriteAllBytes(db.Blobs.PathOf(id), onDisk.Take(BlobStore.HeaderSize + BlobStore.ChunkSize + 16).ToArray());
        Assert.ThrowsAny<CryptographicException>(() => FileStore.Data(db, "geheim.txt"));
        // Mit fremdem Schluessel nicht lesbar
        File.WriteAllBytes(db.Blobs.PathOf(id), onDisk);
        var foreign = new BlobStore(db.Blobs.Dir, RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => foreign.ReadAll(id));
        Assert.Equal(data, FileStore.Data(db, "geheim.txt"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65_535)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(200_000)]
    public void Lesen_an_beliebiger_Stelle_ueber_Blockgrenzen(int size)
    {
        var db = NewDb();
        var data = RandomNumberGenerator.GetBytes(size);
        FileStore.Put(db, "x.bin", data, null);
        using var s = FileStore.OpenRead(db, "x.bin");
        Assert.True(s.CanSeek);
        Assert.Equal(size, s.Length);
        foreach (var at in new[] { 0, size / 2, Math.Max(0, size - 3), Math.Max(0, 65_530) }.Where(a => a < size))
        {
            s.Position = at;
            var buf = new byte[Math.Min(20, size - at)];
            s.ReadExactly(buf);
            Assert.Equal(data.Skip(at).Take(buf.Length), buf);
        }
        s.Position = size;
        Assert.Equal(0, s.Read(new byte[10]));
        Assert.Equal(FileStore.Get(db, "x.bin")!.Etag, "\"" + Convert.ToHexString(SHA256.HashData(data))[..24].ToLowerInvariant() + "\"");
    }

    [Fact]
    public void Leere_Datei_braucht_keinen_Blob()
    {
        var db = NewDb();
        FileStore.Put(db, "leer.txt", [], null);
        Assert.Empty(BlobFiles(db));
        Assert.Empty(FileStore.Data(db, "leer.txt")!);
        // Windows legt erst leer an, dann kommt der Inhalt
        FileStore.Put(db, "leer.txt", B("jetzt"), null);
        Assert.Single(BlobFiles(db));
    }

    [Fact]
    public void Versionen_Papierkorb_und_Kopien_teilen_Blobs_und_der_letzte_Verweis_raeumt_auf()
    {
        var db = NewDb();
        FileStore.Put(db, "a.txt", B("eins"), null);
        FileStore.Put(db, "a.txt", B("zwei"), null);          // "eins" wird Version - derselbe Blob
        Assert.Equal(2, BlobFiles(db).Count);
        FileStore.MoveOrCopy(db, "a.txt", "b.txt", false, copy: true);
        Assert.Equal(2, BlobFiles(db).Count);                 // Kopie ohne Duplikat
        Assert.Equal("zwei", Encoding.UTF8.GetString(FileStore.Data(db, "b.txt")!));

        var fileId = FileStore.Get(db, "a.txt")!.FileId;
        var version = Assert.Single(FileStore.Versions(db, fileId));
        FileStore.RestoreVersion(db, "a.txt", version.Id);    // "eins" wieder aktuell, "zwei" als Version - nichts kopiert
        Assert.Equal(2, BlobFiles(db).Count);
        Assert.Equal("eins", Encoding.UTF8.GetString(FileStore.Data(db, "a.txt")!));

        FileStore.Delete(db, "a.txt");                         // Papierkorb haelt den Blob
        Assert.Equal(2, BlobFiles(db).Count);
        FileStore.PurgeTrash(db, null);                        // Versionen gehen mit (Orphans) - "zwei" haelt noch b.txt
        Assert.Equal("zwei", Encoding.UTF8.GetString(FileStore.Data(db, "b.txt")!));
        Assert.Single(BlobFiles(db));
        FileStore.Delete(db, "b.txt");
        FileStore.PurgeTrash(db, null);
        Assert.Empty(BlobFiles(db));
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM blob_gone"));
    }

    [Fact]
    public void Aufbewahrung_und_Ablauf_loeschen_die_Blobs_mit()
    {
        var db = NewDb();
        for (int i = 0; i < 5; i++) FileStore.Put(db, "t.txt", B("fassung " + i), null);
        Assert.Equal(5, BlobFiles(db).Count);
        var s = FileRetention.Load(db);
        FileRetention.Save(db, s with { VersionsMode = "off" });
        FileRetention.Apply(db, DateTime.UtcNow);
        Assert.Single(BlobFiles(db));

        FileStore.Delete(db, "t.txt");
        Assert.Single(BlobFiles(db));
        FileStore.Expire(db, 30, DateTime.UtcNow.AddDays(31));
        Assert.Empty(BlobFiles(db));
    }

    [Fact]
    public void Notizi_Ordner_loescht_endgueltig_samt_Blob()
    {
        var db = NewDb();
        db.SetJson("settings", new UserSettings { Notizi = new NotiziSettings { Root = "apps/notizi" } });
        FileStore.EnsureDir(db, "apps/notizi");
        FileStore.Put(db, "apps/notizi/db.bin", B("1"), null);
        FileStore.Put(db, "apps/notizi/db.bin", B("2"), null);  // keine Version: alter Blob gleich weg
        Assert.Single(BlobFiles(db));
        FileStore.Delete(db, "apps/notizi/db.bin");
        Assert.Empty(BlobFiles(db));
    }

    [Fact]
    public void Verwaiste_Blob_Dateien_raeumt_der_Durchgang_weg_aber_nur_alte()
    {
        var db = NewDb();
        FileStore.Put(db, "bleibt.txt", B("bleibt"), null);
        var keep = Assert.Single(BlobFiles(db));
        var stray = db.Blobs.Write(B("abgestuerzt vor dem Eintragen")).Id!;
        Assert.Equal(0, FileStore.SweepBlobs(db, TimeSpan.FromHours(1)));   // zu jung - koennte gerade eingetragen werden
        File.SetLastWriteTimeUtc(db.Blobs.PathOf(stray), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(db.Blobs.PathOf(keep), DateTime.UtcNow.AddDays(-2));
        Assert.Equal(1, FileStore.SweepBlobs(db, TimeSpan.FromHours(1)));
        Assert.Equal([keep], BlobFiles(db));
        Assert.Equal("bleibt", Encoding.UTF8.GetString(FileStore.Data(db, "bleibt.txt")!));
    }

    /// <summary>Liefert Bytes, ohne sie vorher zu kennen - kein Seek, keine Laenge (wie ein Upload).</summary>
    private sealed class Generated(long length) : Stream
    {
        private long _pos;
        public readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, length - _pos);
            for (int i = 0; i < n; i++) buffer[i] = (byte)((_pos + i) * 7 % 251);
            Hash.AppendData(buffer[..n]);
            _pos += n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer, offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Grosse_Datei_geht_als_Stream_ohne_sie_im_Speicher_zu_halten()
    {
        var db = NewDb();
        const long size = 96L * 1024 * 1024;
        var source = new Generated(size);
        // Alles laeuft hier synchron auf diesem Thread (Quelle und Blob schreiben ohne echtes Warten) - so laesst sich zaehlen
        var before = GC.GetAllocatedBytesForCurrentThread();
        var (etag, created) = FileStore.PutAsync(db, "film.mp4", source, "video/mp4").GetAwaiter().GetResult();
        var written = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(created);
        Assert.True(written < 8 * 1024 * 1024, $"Beim Schreiben {written / 1024 / 1024} MB belegt");
        Assert.Equal(size, FileStore.Get(db, "film.mp4")!.Size);
        Assert.Equal("\"" + Convert.ToHexString(source.Hash.GetHashAndReset())[..24].ToLowerInvariant() + "\"", etag);

        before = GC.GetAllocatedBytesForCurrentThread();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var s = FileStore.OpenRead(db, "film.mp4"))
        {
            var buf = new byte[256 * 1024];
            int n;
            while ((n = s.Read(buf)) > 0) hash.AppendData(buf, 0, n);
        }
        var read = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(read < 8 * 1024 * 1024, $"Beim Lesen {read / 1024 / 1024} MB belegt");
        Assert.Equal(etag, "\"" + Convert.ToHexString(hash.GetHashAndReset())[..24].ToLowerInvariant() + "\"");
    }

    [Fact]
    public async Task Upload_ueber_der_Grenze_bricht_ab_und_hinterlaesst_nichts()
    {
        var db = NewDb();
        var before = FileStore.QuotaOf;
        FileStore.QuotaOf = u => u == db.UserId ? UserStorage.Database(db) + 1_000_000 : before?.Invoke(u) ?? 0;
        try
        {
            FileStore.Put(db, "a.bin", new byte[400_000], null);
            var source = new Generated(5_000_000);
            await Assert.ThrowsAsync<QuotaExceededException>(() => FileStore.PutAsync(db, "b.bin", source, null));
            // Abgebrochen, sobald es zu viel war - nicht erst nach dem ganzen Upload
            Assert.True(source.Position < 1_000_000);
            Assert.Null(FileStore.Get(db, "b.bin"));
            Assert.Single(BlobFiles(db));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(db.Blobs.Dir, "tmp")));
            // Ersetzen zaehlt die alte Groesse mit
            await FileStore.PutAsync(db, "a.bin", new Generated(900_000), null);
            Assert.Equal(900_000, FileStore.Usage(db));
        }
        finally { FileStore.QuotaOf = before; }
    }

    [Fact]
    public async Task Bedingungen_greifen_vor_dem_Upload_und_hinterlassen_keinen_Blob()
    {
        var db = NewDb();
        FileStore.Put(db, "a.txt", B("a"), null);
        var source = new Generated(1_000_000);
        await Assert.ThrowsAsync<Pim.PreconditionFailedException>(() => FileStore.PutAsync(db, "a.txt", source, null, ifNoneMatch: "*"));
        await Assert.ThrowsAsync<DavConflictException>(() => FileStore.PutAsync(db, "fehlt/a.txt", source, null));
        Assert.Equal(0, source.Position);
        Assert.Single(BlobFiles(db));
        // Name inzwischen belegt (Freigabelink): bekommt eine Nummer, der Blob bleibt
        var path = await FileStore.PutNewAsync(db, "a.txt", new MemoryStream(B("neu")), null);
        Assert.Equal("a (2).txt", path);
        Assert.Equal("neu", Encoding.UTF8.GetString(FileStore.Data(db, path)!));
    }

    // ---------------- Auslagern alter Inhalte ----------------

    /// <summary>Wie vor Version 16: Inhalt in der Spalte data.</summary>
    private static void Legacy(UserDb db, string path, byte[] data)
    {
        var now = DateTime.UtcNow.ToString("o");
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(data))[..24].ToLowerInvariant() + "\"";
        db.Exec("INSERT INTO files(path,parent,is_dir,data,size,etag,mime,created,modified) VALUES($p,$par,0,$d,$s,$e,NULL,$n,$n)",
            ("$p", path), ("$par", FileStore.Parent(path)), ("$d", data), ("$s", data.LongLength), ("$e", etag), ("$n", now));
    }

    [Fact]
    public void Alte_Inhalte_werden_schrittweise_ausgelagert_und_bleiben_lesbar()
    {
        var db = NewDb();
        var big = RandomNumberGenerator.GetBytes(300_000);
        Legacy(db, "alt.bin", big);
        Legacy(db, "klein.txt", B("klein"));
        Legacy(db, "leer.txt", []);
        var fileId = FileStore.Get(db, "alt.bin")!.FileId;
        db.Exec("INSERT INTO file_versions(fileid,size,etag,modified,saved,data) VALUES($f,3,'\"v\"',$n,$n,$d)",
            ("$f", fileId), ("$n", DateTime.UtcNow.ToString("o")), ("$d", B("alt")));
        db.Exec("INSERT INTO file_thumbs(fileid,size,etag,data) VALUES($f,256,'\"t\"',$d)", ("$f", fileId), ("$d", B("webp")));
        db.Exec("INSERT INTO file_thumbs(fileid,size,etag,data) VALUES($f,1600,'\"t\"',X'')", ("$f", fileId));   // misslungen
        // Papierkorb mit altem Inhalt
        Legacy(db, "weg.txt", B("im Papierkorb"));
        FileStore.Delete(db, "weg.txt");
        // Vor dem Auslagern: alles ueber die Datenbank lesbar, auch als Stream mit Bereichen
        using (var s = FileStore.OpenRead(db, "alt.bin"))
        {
            s.Position = 100_000;
            var buf = new byte[16];
            s.ReadExactly(buf);
            Assert.Equal(big.Skip(100_000).Take(16), buf);
        }
        Assert.Equal(6, FileStore.PendingBlobs(db));   // 3 Dateien + Version + Vorschaubild + Papierkorb

        // Unterbrochen nach zwei Zeilen, dann weiter
        Assert.Equal(2, FileStore.MigrateBlobs(db, max: 2));
        Assert.Equal(4, FileStore.PendingBlobs(db));
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => FileStore.MigrateBlobs(db, cts.Token));
        }
        FileStore.MigrateBlobs(db);
        Assert.Equal(0, FileStore.PendingBlobs(db));
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM files WHERE data IS NOT NULL"));
        Assert.Equal(5, BlobFiles(db).Count);           // ohne die leere Datei und das misslungene Vorschaubild

        Assert.Equal(big, FileStore.Data(db, "alt.bin"));
        Assert.Equal("klein", Encoding.UTF8.GetString(FileStore.Data(db, "klein.txt")!));
        Assert.Empty(FileStore.Data(db, "leer.txt")!);
        Assert.Equal("alt", Encoding.UTF8.GetString(FileStore.VersionData(db, fileId, FileStore.Versions(db, fileId).Single().Id)!));
        var restored = FileStore.Restore(db, FileStore.Trash(db).Single().Id);
        Assert.Equal("im Papierkorb", Encoding.UTF8.GetString(FileStore.Data(db, restored)!));
        Assert.Equal(0, FileStore.MigrateBlobs(db));    // nichts mehr zu tun
    }

    [Fact]
    public void Inzwischen_neu_geschriebene_Datei_behaelt_ihren_Inhalt()
    {
        var db = NewDb();
        Legacy(db, "a.txt", B("alt"));
        // Waehrend des Auslagerns ueberschrieben: das Auslagern verwirft seinen Blob
        FileStore.Put(db, "a.txt", B("neu"), null);
        FileStore.MigrateBlobs(db);
        Assert.Equal("neu", Encoding.UTF8.GetString(FileStore.Data(db, "a.txt")!));
        // Die alte Version ist mit ausgelagert
        Assert.Equal(0, FileStore.PendingBlobs(db));
        Assert.Equal(2, BlobFiles(db).Count);
    }

    [Fact]
    public void Kopie_eines_alten_Inhalts_und_Wiederherstellen_einer_alten_Version()
    {
        var db = NewDb();
        Legacy(db, "a.txt", B("eins"));
        FileStore.MoveOrCopy(db, "a.txt", "b.txt", false, copy: true);
        Assert.Equal("eins", Encoding.UTF8.GetString(FileStore.Data(db, "b.txt")!));
        FileStore.Put(db, "a.txt", B("zwei"), null);
        var id = FileStore.Get(db, "a.txt")!.FileId;
        FileStore.RestoreVersion(db, "a.txt", FileStore.Versions(db, id).Single().Id);   // Version noch in data
        Assert.Equal("eins", Encoding.UTF8.GetString(FileStore.Data(db, "a.txt")!));
    }

    [Fact]
    public void Schema_16_mit_Triggern_und_Blob_Ordner_neben_der_Datenbank()
    {
        var db = NewDb();
        Assert.Equal(18L, db.Scalar("PRAGMA user_version"));
        foreach (var t in UserDb.BlobTables)
            Assert.Equal(1L, db.Scalar($"SELECT COUNT(*) FROM pragma_table_info('{t}') WHERE name='blob'"));
        // Eigener Ort (DMOFFICE_BLOBS): DataPaths
        var paths = new DataPaths(Directory.CreateTempSubdirectory("dmpaths").FullName, Path.Combine(Path.GetTempPath(), "anderswo"));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "anderswo", "u1"), paths.UserBlobs("u1"));
        Assert.Equal(Path.Combine(new DataPaths(paths.Root).Root, "blobs"), new DataPaths(paths.Root).Blobs);
    }
}

/// <summary>Herunterladen mit Bereichen (Range) ueber die Weboberflaeche und WebDAV - aus dem BlobStore.</summary>
[Collection("server")]
public class BlobRangeTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public BlobRangeTests(FilesServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Bereiche_kommen_aus_der_Mitte_des_Blobs()
    {
        var data = RandomNumberGenerator.GetBytes(500_000);
        FileStore.Put(_fx.Db, "range.bin", data, "application/octet-stream");
        var c = _fx.Client();

        foreach (var url in new[] { "/api/files/download?path=range.bin", $"/dav/files/{FilesServerFixture.Login}/range.bin" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(131_000, 131_099);
            var r = await c.SendAsync(req);
            Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
            Assert.Equal(data.Skip(131_000).Take(100), await r.Content.ReadAsByteArrayAsync());
            Assert.Equal(500_000, r.Content.Headers.ContentRange!.Length);

            var all = await c.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            Assert.Equal(data, await all.Content.ReadAsByteArrayAsync());
        }
        var head = await c.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/dav/files/{FilesServerFixture.Login}/range.bin"));
        Assert.Equal(500_000, head.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task WebDAV_PUT_und_Upload_landen_verschluesselt_im_Blob()
    {
        var c = _fx.Client();
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Klartext darf nicht auf die Platte. ", 5000)));
        var put = await c.PutAsync($"/dav/files/{FilesServerFixture.Login}/dav-blob.txt", new ByteArrayContent(text));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var form = new MultipartFormDataContent { { new ByteArrayContent(text), "files", "upload-blob.txt" } };
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/files/upload", form)).StatusCode);
        foreach (var p in new[] { "dav-blob.txt", "upload-blob.txt" })
        {
            Assert.Equal(text, FileStore.Data(_fx.Db, p));
            var blob = (string)_fx.Db.Scalar("SELECT blob FROM files WHERE path=$p", ("$p", p))!;
            Assert.False(File.ReadAllBytes(_fx.Db.Blobs.PathOf(blob)).AsSpan().IndexOf("Klartext"u8) >= 0);
        }
    }
}
