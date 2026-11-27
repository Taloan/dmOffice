// [feature:mailaireply]
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// „Antwort mit KI“ (Kennung mailaireply): ein Antwortentwurf auf eine Mail, mit Zweck (zusagen, absagen, nachfragen,
/// danken oder eigene Anweisung). Das Ergebnis kommt als bearbeitbarer Text ins Schreibfenster, gesendet wird nie von hier.
/// Verschlüsselte und als vertraulich gekennzeichnete Mails gehen nicht an die KI - wie der Tresor an den anderen KI-Stellen.
/// </summary>
public static class MailAiReply
{
    /// <summary>Höchstens so viele Zeichen der Mail gehen an die KI.</summary>
    internal const int MaxText = 8000;

    /// <summary>Zwecke der Antwort (Web: Menü „Antwort mit KI“).</summary>
    internal static readonly Dictionary<string, string> Modes = new()
    {
        ["accept"] = "Sage zu: nimm die Anfrage, Einladung oder den Vorschlag freundlich an und bestätige, was vereinbart ist.",
        ["decline"] = "Sage freundlich und klar ab. Begründe kurz und höflich, ohne Ausreden zu erfinden.",
        ["question"] = "Frage nach: stelle die Rückfragen, die zur Mail offen sind - höchstens drei, knapp und konkret.",
        ["thanks"] = "Bedanke dich kurz und passend zum Inhalt der Mail.",
        ["custom"] = "Richte die Antwort nach dieser Anweisung aus:",
    };

    internal const string System = """
        Du schreibst einen Antwortentwurf auf eine E-Mail im Namen des Empfängers.
        Regeln:
        - Schreibe in der Sprache der E-Mail (deutsche Mail: Deutsch, englische Mail: Englisch usw.).
        - Übernimm die Anrede der Mail (Sie oder du, Vorname oder Nachname).
        - Folge dem angegebenen Zweck. Erfinde keine Termine, Zahlen, Zusagen oder Fakten, die nicht in der Mail stehen;
          wo etwas fehlt, lass eine kurze Lücke in eckigen Klammern, etwa [Termin].
        - Kurz und natürlich, ohne Floskeln.
        - Nur der Text der Antwort: keine Betreffzeile, kein Zitat der Mail, keine Unterschrift und kein Name am Ende, keine Erklärung.
        """;

    /// <summary>Warum diese Mail nicht an die KI geht, oder null. Verschlüsselt (PGP, S/MIME) oder als vertraulich gekennzeichnet.</summary>
    internal static string? Blocked(MimeMessage m)
    {
        if (m.Body?.ContentType.IsMimeType("multipart", "encrypted") == true
            || m.BodyParts.OfType<MimePart>().Any(p => p.ContentType.IsMimeType("application", "pkcs7-mime") || p.ContentType.IsMimeType("application", "x-pkcs7-mime"))
            || (m.TextBody ?? "").Contains("-----BEGIN PGP MESSAGE-----"))
            return "Verschlüsselte Mails bleiben bei dir: Die KI bekommt sie nicht.";
        var sensitivity = m.Headers[HeaderId.Sensitivity] ?? "";
        if (sensitivity.Contains("private", StringComparison.OrdinalIgnoreCase) || sensitivity.Contains("confidential", StringComparison.OrdinalIgnoreCase))
            return "Als vertraulich gekennzeichnete Mails bleiben bei dir: Die KI bekommt sie nicht.";
        return null;
    }

    /// <summary>Auftrag an die KI: Zweck, Absender, Betreff und der Text der Mail (gekürzt).</summary>
    internal static string Prompt(MimeMessage m, string mode, string? hint, string? me)
    {
        var text = MailStore.BodyText(m).Trim();
        if (text.Length > MaxText) text = text[..MaxText] + "\n[…]";
        var from = m.From.Mailboxes.FirstOrDefault();
        return $"Zweck: {Modes[mode]}{(string.IsNullOrWhiteSpace(hint) ? "" : " " + hint.Trim())}\n"
            + (string.IsNullOrWhiteSpace(me) ? "" : $"Ich (Empfänger, der antwortet): {me}\n")
            + $"Absender der Mail: {(string.IsNullOrEmpty(from?.Name) ? from?.Address : $"{from.Name} <{from.Address}>")}\n"
            + $"Betreff: {m.Subject}\n\nE-Mail:\n{text}";
    }

    /// <summary>Entwurf erzeugen; <paramref name="write"/> ist der KI-Aufruf (System, Auftrag) → Text oder null.</summary>
    internal static async Task<(string? Reply, string? Error)> Draft(MimeMessage m, string mode, string? hint, string? me, Func<string, string, Task<string?>> write)
    {
        if (!Modes.ContainsKey(mode)) return (null, "Unbekannter Zweck.");
        if (mode == "custom" && string.IsNullOrWhiteSpace(hint)) return (null, "Bitte angeben, wie die Antwort ausfallen soll.");
        if (Blocked(m) is { } why) return (null, why);
        var raw = await write(System, Prompt(m, mode, hint, me));
        if (string.IsNullOrWhiteSpace(raw)) return (null, "Die KI hat nicht geantwortet. Bitte später noch einmal versuchen.");
        return (Clean(raw), null);
    }

    /// <summary>Was die KI trotz Anweisung mitschickt: Betreffzeile am Anfang, Code-Zaun.</summary>
    internal static string Clean(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith("```")) t = t.Trim('`').Trim();
        var first = t.Split('\n')[0];
        if (first.StartsWith("Betreff:", StringComparison.OrdinalIgnoreCase) || first.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            t = t[first.Length..].TrimStart();
        return t;
    }
}
// [/feature:mailaireply]
