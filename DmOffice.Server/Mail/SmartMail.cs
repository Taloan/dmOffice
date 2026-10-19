using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>Was aus einer Mail erkannt wurde: Paket, Flug, Hotel, Reservierung.</summary>
public sealed record SmartItem(
    string Kind, string Key, string Title, DateTime Start, DateTime? End, bool AllDay, string? Location, string? Details);

/// <summary>
/// Termine aus Mails, wie Outlook: Lieferankuendigungen (DHL, Amazon, Hermes, DPD, GLS, UPS) und
/// strukturierte Angaben nach schema.org (Fluege, Bahn, Hotels, Veranstaltungen, Tischreservierungen).
/// Laeuft nur auf dem eigenen Server - kein Dienst von aussen sieht die Mails.
/// Die Termine kommen in den Kalender "E-Mail-Ankündigungen"; je Sendung bzw. Buchung einer (feste UID),
/// eine spaetere Mail mit neuem Datum verschiebt ihn.
/// </summary>
public static class SmartMail
{
    private const string CalendarKey = "smart.calendar";
    private const int Days = 14;

    // ---------------- Erkennen ----------------

    public static List<SmartItem> Extract(MimeMessage m, TimeZoneInfo zone, DateTime nowUtc)
    {
        var items = new List<SmartItem>();
        var html = m.HtmlBody ?? "";
        foreach (var json in JsonLd(html)) Schema(json, zone, items);
        if (items.Count == 0 && Parcel(m, zone, nowUtc) is { } p) items.Add(p);
        return items;
    }

    private static IEnumerable<JsonElement> JsonLd(string html)
    {
        foreach (Match x in Regex.Matches(html, @"<script[^>]*application/ld\+json[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(System.Net.WebUtility.HtmlDecode(x.Groups[1].Value)).RootElement; } catch { continue; }
            foreach (var e in Flatten(root)) yield return e;
        }
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) foreach (var y in Flatten(x)) yield return y; }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("@graph", out var g)) foreach (var y in Flatten(g)) yield return y;
            else yield return e;
        }
    }

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind == JsonValueKind.Array) e = e.EnumerateArray().FirstOrDefault();
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.ToString(), _ => null };
    }

    private static DateTime? When(string? s, TimeZoneInfo zone, out bool allDay)
    {
        allDay = false;
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$") && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        { allDay = true; return DateTime.SpecifyKind(d, DateTimeKind.Unspecified); }
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && Regex.IsMatch(s, @"(Z|[+-]\d{2}:?\d{2})$"))
            return dto.UtcDateTime;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return Ics.ToUtc(local, zone);
        return null;
    }

    private static void Schema(JsonElement e, TimeZoneInfo zone, List<SmartItem> items)
    {
        var type = Str(e, "@type") ?? "";
        switch (type)
        {
            case "ParcelDelivery":
            {
                var from = When(Str(e, "expectedArrivalFrom"), zone, out var ad1);
                var until = When(Str(e, "expectedArrivalUntil"), zone, out _);
                if (from == null && until == null) return;
                var carrier = Str(e, "carrier", "name") ?? Str(e, "provider", "name");
                var merchant = Str(e, "partOfOrder", "merchant", "name") ?? Str(e, "partOfOrder", "seller", "name");
                var item = Str(e, "itemShipped", "name");
                var tracking = Str(e, "trackingNumber");
                var title = "📦 " + (merchant != null ? $"Paket von {merchant}" : carrier != null ? $"Paket ({carrier})" : "Paket") + (item != null ? $": {item}" : "");
                items.Add(new SmartItem("parcel", "parcel:" + (tracking ?? Str(e, "partOfOrder", "orderNumber") ?? title),
                    title, (from ?? until)!.Value, from != null && until != null && !ad1 ? until : null, ad1 || from == null,
                    null, Join(carrier != null ? "Zusteller: " + carrier : null, tracking != null ? "Sendungsnummer: " + tracking : null, Str(e, "trackingUrl"))));
                return;
            }
            case "FlightReservation":
            {
                var f = e.TryGetProperty("reservationFor", out var r) ? r : default;
                if (f.ValueKind != JsonValueKind.Object) return;
                var dep = When(Str(f, "departureTime"), zone, out _);
                if (dep == null) return;
                var arr = When(Str(f, "arrivalTime"), zone, out _);
                var no = (Str(f, "airline", "iataCode") ?? "") + (Str(f, "flightNumber") ?? "");
                var a = Str(f, "departureAirport", "iataCode") ?? Str(f, "departureAirport", "name");
                var b = Str(f, "arrivalAirport", "iataCode") ?? Str(f, "arrivalAirport", "name");
                items.Add(new SmartItem("flight", "flight:" + (Str(e, "reservationNumber") ?? "") + no + dep.Value.ToString("o"),
                    $"✈ Flug {no} {a} → {b}".Trim(), dep.Value, arr ?? dep.Value.AddHours(2), false, Str(f, "departureAirport", "name"),
                    Join(Str(e, "reservationNumber") is { } rn ? "Buchungscode: " + rn : null, Str(f, "airline", "name"))));
                return;
            }
            case "TrainReservation" or "BusReservation":
            {
                var f = e.TryGetProperty("reservationFor", out var r) ? r : default;
                if (f.ValueKind != JsonValueKind.Object) return;
                var dep = When(Str(f, "departureTime"), zone, out _);
                if (dep == null) return;
                var arr = When(Str(f, "arrivalTime"), zone, out _);
                var a = Str(f, "departureStation", "name") ?? Str(f, "departureBusStop", "name");
                var b = Str(f, "arrivalStation", "name") ?? Str(f, "arrivalBusStop", "name");
                var no = Str(f, "trainNumber") ?? Str(f, "busNumber") ?? "";
                items.Add(new SmartItem(type == "TrainReservation" ? "train" : "bus", type + ":" + (Str(e, "reservationNumber") ?? "") + no + dep.Value.ToString("o"),
                    $"{(type == "TrainReservation" ? "🚆" : "🚌")} {no} {a} → {b}".Replace("  ", " ").Trim(), dep.Value, arr ?? dep.Value.AddHours(1), false, a,
                    Str(e, "reservationNumber") is { } rn ? "Buchung: " + rn : null));
                return;
            }
            case "LodgingReservation":
            {
                var cin = When(Str(e, "checkinTime") ?? Str(e, "checkinDate"), zone, out var ad);
                var cout = When(Str(e, "checkoutTime") ?? Str(e, "checkoutDate"), zone, out _);
                if (cin == null) return;
                var name = Str(e, "reservationFor", "name") ?? "Hotel";
                // Hotels als ganze Tage: Anreise bis Abreise
                var startDay = (ad ? cin.Value : TimeZoneInfo.ConvertTimeFromUtc(cin.Value, zone)).Date;
                var endDay = cout == null ? startDay.AddDays(1) : (ad ? cout.Value : TimeZoneInfo.ConvertTimeFromUtc(cout.Value, zone)).Date;
                items.Add(new SmartItem("hotel", "hotel:" + (Str(e, "reservationNumber") ?? name + startDay.ToString("yyyyMMdd")),
                    "🏨 " + name, startDay, endDay > startDay ? endDay : startDay.AddDays(1), true,
                    Str(e, "reservationFor", "address", "streetAddress") is { } st ? st + ", " + Str(e, "reservationFor", "address", "addressLocality") : null,
                    Str(e, "reservationNumber") is { } rn ? "Buchung: " + rn : null));
                return;
            }
            case "EventReservation" or "FoodEstablishmentReservation":
            {
                var start = When(Str(e, "reservationFor", "startDate") ?? Str(e, "startTime"), zone, out var ad);
                if (start == null) return;
                var end = When(Str(e, "reservationFor", "endDate") ?? Str(e, "endTime"), zone, out _);
                var name = Str(e, "reservationFor", "name") ?? (type == "FoodEstablishmentReservation" ? "Tischreservierung" : "Veranstaltung");
                items.Add(new SmartItem(type == "EventReservation" ? "event" : "restaurant", type + ":" + (Str(e, "reservationNumber") ?? name + start.Value.ToString("o")),
                    (type == "EventReservation" ? "🎫 " : "🍽 ") + name, start.Value, end ?? (ad ? start.Value.AddDays(1) : start.Value.AddHours(2)), ad,
                    Str(e, "reservationFor", "location", "name") ?? Str(e, "reservationFor", "address", "streetAddress"),
                    Str(e, "reservationNumber") is { } rn ? "Reservierung: " + rn : null));
                return;
            }
        }
    }

    private static string? Join(params string?[] parts) => parts.Any(p => !string.IsNullOrEmpty(p)) ? string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p))) : null;

    // ---------------- Paketmails ohne strukturierte Angaben ----------------

    private static readonly (string Name, Regex Sender)[] Carriers =
    {
        ("DHL", new(@"(^|[@.])dhl\.(de|com)$|deutschepost", RegexOptions.IgnoreCase)),
        ("Amazon", new(@"(^|[@.])amazon\.(de|com|at|co\.uk)$", RegexOptions.IgnoreCase)),
        ("Hermes", new(@"(^|[@.])(myhermes|hermesworld|hermes-europe)\.", RegexOptions.IgnoreCase)),
        ("DPD", new(@"(^|[@.])dpd\.", RegexOptions.IgnoreCase)),
        ("GLS", new(@"(^|[@.])gls-(pakete|group|germany)\.|(^|[@.])gls\.", RegexOptions.IgnoreCase)),
        ("UPS", new(@"(^|[@.])ups\.com$", RegexOptions.IgnoreCase)),
    };

    private static readonly Regex Delivered = new(@"(wurde|ist)\s+(erfolgreich\s+)?zugestellt|has been delivered|was delivered|zugestellt am", RegexOptions.IgnoreCase);
    private static readonly Regex Announce = new(@"zustellung|lieferung|liefertermin|kommt\s+(heute|morgen|am|voraussichtlich)|voraussichtlich|unterwegs|wird\s+(heute|morgen)\s+zugestellt|arriving|delivery|out for delivery|expected", RegexOptions.IgnoreCase);
    private static readonly Regex Tracking = new(@"\b(JJD\d{14,22}|1Z[0-9A-Z]{16}|\d{12,20})\b");
    private static readonly string[] MonthNames = { "januar", "februar", "märz", "april", "mai", "juni", "juli", "august", "september", "oktober", "november", "dezember" };
    private static readonly string[] MonthsEn = { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

    private static SmartItem? Parcel(MimeMessage m, TimeZoneInfo zone, DateTime nowUtc)
    {
        var from = m.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        var domain = from.Contains('@') ? from[(from.IndexOf('@') + 1)..] : from;
        var carrier = Carriers.FirstOrDefault(c => c.Sender.IsMatch(domain)).Name;
        var subject = m.Subject ?? "";
        var text = subject + "\n" + MailStore.BodyText(m);
        // Weitergeleitete Paketmails (z. B. ueber einen Wegwerf-Alias): Absender im Text
        var fromName = m.From.Mailboxes.FirstOrDefault()?.Name ?? "";
        carrier ??= Carriers.Select(c => c.Name).FirstOrDefault(n => Regex.IsMatch(fromName + " " + subject, $@"\b{n}\b", RegexOptions.IgnoreCase));
        if (carrier == null || !Announce.IsMatch(text)) return null;
        if (Delivered.IsMatch(subject)) return null;

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var sent = TimeZoneInfo.ConvertTimeFromUtc(m.Date.UtcDateTime, zone).Date;
        DateTime? day = null;
        // Reihenfolge: ausdrueckliches Datum vor "heute/morgen" (die beziehen sich auf den Versandtag der Mail)
        var lower = text.ToLowerInvariant();
        var dm = Regex.Match(lower, @"(\d{1,2})\.(\d{1,2})\.(\d{2,4})?");
        var dn = Regex.Match(lower, @"(\d{1,2})\.?\s+(" + string.Join("|", MonthNames) + @"|okt|nov|dez|jan|feb|mär|apr|jun|jul|aug|sep)\.?(\s+(\d{4}))?");
        var en = Regex.Match(lower, @"(" + string.Join("|", MonthsEn) + @")\s+(\d{1,2})(st|nd|rd|th)?");
        if (dm.Success)
        {
            var y = dm.Groups[3].Success ? int.Parse(dm.Groups[3].Value) : sent.Year;
            if (y < 100) y += 2000;
            day = Safe(y, int.Parse(dm.Groups[2].Value), int.Parse(dm.Groups[1].Value));
        }
        else if (dn.Success)
        {
            var name = dn.Groups[2].Value;
            var mo = Array.FindIndex(MonthNames, x => x.StartsWith(name.TrimEnd('.'))) + 1;
            var y = dn.Groups[4].Success ? int.Parse(dn.Groups[4].Value) : sent.Year;
            if (mo > 0) day = Safe(y, mo, int.Parse(dn.Groups[1].Value));
        }
        else if (en.Success)
        {
            var mo = Array.IndexOf(MonthsEn, en.Groups[1].Value) + 1;
            day = Safe(sent.Year, mo, int.Parse(en.Groups[2].Value));
        }
        if (day == null)
        {
            if (Regex.IsMatch(lower, @"\b(heute|today)\b")) day = sent;
            else if (Regex.IsMatch(lower, @"\b(morgen|tomorrow)\b")) day = sent.AddDays(1);
            else if (Regex.IsMatch(lower, @"\bübermorgen\b")) day = sent.AddDays(2);
        }
        if (day == null) return null;
        if (day < sent.AddDays(-1)) day = day.Value.AddYears(1); // "02.01." im Dezember
        if (day < nowLocal.Date.AddDays(-1) || day > nowLocal.Date.AddDays(60)) return null;

        // Zeitfenster "zwischen 10:00 und 14:00 Uhr", "10:30 - 13:30 Uhr", "between 10am and 2pm"
        DateTime start = day.Value, end = day.Value.AddDays(1);
        bool allDay = true;
        var win = Regex.Match(lower, @"(\d{1,2})[:.](\d{2})\s*(uhr)?\s*(und|bis|-|–)\s*(\d{1,2})[:.](\d{2})");
        if (win.Success && int.Parse(win.Groups[1].Value) < 24 && int.Parse(win.Groups[5].Value) <= 24)
        {
            start = day.Value.AddHours(int.Parse(win.Groups[1].Value)).AddMinutes(int.Parse(win.Groups[2].Value));
            end = day.Value.AddHours(int.Parse(win.Groups[5].Value)).AddMinutes(int.Parse(win.Groups[6].Value));
            if (end > start) allDay = false;
        }
        var tracking = Tracking.Match(text);
        var merchant = carrier == "Amazon" || Regex.IsMatch(subject, @"\bamazon\b", RegexOptions.IgnoreCase) ? "Amazon" : null;
        var title = "📦 " + (merchant != null && carrier != "Amazon" ? $"Paket von {merchant} ({carrier})" : $"Paket ({carrier})");
        var key = "parcel:" + (tracking.Success ? tracking.Value : carrier + ":" + Regex.Replace(subject, @"\s+", " ").Trim().ToLowerInvariant());
        return new SmartItem("parcel", key, title,
            allDay ? DateTime.SpecifyKind(start, DateTimeKind.Unspecified) : Ics.ToUtc(start, zone),
            allDay ? DateTime.SpecifyKind(end, DateTimeKind.Unspecified) : Ics.ToUtc(end, zone), allDay, null,
            Join("Aus der Mail: " + subject, tracking.Success ? "Sendungsnummer: " + tracking.Value : null));
    }

    private static DateTime? Safe(int y, int mo, int d)
    {
        try { return new DateTime(y, mo, d); } catch { return null; }
    }

    // ---------------- Eintragen ----------------

    /// <summary>Name wie die Systemliste "E-Mail-Nachverfolgung" - nicht auf Pakete und Reisen festgelegt.</summary>
    public const string CalendarName = "E-Mail-Ankündigungen";
    private const string OldCalendarName = "Lieferungen & Reisen";

    /// <summary>Kalender "E-Mail-Ankündigungen" - wird beim ersten Treffer angelegt.</summary>
    public static Collection CalendarFor(UserDb db)
    {
        RenameOld(db);
        if (long.TryParse(db.Get(CalendarKey), out var id) && PimStore.Find(db, id) is { } c) return c;
        var coll = PimStore.CreateCollection(db, PimStore.Calendar, "deliveries", CalendarName, "#CA5010", "VEVENT");
        db.Set(CalendarKey, coll.Id.ToString());
        return coll;
    }

    /// <summary>Frueher hiess der Kalender "Lieferungen &amp; Reisen" - umbenennen, ausser der Benutzer hat ihn selbst umbenannt.</summary>
    private static void RenameOld(UserDb db)
    {
        if (long.TryParse(db.Get(CalendarKey), out var id) && PimStore.Find(db, id) is { Name: OldCalendarName })
            PimStore.UpdateCollection(db, id, CalendarName, null, null);
    }

    private static string Uid(string key) => "dm-smart-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();

    public static bool InCalendar(UserDb db, SmartItem item) =>
        long.TryParse(db.Get(CalendarKey), out var id) && PimStore.ByUid(db, id, Uid(item.Key)) != null;

    /// <summary>Termin anlegen oder (gleiche Sendung/Buchung) aktualisieren. Gibt die Kalender-Id zurueck.</summary>
    public static long Upsert(UserSpace space, SmartItem item, string? mailLink)
    {
        var db = space.Db;
        var coll = CalendarFor(db);
        var uid = Uid(item.Key);
        var vcal = CalendarModel.NewCalendar();
        var ev = new Component("VEVENT");
        ev.Set("UID", uid);
        ev.Set("DTSTAMP", Ics.Stamp());
        ev.SetText("SUMMARY", item.Title);
        if (item.AllDay)
        {
            ev.Set("DTSTART", Ics.Date(item.Start)).With("VALUE", "DATE");
            ev.Set("DTEND", Ics.Date(item.End ?? item.Start.AddDays(1))).With("VALUE", "DATE");
            ev.Set("TRANSP", "TRANSPARENT"); // ganztaegige Lieferung blockiert den Tag nicht
        }
        else
        {
            ev.Set("DTSTART", Ics.Utc(item.Start));
            ev.Set("DTEND", Ics.Utc(item.End ?? item.Start.AddHours(1)));
            if (item.Kind == "parcel") ev.Set("TRANSP", "TRANSPARENT");
        }
        if (item.Location != null) ev.SetText("LOCATION", item.Location);
        var desc = Join(item.Details, mailLink != null ? "Mail: " + mailLink : null);
        if (desc != null) ev.SetText("DESCRIPTION", desc);
        ev.SetText("CATEGORIES", item.Kind == "parcel" ? "Lieferung" : "Reise");
        vcal.Children.Add(ev);
        var uri = PimStore.ByUid(db, coll.Id, uid)?.Uri ?? (uid + ".ics");
        PimStore.Put(db, coll, uri, vcal.ToString(), zone: space.Zone);
        return coll.Id;
    }

    /// <summary>Neue Mails im Posteingang (letzte 14 Tage) einmal pruefen - nach jedem Sync-Lauf.</summary>
    public static int Scan(UserSpace space, MailAccount account)
    {
        var db = space.Db;
        RenameOld(db);
        if (!space.Settings.SmartCalendar) return 0;
        db.Exec("CREATE TABLE IF NOT EXISTS smart_done (account TEXT NOT NULL, message_id TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(account, message_id)) WITHOUT ROWID");
        var since = DateTimeOffset.UtcNow.AddDays(-Days).ToUnixTimeMilliseconds();
        db.Exec("DELETE FROM smart_done WHERE at < $s", ("$s", since - 30L * 86400000));
        var todo = db.Query(@"SELECT m.folder, m.uid, m.message_id FROM mail m
            JOIN mail_folders f ON f.account=m.account AND f.path=m.folder AND f.special='inbox'
            WHERE m.account=$a AND m.cached=1 AND m.date >= $s AND m.message_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM smart_done d WHERE d.account=m.account AND d.message_id=m.message_id)
            ORDER BY m.date DESC LIMIT 200",
            r => (r.GetString(0), (uint)r.GetInt64(1), r.GetString(2)), ("$a", account.Id), ("$s", since));
        int added = 0;
        foreach (var (folder, uid, mid) in todo)
        {
            try
            {
                if (MailStore.Raw(db, account.Id, folder, uid) is { } raw)
                {
                    var m = MimeMessage.Load(new MemoryStream(raw));
                    foreach (var item in Extract(m, space.Zone, DateTime.UtcNow)) { Upsert(space, item, null); added++; }
                }
            }
            catch { /* eine kaputte Mail haelt die anderen nicht auf */ }
            db.Exec("INSERT OR IGNORE INTO smart_done(account,message_id,at) VALUES($a,$m,$t)",
                ("$a", account.Id), ("$m", mid), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        return added;
    }
}
