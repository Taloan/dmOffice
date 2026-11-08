using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.Extensions.DependencyInjection;
using DmOffice.Gallery;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Personen in der Galerie: Einschalten liest ein, ähnliche Gesichter bilden Gruppen, Benennen, Ablehnen, Ausschalten löscht.
/// Die Gesichtsdaten liegen im Tresor: ohne Tresor keine Personen, geschlossen wird eingelesen und beim Öffnen übernommen.</summary>
[Collection("server")]
public class GalleryFacesTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryFacesTests(ChatFixture fx)
    {
        _fx = fx; GalleryTimeline.Grace = TimeSpan.Zero; GalleryFaces.KickEvery = TimeSpan.Zero;
        Tresor.ArgonMemory = 1024; Tresor.ScryptCost = 1024; Tresor.ArgonIterations = 1;
    }

    /// <summary>Einlesen abwarten (bis nichts mehr läuft und so viele Fotos eingelesen sind).</summary>
    private static async Task<JsonElement> Scanned(HttpClient c, long n)
    {
        JsonElement st = default;
        for (var i = 0; i < 100; i++)
        {
            st = await c.GetFromJsonAsync<JsonElement>("/api/gallery/faces");
            if (!st.GetProperty("running").GetBoolean() && st.GetProperty("overview").ValueKind == JsonValueKind.Object
                && st.GetProperty("overview").GetProperty("scanned").GetInt64() == n) break;
            await Task.Delay(100);
        }
        return st;
    }

    private static byte[] Solid(SKColor color)
    {
        using var bmp = new SKBitmap(400, 300);
        using (var c = new SKCanvas(bmp)) c.Clear(color);
        return SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    /// <summary>Ersatz für die Engine: ein Gesicht je Bild, Fingerabdruck nach der Farbe (rot: Anna, blau: Bert).</summary>
    private static Task<(string Model, List<PhotoEngine.Face> Faces)?> Fake(byte[] jpeg, ILogger? log, CancellationToken ct)
    {
        using var bmp = SKBitmap.Decode(jpeg);
        var p = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        var emb = new float[128];
        emb[p.Red > p.Blue ? 0 : 1] = 1;
        return Task.FromResult<(string, List<PhotoEngine.Face>)?>(("test", [new PhotoEngine.Face(0.4, 0.3, 0.2, 0.3, 0.95, 120, emb)]));
    }

    [Fact]
    public async Task Personen_erkennen_benennen_und_ausschalten()
    {
        var (model, detect) = (GalleryFaces.Model, GalleryFaces.Detect);
        GalleryFaces.Model = _ => Task.FromResult<string?>("test");
        GalleryFaces.Detect = Fake;
        try
        {
            // Eigener Benutzer: nur diese Fotos in der Galerie
            var user = "faces" + Guid.NewGuid().ToString("N")[..6];
            var u = _fx.Factory.Services.GetRequiredService<UserStore>().Create(user, "Faces", "geheim-123456", isAdmin: false);
            _fx.Users[user] = (u.Id, AppPasswords.Create(_fx.Factory.Services.GetRequiredService<Spaces>().Get(u.Id).Db, "Test"));
            var space = _fx.Space(user);
            var c = _fx.As(user);
            FileStore.Put(space.Db, "anna1.jpg", Solid(SKColors.Red), "image/jpeg");
            FileStore.Put(space.Db, "anna2.jpg", Solid(new SKColor(230, 20, 20)), "image/jpeg");
            FileStore.Put(space.Db, "bert.jpg", Solid(SKColors.Blue), "image/jpeg");

            // Aus: keine Daten, Personen-Abfragen abgelehnt
            var st = await c.GetFromJsonAsync<JsonElement>("/api/gallery/faces");
            Assert.True(st.GetProperty("available").GetBoolean());
            Assert.False(st.GetProperty("enabled").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/gallery/faces/persons/1")).StatusCode);

            // Ohne Tresor keine Personen
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/gallery/faces", new { enabled = true })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" })).StatusCode);

            // Einschalten: liest im Hintergrund ein
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/gallery/faces", new { enabled = true })).StatusCode);
            JsonElement ov = default;
            for (var i = 0; i < 100; i++)
            {
                st = await c.GetFromJsonAsync<JsonElement>("/api/gallery/faces");
                ov = st.GetProperty("overview");
                if (!st.GetProperty("running").GetBoolean() && ov.GetProperty("scanned").GetInt64() == 3) break;
                await Task.Delay(100);
            }
            Assert.Equal(3, ov.GetProperty("scanned").GetInt64());
            // Eine Gruppe (Anna, zwei Fotos); Bert allein bildet keine
            var groups = ov.GetProperty("groups").EnumerateArray().ToList();
            Assert.Single(groups);
            Assert.Equal(2, groups[0].GetProperty("count").GetInt32());

            // Benennen
            var named = await c.PostAsJsonAsync("/api/gallery/faces/name", new { name = "Anna", group = groups[0].GetProperty("id").GetString() });
            Assert.Equal(HttpStatusCode.OK, named.StatusCode);
            var anna = (await named.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("person").GetInt64();
            var photos = await c.GetFromJsonAsync<JsonElement>($"/api/gallery/faces/persons/{anna}");
            Assert.Equal(new[] { "anna1.jpg", "anna2.jpg" }, photos.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("i").GetString()!).Order().ToArray());
            var face = photos.GetProperty("faces").GetProperty("anna2.jpg")[0].GetInt64();
            // Suche in der Galerie findet die Fotos einer Person auch über ihren Namen
            var found = await c.GetFromJsonAsync<JsonElement>("/api/gallery/timeline/search?q=anna");
            Assert.Equal(2, found.GetProperty("items").GetArrayLength());

            // Ausschnitt des Gesichts
            var crop = await c.GetAsync($"/api/gallery/faces/crop/{face}?size=64");
            Assert.Equal("image/webp", crop.Content.Headers.ContentType?.MediaType);
            using (var b = SKBitmap.Decode(await crop.Content.ReadAsByteArrayAsync())) Assert.Equal((64, 64), (b.Width, b.Height));

            // „Ist nicht Anna“: weg aus der Person, kein Vorschlag mehr
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/gallery/faces/persons/{anna}/reject", new { faces = new[] { face } })).StatusCode);
            photos = await c.GetFromJsonAsync<JsonElement>($"/api/gallery/faces/persons/{anna}");
            Assert.Single(photos.GetProperty("items").EnumerateArray());
            Assert.Empty(photos.GetProperty("suggestions").EnumerateArray());

            // Neues Foto von Anna: wird beim nächsten Einlesen gleich zugeordnet
            FileStore.Put(space.Db, "anna3.jpg", Solid(new SKColor(200, 30, 30)), "image/jpeg");
            for (var i = 0; i < 100; i++)
            {
                st = await c.GetFromJsonAsync<JsonElement>("/api/gallery/faces");
                if (!st.GetProperty("running").GetBoolean() && st.GetProperty("overview").GetProperty("scanned").GetInt64() == 4) break;
                await Task.Delay(100);
            }
            var persons = st.GetProperty("overview").GetProperty("persons").EnumerateArray().ToList();
            Assert.Equal(("Anna", 2), (persons[0].GetProperty("name").GetString(), persons[0].GetProperty("count").GetInt32()));

            // Umbenennen, dann Ausschalten: alles gelöscht
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/gallery/faces/persons/{anna}", new { name = "Anna B." })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/gallery/faces", new { enabled = false })).StatusCode);
            var tresor = Tresor.DbOf(space.User.Id)!;
            Assert.Equal(0L, tresor.Scalar("SELECT COUNT(*) FROM faces"));
            Assert.Equal(0L, tresor.Scalar("SELECT COUNT(*) FROM persons"));
            Assert.Equal(0L, space.Db.Scalar("SELECT COUNT(*) FROM face_items"));
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/gallery/faces/persons/{anna}")).StatusCode);
        }
        finally { (GalleryFaces.Model, GalleryFaces.Detect) = (model, detect); }
    }

    /// <summary>Gesicht mit Fingerabdruck aus Richtung (a, b) - normiert.</summary>
    private static void AddFace(TresorDb db, long id, string key, double a, double b)
    {
        var n = Math.Sqrt(a * a + b * b);
        var emb = new float[128];
        emb[0] = (float)(a / n); emb[1] = (float)(b / n);
        var bytes = new byte[emb.Length * 4];
        Buffer.BlockCopy(emb, 0, bytes, 0, bytes.Length);
        db.Exec("INSERT INTO faces(id, key, x, y, w, h, score, size, emb) VALUES($i, $k, 0.4, 0.3, 0.2, 0.3, 0.95, 120, $e)", ("$i", id), ("$k", key), ("$e", bytes));
        db.Exec("UPDATE faces_ver SET v = v + 1");
    }

    [Fact]
    public void Schnell_zuordnen_Vorschlag_ausblenden_und_rueckgaengig()
    {
        var dir = Directory.CreateTempSubdirectory("dmfaces");
        var db = new TresorDb(Path.Combine(dir.FullName, "t.tresor.db"), new byte[32], Path.Combine(dir.FullName, "blobs"));
        var visible = new Dictionary<string, GalleryTimeline.Entry>();
        void Photo(string key) => visible[key] = new GalleryTimeline.Entry(key, key + ".jpg", 1, "image/jpeg", 0, "e", 1, null, null);
        // Anna (benannt), eine Gruppe, die Anna ähnelt (unter der Schwelle für „sicher“), und Fremde im Hintergrund
        foreach (var k in new[] { "a1", "a2", "s1", "s2", "x1", "x2", "x3" }) Photo(k);
        AddFace(db, 1, "a1", 1, 0); AddFace(db, 2, "a2", 1, 0.05);
        GalleryFaces.Name(db, [1, 2], "Anna");
        AddFace(db, 3, "s1", 1, 2.2); AddFace(db, 4, "s2", 1, 2.3);       // ähnelt Anna etwas (≈ 0,4)
        AddFace(db, 5, "x1", -1, 0.1); AddFace(db, 6, "x2", -1, 0.12);    // Fremde

        JsonElement Ov() => JsonSerializer.SerializeToElement(GalleryFaces.Overview(db, 0, visible));
        var groups = Ov().GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(2, groups.Count);
        var similar = groups.Single(g => g.GetProperty("id").GetString() == "g3");
        Assert.Equal("Anna", similar.GetProperty("suggest").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, groups.Single(g => g.GetProperty("id").GetString() == "g5").GetProperty("suggest").ValueKind);

        // Vorschlag abgelehnt: kein Vorschlag mehr, Gruppe bleibt
        GalleryFaces.RejectGroup(db, "g3", similar.GetProperty("suggest").GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, Ov().GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("id").GetString() == "g3").GetProperty("suggest").ValueKind);

        // Fremde ausblenden: weg, auch ein neues Foto derselben Person taucht nicht wieder auf
        GalleryFaces.Ignore(db, "g5");
        AddFace(db, 7, "x3", -1, 0.11);
        var ov = Ov();
        Assert.Equal(["g3"], ov.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("id").GetString()));
        Assert.Equal(1, ov.GetProperty("ignored").GetInt32());

        // Rückgängig: wieder da (mit dem neuen Foto)
        GalleryFaces.Unignore(db, "g5");
        ov = Ov();
        Assert.Equal(0, ov.GetProperty("ignored").GetInt32());
        Assert.Equal(3, ov.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("id").GetString() == "g5").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Geschlossen_wird_eingelesen_und_beim_Oeffnen_uebernommen()
    {
        var (model, detect) = (GalleryFaces.Model, GalleryFaces.Detect);
        GalleryFaces.Model = _ => Task.FromResult<string?>("test");
        GalleryFaces.Detect = Fake;
        try
        {
            var user = "facet" + Guid.NewGuid().ToString("N")[..6];
            var u = _fx.Factory.Services.GetRequiredService<UserStore>().Create(user, "Faces", "geheim-123456", isAdmin: false);
            _fx.Users[user] = (u.Id, AppPasswords.Create(_fx.Factory.Services.GetRequiredService<Spaces>().Get(u.Id).Db, "Test"));
            var space = _fx.Space(user);
            var c = _fx.As(user);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/gallery/faces", new { enabled = true })).StatusCode);
            FileStore.Put(space.Db, "anna1.jpg", Solid(SKColors.Red), "image/jpeg");
            FileStore.Put(space.Db, "anna2.jpg", Solid(new SKColor(230, 20, 20)), "image/jpeg");
            var groups = (await Scanned(c, 2)).GetProperty("overview").GetProperty("groups").EnumerateArray().ToList();
            var anna = (await (await c.PostAsJsonAsync("/api/gallery/faces/name", new { name = "Berta", group = groups[0].GetProperty("id").GetString() }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("person").GetInt64();

            // Tresor zu: Übersicht leer, Daten 423, Namen werden in der Suche nicht gefunden
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/tresor/close", null)).StatusCode);
            var st = await c.GetFromJsonAsync<JsonElement>("/api/gallery/faces");
            Assert.Equal(JsonValueKind.Null, st.GetProperty("overview").ValueKind);
            Assert.False(st.GetProperty("tresor").GetProperty("open").GetBoolean());
            Assert.Equal(HttpStatusCode.Locked, (await c.GetAsync($"/api/gallery/faces/persons/{anna}")).StatusCode);
            Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/gallery/timeline/search?q=Berta")).GetProperty("items").EnumerateArray());

            // Neues Foto bei geschlossenem Tresor: wird eingelesen, wartet verschlüsselt
            FileStore.Put(space.Db, "anna3.jpg", Solid(new SKColor(200, 30, 30)), "image/jpeg");
            for (var i = 0; i < 100 && Convert.ToInt64(space.Db.Scalar("SELECT COUNT(*) FROM face_items")) < 3; i++)
            {
                await c.GetAsync("/api/gallery/faces");
                await Task.Delay(100);
            }
            Assert.True(Tresor.Pending(space.User.Id) >= 1);
            // Das DMS zählt nur seine eigenen wartenden Dateien, nicht die Gesichter
            Assert.True(Tresor.Pending(space.User.Id, "faces") >= 1);
            Assert.Equal(0, Tresor.Pending(space.User.Id, "dms"));
            // Beim Öffnen übernommen und gleich Anna zugeordnet
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" })).StatusCode);
            Assert.Equal(0, Tresor.Pending(space.User.Id));
            var photos = await c.GetFromJsonAsync<JsonElement>($"/api/gallery/faces/persons/{anna}");
            Assert.Equal(3, photos.GetProperty("items").GetArrayLength());
            Assert.NotEmpty((await c.GetFromJsonAsync<JsonElement>("/api/gallery/timeline/search?q=Berta")).GetProperty("items").EnumerateArray());
        }
        finally { (GalleryFaces.Model, GalleryFaces.Detect) = (model, detect); }
    }

    [Fact]
    public void Alte_Gesichtsdaten_ziehen_in_den_Tresor()
    {
        var dir = Directory.CreateTempSubdirectory("dmfacemove");
        var user = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "u");
        // Stand von vor dem Tresor: Tabellen in der Benutzerdatenbank
        user.Exec("CREATE TABLE faces (id INTEGER PRIMARY KEY, key TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL, w REAL NOT NULL, h REAL NOT NULL, score REAL NOT NULL, size INTEGER NOT NULL, emb BLOB NOT NULL, person INTEGER, rejected TEXT)");
        user.Exec("CREATE TABLE persons (id INTEGER PRIMARY KEY, name TEXT NOT NULL, cover INTEGER, hidden INTEGER NOT NULL DEFAULT 0)");
        user.Exec("CREATE TABLE faces_ver (id INTEGER PRIMARY KEY CHECK(id = 1), v INTEGER NOT NULL)");
        user.Exec("INSERT INTO persons(id, name) VALUES(7, 'Oma Erna')");
        user.Exec("INSERT INTO faces(id, key, x, y, w, h, score, size, emb, person) VALUES(3, 'f:1', 0.1, 0.1, 0.2, 0.2, 0.9, 120, x'00000000', 7)");
        var tresor = new TresorDb(Path.Combine(dir.FullName, "t.tresor.db"), new byte[32], Path.Combine(dir.FullName, "blobs"));
        GalleryFaces.MoveIntoTresor(user, tresor);
        Assert.Equal("Oma Erna", tresor.Scalar("SELECT name FROM persons WHERE id = 7"));
        Assert.Equal(7L, tresor.Scalar("SELECT person FROM faces WHERE id = 3"));
        // In der Benutzerdatenbank weg - auch die Tabellen
        Assert.Equal(0L, user.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('faces', 'persons', 'faces_ver')"));
        // Ein zweites Mal: nichts zu tun
        GalleryFaces.MoveIntoTresor(user, tresor);
    }
}
