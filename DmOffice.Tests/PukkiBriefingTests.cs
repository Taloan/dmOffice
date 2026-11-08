using DmOffice.Chat;

namespace DmOffice.Tests;

/// <summary>Schlagzeilen für Pukkis Morgenüberblick: RSS und Atom lesen, abwechselnd aus den Quellen, Stichwörter, keine Doppelten.</summary>
public class PukkiBriefingTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0"><channel><title>Test</title>
          <item><title>Wahl in Bayern: Ergebnis &amp; Reaktionen</title><link>https://a.example/1</link><pubDate>Sun, 25 Oct 2026 07:00:00 GMT</pubDate></item>
          <item><title>Fußball: Bayern gewinnt</title><link>https://a.example/2</link><pubDate>Sun, 25 Oct 2026 06:00:00 GMT</pubDate></item>
          <item><title>Zeitumstellung: Uhren zurückgestellt</title><link>https://a.example/3</link><pubDate>Sun, 25 Oct 2026 05:00:00 GMT</pubDate></item>
        </channel></rss>
        """;

    private const string Atom = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom"><title>Atom</title>
          <entry><title>Neue Grafikkarte vorgestellt</title><link rel="alternate" href="https://b.example/1"/><updated>2026-10-25T06:30:00Z</updated></entry>
          <entry><title>Zeitumstellung: Uhren zurückgestellt!</title><link href="https://b.example/2"/><updated>2026-10-25T04:00:00Z</updated></entry>
        </feed>
        """;

    [Fact]
    public void Rss_und_Atom_lesen()
    {
        var a = PukkiBriefing.Parse(Rss, "A");
        Assert.Equal(3, a.Count);
        Assert.Equal("Wahl in Bayern: Ergebnis & Reaktionen", a[0].Title);
        Assert.Equal("https://a.example/1", a[0].Link);
        var b = PukkiBriefing.Parse(Atom, "B");
        Assert.Equal(["https://b.example/1", "https://b.example/2"], b.Select(x => x.Link));
    }

    [Fact]
    public void Auswahl_abwechselnd_mit_Stichwoertern_ohne_Doppelte()
    {
        var feeds = new List<List<PukkiBriefing.Headline>> { PukkiBriefing.Parse(Rss, "A"), PukkiBriefing.Parse(Atom, "B") };
        // Abwechselnd die neuesten, die fast gleiche Zeitumstellung nur einmal
        var all = PukkiBriefing.Pick(feeds, new PukkiBriefing.Settings { Count = 10 });
        Assert.Equal(4, all.Count);
        Assert.Equal(["A", "B"], all.Take(2).Select(h => h.Source));
        // Fußball weglassen, Zeitumstellung vorziehen, höchstens zwei
        var two = PukkiBriefing.Pick(feeds, new PukkiBriefing.Settings { Count = 2, Exclude = "Fußball", Prefer = "zeitumstellung" });
        Assert.StartsWith("Zeitumstellung", two[0].Title);
        Assert.DoesNotContain(two, h => h.Title.Contains("Fußball"));
        Assert.Equal(2, two.Count);
    }
}
