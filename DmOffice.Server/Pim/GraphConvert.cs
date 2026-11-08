using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Dav;

namespace DmOffice.Pim;

/// <summary>
/// Umwandeln zwischen Microsoft Graph (JSON) und iCalendar/vCard - in beide Richtungen. Was eine Seite nicht kennt,
/// faellt weg: Graph kennt keine eigenen X-Eigenschaften, iCalendar keine Outlook-Kategorienfarben.
/// Zeiten fragt dmOffice bei Graph in UTC ab (Prefer: outlook.timezone="UTC"); die Zeitzone eines Termins steht
/// in originalStartTimeZone (Windows-Name) und wird zur IANA-Kennung (Europe/Berlin).
/// </summary>
public static class GraphConvert
{
    // ---------------- Hilfen ----------------

    private static string? S(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? O(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static IEnumerable<JsonElement> A(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static bool B(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? N(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    /// <summary>Kennung des Stands bei Microsoft: changeKey (Termine, Kontakte), sonst @odata.etag (Aufgaben).</summary>
    public static string? ChangeKey(JsonElement e) => S(e, "changeKey") ?? S(e, "@odata.etag") ?? S(e, "lastModifiedDateTime");

    public static DateTime? Modified(JsonElement e) =>
        S(e, "lastModifiedDateTime") is { } m && DateTimeOffset.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    /// <summary>Windows-Zeitzone (Outlook) oder IANA-Kennung zu einer Zone; null, wenn unbekannt.</summary>
    public static TimeZoneInfo? ZoneOf(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "tzone://Microsoft/Utc" or "UTC" or "Etc/UTC" or "Coordinated Universal Time") return id == null ? null : TimeZoneInfo.Utc;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) && Ics.Zone(iana) is { } z) return z;
        return Ics.Zone(id);
    }

    /// <summary>Kennung fuer iCalendar (TZID): IANA.</summary>
    public static string IanaId(TimeZoneInfo tz) =>
        tz.HasIanaId ? tz.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : tz.Id;

    /// <summary>Kennung fuer Graph: Windows-Name, den Outlook ueberall versteht.</summary>
    public static string WindowsId(TimeZoneInfo tz)
    {
        if (tz == TimeZoneInfo.Utc || tz.Id is "UTC" or "Etc/UTC") return "UTC";
        if (!tz.HasIanaId) return tz.Id;
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(tz.Id, out var win) ? win : "UTC";
    }

    /// <summary>dateTimeTimeZone von Graph als UTC.</summary>
    public static DateTime? Utc(JsonElement? dt)
    {
        if (dt is not { } d || S(d, "dateTime") is not { } s) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        if (s.EndsWith('Z')) return DateTime.SpecifyKind(local.ToUniversalTime(), DateTimeKind.Utc);
        var tz = ZoneOf(S(d, "timeZone")) ?? TimeZoneInfo.Utc;
        return Ics.ToUtc(local, tz);
    }

    /// <summary>Nur das Datum aus dateTimeTimeZone (ganztaegig): so, wie es dasteht.</summary>
    private static DateTime? DateOf(JsonElement? dt) =>
        dt is { } d && S(d, "dateTime") is { Length: >= 10 } s && DateTime.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day : null;

    private static JsonObject DateTimeTz(DateTime local, TimeZoneInfo tz) => new()
    {
        ["dateTime"] = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        ["timeZone"] = WindowsId(tz),
    };

    private static string Body(JsonElement e)
    {
        if (O(e, "body") is not { } b || S(b, "content") is not { } content) return "";
        var text = string.Equals(S(b, "contentType"), "html", StringComparison.OrdinalIgnoreCase) ? DmOffice.Mail.MailHtml.ToText(content) : content;
        return text.Replace("\r\n", "\n").Trim();
    }

    private static JsonObject TextBody(string? text) => new() { ["contentType"] = "text", ["content"] = text ?? "" };

    private static List<string> Categories(Component c) =>
        c.All("CATEGORIES").SelectMany(p => p.Value.Split(',')).Select(Ics.Unescape).Where(x => x.Length > 0).ToList();

    private static void SetCategories(Component c, JsonElement e)
    {
        var cats = A(e, "categories").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList();
        c.Remove("CATEGORIES");
        if (cats.Count > 0) c.Set("CATEGORIES", string.Join(",", cats.Select(Ics.Escape)));
    }

    /// <summary>VCALENDAR mit dem ersten Eintrag dieser Art aus dem bisherigen Stand, sonst neu mit frischer UID.</summary>
    private static (Component Cal, Component Item) Open(string? existing, string name)
    {
        if (existing != null && Ics.Parse(existing).FirstOrDefault(r => r.Name == "VCALENDAR") is { } cal && cal.Of(name).FirstOrDefault() is { } item)
            return (cal, item);
        var vcal = CalendarModel.NewCalendar();
        var c = new Component(name);
        c.Set("UID", Guid.NewGuid().ToString("D"));
        vcal.Children.Add(c);
        return (vcal, c);
    }

    private static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());

    // ---------------- Wiederholung ----------------

    private static readonly string[] Days = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
    private static string DayCode(string day) => day.Length >= 2 ? day[..2].ToUpperInvariant() : "MO";
    private static string DayName(string code) => Days.FirstOrDefault(d => d.StartsWith(code.ToLowerInvariant())) ?? "monday";
    private static readonly string[] Index = ["first", "second", "third", "fourth", "last"];
    private static int IndexNumber(string? index) => index switch { "second" => 2, "third" => 3, "fourth" => 4, "last" => -1, _ => 1 };
    private static string IndexName(int n) => n switch { 2 => "second", 3 => "third", 4 => "fourth", < 0 => "last", _ => "first" };

    /// <summary>patternedRecurrence zu RRULE. untilLocal: Ende der Serie als Ortszeit-Tag in der Zone des Termins.</summary>
    public static string? RRule(JsonElement? recurrence, TimeZoneInfo zone, bool allDay)
    {
        if (recurrence is not { } r || O(r, "pattern") is not { } p) return null;
        var type = S(p, "type");
        var parts = new List<string>();
        var interval = Math.Max(1, N(p, "interval") ?? 1);
        var days = A(p, "daysOfWeek").Select(d => d.GetString() ?? "").Where(d => d.Length > 0).Select(DayCode).ToList();
        var index = IndexNumber(S(p, "index"));
        switch (type)
        {
            case "daily": parts.Add("FREQ=DAILY"); break;
            case "weekly":
                parts.Add("FREQ=WEEKLY");
                if (days.Count > 0) parts.Add("BYDAY=" + string.Join(",", days));
                break;
            case "absoluteMonthly": parts.Add("FREQ=MONTHLY"); parts.Add("BYMONTHDAY=" + (N(p, "dayOfMonth") ?? 1)); break;
            case "relativeMonthly":
                parts.Add("FREQ=MONTHLY");
                parts.Add(days.Count == 1 ? $"BYDAY={index}{days[0]}" : $"BYDAY={string.Join(",", days)};BYSETPOS={index}");
                break;
            case "absoluteYearly":
                parts.Add("FREQ=YEARLY"); parts.Add("BYMONTH=" + (N(p, "month") ?? 1)); parts.Add("BYMONTHDAY=" + (N(p, "dayOfMonth") ?? 1));
                break;
            case "relativeYearly":
                parts.Add("FREQ=YEARLY"); parts.Add("BYMONTH=" + (N(p, "month") ?? 1));
                parts.Add(days.Count == 1 ? $"BYDAY={index}{days[0]}" : $"BYDAY={string.Join(",", days)};BYSETPOS={index}");
                break;
            default: return null;
        }
        if (interval > 1) parts.Insert(1, "INTERVAL=" + interval);
        if (type == "weekly" && S(p, "firstDayOfWeek") is { } fd && fd != "monday") parts.Add("WKST=" + DayCode(fd));
        if (O(r, "range") is { } range)
        {
            switch (S(range, "type"))
            {
                case "endDate" when S(range, "endDate") is { } end && DateTime.TryParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDay):
                    parts.Add("UNTIL=" + (allDay ? Ics.Date(endDay) : Ics.Utc(Ics.ToUtc(endDay.AddDays(1).AddSeconds(-1), zone))));
                    break;
                case "numbered" when N(range, "numberOfOccurrences") is int count and > 0:
                    parts.Add("COUNT=" + count);
                    break;
            }
        }
        return string.Join(";", parts);
    }

    /// <summary>RRULE zu patternedRecurrence. startLocal: erster Termin in Ortszeit (startDate der Serie).</summary>
    public static JsonObject? Recurrence(string? rrule, DateTime startLocal, TimeZoneInfo zone)
    {
        if (string.IsNullOrEmpty(rrule)) return null;
        var parts = rrule.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].ToUpperInvariant());
        if (!parts.TryGetValue("FREQ", out var freq)) return null;
        var pattern = new JsonObject { ["interval"] = parts.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out var n) ? Math.Max(1, n) : 1 };
        var byDay = parts.TryGetValue("BYDAY", out var bd) ? bd.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() : [];
        int? setPos = parts.TryGetValue("BYSETPOS", out var sp) && int.TryParse(sp, out var spn) ? spn : null;
        int? monthDay = parts.TryGetValue("BYMONTHDAY", out var md) && int.TryParse(md.Split(',')[0], out var mdn) ? mdn : null;
        int? month = parts.TryGetValue("BYMONTH", out var bm) && int.TryParse(bm.Split(',')[0], out var bmn) ? bmn : null;

        // "+2MO", "-1FR" -> (2, MO)
        (int? Pos, string Day) SplitDay(string d)
        {
            var i = d.Length - 2;
            return (i > 0 && int.TryParse(d[..i], out var pos) ? pos : null, d[i..]);
        }
        var split = byDay.Select(SplitDay).ToList();
        JsonArray DaysOf(IEnumerable<string> codes) => Array(codes.Select(DayName));

        switch (freq)
        {
            case "DAILY": pattern["type"] = "daily"; break;
            case "WEEKLY":
                pattern["type"] = "weekly";
                pattern["daysOfWeek"] = DaysOf(split.Count > 0 ? split.Select(s => s.Day) : [startLocal.DayOfWeek.ToString()[..2].ToUpperInvariant()]);
                pattern["firstDayOfWeek"] = parts.TryGetValue("WKST", out var wk) ? DayName(wk) : "monday";
                break;
            case "MONTHLY" when split.Count > 0:
                pattern["type"] = "relativeMonthly";
                pattern["daysOfWeek"] = DaysOf(split.Select(s => s.Day));
                pattern["index"] = IndexName(split[0].Pos ?? setPos ?? 1);
                break;
            case "MONTHLY":
                pattern["type"] = "absoluteMonthly";
                pattern["dayOfMonth"] = monthDay ?? startLocal.Day;
                break;
            case "YEARLY" when split.Count > 0:
                pattern["type"] = "relativeYearly";
                pattern["daysOfWeek"] = DaysOf(split.Select(s => s.Day));
                pattern["index"] = IndexName(split[0].Pos ?? setPos ?? 1);
                pattern["month"] = month ?? startLocal.Month;
                break;
            case "YEARLY":
                pattern["type"] = "absoluteYearly";
                pattern["dayOfMonth"] = monthDay ?? startLocal.Day;
                pattern["month"] = month ?? startLocal.Month;
                break;
            default: return null;
        }
        var range = new JsonObject { ["startDate"] = startLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["recurrenceTimeZone"] = WindowsId(zone) };
        if (parts.TryGetValue("COUNT", out var c) && int.TryParse(c, out var count))
        {
            range["type"] = "numbered";
            range["numberOfOccurrences"] = count;
        }
        else if (parts.TryGetValue("UNTIL", out var until) && Ics.Time(new Prop("UNTIL", until), zone, out var untilDate) is DateTime u)
        {
            range["type"] = "endDate";
            var day = untilDate ? u : TimeZoneInfo.ConvertTimeFromUtc(u, zone);
            range["endDate"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else range["type"] = "noEnd";
        return new JsonObject { ["pattern"] = pattern, ["range"] = range };
    }

    // ---------------- Aufgaben ----------------

    /// <summary>todoTask zu einer .ics-Datei mit VTODO. existing: bisheriger Stand (UID und Unbekanntes bleiben).</summary>
    public static string TaskToIcs(JsonElement t, string? existing, TimeZoneInfo zone)
    {
        var (vcal, todo) = Open(existing, "VTODO");
        todo.SetText("SUMMARY", S(t, "title") ?? "");
        todo.SetText("DESCRIPTION", Body(t) is { Length: > 0 } body ? body : null);
        // Faellig ist bei To Do ein Tag (Mitternacht in der Zone des Kontos, oft als UTC geliefert)
        if (Utc(O(t, "dueDateTime")) is DateTime due)
            todo.Set("DUE", Ics.Date(DueDay(O(t, "dueDateTime")!.Value, due, zone))).With("VALUE", "DATE");
        else todo.Remove("DUE");
        var status = S(t, "status");
        if (status == "completed")
        {
            todo.Set("STATUS", "COMPLETED");
            todo.Set("PERCENT-COMPLETE", "100");
            todo.Set("COMPLETED", Ics.Utc(Utc(O(t, "completedDateTime")) ?? Modified(t) ?? DateTime.UtcNow));
        }
        else
        {
            todo.Remove("COMPLETED");
            todo.Remove("PERCENT-COMPLETE");
            todo.Set("STATUS", status == "inProgress" ? "IN-PROCESS" : "NEEDS-ACTION");
        }
        switch (S(t, "importance"))
        {
            case "high": todo.Set("PRIORITY", "1"); break;
            case "low": todo.Set("PRIORITY", "9"); break;
            default: todo.Remove("PRIORITY"); break;
        }
        todo.Remove("RRULE");
        if (RRule(O(t, "recurrence"), zone, true) is { } rr) todo.Set("RRULE", rr);
        todo.Children.RemoveAll(c => c.Name == "VALARM");
        if (B(t, "isReminderOn") && Utc(O(t, "reminderDateTime")) is DateTime remind)
        {
            var alarm = new Component("VALARM");
            alarm.Set("ACTION", "DISPLAY");
            alarm.SetText("DESCRIPTION", S(t, "title") ?? "Erinnerung");
            alarm.Set("TRIGGER", Ics.Utc(remind)).With("VALUE", "DATE-TIME");
            todo.Children.Add(alarm);
        }
        SetCategories(todo, t);
        if (S(t, "createdDateTime") is { } created && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cr))
            todo.Set("CREATED", Ics.Utc(cr.UtcDateTime));
        Stamp(todo, t);
        return vcal.ToString();
    }

    private static DateTime DueDay(JsonElement dt, DateTime utc, TimeZoneInfo zone)
    {
        // In UTC geliefert: auf den Tag in der Zone des Benutzers zurueckrechnen; sonst steht der Tag schon da
        var tz = S(dt, "timeZone");
        return tz is null or "UTC" or "Etc/UTC" ? TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Date : DateOf(dt) ?? utc.Date;
    }

    /// <summary>VTODO zu todoTask (fuer POST/PATCH).</summary>
    public static JsonObject TaskToGraph(string ics, TimeZoneInfo zone)
    {
        var todo = Ics.Parse(ics).First(r => r.Name == "VCALENDAR").Of("VTODO").First();
        var o = new JsonObject
        {
            ["title"] = todo.Text("SUMMARY") ?? "",
            ["body"] = TextBody(todo.Text("DESCRIPTION")),
            ["categories"] = Array(Categories(todo)),
        };
        var due = Ics.Time(todo.Get("DUE"), zone, out var dueAllDay);
        DateTime? dueLocal = due == null ? null : dueAllDay ? due.Value.Date : TimeZoneInfo.ConvertTimeFromUtc(due.Value, zone).Date;
        o["dueDateTime"] = dueLocal is DateTime d ? DateTimeTz(d, zone) : null;
        var completed = Ics.Time(todo.Get("COMPLETED"), zone, out _);
        var status = todo.Text("STATUS")?.ToUpperInvariant();
        if (completed != null || status == "COMPLETED")
        {
            o["status"] = "completed";
            o["completedDateTime"] = DateTimeTz(TimeZoneInfo.ConvertTimeFromUtc(completed ?? DateTime.UtcNow, zone), zone);
        }
        else o["status"] = status == "IN-PROCESS" ? "inProgress" : "notStarted";
        var prio = int.TryParse(todo.Text("PRIORITY"), out var p) ? p : 0;
        o["importance"] = prio is >= 1 and <= 4 ? "high" : prio >= 6 ? "low" : "normal";
        // Wiederholung braucht bei To Do ein Faelligkeitsdatum
        o["recurrence"] = dueLocal is DateTime start ? Recurrence(todo.Text("RRULE"), start, zone) : null;
        var trigger = todo.Of("VALARM").Select(a => a.Get("TRIGGER")).FirstOrDefault(t => t != null && t.Param("VALUE")?.Equals("DATE-TIME", StringComparison.OrdinalIgnoreCase) == true);
        if (trigger != null && Ics.Time(trigger, zone, out _) is DateTime remind)
        {
            o["isReminderOn"] = true;
            o["reminderDateTime"] = DateTimeTz(TimeZoneInfo.ConvertTimeFromUtc(remind, zone), zone);
        }
        else
        {
            o["isReminderOn"] = false;
            o["reminderDateTime"] = null;
        }
        return o;
    }

    // ---------------- Kontakte ----------------

    /// <summary>contact zu einer vCard 3.0. existing: bisheriger Stand (UID, Foto und Unbekanntes bleiben).</summary>
    public static string ContactToVcf(JsonElement c, string? existing)
    {
        Component card;
        if (existing != null && Ics.Parse(existing).FirstOrDefault(r => r.Name == "VCARD") is { } old) card = old;
        else
        {
            card = new Component("VCARD");
            card.Set("VERSION", "3.0");
            card.Set("UID", Guid.NewGuid().ToString("D"));
        }
        foreach (var name in new[] { "FN", "N", "NICKNAME", "ORG", "TITLE", "EMAIL", "TEL", "ADR", "BDAY", "NOTE", "CATEGORIES", "URL" }) card.Remove(name);

        string given = S(c, "givenName") ?? "", family = S(c, "surname") ?? "", middle = S(c, "middleName") ?? "",
            prefix = S(c, "title") ?? "", suffix = S(c, "generation") ?? "";
        var fn = S(c, "displayName") is { Length: > 0 } dn ? dn
            : string.Join(" ", new[] { prefix, given, middle, family, suffix }.Where(x => x.Length > 0));
        var emails = A(c, "emailAddresses").Select(e => S(e, "address")).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()).ToList();
        var company = S(c, "companyName") ?? "";
        if (fn.Length == 0) fn = company.Length > 0 ? company : emails.FirstOrDefault() ?? "Kontakt";
        card.SetText("FN", fn);
        card.Set("N", Ics.JoinStructured([family, given, middle, prefix, suffix]));
        card.SetText("NICKNAME", S(c, "nickName"));
        var department = S(c, "department") ?? "";
        if (company.Length + department.Length > 0) card.Set("ORG", Ics.JoinStructured([company, department]));
        card.SetText("TITLE", S(c, "jobTitle"));
        foreach (var e in emails) card.Props.Add(new Prop("EMAIL", Ics.Escape(e)).With("TYPE", "INTERNET"));
        void Tel(string? number, string type)
        {
            if (!string.IsNullOrWhiteSpace(number)) card.Props.Add(new Prop("TEL", Ics.Escape(number.Trim())).With("TYPE", type));
        }
        Tel(S(c, "mobilePhone"), "CELL");
        foreach (var p in A(c, "businessPhones")) Tel(p.GetString(), "WORK,VOICE");
        foreach (var p in A(c, "homePhones")) Tel(p.GetString(), "HOME,VOICE");
        foreach (var (field, type) in new[] { ("homeAddress", "HOME"), ("businessAddress", "WORK"), ("otherAddress", "OTHER") })
        {
            if (O(c, field) is not { } a) continue;
            string[] parts = [S(a, "street") ?? "", S(a, "city") ?? "", S(a, "state") ?? "", S(a, "postalCode") ?? "", S(a, "countryOrRegion") ?? ""];
            if (string.Concat(parts).Trim().Length == 0) continue;
            card.Props.Add(new Prop("ADR", Ics.JoinStructured(["", "", .. parts])).With("TYPE", type));
        }
        if (S(c, "birthday") is { Length: >= 10 } bday && DateTime.TryParseExact(bday[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
            card.Set("BDAY", b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        card.SetText("NOTE", S(c, "personalNotes") is { Length: > 0 } note ? note.Replace("\r\n", "\n") : null);
        SetCategories(card, c);
        card.Set("REV", Ics.Stamp());
        return card.ToString();
    }

    /// <summary>vCard zu contact (fuer POST/PATCH).</summary>
    public static JsonObject ContactToGraph(string vcf)
    {
        var d = ContactModel.Read(new DavObject(0, 0, "x.vcf", null, "VCARD", "", vcf, ""), withPhoto: false) ?? new ContactDto();
        var phones = d.Phones.Where(p => p.Value.Trim().Length > 0).ToList();
        JsonObject? Address(string type)
        {
            // Leere Felder statt eines leeren Objekts - nur so leert PATCH eine entfernte Anschrift
            var a = d.Addresses.FirstOrDefault(x => x.Type == type) ?? new AddressDto();
            return new JsonObject { ["street"] = a.Street, ["city"] = a.City, ["state"] = a.Region, ["postalCode"] = a.Zip, ["countryOrRegion"] = a.Country };
        }
        var o = new JsonObject
        {
            ["displayName"] = d.DisplayName,
            ["givenName"] = d.Given,
            ["surname"] = d.Family,
            ["middleName"] = d.Additional,
            ["title"] = d.Prefix,
            ["generation"] = d.Suffix,
            ["nickName"] = d.Nickname,
            ["companyName"] = d.Org,
            ["department"] = d.Department,
            ["jobTitle"] = d.Title,
            // Graph nimmt hoechstens drei Mailadressen
            ["emailAddresses"] = new JsonArray(d.Emails.Where(e => e.Value.Trim().Length > 0).Take(3)
                .Select(e => (JsonNode)new JsonObject { ["address"] = e.Value.Trim(), ["name"] = d.DisplayName }).ToArray()),
            ["mobilePhone"] = phones.FirstOrDefault(p => p.Type == "cell")?.Value,
            ["businessPhones"] = Array(phones.Where(p => p.Type == "work").Select(p => p.Value)),
            ["homePhones"] = Array(phones.Where(p => p.Type is "home" or "other").Select(p => p.Value)),
            ["homeAddress"] = Address("home"),
            ["businessAddress"] = Address("work"),
            ["otherAddress"] = Address("other"),
            ["birthday"] = d.Birthday is { } bd ? bd + "T00:00:00Z" : null,
            ["personalNotes"] = d.Note,
            ["categories"] = Array(d.Categories),
        };
        return o;
    }

    // ---------------- Termine ----------------

    /// <summary>
    /// Termin (Einzeltermin oder Serie) zu einer .ics-Datei. exceptions: geaenderte Vorkommen der Serie (type=exception),
    /// cancelled: Beginn (UTC bzw. Tag) entfallener Vorkommen. existing: bisheriger Stand (UID bleibt).
    /// </summary>
    public static string EventToIcs(JsonElement e, IEnumerable<JsonElement> exceptions, IEnumerable<DateTime> cancelled, string? existing)
    {
        string? uid = null;
        if (existing != null)
            uid = Ics.Parse(existing).FirstOrDefault(r => r.Name == "VCALENDAR")?.Of("VEVENT").FirstOrDefault()?.Text("UID");
        uid ??= S(e, "iCalUId") ?? Guid.NewGuid().ToString("D");

        var vcal = CalendarModel.NewCalendar();
        var zone = ZoneOf(S(e, "originalStartTimeZone")) ?? ZoneOf(S(O(e, "start") ?? default, "timeZone")) ?? TimeZoneInfo.Utc;
        var allDay = B(e, "isAllDay");
        if (!allDay && zone != TimeZoneInfo.Utc) vcal.Children.Add(Ics.Timezone(zone).Also(v => v.Set("TZID", IanaId(zone))));

        var master = Event(e, uid, zone, allDay);
        if (RRule(O(e, "recurrence"), zone, allDay) is { } rr)
        {
            master.Set("RRULE", rr);
            foreach (var day in cancelled.Distinct().Order())
                master.Props.Add(allDay ? new Prop("EXDATE", Ics.Date(day)).With("VALUE", "DATE") : TimeProp("EXDATE", day, zone));
        }
        vcal.Children.Add(master);
        if (master.Get("RRULE") != null)
            foreach (var x in exceptions)
            {
                var ev = Event(x, uid, zone, allDay);
                if (OriginalStart(x, allDay, zone) is DateTime orig)
                    ev.Props.Insert(1, allDay ? new Prop("RECURRENCE-ID", Ics.Date(orig)).With("VALUE", "DATE") : TimeProp("RECURRENCE-ID", orig, zone));
                vcal.Children.Add(ev);
            }
        return vcal.ToString();
    }

    /// <summary>Urspruenglicher Beginn eines Vorkommens: UTC, bei ganztaegigen der Tag.</summary>
    public static DateTime? OriginalStart(JsonElement x, bool allDay, TimeZoneInfo zone)
    {
        if (S(x, "originalStart") is not { } os || !DateTimeOffset.TryParse(os, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o)) return null;
        // Ganztaegig: Mitternacht in der Zone des Termins (Outlook liefert es als UTC)
        return allDay ? TimeZoneInfo.ConvertTimeFromUtc(o.UtcDateTime, zone).AddHours(12).Date : o.UtcDateTime;
    }

    private static Component Also(this Component c, Action<Component> change) { change(c); return c; }

    private static Prop TimeProp(string name, DateTime utc, TimeZoneInfo zone) =>
        zone == TimeZoneInfo.Utc ? new Prop(name, Ics.Utc(utc)) : new Prop(name, Ics.Local(utc, zone)).With("TZID", IanaId(zone));

    private static Component Event(JsonElement e, string uid, TimeZoneInfo zone, bool allDay)
    {
        var ev = new Component("VEVENT");
        ev.Set("UID", uid);
        ev.Set("DTSTAMP", Ics.Stamp());
        if (allDay)
        {
            var start = DateOf(O(e, "start")) ?? DateTime.UtcNow.Date;
            var end = DateOf(O(e, "end")) is DateTime en && en > start ? en : start.AddDays(1);
            ev.Set("DTSTART", Ics.Date(start)).With("VALUE", "DATE");
            ev.Set("DTEND", Ics.Date(end)).With("VALUE", "DATE");
        }
        else
        {
            var start = Utc(O(e, "start")) ?? DateTime.UtcNow;
            var end = Utc(O(e, "end")) is DateTime en && en >= start ? en : start;
            ev.Props.Add(TimeProp("DTSTART", start, zone));
            ev.Props.Add(TimeProp("DTEND", end, zone));
        }
        ev.SetText("SUMMARY", S(e, "subject") ?? "");
        ev.SetText("DESCRIPTION", Body(e) is { Length: > 0 } body ? body : null);
        ev.SetText("LOCATION", O(e, "location") is { } loc ? S(loc, "displayName") : null);
        if (S(e, "showAs") is "free" or "workingElsewhere") ev.Set("TRANSP", "TRANSPARENT");
        if (S(e, "sensitivity") is "private" or "confidential") ev.Set("CLASS", "PRIVATE");
        if (B(e, "isCancelled")) ev.Set("STATUS", "CANCELLED");
        SetCategories(ev, e);
        if (O(e, "organizer") is { } org && O(org, "emailAddress") is { } oe && S(oe, "address") is { Length: > 0 } oa)
        {
            var p = new Prop("ORGANIZER", "mailto:" + oa);
            if (S(oe, "name") is { Length: > 0 } on) p.With("CN", on);
            ev.Props.Add(p);
        }
        foreach (var a in A(e, "attendees"))
        {
            if (O(a, "emailAddress") is not { } ae || S(ae, "address") is not { Length: > 0 } addr) continue;
            var p = new Prop("ATTENDEE", "mailto:" + addr);
            if (S(ae, "name") is { Length: > 0 } an) p.With("CN", an);
            p.With("ROLE", S(a, "type") == "optional" ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT");
            p.With("PARTSTAT", (O(a, "status") is { } st ? S(st, "response") : null) switch
            {
                "accepted" => "ACCEPTED", "declined" => "DECLINED", "tentativelyAccepted" => "TENTATIVE", _ => "NEEDS-ACTION",
            });
            ev.Props.Add(p);
        }
        if (B(e, "isReminderOn") && N(e, "reminderMinutesBeforeStart") is int minutes)
        {
            var alarm = new Component("VALARM");
            alarm.Set("ACTION", "DISPLAY");
            alarm.SetText("DESCRIPTION", S(e, "subject") ?? "Erinnerung");
            alarm.Set("TRIGGER", minutes == 0 ? "PT0S" : "-PT" + minutes + "M");
            ev.Children.Add(alarm);
        }
        Stamp(ev, e);
        return ev;
    }

    private static void Stamp(Component c, JsonElement e)
    {
        if (Modified(e) is DateTime m) c.Set("LAST-MODIFIED", Ics.Utc(m));
    }

    /// <summary>Zone eines VEVENT (TZID des Beginns), sonst die des Benutzers.</summary>
    public static TimeZoneInfo ZoneOfEvent(Component ev, TimeZoneInfo fallback) =>
        ev.Get("DTSTART") is { } p ? p.Value.EndsWith('Z') ? TimeZoneInfo.Utc : Ics.Zone(p.Param("TZID")) ?? fallback : fallback;

    /// <summary>VEVENT (Serie, Einzeltermin oder geaendertes Vorkommen) zu event (fuer POST/PATCH).</summary>
    public static JsonObject EventToGraph(Component ev, TimeZoneInfo fallback, bool withRecurrence = true)
    {
        var zone = ZoneOfEvent(ev, fallback);
        var start = Ics.Time(ev.Get("DTSTART"), zone, out var allDay) ?? DateTime.UtcNow;
        var end = Ics.Time(ev.Get("DTEND"), zone, out _);
        if (end == null && ev.Text("DURATION") is { } dur && PimStore.TryDuration(dur, out var d)) end = start + d;
        end ??= allDay ? start.AddDays(1) : start;
        if (allDay && end <= start) end = start.AddDays(1);
        var o = new JsonObject
        {
            ["subject"] = ev.Text("SUMMARY") ?? "",
            ["body"] = TextBody(ev.Text("DESCRIPTION")),
            ["isAllDay"] = allDay,
            ["location"] = new JsonObject { ["displayName"] = ev.Text("LOCATION") ?? "" },
            ["showAs"] = string.Equals(ev.Text("TRANSP"), "TRANSPARENT", StringComparison.OrdinalIgnoreCase) ? "free" : "busy",
            ["sensitivity"] = ev.Text("CLASS")?.ToUpperInvariant() is "PRIVATE" or "CONFIDENTIAL" ? "private" : "normal",
            ["categories"] = Array(Categories(ev)),
        };
        if (allDay)
        {
            // Ganztaegig: Mitternacht bis Mitternacht, Zone egal - aber auf beiden Seiten dieselbe
            o["start"] = new JsonObject { ["dateTime"] = start.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture), ["timeZone"] = WindowsId(fallback) };
            o["end"] = new JsonObject { ["dateTime"] = end.Value.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture), ["timeZone"] = WindowsId(fallback) };
        }
        else
        {
            o["start"] = DateTimeTz(TimeZoneInfo.ConvertTimeFromUtc(start, zone), zone);
            o["end"] = DateTimeTz(TimeZoneInfo.ConvertTimeFromUtc(end.Value, zone), zone);
        }
        if (withRecurrence)
        {
            var startLocal = allDay ? start : TimeZoneInfo.ConvertTimeFromUtc(start, zone);
            o["recurrence"] = Recurrence(ev.Text("RRULE"), startLocal, allDay ? fallback : zone);
        }
        var alarm = ev.Of("VALARM").Select(a => a.Text("TRIGGER")).FirstOrDefault(t => t != null && PimStore.TryDuration(t, out var td) && td <= TimeSpan.Zero);
        if (alarm != null && PimStore.TryDuration(alarm, out var before))
        {
            o["isReminderOn"] = true;
            o["reminderMinutesBeforeStart"] = (int)Math.Round(-before.TotalMinutes);
        }
        else o["isReminderOn"] = false;
        // Teilnehmer gehen nicht zu Microsoft: Graph verschickte sonst Einladungen (die erste Fassung spiegelt sie nur)
        return o;
    }
}
