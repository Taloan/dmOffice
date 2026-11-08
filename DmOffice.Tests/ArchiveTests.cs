using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>Archive im Dateibrowser: lesen, ändern, speichern (altes ZIP als Version), Schutz vor ZIP-Bomben und „../“.</summary>
[Collection("server")]
public class ArchiveTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public ArchiveTests(FilesServerFixture fx) => _fx = fx;

    private static byte[] MakeZip(params (string Name, string? Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                var e = zip.CreateEntry(name);
                if (text != null) using (var w = new StreamWriter(e.Open())) w.Write(text);
            }
        return ms.ToArray();
    }

    private static async Task Upload(HttpClient c, string dir, string name, byte[] data)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(data), "files", name } };
        var r = await c.PostAsync($"/api/files/upload?path={Uri.EscapeDataString(dir)}", form);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body.Length == 0 ? "{}" : body).RootElement;
    }

    private static string Q(string s) => Uri.EscapeDataString(s);

    private static async Task<JsonElement> List(HttpClient c, string path, string dir = "") =>
        await Json(await c.GetAsync($"/api/files/archive?path={Q(path)}&dir={Q(dir)}"));

    private static string[] Names(JsonElement listing) =>
        listing.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()! + (i.GetProperty("isDir").GetBoolean() ? "/" : "")).Order().ToArray();

    private static async Task<string> Entry(HttpClient c, string path, string entry)
    {
        var r = await c.GetAsync($"/api/files/archive/entry?path={Q(path)}&entry={Q(entry)}");
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Lesen_wie_ein_Ordner_auch_ohne_Ordnereintraege()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "arc1" });
        await Upload(c, "arc1", "Rechnungen.zip", MakeZip(("2025/januar.txt", "Januar"), ("2025/q1/maerz.txt", "März"), ("liesmich.txt", "Hallo")));

        var root = await List(c, "arc1/Rechnungen.zip");
        Assert.Equal("zip", root.GetProperty("format").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("readOnly").ValueKind);
        Assert.Equal(["2025/", "liesmich.txt"], Names(root));
        Assert.Equal(["januar.txt", "q1/"], Names(await List(c, "arc1/Rechnungen.zip", "2025")));
        Assert.Equal("März", await Entry(c, "arc1/Rechnungen.zip", "2025/q1/maerz.txt"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/files/archive?path={Q("arc1/Rechnungen.zip")}&dir=fehlt")).StatusCode);
    }

    [Fact]
    public async Task TarGz_lesen_und_entpacken()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "arc2" });
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
        using (var tar = new TarWriter(gz))
            foreach (var (name, text) in new[] { ("a/eins.txt", "1"), ("a/b/zwei.txt", "22"), ("drei.txt", "333") })
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        await Upload(c, "arc2", "paket.tar.gz", ms.ToArray());

        var root = await List(c, "arc2/paket.tar.gz");
        Assert.Equal("tgz", root.GetProperty("format").GetString());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("readOnly").ValueKind);
        Assert.Equal(["a/", "drei.txt"], Names(root));
        Assert.Equal("22", await Entry(c, "arc2/paket.tar.gz", "a/b/zwei.txt"));
        // Ändern geht bei TAR.GZ nicht
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/archive/mkdir", new { path = "arc2/paket.tar.gz", entry = "neu" })).StatusCode);

        var x = await Json(await c.PostAsJsonAsync("/api/files/archive/extract", new { path = "arc2/paket.tar.gz", dir = "", entries = Array.Empty<string>(), target = "arc2/paket" }));
        Assert.Equal(3, x.GetProperty("extracted").GetInt32());
        Assert.Equal("22", Encoding.UTF8.GetString(FileStore.Data(_fx.Db, "arc2/paket/a/b/zwei.txt")!));
    }

    [Fact]
    public async Task Aendern_und_speichern_altes_ZIP_bleibt_als_Version()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "arc3" });
        const string P = "arc3/Ablage.zip";
        await Upload(c, "arc3", "Ablage.zip", MakeZip(("alt/a.txt", "A"), ("weg.txt", "weg"), ("text.txt", "vorher")));
        var etag = FileStore.Get(_fx.Db, P)!.Etag;

        // Ordner anlegen, hinzufügen, umbenennen, entfernen, Text bearbeiten
        await Json(await c.PostAsJsonAsync("/api/files/archive/mkdir", new { path = P, entry = "neu/leer" }));
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("frisch")), "files", "dazu.txt" } };
        await Json(await c.PostAsync($"/api/files/archive/upload?path={Q(P)}&dir=neu", form));
        // Gleicher Name noch einmal: ohne conflict=replace abgelehnt
        form = new MultipartFormDataContent { { new ByteArrayContent([1]), "files", "dazu.txt" } };
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/files/archive/upload?path={Q(P)}&dir=neu", form)).StatusCode);
        await Json(await c.PostAsJsonAsync("/api/files/archive/move", new { path = P, from = "alt", to = "umbenannt" }));
        await Json(await c.PostAsJsonAsync("/api/files/archive/delete", new { path = P, entries = new[] { "weg.txt" } }));
        await Json(await c.PostAsJsonAsync("/api/files/archive/text", new { path = P, entry = "text.txt", text = "nachher", create = false }));

        // Noch nicht gespeichert: Datei unverändert, Ansicht zeigt den neuen Stand, offene Änderungen gemeldet
        Assert.Equal(etag, FileStore.Get(_fx.Db, P)!.Etag);
        var root = await List(c, P);
        Assert.Equal(["neu/", "text.txt", "umbenannt/"], Names(root));
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("pending").ValueKind);
        Assert.Equal("nachher", await Entry(c, P, "text.txt"));
        Assert.Equal("A", await Entry(c, P, "umbenannt/a.txt"));
        Assert.Contains((await Json(await c.GetAsync("/api/files/archive/pending"))).GetProperty("items").EnumerateArray(), i => i.GetProperty("path").GetString() == P);

        await Json(await c.PostAsJsonAsync("/api/files/archive/save", new { path = P, copy = false }));

        // Neues ZIP mit dem geänderten Inhalt
        using (var zip = new ZipArchive(FileStore.OpenRead(_fx.Db, P), ZipArchiveMode.Read))
        {
            var names = zip.Entries.Select(e => e.FullName).Order().ToArray();
            Assert.Equal(["neu/dazu.txt", "neu/leer/", "text.txt", "umbenannt/a.txt"], names);
            using var r = new StreamReader(zip.GetEntry("neu/dazu.txt")!.Open());
            Assert.Equal("frisch", r.ReadToEnd());
        }
        // Das alte ZIP liegt unter Versionen
        var versions = (await Json(await c.GetAsync($"/api/files/versions?path={Q(P)}"))).GetProperty("items");
        var v = Assert.Single(versions.EnumerateArray()).GetProperty("id").GetInt64();
        using (var old = new ZipArchive(FileStore.OpenVersion(_fx.Db, FileStore.Get(_fx.Db, P)!.FileId, v)!, ZipArchiveMode.Read))
            Assert.NotNull(old.GetEntry("weg.txt"));
        // Keine offenen Änderungen mehr
        Assert.Equal(JsonValueKind.Null, (await List(c, P)).GetProperty("pending").ValueKind);
    }

    [Fact]
    public async Task Verwerfen_und_anderweitig_geaendert_nur_als_Kopie()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "arc4" });
        const string P = "arc4/k.zip";
        await Upload(c, "arc4", "k.zip", MakeZip(("a.txt", "A")));
        await Json(await c.PostAsJsonAsync("/api/files/archive/mkdir", new { path = P, entry = "x" }));
        await Json(await c.PostAsJsonAsync("/api/files/archive/discard", new { path = P }));
        Assert.Equal(JsonValueKind.Null, (await List(c, P)).GetProperty("pending").ValueKind);

        await Json(await c.PostAsJsonAsync("/api/files/archive/mkdir", new { path = P, entry = "y" }));
        // Inzwischen ersetzt jemand das ZIP
        await Upload(c, "arc4", "k.zip", MakeZip(("a.txt", "A"), ("b.txt", "B")));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/files/archive/save", new { path = P, copy = false })).StatusCode);
        var saved = await Json(await c.PostAsJsonAsync("/api/files/archive/save", new { path = P, copy = true }));
        Assert.Equal("arc4/k (geändert).zip", saved.GetProperty("path").GetString());
        Assert.NotNull(FileStore.Get(_fx.Db, "arc4/k (geändert).zip"));
    }

    [Fact]
    public async Task Pfade_mit_Punkt_Punkt_und_absolute_Pfade_werden_ausgelassen()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "arc5" });
        await Upload(c, "arc5", "boese.zip", MakeZip(("../ausbruch.txt", "x"), ("a/../../ausbruch2.txt", "x"), ("/etc/passwd", "x"), ("C:/Windows/x.txt", "x"), ("ok.txt", "gut")));

        var root = await List(c, "arc5/boese.zip");
        Assert.Equal(["ok.txt"], Names(root));
        Assert.Equal(4, root.GetProperty("unsafe").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("readOnly").ValueKind);
        // Auch direkt angefragt nicht zu haben
        Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync($"/api/files/archive/entry?path={Q("arc5/boese.zip")}&entry={Q("../ausbruch.txt")}")).StatusCode);

        var x = await Json(await c.PostAsJsonAsync("/api/files/archive/extract", new { path = "arc5/boese.zip", dir = "", entries = Array.Empty<string>(), target = "arc5/raus" }));
        Assert.Equal(1, x.GetProperty("extracted").GetInt32());
        Assert.Null(FileStore.Get(_fx.Db, "arc5/ausbruch.txt"));
        Assert.Null(FileStore.Get(_fx.Db, "ausbruch.txt"));
        Assert.Null(FileStore.Get(_fx.Db, "ausbruch2.txt"));
        Assert.Equal(["arc5/raus/ok.txt"], FileStore.Descendants(_fx.Db, "arc5/raus").Select(f => f.Path).ToArray());
    }

    [Theory]
    [InlineData("../a")]
    [InlineData("/a")]
    [InlineData("a/../../b")]
    [InlineData("C:\\x")]
    [InlineData("a/\u0001b")]
    public void CleanPath_lehnt_ab(string key) => Assert.Null(Archives.CleanPath(key));

    [Fact]
    public void CleanPath_bereinigt() => Assert.Equal("a/b/c.txt", Archives.CleanPath("./a\\b//c.txt"));

    /// <summary>Liefert nur Nullen - zum Packen großer Einträge ohne großen Speicher.</summary>
    private sealed class Zeros(long length) : Stream
    {
        private long _pos;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _pos);
            Array.Clear(buffer, offset, n);
            _pos += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Zip_Bombe_nach_Verhaeltnis_wird_nicht_ausgepackt()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var s = zip.CreateEntry("nullen.bin", CompressionLevel.SmallestSize).Open()) new Zeros(Archives.BombMinBytes + 1).CopyTo(s);
            using (var w = new StreamWriter(zip.CreateEntry("klein.txt").Open())) w.Write("klein");
        }
        ms.Position = 0;
        var listing = Archives.Read(ms, "zip", null);
        Assert.True(listing.Bomb);

        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "arc6" });
        await Upload(c, "arc6", "bombe.zip", ms.ToArray());
        var root = await List(c, "arc6/bombe.zip");
        Assert.True(root.GetProperty("bomb").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/files/archive/entry?path={Q("arc6/bombe.zip")}&entry=klein.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/archive/extract", new { path = "arc6/bombe.zip", dir = "", entries = Array.Empty<string>(), target = "arc6/raus" })).StatusCode);
        Assert.Null(FileStore.Get(_fx.Db, "arc6/raus/nullen.bin"));
    }

    [Fact]
    public async Task Zip_Bombe_mit_falscher_Groessenangabe_bricht_ab()
    {
        // Ein Eintrag, der 1 MB auspackt, im Verzeichnis aber 10 Bytes angibt
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            using (var s = zip.CreateEntry("luege.bin").Open()) new Zeros(1024 * 1024).CopyTo(s);
        var data = ms.ToArray();
        void Patch(byte[] sig, int offset)
        {
            for (int i = 0; i + 4 <= data.Length; i++)
                if (data.AsSpan(i, 4).SequenceEqual(sig)) BitConverter.TryWriteBytes(data.AsSpan(i + offset, 4), 10u);
        }
        Patch([0x50, 0x4b, 0x01, 0x02], 24);   // Zentralverzeichnis: ausgepackte Größe
        Patch([0x50, 0x4b, 0x03, 0x04], 22);   // lokaler Kopf

        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "arc7" });
        await Upload(c, "arc7", "luege.zip", data);
        var r = await c.GetAsync($"/api/files/archive/entry?path={Q("arc7/luege.zip")}&entry=luege.bin");
        // Entweder gleich abgelehnt oder der Strom endet nach der angegebenen Größe - nie 1 MB
        var body = r.IsSuccessStatusCode ? await ReadSome(r) : [];
        Assert.True(body.Length <= 10, $"{body.Length} Bytes ausgepackt");
        var x = await c.PostAsJsonAsync("/api/files/archive/extract", new { path = "arc7/luege.zip", dir = "", entries = Array.Empty<string>(), target = "arc7/raus" });
        var f = FileStore.Get(_fx.Db, "arc7/raus/luege.bin");
        Assert.True(f == null || f.Size <= 10);
    }

    private static async Task<byte[]> ReadSome(HttpResponseMessage r)
    {
        try { return await r.Content.ReadAsByteArrayAsync(); }
        catch (HttpRequestException) { return []; }   // abgebrochen, während schon gesendet wurde
        catch (IOException) { return []; }
    }

    // Mit 7-Zip erzeugt (Passwort „sesam“): geheim.txt = „Geheimer Inhalt“, ordner/b.txt = „Unterordner“
    private const string ZipCryptoZip = "UEsDBBQAAQAAAG5MX131tRh1GwAAAA8AAAAKAAAAZ2VoZWltLnR4dM2mbjQL8v1tEpISAtdyjtYAYBHn+MPcgF5aQlBLAwQUAAAAAABuTF9dAAAAAAAAAAAAAAAABwAAAG9yZG5lci9QSwMEFAABAAAAbkxfXeEOz8kXAAAACwAAAAwAAABvcmRuZXIvYi50eHSj5YUZDRNEBEuWwDPdcuJwoQVxrtD30VBLAQI/ABQAAQAAAG5MX131tRh1GwAAAA8AAAAKACQAAAAAAAAAIAAAAAAAAABnZWhlaW0udHh0CgAgAAAAAAABABgA3g9+xxJp3QEAAAAAAAAAAAAAAAAAAAAAUEsBAj8AFAAAAAAAbkxfXQAAAAAAAAAAAAAAAAcAJAAAAAAAAAAQAAAAQwAAAG9yZG5lci8KACAAAAAAAAEAGADIfH3HEmndAQAAAAAAAAAAAAAAAAAAAABQSwECPwAUAAEAAABuTF9d4Q7PyRcAAAALAAAADAAkAAAAAAAAACAAAABoAAAAb3JkbmVyL2IudHh0CgAgAAAAAAABABgAd0t+xxJp3QEAAAAAAAAAAAAAAAAAAAAAUEsFBgAAAAADAAMAEwEAAKkAAAAAAA==";
    private const string AesZip = "UEsDBDMAAQBjAG5MX10AAAAAKwAAAA8AAAAKAAsAZ2VoZWltLnR4dAGZBwACAEFFAwAAhGIxioUaqqIfM9dGLVqV2kzjngJwaLmY314sj9JlWJhFjDpq/mXr+cOWulBLAwQUAAAAAABuTF9dAAAAAAAAAAAAAAAABwAAAG9yZG5lci9QSwMEMwABAGMAbkxfXQAAAAAnAAAACwAAAAwACwBvcmRuZXIvYi50eHQBmQcAAgBBRQMAAB0FM7jpUL11dwBVKRy1NnFNrCv3N3Y1BDiuc5ucVAppe6NC2rpA11BLAQI/ADMAAQBjAG5MX10AAAAAKwAAAA8AAAAKAC8AAAAAAAAAIAAAAAAAAABnZWhlaW0udHh0CgAgAAAAAAABABgA3g9+xxJp3QEAAAAAAAAAAAAAAAAAAAAAAZkHAAIAQUUDAABQSwECPwAUAAAAAABuTF9dAAAAAAAAAAAAAAAABwAkAAAAAAAAABAAAABeAAAAb3JkbmVyLwoAIAAAAAAAAQAYAN4PfscSad0BAAAAAAAAAAAAAAAAAAAAAFBLAQI/ADMAAQBjAG5MX10AAAAAJwAAAAsAAAAMAC8AAAAAAAAAIAAAAIMAAABvcmRuZXIvYi50eHQKACAAAAAAAAEAGAB3S37HEmndAQAAAAAAAAAAAAAAAAAAAAABmQcAAgBBRQMAAFBLBQYAAAAAAwADACkBAADfAAAAAAA=";
    /// <summary>7z mit verschlüsselten Namen (-mhe=on): ohne Passwort nicht einmal das Verzeichnis lesbar.</summary>
    private const string Encrypted7z = "N3q8ryccAARBqlWLwAAAAAAAAAA+AAAAAAAAAFgI47MoMYoTpDcHFPXbBmqElqckOj/1qdMkcbq+t9BSWVZAJf1sxK7swsVwPkKYHfCGBD3qIh2lX3VcOcKfFqQFZ2QoTGg5atiZeQOXMPEEdnlVVglKM2QptK/MVutp7RTKso5NfnBxvCEXATUDF7lMJck2YErGCFYcew5xtlrhUt0eHe9VGUcQ7ZAY24xK38tvsD9sHM74rD23Sma6ubAd2RO5D46rnRpZVUv+uMNSvCqigh7tQpIJhDSNgEVKFB/Hf4YXBiABCYCgAAcLAQACJAbxBwESUw9pryoHUr7djEQFvTDmiWg5IwMBAQVdABAAAAEADICRgMIKAZ1E7j0AAA==";

    [Theory]
    [InlineData("zc", ZipEncryption.ZipCrypto)]
    [InlineData("aes", ZipEncryption.Aes256)]
    public async Task Verschluesseltes_ZIP_entsperren_aendern_und_gleich_verschluesselt_speichern(string kind, ZipEncryption expected)
    {
        var c = _fx.Client();
        var dir = "arcpw-" + kind;
        var P = dir + "/geheim.zip";
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = dir });
        await Upload(c, dir, "geheim.zip", Convert.FromBase64String(kind == "zc" ? ZipCryptoZip : AesZip));

        // Namen sind lesbar, Inhalte erst mit Passwort
        var root = await List(c, P);
        Assert.True(root.GetProperty("encrypted").GetBoolean());
        Assert.False(root.GetProperty("unlocked").GetBoolean());
        var denied = await c.GetAsync($"/api/files/archive/entry?path={Q(P)}&entry=geheim.txt");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True((await Json403(denied)).GetProperty("password").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/files/archive/mkdir", new { path = P, entry = "x" })).StatusCode);
        var wrong = await c.PostAsJsonAsync("/api/files/archive/unlock", new { path = P, password = "falsch" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.True((await Json403(wrong)).GetProperty("wrong").GetBoolean());

        await Json(await c.PostAsJsonAsync("/api/files/archive/unlock", new { path = P, password = "sesam" }));
        Assert.Equal("Geheimer Inhalt", await Entry(c, P, "geheim.txt"));

        // Ändern und speichern: dieselbe Verschlüsselung, dasselbe Passwort
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("neu dazu")), "files", "neu.txt" } };
        await Json(await c.PostAsync($"/api/files/archive/upload?path={Q(P)}&dir=ordner", form));
        await Json(await c.PostAsJsonAsync("/api/files/archive/save", new { path = P, copy = false }));

        using (var s = FileStore.OpenRead(_fx.Db, P)) Assert.Equal(expected, ZipCryptWriter.Inspect(s));
        var saved = FileStore.Data(_fx.Db, P)!;
        var listing = Archives.Read(new MemoryStream(saved), "zip", "sesam");
        Assert.All(listing.Entries.Where(e => !e.IsDir), e => Assert.True(e.Encrypted));
        Assert.Equal("Geheimer Inhalt", ReadEntry(saved, listing, "geheim.txt", "sesam"));
        Assert.Equal("neu dazu", ReadEntry(saved, listing, "ordner/neu.txt", "sesam"));
        Assert.Throws<ArchivePasswordException>(() => ReadEntry(saved, listing, "ordner/neu.txt", "falsch"));
        Assert.Single((await Json(await c.GetAsync($"/api/files/versions?path={Q(P)}"))).GetProperty("items").EnumerateArray());
    }

    private static string ReadEntry(byte[] zip, ArchiveListing listing, string path, string password)
    {
        using var s = Archives.OpenEntry(new MemoryStream(zip), "zip", listing.Find(path)!, password);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void ZipCrypto_mit_Umlaut_im_Passwort_wird_abgelehnt() =>
        Assert.Throws<ArchiveException>(() => new ZipCryptWriter(new MemoryStream(), "sesam-öffne", ZipEncryption.ZipCrypto));

    private static async Task<JsonElement> Json403(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Sieben_z_mit_verschluesselten_Namen_fragt_schon_beim_Oeffnen()
    {
        var c = _fx.Client();
        await c.PostAsJsonAsync("/api/files/mkdir", new { path = "arc7z" });
        await Upload(c, "arc7z", "enc.7z", Convert.FromBase64String(Encrypted7z));
        var r = await c.GetAsync($"/api/files/archive?path={Q("arc7z/enc.7z")}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.True((await Json403(r)).GetProperty("password").GetBoolean());
        await Json(await c.PostAsJsonAsync("/api/files/archive/unlock", new { path = "arc7z/enc.7z", password = "sesam" }));
        var root = await List(c, "arc7z/enc.7z");
        Assert.Equal(["geheim.txt", "ordner/"], Names(root));
        Assert.Equal("Unterordner", await Entry(c, "arc7z/enc.7z", "ordner/b.txt"));
        var x = await Json(await c.PostAsJsonAsync("/api/files/archive/extract", new { path = "arc7z/enc.7z", dir = "", entries = Array.Empty<string>(), target = "arc7z/raus" }));
        Assert.Equal(2, x.GetProperty("extracted").GetInt32());
    }

    [Theory]
    [InlineData(ZipEncryption.ZipCrypto, "sesam")]
    [InlineData(ZipEncryption.Aes128, "sesam-öffne")]
    [InlineData(ZipEncryption.Aes256, "sesam-öffne")]
    public void ZipCryptWriter_liest_sich_mit_SharpCompress(ZipEncryption encryption, string Pw)
    {
        using var ms = new MemoryStream();
        var w = new ZipCryptWriter(ms, Pw, encryption);
        w.AddDirectory("leer", DateTime.Now);
        var big = string.Concat(Enumerable.Repeat("Zeile mit Text ", 20000));
        w.AddFile("ä/groß.txt", new MemoryStream(Encoding.UTF8.GetBytes(big)), DateTime.Now, fast: false);
        w.AddFile("bild.jpg", new MemoryStream([1, 2, 3]), DateTime.Now, fast: true);
        w.Finish();
        ms.Position = 0;
        Assert.Equal(encryption, ZipCryptWriter.Inspect(ms));
        ms.Position = 0;
        var listing = Archives.Read(ms, "zip", Pw);
        Assert.Equal(["bild.jpg", "leer", "ä/groß.txt"], listing.Entries.Select(e => e.Path).Order(StringComparer.Ordinal).ToArray());
        string Read(string p, string pw)
        {
            var copy = new MemoryStream(ms.ToArray());
            using var s = Archives.OpenEntry(copy, "zip", listing.Find(p)!, pw);
            return new StreamReader(s).ReadToEnd();
        }
        Assert.Equal(big, Read("ä/groß.txt", Pw));
        Assert.Throws<ArchivePasswordException>(() => Read("ä/groß.txt", "falsch"));
    }
}
