using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Journal;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Tagebücher im Tresor (docs/tresor-konzept.md, „Tagebücher im Tresor“): anlegen, verschieben hin und zurück,
/// Freigaben werden entfernt, geschlossen 423, Suche und Pukki sehen nichts davon.</summary>
[Collection("server")]
public class JournalTresorTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public JournalTresorTests(ChatFixture fx)
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

    private static byte[] Png()
    {
        using var bmp = new SKBitmap(300, 200);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Teal);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 90).ToArray();
    }

    private static async Task<JsonElement> Photo(HttpClient c, string name, long journal)
    {
        var data = Png();
        var id = (await Json(await c.PostAsJsonAsync("/api/files/uploads", new { name, size = data.Length, mime = "image/png" }))).GetProperty("id").GetString();
        await Json(await c.PutAsync($"/api/files/uploads/{id}?offset=0", new ByteArrayContent(data)));
        return (await Json(await c.PostAsync($"/api/journal/uploads/{id}/attach?journal={journal}", null))).GetProperty("media");
    }

    private static async Task Entry(HttpClient c, long journal, string text, JsonElement media) =>
        await Json(await c.PostAsJsonAsync("/api/journal/entries", new
        {
            journal, date = "2030-05-01T10:00:00Z", title = "Tag", html = $"<p>{text}</p>", media = new[] { JsonSerializer.Deserialize<object>(media.GetRawText()) },
        }));

    private static async Task<List<JsonElement>> Journals(HttpClient c) => (await Json(await c.GetAsync("/api/journal/journals"))).EnumerateArray().ToList();

    [Fact]
    public async Task Tagebuch_im_Tresor_anlegen_verschieben_und_geschlossen()
    {
        var id = _fx.Id("dora");
        Tresor.Delete(id);
        var c = _fx.As("dora");
        var space = _fx.Space("dora");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1", name = "Testhandy" }));

        // Offenes Tagebuch mit Foto, mit Ben geteilt
        var open = (await Json(await c.PostAsJsonAsync("/api/journal/journals", new { name = "Reise" }))).GetProperty("id").GetInt64();
        var photo = await Photo(c, "strand.png", open);
        var openPath = photo.GetProperty("path").GetString()!;
        Assert.StartsWith(JournalMediaStore.Root + "/", openPath);
        await Entry(c, open, "Geheimwort Zebra", photo);
        (await c.PutAsJsonAsync($"/api/pim/shares/{open}", new { members = new[] { _fx.Id("ben") } })).EnsureSuccessStatusCode();
        Assert.Contains(await Journals(_fx.As("ben")), j => j.GetProperty("name").GetString() == "Reise");

        // Geteilt: ohne Bestätigung nicht, mit Bestätigung sind die Freigaben weg
        var refused = await c.PostAsJsonAsync($"/api/journal/journals/{open}/move", new { to = "tresor" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("nicht geteilt werden", await refused.Content.ReadAsStringAsync());
        var vault = (await Json(await c.PostAsJsonAsync($"/api/journal/journals/{open}/move", new { to = "tresor", unshare = true }))).GetProperty("id").GetInt64();
        Assert.True(TresorJournals.Is(vault));
        Assert.DoesNotContain(await Journals(_fx.As("ben")), j => j.GetProperty("name").GetString() == "Reise");

        // Im Tresor: Eintrag und Foto da, im offenen Bereich nichts mehr (auch nicht im Papierkorb)
        var listed = (await Journals(c)).Single(j => j.GetProperty("id").GetInt64() == vault);
        Assert.True(listed.GetProperty("tresor").GetBoolean());
        Assert.Equal(1, listed.GetProperty("count").GetInt32());
        var entries = (await Json(await c.GetAsync($"/api/journal/entries?journal={vault}"))).EnumerateArray().ToList();
        Assert.Single(entries);
        var vaultPath = entries[0].GetProperty("media")[0].GetProperty("path").GetString()!;
        Assert.NotEmpty(await (await c.GetAsync($"/api/journal/file?journal={vault}&path={Uri.EscapeDataString(vaultPath)}")).Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/journal/file?journal={vault}&path={Uri.EscapeDataString(vaultPath)}&size=256")).StatusCode);
        Assert.Null(FileStore.Get(space.Db, openPath));
        Assert.Null(space.Db.Scalar("SELECT 1 FROM file_trash WHERE path = $p", ("$p", openPath)));
        Assert.DoesNotContain(JournalStore.Journals(space.Db), j => j.Name == "Reise");

        // Suche über alle Tagebücher, Schlagwörter und Pukki sehen nichts aus dem Tresor; teilen geht nicht
        Assert.Empty((await Json(await c.GetAsync("/api/journal/entries?q=Zebra"))).EnumerateArray());
        Assert.DoesNotContain(JournalStore.Journals(space.Db).SelectMany(j => JournalStore.Entries(space.Db, j)), e => e.Text.Contains("Zebra"));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/pim/shares/{vault}", new { members = new[] { _fx.Id("ben") } })).StatusCode);

        // Gleich im Tresor anlegen: Foto kommt nicht in die Dateiablage
        var secret = (await Json(await c.PostAsJsonAsync("/api/journal/journals", new { name = "Geheim", tresor = true }))).GetProperty("id").GetInt64();
        Assert.True(TresorJournals.Is(secret));
        var secretPhoto = await Photo(c, "geheim.png", secret);
        Assert.Null(FileStore.Get(space.Db, secretPhoto.GetProperty("path").GetString()!));
        await Entry(c, secret, "Nur im Tresor", secretPhoto);
        Assert.Single((await Json(await c.GetAsync($"/api/journal/entries?journal={secret}"))).EnumerateArray());

        // Geschlossen: 423, die Liste zeigt nur den offenen Bereich
        await Json(await c.PostAsync("/api/tresor/close", null));
        var locked = await c.GetAsync($"/api/journal/entries?journal={vault}");
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        Assert.Contains("\"locked\":\"tresor\"", await locked.Content.ReadAsStringAsync());
        Assert.Equal((HttpStatusCode)423, (await c.PostAsJsonAsync("/api/journal/journals", new { name = "Noch eins", tresor = true })).StatusCode);
        Assert.DoesNotContain(await Journals(c), j => j.GetProperty("tresor").GetBoolean());
        await Json(await c.GetAsync("/api/journal/entries"));

        // Neuer Eintrag mit Foto bei geschlossenem Tresor: abgelegt, beim Öffnen übernommen; ohne bestimmtes Tagebuch (Base)
        // ins zuletzt beschriebene; bestehende Einträge ändern geht nicht
        var dark = await Photo(c, "abends.png", secret);
        Assert.StartsWith(TresorJournals.DropPrefix, dark.GetProperty("path").GetString()!);
        Assert.True((await Json(await c.PostAsJsonAsync("/api/journal/entries", new
        {
            journal = secret, date = "2030-05-02T20:00:00Z", html = "<p>Im Dunkeln</p>", media = new[] { JsonSerializer.Deserialize<object>(dark.GetRawText()) },
        }))).GetProperty("dropped").GetBoolean());
        await Json(await c.PostAsJsonAsync("/api/journal/entries", new { journal = TresorJournals.Base, date = "2030-05-03T20:00:00Z", html = "<p>Ohne Ziel</p>" }));
        Assert.Equal((HttpStatusCode)423, (await c.PostAsJsonAsync("/api/journal/entries", new { journal = secret, uri = "x.ics", date = "2030-05-03T20:00:00Z", html = "" })).StatusCode);
        Assert.Equal(3, Tresor.Pending(id));
        await Json(await c.PostAsJsonAsync("/api/tresor/open", new { password = "tresor-pass-1" }));
        Assert.Equal(0, Tresor.Pending(id));
        var taken = (await Json(await c.GetAsync($"/api/journal/entries?journal={secret}"))).EnumerateArray().ToList();
        Assert.Equal(3, taken.Count);
        var darkPath = taken.Single(e => e.GetProperty("snippet").GetString() == "Im Dunkeln").GetProperty("media")[0].GetProperty("path").GetString()!;
        Assert.StartsWith(JournalMediaStore.Root + "/", darkPath);
        Assert.NotEmpty(await (await c.GetAsync($"/api/journal/file?journal={secret}&path={Uri.EscapeDataString(darkPath)}")).Content.ReadAsByteArrayAsync());
        Assert.Contains(taken, e => e.GetProperty("snippet").GetString() == "Ohne Ziel");

        // Aus dem Tresor holen: wieder im offenen Bereich mit Foto im Systemordner, im Tresor weg
        var back = (await Json(await c.PostAsJsonAsync($"/api/journal/journals/{vault}/move", new { to = "open" }))).GetProperty("id").GetInt64();
        Assert.False(TresorJournals.Is(back));
        var again = (await Json(await c.GetAsync($"/api/journal/entries?journal={back}"))).EnumerateArray().Single();
        var backPath = again.GetProperty("media")[0].GetProperty("path").GetString()!;
        Assert.StartsWith(JournalMediaStore.Root + "/", backPath);
        Assert.NotNull(FileStore.Get(space.Db, backPath));
        Assert.Single((await Json(await c.GetAsync("/api/journal/entries?q=Zebra"))).EnumerateArray());
        Assert.DoesNotContain(await Journals(c), j => j.GetProperty("id").GetInt64() == vault);
        Assert.Null(TresorJournals.Media(Tresor.DbOf(id)!, vaultPath));
        Tresor.Delete(id);
    }

    [Fact]
    public async Task Eintraege_zwischen_Tagebuechern_und_in_den_Tresor_verschieben()
    {
        var id = _fx.Id("anna");
        Tresor.Delete(id);
        var c = _fx.As("anna");
        var space = _fx.Space("anna");
        await Json(await c.PostAsJsonAsync("/api/tresor/setup", new { password = "tresor-pass-1", name = "Testhandy" }));
        var a = (await Json(await c.PostAsJsonAsync("/api/journal/journals", new { name = "Alltag" }))).GetProperty("id").GetInt64();
        var b = (await Json(await c.PostAsJsonAsync("/api/journal/journals", new { name = "Urlaub" }))).GetProperty("id").GetInt64();
        var vault = (await Json(await c.PostAsJsonAsync("/api/journal/journals", new { name = "Privat", tresor = true }))).GetProperty("id").GetInt64();
        var photo = await Photo(c, "see.png", a);
        await Entry(c, a, "Erster", photo);
        await Json(await c.PostAsJsonAsync("/api/journal/entries", new { journal = a, date = "2030-05-02T10:00:00Z", html = "<p>Zweiter</p>" }));
        var ids = (await Json(await c.GetAsync($"/api/journal/entries?journal={a}"))).EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();
        Assert.Equal(2, ids.Count);

        // Mehrere im offenen Bereich: Foto bleibt, wo es ist
        var moved = (await Json(await c.PostAsJsonAsync("/api/journal/entries/move", new { ids, to = b }))).EnumerateArray().ToList();
        Assert.Equal(2, moved.Count);
        Assert.Empty((await Json(await c.GetAsync($"/api/journal/entries?journal={a}"))).EnumerateArray());
        var first = moved.Single(e => e.GetProperty("text").GetString()!.Contains("Erster"));
        var path = first.GetProperty("media")[0].GetProperty("path").GetString()!;
        Assert.NotNull(FileStore.Get(space.Db, path));

        // Einer in den Tresor: Foto kommt mit und ist im offenen Bereich weg; wieder heraus ins Tagebuch Alltag
        var inVault = (await Json(await c.PostAsJsonAsync("/api/journal/entries/move", new { ids = new[] { first.GetProperty("id").GetString() }, to = vault }))).EnumerateArray().Single();
        Assert.Equal(vault, inVault.GetProperty("journal").GetInt64());
        Assert.Null(FileStore.Get(space.Db, path));
        Assert.Single((await Json(await c.GetAsync($"/api/journal/entries?journal={b}"))).EnumerateArray());
        var back = (await Json(await c.PostAsJsonAsync("/api/journal/entries/move", new { ids = new[] { inVault.GetProperty("id").GetString() }, to = a }))).EnumerateArray().Single();
        Assert.NotNull(FileStore.Get(space.Db, back.GetProperty("media")[0].GetProperty("path").GetString()!));
        Assert.Empty((await Json(await c.GetAsync($"/api/journal/entries?journal={vault}"))).EnumerateArray());

        // Fremde Tagebücher nicht; geschlossener Tresor als Ziel: 423
        var bensOwn = (await Json(await _fx.As("ben").GetAsync("/api/journal/journals"))).EnumerateArray().First(j => j.GetProperty("owner").ValueKind == JsonValueKind.Null).GetProperty("id").GetInt64();
        (await _fx.As("ben").PutAsJsonAsync($"/api/pim/shares/{bensOwn}", new { members = new[] { id } })).EnsureSuccessStatusCode();
        var bens = (await Journals(c)).First(j => j.GetProperty("owner").ValueKind != JsonValueKind.Null).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Forbidden,(await c.PostAsJsonAsync("/api/journal/entries/move", new { ids = new[] { back.GetProperty("id").GetString() }, to = bens })).StatusCode);
        await Json(await c.PostAsync("/api/tresor/close", null));
        Assert.Equal((HttpStatusCode)423, (await c.PostAsJsonAsync("/api/journal/entries/move", new { ids = new[] { back.GetProperty("id").GetString() }, to = vault })).StatusCode);
        Tresor.Delete(id);
    }
}
