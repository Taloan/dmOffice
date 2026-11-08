using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Dms;
using D = DmOffice.Dms.Dms;

namespace DmOffice.Tests;

public class DmsTests
{
    private static (UserSpace Space, DmsHost Host) NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmdms");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var host = new DmsHost(new ServerConfig(new DataPaths(Path.Combine(dir.FullName, "data"))));
        DmsHost.Current = host;
        return (new UserSpace(new UserRecord { Login = "t", Id = "u" + Guid.NewGuid().ToString("N")[..6] }, db), host);
    }

    [Fact]
    public async Task Baum_nimmt_jede_Ebene_nur_einmal()
    {
        var (space, host) = NewSpace();
        var tags = Enumerable.Range(0, 6).Select(i => D.Create(space.Db, D.Tag, "t" + i, null, null, D.None, null, false).Id).ToList();
        var doc = await Import(space, host, "baum.txt", "Ein Dokument");
        D.Apply(space.Db, [doc.Id], new D.Change(Tags: tags));
        var once = System.Text.Json.JsonSerializer.Serialize(D.Tree(space, ["tag"], _ => ""));
        var often = System.Text.Json.JsonSerializer.Serialize(D.Tree(space, Enumerable.Repeat("tag", 12).ToList(), _ => ""));
        Assert.Equal(once, often);
    }

    [Theory]
    [InlineData("m1", "Scan", "m1", "Scan/Archiv", true)]
    [InlineData("m1", "Scan/Archiv", "m1", "Scan", true)]
    [InlineData("m1", "", "m1", "Archiv", true)]
    [InlineData("m1", "scan", "m1", "Scan", true)]
    [InlineData("m1", "Scan", "m1", "Scan2", false)]
    [InlineData("m1", "Scan", "m2", "Scan/Archiv", false)]
    public void Quelle_und_Ablage_im_selben_externen_Ordner(string mount, string folder, string target, string targetFolder, bool overlap) =>
        Assert.Equal(overlap, DmsSources.Overlap(mount, folder, new DmsProfile { Target = "external", Mount = target, Folder = targetFolder }));

    [Fact]
    public void Quelle_und_Ablage_an_verschiedenen_Orten()
    {
        Assert.False(DmsSources.Overlap(null, "Scan", new DmsProfile { Target = "external", Mount = "m1", Folder = "Scan" }));
        Assert.False(DmsSources.Overlap("m1", "Scan", new DmsProfile { Target = "files", Folder = "Scan" }));
        Assert.False(DmsSources.Overlap(null, "Scan", new DmsProfile { Target = "tresor", Folder = "Scan" }));
        Assert.True(DmsSources.Overlap(null, "Scan", new DmsProfile { Target = "files", Folder = "Scan/Ablage" }));
    }

    [Fact]
    public async Task Rolle_hat_immer_genau_ein_Dokument()
    {
        var (space, host) = NewSpace();
        var role = D.Create(space.Db, D.Role, "Testament", null, null, null, null, false);
        Assert.Equal(D.None, role.Match);
        Assert.Throws<ArgumentException>(() => D.Create(space.Db, D.Role, "Unter", role.Id, null, null, null, false));
        var alt = await Import(space, host, "testament_2020.txt", "Mein Testament vom 01.02.2020");
        var neu = await Import(space, host, "testament_2026.txt", "Mein Testament vom 01.10.2026");

        D.Apply(space.Db, [alt.Id], new D.Change(Role: role.Id));
        Assert.True(D.Get(space.Db, alt.Id).RoleCurrent);
        // Ein zweites Dokument bekommt die Rolle: das bisherige bleibt als frühere Version
        D.Apply(space.Db, [neu.Id], new D.Change(Role: role.Id));
        var a = D.Get(space.Db, alt.Id);
        var n = D.Get(space.Db, neu.Id);
        Assert.True(n.RoleCurrent);
        Assert.Equal(role.Id, a.Role);
        Assert.False(a.RoleCurrent);
        Assert.NotNull(a.RoleReplaced);
        Assert.Equal(neu.Id, D.Roles(space.Db)[role.Id].Doc);
        // Mehrere auf einmal: nicht erlaubt
        Assert.Throws<ArgumentException>(() => D.Apply(space.Db, [alt.Id, neu.Id], new D.Change(Role: role.Id)));
        // Entfernen beim geltenden: die Rolle hat dann keines
        D.Apply(space.Db, [neu.Id], new D.Change(Role: ""));
        Assert.Null(D.Get(space.Db, neu.Id).Role);
        Assert.False(D.Roles(space.Db).ContainsKey(role.Id));
        // Rolle löschen: an den Dokumenten weg
        D.Delete(space.Db, role.Id);
        Assert.Null(D.Get(space.Db, alt.Id).Role);
    }

    private static async Task<DmsDoc> Import(UserSpace space, DmsHost host, string name, string text, DmsImport.Info? info = null)
    {
        var r = await DmsImport.ImportAsync(space, new MemoryStream(Encoding.UTF8.GetBytes(text)), name, "text/plain", info ?? new DmsImport.Info("upload"), default);
        await DmsImport.ProcessQueueAsync(space, host, 100, default);
        return D.Get(space.Db, r.Id);
    }

    private static async Task<DmsImport.Result> Upload(UserSpace space, string name, string text) =>
        await DmsImport.ImportAsync(space, new MemoryStream(Encoding.UTF8.GetBytes(text)), name, "text/plain", new DmsImport.Info("upload"), default);

    [Fact]
    public async Task Gleicher_Inhalt_wie_im_Papierkorb_wird_neues_Dokument_und_ueberlebt_das_Leeren()
    {
        var (space, host) = NewSpace();
        var old = await Import(space, host, "brief.txt", "Ein Brief vom Amt");
        FileStore.Delete(space.Db, old.Path!);
        // Über eine Quelle: Die Datei kommt ins DMS, nicht nur in den Papierkorb
        FileStore.EnsureDir(space.Db, "Scanner");
        FileStore.Put(space.Db, "Scanner/brief.txt", Encoding.UTF8.GetBytes("Ein Brief vom Amt"), "text/plain");
        var s = DmsSources.Save(space, new DmsSource { Name = "Scanner", Folder = "Scanner" }, host);
        var (done, dup) = await DmsSources.RunAsync(space, host, s.Id, default);
        Assert.Equal((1, 0), (done, dup));
        await DmsImport.ProcessQueueAsync(space, host, 10, default);
        var now = D.Docs(space.Db, new D.Filter()).Items.Single();
        Assert.NotEqual(old.Id, now.Id);
        // Papierkorb leeren: Das alte Dokument ist sofort weg, das neue samt Inhalt bleibt
        FileStore.PurgeTrash(space.Db, null);
        DmsImport.ForgetGone(space.Db);
        Assert.Null(space.Db.Scalar("SELECT 1 FROM dms_docs WHERE id = $d", ("$d", old.Id)));
        Assert.Equal(now.Id, D.Docs(space.Db, new D.Filter()).Items.Single().Id);
        using var content = host.OpenRead(space, space.Db, now.FileId);
        Assert.Equal("Ein Brief vom Amt", new StreamReader(content).ReadToEnd());
    }

    [Fact]
    public async Task Wartendes_Dokument_laesst_sich_samt_Original_entfernen()
    {
        var (space, host) = NewSpace();
        var r = await Upload(space, "aus-versehen.txt", "Versehentlich hochgeladen");
        var blob = (string)space.Db.Scalar("SELECT original_blob FROM dms_docs WHERE id = $d", ("$d", r.Id))!;
        DmsImport.ForgetUnplaced(space.Db, r.Id);
        Assert.Empty(D.Docs(space.Db, new D.Filter(Queued: true)).Items);
        Assert.Equal(0, await DmsImport.ProcessQueueAsync(space, host, 10, default));
        Assert.DoesNotContain(space.Db.Blobs.List(), b => b.Id == blob);
    }

    [Fact]
    public async Task Quelle_zeigt_jeden_Fehler_und_laeuft_zu_Ende()
    {
        var (space, host) = NewSpace();
        FileStore.EnsureDir(space.Db, "Scanner");
        FileStore.Put(space.Db, "Scanner/a.txt", Encoding.UTF8.GetBytes("Inhalt"), "text/plain");
        // Ablageprofil mit Ziel Tresor, den es nicht mehr gibt
        var profile = new DmsProfile { Default = true, Name = "Weg", Target = "tresor" };
        space.Db.SetJson(DmsFiling.Key, new List<DmsProfile> { profile });
        var s = new DmsSource { Name = "Scanner", Folder = "Scanner", Profile = profile.Id };
        space.Db.SetJson(DmsSources.Key, new List<DmsSource> { s });
        Assert.Equal((0, 0), await DmsSources.RunAsync(space, host, s.Id, default));
        var after = DmsSources.Of(space.Db).Single();
        Assert.NotNull(after.LastRun);
        Assert.StartsWith("a.txt: ", after.Error);
        Assert.NotNull(FileStore.Get(space.Db, "Scanner/a.txt"));
    }

    [Fact]
    public async Task Quelle_auf_fehlendem_Ordner_nennt_den_Grund()
    {
        var (space, host) = NewSpace();
        Assert.Throws<ArgumentException>(() => DmsSources.Save(space, new DmsSource { Name = "x", Folder = "Mit mir geteilt/Anna" }, host));
        var s = new DmsSource { Name = "x", Folder = "Gibt es nicht" };
        space.Db.SetJson(DmsSources.Key, new List<DmsSource> { s });
        await DmsSources.RunAsync(space, host, s.Id, default);
        Assert.Equal("Den Ordner gibt es nicht mehr.", DmsSources.Of(space.Db).Single().Error);
    }

    [Fact]
    public async Task Altes_Ablageprofil_legt_nichts_im_Systemordner_ab()
    {
        var (space, host) = NewSpace();
        Assert.Throws<ArgumentException>(() => DmsFiling.Save(space, new DmsProfile { Name = "x", Folder = "dmVAULT" }, host, host));
        var profile = new DmsProfile { Default = true, Name = "Alt", Folder = "dmVAULT/x" };
        space.Db.SetJson(DmsFiling.Key, new List<DmsProfile> { profile });
        var doc = await Import(space, host, "a.txt", "Inhalt");
        Assert.Equal(0, doc.FileId);
        Assert.NotNull(doc.Error);
        Assert.Null(FileStore.Get(space.Db, "dmVAULT/x"));
    }

    [Theory]
    [InlineData("file\u202Egnp.txt", "filegnp.txt")]
    [InlineData("a\u0000b\u2066c.txt", "abc.txt")]
    public async Task Name_und_Titel_ohne_Steuer_und_Richtungszeichen(string name, string clean)
    {
        var (space, host) = NewSpace();
        var doc = await Import(space, host, name, "Inhalt " + clean);
        Assert.Equal(clean, doc.OriginalName);
        Assert.Equal(Path.GetFileNameWithoutExtension(clean), doc.Title);
        D.Apply(space.Db, [doc.Id], new D.Change(Title: "Neuer\u202E Titel"));
        Assert.Equal("Neuer Titel", D.Get(space.Db, doc.Id).Title);
    }

    [Fact]
    public async Task Rolle_im_Papierkorb_zaehlt_nicht_als_geltend()
    {
        var (space, host) = NewSpace();
        var role = D.Create(space.Db, D.Role, "Testament", null, null, null, null, false);
        var alt = await Import(space, host, "alt.txt", "Testament alt");
        var neu = await Import(space, host, "neu.txt", "Testament neu");
        D.Apply(space.Db, [alt.Id], new D.Change(Role: role.Id));
        D.Apply(space.Db, [neu.Id], new D.Change(Role: role.Id));
        Assert.NotNull(D.Get(space.Db, alt.Id).RoleReplaced);
        FileStore.Delete(space.Db, D.Get(space.Db, neu.Id).Path!, out var trash);
        Assert.Null(D.Get(space.Db, alt.Id).RoleReplaced);
        FileStore.Restore(space.Db, trash);
        Assert.NotNull(D.Get(space.Db, alt.Id).RoleReplaced);
        Assert.True(D.Get(space.Db, neu.Id).RoleCurrent);
    }

    [Fact]
    public async Task Hochgeladene_Mail_nutzt_Betreff_Datum_und_Absender()
    {
        var (space, host) = NewSpace();
        var eml = "From: Amt <amt@example.org>\r\nTo: a@example.org\r\nSubject: Ihr Bescheid\r\nDate: Tue, 03 Mar 2026 10:00:00 +0000\r\nContent-Type: text/plain\r\n\r\nGuten Tag\r\n";
        var doc = await Import(space, host, "testmail.eml", eml);
        Assert.Equal("Ihr Bescheid", doc.Title);
        Assert.Equal("2026-03-03", doc.DocDate);
        Assert.Equal("amt@example.org", doc.Sender);
    }

    [Fact]
    public async Task Tags_und_Rolle_lassen_sich_sortieren()
    {
        var (space, host) = NewSpace();
        var a = D.Create(space.Db, D.Tag, "Anton", null, null, D.None, null, false);
        var z = D.Create(space.Db, D.Tag, "Zeppelin", null, null, D.None, null, false);
        var erst = await Import(space, host, "erst.txt", "eins");
        var zweit = await Import(space, host, "zweit.txt", "zwei");
        var ohne = await Import(space, host, "ohne.txt", "drei");
        D.Apply(space.Db, [erst.Id], new D.Change(Tags: [z.Id]));
        D.Apply(space.Db, [zweit.Id], new D.Change(Tags: [a.Id, z.Id]));
        Assert.Equal([zweit.Id, erst.Id, ohne.Id], D.Docs(space.Db, new D.Filter(Sort: "tags")).Items.Select(d => d.Id));
        Assert.Equal([ohne.Id, erst.Id, zweit.Id], D.Docs(space.Db, new D.Filter(Sort: "tags", Desc: true)).Items.Select(d => d.Id));
        var role = D.Create(space.Db, D.Role, "Vertrag", null, null, null, null, false);
        D.Apply(space.Db, [ohne.Id], new D.Change(Role: role.Id));
        Assert.Equal(ohne.Id, D.Docs(space.Db, new D.Filter(Sort: "role")).Items[0].Id);
    }

    [Fact]
    public async Task Baum_zeigt_auf_der_ersten_Ebene_auch_leere_Eintraege()
    {
        var (space, host) = NewSpace();
        var leer = D.Create(space.Db, D.Correspondent, "Finanzamt", null, null, D.None, null, false);
        D.Create(space.Db, D.Doctype, "Bescheid", null, null, D.None, null, false);
        var genutzt = D.Create(space.Db, D.Correspondent, "Stadtwerke", null, null, D.None, null, false);
        var doc = await Import(space, host, "a.txt", "Inhalt");
        D.Apply(space.Db, [doc.Id], new D.Change(Correspondent: genutzt.Id));
        var tree = D.Tree(space, ["correspondent", "doctype"], _ => "");
        Assert.Equal(0, tree.Single(n => n.Key == leer.Id).Count);
        // Unter dem genutzten nur, was Dokumente hat
        Assert.Null(tree.Single(n => n.Key == genutzt.Id).Children.Single().Key);
    }

    [Fact]
    public async Task Eingaben_mit_zu_vielen_Tags_und_altem_Datum_werden_abgewiesen()
    {
        var (space, host) = NewSpace();
        var doc = await Import(space, host, "a.txt", "Inhalt");
        Assert.Throws<ArgumentException>(() => D.Docs(space.Db, new D.Filter(Tags: Enumerable.Range(0, 2000).Select(i => "t" + i).ToList())));
        Assert.Throws<ArgumentException>(() => D.Apply(space.Db, [doc.Id], new D.Change(DocDate: "0001-01-01")));
    }

    [Fact]
    public async Task Unlesbare_Datei_bekommt_verstaendlichen_Hinweis()
    {
        var (space, host) = NewSpace();
        var r = await DmsImport.ImportAsync(space, new MemoryStream(Encoding.UTF8.GetBytes("kein PDF")), "kaputt.pdf", "application/pdf", new DmsImport.Info("upload"), default);
        await DmsImport.ProcessQueueAsync(space, host, 10, default);
        var doc = D.Get(space.Db, r.Id);
        Assert.Equal("done", doc.State);
        Assert.Equal("Der Text dieser Datei ließ sich nicht lesen. Die Datei ist trotzdem abgelegt.", doc.Error);
    }

    [Fact]
    public async Task Import_legt_nach_Ablageprofil_ab_und_erkennt_Datum_und_Titel()
    {
        var (space, host) = NewSpace();
        var fa = D.Create(space.Db, D.Correspondent, "21_Finanzamt", null, null, D.Any, "Finanzamt", false);
        var bescheid = D.Create(space.Db, D.Doctype, "Bescheid", null, null, D.Exact, "Bescheid für", false);
        var steuer = D.Create(space.Db, D.Tag, "steuer", null, "#7b9cde", D.Any, "Einkommensteuerbescheid Lohnsteuer", false);

        var doc = await Import(space, host, "scan_0042.txt", "Finanzamt Musterstadt\nEinkommensteuerbescheid Bescheid für 2025\nDatum: 14.03.2026\nBitte zahlen Sie");
        Assert.Equal("done", doc.State);
        Assert.Equal(fa.Id, doc.Correspondent);
        Assert.Equal(bescheid.Id, doc.Doctype);
        Assert.Equal([steuer.Id], doc.Tags.Select(t => t.Id));
        Assert.Equal("2026-03-14", doc.DocDate);
        // Scanner-Name: Titel aus der ersten aussagekräftigen Zeile
        Assert.Equal("Finanzamt Musterstadt", doc.Title);
        Assert.True(doc.Inbox);
        // In der Inbox flach im Inbox-Ordner, nach dem Bestätigen nach dem Ordnermuster
        Assert.Equal("Dokumente/.INBOX/2026-03-14 Bescheid Finanzamt Musterstadt.txt", doc.Path);
        Assert.Equal([doc.Id], D.Apply(space.Db, [doc.Id], new D.Change(Confirm: true)));
        Assert.Equal("Dokumente/21_Finanzamt/2026/2026-03-14 Bescheid Finanzamt Musterstadt.txt", DmsImport.Refile(space, host, doc.Id));
        doc = D.Get(space.Db, doc.Id);
        // Ohne Archivfassung ist die Datei das Original; der Ordner ist aus der Galerie genommen
        Assert.False(doc.HasOriginal);
        Assert.Contains("Dokumente", space.Settings.PhotoExcludes);

        // Doppelt: gleicher Inhalt wird nicht neu angelegt
        var again = await DmsImport.ImportAsync(space, new MemoryStream(Encoding.UTF8.GetBytes("Finanzamt Musterstadt\nEinkommensteuerbescheid Bescheid für 2025\nDatum: 14.03.2026\nBitte zahlen Sie")),
            "x.txt", "text/plain", new DmsImport.Info("upload"), default);
        Assert.True(again.Duplicate);
        Assert.Equal(doc.Id, again.Id);

        // Volltext
        Assert.Single(D.Docs(space.Db, new D.Filter(Query: "einkommensteuer")).Items);
        Assert.Empty(D.Docs(space.Db, new D.Filter(Query: "urlaub")).Items);
    }

    [Fact]
    public async Task Aendern_legt_neu_ab_und_von_Hand_verschobene_bleiben()
    {
        var (space, host) = NewSpace();
        var a = D.Create(space.Db, D.Correspondent, "Stadtwerke", null, null, D.None, null, false);
        var doc = await Import(space, host, "Abrechnung Strom.txt", "Jahresabrechnung vom 02.01.2026");
        Assert.Equal("Dokumente/.INBOX/2026-01-02 Unbekannt Abrechnung Strom.txt", doc.Path);
        // Ändern in der Inbox: bleibt im Inbox-Ordner (nur der Name folgt)
        D.Apply(space.Db, [doc.Id], new D.Change(Title: "Abrechnung"));
        Assert.Equal("Dokumente/.INBOX/2026-01-02 Unbekannt Abrechnung.txt", DmsImport.Refile(space, host, doc.Id));

        var changed = D.Apply(space.Db, [doc.Id], new D.Change(Correspondent: a.Id, Title: "Abrechnung Strom", Confirm: true));
        Assert.Equal([doc.Id], changed);
        DmsImport.Refile(space, host, doc.Id);
        Assert.Equal("Dokumente/Stadtwerke/2026/2026-01-02 Unbekannt Abrechnung Strom.txt", D.Get(space.Db, doc.Id).Path);

        // Von Hand verschoben: DMS verschiebt nicht mehr, bis „nach Vorgabe ablegen“
        FileStore.EnsureDir(space.Db, "Privat");
        FileStore.MoveOrCopy(space.Db, D.Get(space.Db, doc.Id).Path!, "Privat/strom.txt", overwrite: false, copy: false);
        D.Apply(space.Db, [doc.Id], new D.Change(Title: "Strom 2025"));
        DmsImport.Refile(space, host, doc.Id);
        var now = D.Get(space.Db, doc.Id);
        Assert.Equal("Privat/strom.txt", now.Path);
        Assert.True(now.Manual);
        DmsImport.Refile(space, host, doc.Id, force: true);
        Assert.Equal("Dokumente/Stadtwerke/2026/2026-01-02 Unbekannt Strom 2025.txt", D.Get(space.Db, doc.Id).Path);

        // Papierkorb: unsichtbar, nach endgültigem Löschen vergessen
        FileStore.Delete(space.Db, D.Get(space.Db, doc.Id).Path!);
        Assert.Empty(D.Docs(space.Db, new D.Filter()).Items);
        DmsImport.Cleanup(space, host, default);
        Assert.NotNull(space.Db.Scalar("SELECT 1 FROM dms_docs WHERE id = $d", ("$d", doc.Id)));
        FileStore.PurgeTrash(space.Db, null);
        DmsImport.Cleanup(space, host, default);
        Assert.Null(space.Db.Scalar("SELECT 1 FROM dms_docs WHERE id = $d", ("$d", doc.Id)));
    }

    [Fact]
    public async Task Inbox_reagiert_auf_neue_Eintraege_und_Zuordnungen()
    {
        var (space, host) = NewSpace();
        var a = await Import(space, host, "rechnung1.txt", "Suno Inc.\nRechnung vom 24.10.2026");
        var b = await Import(space, host, "rechnung2.txt", "Suno Inc.\nZahlungsbeleg vom 24.10.2026");
        // Vorschlag der KI für einen neuen Korrespondenten (sonst beim Import gesetzt)
        space.Db.Exec("UPDATE dms_docs SET ai_suggestion = 'Suno' WHERE id IN ($a, $b)", ("$a", a.Id), ("$b", b.Id));
        Assert.Empty(D.Reassess(space.Db));

        // Bei einem Dokument angelegt: das andere bekommt den Eintrag (automatisch), der Vorschlag ist erledigt
        var suno = D.Create(space.Db, D.Correspondent, "Suno", null, null, D.Auto, null, false);
        D.Apply(space.Db, [a.Id], new D.Change(Correspondent: suno.Id));
        Assert.Equal([a.Id, b.Id], D.Reassess(space.Db).Order());
        var now = D.Get(space.Db, b.Id);
        Assert.Equal(suno.Id, now.Correspondent);
        Assert.True(now.AutoCorrespondent);
        Assert.Null(now.AiSuggestion);
        // Von Hand gesetzt bleibt
        var other = D.Create(space.Db, D.Correspondent, "Anderer", null, null, D.Any, "Suno", false);
        D.Apply(space.Db, [b.Id], new D.Change(Correspondent: suno.Id));
        D.Reassess(space.Db);
        Assert.Equal(suno.Id, D.Get(space.Db, b.Id).Correspondent);
        Assert.NotEqual(other.Id, D.Get(space.Db, a.Id).Correspondent);
    }

    [Fact]
    public async Task Warteschlange_getrennt_von_der_Inbox()
    {
        var (space, host) = NewSpace();
        var r = await DmsImport.ImportAsync(space, new MemoryStream(Encoding.UTF8.GetBytes("Brief vom 01.02.2026")), "brief.txt", "text/plain", new DmsImport.Info("upload"), default);
        // Wartend: in der Warteschlange, nicht in der Inbox
        Assert.Equal(0, D.InboxCount(space.Db));
        Assert.Empty(D.Docs(space.Db, new D.Filter(Inbox: true)).Items);
        Assert.Single(D.Docs(space.Db, new D.Filter(Queued: true)).Items);
        await DmsImport.ProcessQueueAsync(space, host, 100, default);
        Assert.Equal(1, D.InboxCount(space.Db));
        Assert.Empty(D.Docs(space.Db, new D.Filter(Queued: true)).Items);
        Assert.Equal(r.Id, D.Docs(space.Db, new D.Filter(Inbox: true)).Items.Single().Id);
    }

    [Fact]
    public async Task Automatisch_lernt_aus_bestaetigten_Dokumenten()
    {
        var (space, host) = NewSpace();
        var bank = D.Create(space.Db, D.Correspondent, "22_Banken", null, null, D.Auto, null, false);
        var arzt = D.Create(space.Db, D.Correspondent, "10_Ärzte", null, null, D.Auto, null, false);
        var docs = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var d1 = await Import(space, host, $"k{i}.txt", $"Kontoauszug Girokonto Sparkasse Buchung Überweisung Saldo {i}");
            D.Apply(space.Db, [d1.Id], new D.Change(Correspondent: bank.Id, Confirm: true));
            var d2 = await Import(space, host, $"a{i}.txt", $"Praxis Dr. Muster Rezept Behandlung Diagnose Patient {i}");
            D.Apply(space.Db, [d2.Id], new D.Change(Correspondent: arzt.Id, Confirm: true));
            docs.Add(d1.Id);
        }
        var neu = await Import(space, host, "neu.txt", "Sparkasse Kontoauszug Girokonto Saldo neu");
        Assert.Equal(bank.Id, neu.Correspondent);
        Assert.True(neu.AutoCorrespondent);
        var neu2 = await Import(space, host, "neu2.txt", "Rezept von Praxis Dr. Muster für Patient");
        Assert.Equal(arzt.Id, neu2.Correspondent);
        Assert.Equal(0, D.Docs(space.Db, new D.Filter(Inbox: true)).Items.Count(d => docs.Contains(d.Id)));
    }

    /// <summary>Datei außerhalb des DMS geändert (etwa im Office-Editor): Text und Größe werden neu gelesen, die Angaben bleiben.</summary>
    [Fact]
    public async Task Geaenderter_Inhalt_wird_neu_gelesen_Angaben_bleiben()
    {
        var (space, host) = NewSpace();
        var werkstatt = D.Create(space.Db, D.Correspondent, "31_Kfz", null, null, D.None, null, false);
        var doc = await Import(space, host, "kalkulation.txt", "Preis alt");
        D.Apply(space.Db, [doc.Id], new D.Change(Correspondent: werkstatt.Id, Title: "Kalkulation"));
        var path = D.Get(space.Db, doc.Id).Path!;

        var inhalt = Encoding.UTF8.GetBytes("Preis neu Omnichannel");
        FileStore.Put(space.Db, path, inhalt, "text/plain");
        DmsImport.Cleanup(space, host, default);

        var now = D.Get(space.Db, doc.Id);
        Assert.Contains("Omnichannel", D.Text(space.Db, doc.Id));
        Assert.Equal(inhalt.Length, now.Size);
        Assert.Equal(werkstatt.Id, now.Correspondent);
        Assert.Equal("Kalkulation", now.Title);
    }

    /// <summary>Ziehen auf einen Eintrag im Baum ändert mehrere Dokumente zugleich und korrigiert das Gelernte.</summary>
    [Fact]
    public async Task Neu_zuordnen_mehrerer_Dokumente_lernt_um()
    {
        var (space, host) = NewSpace();
        var falsch = D.Create(space.Db, D.Correspondent, "05_reeg", null, null, D.Auto, null, false);
        var richtig = D.Create(space.Db, D.Correspondent, "31_Kfz", null, null, D.Auto, null, false);
        var rechnung = D.Create(space.Db, D.Doctype, "Rechnung", null, null, D.Auto, null, false);
        var tag = D.Create(space.Db, D.Tag, "auto", null, null, D.Auto, null, false);
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var d = await Import(space, host, $"w{i}.txt", $"Werkstatt Inspektion Bremsen Reifenwechsel {i}");
            D.Apply(space.Db, [d.Id], new D.Change(Correspondent: falsch.Id, Confirm: true));
            ids.Add(d.Id);
        }
        long? Label(long doc, string entry) => space.Db.Scalar("SELECT label FROM dms_trained WHERE doc=$d AND entry=$e", ("$d", doc), ("$e", entry)) as long?;
        Assert.Equal(1L, Label(ids[0], falsch.Id));

        D.Apply(space.Db, ids, new D.Change(Correspondent: richtig.Id));
        D.Apply(space.Db, ids, new D.Change(Doctype: rechnung.Id));
        D.Apply(space.Db, ids, new D.Change(AddTags: [tag.Id]));

        foreach (var doc in D.ByIds(space.Db, ids))
        {
            Assert.Equal(richtig.Id, doc.Correspondent);
            Assert.False(doc.AutoCorrespondent);
            Assert.Equal(rechnung.Id, doc.Doctype);
            Assert.Contains(doc.Tags, t => t.Id == tag.Id && !t.Auto);
            Assert.Equal(-1L, Label(doc.Id, falsch.Id));
            Assert.Equal(1L, Label(doc.Id, richtig.Id));
            Assert.Equal(1L, Label(doc.Id, rechnung.Id));
            Assert.Equal(1L, Label(doc.Id, tag.Id));
        }
        // Das Umgelernte entscheidet beim nächsten Dokument (Gegenbeispiele bleiben beim bisherigen Eintrag)
        for (var i = 0; i < 3; i++)
        {
            var d = await Import(space, host, $"p{i}.txt", $"Kaufvertrag Grundstück Notar Urkunde {i}");
            D.Apply(space.Db, [d.Id], new D.Change(Correspondent: falsch.Id, Confirm: true));
        }
        var neu =await Import(space, host, "neu.txt", "Werkstatt Inspektion Bremsen Reifenwechsel neu");
        Assert.Equal(richtig.Id, neu.Correspondent);
    }

    [Fact]
    public void Muster_Platzhalter_und_verschachtelte_Eintraege()
    {
        var (space, _) = NewSpace();
        var p = new DmsProfile { Folder = "Archiv", Folders = "{correspondent_path}/{doc_year}/{doc_month_name}", Names = "{doc_date}_{tags}_{title}" };
        var f = DmsFiling.Example("de") with { Correspondent = ["20_Bürokratie", "21_Finanzamt"] };
        Assert.Equal("Archiv/20_Bürokratie/21_Finanzamt/2026/März/2026-03-14_steuer, 2025_Einkommensteuer 2025.pdf", DmsFiling.PathFor(p, f, ".pdf"));
        Assert.Equal(["foo"], DmsFiling.Unknown("{correspondent}/{foo}"));
        Assert.True(DmsFiling.SamePlace("A/b (2).pdf", "A/b.pdf"));
        Assert.False(DmsFiling.SamePlace("A/c.pdf", "A/b.pdf"));

        // Verschachtelt: Oberbegriff zeigt die Dokumente der Unterbegriffe
        var top = D.Create(space.Db, D.Correspondent, "20_Bürokratie", null, null, D.Auto, null, false);
        var sub = D.Create(space.Db, D.Correspondent, "21_Finanzamt", top.Id, null, D.Auto, null, false);
        Assert.Equal(["20_Bürokratie", "21_Finanzamt"], D.PathOf(D.Entries(space.Db), sub.Id));
        Assert.Throws<ArgumentException>(() => D.Update(space.Db, top.Id, null, sub.Id, null, null, null, null));
        Assert.Throws<ArgumentException>(() => D.Create(space.Db, D.Tag, "x", null, null, D.Sender, "a@b.de", false));
        Assert.Throws<ArgumentException>(() => D.Create(space.Db, D.Tag, "y", null, null, D.Regex, "([", false));
    }

    [Fact]
    public void Datum_aus_dem_Text()
    {
        var today = new DateOnly(2026, 10, 30);
        Assert.Equal(new DateOnly(2025, 3, 14), DmsAssign.FindDate("Rechnung Nr. 123 vom 14.03.2025", today));
        Assert.Equal(new DateOnly(2024, 12, 1), DmsAssign.FindDate("Datum 2024-12-01", today));
        Assert.Equal(new DateOnly(2026, 5, 7), DmsAssign.FindDate("Berlin, 7. Mai 2026", today));
        Assert.Equal(new DateOnly(2025, 6, 2), DmsAssign.FindDate("Kunde 99.99.2025, Datum 02.06.25", today));
        Assert.Null(DmsAssign.FindDate("Nichts drin 31.02.2025 1.1.1800", today));
        // Nicht aus der Zukunft, nicht Geburtstag oder Fälligkeit; mit Hinweis wie „Datum“ zuerst, gleiche Daten einmal
        Assert.Null(DmsAssign.FindDate("Termin am 12.11.2026", today));
        var all = DmsAssign.FindDates("Herr Muster, geb. 01.02.1980\nLeistungszeitraum 03.05.2026 bis 31.05.2026\nzahlbar bis 15.11.2026\n" +
            "Bescheid 03.05.2026\nDatum: 10.06.2026", today);
        Assert.Equal([new DateOnly(2026, 6, 10), new DateOnly(2026, 5, 3)], all.Select(c => c.Date));
        Assert.True(all[0].Labeled);
        Assert.Equal(2, all[1].Count);
        Assert.Contains("Leistungszeitraum", all[1].Context);
        // „Datum:“ vor „vom“; ein Bezug auf ein anderes Schreiben zählt nicht als Hinweis
        var letter = DmsAssign.FindDates("Ihr Schreiben vom 12.09.2026\nDatum: 20.10.2026\nLieferung am 03.10.2026", today);
        Assert.Equal([new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 3), new DateOnly(2026, 9, 12)], letter.Select(c => c.Date));
        Assert.Equal("Rechnung Mai", DmsAssign.Title("Rechnung_Mai.pdf", "x"));
        Assert.Equal("Kündigung Ihres Vertrags", DmsAssign.Title("20260101_1200.pdf", "123\nKündigung Ihres Vertrags\nmehr"));
    }

    [Fact]
    public async Task Virtuelle_Baeume_nach_Angaben_und_Datum()
    {
        var (space, host) = NewSpace();
        var fa = D.Create(space.Db, D.Correspondent, "Finanzamt", null, null, D.Any, "Finanzamt", false);
        await Import(space, host, "a.txt", "Finanzamt Bescheid 01.02.2025");
        await Import(space, host, "b.txt", "Finanzamt Mahnung 01.02.2026");
        await Import(space, host, "c.txt", "Sonstiges 05.05.2026");
        var tree = D.Tree(space, ["correspondent", "doc_year"], _ => "");
        Assert.Equal(2, tree.Count);
        Assert.Equal(fa.Id, tree[0].Key);
        Assert.Equal(2, tree[0].Count);
        Assert.Equal(["2026", "2025"], tree[0].Children.Select(c => c.Key));
        Assert.Null(tree[1].Key);
        var byImport = D.Tree(space, ["import_year", "import_month"], _ => "");
        Assert.Equal(3, byImport.Single().Count);
        Assert.Throws<ArgumentException>(() => D.Tree(space, ["nix"], _ => ""));
    }

    [Fact]
    public async Task Quelle_uebernimmt_aus_eigenem_Ordner_und_leert_ihn()
    {
        var (space, host) = NewSpace();
        FileStore.EnsureDir(space.Db, "Scanner");
        FileStore.Put(space.Db, "Scanner/scan1.txt", Encoding.UTF8.GetBytes("Versicherung Police 01.01.2026"), "text/plain");
        var tag = D.Create(space.Db, D.Tag, "versicherung", null, null, D.None, null, false);
        Assert.Throws<ArgumentException>(() => DmsSources.Save(space, new DmsSource { Name = "x", Folder = "Dokumente" }, host));
        var s = DmsSources.Save(space, new DmsSource { Name = "Scanner", Folder = "Scanner", Tags = [tag.Id] }, host);
        var old = DmsSources.Settle;
        DmsSources.Settle = TimeSpan.Zero;
        try
        {
            var (done, _) = await DmsSources.RunAsync(space, host, s.Id, default);
            Assert.Equal(1, done);
        }
        finally { DmsSources.Settle = old; }
        Assert.Null(FileStore.Get(space.Db, "Scanner/scan1.txt"));
        await DmsImport.ProcessQueueAsync(space, host, 10, default);
        var doc = D.Docs(space.Db, new D.Filter()).Items.Single();
        Assert.Equal([new DmsDocTag(tag.Id, false)], doc.Tags);
        Assert.StartsWith("Dokumente/", doc.Path);
    }

    [Fact]
    public void Ki_Antwort_wird_auf_vorhandene_Eintraege_abgebildet()
    {
        var (space, _) = NewSpace();
        var c = D.Create(space.Db, D.Correspondent, "Finanzamt", null, null, D.Auto, null, false);
        var t = D.Create(space.Db, D.Tag, "steuer", null, null, D.Auto, null, false);
        var ai = new FakeAi("""Hier: {"correspondent": 1, "doctype": 99, "tags": [2, 1], "title": "Bescheid 2025", "date": "2026-03-14", "new_correspondent": null}""");
        var r = DmsAssign.AiAsync(space.Db, ai, "Text", "a.pdf", null, null, default).GetAwaiter().GetResult()!;
        Assert.Equal(c.Id, r.Correspondent);
        Assert.Null(r.Doctype);
        Assert.Equal([t.Id], r.Tags);
        Assert.Equal("2026-03-14", r.DocDate);
        Assert.Contains("Finanzamt", ai.Prompt);
        Assert.Null(DmsAssign.AiAsync(space.Db, new FakeAi("kein json"), "Text", "a.pdf", null, null, default).GetAwaiter().GetResult());
    }

    private sealed class FakeAi(string answer) : IDmsAi
    {
        public string Prompt = "";
        public bool Allowed(UserSpace space) => true;
        public string? Service => "Test";
        public Task<string?> WriteAsync(string system, string prompt, CancellationToken ct) { Prompt = prompt; return Task.FromResult<string?>(answer); }
    }
}
