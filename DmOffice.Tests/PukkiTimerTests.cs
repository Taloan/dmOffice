using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Timer bei Pukki: starten, laufende zeigen, ändern, abbrechen, Meldung beim Ablauf. Nichts davon im Kalender.</summary>
[Collection("server")]
public class PukkiTimerTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiTimerTests(ChatFixture fx) => _fx = fx;

    private ChatStore Chat => _fx.Factory.Services.GetRequiredService<ChatStore>();

    private long Room(string login)
    {
        Pukki.Ensure(Chat, _fx.Id(login));
        return Chat.DirectRoom(_fx.Id(login), Pukki.Id)!.Value;
    }

    private async Task<ChatMessage> Ask(string login, string text)
    {
        var room = Room(login);
        var before = Chat.Messages(_fx.Id(login), room).Count + 1;
        var r = await _fx.As(login).PostAsJsonAsync($"/api/chat/rooms/{room}/messages", new { text });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        for (int i = 0; i < 50; i++)
        {
            var reply = Chat.Messages(_fx.Id(login), room).Skip(before).LastOrDefault(m => m.Sender == Pukki.Id);
            if (reply != null) return reply;
            await Task.Delay(100);
        }
        throw new TimeoutException("Pukki hat nicht geantwortet.");
    }

    [Fact]
    public async Task Starten_zeigen_aendern_abbrechen()
    {
        const string who = "cleo";
        // Aufräumen aus früheren Läufen
        Chat.DeleteKv("pukki.timers." + _fx.Id(who));

        Assert.Equal("Es läuft gerade kein Timer. Zum Starten: Timer auf 20 Minuten.", (await Ask(who, "Welche Timer laufen?")).Text);

        var start = await Ask(who, "Timer auf 20 Minuten");
        Assert.Equal("Timer läuft: 20 Minuten. Ich melde mich, wenn er abgelaufen ist.", start.Text);
        Assert.StartsWith("Timer „Pizza“ läuft: 15 Minuten.", (await Ask(who, "Stell einen Pizza-Timer auf 15 Minuten")).Text);

        var list = (await Ask(who, "Welche Timer laufen?")).Text;
        Assert.Contains("Laufende Timer:", list);
        Assert.Contains("Timer „Pizza“ – noch 15 Minuten", list);
        Assert.Contains("Timer – noch 20 Minuten", list);

        Assert.Equal("Timer „Pizza“ läuft jetzt noch 20 Minuten.", (await Ask(who, "Pizza-Timer um 5 Minuten verlängern")).Text);

        // Ohne Namen bei zwei Timern: nachfragen, mit Knopf je Timer
        var which = await Ask(who, "Timer abbrechen");
        Assert.StartsWith("Welchen Timer soll ich abbrechen?", which.Text);
        Assert.Equal(2, which.Actions!.Count);

        Assert.Equal("Timer „Pizza“ ist abgebrochen.", (await Ask(who, "Timer für die Pizza abbrechen")).Text);
        Assert.Equal("Timer läuft jetzt noch 5 Minuten.", (await Ask(who, "Timer auf 5 Minuten ändern")).Text);
        Assert.Equal("Timer ist abgebrochen.", (await Ask(who, "Timer stoppen")).Text);
        Assert.Equal("Es läuft gerade kein Timer.", (await Ask(who, "Timer abbrechen")).Text);
    }

    [Fact]
    public async Task Abgelaufen_meldet_sich_einmal()
    {
        const string who = "dora";
        Chat.DeleteKv("pukki.timers." + _fx.Id(who));
        await Ask(who, "Eieruhr 7 Minuten");
        Assert.Empty(Pukki.DueTimers(Chat, _fx.Id(who), DateTime.UtcNow.AddMinutes(6)));
        var due = Assert.Single(Pukki.DueTimers(Chat, _fx.Id(who), DateTime.UtcNow.AddMinutes(8)));
        Assert.Equal("⏰ Der Timer ist abgelaufen (7 Minuten).", Pukki.TimerDoneText(due));
        Assert.Empty(Pukki.DueTimers(Chat, _fx.Id(who), DateTime.UtcNow.AddMinutes(9)));
    }
}
