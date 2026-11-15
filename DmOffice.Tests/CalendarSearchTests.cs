using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Terminsuche: alle Wörter in Titel, Ort, Beschreibung; ohne Zeitgrenze; Serien als ein Treffer.</summary>
[Collection("server")]
public class CalendarSearchTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public CalendarSearchTests(SharedCalendarFixture fx) => _fx = fx;

    private async Task<long> Calendar(string name)
    {
        var r = await _fx.As("anna").PostAsJsonAsync("/api/pim/collections/calendars", new { name, color = "#2E7D32", components = "VEVENT" });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private async Task Add(long calendar, string title, string start, string end, string? location = null, string? description = null, string? rrule = null) =>
        (await _fx.As("anna").PostAsJsonAsync("/api/pim/events", new { calendar, title, start, end, allDay = false, location, description, rrule })).EnsureSuccessStatusCode();

    private async Task<List<string>> Search(string q, long calendar) =>
        (await (await _fx.As("anna").GetAsync($"/api/pim/events/search?q={Uri.EscapeDataString(q)}&calendars={calendar}")).Content.ReadFromJsonAsync<JsonElement>())
        .EnumerateArray().Select(e => e.GetProperty("title").GetString() + "@" + e.GetProperty("start").GetString()![..4]).ToList();

    [Fact]
    public async Task Findet_in_Titel_Ort_Beschreibung_ohne_Zeitgrenze()
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.CalendarSearch));
        try
        {
            var cal = await Calendar("Suche Test");
            await Add(cal, "Zahnarzt Kontrolle", "2015-03-02T08:00:00Z", "2015-03-02T09:00:00Z", location: "Praxis Lindenweg");
            await Add(cal, "Elternabend", "2035-09-10T17:00:00Z", "2035-09-10T19:00:00Z", description: "Klassenfahrt besprechen");
            await Add(cal, "Chorprobe", "2020-01-07T18:00:00Z", "2020-01-07T20:00:00Z", rrule: "FREQ=WEEKLY");

            Assert.Equal(new[] { "Zahnarzt Kontrolle@2015" }, await Search("zahnarzt", cal));       // weit in der Vergangenheit
            Assert.Equal(new[] { "Zahnarzt Kontrolle@2015" }, await Search("lindenweg praxis", cal)); // Ort, Wörter in beliebiger Reihenfolge
            Assert.Equal(new[] { "Elternabend@2035" }, await Search("klassenfahrt", cal));            // Beschreibung, weit voraus
            Assert.Empty(await Search("zahnarzt elternabend", cal));                                   // alle Wörter müssen passen
            var chor = await Search("chor", cal);
            Assert.Single(chor);                                                                        // Serie: ein Treffer, das nächste Vorkommen
            Assert.True(string.CompareOrdinal(chor[0][^4..], DateTime.UtcNow.Year.ToString()) >= 0);
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public async Task Ohne_Freischaltung_nicht_da()
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Append(Features.CalendarSearch));
        try { Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("anna").GetAsync("/api/pim/events/search?q=x")).StatusCode); }
        finally { Features.Set(before); }
    }
}
