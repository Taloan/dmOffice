using DmOffice.Chat;
using DmOffice.Notes;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Antwort der KI aufheben: das Kennzeichen wird zum Knopf, „als Rezept speichern“ getippt legt die Notiz mit Schlagwort an.</summary>
[Collection("server")]
public class PukkiSaveAnswerTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiSaveAnswerTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public void Kennzeichen_wird_Knopf_und_getippt_gespeichert()
    {
        var r = Pukki.AiReply("Zutaten: 500 g Mehl, 1 Würfel Hefe.\nZubereitung: kneten und gehen lassen.\n[[REZEPT: Hefeteig]]");
        Assert.DoesNotContain("[[", r.Text);
        Assert.Equal("savenote", Assert.Single(r.Actions!).Key);
        Assert.Null(Pukki.AiReply("Gern geschehen!").Actions);

        var services = _fx.Factory.Services;
        var chat = services.GetRequiredService<ChatStore>();
        var space = _fx.Space("cleo");
        Pukki.Ensure(chat, space.User.Id);
        var room = chat.DirectRoom(space.User.Id, Pukki.Id)!.Value;
        chat.Post(Pukki.Id, room, r.Text, actions: r.Actions);
        // Danach noch eine andere Antwort - „als Rezept“ nimmt trotzdem das Rezept
        chat.Post(Pukki.Id, room, "Photosynthese ist, wenn Pflanzen aus Licht Zucker machen.");
        Pukki.Reset(space.User.Id);
        var saved = Pukki.Answer(services, space, "das als Rezept speichern", chat, room);
        Assert.Contains("Hefeteig", saved.Text);
        var note = NotesDb.Of(space).GetAllActive().Single(n => n.DisplayTitle == "Hefeteig");
        Assert.Contains("recipe", note.Tags!);
        Assert.Contains("500 g Mehl", note.Plain);
    }
}
