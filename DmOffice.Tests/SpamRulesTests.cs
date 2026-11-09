using System.Text;
using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Zusätzliche Erkennungsregeln: Prüfen beim Speichern, Treffer je Bereich, feste Wirkung, Einfügen im SpamAssassin-Format.</summary>
public class SpamRulesTests
{
    private static MimeMessage Mail(string headers, string body = "Hallo") =>
        MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(headers.Replace("\n", "\r\n") + "\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" + body + "\r\n")));

    [Fact]
    public void Kaputte_und_langsame_Ausdruecke_werden_abgelehnt()
    {
        Assert.Null(SpamRules.Validate(new SpamRule { Name = "ok", Pattern = @"\bgratis\b" }));
        Assert.Contains("schließende Klammer", SpamRules.Validate(new SpamRule { Name = "x", Pattern = "(abc" }));
        Assert.Contains("eckige Klammer", SpamRules.Validate(new SpamRule { Name = "x", Pattern = "[abc" }));
        Assert.Contains("zu lange", SpamRules.Validate(new SpamRule { Name = "x", Pattern = "^(a+)+$" }));
        Assert.Contains("Kopfzeile", SpamRules.Validate(new SpamRule { Name = "x", Part = "header", Pattern = "." }));
        Assert.NotNull(SpamRules.Validate(new SpamRule { Name = "", Pattern = "." }));
        Assert.NotNull(SpamRules.Validate(new SpamRule { Name = "x", Pattern = ".", Action = "loeschen" }));
        // Die Startregeln sind selbst gültig
        Assert.All(SpamRules.Defaults(), r => Assert.Null(SpamRules.Validate(r)));
        // Beim Speichern: Fehler mit Namen der Regel, Kurznamen eindeutig
        var (_, error) = SpamRules.Clean([new SpamRule { Name = "Kaputt", Pattern = "(" }]);
        Assert.StartsWith("Regel „Kaputt“:", error);
        var (list, ok) = SpamRules.Clean([new SpamRule { Name = "Größe zählt", Pattern = "a" }, new SpamRule { Name = "Größe zählt", Pattern = "b" }]);
        Assert.Null(ok);
        Assert.Equal(["groesse-zaehlt", "groesse-zaehlt-2"], list.Select(r => r.Id));
    }

    [Fact]
    public void Treffer_je_Bereich()
    {
        var m = Mail("From: \"Gewinnspiel\" <win@promo.example>\nTo: a@b.example\nSubject: SIE HABEN GEWONNEN\nReply-To: x@other.example",
            "Hallo ich@gmx.example,\nhier ist Ihr Preis: https://bit.ly/abc123 sichern.");
        var rules = SpamRules.Defaults();
        var hits = SpamRules.Hits(m, rules).Select(r => r.Id).ToList();
        Assert.Contains("betreff-grossbuchstaben", hits);
        Assert.Contains("keine-message-id", hits);
        Assert.Contains("link-kuerzer", hits);
        Assert.Contains("anrede-mit-adresse", hits);
        Assert.Contains("gewinnversprechen", hits);
        Assert.DoesNotContain("betreff-leer", hits);
        // Kopfzeile, Absender, verneint
        var custom = new List<SpamRule>
        {
            new() { Id = "reply-to", Name = "Antwort woanders hin", Part = "header", Header = "Reply-To", Pattern = "@other\\.example$" },
            new() { Id = "from-promo", Name = "promo", Part = "from", Pattern = "@promo\\." },
            new() { Id = "kein-list", Name = "ohne Abmeldelink", Part = "header", Header = "List-Unsubscribe", Pattern = ".", Negate = true },
            new() { Id = "alle-kopf", Name = "Kopfzeilen", Part = "headers", Pattern = "^Reply-To: " },
        };
        Assert.Equal(["reply-to", "from-promo", "kein-list", "alle-kopf"], SpamRules.Hits(m, custom).Select(r => r.Id));
        // Merkmale für den lernenden Filter
        var r = SpamSignals.Of(m, rules: custom);
        Assert.Contains("sig:rule:reply-to", SpamSignals.Tokens(r));
        // Gute Mail: keine Treffer der Startregeln
        var ok = Mail("From: Oma <oma@web.example>\nTo: a@b.example\nSubject: Sonntag Kaffee?\nMessage-ID: <1@web.example>", "Liebe Dora, kommst du am Sonntag?");
        Assert.Empty(SpamRules.Hits(ok, rules));
    }

    [Fact]
    public void Feste_Wirkung_und_sichere_Absender()
    {
        var m = Mail("From: a@spam.example\nSubject: Kredit sofort\nMessage-ID: <1@x>");
        var junk = new List<SpamRule> { new() { Id = "kredit", Name = "Kredit", Pattern = "kredit", Action = "junk" } };
        var r = SpamSignals.Of(m, rules: junk);
        var settings = new SpamSettings();
        Assert.Equal("custom-junk", SpamSignals.Adjust(0.1, r, false, false, settings).Rule);
        Assert.True(SpamSignals.Adjust(0.1, r, false, false, settings).Score >= settings.SpamCutoff);
        // Sichere Absender gehen vor
        Assert.Equal("trusted", SpamSignals.Adjust(0.95, r, true, false, settings).Rule);
        var unsure = SpamSignals.Of(m, rules: [new() { Id = "k", Name = "k", Pattern = "kredit", Action = "uncertain" }]);
        var (score, rule) = SpamSignals.Adjust(0.1, unsure, false, false, settings);
        Assert.Equal("custom-uncertain", rule);
        Assert.InRange(score, settings.HamCutoff, settings.SpamCutoff);
        // Nur Merkmal: keine feste Wirkung
        Assert.Null(SpamSignals.Adjust(0.1, SpamSignals.Of(m, rules: [new() { Id = "k", Name = "k", Pattern = "kredit" }]), false, false, settings).Rule);
    }

    [Fact]
    public void SpamAssassin_Format_einfuegen()
    {
        var text = """
            # Kommentar
            header   SUBJ_FREE       Subject =~ /\bfree\b/i
            describe SUBJ_FREE       Subject says free
            score    SUBJ_FREE       2.5
            header   REPLYTO_OTHER   Reply-To:addr =~ m{\@evil\.example$}i
            score    REPLYTO_OTHER   6
            header   HAS_X_FOO       exists:X-Foo
            body     LOTTERY         /won\s+the\s+lottery/i
            uri      SHORTENER       /bit\.ly\//
            header   NO_LIST         List-Id !~ /./ [if-unset: unset]
            meta     COMBO           SUBJ_FREE && LOTTERY
            header   EVAL_TEST       eval:check_for_forged_received()
            body     BROKEN          /(abc/
            rawbody  PERL_ONLY       /\x{263A}(?{ code })/
            """;
        var r = SpamRules.Import(text);
        var byId = r.Rules.ToDictionary(x => x.Id);
        Assert.Equal(["subj_free", "replyto_other", "has_x_foo", "lottery", "shortener", "no_list"], r.Rules.Select(x => x.Id));
        Assert.Equal("Subject says free (SUBJ_FREE)", byId["subj_free"].Name);
        Assert.Equal(("subject", "uncertain", true), (byId["subj_free"].Part, byId["subj_free"].Action, byId["subj_free"].IgnoreCase));
        Assert.Equal(("header", "Reply-To", "junk", @"\@evil\.example$"), (byId["replyto_other"].Part, byId["replyto_other"].Header, byId["replyto_other"].Action, byId["replyto_other"].Pattern));
        Assert.Equal(("header", "X-Foo"), (byId["has_x_foo"].Part, byId["has_x_foo"].Header));
        Assert.Equal(("links", "bit\\.ly/"), (byId["shortener"].Part, byId["shortener"].Pattern));
        Assert.True(byId["no_list"].Negate);
        Assert.Equal(4, r.Skipped.Count);
        Assert.Contains(r.Skipped, s => s.StartsWith("COMBO: meta"));
        Assert.Contains(r.Skipped, s => s.StartsWith("EVAL_TEST: eval"));
        Assert.Contains(r.Skipped, s => s.StartsWith("BROKEN: Ungültiger Ausdruck"));
        Assert.Contains(r.Skipped, s => s.StartsWith("PERL_ONLY:"));
        // Übernommene Regeln wirken
        var m = Mail("From: a@spam.example\nSubject: Totally FREE stuff\nReply-To: boss@evil.example\nX-Foo: 1", "You have won the lottery!");
        Assert.Equal(["subj_free", "replyto_other", "has_x_foo", "lottery", "no_list"], SpamRules.Hits(m, r.Rules).Select(x => x.Id));
    }
}
