using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Nachfassen: Erkennen, ob eine Mail eine Antwort erwartet (feste Regeln, Deutsch und Englisch). Nur erfundene Beispiele.</summary>
public class ReplyExpectationTests
{
    [Theory]
    // Frage
    [InlineData("Hallo Anna,\n\nhast du am Freitag Zeit für ein kurzes Gespräch?\n\nViele Grüße\nBernd", "question")]
    [InlineData("Wie ist der Stand beim Angebot? Ich hatte noch nichts gehört.", "question")]
    [InlineData("Hi Tom, did you get the invoice?", "question")]
    [InlineData("Passt dir 10 Uhr oder lieber 14 Uhr?", "question")]
    // Bitte um Rückmeldung
    [InlineData("Sehr geehrte Frau Muster,\nanbei das Angebot. Ich bitte um kurze Rückmeldung.\nMit freundlichen Grüßen", "feedback")]
    [InlineData("Ich freue mich auf Ihre Antwort.", "feedback")]
    [InlineData("Lassen Sie mich bitte wissen, ob das so in Ordnung ist.", "feedback")]
    [InlineData("Gib mir kurz Bescheid, wenn es angekommen ist.", "feedback")]
    [InlineData("Melden Sie sich bitte, sobald die Teile da sind.", "feedback")]
    [InlineData("Bitte bestätigen Sie den Termin.", "feedback")]
    [InlineData("Please let me know if that works for you.", "feedback")]
    [InlineData("Looking forward to your reply.", "feedback")]
    // Bitte um etwas
    [InlineData("Könnten Sie mir die Unterlagen bis Freitag zuschicken.", "request")]
    [InlineData("Kannst du das bitte noch prüfen.", "request")]
    [InlineData("Bitte schicken Sie mir die Rechnung noch einmal.", "request")]
    [InlineData("Could you send me the signed contract.", "request")]
    [InlineData("Please send the files by tomorrow.", "request")]
    // Frist mit Bitte
    [InlineData("Die Unterlagen bitte bis zum 15.11.", "deadline")]
    [InlineData("Answer please by end of the week.", "deadline")]
    public void Erwartet_eine_Antwort(string text, string reason)
    {
        var r = ReplyExpectation.OfText(text);
        Assert.True(r.Expects, text);
        Assert.Equal(reason, r.Reason);
    }

    [Theory]
    [InlineData("Danke, passt so.")]
    [InlineData("Vielen Dank! Erledigt.\n\nViele Grüße\nAnna")]
    [InlineData("Alles klar, machen wir so.")]
    [InlineData("Thanks, got it.")]
    [InlineData("Anbei die Unterlagen zur Info.")]
    [InlineData("Das Paket ist heute rausgegangen.")]
    // Höflichkeitsformel: lädt ein, verlangt nichts
    [InlineData("Anbei das Protokoll. Bei Fragen melden Sie sich gern.")]
    [InlineData("Hier die Zahlen. Wenn du noch Fragen hast, melde dich einfach.")]
    [InlineData("Here are the slides. If you have any questions, feel free to reach out.")]
    [InlineData("Für Rückfragen stehe ich Ihnen gern zur Verfügung.")]
    [InlineData("")]
    public void Erwartet_keine_Antwort(string text) => Assert.False(ReplyExpectation.OfText(text).Expects, text);

    [Fact]
    public void Zitat_der_vorigen_Mail_zaehlt_nicht()
    {
        // Gmail-Stil: „Am … schrieb …:“ (hier über zwei Zeilen umbrochen) und „>“-Zeilen
        Assert.False(ReplyExpectation.OfText("Danke, passt so.\n\nAm Mo., 3. Nov. 2026 um 10:00 Uhr schrieb Anna Beispiel <\nanna@beispiel.example>:\n> Passt dir Freitag?").Expects);
        Assert.False(ReplyExpectation.OfText("Danke, passt so.\n\nOn Mon, Nov 3, 2026 at 10:00 AM Anna <anna@beispiel.example> wrote:\n> Does Friday work?").Expects);
        Assert.False(ReplyExpectation.OfText("Erledigt.\n> Kannst du das prüfen?").Expects);
        // Outlook-Kopf
        Assert.False(ReplyExpectation.OfText("Danke!\n\nVon: Anna Beispiel <anna@beispiel.example>\nGesendet: Montag, 3. November 2026 10:00\nAn: Bernd\nBetreff: Termin\n\nPasst dir Freitag?").Expects);
        Assert.False(ReplyExpectation.OfText("Thanks!\n\nFrom: Anna\nSent: Monday, November 3, 2026\nTo: Bernd\n\nDoes Friday work?").Expects);
        Assert.False(ReplyExpectation.OfText("Danke.\n\n-----Ursprüngliche Nachricht-----\nKannst du mir helfen?").Expects);
        // Eigener Text mit Frage über dem Zitat zählt
        Assert.True(ReplyExpectation.OfText("Und wie sieht es am Montag aus?\n\nAm 03.11.2026 schrieb Anna:\n> Freitag geht nicht.").Expects);
    }

    [Fact]
    public void Signatur_Links_und_Adressen_zaehlen_nicht()
    {
        Assert.False(ReplyExpectation.OfText("Anbei der Bericht.\n-- \nBernd Beispiel\nWas können wir für Sie tun?\nTel. 0000").Expects);
        Assert.False(ReplyExpectation.OfText("Hier der Link: https://beispiel.example/seite?id=4&x=1").Expects);
        Assert.False(ReplyExpectation.OfText("Die Unterlagen liegen unter www.beispiel.example/a?b=c bereit.").Expects);
        Assert.False(ReplyExpectation.OfText("Schreib an <frage?@beispiel.example> für alles Weitere.").Expects);
    }

    [Fact]
    public void Anrede_mit_Bitte_in_derselben_Zeile_zaehlt()
    {
        Assert.True(ReplyExpectation.OfText("Hallo Anna, kannst du mir die Datei schicken.").Expects);
        Assert.True(ReplyExpectation.OfText("Danke, können Sie mir noch die Rechnung schicken.").Expects);
    }

    private static MimeMessage Mail(string text, string to = "ich@konto.example", string? cc = null, string from = "anna@beispiel.example",
        string subject = "Frage", Action<HeaderList>? headers = null)
    {
        var m = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = text } };
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(to));
        if (cc != null) m.Cc.Add(MailboxAddress.Parse(cc));
        headers?.Invoke(m.Headers);
        return m;
    }

    private static readonly string[] Own = ["ich@konto.example"];

    [Fact]
    public void Empfangene_Mail_direkt_an_mich_mit_Frage()
    {
        Assert.True(ReplyExpectation.Of(Mail("Hast du morgen Zeit?"), Own).Expects);
    }

    [Fact]
    public void Nur_in_Cc_erwartet_nichts()
    {
        var r = ReplyExpectation.Of(Mail("Hast du morgen Zeit?", to: "bernd@beispiel.example", cc: "ich@konto.example"), Own);
        Assert.False(r.Expects);
        Assert.Equal("cc", r.Reason);
    }

    [Fact]
    public void Abwesenheitsnotiz_und_automatische_Mails_erwarten_nichts()
    {
        Assert.False(ReplyExpectation.Of(Mail("Ich bin bis Montag nicht im Büro. Können Sie sich an Bernd wenden?",
            headers: h => h.Add("Auto-Submitted", "auto-replied")), Own).Expects);
        Assert.False(ReplyExpectation.Of(Mail("Ich bin nicht da. Rufen Sie an?", subject: "Automatische Antwort: Frage"), Own).Expects);
        Assert.False(ReplyExpectation.Of(Mail("Out of office until Monday. Could you contact Bernd?", subject: "Out of Office: Question"), Own).Expects);
        Assert.False(ReplyExpectation.Of(Mail("Neu im Shop: Haben Sie schon unsere Angebote gesehen?",
            headers: h => h.Add(HeaderId.ListUnsubscribe, "<https://beispiel.example/abmelden>")), Own).Expects);
        Assert.False(ReplyExpectation.Of(Mail("Ihr Paket ist unterwegs. Fragen?", from: "noreply@shop.example"), Own).Expects);
        // Smart-Sortierung: Newsletter und Benachrichtigungen
        Assert.False(ReplyExpectation.Of(Mail("Neu bei uns: wollen Sie mehr erfahren?"), Own, ["dm_newsletter"]).Expects);
        Assert.False(ReplyExpectation.Of(Mail("Ihre Rechnung ist da. Fragen?"), Own, ["dm_benachrichtigungen"]).Expects);
        // Eigene Mail
        Assert.False(ReplyExpectation.Of(Mail("Hast du Zeit?", from: "ich@konto.example", to: "anna@beispiel.example"), Own).Expects);
    }

    [Fact]
    public void Html_Mail_ohne_zitierten_Block()
    {
        var m = new MimeMessage { Subject = "AW: Frage" };
        m.From.Add(MailboxAddress.Parse("anna@beispiel.example"));
        m.To.Add(MailboxAddress.Parse("ich@konto.example"));
        m.Body = new TextPart("html") { Text = "<div>Danke, passt so.</div><blockquote>Hast du morgen Zeit?</blockquote>" };
        Assert.False(ReplyExpectation.Of(m, Own).Expects);
        m.Body = new TextPart("html") { Text = "<div>Geht auch Dienstag?</div><div class=\"gmail_quote\">Am Montag schrieb Bernd: Danke.</div>" };
        Assert.True(ReplyExpectation.Of(m, Own).Expects);
    }
}
