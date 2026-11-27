using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Importprofile der Galerie: Muster, Einsortieren (RAW+JPG+XMP zusammen), doppelte Namen, Kopieren, externe Ziele, API.</summary>
[Collection("server")]
public class ImportProfilesTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ImportProfilesTests(ChatFixture fx) { _fx = fx; ImportProfiles.Settle = TimeSpan.Zero; }

    private (UserSpace Space, HttpClient Client) NewUser()
    {
        var user = "imp" + Guid.NewGuid().ToString("N")[..6];
        var u = _fx.Factory.Services.GetRequiredService<UserStore>().Create(user, "Import", "geheim-123456", isAdmin: false);
        _fx.Users[user] = (u.Id, AppPasswords.Create(_fx.Factory.Services.GetRequiredService<Spaces>().Get(u.Id).Db, "Test"));
        return (_fx.Space(user), _fx.As(user));
    }

    private ServerConfig Config => _fx.Factory.Services.GetRequiredService<ServerConfig>();

    /// <summary>JPEG mit EXIF: Hersteller, Modell, Aufnahmezeit („2026:06:14 10:30:45“).</summary>
    internal static byte[] Jpeg(string make = "FUJIFILM", string model = "X100VI", string taken = "2026:06:14 10:30:45", SKColor? color = null)
    {
        using var bmp = new SKBitmap(120, 80);
        using (var c = new SKCanvas(bmp)) c.Clear(color ?? SKColors.SteelBlue);
        var jpeg = bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
        var mk = Encoding.ASCII.GetBytes(make + "\0");
        var md = Encoding.ASCII.GetBytes(model + "\0");
        var dt = Encoding.ASCII.GetBytes(taken + "\0");
        // TIFF: Kopf (8), IFD0 mit drei Einträgen (42), Exif-IFD mit einem (18), dann die Texte
        int mkOff = 68, mdOff = mkOff + mk.Length, dtOff = mdOff + md.Length;
        var t = new MemoryStream();
        var w = new BinaryWriter(t);
        w.Write("II"u8); w.Write((ushort)42); w.Write(8u);
        void Entry(ushort tag, ushort type, uint count, uint value) { w.Write(tag); w.Write(type); w.Write(count); w.Write(value); }
        w.Write((ushort)3);
        Entry(0x010F, 2, (uint)mk.Length, (uint)mkOff);
        Entry(0x0110, 2, (uint)md.Length, (uint)mdOff);
        Entry(0x8769, 4, 1, 50);
        w.Write(0u);
        w.Write((ushort)1);
        Entry(0x9003, 2, (uint)dt.Length, (uint)dtOff);
        w.Write(0u);
        w.Write(mk); w.Write(md); w.Write(dt);
        var tiff = t.ToArray();
        var app1 = new MemoryStream();
        var len = 2 + 6 + tiff.Length;
        app1.Write([0xFF, 0xE1, (byte)(len >> 8), (byte)len]);
        app1.Write("Exif\0\0"u8);
        app1.Write(tiff);
        return [.. jpeg[..2], .. app1.ToArray(), .. jpeg[2..]];
    }

    private static void Put(UserDb db, string path, byte[] data, string? mime = null)
    {
        FileStore.EnsureDir(db, FileStore.Parent(path)!);
        FileStore.Put(db, path, data, mime ?? (path.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "application/octet-stream"));
    }

    private static ImportProfile Add(UserDb db, Action<ImportProfile> setup)
    {
        var p = new ImportProfile { Name = "Fuji", Sources = [new("", "Upload")], Target = "Bilder" };
        setup(p);
        ImportProfiles.Change(db, l => l.Add(p));
        return p;
    }

    private const string Xmp = """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="3"/></rdf:RDF></x:xmpmeta>""";

    [Fact]
    public void Muster_ersetzen_die_Platzhalter_wie_im_CameraImporter()
    {
        var de = ImportProfiles.CultureOf("de");
        var f = ImportProfiles.Example;
        var import = new DateTime(2026, 10, 29, 8, 0, 0);
        Assert.Equal("2026/06/14", ImportProfiles.RenderFolder("{exif_year}/{exif_month}/{exif_day}", f, import, 1, de));
        Assert.Equal("20260614_103045_DSCF0042_FUJIFILM_X100VI",
            ImportProfiles.RenderName("{exif_datetime}_{original_name}_{camera_make}_{camera_model}", f, import, 1, de));
        Assert.Equal("2026/Juni/FUJIFILM X100VI", ImportProfiles.RenderFolder("{exif_year}/{exif_month_name}/{camera_make} {camera_model}", f, import, 1, de));
        Assert.Equal("June", ImportProfiles.RenderFolder("{exif_month_name}", f, import, 1, ImportProfiles.CultureOf("en")));
        // Datum ohne Trennzeichen, laufende Nummer dreistellig
        Assert.Equal("20261029_Oktober_007", ImportProfiles.RenderName("{import_date}_{import_month_name}_{sequence}", f, import, 7, de));
        Assert.Equal("20260614_103045", ImportProfiles.RenderName("{exif_date}_{exif_time}", f, import, 1, de));
        // Foto ohne Titel: statt „_“ der Originalname
        Assert.Equal("DSCF0042", ImportProfiles.RenderName("{title}", f with { Title = null }, import, 1, de));

        // Ohne Kamera: „Unknown“
        var none = f with { Make = null, Model = null };
        Assert.Equal("Unknown/2026", ImportProfiles.RenderFolder("{camera_make}/{exif_year}", none, import, 1, de));
        Assert.Equal("DSCF0042_Unknown", ImportProfiles.RenderName("{original_name}_{camera_model}", none, import, 1, de));
        // Unzulässige Zeichen in Werten werden „_“ und sprengen keinen Pfad
        Assert.Equal("2026/A_B", ImportProfiles.RenderFolder("{exif_year}/{camera_model}", f with { Model = "A/B" }, import, 1, de));
        Assert.Equal("a_b", ImportProfiles.RenderName("a:b", f, import, 1, de));
        Assert.Equal(["kamera"], ImportProfiles.Unknown("{exif_year}/{kamera}"));
    }

    [Fact]
    public void Titel_und_Beschreibung_als_Platzhalter()
    {
        var de = ImportProfiles.CultureOf("de");
        var import = new DateTime(2026, 10, 29, 8, 0, 0);
        var f = ImportProfiles.Example with { Title = "Strand: am Morgen", Description = "Erster\nUrlaubstag" };
        Assert.Equal("20260614_Strand_ am Morgen", ImportProfiles.RenderName("{exif_date}_{title}", f, import, 1, de));
        Assert.Equal("2026/Erster Urlaubstag", ImportProfiles.RenderFolder("{exif_year}/{description}", f, import, 1, de));
        // Im Titel bleibt der Text, wie er ist
        Assert.Equal("Strand: am Morgen – Erster\nUrlaubstag", ImportProfiles.RenderText("{title} – {description}", f, import, 1, de));
        // Ohne Titel: leer statt „_“
        var none = f with { Title = null };
        Assert.Equal("20260614_DSCF0042", ImportProfiles.RenderName("{exif_date}_{title}DSCF0042", none, import, 1, de));
        // Bliebe nur „_“ übrig: der Originalname
        Assert.Equal("DSCF0042", ImportProfiles.RenderName("{title}", none, import, 1, de));
        // Lange Titel im Dateinamen gekürzt
        Assert.Equal(100, ImportProfiles.RenderName("{title}", f with { Title = new string('x', 300) }, import, 1, de).Length);
        Assert.Empty(ImportProfiles.Unknown("{title}/{description}"));
        Assert.Contains("title", ImportProfiles.NameTokens);
        Assert.Contains("description", ImportProfiles.FolderTokens);
    }

    [Fact]
    public void Aufnahmedaten_aus_dem_EXIF()
    {
        var f = ImportProfiles.Read(new MemoryStream(Jpeg()), "DSCF0042", DateTime.UtcNow, TimeZoneInfo.Utc);
        Assert.Equal((new DateTime(2026, 6, 14, 10, 30, 45), true, "FUJIFILM", "X100VI"), (f.Taken, f.FromExif, f.Make, f.Model));
        // Ohne EXIF: die Änderungszeit in der eigenen Zeitzone
        var none = ImportProfiles.Read(new MemoryStream(new byte[100]), "x", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), TimeZoneInfo.Utc);
        Assert.Equal((new DateTime(2026, 1, 2, 3, 4, 5), false), (none.Taken, none.FromExif));
    }

    [Fact]
    public async Task RAW_JPG_und_XMP_werden_zusammen_verschoben()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        Put(db, "Upload/DCIM/DSCF0042.JPG", Jpeg());
        Put(db, "Upload/DCIM/DSCF0042.RAF", new byte[5000]);
        Put(db, "Upload/DCIM/DSCF0042.RAF.xmp", Encoding.UTF8.GetBytes(Xmp), "application/rdf+xml");
        Put(db, "Upload/notiz.txt", "x"u8.ToArray(), "text/plain");
        Put(db, "Upload/DCIM/DSCF0050.JPG", Jpeg(taken: "2026:06:20 09:00:00", color: SKColors.Gold));
        var p = Add(db, x => { x.Folders = "{exif_year}/{exif_month}"; x.Names = "{exif_datetime}_{original_name}_{sequence}"; });

        Assert.Equal(2, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        foreach (var ext in new[] { ".JPG", ".RAF", ".RAF.xmp" })
        {
            Assert.NotNull(FileStore.Get(db, "Bilder/2026/06/20260614_103045_DSCF0042_001" + ext));
            Assert.Null(FileStore.Get(db, "Upload/DCIM/DSCF0042" + ext));
        }
        // Laufende Nummer je Foto (das Paar zählt einmal); was kein Foto ist, bleibt liegen
        Assert.NotNull(FileStore.Get(db, "Bilder/2026/06/20260620_090000_DSCF0050_002.JPG"));
        Assert.NotNull(FileStore.Get(db, "Upload/notiz.txt"));
        var after = ImportProfiles.Of(db).Single();
        Assert.Equal((2, 2, (string?)null), (after.LastCount, after.Total, after.Error));
    }

    [Fact]
    public async Task Doppelte_gleicher_Inhalt_gilt_als_einsortiert_anderer_bekommt_eine_Nummer()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        var jpeg = Jpeg();
        Put(db, "Upload/DSCF0042.JPG", jpeg);
        var p = Add(db, x => { x.Folders = ""; x.Names = "{exif_datetime}"; });
        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));

        // Dasselbe Foto noch einmal hochgeladen: kein zweites, die Quelle wird endgültig gelöscht (nicht in den Papierkorb)
        Put(db, "Upload/DSCF0042.JPG", jpeg);
        Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.Null(FileStore.Get(db, "Upload/DSCF0042.JPG"));
        Assert.Null(FileStore.Get(db, "Bilder/20260614_103045_2.JPG"));
        Assert.DoesNotContain(FileStore.Trash(db), t => t.Path == "Upload/DSCF0042.JPG");

        // Anderes Foto mit derselben Aufnahmezeit: „_2“
        Put(db, "Upload/DSCF0043.JPG", Jpeg(color: SKColors.OrangeRed));
        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "Bilder/20260614_103045.JPG"));
        Assert.NotNull(FileStore.Get(db, "Bilder/20260614_103045_2.JPG"));
    }

    [Fact]
    public async Task Ziel_oberste_Ebene_und_Muster_zurueck_in_die_Quelle()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        Put(db, "Upload/DSCF0042.JPG", Jpeg());
        var p = Add(db, x => { x.Target = ""; x.Folders = "{exif_year}"; });
        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "2026/DSCF0042.JPG"));

        // Muster führt in den Quellordner: Fehler am Profil, das Foto bleibt
        Put(db, "Upload/DSCF0043.JPG", Jpeg(color: SKColors.Green));
        var q = Add(db, x => { x.Target = ""; x.Folders = "Upload"; });
        Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, q.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "Upload/DSCF0043.JPG"));
        Assert.Contains("Quellordner", ImportProfiles.Of(db).Single(x => x.Id == q.Id).Error);
    }

    [Fact]
    public async Task Ordner_aus_Bildangaben_nie_in_Systemordner()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        // [feature:journal]
        // Kameramodell ergibt den Namen des Ordners für Tagebuch-Medien
        Put(db, "Upload/DSCF0050.JPG", Jpeg(model: DmOffice.Journal.JournalMediaStore.Root));
        // [feature:journal:else]
        // // Kameramodell ergibt den Namen eines Systemordners
        // Put(db, "Upload/DSCF0050.JPG", Jpeg(model: DmOffice.Dav.FileStore.ReservedRoot));
        // [/feature:journal]
        var p = Add(db, x => { x.Target = ""; x.Folders = "{camera_model}"; });
        Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "Upload/DSCF0050.JPG"));
        // [feature:journal]
        Assert.Null(FileStore.Get(db, DmOffice.Journal.JournalMediaStore.Root + "/DSCF0050.JPG"));
        // [feature:journal:else]
        // Assert.Null(FileStore.Get(db, DmOffice.Dav.FileStore.ReservedRoot + "/DSCF0050.JPG"));
        // [/feature:journal]
        Assert.Contains("Systemordner", ImportProfiles.Of(db).Single(x => x.Id == p.Id).Error);
    }

    // [feature:importsub]
    [Fact]
    public async Task Ohne_Unterordner_nur_die_Dateien_direkt_im_Quellordner()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        Put(db, "Upload/DSCF0042.JPG", Jpeg());
        Put(db, "Upload/Alt/DSCF0043.JPG", Jpeg(color: SKColors.Green));
        // Ziel liegt in einem Unterordner der Quelle: ohne Unterordner erlaubt
        var p = Add(db, x => { x.Target = "Upload/Sortiert"; x.Folders = "{exif_year}"; x.Subfolders = false; });
        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "Upload/Sortiert/2026/DSCF0042.JPG"));
        Assert.NotNull(FileStore.Get(db, "Upload/Alt/DSCF0043.JPG"));
        Assert.Null(ImportProfiles.Of(db).Single().Error);
        // Zweiter Lauf: das Einsortierte im Unterordner bleibt, wo es ist
        Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "Upload/Sortiert/2026/DSCF0042.JPG"));

        Assert.True(ImportProfiles.InSource(new("", "Upload"), "", "Upload", false));
        Assert.False(ImportProfiles.InSource(new("", "Upload"), "", "Upload/Sortiert", false));
        Assert.True(ImportProfiles.InSource(new("", "Upload"), "", "Upload/Sortiert", true));
    }
    // [/feature:importsub]

    [Fact]
    public async Task Kopieren_laesst_die_Quelle_und_sortiert_nichts_doppelt_ein()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        Put(db, "Upload/DSCF0042.JPG", Jpeg());
        var p = Add(db, x => { x.Copy = true; x.Folders = "{exif_year}"; });
        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "Upload/DSCF0042.JPG"));
        Assert.NotNull(FileStore.Get(db, "Bilder/2026/DSCF0042.JPG"));
        // Zweiter Lauf: schon gesehen - auch wenn das Ziel inzwischen umbenannt wurde
        FileStore.MoveOrCopy(db, "Bilder/2026/DSCF0042.JPG", "Bilder/2026/Strand.JPG", false, false);
        Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.Null(FileStore.Get(db, "Bilder/2026/DSCF0042.JPG"));
    }

    [Fact]
    public async Task Nur_Fotos_ab_dem_Datum_und_Titel_ins_JPEG()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        Put(db, "Upload/alt.JPG", Jpeg(taken: "2025:12:31 23:00:00"));
        Put(db, "Upload/neu.JPG", Jpeg(color: SKColors.Green));
        var p = Add(db, x => { x.Folders = ""; x.Since = new DateOnly(2026, 1, 1); x.Title = "{camera_make} {camera_model}"; x.Description = "Aufgenommen am {exif_date}"; });
        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "Upload/alt.JPG"));
        var text = Encoding.UTF8.GetString(FileStore.Data(db, "Bilder/neu.JPG")!);
        Assert.Contains("FUJIFILM X100VI", text);
        Assert.Contains("Aufgenommen am 20260614", text);
        // Ohne frühere Version (neu geschrieben, nicht ersetzt)
        Assert.Empty(FileStore.Versions(db, FileStore.Get(db, "Bilder/neu.JPG")!.FileId));
    }

    [Fact]
    public async Task Ohne_Datumsgrenze_kommen_frueher_uebersprungene_Fotos_doch()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        Put(db, "Upload/alt.JPG", Jpeg(taken: "2025:12:31 23:00:00"));
        var p = Add(db, x => { x.Folders = ""; x.Since = new DateOnly(2026, 1, 1); });
        Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        // Gleiche Angaben: bleibt übersprungen
        var before = new ImportProfile { Id = p.Id, Since = p.Since, Sources = [.. p.Sources] };
        Assert.False(ImportProfiles.Reconsider(db, before, before));
        Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        // Haken bei „Nur Fotos ab“ weg: jetzt wird es einsortiert
        ImportProfiles.Update(db, p.Id, x => x.Since = null);
        Assert.True(ImportProfiles.Reconsider(db, before, ImportProfiles.Of(db).Single(x => x.Id == p.Id)));
        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "Bilder/alt.JPG"));
    }

    [Fact]
    public async Task Externes_Ziel_im_Galerie_Ordner_steht_gleich_fertig_in_der_Galerie()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        var root = Directory.CreateTempSubdirectory("dmimpgal").FullName;
        var before = Config.Current.ExternalFolders;
        try
        {
            Config.Update(s => s.ExternalFolders = [.. before, new ExternalFolderDef { Id = "impgal", Name = "NAS", Path = root, Users = [space.User.Id] }]);
            GalleryFolders.Save(db, [new GallerySource { Id = "galpri", Mount = "impgal", Path = "PRI", Watch = false }]);
            Put(db, "Upload/DSCF0077.JPG", Jpeg());
            var p = Add(db, x => { x.Mount = "impgal"; x.Target = "PRI"; x.Folders = "{exif_year}"; x.Names = "{original_name}"; });
            Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));

            // Fertig eingetragen (Stufe 2) mit Aufnahmezeit, Kamera und Vorschaubild - ohne Einlesen des Ordners
            var row = db.Query("SELECT stage, camera, taken IS NOT NULL, thumb IS NOT NULL, size, modified FROM gallery_ext WHERE source = 'galpri' AND path = 'PRI/2026/DSCF0077.JPG'",
                r => (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetInt64(4), r.GetInt64(5))).Single();
            Assert.Equal(2, row.Item1);
            Assert.Contains("X100VI", row.Item2);
            Assert.True(row.Item3);
            Assert.True(row.Item4);

            // Das Einlesen kennt die Datei schon: Größe und Zeit stimmen mit dem Speicher überein, sie bleibt fertig
            await GalleryFolders.ScanAsync(space, Config, GalleryFolders.Of(db).Single(), CancellationToken.None);
            Assert.Equal(2L, db.Scalar("SELECT stage FROM gallery_ext WHERE source = 'galpri' AND path = 'PRI/2026/DSCF0077.JPG'"));
        }
        finally
        {
            Config.Update(s => s.ExternalFolders = before);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Import_aus_externem_Ordner_steht_gleich_in_der_Galerie()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        var before = Config.Current.ExternalFolders;
        var from = Directory.CreateTempSubdirectory("dmimpq").FullName;
        var root = Directory.CreateTempSubdirectory("dmimpz").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(from, "Eingang"));
            File.WriteAllBytes(Path.Combine(from, "Eingang", "DSCF0078.JPG"), Jpeg());
            File.SetLastWriteTimeUtc(Path.Combine(from, "Eingang", "DSCF0078.JPG"), DateTime.UtcNow.AddHours(-1));
            Config.Update(s => s.ExternalFolders = [.. before, new ExternalFolderDef { Id = "impvon", Name = "Kamera", Path = from, Users = [space.User.Id] }, new ExternalFolderDef { Id = "impnach", Name = "NAS", Path = root, Users = [space.User.Id] }]);
            GalleryFolders.Save(db, [new GallerySource { Id = "galnach", Mount = "impnach", Path = "PRI", Watch = false }]);
            var p = Add(db, x => { x.Sources = [new("impvon", "Eingang")]; x.Mount = "impnach"; x.Target = "PRI"; x.Folders = "{exif_year}"; x.Names = "{original_name}"; x.Copy = true; });
            Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
            var row = db.Query("SELECT stage, thumb IS NOT NULL FROM gallery_ext WHERE source = 'galnach' AND path LIKE 'PRI/%DSCF0078.JPG'", r => (r.GetInt64(0), r.GetBoolean(1))).Single();
            Assert.Equal((2L, true), row);
        }
        finally
        {
            Config.Update(s => s.ExternalFolders = before);
            Directory.Delete(from, true);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Externes_Ziel_bekommt_die_Dateien_die_Quelle_landet_nicht_im_Papierkorb()
    {
        var (space, _) = NewUser();
        var db = space.Db;
        var root = Directory.CreateTempSubdirectory("dmimport").FullName;
        var before = Config.Current.ExternalFolders;
        try
        {
            Config.Update(s => s.ExternalFolders = [.. before, new ExternalFolderDef { Id = "imp01", Name = "RAW", Path = root }]);
            Put(db, "Upload/DSCF0042.JPG", Jpeg());
            Put(db, "Upload/DSCF0042.RAF", new byte[7000]);
            var p = Add(db, x => { x.Mount = "imp01"; x.Target = "PRI"; x.Folders = "{exif_year}/{exif_month}/{exif_day}"; x.Names = "{exif_datetime}_{original_name}"; });
            Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
            var dir = Path.Combine(root, "PRI", "2026", "06", "14");
            Assert.True(File.Exists(Path.Combine(dir, "20260614_103045_DSCF0042.JPG")));
            Assert.Equal(7000, new FileInfo(Path.Combine(dir, "20260614_103045_DSCF0042.RAF")).Length);
            Assert.Null(FileStore.Get(db, "Upload/DSCF0042.RAF"));
            Assert.DoesNotContain(FileStore.Trash(db), t => t.Path.StartsWith("Upload/DSCF0042"));

            // Ziel weg (Freigabe nicht eingehängt): Fehler am Profil, die Quelle bleibt
            Put(db, "Upload/DSCF0050.JPG", Jpeg(color: SKColors.Gold));
            Directory.Delete(root, true);
            var gone = await Assert.ThrowsAsync<ImportProfiles.TargetUnavailable>(() => ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
            Assert.StartsWith("Der Zielordner ist gerade nicht erreichbar", gone.Message);
            Assert.Equal(gone.Message, ImportProfiles.Of(db).Single(x => x.Id == p.Id).Error);
            Assert.NotNull(FileStore.Get(db, "Upload/DSCF0050.JPG"));
        }
        finally
        {
            Config.Update(s => s.ExternalFolders = before);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Api_anlegen_pruefen_vorschau_loeschen()
    {
        var (space, c) = NewUser();
        var db = space.Db;
        Put(db, "Upload/DSCF0042.JPG", Jpeg());
        var body = new { name = "Fuji", sources = new[] { "Upload" }, mount = "", target = "Bilder", folders = "{exif_year}/{exif_month_name}", names = "{exif_datetime}_{original_name}", every = 10, active = false, language = "de" };

        // Ziel im Quellordner, unbekannter Platzhalter, kein Quellordner: abgelehnt
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/gallery/imports", body with { target = "Upload/sortiert" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/gallery/imports", body with { names = "{kamera}" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/gallery/imports", body with { sources = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/gallery/imports", body with { every = 1 })).StatusCode);

        var r = await c.PostAsJsonAsync("/api/gallery/imports", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/gallery/imports/{id}", body with { name = "Fuji X100VI", every = 30 })).StatusCode);
        var list = await c.GetFromJsonAsync<JsonElement>("/api/gallery/imports");
        var p = list.GetProperty("profiles").EnumerateArray().Single();
        Assert.Equal(("Fuji X100VI", 30), (p.GetProperty("name").GetString(), p.GetProperty("every").GetInt32()));

        // Vorschau mit dem Foto aus dem Quellordner
        var pv = await (await c.PostAsJsonAsync("/api/gallery/imports/preview", new { sources = new[] { "Upload" }, folders = body.folders, names = body.names, title = "{camera_model}", language = "de" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Upload/DSCF0042.JPG", "2026/Juni", "20260614_103045_DSCF0042.JPG", "X100VI"),
            (pv.GetProperty("sample").GetString(), pv.GetProperty("folder").GetString(), pv.GetProperty("name").GetString(), pv.GetProperty("title").GetString()));
        // Ohne Foto: das Beispiel
        pv = await (await c.PostAsJsonAsync("/api/gallery/imports/preview", new { sources = Array.Empty<string>(), folders = "{exif_year}", names = "{original_name}" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("2026", "DSCF0042.JPG"), (pv.GetProperty("folder").GetString(), pv.GetProperty("name").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/gallery/imports/{id}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/gallery/imports/{id}")).StatusCode);
        Assert.Empty(ImportProfiles.Of(db));
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/gallery/imports/{id}")).StatusCode);
    }

    /// <summary>Server-Ordner für einen Test anlegen und danach wieder entfernen.</summary>
    private async Task WithFolderAsync(bool readOnly, Func<string, string, Task> test)
    {
        var root = Directory.CreateTempSubdirectory("dmimpsrc").FullName;
        var id = "src" + Guid.NewGuid().ToString("N")[..6];
        var before = Config.Current.ExternalFolders;
        try
        {
            Config.Update(s => s.ExternalFolders = [.. before, new ExternalFolderDef { Id = id, Name = "Fuji", Path = root, ReadOnly = readOnly }]);
            await test(root, id);
        }
        finally
        {
            Config.Update(s => s.ExternalFolders = before);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public Task Externer_Quellordner_in_die_eigene_Ablage_verschieben() => WithFolderAsync(false, async (root, mount) =>
    {
        var (space, _) = NewUser();
        var db = space.Db;
        var dcim = Directory.CreateDirectory(Path.Combine(root, "Upload", "DCIM")).FullName;
        File.WriteAllBytes(Path.Combine(dcim, "DSCF0042.JPG"), Jpeg());
        File.WriteAllBytes(Path.Combine(dcim, "DSCF0042.RAF"), new byte[9000]);
        File.WriteAllText(Path.Combine(dcim, "DSCF0042.RAF.xmp"), Xmp);
        var p = Add(db, x => { x.Sources = [new(mount, "Upload")]; x.Folders = "{exif_year}/{exif_month}"; x.Names = "{exif_datetime}_{original_name}"; });

        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        foreach (var ext in new[] { ".JPG", ".RAF", ".RAF.xmp" })
        {
            Assert.NotNull(FileStore.Get(db, "Bilder/2026/06/20260614_103045_DSCF0042" + ext));
            // Erst nach dem Vergleich am Ziel endgültig gelöscht
            Assert.False(File.Exists(Path.Combine(dcim, "DSCF0042" + ext)));
        }
        Assert.Equal(9000, FileStore.Get(db, "Bilder/2026/06/20260614_103045_DSCF0042.RAF")!.Size);
        Assert.Null(ImportProfiles.Of(db).Single().Error);
    });

    [Fact]
    public Task Innerhalb_desselben_externen_Ordners_wird_nur_verschoben() => WithFolderAsync(false, async (root, mount) =>
    {
        var (space, _) = NewUser();
        var db = space.Db;
        Directory.CreateDirectory(Path.Combine(root, "Eingang"));
        File.WriteAllBytes(Path.Combine(root, "Eingang", "DSCF0042.JPG"), Jpeg());
        var p = Add(db, x => { x.Sources = [new(mount, "Eingang")]; x.Mount = mount; x.Target = "PRI"; x.Folders = "{exif_year}"; });
        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(root, "PRI", "2026", "DSCF0042.JPG")));
        Assert.False(File.Exists(Path.Combine(root, "Eingang", "DSCF0042.JPG")));
    });

    [Fact]
    public Task Nur_zum_Lesen_eingebunden_kopiert_und_merkt_sich_das_Foto() => WithFolderAsync(true, async (root, mount) =>
    {
        var (space, c) = NewUser();
        var db = space.Db;
        Directory.CreateDirectory(Path.Combine(root, "Fuji"));
        File.WriteAllBytes(Path.Combine(root, "Fuji", "DSCF0042.JPG"), Jpeg());
        var body = new { name = "NAS", sources = new[] { new { mount, path = "Fuji" } }, mount = "", target = "Bilder", folders = "{exif_year}", names = "{original_name}", copy = false, every = 10, active = false };
        // Verschieben löschte in der Quelle: abgelehnt, Kopieren geht
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/gallery/imports", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/gallery/imports", body with { sources = new[] { new { mount, path = "Gibtsnicht" } }, copy = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/imports", body with { copy = true })).StatusCode);
        var p = ImportProfiles.Of(db).Single();
        Assert.Equal(new ImportSource(mount, "Fuji"), p.Sources.Single());

        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
        Assert.NotNull(FileStore.Get(db, "Bilder/2026/DSCF0042.JPG"));
        Assert.True(File.Exists(Path.Combine(root, "Fuji", "DSCF0042.JPG")));
        Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));

        // Vorschau mit dem Foto aus dem externen Ordner
        var pv = await (await c.PostAsJsonAsync("/api/gallery/imports/preview", new { sources = new[] { new { mount, path = "Fuji" } }, folders = "{exif_year}", names = "{exif_datetime}" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Fuji/DSCF0042.JPG", "20260614_103045.JPG"), (pv.GetProperty("sample").GetString(), pv.GetProperty("name").GetString()));
    });

    [Fact]
    public Task Externe_Datei_erst_einsortieren_wenn_sie_sich_nicht_mehr_aendert() => WithFolderAsync(false, async (root, mount) =>
    {
        var (space, _) = NewUser();
        var db = space.Db;
        Directory.CreateDirectory(Path.Combine(root, "In"));
        File.WriteAllBytes(Path.Combine(root, "In", "DSCF0042.JPG"), Jpeg());
        var p = Add(db, x => { x.Sources = [new(mount, "In")]; x.Folders = ""; });
        var settle = ImportProfiles.Settle;
        try
        {
            // Zum ersten Mal gesehen: liegen lassen (könnte noch geschrieben werden)
            ImportProfiles.Settle = TimeSpan.FromHours(1);
            Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(root, "In", "DSCF0042.JPG")));
        }
        finally { ImportProfiles.Settle = settle; }
        Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
    });

    [Fact]
    public async Task Alte_Profile_mit_Pfaden_als_Quellordner_werden_gelesen()
    {
        var (space, _) = NewUser();
        space.Db.Set(ImportProfiles.Key, """[{"Id":"alt1","Name":"Alt","Sources":["Upload","Handy/Fuji"]}]""");
        Assert.Equal([new ImportSource("", "Upload"), new ImportSource("", "Handy/Fuji")], ImportProfiles.Of(space.Db).Single().Sources);
        await Task.CompletedTask;
    }
    [Fact]
    public async Task Ziel_erreichbar_aber_Ablegen_scheitert_nennt_den_Grund_und_Erfolg_raeumt_ihn_weg()
    {
        var (space, c) = NewUser();
        var db = space.Db;
        var root = Directory.CreateTempSubdirectory("dmimpwr").FullName;
        var id = "impwr" + Guid.NewGuid().ToString("N")[..6];
        var before = Config.Current.ExternalFolders;
        try
        {
            Config.Update(s => s.ExternalFolders = [.. before, new ExternalFolderDef { Id = id, Name = "NAS", Path = root, Users = [space.User.Id] }]);
            // Eine Datei, wo der Zielordner hin soll: der externe Ordner ist da, nur das Ablegen scheitert
            File.WriteAllText(Path.Combine(root, "PRI"), "im Weg");
            Put(db, "Upload/DSCF0042.JPG", Jpeg());
            var p = Add(db, x => { x.Name = "FUJI KAMERAS"; x.Mount = id; x.Target = "PRI"; x.Folders = "{exif_year}"; });
            var ok = Add(db, x => { x.Name = "Handy"; x.Sources = [new("", "Handy")]; x.Target = "Bilder"; });

            var e = await Assert.ThrowsAsync<ImportProfiles.RunStopped>(() => ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
            Assert.IsNotType<ImportProfiles.TargetUnavailable>(e);
            Assert.StartsWith("Ablegen im Zielordner fehlgeschlagen (DSCF0042.JPG): ", e.Message);
            Assert.DoesNotContain("nicht erreichbar", e.Message);
            Assert.NotNull(e.InnerException);
            var after = ImportProfiles.Of(db).Single(x => x.Id == p.Id);
            Assert.Equal((e.Message, 0), (after.Error, after.Pending));
            Assert.NotNull(FileStore.Get(db, "Upload/DSCF0042.JPG"));

            // Aktivität: der Fehler steht beim Profil, das scheitert, auch wenn danach ein anderes ohne Fehler lief
            Assert.Equal(0, await ImportProfiles.RunAsync(space, Config, ok.Id, CancellationToken.None));
            var item = await ImportItem(c);
            Assert.Equal("error", item.GetProperty("state").GetString());
            Assert.Equal("FUJI KAMERAS: " + e.Message, item.GetProperty("error").GetString());

            // Behoben: der nächste Lauf räumt den alten Fehler weg, auch in der Aktivität
            File.Delete(Path.Combine(root, "PRI"));
            Assert.Equal(1, await ImportProfiles.RunAsync(space, Config, p.Id, CancellationToken.None));
            Assert.Null(ImportProfiles.Of(db).Single(x => x.Id == p.Id).Error);
            Assert.True(File.Exists(Path.Combine(root, "PRI", "2026", "DSCF0042.JPG")));
            item = await ImportItem(c);
            Assert.Equal("idle", item.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("error").ValueKind);
        }
        finally
        {
            Config.Update(s => s.ExternalFolders = before);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<JsonElement> ImportItem(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/activity")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "gallery-import");

    [Fact]
    public async Task Aktivitaet_zeigt_Fehler_je_Profil_und_nicht_bei_ausgeschalteten()
    {
        var (space, c) = NewUser();
        var db = space.Db;
        Add(db, x => { x.Name = "Eins"; x.Error = "Der Zielordner ist gerade nicht erreichbar."; x.Active = false; });
        Add(db, x => { x.Name = "Zwei"; x.Error = "Ablegen im Zielordner fehlgeschlagen (A.JPG): kein Platz."; });
        Add(db, x => { x.Name = "Drei"; x.Error = "Ein Quellordner ist gerade nicht erreichbar."; });
        Add(db, x => { x.Name = "Vier"; });
        var item = await ImportItem(c);
        Assert.Equal("error", item.GetProperty("state").GetString());
        Assert.Equal("Zwei: Ablegen im Zielordner fehlgeschlagen (A.JPG): kein Platz. · Drei: Ein Quellordner ist gerade nicht erreichbar.", item.GetProperty("error").GetString());
        Assert.Equal("4 Importprofile", item.GetProperty("detail").GetString());
    }

    [Fact]
    public void Grund_fuer_die_Oberflaeche_ohne_Zugangsdaten_und_kurz()
    {
        Assert.Equal("Verbinden: https://nas.example/dav antwortet nicht",
            ExternalFolders.Reason(new IOException("Verbinden: https://max:geheim@nas.example/dav antwortet nicht.\n   at Irgendwo()")));
        Assert.Equal("Schreiben: Failure", ExternalFolders.Reason(new AggregateException(new IOException("Schreiben: Failure"))));
        Assert.True(ExternalFolders.Reason(new IOException(new string('x', 500))).Length <= 201);
    }
}
