using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Zeitleiste der Galerie: Monate mit Anzahl, Fotos je Monat, Stapel, Filter, Werte der Filterleiste, Atlas, ETag.</summary>
[Collection("server")]
public class GalleryTimelineTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryTimelineTests(ChatFixture fx) { _fx = fx; GalleryTimeline.Grace = TimeSpan.Zero; GalleryList.Grace = TimeSpan.Zero; }

    private static byte[] Jpeg(SKColor color)
    {
        using var bmp = new SKBitmap(40, 30);
        using (var c = new SKCanvas(bmp)) c.Clear(color);
        return bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static async Task<JsonElement> Get(HttpClient c, string url) => await c.GetFromJsonAsync<JsonElement>(url);
    private static List<string> Ids(JsonElement bucket) => bucket.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("i").GetString()!).ToList();

    [Fact]
    public async Task Monate_Fotos_Stapel_Filter_und_Stand()
    {
        var services = _fx.Factory.Services;
        var config = services.GetRequiredService<ServerConfig>();
        var space = _fx.Space("anna");
        var db = space.Db;
        var dir = "Zeit-" + Guid.NewGuid().ToString("N")[..6];
        var root = Directory.CreateTempSubdirectory("dmtl").FullName;
        var before = config.Current.ExternalFolders;
        try
        {
            // Eigene Ablage: ein Foto mit Aufnahmedaten (6.5.2024, Canon), eins ohne (Monat der Änderung = jetzt), JPG+RAW als Stapel,
            // eins in einem ausgeschlossenen Ordner
            FileStore.EnsureDir(db, dir);
            FileStore.Put(db, dir + "/canon.jpg", FileMetaTests.ExifJpeg(), "image/jpeg");
            FileStore.Put(db, dir + "/neu.jpg", Jpeg(SKColors.Coral), "image/jpeg");
            FileStore.Put(db, dir + "/paar.jpg", Jpeg(SKColors.SeaGreen), "image/jpeg");
            FileStore.Put(db, dir + "/paar.cr2", [.. new byte[300], .. Jpeg(SKColors.SeaGreen)], "image/x-canon-cr2");
            FileStore.EnsureDir(db, dir + "/Cover");
            FileStore.Put(db, dir + "/Cover/album.jpg", Jpeg(SKColors.Gold), "image/jpeg");
            FileStore.Put(db, dir + "/notiz.txt", "kein Foto"u8.ToArray(), "text/plain");
            FileMeta.Compute(space, CancellationToken.None);
            var c = _fx.As("anna");
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/files/photo-excludes", new { folders = new[] { dir + "/Cover" } })).StatusCode);

            // Galerie-Ordner: ein Foto vom Januar 2023 (ohne Aufnahmedaten: Änderungszeit)
            File.WriteAllBytes(Path.Combine(root, "alt.jpg"), Jpeg(SKColors.SteelBlue));
            File.SetLastWriteTimeUtc(Path.Combine(root, "alt.jpg"), new DateTime(2023, 1, 15, 12, 0, 0, DateTimeKind.Utc));
            config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = "zeitl01", Name = "Archiv", Path = root }]);
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount = "zeitl01", path = "", watch = true })).StatusCode);
            await new GalleryScanJob(services.GetRequiredService<Spaces>(), config, NullLogger<GalleryScanJob>.Instance).RunOnceAsync(CancellationToken.None);

            // Monate, neueste zuerst; der Stapel zählt als eins, der ausgeschlossene Ordner fehlt
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone).ToString("yyyy-MM");
            var b = await Get(c, "/api/gallery/timeline/buckets");
            var buckets = b.GetProperty("buckets").EnumerateArray().Select(x => (k: x.GetProperty("k").GetString()!, n: x.GetProperty("n").GetInt32())).ToList();
            Assert.Equal(now, buckets[0].k);
            Assert.Contains(("2024-05", 1), buckets);
            Assert.Contains(("2023-01", 1), buckets);
            Assert.True(buckets.FindIndex(x => x.k == "2024-05") < buckets.FindIndex(x => x.k == "2023-01"));

            // Ein Monat: genau seine Fotos, mit Bildangaben
            var mai = await Get(c, "/api/gallery/timeline/bucket?k=2024-05");
            var canon = mai.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("i").GetString() == dir + "/canon.jpg");
            Assert.Equal("Canon EOS R5", canon.GetProperty("c").GetString());
            Assert.Equal(2.8, canon.GetProperty("a").GetDouble());
            Assert.True(canon.GetProperty("f").GetInt64() > 0);
            var jan = await Get(c, "/api/gallery/timeline/bucket?k=2023-01");
            Assert.Equal(["ext:"], Ids(jan).Select(i => i[..4]).ToList());

            // Dieser Monat: neu.jpg und der Stapel (paar.jpg mit paar.cr2), nicht album.jpg; ohne Stapeln beide einzeln
            var cur = await Get(c, "/api/gallery/timeline/bucket?k=" + now);
            var mine = Ids(cur).Where(i => i.StartsWith(dir + "/")).ToList();
            Assert.Equal([dir + "/neu.jpg", dir + "/paar.jpg"], mine.Order());
            var paar = cur.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("i").GetString() == dir + "/paar.jpg");
            Assert.Equal(dir + "/paar.cr2", paar.GetProperty("st")[0].GetProperty("i").GetString());
            var flat = await Get(c, "/api/gallery/timeline/bucket?stack=false&k=" + now);
            Assert.Contains(dir + "/paar.cr2", Ids(flat));

            // Nach Tag: Abschnitt je Tag (Name vom Server), Fotos ohne Tag unter „Ohne Tag“ zuletzt; eine neue Zuordnung gilt sofort
            Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/files/labels", new { paths = new[] { dir + "/neu.jpg" }, addTags = new[] { "Strand" } })).StatusCode);
            var byTag = (await Get(c, "/api/gallery/timeline/buckets?group=tag")).GetProperty("buckets").EnumerateArray().ToList();
            Assert.Equal("Strand", byTag[0].GetProperty("l").GetString());
            Assert.Equal("Ohne Tag", byTag[^1].GetProperty("l").GetString());
            Assert.Equal([dir + "/neu.jpg"], Ids(await Get(c, "/api/gallery/timeline/bucket?group=tag&k=" + Uri.EscapeDataString(byTag[0].GetProperty("k").GetString()!))));
            // Die Suche findet Fotos auch über ihren Tag
            var byLabel = (await Get(c, "/api/gallery/timeline/search?q=strand")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("i").GetString()).ToList();
            Assert.Contains(dir + "/neu.jpg", byLabel);
            var files = (await Get(c, "/api/files/search?photos=true&type=media&q=strand")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()).ToList();
            Assert.Contains(dir + "/neu.jpg", files);
            await c.PostAsJsonAsync("/api/files/labels", new { paths = new[] { dir + "/neu.jpg" }, removeTags = new[] { "Strand" } });
            Assert.DoesNotContain((await Get(c, "/api/gallery/timeline/buckets?group=tag")).GetProperty("buckets").EnumerateArray(), x => x.GetProperty("l").GetString() == "Strand");

            // Unverändert: 304; ein neues Foto: anderer Stand, ein Foto mehr
            var first = await c.GetAsync("/api/gallery/timeline/buckets");
            using (var again = new HttpRequestMessage(HttpMethod.Get, "/api/gallery/timeline/buckets"))
            {
                again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
                Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(again)).StatusCode);
            }
            using (var same = new HttpRequestMessage(HttpMethod.Get, "/api/gallery/timeline/bucket?k=2024-05"))
            {
                same.Headers.IfNoneMatch.Add((await c.GetAsync("/api/gallery/timeline/bucket?k=2024-05")).Headers.ETag!);
                Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(same)).StatusCode);
            }
            FileStore.Put(db, dir + "/noch.jpg", Jpeg(SKColors.Plum), "image/jpeg");
            using (var changed = new HttpRequestMessage(HttpMethod.Get, "/api/gallery/timeline/buckets"))
            {
                changed.Headers.IfNoneMatch.Add(first.Headers.ETag!);
                var res = await c.SendAsync(changed);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                var nb = await res.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(buckets[0].n + 1, nb.GetProperty("buckets")[0].GetProperty("n").GetInt32());
            }

            // Filterleiste auf dem Server: nur die Canon - und die Werte mit Anzahl
            var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new { cols = new[] { "camera" }, sel = new[] { new[] { "Canon EOS R5" } } }));
            var only = await Get(c, "/api/gallery/timeline/buckets?filter=" + filter);
            Assert.Equal(["2024-05"], only.GetProperty("buckets").EnumerateArray().Select(x => x.GetProperty("k").GetString()).ToList());
            var facets = await Get(c, "/api/gallery/timeline/facets?cols=year,camera&filter=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { cols = new[] { "year", "camera" }, sel = new[] { new[] { "2024" }, Array.Empty<string>() } })));
            var years = facets.GetProperty("cols")[0].GetProperty("values").EnumerateArray().ToDictionary(v => v[0].GetString()!, v => v[1].GetInt32());
            Assert.Equal(1, years["2024"]);
            Assert.Equal(1, years["2023"]);
            // Spalte 2 zählt nur, was Spalte 1 (2024) durchlässt
            Assert.Equal(1, facets.GetProperty("cols")[1].GetProperty("total").GetInt32());
            Assert.Equal("Canon EOS R5", facets.GetProperty("cols")[1].GetProperty("values")[0][0].GetString());

            // Nach Name: Seiten statt Monate
            var byName = await Get(c, "/api/gallery/timeline/buckets?sort=name&desc=false");
            Assert.Equal("p0", byName.GetProperty("buckets")[0].GetProperty("k").GetString());

            // Suche: eigene Fotos und Galerie-Ordner, ohne Groß/Klein
            var found = await Get(c, "/api/gallery/timeline/search?q=ALT");
            Assert.Contains(Ids(found), i => i.StartsWith("ext:") && i.EndsWith("/alt.jpg"));
            Assert.Contains(dir + "/canon.jpg", Ids(await Get(c, "/api/gallery/timeline/search?q=canon")));
            Assert.Empty(Ids(await Get(c, "/api/gallery/timeline/search?q=gibtsnicht")));

            // Galerie-Ordner nicht mehr sichtbar (externer Ordner entzogen): seine Fotos fehlen
            config.Update(s => s.ExternalFolders = []);
            var gone = await Get(c, "/api/gallery/timeline/buckets");
            Assert.DoesNotContain(gone.GetProperty("buckets").EnumerateArray(), x => x.GetProperty("k").GetString() == "2023-01");
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            await _fx.As("anna").PutAsJsonAsync("/api/files/photo-excludes", new { folders = Array.Empty<string>() });
            Directory.Delete(root, recursive: true);
        }
    }
}
