using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Notes;

namespace DmOffice.Tests;

/// <summary>Frühere Versionen von Notizen (notesversions): gebündelt statt bei jedem Speichern, Grenze je Notiz, Wiederherstellen,
/// Abgleich unberührt. In der Server-Sammlung, weil ein Test die Kennungen umschaltet.</summary>
[Collection("server")]
public class NoteVersionsTests
{
    private static (NotesDb Notes, UserDb Db) NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("dmnotever");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        return (NotesDb.Of(new UserSpace(new UserRecord { Login = "t" }, db)), db);
    }

    private static NoteVersions.State S(string text, DateTime at) => new($"<p>{text}</p>", text, text, at);

    [Fact]
    public void Gebuendelt_nach_Zeit_Quelle_und_Groesse()
    {
        var (_, db) = NewDb();
        var web = new NoteVersions.Source("web", "Chrome · Windows", "Anna");
        var t0 = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        void Change(string from, string to, DateTime now, NoteVersions.Source? by = null) =>
            NoteVersions.BeforeChange(db, "n1", S(from, now), $"<p>{to}</p>", to, by ?? web, now);

        // Leerer Stand (frisch angelegt): keine Version
        Change("", "A", t0);
        Assert.Empty(NoteVersions.List(db, "n1"));
        // Erste Änderung danach: Version; weiteres Tippen innerhalb von 10 Minuten: keine
        Change("A", "AB", t0.AddSeconds(5));
        Change("AB", "ABC", t0.AddMinutes(3));
        Change("ABC", "ABCD", t0.AddMinutes(9));
        Assert.Single(NoteVersions.List(db, "n1"));
        // Nach 10 Minuten wieder eine
        Change("ABCD", "ABCDE", t0.AddMinutes(16));
        Assert.Equal(2, NoteVersions.List(db, "n1").Count);
        Assert.Equal("ABCD", NoteVersions.List(db, "n1")[0].Title);
        // Anderes Gerät: der Stand aus dem Browser bleibt, auch wenn es kurz danach ist
        Change("ABCDE", "ABCDEF", t0.AddMinutes(17), new NoteVersions.Source("notizi", "Pixel"));
        var list = NoteVersions.List(db, "n1");
        Assert.Equal(3, list.Count);
        Assert.Equal("web", list[0].Source);
        Assert.Equal("Anna", list[0].Author);
        // Viel auf einmal gelöscht: Version, auch innerhalb von 10 Minuten
        var big = "ABCDEF " + new string('x', 300);
        Change("ABCDEF", big, t0.AddMinutes(18), new NoteVersions.Source("notizi", "Pixel"));
        Assert.Equal(4, NoteVersions.List(db, "n1").Count);
        Change(big, "ABCDEF", t0.AddMinutes(19), new NoteVersions.Source("notizi", "Pixel"));
        Assert.Equal(5, NoteVersions.List(db, "n1").Count);
        // Kleine Änderung kurz danach von derselben Quelle: keine
        Change("ABCDEF", "ABCDEFG", t0.AddMinutes(20), new NoteVersions.Source("notizi", "Pixel"));
        Assert.Equal(5, NoteVersions.List(db, "n1").Count);
        Assert.Equal("notizi", NoteVersions.List(db, "n1")[0].Source);
        Assert.Equal("Pixel", NoteVersions.List(db, "n1")[0].Device);
    }

    [Fact]
    public void Grenze_je_Notiz_und_Alter()
    {
        var (_, db) = NewDb();
        var by = new NoteVersions.Source("web");
        var t0 = DateTime.UtcNow.AddDays(-200);
        for (var i = 0; i < 60; i++)
            NoteVersions.BeforeChange(db, "n1", S("Stand " + i, t0), "<p>Stand " + (i + 1) + "</p>", "Stand " + (i + 1), by, t0.AddMinutes(11 * i));
        Assert.Equal(NoteVersions.Keep, NoteVersions.List(db, "n1").Count);
        Assert.Equal("Stand 59", NoteVersions.List(db, "n1")[0].Title);
        // Älter als 90 Tage: weg, die letzten fünf bleiben
        NoteVersions.PruneAll(db, DateTime.UtcNow);
        Assert.Equal(NoteVersions.KeepAlways, NoteVersions.List(db, "n1").Count);
        Assert.True(NoteVersions.Big("a" + new string('x', 200), "a"));
        Assert.False(NoteVersions.Big(new string('x', 1000) + "a", new string('x', 1000) + "ab"));
    }

    [Fact]
    public void Speichern_Wiederherstellen_und_Abgleich()
    {
        var (notes, db) = NewDb();
        var id = notes.CreateNote(null);
        var web = new NoteVersions.Source("web", "Firefox · Linux", "Anna");
        notes.SaveNote(id, "<p>Einkauf</p>", "Einkauf", web);
        notes.SaveNote(id, "<p>Einkauf Milch</p>", "Einkauf Milch", web);
        Assert.Single(notes.Versions(id));
        var syncId = notes.GetNote(id)!.SyncId;

        // Wiederherstellen: der jetzige Stand bleibt als Version, neue Revision für den Abgleich
        var rev = notes.RevOf(syncId, "note");
        var v = notes.Versions(id)[0];
        Assert.Equal("Einkauf", notes.Version(id, v.Id)!.Plain);
        Assert.True(notes.RestoreVersion(id, v.Id, web));
        Assert.Equal("<p>Einkauf</p>", notes.GetNote(id)!.Html);
        Assert.Equal(rev + 1, notes.RevOf(syncId, "note"));
        Assert.Equal(2, notes.Versions(id).Count);
        Assert.Equal("Einkauf Milch", notes.Versions(id)[0].Title);
        Assert.False(notes.RestoreVersion(id, 999999, web));

        // Vom Gerät (notizi): der Stand hier wird Version, die Revision ist die des Geräts - die Versionen ändern nichts daran
        var item = notes.ExportNote(syncId, null!);
        item.Rev = 40; item.Html = "<p>Einkauf Brot</p>"; item.Plain = "Einkauf Brot"; item.Device = "Pixel";
        Assert.True(notes.ApplyNote(item, null!));
        Assert.Equal(40, notes.RevOf(syncId, "note"));
        Assert.Equal(3, notes.Versions(id).Count);
        Assert.Equal("Firefox · Linux", notes.Versions(id)[0].Device);
        // Danach im Browser geändert: der Stand vom Gerät bleibt als Version, mit dem Gerätenamen
        notes.SaveNote(id, "<p>Einkauf Brot Butter</p>", "Einkauf Brot Butter", web);
        Assert.Equal(4, notes.Versions(id).Count);
        Assert.Equal(("notizi", "Pixel"), (notes.Versions(id)[0].Source, notes.Versions(id)[0].Device));

        // Endgültig gelöscht: Versionen weg
        notes.DeleteNote(id);
        Assert.Empty(NoteVersions.List(db, syncId));
    }

    [Fact]
    public void Ohne_Kennung_keine_Versionen()
    {
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set(before.Append(Features.NotesVersions));
            var (notes, db) = NewDb();
            var id = notes.CreateNote(null);
            notes.SaveNote(id, "<p>Eins</p>", "Eins");
            notes.SaveNote(id, "<p>Zwei</p>", "Zwei");
            Assert.Equal("<p>Zwei</p>", notes.GetNote(id)!.Html);
            Assert.Null(db.Scalar("SELECT name FROM sqlite_master WHERE name='note_versions'"));
        }
        finally { Features.Set(before); }
    }
}

/// <summary>Versionen geteilter Notizen über die API: beim Besitzer, Mitglieder mit Schreibrecht sehen und stellen wieder her.</summary>
[Collection("server")]
public class NoteVersionsApiTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public NoteVersionsApiTests(SharedCalendarFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Save(HttpClient c, long id, string text) =>
        c.PutAsync($"/api/notes/{id}/content", new StringContent(JsonSerializer.Serialize(new { blocks = new[] { new { html = $"<p>{text}</p>" } }, plain = text }),
            Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Geteilte_Notiz_Versionen_beim_Besitzer()
    {
        var anna = _fx.As("anna");
        var folder = (await Json(await anna.PostAsJsonAsync("/api/notes/folders", new { name = "Versionsprobe" }))).GetProperty("id").GetString()!;
        var id = (await Json(await anna.PostAsJsonAsync("/api/notes", new { folder }))).GetProperty("id").GetInt64();
        await Json(await Save(anna, id, "Plan Anfang"));
        await Json(await anna.PutAsJsonAsync($"/api/notes/shares/{folder}",
            new { members = new[] { _fx.Users["ben"].Id, _fx.Users["cleo"].Id }, readOnly = new[] { _fx.Users["ben"].Id } }));

        // Cleo (Schreibrecht) ändert: Annas Stand wird Version (andere Person), mit Namen
        var wire = (await Json(await _fx.As("cleo").GetAsync($"/api/notes?folder={folder}"))).GetProperty("items")[0].GetProperty("id").GetInt64();
        var cleo = _fx.As("cleo");
        await Json(await Save(cleo, wire, "Plan geändert"));
        var list = await Json(await cleo.GetAsync($"/api/notes/{wire}/versions"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Anna", list[0].GetProperty("author").GetString());
        var vid = list[0].GetProperty("id").GetInt64();
        var one = await Json(await cleo.GetAsync($"/api/notes/{wire}/versions/{vid}"));
        Assert.Contains("Plan Anfang", one.GetProperty("blocks")[0].GetString());

        // Ben (nur lesen): weder sehen noch wiederherstellen
        var ben = _fx.As("ben");
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/api/notes/{wire}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.PostAsync($"/api/notes/{wire}/versions/{vid}/restore", null)).StatusCode);

        // Cleo stellt wieder her; die Versionen liegen bei Anna
        await Json(await cleo.PostAsync($"/api/notes/{wire}/versions/{vid}/restore", null));
        var note = await Json(await anna.GetAsync($"/api/notes/{id}"));
        Assert.Contains("Plan Anfang", note.GetProperty("blocks")[0].GetString());
        var annas = await Json(await anna.GetAsync($"/api/notes/{id}/versions"));
        Assert.Equal(2, annas.GetArrayLength());
        Assert.Equal("Cleo", annas[0].GetProperty("author").GetString());
    }
}
