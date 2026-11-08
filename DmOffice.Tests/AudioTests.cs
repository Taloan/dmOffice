using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Audio;
using DmOffice.Core;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Audiothek: Musik aus einem externen Ordner einlesen (Tags, Cover, Dauer), abspielen mit Spulen, Hörverlauf.</summary>
[Collection("server")]
public class AudioTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public AudioTests(ChatFixture fx) => _fx = fx;

    /// <summary>MP3 aus leeren Frames (MPEG-1 Layer III, 128 kbit/s, 44,1 kHz, je 417 Bytes und 26 ms) - ATL erkennt Format und Dauer.</summary>
    internal static byte[] Mp3(int frames)
    {
        var data = new byte[frames * 417];
        for (var i = 0; i < frames; i++) { data[i * 417] = 0xFF; data[i * 417 + 1] = 0xFB; data[i * 417 + 2] = 0x90; data[i * 417 + 3] = 0x44; }
        return data;
    }

    private static byte[] Jpeg(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(color);
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    /// <summary>MP3 mit Tags und eingebettetem Cover schreiben.</summary>
    private static void Tagged(string file, byte[]? cover)
    {
        File.WriteAllBytes(file, Mp3(400));
        var t = new ATL.Track(file)
        {
            Title = "Lied Eins", Artist = "Die Band", AlbumArtist = "Die Band", Album = "Erstes Album", TrackNumber = 3, DiscNumber = 1, Year = 1999, Genre = "Rock",
        };
        if (cover != null) t.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(cover, ATL.PictureInfo.PIC_TYPE.Front));
        Assert.True(t.Save());
    }

    private async Task<(ServerConfig Config, string Root, Action Restore)> SetupAsync(string mount)
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var root = Directory.CreateTempSubdirectory("dmaudio").FullName;
        var before = config.Current.ExternalFolders;
        var wasOff = config.Current.ModulesOff;
        config.Update(s => { s.ExternalFolders = [new ExternalFolderDef { Id = mount, Name = "Musik", Path = root }]; s.ModulesOff = []; });
        await Task.CompletedTask;
        return (config, root, () =>
        {
            config.Update(s => { s.ExternalFolders = before; s.ModulesOff = wasOff; });
            try { Directory.Delete(root, true); } catch (IOException) { }
        });
    }

    private static Dictionary<string, JsonElement> Row(JsonElement list, JsonElement track)
    {
        var fields = list.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToList();
        return fields.Select((f, i) => (f, track[i])).ToDictionary(x => x.f, x => x.Item2);
    }

    [Fact]
    public async Task Ausgeschaltet_404_nur_die_Verwaltung_schaltet()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var wasOff = config.Current.ModulesOff;
        try
        {
            config.Update(s => s.ModulesOff = [Features.Audio]);
            Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("cleo").GetAsync("/api/audio/tracks")).StatusCode);
            Assert.False(AudioFolders.Enabled(_fx.Space("cleo").User));

            // Nur die Verwaltung schaltet (Hauptschalter der Module)
            Assert.NotEqual(HttpStatusCode.OK, (await _fx.As("cleo").PutAsJsonAsync("/api/admin/modules", new { id = "audio", on = true })).StatusCode);
            config.Update(s => s.ModulesOff = []);
            Assert.Equal(HttpStatusCode.OK, (await _fx.As("cleo").GetAsync("/api/audio/tracks")).StatusCode);
            Assert.True(AudioFolders.Enabled(_fx.Space("cleo").User));
        }
        finally { config.Update(s => s.ModulesOff = wasOff); }
    }

    [Fact]
    public async Task Server_Ordner_wird_fuer_alle_nur_einmal_gelesen()
    {
        var (config, root, restore) = await SetupAsync("musik-gemeinsam");
        try
        {
            var file = Path.Combine(root, "03 Lied Eins.mp3");
            Tagged(file, Jpeg(400, 400, SKColors.OrangeRed));
            var first = _fx.Space("anna");
            AudioFolders.Save(first.Db, [new AudioSource { Id = "aud-a", Mount = "musik-gemeinsam", Path = "" }]);
            var a = AudioFolders.Of(first.Db).Single();
            await AudioFolders.ScanAsync(first, config, a, CancellationToken.None);
            Assert.Equal(0, await AudioFolders.TagsAsync(first, config, a, DateTime.UtcNow.AddMinutes(1), CancellationToken.None));

            // Inhalt kaputt machen, Größe und Zeit bleiben: wer jetzt aufnimmt, bekommt die schon gelesenen Tags samt Cover
            var info = new FileInfo(file);
            var (size, time) = (info.Length, info.LastWriteTimeUtc);
            File.WriteAllBytes(file, new byte[size]);
            File.SetLastWriteTimeUtc(file, time);
            var second = _fx.Space("ben");
            AudioFolders.Save(second.Db, [new AudioSource { Id = "aud-b", Mount = "musik-gemeinsam", Path = "" }]);
            var b = AudioFolders.Of(second.Db).Single();
            await AudioFolders.ScanAsync(second, config, b, CancellationToken.None);
            Assert.Equal(0, await AudioFolders.TagsAsync(second, config, b, DateTime.UtcNow.AddMinutes(1), CancellationToken.None));
            var row = second.Db.Query("SELECT title, artist, cover FROM audio_track WHERE source = 'aud-b'", r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2))).Single();
            Assert.Equal(("Lied Eins", "Die Band"), (row.Item1, row.Item2));
            Assert.NotNull(row.Item3);
            Assert.NotNull(AudioFolders.Cover(second.Db, row.Item3!));
        }
        finally
        {
            foreach (var u in new[] { "anna", "ben" }) AudioFolders.Save(_fx.Space(u).Db, []);
            restore();
        }
    }

    [Fact]
    public async Task Einlesen_Tags_Cover_Abspielen_Hoerverlauf()
    {
        var (config, root, restore) = await SetupAsync("musik01");
        var cleo = _fx.Space("cleo");
        var client = _fx.As("cleo");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Erstes Album"));
            Directory.CreateDirectory(Path.Combine(root, "Ohne Tags"));
            Directory.CreateDirectory(Path.Combine(root, ".versteckt"));
            Tagged(Path.Combine(root, "Erstes Album", "03 Lied Eins.mp3"), Jpeg(800, 800, SKColors.OrangeRed));
            File.WriteAllBytes(Path.Combine(root, "Ohne Tags", "Mein Stück.mp3"), Mp3(200));
            File.WriteAllBytes(Path.Combine(root, "Ohne Tags", "Folder.JPG"), Jpeg(500, 400, SKColors.SeaGreen));
            File.WriteAllBytes(Path.Combine(root, "Ohne Tags", "notizen.txt"), "x"u8.ToArray());
            File.WriteAllBytes(Path.Combine(root, ".versteckt", "geheim.mp3"), Mp3(10));

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/audio/sources", new { mount = "musik01", path = "" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/audio/sources", new { mount = "musik01", path = "" })).StatusCode);
            var src = AudioFolders.Of(cleo.Db).Single(x => x.Mount == "musik01");

            // Stufe 1: nur aufgelistet, Titel aus dem Dateinamen, Album aus dem Ordner
            Assert.Equal(2, await AudioFolders.ScanAsync(cleo, config, src, CancellationToken.None));
            Assert.Equal(2, AudioFolders.Pending(cleo.Db, src.Id));
            var list = await client.GetFromJsonAsync<JsonElement>("/api/audio/tracks");
            var plain = list.GetProperty("tracks").EnumerateArray().Select(t => Row(list, t)).Single(r => r["id"].GetString()!.EndsWith("Mein Stück.mp3"));
            Assert.Equal("Mein Stück", plain["title"].GetString());
            Assert.Equal("Ohne Tags", plain["album"].GetString());

            // Stufe 2: Tags, Dauer, Cover (eingebettet bzw. aus dem Ordner)
            Assert.Equal(0, await AudioFolders.TagsAsync(cleo, config, src, DateTime.UtcNow.AddMinutes(1), CancellationToken.None));
            var res = await client.GetAsync("/api/audio/tracks");
            var etag = res.Headers.ETag!.Tag;
            list = await res.Content.ReadFromJsonAsync<JsonElement>();
            var rows = list.GetProperty("tracks").EnumerateArray().Select(t => Row(list, t)).ToList();
            var tagged = rows.Single(r => r["id"].GetString() == src.Id + "/Erstes Album/03 Lied Eins.mp3");
            Assert.Equal("Lied Eins", tagged["title"].GetString());
            Assert.Equal("Die Band", tagged["artist"].GetString());
            Assert.Equal("Erstes Album", tagged["album"].GetString());
            Assert.Equal(3, tagged["track"].GetInt32());
            Assert.Equal(1999, tagged["year"].GetInt32());
            Assert.Equal("Rock", tagged["genre"].GetString());
            Assert.InRange(tagged["duration"].GetDouble(), 9.5, 11.5);
            plain = rows.Single(r => r["id"].GetString()!.EndsWith("Mein Stück.mp3"));
            Assert.Equal("Mein Stück", plain["title"].GetString());
            Assert.InRange(plain["duration"].GetDouble(), 4.5, 6);
            var covers = new[] { tagged["cover"].GetString()!, plain["cover"].GetString()! };
            Assert.NotEqual(covers[0], covers[1]);
            foreach (var c in covers)
            {
                var img = await client.GetAsync("/api/audio/cover/" + c);
                Assert.Equal("image/jpeg", img.Content.Headers.ContentType?.MediaType);
                using var bmp = SKBitmap.Decode(await img.Content.ReadAsByteArrayAsync());
                Assert.True(Math.Max(bmp.Width, bmp.Height) <= AudioFolders.CoverSize);
            }

            // Unverändert: 304
            var again = new HttpRequestMessage(HttpMethod.Get, "/api/audio/tracks");
            again.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
            Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);

            // Abspielen mit Spulen
            var id = src.Id + "/Ohne Tags/Mein Stück.mp3";
            var file = File.ReadAllBytes(Path.Combine(root, "Ohne Tags", "Mein Stück.mp3"));
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/audio/stream?id=" + Uri.EscapeDataString(id));
            req.Headers.Range = new RangeHeaderValue(1000, 1999);
            var part = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
            Assert.Equal("audio/mpeg", part.Content.Headers.ContentType?.MediaType);
            Assert.Equal(file[1000..2000], await part.Content.ReadAsByteArrayAsync());
            var whole = await client.GetAsync("/api/audio/stream?id=" + Uri.EscapeDataString(id));
            Assert.Equal(file, await whole.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/audio/stream?id=" + Uri.EscapeDataString(src.Id + "/Ohne Tags/notizen.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/audio/stream?id=" + Uri.EscapeDataString("fremd/x.mp3"))).StatusCode);
            // Andere sehen die Ordner der anderen nicht
            Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").GetAsync("/api/audio/stream?id=" + Uri.EscapeDataString(id))).StatusCode);

            // Hörverlauf: 3 s von 5 s zählen (halbe Dauer), 1 s ist übersprungen
            var p = await (await client.PostAsJsonAsync("/api/audio/played", new { id, ms = 3000, device = "Web" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, p.GetProperty("plays").GetInt32());
            await client.PostAsJsonAsync("/api/audio/played", new { id, ms = 1000 });
            var stats = await client.GetFromJsonAsync<Dictionary<string, JsonElement>>("/api/audio/stats");
            Assert.Equal(1, stats![id][0].GetInt32());
            Assert.Equal(1, stats[id][1].GetInt32());
            Assert.True(stats[id][2].GetInt64() > 0);
            Assert.Equal(304, (int)(await client.SendAsync(Copy(again))).StatusCode);   // der Hörverlauf ändert die Liste nicht

            // Datei weg: verschwindet beim nächsten Einlesen
            File.Delete(Path.Combine(root, "Erstes Album", "03 Lied Eins.mp3"));
            Assert.Equal(1, await AudioFolders.ScanAsync(cleo, config, src, CancellationToken.None));
            Assert.Null(AudioFolders.Cover(cleo.Db, covers[0]));

            // Ordner herausnehmen und wieder aufnehmen: der Hörverlauf ist noch da
            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/audio/sources/" + src.Id)).StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/audio/tracks")).GetProperty("tracks").EnumerateArray());
            await client.PostAsJsonAsync("/api/audio/sources", new { mount = "musik01", path = "Ohne Tags" });
            var src2 = AudioFolders.Of(cleo.Db).Single(x => x.Mount == "musik01");
            await AudioFolders.ScanAsync(cleo, config, src2, CancellationToken.None);
            stats = await client.GetFromJsonAsync<Dictionary<string, JsonElement>>("/api/audio/stats");
            Assert.Equal(1, stats![src2.Id + "/Ohne Tags/Mein Stück.mp3"][0].GetInt32());
            await client.DeleteAsync("/api/audio/sources/" + src2.Id);
        }
        finally { restore(); }
    }

    [Fact]
    public async Task Playlists_und_Auto_Playlists()
    {
        var (config, root, restore) = await SetupAsync("musik02");
        var cleo = _fx.Space("cleo");
        var client = _fx.As("cleo");
        try
        {
            foreach (var n in new[] { "a", "b", "c", "d" }) File.WriteAllBytes(Path.Combine(root, n + ".mp3"), Mp3(400));
            Directory.CreateDirectory(Path.Combine(root, "Mehr"));
            File.WriteAllBytes(Path.Combine(root, "Mehr", "e.mp3"), Mp3(400));
            await client.PostAsJsonAsync("/api/audio/sources", new { mount = "musik02", path = "" });
            var src = AudioFolders.Of(cleo.Db).Single(x => x.Mount == "musik02");
            await AudioFolders.ScanAsync(cleo, config, src, CancellationToken.None);
            await AudioFolders.TagsAsync(cleo, config, src, DateTime.UtcNow.AddMinutes(1), CancellationToken.None);
            string Id(string n) => src.Id + "/" + n;

            // Anlegen, anhängen (unbekannte fallen weg), umsortieren, umbenennen
            var pl = (await (await client.PostAsJsonAsync("/api/audio/playlists", new { name = "Abend", tracks = new[] { Id("a.mp3"), Id("b.mp3"), "fremd/x.mp3" } }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/audio/playlists", new { name = " " })).StatusCode);
            await client.PostAsJsonAsync($"/api/audio/playlists/{pl}/add", new { tracks = new[] { Id("Mehr/e.mp3"), Id("c.mp3") } });
            async Task<List<string>> Tracks() => (await client.GetFromJsonAsync<JsonElement>($"/api/audio/playlists/{pl}")).GetProperty("tracks").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Equal([Id("a.mp3"), Id("b.mp3"), Id("Mehr/e.mp3"), Id("c.mp3")], await Tracks());
            await client.PutAsJsonAsync($"/api/audio/playlists/{pl}", new { name = "Später Abend", tracks = new[] { Id("c.mp3"), Id("Mehr/e.mp3"), Id("a.mp3") } });
            Assert.Equal([Id("c.mp3"), Id("Mehr/e.mp3"), Id("a.mp3")], await Tracks());
            var list = await client.GetFromJsonAsync<JsonElement>("/api/audio/playlists");
            var info = list.GetProperty("playlists").EnumerateArray().Single(x => x.GetProperty("id").GetString() == pl);
            Assert.Equal("Später Abend", info.GetProperty("name").GetString());
            Assert.Equal(3, info.GetProperty("count").GetInt32());
            Assert.Equal(5, list.GetProperty("auto").GetArrayLength());
            // Andere sehen sie nicht
            Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").GetAsync($"/api/audio/playlists/{pl}")).StatusCode);

            // Ordner „Mehr“ als eigener Audiothek-Ordner, der ganze Ordner heraus: die Einträge bleiben verborgen erhalten
            await client.PostAsJsonAsync("/api/audio/sources", new { mount = "musik02", path = "Mehr" });
            var mehr = AudioFolders.Of(cleo.Db).Single(x => x.Path == "Mehr");
            await AudioFolders.ScanAsync(cleo, config, mehr, CancellationToken.None);
            await client.DeleteAsync("/api/audio/sources/" + src.Id);
            Assert.Equal([mehr.Id + "/Mehr/e.mp3"], await Tracks());
            // Umsortieren, während a und c verborgen sind: sie gehen nicht verloren
            await client.PutAsJsonAsync($"/api/audio/playlists/{pl}", new { tracks = new[] { mehr.Id + "/Mehr/e.mp3" } });
            await client.PostAsJsonAsync("/api/audio/sources", new { mount = "musik02", path = "" });
            var src2 = AudioFolders.Of(cleo.Db).Single(x => x.Path == "");
            await AudioFolders.ScanAsync(cleo, config, src2, CancellationToken.None);
            Assert.Equal(3, (await Tracks()).Count);
            await AudioFolders.TagsAsync(cleo, config, src2, DateTime.UtcNow.AddMinutes(1), CancellationToken.None);

            // Auto-Playlists aus dem Hörverlauf
            var now = DateTime.UtcNow;
            AudioFolders.Played(cleo, config, src2.Id + "/a.mp3", 10_000, null, now.AddDays(-200));
            AudioFolders.Played(cleo, config, src2.Id + "/b.mp3", 10_000, null, now.AddHours(-2));
            AudioFolders.Played(cleo, config, src2.Id + "/b.mp3", 10_000, null, now.AddHours(-1));
            AudioFolders.Played(cleo, config, src2.Id + "/c.mp3", 1_000, null, now);   // übersprungen
            async Task<List<string>> Auto(string k) => (await client.GetFromJsonAsync<JsonElement>("/api/audio/playlists/auto/" + k)).GetProperty("tracks").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Equal([src2.Id + "/b.mp3", src2.Id + "/a.mp3"], await Auto("recent"));
            Assert.Equal(src2.Id + "/b.mp3", (await Auto("most"))[0]);
            Assert.Equal([src2.Id + "/a.mp3"], await Auto("rare"));
            var never = await Auto("never");
            Assert.DoesNotContain(src2.Id + "/a.mp3", never);
            Assert.Contains(src2.Id + "/c.mp3", never);
            Assert.Equal(5, (await Auto("added")).Count);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/audio/playlists/auto/gibtsnicht")).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/audio/playlists/{pl}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/audio/playlists/{pl}")).StatusCode);
            foreach (var x in AudioFolders.Of(cleo.Db).Where(x => x.Mount == "musik02").ToList()) await client.DeleteAsync("/api/audio/sources/" + x.Id);
        }
        finally { restore(); }
    }

    private static HttpRequestMessage Copy(HttpRequestMessage r)
    {
        var c = new HttpRequestMessage(r.Method, r.RequestUri);
        foreach (var e in r.Headers.IfNoneMatch) c.Headers.IfNoneMatch.Add(e);
        return c;
    }

    /// <summary>Ein kaputtes Cover darf das Einlesen nicht abbrechen (SkiaSharp wirft dabei statt null zu liefern).</summary>
    [Fact]
    public void Kaputtes_Cover_ergibt_kein_Cover_statt_Fehler()
    {
        var db = _fx.Space("ben").Db;
        AudioFolders.Ensure(db);
        Assert.Null(AudioFolders.StoreCover(db, [0xFF, 0xD8, 0x00, 0x01, 0x02]));
        Assert.Null(AudioFolders.StoreCover(db, []));
        Assert.Null(AudioFolders.StoreCover(db, System.Text.Encoding.UTF8.GetBytes("kein Bild")));
    }

    [Fact]
    public void Gehoert_ab_halber_Dauer_hoechstens_vier_Minuten()
    {
        Assert.True(AudioFolders.Counts(100_000, 200));
        Assert.False(AudioFolders.Counts(99_000, 200));
        Assert.True(AudioFolders.Counts(240_000, 3600));
        Assert.False(AudioFolders.Counts(200_000, 3600));
        Assert.True(AudioFolders.Counts(30_000, null));
        Assert.False(AudioFolders.Counts(29_000, null));
    }

    [Fact]
    public async Task DriverStream_liest_beliebige_Stellen()
    {
        var root = Directory.CreateTempSubdirectory("dmaudiostream").FullName;
        try
        {
            var data = new byte[700_000];
            new Random(3).NextBytes(data);
            File.WriteAllBytes(Path.Combine(root, "a.bin"), data);
            using var driver = new FolderDriver(root);
            await using var s = new DriverStream(driver, "a.bin", data.Length, block: 100_000);
            var buf = new byte[50];
            s.Seek(-50, SeekOrigin.End);
            s.ReadExactly(buf);
            Assert.Equal(data[^50..], buf);
            s.Position = 99_990;
            s.ReadExactly(buf);
            Assert.Equal(data[99_990..100_040], buf);
            s.Position = 0;
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            Assert.Equal(data, ms.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
}
