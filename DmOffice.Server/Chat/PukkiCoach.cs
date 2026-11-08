using DmOffice.Core;
using System.Text.RegularExpressions;
using DmOffice.Coach;

namespace DmOffice.Chat;

/// <summary>
/// Pukki fragt den Coach (docs/dmcoach-konzept.md, „Coach-Wissen für Pukki“): eigene Tagebuchdaten, Nährwerte, Wissenskarten.
/// Feste Grenzen gehen vor und nie an die KI: eigene Beschwerden („Dazu möchte ich nichts sagen …“), Medikamente (keine Beratung),
/// akute Notfälle (112), Zeichen einer Essstörung. Mit KI bekommt sie die Karte als Grundlage, eigene Werte nur mit Einwilligung
/// im Coach-Profil.
/// </summary>
public static partial class Pukki
{
    private static readonly Regex SelfSaidRx = new(@"\b(ich|mir|mich|mein\w*)\b", RegexOptions.IgnoreCase);

    /// <summary>Fragen, deren Antwort von den Werten der Uhr abhängt - davor holt Pukki den aktuellen Stand (CoachGarmin.RefreshAsync).</summary>
    internal static readonly Regex WatchDataRx = new(@"\b(wasser|getrunken|trink\w*|schritt\w*|gelaufen|gegangen|bewegt|bewegung|kalorien|kcal|verbrannt|schlaf\w*|geschlafen|puls|stress|body\s*battery|hrv|gewicht|wiege|abgenommen|zugenommen|ziel\w*|budget)\b", RegexOptions.IgnoreCase);

    private static Reply? CoachAnswer(Ctx c, string t)
    {
        var question = IsQuestion(t) || AskFor.IsMatch(t);
        // Aufträge mit Tag oder Uhrzeit („Termin Hausarzt morgen 9 Uhr wegen Rückenschmerzen“) sind keine Coach-Sache
        if (!question && PukkiParse.Parse(t, c.Now) is { } when && (when.Date != null || when.Start != null)) return null;
        // Grenzen: Notfälle immer („Er liegt am Boden und atmet nicht“); Beschwerden und Medikamente bei Fragen und Aussagen
        // über sich selbst („Mir ist schwindlig“) - nicht bei „Ibuprofen kaufen“
        if (CoachKnowledge.Guard(t) is { } guard && (guard.Kind is "emergency" or "emergency-info" or "gentle" || question || SelfSaidRx.IsMatch(t)))
        {
            if (guard.Kind == "refuse")
                c.Mem.Why = () => "Weil ich zu Beschwerden und Medikamenten bewusst nichts sage – das gehört zu Ärztin, Arzt oder Apotheke.";
            // Allgemein gefragt („Was tun, wenn jemand bewusstlos ist?“): mit KI darf sie ausführlicher antworten
            return new Reply(guard.Text, Unknown: guard.Kind == "emergency-info");
        }
        if (!question) return null;
        var answer = CoachKnowledge.Ask(c.Space, c.Services.GetRequiredService<CoachFoods>(), t, c.Now);
        if (answer == null) return null;
        // Für die KI: das Wissen der Karte; eigene Werte nur, wenn man das im Coach-Profil erlaubt hat
        string? grounding = null;
        if (answer.Kind == "card" && answer.Grounding != null)
            grounding = answer.Grounding + (answer.Personal != null && CoachStore.Profile(c.Space.Db).AiData ? "\n\nAus dem Coach des Benutzers: " + answer.Personal : "");
        return new Reply(answer.Text, Grounding: grounding);
    }
}
