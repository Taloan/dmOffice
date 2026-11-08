using System.Text;
using DmOffice.Core;
using DmOffice.Dms;
using DmOffice.Storage;
using D = DmOffice.Dms.Dms;

namespace DmOffice.Tests;

/// <summary>DMS mit externem Ordner als Speicherort (docs/dms-konzept.md, „Externer Ordner als Speicherort“).</summary>
public class DmsExternalTests
{
    private static (UserSpace Space, DmsHost Host, ServerConfig Config, string Root) NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmdmsx");
        var root = Directory.CreateDirectory(Path.Combine(dir.FullName, "nas")).FullName;
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var config = new ServerConfig(new DataPaths(Path.Combine(dir.FullName, "data")));
        config.Update(s => s.ExternalFolders =
        [
            new ExternalFolderDef { Id = "nas", Name = "NAS", Path = root },
            new ExternalFolderDef { Id = "ro", Name = "Nur lesen", Path = root, ReadOnly = true },
        ]);
        var host = new DmsHost(config);
        DmsHost.Current = host;
        return (new UserSpace(new UserRecord { Login = "t", Id = "u" + Guid.NewGuid().ToString("N")[..6] }, db), host, config, root);
    }

    private static async Task<DmsDoc> Import(UserSpace space, DmsHost host, string name, string text, string profile)
    {
        var r = await DmsImport.ImportAsync(space, new MemoryStream(Encoding.UTF8.GetBytes(text)), name, "text/plain", new DmsImport.Info("upload", Profile: profile), default);
        await DmsImport.ProcessQueueAsync(space, host, 100, default);
        return D.Get(space.Db, r.Id);
    }

    private static string Disk(string root, string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Ablegen_neu_ablegen_wiedererkennen_und_loeschen_im_externen_Ordner()
    {
        var (space, host, _, root) = NewSpace();
        // Nur zum Lesen geht nicht als Ziel
        Assert.Throws<ArgumentException>(() => DmsFiling.Save(space, new DmsProfile { Name = "RO", Target = "external", Mount = "ro", Folder = "Archiv" }, host, host));
        var profile = DmsFiling.Save(space, new DmsProfile { Name = "NAS", Target = "external", Mount = "nas", Folder = "Archiv", Folders = "{correspondent}",
            Names = "{title}" }, host, host);
        D.Create(space.Db, D.Correspondent, "Telekom", null, null, D.Any, "Telekom", false);
        var stadtwerke = D.Create(space.Db, D.Correspondent, "Stadtwerke", null, null, D.None, null, false);

        // Abgelegt im externen Ordner, kein Original im Blob-Bereich, Text und Suche wie gewohnt
        var doc = await Import(space, host, "rechnung.txt", "Telekom Rechnung\nBetrag 39,95 EUR vom 12.10.2026", profile.Id);
        Assert.Equal("done", doc.State);
        Assert.Equal("nas", doc.Mount);
        Assert.Equal(0, doc.FileId);
        Assert.Equal("Archiv/.INBOX/rechnung.txt", doc.Path);
        Assert.True(File.Exists(Disk(root, doc.Path!)));
        Assert.False(doc.HasOriginal);
        Assert.Single(D.Docs(space.Db, new D.Filter(Query: "betrag")).Items);
        Assert.Equal(1, D.InboxCount(space.Db));
        Assert.Equal("Telekom Rechnung\nBetrag 39,95 EUR vom 12.10.2026", Encoding.UTF8.GetString(await DmsImport.ReadFileAsync(space, host, space.Db, doc, default)));
        using (var o = DmsImport.OpenOriginal(space, host, doc.Id, out var name, out _))
            Assert.Equal("rechnung.txt", name);

        // Bestätigen: aus dem Inbox-Ordner nach dem Ordnermuster, im externen Ordner verschoben
        D.Apply(space.Db, [doc.Id], new D.Change(Correspondent: stadtwerke.Id, Confirm: true));
        Assert.Equal("Archiv/Stadtwerke/rechnung.txt", DmsImport.Refile(space, host, doc.Id));
        Assert.False(File.Exists(Disk(root, "Archiv/.INBOX/rechnung.txt")));
        Assert.True(File.Exists(Disk(root, "Archiv/Stadtwerke/rechnung.txt")));

        // Draußen verschoben und umbenannt: über die Prüfsumme wiedererkannt, gilt dann als von Hand abgelegt
        Directory.CreateDirectory(Disk(root, "Archiv/Alt"));
        File.Move(Disk(root, "Archiv/Stadtwerke/rechnung.txt"), Disk(root, "Archiv/Alt/rechnung-alt.txt"));
        await DmsImport.ScanExternalAsync(space, host, default);
        doc = D.Get(space.Db, doc.Id);
        Assert.Equal("Archiv/Alt/rechnung-alt.txt", doc.Path);
        Assert.True(doc.Manual);
        Assert.Null(DmsImport.Refile(space, host, doc.Id));

        // Neuer Inhalt draußen: Text neu gelesen
        File.WriteAllText(Disk(root, "Archiv/Alt/rechnung-alt.txt"), "Telekom Mahnung Betrag 79,90 EUR");
        File.SetLastWriteTimeUtc(Disk(root, "Archiv/Alt/rechnung-alt.txt"), DateTime.UtcNow.AddMinutes(1));
        await DmsImport.ScanExternalAsync(space, host, default);
        Assert.Single(D.Docs(space.Db, new D.Filter(Query: "mahnung")).Items);

        // Externer Ordner nicht da (Zielordner fehlt): nichts wird vergessen
        Directory.Move(Disk(root, "Archiv"), Disk(root, "Archiv-weg"));
        await DmsImport.ScanExternalAsync(space, host, default);
        Assert.Equal(doc.Id, D.Get(space.Db, doc.Id).Id);
        Directory.Move(Disk(root, "Archiv-weg"), Disk(root, "Archiv"));

        // Draußen gelöscht: verschwindet aus dem DMS
        File.Delete(Disk(root, "Archiv/Alt/rechnung-alt.txt"));
        await DmsImport.ScanExternalAsync(space, host, default);
        Assert.Throws<KeyNotFoundException>(() => D.Get(space.Db, doc.Id));

        // Löschen im DMS: draußen endgültig
        var second = await Import(space, host, "strom.txt", "Stadtwerke Abrechnung Strom 2026", profile.Id);
        Assert.True(await DmsImport.RemoveFileAsync(space, host, space.Db, second, purge: false, default));
        Assert.False(File.Exists(Disk(root, second.Path!)));
    }

    [Fact]
    public void Original_im_Unterordner_und_Angaben_fuer_die_PDF()
    {
        Assert.Equal("Archiv/Telekom/Original/2026-03-14 Bescheid.jpg", DmsImport.OriginalPath("Archiv/Telekom/2026-03-14 Bescheid.pdf", "IMG_0042.JPG"));
        Assert.Equal("Original/Brief.png", DmsImport.OriginalPath("Brief.pdf", "scan.png"));
        // UTF-16 mit BOM, damit Umlaute in DOCINFO und XMP ankommen
        Assert.Equal("<FEFF00C40072007A0074>", DmsArchive.PsText("Ärzt"));
        var info = DmsArchive.DocInfo(new DmsArchive.PdfInfo("Befund", "Dr. Muster", null, "gesundheit"));
        Assert.Contains("/Title <FEFF", info);
        Assert.Contains("/Author <FEFF", info);
        Assert.DoesNotContain("/Subject", info);
        Assert.EndsWith("/DOCINFO pdfmark", info);
    }
}
