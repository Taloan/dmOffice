using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Nachfassen (docs/nachfassen-konzept.md, „Erkennen, ob eine Mail eine Antwort erwartet“): feste Regeln auf Deutsch und Englisch.
/// Ausgewertet wird nur der eigene Text der Mail - ohne Zitat der vorigen Mail („Am … schrieb …“, „>“-Zeilen, Outlook-Kopf
/// „Von: … Gesendet: …“), ohne Signatur und ohne Links und Adressen (deren Fragezeichen zählen nicht).
/// Treffer: ein Satz endet mit „?“, Bitte um Rückmeldung, Bitte um etwas, Frist zusammen mit „bitte“.
/// Keine Antwort erwartet: reine Danksagung oder Bestätigung, Höflichkeitsformeln („bei Fragen melden Sie sich gern“),
/// Abwesenheitsnotizen und automatische Mails, Mails nur in Cc.
/// Eine KI wird nicht gefragt - die Regeln entscheiden allein.
/// </summary>
public static partial class ReplyExpectation
{
    /// <summary>Ergebnis: erwartet eine Antwort, und warum („question“, „feedback“, „request“, „deadline“) bzw. warum nicht.</summary>
    public sealed record Result(bool Expects, string Reason);

    /// <summary>Kategorien der Smart-Sortierung, deren Mails keine Antwort erwarten (SmartInbox.BuiltIn).</summary>
    private static readonly string[] AutoCategories = ["dm_newsletter", "dm_benachrichtigungen", "dm_sozial"];

    /// <summary>Eine empfangene Mail: erwartet sie eine Antwort von mir?</summary>
    /// <param name="own">Eigene Adressen (Konto, Aliase) - „nur in Cc“ und eigene Mails erkennen</param>
    /// <param name="keywords">Kategorien (IMAP-Schlagwörter) der Mail, etwa von der Smart-Sortierung</param>
    public static Result Of(MimeMessage m, IEnumerable<string> own, IEnumerable<string>? keywords = null)
    {
        var me = own.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().ToLowerInvariant()).ToHashSet();
        var from = m.From.Mailboxes.FirstOrDefault()?.Address?.Trim().ToLowerInvariant() ?? "";
        if (from.Length == 0 || me.Contains(from)) return new(false, "own");
        if (Automatic(m)) return new(false, "auto");
        if (keywords != null && keywords.Any(k => AutoCategories.Contains(k.ToLowerInvariant()))) return new(false, "auto");
        var to = m.To.Mailboxes.Select(a => a.Address.ToLowerInvariant()).ToHashSet();
        var cc = m.Cc.Mailboxes.Select(a => a.Address.ToLowerInvariant()).ToHashSet();
        if (me.Overlaps(cc) && !me.Overlaps(to)) return new(false, "cc");
        return OfText(BodyText(m));
    }

    /// <summary>Automatisch erzeugt: Abwesenheitsnotiz, Benachrichtigung, Newsletter, Verteiler, no-reply-Absender.</summary>
    public static bool Automatic(MimeMessage m)
    {
        var h = m.Headers;
        var auto = (h["Auto-Submitted"] ?? "").Trim().ToLowerInvariant();
        if (auto.Length > 0 && auto != "no") return true;
        if (h.Contains("X-Autoreply") || h.Contains("X-Autorespond") || h.Contains("X-Autoresponder")) return true;
        var prec = (h[HeaderId.Precedence] ?? "").Trim().ToLowerInvariant();
        if (prec is "bulk" or "list" or "junk" or "auto_reply") return true;
        if (!string.IsNullOrEmpty(h[HeaderId.ListId]) || !string.IsNullOrEmpty(h[HeaderId.ListUnsubscribe])) return true;
        var from = m.From.Mailboxes.FirstOrDefault()?.Address?.Trim().ToLowerInvariant() ?? "";
        if (Focus.BulkSender(from)) return true;
        return AutoSubject(m.Subject);
    }

    /// <summary>Betreff einer Abwesenheitsnotiz oder automatischen Antwort.</summary>
    public static bool AutoSubject(string? subject) => subject != null && AutoSubjectRx().IsMatch(subject);

    /// <summary>Text einer Mail: Klartext, sonst HTML ohne zitierte Blöcke.</summary>
    public static string BodyText(MimeMessage m)
    {
        if (!string.IsNullOrWhiteSpace(m.TextBody)) return m.TextBody.Replace("\r", "");
        if (m.HtmlBody is not { } html) return "";
        var doc = new HtmlParser().ParseDocument(html);
        // Zitate in HTML-Mails: blockquote, Gmail, Outlook (Kopf der vorigen Mail), Apple Mail
        foreach (var q in doc.QuerySelectorAll("blockquote, .gmail_quote, #divRplyFwdMsg, #appendonsend, .moz-cite-prefix").ToList()) q.Remove();
        return MailHtml.ToText(doc.Body?.InnerHtml ?? "");
    }

    /// <summary>Nur der eigene Text: ohne Zitat, Signatur, Links und Adressen.</summary>
    public static string OwnText(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var keep = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            // Signatur ab „-- “ (auch ohne Leerzeichen, wie manche Programme sie schreiben)
            if (line == "-- " || trimmed == "--") break;
            // Zitatkopf: „-----Ursprüngliche Nachricht-----“, Trennlinie von Outlook
            if (OriginalRx().IsMatch(trimmed)) break;
            // „Am … schrieb …:“ / „On … wrote:“ - auch über zwei Zeilen umbrochen
            if (WroteRx().IsMatch(trimmed) || (i + 1 < lines.Length && WroteRx().IsMatch(trimmed + " " + lines[i + 1].Trim()) && StartsWroteRx().IsMatch(trimmed))) break;
            // „Name schrieb am …:“ / „Name wrote on …:“ - nur, wenn danach nichts Eigenes mehr kommt (nur Zitat oder nichts)
            if (NameWroteRx().IsMatch(trimmed) && lines.Skip(i + 1).All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith('>'))) break;
            // Outlook-Kopf: „Von: …“ und in den nächsten Zeilen „Gesendet:“/„Datum:“ bzw. „Sent:“/„Date:“
            if (HeadFromRx().IsMatch(trimmed) && lines.Skip(i + 1).Take(4).Any(l => HeadSentRx().IsMatch(l.Trim()))) break;
            if (trimmed.StartsWith('>')) continue;
            keep.Add(line);
        }
        var own = string.Join("\n", keep);
        own = UrlRx().Replace(own, " ");
        own = MailRx().Replace(own, " ");
        return own.Trim();
    }

    /// <summary>Ein Text (z. B. eine neue Mail beim Schreiben): erwartet er eine Antwort?</summary>
    public static Result OfText(string text)
    {
        var own = OwnText(text);
        var sentences = Sentences(own)
            // Höflichkeitsformeln („Bei Fragen melden Sie sich gern“) erwarten nichts
            .Where(s => !CourtesyRx().IsMatch(s))
            // Anrede und Gruß zählen nicht
            .Where(s => !GreetingRx().IsMatch(s))
            .ToList();
        if (sentences.Count == 0) return new(false, "empty");
        if (sentences.All(s => ThanksRx().IsMatch(s) && !RequestRx().IsMatch(s) && !FeedbackRx().IsMatch(s))) return new(false, "thanks");
        var rest = string.Join("\n", sentences);
        if (sentences.Any(s => QuestionRx().IsMatch(s))) return new(true, "question");
        if (FeedbackRx().IsMatch(rest)) return new(true, "feedback");
        if (RequestRx().IsMatch(rest)) return new(true, "request");
        if (DeadlineRx().IsMatch(rest) && PleaseRx().IsMatch(rest)) return new(true, "deadline");
        return new(false, "none");
    }

    /// <summary>Sätze: an Satzzeichen und Zeilenumbrüchen getrennt.</summary>
    private static IEnumerable<string> Sentences(string text) =>
        SentenceSplitRx().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0);

    [GeneratedRegex(@"(?<=[.!?…])\s+|\n+")]
    private static partial Regex SentenceSplitRx();

    // Ein Satz, der mit „?“ endet (Wörter davor, nicht nur Zeichen)
    [GeneratedRegex(@"\p{L}[^?]*\?+[)""'»«“”]*$")]
    private static partial Regex QuestionRx();

    [GeneratedRegex(@"^-{2,}\s*(ursprüngliche nachricht|original message|weitergeleitete nachricht|forwarded message)\s*-{2,}$|^_{10,}$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalRx();

    [GeneratedRegex(@"^(am|on|le|el)\s.{3,200}\b(schrieb|wrote|a écrit|escribió)\b.{0,200}:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WroteRx();

    [GeneratedRegex(@"^(am|on)\s", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWroteRx();

    [GeneratedRegex(@"^.{1,120}?\s(schrieb am|wrote on)\s.{3,120}:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NameWroteRx();

    [GeneratedRegex(@"^\*?(von|from)\s*:\*?\s", RegexOptions.IgnoreCase)]
    private static partial Regex HeadFromRx();

    [GeneratedRegex(@"^\*?(gesendet|datum|sent|date)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex HeadSentRx();

    [GeneratedRegex(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"<?[\w.+-]+@[\w-]+(\.[\w-]+)+>?")]
    private static partial Regex MailRx();

    [GeneratedRegex(@"^(automatische antwort|automatic reply|auto(matic)?[- ]?reply|abwesenheit|abwesend|out of (the )?office|nicht im büro|autoreply|urlaubsnotiz|vacation)\b|^(re|aw|antw)?:?\s*(abwesenheitsnotiz|out of office)", RegexOptions.IgnoreCase)]
    private static partial Regex AutoSubjectRx();

    // Höflichkeitsformeln am Ende einer Mail - laden ein, verlangen aber nichts
    [GeneratedRegex(@"\b(bei|für)\s+(weitere[n]?\s+|etwaige[n]?\s+|eventuelle[n]?\s+)?(fragen|rückfragen)\b|\b(falls|wenn|sofern)\s+(sie|du|ihr)\s+(noch\s+)?(weitere\s+)?(fragen|rückfragen|anmerkungen|wünsche)\b|\b(stehe|stehen|steht)\b[^.?!]*\bzur\s+verfügung\b|\bmelden\s+sie\s+sich\s+gerne?\b.*\b(falls|wenn)\b|\b(if|should)\s+you\s+(have|need)\s+(any\s+)?(further\s+|more\s+|other\s+)?(questions|help|assistance)\b|\bfeel\s+free\s+to\b|\b(don'?t|do\s+not)\s+hesitate\b", RegexOptions.IgnoreCase)]
    private static partial Regex CourtesyRx();

    // Anrede und Gruß
    [GeneratedRegex(@"^(hallo|hi|hey|moin|servus|guten\s+(morgen|tag|abend)|liebe[rs]?|sehr\s+geehrte[rs]?|hello|dear|good\s+(morning|afternoon|evening))(\s+[\p{L}.'-]+){0,4}\s*[,!.]?$|^(viele|beste|liebe|herzliche|freundliche|schöne)\s+grüße(\s+[\p{L}.,'-]+){0,4}$|^(mit\s+freundlichen\s+grüßen|mfg|lg|vg|gruß|grüße|best(\s+regards)?|kind\s+regards|regards|cheers|best\s+wishes|sincerely|yours(\s+sincerely)?)(\s+[\p{L}.,'-]+){0,4}\s*[,!.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex GreetingRx();

    // Reine Danksagung oder Bestätigung
    [GeneratedRegex(@"^(vielen\s+|herzlichen\s+|besten\s+|tausend\s+)?(dank|danke)\b[^?]*$|^(passt|erledigt|alles\s+klar|ok(ay)?|super|perfekt|prima|top|gerne?|gern\s+geschehen|habe?\s+ich\s+erhalten|ist\s+angekommen|verstanden|einverstanden|machen\s+wir\s+so|thanks|thank\s+you|many\s+thanks|great|sounds\s+good|done|noted|got\s+it|perfect|received|understood|will\s+do|fine)\b[^?]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ThanksRx();

    // Bitte um Rückmeldung
    [GeneratedRegex(@"\b(bitte|bäte|bitten)\s+(\w+\s+){0,3}um\s+(\w+\s+){0,2}(rückmeldung|antwort|bestätigung|info|nachricht|feedback|stellungnahme|rückruf)\b|\bfreue?n?\s+(mich|uns)\s+(\w+\s+){0,3}(auf|über)\s+(ihre|deine|eure|eine)\s+(\w+\s+)?(antwort|rückmeldung|nachricht|reaktion)\b|\b(lassen\s+sie|lass|lasst)\s+(mich|uns)\s+(\w+\s+){0,3}wissen\b|\b(geben\s+sie|gib|gebt|sagen\s+sie|sag|sagt)\s+(mir|uns)\s+(\w+\s+){0,3}bescheid\b|\b(melden\s+sie\s+sich|meld(e)?\s+dich|meldet\s+euch)\b|\bbitte\s+(\w+\s+){0,2}(bestätigen|bestätige|antworten|antworte|zurückmelden|melden)\b|\b(warte|warten)\s+(\w+\s+){0,2}auf\s+(ihre|deine|eure)\s+(antwort|rückmeldung|nachricht)\b|\b(please\s+)?let\s+(me|us)\s+know\b|\blook(ing)?\s+forward\s+to\s+(your|hearing)\b|\bget\s+back\s+to\s+(me|us)\b|\bplease\s+(confirm|reply|respond|advise|get\s+back)\b|\bawait(ing)?\s+your\s+(reply|response|answer|feedback)\b|\bkeep\s+(me|us)\s+posted\b", RegexOptions.IgnoreCase)]
    private static partial Regex FeedbackRx();

    // Bitte um etwas
    [GeneratedRegex(@"\b(können|könnten|würden|wären|hätten|kannst|könntest|würdest|wärst|hättest|könnt|würdet)\s+(sie|du|ihr)\b|\bbitte\s+(\w+\s+){0,5}(schicken|schick|senden|sende|zusenden|zuschicken|prüfen|prüfe|bestätigen|zurückrufen|anrufen|ausfüllen|unterschreiben|überweisen|mitteilen|weiterleiten|beantworten|nennen|klären|freigeben|erledigen|vorbeibringen|nachreichen|ergänzen|korrigieren)\b|\b(ich|wir)\s+(bitte|bitten)\s+(sie|dich|euch)\b|\b(ich|wir)\s+(bräuchte|bräuchten|brauche|brauchen)\s+(noch\s+)?(von\s+(ihnen|dir|euch)|ihre|deine|eure)\b|\b(could|would|can|will)\s+you\b|\bplease\s+(\w+\s+){0,2}(send|confirm|review|check|sign|call|provide|share|forward|fill|complete|update|approve)\b|\b(i|we)\s+need\s+you\s+to\b|\bkindly\s+\w+", RegexOptions.IgnoreCase)]
    private static partial Regex RequestRx();

    // Frist an den Empfänger
    [GeneratedRegex(@"\bbis\s+(spätestens\s+|zum\s+|zur\s+|kommenden\s+|nächsten\s+)?(morgen|übermorgen|heute|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|ende\s+(der|dieser|nächster|kommender)?\s*woche|wochenende|monatsende|ende\s+des\s+monats|mitte\s+\w+|\d{1,2}\.\s?(\d{1,2}\.|\p{L}+))|\b(by|until|before|no\s+later\s+than)\s+(tomorrow|today|monday|tuesday|wednesday|thursday|friday|saturday|sunday|end\s+of\s+(the\s+)?(day|week|month)|eod|eow|cob|\d{1,2}(st|nd|rd|th)?\b|(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)\p{L}*)", RegexOptions.IgnoreCase)]
    private static partial Regex DeadlineRx();

    [GeneratedRegex(@"\b(bitte|please)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PleaseRx();
}
