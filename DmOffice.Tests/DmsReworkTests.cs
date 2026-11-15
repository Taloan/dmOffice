using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Dms;

namespace DmOffice.Tests;

/// <summary>Überarbeiten (dmsrework): bestätigte Dokumente zurück in die Inbox (Datei in den Inbox-Ordner) und nach Vorgabe ablegen mit gewähltem Profil (Ordner und Dateiname).</summary>
[Collection("server")]
public class DmsReworkTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public DmsReworkTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Zurueck_in_die_Inbox_und_mit_anderem_Profil_ablegen()
    {
        var c = _fx.As("dora");
        var files = (await Json(await c.GetAsync("/api/dms/profiles"))).EnumerateArray().First(p => p.GetProperty("target").GetString() == "files").GetProperty("id").GetString()!;
        await Json(await c.PostAsync("/api/dms/upload?profile=" + files, new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Ueberarbeiten Probebrief vom 02.03.2026")), "file", "scan_0815.txt" } }));
        await DmsImport.ProcessQueueAsync(_fx.Space("dora"), DmsHost.Current!, 100, default);
        var id = (await Json(await c.GetAsync("/api/dms/docs?q=Probebrief"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        await Json(await c.PostAsJsonAsync("/api/dms/docs/batch", new { ids = new[] { id }, change = new { title = "Probebrief", confirm = true } }));
        var confirmed = (await Json(await c.GetAsync($"/api/dms/docs/{id}"))).GetProperty("path").GetString()!;
        Assert.DoesNotContain(".INBOX", confirmed);

        // Zurück in die Inbox: unbestätigt, Datei im Inbox-Ordner
        await Json(await c.PostAsJsonAsync("/api/dms/docs/to-inbox", new { ids = new[] { id } }));
        var back = await Json(await c.GetAsync($"/api/dms/docs/{id}"));
        Assert.True(back.GetProperty("inbox").GetBoolean());
        Assert.Contains("/.INBOX/", back.GetProperty("path").GetString());

        // Wieder bestätigt, dann mit einem anderen Profil ablegen: Ordner und Dateiname nach diesem Profil
        await Json(await c.PostAsJsonAsync("/api/dms/docs/batch", new { ids = new[] { id }, change = new { confirm = true } }));
        var other = (await Json(await c.PostAsJsonAsync("/api/dms/profiles", new
        {
            name = "Neu", target = "files", folder = "Neu", folders = "{doc_year}", names = "{doc_date} {title}", unknown = "Unbekannt", follow = false, language = "de",
        }))).GetProperty("id").GetString()!;
        await Json(await c.PostAsJsonAsync("/api/dms/docs/refile", new { ids = new[] { id }, profile = other }));
        var placed = await Json(await c.GetAsync($"/api/dms/docs/{id}"));
        Assert.Equal("Neu/2026/2026-03-02 Probebrief.txt", placed.GetProperty("path").GetString());
        Assert.Equal(other, placed.GetProperty("profile").GetString());
    }
}
