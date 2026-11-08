using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Knöpfe unter Pukkis Antworten (Rückgängig, Auswahl, Woanders hin) und Mahlzeiten in den Essensplan.</summary>
[Collection("server")]
public class PukkiActionsTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiActionsTests(ChatFixture fx) => _fx = fx;

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

    /// <summary>Auf Pukkis nächste Antwort warten (nach Nachricht oder Knopf).</summary>
    private async Task<JsonElement> Next(string login, long room, int before)
    {
        for (int i = 0; i < 50; i++)
        {
            var reply = (await Messages(login, room)).Skip(before).LastOrDefault(m => m.GetProperty("sender").GetString() == Pukki.Id);
            if (reply.ValueKind != JsonValueKind.Undefined) return reply;
            await Task.Delay(100);
        }
        throw new TimeoutException("Pukki hat nicht geantwortet.");
    }

    private async Task<JsonElement> Ask(string login, long room, string text)
    {
        var before = (await Messages(login, room)).Count + 1;
        await Json(await _fx.As(login).PostAsJsonAsync($"/api/chat/rooms/{room}/messages", new { text }));
        return await Next(login, room, before);
    }

    private async Task<JsonElement> Click(string login, long room, JsonElement message, string key)
    {
        var before = (await Messages(login, room)).Count;
        var r = await _fx.As(login).PostAsync($"/api/chat/messages/{message.GetProperty("id").GetInt64()}/actions/{key}", null);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await Next(login, room, before);
    }

    private static List<string> Keys(JsonElement m) => m.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("key").GetString()!).ToList();
    private static List<string> Labels(JsonElement m) => m.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("label").GetString()!).ToList();

    private async Task<JsonElement> Reload(string login, long room, JsonElement m) =>
        (await Messages(login, room)).Single(x => x.GetProperty("id").GetInt64() == m.GetProperty("id").GetInt64());

    [Fact]
    public async Task Faellige_Aufgabe_per_Knopf_unter_dem_Ueberblick_abhaken()
    {
        var room = await PukkiRoom("anna");
        var space = _fx.Space("anna");
        await Ask("anna", room, "aufgabe Blumen gießen heute");
        var overview = await Ask("anna", room, "was steht heute an");
        var label = Labels(overview).FirstOrDefault(l => l.Contains("Blumen gießen"));
        Assert.NotNull(label);
        var key = Keys(overview)[Labels(overview).IndexOf(label!)];
        Assert.StartsWith("done", key);
        Assert.Contains("Erledigt: Blumen gießen", (await Click("anna", room, overview, key)).GetProperty("text").GetString());
        var tasks = CalendarModel.Tasks(space, PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO")).Select(c => c.Id));
        Assert.Contains(tasks, t => t.Title == "Blumen gießen" && t.Completed);
        // Nur dieser Knopf ist weg
        Assert.DoesNotContain(key, Keys(await Reload("anna", room, overview)));
    }

    [Fact]
    public async Task Erledigt_als_Antwort_hakt_die_faellige_Aufgabe_ab()
    {
        var room = await PukkiRoom("ben");
        var space = _fx.Space("ben");
        await Ask("ben", room, "aufgabe Altglas wegbringen heute");
        await Ask("ben", room, "aufgabe Paket abholen heute");
        // Zwei offen: Pukki fragt mit Knöpfen nach
        var which = await Ask("ben", room, "ist erledigt");
        Assert.Contains("Welche", which.GetProperty("text").GetString());
        Assert.Contains(Labels(which), l => l.Contains("Altglas wegbringen"));
        // Mit Namen: genau die
        Assert.Contains("Abgehakt: Paket abholen", (await Ask("ben", room, "paket abholen erledigt")).GetProperty("text").GetString());
        // Jetzt nur noch eine: ohne Namen
        Assert.Contains("Erledigt: Altglas wegbringen", (await Ask("ben", room, "erledigt")).GetProperty("text").GetString());
        var tasks = CalendarModel.Tasks(space, PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO")).Select(c => c.Id));
        Assert.All(tasks.Where(t => t.Title is "Altglas wegbringen" or "Paket abholen"), t => Assert.True(t.Completed));
    }

    [Fact]
    public async Task Rueckgaengig_per_Knopf_und_Rueckfrage_per_Knopf()
    {
        var room = await PukkiRoom("anna");
        var space = _fx.Space("anna");
        var done = await Ask("anna", room, "aufgabe Rasen mähen");
        Assert.Contains("Notiert: Rasen mähen", done.GetProperty("text").GetString());
        Assert.DoesNotContain("nein", done.GetProperty("text").GetString());
        Assert.Contains("undo", Keys(done));
        // Die Daten des Knopfs bleiben auf dem Server
        Assert.DoesNotContain("data", done.GetProperty("actions")[0].GetRawText(), StringComparison.OrdinalIgnoreCase);

        // Dazwischen eine andere Aktion - der Knopf nimmt trotzdem genau seine zurück
        await Ask("anna", room, "aufgabe Hecke schneiden");
        Assert.Contains("Zurückgenommen: Aufgabe „Rasen mähen“", (await Click("anna", room, done, "undo")).GetProperty("text").GetString());
        var tasks = CalendarModel.Tasks(space, PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO")).Select(c => c.Id));
        Assert.DoesNotContain(tasks, t => t.Title == "Rasen mähen");
        Assert.Contains(tasks, t => t.Title == "Hecke schneiden");
        Assert.Empty(Keys(await Reload("anna", room, done)));
        Assert.Contains("schon erledigt", (await Click("anna", room, done, "undo")).GetProperty("text").GetString());

        // Rückfrage: die Antworten sind Knöpfe
        var ask = await Ask("anna", room, "Fahrradschloss");
        Assert.Contains("Als Aufgabe", Labels(ask));
        Assert.Contains("Nein", Labels(ask));
        var key = Keys(ask)[Labels(ask).IndexOf("Als Aufgabe")];
        Assert.Contains("Notiert: Fahrradschloss", (await Click("anna", room, ask, key)).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Mahlzeit_kommt_in_den_Essensplan()
    {
        var room = await PukkiRoom("ben");
        var space = _fx.Space("ben");
        Assert.Contains("noch keinen Essensplan", (await Ask("ben", room, "abendessen Spaghetti")).GetProperty("text").GetString());

        var plan = PimStore.CreateCollection(space.Db, PimStore.Calendar, "essen-" + Guid.NewGuid().ToString("N")[..6], "Essen", null, "VEVENT");
        CalendarKinds.Set(space.Db, plan.Id, CalendarKinds.Meal);
        var reply = await Ask("ben", room, "morgen abendessen Spaghetti");
        Assert.Contains("Im Essensplan „Essen“", reply.GetProperty("text").GetString());
        Assert.Contains("Abendessen – Spaghetti", reply.GetProperty("text").GetString());
        var tomorrow = DateOnly.FromDateTime(Pukki.Now(space)).AddDays(1);
        var ev = CalendarModel.Events(space, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(3), [plan.Id]).Single();
        Assert.Equal("Spaghetti", ev.Title);
        Assert.Equal(["Abendessen"], ev.Categories);
        Assert.Equal(tomorrow.ToDateTime(new TimeOnly(18, 0)), Pukki.Local(ev.Start, space.Zone));
        Assert.False(ev.Busy);

        // „mittags gibt es …“ ohne Datum: heute; „heute Abend Kino“ ist keine Mahlzeit
        Assert.Contains("Mittagessen – Linsensuppe", (await Ask("ben", room, "mittags gibt es Linsensuppe")).GetProperty("text").GetString());
        Assert.DoesNotContain("Essensplan", (await Ask("ben", room, "heute abend 20 Uhr Kino")).GetProperty("text").GetString());

        // Nachschauen: was gibt es zu essen?
        var heute = (await Ask("ben", room, "hey was gibt es heute zu essen")).GetProperty("text").GetString()!;
        Assert.Contains("Heute im Essensplan", heute);
        Assert.Contains("Mittagessen – Linsensuppe", heute);
        Assert.DoesNotContain("Spaghetti", heute);
        Assert.Contains("Abendessen – Spaghetti", (await Ask("ben", room, "Was essen wir morgen?")).GetProperty("text").GetString());
        Assert.Contains("Zum Frühstück steht morgen noch nichts", (await Ask("ben", room, "Was gibt es morgen zum Frühstück?")).GetProperty("text").GetString());
        var woche = (await Ask("ben", room, "Essensplan diese Woche")).GetProperty("text").GetString()!;
        Assert.Contains("Linsensuppe", woche);
        Assert.Contains("Spaghetti", woche);

        // Zwei Essenspläne: Auswahl per Knopf
        var second = PimStore.CreateCollection(space.Db, PimStore.Calendar, "essen2-" + Guid.NewGuid().ToString("N")[..6], "Familie", null, "VEVENT");
        CalendarKinds.Set(space.Db, second.Id, CalendarKinds.Meal);
        var choose = await Ask("ben", room, "frühstück Müsli");
        Assert.Contains("In welchen Essensplan", choose.GetProperty("text").GetString());
        Assert.Equal(["Essen", "Familie"], Labels(choose));
        Assert.Contains("Im Essensplan „Familie“", (await Click("ben", room, choose, Keys(choose)[1])).GetProperty("text").GetString());
        Assert.Contains(CalendarModel.Events(space, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(3), [second.Id]), e => e.Title == "Müsli" && e.Categories.Contains("Frühstück"));
    }

    [Fact]
    public async Task Woanders_hin_zieht_auf_die_andere_Liste_um()
    {
        var room = await PukkiRoom("cleo");
        var space = _fx.Space("cleo");
        PimStore.EnsureDefaults(space.Db);
        var a = PimStore.CreateCollection(space.Db, PimStore.Calendar, "shop-a", "Supermarkt", null, "VTODO");
        Lists.Update(space.Db, a.Id, Lists.Shop, null);
        var b = PimStore.CreateCollection(space.Db, PimStore.Calendar, "shop-b", "Drogerie", null, "VTODO");
        Lists.Update(space.Db, b.Id, Lists.Shop, null);

        var added = await Ask("cleo", room, "Milch und Brot");
        Assert.Contains("Andere Liste …", Labels(added));
        var where = await Click("cleo", room, added, "move");
        Assert.Contains("Wohin stattdessen", where.GetProperty("text").GetString());
        var other = Labels(where).Single();
        Assert.Contains($"Auf der Einkaufsliste „{other}“", (await Click("cleo", room, where, "pick0")).GetProperty("text").GetString());

        var from = added.GetProperty("text").GetString()!.Contains("Supermarkt") ? a : b;
        var to = from == a ? b : a;
        Assert.Empty(CalendarModel.Tasks(space, [from.Id]));
        Assert.Equal(2, CalendarModel.Tasks(space, [to.Id]).Count);
        Assert.Empty(Keys(await Reload("cleo", room, added)));
    }
}
