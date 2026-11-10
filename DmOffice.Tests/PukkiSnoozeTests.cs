using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Noch einmal erinnern: Knöpfe unter der Erinnerung und „Erinnere mich in einer halben Stunde noch mal“.</summary>
[Collection("server")]
public class PukkiSnoozeTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiSnoozeTests(ChatFixture fx) => _fx = fx;

    private ChatStore Chat => _fx.Factory.Services.GetRequiredService<ChatStore>();

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private long Room(string login)
    {
        Pukki.Ensure(Chat, _fx.Id(login));
        return Chat.DirectRoom(_fx.Id(login), Pukki.Id)!.Value;
    }

    private async Task<ChatMessage> Next(string login, long room, int before)
    {
        for (int i = 0; i < 50; i++)
        {
            var reply = Chat.Messages(_fx.Id(login), room).Skip(before).LastOrDefault(m => m.Sender == Pukki.Id);
            if (reply != null) return reply;
            await Task.Delay(100);
        }
        throw new TimeoutException("Pukki hat nicht geantwortet.");
    }

    private async Task<ChatMessage> Ask(string login, string text)
    {
        var room = Room(login);
        var before = Chat.Messages(_fx.Id(login), room).Count + 1;
        await Json(await _fx.As(login).PostAsJsonAsync($"/api/chat/rooms/{room}/messages", new { text }));
        return await Next(login, room, before);
    }

    /// <summary>Wie der Dienst eine Erinnerung schickt: Text mit Knöpfen, und sie wird gemerkt.</summary>
    private ChatMessage Remind(string login, string title, DateTime startUtc)
    {
        var room = Room(login);
        var space = _fx.Space(login);
        Chat.Post(Pukki.Id, room, Pukki.ReminderText(space, title, startUtc), actions: Pukki.SnoozeActions(title, startUtc));
        Pukki.RememberReminder(Chat, _fx.Id(login), title, startUtc);
        return Chat.Messages(_fx.Id(login), room).Last();
    }

    [Fact]
    public async Task Knopf_unter_der_Erinnerung_erinnert_spaeter_noch_einmal()
    {
        var start = DateTime.UtcNow.AddMinutes(1);
        var reminder = Remind("anna", "Zähneputzen", start);
        Assert.Equal(["snooze-10", "snooze-60", "snooze-day"], reminder.Actions!.Select(a => a.Key));

        var room = Room("anna");
        var before = Chat.Messages(_fx.Id("anna"), room).Count;
        var r = await _fx.As("anna").PostAsync($"/api/chat/messages/{reminder.Id}/actions/snooze-10", null);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var reply = await Next("anna", room, before);
        Assert.StartsWith("Okay, ich erinnere dich ", reply.Text);
        Assert.EndsWith("noch einmal an „Zähneputzen“.", reply.Text);
        // Die Knöpfe sind weg, die Wiederholung ist in zehn Minuten fällig - nicht früher
        Assert.Null(Chat.Messages(_fx.Id("anna"), room).Single(m => m.Id == reminder.Id).Actions);
        Assert.Empty(Pukki.DueSnoozes(Chat, _fx.Id("anna"), DateTime.UtcNow.AddMinutes(9)));
        var due = Assert.Single(Pukki.DueSnoozes(Chat, _fx.Id("anna"), DateTime.UtcNow.AddMinutes(11)));
        Assert.Equal("Zähneputzen", due.Title);
        Assert.Empty(Pukki.DueSnoozes(Chat, _fx.Id("anna"), DateTime.UtcNow.AddMinutes(11)));
    }

    [Fact]
    public async Task Noch_mal_wiederholt_die_letzte_Erinnerung_statt_eines_neuen_Termins()
    {
        PimStore.EnsureDefaults(_fx.Space("ben").Db);
        Remind("ben", "Tabletten nehmen", DateTime.UtcNow);

        var reply = await Ask("ben", "Erinnere mich in einer halben Stunde noch mal");
        Assert.StartsWith("Okay, ich erinnere dich ", reply.Text);
        Assert.EndsWith("noch einmal an „Tabletten nehmen“.", reply.Text);
        var due = Assert.Single(Pukki.DueSnoozes(Chat, _fx.Id("ben"), DateTime.UtcNow.AddMinutes(31)));
        Assert.Equal("Tabletten nehmen", due.Title);

        // Kein Termin „Noch mal“ im Kalender
        var events = await Json(await _fx.As("ben").GetAsync($"/api/pim/events?from={DateTime.UtcNow.AddDays(-1):yyyy-MM-ddTHH:mm:ssZ}&to={DateTime.UtcNow.AddDays(2):yyyy-MM-ddTHH:mm:ssZ}"));
        Assert.DoesNotContain(events.EnumerateArray(), e => (e.GetProperty("title").GetString() ?? "").Contains("mal", StringComparison.OrdinalIgnoreCase));

        // Ohne Zeit: Pukki fragt mit Knöpfen nach
        var ask = await Ask("ben", "Erinnere mich nochmal");
        Assert.Equal("Wann soll ich dich noch einmal an „Tabletten nehmen“ erinnern?", ask.Text);
        Assert.Equal(3, ask.Actions!.Count);
    }

    [Fact]
    public async Task Ohne_vorherige_Erinnerung_wird_es_eine_neue_Erinnerung()
    {
        PimStore.EnsureDefaults(_fx.Space("cleo").Db);
        var reply = await Ask("cleo", "Erinnere mich morgen um 9 an den Müll");
        Assert.StartsWith("Okay, ich erinnere dich morgen um 09:00 Uhr an „Müll“.", reply.Text);
    }
}
