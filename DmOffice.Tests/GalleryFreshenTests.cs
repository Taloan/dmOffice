using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Galerie-Ordner: Abgleich beim Ansehen - das gezeigte Foto und die sichtbaren Kacheln.</summary>
[Collection("server")]
public class GalleryFreshenTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public GalleryFreshenTests(ChatFixture fx) { _fx = fx; }

    private static byte[] Jpeg(int w = 60, int h = 40, int? rating = null)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.SteelBlue);
        var jpg = bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
        return rating == null ? jpg : PhotoMeta.WriteJpeg(jpg, new PhotoMeta.Fields(Rating: rating));
    }

    /// <summary>Galerie-Ordner mit eingelesener Liste (Stufe 0) in einem eigenen Server-Ordner.</summary>
    private async Task<(ServerConfig Config, UserUnderTest U, GallerySource Src, string Root, List<ExternalFolderDef> Before)> FolderAsync(string user, string mount, Action<string> fill)
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var before = config.Current.ExternalFolders;
        var root = Directory.CreateTempSubdirectory("dmseen").FullName;
        fill(root);
        config.Update(s => s.ExternalFolders = [new ExternalFolderDef { Id = mount, Name = mount, Path = root }]);
        var space = _fx.Space(user);
        var c = _fx.As(user);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/gallery/sources", new { mount, path = "" })).StatusCode);
        var src = GalleryFolders.Of(space.Db).Single(s => s.Mount == mount);
        await GalleryFolders.ScanAsync(space, config, src, CancellationToken.None);
        return (config, new UserUnderTest(space, c), src, root, before);
    }

    private sealed record UserUnderTest(UserSpace Space, HttpClient Client);

    private static async Task<JsonElement> Refresh(HttpClient c, string id)
    {
        var res = await c.PostAsJsonAsync("/api/gallery/external/refresh", new { id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> Visible(HttpClient c, IEnumerable<string> ids)
    {
        var res = await c.PostAsJsonAsync("/api/gallery/external/visible", new { ids });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Gezeigtes_Foto_geaenderte_Markierung_bei_gleicher_Groesse_und_Zeit()
    {
        var stamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var two = Jpeg(rating: 2);
        var four = Jpeg(rating: 4);
        Assert.Equal(two.Length, four.Length);
        var (config, u, src, root, before) = await FolderAsync("cleo", "seen01", r =>
        {
            File.WriteAllBytes(Path.Combine(r, "foto.jpg"), two);
            File.SetLastWriteTimeUtc(Path.Combine(r, "foto.jpg"), stamp);
        });
        try
        {
            await GalleryFolders.HeadsAsync(u.Space, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            var id = src.Id + "/foto.jpg";
            int? Rating() => GalleryFolders.List(u.Space, config).Single(e => e.Source == src.Id).Meta?.Rating;
            Assert.Equal(2, Rating());

            // Nichts geändert: nichts geschrieben, der Stand der Liste bleibt
            var tag = GalleryFolders.Version(u.Space, config);
            var same = await Refresh(u.Client, id);
            Assert.False(same.GetProperty("changed").GetBoolean());
            Assert.True(same.GetProperty("reachable").GetBoolean());
            Assert.Equal(tag, GalleryFolders.Version(u.Space, config));

            File.WriteAllBytes(Path.Combine(root, "foto.jpg"), four);
            File.SetLastWriteTimeUtc(Path.Combine(root, "foto.jpg"), stamp);
            var r = await Refresh(u.Client, id);
            Assert.True(r.GetProperty("changed").GetBoolean());
            Assert.False(r.GetProperty("fileChanged").GetBoolean());
            Assert.Equal(4, r.GetProperty("meta").GetProperty("rating").GetInt32());
            Assert.Equal("ext:" + id, r.GetProperty("key").GetString());
            Assert.Equal(4, Rating());
            // Das Vorschaubild kommt weiter aus dem gemeinsamen Speicher
            Assert.Equal(HttpStatusCode.OK, (await u.Client.GetAsync("/api/gallery/external/thumb?id=" + Uri.EscapeDataString(id))).StatusCode);

            // Datei selbst geändert: neuer Stand, neuer Etag, neue Angaben
            var etag = r.GetProperty("etag").GetString();
            File.WriteAllBytes(Path.Combine(root, "foto.jpg"), Jpeg(120, 80, rating: 1));
            var f = await Refresh(u.Client, id);
            Assert.True(f.GetProperty("fileChanged").GetBoolean());
            Assert.NotEqual(etag, f.GetProperty("etag").GetString());
            Assert.Equal(120, f.GetProperty("meta").GetProperty("width").GetInt32());
            Assert.Equal(1, Rating());
            await u.Client.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sidecar_Datei_geht_beim_gezeigten_Foto_weiter_vor()
    {
        const string Xmp = """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="5"/></rdf:RDF></x:xmpmeta>""";
        var (config, u, src, root, before) = await FolderAsync("cleo", "seen05", r =>
        {
            File.WriteAllBytes(Path.Combine(r, "foto.jpg"), Jpeg(rating: 2));
            File.WriteAllText(Path.Combine(r, "foto.jpg.xmp"), Xmp);
        });
        try
        {
            await GalleryFolders.HeadsAsync(u.Space, config, src, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
            var r = await Refresh(u.Client, src.Id + "/foto.jpg");
            Assert.Equal(5, r.GetProperty("meta").GetProperty("rating").GetInt32());

            // Sidecar-Datei weg: wieder der Wert aus dem Foto
            File.Delete(Path.Combine(root, "foto.jpg.xmp"));
            r = await Refresh(u.Client, src.Id + "/foto.jpg");
            Assert.True(r.GetProperty("changed").GetBoolean());
            Assert.Equal(2, r.GetProperty("meta").GetProperty("rating").GetInt32());
            await u.Client.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Fremde_Kennungen_werden_abgewiesen()
    {
        var (config, u, src, root, before) = await FolderAsync("cleo", "seen02", r => File.WriteAllBytes(Path.Combine(r, "foto.jpg"), Jpeg()));
        try
        {
            var other = _fx.As("ben");
            var id = src.Id + "/foto.jpg";
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/gallery/external/refresh", new { id })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await u.Client.PostAsJsonAsync("/api/gallery/external/refresh", new { id = src.Id + "/gibt-es-nicht.jpg" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await u.Client.PostAsJsonAsync("/api/gallery/external/refresh", new { id = "foto.jpg" })).StatusCode);

            var v = await Visible(other, [id]);
            Assert.Equal(0, v.GetProperty("items").GetArrayLength());
            Assert.Equal(0, v.GetProperty("open").GetArrayLength());
            Assert.Equal(1, GalleryFolders.Stages(u.Space.Db, src.Id).Listed);
            await u.Client.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sichtbare_Kacheln_lesen_nur_bis_zur_Obergrenze()
    {
        var jpg = Jpeg();
        var (config, u, src, root, before) = await FolderAsync("cleo", "seen03", r =>
        {
            for (var n = 0; n < 45; n++) File.WriteAllBytes(Path.Combine(r, $"f{n:D2}.jpg"), jpg);
        });
        try
        {
            var ids = Enumerable.Range(0, 45).Select(n => $"{src.Id}/f{n:D2}.jpg").ToList();
            Assert.Equal(45, GalleryFolders.Stages(u.Space.Db, src.Id).Listed);
            var v = await Visible(u.Client, ids);
            // Höchstens 40 Kennungen, davon 8 gelesen - die anderen kommen beim nächsten Aufruf dran
            Assert.Equal(45 - GalleryFolders.VisibleReads, GalleryFolders.Stages(u.Space.Db, src.Id).Listed);
            Assert.Equal(GalleryFolders.VisibleMax - GalleryFolders.VisibleReads, v.GetProperty("open").GetArrayLength());
            Assert.Equal(GalleryFolders.VisibleReads, v.GetProperty("items").GetArrayLength());
            var item = v.GetProperty("items")[0];
            Assert.True(item.GetProperty("hasThumb").GetBoolean());
            Assert.False(item.GetProperty("pending").GetBoolean());
            Assert.Equal(60, item.GetProperty("meta").GetProperty("width").GetInt32());
            Assert.DoesNotContain("-o", item.GetProperty("etag").GetString());
            Assert.Equal(HttpStatusCode.OK, (await u.Client.GetAsync("/api/gallery/external/thumb?id=" + Uri.EscapeDataString(ids[0]))).StatusCode);

            // Keine Änderung, nichts offen unter den gelesenen: kein neues Lesen, nichts gemeldet
            var read = ids.Take(GalleryFolders.VisibleReads).ToList();
            var tag = GalleryFolders.Version(u.Space, config);
            Assert.Equal(0, (await Visible(u.Client, read)).GetProperty("items").GetArrayLength());
            Assert.Equal(tag, GalleryFolders.Version(u.Space, config));

            // Datei geändert: zurückgesetzt und neu gelesen
            File.WriteAllBytes(Path.Combine(root, "f00.jpg"), Jpeg(100, 50));
            v = await Visible(u.Client, read);
            Assert.Equal(1, v.GetProperty("items").GetArrayLength());
            Assert.True(v.GetProperty("items")[0].GetProperty("fileChanged").GetBoolean());
            Assert.Equal(100, v.GetProperty("items")[0].GetProperty("meta").GetProperty("width").GetInt32());
            await u.Client.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Nicht_erreichbarer_Speicher_loescht_nichts_geloeschte_Datei_geht()
    {
        var (config, u, src, root, before) = await FolderAsync("cleo", "seen04", r =>
        {
            File.WriteAllBytes(Path.Combine(r, "a.jpg"), Jpeg());
            File.WriteAllBytes(Path.Combine(r, "b.jpg"), Jpeg());
        });
        var keep = Path.Combine(Path.GetTempPath(), "dmseen-weg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = src.Id + "/a.jpg";
            // Ausgehängte Freigabe: der Ordner ist leer
            Directory.Move(root, keep); Directory.CreateDirectory(root);
            var r = await Refresh(u.Client, a);
            Assert.False(r.GetProperty("gone").GetBoolean());
            Assert.False(r.GetProperty("changed").GetBoolean());
            Assert.Equal(0, (await Visible(u.Client, [a])).GetProperty("items").GetArrayLength());
            Assert.Equal(2, GalleryFolders.Stages(u.Space.Db, src.Id).Total);

            // Ordner ganz weg: Fehler beim Auflisten bleiben still
            Directory.Delete(root);
            r = await Refresh(u.Client, a);
            Assert.False(r.GetProperty("gone").GetBoolean());
            Assert.Equal(0, (await Visible(u.Client, [a])).GetProperty("items").GetArrayLength());
            Assert.Equal(2, GalleryFolders.Stages(u.Space.Db, src.Id).Total);
            Directory.Move(keep, root);

            // Wirklich gelöscht, Speicher da: der Eintrag geht
            File.Delete(Path.Combine(root, "a.jpg"));
            r = await Refresh(u.Client, a);
            Assert.True(r.GetProperty("gone").GetBoolean());
            Assert.Equal(1, GalleryFolders.Stages(u.Space.Db, src.Id).Total);
            await u.Client.DeleteAsync("/api/gallery/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => s.ExternalFolders = before);
            if (Directory.Exists(keep)) Directory.Delete(keep, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Nicht_lesbare_Datei_haelt_die_uebrigen_nicht_auf()
    {
        // Eine offene Datei laesst sich nur unter Windows sperren
        if (!OperatingSystem.IsWindows()) return;
        var f = await FolderAsync("dora", "spaeter", root =>
        {
            for (var i = 0; i < 3; i++)
            {
                var file = Path.Combine(root, $"f{i}.jpg");
                File.WriteAllBytes(file, Jpeg());
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(i - 3));
            }
        });
        try
        {
            using (File.Open(Path.Combine(f.Root, "f2.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Der Hintergrundlauf kann schon gelesen haben: alle wieder offen
                f.U.Space.Db.Exec("UPDATE gallery_ext SET stage = 0 WHERE source = $s", ("$s", f.Src.Id));
                await GalleryFolders.HeadsAsync(f.U.Space, f.Config, f.Src, DateTime.UtcNow.AddSeconds(30), null, CancellationToken.None);
            }
            Assert.Equal(1, GalleryFolders.Stages(f.U.Space.Db, f.Src.Id).Listed);
            Assert.Equal(["f2.jpg"], GalleryFolders.RetryLater(f.Src.Id));
            GalleryFolders.RetryNow(f.Src.Id);
            await GalleryFolders.HeadsAsync(f.U.Space, f.Config, f.Src, DateTime.UtcNow.AddSeconds(30), null, CancellationToken.None);
            Assert.Equal(0, GalleryFolders.Stages(f.U.Space.Db, f.Src.Id).Listed);
        }
        finally
        {
            await f.U.Client.DeleteAsync("/api/gallery/sources/" + f.Src.Id);
            f.Config.Update(s => s.ExternalFolders = f.Before);
        }
    }
}
