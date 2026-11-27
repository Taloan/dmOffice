using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Dms;
using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Einheitliches Rückgängig (Kennung undoall): Tagebuch-Eintrag, DMS-Dokument, Mails zurückverschieben.</summary>
[Collection("server")]
public class UndoAllTests : IClassFixture<ChatFixture>, IDisposable
{
    private readonly ChatFixture _fx;
    private readonly List<string> _before;

    public UndoAllTests(ChatFixture fx)
    {
        _fx = fx;
        _before = Features.InReleaseOff.ToList();
        Features.Set(_before.Where(x => x is not (Features.UndoAll or Features.Dms)));
        Features.Set(Features.InReleaseOff.Where(x => x != Features.Journal)); // [feature:journal]
    }

    public void Dispose() => Features.Set(_before);

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    // [feature:journal]
    [Fact]
    public async Task Geloeschter_Tagebuch_Eintrag_kommt_mit_Rueckgaengig_zurueck()
    {
        var c = _fx.As("anna");
        var j = (await Json(await c.GetAsync("/api/journal/journals"))).EnumerateArray().First().GetProperty("id").GetInt64();
        var e = await Json(await c.PostAsJsonAsync("/api/journal/entries", new { journal = j, date = "2026-08-02T09:00:00Z", title = "Wanderung", html = "<p>Auf den Berg</p>" }));
        var uri = e.GetProperty("uri").GetString()!;

        var undo = (await Json(await c.DeleteAsync($"/api/journal/entries/{j}/{uri}"))).GetProperty("undo").GetString()!;
        Assert.Contains("Wanderung", undo);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/journal/entries/{j}/{uri}")).StatusCode);

        (await c.PostAsJsonAsync($"/api/journal/entries/{j}/{uri}/restore", new { data = undo })).EnsureSuccessStatusCode();
        Assert.Equal("Wanderung", (await Json(await c.GetAsync($"/api/journal/entries/{j}/{uri}"))).GetProperty("title").GetString());
        // Ein zweites Mal überschreibt nichts
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/journal/entries/{j}/{uri}/restore", new { data = undo })).StatusCode);
        // Kein gültiger Eintrag
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/journal/entries/{j}/anderer.ics/restore", new { data = "kein Kalender" })).StatusCode);

        // Ohne Kennung: wie bisher, kein Stand und kein Wiederherstellen
        Features.Set(_before.Where(x => x is not (Features.Journal or Features.Dms)).Append(Features.UndoAll));
        var plain = await c.DeleteAsync($"/api/journal/entries/{j}/{uri}");
        Assert.True(plain.IsSuccessStatusCode);
        Assert.DoesNotContain("undo", await plain.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"/api/journal/entries/{j}/{uri}/restore", new { data = undo })).StatusCode);
    }

    [Fact]
    public async Task Im_geteilten_Tagebuch_kommt_nur_der_selbst_geloeschte_Stand_zurueck()
    {
        var anna = _fx.As("anna");
        var j = (await Json(await anna.GetAsync("/api/journal/journals"))).EnumerateArray().First(x => x.GetProperty("owner").ValueKind == JsonValueKind.Null).GetProperty("id").GetInt64();
        (await anna.PutAsJsonAsync($"/api/pim/shares/{j}", new { members = new[] { _fx.Id("ben") } })).EnsureSuccessStatusCode();
        var ben = _fx.As("ben");
        var sj = (await Json(await ben.GetAsync("/api/journal/journals"))).EnumerateArray().First(x => x.GetProperty("owner").GetString() == "Anna").GetProperty("id").GetInt64();
        var space = _fx.Space("anna");
        FileStore.EnsureDir(space.Db, "Privat");
        FileStore.Put(space.Db, "Privat/brief.txt", Encoding.UTF8.GetBytes("Nur für Anna"), "text/plain");

        var e = await Json(await ben.PostAsJsonAsync("/api/journal/entries", new { journal = sj, date = "2026-08-03T09:00:00Z", title = "Ausflug", html = "<p>See</p>" }));
        var uri = e.GetProperty("uri").GetString()!;
        var undo = (await Json(await ben.DeleteAsync($"/api/journal/entries/{sj}/{uri}"))).GetProperty("undo").GetString()!;

        // Ein anderer Stand (hier mit einer Datei von Anna) wird nicht angenommen, unter keinem Namen
        var forged = undo.Replace("END:VJOURNAL", "ATTACH;FMTTYPE=text/plain:dmoffice:files/Privat%2Fbrief.txt\r\nEND:VJOURNAL");
        Assert.Equal(HttpStatusCode.NotFound, (await ben.PostAsJsonAsync($"/api/journal/entries/{sj}/{uri}/restore", new { data = forged })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.PostAsJsonAsync($"/api/journal/entries/{sj}/neu-{uri}/restore", new { data = undo })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync($"/api/journal/file?journal={sj}&path={Uri.EscapeDataString("Privat/brief.txt")}")).StatusCode);

        // Der eigene gelöschte Stand kommt zurück, aber nur einmal
        (await ben.PostAsJsonAsync($"/api/journal/entries/{sj}/{uri}/restore", new { data = undo })).EnsureSuccessStatusCode();
        Assert.Equal("Ausflug", (await Json(await ben.GetAsync($"/api/journal/entries/{sj}/{uri}"))).GetProperty("title").GetString());
        (await ben.DeleteAsync($"/api/journal/entries/{sj}/{uri}")).EnsureSuccessStatusCode();
        (await ben.PostAsJsonAsync($"/api/journal/entries/{sj}/{uri}/restore", new { data = undo })).EnsureSuccessStatusCode();
        (await anna.DeleteAsync($"/api/journal/entries/{j}/{uri}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await ben.PostAsJsonAsync($"/api/journal/entries/{sj}/{uri}/restore", new { data = undo })).StatusCode);
    }
    // [/feature:journal]

    [Fact]
    public async Task Geloeschtes_Dokument_kommt_aus_dem_Papierkorb_samt_Angaben_zurueck()
    {
        var space = _fx.Space("anna");
        var r = await DmsImport.ImportAsync(space, new MemoryStream(Encoding.UTF8.GetBytes("Rückgängig Testinhalt Omega")), "undo-omega.txt", "text/plain", new DmsImport.Info("upload"), default);
        await DmsImport.ProcessQueueAsync(space, DmsHost.Current!, 100, default);
        var doc = Dms.Dms.Get(space.Db, r.Id);
        Assert.NotEqual(0, doc.FileId);
        var c = _fx.As("anna");
        await Json(await c.PutAsJsonAsync($"/api/dms/docs/{doc.Id}", new { title = "Omega-Vertrag" }));

        var trash = (await Json(await c.PostAsJsonAsync("/api/dms/docs/delete", new { ids = new[] { doc.Id } }))).GetProperty("trash").EnumerateArray().Select(x => x.GetInt64()).ToList();
        Assert.Single(trash);
        Assert.Null(FileStore.ById(space.Db, doc.FileId));

        await Json(await c.PostAsJsonAsync("/api/files/trash/restore", new { ids = trash }));
        Assert.NotNull(FileStore.ById(space.Db, doc.FileId));
        Assert.Equal("Omega-Vertrag", Dms.Dms.Get(space.Db, doc.Id).Title);
    }

    [Fact]
    public async Task Zurueckverschieben_von_Mails_gibt_es_nur_mit_Kennung()
    {
        var c = _fx.As("anna");
        Features.Set(_before.Append(Features.UndoAll));
        var off = await c.PostAsJsonAsync("/api/mail/x/move-back", new { folder = "Trash", target = "INBOX", messageIds = new[] { "a@b" } });
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
    }

    [Fact]
    public void Message_IDs_fuer_Rueckgaengig_werden_bereinigt()
    {
        var ids = MailApi.UndoMessageIds(["<a1@example.org>", "a1@example.org", " b2@example.org ", "", null, "mit leer@x", "x\"y@z", new string('a', 300)]);
        Assert.Equal(["a1@example.org", "b2@example.org"], ids);
        Assert.Empty(MailApi.UndoMessageIds(null));
    }

    [Fact]
    public void Mails_eines_Ordners_per_Message_ID_finden()
    {
        var db = new UserDb(Path.Combine(Directory.CreateTempSubdirectory("undo-mail").FullName, "u.db"), new byte[32], "t");
        MailStore.ReplaceFolders(db, "a", new() { ("INBOX", "INBOX", "/", "inbox", true), ("Trash", "Trash", "/", "trash", true) });
        void Add(long uid, string folder, string mid) =>
            db.Exec("INSERT INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,date) VALUES('a',$f,$u,$m,'Test','Anna','anna@example.org',0)",
                ("$f", folder), ("$u", uid), ("$m", mid));
        Add(1, "Trash", "<eins@example.org>");
        Add(2, "Trash", "zwei@example.org");
        Add(3, "INBOX", "drei@example.org");
        Assert.Equal([1u, 2u], MailStore.UidsByMessageIds(db, "a", "Trash", ["eins@example.org", "zwei@example.org", "drei@example.org"]).Order().ToList());
        Assert.Empty(MailStore.UidsByMessageIds(db, "a", "INBOX", ["eins@example.org"]));
    }
}
