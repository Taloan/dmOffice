using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using Xunit;

namespace DmOffice.Tests;

/// <summary>
/// Bibliothek der Objektivprofile (Kennung lenslib): ZIP-Import mit Schutzgrenzen, Wiederholung ohne Dubletten, Zuordnung zu Fotos.
/// Die Engine wird hier nachgestellt: jede Datei enthält schon das Ergebnis von /lens/library als JSON (erfundene Profile).
/// </summary>
[Collection("lens")]
public class LensLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dmlenslib").FullName;

    public LensLibraryTests()
    {
        LensLibrary.UseDir(_dir);
        LensProfiles.UseFile(Path.Combine(_dir, "lenscorr.json"));
    }

    public void Dispose()
    {
        LensLibrary.MaxEntries = 100_000;
        LensLibrary.UseDir(Path.Combine(_dir, "leer"));
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Erfundene Datei: was die Engine aus ihr lesen würde.</summary>
    private static string Lcp(string lens, string? make, string? model, bool? raw, double? crop = null, double k = -0.02) => new JsonArray(new JsonObject
    {
        ["profile"] = new JsonObject
        {
            ["name"] = lens, ["lens"] = lens, ["source"] = "lcp",
            ["distortion"] = new JsonArray(new JsonObject { ["focal"] = 24, ["k"] = new JsonArray(k, 0.0, 0.0) }),
            ["vignetting"] = new JsonArray(), ["tca"] = new JsonArray(),
        },
        ["meta"] = new JsonObject
        {
            ["make"] = make, ["model"] = model, ["lens"] = lens, ["lensName"] = lens, ["raw"] = raw, ["crop"] = crop, ["focalMin"] = 24, ["focalMax"] = 24,
        },
    }).ToJsonString();

    private static Task<JsonArray> Read(byte[] data, CancellationToken ct)
    {
        var text = Encoding.UTF8.GetString(data);
        if (text.StartsWith("FISHEYE", StringComparison.Ordinal)) throw new InvalidDataException("unsupported:FisheyeModel");
        if (!text.StartsWith('[')) throw new InvalidDataException("Keine Korrekturwerte in der Datei");
        return Task.FromResult((JsonArray)JsonNode.Parse(text)!);
    }

    private string Zip(params (string Name, string Text)[] files) => ZipBytes(files.Select(f => (f.Name, Encoding.UTF8.GetBytes(f.Text))).ToArray());

    private string ZipBytes(params (string Name, byte[] Data)[] files)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, data) in files)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                s.Write(data);
            }
        return path;
    }

    private static Task<LensLibrary.Result> Import(string zip) => LensLibrary.ImportAsync(zip, Read, null, null, CancellationToken.None);

    [Theory]
    [InlineData("Sony/SONY (FE 24-50).lcp", "Sony/SONY (FE 24-50).lcp")]
    [InlineData("1.0/Sony/a.lcp", "Sony/a.lcp")]
    [InlineData("LensProfiles/1.0/Sony/a.lcp", "Sony/a.lcp")]
    [InlineData("CameraRaw/LensProfiles/1.0/Sony/a.lcp", "Sony/a.lcp")]
    [InlineData("LensProfiles/Sony/a.lcp", "Sony/a.lcp")]
    [InlineData("../../etc/Sony/a.lcp", "etc/Sony/a.lcp")]
    [InlineData("C:\\temp\\..\\Sony\\a.lcp", "temp/Sony/a.lcp")]
    [InlineData("/abs/./Sony/a.lcp", "abs/Sony/a.lcp")]
    public void Weg_in_der_ZIP_wird_bereinigt(string name, string expected) => Assert.Equal(expected, LensLibrary.CleanPath(name));

    [Fact]
    public async Task Import_mit_Ordnerpraefix_und_Wiederholung_ohne_Dubletten()
    {
        var zip = Zip(
            ("LensProfiles/1.0/Testmaker/TESTMAKER (Erfunden 24mm) - RAW.lcp", Lcp("Erfunden 24mm F2", "TESTMAKER", "TM 1", true)),
            ("LensProfiles/1.0/Testmaker/TESTMAKER (Erfunden 24mm).lcp", Lcp("Erfunden 24mm F2", "TESTMAKER", "TM 1", false)),
            ("LensProfiles/1.0/ThirdParty/Read Me.txt", "nicht lesen"),
            ("__MACOSX/LensProfiles/._x.lcp", "nicht lesen"),
            ("LensProfiles/1.0/Fish/fish.lcp", "FISHEYE"),
            ("LensProfiles/1.0/Bad/bad.lcp", "kaputt"));
        var r = await Import(zip);
        Assert.Equal(4, r.Files);
        Assert.Equal(2, r.Added);
        Assert.Equal(1, r.Unsupported);
        Assert.Equal(1, r.Failed);
        Assert.Contains(r.Errors, e => e.StartsWith("Bad/bad.lcp", StringComparison.Ordinal));
        Assert.Equal(2, LensLibrary.Count);
        var all = LensLibrary.All();
        Assert.All(all, e => Assert.Equal("Testmaker", e.Maker));
        Assert.Contains(all, e => e.Raw == true && e.Source == "Testmaker/TESTMAKER (Erfunden 24mm) - RAW.lcp");
        var epoch = LensLibrary.Epoch;

        // Gleiche Dateien noch einmal (anders verpackt): nichts doppelt, Stand bleibt
        var again = await Import(Zip(("Testmaker/a.lcp", Lcp("Erfunden 24mm F2", "TESTMAKER", "TM 1", true)), ("Testmaker/b.lcp", Lcp("Erfunden 24mm F2", "TESTMAKER", "TM 1", false))));
        Assert.Equal(0, again.Added);
        Assert.Equal(2, again.Known);
        Assert.Equal(2, LensLibrary.Count);
        Assert.Equal(epoch, LensLibrary.Epoch);

        // Index und Profile liegen auf der Platte und werden wieder gelesen
        LensLibrary.UseDir(_dir);
        Assert.Equal(2, LensLibrary.Count);
        var id = LensLibrary.Prefix + LensLibrary.All()[0].Id;
        var p = LensLibrary.Profile(id)!;
        Assert.Equal(id, p["id"]!.GetValue<string>());
        Assert.Single((JsonArray)p["distortion"]!);
    }

    [Fact]
    public async Task Zip_Slip_schreibt_nichts_ausserhalb()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_dir)!, "evil-" + Guid.NewGuid().ToString("N") + ".lcp");
        var zip = Zip(("../../" + Path.GetFileName(outside), Lcp("Slip 50mm", null, null, true)), ("/etc/passwd.json", Lcp("Slip 35mm", null, null, true)));
        var r = await Import(zip);
        Assert.Equal(2, r.Added);
        Assert.False(File.Exists(outside));
        Assert.All(LensLibrary.All(), e => Assert.DoesNotContain("..", e.Source));
        // Abgelegt wird nur unter lens-library/profiles mit eigenem Namen (Prüfsumme)
        var files = Directory.GetFiles(Path.Combine(_dir, "lens-library", "profiles"));
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.Matches("^[0-9a-f]{16}\\.json$", Path.GetFileName(f)));
    }

    [Fact]
    public async Task Zip_Bomb_zu_gross_und_zu_stark_gepackt()
    {
        var big = new byte[LensLibrary.MaxFile + 1];
        var dense = new byte[3 * 1024 * 1024];
        var zip = ZipBytes(("A/big.lcp", big), ("A/dense.lcp", dense), ("A/ok.lcp", Encoding.UTF8.GetBytes(Lcp("Ok 28mm", null, null, null))));
        var r = await Import(zip);
        Assert.Equal(1, r.Added);
        Assert.Equal(2, r.Failed);
        Assert.Contains(r.Errors, e => e.StartsWith("A/big.lcp", StringComparison.Ordinal));
        Assert.Contains(r.Errors, e => e.StartsWith("A/dense.lcp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zu_viele_Eintraege_werden_abgelehnt()
    {
        LensLibrary.MaxEntries = 5;
        var zip = Zip([.. Enumerable.Range(0, 6).Select(i => ($"M/{i}.lcp", Lcp($"Viele {i}mm", null, null, null)))]);
        await Assert.ThrowsAsync<ArgumentException>(() => Import(zip));
        Assert.Throws<ArgumentException>(() => LensLibrary.CountFiles(zip));
        Assert.Equal(0, LensLibrary.Count);
    }

    [Fact]
    public void Kaputte_ZIP_Datei()
    {
        var path = Path.Combine(_dir, "kaputt.zip");
        File.WriteAllText(path, "kein zip");
        Assert.Throws<InvalidDataException>(() => LensLibrary.CountFiles(path));
    }

    private static FileMeta.Row Meta(string path, string? camera, string? lens, double? focal = 24) =>
        new(path, null, camera, lens, focal, null, null, null, null, null, null, null, null, null, null, null);

    private async Task Sample()
    {
        await Import(Zip(
            ("Sony/SONY (Testlinse FE 24mm F2) - RAW.lcp", Lcp("Testlinse FE 24mm F2", "SONY", "ILCE-7M3", true, 1)),
            ("Sony/SONY (Testlinse FE 24mm F2).lcp", Lcp("Testlinse FE 24mm F2", "SONY", "ILCE-7M3", false, 1)),
            ("Sony/SONY (Testlinse FE 24mm F2) APS-C - RAW.lcp", Lcp("Testlinse FE 24mm F2", "SONY", "ILCE-6400", true, 1.5)),
            ("Nikon/NIKON Z f (Testlinse FE 24mm F2) - RAW.lcp", Lcp("Testlinse FE 24mm F2", "NIKON CORPORATION", "NIKON Z f", true, 1)),
            ("Canon/Canon (Nur JPEG 50mm).lcp", Lcp("Nur JPEG 50mm", "Canon", "Canon EOS R", false, 1))));
    }

    [Fact]
    public async Task Zuordnung_Kamera_vor_Format_vor_beliebig_und_RAW_JPEG()
    {
        await Sample();
        // Genau diese Kamera
        var hit = LensLibrary.Find("NIKON Z f", "Testlinse FE 24mm F2", raw: true)!;
        Assert.Equal("NIKON Z f", hit.Entry.Model);
        Assert.Equal("camera", hit.Why);
        hit = LensLibrary.Find("SONY ILCE-6400", "Testlinse FE 24mm F2", raw: true)!;
        Assert.Equal("ILCE-6400", hit.Entry.Model);
        // Andere Sony mit Kleinbild: gleicher Hersteller und Sensorformat (aus der Bibliothek: ILCE-7M3 hat 1)
        hit = LensLibrary.Find("SONY ILCE-7RM5", "Testlinse FE 24mm F2", raw: true)!;
        Assert.Equal("SONY", hit.Entry.Make);
        Assert.Equal("make", hit.Why);
        // JPEG-Foto: JPEG-Profil, sofern vorhanden
        hit = LensLibrary.Find("SONY ILCE-7M3", "Testlinse FE 24mm F2", raw: false)!;
        Assert.False(hit.Entry.Raw);
        // Nur ein JPEG-Profil da: wird auch für RAW genommen
        hit = LensLibrary.Find("Canon EOS R", "Nur JPEG 50mm", raw: true)!;
        Assert.False(hit.Entry.Raw);
        // Fremde Kamera: irgendein passendes Profil, aber RAW für RAW
        hit = LensLibrary.Find("FUJIFILM X-T5", "testlinse fe 24MM f/2", raw: true)!;
        Assert.True(hit.Entry.Raw);
        Assert.Equal("lens", hit.Why);
        // Name in den Aufnahmedaten etwas länger: ähnlicher Name
        hit = LensLibrary.Find("NIKON Z f", "Sony Testlinse FE 24mm F2", raw: true)!;
        Assert.Equal("NIKON Z f", hit.Entry.Model);
        Assert.Null(LensLibrary.Find("NIKON Z f", "Ganz anderes 85mm", raw: true));
        Assert.Null(LensLibrary.Find("NIKON Z f", null, raw: true));
    }

    [Fact]
    public async Task Regel_geht_vor_Bibliothek_und_Bibliotheksprofil_in_Regel()
    {
        await Sample();
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.LensCorr && x != Features.LensLib));
        try
        {
            var meta = Meta("photo.nef", "NIKON Z f", "Testlinse FE 24mm F2");
            Assert.NotNull(LensProfiles.Library(meta));
            Assert.NotNull(LensProfiles.Auto(meta));
            var block = LensProfiles.Block(new LensSettings(), meta)!;
            Assert.Equal("Testlinse FE 24mm F2", block["profile"]!["name"]!.GetValue<string>());

            // Regel mit eigenem Profil gewinnt
            var own = LensProfiles.Add([new JsonObject { ["name"] = "Eigen", ["distortion"] = new JsonArray(new JsonObject { ["focal"] = 24, ["k"] = new JsonArray(0.05, 0.0, 0.0) }) }])[0];
            LensProfiles.SetRules([new LensRule("", "Z f", "Testlinse", null, null, own)]);
            Assert.Null(LensProfiles.Library(meta));
            Assert.Equal("Eigen", LensProfiles.Block(new LensSettings(), meta)!["profile"]!["name"]!.GetValue<string>());

            // Regel mit Profil aus der Bibliothek (Sony-Profil für die Nikon)
            var sony = LensLibrary.All().First(e => e.Model == "ILCE-7M3" && e.Raw == true);
            LensProfiles.SetRules([new LensRule("", "Z f", "Testlinse", null, null, LensLibrary.Prefix + sony.Id)]);
            Assert.Equal(LensLibrary.Prefix + sony.Id, LensProfiles.Match(meta)!.Profile);
            Assert.NotNull(LensProfiles.Block(new LensSettings(), meta)!["profile"]);

            // Leeren: Regeln mit Bibliotheksprofil gehen mit
            Assert.Equal(1, LensProfiles.DropLibraryRules());
            Assert.Equal(5, LensLibrary.Clear());
            Assert.Empty(LensProfiles.Rules());
            Assert.Null(LensProfiles.Library(meta));
            Assert.Null(LensProfiles.Block(new LensSettings(), meta));
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public async Task Ohne_Schalter_keine_automatische_Zuordnung()
    {
        await Sample();
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.LensCorr).Append(Features.LensLib));
        try { Assert.Null(LensProfiles.Library(Meta("photo.nef", "NIKON Z f", "Testlinse FE 24mm F2"))); }
        finally { Features.Set(before); }
    }

    [Fact]
    public async Task Suche_nach_Text_Hersteller_und_Art()
    {
        await Sample();
        Assert.Equal(5, LensLibrary.Search(null, null, null, 100).Total);
        Assert.Equal(3, LensLibrary.Search("testlinse sony", null, null, 100).Total);
        Assert.Equal(1, LensLibrary.Search(null, "Canon", null, 100).Total);
        Assert.Equal(2, LensLibrary.Search(null, null, "jpeg", 100).Total);
        Assert.Contains(LensLibrary.Makers(), m => m.Maker == "Nikon" && m.Count == 1);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'c', (byte)'r', (byte)'x', (byte)' ' }, true)]
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, false)]
    public void RAW_am_Inhalt_erkannt(byte[] head, bool raw) => Assert.Equal(raw, LensLibrary.LooksRaw(head));
}
