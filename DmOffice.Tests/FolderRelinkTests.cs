using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Audio;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>
/// Ordner neu zuordnen (Kennung relink): ein Galerie- bzw. Audiothek-Ordner zeigt danach auf einen anderen externen Ordner mit anderer
/// Pfadtiefe. Index, Sterne, Farbe, P/X, Album, Ort, Bearbeitung und Vorschaubilder bleiben, nichts wird neu eingelesen; was am neuen
/// Ort fehlt, geht beim Abgleich raus und steht in der Aktivität.
/// </summary>
[Collection("server")]
public class FolderRelinkTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public FolderRelinkTests(ChatFixture fx) { _fx = fx; GalleryList.Grace = TimeSpan.Zero; FolderRelink.Pause = TimeSpan.Zero; }

    private static byte[] Jpeg(SKColor color, int w)
    {
        using var bmp = new SKBitmap(w, 48);
        using (var cv = new SKCanvas(bmp)) cv.Clear(color);
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    /// <summary>Datei mit Änderungszeit; mit Millisekunden wie über CIFS, ohne wie über SFTP.</summary>
    private static void Put(string file, byte[] data, bool millis)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, data);
        File.SetLastWriteTimeUtc(file, new DateTime(2024, 5, 1, 10, 0, 7, millis ? 123 : 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Pfade_und_Zeiten()
    {
        Assert.Equal("Fotos/Archiv/PRI/2024/a.jpg", FolderRelink.Map("PRI", "Fotos/Archiv/PRI", "PRI/2024/a.jpg"));
        Assert.Equal("a.jpg", FolderRelink.Map("PRI", "", "PRI/a.jpg"));
        Assert.Equal("X/PRI/a.jpg", FolderRelink.Map("", "X", "PRI/a.jpg"));
        Assert.Null(FolderRelink.Map("PRI", "X", "PRIVAT/a.jpg"));
        Assert.True(FolderRelink.SameTime(1714557607123, 1714557607000, coarse: true));
        Assert.False(FolderRelink.SameTime(1714557607123, 1714557607000, coarse: false));
        Assert.False(FolderRelink.SameTime(1714557607123, 1714557608000, coarse: true));
    }

    [Fact]
    public void Gemeinsamer_Bestand_wird_beim_Umziehen_nur_ergaenzt()
    {
        var shared = GalleryFolders.Shared;
        Assert.NotNull(shared);
        var stamp = GalleryProxies.Stamp(500, 1_714_557_607_000);
        var m = "cp-" + Guid.NewGuid().ToString("N")[..6];
        shared!.Put(m + "-a", "x.jpg", stamp, 2, null, [1, 2, 3]);
        shared.Put(m + "-a", "y.jpg", stamp, 2, null, [4, 5, 6]);
        shared.Put(m + "-b", "x.jpg", stamp, 2, null, [9, 9, 9]);
        shared.CopyAll(m + "-a", m + "-b", [("x.jpg", "x.jpg"), ("y.jpg", "y.jpg")]);
        Assert.Equal([9, 9, 9], shared.Thumb(m + "-b", "x.jpg", stamp));
        Assert.Equal([4, 5, 6], shared.Thumb(m + "-b", "y.jpg", stamp));
        var audio = AudioFolders.Shared;
        if (audio != null)
        {
            audio.Put(m + "-a", "x.mp3", stamp, new AudioFolders.Tags("Alt", null, null, null, null, null, null, null, null, null), null, null);
            audio.Put(m + "-b", "x.mp3", stamp, new AudioFolders.Tags("Echt", null, null, null, null, null, null, null, null, null), null, null);
            audio.CopyAll(m + "-a", m + "-b", [("x.mp3", "x.mp3")]);
            Assert.Equal("Echt", audio.Find(m + "-b", "x.mp3", stamp)?.Tags.Title);
        }
    }

    [Fact]
    public async Task Galerie_Server_Mount_auf_anderen_Ordner_mit_anderer_Tiefe()
    {
        Assert.True(Features.On(Features.Relink));
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
        var ben = _fx.Space("ben");
        var c = _fx.As("ben");
        var writeBefore = ben.Settings.PhotoWriteXmp;
        var root = Directory.CreateTempSubdirectory("dmrelink").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            ben.UpdateSettings(s => s.PhotoWriteXmp = false);
            // Alter Ort: Freigabe „a“ mit PRI; neuer Ort: Freigabe „b“, dieselben Dateien zwei Ebenen tiefer - weg.jpg fehlt dort
            var (one, two, gone) = (Jpeg(SKColors.Coral, 64), Jpeg(SKColors.SteelBlue, 80), Jpeg(SKColors.Olive, 72));
            Put(Path.Combine(root, "a", "PRI", "2024", "eins.jpg"), one, millis: true);
            Put(Path.Combine(root, "a", "PRI", "zwei.jpg"), two, millis: true);
            Put(Path.Combine(root, "a", "PRI", "weg.jpg"), gone, millis: true);
            Put(Path.Combine(root, "b", "Fotos", "Archiv", "PRI", "2024", "eins.jpg"), one, millis: false);
            Put(Path.Combine(root, "b", "Fotos", "Archiv", "PRI", "zwei.jpg"), two, millis: false);
            config.Update(s => s.ExternalFolders = [
                new ExternalFolderDef { Id = "rel-a", Name = "Alt", Path = Path.Combine(root, "a") },
                new ExternalFolderDef { Id = "rel-b", Name = "Neu", Path = Path.Combine(root, "b") }]);
            foreach (var old in GalleryFolders.Of(ben.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "rel-a", path = "PRI" })).StatusCode);
            var job = new GalleryScanJob(spaces, config, NullLogger<GalleryScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);
            var src = GalleryFolders.Of(ben.Db).Single();
            var id = src.Id;
            Assert.Equal(3, Rows(ben, id).Count);
            Assert.All(Rows(ben, id).Values, r => Assert.Equal(2, r.Stage));

            // Sterne, Farbe, P/X, Album, Ort, Bearbeitung, große Vorschau
            var r1 = await c.PostAsJsonAsync("/api/gallery/info", new { items = new[] { $"ext:{id}/PRI/zwei.jpg" }, rating = 4, label = "red", pick = 1 });
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            await GalleryInfo.AllPending();
            var album = (await (await c.PostAsJsonAsync("/api/gallery/albums", new { name = "Relink" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/gallery/albums/{album}/add", new { paths = new[] { $"ext:{id}/PRI/zwei.jpg" } })).StatusCode);
            GalleryFolders.SetGps(ben.Db, id, "PRI/2024/eins.jpg", 50.1, 8.6);
            DmOffice.Dav.PhotoEdits.Ensure(ben.Db);
            ben.Db.Exec("INSERT OR REPLACE INTO photo_edits(key, edit, modified) VALUES($k, '{\"rotate\":90}', 1)", ("$k", $"x:{id}/PRI/2024/eins.jpg"));
            var smallBefore = await GalleryFolders.ThumbAsync(ben, config, $"{id}/PRI/zwei.jpg", Thumbs.Small, CancellationToken.None);
            Assert.NotNull(smallBefore);
            // Ohne „Vorschaubilder vorab erzeugen“ liefert die Großansicht die mittlere Vorschau vom Einlesen; die große (2048) entsteht
            // aus dem Original wie für Bearbeiten und Gesichter
            Assert.NotNull(await GalleryFolders.ThumbAsync(ben, config, $"{id}/PRI/zwei.jpg", Thumbs.Large, CancellationToken.None, original: true));
            var rowsBefore = Rows(ben, id);
            var stampBefore = GalleryProxies.Stamp(rowsBefore["PRI/zwei.jpg"].Size, rowsBefore["PRI/zwei.jpg"].Modified);
            Assert.NotNull(GalleryFolders.Proxies);
            Assert.True(GalleryFolders.Proxies!.Has(GalleryProxies.SharedScope, "rel-a", "PRI/zwei.jpg", stampBefore));
            Assert.True(!Features.On(Features.GalleryMedium) || GalleryFolders.Medium?.Has(GalleryProxies.SharedScope, "rel-a", "PRI/zwei.jpg", stampBefore) != false); // [feature:galmedium]

            // Stichprobe: 2 von 3 am neuen Ort
            var probe = await (await c.PostAsJsonAsync($"/api/gallery/sources/{id}/relink/probe", new { mount = "rel-b", path = "Fotos/Archiv/PRI" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(3, probe.GetProperty("checked").GetInt32());
            Assert.Equal(2, probe.GetProperty("found").GetInt32());
            Assert.Equal(3, probe.GetProperty("total").GetInt32());
            // Schon in der Galerie bzw. derselbe Ort: abgelehnt
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/gallery/sources/{id}/relink/probe", new { mount = "rel-a", path = "PRI" })).StatusCode);

            // Umstellen
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/gallery/sources/{id}/relink", new { mount = "rel-b", path = "Fotos/Archiv/PRI" })).StatusCode);
            src = GalleryFolders.Of(ben.Db).Single();
            Assert.Equal(id, src.Id);
            Assert.Equal(("rel-b", "Fotos/Archiv/PRI"), (src.Mount, src.Path));
            Assert.True(src.Relink is { Pending: true });
            var moved = Rows(ben, id);
            Assert.Equal(["Fotos/Archiv/PRI/2024/eins.jpg", "Fotos/Archiv/PRI/weg.jpg", "Fotos/Archiv/PRI/zwei.jpg"], moved.Keys.Order(StringComparer.Ordinal));

            // Abgleich, danach wie gewohnt einlesen (zwei Durchgänge)
            await job.RunOnceAsync(CancellationToken.None);
            await job.RunOnceAsync(CancellationToken.None);
            src = GalleryFolders.Of(ben.Db).Single();
            Assert.False(src.Relink!.Pending);
            Assert.Equal((3, 2, 1), (src.Relink.Total, src.Relink.Found, src.Relink.RemovedCount));
            Assert.Equal(["Fotos/Archiv/PRI/weg.jpg"], src.Relink.Removed);
            var act = Activity.Get(ben.User.Id, Activity.GalleryRelink);
            Assert.NotNull(act);
            Assert.Equal(["Fotos/Archiv/PRI/weg.jpg"], act!.Report);
            Assert.Contains("2 von 3 am neuen Ziel gefunden", act.Detail);

            // Nichts neu eingelesen: Größe, Stufe und Vorschaubilder unverändert; die Änderungszeit ist die vom neuen Ort (ohne die
            // Millisekunden vom alten), damit die Datei im gemeinsamen Bestand nur einen Stand hat
            var after = Rows(ben, id);
            Assert.Equal(2, after.Count);
            foreach (var (p, r) in after)
            {
                var old = rowsBefore[p.Replace("Fotos/Archiv/", "")];
                Assert.Equal((old.Size, old.Modified - 123, 2), (r.Size, r.Modified, r.Stage));
            }
            var newId = $"{id}/Fotos/Archiv/PRI/zwei.jpg";
            Assert.Equal(smallBefore, await GalleryFolders.ThumbAsync(ben, config, newId, Thumbs.Small, CancellationToken.None));
            var stampAfter = GalleryProxies.Stamp(after["Fotos/Archiv/PRI/zwei.jpg"].Size, after["Fotos/Archiv/PRI/zwei.jpg"].Modified);
            Assert.True(GalleryFolders.Proxies.Has(GalleryProxies.SharedScope, "rel-b", "Fotos/Archiv/PRI/zwei.jpg", stampAfter));
            Assert.True(!Features.On(Features.GalleryMedium) || GalleryFolders.Medium?.Has(GalleryProxies.SharedScope, "rel-b", "Fotos/Archiv/PRI/zwei.jpg", stampAfter) != false); // [feature:galmedium]

            // Sterne, Farbe, P/X aus der eigenen Datenbank, Ort, Bearbeitung, Album
            var meta = (await c.GetFromJsonAsync<JsonElement>("/api/gallery/external")).GetProperty("meta").EnumerateArray()
                .Single(x => x.GetProperty("key").GetString() == "ext:" + newId);
            Assert.Equal(4, meta.GetProperty("rating").GetInt32());
            Assert.Equal("red", meta.GetProperty("label").GetString());
            Assert.Equal(1, meta.GetProperty("pick").GetInt32());
            Assert.Equal((true, 50.1, 8.6), GalleryFolders.Gps(ben.Db, id, "Fotos/Archiv/PRI/2024/eins.jpg"));
            Assert.NotNull(DmOffice.Dav.PhotoEdits.Get(ben.Db, $"x:{id}/Fotos/Archiv/PRI/2024/eins.jpg"));
            var items = (await c.GetFromJsonAsync<JsonElement>($"/api/gallery/albums/{album}/items")).GetProperty("items").EnumerateArray().ToList();
            Assert.Single(items);
            Assert.Contains("Fotos/Archiv/PRI/zwei.jpg", items[0].GetRawText());

            await c.DeleteAsync($"/api/gallery/albums/{album}");
            await c.DeleteAsync("/api/gallery/sources/" + id);
        }
        finally
        {
            ben.UpdateSettings(s => s.PhotoWriteXmp = writeBefore);
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Galerie_ohne_Zugriff_auf_den_alten_Server_Ordner_uebernimmt_nichts_aus_dessen_Bestand()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
        var ben = _fx.Space("ben");
        var c = _fx.As("ben");
        var writeBefore = ben.Settings.PhotoWriteXmp;
        var root = Directory.CreateTempSubdirectory("dmrelink").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            ben.UpdateSettings(s => s.PhotoWriteXmp = false);
            var img = Jpeg(SKColors.Coral, 64);
            Put(Path.Combine(root, "a", "eins.jpg"), img, millis: false);
            Put(Path.Combine(root, "b", "eins.jpg"), img, millis: false);
            config.Update(s => s.ExternalFolders = [
                new ExternalFolderDef { Id = "reld-a", Name = "Alt", Path = Path.Combine(root, "a") },
                new ExternalFolderDef { Id = "reld-b", Name = "Neu", Path = Path.Combine(root, "b") }]);
            foreach (var old in GalleryFolders.Of(ben.Db)) await c.DeleteAsync("/api/gallery/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "reld-a", path = "" })).StatusCode);
            await new GalleryScanJob(spaces, config, NullLogger<GalleryScanJob>.Instance).RunOnceAsync(CancellationToken.None);
            var id = GalleryFolders.Of(ben.Db).Single().Id;
            Assert.Equal(2, Rows(ben, id)["eins.jpg"].Stage);
            Assert.NotNull(await GalleryFolders.ThumbAsync(ben, config, $"{id}/eins.jpg", Thumbs.Large, CancellationToken.None, original: true));
            var r = Rows(ben, id)["eins.jpg"];
            var stamp = GalleryProxies.Stamp(r.Size, r.Modified);
            Assert.True(GalleryFolders.Proxies!.Has(GalleryProxies.SharedScope, "reld-a", "eins.jpg", stamp));

            // Zugriff auf den alten Ordner entzogen: Umstellen geht, aus dem Bestand des alten Ordners kommt nichts mit
            config.Update(s => s.ExternalFolders = [
                new ExternalFolderDef { Id = "reld-a", Name = "Alt", Path = Path.Combine(root, "a"), Users = ["jemand-anderes"] },
                new ExternalFolderDef { Id = "reld-b", Name = "Neu", Path = Path.Combine(root, "b") }]);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/gallery/sources/{id}/relink", new { mount = "reld-b", path = "" })).StatusCode);
            Assert.Equal(0, Rows(ben, id)["eins.jpg"].Stage);
            Assert.False(GalleryFolders.Proxies.Has(GalleryProxies.SharedScope, "reld-b", "eins.jpg", stamp));
            await c.DeleteAsync("/api/gallery/sources/" + id);
        }
        finally
        {
            ben.UpdateSettings(s => s.PhotoWriteXmp = writeBefore);
            config.Update(s => s.ExternalFolders = before);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static Dictionary<string, (long Size, long Modified, int Stage)> Rows(UserSpace space, string source) =>
        space.Db.Query("SELECT path, size, modified, stage FROM gallery_ext WHERE source = $s", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3)), ("$s", source))
            .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3, x.Item4));

    [Fact]
    public async Task Audiothek_Titel_Hoerverlauf_und_Wiedergabeliste_bleiben()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
        var cleo = _fx.Space("cleo");
        var c = _fx.As("cleo");
        var root = Directory.CreateTempSubdirectory("dmrelinka").FullName;
        var before = config.Current.ExternalFolders;
        var wasOff = config.Current.ModulesOff;
        var tagPause = AudioFolders.TagPause;
        try
        {
            AudioFolders.TagPause = TimeSpan.Zero;
            var song = AudioTests.Mp3(200);
            Put(Path.Combine(root, "a", "Musik", "Album", "lied.mp3"), song, millis: true);
            Put(Path.Combine(root, "a", "Musik", "fehlt.mp3"), AudioTests.Mp3(120), millis: true);
            Put(Path.Combine(root, "b", "Neu", "Sammlung", "Album", "lied.mp3"), song, millis: false);
            config.Update(s => { s.ModulesOff = []; s.ExternalFolders = [
                new ExternalFolderDef { Id = "rela-a", Name = "Alt", Path = Path.Combine(root, "a") },
                new ExternalFolderDef { Id = "rela-b", Name = "Neu", Path = Path.Combine(root, "b") }]; });
            foreach (var old in AudioFolders.Of(cleo.Db)) await c.DeleteAsync("/api/audio/sources/" + old.Id);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/audio/sources", new { mount = "rela-a", path = "Musik" })).StatusCode);
            var job = new AudioScanJob(spaces, config, NullLogger<AudioScanJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);
            var src = AudioFolders.Of(cleo.Db).Single();
            var id = src.Id;
            var track = $"{id}/Musik/Album/lied.mp3";
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/audio/played", new { id = track, ms = 600_000 })).StatusCode);
            var list = (await (await c.PostAsJsonAsync("/api/audio/playlists", new { name = "Liste", tracks = new[] { track } })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

            var probe = await (await c.PostAsJsonAsync($"/api/audio/sources/{id}/relink/probe", new { mount = "rela-b", path = "Neu/Sammlung" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal((2, 1, 2), (probe.GetProperty("checked").GetInt32(), probe.GetProperty("found").GetInt32(), probe.GetProperty("total").GetInt32()));
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/audio/sources/{id}/relink", new { mount = "rela-b", path = "Neu/Sammlung" })).StatusCode);

            var rows = cleo.Db.Query("SELECT path, mount, modified, stage FROM audio_track WHERE source = $s ORDER BY path", r => (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3)), ("$s", id));
            Assert.Equal(["Neu/Sammlung/Album/lied.mp3", "Neu/Sammlung/fehlt.mp3"], rows.Select(x => x.Item1));
            Assert.All(rows, x => Assert.Equal(("rela-b", 123L, 1), (x.Item2, x.Item3 % 1000, x.Item4)));
            await job.RunOnceAsync(CancellationToken.None);
            await job.RunOnceAsync(CancellationToken.None);
            src = AudioFolders.Of(cleo.Db).Single();
            Assert.False(src.Relink!.Pending);
            Assert.Equal((2, 1, 1), (src.Relink.Total, src.Relink.Found, src.Relink.RemovedCount));
            Assert.Equal(["Neu/Sammlung/fehlt.mp3"], Activity.Get(cleo.User.Id, Activity.AudioRelink)!.Report);
            // Nicht neu eingelesen (Stufe und Zeit wie vorher), Hörverlauf und Wiedergabeliste am neuen Ort
            Assert.Equal([rows[0]], cleo.Db.Query("SELECT path, mount, modified, stage FROM audio_track WHERE source = $s", r => (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3)), ("$s", id)));
            var stats = await c.GetFromJsonAsync<JsonElement>("/api/audio/stats");
            Assert.Contains($"{id}/Neu/Sammlung/Album/lied.mp3", stats.GetRawText());
            var pl = await c.GetFromJsonAsync<JsonElement>($"/api/audio/playlists/{list}");
            Assert.Contains($"{id}/Neu/Sammlung/Album/lied.mp3", pl.GetRawText());
            await c.DeleteAsync($"/api/audio/playlists/{list}");
            await c.DeleteAsync("/api/audio/sources/" + id);
        }
        finally
        {
            AudioFolders.TagPause = tagPause;
            config.Update(s => { s.ExternalFolders = before; s.ModulesOff = wasOff; });
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
