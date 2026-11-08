using System.Text.RegularExpressions;

namespace DmOffice.Chat;

/// <summary>
/// Pukkis Stimmung je Nachricht (Version 2): eine der Posen aus den Bögen in docs/pukki/. Web und App zeigen sie neben der
/// Nachricht, in der Figur, die sich der Benutzer ausgesucht hat. Ohne besondere Stimmung (null) erklärt Pukki („zeigen“).
/// </summary>
public static partial class Pukki
{
    /// <summary>Die Posen (gleiche Namen wie die Bilder in Web und App).</summary>
    public static readonly string[] Moods =
        ["hallo", "winken", "daumen", "frage", "idee", "herz", "jubel", "bitte", "lesen", "laptop", "lupe", "zeigen", "checkliste", "arbeitet", "staunen", "kaffee"];

    private static readonly Regex MoodSorryRx = new(@"^(?:das hat nicht geklappt|entschuldige|tut mir leid|oh,? das tut mir leid|das tut mir leid|schade|leider)|\b(?:kann ich (?:dir )?leider|kann ich nicht|darf ich nicht|leider nicht|leider kein)", RegexOptions.IgnoreCase);
    private static readonly Regex MoodDoneRx = new(@"^(?:eingetragen|notiert|erledigt|gespeichert|verschoben|gelöscht|abgehakt|okay|ok[,.!]|gut, |gemacht|zurückgenommen|steht (?:jetzt )?(?:auf|in|im)|zugesagt|mit vorbehalt zugesagt|abgesagt –)", RegexOptions.IgnoreCase);
    private static readonly Regex MoodFoundRx = new(@"^(?:zu „.*“ habe ich gefunden|ich habe .*gefunden|das habe ich zu|gefunden|treffer)", RegexOptions.IgnoreCase);
    private static readonly Regex MoodJoyRx = new(@"^(?:juhu|super|klasse|toll|prima|geschafft|hurra|glückwunsch|herzlichen glückwunsch|alles erledigt)|\bhat zugesagt\b|🎉", RegexOptions.IgnoreCase);
    private static readonly Regex MoodLoveRx = new(@"^(?:danke|gern geschehen|gern!|das freut mich|freut mich|das ist lieb|kein problem)|❤|😊", RegexOptions.IgnoreCase);

    /// <summary>Stimmung aus Text, Anhängen und Knöpfen - null: keine besondere.</summary>
    internal static string? MoodOf(string? text, IReadOnlyList<ChatRef> refs, IReadOnlyList<ChatAction>? actions)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("Guten Morgen", StringComparison.OrdinalIgnoreCase)) return "kaffee";
        if (t.StartsWith("Hallo", StringComparison.OrdinalIgnoreCase) || t.Contains("ich bin Pukki", StringComparison.OrdinalIgnoreCase)) return "hallo";
        if (t.StartsWith("Erinnerung:", StringComparison.OrdinalIgnoreCase)) return "idee";
        if (t.Contains(" lädt dich ein", StringComparison.Ordinal)) return "winken";
        if (MoodSorryRx.IsMatch(t)) return "bitte";
        if (MoodJoyRx.IsMatch(t)) return "jubel";
        if (MoodLoveRx.IsMatch(t)) return "herz";
        // Aufgaben zum Abhaken (Tagesüberblick, offene Aufgaben)
        if (t.StartsWith("Auf der Einkaufsliste", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Auf die Liste", StringComparison.OrdinalIgnoreCase)) return "checkliste";
        if (actions?.Any(a => a.Key.StartsWith("done")) == true || refs.Count(r => r.Type == ChatRef.TypeTask) > 1) return "checkliste";
        // Rückfrage - auch mit Antwort-Knöpfen darunter
        if (t.EndsWith('?') || actions?.Any(a => a.Key.StartsWith("say") || a.Key.StartsWith("pick")) == true) return "frage";
        if (MoodFoundRx.IsMatch(t) || refs.Count > 1) return "lupe";
        if (refs.Any(r => r.Type == ChatRef.TypeNote)) return "lesen";
        if (MoodDoneRx.IsMatch(t)) return "daumen";
        if (t.StartsWith("Übrigens", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Tipp", StringComparison.OrdinalIgnoreCase)) return "idee";
        return null;
    }
}
