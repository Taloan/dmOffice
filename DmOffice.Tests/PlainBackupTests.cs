using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;
using DmOffice.Storage;

namespace DmOffice.Tests;

/// <summary>Datensicherung (docs/datensicherung-konzept.md): Formate, nur Geändertes, .trash, Frist, Verschieben.
/// Ziel ist ein Ordner (FolderDriver); ein Test läuft gegen dmOffices eigenen WebDAV-Server.</summary>
[Collection("server")] // Ein Test schaltet Finanzen frei, das gilt prozessweit
public class PlainBackupTests
{
    private sealed class Ctx : IDisposable
    {
        public UserSpace Space { get; }
        public string Target { get; } = Directory.CreateTempSubdirectory("dmplain-ziel").FullName;
        public string Tmp { get; } = Directory.CreateTempSubdirectory("dmplain-tmp").FullName;
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dmplain");
        public PlainBackupSettings S { get; } = new() { Enabled = true, Target = PlainBackup.WebDav, Url = "https://ziel.example/", Areas = [.. PlainBackup.AllAreas] };
        public DateTime Now { get; set; } = new(2026, 10, 31, 2, 0, 0, DateTimeKind.Utc);

        public Ctx()
        {
            var db = new UserDb(Path.Combine(_dir.FullName, "u.db"), new byte[32], "t", Path.Combine(_dir.FullName, "blobs"));
            PimStore.EnsureDefaults(db);
            Space = new UserSpace(new UserRecord { Login = "t", DisplayName = "Test" }, db);
        }

        public UserDb Db => Space.Db;
        public string File(string rel) => Path.Combine(Target, rel.Replace('/', Path.DirectorySeparatorChar));
        public string Text(string rel) => System.IO.File.ReadAllText(File(rel));
        public string[] Trash() => Directory.Exists(File(PlainBackup.Trash))
            ? Directory.GetFiles(File(PlainBackup.Trash), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Target, f).Replace('\\', '/')).OrderBy(x => x).ToArray()
            : [];

        public Task<PlainBackup.Result> Run()
        {
            var items = PlainBackupAreas.Items(Space, S, null);
            return PlainBackup.RunAsync(Db, S, new FolderDriver(Target), items, Tmp, () => PlainBackupReadMe.Text(Space, S, Now), null, CancellationToken.None, () => Now);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var d in new[] { Target, Tmp, _dir.FullName }) try { Directory.Delete(d, true); } catch (IOException) { }
        }
    }

    private static void Put(UserDb db, string path, string text)
    {
        var parent = FileStore.Parent(path);
        if (!string.IsNullOrEmpty(parent)) FileStore.EnsureDir(db, parent);
        FileStore.Put(db, path, Encoding.UTF8.GetBytes(text), "text/plain");
    }

    [Fact]
    public async Task Erster_Lauf_legt_lesbare_Kopie_an_und_zweiter_sendet_nichts()
    {
        using var c = new Ctx();
        Put(c.Db, "Belege/2026/Rechnung März.txt", "Rechnung");
        Put(c.Db, "notiz.txt", "Hallo");
        var cal = PimStore.Collections(c.Db, PimStore.Calendar).First();
        const string Tz = "BEGIN:VTIMEZONE\r\nTZID:Europe/Berlin\r\nBEGIN:STANDARD\r\nDTSTART:19701025T030000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n";
        PimStore.Put(c.Db, cal, "a.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\n" + Tz + "BEGIN:VEVENT\r\nUID:a\r\nDTSTART;TZID=Europe/Berlin:20261101T100000\r\nSUMMARY:Zahnarzt\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        PimStore.Put(c.Db, cal, "b.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\n" + Tz + "BEGIN:VEVENT\r\nUID:b\r\nDTSTART;TZID=Europe/Berlin:20261102T100000\r\nSUMMARY:Friseur\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        PimStore.Put(c.Db, cal, "t.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VTODO\r\nUID:t\r\nSUMMARY:Steuer\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        var book = PimStore.Collections(c.Db, PimStore.AddressBook).First();
        PimStore.Put(c.Db, book, "k.vcf", "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:k\r\nFN:Erika Muster\r\nN:Muster;Erika;;;\r\nEND:VCARD\r\n");
        var notes = DmOffice.Notes.NotesDb.Of(c.Space);
        var id = notes.CreateNote(null);
        notes.SaveNote(id, "<p>Einkauf</p><ul><li>Milch</li></ul>", "Einkauf\nMilch");

        var r = await c.Run();
        Assert.Null(r.Error);
        Assert.Equal("Rechnung", c.Text("Dateien/Belege/2026/Rechnung März.txt"));
        var ics = c.Text($"Kalender/{cal.Name}.ics");
        Assert.Contains("SUMMARY:Zahnarzt", ics);
        Assert.Contains("SUMMARY:Friseur", ics);
        Assert.DoesNotContain("SUMMARY:Steuer", ics);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ics, "BEGIN:VTIMEZONE"));
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Contains("SUMMARY:Steuer", c.Text($"Aufgaben/{cal.Name}.ics"));
        Assert.Contains("FN:Erika Muster", c.Text($"Kontakte/{book.Name}.vcf"));
        var md = Assert.Single(Directory.GetFiles(c.File("Notizen"), "*.md"));
        Assert.Contains("Milch", System.IO.File.ReadAllText(md));
        Assert.Contains(".trash", c.Text(PlainBackup.ReadMe));
        Assert.Empty(Directory.GetFiles(c.Target, "*.part", SearchOption.AllDirectories));

        // Nichts geändert: nichts übertragen
        var again = await c.Run();
        Assert.Null(again.Error);
        Assert.Equal(0, again.Written);
        Assert.Equal(r.Files, again.Files);
        Assert.Empty(c.Trash());
    }

    [Fact]
    public async Task Systemordner_notizi_und_Mail_Kopien_nicht_unter_Dateien_Tresor_schon()
    {
        using var c = new Ctx();
        c.S.Areas = [PlainBackup.Files];
        Put(c.Db, "notizisync/db/note.json", "x");
        Put(c.Db, DmOffice.Mail.MailAttachmentCopy.Root + "/abc/Angebot.docx", "x");
        Put(c.Db, FileStore.SystemVault + "/vault/d/AB/inhalt.c9r", "x");
        Put(c.Db, "notizisync-alt/bleibt.txt", "x");
        var r = await c.Run();
        Assert.Null(r.Error);
        Assert.False(Directory.Exists(c.File("Dateien/notizisync")));
        Assert.False(Directory.Exists(c.File("Dateien/" + DmOffice.Mail.MailAttachmentCopy.Root)));
        Assert.True(System.IO.File.Exists(c.File($"Dateien/{FileStore.SystemVault}/vault/d/AB/inhalt.c9r")));
        Assert.True(System.IO.File.Exists(c.File("Dateien/notizisync-alt/bleibt.txt")));
    }

    [Fact]
    public async Task Geaenderte_Datei_aeltere_Version_nach_trash_hoechstens_einmal_am_Tag()
    {
        using var c = new Ctx();
        Put(c.Db, "Ordner/a.txt", "eins");
        await c.Run();
        Put(c.Db, "Ordner/a.txt", "zwei");
        var r = await c.Run();
        Assert.Equal(1, r.Written);
        Assert.Equal("zwei", c.Text("Dateien/Ordner/a.txt"));
        Assert.Equal(["Dateien/Ordner/a.txt".Insert(0, ".trash/")], c.Trash());
        Assert.Equal("eins", c.Text(".trash/Dateien/Ordner/a.txt"));
        // Am selben Tag noch einmal geändert: überschreiben, keine weitere Version
        Put(c.Db, "Ordner/a.txt", "drei");
        await c.Run();
        Assert.Single(c.Trash());
        // Einen Tag später: wieder eine Version, Name mit Datum, weil der Name in .trash belegt ist
        c.Now = c.Now.AddDays(1);
        Put(c.Db, "Ordner/a.txt", "vier");
        await c.Run();
        Assert.Equal(2, c.Trash().Length);
        Assert.Equal("drei", c.Text(".trash/Dateien/Ordner/a (2026-11-01).txt"));
    }

    [Fact]
    public async Task Ohne_Versionen_wird_ueberschrieben()
    {
        using var c = new Ctx();
        c.S.Versions = false;
        Put(c.Db, "a.txt", "eins");
        await c.Run();
        Put(c.Db, "a.txt", "zwei");
        await c.Run();
        Assert.Equal("zwei", c.Text("Dateien/a.txt"));
        Assert.Empty(c.Trash());
    }

    [Fact]
    public async Task Geloeschtes_bleibt_in_trash_bis_zur_Frist()
    {
        using var c = new Ctx();
        c.S.TrashDays = 30;
        Put(c.Db, "Fotos/bild.txt", "Foto");
        await c.Run();
        FileStore.Delete(c.Db, "Fotos/bild.txt");
        var r = await c.Run();
        Assert.Equal(1, r.Trashed);
        Assert.False(System.IO.File.Exists(c.File("Dateien/Fotos/bild.txt")));
        Assert.Equal("Foto", c.Text(".trash/Dateien/Fotos/bild.txt"));
        // Vor Ablauf der Frist bleibt es
        c.Now = c.Now.AddDays(29);
        await c.Run();
        Assert.True(System.IO.File.Exists(c.File(".trash/Dateien/Fotos/bild.txt")));
        // Danach endgültig weg
        c.Now = c.Now.AddDays(2);
        var late = await c.Run();
        Assert.Equal(1, late.Purged);
        Assert.Empty(c.Trash());
    }

    [Fact]
    public async Task Frist_nie_behaelt_alles()
    {
        using var c = new Ctx();
        c.S.TrashDays = 0;
        Put(c.Db, "x.txt", "x");
        await c.Run();
        FileStore.Delete(c.Db, "x.txt");
        await c.Run();
        c.Now = c.Now.AddYears(3);
        await c.Run();
        Assert.Single(c.Trash());
    }

    [Fact]
    public async Task Umbenennen_verschiebt_im_Ziel_statt_neu_zu_senden()
    {
        using var c = new Ctx();
        Put(c.Db, "Alt/gross.txt", new string('x', 10000));
        await c.Run();
        FileStore.EnsureDir(c.Db, "Neu");
        FileStore.MoveOrCopy(c.Db, "Alt/gross.txt", "Neu/gross.txt", false, false);
        var r = await c.Run();
        Assert.Equal(1, r.Moved);
        Assert.Equal(0, r.Written);
        Assert.True(System.IO.File.Exists(c.File("Dateien/Neu/gross.txt")));
        Assert.False(System.IO.File.Exists(c.File("Dateien/Alt/gross.txt")));
        Assert.Empty(c.Trash());
    }

    [Fact]
    public async Task Abgewaehlter_Ordner_und_Bereich_fehlen()
    {
        using var c = new Ctx();
        Put(c.Db, "Privat/geheim.txt", "nein");
        Put(c.Db, "Offen/ja.txt", "ja");
        c.S.Skip = ["files:Privat"];
        c.S.Areas = [PlainBackup.Files];
        await c.Run();
        Assert.False(Directory.Exists(c.File("Dateien/Privat")));
        Assert.Equal("ja", c.Text("Dateien/Offen/ja.txt"));
        Assert.False(Directory.Exists(c.File("Kontakte")));
    }


    [Fact]
    public async Task Neues_Ziel_mit_vorhandenen_Dateien_sendet_sie_nicht_noch_einmal()
    {
        using var c = new Ctx();
        Put(c.Db, "Gross/a.txt", "schon da");
        Directory.CreateDirectory(c.File("Dateien/Gross"));
        System.IO.File.WriteAllText(c.File("Dateien/Gross/a.txt"), "schon da");
        System.IO.File.SetLastWriteTimeUtc(c.File("Dateien/Gross/a.txt"), DateTime.UtcNow.AddMinutes(5));
        var r = await c.Run();
        Assert.Null(r.Error);
        Assert.DoesNotContain(".trash/Dateien/Gross/a.txt", c.Trash());
        Put(c.Db, "Gross/a.txt", "neu");
        Assert.Equal(1, (await c.Run()).Written);
    }

    [Fact]
    public async Task Englische_Ordnernamen_wenn_beim_ersten_Lauf_Englisch()
    {
        using var c = new Ctx();
        c.S.Lang = "en";
        Put(c.Db, "Docs/a.txt", "a");
        Assert.Null((await c.Run()).Error);
        Assert.Equal("a", c.Text("Files/Docs/a.txt"));
        var readMe = c.Text("README.txt");
        Assert.Contains("Files/", readMe);
        Assert.False(System.IO.File.Exists(c.File("LIESMICH.txt")));
        Assert.False(Directory.Exists(c.File("Dateien")));
    }

    [Fact]
    public async Task Cryptomator_Tresor_in_Dateien_kommt_bytegenau_ins_Ziel()
    {
        using var c = new Ctx();
        var root = ".vault/.dmVAULT";
        var longName = new string('B', 216) + "==.c9r";
        var files = new Dictionary<string, byte[]>
        {
            [root + "/vault.cryptomator"] = Encoding.ASCII.GetBytes("eyJraWQiOiJtYXN0ZXJrZXlmaWxlOm1hc3RlcmtleS5jcnlwdG9tYXRvciJ9.e30.x"),
            [root + "/masterkey.cryptomator"] = Encoding.ASCII.GetBytes("{\"version\":999}"),
            [root + "/d/AB/CDEFGHIJKLMNOPQRSTUVWXYZ234567/dir.c9r"] = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString()),
            [root + "/d/AB/CDEFGHIJKLMNOPQRSTUVWXYZ234567/aGFsbG8-_w==.c9r"] = [0, 1, 2, 255, 254, 13, 10],
            [root + "/d/AB/CDEFGHIJKLMNOPQRSTUVWXYZ234567/" + longName] = new byte[100_000].Select((_, i) => (byte)(i * 7)).ToArray(),
            [root + "/d/AB/CDEFGHIJKLMNOPQRSTUVWXYZ234567/X1Y2Z3.c9s/name.c9s"] = Encoding.ASCII.GetBytes(new string('n', 300) + ".c9r"),
            [root + "/d/AB/CDEFGHIJKLMNOPQRSTUVWXYZ234567/X1Y2Z3.c9s/contents.c9r"] = [42],
            [root + "/ohne-endung"] = [],
        };
        foreach (var (path, data) in files)
        {
            FileStore.EnsureDir(c.Db, FileStore.Parent(path)!);
            FileStore.Put(c.Db, path, data, "application/octet-stream");
        }
        var r = await c.Run();
        Assert.Null(r.Error);
        foreach (var (path, data) in files)
            Assert.Equal(data, System.IO.File.ReadAllBytes(c.File("Dateien/" + path)));
        // Nichts geändert: kein einziger Eintrag erneut übertragen
        Assert.Equal(0, (await c.Run()).Written);
    }

    /// <summary>Ziel, das bei den ersten Schreibversuchen die Verbindung schließt (wie pCloud mit „ResponseEnded“).</summary>
    private sealed class Flaky(IStorageDriver inner, int fails) : IStorageDriver
    {
        public int Fails = fails;
        public Task<List<DriverEntry>> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);
        public Task<DriverEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct) => inner.ReadRangeAsync(path, offset, count, ct);
        public Task WriteAsync(string path, Stream data, CancellationToken ct) =>
            Fails-- > 0 ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely. (ResponseEnded)") : inner.WriteAsync(path, data, ct);
        public Task MkdirAsync(string path, CancellationToken ct) => inner.MkdirAsync(path, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);
        public Task MoveAsync(string from, string to, CancellationToken ct) => inner.MoveAsync(from, to, ct);
        public Task<DriverSpace?> SpaceAsync(CancellationToken ct) => inner.SpaceAsync(ct);
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task Abgebrochene_Verbindung_wird_wiederholt()
    {
        using var c = new Ctx();
        Put(c.Db, "a.txt", "eins");
        Put(c.Db, "b.txt", "zwei");
        var items = PlainBackupAreas.Items(c.Space, c.S, null);
        // Zweimal abgebrochen: dieselbe Datei geht beim dritten Versuch, der Lauf läuft weiter
        var r = await PlainBackup.RunAsync(c.Db, c.S, new Flaky(new FolderDriver(c.Target), 2), items, c.Tmp, () => "x", null, CancellationToken.None, () => c.Now);
        Assert.Null(r.Error);
        Assert.Equal("eins", c.Text("Dateien/a.txt"));
        Assert.Equal("zwei", c.Text("Dateien/b.txt"));
        // Bleibt es abgebrochen: Lauf endet, die Meldung nennt die Datei
        Put(c.Db, "c.txt", "drei");
        r = await PlainBackup.RunAsync(c.Db, c.S, new Flaky(new FolderDriver(c.Target), 99), PlainBackupAreas.Items(c.Space, c.S, null), c.Tmp, () => "x", null, CancellationToken.None, () => c.Now);
        Assert.Contains("c.txt", r.Error);
        Assert.Contains("ResponseEnded", r.Error);
    }

    [Fact]
    public async Task Unterordner_eines_Ziels_als_Wurzel()
    {
        using var c = new Ctx();
        Put(c.Db, "a.txt", "eins");
        var items = PlainBackupAreas.Items(c.Space, c.S, null);
        using var d = new SubfolderDriver(new FolderDriver(c.Target), "Sicherung/dmOffice");
        var r = await PlainBackup.RunAsync(c.Db, c.S, d, items, c.Tmp, () => "x", null, CancellationToken.None, () => c.Now);
        Assert.Null(r.Error);
        Assert.Equal("eins", c.Text("Sicherung/dmOffice/Dateien/a.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.DeleteAsync("", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => d.WriteAsync("../raus.txt", new MemoryStream([1]), CancellationToken.None));
    }

    [Fact]
    public async Task Grosse_Datei_ueber_part()
    {
        using var c = new Ctx();
        var big = new byte[PlainBackup.SmallFile + 10];
        Random.Shared.NextBytes(big);
        FileStore.Put(c.Db, "gross.bin", big, "application/octet-stream");
        await c.Run();
        Assert.Equal(big, System.IO.File.ReadAllBytes(c.File("Dateien/gross.bin")));
        big[0] ^= 1;
        FileStore.Put(c.Db, "gross.bin", big, "application/octet-stream");
        await c.Run();
        Assert.Equal(big, System.IO.File.ReadAllBytes(c.File("Dateien/gross.bin")));
        Assert.Single(c.Trash());
        Assert.Empty(Directory.GetFiles(c.Target, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public void Namen_fuer_jedes_Ziel_tauglich()
    {
        Assert.Equal("a_b_c", PlainBackup.Segment("a:b?c"));
        Assert.Equal("_CON.txt", PlainBackup.Segment("CON.txt"));
        Assert.Equal("Ende", PlainBackup.Segment("Ende. "));
        Assert.Equal("_", PlainBackup.Segment("   "));
        Assert.True(PlainBackup.Segment(new string('x', 400) + ".pdf").Length <= PlainBackup.MaxName);
        // Lange Namen bis zur Grenze bleiben (Cryptomator kürzt selbst erst ab 220 Zeichen)
        var c9r = new string('A', 216) + "==.c9r";
        Assert.Equal(c9r, PlainBackup.Segment(c9r));
        Assert.Equal(" führend", PlainBackup.Segment(" führend"));
        Assert.EndsWith(".pdf", PlainBackup.Segment(new string('x', 400) + ".pdf"));
        // Zerlegte Umlaute (macOS) werden zusammengesetzt
        Assert.Equal("Mär", PlainBackup.Segment("Mär"));
        var n = new PlainBackup.Names();
        Assert.Equal("D/Foto.jpg", n.Take("D/Foto.jpg"));
        // Groß-/Kleinschreibung zählt: nichts umbenennen
        Assert.Equal("D/foto.jpg", n.Take("D/foto.jpg"));
        Assert.Equal("D/Foto (2).jpg", n.Take("D/Foto.jpg"));
    }

    [Fact]
    public void Zeitplan_letzte_faellige_Uhrzeit()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // Mitten in der Sommerzeit: 03:00 Berlin = 01:00 UTC
        Assert.Equal(new DateTime(2026, 7, 1, 1, 0, 0, DateTimeKind.Utc), PlainBackupService.LastSlot("03:00", berlin, new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
        // 31.10.2026 02:30 UTC = 03:30 Berlin (Winterzeit): heute 03:00 ist vorbei
        Assert.Equal(new DateTime(2026, 10, 31, 2, 0, 0, DateTimeKind.Utc), PlainBackupService.LastSlot("03:00", berlin, new DateTime(2026, 10, 31, 2, 30, 0, DateTimeKind.Utc)));
        // 01:00 UTC = 02:00 Berlin: noch nicht, also gestern 03:00 (02:00 UTC)
        Assert.Equal(new DateTime(2026, 10, 30, 2, 0, 0, DateTimeKind.Utc), PlainBackupService.LastSlot("03:00", berlin, new DateTime(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Bestaetigung_gilt_fuer_das_Ziel()
    {
        var s = new PlainBackupSettings { Enabled = true, Target = PlainBackup.WebDav, Url = "https://a.example/dav/" };
        s.Confirmed = PlainBackup.ConfirmKey(s);
        Assert.True(PlainBackup.Ready(s));
        s.Password = "anderes";
        Assert.True(PlainBackup.Ready(s));
        s.Url = "https://b.example/dav/";
        Assert.False(PlainBackup.Ready(s));
        s.Url = "https://a.example/dav/";
        Assert.True(PlainBackup.Ready(s));
        // Externer Ordner statt Adresse: neues Ziel, erneut bestätigen
        s.Target = PlainBackup.External; s.Mount = "m1";
        Assert.False(PlainBackup.Ready(s));
    }
}

/// <summary>Datensicherung gegen einen echten WebDAV-Server (dmOffice selbst): .part, Umbenennen, .trash über MOVE.</summary>
[Collection("server")]
public class PlainBackupWebDavTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public PlainBackupWebDavTests(FilesServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Lauf_gegen_WebDav()
    {
        var root = "Datensicherung-" + Guid.NewGuid().ToString("N")[..6];
        using var http = _fx.Client();
        var dir = Directory.CreateTempSubdirectory("dmplain-src");
        try
        {
            var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "q", Path.Combine(dir.FullName, "blobs"));
            PimStore.EnsureDefaults(db);
            var space = new UserSpace(new UserRecord { Login = "q", DisplayName = "Quelle" }, db);
            FileStore.EnsureDir(db, "Ordner mit Ümlaut");
            FileStore.Put(db, "Ordner mit Ümlaut/a.txt", Encoding.UTF8.GetBytes("eins"), "text/plain");
            var s = new PlainBackupSettings { Enabled = true, Target = PlainBackup.WebDav, Url = $"http://localhost/dav/files/{FilesServerFixture.Login}/{root}/", Areas = [PlainBackup.Files] };
            var tmp = Path.Combine(dir.FullName, "tmp");
            var now = DateTime.UtcNow;
            async Task<PlainBackup.Result> Run()
            {
                using var d = new WebDavDriver(s.Url, FilesServerFixture.Login, _fx.AppPassword, http) { RememberDirs = true };
                return await PlainBackup.RunAsync(db, s, d, PlainBackupAreas.Items(space, s, null), tmp, () => "liesmich", null, CancellationToken.None, () => now);
            }
            Assert.Null((await Run()).Error);
            Assert.Equal("eins", Encoding.UTF8.GetString(FileStore.Data(_fx.Db, $"{root}/Dateien/Ordner mit Ümlaut/a.txt")!));
            FileStore.Put(db, "Ordner mit Ümlaut/a.txt", Encoding.UTF8.GetBytes("zwei"), "text/plain");
            Assert.Null((await Run()).Error);
            Assert.Equal("zwei", Encoding.UTF8.GetString(FileStore.Data(_fx.Db, $"{root}/Dateien/Ordner mit Ümlaut/a.txt")!));
            Assert.Equal("eins", Encoding.UTF8.GetString(FileStore.Data(_fx.Db, $"{root}/.trash/Dateien/Ordner mit Ümlaut/a.txt")!));
            Assert.Null(FileStore.Get(_fx.Db, $"{root}/Dateien/Ordner mit Ümlaut/a.txt.part"));
            Assert.Equal("liesmich", Encoding.UTF8.GetString(FileStore.Data(_fx.Db, $"{root}/LIESMICH.txt")!).TrimStart('﻿'));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }
}
