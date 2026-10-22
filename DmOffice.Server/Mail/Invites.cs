using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>Einladung in einer Mail (iTIP/iMIP, RFC 5546/6047) - fuer den Hinweis im Lesebereich.</summary>
public sealed record InviteDto(
    string Method, string Uid, int Sequence, string Title, DateTime? Start, DateTime? End, bool AllDay,
    string? Location, string? OrganizerName, string? OrganizerEmail, bool Recurring,
    /// <summary>Eigene Antwort laut Mail bzw. laut Kalender: NEEDS-ACTION, ACCEPTED, TENTATIVE, DECLINED.</summary>
    string? MyStatus,
    /// <summary>Kalender, in dem der Termin schon steht (null: noch nirgends).</summary>
    long? CalendarId, string? CalendarName,
    /// <summary>Im Kalender steht schon eine neuere Version (hoehere SEQUENCE).</summary>
    bool Outdated,
    /// <summary>Andere Termine zur selben Zeit.</summary>
    List<string> Conflicts,
    /// <summary>Organisator hat geantwortet (METHOD:REPLY): wer und wie.</summary>
    string? ReplyFrom, string? ReplyStatus);

/// <summary>
/// Einladungen annehmen, mit Vorbehalt annehmen, ablehnen - wie in Outlook.
/// Der Termin kommt in den Kalender (Standard: Einstellungen -> Kalender -> Ereignisse),
/// die Antwort (METHOD:REPLY) geht per SMTP an den Organisator.
/// </summary>
public static class Invites
{
    /// <summary>Der Kalender-Teil einer Mail: text/calendar oder ein .ics-Anhang.</summary>
    public static Component? Calendar(MimeMessage m)
    {
        var part = m.BodyParts.OfType<MimePart>().FirstOrDefault(p =>
            p.ContentType.IsMimeType("text", "calendar") || p.ContentType.IsMimeType("application", "ics")
            || (p.FileName?.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ?? false));
        if (part?.Content == null) return null;
        using var ms = new MemoryStream();
        part.Content.DecodeTo(ms);
        var charset = part.ContentType.Charset;
        Encoding enc;
        try { enc = charset != null ? Encoding.GetEncoding(charset) : Encoding.UTF8; } catch { enc = Encoding.UTF8; }
        try { return Ics.Parse(enc.GetString(ms.ToArray())).FirstOrDefault(c => c.Name == "VCALENDAR"); }
        catch { return null; }
    }

    private static Component? Master(Component vcal) =>
        vcal.Of("VEVENT").FirstOrDefault(e => e.Get("RECURRENCE-ID") == null) ?? vcal.Of("VEVENT").FirstOrDefault();

    public static string? Mailto(Prop? p) =>
        p == null ? null : p.Value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? p.Value[7..].Trim() : p.Value.Trim();

    private static HashSet<string> MyAddresses(UserSpace space) =>
        space.Settings.MailAccounts.Select(a => a.Email.ToLowerInvariant()).ToHashSet();

    private static Prop? MyAttendee(UserSpace space, Component ev)
    {
        var mine = MyAddresses(space);
        return ev.All("ATTENDEE").FirstOrDefault(a => Mailto(a) is { } m && mine.Contains(m.ToLowerInvariant()));
    }

    private static int Seq(Component ev) => int.TryParse(ev.Text("SEQUENCE"), out var s) ? s : 0;

    /// <summary>Wo der Termin mit dieser UID schon steht (erster Treffer ueber alle Kalender).</summary>
    private static (Collection Coll, DavObject Obj)? Existing(UserSpace space, string uid)
    {
        foreach (var c in PimStore.Collections(space.Db, PimStore.Calendar))
            if (PimStore.ByUid(space.Db, c.Id, uid) is { } o) return (c, o);
        return null;
    }

    /// <summary>Standardkalender fuer Einladungen: aus den Einstellungen, sonst der erste fuer Termine.</summary>
    public static Collection DefaultCalendar(UserSpace space)
    {
        PimStore.EnsureDefaults(space.Db);
        var all = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT")).ToList();
        return all.FirstOrDefault(c => c.Id == space.Settings.InviteCalendar) ?? all.FirstOrDefault()
            ?? throw new InvalidOperationException("Kein Kalender für Termine vorhanden.");
    }

    public static InviteDto? Read(UserSpace space, MimeMessage m)
    {
        var vcal = Calendar(m);
        if (vcal == null) return null;
        var ev = Master(vcal);
        var uid = ev?.Text("UID");
        if (ev == null || string.IsNullOrEmpty(uid)) return null;
        var method = (vcal.Text("METHOD") ?? "PUBLISH").Trim().ToUpperInvariant();

        var zone = space.Zone;
        var start = Ics.Time(ev.Get("DTSTART"), zone, out var allDay);
        var end = Ics.Time(ev.Get("DTEND"), zone, out _);
        if (end == null && start != null && ev.Text("DURATION") is { } dur && PimStore.TryDuration(dur, out var d)) end = start + d;
        end ??= start;

        var org = ev.Get("ORGANIZER");
        var me = MyAttendee(space, ev);
        string? myStatus = me?.Param("PARTSTAT")?.ToUpperInvariant();

        var existing = Existing(space, uid);
        bool outdated = false;
        if (existing is { } ex)
        {
            var stored = Ics.Parse(ex.Obj.Data).FirstOrDefault()?.Of("VEVENT").FirstOrDefault(e => e.Get("RECURRENCE-ID") == null);
            if (stored != null)
            {
                outdated = Seq(stored) > Seq(ev);
                // Im Kalender steht die eigene Antwort - die gilt
                if (MyAttendee(space, stored)?.Param("PARTSTAT") is { } st) myStatus = st.ToUpperInvariant();
            }
        }

        var conflicts = new List<string>();
        if (start != null && end != null && !allDay && method == "REQUEST")
        {
            var ids = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT")).Select(c => c.Id).ToList();
            // Serien aufgeloest (RRULE, EXDATE, verschobene Vorkommen) - nur was wirklich in die Zeit faellt
            var localStart = TimeZoneInfo.ConvertTimeFromUtc(start.Value, zone);
            var localEnd = TimeZoneInfo.ConvertTimeFromUtc(end.Value, zone);
            foreach (var o in CalendarModel.Events(space, start.Value, end.Value, ids))
            {
                if (o.Uid == uid) continue;
                var s = CalendarModel.ParseIso(o.Start, out _);
                var e = CalendarModel.ParseIso(o.End, out _);
                bool hit = o.AllDay
                    ? s.Date < localEnd && e.Date > localStart.Date // Tage [s, e) in Ortszeit
                    : s < end.Value && e > start.Value;
                if (!hit) continue;
                conflicts.Add(string.IsNullOrWhiteSpace(o.Title) ? "(ohne Titel)" : o.Title);
                if (conflicts.Count >= 3) break;
            }
        }

        // Antwort eines Teilnehmers auf eine eigene Einladung
        string? replyFrom = null, replyStatus = null;
        if (method == "REPLY" && ev.Get("ATTENDEE") is { } att)
        {
            replyFrom = att.Param("CN") ?? Mailto(att);
            replyStatus = att.Param("PARTSTAT")?.ToUpperInvariant();
        }

        return new InviteDto(method, uid, Seq(ev), ev.Text("SUMMARY") ?? "", start, end, allDay, ev.Text("LOCATION"),
            org?.Param("CN"), Mailto(org), ev.Get("RRULE") != null, myStatus,
            existing?.Coll.Id, existing?.Coll.Name, outdated, conflicts, replyFrom, replyStatus);
    }

    /// <summary>
    /// accept | tentative | decline | remove (bei Absage des Organisators) | apply (Antwort eines Teilnehmers uebernehmen).
    /// Gibt zurueck, ob eine Antwort an den Organisator gesendet wurde.
    /// </summary>
    public static async Task<bool> RespondAsync(UserSpace space, MailAccount account, MimeMessage m, string response, bool send, CancellationToken ct)
    {
        var vcal = Calendar(m) ?? throw new ArgumentException("Die Mail enthält keine Einladung.");
        var ev = Master(vcal) ?? throw new ArgumentException("Die Einladung enthält keinen Termin.");
        var uid = ev.Text("UID") ?? throw new ArgumentException("Die Einladung hat keine UID.");
        var existing = Existing(space, uid);
        var db = space.Db;

        // Ein vorhandener Termin darf nur geaendert oder geloescht werden, wenn die Einladung wirklich von seinem
        // Organisator kommt. Sonst koennte ein Fremder, der nur die UID kennt, fremde Termine ueberschreiben oder loeschen.
        if (existing is { } cur)
        {
            var storedOrg = Mailto(Ics.Parse(cur.Obj.Data).FirstOrDefault(c => c.Name == "VCALENDAR")?.Of("VEVENT").FirstOrDefault()?.Get("ORGANIZER"))?.ToLowerInvariant();
            var mailOrg = Mailto(ev.Get("ORGANIZER"))?.ToLowerInvariant();
            var mine = MyAddresses(space);
            // Eigene Termine (ich bin Organisator) nie durch fremde REQUEST/CANCEL aendern
            if (storedOrg != null && mine.Contains(storedOrg) && response != "apply")
                throw new ArgumentException("Dieser Termin gehört dir - eine fremde Einladung ändert ihn nicht.");
            // Organisator der Einladung muss zum gespeicherten passen
            if (storedOrg != null && mailOrg != null && storedOrg != mailOrg)
                throw new ArgumentException("Die Einladung kommt nicht vom Organisator dieses Termins.");
            // Und der Mail-Absender muss der Organisator sein (bzw. bei einer Antwort der antwortende Teilnehmer)
            var from = (m.From.Mailboxes.FirstOrDefault()?.Address ?? m.Sender?.Address)?.ToLowerInvariant();
            if (response == "apply")
            {
                var who = Mailto(ev.Get("ATTENDEE"))?.ToLowerInvariant();
                if (from != null && who != null && from != who)
                    throw new ArgumentException("Die Antwort passt nicht zum Absender.");
            }
            else if (from != null && mailOrg != null && from != mailOrg)
                throw new ArgumentException("Die Einladung kommt nicht vom Organisator dieses Termins.");
        }

        // Mail betrifft nur einzelne Termine einer Serie (RECURRENCE-ID ohne die Serie selbst) - so schicken Outlook und
        // Google "nur dieses Vorkommen"; dann nur diese Termine aendern, die Serie bleibt
        var occurrences = vcal.Of("VEVENT").All(e => e.Get("RECURRENCE-ID") != null)
            && existing is { } se && Ics.Parse(se.Obj.Data).First(c => c.Name == "VCALENDAR").Of("VEVENT").Any(e => e.Get("RECURRENCE-ID") == null && e.Get("RRULE") != null);

        if ((response == "remove" || response == "decline") && occurrences)
        {
            var ex = existing!.Value;
            foreach (var e in vcal.Of("VEVENT"))
                if (Ics.Time(e.Get("RECURRENCE-ID"), space.Zone, out var day) is DateTime t)
                    CalendarModel.DeleteEvent(space, ex.Coll.Id, ex.Obj.Uri, day ? t.ToString("yyyy-MM-dd") : t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
            if (response == "remove") return false;
        }
        else if (response == "remove" || response == "decline")
        {
            if (existing is { } ex) PimStore.Delete(db, ex.Coll, ex.Obj.Uri);
            if (response == "remove") return false;
        }
        else if (response == "apply")
        {
            // Teilnehmer hat geantwortet: dessen PARTSTAT im eigenen Termin nachtragen
            if (existing is not { } ex || ev.Get("ATTENDEE") is not { } reply) return false;
            var stored = Ics.Parse(ex.Obj.Data).First(c => c.Name == "VCALENDAR");
            var who = Mailto(reply)?.ToLowerInvariant();
            // Antwort fuer einen Termin der Serie: nur dort vermerken
            var targets = stored.Of("VEVENT").ToList();
            if (Ics.Time(ev.Get("RECURRENCE-ID"), space.Zone, out _) is DateTime rid
                && targets.Any(e => e.Get("RECURRENCE-ID") == null && e.Get("RRULE") != null))
                targets = [CalendarModel.Occurrence(stored, rid, space.Zone)];
            foreach (var e in targets)
                foreach (var a in e.All("ATTENDEE").Where(a => Mailto(a)?.ToLowerInvariant() == who))
                    a.With("PARTSTAT", reply.Param("PARTSTAT") ?? "NEEDS-ACTION");
            PimStore.Put(db, ex.Coll, ex.Obj.Uri, stored.ToString(), zone: space.Zone);
            return false;
        }
        else
        {
            var partstat = response == "tentative" ? "TENTATIVE" : "ACCEPTED";
            // Gespeichert wird die Einladung selbst, ohne METHOD, mit der eigenen Antwort
            var copy = Ics.Parse(vcal.ToString()).First(c => c.Name == "VCALENDAR");
            copy.Remove("METHOD");
            foreach (var e in copy.Of("VEVENT"))
            {
                var me = MyAttendee(space, e);
                if (me == null)
                {
                    me = new Prop("ATTENDEE", "mailto:" + account.Email);
                    if (!string.IsNullOrEmpty(account.DisplayName)) me.With("CN", account.DisplayName);
                    e.Props.Add(me);
                }
                me.With("PARTSTAT", partstat);
                me.Params.RemoveAll(p => p.Key.Equals("RSVP", StringComparison.OrdinalIgnoreCase));
                if (response == "tentative") e.Set("STATUS", "TENTATIVE"); else e.Remove("STATUS");
            }
            if (occurrences)
            {
                // Die geaenderten Termine ersetzen ihre Vorgaenger in der gespeicherten Serie
                var ex = existing!.Value;
                var stored = Ics.Parse(ex.Obj.Data).First(c => c.Name == "VCALENDAR");
                foreach (var e in copy.Of("VEVENT").ToList())
                {
                    var t = Ics.Time(e.Get("RECURRENCE-ID"), space.Zone, out _);
                    stored.Children.RemoveAll(o => o.Name == "VEVENT" && o.Get("RECURRENCE-ID") is { } r && Ics.Time(r, space.Zone, out _) == t);
                    stored.Children.Add(e);
                }
                foreach (var tz in copy.Of("VTIMEZONE").Where(z => !stored.Of("VTIMEZONE").Any(x => x.Text("TZID") == z.Text("TZID"))))
                    stored.Children.Insert(0, tz);
                PimStore.Put(db, ex.Coll, ex.Obj.Uri, stored.ToString(), zone: space.Zone);
            }
            else if (existing is { } ex) PimStore.Put(db, ex.Coll, ex.Obj.Uri, copy.ToString(), zone: space.Zone);
            else PimStore.Put(db, DefaultCalendar(space), PimStore.NewUri(".ics"), copy.ToString(), zone: space.Zone);
        }

        var organizer = Mailto(ev.Get("ORGANIZER"));
        if (!send || organizer == null || MyAddresses(space).Contains(organizer.ToLowerInvariant()) || (vcal.Text("METHOD") ?? "").ToUpperInvariant() != "REQUEST")
            return false;

        using var reply2 = Reply(space, account, vcal, ev, response);
        using var smtp = await ImapPool.SmtpAsync(account, ct);
        await smtp.SendAsync(reply2, ct);
        await smtp.DisconnectAsync(true, ct);
        return true;
    }

    private static readonly HashSet<string> ForAttendees =
        ["SUMMARY", "LOCATION", "DESCRIPTION", "DTSTART", "DTEND", "DURATION", "RRULE", "RDATE", "EXDATE", "RECURRENCE-ID", "STATUS"];

    /// <summary>
    /// Hat sich etwas geaendert, das die Teilnehmer angeht - Zeit, Ort, Titel, Beschreibung, Wiederholung, gestrichene Termine?
    /// Eigene Erinnerung, frei/beschaeftigt oder ein anderer Kalender gehen sie nichts an.
    /// </summary>
    public static bool Changed(string? before, string? after)
    {
        static string Key(string ics)
        {
            try
            {
                var vcal = Ics.Parse(ics).First(c => c.Name == "VCALENDAR");
                return string.Join('\n', vcal.Of("VEVENT").SelectMany(e => e.Props.Where(p => ForAttendees.Contains(p.Name))
                    .Select(p => p.Name + string.Concat(p.Params.Select(x => ";" + x.Key + "=" + x.Value)) + ":" + p.Value)).Order());
            }
            catch { return ics; }
        }
        return before == null || after == null || Key(before) != Key(after);
    }

    /// <summary>Teilnehmer eines gespeicherten Termins (Kleinbuchstaben), ohne die eigenen Adressen.</summary>
    public static HashSet<string> Attendees(UserSpace space, string? ics)
    {
        if (string.IsNullOrEmpty(ics)) return new();
        try
        {
            var vcal = Ics.Parse(ics).FirstOrDefault(c => c.Name == "VCALENDAR");
            var mine = MyAddresses(space);
            return vcal == null ? new() : vcal.Of("VEVENT").SelectMany(e => e.All("ATTENDEE")).Select(Mailto)
                .Where(m => m != null && !mine.Contains(m.ToLowerInvariant())).Select(m => m!.ToLowerInvariant()).ToHashSet();
        }
        catch { return new(); }
    }

    /// <summary>
    /// Eigene Einladung verschicken (RFC 6047): METHOD:REQUEST an die Teilnehmer, METHOD:CANCEL bei Absage. Geht ueber den
    /// Postausgang des Organisator-Kontos (mit Wiederholung bei Fehlern, danach im Ordner Gesendet). Nur wenn der Organisator
    /// ein eigenes Mailkonto ist. Gibt die Zahl der Empfaenger zurueck.
    /// </summary>
    public static int Send(UserSpace space, string ics, string method, IReadOnlyCollection<string>? only = null)
    {
        var vcal = Ics.Parse(ics).FirstOrDefault(c => c.Name == "VCALENDAR");
        var master = vcal == null ? null : Master(vcal);
        if (vcal == null || master == null) return 0;
        var org = Mailto(master.Get("ORGANIZER"));
        var account = org == null ? null : space.Settings.MailAccounts.FirstOrDefault(a => a.Email.Equals(org, StringComparison.OrdinalIgnoreCase));
        if (account == null) return 0;
        var mine = MyAddresses(space);
        var attendees = master.All("ATTENDEE").Where(a => Mailto(a) is { } m && !mine.Contains(m.ToLowerInvariant())
            && (only == null || only.Contains(m.ToLowerInvariant()))).ToList();
        if (only != null)
            foreach (var x in only.Where(x => !attendees.Any(a => Mailto(a)!.Equals(x, StringComparison.OrdinalIgnoreCase))))
                attendees.Add(new Prop("ATTENDEE", "mailto:" + x));
        if (attendees.Count == 0) return 0;

        var cancel = method == "CANCEL";
        var cal = Ics.Parse(vcal.ToString()).First(c => c.Name == "VCALENDAR");
        cal.Set("METHOD", method);
        foreach (var e in cal.Of("VEVENT"))
        {
            e.Set("DTSTAMP", Ics.Stamp());
            e.Children.RemoveAll(c => c.Name == "VALARM");
            if (!cancel) continue;
            e.Set("STATUS", "CANCELLED");
            if (only != null)
            {
                e.Remove("ATTENDEE");
                e.Props.AddRange(attendees);
            }
        }

        var en = account.Language == "en";
        var seq = Seq(master);
        var title = master.Text("SUMMARY") ?? "";
        var prefix = cancel ? (en ? "Cancelled: " : "Abgesagt: ") : seq > 0 ? (en ? "Updated: " : "Aktualisiert: ") : (en ? "Invitation: " : "Einladung: ");
        var start = Ics.Time(master.Get("DTSTART"), space.Zone, out var allDay);
        var culture = System.Globalization.CultureInfo.GetCultureInfo(en ? "en-GB" : "de-DE");
        var when = start is DateTime s
            ? allDay ? s.ToString(en ? "dddd, d MMMM yyyy" : "dddd, d. MMMM yyyy", culture)
                     : TimeZoneInfo.ConvertTimeFromUtc(s, space.Zone).ToString(en ? "dddd, d MMMM yyyy, HH:mm" : "dddd, d. MMMM yyyy, HH:mm 'Uhr'", culture)
            : "";
        var text = new StringBuilder();
        text.AppendLine(cancel ? (en ? "This event has been cancelled." : "Dieser Termin wurde abgesagt.") : title);
        if (cancel) text.AppendLine(title);
        if (when.Length > 0) text.AppendLine((en ? "When: " : "Wann: ") + when);
        if (master.Text("LOCATION") is { Length: > 0 } loc) text.AppendLine((en ? "Where: " : "Wo: ") + loc);
        if (!cancel && master.Text("DESCRIPTION") is { Length: > 0 } desc) text.AppendLine().AppendLine(desc);

        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(string.IsNullOrEmpty(account.DisplayName) ? null : account.DisplayName, account.Email));
        foreach (var a in attendees) m.To.Add(new MailboxAddress(a.Param("CN"), Mailto(a)!));
        m.Subject = prefix + title;
        var body = new TextPart("plain") { Text = text.ToString() };
        var calPart = new TextPart("calendar") { Text = cal.ToString() };
        calPart.ContentType.Parameters.Add("method", method);
        calPart.ContentType.Charset = "utf-8";
        m.Body = new MultipartAlternative { body, calPart };

        var draft = new MailCompose.Draft { To = string.Join(", ", m.To.Mailboxes.Select(x => x.Address)), Subject = m.Subject };
        MailQueue.Add(space.Db, account.Id, account.Id, draft, m, DateTime.UtcNow);
        return attendees.Count;
    }

    /// <summary>Antwort an den Organisator: METHOD:REPLY mit der eigenen Teilnahme (RFC 5546 3.2.3).</summary>
    private static MimeMessage Reply(UserSpace space, MailAccount account, Component vcal, Component ev, string response)
    {
        var partstat = response switch { "tentative" => "TENTATIVE", "decline" => "DECLINED", _ => "ACCEPTED" };
        var en = account.Language == "en";
        var (prefix, text) = partstat switch
        {
            "TENTATIVE" => en ? ("Tentative: ", "has tentatively accepted") : ("Mit Vorbehalt: ", "hat mit Vorbehalt zugesagt"),
            "DECLINED" => en ? ("Declined: ", "has declined") : ("Abgesagt: ", "hat abgesagt"),
            _ => en ? ("Accepted: ", "has accepted") : ("Zugesagt: ", "hat zugesagt"),
        };

        var cal = new Component("VCALENDAR");
        cal.Set("VERSION", "2.0");
        cal.Set("PRODID", "-//dmOffice//dmOffice//DE");
        cal.Set("METHOD", "REPLY");
        foreach (var tz in vcal.Of("VTIMEZONE")) cal.Children.Add(tz);
        var r = new Component("VEVENT");
        foreach (var key in new[] { "UID", "SEQUENCE", "DTSTART", "DTEND", "DURATION", "RECURRENCE-ID", "SUMMARY", "ORGANIZER" })
            if (ev.Get(key) is { } p) r.Props.Add(p);
        r.Set("DTSTAMP", Ics.Stamp());
        var me = MyAttendee(space, ev) is { } a ? new Prop("ATTENDEE", a.Value) : new Prop("ATTENDEE", "mailto:" + account.Email);
        if (!string.IsNullOrEmpty(account.DisplayName)) me.With("CN", account.DisplayName);
        me.With("PARTSTAT", partstat);
        r.Props.Add(me);
        cal.Children.Add(r);

        var name = string.IsNullOrEmpty(account.DisplayName) ? account.Email : account.DisplayName;
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(string.IsNullOrEmpty(account.DisplayName) ? null : account.DisplayName, account.Email));
        m.To.Add(MailboxAddress.Parse(Mailto(ev.Get("ORGANIZER"))!));
        m.Subject = prefix + (ev.Text("SUMMARY") ?? "");
        var body = new TextPart("plain") { Text = $"{name} {text}." };
        var calPart = new TextPart("calendar") { Text = cal.ToString() };
        calPart.ContentType.Parameters.Add("method", "REPLY");
        calPart.ContentType.Charset = "utf-8";
        m.Body = new MultipartAlternative { body, calPart };
        return m;
    }
}
