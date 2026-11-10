using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Suche in Ordnern auf dem Server: Namen und Inhalte, je Ordner einschaltbar, nur für die, die den Ordner sehen.</summary>
[Collection("server")]
public class ExternalIndexTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public ExternalIndexTests(FilesServerFixture fx) => _fx = fx;

    private async Task<List<JsonElement>> Find(HttpClient c, string q, string? mount = null, string? path = null)
    {
        var url = "/api/files/external/search?q=" + Uri.EscapeDataString(q) + (mount != null ? "&mount=" + mount : "") + (path != null ? "&path=" + Uri.EscapeDataString(path) : "");
        return (await c.GetFromJsonAsync<JsonElement>(url)).GetProperty("items").EnumerateArray().ToList();
    }

    [Fact]
    public async Task Stufe_nur_Dateien_und_Ordner_findet_keine_Inhalte_und_nennt_den_Stand()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var index = services.GetRequiredService<ExternalIndex>();
        var me = services.GetRequiredService<UserStore>().ByLogin(FilesServerFixture.Login)!.Id;
        var root = Directory.CreateTempSubdirectory("dmextstufe").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            File.WriteAllText(Path.Combine(root, "anwalt.txt"), "Schreiben vom Notar");
            File.WriteAllText(Path.Combine(root, "gross.txt"), new string('x', 2 * 1024 * 1024));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "st", Name = "Stufen", Path = root, Users = [me], SearchLevel = ExternalFolderDef.LevelNames }]);
            await ExternalIndexer.RunOnceAsync(index, config.Current.ExternalFolders, 100, default, forceScan: true);
            var c = _fx.Client();
            Assert.Single(await Find(c, "anwalt"));
            Assert.Empty(await Find(c, "notar"));
            var answer = await c.GetFromJsonAsync<JsonElement>("/api/files/external/search?q=notar");
            var stand = Assert.Single(answer.GetProperty("stand").EnumerateArray().ToList());
            Assert.Equal("names", stand.GetProperty("level").GetString());
            Assert.NotEqual(JsonValueKind.Null, stand.GetProperty("lastScan").ValueKind);

            // Inhalte dazu: gefunden, die zu große Datei bleibt draußen
            var contents = new ExternalFolderDef { Id = "st", Name = "Stufen", Path = root, Users = [me], SearchLevel = ExternalFolderDef.LevelContents, SearchGroups = [ExternalFolderDef.GroupText], SearchMaxMb = 1 };
            config.Update(s => s.ExternalFolders = [contents]);
            await ExternalIndexer.RunOnceAsync(index, config.Current.ExternalFolders, 100, default);
            Assert.Single(await Find(c, "notar"));
            var st = index.StateOf(contents);
            Assert.Equal(1, st.Wanted);
            Assert.Equal(1, st.Done);

            // Zurück auf Dateien und Ordner: Texte weg, Namen bleiben
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "st", Name = "Stufen", Path = root, Users = [me], SearchLevel = ExternalFolderDef.LevelNames }]);
            await ExternalIndexer.RunOnceAsync(index, config.Current.ExternalFolders, 100, default);
            Assert.Empty(await Find(c, "notar"));
            Assert.Single(await Find(c, "anwalt"));

            // Nur bereitstellen: nichts mehr auffindbar, der Mount bietet keine Suche an
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "st", Name = "Stufen", Path = root, Users = [me], SearchLevel = ExternalFolderDef.LevelNone }]);
            await ExternalIndexer.RunOnceAsync(index, config.Current.ExternalFolders, 100, default);
            Assert.Empty(await Find(c, "anwalt"));
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Pruefung_geht_nach_Abbruch_weiter_und_Aenderung_kommt_sofort()
    {
        var services = _fx.Factory.Services;
        var index = services.GetRequiredService<ExternalIndex>();
        var root = Directory.CreateTempSubdirectory("dmextweiter").FullName;
        var f = new ExternalFolderDef { Id = "weiter", Name = "Weiter", Path = root, SearchLevel = ExternalFolderDef.LevelNames };
        try
        {
            for (var i = 0; i < 5; i++)
            {
                Directory.CreateDirectory(Path.Combine(root, "o" + i));
                File.WriteAllText(Path.Combine(root, "o" + i, "datei" + i + ".txt"), "x");
            }
            index.Sync([f]);
            // Abbruch nach dem ersten Ordner, wie bei einem Neustart: gilt als offen, noch nicht fertig
            using var cts = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => index.Scan(f, cts.Token, _ => cts.Cancel()));
            Assert.True(index.ScanOpen(f.Id));
            Assert.Null(index.LastScan(f.Id));
            // Geht weiter und wird fertig
            Assert.Equal(10, index.Scan(f));
            Assert.False(index.ScanOpen(f.Id));
            Assert.NotNull(index.LastScan(f.Id));
            Assert.Equal(5, index.StateOf(f).Files);

            // Über dmOffice geändert: nur diesen Ordner nachziehen, neuer Unterordner mit allem darin
            File.Delete(Path.Combine(root, "o1", "datei1.txt"));
            Directory.CreateDirectory(Path.Combine(root, "o1", "neu"));
            File.WriteAllText(Path.Combine(root, "o1", "neu", "frisch.txt"), "x");
            index.RefreshDir(f, "o1");
            var names = index.Search(["weiter"], "frisch");
            Assert.Equal("o1/neu/frisch.txt", Assert.Single(names).Path);
            Assert.Empty(index.Search(["weiter"], "datei1"));
            await Task.CompletedTask;
        }
        finally
        {
            index.Sync([]);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(ExternalFolderDef.CheckManual, "2026-11-04T10:00:00", "2026-11-05T10:00:00", false)]
    [InlineData(ExternalFolderDef.CheckHourly, "2026-11-05T09:30:00", "2026-11-05T10:00:00", false)]
    [InlineData(ExternalFolderDef.CheckHourly, "2026-11-05T08:30:00", "2026-11-05T10:00:00", true)]
    [InlineData(ExternalFolderDef.CheckSixHours, "2026-11-05T05:00:00", "2026-11-05T10:00:00", false)]
    [InlineData(ExternalFolderDef.CheckDaily, "2026-11-05T03:30:00", "2026-11-05T10:00:00", false)]
    [InlineData(ExternalFolderDef.CheckDaily, "2026-11-05T02:30:00", "2026-11-05T10:00:00", true)]
    [InlineData(ExternalFolderDef.CheckDaily, "2026-11-04T10:00:00", "2026-11-05T02:00:00", false)]
    [InlineData(ExternalFolderDef.CheckWeekly, "2026-11-01T04:00:00", "2026-11-05T10:00:00", false)]
    [InlineData(ExternalFolderDef.CheckWeekly, "2026-10-31T10:00:00", "2026-11-05T10:00:00", true)]
    public void Pruefung_faellig_nach_Zeitplan(string check, string last, string now, bool due)
    {
        var f = new ExternalFolderDef { SearchCheck = check };
        Assert.Equal(due, ExternalIndexer.Due(f, DateTimeOffset.Parse(last + "Z"), DateTimeOffset.Parse(now + "Z")));
    }

    [Fact]
    public void Dateigruppen_nach_Endung()
    {
        Assert.Equal("text", DmOffice.Search.TextExtract.GroupOf("a.EML"));
        Assert.Equal("text", DmOffice.Search.TextExtract.GroupOf("a.html"));
        Assert.Equal("docs", DmOffice.Search.TextExtract.GroupOf("a.pdf"));
        Assert.Equal("images", DmOffice.Search.TextExtract.GroupOf("a.heic"));
        Assert.Equal("", DmOffice.Search.TextExtract.GroupOf("a.zip"));
    }

    [Fact]
    public async Task Namen_und_Inhalte_durchsuchbar()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var index = services.GetRequiredService<ExternalIndex>();
        var me = services.GetRequiredService<UserStore>().ByLogin(FilesServerFixture.Login)!.Id;
        var root = Directory.CreateTempSubdirectory("dmextidx").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Rechnungen", "@eaDir"));
            Directory.CreateDirectory(Path.Combine(root, "Fotos"));
            File.WriteAllText(Path.Combine(root, "Rechnungen", "strom-2026.txt"), "Abschlag beim Stromanbieter für Oktober");
            File.WriteAllText(Path.Combine(root, "Rechnungen", "@eaDir", "strom-2026.txt"), "Vorschau Stromanbieter");
            File.WriteAllText(Path.Combine(root, "Fotos", "urlaub.md"), "Strandkorb und Möwen");
            File.WriteAllText(Path.Combine(root, "liesmich.txt"), "Oberste Ebene");

            var def = new ExternalFolderDef { Id = "nas", Name = "NAS", Path = root, Users = [me], Search = true };
            config.Update(s => s.ExternalFolders = [def]);
            await ExternalIndexer.RunOnceAsync(index, config.Current.ExternalFolders, 100, default, forceScan: true);
            var c = _fx.Client();

            // Inhalt: mit Ausschnitt; der Vorschau-Ordner des NAS kommt nicht vor
            var hits = await Find(c, "stromanbieter");
            var h = Assert.Single(hits);
            Assert.Equal("Rechnungen/strom-2026.txt", h.GetProperty("path").GetString());
            Assert.Equal("NAS", h.GetProperty("mountName").GetString());
            Assert.Contains('', h.GetProperty("snippet").GetString());
            // Name: auch Ordner, ohne Ausschnitt
            var folder = Assert.Single(await Find(c, "rechnungen"));
            Assert.True(folder.GetProperty("isDir").GetBoolean());
            Assert.Equal(JsonValueKind.Null, folder.GetProperty("snippet").ValueKind);
            Assert.Single(await Find(c, "möwen"));
            // Nur unterhalb eines Ordners
            Assert.Empty(await Find(c, "möwen", "nas", "Rechnungen"));
            Assert.Single(await Find(c, "möwen", "nas", "Fotos"));
            var state = index.StateOf(def);
            Assert.Equal(3, state.Files);
            Assert.Equal(3, state.Done);
            Assert.Equal(3, state.Wanted);

            // An dmOffice vorbei geändert und gelöscht: der nächste Lauf zieht es nach
            var strom = Path.Combine(root, "Rechnungen", "strom-2026.txt");
            File.WriteAllText(strom, "Gutschrift vom Wasserwerk");
            File.SetLastWriteTimeUtc(strom, DateTime.UtcNow.AddMinutes(5));
            File.Delete(Path.Combine(root, "Fotos", "urlaub.md"));
            await ExternalIndexer.RunOnceAsync(index, config.Current.ExternalFolders, 100, default, forceScan: true);
            Assert.Empty(await Find(c, "stromanbieter"));
            Assert.Single(await Find(c, "wasserwerk"));
            Assert.Empty(await Find(c, "möwen"));

            // Nur ausgewählte Ordner der obersten Ebene: der Rest verschwindet aus dem Index
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "nas", Name = "NAS", Path = root, Users = [me], Search = true, Include = ["Fotos"] }]);
            await ExternalIndexer.RunOnceAsync(index, config.Current.ExternalFolders, 100, default);
            Assert.Empty(await Find(c, "wasserwerk"));
            Assert.Empty(await Find(c, "liesmich"));

            // Wer den Ordner nicht sehen darf, findet nichts darin
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "nas", Name = "NAS", Path = root, Users = ["jemand-anderes"], Search = true }]);
            await ExternalIndexer.RunOnceAsync(index, config.Current.ExternalFolders, 100, default, forceScan: true);
            Assert.Empty(await Find(c, "wasserwerk"));

            // Suche abgeschaltet: Index weg
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "nas", Name = "NAS", Path = root, Users = [me] }]);
            await ExternalIndexer.RunOnceAsync(index, config.Current.ExternalFolders, 100, default);
            Assert.Equal(0, index.StateOf(def).Files);
            Assert.Empty(await Find(c, "wasserwerk"));
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, true);
        }
    }
}
