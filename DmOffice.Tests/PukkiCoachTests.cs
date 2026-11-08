using DmOffice.Chat;
using DmOffice.Coach;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Coach-Wissen für Pukki (docs/dmcoach-konzept.md): feste Grenzen, eigene Daten, Nährwerte, Wissenskarten.</summary>
[Collection("server")]
public class PukkiCoachTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiCoachTests(ChatFixture fx) => _fx = fx;

    private Pukki.Reply Ask(string user, string text)
    {
        var services = _fx.Factory.Services;
        var chat = services.GetRequiredService<ChatStore>();
        var space = _fx.Space(user);
        Pukki.Ensure(chat, space.User.Id);
        var room = chat.DirectRoom(space.User.Id, Pukki.Id)!.Value;
        Pukki.Reset(space.User.Id);
        return Pukki.Answer(services, space, text, chat, room);
    }

    [Theory]
    [InlineData("Ich habe seit gestern Bauchschmerzen, was kann ich tun?")]
    [InlineData("Mir ist schwindlig")]
    [InlineData("Was hilft gegen Kopfschmerzen?")]
    [InlineData("Mein Sohn hat Fieber, ist das schlimm?")]
    [InlineData("Ich habe Muskelkater, was hilft?")]
    public void Beschwerden_nie_beurteilt(string text)
    {
        var r = Ask("anna", text);
        Assert.Contains(CoachKnowledge.Complaint, r.Text);
        Assert.False(r.Unknown);
        Assert.Null(r.Grounding);
    }

    [Theory]
    [InlineData("Kann ich Ibuprofen mit Alkohol nehmen?")]
    [InlineData("Wie viel Paracetamol darf ich nehmen?")]
    [InlineData("Darf ich meine Tabletten zum Essen nehmen?")]
    [InlineData("Hilft Metformin beim Abnehmen?")]
    [InlineData("Was ist besser, Ozempic oder Mounjaro?")]
    public void Medikamente_nie_beraten(string text)
    {
        var r = Ask("anna", text);
        Assert.Equal(CoachKnowledge.Medication, r.Text);
        Assert.False(r.Unknown);
        Assert.Null(r.Grounding);
    }

    [Fact]
    public void Medikamente_und_Beschwerden_auch_nicht_ueber_die_KI()
    {
        // Was bei Pukki unbekannt bleibt, prüft der Dienst vor der KI noch einmal
        Assert.NotNull(CoachKnowledge.GuardForAi("ibuprofen und alkohol"));
        Assert.NotNull(CoachKnowledge.GuardForAi("Kopfschmerzen?"));
        Assert.Null(CoachKnowledge.GuardForAi("Wie lange dauert es, bis man verblutet ist"));
        Assert.Null(CoachKnowledge.GuardForAi("Was tun, wenn jemand bewusstlos ist?"));
    }

    [Theory]
    [InlineData("mein freudn ist zusammengeklappt und liegt zuckend auf dem boden", "Bleib bei der Person")]
    [InlineData("Er liegt am Boden und atmet nicht", "Bleib bei der Person")]
    [InlineData("Ich bekomme keine Luft", "bitte jemanden in deiner Nähe")]
    public void Notfall_ohne_Diagnose_nur_Hilfe_holen(string text, string part)
    {
        var r = Ask("anna", text);
        Assert.StartsWith(CoachKnowledge.Emergency, r.Text);
        Assert.Contains(part, r.Text);
        Assert.False(r.Unknown);   // nie an die KI
    }

    [Fact]
    public void Allgemeine_Notfallfrage_darf_an_die_KI()
    {
        var r = Ask("anna", "Was tun, wenn jemand bewusstlos ist?");
        Assert.Equal(CoachKnowledge.EmergencyInfo, r.Text);
        Assert.True(r.Unknown);
    }

    [Fact]
    public void Essstoerung_behutsam_ohne_Tipps()
    {
        var r = Ask("anna", "Ich übergebe mich absichtlich nach dem Essen, damit ich nicht zunehme");
        Assert.Contains("Beratungsstelle für Essstörungen", r.Text);
        Assert.Null(r.Grounding);
    }

    [Theory]
    [InlineData("Termin Hausarzt morgen 9 Uhr wegen Rückenschmerzen")]
    [InlineData("Ibuprofen kaufen")]
    [InlineData("Haben wir noch Zucker?")]
    public void Auftraege_bleiben_Auftraege(string text)
    {
        var r = Ask("anna", text);
        Assert.NotEqual(CoachKnowledge.Complaint, r.Text);
        Assert.NotEqual(CoachKnowledge.Medication, r.Text);
        Assert.DoesNotContain("Zucker und Süßes", r.Text);
    }

    [Theory]
    [InlineData("Was sind Ballaststoffe?", "ballaststoffe")]
    [InlineData("Ist Keto gesund?", "keto")]
    [InlineData("Wie funktioniert Intervallfasten?", "fasten")]
    [InlineData("Was muss ich bei veganer Ernährung beachten?", "vegan")]
    [InlineData("Ich esse oft aus Langeweile, was kann ich tun?", "emotional")]
    [InlineData("Ich nehme seit drei Wochen nicht mehr ab, warum?", "plateau")]
    [InlineData("Wie viel sollte ich trinken?", "trinken")]
    [InlineData("Was ist Zöliakie?", "zoeliakie")]
    [InlineData("Was esse ich vor dem Training?", "sport")]
    [InlineData("Wann sind Nahrungsergänzungsmittel sinnvoll?", "supplemente")]
    public void Wissenskarten(string text, string card)
    {
        Assert.Equal(card, CoachKnowledge.Match(text)?.Id);
        var r = Ask("dora", text);
        Assert.False(r.Unknown);
        Assert.True(r.Grounding != null, r.Text);   // mit KI antwortet sie auf dieser Grundlage
    }

    [Fact]
    public void Alle_Karten_vollstaendig()
    {
        Assert.True(CoachKnowledge.Cards.Count >= 40);
        foreach (var c in CoachKnowledge.Cards)
        {
            Assert.True(c.Keys.Length >= 3, c.Id);
            Assert.True(c.Text.Length >= 200, c.Id);
            Assert.Contains(c.Art, new[] { "wissen", "gesundheit", "verhalten" });
            // Jede Karte findet man über ihre eigenen Stichwörter
            Assert.Equal(c.Id, CoachKnowledge.Match("was ist " + c.Keys[0])?.Id);
        }
    }

    [Fact]
    public void Gesundheitskarten_mit_Hinweis()
    {
        var r = Ask("dora", "Was ist Laktoseintoleranz?");
        Assert.EndsWith(CoachKnowledge.Disclaimer, r.Text);
    }

    [Fact]
    public void Naehrwerte_aus_dem_BLS()
    {
        var r = Ask("dora", "Wie viel Eiweiß hat Magerquark?");
        Assert.Contains("Speisequark Magerstufe", r.Text);
        Assert.Contains("je 100 g", r.Text);
        r = Ask("dora", "Was hat mehr Eiweiß, Reis oder Linsen?");
        Assert.Contains("hat mehr Eiweiß", r.Text);
        Assert.Contains("roh", r.Text);
        r = Ask("dora", "Was enthält viel Eisen?");
        Assert.StartsWith("Viel Eisen", r.Text);
        Assert.Contains("DGE", r.Text);
        Assert.DoesNotContain("Leber", r.Text);
    }

    [Fact]
    public void Eigene_Daten_und_Profil()
    {
        var services = _fx.Factory.Services;
        var space = _fx.Space("ben");
        var today = CoachStore.Today(space);
        CoachStore.SaveProfile(space.Db, new CoachProfile { Birth = new DateOnly(1980, 5, 1), Sex = "f", HeightCm = 168, WeightKg = 70 });
        CoachStore.AddWater(space.Db, today, 500);
        var r = Ask("ben", "Wie viel Wasser habe ich heute getrunken?");
        Assert.Contains("0,5 l getrunken", r.Text);
        r = Ask("ben", "Wie viele Kalorien darf ich heute noch essen?");
        Assert.Contains("bleiben noch etwa", r.Text);
        // Persönlicher Satz aus dem Profil, für die KI nur mit Einwilligung
        r = Ask("ben", "Was ist mein Grundumsatz?");
        Assert.Contains("Grundumsatz etwa", r.Text);
        Assert.DoesNotContain("Aus dem Coach des Benutzers", r.Grounding);
        var p = CoachStore.Profile(space.Db);
        p.AiData = true;
        CoachStore.SaveProfile(space.Db, p);
        Assert.Contains("Aus dem Coach des Benutzers", Ask("ben", "Was ist mein Grundumsatz?").Grounding);
        // Zahlen ausgeblendet: keine kcal
        p.HideNumbers = true;
        CoachStore.SaveProfile(space.Db, p);
        Assert.DoesNotContain("kcal", Ask("ben", "Wie viele Kalorien darf ich heute noch essen?").Text);
    }

    [Fact]
    public void Keine_Diaettipps_fuer_Minderjaehrige()
    {
        var space = _fx.Space("cleo");
        CoachStore.SaveProfile(space.Db, new CoachProfile { Birth = DateOnly.FromDateTime(DateTime.Today).AddYears(-15), Sex = "m", HeightCm = 170, WeightKg = 60 });
        var r = Ask("cleo", "Wie kann ich schnell abnehmen?");
        Assert.Contains("Unter 18", r.Text);
        Assert.Null(r.Grounding);
    }
}
