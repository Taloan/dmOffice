using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using Xunit;

namespace DmOffice.Tests;

/// <summary>Objektivkorrektur (Kennung lenscorr): Regeln, Profile, Block für die Engine, Fingerabdruck der Bearbeitung.</summary>
[Collection("lens")]
public class LensProfilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dmlens").FullName;

    public LensProfilesTests() => LensProfiles.UseFile(Path.Combine(_dir, "lenscorr.json"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static FileMeta.Row Meta(string? camera, string? lens, double? focal, double? aperture = null) =>
        new("x.jpg", null, camera, lens, focal, aperture, null, null, null, null, null, null, null, null, null, null);

    private static JsonObject Profile(string name, params double[] focals) => new()
    {
        ["name"] = name,
        ["lens"] = name,
        ["distortion"] = new JsonArray([.. focals.Select(f => (JsonNode)new JsonObject { ["focal"] = f, ["k"] = new JsonArray(-0.02 * f / 24, 0.0, 0.0) })]),
    };

    [Fact]
    public void Zwei_Regeln_fuer_ein_adaptiertes_Objektiv_nach_Brennweite()
    {
        var ids = LensProfiles.Add([Profile("FE 24-50", 24, 50), Profile("FE 16-25", 16, 25)]);
        LensProfiles.SetRules([
            new LensRule("", "Z f", "NOKTON 28mm", 24, 50, ids[0]),
            new LensRule("", "z F", "nokton 28MM", 16, 25, ids[1]),
        ]);
        Assert.All(LensProfiles.Rules(), r => Assert.False(string.IsNullOrEmpty(r.Id)));
        Assert.Equal(ids[0], LensProfiles.Match(Meta("NIKON Z f", "NOKTON 28mm F1.5", 24))?.Profile);
        Assert.Equal(ids[0], LensProfiles.Match(Meta("NIKON Z f", "NOKTON 28mm F1.5", 50))?.Profile);
        Assert.Equal(ids[1], LensProfiles.Match(Meta("NIKON Z f", "NOKTON 28mm F1.5", 16))?.Profile);
        // An der Grenze gilt die erste passende Regel
        Assert.Equal(ids[0], LensProfiles.Match(Meta("NIKON Z f", "NOKTON 28mm F1.5", 24))?.Profile);
        Assert.Null(LensProfiles.Match(Meta("NIKON Z f", "NOKTON 28mm F1.5", 85)));
        Assert.Null(LensProfiles.Match(Meta("NIKON Z 6", "NOKTON 28mm F1.5", 28)));
        Assert.Null(LensProfiles.Match(Meta("NIKON Z f", null, 28)));
        Assert.Null(LensProfiles.Match(null));
        Assert.NotNull(LensProfiles.Auto(Meta("NIKON Z f", "NOKTON 28mm", 35)));
    }

    [Fact]
    public void Ungueltige_Regeln_werden_abgelehnt()
    {
        var id = LensProfiles.Add([Profile("A", 24)])[0];
        Assert.Throws<ArgumentException>(() => LensProfiles.SetRules([new LensRule("", null, " ", null, null, id)]));
        Assert.Throws<ArgumentException>(() => LensProfiles.SetRules([new LensRule("", "Z f", null, 50, 24, id)]));
        Assert.Throws<ArgumentException>(() => LensProfiles.SetRules([new LensRule("", "Z f", null, null, null, "gibtsnicht")]));
        Assert.Empty(LensProfiles.Rules());
    }

    [Fact]
    public void Block_nur_wenn_es_etwas_zu_tun_gibt()
    {
        var id = LensProfiles.Add([Profile("A", 24, 50)])[0];
        LensProfiles.SetRules([new LensRule("", null, "NOKTON", null, null, id)]);
        var meta = Meta("NIKON Z f", "NOKTON 28mm", 24, 2.8);
        Assert.Null(LensProfiles.Block(null, meta));
        Assert.Null(LensProfiles.Block(new LensSettings(Off: true), meta));
        // Ohne Profil und ohne eigene Regler: nichts zu tun
        Assert.Null(LensProfiles.Block(new LensSettings(Profile: "-"), meta));
        Assert.Null(LensProfiles.Block(new LensSettings(), Meta("X", "Y", 24)));
        // Nach Regel: Profil aus der Regel, Brennweite und Blende aus den Aufnahmedaten
        var b = LensProfiles.Block(new LensSettings(), meta)!;
        Assert.Equal("A", b["profile"]!["name"]!.GetValue<string>());
        Assert.Null(b["profile"]!["id"]);
        Assert.Equal(24, b["focal"]!.GetValue<double>());
        Assert.Equal(2.8, b["aperture"]!.GetValue<double>());
        // Ohne Profil, nur von Hand
        var m = LensProfiles.Block(new LensSettings(Profile: "-", ManualDistortion: 30), Meta("X", "Y", null))!;
        Assert.Null(m["profile"]);
        Assert.Equal(30, m["manual"]!["distortion"]!.GetValue<double>());
        // Werte werden begrenzt
        var c = LensProfiles.Block(new LensSettings(Profile: id, Distortion: 900, ManualVignetting: -400), meta)!;
        Assert.Equal(200, c["distortion"]!.GetValue<double>());
        Assert.Equal(-100, c["manual"]!["vignetting"]!.GetValue<double>());
    }

    [Fact]
    public void Profile_anlegen_umbenennen_loeschen_und_einmessen()
    {
        var ids = LensProfiles.Add([Profile("Nokton", 28), Profile("nokton")]);
        Assert.Equal(["Nokton", "nokton (2)"], LensProfiles.Profiles().Select(LensProfiles.NameOf));
        LensProfiles.Rename(ids[1], "Zweites");
        Assert.Equal("Zweites", LensProfiles.NameOf(LensProfiles.Profile(ids[1])!));
        LensProfiles.SetDistortion(ids[1], 35, [-0.05]);
        LensProfiles.SetDistortion(ids[1], 24, [-0.08, 0.01]);
        LensProfiles.SetDistortion(ids[1], 35, [-0.04]);
        var d = (JsonArray)LensProfiles.Profile(ids[1])!["distortion"]!;
        Assert.Equal([24.0, 35.0], d.Select(e => e!["focal"]!.GetValue<double>()));
        Assert.Equal(-0.04, d[1]!["k"]![0]!.GetValue<double>());
        Assert.Equal(3, ((JsonArray)d[1]!["k"]!).Count);
        Assert.Throws<ArgumentException>(() => LensProfiles.SetDistortion(ids[1], 35, [double.NaN]));

        LensProfiles.SetRules([new LensRule("", "Z f", null, null, null, ids[0]), new LensRule("", null, "x", null, null, ids[1])]);
        var epoch = LensProfiles.Epoch;
        Assert.Equal(1, LensProfiles.Delete(ids[0]));
        Assert.True(LensProfiles.Epoch > epoch);
        Assert.Single(LensProfiles.Rules());
        Assert.Throws<KeyNotFoundException>(() => LensProfiles.Delete(ids[0]));

        // Gespeichert: neu geladen ist alles noch da
        LensProfiles.UseFile(Path.Combine(_dir, "lenscorr.json"));
        Assert.Single(LensProfiles.Profiles());
        Assert.Single(LensProfiles.Rules());
    }

    [Fact]
    public void Fingerabdruck_ohne_Objektivkorrektur_bleibt_wie_bisher()
    {
        var plain = new PhotoEdits.Edit(90, null, null, "", 0);
        var before = PhotoEdits.Fingerprint(plain);
        LensProfiles.Add([Profile("A", 24)]);
        Assert.Equal(before, PhotoEdits.Fingerprint(plain));
        var withLens = plain with { Lens = new LensSettings() };
        Assert.False(withLens.IsEmpty);
        Assert.False((plain with { Lens = new LensSettings(Off: true) }).IsEmpty);
        var a = PhotoEdits.Fingerprint(withLens);
        Assert.NotEqual(before, a);
        // Ein geändertes Profil macht die Vorschaubilder mit Korrektur neu
        LensProfiles.Add([Profile("B", 24)]);
        Assert.NotEqual(a, PhotoEdits.Fingerprint(withLens));
    }

    // ---------------- Auf Fotos anwenden, neue Fotos (LensApply) ----------------

    private static T WithLensCorr<T>(Func<T> run)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.LensCorr));
        try { return run(); } finally { Features.Set(before); }
    }

    private static GalleryTimeline.Entry Photo(string id, FileMeta.Row? meta, long fileId = 0, string name = "x.jpg", string? owner = null) =>
        new(id, name, 1, "image/jpeg", 0, "e", fileId, meta, null, Owner: owner);

    [Fact]
    public void Regeln_ignorieren_Gross_Klein_und_mehrfache_Leerzeichen()
    {
        var id = LensProfiles.Add([Profile("A", 24)])[0];
        LensProfiles.SetRules([new LensRule("", "  z   F ", "Nokton  28mm", null, null, id)]);
        Assert.Equal("z F", LensProfiles.Rules()[0].Camera);
        Assert.NotNull(LensProfiles.Match(Meta("NIKON Z  f", "NOKTON 28mm F1.5", 28)));
        Assert.NotNull(LensProfiles.Match(Meta("nikon z f", "nokton   28MM", 28)));
        Assert.Null(LensProfiles.Match(Meta("NIKON Zf", "NOKTON 28mm", 28)));
        Assert.Equal("a b", LensRule.Squeeze(" a \t  b "));
    }

    [Fact]
    public void Kandidaten_nach_Zustand_eigene_Bilder_nur()
    {
        var id = LensProfiles.Add([Profile("A", 24)])[0];
        LensProfiles.SetRules([new LensRule("", "Z f", null, null, null, id)]);
        var zf = Meta("NIKON Z f", "NOKTON", 24);
        var edits = new Dictionary<string, PhotoEdits.Edit>
        {
            ["f:2"] = new PhotoEdits.Edit(0, null, null, "hand", 0) { Lens = new LensSettings() },
            ["f:3"] = new PhotoEdits.Edit(0, null, null, "hand", 0) { Lens = new LensSettings(Off: true) },
            ["x:Fotos/a.jpg"] = new PhotoEdits.Edit(90, null, null, "hand", 0),
        };
        var list = LensApply.Candidates([
            Photo("1", zf, 1),
            Photo("2", zf, 2),
            Photo("3", zf, 3),
            Photo("ext:Fotos/a.jpg", zf),
            Photo("4", zf, 4, owner: "anna"),             // geteilter Ordner eines anderen
            Photo("5", zf, 5, name: "clip.mp4"),          // kein Bild
            Photo("6", Meta("Canon", "EF", 50), 6),       // keine Regel
            Photo("7", null, 7),                          // ohne Aufnahmedaten
        ], k => edits.GetValueOrDefault(k));
        Assert.Equal(["f:1", "f:2", "f:3", "x:Fotos/a.jpg"], list.Select(p => p.Key));
        Assert.Equal([LensApply.State.Todo, LensApply.State.Done, LensApply.State.Off, LensApply.State.Todo], list.Select(p => p.State));
        Assert.Empty(LensApply.Candidates([Photo("1", zf, 1)], _ => null, rule: "andere"));
    }

    [Fact]
    public void Eintragen_behaelt_vorhandene_Bearbeitung()
    {
        var before = new PhotoEdits.Edit(90, null, new PhotoAdjust.Values(Exposure: 0.5), "hand", 7);
        var after = LensApply.WithLens(before);
        Assert.Equal(90, after.Rotate);
        Assert.Equal(0.5, after.Adjust!.Exposure);
        Assert.Equal(7, after.Modified);
        Assert.Equal(new LensSettings(), after.Lens);
        Assert.Equal(new LensSettings(), LensApply.WithLens(null).Lens);
        Assert.Equal(0, LensApply.WithLens(null).Rotate);
    }

    [Fact]
    public void Neue_Fotos_nur_mit_Einstellung_und_Regel_ohne_neue_Vorschaubilder_beim_Umschalten()
    {
        var id = LensProfiles.Add([Profile("A", 24)])[0];
        LensProfiles.SetRules([new LensRule("", "Z f", null, null, null, id)]);
        var epoch = LensProfiles.Epoch;
        WithLensCorr(() =>
        {
            Assert.True(LensProfiles.AutoNewDefault);
            Assert.True(LensApply.Wanted(Meta("NIKON Z f", "X", 24), () => true));
            Assert.False(LensApply.Wanted(Meta("Canon", "X", 24), () => true));
            Assert.False(LensApply.Wanted(Meta(null, null, 24), () => true));
            Assert.False(LensApply.Wanted(Meta("NIKON Z f", "X", 24), () => false));
            // Die Einstellung der Person wird nur gelesen, wenn eine Regel passt
            var asked = false;
            LensApply.Wanted(Meta("Canon", "X", 24), () => asked = true);
            Assert.False(asked);
            Assert.Equal("|lens" + epoch, LensProfiles.ListTag);
            return 0;
        });
        Assert.Equal(epoch, LensProfiles.Epoch);
        LensProfiles.Rename(id, "B");
        Assert.Equal(epoch, LensProfiles.Epoch);
        LensProfiles.SetRules([new LensRule("", "Z 6", null, null, null, id)]);
        Assert.NotEqual(epoch, LensProfiles.Epoch);
    }

    [Fact]
    public void Neue_Fotos_je_Person_Vorgabe_aus_frueherer_Servereinstellung()
    {
        UserSpace Space(string name)
        {
            var db = new UserDb(Path.Combine(_dir, name + ".db"), new byte[32], name);
            return new UserSpace(new UserRecord { Id = name, Login = name }, db);
        }
        var a = Space("a");
        var b = Space("b");
        // Ohne eigene Wahl: Vorgabe an
        Assert.True(LensApply.AutoNewFor(a));
        a.UpdateSettings(s => s.LensAutoNew = false);
        Assert.False(LensApply.AutoNewFor(a));
        Assert.True(LensApply.AutoNewFor(b));
        // Früher serverweit aus: Vorgabe aus für alle ohne eigene Wahl, eigene Wahl geht vor
        LensProfiles.SetAutoNewDefault(false);
        Assert.False(LensApply.AutoNewFor(b));
        b.UpdateSettings(s => s.LensAutoNew = true);
        Assert.True(LensApply.AutoNewFor(b));
        Assert.False(LensApply.AutoNewFor(a));
    }
}
