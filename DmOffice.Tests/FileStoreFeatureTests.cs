using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Papierkorb, Versionen, Suche, Vorschaubilder und der Schutz des notizi-Ordners direkt am FileStore.</summary>
public class FileStoreFeatureTests
{
    private static UserDb NewDb(string? root = null)
    {
        var dir = Directory.CreateTempSubdirectory("dmfiles");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "files-" + Guid.NewGuid().ToString("N"));
        if (root != null) db.SetJson("settings", new UserSettings { Notizi = new NotiziSettings { Root = root } });
        return db;
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Ordner_mit_anderer_Schreibweise_oder_Sonderzeichen_bleiben_getrennt()
    {
        // Frueher per LIKE ohne Ruecksicht auf Gross-/Kleinschreibung: unter "Fotos" kam auch "fotos" mit
        var db = NewDb();
        FileStore.EnsureDir(db, "Fotos");
        FileStore.EnsureDir(db, "fotos");
        FileStore.Put(db, "Fotos/a.txt", B("a"), "text/plain");
        FileStore.Put(db, "fotos/geheim.txt", B("g"), "text/plain");
        Assert.Equal(new[] { "Fotos/a.txt" }, FileStore.Descendants(db, "Fotos").Select(f => f.Path).ToArray());
        // Platzhalter im Namen sind keine Platzhalter
        FileStore.EnsureDir(db, "x*");
        FileStore.EnsureDir(db, "xy");
        FileStore.Put(db, "x*/1.txt", B("1"), "text/plain");
        FileStore.Put(db, "xy/2.txt", B("2"), "text/plain");
        Assert.Equal(new[] { "x*/1.txt" }, FileStore.Descendants(db, "x*").Select(f => f.Path).ToArray());
        // Loeschen von "Fotos" nimmt "fotos" nicht mit
        FileStore.Delete(db, "Fotos");
        Assert.NotNull(FileStore.Get(db, "fotos/geheim.txt"));
        Assert.Null(FileStore.Get(db, "Fotos/a.txt"));
    }

    [Fact]
    public void Loeschen_landet_im_Papierkorb_und_kommt_mit_derselben_fileid_zurueck()
    {
        var db = NewDb();
        FileStore.EnsureDir(db, "a/b");
        FileStore.Put(db, "a/b/x.txt", B("hallo"), "text/plain");
        FileStore.Put(db, "a/y.txt", B("welt"), "text/plain");
        var id = FileStore.Get(db, "a/b/x.txt")!.FileId;
        FileStore.Favorite(db, id, true);

        Assert.True(FileStore.Delete(db, "a", out var trashId));
        Assert.True(trashId > 0);
        Assert.Null(FileStore.Get(db, "a/b/x.txt"));
        var t = Assert.Single(FileStore.Trash(db));
        Assert.Equal("a", t.Path);
        Assert.True(t.IsDir);
        Assert.Equal(9, t.Size);
        // Favorit haengt an der fileid und ueberlebt den Papierkorb
        Assert.Empty(FileStore.Favorites(db));

        Assert.Equal("a", FileStore.Restore(db, trashId));
        Assert.Equal("hallo", Encoding.UTF8.GetString(FileStore.Data(db, "a/b/x.txt")!));
        Assert.Equal(id, FileStore.Get(db, "a/b/x.txt")!.FileId);
        Assert.Single(FileStore.Favorites(db));
        Assert.Empty(FileStore.Trash(db));
    }

    [Fact]
    public void Wiederherstellen_legt_fehlende_Ordner_an_und_nummeriert_belegte_Namen()
    {
        var db = NewDb();
        FileStore.EnsureDir(db, "x/y");
        FileStore.Put(db, "x/y/d.txt", B("1"), null);
        FileStore.Delete(db, "x/y/d.txt", out var t1);
        FileStore.Delete(db, "x", out _);
        Assert.Equal("x/y/d.txt", FileStore.Restore(db, t1));
        Assert.True(FileStore.Get(db, "x/y")!.IsDir);

        FileStore.Delete(db, "x/y/d.txt", out var t2);
        FileStore.Put(db, "x/y/d.txt", B("neu"), null);
        Assert.Equal("x/y/d (2).txt", FileStore.Restore(db, t2));
        Assert.Equal("x/y/d (3).txt", FileStore.FreeName(db, "x/y/d (2).txt"));
        Assert.Equal("x/y/neu.txt", FileStore.FreeName(db, "x/y/neu.txt"));
    }

    [Fact]
    public void Ersetzen_beim_Verschieben_legt_das_alte_Ziel_in_den_Papierkorb()
    {
        var db = NewDb();
        FileStore.Put(db, "a.txt", B("A"), null);
        FileStore.Put(db, "b.txt", B("B"), null);
        FileStore.MoveOrCopy(db, "a.txt", "b.txt", overwrite: true, copy: false, out var replaced);
        Assert.True(replaced > 0);
        Assert.Equal("A", Encoding.UTF8.GetString(FileStore.Data(db, "b.txt")!));
        Assert.Equal("b.txt", Assert.Single(FileStore.Trash(db)).Path);
    }

    [Fact]
    public void Notizi_Ordner_ohne_Papierkorb_ohne_Versionen_und_nicht_in_Kopien()
    {
        var db = NewDb("apps/notizi");
        FileStore.EnsureDir(db, "apps/notizi/x");
        FileStore.Put(db, "apps/notizi/x/db.bin", B("geheim"), null);
        FileStore.Put(db, "apps/notizi/x/db.bin", B("geheim2"), null);
        FileStore.Put(db, "apps/sonst.txt", B("offen"), null);
        Assert.Empty(FileStore.Versions(db, FileStore.Get(db, "apps/notizi/x/db.bin")!.FileId));

        // Kopie des Ordners darueber: ohne den notizi-Ordner
        FileStore.MoveOrCopy(db, "apps", "kopie", false, copy: true);
        Assert.NotNull(FileStore.Get(db, "kopie/sonst.txt"));
        Assert.Null(FileStore.Get(db, "kopie/notizi"));

        // Loeschen darin: endgueltig, nichts im Papierkorb
        Assert.True(FileStore.Delete(db, "apps/notizi/x/db.bin", out var t));
        Assert.Equal(0, t);
        Assert.Empty(FileStore.Trash(db));

        // Loeschen des Ordners darueber (WebDAV): der Papierkorb bekommt alles ausser dem notizi-Ordner
        FileStore.Put(db, "apps/notizi/x/db2.bin", B("geheim"), null);
        FileStore.Delete(db, "apps", out var t2);
        Assert.True(t2 > 0);
        Assert.Equal(5, Assert.Single(FileStore.Trash(db)).Size);
        var restored = FileStore.Restore(db, t2);
        Assert.Null(FileStore.Get(db, restored + "/notizi"));
        Assert.Null(FileStore.Get(db, restored + "/notizi/x/db2.bin"));
        Assert.True(FileStore.Contains("apps", "apps/notizi"));
        Assert.True(FileStore.Contains("", "apps/notizi"));
        Assert.False(FileStore.Contains("apps/notizi", "apps/notizi"));
    }

    [Fact]
    public void Ueberschreiben_sichert_Versionen_und_stellt_sie_wieder_her()
    {
        var db = NewDb();
        FileStore.Put(db, "t.txt", B("eins"), null);
        FileStore.Put(db, "t.txt", B("eins"), null);   // gleicher Inhalt: keine Version
        FileStore.Put(db, "t.txt", B("zwei"), null);
        var f = FileStore.Get(db, "t.txt")!;
        var v = Assert.Single(FileStore.Versions(db, f.FileId));
        Assert.Equal("eins", Encoding.UTF8.GetString(FileStore.VersionData(db, f.FileId, v.Id)!));
        FileStore.RestoreVersion(db, "t.txt", v.Id);
        Assert.Equal("eins", Encoding.UTF8.GetString(FileStore.Data(db, "t.txt")!));
        Assert.Equal(2, FileStore.Versions(db, f.FileId).Count);
        // Vorgabe "auto": erst einmal alles behalten, das Ausduennen raeumt stuendlich auf
        for (int i = 0; i < 30; i++) FileStore.Put(db, "t.txt", B("n" + i), null);
        Assert.Equal(32, FileStore.Versions(db, f.FileId).Count);
    }

    private static FileRetention.Settings With(UserDb db, Func<FileRetention.Settings, FileRetention.Settings> change)
    {
        var s = change(FileRetention.Load(db));
        FileRetention.Save(db, s);
        return s;
    }

    /// <summary>Versionen zurueckdatieren: die neueste bekommt "jetzt minus ages[0]", die naechste ages[1] usw.</summary>
    private static void Backdate(UserDb db, long fileId, DateTime now, params TimeSpan[] ages)
    {
        var ids = FileStore.Versions(db, fileId).Select(v => v.Id).OrderByDescending(x => x).ToList();
        for (int i = 0; i < ids.Count && i < ages.Length; i++)
            db.Exec("UPDATE file_versions SET saved=$s WHERE id=$i", ("$s", (now - ages[i]).ToString("o")), ("$i", ids[i]));
    }

    [Fact]
    public void Ausduennen_behaelt_je_Stunde_Tag_und_Woche_die_neueste()
    {
        var now = new DateTime(2026, 10, 20, 12, 30, 0, DateTimeKind.Utc);
        TimeSpan H(double h) => TimeSpan.FromHours(h);
        var v = new (long, DateTime)[]
        {
            (1, now - H(0.1)), (2, now - H(0.2)),          // dieselbe Stunde: nur 1
            (3, now - H(2)),                                // andere Stunde
            (4, now - H(30)), (5, now - H(31)),             // gestern: ein Tag -> nur 4
            (6, now - TimeSpan.FromDays(40)), (7, now - TimeSpan.FromDays(40.1)),   // dieselbe Woche: nur 6
            (8, now - TimeSpan.FromDays(400)),              // aelter als Hoechstalter
        };
        Assert.Equal(new HashSet<long> { 1, 3, 4, 6 }, FileRetention.Thin(v, now, 365));
        Assert.Contains(8L, FileRetention.Thin(v, now, 0));   // 0 = unbegrenzt
    }

    [Fact]
    public void Auto_duennt_aus_fixed_kuerzt_off_loescht_alles()
    {
        var db = NewDb();
        var now = DateTime.UtcNow;
        for (int i = 0; i < 6; i++) FileStore.Put(db, "t.txt", B("v" + i), null);
        var id = FileStore.Get(db, "t.txt")!.FileId;
        Assert.Equal(5, FileStore.Versions(db, id).Count);
        // zwei in derselben Stunde vor 3 Tagen, eine vor 2 Jahren
        Backdate(db, id, now, TimeSpan.Zero, TimeSpan.FromHours(1), TimeSpan.FromDays(3), TimeSpan.FromDays(3).Add(TimeSpan.FromMinutes(1)), TimeSpan.FromDays(800));
        FileRetention.Apply(db, now);
        Assert.Equal(3, FileStore.Versions(db, id).Count);

        With(db, s => s with { VersionsMode = "fixed", VersionsMax = 2, VersionsDays = 0 });
        FileRetention.Apply(db, now);
        Assert.Equal(2, FileStore.Versions(db, id).Count);
        FileStore.Put(db, "t.txt", B("neu1"), null);
        FileStore.Put(db, "t.txt", B("neu2"), null);
        Assert.Equal(2, FileStore.Versions(db, id).Count);   // schon beim Speichern begrenzt

        With(db, s => s with { VersionsMode = "off" });
        FileStore.Put(db, "t.txt", B("neu3"), null);
        Assert.Equal(2, FileStore.Versions(db, id).Count);   // keine neue Version
        FileRetention.Apply(db, now);
        Assert.Empty(FileStore.Versions(db, id));
    }

    [Fact]
    public void Vorschau_aus_Bildbytes_ist_klein_und_kein_Bild_gibt_null()
    {
        using var bmp = new SKBitmap(800, 400);
        bmp.Erase(SKColors.Orange);
        using var img = SKImage.FromBitmap(bmp);
        var png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        var thumb = Thumbs.FromBytes(png, 160);
        Assert.NotNull(thumb);
        Assert.True(thumb!.Length < png.Length);
        using var small = SKBitmap.Decode(thumb);
        Assert.True(Math.Max(small.Width, small.Height) <= 160);
        Assert.Null(Thumbs.FromBytes(B("das ist kein Bild"), 160));
    }

    [Fact]
    public void Hoechstwerte_der_Verwaltung_begrenzen_die_Einstellungen()
    {
        var db = NewDb();
        var before = FileRetention.LimitsOf;
        FileRetention.LimitsOf = () => new FileLimits { VersionsDays = 90, VersionsMb = 100, TrashDays = 30, LinkMaxDownloads = 25 };
        try
        {
            With(db, s => s with { VersionsDays = 0, VersionsMb = 0, TrashDays = 365, TrashMb = 10, LinkMaxDownloads = 0, LinkDays = 7 });
            var e = FileRetention.Load(db);
            Assert.Equal(90, e.VersionsDays);       // "unbegrenzt" gibt es nicht mehr
            Assert.Equal(100, e.VersionsMb);
            Assert.Equal(30, e.TrashDays);           // 365 > 30
            Assert.Equal(10, e.TrashMb);             // kleiner als erlaubt bleibt
            Assert.Equal(25, e.LinkMaxDownloads);
            Assert.Equal(7, e.LinkDays);             // ohne Vorgabe frei
        }
        finally { FileRetention.LimitsOf = before; }
    }

    [Fact]
    public void Speichergrenzen_werfen_aelteste_Versionen_und_Papierkorbeintraege_zuerst_raus()
    {
        var db = NewDb();
        var mb = 1024 * 1024;
        FileStore.Put(db, "a.bin", new byte[mb], null);
        FileStore.Put(db, "a.bin", Enumerable.Repeat((byte)1, mb).ToArray(), null);
        FileStore.Put(db, "a.bin", Enumerable.Repeat((byte)2, mb).ToArray(), null);
        var id = FileStore.Get(db, "a.bin")!.FileId;
        var newest = FileStore.Versions(db, id).Max(v => v.Id);
        FileStore.Put(db, "b.bin", new byte[mb], null);
        FileStore.Put(db, "c.bin", new byte[mb], null);
        FileStore.Delete(db, "b.bin");
        FileStore.Delete(db, "c.bin");
        With(db, s => s with { VersionsMb = 1, TrashMb = 1, TrashDays = 0 });
        FileRetention.Apply(db, DateTime.UtcNow);
        Assert.Equal(newest, Assert.Single(FileStore.Versions(db, id)).Id);
        Assert.Equal("c.bin", Assert.Single(FileStore.Trash(db)).Path);
        Assert.Equal(0, FileRetention.Load(db).TrashDays);
    }

    [Fact]
    public void Volle_Grenze_raeumt_erst_Versionen_dann_den_Papierkorb()
    {
        var db = NewDb();
        var before = FileStore.QuotaOf;
        FileStore.QuotaOf = u => u == db.UserId ? UserStorage.Database(db) + 1000 : before?.Invoke(u) ?? 0;
        try
        {
            FileStore.Put(db, "a.bin", new byte[400], null);
            FileStore.Put(db, "a.bin", Enumerable.Repeat((byte)1, 400).ToArray(), null);   // 400 als Version
            FileStore.Put(db, "b.bin", new byte[100], null);
            FileStore.Delete(db, "b.bin");                                                  // 100 im Papierkorb
            Assert.Equal(900, FileStore.UsageTotal(db));
            FileStore.Put(db, "c.bin", new byte[300], null);                                // braucht Platz: Version weicht
            Assert.Empty(FileStore.Versions(db, FileStore.Get(db, "a.bin")!.FileId));
            Assert.Single(FileStore.Trash(db));
            FileStore.Put(db, "d.bin", new byte[250], null);                                // jetzt auch der Papierkorb
            Assert.Empty(FileStore.Trash(db));
            Assert.True(FileStore.UsageTotal(db) <= 1000);
            Assert.Throws<QuotaExceededException>(() => FileStore.Put(db, "e.bin", new byte[100], null));
        }
        finally { FileStore.QuotaOf = before; }
    }

    [Fact]
    public void Ablauf_leert_den_Papierkorb_und_raeumt_Verwaistes()
    {
        var db = NewDb();
        FileStore.Put(db, "alt.txt", B("x"), null);
        var id = FileStore.Get(db, "alt.txt")!.FileId;
        FileStore.Favorite(db, id, true);
        var link = Links.Create(db, id, "read", null, null);
        FileStore.Delete(db, "alt.txt");
        Assert.Equal(0, FileStore.Expire(db, 30, DateTime.UtcNow));
        Assert.NotNull(Links.Get(db, link.Id));
        Assert.Equal(1, FileStore.Expire(db, 30, DateTime.UtcNow.AddDays(31)));
        Assert.Empty(FileStore.Trash(db));
        Assert.Null(Links.Get(db, link.Id));
        Assert.DoesNotContain(id, FileStore.FavoriteIds(db));
    }

    [Fact]
    public void Suche_nach_Art_filtert_vor_der_Grenze()
    {
        var db = NewDb();
        // Viele andere Dateien vor den Bildern: die Bilder duerfen nicht hinter der Grenze verschwinden
        for (var i = 0; i < 20; i++) FileStore.Put(db, $"doc{i}.txt", B("x"), null);
        for (var i = 0; i < 3; i++) FileStore.Put(db, $"foto{i}.jpg", B("x"), null);
        var hits = FileStore.Search(db, "", "", 5, f => FileKinds.Of(f) == "image");
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Suche_findet_Namen_ohne_Ruecksicht_auf_Gross_und_Umlaute()
    {
        var db = NewDb();
        FileStore.EnsureDir(db, "Büro/Rechnungen");
        FileStore.Put(db, "Büro/Rechnungen/Übersicht 2026.xlsx", B("x"), null);
        FileStore.Put(db, "Büro/notiz.txt", B("x"), null);
        Assert.Equal("Büro/Rechnungen/Übersicht 2026.xlsx", Assert.Single(FileStore.Search(db, "", "ubersicht")).Path);
        Assert.Single(FileStore.Search(db, "Büro/Rechnungen", "2026"));
        Assert.Empty(FileStore.Search(db, "Büro/Rechnungen", "notiz"));
        Assert.Equal(2, FileStore.Search(db, "", "RECHNUNG").Count + FileStore.Search(db, "", "notiz").Count);
    }

    [Fact]
    public void Inhalt_kommt_als_Stream_mit_Bereichen()
    {
        var db = NewDb();
        var data = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        FileStore.Put(db, "gross.bin", data, null);
        using var s = FileStore.OpenRead(db, "gross.bin");
        Assert.True(s.CanSeek);
        Assert.Equal(data.Length, s.Length);
        s.Seek(123_456, SeekOrigin.Begin);
        var buf = new byte[10];
        s.ReadExactly(buf);
        Assert.Equal(data.Skip(123_456).Take(10), buf);
        FileStore.Put(db, "leer.bin", [], null);
        using var e = FileStore.OpenRead(db, "leer.bin");
        Assert.Equal(0, e.Length);
    }

    private static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.Red); c.DrawRect(0, 0, w / 2, h, new SKPaint { Color = SKColors.Blue }); }
        return SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static SKBitmap DecodeWebp(byte[] data)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(data, 0, 4));
        return SKBitmap.Decode(data);
    }

    [Fact]
    public async Task Vorschaubild_fuer_Bild_verkleinert_und_gecacht()
    {
        var db = NewDb();
        FileStore.Put(db, "foto.png", Png(1200, 600), "image/png");
        var f = FileStore.Get(db, "foto.png")!;
        var small = await Thumbs.Get(db, f, 256, default);
        using var bmp = DecodeWebp(small!);
        Assert.Equal(256, bmp.Width);
        Assert.Equal(128, bmp.Height);
        // Zweiter Abruf aus dem Cache, neuer Inhalt macht ihn ungueltig
        Assert.Equal(small, await Thumbs.Get(db, f, 200, default));
        FileStore.Put(db, "foto.png", Png(100, 400), "image/png");
        using var bmp2 = DecodeWebp((await Thumbs.Get(db, FileStore.Get(db, "foto.png")!, 256, default))!);
        Assert.Equal((64, 256), (bmp2.Width, bmp2.Height));
        // Kein Bild: keins, auch nicht beim zweiten Mal
        FileStore.Put(db, "kaputt.jpg", B("kein jpeg"), "image/jpeg");
        Assert.Null(await Thumbs.Get(db, FileStore.Get(db, "kaputt.jpg")!, 256, default));
        Assert.Null(await Thumbs.Get(db, FileStore.Get(db, "kaputt.jpg")!, 256, default));
    }

    [Fact]
    public async Task Vorschaubild_nach_altem_Fehlversuch_neu_erzeugt()
    {
        // Fruehere Versionen merkten sich auch voruebergehende Lesefehler als "kein Vorschaubild" - fuer immer
        var db = NewDb();
        FileStore.Put(db, "urlaub.png", Png(600, 300), "image/png");
        var f = FileStore.Get(db, "urlaub.png")!;
        db.Exec("INSERT INTO file_thumbs(fileid,size,etag,data,blob) VALUES($f,256,$e,X'',NULL)", ("$f", f.FileId), ("$e", f.Etag));
        // Einmal je Datenbank werden solche leeren Eintraege weggeraeumt - danach entsteht das Bild
        using var bmp = DecodeWebp((await Thumbs.Get(db, f, 256, default))!);
        Assert.Equal(256, bmp.Width);
    }

    /// <summary>JPEG mit EXIF-Ausrichtung 6 (Handy hochkant): Vorschau muss hochkant sein.</summary>
    [Fact]
    public async Task Vorschaubild_beachtet_EXIF_Ausrichtung()
    {
        using var bmp = new SKBitmap(400, 200);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Green);
        var jpeg = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        // APP1/Exif mit einem Eintrag: Orientation (0x0112) = 6, big-endian TIFF
        byte[] tiff = [0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0, 0, 0];
        var exif = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var app1 = new byte[] { 0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)((exif.Length + 2) & 0xFF) }.Concat(exif);
        var withExif = jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();
        var db = NewDb();
        FileStore.Put(db, "hoch.jpg", withExif, "image/jpeg");
        using var thumb = DecodeWebp((await Thumbs.Get(db, FileStore.Get(db, "hoch.jpg")!, 256, default))!);
        Assert.Equal((128, 256), (thumb.Width, thumb.Height));
    }

    [Fact]
    public async Task Vorschaubild_fuer_PDF_zeigt_die_erste_Seite()
    {
        using var ms = new MemoryStream();
        using (var doc = SKDocument.CreatePdf(ms))
        {
            var page = doc.BeginPage(595, 842);
            page.Clear(SKColors.White);
            page.DrawRect(0, 0, 595, 421, new SKPaint { Color = SKColors.Blue });
            doc.EndPage();
            doc.Close();
        }
        var db = NewDb();
        FileStore.Put(db, "brief.pdf", ms.ToArray(), "application/pdf");
        var data = await Thumbs.Get(db, FileStore.Get(db, "brief.pdf")!, 256, default);
        using var thumb = DecodeWebp(data!);
        Assert.Equal(256, thumb.Height);
        Assert.InRange(thumb.Width, 175, 185);
        // Oben blau, unten weiss
        Assert.True(thumb.GetPixel(thumb.Width / 2, 20).Blue > 200 && thumb.GetPixel(thumb.Width / 2, 20).Red < 60);
        Assert.True(thumb.GetPixel(thumb.Width / 2, 240).Red > 200);
    }

    [Fact]
    public void Standbild_fuer_Video_wird_neu_kodiert()
    {
        var db = NewDb();
        FileStore.Put(db, "film.mp4", B("kein echtes video"), "video/mp4");
        var f = FileStore.Get(db, "film.mp4")!;
        Assert.False(Thumbs.Accept(db, f, B("<script>alert(1)</script>")));
        Assert.True(Thumbs.Accept(db, f, Png(640, 360)));
        var data = Thumbs.Get(db, f, 256, default).Result;
        using var thumb = DecodeWebp(data!);
        Assert.Equal(256, thumb.Width);
    }
}
