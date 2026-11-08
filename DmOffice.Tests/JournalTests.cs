using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Dav;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Tagebuch (Version 2): Tagebücher, Einträge, Suche, „Heute vor …“, Fotos kleingerechnet mit Datum und Ort aus dem Bild.</summary>
[Collection("server")]
public class JournalTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public JournalTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<long> Journal(string login) =>
        (await Json(await _fx.As(login).GetAsync("/api/journal/journals"))).EnumerateArray().First().GetProperty("id").GetInt64();

    private async Task<JsonElement> Save(string login, object body) => await Json(await _fx.As(login).PostAsJsonAsync("/api/journal/entries", body));

    [Fact]
    public async Task Eintrag_schreiben_lesen_suchen()
    {
        // Ohne Tagebuch gibt es gleich eins
        var list = await Json(await _fx.As("anna").GetAsync("/api/journal/journals"));
        Assert.Equal("Mein Tagebuch", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());
        var j = await Journal("anna");

        var e = await Save("anna", new
        {
            journal = j, date = "2026-07-14T16:30:00Z", title = "Am See",
            html = "<p>Baden <b>war</b> schön.<script>alert(1)</script></p><ul data-check><li data-done=\"1\">Sonnencreme</li><li>Handtuch</li></ul>",
            mood = 5, tags = new[] { "#Urlaub", "Familie", "urlaub" }, place = new { name = "Wörthersee", lat = 46.62, lon = 14.15 },
            weather = "28 / 17 °C", @private = true,
        });
        Assert.Equal("Am See", e.GetProperty("title").GetString());
        Assert.DoesNotContain("script", e.GetProperty("html").GetString());
        Assert.Contains("<b>war</b>", e.GetProperty("html").GetString());
        Assert.Equal("Baden war schön.\n☑ Sonnencreme\n☐ Handtuch", e.GetProperty("text").GetString());
        Assert.Equal(["Urlaub", "Familie"], e.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(5, e.GetProperty("mood").GetInt32());
        Assert.True(e.GetProperty("private").GetBoolean());
        Assert.Equal("28 / 17 °C", e.GetProperty("weather").GetString());
        Assert.Equal(46.62, e.GetProperty("place").GetProperty("lat").GetDouble());

        // Ändern mit altem Stand: abgelehnt
        var uri = e.GetProperty("uri").GetString();
        var etag = e.GetProperty("etag").GetString();
        await Save("anna", new { journal = j, uri, etag, date = "2026-07-14T16:30:00Z", title = "Am Wörthersee", html = "<p>Baden war schön.</p>", tags = new[] { "Urlaub" } });
        Assert.Equal(HttpStatusCode.Conflict, (await _fx.As("anna").PostAsJsonAsync("/api/journal/entries", new { journal = j, uri, etag, date = "2026-07-14T16:30:00Z", html = "x" })).StatusCode);

        await Save("anna", new { journal = j, date = "2025-07-14T08:00:00Z", html = "<p>Ein Jahr vorher: Regen.</p>", tags = new[] { "Wetter" }, mood = 2 });
        await Save("anna", new { journal = j, date = "2026-07-07T08:00:00Z", html = "<p>Eine Woche vorher.</p>" });

        // Zeitleiste neueste zuerst, Suche, Schlagwort, Stimmung, Zeitraum
        var all = (await Json(await _fx.As("anna").GetAsync("/api/journal/entries"))).EnumerateArray().ToList();
        Assert.Equal(["Am Wörthersee", null, null], all.Select(x => x.GetProperty("title").GetString()));
        Assert.Single((await Json(await _fx.As("anna").GetAsync("/api/journal/entries?q=regen"))).EnumerateArray());
        Assert.Single((await Json(await _fx.As("anna").GetAsync("/api/journal/entries?tag=urlaub"))).EnumerateArray());
        Assert.Single((await Json(await _fx.As("anna").GetAsync("/api/journal/entries?mood=2"))).EnumerateArray());
        Assert.Equal(2, (await Json(await _fx.As("anna").GetAsync("/api/journal/entries?from=2026-01-01T00:00:00Z"))).GetArrayLength());
        Assert.Single((await Json(await _fx.As("anna").GetAsync("/api/journal/entries?before=2026-07-10T00:00:00Z&limit=1"))).EnumerateArray());

        // Heute vor …
        var otd = (await Json(await _fx.As("anna").GetAsync("/api/journal/onthisday?date=2026-07-14"))).EnumerateArray().ToList();
        Assert.Equal(["vor einer Woche", "vor einem Jahr"], otd.Select(x => x.GetProperty("label").GetString()));

        // Schlagwörter mit Anzahl
        var tags = (await Json(await _fx.As("anna").GetAsync("/api/journal/tags"))).EnumerateArray().Select(x => x.GetProperty("tag").GetString()).ToList();
        Assert.Contains("Urlaub", tags);

        // Andere sehen nichts davon
        Assert.Empty((await Json(await _fx.As("ben").GetAsync("/api/journal/entries"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync($"/api/journal/entries/{j}/{uri}")).StatusCode);
    }

    [Fact]
    public async Task Tagebuecher_anlegen_umbenennen_loeschen()
    {
        var first = await Journal("cleo");
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("cleo").DeleteAsync($"/api/journal/journals/{first}")).StatusCode);
        var travel = (await Json(await _fx.As("cleo").PostAsJsonAsync("/api/journal/journals", new { name = "Reisen", color = "#E08A00" }))).GetProperty("id").GetInt64();
        (await _fx.As("cleo").PutAsJsonAsync($"/api/journal/journals/{travel}", new { name = "Reisetagebuch", color = "#E08A00" })).EnsureSuccessStatusCode();
        var names = (await Json(await _fx.As("cleo").GetAsync("/api/journal/journals"))).EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Mein Tagebuch", "Reisetagebuch"], names);
        // Tagebücher sind keine Kalender - sie tauchen in der Kalenderliste nicht als Terminkalender auf
        (await _fx.As("cleo").DeleteAsync($"/api/journal/journals/{travel}")).EnsureSuccessStatusCode();
        Assert.Single((await Json(await _fx.As("cleo").GetAsync("/api/journal/journals"))).EnumerateArray());
    }

    /// <summary>Hochladen in Stücken wie Web und App, dann ins Tagebuch.</summary>
    private async Task<JsonElement> Attach(string login, string name, byte[] data, string mime)
    {
        var c = _fx.As(login);
        var start = await Json(await c.PostAsJsonAsync("/api/files/uploads", new { name, size = data.Length, mime }));
        var id = start.GetProperty("id").GetString();
        await Json(await c.PutAsync($"/api/files/uploads/{id}?offset=0", new ByteArrayContent(data)));
        return await Json(await c.PostAsync($"/api/journal/uploads/{id}/attach", null));
    }

    [Fact]
    public async Task Foto_kleingerechnet_mit_Datum_und_Ort_aus_dem_Bild()
    {
        var j = await Journal("dora");
        var photo = WithExif(Jpeg(4000, 3000), "2025:07:14 18:30:00", (48, 12, 30), (16, 22, 0));
        var r = await Attach("dora", "DSCF0277.JPG", photo, "image/jpeg");
        var media = r.GetProperty("media");
        Assert.Equal("sys-dmoffice-journal/2025/07/DSCF0277.jpg", media.GetProperty("path").GetString());
        Assert.Equal(2048, media.GetProperty("width").GetInt32());
        Assert.Equal(1536, media.GetProperty("height").GetInt32());
        // Ortszeit Berlin (Sommerzeit) → UTC
        Assert.Equal(new DateTime(2025, 7, 14, 16, 30, 0, DateTimeKind.Utc), media.GetProperty("taken").GetDateTime().ToUniversalTime());
        Assert.Equal(48.208333, r.GetProperty("lat").GetDouble(), 4);
        Assert.Equal(16.366667, r.GetProperty("lon").GetDouble(), 4);
        var stored = FileStore.Get(_fx.Space("dora").Db, "sys-dmoffice-journal/2025/07/DSCF0277.jpg")!;
        Assert.True(stored.Size < photo.Length);

        // Kleiner eingestellt: 1024
        await Json(await _fx.As("dora").PutAsJsonAsync("/api/journal/settings", new { imageSize = 1024 }));
        var small = (await Attach("dora", "zweites.png", Png(3000, 1000), "image/png")).GetProperty("media");
        Assert.Equal(1024, small.GetProperty("width").GetInt32());
        Assert.Equal("image/jpeg", small.GetProperty("mime").GetString());
        Assert.EndsWith("zweites.jpg", small.GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("dora").PutAsJsonAsync("/api/journal/settings", new { imageSize = 777 })).StatusCode);

        // Geteilte Bilder → Eintrag am Aufnahmetag mit Ort; ein zweites Bild desselben Tages kommt dazu
        var made = await Json(await _fx.As("dora").PostAsJsonAsync("/api/journal/from-media", new
        {
            journal = j, media = new[] { JsonSerializer.Deserialize<object>(media.GetRawText()) },
            places = new[] { new { path = media.GetProperty("path").GetString(), lat = 48.208333, lon = 16.366667 } },
        }));
        var entry = Assert.Single(made.EnumerateArray());
        Assert.Equal(new DateTime(2025, 7, 14, 16, 30, 0, DateTimeKind.Utc), entry.GetProperty("date").GetDateTime().ToUniversalTime());
        Assert.Equal(48.208333, entry.GetProperty("place").GetProperty("lat").GetDouble(), 4);
        var second = WithExif(Jpeg(800, 600), "2025:07:14 20:00:00", null, null);
        var m2 = (await Attach("dora", "abends.jpg", second, "image/jpeg")).GetProperty("media");
        made = await Json(await _fx.As("dora").PostAsJsonAsync("/api/journal/from-media", new { journal = j, media = new[] { JsonSerializer.Deserialize<object>(m2.GetRawText()) } }));
        Assert.Equal(2, Assert.Single(made.EnumerateArray()).GetProperty("media").GetArrayLength());
        Assert.Single((await Json(await _fx.As("dora").GetAsync("/api/journal/entries"))).EnumerateArray());
        Assert.Equal(2, (await Json(await _fx.As("dora").GetAsync("/api/journal/media"))).GetArrayLength());
    }

    [Fact]
    public async Task Export_als_Zip_und_wieder_einlesen()
    {
        var c = _fx.As("dora");
        var a = (await Json(await c.PostAsJsonAsync("/api/journal/journals", new { name = "Export", color = "#7B1DD3" }))).GetProperty("id").GetInt64();
        var b = (await Json(await c.PostAsJsonAsync("/api/journal/journals", new { name = "Wieder", color = "#7B1DD3" }))).GetProperty("id").GetInt64();
        var photo = (await Attach("dora", "Strand.jpg", Jpeg(800, 600), "image/jpeg")).GetProperty("media");
        await Save("dora", new { journal = a, date = "2026-03-05T08:30:00Z", title = "Am Meer", mood = 4, tags = new[] { "Urlaub" }, weather = "sonnig, 18 °C",
            place = new { name = "Cuxhaven", lat = 53.86, lon = 8.69 },
            html = "<h2>Morgens</h2><p>Sehr <b>fett</b> und <i>schräg</i>, <a href=\"https://example.org\">Link</a></p><ul data-check><li data-done=\"1\">Packen</li><li>Baden</li></ul>",
            media = new[] { photo } });
        await Save("dora", new { journal = a, date = "2026-03-05T17:00:00Z", html = "<p>Abends</p>" });
        await Save("dora", new { journal = a, date = "2026-04-01T10:00:00Z", html = "<p>April</p>", @private = true });

        var r = await c.GetAsync($"/api/journal/export?journal={a}");
        Assert.True(r.IsSuccessStatusCode);
        Assert.Equal("application/zip", r.Content.Headers.ContentType?.MediaType);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes)))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            // Zwei am selben Tag: mit Uhrzeit (Ortszeit), einer allein: nur das Datum
            Assert.Contains("2026/03/2026-03-05_0930.md", names);
            Assert.Contains("2026/03/2026-03-05_1800.md", names);
            Assert.Contains("2026/04/2026-04-01.md", names);
            Assert.Contains("tagebuch.json", names);
            var media = Assert.Single(names, n => n.StartsWith("2026/03/media/"));
            string Read(string n) { using var sr = new StreamReader(zip.GetEntry(n)!.Open()); return sr.ReadToEnd(); }
            var md = Read("2026/03/2026-03-05_0930.md");
            Assert.StartsWith("---\ndate: 2026-03-05\ntime: \"09:30\"\ntitle: \"Am Meer\"\nplace: \"Cuxhaven\"\nlocation: [53.86, 8.69]\nweather: \"sonnig, 18 °C\"\nmood: 4\ntags: [\"Urlaub\"]\n---", md);
            Assert.Contains("## Morgens", md);
            Assert.Contains("Sehr **fett** und *schräg*, [Link](<https://example.org>)", md);
            Assert.Contains("- [x] Packen\n- [ ] Baden", md);
            Assert.Contains("![Strand.jpg](<" + media["2026/03/".Length..] + ">)", md);
            Assert.Contains("private: true", Read("2026/04/2026-04-01.md"));
        }

        // Zeitraum: nur der April
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await c.GetByteArrayAsync($"/api/journal/export?journal={a}&from=2026-04-01&to=2026-04-30"))))
            Assert.Equal(["2026/04/2026-04-01.md", "tagebuch.json"], zip.Entries.Select(e => e.FullName).Order().ToList());

        // Wieder einlesen in ein anderes Tagebuch, ein zweites Mal ohne Doppelte; ins Ursprungs-Tagebuch: alles schon da
        var db = _fx.Space("dora").Db;
        FileStore.EnsureDir(db, "Import");
        FileStore.Put(db, "Import/Export.zip", bytes, "application/zip");
        async Task<JsonElement> Run(long journal)
        {
            await Json(await c.PostAsJsonAsync("/api/journal/import/diarium", new { path = "Import/Export.zip", journal }));
            for (var i = 0; i < 100; i++)
            {
                var st = await Json(await c.GetAsync("/api/journal/import"));
                if (!st.GetProperty("running").GetBoolean()) return st;
                await Task.Delay(100);
            }
            throw new TimeoutException("Import läuft zu lange.");
        }
        var first = await Run(b);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("error").ValueKind);
        Assert.Equal(3, first.GetProperty("created").GetInt32());
        Assert.Equal(1, first.GetProperty("media").GetInt32());
        var back = (await Json(await c.GetAsync($"/api/journal/entries?journal={b}&full=true"))).EnumerateArray().Single(e => e.GetProperty("title").GetString() == "Am Meer");
        Assert.Equal(4, back.GetProperty("mood").GetInt32());
        Assert.Equal("Cuxhaven", back.GetProperty("place").GetProperty("name").GetString());
        Assert.Single(back.GetProperty("media").EnumerateArray());
        Assert.Contains("data-check", back.GetProperty("html").GetString());
        Assert.Equal(3, (await Run(b)).GetProperty("skipped").GetInt32());
        Assert.Equal(3, (await Run(a)).GetProperty("skipped").GetInt32());
        // Aufräumen: die anderen Tests zählen alle Einträge von dora
        foreach (var id in new[] { a, b }) (await c.DeleteAsync($"/api/journal/journals/{id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Import_aus_Diarium_ohne_Doppelte()
    {
        // Aufbau wie ein echter Diarium-Export: JSON mit allen Einträgen, Bilder je Eintrag in media/<Datum_Uhrzeit>/
        var json = """
            [{"date":"2026-09-08T18:09:07.9531272","heading":"Wandern","html":"<p>Hoch hinaus</p>","tags":["Berge"],"people":["Anna"],"tracker":[],"location":[47.576208,10.6730102],"weather":"21 / 12 °C"},
             {"date":"2026-09-09T15:31:33.2192534","heading":"","html":"<p>Ohne Bild</p>","tags":[],"people":[],"tracker":[]}]
            """;
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("Diarium_2026-09-07_2026-10-22.json").Open())) w.Write(json);
            using (var s = zip.CreateEntry("media/2026-09-08_180907953/20260908_193453_DSCF0277.jpg").Open())
                s.Write(WithExif(Jpeg(3000, 2000), "2026:09:08 19:34:53", (47, 34, 34), (10, 40, 22)));
        }
        var db = _fx.Space("cleo").Db;
        FileStore.EnsureDir(db, "Import");
        FileStore.Put(db, "Import/Diarium.zip", ms.ToArray(), "application/zip");
        var j = await Journal("cleo");

        async Task<JsonElement> Run()
        {
            await Json(await _fx.As("cleo").PostAsJsonAsync("/api/journal/import/diarium", new { path = "Import/Diarium.zip", journal = j }));
            for (var i = 0; i < 100; i++)
            {
                var s = await Json(await _fx.As("cleo").GetAsync("/api/journal/import"));
                if (!s.GetProperty("running").GetBoolean()) return s;
                await Task.Delay(100);
            }
            throw new TimeoutException("Import läuft zu lange.");
        }

        var first = await Run();
        Assert.Equal(JsonValueKind.Null, first.GetProperty("error").ValueKind);
        Assert.Equal(2, first.GetProperty("created").GetInt32());
        Assert.Equal(1, first.GetProperty("media").GetInt32());
        var entries = (await Json(await _fx.As("cleo").GetAsync("/api/journal/entries?q=hinaus"))).EnumerateArray().ToList();
        var e = Assert.Single(entries);
        Assert.Equal("Wandern", e.GetProperty("title").GetString());
        // Diarium speichert Ortszeit (Berlin, Sommerzeit)
        Assert.Equal(new DateTime(2026, 9, 8, 16, 9, 7, DateTimeKind.Utc), e.GetProperty("date").GetDateTime().ToUniversalTime().AddTicks(-(e.GetProperty("date").GetDateTime().Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal(["Berge", "Anna"], e.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("21 / 12 °C", e.GetProperty("weather").GetString());
        Assert.Equal(47.576208, e.GetProperty("place").GetProperty("lat").GetDouble(), 5);
        var photo = Assert.Single(e.GetProperty("media").EnumerateArray());
        Assert.Equal(2048, photo.GetProperty("width").GetInt32());
        Assert.StartsWith("sys-dmoffice-journal/2026/09/", photo.GetProperty("path").GetString());

        // Noch einmal: nichts doppelt
        var again = await Run();
        Assert.Equal(0, again.GetProperty("created").GetInt32());
        Assert.Equal(2, again.GetProperty("skipped").GetInt32());

        // Keine ZIP: verständlich abgelehnt
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("cleo").PostAsJsonAsync("/api/journal/import/diarium", new { path = "Import", journal = j })).StatusCode);
    }

    [Fact]
    public async Task Import_aus_Journey_ohne_Doppelte()
    {
        // Aufbau wie ein Journey-Export: je Eintrag eine JSON-Datei, die Fotos daneben; ohne Ort steht die größte Zahl drin
        var withPhoto = """
            {"text":"<p>Journeytest am See</p>","date_modified":1757000000000,"date_journal":1756743865000,"id":"1756804146848-3fcd967b0f1459f4","preview_text":"Am See",
             "address":"Seeufer 1, Musterstadt","music_artist":"","music_title":"","lat":47.5,"lon":10.6,"mood":4,"label":"","folder":"","sentiment":0,
             "timezone":"Europe/Berlin","favourite":false,"type":"html","linked_account_id":"drive-x",
             "weather":{"id":0,"degree_c":18.4,"description":"Few clouds","icon":"02d","place":"Musterstadt"},
             "photos":["1756804146848-3fcd967b0f1459f4-3fe547ba8fe22412.jpg"],"tags":["Urlaub"]}
            """;
        var noPlace = """
            {"text":"<p>Journeyheim</p>","date_modified":1757000000000,"date_journal":1756833162000,"id":"1756891455115-0a1b2c3d4e5f6a7b","preview_text":"Daheim",
             "address":"","lat":1.7976931348623157E308,"lon":1.7976931348623157E308,"mood":0,"label":"","folder":"","sentiment":0,"timezone":"Europe/Berlin",
             "favourite":false,"type":"html","linked_account_id":"drive-x","weather":{"id":-1,"degree_c":1.7976931348623157E308,"description":"","icon":"","place":""},
             "photos":[],"tags":[]}
            """;
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("1756804146848-3fcd967b0f1459f4.json").Open())) w.Write(withPhoto);
            using (var w = new StreamWriter(zip.CreateEntry("1756891455115-0a1b2c3d4e5f6a7b.json").Open())) w.Write(noPlace);
            using (var s = zip.CreateEntry("1756804146848-3fcd967b0f1459f4-3fe547ba8fe22412.jpg").Open()) s.Write(Jpeg(1200, 800));
        }
        var db = _fx.Space("cleo").Db;
        FileStore.EnsureDir(db, "Import");
        FileStore.Put(db, "Import/journey.zip", ms.ToArray(), "application/zip");
        var j = await Journal("cleo");

        async Task<JsonElement> Run()
        {
            await Json(await _fx.As("cleo").PostAsJsonAsync("/api/journal/import/diarium", new { path = "Import/journey.zip", journal = j }));
            for (var i = 0; i < 100; i++)
            {
                var s = await Json(await _fx.As("cleo").GetAsync("/api/journal/import"));
                if (!s.GetProperty("running").GetBoolean()) return s;
                await Task.Delay(100);
            }
            throw new TimeoutException("Import läuft zu lange.");
        }

        var first = await Run();
        Assert.Equal(JsonValueKind.Null, first.GetProperty("error").ValueKind);
        Assert.Equal(2, first.GetProperty("created").GetInt32());
        Assert.Equal(1, first.GetProperty("media").GetInt32());
        var e = Assert.Single((await Json(await _fx.As("cleo").GetAsync("/api/journal/entries?q=Journeytest"))).EnumerateArray());
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1756743865000).UtcDateTime, e.GetProperty("date").GetDateTime().ToUniversalTime());
        Assert.Equal(["Urlaub"], e.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(4, e.GetProperty("mood").GetInt32());
        Assert.Equal("heiter, 18 °C", e.GetProperty("weather").GetString());
        Assert.Equal("Seeufer 1, Musterstadt", e.GetProperty("place").GetProperty("name").GetString());
        Assert.Equal(47.5, e.GetProperty("place").GetProperty("lat").GetDouble(), 5);
        Assert.Single(e.GetProperty("media").EnumerateArray());

        // Ohne Ort und Wetter: nichts erfunden
        var home = Assert.Single((await Json(await _fx.As("cleo").GetAsync("/api/journal/entries?q=Journeyheim"))).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, home.GetProperty("place").ValueKind);
        Assert.Equal(JsonValueKind.Null, home.GetProperty("weather").ValueKind);

        var again = await Run();
        Assert.Equal(0, again.GetProperty("created").GetInt32());
        Assert.Equal(2, again.GetProperty("skipped").GetInt32());
    }

    private static byte[] Jpeg(int w, int h) => Encode(w, h, SKEncodedImageFormat.Jpeg);
    private static byte[] Png(int w, int h) => Encode(w, h, SKEncodedImageFormat.Png);

    private static byte[] Encode(int w, int h, SKEncodedImageFormat format)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) { c.Clear(new SKColor(0x30, 0x80, 0xC0)); c.DrawCircle(w / 2f, h / 2f, h / 3f, new SKPaint { Color = SKColors.Orange }); }
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(format, 92).ToArray();
    }

    /// <summary>EXIF (Aufnahmezeit, GPS) als APP1-Segment direkt hinter den JPEG-Anfang setzen - wie es eine Kamera schreibt.</summary>
    private static byte[] WithExif(byte[] jpeg, string taken, (int D, int M, int S)? lat, (int D, int M, int S)? lon)
    {
        var t = new List<byte>();
        void U16(int v) { t.Add((byte)v); t.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }
        void Entry(int tag, int type, int count, int value) { U16(tag); U16(type); U32(count); U32(value); }
        bool gps = lat != null && lon != null;
        t.AddRange("II"u8.ToArray()); U16(42); U32(8);
        // IFD0: Zeiger auf Exif- und GPS-Verzeichnis
        int ifd0Count = gps ? 2 : 1, exifAt = 8 + 2 + ifd0Count * 12 + 4, dateAt = exifAt + 2 + 12 + 4, gpsAt = dateAt + 20;
        U16(ifd0Count); Entry(0x8769, 4, 1, exifAt); if (gps) Entry(0x8825, 4, 1, gpsAt); U32(0);
        U16(1); Entry(0x9003, 2, 20, dateAt); U32(0);
        t.AddRange(Encoding.ASCII.GetBytes(taken)); t.Add(0);
        if (gps)
        {
            int latAt = gpsAt + 2 + 4 * 12 + 4, lonAt = latAt + 24;
            U16(4); Entry(1, 2, 2, 'N'); Entry(2, 5, 3, latAt); Entry(3, 2, 2, 'E'); Entry(4, 5, 3, lonAt); U32(0);
            foreach (var v in new[] { lat!.Value.D, lat.Value.M, lat.Value.S, lon!.Value.D, lon.Value.M, lon.Value.S }) { U32(v); U32(1); }
        }
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var len = 2 + 6 + t.Count;
        app1.Add((byte)(len >> 8)); app1.Add((byte)len);
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(t);
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }
}
