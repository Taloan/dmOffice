using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Mail;
using DmOffice.Pim;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>
/// Drei Personen auf einem Server, jede mit eigenem Mailkonto. Mails gehen ueber den echten Postausgang an einen
/// SMTP-Server im Test und von dort in den Posteingang der Empfaenger - Einladungen laufen so wie im Alltag hin und her.
/// </summary>
public sealed class InviteFlowFixture : IDisposable
{
    public const string Domain = "flow.test";
    public FakeSmtp Smtp { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }
    public Dictionary<string, (string Id, string AppPassword)> Users { get; } = new();
    /// <summary>Posteingang je Person; "extern" fuer Adressen ausserhalb (Outlook, Google).</summary>
    public Dictionary<string, List<MimeMessage>> Inbox { get; } = new();
    private readonly DirectoryInfo _data;

    public InviteFlowFixture()
    {
        _data = Directory.CreateTempSubdirectory("dmflow");
        Environment.SetEnvironmentVariable("DMOFFICE_DATA", _data.FullName);
        Factory = new WebApplicationFactory<Program>();
        var users = Factory.Services.GetRequiredService<UserStore>();
        var spaces = Factory.Services.GetRequiredService<Spaces>();
        foreach (var (login, name) in new[] { ("chris", "Chris Berger"), ("anna", "Anna Klein"), ("ben", "Ben Ott") })
        {
            var u = users.Create(login, name, "geheim-123456", isAdmin: login == "chris");
            var space = spaces.Get(u.Id);
            Users[login] = (u.Id, AppPasswords.Create(space.Db, "Test"));
            space.UpdateSettings(s => s.MailAccounts.Add(new MailAccount
            {
                Name = "Arbeit", Email = $"{login}@{Domain}", DisplayName = name,
                SmtpHost = "127.0.0.1", SmtpPort = Smtp.Port, SmtpSecurity = Security.None, SaveSentCopy = false,
            }));
            PimStore.EnsureDefaults(space.Db);
            Inbox[login] = new();
        }
        Inbox["extern"] = new();
    }

    public UserSpace Space(string login) => Factory.Services.GetRequiredService<Spaces>().Get(Users[login].Id);
    public MailAccount Account(string login) => Space(login).Settings.MailAccounts[0];
    public static string Email(string login) => $"{login}@{Domain}";

    public long Calendar(string login) =>
        PimStore.Collections(Space(login).Db, PimStore.Calendar).First(c => c.Components.Contains("VEVENT")).Id;

    public HttpClient As(string login)
    {
        var c = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{Users[login].AppPassword}")));
        return c;
    }

    /// <summary>
    /// Postausgaenge leeren (wie der Versand im Hintergrund, nur ohne auf ihn zu warten) und alles, was beim SMTP-Server
    /// angekommen ist, in die Posteingaenge der Empfaenger legen.
    /// </summary>
    public async Task Deliver()
    {
        var pool = Factory.Services.GetRequiredService<ImapPool>();
        var sync = Factory.Services.GetRequiredService<MailSync>();
        foreach (var login in Users.Keys)
        {
            var space = Space(login);
            foreach (var item in MailQueue.TakeDue(space.Db, DateTime.UtcNow.AddYears(1)))
            {
                var account = space.Account(item.Account);
                await MailApi.Deliver(space, account, account, item.Draft, item.Message, pool, sync, null, default);
                MailQueue.Done(space.Db, item.Id);
            }
        }
        // Der Versand im Hintergrund kann einzelne Mails schon genommen haben - warten, bis alles raus ist
        var until = DateTime.UtcNow.AddSeconds(30);
        while (Users.Keys.SelectMany(l => MailQueue.List(Space(l).Db)).ToList() is { Count: > 0 } left)
        {
            if (left.FirstOrDefault(r => r.Error != null) is { } bad) throw new InvalidOperationException("Versand gescheitert: " + bad.Error);
            if (DateTime.UtcNow > until) throw new TimeoutException("Postausgang wird nicht leer");
            await Task.Delay(100);
        }
        foreach (var r in Smtp.Take())
            foreach (var to in r.To)
                Inbox[to.EndsWith("@" + Domain) && Inbox.ContainsKey(to[..to.IndexOf('@')]) ? to[..to.IndexOf('@')] : "extern"].Add(r.Message);
    }

    public void Dispose()
    {
        Factory.Dispose();
        Smtp.Dispose();
        try { _data.Delete(true); } catch (IOException) { /* Datenbank noch offen */ }
    }
}

/// <summary>Einladungen von Anfang bis Ende: einladen, zusagen, absagen, verschieben, Serien - und Einladungen aus Outlook und Google.</summary>
[Collection("server")]
public class InviteFlowTests : IClassFixture<InviteFlowFixture>
{
    private readonly InviteFlowFixture _fx;
    public InviteFlowTests(InviteFlowFixture fx) => _fx = fx;

    private const string Year = "from=2030-01-01T00:00:00Z&to=2031-01-01T00:00:00Z";

    // ---------------- Hilfen ----------------

    private sealed record Saved(long Calendar, string Uri, string Uid, int Invited, int Cancelled);

    private static object People(params string[] logins) =>
        logins.Select(l => new { email = l.Contains('@') ? l : InviteFlowFixture.Email(l), name = (string?)null }).ToArray();

    /// <summary>Chris legt einen Termin mit Teilnehmern an.</summary>
    private async Task<Saved> Invite(string title, string start, string end, string[] who, string? rrule = null)
    {
        var cal = _fx.Calendar("chris");
        var r = await _fx.As("chris").PostAsJsonAsync("/api/pim/events", new
        {
            calendar = cal, title, start, end, allDay = false, busy = true, rRule = rrule ?? "", location = "Besprechungsraum",
            attendees = People(who),
        });
        r.EnsureSuccessStatusCode();
        var res = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(res.TryGetProperty("error", out var err), err.ToString());
        var uri = res.GetProperty("id").GetString()!.Split('/', 2)[1];
        var uid = Ics.Parse(PimStore.Object(_fx.Space("chris").Db, cal, uri)!.Data).First().Of("VEVENT").First().Text("UID")!;
        return new Saved(cal, uri, uid, res.GetProperty("invited").GetInt32(), res.TryGetProperty("cancelled", out var c) ? c.GetInt32() : 0);
    }

    /// <summary>Termin speichern wie die Oberflaeche (Dialog mit Teilnehmern oder Ziehen im Kalender ohne).</summary>
    private async Task<JsonElement> Save(string login, object body)
    {
        var r = await _fx.As(login).PostAsJsonAsync("/api/pim/events", body);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private string Etag(string login, long cal, string uri) => PimStore.Object(_fx.Space(login).Db, cal, uri)!.Etag;

    /// <summary>Termine einer Person mit diesem Titel im Jahr 2030, wie sie die Oberflaeche bekommt (Serien aufgeloest).</summary>
    private async Task<List<JsonElement>> Events(string login, string title)
    {
        var all = await (await _fx.As(login).GetAsync("/api/pim/events?" + Year)).Content.ReadFromJsonAsync<JsonElement>();
        return all.EnumerateArray().Where(e => e.GetProperty("title").GetString() == title).ToList();
    }

    private static string Start(JsonElement e) => e.GetProperty("start").GetString()!;

    private static string Status(JsonElement e, string who) =>
        e.GetProperty("attendees").EnumerateArray().First(a => a.GetProperty("email").GetString() == (who.Contains('@') ? who : InviteFlowFixture.Email(who))).GetProperty("status").GetString()!;

    /// <summary>Die neueste Mail im Posteingang mit einer Einladung (bzw. Antwort, Absage) zu dieser UID.</summary>
    private MimeMessage Mail(string login, string uid, string method, int skip = 0)
    {
        var list = _fx.Inbox[login].Where(m => Invites.Calendar(m) is { } c && (c.Text("METHOD") ?? "").Trim().ToUpperInvariant() == method
            && c.Of("VEVENT").Any(e => e.Text("UID") == uid)).Reverse().ToList();
        Assert.True(list.Count > skip, $"{login} hat keine Mail {method} zu {uid}");
        return list[skip];
    }

    private int Count(string login, string uid, string method) =>
        _fx.Inbox[login].Count(m => Invites.Calendar(m) is { } c && (c.Text("METHOD") ?? "").Trim().ToUpperInvariant() == method
            && c.Of("VEVENT").Any(e => e.Text("UID") == uid));

    private Task<bool> Respond(string login, MimeMessage m, string response) =>
        Invites.RespondAsync(_fx.Space(login), _fx.Account(login), m, response, true, default);

    private InviteDto Read(string login, MimeMessage m) => Invites.Read(_fx.Space(login), m)!;

    // ---------------- Einladen, zusagen, absagen ----------------

    [Fact]
    public async Task Einladung_kommt_an_und_Zusage_und_Absage_landen_beim_Organisator()
    {
        var ev = await Invite("Teamrunde", "2030-03-04T09:00:00Z", "2030-03-04T10:00:00Z", ["anna", "ben"]);
        Assert.Equal(2, ev.Invited);
        await _fx.Deliver();

        // Beide haben die Einladung, mit Organisator und Zeit
        var toAnna = Mail("anna", ev.Uid, "REQUEST");
        Assert.Equal("Einladung: Teamrunde", toAnna.Subject);
        var dto = Read("anna", toAnna);
        Assert.Equal(InviteFlowFixture.Email("chris"), dto.OrganizerEmail);
        Assert.Equal(new DateTime(2030, 3, 4, 9, 0, 0, DateTimeKind.Utc), dto.Start);
        Assert.Null(dto.CalendarId);
        var toBen = Mail("ben", ev.Uid, "REQUEST");

        // Anna sagt zu, Ben sagt ab
        Assert.True(await Respond("anna", toAnna, "accept"));
        Assert.True(await Respond("ben", toBen, "decline"));
        var annas = Assert.Single(await Events("anna", "Teamrunde"));
        Assert.Equal("2030-03-04T09:00:00Z", Start(annas));
        Assert.Equal("ACCEPTED", Status(annas, "anna"));
        Assert.Empty(await Events("ben", "Teamrunde"));
        Assert.Equal("ACCEPTED", Read("anna", toAnna).MyStatus);

        // Beim Organisator kommen beide Antworten an und lassen sich im Termin vermerken
        await _fx.Deliver();
        var replies = _fx.Inbox["chris"].Where(m => Invites.Calendar(m)?.Of("VEVENT").Any(e => e.Text("UID") == ev.Uid) == true).ToList();
        Assert.Equal(2, replies.Count);
        Assert.Contains(replies, m => m.Subject == "Zugesagt: Teamrunde" && Read("chris", m).ReplyStatus == "ACCEPTED");
        Assert.Contains(replies, m => m.Subject == "Abgesagt: Teamrunde" && Read("chris", m).ReplyStatus == "DECLINED");
        foreach (var m in replies) await Respond("chris", m, "apply");
        var chris_ = Assert.Single(await Events("chris", "Teamrunde"));
        Assert.Equal("ACCEPTED", Status(chris_, "anna"));
        Assert.Equal("DECLINED", Status(chris_, "ben"));
    }

    [Fact]
    public async Task Mit_Vorbehalt_und_spaeter_doch_abgesagt()
    {
        var ev = await Invite("Strategietag", "2030-03-05T08:00:00Z", "2030-03-05T16:00:00Z", ["anna"]);
        await _fx.Deliver();
        var invite = Mail("anna", ev.Uid, "REQUEST");

        await Respond("anna", invite, "tentative");
        Assert.Equal("TENTATIVE", Status(Assert.Single(await Events("anna", "Strategietag")), "anna"));
        await _fx.Deliver();
        await Respond("chris", Mail("chris", ev.Uid, "REPLY"), "apply");
        Assert.Equal("TENTATIVE", Status(Assert.Single(await Events("chris", "Strategietag")), "anna"));

        // Dieselbe Einladung spaeter noch einmal geoeffnet: absagen nimmt den Termin heraus und meldet es
        await Respond("anna", invite, "decline");
        Assert.Empty(await Events("anna", "Strategietag"));
        await _fx.Deliver();
        var last = Mail("chris", ev.Uid, "REPLY");
        Assert.Equal("Abgesagt: Strategietag", last.Subject);
        await Respond("chris", last, "apply");
        Assert.Equal("DECLINED", Status(Assert.Single(await Events("chris", "Strategietag")), "anna"));
    }

    // ---------------- Aendern und absagen ----------------

    [Fact]
    public async Task Verschieben_erreicht_die_Teilnehmer_und_die_alte_Einladung_aendert_nichts_mehr()
    {
        var ev = await Invite("Kundentermin", "2030-03-06T13:00:00Z", "2030-03-06T14:00:00Z", ["anna"]);
        await _fx.Deliver();
        var first = Mail("anna", ev.Uid, "REQUEST");
        await Respond("anna", first, "accept");

        // Chris verschiebt im Dialog um eine Stunde
        await Save("chris", new
        {
            calendar = ev.Calendar, uri = ev.Uri, etag = Etag("chris", ev.Calendar, ev.Uri), title = "Kundentermin", location = "Besprechungsraum",
            start = "2030-03-06T14:00:00Z", end = "2030-03-06T15:00:00Z", allDay = false, busy = true, attendees = People("anna"),
        });
        await _fx.Deliver();
        var update = Mail("anna", ev.Uid, "REQUEST");
        Assert.Equal("Aktualisiert: Kundentermin", update.Subject);
        var dto = Read("anna", update);
        Assert.False(dto.Outdated);
        Assert.Equal(new DateTime(2030, 3, 6, 14, 0, 0, DateTimeKind.Utc), dto.Start);

        await Respond("anna", update, "accept");
        Assert.Equal("2030-03-06T14:00:00Z", Start(Assert.Single(await Events("anna", "Kundentermin"))));

        // Die erste Einladung ist jetzt ueberholt - wer sie noch einmal oeffnet, sieht das
        Assert.True(Read("anna", first).Outdated);
    }

    [Fact]
    public async Task Verschieben_durch_Ziehen_im_Kalender_benachrichtigt_die_Teilnehmer()
    {
        var ev = await Invite("Jour fixe", "2030-03-07T07:00:00Z", "2030-03-07T07:30:00Z", ["anna"]);
        await _fx.Deliver();
        await Respond("anna", Mail("anna", ev.Uid, "REQUEST"), "accept");
        var before = Count("anna", ev.Uid, "REQUEST");

        // Ziehen im Kalender speichert ohne Teilnehmerliste (bleibt unveraendert)
        await Save("chris", new
        {
            calendar = ev.Calendar, uri = ev.Uri, etag = Etag("chris", ev.Calendar, ev.Uri), title = "Jour fixe", location = "Besprechungsraum",
            start = "2030-03-07T08:00:00Z", end = "2030-03-07T08:30:00Z", allDay = false, busy = true,
        });
        await _fx.Deliver();
        Assert.Equal(before + 1, Count("anna", ev.Uid, "REQUEST"));
        var update = Mail("anna", ev.Uid, "REQUEST");
        Assert.Equal(new DateTime(2030, 3, 7, 8, 0, 0, DateTimeKind.Utc), Read("anna", update).Start);

        // Nur "frei/beschaeftigt" umgestellt: das geht die Teilnehmer nichts an
        await Save("chris", new
        {
            calendar = ev.Calendar, uri = ev.Uri, etag = Etag("chris", ev.Calendar, ev.Uri), title = "Jour fixe", location = "Besprechungsraum",
            start = "2030-03-07T08:00:00Z", end = "2030-03-07T08:30:00Z", allDay = false, busy = false,
        });
        await _fx.Deliver();
        Assert.Equal(before + 1, Count("anna", ev.Uid, "REQUEST"));
    }

    [Fact]
    public async Task Eigene_Aenderungen_am_angenommenen_Termin_halten_spaetere_Updates_nicht_auf()
    {
        var ev = await Invite("Budgetrunde", "2030-03-12T09:00:00Z", "2030-03-12T10:00:00Z", ["anna"]);
        await _fx.Deliver();
        await Respond("anna", Mail("anna", ev.Uid, "REQUEST"), "accept");

        // Anna stellt an ihrer Kopie zweimal etwas um (Erinnerung, frei/beschaeftigt)
        var mine = Assert.Single(await Events("anna", "Budgetrunde"));
        var cal = mine.GetProperty("calendar").GetInt64();
        var uri = mine.GetProperty("uri").GetString()!;
        foreach (var (reminder, busy) in new[] { (15, true), (30, false) })
            await Save("anna", new
            {
                calendar = cal, uri, etag = Etag("anna", cal, uri), title = "Budgetrunde", location = "Besprechungsraum",
                start = "2030-03-12T09:00:00Z", end = "2030-03-12T10:00:00Z", allDay = false, busy, reminder,
            });

        // Chris verschiebt einmal: Annas Kopie darf die Aenderung nicht fuer veraltet halten
        await Save("chris", new
        {
            calendar = ev.Calendar, uri = ev.Uri, etag = Etag("chris", ev.Calendar, ev.Uri), title = "Budgetrunde", location = "Besprechungsraum",
            start = "2030-03-12T11:00:00Z", end = "2030-03-12T12:00:00Z", allDay = false, busy = true, attendees = People("anna"),
        });
        await _fx.Deliver();
        var update = Mail("anna", ev.Uid, "REQUEST");
        Assert.False(Read("anna", update).Outdated);
        await Respond("anna", update, "accept");
        Assert.Equal("2030-03-12T11:00:00Z", Start(Assert.Single(await Events("anna", "Budgetrunde"))));
    }

    [Fact]
    public async Task Absage_des_Organisators_nimmt_den_Termin_beim_Teilnehmer_heraus()
    {
        var ev = await Invite("Abteilungsfest", "2030-03-08T15:00:00Z", "2030-03-08T20:00:00Z", ["anna", "ben"]);
        await _fx.Deliver();
        await Respond("anna", Mail("anna", ev.Uid, "REQUEST"), "accept");
        await Respond("ben", Mail("ben", ev.Uid, "REQUEST"), "accept");

        var r = await _fx.As("chris").DeleteAsync($"/api/pim/events/{ev.Calendar}/{ev.Uri}");
        r.EnsureSuccessStatusCode();
        Assert.Equal(2, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancelled").GetInt32());
        await _fx.Deliver();

        var cancel = Mail("anna", ev.Uid, "CANCEL");
        Assert.Equal("Abgesagt: Abteilungsfest", cancel.Subject);
        var dto = Read("anna", cancel);
        Assert.NotNull(dto.CalendarId);
        await Respond("anna", cancel, "remove");
        Assert.Empty(await Events("anna", "Abteilungsfest"));
        // Ben hat die Absage auch, sein Termin steht bis zum Klick noch drin
        Assert.Single(await Events("ben", "Abteilungsfest"));
        await Respond("ben", Mail("ben", ev.Uid, "CANCEL"), "remove");
        Assert.Empty(await Events("ben", "Abteilungsfest"));
    }

    [Fact]
    public async Task Ausgeladener_Teilnehmer_bekommt_eine_Absage_die_anderen_ein_Update()
    {
        var ev = await Invite("Projektstart", "2030-03-11T08:00:00Z", "2030-03-11T09:00:00Z", ["anna", "ben"]);
        await _fx.Deliver();
        await Respond("anna", Mail("anna", ev.Uid, "REQUEST"), "accept");
        await Respond("ben", Mail("ben", ev.Uid, "REQUEST"), "accept");

        var res = await Save("chris", new
        {
            calendar = ev.Calendar, uri = ev.Uri, etag = Etag("chris", ev.Calendar, ev.Uri), title = "Projektstart", location = "Besprechungsraum",
            start = "2030-03-11T08:00:00Z", end = "2030-03-11T09:00:00Z", allDay = false, busy = true, attendees = People("anna"),
        });
        Assert.Equal(1, res.GetProperty("cancelled").GetInt32());
        await _fx.Deliver();

        await Respond("ben", Mail("ben", ev.Uid, "CANCEL"), "remove");
        Assert.Empty(await Events("ben", "Projektstart"));
        Assert.Equal(0, Count("anna", ev.Uid, "CANCEL"));
        Assert.Single(await Events("anna", "Projektstart"));
    }

    // ---------------- Serien ----------------

    [Fact]
    public async Task Serie_einladen_zusagen_und_einen_Termin_verschieben()
    {
        // Montags 9 Uhr (Sommerzeit: 07:00 UTC), vier Mal
        var ev = await Invite("Wochenrunde", "2030-05-06T07:00:00Z", "2030-05-06T08:00:00Z", ["anna"], "FREQ=WEEKLY;BYDAY=MO;COUNT=4");
        await _fx.Deliver();
        var invite = Mail("anna", ev.Uid, "REQUEST");
        Assert.True(Read("anna", invite).Recurring);
        await Respond("anna", invite, "accept");
        Assert.Equal(["2030-05-06T07:00:00Z", "2030-05-13T07:00:00Z", "2030-05-20T07:00:00Z", "2030-05-27T07:00:00Z"],
            (await Events("anna", "Wochenrunde")).Select(Start));

        // Nur der zweite Termin wandert auf Dienstag
        await Save("chris", new
        {
            calendar = ev.Calendar, uri = ev.Uri, etag = Etag("chris", ev.Calendar, ev.Uri), title = "Wochenrunde", location = "Besprechungsraum",
            start = "2030-05-14T07:00:00Z", end = "2030-05-14T08:00:00Z", allDay = false, busy = true,
            scope = "instance", instance = "2030-05-13T07:00:00Z", attendees = People("anna"),
        });
        await _fx.Deliver();
        await Respond("anna", Mail("anna", ev.Uid, "REQUEST"), "accept");
        Assert.Equal(["2030-05-06T07:00:00Z", "2030-05-14T07:00:00Z", "2030-05-20T07:00:00Z", "2030-05-27T07:00:00Z"],
            (await Events("anna", "Wochenrunde")).Select(Start));

        // Ganze Serie eine Stunde spaeter: der einzeln verschobene Termin behaelt seine Zeit
        await Save("chris", new
        {
            calendar = ev.Calendar, uri = ev.Uri, etag = Etag("chris", ev.Calendar, ev.Uri), title = "Wochenrunde", location = "Besprechungsraum",
            start = "2030-05-06T08:00:00Z", end = "2030-05-06T09:00:00Z", allDay = false, busy = true,
            scope = "series", instance = "2030-05-06T07:00:00Z", attendees = People("anna"),
        });
        await _fx.Deliver();
        await Respond("anna", Mail("anna", ev.Uid, "REQUEST"), "accept");
        Assert.Equal(["2030-05-06T08:00:00Z", "2030-05-14T07:00:00Z", "2030-05-20T08:00:00Z", "2030-05-27T08:00:00Z"],
            (await Events("anna", "Wochenrunde")).Select(Start));
    }

    [Fact]
    public async Task Einen_Termin_der_Serie_streichen_sagt_nur_diesen_ab()
    {
        var ev = await Invite("Sprechstunde", "2030-06-04T12:00:00Z", "2030-06-04T13:00:00Z", ["anna"], "FREQ=WEEKLY;BYDAY=TU;COUNT=3");
        await _fx.Deliver();
        await Respond("anna", Mail("anna", ev.Uid, "REQUEST"), "accept");
        Assert.Equal(3, (await Events("anna", "Sprechstunde")).Count);

        var r = await _fx.As("chris").DeleteAsync($"/api/pim/events/{ev.Calendar}/{ev.Uri}?instance=2030-06-11T12:00:00Z");
        r.EnsureSuccessStatusCode();
        Assert.Equal(1, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancelled").GetInt32());
        await _fx.Deliver();

        var cancel = Mail("anna", ev.Uid, "CANCEL");
        await Respond("anna", cancel, "remove");
        Assert.Equal(["2030-06-04T12:00:00Z", "2030-06-18T12:00:00Z"], (await Events("anna", "Sprechstunde")).Select(Start));
    }

    // ---------------- Einladungen aus anderen Programmen ----------------

    private static MimeMessage Foreign(string from, string to, string method, string subject, params string[] events)
    {
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Microsoft Corporation//Outlook 16.0 MIMEDIR//EN\r\nMETHOD:" + method + "\r\n"
            + "BEGIN:VTIMEZONE\r\nTZID:W. Europe Standard Time\r\nBEGIN:STANDARD\r\nDTSTART:16010101T030000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\n"
            + "RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=10\r\nEND:STANDARD\r\nBEGIN:DAYLIGHT\r\nDTSTART:16010101T020000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\n"
            + "RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=3\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n"
            + string.Concat(events.Select(e => "BEGIN:VEVENT\r\n" + e.Replace("\n", "\r\n").TrimEnd() + "\r\nEND:VEVENT\r\n"))
            + "END:VCALENDAR\r\n";
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(to));
        m.Subject = subject;
        var cal = new TextPart("calendar") { Text = ics };
        cal.ContentType.Parameters.Add("method", method);
        cal.ContentType.Charset = "utf-8";
        m.Body = new MultipartAlternative { new TextPart("plain") { Text = subject }, cal };
        return m;
    }

    private const string Laura = "laura.neumann@firma.example";

    private static string OutlookSeries(string uid, int seq) => $"""
        UID:{uid}
        SEQUENCE:{seq}
        DTSTAMP:20300401T080000Z
        DTSTART;TZID=W. Europe Standard Time:20300603T100000
        DTEND;TZID=W. Europe Standard Time:20300603T110000
        RRULE:FREQ=WEEKLY;COUNT=3;BYDAY=MO
        SUMMARY:Lenkungskreis
        ORGANIZER;CN=Laura Neumann:mailto:{Laura}
        ATTENDEE;CN=Anna Klein;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:anna@flow.test
        """;

    [Fact]
    public async Task Outlook_Serie_mit_Windows_Zeitzone_annehmen_und_Antwort_geht_an_den_Organisator()
    {
        var uid = "040000008200E00074C5B7101A82E00800000000" + Guid.NewGuid().ToString("N");
        var invite = Foreign(Laura, "anna@flow.test", "REQUEST", "Lenkungskreis", OutlookSeries(uid, 0));
        Assert.Equal(new DateTime(2030, 6, 3, 8, 0, 0, DateTimeKind.Utc), Read("anna", invite).Start);

        Assert.True(await Respond("anna", invite, "accept"));
        Assert.Equal(["2030-06-03T08:00:00Z", "2030-06-10T08:00:00Z", "2030-06-17T08:00:00Z"],
            (await Events("anna", "Lenkungskreis")).Where(e => e.GetProperty("uid").GetString() == uid).Select(Start));
        await _fx.Deliver();

        // Antwort an Laura: REPLY mit derselben UID und Annas Zusage
        var reply = _fx.Inbox["extern"].Last(m => Invites.Calendar(m)?.Of("VEVENT").Any(e => e.Text("UID") == uid) == true);
        var cal = Invites.Calendar(reply)!;
        Assert.Equal("REPLY", cal.Text("METHOD"));
        var att = Assert.Single(cal.Of("VEVENT").Single().All("ATTENDEE"));
        Assert.Equal("mailto:anna@flow.test", att.Value);
        Assert.Equal("ACCEPTED", att.Param("PARTSTAT"));
        Assert.Equal(Laura, reply.To.Mailboxes.Single().Address);
    }

    [Fact]
    public async Task Outlook_verschiebt_einen_Termin_der_Serie_und_die_Serie_bleibt()
    {
        var uid = "040000008200E00074C5B7101A82E00800000001" + Guid.NewGuid().ToString("N");
        await Respond("anna", Foreign(Laura, "anna@flow.test", "REQUEST", "Lenkungskreis", OutlookSeries(uid, 0)), "accept");

        // Outlook schickt bei "nur dieses Vorkommen" nur das Vorkommen mit RECURRENCE-ID
        var single = Foreign(Laura, "anna@flow.test", "REQUEST", "Lenkungskreis", $"""
            UID:{uid}
            RECURRENCE-ID;TZID=W. Europe Standard Time:20300610T100000
            SEQUENCE:1
            DTSTAMP:20300402T080000Z
            DTSTART;TZID=W. Europe Standard Time:20300611T140000
            DTEND;TZID=W. Europe Standard Time:20300611T150000
            SUMMARY:Lenkungskreis
            ORGANIZER;CN=Laura Neumann:mailto:{Laura}
            ATTENDEE;CN=Anna Klein;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:anna@flow.test
            """);
        await Respond("anna", single, "accept");
        var events = await Events("anna", "Lenkungskreis");
        Assert.Equal(["2030-06-03T08:00:00Z", "2030-06-11T12:00:00Z", "2030-06-17T08:00:00Z"], events.Where(e => e.GetProperty("uid").GetString() == uid).Select(Start));
    }

    [Fact]
    public async Task Outlook_sagt_einen_Termin_der_Serie_ab_und_die_anderen_bleiben()
    {
        var uid = "040000008200E00074C5B7101A82E00800000002" + Guid.NewGuid().ToString("N");
        await Respond("anna", Foreign(Laura, "anna@flow.test", "REQUEST", "Lenkungskreis", OutlookSeries(uid, 0)), "accept");

        var cancel = Foreign(Laura, "anna@flow.test", "CANCEL", "Abgesagt: Lenkungskreis", $"""
            UID:{uid}
            RECURRENCE-ID;TZID=W. Europe Standard Time:20300610T100000
            SEQUENCE:1
            DTSTAMP:20300402T080000Z
            DTSTART;TZID=W. Europe Standard Time:20300610T100000
            DTEND;TZID=W. Europe Standard Time:20300610T110000
            STATUS:CANCELLED
            SUMMARY:Lenkungskreis
            ORGANIZER;CN=Laura Neumann:mailto:{Laura}
            ATTENDEE;CN=Anna Klein:mailto:anna@flow.test
            """);
        Assert.NotNull(Read("anna", cancel).CalendarId);
        await Respond("anna", cancel, "remove");
        var events = await Events("anna", "Lenkungskreis");
        Assert.Equal(["2030-06-03T08:00:00Z", "2030-06-17T08:00:00Z"], events.Where(e => e.GetProperty("uid").GetString() == uid).Select(Start));
    }

    [Fact]
    public async Task Outlook_Teilnehmer_sagt_einen_Termin_der_Serie_ab_und_nur_dort_steht_die_Absage()
    {
        var ev = await Invite("Teamfruehstueck", "2030-07-01T06:00:00Z", "2030-07-01T07:00:00Z", [Laura], "FREQ=WEEKLY;BYDAY=MO;COUNT=3");
        await _fx.Deliver();
        Assert.Contains(_fx.Inbox["extern"], m => m.Subject == "Einladung: Teamfruehstueck");

        // Laura (Outlook) sagt die ganze Serie zu, dann nur den 8. Juli ab
        var chrisStart = "20300701T080000";
        MimeMessage Reply(string partstat, string? rid) => Foreign(Laura, "chris@flow.test", "REPLY", partstat + ": Teamfruehstueck", $"""
            UID:{ev.Uid}
            {(rid == null ? "" : "RECURRENCE-ID;TZID=W. Europe Standard Time:" + rid)}
            SEQUENCE:1
            DTSTAMP:20300402T080000Z
            DTSTART;TZID=W. Europe Standard Time:{chrisStart}
            SUMMARY:Teamfruehstueck
            ORGANIZER:mailto:chris@flow.test
            ATTENDEE;PARTSTAT={partstat};CN=Laura Neumann:mailto:{Laura}
            """.Replace("\n\n", "\n"));
        await Respond("chris", Reply("ACCEPTED", null), "apply");
        await Respond("chris", Reply("DECLINED", "20300708T080000"), "apply");

        var events = await Events("chris", "Teamfruehstueck");
        Assert.Equal(["ACCEPTED", "DECLINED", "ACCEPTED"], events.Select(e => Status(e, Laura)));
    }
}
