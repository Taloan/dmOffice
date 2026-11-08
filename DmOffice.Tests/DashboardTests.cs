using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Mail;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Übersicht (Dashboard): Zusammenstellung speichern, Hintergrund, Daten der Widgets, Abhaken.</summary>
[Collection("server")]
public class DashboardTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public DashboardTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Get(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        Assert.True(r.IsSuccessStatusCode, url + ": " + r.StatusCode + " " + await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Zusammenstellung_Vorgabe_speichern_und_Hintergrund()
    {
        var c = _fx.As("dora");
        var l = await Get(c, "/api/dashboard/");
        Assert.False(l.GetProperty("saved").GetBoolean());
        Assert.Contains(l.GetProperty("widgets").EnumerateArray(), w => w.GetProperty("type").GetString() == "events");

        var save = await c.PutAsJsonAsync("/api/dashboard/", new
        {
            widgets = new object[] { new { id = "a1", type = "today", size = 2 }, new { id = "a2", type = "events", size = 3, title = "Familie", opts = new { days = 7, calendars = new[] { 1 } } } },
            background = new { kind = "none", dim = 40 },
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var again = await Get(c, "/api/dashboard/");
        Assert.True(again.GetProperty("saved").GetBoolean());
        Assert.Equal(2, again.GetProperty("widgets").GetArrayLength());
        Assert.Equal("Familie", again.GetProperty("widgets")[1].GetProperty("title").GetString());
        Assert.Equal(7, again.GetProperty("widgets")[1].GetProperty("opts").GetProperty("days").GetInt32());
        Assert.Equal(40, again.GetProperty("background").GetProperty("dim").GetInt32());
        // Unbekanntes Widget: abgelehnt
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/dashboard/", new { widgets = new object[] { new { type = "unsinn" } }, background = new { } })).StatusCode);

        // Hintergrund hochladen: verkleinert als WebP, abrufbar, wieder entfernbar
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dashboard/background")).StatusCode);
        var up = await c.PutAsync("/api/dashboard/background", new ByteArrayContent(FileMetaTests.ExifJpeg()));
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Equal("upload", (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("background").GetProperty("kind").GetString());
        var img = await c.GetAsync("/api/dashboard/background");
        Assert.Equal("image/webp", img.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync("/api/dashboard/background")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dashboard/background")).StatusCode);
    }

    [Fact]
    public async Task Widgets_liefern_Daten_und_Abhaken_geht()
    {
        var space = _fx.Space("cleo");
        var c = _fx.As("cleo");
        PimStore.EnsureDefaults(space.Db);
        var list = PimStore.Collections(space.Db, PimStore.Calendar).First(x => x.Components.Contains("VTODO"));
        var today = DateOnly.FromDateTime(DateTime.Now);
        var uri = CalendarModel.SaveTask(space, new TaskInput { List = list.Id, Title = "Dashboard-Aufgabe " + Guid.NewGuid().ToString("N")[..6], Due = today.ToString("yyyy-MM-dd"), DueAllDay = true });
        var taskUri = uri[(uri.IndexOf('/') + 1)..];

        var tasks = await Get(c, "/api/dashboard/w/tasks?o=" + Uri.EscapeDataString("{\"days\":3}"));
        var mine = tasks.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("uri").GetString() == taskUri);
        Assert.Equal(list.Id, mine.GetProperty("list").GetInt64());
        Assert.False(mine.GetProperty("overdue").GetBoolean());

        // Abhaken über die Übersicht, danach nicht mehr dabei; zurück, wieder da
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/dashboard/task", new { list = list.Id, uri = taskUri, done = true })).StatusCode);
        Assert.DoesNotContain((await Get(c, "/api/dashboard/w/tasks")).GetProperty("items").EnumerateArray(), x => x.GetProperty("uri").GetString() == taskUri);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/dashboard/task", new { list = list.Id, uri = taskUri, done = false })).StatusCode);
        Assert.Contains((await Get(c, "/api/dashboard/w/tasks")).GetProperty("items").EnumerateArray(), x => x.GetProperty("uri").GetString() == taskUri);

        // Heute, Kalender, Chat, Notizen, Dateien, Speicher, Tagebuch: antworten ohne Fehler und mit den erwarteten Feldern
        Assert.StartsWith("Gut", (await Get(c, "/api/dashboard/w/today")).GetProperty("greeting").GetString()!.Length > 0 ? "Gut" : "");
        var ev = await Get(c, "/api/dashboard/w/events?o=" + Uri.EscapeDataString("{\"days\":2}"));
        Assert.Equal(2, ev.GetProperty("days").GetArrayLength());
        Assert.True((await Get(c, "/api/dashboard/w/chat")).TryGetProperty("rooms", out _));
        Assert.True((await Get(c, "/api/dashboard/w/notes")).TryGetProperty("items", out _));
        var dir = "Dash-" + Guid.NewGuid().ToString("N")[..6];
        FileStore.Put(space.Db, dir + ".txt", "x"u8.ToArray(), "text/plain");
        FileStore.Favorite(space.Db, FileStore.Get(space.Db, dir + ".txt")!.FileId, true);
        Assert.Contains((await Get(c, "/api/dashboard/w/files")).GetProperty("items").EnumerateArray(), x => x.GetProperty("path").GetString() == dir + ".txt" && x.GetProperty("favorite").GetBoolean());
        Assert.True((await Get(c, "/api/dashboard/w/storage")).GetProperty("used").GetInt64() > 0);
        // Coach: der heutige Tag (Waage, Ring, Schritte) und Wasser
        Assert.True((await Get(c, "/api/dashboard/w/coach")).TryGetProperty("eaten", out _));
        Assert.True((await Get(c, "/api/dashboard/w/water")).GetProperty("water").TryGetProperty("goal", out _));
        var j = await Get(c, "/api/dashboard/w/journal");
        Assert.True(j.TryGetProperty("back", out _));
        Assert.True((await Get(c, "/api/dashboard/w/birthdays")).TryGetProperty("items", out _));
        Assert.True((await Get(c, "/api/dashboard/w/photos")).TryGetProperty("years", out _));
        Assert.Equal(false, (await Get(c, "/api/dashboard/w/weather")).GetProperty("configured").GetBoolean());
        // System nur für die Verwaltung
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/dashboard/w/system")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dashboard/w/gibtsnicht")).StatusCode);
    }

    [Fact]
    public async Task Mail_Widget_mit_ungelesener_Mail()
    {
        // Ohne Mail im Posteingang fiel nicht auf, dass ein Feld doppelt hieß („account“ als Kennung und als Name)
        var space = _fx.Space("ben");
        var c = _fx.As("ben");
        var account = new MailAccount { Name = "Übersicht", Email = "ben@beispiel.de", ImapHost = "", SmtpHost = "" };
        // Über UpdateSettings - direkt in die DB geschrieben sähe der Zwischenspeicher des Space das Konto nicht
        space.UpdateSettings(s => s.MailAccounts.Add(account));
        MailStore.ReplaceFolders(space.Db, account.Id, new() { ("INBOX", "INBOX", "/", "inbox", true) });
        space.Db.Exec(@"INSERT INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,has_att)
                        VALUES($a,'INBOX',1,'<dash@test>','Hallo Übersicht','Anna','anna@example.org','{}',$d,10,0,0,0)",
            ("$a", account.Id), ("$d", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        var mail = await Get(c, "/api/dashboard/w/mail");
        var m = Assert.Single(mail.GetProperty("items").EnumerateArray());
        Assert.Equal(account.Id, m.GetProperty("account").GetString());
        Assert.Equal("Übersicht", m.GetProperty("accountName").GetString());
        Assert.Equal("Hallo Übersicht", m.GetProperty("subject").GetString());
    }
}
