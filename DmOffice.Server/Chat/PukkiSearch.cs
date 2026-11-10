using System.Globalization;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Chat;

/// <summary>
/// Pukki sucht im eigenen Bestand (Stufe 5, Schritt 3): „Was hatten wir zum Zahnarzt?“, „Wo ist der Mietvertrag?“.
/// Termine, Aufgaben, Notizen, Kontakte und Dateien (Name und - wenn eingeschaltet - Inhalt); die Treffer hängen als
/// Verweise an der Antwort, ein Tipp öffnet sie. Alles auf dem Server, ohne KI. „Wann ist der nächste Zahnarzt?“
/// beantwortet Pukki.EventInfo (PukkiRead.cs) - auch aus geteilten Kalendern, mit Ort, Teilnehmern und Erinnerung.
/// </summary>
public static partial class PukkiSearch
{
    [GeneratedRegex(@"^\s*(?:such(?:e)?|finde?|find|zeig(?:e)?(?:\s+mir)?|wo\s+(?:ist|sind|finde\s+ich|liegt|liegen)|was\s+(?:haben|hatten|hab|habe)\s+(?:wir|ich)\s+(?:zu|zum|zur|über|wegen|von)|was\s+wei(?:ß|ss)t\s+du\s+(?:über|zu|zum|zur|von)|gibt\s+es\s+(?:was|etwas)\s+(?:zu|zum|zur|über)|search(?:\s+for)?|where\s+is|what\s+do\s+(?:we|i)\s+have\s+(?:on|about))\b[\s:]*(?<q>.+?)[\s?!.]*$", RegexOptions.IgnoreCase)]
    private static partial Regex FindRx();

    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "was", "wir", "zu", "zum", "zur", "über", "ueber", "von", "wegen", "alles", "etwas", "irgendwas", "termin", "termine", "nächste", "nächster",
        "nächsten", "letzte", "letzter", "letzten", "datei", "dateien", "notiz", "notizen", "wann", "wo", "ist", "der", "die", "das",
    };

    /// <summary>Eine Suchfrage? Dann die Antwort, sonst null. („Wann ist …?“ beantwortet Pukki.EventInfo - mit Ort, Teilnehmern und Erinnerung.)</summary>
    public static Pukki.Reply? TryAnswer(IServiceProvider services, UserSpace space, string text)
    {
        if (FindRx().Match(text) is { Success: true } f) return Find(services, space, f.Groups["q"].Value);
        return null;
    }

    internal static List<string> Terms(string q) =>
        PukkiWords.Words(q).Where(w => w.Length >= 3 && !Ignore.Contains(w)).ToList();

    /// <summary>Kommt eines der Suchwörter (auch gebeugt, auch ohne Umlaute) im Text vor?</summary>
    internal static bool Matches(string? text, List<string> terms)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.ToLowerInvariant();
        var f = PukkiWords.Fold(t);
        return terms.Any(term => PukkiWords.Forms(term).Any(x => x.Length >= 3 && (t.Contains(x) || f.Contains(PukkiWords.Fold(x)))));
    }

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static string When(DateTime local, bool allDay, DateOnly today)
    {
        var d = DateOnly.FromDateTime(local);
        var day = Math.Abs(d.DayNumber - today.DayNumber) <= 1 ? PukkiParse.DayText(d, today)
            : d.Year == today.Year ? local.ToString("ddd, d.M.", De) : local.ToString("ddd, d.M.yyyy", De);
        return allDay ? day : day + ", " + local.ToString("HH:mm");
    }

    /// <summary>Eigene Termine rund um heute (ein Jahr zurück und voraus), die passen - je Termin ein Vorkommen.</summary>
    private static (List<(EventDto E, DateTime Local)> Next, List<(EventDto E, DateTime Local)> Last) Events(UserSpace space, List<string> terms)
    {
        var now = DateTime.UtcNow;
        var cals = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT")).Select(c => c.Id).ToList();
        var hits = CalendarModel.Events(space, now.AddDays(-400), now.AddDays(400), cals)
            .Where(e => Matches(e.Title + " " + e.Location + " " + e.Description, terms))
            .Select(e => (E: e, Local: Pukki.Local(e.Start, space.Zone))).ToList();
        var nowLocal = Pukki.Now(space);
        var next = hits.Where(h => h.Local >= nowLocal.Date).OrderBy(h => h.Local).DistinctBy(h => h.E.Uri).Take(3).ToList();
        var last = hits.Where(h => h.Local < nowLocal.Date).OrderByDescending(h => h.Local).DistinctBy(h => h.E.Uri).Take(2).ToList();
        return (next, last);
    }

    private static Pukki.Reply Find(IServiceProvider services, UserSpace space, string q)
    {
        var terms = Terms(q);
        if (terms.Count == 0) return "Wonach soll ich suchen? Zum Beispiel: Was hatten wir zum Zahnarzt?";
        var today = DateOnly.FromDateTime(Pukki.Now(space));
        var lines = new List<string>();
        var refs = new List<ChatRef>();

        // Termine: die nächsten und der letzte
        var (next, last) = Events(space, terms);
        foreach (var x in next.Take(2)) { lines.Add($"📅 {When(x.Local, x.E.AllDay, today)} – {x.E.Title}"); TryRef(refs, () => ChatApi.RefFor(space, ChatRef.TypeEvent, collection: x.E.Calendar, item: x.E.Uri)); }
        foreach (var x in last.Take(1)) { lines.Add($"📅 zuletzt {When(x.Local, x.E.AllDay, today)} – {x.E.Title}"); TryRef(refs, () => ChatApi.RefFor(space, ChatRef.TypeEvent, collection: x.E.Calendar, item: x.E.Uri)); }

        // Offene Aufgaben
        var lists = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO")).Select(c => c.Id).ToList();
        foreach (var t in CalendarModel.Tasks(space, lists).Where(t => !t.Completed && Matches(t.Title + " " + t.Description, terms)).Take(3))
        { lines.Add("☐ " + t.Title); TryRef(refs, () => ChatApi.RefFor(space, ChatRef.TypeTask, collection: t.List, item: t.Uri)); }

        // Notizen (Volltext)
        foreach (var n in NotesDb.Of(space).Search(string.Join(' ', terms)).Where(n => n.Archived == null).Take(4))
        { lines.Add("📝 " + n.DisplayTitle); TryRef(refs, () => ChatApi.RefFor(space, ChatRef.TypeNote, noteId: n.Id)); }

        // Kontakte
        var books = PimStore.Collections(space.Db, PimStore.AddressBook).Select(c => c.Id).ToList();
        foreach (var c in ContactModel.All(space, books).Where(c => Matches(c.DisplayName + " " + c.Org + " " + c.Title + " " + c.Note, terms)).Take(3))
        { lines.Add("👤 " + c.DisplayName + (c.Org.Length > 0 && c.Org != c.DisplayName ? $" ({c.Org})" : "")); TryRef(refs, () => ChatApi.RefFor(space, ChatRef.TypeContact, collection: c.Book, item: c.Uri)); }

        // Dateien: Name, und Inhalt, wenn die Inhaltssuche eingeschaltet ist; der notizi-Ordner bleibt draußen
        if (!FileStore.FilesOff(space.Db))
        {
            var root = space.Settings.Notizi.Root;
            var paths = new List<string>();
            foreach (var term in terms)
                paths.AddRange(FileStore.Search(space.Db, "", term, 20).Where(f => !FileStore.InRoot(f.Path, root)).Select(f => f.Path));
            if (space.Settings.TextIndex.Enabled)
                paths.AddRange(Search.TextIndex.SearchFiles(space, string.Join(' ', terms), null, 10).Select(h => h.Path).Where(p => !FileStore.InRoot(p, root)));
            foreach (var path in paths.Distinct().Take(4))
                if (FileStore.Get(space.Db, path) is { } f)
                {
                    lines.Add((f.IsDir ? "📁 " : "📄 ") + f.Path);
                    TryRef(refs, () => ChatApi.RefFor(space, f.IsDir ? ChatRef.TypeFolder : ChatRef.TypeFile, fileId: f.FileId));
                }
        }

        var shown = Regex.Replace(q.Trim().TrimEnd('?', '!', '.'), @"^(?:der|die|das|den|dem|mein|meine|meinen|unser|unsere)\s+", "", RegexOptions.IgnoreCase);
        // Nichts in den eigenen Daten: „weiß ich nicht“ - mit KI fragt Pukki dann sie (etwa „Wo ist das Würzjoch?“)
        if (lines.Count == 0) return new Pukki.Reply($"Zu „{shown}“ finde ich nichts – weder in Terminen, Aufgaben, Notizen, Kontakten noch Dateien.", Unknown: true);
        return new Pukki.Reply($"Zu „{shown}“ habe ich gefunden:\n" + string.Join("\n", lines) + (refs.Count > 0 ? "\nTipp unten auf einen Eintrag, dann öffnet er sich." : ""), refs.Take(12).ToList());
    }

    private static void TryRef(List<ChatRef> refs, Func<ChatRef> make)
    {
        try { refs.Add(make()); } catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException) { }
    }
}
