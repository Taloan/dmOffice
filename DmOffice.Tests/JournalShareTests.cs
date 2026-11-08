using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DmOffice.Tests;

/// <summary>Tagebuch teilen (lesen, schreiben), private Einträge, Fotos geteilter Tagebücher; Pukki und das Tagebuch; Termine des Tages.</summary>
[Collection("server")]
public class JournalShareTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public JournalShareTests(ChatFixture fx) => _fx = fx;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<long> Journal(string login) =>
        (await Json(await _fx.As(login).GetAsync("/api/journal/journals"))).EnumerateArray().First(j => j.GetProperty("owner").ValueKind == JsonValueKind.Null).GetProperty("id").GetInt64();

    private static byte[] Png()
    {
        using var bmp = new SKBitmap(300, 200);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Teal);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 90).ToArray();
    }

    private async Task<JsonElement> Photo(string login, string name, long? journal = null)
    {
        var c = _fx.As(login);
        var data = Png();
        var id = (await Json(await c.PostAsJsonAsync("/api/files/uploads", new { name, size = data.Length, mime = "image/png" }))).GetProperty("id").GetString();
        await Json(await c.PutAsync($"/api/files/uploads/{id}?offset=0", new ByteArrayContent(data)));
        return (await Json(await c.PostAsync($"/api/journal/uploads/{id}/attach" + (journal is long j ? $"?journal={j}" : ""), null))).GetProperty("media");
    }

    [Fact]
    public async Task Geteiltes_Tagebuch_ohne_private_Eintraege_und_mit_Fotos()
    {
        var j = await Journal("anna");
        var open = await Photo("anna", "offen.png");
        var secret = await Photo("anna", "geheim.png");
        await Json(await _fx.As("anna").PostAsJsonAsync("/api/journal/entries", new
        {
            journal = j, date = "2030-04-01T10:00:00Z", title = "Ostern", html = "<p>Eier suchen</p>", media = new[] { JsonSerializer.Deserialize<object>(open.GetRawText()) },
        }));
        await Json(await _fx.As("anna").PostAsJsonAsync("/api/journal/entries", new
        {
            journal = j, date = "2030-04-02T10:00:00Z", title = "Nur für mich", html = "<p>Geheim</p>", @private = true, media = new[] { JsonSerializer.Deserialize<object>(secret.GetRawText()) },
        }));

        // Anna teilt mit Ben zum Lesen
        (await _fx.As("anna").PutAsJsonAsync($"/api/pim/shares/{j}", new { members = new[] { _fx.Id("ben") }, readOnly = new[] { _fx.Id("ben") } })).EnsureSuccessStatusCode();
        var shared = (await Json(await _fx.As("ben").GetAsync("/api/journal/journals"))).EnumerateArray().Single(x => x.GetProperty("owner").GetString() == "Anna");
        var sj = shared.GetProperty("id").GetInt64();
        Assert.True(sj < 0);
        Assert.False(shared.GetProperty("canWrite").GetBoolean());
        var mine = (await Json(await _fx.As("anna").GetAsync("/api/journal/journals"))).EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == j);
        Assert.Contains(mine.GetProperty("sharedWith").EnumerateArray(), m => m.GetProperty("id").GetString() == _fx.Id("ben"));

        // Ben sieht nur „Ostern“, nicht den privaten Eintrag
        var list = (await Json(await _fx.As("ben").GetAsync($"/api/journal/entries?journal={sj}"))).EnumerateArray().ToList();
        var easter = Assert.Single(list);
        Assert.Equal("Ostern", easter.GetProperty("title").GetString());
        Assert.Equal("Anna", easter.GetProperty("owner").GetString());
        Assert.Equal(sj, easter.GetProperty("journal").GetInt64());

        // Fotos: das sichtbare ja (Vorschaubild und Inhalt), das private nein
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("ben").GetAsync($"/api/journal/file?journal={sj}&path={Uri.EscapeDataString(open.GetProperty("path").GetString()!)}&size=256")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.As("ben").GetAsync($"/api/journal/file?journal={sj}&path={Uri.EscapeDataString(open.GetProperty("path").GetString()!)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").GetAsync($"/api/journal/file?journal={sj}&path={Uri.EscapeDataString(secret.GetProperty("path").GetString()!)}")).StatusCode);
        // Cleo hat keine Freigabe
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("cleo").GetAsync($"/api/journal/entries?journal={sj}")).StatusCode);

        // Nur lesen: schreiben verboten
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").PostAsJsonAsync("/api/journal/entries", new { journal = sj, date = "2030-04-03T10:00:00Z", html = "<p>x</p>" })).StatusCode);

        // Mit Bearbeitungsrecht: Ben schreibt hinein, sein Foto landet bei Anna; privat gibt es für ihn dort nicht
        (await _fx.As("anna").PutAsJsonAsync($"/api/pim/shares/{j}", new { members = new[] { _fx.Id("ben") } })).EnsureSuccessStatusCode();
        var benPhoto = await Photo("ben", "von-ben.png", sj);
        var saved = await Json(await _fx.As("ben").PostAsJsonAsync("/api/journal/entries", new
        {
            journal = sj, date = "2030-04-03T10:00:00Z", title = "Von Ben", html = "<p>Hallo</p>", @private = true, media = new[] { JsonSerializer.Deserialize<object>(benPhoto.GetRawText()) },
        }));
        Assert.False(saved.GetProperty("private").GetBoolean());
        Assert.NotNull(DmOffice.Dav.FileStore.Get(_fx.Space("anna").Db, benPhoto.GetProperty("path").GetString()!));
        Assert.Contains((await Json(await _fx.As("anna").GetAsync($"/api/journal/entries?journal={j}"))).EnumerateArray(), e => e.GetProperty("title").GetString() == "Von Ben");
        // Den privaten Eintrag von Anna kann Ben auch nicht über seine Adresse ändern
        var secretUri = (await Json(await _fx.As("anna").GetAsync($"/api/journal/entries?journal={j}&q=Geheim"))).EnumerateArray().Single().GetProperty("uri").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await _fx.As("ben").PostAsJsonAsync("/api/journal/entries", new { journal = sj, uri = secretUri, date = "2030-04-02T10:00:00Z", html = "<p>überschrieben</p>" })).StatusCode);

        // Einträge aus geteilten Tagebüchern gibt nur der Besitzer in den Chat
        Assert.Equal(HttpStatusCode.Forbidden, (await _fx.As("ben").GetAsync($"/api/journal/entries/{sj}/{easter.GetProperty("uri").GetString()}/chat")).StatusCode);
        var chat = await Json(await _fx.As("anna").GetAsync($"/api/journal/entries/{j}/{easter.GetProperty("uri").GetString()}/chat"));
        Assert.Contains("Ostern", chat.GetProperty("text").GetString());
        Assert.Single(chat.GetProperty("refs").EnumerateArray());
    }

    [Fact]
    public async Task Pukki_fragt_abends_und_schreibt_ins_Tagebuch()
    {
        var chat = _fx.Factory.Services.GetRequiredService<ChatStore>();
        var space = _fx.Space("dora");
        Pukki.Ensure(chat, _fx.Id("dora"));
        var room = chat.DirectRoom(_fx.Id("dora"), Pukki.Id)!.Value;

        // Einstellung: um 00:00 fragen (also gleich)
        var set = await Json(await _fx.As("dora").PutAsJsonAsync("/api/journal/settings", new { ask = "00:00" }));
        Assert.Equal("00:00", set.GetProperty("ask").GetString());
        Pukki.JournalAsk(_fx.Factory.Services, chat, space, room);
        Assert.StartsWith("Wie war dein Tag?", chat.Messages(_fx.Id("dora"), room).Last().Text);
        // Nur einmal am Tag
        var before = chat.Messages(_fx.Id("dora"), room).Count;
        Pukki.JournalAsk(_fx.Factory.Services, chat, space, room);
        Assert.Equal(before, chat.Messages(_fx.Id("dora"), room).Count);

        var reply = Pukki.Answer(_fx.Factory.Services, space, "Schöner Tag im Garten, viel Sonne.", chat, room);
        Assert.StartsWith("Danke – steht in deinem Tagebuch", reply.Text);
        var e = Assert.Single((await Json(await _fx.As("dora").GetAsync("/api/journal/entries"))).EnumerateArray());
        Assert.Equal("Schöner Tag im Garten, viel Sonne.", e.GetProperty("snippet").GetString());

        // Danach wieder normale Gespräche - und „Tagebuch: …“ hängt am heutigen Eintrag an
        Assert.DoesNotContain("Tagebuch", Pukki.Answer(_fx.Factory.Services, space, "Danke dir", chat, room).Text);
        reply = Pukki.Answer(_fx.Factory.Services, space, "Tagebuch: Abends noch gegrillt.", chat, room);
        Assert.StartsWith("Danke – ich habe es zum heutigen Eintrag", reply.Text);
        e = Assert.Single((await Json(await _fx.As("dora").GetAsync("/api/journal/entries"))).EnumerateArray());
        Assert.Contains("Abends noch gegrillt.", e.GetProperty("snippet").GetString());

        // Ausschalten
        Assert.Equal(JsonValueKind.Null, (await Json(await _fx.As("dora").PutAsJsonAsync("/api/journal/settings", new { ask = "" }))).GetProperty("ask").ValueKind);
    }

    [Fact]
    public async Task Termine_Vorschlag_Stimmung_und_Wetter_nur_eingeschaltet()
    {
        PimStore.EnsureDefaults(_fx.Space("cleo").Db);
        var cal = PimStore.Collections(_fx.Space("cleo").Db, PimStore.Calendar).First(c => c.Components.Contains("VEVENT")).Id;
        await Json(await _fx.As("cleo").PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Zahnarzt", start = "2030-05-06T08:00:00Z", end = "2030-05-06T09:00:00Z", allDay = false, busy = true }));
        var events = (await Json(await _fx.As("cleo").GetAsync("/api/journal/events?date=2030-05-06"))).EnumerateArray().ToList();
        Assert.Equal("Zahnarzt", Assert.Single(events).GetProperty("title").GetString());
        Assert.Equal("10:00", events[0].GetProperty("time").GetString());
        var suggest = await Json(await _fx.As("cleo").GetAsync("/api/journal/suggest?date=2030-05-06"));
        Assert.Contains("10:00 Zahnarzt", suggest.GetProperty("html").GetString());
        // Termine des Folgetags (ganztägig und am Morgen) gehören nicht dazu
        await Json(await _fx.As("cleo").PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Geburtstag", start = "2030-05-07", end = "2030-05-08", allDay = true, busy = false }));
        await Json(await _fx.As("cleo").PostAsJsonAsync("/api/pim/events", new { calendar = cal, title = "Autoreifen", start = "2030-05-07T07:00:00Z", end = "2030-05-07T08:00:00Z", allDay = false, busy = true }));
        Assert.Single((await Json(await _fx.As("cleo").GetAsync("/api/journal/events?date=2030-05-06"))).EnumerateArray());
        Assert.Equal(["Geburtstag", "Autoreifen"], (await Json(await _fx.As("cleo").GetAsync("/api/journal/events?date=2030-05-07"))).EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var j = await Journal("cleo");
        foreach (var (d, m) in new[] { ("2030-05-01T10:00:00Z", 2), ("2030-05-01T18:00:00Z", 4), ("2030-05-02T10:00:00Z", 5) })
            await Json(await _fx.As("cleo").PostAsJsonAsync("/api/journal/entries", new { journal = j, date = d, html = "<p>x</p>", mood = m }));
        var moods = (await Json(await _fx.As("cleo").GetAsync("/api/journal/moods?from=2030-01-01T00:00:00Z"))).EnumerateArray().ToList();
        Assert.Equal([3.0, 5.0], moods.Select(x => x.GetProperty("mood").GetDouble()));

        // Wetter und Ortsname gehen nur eingeschaltet an Dienste außerhalb
        Assert.Equal(HttpStatusCode.BadRequest, (await _fx.As("cleo").GetAsync("/api/journal/lookup?lat=48.2&lon=16.4&date=2030-05-06T10:00:00Z")).StatusCode);
        Assert.True((await Json(await _fx.As("cleo").PutAsJsonAsync("/api/journal/settings", new { lookup = true }))).GetProperty("lookup").GetBoolean());
        Assert.Equal("Gewitter", DmOffice.Journal.JournalLookup.Describe(95));
        Assert.Equal("sonnig", DmOffice.Journal.JournalLookup.Describe(0));
    }

    [Fact]
    public void Wetter_ist_das_haeufigste_am_Tag_nicht_der_Morgennebel()
    {
        // Nebel bis 10 Uhr, danach sonnig - der Tageswert von Open-Meteo wäre 45 (Nebel)
        var times = string.Join(",", Enumerable.Range(0, 24).Select(h => $"\"2026-10-27T{h:00}:00\""));
        var codes = string.Join(",", Enumerable.Range(0, 24).Select(h => h <= 10 ? "45" : "0"));
        using var doc = System.Text.Json.JsonDocument.Parse($"{{\"hourly\":{{\"time\":[{times}],\"weather_code\":[{codes}]}}}}");
        Assert.Equal(0, DmOffice.Journal.JournalLookup.Daytime(doc.RootElement));
        using var none = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Null(DmOffice.Journal.JournalLookup.Daytime(none.RootElement));
        // Zur Uhrzeit des Eintrags: Wetter und Temperatur dieser Stunde
        var temps = string.Join(",", Enumerable.Range(0, 24).Select(h => (h / 2).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        using var withTemp = System.Text.Json.JsonDocument.Parse($"{{\"hourly\":{{\"time\":[{times}],\"weather_code\":[{codes}],\"temperature_2m\":[{temps}]}}}}");
        Assert.Equal((45.0, 4.0), DmOffice.Journal.JournalLookup.AtHour(withTemp.RootElement, 8));
        Assert.Equal((0.0, 7.0), DmOffice.Journal.JournalLookup.AtHour(withTemp.RootElement, 14));
    }
}
