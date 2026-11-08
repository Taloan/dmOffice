using DmOffice.Audio;
using DmOffice.Core;
using DmOffice.Dms;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Externer Ordner nicht mehr eingebunden: Galerie, Audiothek, Importprofile, DMS und Klartext-Sicherung werden abgeschaltet, nicht gelöscht.</summary>
[Collection("server")]
public class MountDetachTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public MountDetachTests(FilesServerFixture fx) => _fx = fx;

    private static void Use(UserDb db, string mount)
    {
        GalleryFolders.Save(db, [new GallerySource { Id = "gal-" + mount, Mount = mount, Path = "Fotos", Proxies = true }]);
        AudioFolders.Save(db, [new AudioSource { Id = "aud-" + mount, Mount = mount, Path = "Musik" }]);
        ImportProfiles.Save(db, [new ImportProfile { Id = "imp-" + mount, Name = "Kamera", Sources = [new ImportSource(mount, "DCIM")] }]);
        db.SetJson(DmsFiling.Key, new List<DmsProfile> { new() { Id = "std", Name = "Standard", Default = true }, new() { Id = "ext", Name = "Archiv", Target = "external", Mount = mount } });
        db.SetJson(DmsSources.Key, new List<DmsSource>
        {
            new() { Id = "q-ordner", Name = "Scanner", Mount = mount },
            new() { Id = "q-profil", Name = "Eingang", Profile = "ext" },
            new() { Id = "q-fremd", Name = "Andere" },
        });
        var s = PlainBackup.Settings(db);
        s.Enabled = true; s.Target = PlainBackup.External; s.Mount = mount;
        PlainBackup.Save(db, s);
    }

    private static void Off(UserDb db, string mount)
    {
        var g = Assert.Single(GalleryFolders.Of(db));
        Assert.False(g.Watch); Assert.False(g.Proxies); Assert.Equal(MountDetach.Reason, g.Error);
        var a = Assert.Single(AudioFolders.Of(db));
        Assert.False(a.Watch); Assert.Equal(MountDetach.Reason, a.Error);
        var p = Assert.Single(ImportProfiles.Of(db));
        Assert.False(p.Active); Assert.Equal(MountDetach.Reason, p.Error);
        var q = DmsSources.Of(db).ToDictionary(x => x.Id);
        Assert.False(q["q-ordner"].Active);
        Assert.False(q["q-profil"].Active);
        Assert.Contains("Archiv", q["q-profil"].Error);
        Assert.True(q["q-fremd"].Active);
        Assert.False(PlainBackup.Settings(db).Enabled);
        Assert.Equal(MountDetach.Reason, PlainBackup.State(db).LastError);
        // nichts gelöscht, schon Abgeschaltetes zählt nicht noch einmal
        Assert.Equal(3, q.Count);
        Assert.Equal(0, MountDetach.Run(db, mount));
    }

    [Fact]
    public async Task Nicht_mehr_einbinden_schaltet_alle_Verwendungen_ab()
    {
        var db = _fx.Db;
        db.SetJson(ExternalFolders.UserKey, new List<UserMount> { new() { Id = "pcloud-test", Name = "pCloud", Url = "https://webdav.example/" } });
        Use(db, "pcloud-test");
        var res = await _fx.Client().DeleteAsync("/api/files/external/mounts/pcloud-test");
        res.EnsureSuccessStatusCode();
        // Die Einbindung ist weg; das externe Ziel (aus der Umstellung) bleibt unter Einstellungen → Externe Ziele
        Assert.DoesNotContain(ExternalFolders.Own(_fx.Factory.Services.GetRequiredService<Spaces>().Get(db.UserId)), m => m.Access != null);
        Off(db, "pcloud-test");
    }

    [Fact]
    public void Verwendungen_schon_entfernter_Ordner_werden_abgeschaltet()
    {
        var space = _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Db.UserId);
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        space.Db.SetJson(ExternalFolders.UserKey, new List<UserMount> { new() { Id = "noch-da", Name = "NAS", Url = "https://nas.example/" } });
        Use(space.Db, "laengst-weg");
        Assert.True(MountDetach.Orphans(space, config) > 0);
        Off(space.Db, "laengst-weg");

        // Ein noch eingebundener Ordner bleibt unberührt
        Use(space.Db, "noch-da");
        Assert.Equal(0, MountDetach.Orphans(space, config));
        Assert.True(Assert.Single(ImportProfiles.Of(space.Db)).Active);
    }
}
