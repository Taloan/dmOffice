using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Mail;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Eigene Termine mit Teilnehmern: Einladung, Aenderung, Absage ueber den Postausgang (iMIP).</summary>
[Collection("server")]
public class OutgoingInviteTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public OutgoingInviteTests(SharedCalendarFixture fx) => _fx = fx;

    private UserSpace Anna => _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Users["anna"].Id);

    private List<MailQueue.Row> Outbox() => MailQueue.List(Anna.Db);

    private long Personal()
    {
        PimStore.EnsureDefaults(Anna.Db);
        return PimStore.Collections(Anna.Db, PimStore.Calendar).First(c => c.Components.Contains("VEVENT")).Id;
    }

    [Fact]
    public async Task Einladen_aendern_und_absagen_landen_im_Postausgang()
    {
        Anna.UpdateSettings(s =>
        {
            if (!s.MailAccounts.Any(a => a.Email == "anna@example.org"))
                s.MailAccounts.Add(new MailAccount { Email = "anna@example.org", DisplayName = "Anna Beispiel" });
        });
        var anna = _fx.As("anna");
        var cal = Personal();
        var start = Outbox().Count;

        var r = await anna.PostAsJsonAsync("/api/pim/events", new
        {
            calendar = cal, title = "Grillabend", start = "2027-10-08T16:00:00Z", end = "2027-10-08T19:00:00Z", allDay = false, busy = true,
            attendees = new[] { new { email = "xaver@example.com", name = "Xaver" }, new { email = "yvonne@example.com", name = (string?)null } },
        });
        r.EnsureSuccessStatusCode();
        var res = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, res.GetProperty("invited").GetInt32());
        var id = res.GetProperty("id").GetString()!;
        var uri = id.Split('/', 2)[1];

        var ics = PimStore.Object(Anna.Db, cal, uri)!.Data.Replace("\r\n ", "");   // gefaltete Zeilen (RFC 5545) zusammenfuegen
        Assert.Contains("ORGANIZER;CN=Anna Beispiel:mailto:anna@example.org", ics);
        Assert.Contains("mailto:xaver@example.com", ics);
        Assert.Contains("PARTSTAT=NEEDS-ACTION", ics);
        var invite = Outbox().Skip(start).Single();
        Assert.StartsWith("Einladung: Grillabend", invite.Subject);
        Assert.Contains("Xaver", invite.Recipients);
        Assert.Contains("yvonne@example.com", invite.Recipients);

        // Termin liefert die Teilnehmer an die Oberflaeche
        var events = await (await anna.GetAsync($"/api/pim/events?from=2027-10-01T00:00:00Z&to=2027-11-01T00:00:00Z&calendars={cal}")).Content.ReadFromJsonAsync<JsonElement>();
        var ev = events.EnumerateArray().First(e => e.GetProperty("title").GetString() == "Grillabend");
        Assert.Equal(2, ev.GetProperty("attendees").GetArrayLength());
        Assert.Equal("anna@example.org", ev.GetProperty("organizer").GetString());

        // Yvonne entfernt: Aktualisierung an Xaver, Absage an Yvonne
        var etag = PimStore.Object(Anna.Db, cal, uri)!.Etag;
        r = await anna.PostAsJsonAsync("/api/pim/events", new
        {
            calendar = cal, uri, etag, title = "Grillabend", start = "2027-10-08T17:00:00Z", end = "2027-10-08T20:00:00Z", allDay = false, busy = true,
            attendees = new[] { new { email = "xaver@example.com", name = "Xaver" } },
        });
        r.EnsureSuccessStatusCode();
        res = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, res.GetProperty("invited").GetInt32());
        Assert.Equal(1, res.GetProperty("cancelled").GetInt32());
        var after = Outbox().Skip(start + 1).ToList();
        Assert.Contains(after, m => m.Subject.StartsWith("Aktualisiert: Grillabend") && m.Recipients.Contains("Xaver") && !m.Recipients.Contains("yvonne"));
        Assert.Contains(after, m => m.Subject.StartsWith("Abgesagt: Grillabend") && m.Recipients.Contains("yvonne@example.com") && !m.Recipients.Contains("Xaver"));

        // Ganzen Termin loeschen: Absage an die verbliebenen Teilnehmer
        var del = await anna.DeleteAsync($"/api/pim/events/{cal}/{Uri.EscapeDataString(uri)}");
        del.EnsureSuccessStatusCode();
        Assert.Equal(1, (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancelled").GetInt32());
        Assert.Contains(Outbox().Skip(start + 3), m => m.Subject.StartsWith("Abgesagt: Grillabend") && m.Recipients.Contains("Xaver"));
    }

    [Fact]
    public async Task Ohne_Teilnehmer_oder_ohne_Benachrichtigung_geht_nichts_raus()
    {
        var anna = _fx.As("anna");
        var cal = Personal();
        var before = Outbox().Count;
        (await anna.PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Allein", start = "2027-10-09T08:00:00Z", end = "2027-10-09T09:00:00Z", allDay = false, busy = true }))
            .EnsureSuccessStatusCode();
        (await anna.PostAsJsonAsync("/api/pim/events", new
        {
            calendar = cal, title = "Still", start = "2027-10-09T10:00:00Z", end = "2027-10-09T11:00:00Z", allDay = false, busy = true, notify = false,
            attendees = new[] { new { email = "xaver@example.com", name = "Xaver" } },
        })).EnsureSuccessStatusCode();
        Assert.Equal(before, Outbox().Count);
    }
}
