using DmOffice.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Vorhaben der Woche: Antwort auf den Wochenrückblick merken, abfragen, „nichts“ akzeptieren, Fragen und Befehle nicht schlucken.</summary>
[Collection("server")]
public class PukkiWeekGoalTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiWeekGoalTests(ChatFixture fx) => _fx = fx;

    private ChatStore Chat => _fx.Factory.Services.GetRequiredService<ChatStore>();

    private string Say(string login, string text) => Pukki.Answer(_fx.Factory.Services, _fx.Space(login), text, Chat, 1).Text;

    [Fact]
    public void Antwort_auf_den_Rueckblick_wird_das_Vorhaben()
    {
        const string who = "ben";
        var id = _fx.Id(who);
        Chat.DeleteKv("pukki.weekgoal." + id);
        Assert.StartsWith("Für diese Woche hast du dir nichts vorgenommen.", Say(who, "Was habe ich mir vorgenommen?"));

        Pukki.WeekGoalAsked(Chat, id);
        // Eine Frage zwischendurch bleibt eine Frage
        Assert.DoesNotContain("Vorhaben", Say(who, "Wie spät ist es?"));
        Assert.Equal("Gut, dein Vorhaben für diese Woche: „Mehr Bewegung“. Im nächsten Wochenrückblick schauen wir, wie es lief.", Say(who, "Mehr Bewegung"));
        Assert.Equal("Mehr Bewegung", Pukki.WeekGoalOf(Chat, id));
        Assert.Equal("Dein Vorhaben für diese Woche: „Mehr Bewegung“.", Say(who, "Was habe ich mir vorgenommen?"));
        // Nur die eine Antwort: danach geht alles normal weiter
        Assert.DoesNotContain("Vorhaben", Say(who, "Mehr Gemüse"));

        Pukki.WeekGoalAsked(Chat, id);
        Assert.StartsWith("Okay, dann ohne festes Vorhaben.", Say(who, "nichts"));
        Assert.Equal("Mehr Bewegung", Pukki.WeekGoalOf(Chat, id));
    }
}
