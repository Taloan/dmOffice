using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dms;

namespace DmOffice.Tests;

/// <summary>DMS im Tresor (docs/dms-konzept.md, „Tresor“): Ablage und Suche nur im offenen Tresor, nichts im offenen Bereich,
/// Ablage bei geschlossenem Tresor und Übernahme beim Öffnen, Verschieben zwischen den Bereichen.</summary>
[Collection("server")]
public class DmsTresorTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public DmsTresorTests(ChatFixture fx)
    {
        _fx = fx;
        Tresor.ArgonMemory = 1024; Tresor.ScryptCost = 1024;
        Tresor.ArgonIterations = 1;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Upload(HttpClient c, string profile, string name, string text) =>
        c.PostAsync("/api/dms/upload?profile=" + profile, new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", name } });

    private async Task Process(string login)
    {
        var space = _fx.Space(login);
        var store = DmsStore.ForTresor(space.Db, Tresor.DbOf(space.User.Id)!);
        await DmsImport.ProcessQueueAsync(space, DmsHost.Current!, 100, default, store);
    }

    [Fact]
    public async Task Dokumente_im_Tresor_offen_zu_und_verschieben()
    {
        var id = _fx.Id("cleo");
        Tresor.Delete(id);
        var c = _fx.As("cleo");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1", name = "Testhandy" }));
        await Json(await c.GetAsync("/api/dms/profiles"));
        var profile = (await Json(await c.PostAsJsonAsync("/api/dms/profiles", new
        {
            name = "Privat", target = "tresor", folder = "Dokumente", folders = "{doc_year}", names = "{title}", unknown = "Unbekannt", follow = true, language = "de",
        }))).GetProperty("id").GetString()!;

        // Offen: liegt im Tresor, nicht im offenen Bereich
        await Json(await Upload(c, profile, "Befund.txt", "Befund Dr. Muster Blutwerte vom 03.02.2026"));
        await Process("cleo");
        var inTresor = await Json(await c.GetAsync("/api/dms/docs?area=tresor"));
        var doc = inTresor.GetProperty("items")[0];
        Assert.Equal("Dokumente/.INBOX/Befund.txt", doc.GetProperty("path").GetString());
        Assert.Equal(1, (await Json(await c.GetAsync("/api/dms/docs?area=tresor&q=Blutwerte"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await c.GetAsync("/api/dms/docs"))).GetProperty("total").GetInt32());
        Assert.Null(_fx.Space("cleo").Db.Scalar("SELECT 1 FROM dms_text WHERE body LIKE '%Blutwerte%'"));
        Assert.Empty((await Json(await c.GetAsync("/api/files/search/content?q=Blutwerte"))).GetProperty("items").EnumerateArray());

        // Herunterladen aus dem offenen Tresor, unter dem Titel
        var docId = doc.GetProperty("id").GetInt64().ToString();
        HttpContent Pick(params string[] ids) => new FormUrlEncodedContent(ids.Select(x => new KeyValuePair<string, string>("ids", x)).Append(new("area", "tresor")));
        var one = await c.PostAsync("/api/dms/docs/download", Pick(docId));
        Assert.True(one.IsSuccessStatusCode);
        Assert.EndsWith(".txt", (one.Content.Headers.ContentDisposition?.FileNameStar ?? one.Content.Headers.ContentDisposition?.FileName ?? "").Trim('"'));
        Assert.Contains("Blutwerte", await one.Content.ReadAsStringAsync());

        // Zu: nicht lesbar, Neues wird verschlüsselt abgelegt und beim Öffnen übernommen
        await Json(await c.PostAsync("/api/tresor/close", null));
        Assert.Equal((HttpStatusCode)423, (await c.GetAsync("/api/dms/docs?area=tresor")).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await c.PostAsync("/api/dms/docs/download", Pick(docId))).StatusCode);
        var up = await Json(await Upload(c, profile, "Rezept.txt", "Rezept Ibuprofen 10.02.2026"));
        Assert.True(up.GetProperty("files")[0].GetProperty("dropped").GetBoolean());
        Assert.Equal(1, Tresor.Pending(id));
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.Equal(0, Tresor.Pending(id));
        await Process("cleo");
        var pair = await Json(await c.GetAsync("/api/dms/docs?area=tresor"));
        Assert.Equal(2, pair.GetProperty("total").GetInt32());

        // Mehrere als ZIP
        var zipped = await c.PostAsync("/api/dms/docs/download", Pick([.. pair.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt64().ToString())]));
        Assert.Equal("application/zip", zipped.Content.Headers.ContentType?.MediaType);
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await zipped.Content.ReadAsByteArrayAsync())))
            Assert.Equal(2, zip.Entries.Count);

        // Aus dem Tresor holen: im offenen Bereich mit Angaben und Text, im Tresor weg
        var moved = await Json(await c.PostAsJsonAsync("/api/dms/docs/move-area", new { ids = new[] { doc.GetProperty("id").GetInt64() }, to = "files" }));
        Assert.Equal(1, moved.GetProperty("moved").GetInt32());
        var open = await Json(await c.GetAsync("/api/dms/docs?q=Blutwerte"));
        Assert.Equal(1, open.GetProperty("total").GetInt32());
        Assert.StartsWith("Dokumente/", open.GetProperty("items")[0].GetProperty("path").GetString());
        Assert.Equal(1, (await Json(await c.GetAsync("/api/dms/docs?area=tresor"))).GetProperty("total").GetInt32());

        // Unterbrochenes Verschieben (Kopie im Tresor fertig, die im offenen Bereich blieb): erneut verschieben löscht die offene Kopie
        var files = (await Json(await c.GetAsync("/api/dms/profiles"))).EnumerateArray().First(p => p.GetProperty("target").GetString() == "files").GetProperty("id").GetString()!;
        await Json(await Upload(c, files, "Laborbefund.txt", "Laborbefund Kalium 4,2 vom 05.02.2026"));
        await DmsImport.ProcessQueueAsync(_fx.Space("cleo"), DmsHost.Current!, 100, default);
        await Json(await Upload(c, profile, "Laborbefund.txt", "Laborbefund Kalium 4,2 vom 05.02.2026"));
        await Process("cleo");
        var both = (await Json(await c.GetAsync("/api/dms/docs?q=Kalium"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        var merged = await Json(await c.PostAsJsonAsync("/api/dms/docs/move-area", new { ids = new[] { both }, to = "tresor" }));
        Assert.Equal(0, merged.GetProperty("moved").GetInt32());
        Assert.Equal(1, merged.GetProperty("merged").GetInt32());
        Assert.Equal(0, (await Json(await c.GetAsync("/api/dms/docs?q=Kalium"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Json(await c.GetAsync("/api/dms/docs?area=tresor&q=Kalium"))).GetProperty("total").GetInt32());

        // Bestätigt verschoben: im Tresor nach dem Ordnermuster, nicht im Inbox-Ordner
        await Json(await Upload(c, files, "Lohnsteuer.txt", "Lohnsteuerbescheinigung 2025 vom 01.04.2026"));
        await DmsImport.ProcessQueueAsync(_fx.Space("cleo"), DmsHost.Current!, 100, default);
        var tax = (await Json(await c.GetAsync("/api/dms/docs?q=Lohnsteuerbescheinigung"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        await Json(await c.PostAsJsonAsync("/api/dms/docs/batch", new { ids = new[] { tax }, change = new { confirm = true } }));
        await Json(await c.PostAsJsonAsync("/api/dms/docs/move-area", new { ids = new[] { tax }, to = "tresor", profile }));
        var taxDoc = (await Json(await c.GetAsync("/api/dms/docs?area=tresor&q=Lohnsteuerbescheinigung"))).GetProperty("items")[0];
        Assert.False(taxDoc.GetProperty("inbox").GetBoolean());
        Assert.DoesNotContain(".INBOX", taxDoc.GetProperty("path").GetString());

        // Zusammengeführt (Kopie im Tresor noch im Inbox-Ordner): bestätigt und nach dem Ordnermuster
        await Json(await Upload(c, files, "Rentenbescheid.txt", "Rentenbescheid Versicherungsverlauf vom 01.05.2026"));
        await DmsImport.ProcessQueueAsync(_fx.Space("cleo"), DmsHost.Current!, 100, default);
        await Json(await Upload(c, profile, "Rentenbescheid.txt", "Rentenbescheid Versicherungsverlauf vom 01.05.2026"));
        await Process("cleo");
        var rent = (await Json(await c.GetAsync("/api/dms/docs?q=Versicherungsverlauf"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        await Json(await c.PostAsJsonAsync("/api/dms/docs/batch", new { ids = new[] { rent }, change = new { confirm = true } }));
        Assert.Equal(1, (await Json(await c.PostAsJsonAsync("/api/dms/docs/move-area", new { ids = new[] { rent }, to = "tresor", profile }))).GetProperty("merged").GetInt32());
        var rentDoc = (await Json(await c.GetAsync("/api/dms/docs?area=tresor&q=Versicherungsverlauf"))).GetProperty("items")[0];
        Assert.False(rentDoc.GetProperty("inbox").GetBoolean());
        Assert.DoesNotContain(".INBOX", rentDoc.GetProperty("path").GetString());
        Tresor.Delete(id);
    }
}
