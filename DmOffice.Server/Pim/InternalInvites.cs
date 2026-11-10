using System.Globalization;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Pim;

/// <summary>
/// Termineinladungen innerhalb von dmOffice (Version 2): Teilnehmer, die Benutzer dieses Servers sind, bekommen die Einladung
/// von Pukki im Chat mit Knöpfen statt per Mail. Zusagen trägt den Termin in ihren Kalender ein und vermerkt die Antwort sofort
/// beim Organisator; Änderungen, Ausladen und Absagen des Organisators ziehen ihre Kopie mit.
/// Erkannt wird ein Benutzer nur an seiner internen Adresse login@dmoffice.intern: Die Adresse eines Mailkontos trägt jeder selbst
/// ein, sie weist nicht nach, dass sie ihm gehört. Einladungen an Mailadressen gehen darum per Mail hinaus.
/// </summary>
public static class InternalInvites
{
    public const string Domain = "dmoffice.intern";
    public const string Accept = "inv-accept", Tentative = "inv-tentative", Decline = "inv-decline";

    /// <summary>Für Tests: Benutzer dieses Servers wie Fremde per Mail einladen (Abläufe zwischen zwei Servern nachstellen).</summary>
    internal static bool ViaMail;

    /// <summary>Merkmal der Kopie beim Teilnehmer: Kennung des Organisators.</summary>
    private const string InvitedBy = "X-DM-INVITED-BY";

    /// <summary>Was ein Knopf braucht: wessen Termin, wo, welche UID.</summary>
    private sealed record Target(string Org, long Cal, string Uri, string Uid);

    public static string InternalAddress(UserRecord user) => user.Login.ToLowerInvariant() + "@" + Domain;

    /// <summary>Interne Adresse -> Benutzer.</summary>
    internal static Dictionary<string, string> Directory(IServiceProvider services) =>
        services.GetRequiredService<UserStore>().All().ToDictionary(InternalAddress, u => u.Id);

    private static HashSet<string> AddressesOf(UserRecord u) => [InternalAddress(u)];

    /// <summary>Teilnehmer aufteilen: Benutzer dieses dmOffice (Adresse -> Kennung) und alle anderen (per Mail).</summary>
    public static (Dictionary<string, string> Inside, List<string> Outside) Split(IServiceProvider services, string me, IEnumerable<string> addresses)
    {
        if (ViaMail) return (new(), addresses.Select(a => a.ToLowerInvariant()).Distinct().ToList());
        var dir = Directory(services);
        var inside = new Dictionary<string, string>();
        var outside = new List<string>();
        foreach (var a in addresses.Select(a => a.ToLowerInvariant()).Distinct())
            if (dir.TryGetValue(a, out var user)) { if (user != me) inside[a] = user; }
            else if (!a.EndsWith("@" + Domain)) outside.Add(a);
        return (inside, outside);
    }

    /// <summary>Benutzer dieses dmOffice zum Einladen (ohne einen selbst), passend zu Name oder Anmeldename.</summary>
    public static List<ContactStore.Suggestion> Suggest(IServiceProvider services, string me, string q)
    {
        q = q.Trim();
        if (q.Length < 2) return [];
        return services.GetRequiredService<UserStore>().All()
            .Where(u => u.Id != me && (u.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase) || u.Login.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(u => u.DisplayName).Take(6)
            .Select(u => new ContactStore.Suggestion(u.DisplayName, InternalAddress(u), "people")).ToList();
    }

    // ---------------- Organisator ----------------

    /// <summary>
    /// Nach dem Speichern eines eigenen Termins: neue interne Teilnehmer einladen, Ausgeladenen den Termin herausnehmen und
    /// bei Änderungen, die sie angehen, die Kopien mitziehen. Zurück: wie viele eine Einladung bekamen.
    /// </summary>
    public static int Saved(IServiceProvider services, UserSpace space, string? oldIcs, string newIcs, long calendar, string uri)
    {
        var before = Split(services, space.User.Id, Mail.Invites.Attendees(space, oldIcs)).Inside;
        var now = Split(services, space.User.Id, Mail.Invites.Attendees(space, newIcs)).Inside;
        var changed = Mail.Invites.Changed(oldIcs, newIcs);
        var vcal = Ics.Parse(newIcs).First(c => c.Name == "VCALENDAR");
        var master = Master(vcal);
        var uid = master.Text("UID") ?? "";
        var title = Title(master);
        var org = space.User.DisplayName;
        int invited = 0;
        foreach (var user in before.Values.Distinct().Except(now.Values))
        {
            var gone = RemoveCopy(services, user, uid, space.User.Id);
            Say(services, user, $"{org} hat dich aus „{title}“ ausgeladen. " + (gone ? "Der Termin ist aus deinem Kalender heraus." : StillThere));
        }
        foreach (var user in now.Values.Distinct())
        {
            var isNew = !before.ContainsValue(user);
            if (!isNew && !changed) continue;
            var target = new Target(space.User.Id, calendar, uri, uid);
            if (!isNew && UpdateCopy(services, user, newIcs, space.User.Id))
            {
                Say(services, user, $"{org} hat „{title}“ geändert: {When(services, user, master)}. Dein Kalender ist schon angepasst.", target, buttons: false);
                continue;
            }
            Say(services, user, isNew
                ? $"{org} lädt dich ein: „{title}“ – {When(services, user, master)}{Where(master)}."
                : $"{org} hat die Einladung zu „{title}“ geändert: {When(services, user, master)}{Where(master)}.", target, buttons: true);
            invited++;
        }
        return invited;
    }

    /// <summary>Eigener Termin gelöscht (ganz oder ein Termin der Serie): bei den internen Teilnehmern ebenso.</summary>
    public static int Deleted(IServiceProvider services, UserSpace space, string ics, string? instance)
    {
        var inside = Split(services, space.User.Id, Mail.Invites.Attendees(space, ics)).Inside.Values.Distinct().ToList();
        var master = Master(Ics.Parse(ics).First(c => c.Name == "VCALENDAR"));
        var uid = master.Text("UID") ?? "";
        var title = Title(master);
        foreach (var user in inside)
        {
            if (instance == null)
            {
                var gone = RemoveCopy(services, user, uid, space.User.Id);
                Say(services, user, $"{space.User.DisplayName} hat „{title}“ abgesagt. " + (gone ? "Der Termin ist aus deinem Kalender heraus." : StillThere));
            }
            else
            {
                var spaces = services.GetRequiredService<Spaces>();
                var them = spaces.Get(user);
                if (Copy(services, them, uid, space.User.Id) is { } c)
                    try { CalendarModel.DeleteEvent(them, c.Coll.Id, c.Obj.Uri, instance); } catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { }
                var day = CalendarModel.ParseIso(instance, out var allDay);
                var local = allDay ? day : TimeZoneInfo.ConvertTimeFromUtc(day, them.Zone);
                Say(services, user, $"{space.User.DisplayName} hat „{title}“ am {local.ToString("dddd, d. MMMM", De)} abgesagt. Die anderen Termine der Serie bleiben.");
            }
        }
        return inside.Count;
    }

    // ---------------- Eingeladene ----------------

    /// <summary>Knopf unter Pukkis Einladung: zusagen, mit Vorbehalt, absagen. Danach bleiben nur die anderen beiden stehen - wer es sich anders überlegt.</summary>
    public static void Respond(IServiceProvider services, UserSpace me, ChatMessage m, string key)
    {
        var chat = services.GetRequiredService<ChatStore>();
        var spaces = services.GetRequiredService<Spaces>();
        var users = services.GetRequiredService<UserStore>();
        if (m.Actions?.FirstOrDefault(a => a.Key == key)?.Data is not { } data || JsonSerializer.Deserialize<Target>(data) is not { } t)
            throw new InvalidOperationException("Das ist schon erledigt.");
        var orgUser = users.ById(t.Org);
        var org = orgUser == null ? null : spaces.Get(t.Org);
        var coll = org == null ? null : PimStore.Find(org.Db, t.Cal);
        var obj = coll == null ? null : PimStore.Object(org!.Db, coll.Id, t.Uri);
        if (obj == null || coll == null || org == null || Ics.Parse(obj.Data).FirstOrDefault(c => c.Name == "VCALENDAR") is not { } vcal
            || Master(vcal).Text("UID") != t.Uid)
        {
            chat.SetActions(m.Id, null);
            chat.Post(Pukki.Id, m.Room, "Diesen Termin gibt es nicht mehr.");
            return;
        }
        var mine = AddressesOf(me.User);
        if (!vcal.Of("VEVENT").SelectMany(e => e.All("ATTENDEE")).Any(a => Mail.Invites.Mailto(a)?.ToLowerInvariant() is { } x && mine.Contains(x)))
        {
            chat.SetActions(m.Id, null);
            chat.Post(Pukki.Id, m.Room, "Zu diesem Termin bist du nicht mehr eingeladen.");
            return;
        }
        var partstat = key switch { Accept => "ACCEPTED", Tentative => "TENTATIVE", _ => "DECLINED" };

        // Eigener Kalender: eintragen bzw. herausnehmen
        string? where = null;
        if (key == Decline) RemoveCopy(services, me.User.Id, t.Uid, t.Org);
        else if (Copy(services, me, t.Uid, t.Org) is { } c)
        {
            PimStore.Put(me.Db, c.Coll, c.Obj.Uri, CopyFor(obj.Data, mine, partstat, c.Obj.Data, t.Org), zone: me.Zone);
            where = c.Coll.Name;
        }
        else
        {
            var target = Mail.Invites.DefaultCalendar(me);
            // Dieselbe Kennung bei einem Termin, der nicht aus dieser Einladung stammt: Der bleibt, wie er ist
            if (PimStore.ByUid(me.Db, target.Id, t.Uid) != null)
            {
                chat.SetActions(m.Id, null);
                chat.Post(Pukki.Id, m.Room, "Das geht nicht: In deinem Kalender gibt es schon einen anderen Termin mit derselben Kennung.");
                return;
            }
            PimStore.Put(me.Db, target, PimStore.NewUri(".ics"), CopyFor(obj.Data, mine, partstat, null, t.Org), zone: me.Zone);
            where = target.Name;
        }

        // Beim Organisator sofort vermerken - kein Klick in einer Mail nötig
        foreach (var e in vcal.Of("VEVENT"))
            foreach (var a in e.All("ATTENDEE").Where(a => Mail.Invites.Mailto(a)?.ToLowerInvariant() is { } x && mine.Contains(x)))
            {
                a.With("PARTSTAT", partstat);
                a.Params.RemoveAll(p => p.Key.Equals("RSVP", StringComparison.OrdinalIgnoreCase));
            }
        PimStore.Put(org.Db, coll, obj.Uri, vcal.ToString(), zone: org.Zone);
        // Die gewählte Antwort ist erledigt - übrig bleiben die anderen zum Umentscheiden
        chat.SetActions(m.Id, m.Actions!.Where(a => a.Key != key).ToList());

        var title = Title(Master(vcal));
        var orgName = orgUser!.DisplayName;
        chat.Post(Pukki.Id, m.Room, key switch
        {
            Accept => $"Zugesagt – „{title}“ steht in deinem Kalender „{where}“. {orgName} weiß Bescheid.",
            Tentative => $"Mit Vorbehalt zugesagt – „{title}“ steht in deinem Kalender „{where}“. {orgName} weiß Bescheid.",
            _ => $"Abgesagt – {orgName} weiß Bescheid.",
        });
        var who = me.User.DisplayName;
        Say(services, t.Org, key switch
        {
            Accept => $"{who} hat zugesagt: „{title}“.",
            Tentative => $"{who} hat mit Vorbehalt zugesagt: „{title}“.",
            _ => $"{who} hat abgesagt: „{title}“.",
        });
    }

    // ---------------- Hilfen ----------------

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static Component Master(Component vcal) =>
        vcal.Of("VEVENT").FirstOrDefault(e => e.Get("RECURRENCE-ID") == null) ?? vcal.Of("VEVENT").First();

    private static string Title(Component e) => string.IsNullOrWhiteSpace(e.Text("SUMMARY")) ? "Termin" : e.Text("SUMMARY")!;

    /// <summary>Wann, in der Zeitzone des Empfängers: „Montag, 4. März 2030, 10:00–11:00 Uhr“, bei Serien mit „ab“.</summary>
    private static string When(IServiceProvider services, string user, Component e)
    {
        var zone = services.GetRequiredService<Spaces>().Get(user).Zone;
        if (Ics.Time(e.Get("DTSTART"), zone, out var allDay) is not DateTime s) return "";
        var end = Ics.Time(e.Get("DTEND"), zone, out _);
        if (end == null && e.Text("DURATION") is { } dur && PimStore.TryDuration(dur, out var d)) end = s + d;
        var series = e.Get("RRULE") != null ? "ab " : "";
        if (allDay)
        {
            var last = (end ?? s.AddDays(1)).AddDays(-1);
            return series + (last.Date > s.Date ? $"{s.ToString("dddd, d. MMMM", De)} bis {last.ToString("dddd, d. MMMM yyyy", De)}" : s.ToString("dddd, d. MMMM yyyy", De))
                + (series.Length > 0 ? " (Serie)" : "");
        }
        var ls = TimeZoneInfo.ConvertTimeFromUtc(s, zone);
        var text = ls.ToString("dddd, d. MMMM yyyy, HH:mm", De);
        if (end is DateTime en) text += (TimeZoneInfo.ConvertTimeFromUtc(en, zone) is var le && le.Date == ls.Date ? "–" + le.ToString("HH:mm", De) : "");
        return series + text + " Uhr" + (series.Length > 0 ? " (Serie)" : "");
    }

    private static string Where(Component e) => e.Text("LOCATION") is { Length: > 0 } l ? ", " + l : "";

    /// <summary>Wo die Kopie aus der Einladung dieses Organisators steht. Andere Termine mit derselben UID gehen die Einladung nichts an.</summary>
    private static (Collection Coll, DavObject Obj)? Copy(IServiceProvider services, UserSpace space, string uid, string org)
    {
        foreach (var c in PimStore.Collections(space.Db, PimStore.Calendar))
            if (PimStore.ByUid(space.Db, c.Id, uid) is { } o && IsCopyFrom(services, space, o.Data, org)) return (c, o);
        return null;
    }

    /// <summary>
    /// Stammt der Termin aus einer Einladung dieses Organisators? Kopien tragen ihn im Merkmal <see cref="InvitedBy"/>, ältere Kopien
    /// am ORGANIZER mit seiner internen Adresse. Alles andere bleibt unberührt, im Zweifel löscht der Teilnehmer den Termin selbst.
    /// </summary>
    private static bool IsCopyFrom(IServiceProvider services, UserSpace space, string ics, string org)
    {
        Component master;
        try { master = Master(Ics.Parse(ics).First(c => c.Name == "VCALENDAR")); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException) { return false; }
        if (master.Text(InvitedBy) is { } by) return by == org;
        return Mail.Invites.Mailto(master.Get("ORGANIZER"))?.ToLowerInvariant() is { } address
            && services.GetRequiredService<UserStore>().ById(org) is { } user && InternalAddress(user) == address;
    }

    /// <summary>false: keine Kopie aus dieser Einladung gefunden.</summary>
    private static bool RemoveCopy(IServiceProvider services, string user, string uid, string org)
    {
        var space = services.GetRequiredService<Spaces>().Get(user);
        if (Copy(services, space, uid, org) is not { } c) return false;
        PimStore.Delete(space.Db, c.Coll, c.Obj.Uri);
        return true;
    }

    private const string StillThere = "Steht der Termin noch in deinem Kalender, kannst du ihn dort löschen.";

    /// <summary>Kopie beim Teilnehmer auf den neuen Stand bringen (Antwort und Erinnerungen bleiben); false: hat keine.</summary>
    private static bool UpdateCopy(IServiceProvider services, string user, string ics, string org)
    {
        var spaces = services.GetRequiredService<Spaces>();
        var space = spaces.Get(user);
        var uid = Master(Ics.Parse(ics).First(c => c.Name == "VCALENDAR")).Text("UID") ?? "";
        if (Copy(services, space, uid, org) is not { } c) return false;
        var mine = AddressesOf(space.User);
        var old = Ics.Parse(c.Obj.Data).First(x => x.Name == "VCALENDAR");
        var status = old.Of("VEVENT").SelectMany(e => e.All("ATTENDEE"))
            .FirstOrDefault(a => Mail.Invites.Mailto(a)?.ToLowerInvariant() is { } x && mine.Contains(x))?.Param("PARTSTAT") ?? "ACCEPTED";
        PimStore.Put(space.Db, c.Coll, c.Obj.Uri, CopyFor(ics, mine, status, c.Obj.Data, org), zone: space.Zone);
        return true;
    }

    /// <summary>Die Kopie für einen Teilnehmer: Termin des Organisators mit seiner Antwort, seine Erinnerungen statt der fremden.</summary>
    private static string CopyFor(string ics, HashSet<string> mine, string partstat, string? previous, string org)
    {
        var vcal = Ics.Parse(ics).First(c => c.Name == "VCALENDAR");
        vcal.Remove("METHOD");
        var alarms = previous == null ? [] : Master(Ics.Parse(previous).First(c => c.Name == "VCALENDAR")).Of("VALARM").ToList();
        foreach (var e in vcal.Of("VEVENT"))
        {
            e.Children.RemoveAll(c => c.Name == "VALARM");
            foreach (var a in e.All("ATTENDEE").Where(a => Mail.Invites.Mailto(a)?.ToLowerInvariant() is { } x && mine.Contains(x)))
            {
                a.With("PARTSTAT", partstat);
                a.Params.RemoveAll(p => p.Key.Equals("RSVP", StringComparison.OrdinalIgnoreCase));
            }
            if (partstat == "TENTATIVE") e.Set("STATUS", "TENTATIVE"); else e.Remove("STATUS");
            e.SetText(InvitedBy, org);
        }
        Master(vcal).Children.AddRange(alarms);
        return vcal.ToString();
    }

    /// <summary>Pukki schreibt dem Benutzer - mit Knöpfen zum Antworten und dem Termin zum Antippen.</summary>
    private static void Say(IServiceProvider services, string to, string text, Target? target = null, bool buttons = false)
    {
        var chat = services.GetRequiredService<ChatStore>();
        var users = services.GetRequiredService<UserStore>();
        if (users.ById(to) == null) return;
        Pukki.Ensure(chat, to);
        var room = chat.DirectRoom(to, Pukki.Id) ?? chat.OpenDirect(to, Pukki.Id);
        List<ChatRef>? refs = null;
        List<ChatAction>? actions = null;
        if (target != null)
        {
            refs = [new ChatRef(ChatRef.TypeEvent, target.Org, "Termin", Mode: ChatRef.Show, Collection: target.Cal, Item: target.Uri)];
            if (buttons)
            {
                var data = JsonSerializer.Serialize(target);
                actions = [new ChatAction(Accept, "Zusagen", data), new ChatAction(Tentative, "Mit Vorbehalt", data), new ChatAction(Decline, "Absagen", data)];
            }
        }
        chat.Post(Pukki.Id, room, text, refs: refs, actions: actions);
    }
}
