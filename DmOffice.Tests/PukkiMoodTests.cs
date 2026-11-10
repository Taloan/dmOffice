using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Pukkis Stimmung je Nachricht: aus Text, Knöpfen und Anhängen; nur bei Pukki, und sie kommt bei Web und App an.</summary>
[Collection("server")]
public class PukkiMoodTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiMoodTests(ChatFixture fx) => _fx = fx;

    [Theory]
    [InlineData("Guten Morgen! Heute steht an: …", "kaffee")]
    [InlineData("Hallo! Ich bin Pukki. Das kann ich schon: …", "hallo")]
    [InlineData("Wann soll ich dich erinnern?", "frage")]
    [InlineData("Eingetragen: morgen, 09:00 Müll – im Kalender „Persönlich“.", "daumen")]
    [InlineData("Auf der Einkaufsliste „Einkauf“: Milch, Eier, Brot.", "checkliste")]
    [InlineData("Das hat nicht geklappt: Den Termin gibt es nicht mehr.", "bitte")]
    [InlineData("Das kann ich leider nicht – ich schaue nicht ins Internet.", "bitte")]
    [InlineData("Gern geschehen!", "herz")]
    [InlineData("Ben hat zugesagt: „Grillfest“.", "jubel")]
    [InlineData("Erinnerung: Zähneputzen – heute 10:00", "idee")]
    [InlineData("Anna lädt dich ein: „Grillfest“ – Donnerstag, 4. Juli 2030", "winken")]
    [InlineData("Morgen, Samstag, 24. Oktober: 09:00 Müll", null)]
    public void Stimmung_aus_dem_Text(string text, string? mood) => Assert.Equal(mood, Pukki.MoodOf(text, [], null));

    [Fact]
    public void Knoepfe_und_Anhaenge_bestimmen_mit()
    {
        Assert.Equal("checkliste", Pukki.MoodOf("Offen sind noch:", [], [new ChatAction("done1", "Erledigt")]));
        Assert.Equal("frage", Pukki.MoodOf("Meinst du das hier", [], [new ChatAction("pick1", "Zahnarzt")]));
        Assert.Equal("lesen", Pukki.MoodOf("Das Rezept", [new ChatRef(ChatRef.TypeNote, "x", "Rezept")], null));
        Assert.Equal("lupe", Pukki.MoodOf("Das habe ich", [new ChatRef(ChatRef.TypeNote, "x", "a"), new ChatRef(ChatRef.TypeEvent, "x", "b")], null));
        // Jede Pose gibt es als Bild
        Assert.Equal(16, Pukki.Moods.Length);
    }

    [Fact]
    public async Task Stimmung_nur_bei_Pukki_und_in_der_Schnittstelle()
    {
        var chat = _fx.Factory.Services.GetRequiredService<ChatStore>();
        Pukki.Ensure(chat, _fx.Id("dora"));
        var room = chat.DirectRoom(_fx.Id("dora"), Pukki.Id)!.Value;
        chat.Post(Pukki.Id, room, "Wann soll ich dich erinnern?");
        chat.Post(_fx.Id("dora"), room, "Gern geschehen!", mood: "herz");
        var list = await (await _fx.As("dora").GetAsync($"/api/chat/rooms/{room}/messages")).Content.ReadFromJsonAsync<JsonElement>();
        var msgs = list.EnumerateArray().ToList();
        Assert.Contains(msgs, m => m.GetProperty("sender").GetString() == Pukki.Id && m.GetProperty("text").GetString() == "Wann soll ich dich erinnern?" && m.GetProperty("mood").GetString() == "frage");
        // Andere haben keine Stimmung, auch wenn ein Aufrufer eine mitgibt
        Assert.Equal(JsonValueKind.Null, msgs.Last(m => m.GetProperty("sender").GetString() == _fx.Id("dora")).GetProperty("mood").ValueKind);
    }
}
