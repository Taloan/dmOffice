using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Notes;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Zeilen als &lt;div&gt; aus dem Browser-Editor (Enter, Einfügen) werden Absätze und speichern; gesperrte Blöcke bleiben geschützt.</summary>
[Collection("server")]
public class NotesPlainDivTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public NotesPlainDivTests(SharedCalendarFixture fx) => _fx = fx;

    private NotesDb Db() => NotesDb.Of(_fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id));

    private async Task<long> NewNote(HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/api/notes", new { folder = (string?)null });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static Task<HttpResponseMessage> Put(HttpClient c, long id, object[] blocks, string plain) =>
        c.PutAsync($"/api/notes/{id}/content", new StringContent(JsonSerializer.Serialize(new { blocks, plain }), Encoding.UTF8, "application/json"));

    [Fact]
    public void PlainDivs_macht_Absaetze_und_laesst_Gesperrtes()
    {
        Assert.Equal("<p>Zeile zwei</p>", NotesApi.PlainDivs("<div>Zeile zwei</div>"));
        Assert.Equal("<p><b>Kopf</b></p><p>Absatz</p>", NotesApi.PlainDivs("<div><p><b>Kopf</b></p><p>Absatz</p></div>"));
        Assert.Equal("<p>los</p><ul><li>a</li></ul>", NotesApi.PlainDivs("<div>los<ul><li>a</li></ul></div>"));
        Assert.Equal("<p style=\"text-align:center\">Mitte</p>", NotesApi.PlainDivs("<div style=\"text-align:center\">Mitte</div>"));
        Assert.Equal("<div data-block=\"image\"><img src=\"blob:x\"></div>", NotesApi.PlainDivs("<div data-block=\"image\"><img src=\"blob:x\"></div>"));
        Assert.Equal("<div><table><tbody><tr><td>x</td></tr></tbody></table></div>", NotesApi.PlainDivs("<div><table><tr><td>x</td></tr></table></div>"));
        Assert.Equal("<p>ohne</p>", NotesApi.PlainDivs("<p>ohne</p>"));
    }

    [Fact]
    public async Task Geaenderte_und_eingefuegte_Zeilen_als_div_speichern()
    {
        var c = _fx.As("anna");
        var id = await NewNote(c);
        Assert.Equal(HttpStatusCode.OK, (await Put(c, id, [new { html = "<p>Zeile eins</p>" }, new { html = "<div>Zeile zwei</div>" }], "Zeile eins\nZeile zwei")).StatusCode);
        Assert.Contains("<p>Zeile zwei</p>", Db().GetNote(id)!.Html);

        // Gerade getippte Zeile geändert, und eingefügtes HTML in einer solchen Zeile
        var r = await Put(c, id, [new { i = 0 }, new { html = "<div>Zeile zwei ergänzt</div>" }, new { html = "<div><p>Kopf</p><p>Absatz</p></div>" }], "x");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = Db().GetNote(id)!.Html;
        Assert.Contains("<p>Zeile zwei ergänzt</p>", html);
        Assert.Contains("<p>Kopf</p><p>Absatz</p>", html);
        Assert.DoesNotContain("<div>Zeile", html);
    }

    [Fact]
    public async Task Gesperrte_Tabelle_bleibt_geschuetzt()
    {
        var c = _fx.As("anna");
        var id = await NewNote(c);
        Assert.Equal(HttpStatusCode.OK, (await Put(c, id, [new { html = "<p>Eins</p>" }], "Eins")).StatusCode);
        var db = Db();
        var withTable = db.GetNote(id)!.Html.Replace("<p>Eins</p>", "<p>Eins</p><table><tr><td>alt</td></tr></table>");
        Assert.Contains("<table>", withTable);
        db.SaveNote(id, withTable, "Eins alt");

        // Tabelle unverändert ({ i }), neue Zeile als div: gespeichert
        Assert.Equal(HttpStatusCode.OK, (await Put(c, id, [new { i = 0 }, new { i = 1 }, new { html = "<div>neu</div>" }], "Eins alt neu")).StatusCode);
        Assert.Contains("<td>alt</td>", Db().GetNote(id)!.Html);

        // Tabelle verändert oder in ein div gepackt: weiterhin abgelehnt
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, id, [new { i = 0 }, new { html = "<table><tr><td>neu</td></tr></table>" }], "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(c, id, [new { i = 0 }, new { html = "<div><table><tr><td>neu</td></tr></table></div>" }], "x")).StatusCode);
        Assert.Contains("<td>alt</td>", Db().GetNote(id)!.Html);
    }
}
