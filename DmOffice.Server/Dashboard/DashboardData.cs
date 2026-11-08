using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Gallery;
using DmOffice.Journal;
using DmOffice.Mail;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Dashboard;

/// <summary>
/// Daten der Widgets - klein, für Web und App gleich. Jedes Widget holt für sich (ein langsames wie Wetter oder Schlagzeilen
/// hält die anderen nicht auf). Zum Öffnen bekommt jeder Eintrag das, was die Oberflächen schon kennen (Pfade, Ids, Uris).
/// </summary>
public static class DashboardData
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Einstellungen eines Widgets lesen - fehlende Werte: Vorgabe.</summary>
    public sealed class Opts(Dictionary<string, JsonElement>? o)
    {
        public int Int(string k, int def, int min, int max) => o != null && o.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetInt32(), min, max) : def;
        public string Str(string k, string def = "") => o != null && o.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;
        public bool Bool(string k, bool def = false) => o != null && o.TryGetValue(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
        public List<long> Longs(string k) => o != null && o.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];
        public List<string> Strs(string k) => o != null && o.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
        public long? Long(string k) => o != null && o.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    }

    private static DateOnly Today(UserSpace space) => DateOnly.FromDateTime(Pukki.Now(space));
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---------------- Heute ----------------

    public static async Task<object> TodayAsync(HttpContext http, IHttpClientFactory f, CancellationToken ct)
    {
        var space = http.Space();
        var now = Pukki.Now(space);
        var b = PukkiBriefing.Of(space.Db);
        object? weather = null;
        if (b.Weather && b.Lat is double lat && b.Lon is double lon && await ForecastAsync(f.CreateClient(), lat, lon, ct) is { } w)
            weather = new { place = b.Place, now = w.Now, today = w.Days.FirstOrDefault() };
        var greeting = now.Hour is >= 5 and < 11 ? "Guten Morgen" : now.Hour is >= 11 and < 18 ? "Hallo" : now.Hour is >= 18 and < 23 ? "Guten Abend" : "Gute Nacht";
        var first = space.User.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? space.User.DisplayName;
        return new { greeting = $"{greeting}, {first}", date = now.ToString("dddd, d. MMMM", De), weather };
    }

    // ---------------- Kalender ----------------

    /// <summary>Termine der nächsten Tage aus den gewählten Kalendern (leer: alle normalen eigenen und geteilten).</summary>
    public static object Events(HttpContext http, Opts o)
    {
        var space = http.Space();
        var services = http.RequestServices;
        var today = Today(space);
        var days = o.Int("days", 3, 1, 14);
        var wanted = o.Longs("calendars").ToHashSet();
        var all = Pukki.Calendars(services, space).ToList();
        var only = wanted.Count == 0 ? all : all.Where(c => wanted.Contains(PublicCalId(http, c.Item1, c.Item2))).ToList();
        var now = Pukki.Now(space);
        var result = new List<object>();
        for (var d = today; d < today.AddDays(days); d = d.AddDays(1))
        {
            var items = Pukki.DayEvents(services, space, d, only).Select(e => new
            {
                e.Title, start = e.AllDay ? null : e.Start.ToString("HH:mm"), end = e.AllDay ? null : e.End.ToString("HH:mm"), e.AllDay, calendar = e.Calendar, e.Icon,
                // Zum Öffnen im Kalender: der Tag und der Termin
                day = Iso(d), uri = e.Uri,
                // „jetzt“ und „in 25 Min.“ nur heute
                now = d == today && !e.AllDay && e.Start <= TimeOnly.FromDateTime(now) && e.End > TimeOnly.FromDateTime(now),
                inMinutes = d == today && !e.AllDay && e.Start > TimeOnly.FromDateTime(now) ? (int?)(e.Start - TimeOnly.FromDateTime(now)).TotalMinutes : null,
            }).ToList();
            using (Pukki.ForAccount(space))
                result.Add(new { day = Iso(d), label = Pukki.DayWords(d, today), weekday = Pukki.DateWords(d, "dddd", "dddd"), items });
        }
        var calendars = all.Select(c => new { id = PublicCalId(http, c.Item1, c.Item2), c.Item2.Name, c.Item2.Color, shared = c.Item3 }).ToList();
        return new { days = result, calendars, invites = o.Bool("invites", true) ? Invites(http) : [] };
    }

    /// <summary>
    /// Offene interne Einladungen: Pukki legt sie als Nachricht mit den Knöpfen Zusagen/Vielleicht/Absagen in den eigenen Raum;
    /// beantwortet, sind die Knöpfe weg. Zum Antworten: POST /api/chat/messages/{id}/actions/{key}.
    /// </summary>
    private static List<object> Invites(HttpContext http)
    {
        var chat = http.RequestServices.GetRequiredService<ChatStore>();
        var me = http.Space().User.Id;
        if (Pukki.RoomOf(chat, me) is not long room) return [];
        return chat.Messages(me, room, limit: 100).Where(m => !m.Deleted && m.Sender == Pukki.Id && m.Actions != null && m.Actions.Any(a => a.Key == InternalInvites.Accept))
            .Select(m => (object)new { message = m.Id, m.Text, m.Created, actions = m.Actions!.Select(a => new { a.Key, a.Label }) }).ToList();
    }

    /// <summary>Id eines Kalenders, wie der Benutzer ihn kennt (geteilte: minus Freigabe-Id, wie /api/pim).</summary>
    private static long PublicCalId(HttpContext http, UserSpace owner, Collection c)
    {
        if (owner.User.Id == http.Space().User.Id) return c.Id;
        return SharedLists.WithMe(http).FirstOrDefault(x => x.Owner.User.Id == owner.User.Id && x.Coll.Id == c.Id) is { } s ? -(long)s.Share.Id : c.Id;
    }

    // ---------------- Essensplan ----------------

    public static object Meals(HttpContext http, Opts o)
    {
        var space = http.Space();
        var today = Today(space);
        var week = o.Str("range", "week") == "week";
        var days = week ? 7 : 2;
        PimStore.EnsureDefaults(space.Db);
        var plans = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT") && CalendarKinds.Of(space.Db, c.Id) == CalendarKinds.Meal)
            .Select(c => (Owner: space, Coll: c, Id: c.Id))
            .Concat(SharedLists.WithMe(http).Where(x => x.Coll.Components.Contains("VEVENT") && CalendarKinds.Of(x.Owner.Db, x.Coll.Id) == CalendarKinds.Meal)
                .Select(x => (Owner: x.Owner, Coll: x.Coll, Id: -(long)x.Share.Id))).ToList();
        var pick = o.Long("calendar");
        var use = pick is long p && plans.Any(x => x.Id == p) ? plans.Where(x => x.Id == p).ToList() : plans;
        var zone = space.Zone;
        var from = Ics.ToUtc(today.ToDateTime(TimeOnly.MinValue), zone);
        var to = Ics.ToUtc(today.AddDays(days).ToDateTime(TimeOnly.MinValue), zone);
        var byDay = new Dictionary<DateOnly, List<object>>();
        foreach (var (owner, coll, id) in use)
            foreach (var e in CalendarModel.Events(owner, from, to, [coll.Id]))
            {
                var day = e.AllDay && DateOnly.TryParse(e.Start, CultureInfo.InvariantCulture, out var s) ? s : DateOnly.FromDateTime(Pukki.Local(e.Start, zone));
                if (day < today || day >= today.AddDays(days)) continue;
                var list = byDay.TryGetValue(day, out var l) ? l : byDay[day] = [];
                list.Add(new { e.Title, meal = e.Categories.FirstOrDefault(), calendar = id, e.Uri, e.Url, recipe = e.Recipe, time = e.AllDay ? null : Pukki.Local(e.Start, zone).ToString("HH:mm") });
            }
        var result = new List<object>();
        for (var d = today; d < today.AddDays(days); d = d.AddDays(1))
            result.Add(new { day = Iso(d), label = PukkiParse.DayText(d, today), weekday = d.ToString("ddd", De), items = byDay.GetValueOrDefault(d) ?? [] });
        return new { days = result, plans = plans.Select(x => new { id = x.Id, x.Coll.Name, x.Coll.Color }).ToList() };
    }

    // ---------------- Aufgaben ----------------

    public static object Tasks(HttpContext http, Opts o)
    {
        var space = http.Space();
        var today = Today(space);
        var days = o.Int("days", 3, 1, 14);
        var wanted = o.Longs("lists").ToHashSet();
        var mine = o.Bool("assigned");
        PimStore.EnsureDefaults(space.Db);
        var lists = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO") && Lists.TypeOf(space.Db, c.Id) == Lists.Tasks)
            .Select(c => (Owner: space, Coll: c, Id: c.Id, Shared: false))
            .Concat(SharedLists.WithMe(http).Where(x => x.Coll.Components.Contains("VTODO") && Lists.TypeOf(x.Owner.Db, x.Coll.Id) == Lists.Tasks)
                .Select(x => (Owner: x.Owner, Coll: x.Coll, Id: -(long)x.Share.Id, Shared: true))).ToList();
        var use = wanted.Count == 0 ? lists : lists.Where(x => wanted.Contains(x.Id)).ToList();
        var items = new List<(DateOnly? Due, object Dto)>();
        foreach (var (owner, coll, id, shared) in use)
            foreach (var t in CalendarModel.Tasks(owner, [coll.Id]).Where(t => !t.Completed))
            {
                if (mine && t.Assignee != space.User.Id && t.Assignee != space.User.Login) continue;
                DateOnly? due = t.Due == null ? null : t.DueAllDay ? DateOnly.Parse(t.Due[..10], CultureInfo.InvariantCulture) : DateOnly.FromDateTime(Pukki.Local(t.Due, space.Zone));
                if (due == null || due >= today.AddDays(days)) continue;
                items.Add((due, new
                {
                    id = shared ? id + "/" + t.Uri : t.Id, list = id, listName = coll.Name, color = coll.Color, t.Uri, t.Title, due = Iso(due.Value),
                    time = t.DueAllDay || t.Due == null ? null : Pukki.Local(t.Due, space.Zone).ToString("HH:mm"),
                    overdue = due < today, when = due < today ? "überfällig" : PukkiParse.DayText(due.Value, today), t.Priority, t.Assignee,
                }));
            }
        // Fällige Wiedervorlage-Mails dazu
        var followUps = new List<object>();
        if (o.Bool("mail", true))
            foreach (var (m, due) in MailStore.FollowUps(space.Db))
                if (due != null && DateOnly.TryParse(due, CultureInfo.InvariantCulture, out var d) && d < today.AddDays(days))
                    followUps.Add(new { m.Account, m.Folder, m.Uid, m.MessageId, m.Subject, from = m.From?.Name ?? m.From?.Address, due = Iso(d), overdue = d < today, when = d < today ? "überfällig" : PukkiParse.DayText(d, today) });
        return new
        {
            items = items.OrderBy(x => x.Due).Select(x => x.Dto).ToList(),
            followUps,
            lists = lists.Select(x => new { id = x.Id, x.Coll.Name, x.Coll.Color, x.Shared }).ToList(),
            // Für die Schnelleingabe: die erste eigene Liste
            defaultList = lists.FirstOrDefault(x => !x.Shared).Id,
        };
    }

    // ---------------- Einkaufsliste ----------------

    public static object Shopping(HttpContext http, Opts o)
    {
        var space = http.Space();
        PimStore.EnsureDefaults(space.Db);
        var lists = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO") && Lists.TypeOf(space.Db, c.Id) == Lists.Shop)
            .Select(c => (Owner: space, Coll: c, Id: c.Id))
            .Concat(SharedLists.WithMe(http).Where(x => x.Coll.Components.Contains("VTODO") && Lists.TypeOf(x.Owner.Db, x.Coll.Id) == Lists.Shop)
                .Select(x => (Owner: x.Owner, Coll: x.Coll, Id: -(long)x.Share.Id))).ToList();
        var pick = o.Long("list");
        var use = pick is long p && lists.Any(x => x.Id == p) ? lists.First(x => x.Id == p) : lists.FirstOrDefault();
        if (use == default) return new { list = (long?)null, items = Array.Empty<object>(), lists = Array.Empty<object>() };
        var items = CalendarModel.Tasks(use.Owner, [use.Coll.Id]).Where(t => !t.Completed)
            .Select(t => new { id = use.Owner.User.Id == space.User.Id ? t.Id : use.Id + "/" + t.Uri, t.Uri, text = ((t.Qty + " " + t.Unit).Trim() + " " + (t.Item ?? t.Title)).Trim(), t.Section })
            .OrderBy(x => x.Section ?? "￿").ThenBy(x => x.text, StringComparer.Create(De, true)).ToList();
        return new { list = use.Id, name = use.Coll.Name, items, lists = lists.Select(x => new { id = x.Id, x.Coll.Name }).ToList() };
    }

    // ---------------- E-Mail ----------------

    public static object Mail(HttpContext http, Opts o)
    {
        var space = http.Space();
        var db = space.Db;
        var count = o.Int("count", 5, 1, 20);
        var mode = o.Str("mode", "all");   // all | focused | category:<id>
        var accounts = o.Strs("accounts");
        var use = space.Settings.MailAccounts.Where(a => accounts.Count == 0 || accounts.Contains(a.Id)).ToList();
        var inboxes = use.SelectMany(a => MailStore.Folders(db, a.Id).Where(f => f.Special == "inbox").Select(f => a.Id + "|" + f.Path)).ToList();
        if (inboxes.Count == 0) return new { unread = 0, items = Array.Empty<object>(), accounts = use.Select(a => new { a.Id, a.Name }) };
        var c = new MailCriteria { Folders = inboxes, Unread = true };
        if (mode.StartsWith("category:")) c.Categories = [mode["category:".Length..]];
        var (_, unread, rows) = MailStore.Query(db, c, 0, mode == "focused" ? count * 4 : count);
        if (mode == "focused") rows = rows.Where(m => m.Focus != false).Take(count).ToList();
        var names = use.ToDictionary(a => a.Id, a => a.Name);
        return new
        {
            unread = mode == "focused" ? use.Sum(a => MailStore.Folders(db, a.Id).Where(f => f.Special == "inbox").Sum(f => MailStore.FocusUnread(db, a.Id, f.Path).Focused)) : unread,
            items = rows.Select(m => new { m.Account, accountName = names.GetValueOrDefault(m.Account), m.Folder, m.Uid, m.MessageId, m.Subject, from = m.From?.Name is { Length: > 0 } n ? n : m.From?.Address, m.Date, m.Preview, m.Flagged, m.HasAttachments }),
            accounts = use.Select(a => new { a.Id, a.Name }),
        };
    }

    // ---------------- Chat ----------------

    public static object Chat(HttpContext http, ChatStore chat, UserStore users, Opts o)
    {
        var me = http.Space().User.Id;
        Pukki.Ensure(chat, me);
        var count = o.Int("count", 6, 1, 20);
        string NameOf(string id) => id == Pukki.Id ? Pukki.Name : users.ById(id)?.DisplayName ?? "Gelöschter Benutzer";
        var rooms = chat.Rooms(me);
        var shown = rooms.Where(r => r.Unread > 0 || r.Mentioned > 0).Concat(rooms.Where(r => r.Unread == 0 && r.Mentioned == 0)).Take(count).Select(r => new
        {
            r.Id, title = r.Kind == ChatStore.Direct ? NameOf(r.Members.FirstOrDefault(m => m.Id != me)?.Id ?? "") : r.Title ?? "", r.Kind, r.Unread, r.Mentioned,
            other = r.Kind == ChatStore.Direct ? r.Members.FirstOrDefault(m => m.Id != me)?.Id : null,
            last = r.Last == null || r.Last.Deleted ? null : new { r.Last.Created, from = NameOf(r.Last.Sender), text = r.Last.Text is { Length: > 140 } t ? t[..140] + " …" : r.Last.Text },
        }).ToList();
        return new { unread = chat.Unread(me), rooms = shown, pukki = chat.DirectRoom(me, Pukki.Id) };
    }

    // ---------------- Pukki ----------------

    /// <summary>Morgens der Überblick für heute, ab dem Nachmittag der für morgen - die Frage selbst stellt die Oberfläche in Pukkis Raum.</summary>
    public static object PukkiText(HttpContext http, ChatStore chat, UserStore users)
    {
        var space = http.Space();
        var now = Pukki.Now(space);
        var today = DateOnly.FromDateTime(now);
        var day = now.Hour >= 17 ? today.AddDays(1) : today;
        using var voice = Pukki.ForAccount(space);
        var text = Pukki.Overview(http.RequestServices, space, day, today) ?? (day == today
            ? Pukki.L("Heute steht nichts im Kalender und nichts ist fällig.", "Nothing in the calendar and nothing due today.")
            : Pukki.L("Morgen steht nichts an.", "Nothing planned for tomorrow."));
        Pukki.Ensure(chat, space.User.Id);
        return new { text, room = chat.DirectRoom(space.User.Id, Pukki.Id), tomorrow = day != today };
    }

    // ---------------- Wetter ----------------

    public sealed record Day(string Date, int Code, string Text, double Max, double Min, int? Rain);
    public sealed record Hour(string Time, double Temp, int Code, int? Rain);
    public sealed record Forecast(Hour? Now, List<Day> Days, List<Hour> Hours);
    private static readonly ConcurrentDictionary<string, (DateTime At, Forecast F)> WeatherCache = new();

    /// <summary>Open-Meteo: 7 Tage und die Stunden von heute (20 Minuten zwischengespeichert). Null, wenn nichts kommt.</summary>
    public static async Task<Forecast?> ForecastAsync(HttpClient http, double lat, double lon, CancellationToken ct)
    {
        var key = $"{lat:0.##}|{lon:0.##}";
        if (WeatherCache.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(20)) return c.F;
        var url = "https://api.open-meteo.com/v1/forecast" + $"?latitude={lat.ToString("0.####", CultureInfo.InvariantCulture)}&longitude={lon.ToString("0.####", CultureInfo.InvariantCulture)}"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&hourly=temperature_2m,weather_code,precipitation_probability&forecast_days=7&timezone=auto";
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
            var d = doc.RootElement.GetProperty("daily");
            var days = new List<Day>();
            var dates = d.GetProperty("time");
            for (var i = 0; i < dates.GetArrayLength(); i++)
            {
                double N(string k) => d.GetProperty(k)[i].ValueKind == JsonValueKind.Number ? d.GetProperty(k)[i].GetDouble() : 0;
                var code = (int)N("weather_code");
                var rain = d.TryGetProperty("precipitation_probability_max", out var pp) && pp[i].ValueKind == JsonValueKind.Number ? (int?)pp[i].GetInt32() : null;
                days.Add(new Day(dates[i].GetString()!, code, JournalLookup.Describe(code), N("temperature_2m_max"), N("temperature_2m_min"), rain));
            }
            var h = doc.RootElement.GetProperty("hourly");
            var hours = new List<Hour>();
            var times = h.GetProperty("time");
            var todayStr = days.FirstOrDefault()?.Date ?? "";
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                var t = times[i].GetString()!;
                if (!t.StartsWith(todayStr)) continue;
                var temp = h.GetProperty("temperature_2m")[i]; var code = h.GetProperty("weather_code")[i]; var rain = h.GetProperty("precipitation_probability")[i];
                hours.Add(new Hour(t[11..16], temp.ValueKind == JsonValueKind.Number ? temp.GetDouble() : 0, code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 0, rain.ValueKind == JsonValueKind.Number ? rain.GetInt32() : null));
            }
            // „Jetzt“: die laufende Stunde (Ortszeit des Ortes, timezone=auto)
            var localNow = doc.RootElement.TryGetProperty("utc_offset_seconds", out var off) ? DateTime.UtcNow.AddSeconds(off.GetInt32()) : DateTime.UtcNow;
            var now = hours.FirstOrDefault(x => x.Time == localNow.ToString("HH:00")) ?? hours.FirstOrDefault();
            var f = new Forecast(now, days, hours);
            WeatherCache[key] = (DateTime.UtcNow, f);
            return f;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException or InvalidOperationException) { return c.F; }
    }

    public static async Task<object> WeatherAsync(HttpContext http, IHttpClientFactory f, CancellationToken ct)
    {
        var space = http.Space();
        var b = PukkiBriefing.Of(space.Db);
        if (b.Lat is not double lat || b.Lon is not double lon) return new { configured = false };
        var w = await ForecastAsync(f.CreateClient(), lat, lon, ct);
        if (w == null) return new { configured = true, place = b.Place, error = "Keine Wetterdaten bekommen." };
        // Termine heute im Freien? Einfach: Termine heute mit Uhrzeit, dazu die Regenwahrscheinlichkeit zu der Stunde
        var today = Today(space);
        var hints = Pukki.DayEvents(http.RequestServices, space, today).Where(e => !e.AllDay)
            .Select(e => new { e.Title, start = e.Start.ToString("HH:mm"), rain = w.Hours.FirstOrDefault(h => h.Time == e.Start.ToString("HH:00"))?.Rain })
            .Where(x => x.rain is >= 50).ToList();
        return new { configured = true, place = b.Place, w.Now, w.Days, w.Hours, hints };
    }

    // ---------------- Schlagzeilen ----------------

    public static async Task<object> NewsAsync(HttpContext http, IHttpClientFactory f, Opts o, CancellationToken ct)
    {
        var space = http.Space();
        var s = PukkiBriefing.Of(space.Db);
        if (s.Sources.Count == 0) return new { configured = false, items = Array.Empty<object>() };
        var count = o.Int("count", s.Count, 1, 15);
        var client = f.CreateClient();
        var feeds = await Task.WhenAll(s.Sources.Select(x => PukkiBriefing.Presets.FirstOrDefault(p => p.Id == x) is { } p
            ? PukkiBriefing.FeedAsync(client, p.Url, p.Name, ct) : PukkiBriefing.FeedAsync(client, x, new Uri(x).Host.Replace("www.", ""), ct)));
        var picked = PukkiBriefing.Pick(feeds.ToList(), new PukkiBriefing.Settings { Count = count, Prefer = s.Prefer, Exclude = s.Exclude });
        return new { configured = true, items = picked.Select(h => new { h.Title, h.Link, h.At, h.Source }) };
    }

    // ---------------- Tagebuch ----------------

    public static object Journal(HttpContext http, Opts o)
    {
        var space = http.Space();
        var today = Today(space);
        var scopes = JournalApi.Scopes(http);
        var pick = o.Long("journal");
        var use = pick is long p && scopes.Any(s => s.PublicId == p) ? scopes.Where(s => s.PublicId == p).ToList() : scopes;
        var all = use.SelectMany(s => s.Entries().Select(e => (Scope: s, Entry: e))).ToList();
        object Dto((JournalApi.Scope Scope, JournalEntry Entry) x)
        {
            var pv = JournalApi.Preview(x.Entry);
            var img = x.Entry.Media.FirstOrDefault(m => m.Mime.StartsWith("image/"));
            return new { pv.Id, journal = x.Scope.PublicId, pv.Uri, pv.Date, pv.Title, snippet = pv.Snippet.Length > 160 ? pv.Snippet[..160].TrimEnd() + " …" : pv.Snippet, pv.Mood, pv.Weather, place = pv.Place?.Name, image = img?.Path, photos = x.Entry.Media.Count(m => m.Mime.StartsWith("image/")) };
        }
        var todays = all.Where(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(x.Entry.Date, space.Zone)) == today).OrderBy(x => x.Entry.Date).Select(Dto).ToList();
        var back = JournalApi.OnThisDay(all.Select(x => x.Entry), today, space.Zone)
            .Select(g => new { g.Label, day = Iso(g.Day), entries = g.Entries.Select(e => Dto(all.First(x => x.Entry.Id == e.Id))).ToList() }).ToList();
        // Serie: Tage in Folge mit Eintrag (eigene Tagebücher), gestern zählt noch
        var days = all.Where(x => x.Scope.Own).Select(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(x.Entry.Date, space.Zone))).ToHashSet();
        var streak = 0;
        for (var d = days.Contains(today) ? today : today.AddDays(-1); days.Contains(d); d = d.AddDays(-1)) streak++;
        return new { today = todays, back, streak, journals = scopes.Select(s => new { id = s.PublicId, s.Coll.Name, s.Coll.Color, s.CanWrite }).ToList(), writeTo = scopes.FirstOrDefault(s => s.Own)?.PublicId };
    }

    // ---------------- Fotos ----------------

    public static async Task<object> PhotosAsync(HttpContext http, ServerConfig config, Opts o)
    {
        var space = http.Space();
        var today = Today(space);
        var all = await GalleryTimeline.AllAsync(http, config);
        var zone = space.Zone;
        var hits = all.Where(e => e.Mime?.StartsWith("image/") == true || e.Ext is ".jpg" or ".jpeg" or ".heic" or ".png" or ".webp")
            .Select(e => (E: e, D: DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(e.Taken).UtcDateTime, zone))))
            .Where(x => x.D.Month == today.Month && x.D.Day == today.Day && x.D.Year < today.Year)
            .OrderByDescending(x => x.D).ThenBy(x => x.E.Taken).ToList();
        // Je Jahr höchstens einige, damit es ein bunter Rückblick bleibt
        var perYear = o.Int("perYear", 6, 1, 30);
        var years = hits.GroupBy(x => x.D.Year).OrderByDescending(g => g.Key).Select(g => new
        {
            year = g.Key, yearsAgo = today.Year - g.Key, total = g.Count(),
            items = g.Take(perYear).Select(x => new { path = x.E.Id, x.E.Name, x.E.Etag, mime = x.E.Mime, taken = x.E.Taken, width = x.E.Meta?.Width, height = x.E.Meta?.Height }).ToList(),
        }).ToList();
        // Nichts von diesem Tag: die neuesten Fotos, damit das Widget nie leer bleibt
        var recent = hits.Count > 0 ? [] : all.Where(e => e.Mime?.StartsWith("image/") == true).OrderByDescending(e => e.Taken).Take(perYear)
            .Select(e => new { path = e.Id, e.Name, e.Etag, mime = e.Mime, taken = e.Taken, width = e.Meta?.Width, height = e.Meta?.Height }).ToList();
        return new { years, total = hits.Count, recent };
    }

    // ---------------- Notizen ----------------

    public static object Notes(HttpContext http, Opts o)
    {
        var space = http.Space();
        var notes = NotesDb.Of(space);
        var count = o.Int("count", 6, 1, 20);
        var onlyFav = o.Bool("favorites");
        var all = notes.GetAllActive();
        var fav = all.Where(n => n.Important).ToList();
        var rest = onlyFav ? [] : all.Where(n => !n.Important).ToList();
        object Dto(NoteRow n) => new { n.Id, title = n.DisplayTitle, n.Snippet, n.Modified, n.Important, n.Color, n.Tags, images = n.Images };
        return new { items = fav.Concat(rest).Take(count).Select(Dto).ToList(), favorites = fav.Count, total = all.Count };
    }

    // ---------------- Dateien ----------------

    public static object Files(HttpContext http, Opts o)
    {
        var space = http.Space();
        var db = space.Db;
        var count = o.Int("count", 6, 1, 20);
        var onlyFav = o.Bool("favorites");
        bool Hidden(string p) => FileStore.InRoot(p, space.Settings.Notizi.Root) || FileStore.InRoot(p, JournalMediaStore.Root) || FileStore.InRoot(p, FileStore.SystemVault);
        var favs = FileStore.Favorites(db).Where(f => !Hidden(f.Path)).Select(f => new { f.Path, f.Name, f.IsDir, f.Size, f.Mime, f.Modified, f.Etag, f.FileId, favorite = true, used = (DateTime?)null }).ToList();
        var recent = onlyFav ? [] : FileStore.Recent(db).Where(x => !Hidden(x.File.Path) && favs.All(f => f.Path != x.File.Path))
            .Select(x => new { x.File.Path, x.File.Name, x.File.IsDir, x.File.Size, x.File.Mime, x.File.Modified, x.File.Etag, x.File.FileId, favorite = false, used = (DateTime?)x.Used }).ToList();
        return new { items = favs.Concat(recent).Take(count).ToList(), favorites = favs.Count };
    }

    // ---------------- Geburtstage ----------------

    private static (DateOnly Day, int? Age)? NextBirthday(string? birthday, DateOnly today)
    {
        if (birthday == null || !DateOnly.TryParse(birthday, CultureInfo.InvariantCulture, out var b)) return null;
        DateOnly Make(int y) => new(y, b.Month, b.Month == 2 && b.Day == 29 && !DateTime.IsLeapYear(y) ? 28 : b.Day);
        var next = Make(today.Year);
        if (next < today) next = Make(today.Year + 1);
        return (next, b.Year > 1900 ? next.Year - b.Year : null);
    }

    public static object Birthdays(HttpContext http, UserStore users, Opts o)
    {
        var space = http.Space();
        var today = Today(space);
        var days = o.Int("days", 30, 1, 365);
        PimStore.EnsureDefaults(space.Db);
        var books = PimStore.Collections(space.Db, PimStore.AddressBook).Select(c => c.Id).ToList();
        var list = new List<(DateOnly Day, object Dto, string[] Names)>();
        // Benutzer dieses Servers mit passendem Namen: dann „Glückwunsch im Chat“
        var people = users.All().Where(u => u.Id != space.User.Id).Select(u => (u.Id, u.DisplayName)).ToList();
        foreach (var x in ContactModel.All(space, books))
            if (NextBirthday(x.Birthday, today) is { } nb && nb.Day < today.AddDays(days))
            {
                var chatWith = people.FirstOrDefault(p => string.Equals(p.DisplayName, x.DisplayName, StringComparison.OrdinalIgnoreCase)).Id;
                list.Add((nb.Day, new { name = x.DisplayName, day = Iso(nb.Day), inDays = nb.Day.DayNumber - today.DayNumber, age = nb.Age, contact = x.Id, email = x.Emails.FirstOrDefault()?.Value, chatWith }, new[] { x.Given, x.Family, x.Nickname, x.DisplayName }.Where(s => !string.IsNullOrEmpty(s) && s.Length >= 3).ToArray()));
            }
        for (var day = today; day < today.AddDays(Math.Min(days, 60)); day = day.AddDays(1))
            foreach (var e in Pukki.DayEvents(http.RequestServices, space, day).Where(e => Regex.IsMatch(e.Title, "geburtstag|birthday|🎂", RegexOptions.IgnoreCase)))
                if (!list.Any(l => l.Day == day && l.Names.Any(n => e.Title.Contains(n, StringComparison.OrdinalIgnoreCase))))
                    list.Add((day, new { name = e.Title, day = Iso(day), inDays = day.DayNumber - today.DayNumber, age = (int?)null, contact = (string?)null, email = (string?)null, chatWith = (string?)null }, []));
        return new { items = list.OrderBy(l => l.Day).Select(l => l.Dto).ToList() };
    }

    // ---------------- Countdown ----------------

    /// <summary>„Noch 12 Tage bis Urlaub“: der nächste Termin, dessen Titel das eingestellte Wort enthält, sonst der nächste ganztägige Termin über mehrere Tage.</summary>
    public static object Countdown(HttpContext http, Opts o)
    {
        var space = http.Space();
        var today = Today(space);
        var word = o.Str("title").Trim();
        var zone = space.Zone;
        var from = Ics.ToUtc(today.ToDateTime(TimeOnly.MinValue), zone);
        var to = Ics.ToUtc(today.AddDays(400).ToDateTime(TimeOnly.MinValue), zone);
        var hits = new List<(DateOnly Day, string Title, string? Cal)>();
        foreach (var (owner, coll, shared) in Pukki.Calendars(http.RequestServices, space))
            foreach (var e in CalendarModel.Events(owner, from, to, [coll.Id]))
            {
                var day = e.AllDay && DateOnly.TryParse(e.Start, CultureInfo.InvariantCulture, out var s) ? s : DateOnly.FromDateTime(Pukki.Local(e.Start, zone));
                if (day <= today) continue;
                var match = word.Length > 0 ? e.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
                    : e.AllDay && DateOnly.TryParse(e.End, CultureInfo.InvariantCulture, out var en) && en.DayNumber - day.DayNumber >= 2;
                if (match) hits.Add((day, e.Title, shared ? coll.Name : null));
            }
        var next = hits.OrderBy(h => h.Day).FirstOrDefault();
        return next == default ? new { found = false, word } : new { found = true, word, next.Title, day = Iso(next.Day), days = next.Day.DayNumber - today.DayNumber, calendar = next.Cal };
    }

    // ---------------- System (nur Verwaltung) ----------------

    public static object System(HttpContext http, ServerConfig config, S3Mirror mirror, S3Backup backup)
    {
        var s = config.Current;
        var st = mirror.Status;
        var vol = mirror.LocalVolume();
        return new
        {
            version = SettingsApi.Version(),
            storage = new { on = s.S3Mirror, target = s.StorageTarget, st.Running, st.LastRun, st.LastError, st.Pending, notHome = mirror.NotHomeRecent().Count },
            backup = new { enabled = s.S3Backup, backup.Status.LastRun, backup.Status.LastError, blocked = backup.Blocked(), own = s.BackupOwnTarget },
            volume = vol.Total > 0 ? new { vol.Total, vol.Free } : null,
            // Zugangsdaten für den Notfall: nie abgeholt oder veraltet
            profile = new { at = s.StorageProfileAt, current = s.StorageProfileStamp != null && s.StorageProfileStamp == StorageProfile.Stamp(config) },
        };
    }

    // ---------------- Speicher ----------------

    public static object Storage(HttpContext http)
    {
        var db = http.Space().Db;
        return new { used = UserStorage.Used(db), database = UserStorage.Database(db), files = FileStore.Usage(db), trash = FileStore.TrashUsage(db), versions = FileStore.VersionUsage(db), quota = FileStore.Quota(db) };
    }
}
