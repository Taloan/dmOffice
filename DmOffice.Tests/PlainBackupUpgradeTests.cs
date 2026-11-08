using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;
using DmOffice.Storage;

namespace DmOffice.Tests;

/// <summary>Datensicherung: ein früher eingerichtetes Ziel (eigene WebDAV-Adresse) wird ein externes Ziel und bleibt bestätigt.</summary>
[Collection("server")]
public class PlainBackupUpgradeTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public PlainBackupUpgradeTests(FilesServerFixture fx) => _fx = fx;

    private UserSpace Space() => _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Factory.Services.GetRequiredService<UserStore>().ByLogin(FilesServerFixture.Login)!.Id);

    [Fact]
    public void Frueheres_WebDav_Ziel_wird_externes_Ziel()
    {
        var db = _fx.Db;
        var before = db.GetJson<List<UserMount>>(ExternalFolders.UserKey).Count;
        var s = new PlainBackupSettings { Enabled = true, Target = PlainBackup.WebDav, Url = "https://ziel.example/dav/", User = "anna", Password = "geheim" };
        s.Confirmed = PlainBackup.ConfirmKey(s);
        PlainBackup.Save(db, s);

        PlainBackupService.Settings(Space());

        var now = PlainBackup.Settings(db);
        Assert.Equal(PlainBackup.External, now.Target);
        Assert.Equal("", now.Url);
        Assert.Null(now.Password);
        var m = Assert.Single(db.GetJson<List<UserMount>>(ExternalFolders.UserKey), x => x.Id == now.Mount);
        Assert.Equal(("ziel.example", "https://ziel.example/dav/", "anna", "geheim"), (m.Name, m.Url, m.User, m.Password));
        Assert.Null(m.Access);
        Assert.Equal(before + 1, db.GetJson<List<UserMount>>(ExternalFolders.UserKey).Count);
        // weiter eingeschaltet und bestätigt
        Assert.True(PlainBackup.Ready(now));

        // Ohne Adresse: einfach kein Ziel gewählt
        PlainBackup.Save(db, new PlainBackupSettings { Target = PlainBackup.WebDav });
        PlainBackupService.Settings(Space());
        Assert.Equal(PlainBackup.External, PlainBackup.Settings(db).Target);
        Assert.Null(PlainBackup.Settings(db).Mount);
        Assert.Equal(before + 1, db.GetJson<List<UserMount>>(ExternalFolders.UserKey).Count);
    }
}
