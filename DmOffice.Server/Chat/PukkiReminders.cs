using System.Globalization;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Chat;

/// <summary>
/// Erinnerungen als Aufgaben: „Erinnere mich in 2 Stunden an den Kuchen“ legt eine Aufgabe mit Fälligkeit samt Uhrzeit und der
/// Kategorie „Erinnerung“ an; zur Fälligkeit meldet sich Pukki im Chat (Knöpfe zum erneuten Erinnern). Dazu „Welche Erinnerungen
/// habe ich?“, „Erinnerung an den Müll löschen“, „Erinnerung Müll auf 10 Uhr verschieben“. Erledigt wird sie wie jede Aufgabe.
/// Nur Aufgaben mit dieser Kategorie melden sich - andere Aufgaben mit Uhrzeit bleiben still wie bisher.
/// </summary>
public static partial class Pukki
{
    internal const string ReminderCategory = "Erinnerung";

    internal sealed record ReminderTask(UserSpace Owner, Collection List, TaskDto Task, DateTime AtUtc);

    /// <summary>Offene Erinnerungen in den eigenen Aufgabenlisten, nach Zeit.</summary>
    internal static List<ReminderTask> ReminderTasks(UserSpace space)
    {
        var result = new List<ReminderTask>();
        foreach (var coll in PimStore.Collections(space.Db, PimStore.Calendar)
                     .Where(c => c.Components.Contains("VTODO") && Lists.TypeOf(space.Db, c.Id) == Lists.Tasks && !Subscriptions.IsSubscribed(space.Db, c.Id)))
            foreach (var t in CalendarModel.Tasks(space, [coll.Id]))
            {
                if (t.Completed || t.DueAllDay || t.Due == null || !t.Categories.Any(x => x.Equals(ReminderCategory, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new ReminderTask(space, coll, t, CalendarModel.ParseIso(t.Due, out _).ToUniversalTime()));
            }
        return result.OrderBy(r => r.AtUtc).ToList();
    }

    /// <summary>Erinnerungen, deren Zeit in (from, to] fällt.</summary>
    internal static List<ReminderTask> DueReminderTasks(UserSpace space, DateTime fromUtc, DateTime toUtc) =>
        ReminderTasks(space).Where(r => r.AtUtc > fromUtc && r.AtUtc <= toUtc).ToList();

    private static Collection? ReminderList(UserSpace space)
    {
        PimStore.EnsureDefaults(space.Db);
        return PimStore.Collections(space.Db, PimStore.Calendar)
            .FirstOrDefault(c => c.Components.Contains("VTODO") && Lists.TypeOf(space.Db, c.Id) == Lists.Tasks && !Subscriptions.IsSubscribed(space.Db, c.Id));
    }

    private static string WhenText(UserSpace space, DateTime atUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(atUtc, space.Zone);
        var day = PukkiParse.DayText(DateOnly.FromDateTime(local), DateOnly.FromDateTime(Now(space)));
        return (day == "heute" ? "heute" : day) + " um " + local.ToString("HH:mm", CultureInfo.InvariantCulture) + " Uhr";
    }

    /// <summary>Neue Erinnerung aus „morgen um 9 an den Müll“, „in 20 Minuten Kuchen“. Null: keine Uhrzeit im Satz.</summary>
    private static Reply? RemindTask(Ctx c, string text)
    {
        var w = PukkiParse.Parse(text, c.Now);
        if (w.Start is not TimeOnly time) return null;
        var title = CleanTitle(w.Title);
        if (title.Length == 0) return "Woran soll ich dich erinnern? Zum Beispiel: Erinnere mich morgen um 9 an den Müll.";
        var at = TimeZoneInfo.ConvertTimeToUtc((w.Date ?? c.Today).ToDateTime(time), c.Space.Zone);
        if (at <= DateTime.UtcNow) return "Die Zeit ist schon vorbei – sag mir eine spätere, zum Beispiel: in 20 Minuten.";
        var list = ReminderList(c.Space) ?? throw new InvalidOperationException("Ich finde keine Aufgabenliste.");
        var id = CalendarModel.SaveTask(c.Space, new TaskInput { List = list.Id, Title = Cap(title), Due = Iso(at), DueAllDay = false, Categories = [ReminderCategory] });
        var made = new List<Created> { new("task", c.Space.User.Id, list.Id, id[(id.IndexOf('/') + 1)..], 0, $"Erinnerung „{Cap(title)}“") };
        var local = TimeZoneInfo.ConvertTimeFromUtc(at, c.Space.Zone);
        var en = $"{DayWords(DateOnly.FromDateTime(local), c.Today)} at {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        return Made(SpeaksEnglish ? $"Okay, I'll remind you {en} about “{Cap(title)}”. It's a task in “{list.Name}”."
            : $"Okay, ich erinnere dich {WhenText(c.Space, at)} an „{Cap(title)}“. Sie steht als Aufgabe in „{list.Name}“.", made, "task", text, 0);
    }

    private static readonly Regex ReminderWordRx = new(@"\berinnerung(?:en)?\b", RegexOptions.IgnoreCase);
    private static readonly Regex ReminderCancelRx = new(@"\b(?:abbrechen|stopp?e?n?|stop|löschen|loeschen|lösche|loesche|entfernen|entferne|weg|streichen|streiche|aufheben|absagen|cancel)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ReminderMoveRx = new(@"\b(?:verschieb\w*|verleg\w*|änder\w*|aender\w*|später|spaeter|früher|frueher|umstellen|stell\w*)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ReminderFillRx = new(@"\b(?:erinnerung|erinnerungen|die|der|den|das|dem|an|ans|am|für|fuer|zum|zur|meine|meinen|mein|bitte|mal|auf|um|von|nach|welche|welcher|habe|hab|ich|gibt|es|zeig\w*|liste|offene?n?|laufende?n?|alle|noch|kannst|du|mir)\b", RegexOptions.IgnoreCase);

    /// <summary>Erinnerungen zeigen, löschen, verschieben. Null: kein Satz über Erinnerungen (das Anlegen läuft über „erinnere mich“).</summary>
    private static Reply? ReminderReply(Ctx c, string t)
    {
        if (!ReminderWordRx.IsMatch(t) || RemindRx.IsMatch(t)) return null;
        var all = ReminderTasks(c.Space).Where(r => r.AtUtc > DateTime.UtcNow).ToList();
        var cancel = ReminderCancelRx.IsMatch(t);
        var move = !cancel && ReminderMoveRx.IsMatch(t);
        var rest = ReminderCancelRx.Replace(t, " ");
        rest = ReminderMoveRx.Replace(rest, " ");

        if (cancel || move)
        {
            if (all.Count == 0) return "Du hast gerade keine Erinnerung.";
            // Neue Zeit („auf 10 Uhr“, „auf morgen 9 Uhr“, „um 15 Minuten“) vom Namen trennen
            var shift = Regex.Match(t, @"\bum\s+(?<n>\d+|eine?n?|einer|zwei|drei|fünf|fuenf|zehn|fünfzehn|fuenfzehn|zwanzig|dreißig|dreissig|halbe?n?)\s*(?:halben\s+)?(?<u>minuten?|min|stunden?|std|tage?n?)\b", RegexOptions.IgnoreCase);
            var name = Regex.Replace(rest, @"\b(?:um|auf|in)\s+.*$", "", RegexOptions.IgnoreCase);
            name = ReminderFillRx.Replace(name, " ");
            name = Regex.Replace(Regex.Replace(name, @"[^\p{L}\p{N}\s\-]", " "), @"\s+", " ").Trim();
            var quoted = Regex.Match(t, @"[„""“](?<n>[^„""“”]+)[“”""]");
            if (quoted.Success) name = quoted.Groups["n"].Value.Trim();
            var hit = name.Length == 0 ? all : all.Where(r => r.Task.Title.Contains(name, StringComparison.CurrentCultureIgnoreCase)
                || name.Split(' ').Any(w => w.Length > 2 && r.Task.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList();
            if (hit.Count == 0) return $"Eine Erinnerung „{name}“ finde ich nicht. " + RemindersText(c.Space, all);
            if (hit.Count > 1 && !(cancel && Regex.IsMatch(t, @"\balle\b", RegexOptions.IgnoreCase)))
            {
                var shown = hit.Take(4).ToList();
                var verb = cancel ? "löschen" : "verschieben";
                var tail = cancel ? "löschen" : Regex.Match(t, @"\b(?:um|auf|in)\s+.*$", RegexOptions.IgnoreCase).Value.Trim().TrimEnd('?', '!', '.') + " verschieben";
                return new Reply($"Welche Erinnerung soll ich {verb}?\n" + string.Join("\n", shown.Select(r => $"• {r.Task.Title} – {WhenText(c.Space, r.AtUtc)}")),
                    Actions: shown.Select((r, i) => Say(i, r.Task.Title, $"Erinnerung „{r.Task.Title}“ {tail}")).ToList());
            }
            if (cancel)
            {
                foreach (var r in hit) PimStore.Delete(r.Owner.Db, r.List, r.Task.Uri);
                return hit.Count == 1 ? $"Die Erinnerung „{hit[0].Task.Title}“ ist gelöscht." : $"Alle {hit.Count} Erinnerungen sind gelöscht.";
            }
            var one = hit[0];
            DateTime at;
            if (shift.Success)
            {
                var minutes = ReminderMinutes(shift.Value[2..].Trim()) ?? 0;
                var earlier = Regex.IsMatch(t, @"\b(?:früher|frueher|vor)\b", RegexOptions.IgnoreCase);
                at = one.AtUtc.AddMinutes(earlier ? -minutes : minutes);
            }
            else
            {
                var w = PukkiParse.Parse(Regex.Match(t, @"\b(?:auf|in|nach)\s+.*$", RegexOptions.IgnoreCase).Value is { Length: > 0 } when ? Regex.Replace(when, @"^(?:auf|nach)\s+", "", RegexOptions.IgnoreCase) : "", c.Now);
                if (w.Start is not TimeOnly time) return $"Auf wann? Zum Beispiel: Erinnerung „{one.Task.Title}“ auf 10 Uhr verschieben.";
                var local = TimeZoneInfo.ConvertTimeFromUtc(one.AtUtc, c.Space.Zone);
                // Ohne Tag: derselbe Tag wie bisher
                at = TimeZoneInfo.ConvertTimeToUtc((w.Date ?? DateOnly.FromDateTime(local)).ToDateTime(time), c.Space.Zone);
            }
            if (at <= DateTime.UtcNow) return "Die Zeit ist schon vorbei – sag mir eine spätere.";
            var task = one.Task;
            CalendarModel.SaveTask(one.Owner, new TaskInput
            {
                List = one.List.Id, Uri = task.Uri, Title = task.Title, Description = task.Description, Due = Iso(at), DueAllDay = false,
                Priority = task.Priority, Percent = task.Percent, Categories = task.Categories,
            });
            return $"Ich erinnere dich jetzt {WhenText(c.Space, at)} an „{task.Title}“.";
        }

        return RemindersText(c.Space, all);
    }

    private static string RemindersText(UserSpace space, List<ReminderTask> list) => list.Count switch
    {
        0 => "Du hast gerade keine Erinnerung. Zum Anlegen: Erinnere mich morgen um 9 an den Müll.",
        1 => $"Deine Erinnerung: „{list[0].Task.Title}“ – {WhenText(space, list[0].AtUtc)}.",
        _ => "Deine Erinnerungen:\n" + string.Join("\n", list.Take(15).Select(r => $"• {r.Task.Title} – {WhenText(space, r.AtUtc)}")),
    };
}
