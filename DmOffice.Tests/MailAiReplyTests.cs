using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>„Antwort mit KI“ (Kennung mailaireply): Auftrag an die KI, Sperren, Ergebnis - mit einer Attrappe statt der KI.</summary>
public class MailAiReplyTests
{
    private static MimeMessage Mail(string body, string subject = "Einladung zum Sommerfest")
    {
        var m = new MimeMessage { Subject = subject };
        m.From.Add(new MailboxAddress("Erika Beispiel", "erika@example.invalid"));
        m.To.Add(new MailboxAddress("Max Muster", "max@example.invalid"));
        m.Body = new TextPart("plain") { Text = body };
        return m;
    }

    [Fact]
    public async Task Auftrag_enthaelt_Zweck_Mail_und_Regeln()
    {
        string? system = null, prompt = null;
        var (reply, error) = await MailAiReply.Draft(Mail("Hallo Max, kommst du am Samstag zum Sommerfest?"), "accept", null, "Max Muster",
            (s, p) => { system = s; prompt = p; return Task.FromResult<string?>("Hallo Erika,\n\ngerne, ich bin dabei!"); });
        Assert.Null(error);
        Assert.Equal("Hallo Erika,\n\ngerne, ich bin dabei!", reply);
        Assert.Contains("Sprache der E-Mail", system);
        Assert.Contains("keine Unterschrift", system);
        Assert.Contains(MailAiReply.Modes["accept"], prompt);
        Assert.Contains("Erika Beispiel <erika@example.invalid>", prompt);
        Assert.Contains("Betreff: Einladung zum Sommerfest", prompt);
        Assert.Contains("kommst du am Samstag", prompt);
        Assert.Contains("Max Muster", prompt);
    }

    [Fact]
    public async Task Eigene_Anweisung_ist_Pflicht_und_kommt_mit()
    {
        var called = false;
        var (_, error) = await MailAiReply.Draft(Mail("Hi"), "custom", " ", null, (_, _) => { called = true; return Task.FromResult<string?>("x"); });
        Assert.NotNull(error);
        Assert.False(called);
        string? prompt = null;
        await MailAiReply.Draft(Mail("Hi"), "custom", "Verschiebe auf nächste Woche", null, (_, p) => { prompt = p; return Task.FromResult<string?>("ok"); });
        Assert.Contains("Verschiebe auf nächste Woche", prompt);
        Assert.Equal("Unbekannter Zweck.", (await MailAiReply.Draft(Mail("Hi"), "boese", null, null, (_, _) => Task.FromResult<string?>("x"))).Error);
    }

    [Fact]
    public async Task Verschluesseltes_und_Vertrauliches_geht_nicht_an_die_KI()
    {
        var called = false;
        Task<string?> Fake(string s, string p) { called = true; return Task.FromResult<string?>("x"); }
        var pgp = Mail("-----BEGIN PGP MESSAGE-----\nhQEMA...\n-----END PGP MESSAGE-----");
        Assert.NotNull((await MailAiReply.Draft(pgp, "thanks", null, null, Fake)).Error);
        var smime = new MimeMessage { Subject = "x", Body = new MimePart("application", "pkcs7-mime") { Content = new MimeContent(new MemoryStream([1, 2, 3])) } };
        Assert.NotNull((await MailAiReply.Draft(smime, "thanks", null, null, Fake)).Error);
        var confidential = Mail("Gehaltsliste");
        confidential.Headers.Add(HeaderId.Sensitivity, "Company-Confidential");
        Assert.NotNull((await MailAiReply.Draft(confidential, "thanks", null, null, Fake)).Error);
        Assert.False(called);
    }

    [Fact]
    public async Task Langer_Text_wird_gekuerzt_und_Betreffzeile_entfernt()
    {
        string? prompt = null;
        var (reply, _) = await MailAiReply.Draft(Mail(new string('a', 20_000)), "question", null, null,
            (_, p) => { prompt = p; return Task.FromResult<string?>("Subject: Re: Fest\nWann genau?"); });
        Assert.True(prompt!.Length < MailAiReply.MaxText + 1000);
        Assert.Equal("Wann genau?", reply);
        Assert.Equal("Die KI hat nicht geantwortet. Bitte später noch einmal versuchen.",
            (await MailAiReply.Draft(Mail("x"), "thanks", null, null, (_, _) => Task.FromResult<string?>(null))).Error);
    }

    [Fact]
    public void Zwecke_wie_im_Menue() =>
        Assert.Equal(["accept", "decline", "question", "thanks", "custom"], MailAiReply.Modes.Keys);
}
