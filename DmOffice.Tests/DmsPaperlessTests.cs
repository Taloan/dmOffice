using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Dms;
using D = DmOffice.Dms.Dms;

namespace DmOffice.Tests;

public class DmsPaperlessTests
{
    private static (UserSpace Space, DmsHost Host) NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmdmspl");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var host = new DmsHost(new ServerConfig(new DataPaths(Path.Combine(dir.FullName, "data"))));
        DmsHost.Current = host;
        return (new UserSpace(new UserRecord { Login = "t", Id = "u" + Guid.NewGuid().ToString("N")[..6] }, db), host);
    }

    private static void Put(UserSpace space, string path, string text)
    {
        FileStore.EnsureDir(space.Db, FileStore.Parent(path)!);
        FileStore.Put(space.Db, path, Encoding.UTF8.GetBytes(text), null);
    }

    /// <summary>Ein kleiner Export wie von document_exporter: zwei Korrespondenten (Auto), ein Dokumenttyp (Wort), Tags samt Posteingang-Tag.</summary>
    private static void Export(UserSpace space, int bank = 8, int arzt = 8, bool wrongLast = false)
    {
        var list = new List<object>
        {
            new { model = "documents.correspondent", pk = 1, fields = new { name = "22_Banken", match = "", matching_algorithm = 6, is_insensitive = true } },
            new { model = "documents.correspondent", pk = 2, fields = new { name = "10_Ärzte", match = "", matching_algorithm = 6, is_insensitive = true } },
            new { model = "documents.documenttype", pk = 1, fields = new { name = "Rechnung", match = "Rechnung", matching_algorithm = 1, is_insensitive = true } },
            new { model = "documents.tag", pk = 1, fields = new { name = "[neu]", color = "#75dca7", match = "", matching_algorithm = 0, is_insensitive = true, is_inbox_tag = true } },
            new { model = "documents.tag", pk = 2, fields = new { name = "gesundheit", color = "#e598cb", match = "", matching_algorithm = 6, is_insensitive = true, is_inbox_tag = false } },
        };
        var pk = 1;
        void Doc(string text, int correspondent, int[] tags, bool archive)
        {
            var name = $"originals/{pk:D7}.txt";
            Put(space, "Export/" + name, text + " " + pk);
            if (archive) Put(space, $"Export/archive/{pk:D7}.pdf", "%PDF-1.4 archiv " + pk);
            list.Add(new Dictionary<string, object?>
            {
                ["model"] = "documents.document", ["pk"] = pk,
                ["fields"] = new { title = $"Dokument {pk}", content = text + " " + pk, created = "2024-05-0" + (pk % 9 + 1), added = "2024-06-01T10:00:00Z",
                    correspondent = correspondent, document_type = (int?)null, tags, mime_type = "text/plain", original_filename = $"scan{pk}.txt" },
                ["__exported_file_name__"] = name,
                ["__exported_archive_name__"] = archive ? $"archive/{pk:D7}.pdf" : null,
            });
            pk++;
        }
        for (var i = 0; i < bank; i++) Doc("Sparkasse Kontoauszug Girokonto Saldo Buchung", 1, [1], i == 0);
        for (var i = 0; i < arzt; i++) Doc("Praxis Dr. Muster Behandlung Diagnose Rezept", 2, [2], false);
        // In paperless falsch zugeordnet: ein Kontoauszug beim Arzt
        if (wrongLast) Doc("Sparkasse Kontoauszug Girokonto Saldo Buchung", 2, [], false);
        Put(space, "Export/manifest.json", JsonSerializer.Serialize(list));
    }

    [Fact]
    public async Task Export_lesen_Listen_uebernehmen_und_Probelauf()
    {
        var (space, host) = NewSpace();
        Export(space);
        var export = await DmsPaperless.ReadAsync(space, host, null, "Export", default);
        Assert.Equal(16, export.Docs.Count);
        Assert.Equal(0, export.Missing);
        Assert.Equal(5, export.Entries.Count);
        Assert.True(export.Entries.Single(e => e.Name == "[neu]").InboxTag);
        Assert.Equal(D.Any, export.Entries.Single(e => e.Name == "Rechnung").Match);

        var map = DmsPaperless.ApplyLists(space, export, new HashSet<string>());
        Assert.Equal(4, map.Count);
        Assert.DoesNotContain(D.Entries(space.Db), e => e.Name == "[neu]");
        // Zweimal übernehmen legt nichts doppelt an
        DmsPaperless.ApplyLists(space, export, new HashSet<string>());
        Assert.Equal(4, D.Entries(space.Db).Count);

        var trial = DmsPaperless.RunTrial(space, export, 0.75);
        Assert.Equal(12, trial.Learned);
        Assert.Equal(4, trial.Tested);
        var corr = trial.Lists.Single(l => l.Kind == D.Correspondent);
        Assert.Equal(4, corr.Total);
        Assert.Equal(4, corr.Same);
        // Probelauf lernt nichts dauerhaft
        Assert.Equal(0L, space.Db.Scalar("SELECT COUNT(*) FROM dms_trained"));
    }

    [Fact]
    public async Task Uebernahme_ordnet_selbst_ein_und_zeigt_Abweichungen()
    {
        var (space, host) = NewSpace();
        Export(space, wrongLast: true);
        var export = await DmsPaperless.ReadAsync(space, host, null, "Export", default);
        var map = DmsPaperless.ApplyLists(space, export, new HashSet<string>());
        await DmsPaperless.RunImportAsync(space, host, null, "Export", null, default);
        Assert.Equal(17, DmsPaperless.StatusOf(space.User.Id)!.Done);
        await DmsImport.ProcessQueueAsync(space, host, 100, default);

        var docs = D.Docs(space.Db, new D.Filter()).Items;
        Assert.Equal(17, docs.Count);
        Assert.All(docs, d => Assert.Equal("done", d.State));
        // Text aus paperless, keine eigene Texterkennung; Titel und Datum aus paperless
        var first = docs.Single(d => d.Title == "Dokument 1");
        Assert.True(first.Archive);
        Assert.True(first.HasOriginal);
        Assert.EndsWith(".pdf", first.Path);
        Assert.Equal("2024-05-02", first.DocDate);
        Assert.Contains("Sparkasse", D.Text(space.Db, first.Id));

        // Der falsch zugeordnete Kontoauszug: DMS sagt Bank, paperless Arzt
        var wrong = docs.Single(d => d.Title == "Dokument 17");
        Assert.Equal(map["correspondent:1"], wrong.Correspondent);
        Assert.Equal(map["correspondent:2"], wrong.Paperless!.Correspondent);
        Assert.True(wrong.Paperless.Differs);
        Assert.Contains(wrong.Id, D.Docs(space.Db, new D.Filter(PaperlessDiffers: true)).Items.Select(d => d.Id));

        // Gleiche bestätigen, dann für den Rest paperless übernehmen
        var same = DmsPaperless.SameIds(space.Db);
        Assert.DoesNotContain(wrong.Id, same);
        D.Apply(space.Db, same, new D.Change(Confirm: true));
        Assert.True(space.Db.Scalar("SELECT COUNT(*) FROM dms_trained") is long n && n > 0);
        D.Apply(space.Db, [wrong.Id], DmsPaperless.TakePaperless(D.Get(space.Db, wrong.Id)));
        var taken = D.Get(space.Db, wrong.Id);
        Assert.Equal(map["correspondent:2"], taken.Correspondent);
        Assert.False(taken.Inbox);
        Assert.False(taken.Paperless!.Differs);

        // Nochmal übernehmen: alles schon da
        await DmsPaperless.RunImportAsync(space, host, null, "Export", null, default);
        Assert.Equal(17, DmsPaperless.StatusOf(space.User.Id)!.Duplicates);
        Assert.Equal(17, DmsPaperless.Discard(space.Db));
    }

    [Fact]
    public async Task Kein_Export_und_verschluesselter_Export_melden_sich()
    {
        var (space, host) = NewSpace();
        Put(space, "Leer/a.txt", "x");
        await Assert.ThrowsAsync<ArgumentException>(() => DmsPaperless.ReadAsync(space, host, null, "Leer", default));
        Put(space, "Enc/manifest.json", "[]");
        Put(space, "Enc/metadata.json", "{\"version\":\"2.0\",\"encryption\":{\"kdf\":\"x\"}}");
        await Assert.ThrowsAsync<ArgumentException>(() => DmsPaperless.ReadAsync(space, host, null, "Enc", default));
    }

    [Fact]
    public async Task Kaputter_Export_meldet_sich_verstaendlich()
    {
        var (space, host) = NewSpace();
        Put(space, "Kaputt/manifest.json", "[{\"model\": ");
        var e = await Assert.ThrowsAsync<ArgumentException>(() => DmsPaperless.ReadAsync(space, host, null, "Kaputt", default));
        Assert.Contains("manifest.json", e.Message);
        Put(space, "Tief/manifest.json", new string('[', 500) + new string(']', 500));
        await Assert.ThrowsAsync<ArgumentException>(() => DmsPaperless.ReadAsync(space, host, null, "Tief", default));
        Put(space, "Schief/manifest.json", "[{\"model\": 1, \"fields\": 2, \"pk\": \"x\"}, {\"model\": \"documents.tag\", \"fields\": {\"name\": \"Ok\"}, \"pk\": \"x\"}]");
        Assert.Single((await DmsPaperless.ReadAsync(space, host, null, "Schief", default)).Entries);
    }

    [Fact]
    public async Task Oberbegriffe_im_Kreis_legen_jeden_Eintrag_nur_einmal_an()
    {
        var (space, host) = NewSpace();
        Put(space, "Kreis/manifest.json", JsonSerializer.Serialize(new object[]
        {
            new { model = "documents.tag", pk = 1, fields = new { name = "Zyklus A", parent = 2, matching_algorithm = 0 } },
            new { model = "documents.tag", pk = 2, fields = new { name = "Zyklus B", parent = 1, matching_algorithm = 0 } },
        }));
        var export = await DmsPaperless.ReadAsync(space, host, null, "Kreis", default);
        DmsPaperless.ApplyLists(space, export, new HashSet<string>());
        Assert.Equal(["Zyklus A", "Zyklus B"], D.Entries(space.Db, D.Tag).Select(e => e.Name));
    }
}
