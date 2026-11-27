using System.Globalization;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Pim;

/// <summary>
/// Eine Erinnerung: Minuten vor Beginn. Bei ganztägigen Terminen vor 9:00 des Tages (60 = um 8:00, 1440 = am Vortag um 9:00);
/// mit <see cref="At"/> („N Tage vorher um HH:MM“) vor 0:00 des Tages (360 = am Vortag um 18:00, negativ = am Tag selbst,
/// -480 = um 8:00). Bei Terminen mit Uhrzeit hat <see cref="At"/> keine Bedeutung. Mit <see cref="List"/> legt sie eine
/// Aufgabe in dieser Aufgabenliste an statt zu benachrichtigen (calremtask).
/// </summary>
public sealed class RemindRule
{
    public int Minutes { get; set; }
    public long? List { get; set; }
    public bool At { get; set; }
}

/// <summary>Regel nach Titel: enthält der Titel den Text, gelten diese Erinnerungen statt der Vorgabe.</summary>
public sealed class TitleRule
{
    public string Contains { get; set; } = "";
    public List<RemindRule> Reminders { get; set; } = new();
}

/// <summary>Standard-Erinnerung eines Kalenders (calremdefault): wirkt beim Auslösen, die Termine bleiben unverändert.</summary>
public sealed class CalendarRemindDefault
{
    public List<RemindRule> Reminders { get; set; } = new();
    public List<TitleRule> Rules { get; set; } = new();

    public List<RemindRule> For(string title) =>
        Rules.FirstOrDefault(r => r.Contains.Trim().Length > 0 && title.Contains(r.Contains.Trim(), StringComparison.CurrentCultureIgnoreCase))?.Reminders
        ?? Reminders;

    public bool Empty => Reminders.Count == 0 && Rules.Count == 0;
}

/// <summary>Eine fällige Erinnerung.</summary>
public sealed record DueReminder(string Title, DateTime StartUtc, bool AllDay, string Start, int Minutes, DateTime FireUtc, long Calendar, string Uri, long? List, bool At = false);

/// <summary>
/// Kalender-Erinnerungen ab 2.2 (calremind, calremdefault, calbulkremind, calremtask).
/// Vorrang beim Auslösen:
/// 1. Eigene Erinnerungen am Termin: VALARMs, dazu der Eintrag in „pim.evremind.&lt;Kalender&gt;“ (Aufgaben). Ausdrücklich keine:
///    X-DM-NOREMIND am Termin (früher: ein leerer Eintrag). Bei Abos (nur lesbar) steht dort die ganze Liste, sie ersetzt die
///    Alarme des Anbieters.
/// 2. Sonst der Standard des Kalenders: erste passende Titelregel, sonst die Vorgabe. Bei Abos ersetzt er die Alarme des Anbieters.
/// 3. Sonst die Alarme des Anbieters (Abo) bzw. keine.
/// Geteilte Kalender rechnen mit Standard und eigener Liste des Besitzers; Aufgaben legt nur der Besitzer an. Ist der Kalender
/// für den Benutzer nur lesbar, gehen dessen eigene Erinnerungen (persönlicher Speicher, calremshared) allem davon vor.
/// Ganztägig zählen relative Erinnerungen ab 9:00 des Tages. Im VALARM steht RFC-gerecht der Abstand zu 0:00, markiert mit
/// X-DM-REL (andere Programme lösen damit zur selben Zeit aus).
/// </summary>
public static class CalendarReminders
{
    /// <summary>Höchstens vier Wochen vorher (sonst müsste jede Runde zu weit vorausrechnen).</summary>
    public const int MaxMinutes = 28 * 1440;

    public static bool Active => Features.On(Features.CalRemind) || Features.On(Features.CalRemDefault) || Features.On(Features.CalBulkRemind) || Features.On(Features.CalRemTask);
    private static bool Overlays => Features.On(Features.CalRemind) || Features.On(Features.CalBulkRemind);

    // ---------------- TRIGGER ----------------

    private static readonly Regex Dur = new(@"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>TRIGGER-Dauer als Minuten vor Beginn (positiv = davor). Null: keine Dauer (absolute Zeit).</summary>
    public static int? ParseTrigger(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Dur.Match(s.Trim());
        if (!m.Success || s.Trim().Length < 3) return null;
        int N(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        long min = N(2) * 7L * 1440 + N(3) * 1440L + N(4) * 60L + N(5) + N(6) / 60;
        if (min > int.MaxValue / 2) return null;
        return m.Groups[1].Value == "-" ? (int)min : -(int)min;
    }

    /// <summary>Minuten vor Beginn als TRIGGER: Tage nominal (RFC 5545: Kalendertage), der Rest als Stunden und Minuten.</summary>
    public static string Trigger(int minutes)
    {
        if (minutes == 0) return "-PT0M";
        var sign = minutes > 0 ? "-" : "";
        var abs = Math.Abs(minutes);
        int d = abs / 1440, rest = abs % 1440, h = rest / 60, mm = rest % 60;
        var s = sign + "P" + (d > 0 ? d + "D" : "");
        if (rest > 0) s += "T" + (h > 0 ? h + "H" : "") + (mm > 0 ? mm + "M" : "");
        return s;
    }

    /// <summary>Ganztägig: relative Erinnerungen zählen ab dieser Uhrzeit des Tages (Minuten nach 0:00).</summary>
    public const int AllDayBase = 9 * 60;

    /// <summary>
    /// Auslösezeit: vom Beginn (ganztägig: 9:00 Ortszeit des Tages, mit <paramref name="at"/> 0:00) die ganzen Tage als
    /// Kalendertage zurück (gleiche Uhrzeit, auch über die Zeitumstellung), dann den Rest als genaue Dauer.
    /// </summary>
    public static DateTime Fire(DateTime startUtc, bool allDay, DateOnly day, int minutes, bool at, TimeZoneInfo zone)
    {
        var wall = allDay ? day.ToDateTime(at ? TimeOnly.MinValue : new TimeOnly(AllDayBase / 60, AllDayBase % 60))
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), zone);
        var sign = minutes >= 0 ? 1 : -1;
        var abs = Math.Abs(minutes);
        var utc = Ics.ToUtc(wall.AddDays(-sign * (abs / 1440)), zone);
        return DateTime.SpecifyKind(utc.AddMinutes(-sign * (abs % 1440)), DateTimeKind.Utc);
    }

    /// <summary>Beginn eines Termins in UTC; ganztägig 0:00 Ortszeit.</summary>
    public static DateTime StartUtc(EventDto e, TimeZoneInfo zone) => e.AllDay
        ? Ics.ToUtc(DateTime.Parse(e.Start, CultureInfo.InvariantCulture), zone)
        : CalendarModel.ParseIso(e.Start, out _).ToUniversalTime();

    // ---------------- VALARM ----------------

    private static bool Relative(Component a) =>
        ParseTrigger(a.Text("TRIGGER")) != null && (a.Get("TRIGGER")?.Param("VALUE") is not { } v || !v.Equals("DATE-TIME", StringComparison.OrdinalIgnoreCase));

    private static bool Rel(Component a) => string.Equals(a.Text("X-DM-REL"), "TRUE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Alle VALARMs mit relativer Zeit als Erinnerungen (für EventDto.Alarms). Ganztägig: mit X-DM-REL relativ zu 9:00,
    /// sonst „Tage vorher um“ (so legen andere Programme sie an).
    /// </summary>
    public static List<RemindRule> Alarms(Component e, bool allDay) => e.Of("VALARM").Where(Relative).Select(a =>
    {
        var m = ParseTrigger(a.Text("TRIGGER"))!.Value;
        if (!allDay) return new RemindRule { Minutes = m };
        return Rel(a) ? new RemindRule { Minutes = m + AllDayBase } : new RemindRule { Minutes = m, At = true };
    }).ToList();

    /// <summary>Ausdrücklich keine Erinnerung (auch kein Kalender-Standard).</summary>
    public static bool NoRemind(Component e) => string.Equals(e.Text("X-DM-NOREMIND"), "TRUE", StringComparison.OrdinalIgnoreCase);

    public static void SetNoRemind(Component e, bool on)
    {
        e.Remove("X-DM-NOREMIND");
        if (on) e.Set("X-DM-NOREMIND", "TRUE");
    }

    /// <summary>Erinnerung, die als VALARM geschrieben werden kann (Termine mit Uhrzeit nur vor Beginn).</summary>
    public static bool Writable(RemindRule r, bool allDay) => r.List == null && (r.Minutes >= 0 || (allDay && r.At));

    /// <summary>Alle relativen VALARMs durch diese Erinnerungen ersetzen; Alarme mit fester Zeit bleiben.</summary>
    public static void WriteAlarms(Component e, IEnumerable<RemindRule> rules, bool allDay, string title)
    {
        static (int Trigger, bool Rel) Key(RemindRule r, bool allDay) =>
            allDay && !r.At ? (r.Minutes - AllDayBase, true) : (r.Minutes, false);
        // Unveränderte behalten (samt Eigenschaften anderer Programme), neue anhängen
        var wanted = rules.Where(r => r.List == null).Select(r => Key(r, allDay)).Distinct().ToList();
        foreach (var a in e.Of("VALARM").Where(Relative).ToList())
        {
            if (wanted.Remove((ParseTrigger(a.Text("TRIGGER"))!.Value, allDay && Rel(a)))) continue;
            e.Children.Remove(a);
        }
        foreach (var (trigger, rel) in wanted)
        {
            var alarm = new Component("VALARM");
            alarm.Set("ACTION", "DISPLAY");
            alarm.Set("DESCRIPTION", Ics.Escape(title.Length > 0 ? title : "Erinnerung"));
            alarm.Set("TRIGGER", Trigger(trigger));
            if (rel) alarm.Set("X-DM-REL", "TRUE");
            e.Children.Add(alarm);
        }
    }

    public static List<RemindRule> Clean(IEnumerable<RemindRule>? rules) => (rules ?? [])
        .Where(r => r.Minutes is >= -1440 and <= MaxMinutes)
        .GroupBy(r => (r.Minutes, r.List, r.At)).Select(g => new RemindRule { Minutes = g.Key.Minutes, List = g.Key.List, At = g.Key.At }).Take(20).ToList();

    // ---------------- Speicher ----------------

    private static string DefaultKey(long coll) => "pim.calremind." + coll;
    private static string OverlayKey(long coll) => "pim.evremind." + coll;

    public static CalendarRemindDefault? DefaultOf(UserDb db, long coll) =>
        db.Get(DefaultKey(coll)) == null ? null : db.GetJson<CalendarRemindDefault>(DefaultKey(coll)) is { Empty: false } d ? d : null;

    public static void SetDefault(UserDb db, long coll, CalendarRemindDefault? value)
    {
        if (value == null) { db.Set(DefaultKey(coll), null); return; }
        var clean = new CalendarRemindDefault
        {
            Reminders = Clean(value.Reminders),
            Rules = (value.Rules ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Contains))
                .Select(r => new TitleRule { Contains = r.Contains.Trim(), Reminders = Clean(r.Reminders) }).Take(50).ToList(),
        };
        if (clean.Empty) db.Set(DefaultKey(coll), null); else db.SetJson(DefaultKey(coll), clean);
    }

    public static Dictionary<string, List<RemindRule>> OverlayOf(UserDb db, long coll) => db.GetJson<Dictionary<string, List<RemindRule>>>(OverlayKey(coll));

    /// <summary>Eigene Erinnerungen am Termin (Uri) merken; null entfernt den Eintrag.</summary>
    public static void SetOverlay(UserDb db, long coll, string uri, List<RemindRule>? rules)
    {
        lock (db)
        {
            var all = OverlayOf(db, coll);
            if (rules == null) { if (!all.Remove(uri)) return; }
            else all[uri] = Clean(rules);
            if (all.Count == 0) db.Set(OverlayKey(coll), null); else db.SetJson(OverlayKey(coll), all);
        }
    }

    // ---------------- Persönlich in geteilten Kalendern (calremshared) ----------------

    /// <summary>Eigene Erinnerungen in einem geteilten, für mich nur lesbaren Kalender: in meiner Datenbank, je Besitzer und Sammlung.</summary>
    private static string MineKey(string owner, long coll) => "pim.myremind." + owner + "." + coll;

    public static Dictionary<string, List<RemindRule>> MineOf(UserDb db, string owner, long coll) => db.GetJson<Dictionary<string, List<RemindRule>>>(MineKey(owner, coll));

    /// <summary>Eigene Erinnerungen an einem geteilten Termin merken (nur Benachrichtigungen); null entfernt den Eintrag.</summary>
    public static void SetMine(UserDb db, string owner, long coll, string uri, List<RemindRule>? rules)
    {
        lock (db)
        {
            var all = MineOf(db, owner, coll);
            if (rules == null) { if (!all.Remove(uri)) return; }
            else all[uri] = Clean(rules).Where(r => r.List == null).ToList();
            if (all.Count == 0) db.Set(MineKey(owner, coll), null); else db.SetJson(MineKey(owner, coll), all);
        }
    }

    /// <summary>
    /// Geteilt und für diesen Benutzer nicht beschreibbar (nur lesen oder ein geteiltes Abo): eigene Erinnerungen kommen in
    /// den persönlichen Speicher statt in den Termin.
    /// </summary>
    public static bool Personal(Share share, string user, UserDb ownerDb, long coll) =>
        share.ReadOnly.Contains(user) || Subscriptions.IsSubscribed(ownerDb, coll);

    /// <summary>Persönliche Erinnerungen dieses Benutzers in einem geteilten Kalender; null, wenn keine gelten können.</summary>
    public static Dictionary<string, List<RemindRule>>? MineFor(UserSpace me, Share? share, UserSpace owner, long coll) =>
        share != null && Features.On(Features.CalRemShared) && Personal(share, me.User.Id, owner.Db, coll) ? MineOf(me.Db, owner.User.Id, coll) : null;

    /// <summary>Was an einem Termin jetzt gilt (Besitzer-Sicht), für „hinzufügen“ (Übernahme aus dem Android-Kalender).</summary>
    public static List<RemindRule> EffectiveOf(UserDb ownerDb, long coll, EventDto e)
    {
        var (subscribed, overlay, def) = Settings(ownerDb, coll);
        return Rules(e, subscribed, overlay, def).Effective;
    }

    public static void Remove(UserDb db, long coll)
    {
        db.Set(DefaultKey(coll), null);
        db.Set(OverlayKey(coll), null);
    }

    // ---------------- Wirksame Erinnerungen ----------------

    /// <summary>Was ein Termin hat: eigene Liste (null = Standard des Kalenders gilt) und ob ein Standard da ist.</summary>
    public static (List<RemindRule>? Own, List<RemindRule> Effective) Rules(EventDto e, bool subscribed, Dictionary<string, List<RemindRule>>? overlay, CalendarRemindDefault? def)
    {
        var alarms = e.Alarms ?? [];
        List<RemindRule>? own;
        if (overlay != null && overlay.TryGetValue(e.Uri, out var o)) own = subscribed ? o : alarms.Concat(o.Where(r => r.List != null)).ToList();
        else if (alarms.Count > 0 && !(subscribed && def != null)) own = alarms;
        else if (e.NoRemind && !subscribed) own = [];
        else own = null;
        var effective = own ?? def?.For(e.Title) ?? [];
        if (!Features.On(Features.CalRemTask)) effective = effective.Where(r => r.List == null).ToList();
        return (own, effective);
    }

    /// <summary>
    /// Für die Oberfläche: eigene Erinnerungen; null = der Kalender-Standard gilt. <paramref name="db"/> ist die Datenbank des
    /// Besitzers (bei geteilten Kalendern zählen Standard und eigene Liste des Besitzers).
    /// </summary>
    public static List<EventDto> Decorate(UserDb db, List<EventDto> events, long coll, Dictionary<string, List<RemindRule>>? mine = null)
    {
        if (!Active) return events;
        var (subscribed, overlay, def) = Settings(db, coll);
        return events.Select(e =>
        {
            var (o, _) = Rules(e, subscribed, overlay, def);
            return e with { Reminders = o ?? (def == null ? [] : null), MyReminders = mine?.GetValueOrDefault(e.Uri) };
        }).ToList();
    }

    private static (bool Subscribed, Dictionary<string, List<RemindRule>>? Overlay, CalendarRemindDefault? Def) Settings(UserDb db, long coll) => (
        Subscriptions.IsSubscribed(db, coll),
        Overlays ? OverlayOf(db, coll) : null,
        Features.On(Features.CalRemDefault) ? DefaultOf(db, coll) : null);

    /// <summary>
    /// Erinnerungen und Aufgaben, die in (from, to] fällig werden. Erinnerungen aus eigenen und geteilten Kalendern (mit
    /// Standard und eigener Liste des Besitzers), Aufgaben nur aus eigenen.
    /// </summary>
    public static List<DueReminder> Due(IServiceProvider services, UserSpace space, DateTime from, DateTime to, bool tasks = false)
    {
        var spaces = services.GetRequiredService<Spaces>();
        var cals = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT")).Select(c => (Owner: space, Coll: c, Id: c.Id))
            .Concat(tasks ? [] : SharedLists.For(services.GetRequiredService<ShareStore>(), spaces, space.User.Id)
                .Where(x => x.Coll.Components.Contains("VEVENT")).Select(x => (Owner: x.Owner, Coll: x.Coll, Id: -(long)x.Share.Id)));
        var result = new List<DueReminder>();
        var shares = tasks ? new Dictionary<long, Share>() : SharedLists.For(services.GetRequiredService<ShareStore>(), spaces, space.User.Id).ToDictionary(x => -(long)x.Share.Id, x => x.Share);
        foreach (var (owner, coll, id) in cals)
        {
            // Geteilt und nur lesbar: eigene Erinnerungen dieses Benutzers haben Vorrang (calremshared)
            var mine = owner == space ? null : MineFor(space, shares.GetValueOrDefault(id), owner, coll.Id);
            result.AddRange(DueIn(owner, coll.Id, space.Zone, from, to, tasks, mine).Select(d => d with { Calendar = id }));
        }
        return result.OrderBy(d => d.FireUtc).ToList();
    }

    internal static IEnumerable<DueReminder> DueIn(UserSpace owner, long coll, TimeZoneInfo zone, DateTime from, DateTime to, bool tasks,
        Dictionary<string, List<RemindRule>>? mine = null)
    {
        var (subscribed, overlay, def) = Settings(owner.Db, coll);
        foreach (var e in CalendarModel.Events(owner, from.AddDays(-2), to.AddDays(29), [coll]))
        {
            if (string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var rules = mine != null && mine.TryGetValue(e.Uri, out var own) ? own : Rules(e, subscribed, overlay, def).Effective;
            if (rules.Count == 0) continue;
            var start = StartUtc(e, zone);
            var day = DateOnly.ParseExact(e.Start[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var r in rules)
            {
                if ((r.List != null) != tasks) continue;
                if (r.Minutes > MaxMinutes) continue;
                // Termine mit Uhrzeit: nur vor Beginn („am Tag um 8:00“ aus dem Kalender-Standard entfällt)
                if (!e.AllDay && r.Minutes < 0) continue;
                var fire = Fire(start, e.AllDay, day, r.Minutes, r.At, zone);
                if (fire > from && fire <= to) yield return new DueReminder(e.Title, start, e.AllDay, e.Start, r.Minutes, fire, coll, e.Uri, r.List, e.AllDay && r.At);
            }
        }
    }
}

/// <summary>
/// Aufgaben aus Erinnerungen (calremtask): alle 5 Minuten für die eigenen Kalender. Je Termin, Tag und Regel höchstens
/// einmal (gemerkt in „pim.remtasks“), nicht für vergangene Termine. Fällt der Termin weg oder wird er verschoben, löscht
/// der nächste Lauf die Aufgabe, solange sie nicht erledigt ist.
/// </summary>
public sealed class CalendarRemindTasks : BackgroundService
{
    private readonly Spaces _spaces;
    private readonly ILogger<CalendarRemindTasks> _log;
    public CalendarRemindTasks(Spaces spaces, ILogger<CalendarRemindTasks> log) { _spaces = spaces; _log = log; }

    public sealed class Made
    {
        public long List { get; set; }
        public string Uri { get; set; } = "";
        public DateTime EventStart { get; set; }
    }

    private const string Key = "pim.remtasks";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stop);
        while (!stop.IsCancellationRequested)
        {
            if (Features.On(Features.CalRemTask))
                foreach (var space in _spaces.Working())
                {
                    try { Run(space, DateTime.UtcNow); }
                    catch (Exception ex) { _log.LogInformation("Aufgaben aus Erinnerungen: {Error}", ex.Message); }
                }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stop); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Ein Lauf für einen Benutzer; gibt die Zahl neu angelegter Aufgaben zurück.</summary>
    public static int Run(UserSpace space, DateTime utcNow)
    {
        var db = space.Db;
        var zone = space.Zone;
        var made = db.GetJson<Dictionary<string, Made>>(Key);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone));
        // Fällig ist, was bis Ende heute (Ortszeit) ausgelöst würde, solange der Termin noch kommt
        var endOfToday = Ics.ToUtc(today.AddDays(1).ToDateTime(TimeOnly.MinValue), zone).AddSeconds(-1);
        var lists = PimStore.Collections(db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO")).Select(c => c.Id).ToHashSet();
        var current = new HashSet<string>();
        int created = 0;
        foreach (var coll in PimStore.Collections(db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT")))
            foreach (var d in CalendarReminders.DueIn(space, coll.Id, zone, utcNow.AddDays(-29), endOfToday, tasks: true))
            {
                if (d.StartUtc <= utcNow || d.List is not long list || !lists.Contains(list)) continue;
                var key = $"{coll.Id}|{d.Uri}|{d.Start}|{d.Minutes}|{list}" + (d.At ? "|at" : "");
                current.Add(key);
                if (made.ContainsKey(key)) continue;
                var due = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(d.FireUtc, zone));
                if (due < today) due = today;
                var when = d.AllDay
                    ? DateOnly.ParseExact(d.Start[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                    : TimeZoneInfo.ConvertTimeFromUtc(d.StartUtc, zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
                var id = CalendarModel.SaveTask(space, new TaskInput
                {
                    List = list, Title = d.Title, Description = "Termin: " + when,
                    Due = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DueAllDay = true,
                });
                made[key] = new Made { List = list, Uri = id[(id.IndexOf('/') + 1)..], EventStart = d.StartUtc };
                created++;
            }
        // Weggefallen oder verschoben: offene Aufgabe löschen, erledigte bleibt. Alte Einträge vergessen.
        var changed = created > 0;
        foreach (var (key, m) in made.ToList())
        {
            if (m.EventStart < utcNow.AddDays(-30)) { made.Remove(key); changed = true; continue; }
            if (current.Contains(key) || m.EventStart <= utcNow) continue;
            var coll = PimStore.Find(db, m.List);
            if (coll != null && CalendarModel.Tasks(space, [m.List]).FirstOrDefault(t => t.Uri == m.Uri) is { Completed: false })
                PimStore.Delete(db, coll, m.Uri);
            made.Remove(key);
            changed = true;
        }
        if (changed) db.SetJson(Key, made);
        return created;
    }
}
