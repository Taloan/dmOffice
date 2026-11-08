using System.Net.Http.Json;
using DmOffice.Chat;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Erinnerungen als Aufgaben (Kategorie „Erinnerung“): anlegen, zeigen, verschieben, löschen, fällig melden.</summary>
[Collection("server")]
public class PukkiReminderTaskTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiReminderTaskTests(ChatFixture fx) => _fx = fx;

    private ChatStore Chat => _fx.Factory.Services.GetRequiredService<ChatStore>();

    private async Task<string> Ask(string login, string text)
    {
        Pukki.Ensure(Chat, _fx.Id(login));
        var room = Chat.DirectRoom(_fx.Id(login), Pukki.Id)!.Value;
        var before = Chat.Messages(_fx.Id(login), room).Count + 1;
        var r = await _fx.As(login).PostAsJsonAsync($"/api/chat/rooms/{room}/messages", new { text });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        for (int i = 0; i < 50; i++)
        {
            var reply = Chat.Messages(_fx.Id(login), room).Skip(before).LastOrDefault(m => m.Sender == Pukki.Id);
            if (reply != null) return reply.Text;
            await Task.Delay(100);
        }
        throw new TimeoutException("Pukki hat nicht geantwortet.");
    }

    [Fact]
    public async Task Anlegen_zeigen_verschieben_loeschen()
    {
        const string who = "anna";
        var space = _fx.Space(who);
        PimStore.EnsureDefaults(space.Db);
        foreach (var r in Pukki.ReminderTasks(space)) PimStore.Delete(space.Db, r.List, r.Task.Uri);

        Assert.StartsWith("Okay, ich erinnere dich morgen um 09:00 Uhr an „Müll rausbringen“.", await Ask(who, "Erinnere mich morgen um 9 an Müll rausbringen"));
        Assert.StartsWith("Okay, ich erinnere dich morgen um 18:00 Uhr an „Blumen gießen“.", await Ask(who, "Erinnere mich morgen um 18 Uhr ans Blumen gießen"));

        // Als Aufgabe mit Uhrzeit und Kategorie, nicht im Kalender
        var tasks = Pukki.ReminderTasks(space);
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, r => Assert.Contains(Pukki.ReminderCategory, r.Task.Categories));

        var list = await Ask(who, "Welche Erinnerungen habe ich?");
        Assert.StartsWith("Deine Erinnerungen:", list);
        Assert.Contains("Müll rausbringen – morgen um 09:00 Uhr", list);

        Assert.Equal("Ich erinnere dich jetzt morgen um 10:30 Uhr an „Müll rausbringen“.", await Ask(who, "Erinnerung Müll auf 10:30 verschieben"));
        Assert.Equal("Ich erinnere dich jetzt morgen um 11:00 Uhr an „Müll rausbringen“.", await Ask(who, "Erinnerung Müll um 30 Minuten später"));

        Assert.StartsWith("Welche Erinnerung soll ich löschen?", await Ask(who, "Erinnerung löschen"));
        Assert.Equal("Die Erinnerung „Blumen gießen“ ist gelöscht.", await Ask(who, "Erinnerung an die Blumen löschen"));
        Assert.Equal("Deine Erinnerung: „Müll rausbringen“ – morgen um 11:00 Uhr.", await Ask(who, "Erinnerungen?"));

        // Fällig: nur Aufgaben mit der Kategorie
        var at = Pukki.ReminderTasks(space).Single().AtUtc;
        Assert.Single(Pukki.DueReminderTasks(space, at.AddMinutes(-1), at));
        Assert.Empty(Pukki.DueReminderTasks(space, at, at.AddMinutes(1)));
    }
}
