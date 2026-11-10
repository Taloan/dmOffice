using System.Text.RegularExpressions;
using DmOffice.Core;

namespace DmOffice.Chat;

/// <summary>
/// Pukki lernt (Version 2) - je Benutzer, denn jede Unterhaltung mit Pukki gehoert genau einem Benutzer:
/// <list type="bullet">
/// <item>Daumen neben seinen Antworten: 👍 bestaetigt („so war es gemeint“), 👎 fragt mit Knoepfen nach, was falsch war.</item>
/// <item>Vorlieben: in welchen Kalender bzw. welche Liste ein Termin oder eine Aufgabe mit diesem Wort gehoert - aus 👍 und
/// aus „Anderer Kalender …“. Gelten nur fuer diesen Benutzer.</item>
/// <item>Lernliste: was er falsch (oder richtig) verstanden hat, samt Erwartung. Sieht nur der Benutzer selbst.</item>
/// </list>
/// Alles liegt in der eigenen, verschluesselten Benutzerdatenbank (Schluessel „pukki.learn“) und verlässt sie nicht.
/// </summary>
public static partial class Pukki
{
    public const string LearnKey = "pukki.learn";
    private const string ExplainedText = "Danke! Das steht jetzt auf deiner Lernliste";
    private const int MaxEntries = 300, MaxRatings = 500, MaxPrefs = 300;
    /// <summary>Ab dieser Staerke gilt eine Vorliebe: ein Umzug („Anderer Kalender“) zaehlt 2, ein 👍 zaehlt 1.</summary>
    private const int PrefThreshold = 2;

    public sealed class LearnData
    {
        public Dictionary<long, int> Ratings { get; set; } = new();
        public List<LearnEntry> Entries { get; set; } = new();
        public List<LearnPref> Prefs { get; set; } = new();
    }

    /// <param name="Verdict">good (👍), wrong (falsch verstanden), target (falscher Kalender/Liste), time (falsche Zeit), bad (👎 ohne Angabe), ai (KI-Antwort unpassend)</param>
    public sealed record LearnEntry(long Message, DateTime At, string Text, string Answer, string Verdict, string? Expected = null, bool Shared = false);

    /// <param name="Word">Schluesselwort aus dem Titel („zahnarzttermin“)</param>
    public sealed record LearnPref(string Kind, string Word, string Owner, long Collection, string Name, int Score);


    public static LearnData Learned(UserSpace space) => space.Db.GetJson<LearnData>(LearnKey);

    private static void SaveLearned(UserSpace space, LearnData d)
    {
        if (d.Ratings.Count > MaxRatings) d.Ratings = d.Ratings.OrderByDescending(x => x.Key).Take(MaxRatings).ToDictionary(x => x.Key, x => x.Value);
        if (d.Entries.Count > MaxEntries) d.Entries = d.Entries.OrderByDescending(e => e.At).Take(MaxEntries).ToList();
        if (d.Prefs.Count > MaxPrefs) d.Prefs = d.Prefs.OrderByDescending(p => p.Score).Take(MaxPrefs).ToList();
        space.Db.SetJson(LearnKey, d);
    }

    private static readonly HashSet<string> NoKeyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "termin", "aufgabe", "heute", "morgen", "übermorgen", "uebermorgen", "abends", "morgens", "mittags", "nachmittags",
        "früh", "frueh", "uhr", "bitte", "noch", "mal", "nicht", "vergessen", "erinnern", "machen", "kaufen", "holen",
    };

    /// <summary>Schluesselwoerter eines Titels: Woerter ab 4 Buchstaben, ohne Fuellwoerter, klein.</summary>
    internal static List<string> KeyWords(string title) =>
        Regex.Matches(title.ToLowerInvariant(), @"\p{L}{4,}").Select(m => m.Value).Where(w => !NoKeyWords.Contains(w)).Distinct().Take(4).ToList();

    /// <summary>Titel aus der Beschriftung des Angelegten („Termin „Zahnarzt““ → „Zahnarzt“).</summary>
    private static string TitleOf(Created c) => Regex.Match(c.Label, "„(.+)“") is { Success: true } m ? m.Groups[1].Value : c.Label;

    /// <summary>Vorliebe staerken (oder fuer das alte Ziel schwaechen).</summary>
    private static void Prefer(LearnData d, string kind, string title, string owner, long coll, string name, int delta)
    {
        foreach (var w in KeyWords(title))
        {
            var i = d.Prefs.FindIndex(p => p.Kind == kind && p.Word == w && p.Owner == owner && p.Collection == coll);
            var score = (i >= 0 ? d.Prefs[i].Score : 0) + delta;
            if (i >= 0) d.Prefs.RemoveAt(i);
            if (score > 0) d.Prefs.Add(new LearnPref(kind, w, owner, coll, name, Math.Min(score, 10)));
        }
    }

    /// <summary>Gelernter Kalender bzw. gelernte Liste fuer diesen Titel - nur ein Ziel, das es noch gibt und das passt.</summary>
    private static Target? Preferred(IServiceProvider services, UserSpace space, string kind, string title)
    {
        var words = KeyWords(title);
        if (words.Count == 0) return null;
        var prefs = Learned(space).Prefs.Where(p => p.Kind == kind && p.Score >= PrefThreshold && words.Contains(p.Word)).ToList();
        if (prefs.Count == 0) return null;
        var best = prefs.GroupBy(p => (p.Owner, p.Collection)).OrderByDescending(g => g.Sum(p => p.Score)).First().Key;
        return Targets(services, space, kind).FirstOrDefault(t => t.Owner.User.Id == best.Owner && t.Coll.Id == best.Collection);
    }

    /// <summary>„Anderer Kalender …“ gewaehlt: das neue Ziel merken, das alte schwaechen.</summary>
    private static void LearnMove(UserSpace space, string kind, List<Created>? old, Target to)
    {
        var first = old?.FirstOrDefault();
        if (first == null || kind is not ("event" or "task")) return;
        var d = Learned(space);
        var title = TitleOf(first);
        Prefer(d, kind, title, first.Owner, first.Collection, "", -2);
        Prefer(d, kind, title, to.Owner.User.Id, to.Coll.Id, to.Coll.Name, 2);
        SaveLearned(space, d);
    }

    /// <summary>Darf diese Nachricht bewertet werden? Antworten von Pukki direkt auf einen Satz des Benutzers - keine Rueckfragen,
    /// keine Auswahl, nicht der Abschluss einer Rueckfrage nach 👎.</summary>
    public static bool Rateable(ChatMessage m, ChatMessage? prev, string me) =>
        m.Sender == Id && prev?.Sender == me && m.Kind == ChatStore.KindText && !m.Deleted && !string.IsNullOrWhiteSpace(m.Text) && !m.Text!.TrimEnd().EndsWith('?')
        && !m.Text.StartsWith(ExplainedText)
        && m.Actions?.Any(a => a.Key.StartsWith("say") || a.Key.StartsWith("pick") || a.Key.StartsWith("fb-")) != true;

    /// <summary>
    /// Daumen: 1 (👍), -1 (👎), 0 (zurueck). 👍 merkt sich Kalender/Liste als Vorliebe; 👎 fragt nach - mit Knoepfen
    /// (Rueckgaengig, Anderer Kalender, Andere Zeit, Ich erklaere es) oder bei KI-Antworten nur mit „Ich erklaere es“.
    /// </summary>
    public static void Rate(IServiceProvider services, ChatStore chat, UserSpace space, ChatMessage m, int value)
    {
        var me = space.User.Id;
        var prev = chat.Messages(me, m.Room, before: m.Id, limit: 1).FirstOrDefault();
        if (!Rateable(m, prev, me) || RoomOf(chat, me) != m.Room) throw new ArgumentException("Diese Nachricht lässt sich nicht bewerten.");
        var d = Learned(space);
        d.Ratings.TryGetValue(m.Id, out var before);
        if (value == before) return;
        if (value == 0) d.Ratings.Remove(m.Id); else d.Ratings[m.Id] = value;
        d.Entries.RemoveAll(e => e.Message == m.Id && !e.Shared);
        var said = chat.Messages(me, m.Room, before: m.Id, limit: 6).Where(x => x.Sender == me && !string.IsNullOrWhiteSpace(x.Text)).OrderByDescending(x => x.Id).FirstOrDefault()?.Text ?? "";
        var undo = m.Actions?.FirstOrDefault(a => a.Key == "undo");
        var made = undo != null ? Unpack(undo).Made : null;
        var ai = undo == null && m.Actions?.Any(a => a.Key == "move") != true;
        if (value == 1)
        {
            d.Entries.Add(new LearnEntry(m.Id, DateTime.UtcNow, said, m.Text!, "good"));
            foreach (var c in made ?? [])
                if (c.Kind is "event" or "task" && c.Collection != 0)
                    Prefer(d, c.Kind, TitleOf(c), c.Owner, c.Collection, "", 1);
        }
        else if (value == -1)
        {
            d.Entries.Add(new LearnEntry(m.Id, DateTime.UtcNow, said, m.Text!, ai ? "ai" : "bad"));
            // Ein 👎 nimmt die Bestaetigung zurueck: die Vorliebe aus dem 👍 wieder schwaechen
            if (before == 1)
                foreach (var c in made ?? [])
                    if (c.Kind is "event" or "task" && c.Collection != 0) Prefer(d, c.Kind, TitleOf(c), c.Owner, c.Collection, "", -1);
        }
        SaveLearned(space, d);
        if (value != -1) return;

        var data = Pack(new ActData(Origin: m.Id));
        var buttons = new List<ChatAction>();
        if (undo != null) buttons.Add(new ChatAction("fb-undo", "Rückgängig", data));
        if (m.Actions?.FirstOrDefault(a => a.Key == "move") is { } move) buttons.Add(new ChatAction("fb-move", move.Label, data));
        if (made?.Any(c => c.Kind is "event" or "task") == true) buttons.Add(new ChatAction("fb-time", "Andere Zeit", data));
        buttons.Add(new ChatAction("fb-explain", "Ich erkläre es", data));
        chat.Post(Id, m.Room, ai ? "Danke für die Rückmeldung – ich habe es mir notiert. Magst du sagen, was du erwartet hättest?" : "Tut mir leid. Was war falsch?", actions: buttons);
    }

    /// <summary>Knopf aus der Rueckfrage nach 👎. Null: die Antwort hat schon ein anderer Weg gepostet (Rueckgaengig, Umziehen).</summary>
    private static Reply? Feedback(IServiceProvider services, ChatStore chat, UserSpace space, ChatMessage message, string key, ActData d)
    {
        chat.SetActions(message.Id, null);
        var rated = chat.Messages(space.User.Id, message.Room, before: d.Origin + 1, limit: 1).FirstOrDefault(x => x.Id == d.Origin);
        void Verdict(string v, string? expected = null)
        {
            var l = Learned(space);
            var i = l.Entries.FindIndex(e => e.Message == d.Origin && !e.Shared);
            if (i >= 0) l.Entries[i] = l.Entries[i] with { Verdict = v, Expected = expected ?? l.Entries[i].Expected };
            SaveLearned(space, l);
        }
        switch (key)
        {
            case "fb-undo":
                Verdict("wrong");
                if (rated?.Actions?.Any(a => a.Key == "undo") == true) { Act(services, chat, space, rated, "undo"); return null; }
                return "Das ist schon zurückgenommen.";
            case "fb-move":
                Verdict("target");
                if (rated?.Actions?.Any(a => a.Key == "move") == true) { Act(services, chat, space, rated, "move"); return null; }
                return "Das lässt sich nicht mehr umziehen.";
            case "fb-time":
                Verdict("time");
                return "Schreib mir die richtige Zeit, zum Beispiel „mach 16 Uhr draus“ oder „verschieb auf Freitag“.";
            default:
            {
                var mem = MemoryOf(space.User.Id);
                lock (mem) SetAsk(mem, "explain", d.Origin.ToString(), null);
                return "Was hättest du erwartet? Schreib es mir einfach – ich lege es auf deine Lernliste.";
            }
        }
    }

    /// <summary>Antwort auf „Was hättest du erwartet?“ - kommt zur Bewertung auf die Lernliste.</summary>
    private static Reply Explained(Ctx c, string t)
    {
        var origin = long.TryParse(c.Mem.Pending, out var o) ? o : 0;
        ClearAsk(c.Mem);
        var d = Learned(c.Space);
        var i = d.Entries.FindIndex(e => e.Message == origin && !e.Shared);
        if (i >= 0) d.Entries[i] = d.Entries[i] with { Expected = t.Trim(), Verdict = d.Entries[i].Verdict is "bad" ? "wrong" : d.Entries[i].Verdict };
        else d.Entries.Add(new LearnEntry(origin, DateTime.UtcNow, "", "", "wrong", t.Trim()));
        SaveLearned(c.Space, d);
        return ExplainedText + " (Einstellungen → Chat).";
    }

    public static void ForgetEntry(UserSpace space, long message)
    {
        var d = Learned(space);
        d.Entries.RemoveAll(e => e.Message == message);
        SaveLearned(space, d);
    }

    public static void ForgetPref(UserSpace space, string kind, string word)
    {
        var d = Learned(space);
        d.Prefs.RemoveAll(p => p.Kind == kind && p.Word == word);
        SaveLearned(space, d);
    }
}
