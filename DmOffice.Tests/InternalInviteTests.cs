using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Mail;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>
/// Termineinladungen innerhalb von dmOffice (Version 2): Pukki lädt ein, Knöpfe statt Mail, Antwort sofort beim Organisator,
/// Änderungen und Absagen ziehen die Kopien mit. Anna und Ben haben ein Mailkonto, Cleo nicht.
/// </summary>
[Collection("server")]
public class InternalInviteTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;

    public InternalInviteTests(ChatFixture fx)
    {
        _fx = fx;
        foreach (var login in new[] { "anna", "ben" })
            _fx.Space(login).UpdateSettings(s =>
            {
                if (s.MailAccounts.Count == 0) s.MailAccounts.Add(new MailAccount { Name = "Privat", Email = $"{login}@beispiel.de", DisplayName = login == "anna" ? "Anna" : "Ben" });
            });
        foreach (var login in new[] { "anna", "ben", "cleo" }) PimStore.EnsureDefaults(_fx.Space(login).Db);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private long Calendar(string login) =>
        PimStore.Collections(_fx.Space(login).Db, PimStore.Calendar).First(c => c.Components.Contains("VEVENT")).Id;

    private ChatStore Chat => _fx.Factory.Services.GetRequiredService<ChatStore>();

    /// <summary>Pukkis letzte Nachricht an den Benutzer.</summary>
    private ChatMessage LastFromPukki(string login)
    {
        var room = Chat.DirectRoom(_fx.Id(login), Pukki.Id) ?? throw new InvalidOperationException("Kein Pukki-Raum");
        return Chat.Messages(_fx.Id(login), room).Last(m => m.Sender == Pukki.Id);
    }

    private async Task<(string Uri, long Cal)> Invite(string organizer, string title, string start, string end, string[] who, string? rrule = null)
    {
        var cal = Calendar(organizer);
        var r = await Json(await _fx.As(organizer).PostAsJsonAsync("/api/pim/events", new
        {
            calendar = cal, title, start, end, allDay = false, busy = true, rRule = rrule ?? "", location = "Küche",
            attendees = who.Select(w => new { email = w, name = (string?)null }).ToArray(),
        }));
        Assert.False(r.TryGetProperty("error", out var err), err.ToString());
        return (r.GetProperty("id").GetString()!.Split('/', 2)[1], cal);
    }

    private async Task<List<JsonElement>> Events(string login, string title)
    {
        var all = await Json(await _fx.As(login).GetAsync("/api/pim/events?from=2030-01-01T00:00:00Z&to=2031-01-01T00:00:00Z"));
        return all.EnumerateArray().Where(e => e.GetProperty("title").GetString() == title).ToList();
    }

    private static string Status(JsonElement e, string email) =>
        e.GetProperty("attendees").EnumerateArray().First(a => a.GetProperty("email").GetString() == email).GetProperty("status").GetString()!;

    private async Task Press(string login, ChatMessage m, string key)
    {
        var r = await _fx.As(login).PostAsync($"/api/chat/messages/{m.Id}/actions/{key}", null);
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Einladung_ueber_Pukki_Antworten_landen_sofort_beim_Organisator()
    {
        // Anna lädt Ben und Cleo (interne Adressen) und jemanden von außen ein
        var (uri, cal) = await Invite("anna", "Grillfest", "2030-07-04T16:00:00Z", "2030-07-04T20:00:00Z",
            ["ben@dmoffice.intern", "cleo@dmoffice.intern", "xaver@extern.example"]);

        // Ben und Cleo: keine Mail, sondern Pukki mit drei Knöpfen; nur Xaver steht im Postausgang
        var outbox = MailQueue.List(_fx.Space("anna").Db);
        Assert.Contains(outbox, m => m.Recipients.Contains("xaver@extern.example"));
        Assert.DoesNotContain(outbox, m => m.Recipients.Contains("ben@dmoffice.intern") || m.Recipients.Contains("cleo@"));
        var toBen = LastFromPukki("ben");
        Assert.StartsWith("Anna lädt dich ein: „Grillfest“ – Donnerstag, 4. Juli 2030, 18:00–22:00 Uhr, Küche", toBen.Text);
        Assert.Equal([InternalInvites.Accept, InternalInvites.Tentative, InternalInvites.Decline], toBen.Actions!.Select(a => a.Key));
        Assert.Equal(ChatRef.TypeEvent, toBen.Refs[0].Type);
        var toCleo = LastFromPukki("cleo");
        Assert.Contains("„Grillfest“", toCleo.Text);

        // Ben sagt zu: Termin in seinem Kalender, bei Anna sofort „zugesagt“, Pukki sagt es Anna
        await Press("ben", toBen, InternalInvites.Accept);
        // Der gedrückte Knopf ist weg, die anderen beiden bleiben zum Umentscheiden
        Assert.Equal([InternalInvites.Tentative, InternalInvites.Decline], Chat.Message(_fx.Id("ben"), toBen.Id).Actions!.Select(a => a.Key));
        var bens = Assert.Single(await Events("ben", "Grillfest"));
        Assert.Equal("2030-07-04T16:00:00Z", bens.GetProperty("start").GetString());
        Assert.Equal("ACCEPTED", Status(bens, "ben@dmoffice.intern"));
        Assert.StartsWith("Zugesagt – „Grillfest“ steht in deinem Kalender", LastFromPukki("ben").Text);
        var annas = Assert.Single(await Events("anna", "Grillfest"));
        Assert.Equal("ACCEPTED", Status(annas, "ben@dmoffice.intern"));
        Assert.Equal("Ben hat zugesagt: „Grillfest“.", LastFromPukki("anna").Text);

        // Cleo sagt ab: kein Termin bei ihr, bei Anna „abgelehnt“
        await Press("cleo", toCleo, InternalInvites.Decline);
        Assert.Empty(await Events("cleo", "Grillfest"));
        Assert.Equal("DECLINED", Status(Assert.Single(await Events("anna", "Grillfest")), "cleo@dmoffice.intern"));

        // Ben überlegt es sich anders: mit Vorbehalt - derselbe Knopf geht noch
        await Press("ben", toBen, InternalInvites.Tentative);
        Assert.Equal("TENTATIVE", Status(Assert.Single(await Events("ben", "Grillfest")), "ben@dmoffice.intern"));
        Assert.Equal("TENTATIVE", Status(Assert.Single(await Events("anna", "Grillfest")), "ben@dmoffice.intern"));
        Assert.Equal(cal, Calendar("anna"));
        Assert.NotNull(uri);
    }

    [Fact]
    public async Task Eingetragene_Mailadresse_zieht_keine_Einladung_an_sich()
    {
        var room = Chat.DirectRoom(_fx.Id("ben"), Pukki.Id) ?? Chat.OpenDirect(_fx.Id("ben"), Pukki.Id);
        var before = Chat.Messages(_fx.Id("ben"), room, limit: 500).Count;
        await Invite("anna", "Vertraulich", "2030-12-01T16:00:00Z", "2030-12-01T17:00:00Z", ["ben@beispiel.de"]);
        Assert.Equal(before, Chat.Messages(_fx.Id("ben"), room, limit: 500).Count);
        Assert.Contains(MailQueue.List(_fx.Space("anna").Db), m => m.Recipients.Contains("ben@beispiel.de"));
        // Bens eigene interne Adresse bleibt der Weg zu ihm
        var hits = (await Json(await _fx.As("anna").GetAsync("/api/pim/suggest?q=Ben&people=1"))).EnumerateArray().ToList();
        Assert.Contains(hits, h => h.GetProperty("address").GetString() == "ben@dmoffice.intern" && h.GetProperty("source").GetString() == "people");
    }

    [Fact]
    public async Task Termin_ueber_die_doppelte_Stunde_behaelt_die_Dauer()
    {
        await Json(await _fx.As("cleo").PostAsJsonAsync("/api/pim/events", new
        {
            calendar = PimStore.Collections(_fx.Space("cleo").Db, PimStore.Calendar)[0].Id,
            title = "Nachtschicht", start = "2026-10-25T00:30:00Z", end = "2026-10-25T01:30:00Z", allDay = false, notify = false,
        }));
        var e = (await Json(await _fx.As("cleo").GetAsync("/api/pim/events?from=2026-10-24T00:00:00Z&to=2026-10-26T00:00:00Z"))).EnumerateArray()
            .Single(x => x.GetProperty("title").GetString() == "Nachtschicht");
        Assert.Equal(DateTime.Parse("2026-10-25T00:30:00Z").ToUniversalTime(), DateTime.Parse(e.GetProperty("start").GetString()!).ToUniversalTime());
        Assert.Equal(DateTime.Parse("2026-10-25T01:30:00Z").ToUniversalTime(), DateTime.Parse(e.GetProperty("end").GetString()!).ToUniversalTime());
    }

    [Fact]
    public async Task Verschieben_Ausladen_und_Absagen_ziehen_die_Kopien_mit()
    {
        var (uri, cal) = await Invite("anna", "Elternabend", "2030-09-10T17:00:00Z", "2030-09-10T18:30:00Z", ["ben@dmoffice.intern", "cleo@dmoffice.intern"]);
        await Press("ben", LastFromPukki("ben"), InternalInvites.Accept);
        await Press("cleo", LastFromPukki("cleo"), InternalInvites.Accept);

        // Anna verschiebt (Ziehen im Kalender, ohne Teilnehmerliste): Kopien wandern mit, Pukki sagt Bescheid
        var etag = PimStore.Object(_fx.Space("anna").Db, cal, uri)!.Etag;
        await Json(await _fx.As("anna").PostAsJsonAsync("/api/pim/events", new
        {
            calendar = cal, uri, etag, title = "Elternabend", location = "Küche", start = "2030-09-10T18:00:00Z", end = "2030-09-10T19:30:00Z", allDay = false, busy = true,
        }));
        Assert.Equal("2030-09-10T18:00:00Z", Assert.Single(await Events("ben", "Elternabend")).GetProperty("start").GetString());
        Assert.Contains("hat „Elternabend“ geändert", LastFromPukki("ben").Text);
        Assert.Equal("ACCEPTED", Status(Assert.Single(await Events("ben", "Elternabend")), "ben@dmoffice.intern"));

        // Cleo ausgeladen: Termin bei ihr weg, Ben bleibt
        etag = PimStore.Object(_fx.Space("anna").Db, cal, uri)!.Etag;
        await Json(await _fx.As("anna").PostAsJsonAsync("/api/pim/events", new
        {
            calendar = cal, uri, etag, title = "Elternabend", location = "Küche", start = "2030-09-10T18:00:00Z", end = "2030-09-10T19:30:00Z", allDay = false, busy = true,
            attendees = new[] { new { email = "ben@dmoffice.intern", name = (string?)null } },
        }));
        Assert.Empty(await Events("cleo", "Elternabend"));
        Assert.Contains("ausgeladen", LastFromPukki("cleo").Text);
        Assert.Single(await Events("ben", "Elternabend"));

        // Ganz abgesagt: auch bei Ben weg
        await Json(await _fx.As("anna").DeleteAsync($"/api/pim/events/{cal}/{uri}"));
        Assert.Empty(await Events("ben", "Elternabend"));
        Assert.StartsWith("Anna hat „Elternabend“ abgesagt", LastFromPukki("ben").Text);

        // Ein alter Knopf zum gelöschten Termin: freundlich erklärt, Knöpfe verschwinden
        var stale = Chat.Messages(_fx.Id("ben"), Chat.DirectRoom(_fx.Id("ben"), Pukki.Id)!.Value).Last(m => m.Actions?.Any(a => a.Key == InternalInvites.Decline) == true);
        await Press("ben", stale, InternalInvites.Decline);
        Assert.Equal("Diesen Termin gibt es nicht mehr.", LastFromPukki("ben").Text);
    }

    [Fact]
    public async Task Serie_ein_Termin_abgesagt_die_anderen_bleiben()
    {
        var (uri, cal) = await Invite("anna", "Chorprobe", "2030-10-01T17:00:00Z", "2030-10-01T18:00:00Z", ["ben@dmoffice.intern"], "FREQ=WEEKLY;COUNT=3");
        Assert.StartsWith("Anna lädt dich ein: „Chorprobe“ – ab Dienstag, 1. Oktober 2030", LastFromPukki("ben").Text);
        await Press("ben", LastFromPukki("ben"), InternalInvites.Accept);
        Assert.Equal(3, (await Events("ben", "Chorprobe")).Count);

        await Json(await _fx.As("anna").DeleteAsync($"/api/pim/events/{cal}/{uri}?instance=2030-10-08T17:00:00Z"));
        Assert.Equal(["2030-10-01T17:00:00Z", "2030-10-15T17:00:00Z"], (await Events("ben", "Chorprobe")).Select(e => e.GetProperty("start").GetString()));
        Assert.Contains("am Dienstag, 8. Oktober abgesagt", LastFromPukki("ben").Text);
    }

    [Fact]
    public async Task Ohne_Mailkonto_einladen_und_Vorschlaege_fuer_Benutzer()
    {
        // Cleo hat kein Mailkonto - sie lädt trotzdem ein, als Organisatorin mit ihrer internen Adresse
        var (uri, cal) = await Invite("cleo", "Spieleabend", "2030-11-02T18:00:00Z", "2030-11-02T22:00:00Z", ["anna@dmoffice.intern"]);
        var ics = PimStore.Object(_fx.Space("cleo").Db, cal, uri)!.Data.Replace("\r\n ", "");
        Assert.Contains("mailto:cleo@dmoffice.intern", ics);
        await Press("anna", LastFromPukki("anna"), InternalInvites.Accept);
        var copy = Assert.Single(await Events("anna", "Spieleabend"));
        Assert.Equal("cleo@dmoffice.intern", copy.GetProperty("organizer").GetString());
        Assert.Equal("Anna hat zugesagt: „Spieleabend“.", LastFromPukki("cleo").Text);

        // Vorschläge im Teilnehmerfeld: Benutzer dieses dmOffice mit ihrer Einladungsadresse, nicht man selbst
        var hits = (await Json(await _fx.As("anna").GetAsync("/api/pim/suggest?q=Cle&people=1"))).EnumerateArray().ToList();
        Assert.Contains(hits, h => h.GetProperty("address").GetString() == "cleo@dmoffice.intern" && h.GetProperty("source").GetString() == "people");
        Assert.DoesNotContain((await Json(await _fx.As("anna").GetAsync("/api/pim/suggest?q=Ann&people=1"))).EnumerateArray(),
            h => h.GetProperty("source").GetString() == "people");
        // Ohne people (Mail schreiben): keine internen Adressen
        Assert.DoesNotContain((await Json(await _fx.As("anna").GetAsync("/api/pim/suggest?q=Cle"))).EnumerateArray(),
            h => h.GetProperty("source").GetString() == "people");
    }
}
