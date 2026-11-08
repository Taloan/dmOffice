using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;

namespace DmOffice.Chat;

/// <summary>
/// Vorhaben der Woche: Der Wochenrückblick des Coachs fragt „Was möchtest du dir für die nächste Woche vornehmen?“. Die nächste
/// Antwort (zwölf Stunden lang, keine Frage, kein Befehl) merkt sich Pukki als Vorhaben; der nächste Rückblick nennt es wieder.
/// „Was habe ich mir vorgenommen?“ fragt es ab. Liegt in chat.db (kv „pukki.weekgoal.&lt;Benutzer&gt;“).
/// </summary>
public static partial class Pukki
{
    private const string WeekGoalKey = "pukki.weekgoal.", WeekGoalPendingKey = "pukki.weekgoal.pending.";
    private sealed record WeekGoal(string Text, DateTime At);

    private static readonly Regex NoGoalRx = new(@"^(?:nein|nö|noe|nichts|nix|keins?|kein vorhaben|weiß nicht|weiss nicht|keine ahnung|mal sehen|mal schauen|später|lass mal)[\s!.]*$", RegexOptions.IgnoreCase);
    private static readonly Regex AskGoalRx = new(@"\b(?:vorgenommen|vorhaben|wochenziel|vorsatz)\b", RegexOptions.IgnoreCase);

    /// <summary>Der Wochenrückblick ist raus: die nächste Antwort ist das Vorhaben.</summary>
    public static void WeekGoalAsked(ChatStore chat, string user) =>
        chat.SetKv(WeekGoalPendingKey + user, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));

    /// <summary>Das Vorhaben aus dem letzten Rückblick (höchstens acht Tage alt).</summary>
    public static string? WeekGoalOf(ChatStore chat, string user)
    {
        try
        {
            return chat.GetKv(WeekGoalKey + user) is { } json && JsonSerializer.Deserialize<WeekGoal>(json) is { } g
                && DateTime.UtcNow - g.At < TimeSpan.FromDays(8) ? g.Text : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Antwort auf den Rückblick oder Frage nach dem Vorhaben. Null: gehört nicht hierher.</summary>
    private static Reply? WeekGoalSaid(UserSpace space, ChatStore? chat, string raw)
    {
        if (chat == null) return null;
        var user = space.User.Id;
        var text = raw.Trim();
        if (text.EndsWith('?') && AskGoalRx.IsMatch(text))
            return WeekGoalOf(chat, user) is { } current ? $"Dein Vorhaben für diese Woche: „{current}“." : "Für diese Woche hast du dir nichts vorgenommen. Ich frage sonntags im Wochenrückblick danach.";
        if (chat.GetKv(WeekGoalPendingKey + user) is not { } at || !long.TryParse(at, out var ticks)) return null;
        if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromHours(12) || text.EndsWith('?') || text.Length < 3) return null;
        var (word, _) = Split(Normalize(text));
        if (CommandWord(word) is "termin" or "event" or "aufgabe" or "task" or "todo" or "notiz" or "note" or "einkauf" or "einkaufsliste" or "essen" or "hilfe") return null;
        chat.DeleteKv(WeekGoalPendingKey + user);
        if (NoGoalRx.IsMatch(text)) return "Okay, dann ohne festes Vorhaben. Nächsten Sonntag gibt es wieder den Rückblick.";
        var goal = Cap(text.TrimEnd('.', '!', ' '));
        chat.SetKv(WeekGoalKey + user, JsonSerializer.Serialize(new WeekGoal(goal, DateTime.UtcNow)));
        return $"Gut, dein Vorhaben für diese Woche: „{goal}“. Im nächsten Wochenrückblick schauen wir, wie es lief.";
    }
}
