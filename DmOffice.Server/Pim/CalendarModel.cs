using System.Globalization;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Pim;

public sealed record EventDto(string Id, long Calendar, string Uri, string Etag, string? Uid, string Title, string? Location,
    string? Description, string Start, string End, bool AllDay, bool Recurring, string? RRule, string? RecurrenceId,
    string? Status, int? Reminder, bool Busy, List<string> Categories, string? Url, bool CanWrite = true,
    List<AttendeeDto>? Attendees = null, string? Organizer = null,
    /// <summary>Essensplan: sync_id der Rezeptnotiz (X-DM-RECIPE)</summary>
    string? Recipe = null);

/// <summary>Teilnehmer eines Termins mit seiner Antwort (PARTSTAT): NEEDS-ACTION, ACCEPTED, TENTATIVE, DECLINED.</summary>
public sealed record AttendeeDto(string Email, string? Name, string Status);

public sealed class AttendeeInput
{
    public string Email { get; set; } = "";
    public string? Name { get; set; }
}

public sealed class EventInput
{
    public long Calendar { get; set; }
    /// <summary>Leer: neuer Termin.</summary>
    public string? Uri { get; set; }
    public string Title { get; set; } = "";
    public string? Location { get; set; }
    public string? Description { get; set; }
    /// <summary>ISO, UTC - bei ganztaegig nur das Datum (yyyy-MM-dd).</summary>
    public string Start { get; set; } = "";
    /// <summary>Bei ganztaegig: der Tag nach dem letzten Tag (wie in iCalendar).</summary>
    public string End { get; set; } = "";
    public bool AllDay { get; set; }
    /// <summary>RRULE ohne "RRULE:", z. B. FREQ=WEEKLY;BYDAY=MO. Null: bleibt; "": keine Serie.</summary>
    public string? RRule { get; set; }
    public int? Reminder { get; set; }
    public bool Busy { get; set; } = true;
    public List<string>? Categories { get; set; }
    /// <summary>Essensplan: sync_id der Rezeptnotiz. Null: bleibt; "": kein Rezept.</summary>
    public string? Recipe { get; set; }
    /// <summary>series | instance - bei Serien: alle oder nur dieses Vorkommen.</summary>
    public string Scope { get; set; } = "series";
    /// <summary>Beginn des Vorkommens, das bearbeitet wurde (RECURRENCE-ID), ISO UTC.</summary>
    public string? Instance { get; set; }
    /// <summary>Anderer Kalender: verschieben.</summary>
    public long? MoveTo { get; set; }
    /// <summary>ETag des geladenen Stands (aus dem GET). Gesetzt: nur speichern, wenn er noch gilt; sonst 409.</summary>
    public string? Etag { get; set; }
    /// <summary>Teilnehmer; null: unveraendert lassen, leer: keine.</summary>
    public List<AttendeeInput>? Attendees { get; set; }
    /// <summary>Mailkonto, das als Organisator einlaedt (null: das erste).</summary>
    public string? Account { get; set; }
    /// <summary>Einladungen bzw. Aenderungen an die Teilnehmer schicken.</summary>
    public bool Notify { get; set; } = true;
}

public sealed record TaskDto(string Id, long List, string Uri, string Etag, string? Uid, string Title, string? Description,
    string? Due, bool DueAllDay, string? Start, bool Completed, string? CompletedAt, int Priority, int Percent,
    string? Status, List<string> Categories, string? Parent, string Created,
    // Einkaufs- und Checklisten (Lists.cs): Artikel ohne Menge, Menge, Einheit, Abteilung
    string? Item = null, string? Qty = null, string? Unit = null, string? Section = null);

public sealed class TaskInput
{
    public long List { get; set; }
    public string? Uri { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Due { get; set; }
    public bool DueAllDay { get; set; } = true;
    public int Priority { get; set; }
    public bool Completed { get; set; }
    public int? Percent { get; set; }
    public List<string>? Categories { get; set; }
    public long? MoveTo { get; set; }
    /// <summary>ETag des geladenen Stands (aus dem GET). Gesetzt: nur speichern, wenn er noch gilt; sonst 409.</summary>
    public string? Etag { get; set; }
    /// <summary>Einkaufs-/Checkliste: Artikel, Menge, Einheit, Abteilung. Null: unveraendert lassen.</summary>
    public string? Item { get; set; }
    public string? Qty { get; set; }
    public string? Unit { get; set; }
    public string? Section { get; set; }
}

/// <summary>Termine und Aufgaben fuer die Weboberflaeche: lesen (Serien aufgeloest) und schreiben.</summary>
public static class CalendarModel
{
    private const string Prodid = "-//dmOffice//DE";

    // ---------------- Termine lesen ----------------

    public static List<EventDto> Events(UserSpace space, DateTime from, DateTime to, IEnumerable<long> calendars)
    {
        var zone = space.Zone;
        var result = new List<EventDto>();
        foreach (var obj in PimStore.InRange(space.Db, calendars, "VEVENT", from, to))
        {
            Component cal;
            try { cal = Ics.Parse(obj.Data).First(r => r.Name == "VCALENDAR"); }
            catch { continue; }
            var events = cal.Of("VEVENT").ToList();
            var master = events.FirstOrDefault(e => e.Get("RECURRENCE-ID") == null) ?? events.FirstOrDefault();
            if (master == null) continue;

            if (master.Get("RRULE") == null && master.Get("RDATE") == null)
            {
                foreach (var e in events)
                    if (Dto(obj, e, zone, null, null, false) is { } d && Overlaps(d, from, to)) result.Add(d);
                continue;
            }

            // Serie: Vorkommen mit Ical.Net ausrechnen, Felder aus dem eigenen Leser
            try
            {
                var ical = Ical.Net.Calendar.Load(obj.Data);
                foreach (var occ in ical.GetOccurrences(from.AddDays(-1), to.AddDays(1)))
                {
                    if (occ.Source is not Ical.Net.CalendarComponents.CalendarEvent src) continue;
                    var start = occ.Period.StartTime;
                    var end = occ.Period.EndTime ?? start.Add(src.Duration);
                    var sourceComp = src.RecurrenceId != null
                        ? events.FirstOrDefault(e => Ics.Time(e.Get("RECURRENCE-ID"), zone, out _) is DateTime rid && Math.Abs((rid - src.RecurrenceId.AsUtc).TotalSeconds) < 1) ?? master
                        : master;
                    bool allDay = !start.HasTime;
                    DateTime s = allDay ? DateTime.SpecifyKind(start.Value.Date, DateTimeKind.Utc) : start.AsUtc;
                    DateTime en = allDay ? DateTime.SpecifyKind(end.Value.Date, DateTimeKind.Utc) : end.AsUtc;
                    // RECURRENCE-ID: das Vorkommen, wie es nach der Regel laege
                    var rid2 = src.RecurrenceId != null ? src.RecurrenceId.AsUtc : s;
                    if (Dto(obj, sourceComp, zone, (s, en, allDay), rid2, true) is { } d && Overlaps(d, from, to)) result.Add(d);
                }
            }
            catch
            {
                if (Dto(obj, master, zone, null, null, true) is { } d) result.Add(d);
            }
        }
        return result.OrderBy(e => e.Start).ToList();
    }

    /// <summary>Ganztaegiges als Mitternacht UTC - einen Tag Spielraum, die Oberflaeche schneidet selbst.</summary>
    private static bool Overlaps(EventDto e, DateTime from, DateTime to) =>
        ParseIso(e.Start, out _) < (e.AllDay ? to.AddDays(1) : to) && ParseIso(e.End, out _) > (e.AllDay ? from.AddDays(-1) : from);

    private static string Iso(DateTime t, bool dateOnly) =>
        dateOnly ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static EventDto? Dto(DavObject obj, Component e, TimeZoneInfo zone, (DateTime S, DateTime E, bool AllDay)? occ, DateTime? rid, bool recurring)
    {
        DateTime start, end;
        bool allDay;
        if (occ is { } o) (start, end, allDay) = o;
        else
        {
            if (Ics.Time(e.Get("DTSTART"), zone, out allDay) is not DateTime s) return null;
            start = s;
            var en = Ics.Time(e.Get("DTEND"), zone, out _);
            if (en == null && e.Text("DURATION") is { } dur && PimStore.TryDuration(dur, out var d)) en = start + d;
            end = en ?? (allDay ? start.AddDays(1) : start.AddHours(1));
        }

        int? reminder = ReminderAlarm(e, out var before) != null ? (int)-before.TotalMinutes : null;

        var ridText = rid is DateTime r ? Iso(r, allDay) : null;
        return new EventDto(
            Id: obj.Collection + "/" + obj.Uri + (ridText != null ? "/" + ridText : ""),
            Calendar: obj.Collection, Uri: obj.Uri, Etag: obj.Etag, Uid: e.Text("UID"),
            Title: e.Text("SUMMARY") ?? "", Location: e.Text("LOCATION"), Description: e.Text("DESCRIPTION"),
            Start: Iso(start, allDay), End: Iso(end, allDay), AllDay: allDay, Recurring: recurring,
            RRule: e.Get("RRULE")?.Value, RecurrenceId: ridText, Status: e.Text("STATUS"), Reminder: reminder,
            Busy: !string.Equals(e.Text("TRANSP"), "TRANSPARENT", StringComparison.OrdinalIgnoreCase),
            Categories: e.All("CATEGORIES").SelectMany(p => p.Value.Split(',')).Select(Ics.Unescape).Where(x => x.Length > 0).ToList(),
            Url: e.Text("URL"),
            Attendees: e.All("ATTENDEE").Select(a => new AttendeeDto(Mail.Invites.Mailto(a) ?? "", a.Param("CN"), (a.Param("PARTSTAT") ?? "NEEDS-ACTION").ToUpperInvariant()))
                .Where(a => a.Email.Length > 0).ToList() is { Count: > 0 } att ? att : null,
            Organizer: Mail.Invites.Mailto(e.Get("ORGANIZER")), Recipe: e.Text("X-DM-RECIPE"));
    }

    /// <summary>
    /// Teilnehmer setzen: bekannte behalten ihre Antwort, neue bekommen NEEDS-ACTION und RSVP. Mit Teilnehmern steht der
    /// Organisator (eigenes Mailkonto) im Termin; ohne Teilnehmer faellt ein eigener Organisator wieder weg.
    /// </summary>
    public static void ApplyAttendees(Component e, List<AttendeeInput> list, MailAccount? organizer, ICollection<string> mine)
    {
        var known = e.All("ATTENDEE").Where(a => Mail.Invites.Mailto(a) != null)
            .GroupBy(a => Mail.Invites.Mailto(a)!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        e.Remove("ATTENDEE");
        var seen = new HashSet<string>();
        foreach (var a in list)
        {
            var email = (a.Email ?? "").Trim();
            if (email.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) email = email[7..];
            if (!MimeKit.MailboxAddress.TryParse(email, out _) || !email.Contains('@') || !seen.Add(email.ToLowerInvariant())) continue;
            var p = new Prop("ATTENDEE", "mailto:" + email);
            var name = string.IsNullOrWhiteSpace(a.Name) ? known.GetValueOrDefault(email.ToLowerInvariant())?.Param("CN") : a.Name.Trim();
            if (!string.IsNullOrEmpty(name)) p.With("CN", name);
            p.With("ROLE", "REQ-PARTICIPANT");
            var status = known.GetValueOrDefault(email.ToLowerInvariant())?.Param("PARTSTAT") ?? "NEEDS-ACTION";
            p.With("PARTSTAT", status);
            if (status.Equals("NEEDS-ACTION", StringComparison.OrdinalIgnoreCase)) p.With("RSVP", "TRUE");
            e.Props.Add(p);
        }
        var org = Mail.Invites.Mailto(e.Get("ORGANIZER"));
        if (seen.Count == 0)
        {
            if (org == null || mine.Contains(org.ToLowerInvariant())) e.Remove("ORGANIZER");
            return;
        }
        if (org == null && organizer != null)
        {
            var o = new Prop("ORGANIZER", "mailto:" + organizer.Email);
            if (!string.IsNullOrEmpty(organizer.DisplayName)) o.With("CN", organizer.DisplayName);
            e.Props.Add(o);
        }
    }

    // ---------------- Termine schreiben ----------------

    private static void Writable(UserDb db, long collection)
    {
        if (Subscriptions.IsSubscribed(db, collection)) throw new InvalidOperationException("Abonnierter Kalender – nur lesbar.");
    }

    public static string SaveEvent(UserSpace space, EventInput input)
    {
        Writable(space.Db, input.Calendar);
        if (input.MoveTo is long mt) Writable(space.Db, mt);
        var db = space.Db;
        var zone = space.Zone;
        var coll = PimStore.Find(db, input.Calendar) ?? throw new KeyNotFoundException("Kalender nicht gefunden.");
        Component vcal;
        Component target;
        string uri;
        string? etag = null;

        if (string.IsNullOrEmpty(input.Uri))
        {
            uri = PimStore.NewUri(".ics");
            vcal = NewCalendar();
            target = new Component("VEVENT");
            target.Set("UID", Guid.NewGuid().ToString("D"));
            target.Set("CREATED", Ics.Stamp());
            target.Set("SEQUENCE", "0");
            vcal.Children.Add(target);
            Fill(target, input, zone);
        }
        else
        {
            uri = input.Uri;
            var obj = PimStore.Object(db, coll.Id, uri) ?? throw new KeyNotFoundException("Termin nicht gefunden.");
            // Stand aus der Oberflaeche pruefen - sonst ueberschriebe sie Aenderungen vom Handy
            if (!string.IsNullOrEmpty(input.Etag) && input.Etag != obj.Etag) throw new PreconditionFailedException();
            etag = obj.Etag;
            vcal = Ics.Parse(obj.Data).First(r => r.Name == "VCALENDAR");
            var master = vcal.Of("VEVENT").FirstOrDefault(e => e.Get("RECURRENCE-ID") == null) ?? vcal.Of("VEVENT").First();
            bool recurring = master.Get("RRULE") != null || master.Get("RDATE") != null;

            if (recurring && input.Scope == "instance" && input.Instance != null)
            {
                // Nur dieses Vorkommen: eigene Komponente mit RECURRENCE-ID
                var rid = ParseIso(input.Instance, out var ridAllDay);
                var ridProp = TimeProp("RECURRENCE-ID", master.Get("DTSTART"), rid, ridAllDay, zone);
                target = vcal.Of("VEVENT").FirstOrDefault(e => e.Get("RECURRENCE-ID") is { } p && p.Value == ridProp.Value)
                         ?? NewOverride(vcal, master, ridProp);
                input.RRule = null;
                Fill(target, input, zone);
                target.Remove("RRULE");
            }
            else if (recurring && input.Instance != null)
            {
                // Ganze Serie, aber am Vorkommen verschoben: Unterschied auf den Beginn der Serie anwenden
                var instance = ParseIso(input.Instance, out _);
                var newStart = ParseIso(input.Start, out _);
                var masterStart = Ics.Time(master.Get("DTSTART"), zone, out _) ?? newStart;
                var shift = newStart - instance;
                var length = ParseIso(input.End, out _) - newStart;
                input.Start = Iso(masterStart + shift, input.AllDay);
                input.End = Iso(masterStart + shift + length, input.AllDay);
                target = master;
                var oldStart = CopyProp(master.Get("DTSTART"));
                Fill(target, input, zone);
                ShiftExceptions(vcal, master, oldStart, zone);
            }
            else
            {
                target = master;
                var oldStart = CopyProp(master.Get("DTSTART"));
                Fill(target, input, zone);
                if (recurring) ShiftExceptions(vcal, master, oldStart, zone);
            }
            // Einladung eines anderen: SEQUENCE zaehlt nur der Organisator hoch (RFC 5546) - sonst hielte die eigene Kopie
            // seine naechste Aenderung fuer veraltet
            var invited = Invited(space, master);
            Touch(master, !invited);
            if (target != master) Touch(target, !invited);
        }

        if (input.Attendees != null)
        {
            var accounts = space.Settings.MailAccounts;
            var organizer = accounts.FirstOrDefault(a => a.Id == input.Account) ?? accounts.FirstOrDefault();
            ApplyAttendees(target, input.Attendees, organizer, accounts.Select(a => a.Email.ToLowerInvariant()).ToHashSet());
        }

        if (!input.AllDay && !vcal.Of("VTIMEZONE").Any(t => t.Text("TZID") == zone.Id) && zone != TimeZoneInfo.Utc)
            vcal.Children.Insert(0, Ics.Timezone(zone));

        var data = vcal.ToString();
        if (input.MoveTo is long move && move != coll.Id)
        {
            var dest = PimStore.Find(db, move) ?? throw new KeyNotFoundException("Kalender nicht gefunden.");
            PimStore.Put(db, dest, uri, data, zone: zone);
            PimStore.Delete(db, coll, uri);
            return dest.Id + "/" + uri;
        }
        PimStore.Put(db, coll, uri, data, ifMatch: etag, zone: zone);
        return coll.Id + "/" + uri;
    }

    public static void DeleteEvent(UserSpace space, long calendar, string uri, string? instance)
    {
        Writable(space.Db, calendar);
        var db = space.Db;
        var coll = PimStore.Find(db, calendar) ?? throw new KeyNotFoundException();
        if (instance == null)
        {
            PimStore.Delete(db, coll, uri);
            return;
        }
        // Ein Vorkommen: EXDATE an der Serie, ein etwaiges eigenes Vorkommen entfernen
        var obj = PimStore.Object(db, coll.Id, uri) ?? throw new KeyNotFoundException();
        var vcal = Ics.Parse(obj.Data).First(r => r.Name == "VCALENDAR");
        var master = vcal.Of("VEVENT").First(e => e.Get("RECURRENCE-ID") == null);
        var when = ParseIso(instance, out var allDay);
        var ex = TimeProp("EXDATE", master.Get("DTSTART"), when, allDay, space.Zone);
        master.Props.Add(ex);
        vcal.Children.RemoveAll(e => e.Name == "VEVENT" && e.Get("RECURRENCE-ID") is { } p && Ics.Time(p, space.Zone, out _) == when);
        Touch(master, !Invited(space, master));
        PimStore.Put(db, coll, uri, vcal.ToString(), ifMatch: obj.Etag, zone: space.Zone);
    }

    private static Component NewOverride(Component vcal, Component master, Prop rid)
    {
        var o = new Component("VEVENT");
        foreach (var p in master.Props.Where(p => p.Name is not ("RRULE" or "EXDATE" or "RDATE" or "DTSTART" or "DTEND" or "DURATION")))
        {
            var copy = new Prop(p.Name, p.Value);
            copy.Params.AddRange(p.Params);
            o.Props.Add(copy);
        }
        o.Props.Add(rid);
        // Kopien - sonst aenderte die Erinnerung des Vorkommens auch die der Serie
        foreach (var alarm in master.Of("VALARM")) o.Children.Add(Clone(alarm));
        vcal.Children.Add(o);
        return o;
    }

    private static void Fill(Component e, EventInput input, TimeZoneInfo zone)
    {
        e.SetText("SUMMARY", input.Title);
        e.SetText("LOCATION", input.Location);
        e.SetText("DESCRIPTION", input.Description);

        var start = ParseIso(input.Start, out var sd);
        var end = ParseIso(input.End, out _);
        if (end <= start) end = input.AllDay ? start.AddDays(1) : start.AddMinutes(30);
        e.Remove("DURATION");
        if (input.AllDay)
        {
            e.Set("DTSTART", Ics.Date(start)).With("VALUE", "DATE");
            e.Set("DTEND", Ics.Date(end)).With("VALUE", "DATE");
        }
        else if (zone == TimeZoneInfo.Utc)
        {
            e.Set("DTSTART", Ics.Utc(start));
            e.Set("DTEND", Ics.Utc(end));
        }
        else
        {
            e.Set("DTSTART", Ics.Local(start, zone)).With("TZID", zone.Id);
            e.Set("DTEND", Ics.Local(end, zone)).With("TZID", zone.Id);
        }

        if (input.RRule != null)
        {
            if (input.RRule.Length == 0) { e.Remove("RRULE"); e.Remove("EXDATE"); }
            else e.Set("RRULE", input.RRule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? input.RRule[6..] : input.RRule);
        }
        // EXDATE einer Serie, deren Beginn sich geaendert hat, passen in der Zeitform nicht mehr - nur bei Ganztaegig/Zeit-Wechsel loeschen
        if (e.Get("EXDATE") is { } exd && (exd.Param("VALUE") == "DATE") != input.AllDay) e.Remove("EXDATE");

        if (input.Recipe != null)
        {
            e.Remove("X-DM-RECIPE");
            if (input.Recipe.Length > 0) e.Set("X-DM-RECIPE", Ics.Escape(input.Recipe));
        }

        if (input.Busy) e.Remove("TRANSP"); else e.Set("TRANSP", "TRANSPARENT");
        if (input.Categories != null)
        {
            e.Remove("CATEGORIES");
            if (input.Categories.Count > 0) e.Set("CATEGORIES", string.Join(",", input.Categories.Select(Ics.Escape)));
        }

        // Nur den Alarm aendern, den Dto als Erinnerung zeigt - weitere (vom Handy) bleiben
        var existing = ReminderAlarm(e, out var current);
        if (input.Reminder is int min && min >= 0)
        {
            var trigger = min == 0 ? "-PT0M" : "-" + System.Xml.XmlConvert.ToString(TimeSpan.FromMinutes(min));
            if (existing == null)
            {
                var alarm = new Component("VALARM");
                alarm.Set("ACTION", "DISPLAY");
                alarm.Set("DESCRIPTION", Ics.Escape(input.Title.Length > 0 ? input.Title : "Erinnerung"));
                alarm.Set("TRIGGER", trigger);
                e.Children.Add(alarm);
            }
            else if (current != TimeSpan.FromMinutes(-min))
            {
                existing.Set("TRIGGER", trigger);
                existing.Remove("ACKNOWLEDGED");
            }
        }
        else if (existing != null) e.Children.Remove(existing);
    }

    /// <summary>Der Alarm, der als Erinnerung gilt: der erste mit relativer Zeit davor.</summary>
    private static Component? ReminderAlarm(Component e, out TimeSpan before)
    {
        foreach (var alarm in e.Of("VALARM"))
            if (alarm.Text("TRIGGER") is { } trig && PimStore.TryDuration(trig, out var t) && t <= TimeSpan.Zero)
            {
                before = t;
                return alarm;
            }
        before = TimeSpan.Zero;
        return null;
    }

    private static Prop? CopyProp(Prop? p)
    {
        if (p == null) return null;
        var copy = new Prop(p.Name, p.Value);
        copy.Params.AddRange(p.Params);
        return copy;
    }

    private static Component Clone(Component c)
    {
        var copy = new Component(c.Name);
        foreach (var p in c.Props) copy.Props.Add(CopyProp(p)!);
        foreach (var child in c.Children) copy.Children.Add(Clone(child));
        return copy;
    }

    /// <summary>
    /// Serie zeitlich verschoben: EXDATE und RECURRENCE-ID der Ausnahmen um denselben
    /// Versatz mitziehen - sonst tauchen abgesagte Termine wieder auf. Der Versatz gilt
    /// in Ortszeit der Serie, so bleibt er ueber Sommer- und Winterzeit hinweg gleich.
    /// </summary>
    internal static void ShiftExceptions(Component vcal, Component master, Prop? oldStart, TimeZoneInfo zone)
    {
        var newStart = master.Get("DTSTART");
        if (oldStart == null || newStart == null) return;
        if (Ics.Time(oldStart, zone, out var oldAllDay) is not DateTime before || Ics.Time(newStart, zone, out var newAllDay) is not DateTime after) return;
        // Wechsel ganztaegig/mit Uhrzeit: Fill hat EXDATE schon verworfen
        if (before == after || oldAllDay != newAllDay) return;
        var oldTz = SeriesZone(oldStart, zone);
        var newTz = SeriesZone(newStart, zone);
        var delta = Wall(after, newTz) - Wall(before, oldTz);

        Prop? Shift(string name, Prop value)
        {
            if (Ics.Time(value, zone, out _) is not DateTime t) return null;
            var moved = Ics.ToUtc(Wall(t, oldTz) + delta, newTz);
            var p = TimeProp(name, newStart, moved, newAllDay, zone);
            if (value.Param("RANGE") is { } range) p.With("RANGE", range);
            return p;
        }

        // EXDATE, auch mit mehreren Werten in einer Zeile
        var exdates = master.All("EXDATE").ToList();
        if (exdates.Count > 0)
        {
            var shifted = new List<Prop>();
            foreach (var ex in exdates)
                foreach (var v in ex.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var single = new Prop("EXDATE", v);
                    single.Params.AddRange(ex.Params);
                    shifted.Add(Shift("EXDATE", single) ?? single);
                }
            master.Remove("EXDATE");
            master.Props.AddRange(shifted);
        }

        foreach (var o in vcal.Of("VEVENT"))
        {
            if (o == master || o.Get("RECURRENCE-ID") is not { } rid) continue;
            if (Shift("RECURRENCE-ID", rid) is not { } moved) continue;
            o.Props[o.Props.IndexOf(rid)] = moved;
        }
    }

    /// <summary>Die Zone, in der eine Zeit der Serie gilt: ihre TZID, UTC, oder bei schwebender Zeit die eigene.</summary>
    private static TimeZoneInfo SeriesZone(Prop p, TimeZoneInfo zone)
    {
        var v = p.Value.Trim();
        if (p.Param("VALUE") == "DATE" || v.Length == 8 || v.EndsWith('Z')) return TimeZoneInfo.Utc;
        return Ics.Zone(p.Param("TZID")) ?? zone;
    }

    private static DateTime Wall(DateTime utc, TimeZoneInfo tz) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz), DateTimeKind.Unspecified);

    private static void Touch(Component e, bool sequence = true)
    {
        e.Set("DTSTAMP", Ics.Stamp());
        e.Set("LAST-MODIFIED", Ics.Stamp());
        if (sequence) e.Set("SEQUENCE", ((int.TryParse(e.Text("SEQUENCE"), out var s) ? s : 0) + 1).ToString());
    }

    /// <summary>Termin aus einer Einladung: der Organisator ist jemand anderes.</summary>
    private static bool Invited(UserSpace space, Component master) =>
        Mail.Invites.Mailto(master.Get("ORGANIZER")) is { } org
        && !space.Settings.MailAccounts.Any(a => a.Email.Equals(org, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ein Vorkommen der Serie als eigene Komponente (RECURRENCE-ID): die vorhandene oder eine neue aus der Serie, mit Beginn
    /// und Ende dieses Vorkommens. Fuer Antworten und Absagen, die nur einen Termin der Serie betreffen.
    /// </summary>
    internal static Component Occurrence(Component vcal, DateTime utc, TimeZoneInfo zone)
    {
        var master = vcal.Of("VEVENT").First(e => e.Get("RECURRENCE-ID") == null);
        if (vcal.Of("VEVENT").FirstOrDefault(e => e.Get("RECURRENCE-ID") is { } p && Ics.Time(p, zone, out _) == utc) is { } found) return found;
        var dtstart = master.Get("DTSTART");
        var start = Ics.Time(dtstart, zone, out var allDay) ?? utc;
        var end = Ics.Time(master.Get("DTEND"), zone, out _);
        if (end == null && master.Text("DURATION") is { } dur && PimStore.TryDuration(dur, out var d)) end = start + d;
        var o = NewOverride(vcal, master, TimeProp("RECURRENCE-ID", dtstart, utc, allDay, zone));
        o.Props.Add(TimeProp("DTSTART", dtstart, utc, allDay, zone));
        o.Props.Add(TimeProp("DTEND", dtstart, utc + ((end ?? start) - start), allDay, zone));
        return o;
    }

    /// <summary>RECURRENCE-ID/EXDATE in derselben Zeitform wie DTSTART der Serie.</summary>
    private static Prop TimeProp(string name, Prop? dtstart, DateTime utc, bool allDay, TimeZoneInfo zone)
    {
        if (allDay || dtstart?.Param("VALUE") == "DATE" || dtstart?.Value.Length == 8)
            return new Prop(name, Ics.Date(utc)).With("VALUE", "DATE");
        if (dtstart?.Param("TZID") is { } tzid && Ics.Zone(tzid) is { } tz)
            return new Prop(name, Ics.Local(utc, tz)).With("TZID", tzid);
        if (dtstart != null && !dtstart.Value.EndsWith('Z'))
            return new Prop(name, Ics.Local(utc, zone));   // schwebende Zeit
        return new Prop(name, Ics.Utc(utc));
    }

    public static DateTime ParseIso(string s, out bool dateOnly)
    {
        dateOnly = s.Length == 10;
        if (dateOnly) return DateTime.SpecifyKind(DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    public static Component NewCalendar()
    {
        var vcal = new Component("VCALENDAR");
        vcal.Set("VERSION", "2.0");
        vcal.Set("PRODID", Prodid);
        vcal.Set("CALSCALE", "GREGORIAN");
        return vcal;
    }

    // ---------------- Aufgaben ----------------

    public static List<TaskDto> Tasks(UserSpace space, IEnumerable<long> lists)
    {
        var zone = space.Zone;
        var result = new List<TaskDto>();
        foreach (var obj in PimStore.InRange(space.Db, lists, "VTODO", null, null))
        {
            Component? t;
            try { t = Ics.Parse(obj.Data).First(r => r.Name == "VCALENDAR").Of("VTODO").FirstOrDefault(); }
            catch { continue; }
            if (t == null) continue;
            // Eine kaputte Aufgabe darf nicht die ganze Liste scheitern lassen
            try
            {
                var due = Ics.Time(t.Get("DUE"), zone, out var dueAllDay);
                var start = Ics.Time(t.Get("DTSTART"), zone, out var startAllDay);
                var completedAt = Ics.Time(t.Get("COMPLETED"), zone, out _);
                var status = t.Text("STATUS");
                bool completed = completedAt != null || string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase);
                var created = Ics.Time(t.Get("CREATED"), zone, out _) ?? Ics.Time(t.Get("DTSTAMP"), zone, out _) ?? DateTime.UtcNow;
                result.Add(new TaskDto(obj.Collection + "/" + obj.Uri, obj.Collection, obj.Uri, obj.Etag, t.Text("UID"),
                    t.Text("SUMMARY") ?? "", t.Text("DESCRIPTION"),
                    due == null ? null : Iso(due.Value, dueAllDay), dueAllDay,
                    start == null ? null : Iso(start.Value, startAllDay),
                    completed, completedAt == null ? null : Iso(completedAt.Value, false),
                    int.TryParse(t.Text("PRIORITY"), out var pr) ? pr : 0,
                    int.TryParse(t.Text("PERCENT-COMPLETE"), out var pc) ? pc : (completed ? 100 : 0),
                    status,
                    t.All("CATEGORIES").SelectMany(p => p.Value.Split(',')).Select(Ics.Unescape).Where(x => x.Length > 0).ToList(),
                    t.All("RELATED-TO").FirstOrDefault(p => (p.Param("RELTYPE") ?? "PARENT").Equals("PARENT", StringComparison.OrdinalIgnoreCase))?.Text,
                    Iso(created, false),
                    t.Text("X-DM-ITEM"), t.Text("X-DM-QTY"), t.Text("X-DM-UNIT"), t.Text("X-DM-SECTION")));
            }
            catch { /* auslassen */ }
        }
        return result;
    }

    public static string SaveTask(UserSpace space, TaskInput input)
    {
        var db = space.Db;
        var zone = space.Zone;
        var coll = PimStore.Find(db, input.List) ?? throw new KeyNotFoundException("Liste nicht gefunden.");
        Component vcal, t;
        string uri;
        string? etag = null;
        if (string.IsNullOrEmpty(input.Uri))
        {
            uri = PimStore.NewUri(".ics");
            vcal = NewCalendar();
            t = new Component("VTODO");
            t.Set("UID", Guid.NewGuid().ToString("D"));
            t.Set("CREATED", Ics.Stamp());
            vcal.Children.Add(t);
        }
        else
        {
            uri = input.Uri;
            var obj = PimStore.Object(db, coll.Id, uri) ?? throw new KeyNotFoundException("Aufgabe nicht gefunden.");
            if (!string.IsNullOrEmpty(input.Etag) && input.Etag != obj.Etag) throw new PreconditionFailedException();
            etag = obj.Etag;
            vcal = Ics.Parse(obj.Data).First(r => r.Name == "VCALENDAR");
            t = vcal.Of("VTODO").First();
        }

        t.SetText("SUMMARY", input.Title);
        t.SetText("DESCRIPTION", input.Description);
        if (string.IsNullOrEmpty(input.Due)) t.Remove("DUE");
        else
        {
            var due = ParseIso(input.Due, out var dateOnly);
            if (input.DueAllDay || dateOnly) t.Set("DUE", Ics.Date(due)).With("VALUE", "DATE");
            else t.Set("DUE", Ics.Utc(due));
            // DTSTART nach DUE ist ungueltig
            if (Ics.Time(t.Get("DTSTART"), zone, out _) is DateTime st && st > due) t.Remove("DTSTART");
        }
        if (input.Priority > 0) t.Set("PRIORITY", Math.Clamp(input.Priority, 1, 9).ToString()); else t.Remove("PRIORITY");
        if (input.Completed)
        {
            if (t.Get("COMPLETED") == null) t.Set("COMPLETED", Ics.Stamp());
            t.Set("STATUS", "COMPLETED");
            t.Set("PERCENT-COMPLETE", "100");
        }
        else
        {
            t.Remove("COMPLETED");
            if (t.Text("STATUS") == "COMPLETED") t.Set("STATUS", "NEEDS-ACTION");
            if (input.Percent is int p) { if (p > 0) t.Set("PERCENT-COMPLETE", Math.Clamp(p, 0, 99).ToString()); else t.Remove("PERCENT-COMPLETE"); }
            else if (t.Text("PERCENT-COMPLETE") == "100") t.Remove("PERCENT-COMPLETE");
        }
        if (input.Categories != null)
        {
            t.Remove("CATEGORIES");
            if (input.Categories.Count > 0) t.Set("CATEGORIES", string.Join(",", input.Categories.Select(Ics.Escape)));
        }
        // Einkaufsliste: neue Artikel und geaenderte Abteilungen merken (nicht jedes Abhaken)
        bool learn = input.Item != null && (string.IsNullOrEmpty(input.Uri)
            || !string.Equals(t.Text("X-DM-ITEM"), input.Item, StringComparison.OrdinalIgnoreCase)
            || t.Text("X-DM-SECTION") != (input.Section ?? t.Text("X-DM-SECTION")));
        if (input.Item != null) t.SetText("X-DM-ITEM", input.Item);
        if (input.Qty != null) t.SetText("X-DM-QTY", input.Qty);
        if (input.Unit != null) t.SetText("X-DM-UNIT", input.Unit);
        if (input.Section != null) t.SetText("X-DM-SECTION", input.Section);
        t.Set("DTSTAMP", Ics.Stamp());
        t.Set("LAST-MODIFIED", Ics.Stamp());

        var data = vcal.ToString();
        if (learn && !string.IsNullOrWhiteSpace(input.Item)) Lists.Learn(db, coll.Id, input.Item, input.Unit, input.Section);
        if (input.MoveTo is long move && move != coll.Id)
        {
            var dest = PimStore.Find(db, move) ?? throw new KeyNotFoundException();
            PimStore.Put(db, dest, uri, data, zone: zone);
            PimStore.Delete(db, coll, uri);
            return dest.Id + "/" + uri;
        }
        PimStore.Put(db, coll, uri, data, ifMatch: etag, zone: zone);
        return coll.Id + "/" + uri;
    }
}
