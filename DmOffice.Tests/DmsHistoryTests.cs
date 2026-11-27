using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Dms;

namespace DmOffice.Tests;

/// <summary>Versionen der DMS-Angaben (dmshistory): jede Änderung von Hand wird festgehalten, lässt sich wiederherstellen
/// und als Massenänderung zurücknehmen - ein seitdem erneut geändertes Dokument bleibt dabei unangetastet.</summary>
[Collection("server")]
public class DmsHistoryTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public DmsHistoryTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<long> Upload(HttpClient c, string text, string name)
    {
        var files = (await Json(await c.GetAsync("/api/dms/profiles"))).EnumerateArray().First(p => p.GetProperty("target").GetString() == "files").GetProperty("id").GetString()!;
        await Json(await c.PostAsync("/api/dms/upload?profile=" + files, new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", name } }));
        await DmsImport.ProcessQueueAsync(_fx.Space("dora"), DmsHost.Current!, 100, default);
        return (await Json(await c.GetAsync("/api/dms/docs?q=" + Uri.EscapeDataString(text.Split(' ')[0])))).GetProperty("items")[0].GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Version_festhalten_wiederherstellen_und_Massenaenderung_zuruecknehmen()
    {
        var c = _fx.As("dora");
        var a = await Upload(c, "Verlaufeins Schreiben", "verlauf_a.txt");
        var b = await Upload(c, "Verlaufzwei Schreiben", "verlauf_b.txt");

        // Einzeln: Titel ändern - eine Version mit altem und neuem Titel
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{a}", new { title = "Erster Titel" }));
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{a}", new { title = "Zweiter Titel" }));
        var list = await Json(await c.GetAsync($"/api/dms/docs/{a}/history"));
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("Erster Titel", list[0].GetProperty("before").GetProperty("title").GetString());
        Assert.Equal("Zweiter Titel", list[0].GetProperty("after").GetProperty("title").GetString());

        // Wiederherstellen: Stand vor der letzten Änderung, als neue Version
        await Json(await c.PostAsync($"/api/dms/docs/{a}/history/{list[0].GetProperty("id").GetInt64()}/restore", null));
        Assert.Equal("Erster Titel", (await Json(await c.GetAsync($"/api/dms/docs/{a}"))).GetProperty("title").GetString());
        Assert.Equal(3, (await Json(await c.GetAsync($"/api/dms/docs/{a}/history"))).GetArrayLength());

        // Massenänderung mit Kennung: Korrespondent für beide, dann b noch einmal von Hand geändert
        var corr = (await Json(await c.PostAsJsonAsync("/api/dms/lists", new { kind = "correspondent", name = "Verlauf GmbH" }))).GetProperty("id").GetString()!;
        await Json(await c.PostAsJsonAsync("/api/dms/docs/batch", new { ids = new[] { a, b }, change = new { correspondent = corr, batch = true, changeId = "probe-1" } }));
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{b}", new { title = "Danach geändert" }));

        // Zurücknehmen: a ohne Korrespondent, b bleibt (seitdem geändert)
        var undo = await Json(await c.PostAsJsonAsync("/api/dms/history/undo", new { change = "probe-1" }));
        Assert.Equal(1, undo.GetProperty("restored").GetInt32());
        Assert.Equal(1, undo.GetProperty("skipped").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await Json(await c.GetAsync($"/api/dms/docs/{a}"))).GetProperty("correspondent").ValueKind);
        Assert.Equal(corr, (await Json(await c.GetAsync($"/api/dms/docs/{b}"))).GetProperty("correspondent").GetString());
    }
}
