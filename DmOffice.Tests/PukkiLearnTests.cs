using DmOffice.Chat;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Pukki lernt je Benutzer: „Anderer Kalender“ wird zur Vorliebe, 👎 fragt mit Knöpfen nach, die Erklärung landet auf der Lernliste.</summary>
[Collection("server")]
public class PukkiLearnTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiLearnTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public void Umziehen_wird_Vorliebe_und_Daumen_runter_fragt_nach()
    {
        var services = _fx.Factory.Services;
        var chat = services.GetRequiredService<ChatStore>();
        var space = _fx.Space("ben");
        Pukki.Ensure(chat, space.User.Id);
        var room = chat.DirectRoom(space.User.Id, Pukki.Id)!.Value;
        PimStore.EnsureDefaults(space.Db);
        PimStore.CreateCollection(space.Db, PimStore.Calendar, "aerzte", "Arzttermine", null, "VEVENT");
        ChatMessage Last() => chat.Messages(space.User.Id, room, limit: 1)[0];
        void Say(string text)
        {
            var id = chat.Post(space.User.Id, room, text);
            var r = Pukki.Answer(services, space, text, chat, room);
            chat.Post(Pukki.Id, room, r.Text, refs: r.Refs, actions: r.Actions);
        }

        Pukki.Reset(space.User.Id);
        Say("termin morgen 10 Uhr Zahnarzt");
        Assert.DoesNotContain("Arzttermine", Last().Text);
        // „Anderer Kalender …“ → „Arzttermine“
        Pukki.Act(services, chat, space, Last(), "move");
        var pick = Last().Actions!.First(a => a.Label.Contains("Arzttermine"));
        Pukki.Act(services, chat, space, Last(), pick.Key);
        Assert.Contains("Arzttermine", Last().Text);

        // Beim nächsten Zahnarzt gleich dorthin
        Pukki.Reset(space.User.Id);
        Say("termin übermorgen 9 Uhr Zahnarzt");
        var answer = Last();
        Assert.Contains("„Arzttermine“", answer.Text);

        // 👎: Rückfrage mit Knöpfen, „Ich erkläre es“, die Erklärung kommt auf die Lernliste
        Pukki.Rate(services, chat, space, answer, -1);
        var ask = Last();
        Assert.Contains("Was war falsch", ask.Text);
        Assert.Contains(ask.Actions!, a => a.Key == "fb-undo");
        Assert.Contains(ask.Actions!, a => a.Key == "fb-explain");
        Pukki.Act(services, chat, space, ask, "fb-explain");
        Say("Ich wollte nur eine Erinnerung, keinen Termin");
        var entry = Assert.Single(Pukki.Learned(space).Entries, e => e.Message == answer.Id);
        Assert.Equal("Ich wollte nur eine Erinnerung, keinen Termin", entry.Expected);
        Assert.Equal("termin übermorgen 9 Uhr Zahnarzt", entry.Text);

        // Freigeben: landet bei der Verwaltung, mit Anmeldenamen
    }
}
