using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Chat;

/// <summary>
/// Timer: „Timer auf 20 Minuten“, „Pizza-Timer 15 Minuten“, „Welche Timer laufen?“, „Timer abbrechen“, „Pizza-Timer um 5 Minuten
/// verlängern“, „Timer auf 10 Minuten ändern“. Die Timer liegen in chat.db (kv „pukki.timers.&lt;Benutzer&gt;“), nicht im Kalender.
/// Abgelaufen meldet sich Pukki im Chat (Runde der Erinnerungen, etwa einmal je Minute).
/// </summary>
public static partial class Pukki
{
    internal sealed record TimerEntry(string Name, DateTime Started, DateTime At);

    private const string TimersKey = "pukki.timers.";
    private const int MaxTimerMinutes = 24 * 60;

    private static List<TimerEntry> Timers(ChatStore chat, string user)
    {
        try { return chat.GetKv(TimersKey + user) is { } json ? JsonSerializer.Deserialize<List<TimerEntry>>(json) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private static void SaveTimers(ChatStore chat, string user, List<TimerEntry> list)
    {
        if (list.Count == 0) chat.DeleteKv(TimersKey + user);
        else chat.SetKv(TimersKey + user, JsonSerializer.Serialize(list.OrderBy(x => x.At).ToList()));
    }

    /// <summary>Abgelaufene Timer herausnehmen.</summary>
    internal static List<TimerEntry> DueTimers(ChatStore chat, string user, DateTime utcNow)
    {
        var list = Timers(chat, user);
        var due = list.Where(x => x.At <= utcNow).ToList();
        if (due.Count == 0) return [];
        SaveTimers(chat, user, list.Where(x => x.At > utcNow).ToList());
        return due;
    }

    internal static string TimerDoneText(TimerEntry x) =>
        L($"⏰ {TimerLabel(x.Name)} ist abgelaufen ({Duration(x.At - x.Started)}).", $"⏰ {TimerLabelEn(x.Name)} is done ({DurationEn(x.At - x.Started)}).");

    private static string TimerLabel(string name) => name.Length == 0 ? "Der Timer" : $"Der Timer „{name}“";
    private static string TimerLabelEn(string name) => name.Length == 0 ? "The timer" : $"The timer “{name}”";
    private static string TimerName(string name) => name.Length == 0 ? "Timer" : $"Timer „{name}“";
    /// <summary>Knopf für den Timer ohne Namen, wenn mehrere laufen.</summary>
    private const string Unnamed = "ohne Namen";
    private static string TimerRef(string name) => name.Length == 0 ? "Timer " + Unnamed : TimerName(name);

    /// <summary>„20 Minuten“, „1 Stunde 5 Minuten“, „45 Sekunden“.</summary>
    private static string Duration(TimeSpan d)
    {
        var m = (int)Math.Round(d.TotalMinutes);
        if (m < 1) return "weniger als 1 Minute";
        var h = m / 60; m %= 60;
        var parts = new List<string>();
        if (h > 0) parts.Add(h == 1 ? "1 Stunde" : $"{h} Stunden");
        if (m > 0) parts.Add(m == 1 ? "1 Minute" : $"{m} Minuten");
        return string.Join(" ", parts);
    }

    private static string DurationEn(TimeSpan d)
    {
        var m = (int)Math.Round(d.TotalMinutes);
        if (m < 1) return "less than a minute";
        var h = m / 60; m %= 60;
        var parts = new List<string>();
        if (h > 0) parts.Add(h == 1 ? "1 hour" : $"{h} hours");
        if (m > 0) parts.Add(m == 1 ? "1 minute" : $"{m} minutes");
        return string.Join(" ", parts);
    }

    private static readonly Regex TimerWordRx = new(@"(?:^|[\s\-])(?:timer|timers|kurzzeitwecker|eieruhr)\b", RegexOptions.IgnoreCase);
    private static readonly Regex TimerDurRx = new(@"(?:\d+|eine?n?|einer|zwei|drei|vier|fünf|fuenf|sechs|sieben|acht|neun|zehn|zwölf|zwoelf|fünfzehn|fuenfzehn|zwanzig|dreißig|dreissig|vierzig|fünfzig|fuenfzig|halbe?n?)(?:\s+halben)?\s*(?:minuten?|min|stunden?|std|h)\b", RegexOptions.IgnoreCase);
    private static readonly Regex TimerCancelRx = new(@"\b(?:abbrechen|abbruch|brich|stopp?e?n?|stop|anhalten|halt|aus|ausschalten|ausmachen|löschen|loeschen|lösche|beenden|beende|entfernen|weg|cancel)\b", RegexOptions.IgnoreCase);
    private static readonly Regex TimerExtendRx = new(@"\b(?:verlänger\w*|verlaenger\w*|länger|laenger|mehr|plus|dazu|drauf|zusätzlich|zusaetzlich)\b", RegexOptions.IgnoreCase);
    private static readonly Regex TimerChangeRx = new(@"\b(?:ändern|aendern|änder|aender|umstellen|stell\w*\s+\w*\s*um|neu\s+stellen|korrigieren|verkürz\w*|verkuerz\w*|kürzer|kuerzer|weniger)\b", RegexOptions.IgnoreCase);
    private static readonly Regex TimerListRx = new(@"\b(?:welche|welcher|laufende?n?|läuft|laeuft|laufen|zeig\w*|liste|wie\s+lange|wieviel|wie\s+viel|status|übersicht|uebersicht|noch\s+übrig|restzeit)\b", RegexOptions.IgnoreCase);
    private static readonly Regex TimerFillRx = new(@"\b(?:bitte|kannst|könntest|koenntest|du|mir|mal|einen|ein|eine|den|die|das|der|dem|neuen|neue|meinen|meine|mein|für|fuer|auf|von|mit|um|stell|stelle|stellen|setz|setze|setzen|mach|mache|machen|start|starte|starten|an|noch|ist|sind|es|gibt|alle|allen|timer|timers|kurzzeitwecker|eieruhr|und|jetzt|gerade|wie|lange|viel)\b", RegexOptions.IgnoreCase);

    /// <summary>Alles zu Timern. Null: kein Satz über Timer.</summary>
    private static Reply? TimerReply(Ctx c, string t)
    {
        if (!TimerWordRx.IsMatch(t)) return null;
        var chat = c.Chat ?? c.Services.GetRequiredService<ChatStore>();
        var user = c.Space.User.Id;
        var now = DateTime.UtcNow;
        var list = Timers(chat, user).Where(x => x.At > now).ToList();
        var dur = TimerDurRx.Match(t);
        int? minutes = dur.Success ? ReminderMinutes(dur.Value) : null;
        var name = TimerNameOf(t);
        var all = Regex.IsMatch(t, @"\balle\b", RegexOptions.IgnoreCase);

        // Abbrechen
        if (TimerCancelRx.IsMatch(t))
        {
            if (list.Count == 0) return "Es läuft gerade kein Timer.";
            var hit = all ? list : Pick(list, name);
            if (hit.Count == 0) return $"Einen Timer „{name}“ finde ich nicht. " + ListText(list, now);
            if (hit.Count > 1 && !all) return Choose(list, now, "Welchen Timer soll ich abbrechen?", x => $"{TimerRef(x.Name)} abbrechen");
            SaveTimers(chat, user, list.Except(hit).ToList());
            return hit.Count == 1 ? $"{TimerName(hit[0].Name)} ist abgebrochen." : $"Alle {hit.Count} Timer sind abgebrochen.";
        }

        // Verlängern oder ändern
        var extend = TimerExtendRx.IsMatch(t);
        var shorten = Regex.IsMatch(t, @"\b(?:verkürz\w*|verkuerz\w*|kürzer|kuerzer|weniger)\b", RegexOptions.IgnoreCase);
        if ((extend || TimerChangeRx.IsMatch(t)) && list.Count > 0)
        {
            if (minutes == null) return "Um wie viel? Zum Beispiel: Timer um 5 Minuten verlängern.";
            var hit = Pick(list, name);
            if (hit.Count == 0) return $"Einen Timer „{name}“ finde ich nicht. " + ListText(list, now);
            if (hit.Count > 1) return Choose(list, now, "Welchen Timer soll ich ändern?", x => $"{TimerRef(x.Name)} {(extend ? "um" : shorten ? "um" : "auf")} {minutes} Minuten {(extend ? "verlängern" : shorten ? "verkürzen" : "ändern")}");
            var old = hit[0];
            var at = extend ? old.At.AddMinutes(minutes.Value) : shorten ? old.At.AddMinutes(-minutes.Value) : now.AddMinutes(minutes.Value);
            if (at <= now) return $"Dann wäre {TimerName(old.Name)} schon abgelaufen – er läuft noch {Duration(old.At - now)}.";
            if (at - now > TimeSpan.FromMinutes(MaxTimerMinutes)) return "Ein Timer geht bis 24 Stunden. Für länger trag lieber einen Termin ein.";
            list.Remove(old);
            list.Add(old with { At = at, Started = extend || shorten ? old.Started : now });
            SaveTimers(chat, user, list);
            return $"{TimerName(old.Name)} läuft jetzt noch {Duration(at - now)}.";
        }

        // Starten
        if (minutes != null)
        {
            if (minutes > MaxTimerMinutes) return "Ein Timer geht bis 24 Stunden. Für länger trag lieber einen Termin ein.";
            // Zweiter Timer ohne Namen: durchnummerieren, damit er sich ansprechen lässt
            if (name.Length == 0 && list.Any(x => x.Name.Length == 0))
                name = Enumerable.Range(2, 50).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).First(n => list.All(x => x.Name != n));
            var entry = new TimerEntry(name, now, now.AddMinutes(minutes.Value));
            list.Add(entry);
            SaveTimers(chat, user, list);
            return $"{TimerName(name)} läuft: {Duration(entry.At - now)}. Ich melde mich, wenn er abgelaufen ist.";
        }

        // Anzeigen („Welche Timer laufen?“, „Wie lange noch?“, einfach „Timer?“)
        if (TimerListRx.IsMatch(t) || name.Length == 0 || list.Any(x => x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            var hit = Pick(list, name);
            return ListText(hit.Count > 0 ? hit : list, now);
        }
        return "Wie lange? Zum Beispiel: Timer auf 20 Minuten.";
    }

    /// <summary>Name aus dem Satz: „Pizza-Timer“, „Timer für die Pizza“ - ohne Dauer, Verben und Füllwörter.</summary>
    private static string TimerNameOf(string t)
    {
        // Aus einem Knopf: Name in Anführungszeichen oder „ohne Namen“
        var quoted = Regex.Match(t, @"[„""“](?<n>[^„""“”]+)[“”""]");
        if (quoted.Success) return quoted.Groups["n"].Value.Trim();
        if (Regex.IsMatch(t, @"\bohne\s+namen\b", RegexOptions.IgnoreCase)) return Unnamed;
        var s = TimerDurRx.Replace(t, " ");
        s = TimerCancelRx.Replace(s, " ");
        s = TimerExtendRx.Replace(s, " ");
        s = TimerChangeRx.Replace(s, " ");
        s = TimerListRx.Replace(s, " ");
        s = Regex.Replace(s, @"(\p{L})-(?=timer|kurzzeitwecker|eieruhr)", "$1 ", RegexOptions.IgnoreCase);
        s = TimerFillRx.Replace(s, " ");
        s = Regex.Replace(s, @"[^\p{L}\p{N}\s\-]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-');
        return Cap(s);
    }

    /// <summary>Timer zum Namen; ohne Namen alle.</summary>
    private static List<TimerEntry> Pick(List<TimerEntry> list, string name) => name.Length == 0
        ? list
        : name == Unnamed ? list.Where(x => x.Name.Length == 0).ToList()
        : list.Where(x => x.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase) || x.Name.Length > 0 && name.Contains(x.Name, StringComparison.CurrentCultureIgnoreCase)).ToList();

    private static string ListText(List<TimerEntry> list, DateTime now)
    {
        if (list.Count == 0) return "Es läuft gerade kein Timer. Zum Starten: Timer auf 20 Minuten.";
        if (list.Count == 1) return $"{TimerName(list[0].Name)} läuft noch {Duration(list[0].At - now)}.";
        return "Laufende Timer:\n" + string.Join("\n", list.OrderBy(x => x.At).Select(x => $"• {TimerName(x.Name)} – noch {Duration(x.At - now)}"));
    }

    /// <summary>Mehrere passen: Liste und je Timer ein Knopf, der den Satz für genau diesen schickt.</summary>
    private static Reply Choose(List<TimerEntry> list, DateTime now, string question, Func<TimerEntry, string> say)
    {
        var shown = list.OrderBy(x => x.At).Take(4).ToList();
        return new Reply(question + "\n" + string.Join("\n", shown.Select(x => $"• {TimerName(x.Name)} – noch {Duration(x.At - now)}")),
            Actions: shown.Select((x, i) => Say(i, TimerName(x.Name), say(x))).ToList());
    }
}
