using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using Xunit;

namespace DmOffice.Tests;

/// <summary>
/// Eigene Objektivprofile und Regeln je Person (Kennung lenslib) neben denen vom Admin: Sichtbarkeit, Reihenfolge der Zuordnung, Rechte,
/// alte Regeln. Profile aus ZIP-Dateien werden wie in <see cref="LensLibraryTests"/> mit einer nachgestellten Engine eingelesen.
/// </summary>
[Collection("lens")]
public class LensOwnTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dmlensown").FullName;
    private readonly Dictionary<string, UserDb> _dbs = [];
    private readonly List<string> _before = Features.InReleaseOff.ToList();

    public LensOwnTests()
    {
        LensLibrary.UseDir(_dir);
        LensProfiles.UseFile(Path.Combine(_dir, "lenscorr.json"));
        LensOwn.Use(id => _dbs.TryGetValue(id, out var db) ? db : null);
        foreach (var u in new[] { "anna", "ben" }) _dbs[u] = new UserDb(Path.Combine(_dir, u + ".db"), new byte[32], u);
        Features.Set(_before.Where(x => x != Features.LensCorr && x != Features.LensLib));
    }

    public void Dispose()
    {
        Features.Set(_before);
        LensOwn.Use(_ => null);
        LensLibrary.UseDir(Path.Combine(_dir, "leer"));
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static FileMeta.Row Meta(string path, string? camera, string? lens, double? focal = 24) =>
        new(path, null, camera, lens, focal, null, null, null, null, null, null, null, null, null, null, null);

    private static JsonObject Profile(string name, string? lens = null, double k = 0.05) => new()
    {
        ["name"] = name, ["lens"] = lens, ["source"] = "json",
        ["distortion"] = new JsonArray(new JsonObject { ["focal"] = 24, ["k"] = new JsonArray(k, 0.0, 0.0) }),
    };

    private static string Name(JsonObject? block) => block!["profile"]!["name"]!.GetValue<string>();

    /// <summary>Ein Profil aus einer ZIP-Datei vom Admin (Objektiv „Testlinse FE 24mm F2“, Nikon Z f, RAW).</summary>
    private async Task<string> ZipProfile()
    {
        var text = new JsonArray(new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["name"] = "Testlinse FE 24mm F2", ["lens"] = "Testlinse FE 24mm F2", ["source"] = "lcp",
                ["distortion"] = new JsonArray(new JsonObject { ["focal"] = 24, ["k"] = new JsonArray(-0.02, 0.0, 0.0) }),
                ["vignetting"] = new JsonArray(), ["tca"] = new JsonArray(),
            },
            ["meta"] = new JsonObject { ["make"] = "NIKON CORPORATION", ["model"] = "NIKON Z f", ["lens"] = "Testlinse FE 24mm F2", ["lensName"] = "Testlinse FE 24mm F2", ["raw"] = true },
        }).ToJsonString();
        var path = Path.Combine(_dir, "p.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("Nikon/NIKON Z f (Testlinse FE 24mm F2) - RAW.lcp").Open()) s.Write(Encoding.UTF8.GetBytes(text));
        await LensLibrary.ImportAsync(path, (d, _) => Task.FromResult((JsonArray)JsonNode.Parse(Encoding.UTF8.GetString(d))!), null, null, CancellationToken.None);
        return LensLibrary.Prefix + LensLibrary.All().Single().Id;
    }

    [Fact]
    public async Task Person_sieht_eigene_und_alle_Profile_vom_Admin()
    {
        var zip = await ZipProfile();
        var server = LensProfiles.Add([Profile("Serverweit")])[0];
        var own = LensOwn.Add("anna", [Profile("Meins")])[0];
        var cal = LensOwn.Add("anna", [new JsonObject { ["name"] = "Eingemessen", ["source"] = "calibrated" }])[0];

        var list = LensProfiles.List("anna", null, null, null, null, 100);
        Assert.Equal(4, list.Count);
        Assert.Equal("admin", list.Items.Single(r => r.Id == zip).Origin);
        Assert.Equal("admin", list.Items.Single(r => r.Id == server).Origin);
        Assert.Equal("own", list.Items.Single(r => r.Id == own).Origin);
        Assert.Equal("calibrated", list.Items.Single(r => r.Id == cal).Origin);
        // Filter nach Herkunft und Suche
        Assert.Equal(2, LensProfiles.List("anna", null, null, null, "admin", 100).Total);
        Assert.Single(LensProfiles.List("anna", "meins", null, null, null, 100).Items);
        // Verwaltung: nur die vom Admin
        Assert.Equal(2, LensProfiles.List("anna", null, null, null, null, 100, adminOnly: true).Items.Count);
    }

    [Fact]
    public void Person_sieht_keine_Profile_anderer()
    {
        var bens = LensOwn.Add("ben", [Profile("Bens Linse", "Testlinse FE 24mm F2")])[0];
        Assert.DoesNotContain(LensProfiles.List("anna", null, null, null, null, 100).Items, r => r.Id == bens);
        Assert.Null(LensProfiles.Profile(bens, "anna"));
        // Auch eine fremde Kennung in den Einstellungen eines Fotos bringt das Profil nicht
        Assert.Null(LensProfiles.Block(new LensSettings { Profile = bens }, Meta("a.jpg", "NIKON Z f", "Testlinse FE 24mm F2"), "anna"));
        Assert.Null(LensProfiles.AutoProfile(Meta("a.jpg", "NIKON Z f", "Testlinse FE 24mm F2"), "anna"));
        // Regeln mit fremden Profilen werden abgelehnt
        Assert.Throws<ArgumentException>(() => LensOwn.SetRules("anna", [new LensRule("", "Z f", null, null, null, bens)], LensProfiles.AdminExists));
        Assert.Equal("Bens Linse", Name(LensProfiles.Block(new LensSettings(), Meta("a.jpg", "NIKON Z f", "Testlinse FE 24mm F2"), "ben")));
    }

    [Fact]
    public void Eigene_Regel_geht_vor_Admin_Regel_und_Admin_Regel_gilt_fuer_alle_anderen()
    {
        var admin = LensProfiles.Add([Profile("Vom Admin")])[0];
        LensProfiles.SetRules([new LensRule("", "Z f", null, null, null, admin)]);
        var own = LensOwn.Add("anna", [Profile("Meins")])[0];
        LensOwn.SetRules("anna", [new LensRule("", "Z f", null, null, null, own)], LensProfiles.AdminExists);
        var meta = Meta("a.jpg", "NIKON Z f", "Irgendein 50mm");

        Assert.Equal(own, LensProfiles.Match(meta, "anna")!.Profile);
        Assert.Equal("Meins", Name(LensProfiles.Block(new LensSettings(), meta, "anna")));
        // Ben hat keine eigene Regel: die vom Admin
        Assert.Equal(admin, LensProfiles.Match(meta, "ben")!.Profile);
        Assert.Equal("Vom Admin", Name(LensProfiles.Block(new LensSettings(), meta, "ben")));
        // Eigene Regel darf auch ein Profil vom Admin nennen
        LensOwn.SetRules("ben", [new LensRule("", null, "Irgendein", null, null, admin)], LensProfiles.AdminExists);
        Assert.Single(LensOwn.Rules("ben"));
        // Eigenes Profil gelöscht: eigene Regel geht mit, die vom Admin greift wieder
        Assert.Equal(1, LensProfiles.DeleteProfiles("anna", false, [own]));
        Assert.Equal(admin, LensProfiles.Match(meta, "anna")!.Profile);
    }

    [Fact]
    public async Task Automatisch_eigenes_Profil_vor_dem_vom_Admin()
    {
        var zip = await ZipProfile();
        var meta = Meta("a.nef", "NIKON Z f", "Testlinse FE 24mm F2");
        Assert.Equal(zip, LensProfiles.Library(meta, "anna")!.Profile);

        var own = LensOwn.Add("anna", [new JsonObject { ["name"] = "Mein Einmessen", ["source"] = "calibrated" }])[0];
        LensOwn.SetDistortion("anna", own, 24, [0.03], "Testlinse FE 24mm F2", "NIKON Z f");
        var hit = LensProfiles.Library(meta, "anna")!;
        Assert.Equal(own, hit.Profile);
        Assert.Equal("calibrated", hit.Origin);
        Assert.Equal("Mein Einmessen", Name(LensProfiles.Block(new LensSettings(), meta, "anna")));
        Assert.NotNull(LensProfiles.Auto(meta, "anna"));
        // Ben: weiter das vom Admin
        Assert.Equal(zip, LensProfiles.Library(meta, "ben")!.Profile);
        Assert.Equal("admin", LensProfiles.Library(meta, "ben")!.Origin);
    }

    [Fact]
    public async Task Nicht_Admin_loescht_keine_Profile_vom_Admin_und_laedt_keine_ZIP_Datei()
    {
        var zip = await ZipProfile();
        var server = LensProfiles.Add([Profile("Serverweit")])[0];
        Assert.Throws<UnauthorizedAccessException>(() => LensProfiles.DeleteProfiles("anna", false, [zip]));
        Assert.Throws<UnauthorizedAccessException>(() => LensProfiles.DeleteProfiles("anna", false, [server]));
        Assert.Throws<UnauthorizedAccessException>(() => LensProfiles.RenameProfile("anna", false, server, "Neu"));
        Assert.Throws<UnauthorizedAccessException>(() => LensLibrary.StartUpload("anna", false, 1000));
        Assert.NotNull(LensProfiles.Profile(zip));
        Assert.NotNull(LensProfiles.Profile(server));

        // Admin darf - Regeln mit diesen Profilen gehen mit
        LensProfiles.SetRules([new LensRule("", "Z f", null, null, null, zip)]);
        Assert.NotNull(LensLibrary.StartUpload("admin", true, 1000));
        Assert.Equal(1, LensProfiles.DeleteProfiles("admin", true, [zip, server]));
        Assert.Equal(0, LensLibrary.Count);
        Assert.Empty(LensProfiles.Profiles());
        Assert.Empty(LensProfiles.Rules());
    }

    [Fact]
    public async Task Alte_Regel_mit_Profil_aus_ZIP_Datei_und_alte_Profile_greifen_weiter()
    {
        var zip = await ZipProfile();
        var old = LensProfiles.Add([Profile("Alt serverweit")])[0];
        LensProfiles.SetRules([new LensRule("r1", "Z f", "Testlinse", null, null, zip), new LensRule("r2", "X-T5", null, null, null, old)]);
        var nikon = Meta("a.nef", "NIKON Z f", "Testlinse FE 24mm F2");
        var fuji = Meta("b.raf", "FUJIFILM X-T5", "Anderes 35mm");
        // Für eine Person ohne eigene Daten genau wie vorher
        Assert.Equal("r1", LensProfiles.Match(nikon, "anna")!.Id);
        Assert.Equal("Testlinse FE 24mm F2", Name(LensProfiles.Block(new LensSettings(), nikon, "anna")));
        Assert.Equal("Alt serverweit", Name(LensProfiles.Block(new LensSettings(), fuji, "anna")));
        // Gespeicherte Kennungen in Fotos (ohne Person, wie vor den eigenen Profilen) gelten weiter
        Assert.Equal("Alt serverweit", Name(LensProfiles.Block(new LensSettings { Profile = old }, fuji)));
        Assert.Equal("Testlinse FE 24mm F2", Name(LensProfiles.Block(new LensSettings { Profile = zip }, fuji)));
    }

    [Fact]
    public void Eigene_Daten_liegen_in_der_Datenbank_der_Person()
    {
        var id = LensOwn.Add("anna", [Profile("Meins")])[0];
        LensOwn.Use(u => _dbs.TryGetValue(u, out var db) ? db : null);   // Zwischenspeicher leeren: neu aus der Datenbank
        Assert.Equal("Meins", LensProfiles.NameOf(LensOwn.Profile("anna", id)!));
        Assert.Empty(LensProfiles.Profiles());
        LensProfiles.RenameProfile("anna", false, id, "Umbenannt");
        Assert.Equal("Umbenannt", LensProfiles.NameOf(LensProfiles.Profile(id, "anna")!));
    }
}
