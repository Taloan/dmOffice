using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Chat;

/// <summary>
/// Noch einmal erinnern (Version 2): Unter jeder Erinnerung stehen Knöpfe „In 10 Minuten“, „In 1 Stunde“, „Morgen“; dazu
/// versteht Pukki „Erinnere mich in einer halben Stunde noch mal“ als Wiederholung der letzten Erinnerung. Die Wiederholungen
/// liegen in chat.db (kv „pukki.snooze.&lt;Benutzer&gt;“), der Kalender bleibt unberührt.
/// </summary>
public static partial class Pukki
{
    private sealed record Snoozed(string Title, DateTime Start, DateTime At);

    private const string SnoozeKey = "pukki.snooze.", LastReminderKey = "pukki.lastreminder.";

    /// <summary>Knöpfe unter einer Erinnerung: noch einmal erinnern.</summary>
    internal static List<ChatAction> SnoozeActions(string title, DateTime startUtc)
    {
        var data = JsonSerializer.Serialize(new Snoozed(title, startUtc, default));
        return [new("snooze-10", "In 10 Minuten", data), new("snooze-60", "In 1 Stunde", data), new("snooze-day", "Morgen", data)];
    }

    /// <summary>Gerade erinnert: merken, damit „noch mal“ weiß, woran.</summary>
    internal static void RememberReminder(ChatStore chat, string user, string title, DateTime startUtc) =>
        chat.SetKv(LastReminderKey + user, JsonSerializer.Serialize(new Snoozed(title, startUtc, DateTime.UtcNow)));

    /// <summary>Die letzte Erinnerung, wenn sie höchstens zwölf Stunden her ist.</summary>
    private static Snoozed? LastReminder(ChatStore chat, string user)
    {
        try
        {
            return chat.GetKv(LastReminderKey + user) is { } json && JsonSerializer.Deserialize<Snoozed>(json) is { } s
                && DateTime.UtcNow - s.At < TimeSpan.FromHours(12) ? s : null;
        }
        catch (JsonException) { return null; }
    }

    private static List<Snoozed> Snoozes(ChatStore chat, string user)
    {
        try { return chat.GetKv(SnoozeKey + user) is { } json ? JsonSerializer.Deserialize<List<Snoozed>>(json) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private static void AddSnooze(ChatStore chat, string user, Snoozed s)
    {
        var list = Snoozes(chat, user);
        // Dieselbe Erinnerung nur einmal vormerken - die neue Zeit gilt
        list.RemoveAll(x => x.Title == s.Title && x.Start == s.Start);
        list.Add(s);
        chat.SetKv(SnoozeKey + user, JsonSerializer.Serialize(list));
    }

    /// <summary>Fällige Wiederholungen herausnehmen.</summary>
    internal static List<(string Title, DateTime Start)> DueSnoozes(ChatStore chat, string user, DateTime utcNow)
    {
        var list = Snoozes(chat, user);
        var due = list.Where(s => s.At <= utcNow).ToList();
        if (due.Count == 0) return [];
        list.RemoveAll(s => s.At <= utcNow);
        if (list.Count == 0) chat.DeleteKv(SnoozeKey + user); else chat.SetKv(SnoozeKey + user, JsonSerializer.Serialize(list));
        return due.Select(s => (s.Title, s.Start)).ToList();
    }

    /// <summary>Text einer Erinnerung: „Erinnerung: Zähneputzen – heute 10:00“.</summary>
    internal static string ReminderText(UserSpace space, string title, DateTime startUtc, string? suffix = null)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, space.Zone);
        var when = DayWords(DateOnly.FromDateTime(local), DateOnly.FromDateTime(Now(space))) + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return L($"Erinnerung: {title} – {when}", $"Reminder: {title} – {when}") + suffix;
    }

    /// <summary>Knopf unter einer Erinnerung gedrückt.</summary>
    private static Reply Snooze(ChatStore chat, UserSpace space, ChatMessage message, string key, ChatAction action)
    {
        var s = JsonSerializer.Deserialize<Snoozed>(action.Data ?? "") ?? throw new InvalidOperationException("Das ist schon erledigt.");
        chat.SetActions(message.Id, null);
        var now = DateTime.UtcNow;
        var at = key switch
        {
            "snooze-10" => now.AddMinutes(10),
            "snooze-60" => now.AddHours(1),
            _ => now.AddDays(1),
        };
        return SnoozeAt(chat, space, s.Title, s.Start, at);
    }

    private static Reply SnoozeAt(ChatStore chat, UserSpace space, string title, DateTime start, DateTime atUtc)
    {
        AddSnooze(chat, space.User.Id, new Snoozed(title, start, atUtc));
        var local = TimeZoneInfo.ConvertTimeFromUtc(atUtc, space.Zone);
        var day = PukkiParse.DayText(DateOnly.FromDateTime(local), DateOnly.FromDateTime(Now(space)));
        var when = day == "heute" ? "um " + local.ToString("HH:mm", CultureInfo.InvariantCulture) + " Uhr" : day + " um " + local.ToString("HH:mm", CultureInfo.InvariantCulture) + " Uhr";
        return $"Okay, ich erinnere dich {when} noch einmal an „{title}“.";
    }

    private static readonly Regex AgainRx = new(@"\b(?:noch\s*mal|noch\s+einmal|erneut|wieder|nochmals)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// „Erinnere mich in einer halben Stunde noch mal“ (oder ohne Thema kurz nach einer Erinnerung): die letzte Erinnerung
    /// wiederholen statt einen neuen Termin anzulegen. Null: gehört nicht hierher.
    /// </summary>
    private static Reply? SnoozeByText(Ctx c, string body)
    {
        var chat = c.Chat ?? c.Services.GetRequiredService<ChatStore>();
        if (LastReminder(chat, c.Space.User.Id) is not { } last) return null;
        var again = AgainRx.IsMatch(body);
        var rest = AgainRx.Replace(body, " ");
        rest = Regex.Replace(rest, @"\b(?:daran|dran|bitte|mich|uns)\b", " ", RegexOptions.IgnoreCase).Trim(' ', ',', '.', '!', '?');
        var w = PukkiParse.Parse(rest, c.Now);
        // Ohne „noch mal“ nur, wenn außer der Zeit nichts dasteht und die Erinnerung eben erst kam
        if (!again && (CleanTitle(w.Title).Length > 0 || DateTime.UtcNow - last.At > TimeSpan.FromMinutes(30) || w.Start == null)) return null;
        if (again && CleanTitle(w.Title).Length > 0 && !last.Title.Contains(CleanTitle(w.Title), StringComparison.CurrentCultureIgnoreCase)) return null;
        if (w.Start is not TimeOnly time)
            return new Reply($"Wann soll ich dich noch einmal an „{last.Title}“ erinnern?", Actions: SnoozeActions(last.Title, last.Start));
        var at = TimeZoneInfo.ConvertTimeToUtc((w.Date ?? c.Today).ToDateTime(time), c.Space.Zone);
        if (at <= DateTime.UtcNow) return "Die Zeit ist schon vorbei – sag mir eine spätere, zum Beispiel: in 20 Minuten noch mal.";
        return SnoozeAt(chat, c.Space, last.Title, last.Start, at);
    }
}
