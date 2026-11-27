using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using DmOffice.Journal;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Fotos eines Tages fürs Tagebuch: nur über den Index der Aufnahmezeit gelesen - genau der Tag in der Zeitzone, eigene Ablage und Galerie-Ordner.</summary>
[Collection("server")]
public class JournalDayPhotosTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public JournalDayPhotosTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public void Nur_Fotos_des_Tages_in_der_eigenen_Zeitzone()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var user = "tag" + Guid.NewGuid().ToString("N")[..6];
        var u = services.GetRequiredService<UserStore>().Create(user, "Tag", "geheim-123456", isAdmin: false);
        var space = services.GetRequiredService<Spaces>().Get(u.Id);
        var before = config.Current.ExternalFolders;
        var root = Directory.CreateTempSubdirectory("dmtag").FullName;
        try
        {
            var day = new DateOnly(2025, 7, 15);
            long At(int d, int h, int m) => new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.AddDays(d).ToDateTime(new TimeOnly(h, m)), space.Zone), TimeSpan.Zero).ToUnixTimeMilliseconds();
            var times = new (string Name, long Taken)[] { ("vortag", At(-1, 23, 50)), ("frueh", At(0, 0, 10)), ("mittag", At(0, 12, 0)), ("spaet", At(0, 23, 50)), ("folgetag", At(1, 0, 10)) };

            // Eigene Ablage
            FileMeta.Ensure(space.Db);
            for (var i = 0; i < times.Length; i++)
            {
                FileStore.Put(space.Db, $"{times[i].Name}.jpg", [(byte)i, 1, 2, 3], "image/jpeg");
                space.Db.Exec("INSERT OR REPLACE INTO file_meta(path, etag, ok, taken) VALUES ($p, 'e', 1, $t)", ("$p", $"{times[i].Name}.jpg"), ("$t", times[i].Taken + 7_000));
            }
            // Galerie-Ordner (anderer Name und andere Sekunde: keine Dublette)
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "tagnas", Name = "NAS", Path = root }]);
            GalleryFolders.Save(space.Db, [new GallerySource { Id = "tagsrc", Mount = "tagnas", Path = "", Watch = false }]);
            GalleryFolders.Ensure(space.Db);
            foreach (var (name, taken) in times)
                space.Db.Exec("INSERT INTO gallery_ext(source, path, size, modified, mime, taken, camera, stage) VALUES ('tagsrc', $p, 1000, 1700000000000, 'image/jpeg', $t, 'Kamera', 2)",
                    ("$p", $"Urlaub/nas-{name}.jpg"), ("$t", taken));
            // Fremde Quelle (kein Galerie-Ordner dieses Benutzers): nicht dabei
            space.Db.Exec("INSERT INTO gallery_ext(source, path, size, modified, mime, taken, camera, stage) VALUES ('fremd', 'x.jpg', 1000, 1700000000000, 'image/jpeg', $t, 'Kamera', 2)", ("$t", times[2].Taken));

            var photos = JournalApi.PhotosOf(space, config, day);
            Assert.Equal(["frueh.jpg", "mittag.jpg", "nas-frueh.jpg", "nas-mittag.jpg", "nas-spaet.jpg", "spaet.jpg"],
                photos.Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(3, photos.Count(p => p.Kind == "ext"));
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
