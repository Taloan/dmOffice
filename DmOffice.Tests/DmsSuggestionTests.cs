using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Dms;

namespace DmOffice.Tests;

/// <summary>Vorschlag der KI für einen neuen Korrespondenten: sobald es den Eintrag gibt (über den Knopf, über Verwalten oder schon vorher),
/// hat jedes Dokument mit diesem Vorschlag den Korrespondenten, und der Hinweis „Den Eintrag gibt es noch nicht“ ist weg.</summary>
[Collection("server")]
public class DmsSuggestionTests(ChatFixture fx) : IClassFixture<ChatFixture>
{
    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Dokument hochladen, verarbeiten und den Vorschlag der KI setzen (die KI läuft im Test nicht).</summary>
    private async Task<long> WithSuggestion(string name, string text, string suggestion)
    {
        var space = fx.Space("dora");
        var r = await DmsImport.ImportAsync(space, new MemoryStream(Encoding.UTF8.GetBytes(text)), name, "text/plain", new DmsImport.Info("upload"), default);
        await DmsImport.ProcessQueueAsync(space, DmsHost.Current!, 100, default);
        space.Db.Exec("UPDATE dms_docs SET ai_suggestion = $s WHERE id = $d", ("$s", suggestion), ("$d", r.Id));
        return r.Id;
    }

    private static async Task<JsonElement> Doc(HttpClient c, long id) => await Json(await c.GetAsync($"/api/dms/docs/{id}"));

    private static string? Str(JsonElement e, string prop) => e.GetProperty(prop).ValueKind == JsonValueKind.Null ? null : e.GetProperty(prop).GetString();

    [Fact]
    public async Task Vorschlag_wird_zugeordnet_sobald_es_den_Eintrag_gibt()
    {
        var c = fx.As("dora");
        await Json(await c.GetAsync("/api/dms/lists"));
        var a = await WithSuggestion("Rechnung Zylonix 0001.txt", "Zylonix Pro Rechnung vom 24.10.2026", "Zylonix");
        var b = await WithSuggestion("Rechnung Zylonix 0003.txt", "Zylonix Pro Zahlungsbeleg vom 24.10.2026", "zylonix ");
        Assert.Equal("Zylonix", Str(await Doc(c, a), "aiSuggestion"));

        // Über den Knopf „anlegen und zuordnen“ am ersten Dokument: beide haben den Korrespondenten, kein Hinweis mehr
        var adopted = await Json(await c.PostAsync($"/api/dms/docs/{a}/adopt-correspondent", null));
        var zylonix = Str(adopted, "correspondent");
        Assert.NotNull(zylonix);
        Assert.Null(Str(adopted, "aiSuggestion"));
        var other = await Doc(c, b);
        Assert.Equal(zylonix, Str(other, "correspondent"));
        Assert.Null(Str(other, "aiSuggestion"));

        // Über Verwalten angelegt (anders geschrieben): zugeordnet
        var d = await WithSuggestion("Brief Quarkbau.txt", "Quarkbau GmbH Angebot vom 01.10.2026", "Quarkbau GmbH");
        var quarkbau = await Json(await c.PostAsJsonAsync("/api/dms/lists", new { kind = Dms.Dms.Correspondent, name = "quarkbau  gmbh", match = Dms.Dms.Auto }));
        var now = await Doc(c, d);
        Assert.Equal(quarkbau.GetProperty("id").GetString(), Str(now, "correspondent"));
        Assert.Null(Str(now, "aiSuggestion"));

        // Eintrag gab es schon vorher, ohne dass etwas neu einordnete (Stand vor dem Neueinordnen): der Durchlauf im Hintergrund ordnet zu
        var e = await WithSuggestion("Rechnung Zylonix 0004.txt", "Zylonix Pro Rechnung vom 25.10.2026", "ZYLONIX");
        Assert.Equal("ZYLONIX", Str(await Doc(c, e), "aiSuggestion"));
        Assert.Contains(e, Dms.Dms.Reassess(fx.Space("dora").Db));
        now = await Doc(c, e);
        Assert.Equal(zylonix, Str(now, "correspondent"));
        Assert.Null(Str(now, "aiSuggestion"));
    }

    /// <summary>Zähler der Warteschlange wie die Liste: ein Dokument, dessen Datei nicht mehr in der Ablage liegt, zählt nicht mit.</summary>
    [Fact]
    public async Task Warteschlange_zaehlt_wie_die_Liste()
    {
        var c = fx.As("dora");
        await Json(await c.GetAsync("/api/dms/lists"));
        var db = fx.Space("dora").Db;
        await DmsImport.ProcessQueueAsync(fx.Space("dora"), DmsHost.Current!, 100, default);
        var before = (await Json(await c.GetAsync("/api/dms/state"))).GetProperty("queued").GetInt32();
        db.Exec("INSERT INTO dms_docs (fileid, state, imported, original_name, sha256, inbox) VALUES (987654321, 'queued', 0, 'weg.pdf', 'x', 1)");
        try
        {
            var state = await Json(await c.GetAsync("/api/dms/state"));
            var list = await Json(await c.GetAsync("/api/dms/docs?queued=true"));
            Assert.Equal(before, state.GetProperty("queued").GetInt32());
            Assert.Equal(list.GetProperty("total").GetInt32(), state.GetProperty("queued").GetInt32());
        }
        finally { db.Exec("DELETE FROM dms_docs WHERE fileid = 987654321"); }
    }

    /// <summary>„Nach Vorgabe ablegen“ lässt Unbestätigte in Ruhe (die legt „Bestätigen“ ab); Bestätigte kommen nach dem Muster zurück.</summary>
    [Fact]
    public async Task Nach_Vorgabe_ablegen_nur_fuer_Bestaetigte()
    {
        var c = fx.As("dora");
        await Json(await c.GetAsync("/api/dms/lists"));
        var id = await WithSuggestion("Brief Vorgabe 0001.txt", "Vorgabe Brief vom 02.10.2026", "");
        var before = Str(await Doc(c, id), "path");
        Assert.Contains("/.INBOX/", before);
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{id}", new { title = "Ganz neuer Titel" }));
        var inbox = Str(await Doc(c, id), "path");
        await Json(await c.PostAsJsonAsync("/api/dms/docs/refile", new { ids = new[] { id } }));
        Assert.Equal(inbox, Str(await Doc(c, id), "path"));

        // Bestätigt und von Hand umbenannt: kommt nach dem Muster zurück
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{id}", new { confirm = true }));
        var filed = Str(await Doc(c, id), "path")!;
        Assert.DoesNotContain("/.INBOX/", filed);
        var moved = filed[..(filed.LastIndexOf('/') + 1)] + "von Hand.txt";
        var space = fx.Space("dora");
        Assert.True(DmOffice.Dav.FileStore.MoveOrCopy(space.Db, filed, moved, false, false));
        Assert.Equal(moved, Str(await Doc(c, id), "path"));
        await Json(await c.PostAsJsonAsync("/api/dms/docs/refile", new { ids = new[] { id } }));
        Assert.Equal(filed, Str(await Doc(c, id), "path"));
    }
}
