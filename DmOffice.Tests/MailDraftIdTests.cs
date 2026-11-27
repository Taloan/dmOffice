using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Mail-Entwurf mit fester Kennung: Speichern in zwei Tabs oder doppelt ersetzt denselben Entwurf. Ohne IMAP-Server im
/// Testprojekt nur die Regeln (Kennung bilden, welche gefundenen Entwürfe wegfallen).</summary>
public class MailDraftIdTests
{
    [Fact]
    public void Kennung_aus_Zufallswert_oder_Message_ID()
    {
        // Zufallskennung des Web: Domain der neu erzeugten Message-ID
        Assert.Equal("a1b2c3d4.draft@beispiel.de", MailApi.DraftMessageId("a1b2c3d4", "<XYZ123@beispiel.de>"));
        // Vollständige Message-ID (geöffneter Entwurf, Antwort des Servers): bleibt, ohne Klammern
        Assert.Equal("abc@beispiel.de", MailApi.DraftMessageId("<abc@beispiel.de>", "neu@anders.de"));
        Assert.Equal("abc@beispiel.de", MailApi.DraftMessageId("abc@beispiel.de", null));
        // Leer, ungültig oder ohne Domain: keine
        Assert.Null(MailApi.DraftMessageId(null, "x@beispiel.de"));
        Assert.Null(MailApi.DraftMessageId("  ", "x@beispiel.de"));
        Assert.Null(MailApi.DraftMessageId("a\"b@c.de", null));
        Assert.Null(MailApi.DraftMessageId("a b", "x@beispiel.de"));
        Assert.Null(MailApi.DraftMessageId("a@@b", null));
        Assert.Null(MailApi.DraftMessageId("token", null));
        Assert.Null(MailApi.DraftMessageId(new string('a', 251), "x@beispiel.de"));
    }

    [Fact]
    public void Aeltere_Entwuerfe_fallen_weg()
    {
        // Eigener Entwurf bekannt: alle älteren mit derselben Kennung, nie der eigene oder ein neuerer (zweiter Tab gleichzeitig)
        Assert.Equal([3u, 7u], MailApi.StaleDrafts([7, 3, 9, 10], 9));
        Assert.Empty(MailApi.StaleDrafts([12], 12));
        // Eigener unbekannt (ohne UIDPLUS): alle außer dem neuesten
        Assert.Equal([3u, 7u], MailApi.StaleDrafts([7, 3, 9], null));
        Assert.Empty(MailApi.StaleDrafts([], null));
        // Nach dem Senden: alle
        Assert.Equal([3u, 7u, 9u], MailApi.StaleDrafts([9, 7, 3], null, all: true));
    }
}
