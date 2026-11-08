using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Mail;

namespace DmOffice.Pim;

/// <summary>
/// Abgleich eines Microsoft-Kontos in beide Richtungen (docs/microsoft-graph-konzept.md):
/// 1. Zuordnung: je Kalender, Kontaktordner und To-Do-Liste bei Microsoft eine lokale Sammlung (eingeschaltete Bereiche).
/// 2. Abholen: Kontakte und Aufgaben ueber Delta, Termine ueber die Liste mit changeKey (nur Geaendertes vollstaendig).
/// 3. Zurueckschreiben: was in graph_outbox wartet (POST, PATCH, DELETE).
/// Konflikte (beide Seiten seit dem letzten Abgleich geaendert): die neuere Aenderung gewinnt; geloescht gegen geaendert:
/// die Aenderung gewinnt.
/// </summary>
public sealed class GraphEngine
{
    private readonly UserSpace _space;
    private readonly MailAccount _account;
    private readonly GraphClient _graph;
    private UserDb Db => _space.Db;
    private int _fetched, _sent;

    /// <summary>Protokoll: Ergebnis je Sammlung (Information), Fehler (Warnung).</summary>
    public ILogger? Log { get; init; }
    /// <summary>Woran gerade gearbeitet wird (Aktivitaet).</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>Wie weit Ausnahmen von Serien gesucht werden (Graph liefert Vorkommen nur fuer einen Zeitraum).</summary>
    public static readonly TimeSpan Past = TimeSpan.FromDays(365), Ahead = TimeSpan.FromDays(2 * 365);

    public GraphEngine(UserSpace space, MailAccount account, GraphClient graph)
    {
        _space = space;
        _account = account;
        _graph = graph;
    }

    private bool Enabled(string kind) => kind switch
    {
        Graph.Calendar => _account.GraphCalendars,
        Graph.Contacts => _account.GraphContacts,
        Graph.Tasks => _account.GraphTasks,
        _ => false,
    };

    /// <summary>Alles einmal: zuordnen, abholen (pull), zurueckschreiben. Fehler je Sammlung stehen in LastError.</summary>
    public async Task RunAsync(bool pull, CancellationToken ct)
    {
        if (pull) await AssignAsync(ct);
        // Aufgaben und Kontakte zuerst: Delta ist schnell, Kalender brauchen beim ersten Mal viele Anfragen
        foreach (var link in Graph.All(Db).Where(l => l.Account == _account.Id).OrderBy(l => l.Kind == Graph.Calendar).ToList())
        {
            var name = link.Kind + " \"" + (PimStore.Find(Db, link.Collection)?.Name ?? link.RemoteName) + "\"";
            // Im Protokoll ohne Namen der Liste (der Admin liest mit)
            var logName = link.Kind;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _fetched = _sent = 0;
            try
            {
                Progress?.Invoke(name);
                if (pull) await PullAsync(link, ct);
                await PushAsync(link, ct);
                var (_, failing) = Graph.Waiting(Db, link.Collection);
                var error = failing > 0 ? Graph.Outbox(Db, link.Collection).FirstOrDefault(o => o.Tries >= 3)?.Error : null;
                Graph.Update(Db, link.Collection, l => { if (pull) l.LastSync = DateTime.UtcNow; l.LastError = error; });
                if (_fetched + _sent > 0 || error != null)
                    Log?.LogInformation("Microsoft-Abgleich {Account} {Kind}: {Fetched} geholt, {Sent} geschickt ({Ms} ms){Error}",
                        LogNames.Of(_space, _account), logName, _fetched, _sent, watch.ElapsedMilliseconds, error == null ? "" : " - " + error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (MailKit.Security.AuthenticationException ex)
            {
                // Anmeldung abgelaufen: gilt fuer alle Sammlungen des Kontos
                foreach (var l in Graph.All(Db).Where(l => l.Account == _account.Id))
                    Graph.Update(Db, l.Collection, x => x.LastError = ex.Message);
                Log?.LogWarning("Microsoft-Abgleich {Account}: {Error}", LogNames.Of(_space, _account), ex.Message);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Auch Unerwartetes (kaputte Antwort, Fehler beim Umwandeln) nur fuer diese Sammlung - die anderen laufen weiter
                var msg = ex is TaskCanceledException ? "Zeitüberschreitung bei Microsoft." : ex is HttpRequestException ? "Keine Verbindung zu Microsoft." : ex.Message;
                Graph.Update(Db, link.Collection, l => { l.LastSync = DateTime.UtcNow; l.LastError = msg; });
                if (ex is GraphException or HttpRequestException or TaskCanceledException)
                    Log?.LogWarning("Microsoft-Abgleich {Account} {Kind}: {Error}", LogNames.Of(_space, _account), logName, msg);
                else
                    Log?.LogWarning(ex, "Microsoft-Abgleich {Account} {Kind}: unerwarteter Fehler", LogNames.Of(_space, _account), logName);
            }
        }
    }

    // ---------------- Zuordnung ----------------

    private sealed record Remote(string Id, string Name, string? Color);

    private async Task<List<Remote>> RemotesAsync(string kind, CancellationToken ct)
    {
        switch (kind)
        {
            case Graph.Calendar:
            {
                var (items, _) = await _graph.PagesAsync("/me/calendars?$select=id,name,hexColor,canEdit", ct);
                return items.Select(c => new Remote(Str(c, "id")!, Str(c, "name") ?? "Kalender", Str(c, "hexColor") is { Length: 7 } hc ? hc : null)).ToList();
            }
            case Graph.Tasks:
            {
                var (items, _) = await _graph.PagesAsync("/me/todo/lists", ct);
                // "Gekennzeichnete E-Mails" macht Outlook aus Mails - dort lassen sich keine Aufgaben anlegen
                return items.Where(l => Str(l, "wellknownListName") != "flaggedEmails")
                    .Select(l => new Remote(Str(l, "id")!, Str(l, "displayName") ?? "Aufgaben", null)).ToList();
            }
            default:
            {
                var result = new List<Remote>();
                // Der Standardordner "Kontakte" steht nicht in /me/contactFolders - seine Kennung ueber den bekannten Namen
                try
                {
                    var main = await _graph.GetAsync("/me/contactFolders/contacts?$select=id,displayName", ct);
                    if (Str(main, "id") is { } id) result.Add(new Remote(id, Str(main, "displayName") ?? "Kontakte", null));
                }
                catch (GraphException e) when (e.Status is 400 or 404)
                {
                    var (first, _) = await _graph.PagesAsync("/me/contacts?$top=1&$select=parentFolderId", ct);
                    if (first.Count > 0 && Str(first[0], "parentFolderId") is { } pid) result.Add(new Remote(pid, "Kontakte", null));
                }
                var (items, _) = await _graph.PagesAsync("/me/contactFolders?$select=id,displayName", ct);
                result.AddRange(items.Where(f => result.All(r => r.Id != Str(f, "id"))).Select(f => new Remote(Str(f, "id")!, Str(f, "displayName") ?? "Kontakte", null)));
                return result;
            }
        }
    }

    /// <summary>Lokale Sammlungen zu den Kalendern, Ordnern und Listen bei Microsoft anlegen, umbenennen, entfernen.</summary>
    public async Task AssignAsync(CancellationToken ct)
    {
        foreach (var kind in new[] { Graph.Calendar, Graph.Contacts, Graph.Tasks })
        {
            if (!Enabled(kind)) { Graph.UnlinkAccount(Db, _account.Id, kind); continue; }
            var links = Graph.All(Db).Where(l => l.Account == _account.Id && l.Kind == kind).ToList();
            List<Remote> remotes;
            try { remotes = await RemotesAsync(kind, ct); }
            catch (Exception ex) when (ex is GraphException or HttpRequestException)
            {
                // Liste nicht lesbar: nichts loesen, nur zeigen - die Bereiche fuer sich weiter abgleichen
                var msg = ex is HttpRequestException ? "Keine Verbindung zu Microsoft." : ex.Message;
                foreach (var l in links) Graph.Update(Db, l.Collection, x => x.LastError = msg);
                Log?.LogWarning("Microsoft-Abgleich {Account}: {Kind} nicht lesbar: {Error}", LogNames.Of(_space, _account), kind, msg);
                continue;
            }
            foreach (var l in links.Where(l => remotes.All(r => r.Id != l.RemoteId) || PimStore.Find(Db, l.Collection) == null))
                Graph.Unlink(Db, l);
            foreach (var r in remotes)
            {
                if (links.FirstOrDefault(l => l.RemoteId == r.Id) is { } link && PimStore.Find(Db, link.Collection) is { } coll)
                {
                    if (link.RemoteName != r.Name)
                    {
                        PimStore.UpdateCollection(Db, coll.Id, UniqueName(kind, r.Name, coll.Id), r.Color, null);
                        Graph.Update(Db, coll.Id, l => l.RemoteName = r.Name);
                    }
                    continue;
                }
                var storeKind = kind == Graph.Contacts ? PimStore.AddressBook : PimStore.Calendar;
                var created = PimStore.CreateCollection(Db, storeKind, "ms-" + Guid.NewGuid().ToString("N")[..12], UniqueName(kind, r.Name, null),
                    r.Color ?? (kind == Graph.Contacts ? null : "#0F6CBD"), kind switch { Graph.Calendar => "VEVENT", Graph.Tasks => "VTODO", _ => "" });
                Graph.Add(Db, new GraphLink { Collection = created.Id, Account = _account.Id, Kind = kind, RemoteId = r.Id, RemoteName = r.Name });
            }
        }
    }

    /// <summary>Name vom Microsoft-Konto; nur bei Gleichheit mit einer anderen Sammlung mit Zusatz.</summary>
    private string UniqueName(string kind, string name, long? self)
    {
        var storeKind = kind == Graph.Contacts ? PimStore.AddressBook : PimStore.Calendar;
        var taken = PimStore.Collections(Db, storeKind).Any(c => c.Id != self && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return taken ? $"{name} (Outlook)" : name;
    }

    // ---------------- Abholen ----------------

    public async Task PullAsync(GraphLink link, CancellationToken ct)
    {
        var coll = PimStore.Find(Db, link.Collection) ?? throw new KeyNotFoundException();
        if (link.Kind == Graph.Calendar) { await PullEventsAsync(link, coll, ct); return; }

        var start = link.Kind == Graph.Tasks
            ? $"/me/todo/lists/{Esc(link.RemoteId)}/tasks/delta"
            : $"/me/contactFolders/{Esc(link.RemoteId)}/contacts/delta";
        bool full = link.DeltaLink == null;
        List<JsonElement> items;
        string? delta;
        try { (items, delta) = await _graph.PagesAsync(link.DeltaLink ?? start, ct); }
        catch (GraphException e) when (e.Resync && link.DeltaLink != null)
        {
            full = true;
            (items, delta) = await _graph.PagesAsync(start, ct);
        }
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            if (Str(item, "id") is not { } id) continue;
            if (item.TryGetProperty("@removed", out _)) { RemoteDeleted(coll, id); continue; }
            seen.Add(id);
            Apply(coll, id, GraphConvert.ChangeKey(item), GraphConvert.Modified(item), Str(item, "title") ?? Str(item, "displayName"),
                existing => link.Kind == Graph.Tasks ? GraphConvert.TaskToIcs(item, existing, _space.Zone) : GraphConvert.ContactToVcf(item, existing));
        }
        // Von vorn geholt: was jetzt fehlt, gibt es bei Microsoft nicht mehr
        if (full)
            foreach (var row in Graph.Map(Db, coll.Id).Where(r => !seen.Contains(r.RemoteId)))
                RemoteDeleted(coll, row.RemoteId);
        Graph.Update(Db, coll.Id, l => l.DeltaLink = delta);
    }

    private async Task PullEventsAsync(GraphLink link, Collection coll, CancellationToken ct)
    {
        // Ohne Zeitraum gibt es in v1.0 kein Delta - die Liste kommt gleich mit allen Feldern (eine Anfrage je 100 Termine),
        // fuer geaenderte Serien werden zusaetzlich die Vorkommen geholt
        var (items, _) = await _graph.PagesAsync($"/me/calendars/{Esc(link.RemoteId)}/events?$select={EventFields}&$top=100", ct);
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            if (Str(item, "id") is not { } id) continue;
            seen.Add(id);
            var ck = GraphConvert.ChangeKey(item);
            if (Graph.MapByRemote(Db, coll.Id, id) is { } row && row.ChangeKey == ck) continue;
            var full = item;
            var exceptions = new List<JsonElement>();
            var cancelled = new List<DateTime>();
            if (Str(full, "type") == "seriesMaster") (exceptions, cancelled) = await InstancesAsync(full, ct);
            Apply(coll, id, GraphConvert.ChangeKey(full) ?? ck, GraphConvert.Modified(full), Str(full, "subject"),
                existing => GraphConvert.EventToIcs(full, exceptions, cancelled, existing));
        }
        foreach (var row in Graph.Map(Db, coll.Id).Where(r => !seen.Contains(r.RemoteId)))
            RemoteDeleted(coll, row.RemoteId);
    }

    private const string EventFields = "id,changeKey,type,iCalUId,subject,body,start,end,isAllDay,location,recurrence,showAs,categories,isReminderOn," +
        "reminderMinutesBeforeStart,attendees,organizer,originalStartTimeZone,originalEndTimeZone,sensitivity,isCancelled,lastModifiedDateTime";

    /// <summary>Ausnahmen einer Serie im Zeitraum und entfallene Vorkommen (nach der Regel erwartet, bei Microsoft nicht da).</summary>
    private async Task<(List<JsonElement> Exceptions, List<DateTime> Cancelled)> InstancesAsync(JsonElement master, CancellationToken ct)
    {
        var from = DateTime.UtcNow - Past;
        var to = DateTime.UtcNow + Ahead;
        var (instances, _) = await _graph.PagesAsync($"/me/events/{Esc(Str(master, "id")!)}/instances?startDateTime={Iso(from)}&endDateTime={Iso(to)}" +
            "&$top=500&$select=id,type,originalStart,start,end,subject,body,location,isAllDay,showAs,categories,isCancelled,isReminderOn,reminderMinutesBeforeStart,sensitivity,lastModifiedDateTime", ct);
        var allDay = master.TryGetProperty("isAllDay", out var ad) && ad.ValueKind == JsonValueKind.True;
        var zone = GraphConvert.ZoneOf(Str(master, "originalStartTimeZone")) ?? TimeZoneInfo.Utc;
        var exceptions = instances.Where(i => Str(i, "type") == "exception").ToList();
        var present = instances.Select(i => GraphConvert.OriginalStart(i, allDay, zone)).OfType<DateTime>().ToList();

        // Erwartete Vorkommen aus der Regel - was davon fehlt, wurde abgesagt (EXDATE)
        var cancelled = new List<DateTime>();
        try
        {
            var ics = GraphConvert.EventToIcs(master, [], [], null);
            var cal = Ical.Net.Calendar.Load(ics);
            foreach (var occ in cal.GetOccurrences(from, to))
            {
                var s = occ.Period.StartTime;
                var expected = allDay ? s.Value.Date : s.AsUtc;
                if (!present.Any(p => allDay ? p == expected : Math.Abs((p - expected).TotalSeconds) < 60)) cancelled.Add(expected);
            }
        }
        catch (Exception) { /* Regel, die Ical.Net nicht versteht: keine Absagen ableiten */ }
        return (exceptions, cancelled);
    }

    /// <summary>
    /// Eine geaenderte oder neue Fassung von Microsoft uebernehmen. Hat sich das Objekt auch lokal geaendert (wartet in der
    /// Warteschlange), gewinnt die neuere Aenderung; ein lokal geloeschtes wird wieder angelegt.
    /// </summary>
    private void Apply(Collection coll, string remoteId, string? changeKey, DateTime? remoteModified, string? title, Func<string?, string> build)
    {
        var row = Graph.MapByRemote(Db, coll.Id, remoteId);
        if (row != null && row.ChangeKey == changeKey && changeKey != null) return;   // eigenes Zurueckschreiben, kein Echo
        var obj = row != null ? PimStore.Object(Db, coll.Id, row.Uri) : null;
        if (row != null && Graph.Pending(Db, coll.Id, row.Uri) is { } pending)
        {
            if (pending.Op == "put" && obj != null && Local(obj) is DateTime local && (remoteModified == null || local > remoteModified))
            {
                Conflict(coll.Id, title, "Fassung von Microsoft verworfen, die neuere aus dmOffice gilt");
                return;   // die lokale geht gleich zu Microsoft
            }
            Graph.Done(Db, coll.Id, row.Uri);
            Conflict(coll.Id, title, pending.Op == "delete"
                ? "in dmOffice gelöscht, bei Microsoft geändert – wieder angelegt"
                : "Fassung aus dmOffice verworfen, die neuere von Microsoft gilt");
        }
        var uri = row?.Uri ?? PimStore.NewUri(coll.Kind == PimStore.AddressBook ? ".vcf" : ".ics");
        var data = build(obj?.Data);
        try
        {
            string etag;
            using (PimStore.Remote()) etag = PimStore.Put(Db, coll, uri, data, zone: _space.Zone);
            Graph.SetMap(Db, coll.Id, uri, remoteId, changeKey, etag);
            _fetched++;
        }
        catch (ArgumentException) { /* ein kaputter Eintrag haelt den Rest nicht auf */ }
    }

    private static DateTime? Local(DavObject obj) =>
        DateTime.TryParse(obj.Modified, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var m) ? m : null;

    /// <summary>Bei Microsoft geloescht. Lokal geaendert (wartet): die Aenderung gewinnt und wird neu angelegt.</summary>
    private void RemoteDeleted(Collection coll, string remoteId)
    {
        if (Graph.MapByRemote(Db, coll.Id, remoteId) is not { } row) return;
        Graph.Unmap(Db, coll.Id, row.Uri);
        if (Graph.Pending(Db, coll.Id, row.Uri) is { Op: "put" }) return;
        Graph.Done(Db, coll.Id, row.Uri);
        using (PimStore.Remote()) PimStore.Delete(Db, coll, row.Uri);
    }

    private void Conflict(long coll, string? title, string what) =>
        Graph.Update(Db, coll, l =>
        {
            l.Conflicts.Insert(0, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} {(string.IsNullOrWhiteSpace(title) ? "(ohne Titel)" : title)}: {what}");
            if (l.Conflicts.Count > 20) l.Conflicts.RemoveRange(20, l.Conflicts.Count - 20);
        });

    // ---------------- Zurueckschreiben ----------------

    private string Items(GraphLink link) => link.Kind switch
    {
        Graph.Tasks => $"/me/todo/lists/{Esc(link.RemoteId)}/tasks",
        Graph.Contacts => $"/me/contactFolders/{Esc(link.RemoteId)}/contacts",
        _ => $"/me/calendars/{Esc(link.RemoteId)}/events",
    };

    private string Item(GraphLink link, string remoteId) => link.Kind switch
    {
        Graph.Tasks => $"/me/todo/lists/{Esc(link.RemoteId)}/tasks/{Esc(remoteId)}",
        Graph.Contacts => $"/me/contacts/{Esc(remoteId)}",
        _ => $"/me/events/{Esc(remoteId)}",
    };

    public async Task PushAsync(GraphLink link, CancellationToken ct)
    {
        var coll = PimStore.Find(Db, link.Collection) ?? throw new KeyNotFoundException();
        foreach (var o in Graph.Outbox(Db, coll.Id))
        {
            try { await PushOneAsync(link, coll, o, ct); }
            catch (GraphException e) when (e.Status is not (401 or 403 or 429 or 0))
            {
                Graph.Failed(Db, coll.Id, o.Uri, e.Message);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
            {
                Graph.Failed(Db, coll.Id, o.Uri, "Lässt sich nicht für Microsoft umwandeln: " + e.Message);
            }
        }
    }

    private async Task PushOneAsync(GraphLink link, Collection coll, Graph.OutboxRow o, CancellationToken ct)
    {
        var row = Graph.MapByUri(Db, coll.Id, o.Uri);
        if (o.Op == "delete")
        {
            if (row != null)
            {
                try { await _graph.SendAsync(HttpMethod.Delete, Item(link, row.RemoteId), null, ct); }
                catch (GraphException e) when (e.Status == 404) { }
                Graph.Unmap(Db, coll.Id, o.Uri);
            }
            Graph.Done(Db, coll.Id, o.Uri, o.At);
            return;
        }

        if (PimStore.Object(Db, coll.Id, o.Uri) is not { } obj) { Graph.Done(Db, coll.Id, o.Uri, o.At); return; }
        // Seit dem letzten Abgleich unveraendert (z. B. nur verschoben und zurueck): nichts zu tun
        if (row != null && row.Etag == obj.Etag) { Graph.Done(Db, coll.Id, o.Uri, o.At); return; }

        Dav.Component? master = null;
        JsonObject body;
        if (link.Kind == Graph.Tasks) body = GraphConvert.TaskToGraph(obj.Data, _space.Zone);
        else if (link.Kind == Graph.Contacts) body = GraphConvert.ContactToGraph(obj.Data);
        else
        {
            var events = Ics.Parse(obj.Data).First(r => r.Name == "VCALENDAR").Of("VEVENT").ToList();
            master = events.FirstOrDefault(e => e.Get("RECURRENCE-ID") == null) ?? events.First();
            body = GraphConvert.EventToGraph(master, _space.Zone);
        }

        JsonElement result;
        if (row != null)
        {
            try { result = await _graph.SendAsync(HttpMethod.Patch, Item(link, row.RemoteId), body, ct); }
            catch (GraphException e) when (e.Status == 404)
            {
                // Bei Microsoft inzwischen geloescht - die lokale Aenderung gewinnt, neu anlegen
                result = await _graph.SendAsync(HttpMethod.Post, Items(link), body, ct);
            }
        }
        else result = await _graph.SendAsync(HttpMethod.Post, Items(link), body, ct);

        var remoteId = Str(result, "id") ?? throw new GraphException(0, null, "Microsoft hat keine Kennung geliefert.");
        var changeKey = GraphConvert.ChangeKey(result);
        if (master != null && master.Get("RRULE") != null)
        {
            await PushOccurrencesAsync(remoteId, obj.Data, ct);
            // Geaenderte Vorkommen aendern auch den Stand der Serie - den aktuellen merken, sonst kaeme sie als Echo zurueck
            changeKey = GraphConvert.ChangeKey(await _graph.GetAsync($"/me/events/{Esc(remoteId)}?$select=id,changeKey", ct)) ?? changeKey;
        }
        Graph.SetMap(Db, coll.Id, o.Uri, remoteId, changeKey, obj.Etag);
        Graph.Done(Db, coll.Id, o.Uri, o.At);
        _sent++;
    }

    /// <summary>Geaenderte Vorkommen (RECURRENCE-ID) und entfallene (EXDATE) einer Serie bei Microsoft nachziehen.</summary>
    private async Task PushOccurrencesAsync(string masterId, string data, CancellationToken ct)
    {
        var events = Ics.Parse(data).First(r => r.Name == "VCALENDAR").Of("VEVENT").ToList();
        var master = events.First(e => e.Get("RECURRENCE-ID") == null);
        var zone = GraphConvert.ZoneOfEvent(master, _space.Zone);
        Ics.Time(master.Get("DTSTART"), zone, out var allDay);

        async Task<string?> FindAsync(DateTime original)
        {
            var (list, _) = await _graph.PagesAsync($"/me/events/{Esc(masterId)}/instances?startDateTime={Iso(original.AddDays(-1))}&endDateTime={Iso(original.AddDays(2))}&$select=id,originalStart,type", ct);
            return list.FirstOrDefault(i => GraphConvert.OriginalStart(i, allDay, zone) is DateTime s
                && (allDay ? s == original.Date : Math.Abs((s - original).TotalSeconds) < 60)) is { ValueKind: JsonValueKind.Object } hit ? Str(hit, "id") : null;
        }

        foreach (var ov in events.Where(e => e.Get("RECURRENCE-ID") != null))
        {
            if (Ics.Time(ov.Get("RECURRENCE-ID"), zone, out _) is not DateTime rid) continue;
            if (await FindAsync(rid) is { } id)
                await _graph.SendAsync(HttpMethod.Patch, $"/me/events/{Esc(id)}", GraphConvert.EventToGraph(ov, _space.Zone, withRecurrence: false), ct);
        }
        foreach (var ex in master.All("EXDATE").ToList())
            foreach (var v in ex.Value.Split(','))
            {
                var p = new Prop("EXDATE", v);
                foreach (var kv in ex.Params) p.Params.Add(kv);
                if (Ics.Time(p, zone, out _) is not DateTime day) continue;
                if (await FindAsync(day) is { } id)
                {
                    try { await _graph.SendAsync(HttpMethod.Delete, $"/me/events/{Esc(id)}", null, ct); }
                    catch (GraphException e) when (e.Status == 404) { }
                }
            }
    }

    // ---------------- Hilfen ----------------

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Esc(string id) => Uri.EscapeDataString(id);
    private static string Iso(DateTime utc) => Uri.EscapeDataString(utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

/// <summary>
/// Gleicht alle Microsoft-Konten mit verbundenen Bereichen ab: wartende Aenderungen gehen gleich (alle 30 Sekunden
/// geprueft) zu Microsoft, abgeholt wird je Konto hoechstens alle 5 Minuten.
/// </summary>
public sealed class GraphSync : BackgroundService
{
    private readonly Spaces _spaces;
    private readonly MicrosoftOAuth _oauth;
    private readonly ILogger<GraphSync> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Dictionary<string, DateTime> _pulled = new();
    private readonly SemaphoreSlim _kick = new(0);

    public static readonly TimeSpan PullEvery = TimeSpan.FromMinutes(5);

    public GraphSync(Spaces spaces, MicrosoftOAuth oauth, ILogger<GraphSync> log)
    {
        _spaces = spaces;
        _oauth = oauth;
        _log = log;
    }

    /// <summary>Gleich abgleichen (nach dem Einschalten oder "Jetzt abgleichen").</summary>
    public void Kick(string accountId)
    {
        lock (_pulled) _pulled.Remove(accountId);
        _kick.Release();
    }

    public GraphClient Client(MailAccount account) => new(_http, ct => _oauth.GraphTokenAsync(account, ct));

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in _spaces.All())
            {
                List<MailAccount> accounts;
                try
                {
                    // Mit Graph-Token immer: die Mail-Kategorien gehen darueber (Mail/GraphCategories.cs)
                    accounts = space.Settings.MailAccounts.Where(GraphCategories.Uses).ToList();
                }
                catch { continue; }
                foreach (var account in accounts)
                {
                    bool pull;
                    lock (_pulled) pull = !_pulled.TryGetValue(account.Id, out var last) || DateTime.UtcNow - last >= PullEvery;
                    if (!pull && !Graph.All(space.Db).Where(l => l.Account == account.Id).Any(l => Graph.Waiting(space.Db, l.Collection).Pending > 0)) continue;
                    if (pull) lock (_pulled) _pulled[account.Id] = DateTime.UtcNow;
                    using var activity = Activity.Start(space.User.Id, Activity.Graph, account.Email);
                    try
                    {
                        if (account.GraphCalendars || account.GraphContacts || account.GraphTasks || Graph.All(space.Db).Any(l => l.Account == account.Id))
                            await new GraphEngine(space, account, Client(account)) { Log = _log, Progress = d => activity.Progress(0, detail: account.Email + ": " + d) }
                                .RunAsync(pull, stop);
                        if (pull)
                        {
                            activity.Progress(0, detail: account.Email + ": Mail-Kategorien");
                            var watch = System.Diagnostics.Stopwatch.StartNew();
                            await GraphCategories.PullAsync(space, account, stop);
                            var state = GraphCategories.StateOf(space.Db, account.Id);
                            if (state.LastError != null) _log.LogWarning("Microsoft-Abgleich {Account} Mail-Kategorien: {Error}", LogNames.Of(space, account), state.LastError);
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                    catch (Exception ex) { activity.Fail(ex.Message); _log.LogWarning(ex, "Microsoft-Abgleich {Account}", LogNames.Of(space, account)); }
                }
            }
            try { await _kick.WaitAsync(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { break; }
        }
    }
}
