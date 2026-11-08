using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Notes;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Pukki (Stufe 5): Begrüßung, Kurzbefehle, Überblick, „@Pukki“ in Gruppen.</summary>
[Collection("server")]
public class PukkiTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<long> PukkiRoom(string login) =>
        (await Json(await _fx.As(login).GetAsync("/api/chat/rooms"))).EnumerateArray()
            .Single(r => r.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("id").GetString() == Pukki.Id)).GetProperty("id").GetInt64();

    private async Task<List<JsonElement>> Messages(string login, long room) =>
        (await Json(await _fx.As(login).GetAsync($"/api/chat/rooms/{room}/messages"))).EnumerateArray().ToList();

    /// <summary>Schreiben und auf Pukkis Antwort warten (er antwortet im Hintergrund).</summary>
    private async Task<string> Ask(string login, long room, string text)
    {
        var before = (await Messages(login, room)).Count;
        await Json(await _fx.As(login).PostAsJsonAsync($"/api/chat/rooms/{room}/messages", new { text }));
        for (int i = 0; i < 50; i++)
        {
            var list = await Messages(login, room);
            var reply = list.Skip(before + 1).FirstOrDefault(m => m.GetProperty("sender").GetString() == Pukki.Id);
            if (reply.ValueKind != JsonValueKind.Undefined) return reply.GetProperty("text").GetString()!;
            await Task.Delay(100);
        }
        throw new TimeoutException("Pukki hat nicht geantwortet.");
    }

    [Fact]
    public async Task Begruessung_Kurzbefehle_und_Ueberblick()
    {
        var room = await PukkiRoom("anna");
        var first = (await Messages("anna", room)).First();
        Assert.Equal(Pukki.Id, first.GetProperty("sender").GetString());
        Assert.Contains("Ich bin Pukki", first.GetProperty("text").GetString());
        var rooms = await Json(await _fx.As("anna").GetAsync("/api/chat/rooms"));
        Assert.Contains(rooms.EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == room).GetProperty("members").EnumerateArray(), m => m.GetProperty("name").GetString() == "Pukki");

        var space = _fx.Space("anna");
        var title = "Zahnarzt" + Guid.NewGuid().ToString("N")[..4];
        var reply = await Ask("anna", room, $"termin morgen 15 Uhr {title}");
        Assert.Contains("Eingetragen", reply);
        var tomorrow = DateOnly.FromDateTime(Pukki.Now(space)).AddDays(1);
        var ev = Pukki.DayEvents(_fx.Factory.Services, space, tomorrow).Single(e => e.Title == title);
        Assert.Equal(new TimeOnly(15, 0), ev.Start);
        Assert.Equal(new TimeOnly(16, 0), ev.End);
        Assert.Equal("🩺", ev.Icon);

        Assert.Contains("Notiert", await Ask("anna", room, "aufgabe Milch kaufen"));
        var tasks = CalendarModel.Tasks(space, PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO")).Select(c => c.Id));
        Assert.Contains(tasks, t => t.Title == "Milch kaufen");

        Assert.Contains("Notiz angelegt", await Ask("anna", room, "notiz Ideen für den Urlaub"));
        Assert.Contains(NotesDb.Of(space).GetAllActive(), n => n.DisplayTitle.StartsWith("Ideen für den Urlaub"));

        Assert.Contains(title, await Ask("anna", room, "Was steht morgen an?"));
    }

    [Fact]
    public async Task Hat_sich_erledigt_laesst_die_Rueckfrage_fallen()
    {
        var room = await PukkiRoom("ben");
        Assert.Contains("Wann soll ich dich", await Ask("ben", room, "erinnere mich an Lotto spielen"));
        Assert.Equal("Gut, dann lasse ich das.", await Ask("ben", room, "haben wir schon erledigt"));
        // Auch wenn zwischendurch etwas anderes kam
        Assert.Contains("Wann soll ich dich", await Ask("ben", room, "erinnere mich an Lotto spielen"));
        await Ask("ben", room, "Wie geht's?");
        Assert.Equal("Gut, dann lasse ich das.", await Ask("ben", room, "Hat sich erledigt!"));
    }

    [Fact]
    public async Task Freie_Saetze_Einkauf_Rueckfrage_und_Zuruecknehmen()
    {
        var room = await PukkiRoom("ben");
        var space = _fx.Space("ben");
        Assert.Contains("Mir geht's gut", await Ask("ben", room, "Wie geht's?"));
        Assert.Contains("Gern geschehen", await Ask("ben", room, "Danke!"));

        // Einkauf ohne Befehlswort: Liste wird angelegt, Mengen und Abteilungen erkannt
        var shop = await Ask("ben", room, "Milch, 6 Eier und 2 kg Kartoffeln");
        Assert.Contains("Einkaufsliste", shop);
        var list = PimStore.Collections(space.Db, PimStore.Calendar).Single(c => Lists.TypeOf(space.Db, c.Id) == Lists.Shop);
        var items = CalendarModel.Tasks(space, [list.Id]);
        Assert.Contains(items, i => i.Title == "Eier" && i.Qty == "6" && i.Section == "Kühlregal");
        Assert.Contains(items, i => i.Title == "Kartoffeln" && i.Qty == "2" && i.Unit == "kg" && i.Section == "Obst & Gemüse");
        // „nein“ nimmt alles Angelegte zurück
        Assert.Contains("Zurückgenommen", await Ask("ben", room, "nein"));
        Assert.Empty(CalendarModel.Tasks(space, [list.Id]));

        // Freie Sätze: Uhrzeit → Termin, Erledigungswort → Aufgabe, Termin-Wort mit Datum → ganztägig
        Assert.Contains("Eingetragen", await Ask("ben", room, "morgen um 9 Friseur"));
        Assert.Contains("Notiert: Arzt anrufen", await Ask("ben", room, "Arzt anrufen"));
        Assert.Contains("Notiert: Steuer abgeben – fällig", await Ask("ben", room, "Freitag Steuer abgeben"));
        Assert.Contains("ganztägig", await Ask("ben", room, "Samstag Geburtstag Oma"));

        // Unklar: Rückfrage, „ja“ übernimmt den Vorschlag
        var ask = await Ask("ben", room, "Sonntag Grillen bei Cleo");
        Assert.Contains("als Termin eintragen", ask);
        Assert.Contains("Eingetragen", await Ask("ben", room, "ja"));
        var ask2 = await Ask("ben", room, "Fahrrad");
        Assert.Contains("als Aufgabe notieren", ask2);
        Assert.Contains("Gut, dann lasse ich das", await Ask("ben", room, "nein"));
    }

    [Fact]
    public async Task Gruppenfrage_aus_dem_Gruppen_Kalender_und_Ausblenden()
    {
        var group = (await Json(await _fx.As("cleo").PostAsJsonAsync("/api/chat/rooms", new { title = "Familie P", members = new[] { _fx.Id("dora") } }))).GetProperty("id").GetInt64();
        // Ohne Gruppen-Kalender: Hinweis
        Assert.Contains("keinen Gruppen-Kalender", await Ask("dora", group, "@Pukki wer holt heute die Kinder?"));
        await Json(await _fx.As("cleo").PostAsJsonAsync($"/api/chat/rooms/{group}/spaces", new { kind = "calendar" }));
        var cleo = _fx.Space("cleo");
        var cal = PimStore.Collections(cleo.Db, PimStore.Calendar).Single(c => c.Name == "Familie P");
        var today = DateOnly.FromDateTime(Pukki.Now(cleo));
        CalendarModel.SaveEvent(cleo, new EventInput { Calendar = cal.Id, Title = "Kinder abholen (Cleo)", AllDay = true, Start = today.ToString("yyyy-MM-dd"), End = today.AddDays(1).ToString("yyyy-MM-dd") });
        CalendarModel.SaveEvent(cleo, new EventInput { Calendar = cal.Id, Title = "Elternabend", AllDay = true, Start = today.ToString("yyyy-MM-dd"), End = today.AddDays(1).ToString("yyyy-MM-dd") });

        var answer = await Ask("dora", group, "@Pukki wer holt heute die Kinder?");
        Assert.Contains("Kinder abholen (Cleo)", answer);
        Assert.DoesNotContain("Elternabend", answer);
        // @Pukki auch mitten im Satz; die Antwort erwaehnt den Fragenden
        Assert.Contains("Kinder abholen (Cleo)", await Ask("dora", group, "Weisst du das, @Pukki: wer holt heute die Kinder?"));
        Assert.Contains(_fx.Id("dora"), (await Messages("dora", group)).Last().GetProperty("mentions").EnumerateArray().Select(x => x.GetString()));
        // Pukki ist dabei nicht Mitglied geworden
        var members = (await Json(await _fx.As("dora").GetAsync("/api/chat/rooms"))).EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == group).GetProperty("members");
        Assert.DoesNotContain(members.EnumerateArray(), m => m.GetProperty("id").GetString() == Pukki.Id);

        // Ausgeblendet bleibt ausgeblendet
        var pukki = await PukkiRoom("dora");
        Assert.True((await _fx.As("dora").PostAsync($"/api/chat/rooms/{pukki}/leave", null)).IsSuccessStatusCode);
        Assert.DoesNotContain((await Json(await _fx.As("dora").GetAsync("/api/chat/rooms"))).EnumerateArray(), r => r.GetProperty("id").GetInt64() == pukki);
        Assert.Null(Pukki.RoomOf(_fx.Factory.Services.GetRequiredService<ChatStore>(), _fx.Id("dora")));
    }

    [Fact]
    public async Task Sucht_im_eigenen_Bestand_mit_Verweisen()
    {
        var room = await PukkiRoom("cleo");
        var space = _fx.Space("cleo");
        Assert.Contains("Eingetragen", await Ask("cleo", room, "termin nächsten Dienstag 10 Uhr Zahnarzt Kontrolle"));
        var notes = NotesDb.Of(space);
        var note = notes.CreateNote(null);
        notes.SaveNote(note, "<div data-notizi=\"1\"><p>Fragen an den Zahnarzt</p><p>Weisheitszahn?</p></div>", "Fragen an den Zahnarzt\r\nWeisheitszahn?");
        PimStore.EnsureDefaults(space.Db);
        var book = PimStore.Collections(space.Db, PimStore.AddressBook).First().Id;
        ContactModel.Save(space, new ContactDto { Book = book, DisplayName = "Dr. Weiß", Org = "Zahnarztpraxis am Markt" });

        var answer = await Ask("cleo", room, "Was hatten wir zum Zahnarzt?");
        Assert.Contains("habe ich gefunden", answer);
        Assert.Contains("📅", answer);
        Assert.Contains("📝 Fragen an den Zahnarzt", answer);
        Assert.Contains("👤 Dr. Weiß", answer);
        // Die Treffer hängen als Verweise an der Antwort - der Besitzer kann sie öffnen
        var last = (await Messages("cleo", room)).Last();
        var types = last.GetProperty("refs").EnumerateArray().Select(r => r.GetProperty("type").GetString()).ToList();
        Assert.Contains("event", types);
        Assert.Contains("note", types);
        Assert.Contains("contact", types);

        // Wann-Fragen nennen Kalender, Termin und Uhrzeit (Vorgabe: „Ich habe im Kalender … einen Eintrag zu … gefunden“)
        var when = await Ask("cleo", room, "Wann ist der nächste Zahnarzt?");
        Assert.Contains("einen Eintrag zu „Zahnarzt“ gefunden", when);
        Assert.Contains("10:00 Uhr", when);
        Assert.Contains("finde ich nichts", await Ask("cleo", room, "Such Quantenphysik"));

        // Nur der Ort passt („Biotonne“ in Vöhringen): kein Treffer für „Schlachtplatte in Vöhringen“
        var cal = PimStore.Collections(space.Db, PimStore.Calendar).First(c => c.Components.Contains("VEVENT"));
        var day = DateTime.UtcNow.AddDays(3).ToString("yyyyMMdd");
        PimStore.Put(space.Db, cal, "bio-test.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//t//DE\r\nBEGIN:VEVENT\r\nUID:bio-test\r\nDTSTAMP:20260101T000000Z\r\n"
            + $"DTSTART;VALUE=DATE:{day}\r\nSUMMARY:Biotonne\r\nLOCATION:Neue Wiesen, Vöhringen\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", zone: space.Zone);
        Assert.Contains("finde ich keinen Termin", await Ask("cleo", room, "wann ist die Schlachtplatte in 72189 Vöhringen"));
        Assert.Contains("Biotonne", await Ask("cleo", room, "wann ist die Biotonne"));
    }
}
