using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Notes;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Bilder in Notizen im Web-Editor (noteimg): Bildbloecke nicht gesperrt - Groesse, Verschieben, Loeschen kommen durch,
/// Tabellen bleiben gesperrt (notizi nimmt die Version sonst nicht an).</summary>
[Collection("server")]
public class NoteImageBlockTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public NoteImageBlockTests(SharedCalendarFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Put(HttpClient c, long id, object[] blocks, string? version) =>
        c.PutAsync($"/api/notes/{id}/content", new StringContent(JsonSerializer.Serialize(new { blocks, plain = "x", version }), Encoding.UTF8, "application/json"));

    private const string Img = "<p><img src=\"blob:abc\" style=\"max-width:100%\" /></p>";
    private const string Table = "<table><tbody><tr><td>Mehl</td><td>200 g</td></tr></tbody></table>";

    [Fact]
    public async Task Bild_frei_Tabelle_gesperrt()
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.NoteImages));
        try
        {
            var anna = _fx.As("anna");
            // Tabelle kommt aus notizi (Sync), nicht aus dem Web-Editor
            var notes = NotesDb.Of(_fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id));
            var id = notes.CreateNote(null);
            notes.SaveNote(id, $"<div data-notizi=\"1\"><p>Gänsekeulen</p>{Img}{Table}</div>", "Gänsekeulen\nMehl 200 g");
            var note = await Json(await anna.GetAsync($"/api/notes/{id}"));
            var v = note.TryGetProperty("version", out var vv) ? vv.GetString() : null;
            Assert.Equal([false, false, true], note.GetProperty("protected").EnumerateArray().Select(x => x.GetBoolean()).ToArray());

            // Kleiner und unter die Tabelle verschoben
            var r = await Json(await Put(anna, id, [new { html = "<p>Gänsekeulen</p>" }, new { i = 2 }, new { html = "<p><img src=\"blob:abc\" style=\"max-width:100%;width:50%\" /></p>" }], v));
            v = r.GetProperty("version").GetString();
            note = await Json(await anna.GetAsync($"/api/notes/{id}"));
            Assert.Matches("width: ?50%", note.GetProperty("blocks")[2].GetString());
            Assert.Contains("<table", note.GetProperty("blocks")[1].GetString());

            // Geloescht
            v = (await Json(await Put(anna, id, [new { i = 0 }, new { i = 1 }], v))).GetProperty("version").GetString();
            Assert.Equal(2, (await Json(await anna.GetAsync($"/api/notes/{id}"))).GetProperty("blocks").GetArrayLength());

            // Tabelle geaendert: abgelehnt
            var bad = await Put(anna, id, [new { i = 0 }, new { html = Table.Replace("200 g", "300 g") }], v);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally { Features.Set(before); }
    }
}
