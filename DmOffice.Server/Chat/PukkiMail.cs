using System.Globalization;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Chat;

/// <summary>
/// Pukki und die Mails (Version 2): nur mit Erlaubnis (Schalter „Pukki darf meine Mails lesen“ unter Einstellungen → Chat,
/// je Benutzer im Schlüssel „pukki.mail“). Fragt man ihn nach Mails, ohne es erlaubt zu haben, fragt er mit Ja/Nein - Ja setzt
/// den Schalter und beantwortet gleich die Frage. Er liest nur, was der Server ohnehin zwischenspeichert: Wiedervorlage
/// (zur Nachverfolgung markiert, zurückgestellt), Ungelesenes, Mails von jemandem, Mails von heute.
/// </summary>
public static partial class Pukki
{
    private const string MailKey = "pukki.mail";
    public const string MailAllow = "mail-allow", MailDeny = "mail-deny";

    public static bool MailAllowed(UserDb db) => db.Get(MailKey) == "1";
    public static void SetMailAllowed(UserDb db, bool on) => db.Set(MailKey, on ? "1" : null);

    private static readonly Regex MailWordRx = new(@"\b(?:e-?mails?|mails?|posteingang|postfach|inbox|wiedervorlage|nachverfolgung|ungelesen\w*|zurückgestellt\w*)\b", RegexOptions.IgnoreCase);
    private static readonly Regex MailAskRx = new(@"\?|^(?:welche|was|wie viele|wieviele|gibt es|gibts|hab ich|habe ich|hast du|zeig|zeige|liste|neue|sind|ist|steht|stehen|hat|von)\b|\b(?:von|heute|neu|ungelesen\w*|wiedervorlage|nachverfolgung)\b", RegexOptions.IgnoreCase);
    /// <summary>Aufträge, in denen eine Mail nur vorkommt („Erinnere mich an die Mail von Bernd“, „Schreib Anna eine Mail“) - die gehen ihren normalen Weg.</summary>
    private static readonly Regex MailOrderRx = new(@"^(?:(?:bitte|kannst du|könntest du)\s+)?(?:erinner\w*|schreib\w*|send\w*|schick\w*|trag\w*|notier\w*|merk\w*|antworte\w*|leite\w*|lösch\w*|kauf\w*|termin|aufgabe|notiz)\b", RegexOptions.IgnoreCase);
    /// <summary>Frage nach der Adresse eines Kontakts („E-Mail von Dr. Weiß“, „Mailadresse von Anna“) - das beantworten die Kontakte.</summary>
    private static readonly Regex MailAddressRx = new(@"adresse|^\W*(?:(?:wie|was)\s+ist\s+)?(?:die\s+)?e-?mail\s+(?:von|vom)\b", RegexOptions.IgnoreCase);
    private static readonly Regex MailFromRx = new(@"\b(?:mails?|e-?mails?|nachrichten?)\s+(?:von|vom)\s+(?<who>[\p{L}][\p{L}\-\.@ ]{1,40}?)[\s?!.]*$|\bvon\s+(?<who>[\p{L}][\p{L}\-\.@]{1,40})\b", RegexOptions.IgnoreCase);

    /// <summary>Frage nach Mails? Dann Antwort (oder die Bitte um Erlaubnis); sonst null.</summary>
    private static Reply? MailQuestion(Ctx c, string t)
    {
        if (!MailWordRx.IsMatch(t) || !MailAskRx.IsMatch(t) || MailOrderRx.IsMatch(t) || MailAddressRx.IsMatch(t)) return null;
        if (c.Space.Settings.MailAccounts.Count == 0) return "Du hast in dmOffice kein Mailkonto eingerichtet – da kann ich nichts nachschauen.";
        if (!MailAllowed(c.Space.Db))
            return new Reply("Dafür müsste ich in deine Mails schauen. Darf ich das? Ausschalten kannst du es jederzeit unter Einstellungen → Chat.",
                Actions: [new ChatAction(MailAllow, "Ja, erlauben", Pack(new ActData(Text: t))), new ChatAction(MailDeny, "Nein", null)]);
        return MailAnswer(c.Space, t, c.Now);
    }

    /// <summary>Knöpfe unter der Bitte um Erlaubnis.</summary>
    private static Reply MailPermission(ChatStore chat, UserSpace space, ChatMessage message, string key, ChatAction action)
    {
        chat.SetActions(message.Id, null);
        if (key == MailDeny) return "Okay – deine Mails bleiben bei dir. Wenn du es dir anders überlegst: Einstellungen → Chat.";
        SetMailAllowed(space.Db, true);
        var question = Unpack(action).Text ?? "";
        var answer = MailAnswer(space, question, Now(space));
        return answer with { Text = "Danke! " + answer.Text };
    }

    /// <summary>Antwort aus dem Zwischenspeicher der Mails.</summary>
    internal static Reply MailAnswer(UserSpace space, string t, DateTime now)
    {
        var db = space.Db;
        if (Regex.IsMatch(t, @"wiedervorlage|nachverfolg|markiert|zurückgestellt|erinner", RegexOptions.IgnoreCase))
        {
            var follow = MailStore.FollowUps(db, 50);
            var snoozed = DmOffice.Mail.Snooze.All(db).OrderBy(s => s.Until).ToList();
            if (follow.Count == 0 && snoozed.Count == 0) return "Auf Wiedervorlage liegt gerade nichts – keine Mail ist zur Nachverfolgung markiert oder zurückgestellt.";
            var parts = new List<string>();
            if (follow.Count > 0)
                parts.Add($"Zur Nachverfolgung markiert ({follow.Count}):\n" + string.Join("\n", follow.OrderBy(f => f.Due ?? "9999").Take(10).Select(f =>
                    "• " + Line(f.Mail, space) + (f.Due is { } due && DateOnly.TryParse(due, CultureInfo.InvariantCulture, out var d) ? " – fällig " + PukkiParse.DayText(d, DateOnly.FromDateTime(now)) : ""))));
            if (snoozed.Count > 0)
                parts.Add($"Zurückgestellt ({snoozed.Count}):\n" + string.Join("\n", snoozed.Take(10).Select(s =>
                    "• " + (string.IsNullOrWhiteSpace(s.Subject) ? "(ohne Betreff)" : s.Subject) + " – kommt " + When(TimeZoneInfo.ConvertTimeFromUtc(s.Until, space.Zone), now) + " wieder")));
            return string.Join("\n\n", parts);
        }
        var inboxes = space.Settings.MailAccounts.SelectMany(a => MailStore.Folders(db, a.Id).Where(f => f.Special == "inbox").Select(f => a.Id + "|" + f.Path)).ToList();
        var from = MailFromRx.Match(t);
        if (from.Success && !Regex.IsMatch(from.Groups["who"].Value, @"^(heute|gestern|dieser|letzter|der|den)\b", RegexOptions.IgnoreCase))
        {
            var who = from.Groups["who"].Value.Trim();
            var (total, _, items) = MailStore.Query(db, new MailCriteria { Text = "von:" + who.Split(' ')[0] }, 0, 8);
            if (total == 0) return $"Von „{who}“ finde ich keine Mail.";
            return $"Mails von {who} ({total}, neueste zuerst):\n" + string.Join("\n", items.Select(m => "• " + Line(m, space, sender: false) + (m.Seen ? "" : " ●")));
        }
        if (Regex.IsMatch(t, @"\bheute\b|\bneu(?:e|en)?\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(t, @"ungelesen", RegexOptions.IgnoreCase))
        {
            var day = DateOnly.FromDateTime(now).ToString("yyyy-MM-dd");
            var (total, unread, items) = MailStore.Query(db, new MailCriteria { Folders = inboxes, From = day, To = day }, 0, 8);
            if (total == 0) return "Heute ist noch keine Mail gekommen.";
            return $"Heute {(total == 1 ? "kam eine Mail" : $"kamen {total} Mails")}{(unread > 0 ? $", {unread} davon ungelesen" : "")}:\n"
                + string.Join("\n", items.Select(m => "• " + Line(m, space) + (m.Seen ? "" : " ●")));
        }
        {
            var (total, _, items) = MailStore.Query(db, new MailCriteria { Folders = inboxes, Unread = true }, 0, 8);
            if (total == 0) return "Im Posteingang ist alles gelesen. 👍";
            return $"Ungelesen im Posteingang: {total}{(total > items.Count ? $", die neuesten {items.Count}" : "")}:\n" + string.Join("\n", items.Select(m => "• " + Line(m, space)));
        }
    }

    private static string Line(MailRow m, UserSpace space, bool sender = true)
    {
        var subject = string.IsNullOrWhiteSpace(m.Subject) ? "(ohne Betreff)" : m.Subject.Trim();
        var who = m.From?.Name is { Length: > 0 } n ? n : m.From?.Address;
        var when = When(TimeZoneInfo.ConvertTimeFromUtc(m.Date.UtcDateTime, space.Zone), Now(space));
        return subject + (sender && who != null ? " – " + who : "") + " (" + when + ")";
    }

    private static string When(DateTime local, DateTime now)
    {
        var day = PukkiParse.DayText(DateOnly.FromDateTime(local), DateOnly.FromDateTime(now));
        return day + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
