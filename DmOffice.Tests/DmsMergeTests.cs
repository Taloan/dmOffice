using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dms;

namespace DmOffice.Tests;

/// <summary>Einträge zusammenführen (dmsmerge): Dokumente, Gelerntes, Unterbegriffe und Rollen gehen auf das Ziel über, die Quellen
/// verschwinden; je Dokument entsteht eine Version, die sich zurücknehmen lässt; Umbenennen auf einen vorhandenen Namen meldet den
/// Eintrag; der geschlossene Tresor holt das Umhängen beim Öffnen nach.</summary>
[Collection("server")]
public class DmsMergeTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public DmsMergeTests(ChatFixture fx)
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

    private async Task<long> Upload(HttpClient c, string login, string text, string name)
    {
        var files = (await Json(await c.GetAsync("/api/dms/profiles"))).EnumerateArray().First(p => p.GetProperty("target").GetString() == "files").GetProperty("id").GetString()!;
        await Json(await c.PostAsync("/api/dms/upload?profile=" + files, new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", name } }));
        await DmsImport.ProcessQueueAsync(_fx.Space(login), DmsHost.Current!, 100, default);
        return (await Json(await c.GetAsync("/api/dms/docs?q=" + Uri.EscapeDataString(text.Split(' ')[0])))).GetProperty("items")[0].GetProperty("id").GetInt64();
    }

    private static async Task<string> Entry(HttpClient c, object body) =>
        (await Json(await c.PostAsJsonAsync("/api/dms/lists", body))).GetProperty("id").GetString()!;

    [Fact]
    public async Task Korrespondenten_und_Tags_zusammenfuehren_samt_Gelerntem_und_Version()
    {
        var c = _fx.As("anna");
        var db = _fx.Space("anna").Db;
        var a = await Upload(c, "anna", "Zusammenalpha Rechnung Telefon", "merge_a.txt");
        var b = await Upload(c, "anna", "Zusammenbeta Rechnung Telefon", "merge_b.txt");
        var telekom = await Entry(c, new { kind = "correspondent", name = "Telekom", match = "sender", matchText = "telekom.de" });
        var deutsche = await Entry(c, new { kind = "correspondent", name = "Deutsche Telekom", match = "sender", matchText = "rechnung@example.org" });
        var unter = await Entry(c, new { kind = "correspondent", name = "Telekom Mobil", parent = deutsche });
        var x = await Entry(c, new { kind = "tag", name = "Telefon" });
        var y = await Entry(c, new { kind = "tag", name = "Telefonie" });
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{a}", new { correspondent = telekom, tags = new[] { x, y } }));
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{b}", new { correspondent = deutsche, tags = new[] { y } }));

        // Probelauf zählt nur
        var dry = await Json(await c.PostAsJsonAsync("/api/dms/lists/merge", new { target = telekom, sources = new[] { deutsche }, dryRun = true }));
        Assert.Equal(1, dry.GetProperty("docs").GetInt32());
        Assert.NotNull(db.Scalar("SELECT 1 FROM dms_lists WHERE id = $i", ("$i", deutsche)));

        // Verschiedene Listen gehen nicht
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/dms/lists/merge", new { target = telekom, sources = new[] { x } })).StatusCode);

        var r = await Json(await c.PostAsJsonAsync("/api/dms/lists/merge", new { target = telekom, sources = new[] { deutsche } }));
        Assert.Equal(1, r.GetProperty("docs").GetInt32());
        var change = r.GetProperty("change").GetString()!;
        Assert.Equal(telekom, (await Json(await c.GetAsync($"/api/dms/docs/{b}"))).GetProperty("correspondent").GetString());
        var lists = (await Json(await c.GetAsync("/api/dms/lists"))).EnumerateArray().ToList();
        Assert.DoesNotContain(lists, e => e.GetProperty("id").GetString() == deutsche);
        // Unterbegriff rückt unters Ziel, die Absender werden vereint
        Assert.Equal(telekom, lists.First(e => e.GetProperty("id").GetString() == unter).GetProperty("parent").GetString());
        var matchText = lists.First(e => e.GetProperty("id").GetString() == telekom).GetProperty("matchText").GetString()!;
        Assert.Contains("telekom.de", matchText);
        Assert.Contains("rechnung@example.org", matchText);
        // Gelerntes: nichts mehr an der Quelle
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM dms_trained WHERE entry = $e", ("$e", deutsche)));
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM dms_tokens WHERE entry = $e", ("$e", deutsche)));

        // Tags: a trägt beide - das Gelernte zählt danach einmal, nicht doppelt
        Assert.Equal(1L, db.Scalar("SELECT pos FROM dms_tokens WHERE entry = $e AND token = 'zusammenalpha'", ("$e", x)));
        await Json(await c.PostAsJsonAsync("/api/dms/lists/merge", new { target = x, sources = new[] { y } }));
        Assert.Equal(1L, db.Scalar("SELECT pos FROM dms_tokens WHERE entry = $e AND token = 'zusammenalpha'", ("$e", x)));
        Assert.Equal(1L, db.Scalar("SELECT pos FROM dms_tokens WHERE entry = $e AND token = 'zusammenbeta'", ("$e", x)));
        Assert.Equal(2L, db.Scalar("SELECT pos FROM dms_tokens WHERE entry = $e AND token = 'telefon'", ("$e", x)));
        var tagsA = (await Json(await c.GetAsync($"/api/dms/docs/{a}"))).GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.Equal([x], tagsA);

        // Je Dokument zurücknehmen: die Version stellt die Quelle wieder her und hängt b zurück
        var history = await Json(await c.GetAsync($"/api/dms/docs/{b}/history"));
        var merge = history.EnumerateArray().First(v => v.GetProperty("change").GetString() == change);
        await Json(await c.PostAsync($"/api/dms/docs/{b}/history/{merge.GetProperty("id").GetInt64()}/restore", null));
        Assert.Equal(deutsche, (await Json(await c.GetAsync($"/api/dms/docs/{b}"))).GetProperty("correspondent").GetString());
        Assert.Contains((await Json(await c.GetAsync("/api/dms/lists"))).EnumerateArray(), e => e.GetProperty("id").GetString() == deutsche
            && e.GetProperty("name").GetString() == "Deutsche Telekom");
    }

    [Fact]
    public async Task Umbenennen_auf_vorhandenen_Namen_meldet_den_Eintrag()
    {
        var c = _fx.As("ben");
        var one = await Entry(c, new { kind = "doctype", name = "Rechnung" });
        var two = await Entry(c, new { kind = "doctype", name = "Rechnungen" });
        var r = await c.PutAsJsonAsync($"/api/dms/lists/{two}", new { name = "rechnung" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(one, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("existing").GetString());

        // Ohne Freischaltung: wie bisher abgelehnt, Zusammenführen gibt es nicht
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set([Features.DmsMerge]);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/dms/lists/{two}", new { name = "rechnung" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/dms/lists/merge", new { target = one, sources = new[] { two } })).StatusCode);
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public async Task Rollen_zusammenfuehren_das_neuere_Dokument_gilt()
    {
        var c = _fx.As("dora");
        var a = await Upload(c, "dora", "Rollenalt Testament", "rolle_a.txt");
        var b = await Upload(c, "dora", "Rollenneu Testament", "rolle_b.txt");
        var r1 = await Entry(c, new { kind = "role", name = "Testament" });
        var r2 = await Entry(c, new { kind = "role", name = "Letzter Wille" });
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{a}", new { role = r1 }));
        _fx.Space("dora").Db.Exec("UPDATE dms_roles SET since = since - 100 WHERE role = $r", ("$r", r1));
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{b}", new { role = r2 }));
        await Json(await c.PostAsJsonAsync("/api/dms/lists/merge", new { target = r1, sources = new[] { r2 } }));
        var da = await Json(await c.GetAsync($"/api/dms/docs/{a}"));
        var dbb = await Json(await c.GetAsync($"/api/dms/docs/{b}"));
        Assert.Equal(r1, da.GetProperty("role").GetString());
        Assert.Equal(r1, dbb.GetProperty("role").GetString());
        Assert.Equal(b, DmOffice.Dms.Dms.Roles(_fx.Space("dora").Db)[r1].Doc);
    }

    [Fact]
    public async Task Geschlossener_Tresor_holt_das_Zusammenfuehren_beim_Oeffnen_nach()
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
        await Json(await c.PostAsync("/api/dms/upload?profile=" + profile,
            new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Tresorbrief Praxis Befund")), "file", "Brief.txt" } }));
        var space = _fx.Space("cleo");
        await DmsImport.ProcessQueueAsync(space, DmsHost.Current!, 100, default, DmsStore.ForTresor(space.Db, Tresor.DbOf(space.User.Id)!));
        var doc = (await Json(await c.GetAsync("/api/dms/docs?area=tresor"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        var praxis = await Entry(c, new { kind = "correspondent", name = "Praxis" });
        var arzt = await Entry(c, new { kind = "correspondent", name = "Arztpraxis" });
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{doc}?area=tresor", new { correspondent = arzt }));

        await Json(await c.PostAsync("/api/tresor/close", null));
        var r = await Json(await c.PostAsJsonAsync("/api/dms/lists/merge", new { target = praxis, sources = new[] { arzt } }));
        Assert.True(r.GetProperty("tresorLater").GetBoolean());
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.Equal(praxis, (await Json(await c.GetAsync($"/api/dms/docs/{doc}?area=tresor"))).GetProperty("correspondent").GetString());
        Assert.Empty(space.Db.GetJson<List<DmsMerge.Pending>>(DmsMerge.PendingKey));
    }
}
