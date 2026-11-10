using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Kalenderarten (normal, Essensplan): speichern, lesen, teilen, Altbestand uebernehmen.</summary>
[Collection("server")]
public class CalendarKindTests : IClassFixture<SharedCalendarFixture>
{
    private readonly SharedCalendarFixture _fx;
    public CalendarKindTests(SharedCalendarFixture fx) => _fx = fx;

    private async Task<long> New(string name, string? kind = null)
    {
        var r = await _fx.As("anna").PostAsJsonAsync("/api/pim/collections/calendars", new { name, color = "#2E7D32", components = "VEVENT", calendarKind = kind });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> Find(string login, string name) =>
        (await (await _fx.As(login).GetAsync("/api/pim/collections/calendars")).Content.ReadFromJsonAsync<JsonElement>())
        .EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

    private static string? KindOf(JsonElement c) => c.GetProperty("calendarKind").GetString();

    private async Task Share(long calendar, string[] members, string[] readOnly) =>
        (await _fx.As("anna").PutAsJsonAsync($"/api/pim/shares/{calendar}", new
        {
            members = members.Select(m => _fx.Users[m].Id).ToArray(), readOnly = readOnly.Select(m => _fx.Users[m].Id).ToArray(),
        })).EnsureSuccessStatusCode();

    private static object Meal(long calendar, string title, string meal = "Mittagessen") =>
        new { calendar, title, start = "2027-10-05T12:00:00Z", end = "2027-10-05T13:00:00Z", allDay = false, busy = false, categories = new[] { meal } };

    [Fact]
    public async Task Art_ist_normal_ohne_Angabe_und_laesst_sich_aendern()
    {
        await New("Kind Normal");
        await New("Kind Essen", "meal");
        await New("Kind Unbekannt", "dienstplan");
        Assert.Equal("normal", KindOf(await Find("anna", "Kind Normal")));
        Assert.Equal("meal", KindOf(await Find("anna", "Kind Essen")));
        Assert.Equal("normal", KindOf(await Find("anna", "Kind Unbekannt")));

        var id = (await Find("anna", "Kind Normal")).GetProperty("id").GetInt64();
        (await _fx.As("anna").PutAsJsonAsync($"/api/pim/collections/{id}", new { name = "Kind Normal", calendarKind = "meal" })).EnsureSuccessStatusCode();
        Assert.Equal("meal", KindOf(await Find("anna", "Kind Normal")));
        // Ohne Angabe bleibt die Art beim Umbenennen
        (await _fx.As("anna").PutAsJsonAsync($"/api/pim/collections/{id}", new { name = "Kind Umbenannt" })).EnsureSuccessStatusCode();
        Assert.Equal("meal", KindOf(await Find("anna", "Kind Umbenannt")));
    }

    [Fact]
    public async Task Geteilter_Essensplan_Art_gehoert_dem_Besitzer_und_Leser_koennen_nicht_eintragen()
    {
        var id = await New("Kind Familie", "meal");
        await Share(id, new[] { "ben", "cleo" }, new[] { "ben" });

        var ben = await Find("ben", "Kind Familie");
        Assert.Equal("meal", KindOf(ben));
        Assert.False(ben.GetProperty("canWrite").GetBoolean());
        var cleo = await Find("cleo", "Kind Familie");
        Assert.Equal("meal", KindOf(cleo));
        Assert.True(cleo.GetProperty("canWrite").GetBoolean());

        // Ben: lesen ja, eintragen und die Art aendern nein
        var benId = ben.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync("/api/pim/events", Meal(benId, "Von Ben"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PutAsJsonAsync($"/api/pim/collections/{benId}", new { name = "Kind Familie", calendarKind = "normal" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("cleo").PutAsJsonAsync($"/api/pim/collections/{cleo.GetProperty("id").GetInt64()}", new { name = "Kind Familie", calendarKind = "normal" })).StatusCode);
        Assert.True((await _fx.As("cleo").PostAsJsonAsync("/api/pim/events", Meal(cleo.GetProperty("id").GetInt64(), "Von Cleo"))).IsSuccessStatusCode);

        // Wechselt der Besitzer die Art, gilt das fuer alle Empfaenger
        (await _fx.As("anna").PutAsJsonAsync($"/api/pim/collections/{id}", new { name = "Kind Familie", calendarKind = "normal" })).EnsureSuccessStatusCode();
        Assert.Equal("normal", KindOf(await Find("ben", "Kind Familie")));
    }

    [Fact]
    public async Task Mehrere_Essensplan_Kalender_liefern_ihre_Mahlzeiten_zusammen()
    {
        var a = await New("Kind Plan A", "meal");
        var b = await New("Kind Plan B", "meal");
        var anna = _fx.As("anna");
        Assert.True((await anna.PostAsJsonAsync("/api/pim/events", Meal(a, "Bauerntopf"))).IsSuccessStatusCode);
        Assert.True((await anna.PostAsJsonAsync("/api/pim/events", Meal(b, "Pizza", "Abendessen"))).IsSuccessStatusCode);
        var list = (await (await anna.GetAsync($"/api/pim/events?from=2027-10-01T00:00:00Z&to=2027-11-01T00:00:00Z&calendars={a},{b}")).Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().ToList();
        Assert.Equal(new[] { a, b }, list.Select(e => e.GetProperty("calendar").GetInt64()).OrderBy(x => x).ToArray());
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Altbestand_mit_Mahlzeiten_wird_beim_ersten_Zugriff_zum_Essensplan()
    {
        var dir = Directory.CreateTempSubdirectory("dmkind");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        PimStore.EnsureDefaults(db);
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        var plain = PimStore.Collections(db, PimStore.Calendar).First();
        var plan = PimStore.CreateCollection(db, PimStore.Calendar, "essen", "Essensplanung", null, "VEVENT");
        var other = PimStore.CreateCollection(db, PimStore.Calendar, "arbeit", "Arbeit", null, "VEVENT");
        var chosen = PimStore.CreateCollection(db, PimStore.Calendar, "gewaehlt", "Gewählt", null, "VEVENT");

        void Save(long cal, string title, params string[] cats) => CalendarModel.SaveEvent(space, new EventInput
        {
            Calendar = cal, Title = title, Start = "2027-10-05T12:00:00Z", End = "2027-10-05T13:00:00Z", Categories = cats.ToList(),
        });
        Save(plan.Id, "Bauerntopf", "Mittagessen");
        Save(other.Id, "Besprechung", "Arbeit");
        Save(other.Id, "Mittagessen mit Kunden");          // nur im Titel, keine Kategorie
        Save(chosen.Id, "Frühstück", "Frühstück");
        db.Set("pim.calkind." + chosen.Id, "normal");        // Besitzer hat schon entschieden

        Assert.Equal("meal", CalendarKinds.Of(db, plan.Id));
        Assert.Equal("normal", CalendarKinds.Of(db, plain.Id));
        Assert.Equal("normal", CalendarKinds.Of(db, other.Id));
        Assert.Equal("normal", CalendarKinds.Of(db, chosen.Id));

        // Nur einmal: ein Kalender, der spaeter Mahlzeiten bekommt, bleibt normal
        Save(other.Id, "Abendessen", "Abendessen");
        Assert.Equal("normal", CalendarKinds.Of(db, other.Id));
        CalendarKinds.Remove(db, plan.Id);
        Assert.Equal("normal", CalendarKinds.Of(db, plan.Id));
    }
}
