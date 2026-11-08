using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Journal;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Chat;

/// <summary>
/// Pukki und das Tagebuch (Version 2): abends zur eingestellten Zeit „Wie war dein Tag?“ (mit den Terminen des Tages als
/// Gedankenstütze) - die Antwort wird ein Eintrag; „Tagebuch: …“ jederzeit; morgens im Überblick „Heute vor einem Jahr …“.
/// </summary>
public static partial class Pukki
{
    private const string AskKey = "journal.ask", AskedKey = "pukki.journal.asked.", PendingKey = "pukki.journal.pending.";
    private static readonly Regex JournalRx = new(@"^(?:ins\s+)?tagebuch\s*[:,\-]\s*(?<body>.+)$|^(?:schreib|schreibe|notier|notiere|trag|trage)\s+(?:das\s+|bitte\s+)?(?:ins|in mein|in das)\s+tagebuch\s*[:,]?\s*(?<body>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex NotTodayRx = new(@"^(?:nein|nö|heute nicht|nicht heute|keine lust|später|morgen|lass mal|danke,? nein)[\s!.]*$", RegexOptions.IgnoreCase);

    /// <summary>Uhrzeit fürs Nachfragen am Abend (HH:mm) oder null: aus.</summary>
    public static string? JournalAskTime(UserDb db) => db.Get(AskKey);

    public static void SetJournalAskTime(UserDb db, string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) { db.Set(AskKey, null); return; }
        if (!TimeOnly.TryParse(time, CultureInfo.InvariantCulture, out var t)) throw new ArgumentException("Ungültige Uhrzeit.");
        db.Set(AskKey, t.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>Vor dem normalen Verstehen: „Tagebuch: …“ oder die Antwort auf „Wie war dein Tag?“. Null: nichts fürs Tagebuch.</summary>
    private static Reply? JournalSaid(IServiceProvider services, UserSpace space, ChatStore? chat, string raw)
    {
        var text = raw.Trim();
        var m = JournalRx.Match(text);
        if (m.Success) return ToJournal(space, chat, m.Groups["body"].Value.Trim());
        if (chat == null || chat.GetKv(PendingKey + space.User.Id) is not { } at || !long.TryParse(at, out var ticks)) return null;
        // Die Frage gilt vier Stunden; Fragen und Aufträge an Pukki gehen normal weiter
        if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromHours(4) || text.EndsWith('?') || text.Length < 3) return null;
        var (word, _) = Split(Normalize(text));
        if (CommandWord(word) is "termin" or "event" or "aufgabe" or "task" or "todo" or "notiz" or "note" or "einkauf" or "einkaufsliste" or "essen" or "hilfe") return null;
        chat.DeleteKv(PendingKey + space.User.Id);
        if (NotTodayRx.IsMatch(text)) return "Okay – vielleicht morgen. Gute Nacht!";
        return ToJournal(space, chat, text);
    }

    private static Reply ToJournal(UserSpace space, ChatStore? chat, string text)
    {
        if (text.Length == 0) return "Was soll ich ins Tagebuch schreiben?";
        var journal = JournalStore.Journals(space.Db).First();
        var html = string.Concat(text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Select(l => "<p>" + WebUtility.HtmlEncode(l.Trim()) + "</p>"));
        // Am selben Tag gibt es schon einen Eintrag: dort anhängen statt einen zweiten anzulegen
        var today = DateOnly.FromDateTime(Now(space));
        var same = JournalStore.Entries(space.Db, journal).Where(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(e.Date, space.Zone)) == today)
            .OrderByDescending(e => e.Date).FirstOrDefault();
        JournalEntry saved;
        if (same != null)
            saved = JournalStore.Save(space.Db, journal, new JournalInput
            {
                Journal = journal.Id, Uri = same.Uri, Date = same.Date, Title = same.Title, Html = same.Html + html, Mood = same.Mood, Tags = same.Tags,
                Place = same.Place, Private = same.Private, ShowEvents = same.ShowEvents, Weather = same.Weather, Media = same.Media, Refs = same.Refs,
            });
        else saved = JournalStore.Save(space.Db, journal, new JournalInput { Journal = journal.Id, Date = DateTime.UtcNow, Html = html, ShowEvents = true });
        // Link zum Eintrag: im Web öffnet er ihn zum Weiterschreiben
        var link = $" [Eintrag öffnen](#/journal/entry/{saved.Journal}/{Uri.EscapeDataString(saved.Uri)})";
        return (same != null ? $"Danke – ich habe es zum heutigen Eintrag in „{journal.Name}“ geschrieben. 📔" : $"Danke – steht in deinem Tagebuch „{journal.Name}“. 📔") + link;
    }

    /// <summary>Abends nachfragen (aus dem Takt des Dienstes): einmal am Tag, nur wenn es heute noch keinen Eintrag gibt.</summary>
    internal static void JournalAsk(IServiceProvider services, ChatStore chat, UserSpace space, long room)
    {
        if (JournalAskTime(space.Db) is not { } time || !TimeOnly.TryParse(time, CultureInfo.InvariantCulture, out var at)) return;
        var now = Now(space);
        if (TimeOnly.FromDateTime(now) < at) return;
        var today = DateOnly.FromDateTime(now);
        var key = AskedKey + space.User.Id;
        if (chat.GetKv(key) == today.ToString("yyyy-MM-dd")) return;
        chat.SetKv(key, today.ToString("yyyy-MM-dd"));
        var journals = JournalStore.Journals(space.Db);
        if (journals.SelectMany(j => JournalStore.Entries(space.Db, j)).Any(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(e.Date, space.Zone)) == today)) return;
        var events = DayEvents(services, space, today).Select(e => e.Title).Where(t => t.Length > 0).Distinct().Take(5).ToList();
        var hint = events.Count > 0 ? $" Heute stand an: {string.Join(", ", events)}." : "";
        chat.Post(Id, room, $"Wie war dein Tag?{hint} Erzähl es mir – ich schreibe es in dein Tagebuch.", mood: "lesen");
        chat.SetKv(PendingKey + space.User.Id, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Für den Morgenüberblick: Zeitumstellung letzte Nacht oder heute Nacht (nach der Zeitzone des Benutzers - zurück heißt
    /// Winterzeit, vor Sommerzeit). Null: keine.
    /// </summary>
    internal static string? ClockChange(TimeZoneInfo zone, DateOnly today)
    {
        TimeSpan Offset(DateOnly d) => zone.GetUtcOffset(TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(new TimeOnly(12, 0)), zone));
        var yesterday = Offset(today.AddDays(-1));
        var now = Offset(today);
        var tomorrow = Offset(today.AddDays(1));
        if (now != yesterday)
            return now < yesterday
                ? "Heute Nacht wurde auf Winterzeit umgestellt – die Uhr ging eine Stunde zurück. Eine Stunde länger geschlafen 😴"
                : "Heute Nacht wurde auf Sommerzeit umgestellt – die Uhr ging eine Stunde vor. Eine Stunde weniger Schlaf ☕";
        if (tomorrow != now)
            return tomorrow < now
                ? "Heute Nacht wird auf Winterzeit umgestellt: Die Uhr geht eine Stunde zurück."
                : "Heute Nacht wird auf Sommerzeit umgestellt: Die Uhr geht eine Stunde vor – eine Stunde weniger Schlaf.";
        return null;
    }

    /// <summary>Für den Morgenüberblick: „Heute vor einem Jahr hast du geschrieben: …“ (nur eigene Tagebücher). Null: nichts.</summary>
    internal static string? JournalLookBack(UserSpace space, DateOnly today)
    {
        var entries = JournalStore.Journals(space.Db).SelectMany(j => JournalStore.Entries(space.Db, j));
        var hit = JournalApi.OnThisDay(entries, today, space.Zone).FirstOrDefault(g => g.Label.Contains("Jahr"));
        if (hit.Entries == null) return null;
        var e = hit.Entries[0];
        var text = (e.Title is { Length: > 0 } t ? t + ": " : "") + e.Text.Replace('\n', ' ');
        if (text.Length > 160) text = text[..160].TrimEnd() + " …";
        return text.Length == 0 ? $"📔 Heute {hit.Label} gab es einen Tagebucheintrag – schau mal rein." : $"📔 Heute {hit.Label}: „{text}“";
    }
}
