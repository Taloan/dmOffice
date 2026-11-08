using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Notes;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Einzelne Notiz teilen (Version 2): Zeigen, Senden, Teilen; nur genau diese Notiz; Verschieben und Löschen beim Besitzer.</summary>
[Collection("server")]
public class SingleNoteShareTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public SingleNoteShareTests(SharedCalendarFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private string[] Ids(params string[] logins) => logins.Select(m => _fx.Users[m].Id).ToArray();

    private NotesDb AnnasDb() => NotesDb.Of(_fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id));

    private static Task<HttpResponseMessage> Save(HttpClient c, long id, string text) =>
        c.PutAsync($"/api/notes/{id}/content", new StringContent(JsonSerializer.Serialize(new { blocks = new[] { new { html = $"<p>{text}</p>" } }, plain = text }), Encoding.UTF8, "application/json"));

    private async Task<long> NewNote(string? folder, string text)
    {
        var anna = _fx.As("anna");
        var id = (await Json(await anna.PostAsJsonAsync("/api/notes", new { folder }))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Save(anna, id, text)).StatusCode);
        return id;
    }

    private async Task<JsonElement?> SharedWith(string login, string title) =>
        (await Json(await _fx.As(login).GetAsync("/api/notes/single-shared"))).EnumerateArray().Cast<JsonElement?>()
            .FirstOrDefault(x => x!.Value.GetProperty("title").GetString() == title);

    [Fact]
    public async Task Einzelne_Notiz_mit_drei_Stufen_nur_diese_Notiz()
    {
        var anna = _fx.As("anna");
        var folder = (await Json(await anna.PostAsJsonAsync("/api/notes/folders", new { name = "Privat-" + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetString()!;
        var title = "Einkauf " + Guid.NewGuid().ToString("N")[..6];
        var note = await NewNote(folder, title);
        var other = await NewNote(folder, "Geheim daneben");
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Liste")), "files", "liste.txt" } };
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsync($"/api/notes/{note}/attachments", form)).StatusCode);
        var blob = AnnasDb().GetNote(note)!.Attachments.Single().Blob;

        // Ben teilen, Cleo senden, Dora zeigen
        var body = new { members = Ids("ben", "cleo", "dora"), readOnly = Ids("cleo"), showOnly = Ids("dora") };
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync($"/api/notes/{note}/shares", body)).StatusCode);
        var members = (await Json(await anna.GetAsync($"/api/notes/{note}/shares"))).EnumerateArray().ToList();
        Assert.Equal(["share", "send", "show"], members.OrderBy(m => m.GetProperty("name").GetString()).Select(m => m.GetProperty("level").GetString()));
        Assert.Equal(3, (await Json(await anna.GetAsync($"/api/notes/{note}"))).GetProperty("sharedWith").GetInt32());

        // Ben: sieht sie unter „Mit mir geteilt“, darf bearbeiten - landet beim Besitzer
        var ben = _fx.As("ben");
        var entry = (await SharedWith("ben", title))!.Value;
        var wire = entry.GetProperty("id").GetInt64();
        Assert.True(wire < 0);
        Assert.True(entry.GetProperty("canWrite").GetBoolean());
        Assert.Equal("Anna", entry.GetProperty("owner").GetString());
        var view = await Json(await ben.GetAsync($"/api/notes/{wire}"));
        Assert.True(view.GetProperty("single").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Save(ben, wire, title + " und Milch")).StatusCode);
        Assert.Contains("Milch", AnnasDb().GetNote(note)!.Html);
        // ... aber nicht verschieben, archivieren oder in den Papierkorb legen
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PutAsJsonAsync($"/api/notes/{wire}/meta", new { folder = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PostAsync($"/api/notes/{wire}/trash", null)).StatusCode);
        Assert.Null(AnnasDb().GetNote(note)!.Deleted);
        // Die Notiz daneben ist über dieselbe Freigabe nicht erreichbar
        var stride = 1L << 32;
        var guessed = wire / stride * stride - other;
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/notes/{guessed}")).StatusCode);
        // Teilen kann nur der Besitzer
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PutAsJsonAsync($"/api/notes/{wire}/shares", body)).StatusCode);

        // Cleo (senden): lesen und Anhang herunterladen ja, ändern nein
        var cleo = _fx.As("cleo");
        Assert.False((await SharedWith("cleo", title + " und Milch"))!.Value.GetProperty("canWrite").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Save(cleo, wire, "von Cleo")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cleo.GetAsync($"/api/notes/blob/{blob}?download=true")).StatusCode);

        // Dora (zeigen): lesen ja, Anhang nicht herunterladen
        var dora = _fx.As("dora");
        Assert.True((await SharedWith("dora", title + " und Milch"))!.Value.GetProperty("showOnly").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await dora.GetAsync($"/api/notes/{wire}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dora.GetAsync($"/api/notes/blob/{blob}?download=true")).StatusCode);

        // Ben steigt aus; Anna hebt die Freigabe auf
        var shareId = entry.GetProperty("share").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await ben.PostAsync($"/api/notes/single-shared/{shareId}/leave", null)).StatusCode);
        Assert.Null(await SharedWith("ben", title + " und Milch"));
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/notes/{wire}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync($"/api/notes/{note}/shares", new { members = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cleo.GetAsync($"/api/notes/{wire}")).StatusCode);
    }

    [Fact]
    public async Task Im_Papierkorb_nicht_mehr_sichtbar()
    {
        var anna = _fx.As("anna");
        var title = "Bald weg " + Guid.NewGuid().ToString("N")[..6];
        var note = await NewNote(null, title);
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync($"/api/notes/{note}/shares", new { members = Ids("ben") })).StatusCode);
        Assert.NotNull(await SharedWith("ben", title));
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsync($"/api/notes/{note}/trash", null)).StatusCode);
        Assert.Null(await SharedWith("ben", title));
    }
}
