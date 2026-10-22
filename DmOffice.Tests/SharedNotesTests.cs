using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Notes;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Geteilte Notizordner: Lesen/Bearbeiten, Unterordner, Bilder, Widerruf, Suche, Abgleich des Besitzers.</summary>
[Collection("server")]
public class SharedNotesTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public SharedNotesTests(SharedCalendarFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<string> NewFolder(string login, string name, string? parent = null)
    {
        var r = await _fx.As(login).PostAsJsonAsync("/api/notes/folders", new { name, parent });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    private async Task<long> NewNote(string login, string? folder, string text)
    {
        var c = _fx.As(login);
        var r = await c.PostAsJsonAsync("/api/notes", new { folder });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await Json(r)).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Save(c, id, text)).StatusCode);
        return id;
    }

    private static async Task<HttpResponseMessage> Save(HttpClient c, long id, string text, string? version = null)
    {
        var body = JsonSerializer.Serialize(new { blocks = new[] { new { html = $"<p>{text}</p>" } }, plain = text, version });
        return await c.PutAsync($"/api/notes/{id}/content", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private async Task Share(string folder, string[] members, string[]? readOnly = null)
    {
        var body = new { members = members.Select(m => _fx.Users[m].Id).ToArray(), readOnly = (readOnly ?? Array.Empty<string>()).Select(m => _fx.Users[m].Id).ToArray() };
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("anna").PutAsJsonAsync($"/api/notes/shares/{folder}", body)).StatusCode);
    }

    private async Task<JsonElement> Items(string login, string folder)
    {
        var r = await _fx.As(login).GetAsync($"/api/notes?folder={folder}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("items");
    }

    private async Task<JsonElement> Shared(string login) => await Json(await _fx.As(login).GetAsync("/api/notes/shared"));

    private NotesDb AnnasDb() => NotesDb.Of(_fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id));

    [Fact]
    public async Task Lesen_und_Bearbeiten_mit_Unterordnern_und_Widerruf()
    {
        var rezepte = await NewFolder("anna", "Rezepte");
        var suppen = await NewFolder("anna", "Suppen", rezepte);
        var privat = await NewFolder("anna", "Privat");
        var n1 = await NewNote("anna", rezepte, "Pfannkuchen");
        await NewNote("anna", suppen, "Kuerbissuppe");
        var geheim = await NewNote("anna", privat, "Tagebuch");
        await Share(rezepte, new[] { "ben", "cleo" }, new[] { "ben" });

        // Sichtbar: der Ordner samt Unterordner bei Ben und Cleo, bei Dora nichts
        var bens = (await Shared("ben")).EnumerateArray().Single();
        Assert.Equal("Anna", bens.GetProperty("owner").GetString());
        Assert.False(bens.GetProperty("canWrite").GetBoolean());
        Assert.Equal(2, bens.GetProperty("folders").GetArrayLength());
        Assert.True((await Shared("cleo")).EnumerateArray().Single().GetProperty("canWrite").GetBoolean());
        Assert.Empty((await Shared("dora")).EnumerateArray());

        // Ben liest Ordner, Unterordner und Notiz; die Kennung ist negativ (kollidiert nicht mit eigenen)
        var item = (await Items("ben", rezepte)).EnumerateArray().Single();
        Assert.Equal("Pfannkuchen", item.GetProperty("title").GetString());
        var wire = item.GetProperty("id").GetInt64();
        Assert.True(wire < 0);
        Assert.Single((await Items("ben", suppen)).EnumerateArray());
        var ben = _fx.As("ben");
        var detail = await Json(await ben.GetAsync($"/api/notes/{wire}"));
        Assert.True(detail.GetProperty("readOnly").GetBoolean());
        Assert.Equal("Anna", detail.GetProperty("owner").GetString());

        // Lesen heisst: nichts aendern
        Assert.Equal(HttpStatusCode.Forbidden, (await Save(ben, wire, "gehackt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PutAsJsonAsync($"/api/notes/{wire}/meta", new { color = "#FFFFFF" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PostAsync($"/api/notes/{wire}/trash", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.DeleteAsync($"/api/notes/{wire}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PostAsJsonAsync("/api/notes", new { folder = rezepte })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PostAsJsonAsync("/api/notes/folders", new { name = "x", parent = rezepte })).StatusCode);

        // Nichts ausserhalb der Freigabe: die private Notiz von Anna, direkt oder geraten
        var share = bens.GetProperty("share").GetInt64();
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/notes/{-(share * (1L << 32) + geheim)}")).StatusCode);
        Assert.Empty((await Items("ben", privat)).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").GetAsync($"/api/notes/{wire}")).StatusCode);
        Assert.Empty((await Items("dora", rezepte)).EnumerateArray());

        // Cleo bearbeitet: der Text steht in Annas Datenbank, ihr Abgleich merkt es
        var cleo = _fx.As("cleo");
        var cw = (await Items("cleo", rezepte)).EnumerateArray().Single().GetProperty("id").GetInt64();
        var v = (await Json(await cleo.GetAsync($"/api/notes/{cw}"))).GetProperty("version").GetString();
        Assert.Equal(HttpStatusCode.OK, (await Save(cleo, cw, "Pfannkuchen mit Zimt", v)).StatusCode);
        Assert.Contains("Zimt", AnnasDb().GetNote(n1)!.Plain);
        Assert.True(AnnasDb().IsPending(n1));
        Assert.Equal("Pfannkuchen mit Zimt", (await Json(await _fx.As("anna").GetAsync($"/api/notes/{n1}"))).GetProperty("title").GetString());

        // Gleichzeitig: veralteter Stand -> 409, bei Cleo wie bei Anna
        Assert.Equal(HttpStatusCode.Conflict, (await Save(cleo, cw, "noch einer", v)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Save(_fx.As("anna"), n1, "Annas Sicht", v)).StatusCode);

        // Cleo legt an (auch im Unterordner), verschiebt innerhalb der Freigabe, nicht heraus
        var neu = (await Json(await cleo.PostAsJsonAsync("/api/notes", new { folder = suppen }))).GetProperty("id").GetInt64();
        Assert.True(neu < 0);
        Assert.Equal(HttpStatusCode.OK, (await Save(cleo, neu, "Linsensuppe")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cleo.PutAsJsonAsync($"/api/notes/{neu}/meta", new { folder = rezepte })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cleo.PutAsJsonAsync($"/api/notes/{neu}/meta", new { folder = privat })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cleo.PutAsJsonAsync($"/api/notes/{neu}/meta", new { folder = "" })).StatusCode);
        Assert.Equal(2, (await Items("anna", rezepte)).GetArrayLength());
        // Unterordner anlegen darf sie
        var sub = (await Json(await cleo.PostAsJsonAsync("/api/notes/folders", new { name = "Salate", parent = rezepte }))).GetProperty("id").GetString()!;
        Assert.Contains(AnnasDb().AllFolders(), f => f.SyncId == sub && f.Parent == rezepte);
        // In den Papierkorb darf Cleo, endgueltig loeschen und archivieren nicht; Ben sieht die Notiz dann nicht mehr
        Assert.Equal(HttpStatusCode.OK, (await cleo.PostAsync($"/api/notes/{neu}/trash", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cleo.DeleteAsync($"/api/notes/{neu}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/notes/{neu}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cleo.PostAsync($"/api/notes/{cw}/archive", null)).StatusCode);

        // Eigene Notiz in den geteilten Ordner: wird dorthin kopiert (Besitzer sieht sie), das Original ist weg
        var eigene = await NewNote("cleo", null, "Meine Linsen");
        var cleoDb = NotesDb.Of(_fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["cleo"].Id));
        var mv = await cleo.PutAsJsonAsync($"/api/notes/{eigene}/meta", new { folder = rezepte });
        Assert.Equal(HttpStatusCode.OK, mv.StatusCode);
        Assert.True((await Json(mv)).GetProperty("id").GetInt64() < 0);
        Assert.Null(cleoDb.GetNote(eigene));
        Assert.Contains(cleoDb.Tombstones(), t => t.Kind == "note");
        Assert.Contains("Meine Linsen", (await Items("anna", rezepte)).EnumerateArray().Select(i => i.GetProperty("title").GetString()));
        // Nur lesend geteilt: nicht
        var eigene2 = await NewNote("ben", null, "Bens");
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PutAsJsonAsync($"/api/notes/{eigene2}/meta", new { folder = rezepte })).StatusCode);
        Assert.NotNull(NotesDb.Of(_fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["ben"].Id)).GetNote(eigene2));
        // Geteilte Notizen gehen nie in den Abgleich (notizi) eines Empfaengers - nur in den des Besitzers
        var annaIds = AnnasDb().GetAllActive().Select(x => x.SyncId).ToHashSet();
        var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
        foreach (var u in new[] { "ben", "cleo" })
            Assert.DoesNotContain(NotesDb.Of(spaces.Get(_fx.Users[u].Id)).Entries(), e => annaIds.Contains(e.Id));
        Assert.Contains(AnnasDb().Entries(), e => e.Kind == "note" && AnnasDb().GetAllActive().Any(x => x.SyncId == e.Id && x.Title == "Meine Linsen"));

        // Widerruf wirkt sofort
        await Share(rezepte, new[] { "cleo" });
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/notes/{wire}")).StatusCode);
        Assert.Empty((await Shared("ben")).EnumerateArray());
        Assert.Empty((await Items("ben", rezepte)).EnumerateArray());
        // ... und ganz
        await Share(rezepte, Array.Empty<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await cleo.GetAsync($"/api/notes/{cw}")).StatusCode);
        Assert.Empty((await Shared("cleo")).EnumerateArray());
    }

    [Fact]
    public async Task Bilder_und_Anhaenge_nur_solange_die_Notiz_im_geteilten_Ordner_liegt()
    {
        var f = await NewFolder("anna", "Fotos");
        var other = await NewFolder("anna", "Nicht geteilt");
        var inside = await NewNote("anna", f, "mit Anhang");
        var outside = await NewNote("anna", other, "ohne Freigabe");
        var anna = _fx.As("anna");
        string Attach(long note, string text)
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "files", text + ".txt" } };
            Assert.Equal(HttpStatusCode.OK, anna.PostAsync($"/api/notes/{note}/attachments", form).Result.StatusCode);
            return AnnasDb().GetNote(note)!.Attachments.Single().Blob;
        }
        var blobIn = Attach(inside, "innen");
        var blobOut = Attach(outside, "aussen");
        await Share(f, new[] { "ben" }, new[] { "ben" });

        var ben = _fx.As("ben");
        Assert.Equal(HttpStatusCode.OK, (await ben.GetAsync($"/api/notes/blob/{blobIn}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/notes/blob/{blobOut}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("dora").GetAsync($"/api/notes/blob/{blobIn}")).StatusCode);

        // Notiz aus dem Ordner genommen (Besitzer): der Anhang ist nicht mehr lesbar
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync($"/api/notes/{inside}/meta", new { folder = other })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/notes/blob/{blobIn}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsJsonAsync($"/api/notes/{inside}/meta", new { folder = f })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ben.GetAsync($"/api/notes/blob/{blobIn}")).StatusCode);

        // Widerruf
        await Share(f, Array.Empty<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/notes/blob/{blobIn}")).StatusCode);
    }

    [Fact]
    public async Task Bild_eines_Bearbeiters_landet_beim_Besitzer()
    {
        var f = await NewFolder("anna", "Bilderordner");
        await NewNote("anna", f, "Bildnotiz");
        await Share(f, new[] { "cleo" });
        var cleo = _fx.As("cleo");
        var wire = (await Items("cleo", f)).EnumerateArray().Single().GetProperty("id").GetInt64();

        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 }), "file", "b.png" } };
        var up = await cleo.PostAsync($"/api/notes/images?note={wire}", form);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var name = (await Json(up)).GetProperty("name").GetString()!;
        Assert.NotNull(AnnasDb().GetBlob(name));
        // Ohne Bearbeitungsrecht nicht
        await Share(f, new[] { "cleo" }, new[] { "cleo" });
        form = new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 9, 9 }), "file", "c.png" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await cleo.PostAsync($"/api/notes/images?note={wire}", form)).StatusCode);
    }

    [Fact]
    public async Task Suche_des_Empfaengers_findet_geteilte_Ordner_und_nur_die()
    {
        var f = await NewFolder("anna", "Suche");
        var g = await NewFolder("anna", "Suche privat");
        await NewNote("anna", f, "Zimtschnecken Rezept");
        await NewNote("anna", g, "Zimtsterne privat");
        await NewNote("ben", null, "Zimtkaffee eigene");
        await Share(f, new[] { "ben" });

        var r = await Json(await _fx.As("ben").GetAsync("/api/notes?q=Zimt"));
        var titles = r.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
        Assert.Contains("Zimtkaffee eigene", titles);
        Assert.Contains("Zimtschnecken Rezept", titles);
        Assert.DoesNotContain("Zimtsterne privat", titles);
        var shared = r.GetProperty("items").EnumerateArray().First(i => i.GetProperty("title").GetString() == "Zimtschnecken Rezept");
        Assert.Equal("Anna", shared.GetProperty("owner").GetString());

        await Share(f, Array.Empty<string>());
        r = await Json(await _fx.As("ben").GetAsync("/api/notes?q=Zimt"));
        Assert.DoesNotContain("Zimtschnecken Rezept", r.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Aussteigen_und_Loeschen_des_Ordners_raeumt_die_Freigabe_auf()
    {
        var f = await NewFolder("anna", "Weg");
        await Share(f, new[] { "ben" });
        var share = (await Shared("ben")).EnumerateArray().Single().GetProperty("share").GetInt32();
        // Fremde koennen nicht fuer andere aussteigen
        await _fx.As("dora").PostAsync($"/api/notes/shares/{share}/leave", null);
        Assert.Single((await Shared("ben")).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("ben").PostAsync($"/api/notes/shares/{share}/leave", null)).StatusCode);
        Assert.Empty((await Shared("ben")).EnumerateArray());

        await Share(f, new[] { "ben" });
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("anna").DeleteAsync($"/api/notes/folders/{f}")).StatusCode);
        Assert.Empty((await Shared("ben")).EnumerateArray());
        // Nur eigene Ordner lassen sich teilen
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").PutAsJsonAsync($"/api/notes/shares/{f}", new { members = new[] { _fx.Users["cleo"].Id } })).StatusCode);
    }

    [Fact]
    public async Task Eigene_Ordnerliste_nennt_die_Empfaenger()
    {
        var f = await NewFolder("anna", "Mit Liste");
        await Share(f, new[] { "ben", "cleo" }, new[] { "ben" });
        var folders = await Json(await _fx.As("anna").GetAsync("/api/notes/folders"));
        var mine = folders.EnumerateArray().Single(x => x.GetProperty("syncId").GetString() == f).GetProperty("sharedWith");
        Assert.Equal(2, mine.GetArrayLength());
        Assert.True(mine.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "Ben").GetProperty("readOnly").GetBoolean());
    }
}
