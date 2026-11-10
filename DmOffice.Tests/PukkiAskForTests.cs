using DmOffice.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Was über Pukki hinausgeht („Erklär mir …“, „Ich brauche ein Rezept für …“), legt nichts an und geht mit KI an die KI;
/// Aufträge bleiben Aufträge.</summary>
[Collection("server")]
public class PukkiAskForTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiAskForTests(ChatFixture fx) => _fx = fx;

    [Theory]
    [InlineData("ich brauche ein Rezept für Pizzateig", true)]
    [InlineData("erklär mir Photosynthese", true)]
    [InlineData("schreib mir ein Gedicht über Katzen", true)]
    [InlineData("gib mir Tipps zum Einschlafen", true)]
    [InlineData("kannst du mir eine Mail formulieren", true)]
    [InlineData("Formuliere mir einen netten Mail Text für die Oma die 80 wird zum Geburtstag", true)]
    [InlineData("schreib mir einen Geburtstagsgruß für Tante Inge", true)]
    [InlineData("schreib einen Brief an die Versicherung", false)]
    [InlineData("wer war Goethe?", true)]
    [InlineData("wie wird das Wetter morgen?", true)]
    [InlineData("ich brauche Milch", false)]
    [InlineData("Schreib Käse auf die Liste", false)]
    [InlineData("was ist 17 mal 23", false)]
    [InlineData("recherchiere wo das würzjoch ist", true)]
    [InlineData("such im Internet nach dem Würzjoch", true)]
    [InlineData("finde heraus wann der Baumarkt öffnet", true)]
    [InlineData("wo ist das würzjoch", true)]
    [InlineData("erstelle einen kartenausschnitt damit ich mir einen Überblick verschaffen kann", true)]
    [InlineData("Karte für Oma kaufen", false)]
    [InlineData("recherchieren für die Steuererklärung", false)]
    public void Geht_an_die_KI(string text, bool unknown)
    {
        var services = _fx.Factory.Services;
        var chat = services.GetRequiredService<ChatStore>();
        var space = _fx.Space("dora");
        Pukki.Ensure(chat, space.User.Id);
        var room = chat.DirectRoom(space.User.Id, Pukki.Id)!.Value;
        Pukki.Reset(space.User.Id);
        var r = Pukki.Answer(services, space, text, chat, room);
        if (r.Actions?.Any(a => a.Key == "undo") == true)
            Pukki.Act(services, chat, space, chat.Messages(space.User.Id, room, limit: 1)[0], "undo");
        Pukki.Reset(space.User.Id);
        Assert.True(r.Unknown == unknown, $"„{text}“ → „{r.Text}“");
    }

    // Karte mit genanntem Ort: Link auf OpenStreetMap, auch ohne KI
    [Theory]
    [InlineData("zeig mir das Würzjoch auf der Karte", "Würzjoch")]
    [InlineData("Karte von Brixen", "Brixen")]
    public void Karte_mit_Ort_als_Link(string text, string place)
    {
        var services = _fx.Factory.Services;
        var chat = services.GetRequiredService<ChatStore>();
        var space = _fx.Space("dora");
        Pukki.Ensure(chat, space.User.Id);
        var room = chat.DirectRoom(space.User.Id, Pukki.Id)!.Value;
        Pukki.Reset(space.User.Id);
        var r = Pukki.Answer(services, space, text, chat, room);
        Pukki.Reset(space.User.Id);
        Assert.False(r.Unknown, r.Text);
        Assert.Contains("[" + place + "](https://www.openstreetmap.org/search?query=" + Uri.EscapeDataString(place) + ")", r.Text);
    }
}
