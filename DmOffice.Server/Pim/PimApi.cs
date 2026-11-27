using System.Globalization;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Pim;

public static class PimApi
{
    public record SubscribeBody(string Url, string? Name, string? Color, int? Interval);
    public record RemindItem(long Calendar, string Uri);
    /// <summary>
    /// Erinnerungen für mehrere Termine (calbulkremind) oder einen Abo-Termin (calremind). Default: eigene entfernen, Kalender-Standard
    /// gilt. Add: zu den jetzt geltenden hinzufügen statt sie zu ersetzen (Übernahme aus dem Android-Kalender). In geteilten, für
    /// mich nur lesbaren Kalendern gelten sie nur für mich (calremshared).
    /// </summary>
    public record RemindBody(List<RemindItem> Items, List<RemindRule>? Reminders, bool Default = false, bool Add = false);
    public record CollectionBody(string Name, string? Color, string? Description, string? Components, string? CalendarKind = null);
    public record AddressesBody(List<string> Addresses, long? Book);
    public record RestoreBody(string Data);
    public record ListBody(string? Type, List<string>? Sections, string? Done);
    public record ForgetBody(string Item);
    public record ImportBody(string Text, bool Apply);
    public record ShareBody(List<string> Members, List<string>? ReadOnly, List<string>? ShowOnly = null);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/pim").RequireAuthorization();

        api.MapGet("/collections/{kind}", (string kind, HttpContext http, ShareStore shares, UserStore users) =>
        {
            var space = http.Space();
            var db = space.Db;
            PimStore.EnsureDefaults(db);
            var subs = Subscriptions.All(db);
            var links = Graph.All(db);
            var accounts = space.Settings.MailAccounts;
            var k = Kind(kind);
            string? NameOf(string id) => users.ById(id)?.DisplayName;
            // Mitglieder einer Freigabe mit ihrem Recht (ohne "except")
            object Members(Share sh, string? except) => sh.Members.Where(m => m != except)
                .Select(m => new { id = m, name = NameOf(m), readOnly = sh.ReadOnly.Contains(m), level = sh.LevelOf(m) }).Where(m => m.name != null).ToList();
            // Alle Zählungen in einer Abfrage über den Index objects_component (statt drei je Sammlung)
            var counts = db.Query("SELECT collection, component, COUNT(*) FROM objects GROUP BY collection, component",
                r => (Coll: r.GetInt64(0), Comp: r.IsDBNull(1) ? "" : r.GetString(1), N: Convert.ToInt32(r.GetInt64(2))));
            var own = PimStore.Collections(db, k).Select(c => new
            {
                c.Id, c.Uri, c.Name, c.Color, c.Description, components = c.Components.Split(',', StringSplitOptions.RemoveEmptyEntries),
                // Abonniert: schreibgeschuetzt, wird regelmaessig neu geholt
                subscription = subs.FirstOrDefault(s => s.Collection == c.Id) is { } sub
                    ? new { sub.Url, sub.Interval, sub.LastFetch, sub.LastError } : null,
                // Mit Microsoft verbunden: beschreibbar, eigener Abschnitt je Konto
                graph = links.FirstOrDefault(l => l.Collection == c.Id) is { } gl
                    ? new { gl.Account, email = accounts.FirstOrDefault(a => a.Id == gl.Account)?.Email ?? "",
                        // Ueberschrift des Abschnitts: Kontoname aus den Einstellungen ("Kontoname (in der Ordnerliste)"), sonst die Adresse
                        name = accounts.FirstOrDefault(a => a.Id == gl.Account) is { } ga ? (string.IsNullOrWhiteSpace(ga.Name) ? ga.Email : ga.Name) : "",
                        gl.Kind, gl.LastSync, gl.LastError,
                        pending = Graph.Waiting(db, c.Id).Pending } : null,
                count = counts.Where(x => x.Coll == c.Id).Sum(x => x.N),
                // Getrennt gezaehlt: ein Kalender kann Termine und Aufgaben enthalten
                events = counts.Where(x => x.Coll == c.Id && x.Comp == "VEVENT").Sum(x => x.N),
                tasks = counts.Where(x => x.Coll == c.Id && x.Comp == "VTODO").Sum(x => x.N),
                // Aufgabenliste, Einkaufsliste oder Checkliste
                listType = c.Components.Contains("VTODO") ? Lists.TypeOf(db, c.Id) : null,
                // Art des Kalenders (CalendarKinds): normal, Essensplan, ...
                calendarKind = k == PimStore.Calendar && c.Components.Contains("VEVENT") ? CalendarKinds.Of(db, c.Id) : null,
                // Standard-Erinnerung des Kalenders (calremdefault)
                remind = k == PimStore.Calendar && c.Components.Contains("VEVENT") && Features.On(Features.CalRemDefault) ? CalendarReminders.DefaultOf(db, c.Id) : null,
                // Ich teile sie mit ... / sie gehoert ...
                sharedWith = shares.Of(space.User.Id, c.Id) is { } sh ? Members(sh, null) : null,
                owner = (string?)null,
                canWrite = true,
                showOnly = false,
            }).ToList();
            if (k != PimStore.Calendar) return Results.Ok(own);
            // Mit mir geteilte Kalender und Aufgabenlisten (Id negativ)
            var withMe = SharedLists.WithMe(http).Where(x => !PimStore.IsReserved(x.Coll)).Select(x => new
            {
                Id = -(long)x.Share.Id, Uri = "shared-" + x.Share.Id, x.Coll.Name, x.Coll.Color, x.Coll.Description,
                components = x.Coll.Components.Split(',', StringSplitOptions.RemoveEmptyEntries),
                subscription = (object?)null,
                count = Convert.ToInt32(x.Owner.Db.Scalar("SELECT COUNT(*) FROM objects WHERE collection=$c", ("$c", x.Coll.Id))),
                events = Convert.ToInt32(x.Owner.Db.Scalar("SELECT COUNT(*) FROM objects WHERE collection=$c AND component='VEVENT'", ("$c", x.Coll.Id))),
                tasks = Convert.ToInt32(x.Owner.Db.Scalar("SELECT COUNT(*) FROM objects WHERE collection=$c AND component='VTODO'", ("$c", x.Coll.Id))),
                listType = x.Coll.Components.Contains("VTODO") ? (string?)Lists.TypeOf(x.Owner.Db, x.Coll.Id) : null,
                // Die Art gehoert dem Besitzer
                calendarKind = x.Coll.Components.Contains("VEVENT") ? (string?)CalendarKinds.Of(x.Owner.Db, x.Coll.Id) : null,
                // Standard-Erinnerung des Besitzers gilt auch hier (ändern kann nur er)
                remind = x.Coll.Components.Contains("VEVENT") && Features.On(Features.CalRemDefault) ? CalendarReminders.DefaultOf(x.Owner.Db, x.Coll.Id) : null,
                sharedWith = Members(x.Share, space.User.Id),
                owner = (string?)x.Owner.User.DisplayName,
                canWrite = !x.Share.ReadOnly.Contains(space.User.Id) && !Subscriptions.IsSubscribed(x.Owner.Db, x.Coll.Id),
                // Nur gezeigt (Version 2): ansehen in dmOffice, kein Export, nicht auf Geraete
                showOnly = x.Share.IsShowOnly(space.User.Id),
            });
            return Results.Ok(own.Select(o => (object)o).Concat(withMe));
        });

        // Wer auf diesem Server ist - zum Teilen (nur Name, kein Admin-Wissen)
        api.MapGet("/people", (HttpContext http, UserStore users) =>
        {
            var me = http.Space().User.Id;
            return users.All().Where(u => u.Id != me)
                .Select(u => new { u.Id, u.Login, u.DisplayName }).OrderBy(u => u.DisplayName);
        });

        // Einen eigenen Kalender oder eine Liste teilen (ReadOnly: wer davon nur lesen darf); leere Mitglieder: Freigabe aufheben
        api.MapPut("/shares/{id:long}", (long id, ShareBody body, HttpContext http, ShareStore shares, UserStore users) =>
        {
            var space = http.Space();
            // [feature:journal]
            if (DmOffice.Journal.TresorJournals.Is(id)) throw new ArgumentException("Tagebücher im Tresor können nicht geteilt werden.");
            // [/feature:journal]
            var c = PimStore.Find(space.Db, id) ?? throw new KeyNotFoundException();
            var members = body.Members.Where(m => m != space.User.Id && users.ById(m) != null).ToList();
            shares.Set(space.User.Id, id, members, body.ReadOnly, body.ShowOnly);
            return Results.Ok();
        });

        // Mit mir geteilte Liste nicht mehr sehen
        api.MapPost("/shares/{id:long}/leave", (long id, HttpContext http, ShareStore shares) =>
        {
            if (id >= 0) return Results.BadRequest(new { error = "Das ist ein eigener Kalender bzw. eine eigene Liste." });
            shares.Leave((int)-id, http.Space().User.Id);
            return Results.Ok();
        });

        api.MapPost("/collections/{kind}", (string kind, CollectionBody body, HttpContext http) =>
        {
            var k = Kind(kind);
            var db = http.Space().Db;
            var c = PimStore.CreateCollection(db, k, Guid.NewGuid().ToString("N")[..12], body.Name.Trim().Length > 0 ? body.Name.Trim() : "Neu",
                body.Color, k == PimStore.Calendar ? (body.Components ?? "VEVENT,VTODO") : "");
            if (k == PimStore.Calendar && body.CalendarKind != null) CalendarKinds.Set(db, c.Id, body.CalendarKind);
            return Results.Ok(new { c.Id });
        });

        api.MapPut("/collections/{id:long}", (long id, CollectionBody body, HttpContext http) =>
        {
            if (id < 0) return Results.StatusCode(403);   // Name und Farbe bestimmt der Besitzer
            var db = http.Space().Db;
            PimStore.UpdateCollection(db, id, body.Name, body.Color, body.Description, body.Components);
            if (body.CalendarKind != null && PimStore.Find(db, id)?.Kind == PimStore.Calendar) CalendarKinds.Set(db, id, body.CalendarKind);
            return Results.Ok();
        });

        api.MapDelete("/collections/{id:long}", (long id, HttpContext http) =>
        {
            if (id < 0) return Results.StatusCode(403);   // geteilte nur verlassen (shares/{id}/leave)
            var db = http.Space().Db;
            var c = PimStore.Find(db, id) ?? throw new KeyNotFoundException();
            if (PimStore.Collections(db, c.Kind).Count <= 1) return Results.BadRequest(new { error = "Die letzte Sammlung bleibt." });
            if (Graph.IsLinked(db, id)) return Results.BadRequest(new { error = "Mit Microsoft verbunden – in den Einstellungen des E-Mail-Kontos abschalten." });
            PimStore.DeleteCollection(db, id);
            Subscriptions.Remove(db, id);
            Lists.Remove(db, id);
            CalendarKinds.Remove(db, id);
            CalendarReminders.Remove(db, id);
            http.RequestServices.GetRequiredService<ShareStore>().Forget(http.Space().User.Id, id);
            return Results.Ok();
        });

        // ---------------- Termine ----------------

        api.MapGet("/events", (string from, string to, string? calendars, HttpContext http) =>
        {
            var space = http.Space();
            PimStore.EnsureDefaults(space.Db);
            var ids = Ids(calendars) ?? PimStore.Collections(space.Db, PimStore.Calendar).Select(c => c.Id)
                .Concat(SharedLists.WithMe(http).Where(x => x.Coll.Components.Contains("VEVENT")).Select(x => -(long)x.Share.Id)).ToList();
            var start = CalendarModel.ParseIso(from, out _);
            var end = CalendarModel.ParseIso(to, out _);
            var result = new List<EventDto>();
            foreach (var id in ids)
            {
                ListRef r;
                try { r = SharedLists.Resolve(http, id); } catch (KeyNotFoundException) { continue; }
                result.AddRange(CalendarReminders.Decorate(r.Space.Db, CalendarModel.Events(r.Space, start, end, new[] { r.Collection }), r.Collection, Mine(space, r)).Select(e => Public(e, r)));
            }
            return result.OrderBy(e => e.Start).ToList();
        });

        // Terminsuche in allen sichtbaren Kalendern (eigene und geteilte), ohne Zeitgrenze
        api.MapGet("/events/search", (string q, string? calendars, HttpContext http) =>
        {
            if (!Features.On(Features.CalendarSearch)) return Results.NotFound();
            var space = http.Space();
            PimStore.EnsureDefaults(space.Db);
            var ids = Ids(calendars) ?? PimStore.Collections(space.Db, PimStore.Calendar).Select(c => c.Id)
                .Concat(SharedLists.WithMe(http).Where(x => x.Coll.Components.Contains("VEVENT")).Select(x => -(long)x.Share.Id)).ToList();
            var now = DateTime.UtcNow;
            var result = new List<EventDto>();
            foreach (var id in ids)
            {
                ListRef r;
                try { r = SharedLists.Resolve(http, id); } catch (KeyNotFoundException) { continue; }
                result.AddRange(CalendarReminders.Decorate(r.Space.Db, CalendarModel.Search(r.Space, new[] { r.Collection }, q ?? "", now), r.Collection, Mine(space, r)).Select(e => Public(e, r)));
            }
            var upcoming = result.Where(e => CalendarModel.ParseIso(e.End, out _) > now).OrderBy(e => e.Start, StringComparer.Ordinal);
            var earlier = result.Where(e => CalendarModel.ParseIso(e.End, out _) <= now).OrderByDescending(e => e.Start, StringComparer.Ordinal);
            return Results.Ok(upcoming.Concat(earlier).Take(200).ToList());
        });

        api.MapPost("/events", (EventInput body, HttpContext http) =>
        {
            // Einladungen nur fuer eigene Kalender (Organisator = eigenes Mailkonto)
            var space = http.Space();
            var own = SharedLists.Resolve(http, body.Calendar).Space == space;
            var old = own && !string.IsNullOrEmpty(body.Uri) ? PimStore.Object(space.Db, body.Calendar, body.Uri)?.Data : null;
            var before = Mail.Invites.Attendees(space, old);
            // Ziehen im Kalender schickt keine Teilnehmer mit - die bleiben dann, und eine neue Zeit geht trotzdem an sie
            var notify = body.Notify && own && (body.Attendees != null || before.Count > 0);
            var result = Saved(() => SaveEventFor(body, http));
            if (!notify || result is not Microsoft.AspNetCore.Http.HttpResults.Ok<SavedId> ok) return result;
            var id = ok.Value!.Id;
            var parts = id.Split('/', 2);
            var obj = parts.Length == 2 && long.TryParse(parts[0], out var cal) ? PimStore.Object(space.Db, cal, parts[1]) : null;
            var now = Mail.Invites.Attendees(space, obj?.Data);
            int invited = 0, cancelled = 0;
            try
            {
                // Benutzer dieses dmOffice bekommen die Einladung von Pukki (Version 2), alle anderen per Mail
                var services = http.RequestServices;
                var outBefore = InternalInvites.Split(services, space.User.Id, before).Outside;
                var outNow = InternalInvites.Split(services, space.User.Id, now).Outside;
                // Neue Teilnehmer oder etwas, das sie angeht (Zeit, Ort, Titel ...) - nicht bei eigener Erinnerung oder frei/beschaeftigt
                if (obj != null && outNow.Count > 0 && (outNow.Except(outBefore).Any() || Mail.Invites.Changed(old, obj.Data)))
                    invited = Mail.Invites.Send(space, obj.Data, "REQUEST", outNow);
                var removed = outBefore.Except(outNow).ToList();
                if (obj != null && removed.Count > 0) cancelled = Mail.Invites.Send(space, obj.Data, "CANCEL", removed);
                if (obj != null) invited += InternalInvites.Saved(services, space, old, obj.Data, obj.Collection, obj.Uri);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Ok(new { id, invited, cancelled, error = "Termin gespeichert, Einladung nicht verschickt: " + ex.Message });
            }
            return Results.Ok(new { id, invited, cancelled });
        });

        string SaveEventFor(EventInput body, HttpContext http)
        {
            var from = SharedLists.Resolve(http, body.Calendar).Writable();
            var to = body.MoveTo is long m && m != body.Calendar ? SharedLists.Resolve(http, m).Writable() : null;
            body.Calendar = from.Collection;
            body.MoveTo = null;
            if (to != null && to.Space == from.Space) body.MoveTo = to.Collection;
            var id = CalendarModel.SaveEvent(from.Space, body);
            var uri = id[(id.IndexOf('/') + 1)..];
            // calremind: Aufgaben-Regeln stehen nicht im Termin, sondern in der Liste des Besitzers („ausdrücklich keine“: X-DM-NOREMIND)
            if (Features.On(Features.CalRemind) && from.Share == null && (to == null || to.Space == from.Space) && (body.Reminders != null || body.RemindDefault))
                RememberOwn(from.Space.Db, body.MoveTo ?? from.Collection, uri, body.RemindDefault ? null : body.Reminders);
            // In einen Kalender einer anderen Datenbank (eigen <-> geteilt): kopieren und hier loeschen
            if (to != null && to.Space != from.Space)
            {
                var src = PimStore.Find(from.Space.Db, from.Collection)!;
                var obj = PimStore.Object(from.Space.Db, src.Id, uri) ?? throw new KeyNotFoundException();
                PimStore.Put(to.Space.Db, PimStore.Find(to.Space.Db, to.Collection) ?? throw new KeyNotFoundException(), uri, obj.Data, zone: to.Space.Zone);
                PimStore.Delete(from.Space.Db, src, uri);
                return to.PublicId + "/" + uri;
            }
            return (to ?? from).PublicId + "/" + uri;
        }

        // ---------------- Erinnerungen (2.2) ----------------

        // Standard-Erinnerung eines eigenen Kalenders, auch eines abonnierten; null bzw. leer entfernt sie
        api.MapPut("/collections/{id:long}/remind", (long id, CalendarRemindDefault? body, HttpContext http) =>
        {
            if (!Features.On(Features.CalRemDefault)) return Results.NotFound();
            if (id < 0) return Results.StatusCode(403);
            var db = http.Space().Db;
            if (PimStore.Find(db, id) is not { } c || c.Kind != PimStore.Calendar) return Results.NotFound();
            CalendarReminders.SetDefault(db, id, body);
            return Results.Ok(CalendarReminders.DefaultOf(db, id));
        });

        // Fällige Erinnerungen (Benachrichtigung) im Zeitraum - Web, App, Push und Pukki rechnen gleich. Höchstens 15 Tage
        // (die App plant zwei Wochen voraus); calendar ist die Id wie in /collections (geteilt negativ), collection deren Uri.
        api.MapGet("/reminders", (string from, string to, HttpContext http) =>
        {
            if (!CalendarReminders.Active) return Results.NotFound();
            var space = http.Space();
            var start = CalendarModel.ParseIso(from, out _).ToUniversalTime();
            var end = CalendarModel.ParseIso(to, out _).ToUniversalTime();
            if (end - start > TimeSpan.FromDays(15)) end = start.AddDays(15);
            var uris = PimStore.Collections(space.Db, PimStore.Calendar).ToDictionary(c => c.Id, c => c.Uri);
            return Results.Ok(CalendarReminders.Due(http.RequestServices, space, start, end).Select(d => new
            {
                d.Title, start = d.Start, d.AllDay, fire = d.FireUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                d.Minutes, d.At, d.Calendar, collection = uris.GetValueOrDefault(d.Calendar, "shared-" + -d.Calendar), d.Uri,
            }));
        });

        // Erinnerungen für mehrere Termine setzen; Abo-Termine bekommen eine eigene Liste, die den Anbieter ersetzt
        api.MapPost("/events/remind", (RemindBody body, HttpContext http) =>
        {
            if (!Features.On(Features.CalRemind) && !Features.On(Features.CalBulkRemind)) return Results.NotFound();
            if (body.Items.Count > 1 && !Features.On(Features.CalBulkRemind)) return Results.NotFound();
            if (body.Add && (body.Reminders == null || body.Reminders.Count == 0)) return Results.BadRequest(new { error = "Keine Erinnerung angegeben." });
            var me = http.Space();
            int changed = 0, skipped = 0;
            foreach (var group in body.Items.GroupBy(i => i.Calendar))
            {
                ListRef r;
                try { r = SharedLists.Resolve(http, group.Key); } catch (KeyNotFoundException) { skipped += group.Count(); continue; }
                var db = r.Space.Db;
                var coll = PimStore.Find(db, r.Collection);
                if (coll == null) { skipped += group.Count(); continue; }
                var subscribed = Subscriptions.IsSubscribed(db, r.Collection);
                // Geteilt und für mich nur lesbar: eigene Erinnerungen im persönlichen Speicher (calremshared)
                var mine = r.Share != null && CalendarReminders.Personal(r.Share, me.User.Id, db, r.Collection) ? CalendarReminders.MineFor(me, r.Share, r.Space, r.Collection) : null;
                foreach (var uri in group.Select(i => i.Uri).Distinct())
                {
                    var obj = PimStore.Object(db, r.Collection, uri);
                    if (obj == null) { skipped++; continue; }
                    // Hinzufügen (Übernahme aus dem Android-Kalender): zu dem, was jetzt gilt - nichts davon fällt weg
                    List<RemindRule> Added(List<RemindRule> now) => CalendarReminders.Clean(now.Concat(body.Reminders ?? []));
                    List<RemindRule> Now() => CalendarModel.MasterOf(obj, r.Space.Zone) is { } ev ? CalendarReminders.EffectiveOf(db, r.Collection, ev) : [];
                    if (r.Share != null && CalendarReminders.Personal(r.Share, me.User.Id, db, r.Collection))
                    {
                        if (mine == null) { skipped++; continue; }
                        var list = body.Add ? Added(mine.TryGetValue(uri, out var had) ? had : Now().Where(x => x.List == null).ToList())
                            : body.Default ? null : CalendarReminders.Clean(body.Reminders);
                        CalendarReminders.SetMine(me.Db, r.Space.User.Id, r.Collection, uri, list);
                        if (list != null) mine[uri] = list; else mine.Remove(uri);
                        changed++;
                        continue;
                    }
                    if (subscribed)
                    {
                        if (r.Share != null) { skipped++; continue; }
                        CalendarReminders.SetOverlay(db, r.Collection, uri, body.Add ? Added(Now()) : body.Default ? null : CalendarReminders.Clean(body.Reminders));
                        changed++;
                        continue;
                    }
                    if (!r.CanWrite) { skipped++; continue; }
                    var vcal = Ics.Parse(obj.Data).First(c => c.Name == "VCALENDAR");
                    var events = vcal.Of("VEVENT").ToList();
                    if (events.Count == 0) { skipped++; continue; }
                    var wanted = body.Add ? Added(Now()) : body.Reminders;
                    var useDefault = body.Default && !body.Add;
                    var notify = useDefault ? [] : CalendarReminders.Clean(wanted).Where(x => x.List == null).ToList();
                    var hasDefault = Features.On(Features.CalRemDefault) && CalendarReminders.DefaultOf(db, r.Collection) != null;
                    foreach (var e in events)
                    {
                        var allDay = e.Get("DTSTART")?.Param("VALUE") is { } v && v.Equals("DATE", StringComparison.OrdinalIgnoreCase);
                        var rules = notify.Where(x => CalendarReminders.Writable(x, allDay)).ToList();
                        CalendarReminders.WriteAlarms(e, rules, allDay, e.Text("SUMMARY") ?? "");
                        CalendarReminders.SetNoRemind(e, !useDefault && rules.Count == 0 && hasDefault);
                    }
                    var data = vcal.ToString();
                    if (data != obj.Data) PimStore.Put(db, coll, uri, data, zone: r.Space.Zone);
                    if (r.Share == null) RememberOwn(db, r.Collection, uri, useDefault ? null : wanted);
                    changed++;
                }
            }
            return Results.Ok(new { changed, skipped });
        });

        // ---------------- Kalender abonnieren ----------------
        api.MapPost("/subscriptions", async (SubscribeBody body, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var url = Subscriptions.Normalize(body.Url).ToString();
            var name = string.IsNullOrWhiteSpace(body.Name) ? new Uri(url).Host : body.Name.Trim();
            var coll = PimStore.CreateCollection(space.Db, PimStore.Calendar, Guid.NewGuid().ToString("N")[..12], name, body.Color, "VEVENT");
            var sub = new Subscription { Collection = coll.Id, Url = body.Url.Trim(), Interval = Subscriptions.IntervalOf(body.Interval) };
            Subscriptions.Add(space.Db, sub);
            try
            {
                var count = await Subscriptions.FetchAsync(space, sub, ct);
                return Results.Ok(new { id = coll.Id, count });
            }
            catch (InvalidOperationException ex)
            {
                // Nichts Brauchbares unter der Adresse: Abo gleich wieder weg
                PimStore.DeleteCollection(space.Db, coll.Id);
                Subscriptions.Remove(space.Db, coll.Id);
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapPut("/subscriptions/{id:long}", (long id, SubscribeBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            if (!Subscriptions.IsSubscribed(db, id)) throw new KeyNotFoundException();
            if (!string.IsNullOrWhiteSpace(body.Url)) Subscriptions.Normalize(body.Url);
            Subscriptions.Update(db, id, s =>
            {
                if (!string.IsNullOrWhiteSpace(body.Url) && body.Url.Trim() != s.Url) { s.Url = body.Url.Trim(); s.ETag = null; s.LastFetch = null; }
                if (body.Interval is int i) s.Interval = Subscriptions.IntervalOf(i);
            });
            return Results.Ok();
        });

        api.MapPost("/subscriptions/{id:long}/refresh", async (long id, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var sub = Subscriptions.Of(space.Db, id) ?? throw new KeyNotFoundException();
            sub.ETag = null; // ausdruecklich neu holen
            return Results.Ok(new { count = await Subscriptions.FetchAsync(space, sub, ct) });
        });

        api.MapDelete("/events/{calendar:long}/{uri}", (long calendar, string uri, string? instance, bool? notify, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, calendar).Writable();
            // Eigenen Termin mit Teilnehmern loeschen: Absage an alle (SEQUENCE hoeher als die letzte Einladung) - bei einem
            // Termin der Serie nur fuer diesen (RECURRENCE-ID), die Serie bleibt bei ihnen stehen
            int cancelled = 0;
            if (notify != false && r.Space == http.Space() && PimStore.Object(r.Space.Db, r.Collection, uri) is { } obj
                && Mail.Invites.Attendees(r.Space, obj.Data).Count > 0)
            {
                try
                {
                    var vcal = Ics.Parse(obj.Data).First(c => c.Name == "VCALENDAR");
                    foreach (var e in vcal.Of("VEVENT"))
                        e.Set("SEQUENCE", ((int.TryParse(e.Text("SEQUENCE"), out var s) ? s : 0) + 1).ToString());
                    if (instance != null)
                    {
                        var one = CalendarModel.Occurrence(vcal, CalendarModel.ParseIso(instance, out _), r.Space.Zone);
                        one.Set("SEQUENCE", vcal.Of("VEVENT").First(e => e.Get("RECURRENCE-ID") == null).Text("SEQUENCE") ?? "1");
                        vcal.Children.RemoveAll(c => c.Name == "VEVENT" && c != one);
                    }
                    // Benutzer dieses dmOffice: Pukki sagt es ihnen und nimmt den Termin heraus (Version 2); alle anderen per Mail
                    var outside = InternalInvites.Split(http.RequestServices, r.Space.User.Id, Mail.Invites.Attendees(r.Space, obj.Data)).Outside;
                    if (outside.Count > 0) cancelled = Mail.Invites.Send(r.Space, vcal.ToString(), "CANCEL", outside);
                    cancelled += InternalInvites.Deleted(http.RequestServices, r.Space, obj.Data, instance);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* Loeschen geht trotzdem */ }
            }
            // Für „Rückgängig“ direkt nach dem Löschen: der Stand von vorher, nicht wenn schon Absagen unterwegs sind
            var before = cancelled == 0 ? PimStore.Object(r.Space.Db, r.Collection, uri)?.Data : null;
            CalendarModel.DeleteEvent(r.Space, r.Collection, uri, instance);
            return Results.Ok(new { cancelled, undo = before });
        });

        api.MapPost("/events/{calendar:long}/{uri}/restore", (long calendar, string uri, RestoreBody body, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, calendar).Writable();
            PimStore.Put(r.Space.Db, PimStore.Find(r.Space.Db, r.Collection) ?? throw new KeyNotFoundException(), uri, body.Data, zone: r.Space.Zone);
            return Results.Ok();
        });

        api.MapGet("/events/{calendar:long}/{uri}/ics", (long calendar, string uri, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, calendar).NotShowOnly();
            var obj = PimStore.Object(r.Space.Db, r.Collection, uri) ?? throw new KeyNotFoundException();
            return Results.File(Encoding.UTF8.GetBytes(obj.Data), "text/calendar", uri);
        });

        // ---------------- Aufgaben ----------------

        api.MapGet("/tasks", (string? lists, HttpContext http) =>
        {
            var space = http.Space();
            PimStore.EnsureDefaults(space.Db);
            var ids = Ids(lists) ?? PimStore.Collections(space.Db, PimStore.Calendar)
                .Where(c => c.Components.Contains("VTODO")).Select(c => c.Id)
                .Concat(SharedLists.WithMe(http).Where(x => x.Coll.Components.Contains("VTODO")).Select(x => -(long)x.Share.Id)).ToList();
            var result = new List<TaskDto>();
            foreach (var id in ids)
            {
                ListRef r;
                try { r = SharedLists.Resolve(http, id); } catch (KeyNotFoundException) { continue; }
                // Geteilte Liste: Ids so, wie dieser Benutzer die Liste kennt
                result.AddRange(CalendarModel.Tasks(r.Space, new[] { r.Collection })
                    .Select(t => r.Share == null ? t : t with { List = r.PublicId, Id = r.PublicId + "/" + t.Uri }));
            }
            return result;
        });

        // Zuweisen (Version 2): an wen man eine Aufgabe dieser Liste geben kann (Besitzer und Mitglieder)
        api.MapGet("/lists/{id:long}/people", (long id, HttpContext http) =>
            SharedLists.People(http.RequestServices, SharedLists.Resolve(http, id)).Select(u => new { u.Id, u.Login, u.DisplayName }));
        // Dasselbe fuer die App, die Listen nur ueber CalDAV kennt
        api.MapGet("/list-people", (string uri, HttpContext http) =>
            SharedLists.ByUri(http, uri) is { } r ? Results.Ok(SharedLists.People(http.RequestServices, r).Select(u => new { u.Id, u.Login, u.DisplayName })) : Results.NotFound());

        api.MapPost("/tasks", (TaskInput body, HttpContext http) => Saved(() =>
        {
            var from = SharedLists.Resolve(http, body.List).Writable();
            var to = body.MoveTo is long m && m != body.List ? SharedLists.Resolve(http, m).Writable() : null;
            body.List = from.Collection;
            body.MoveTo = null;
            if (to != null && to.Space == from.Space) body.MoveTo = to.Collection;
            // Zuweisen (Version 2): nur an jemanden, der die Liste sieht; wer neu zugewiesen wird, erfaehrt es von Pukki
            string? before = null;
            if (body.Assignee is { Length: > 0 } who)
            {
                if (SharedLists.People(http.RequestServices, to ?? from).All(u => u.Id != who)) throw new ArgumentException("Dieser Benutzer hat keinen Zugriff auf die Liste.");
                if (body.Uri != null) before = CalendarModel.Tasks(from.Space, [from.Collection]).FirstOrDefault(t => t.Uri == body.Uri)?.Assignee;
            }
            var id = CalendarModel.SaveTask(from.Space, body);
            var uri = id[(id.IndexOf('/') + 1)..];
            if (body.Assignee is { Length: > 0 } assignee && assignee != before && assignee != http.Space().User.Id)
            {
                var listOwner = to ?? from;
                DmOffice.Chat.Pukki.TaskAssigned(http.RequestServices, http.Space().User.Id, assignee, listOwner.Space.User.Id, listOwner.Collection, uri, body.Title,
                    PimStore.Find(listOwner.Space.Db, listOwner.Collection)?.Name ?? "");
            }
            // In eine Liste einer anderen Datenbank (eigene <-> geteilte): kopieren und hier loeschen
            if (to != null && to.Space != from.Space)
            {
                var src = PimStore.Find(from.Space.Db, from.Collection)!;
                var obj = PimStore.Object(from.Space.Db, src.Id, uri) ?? throw new KeyNotFoundException();
                PimStore.Put(to.Space.Db, PimStore.Find(to.Space.Db, to.Collection) ?? throw new KeyNotFoundException(), uri, obj.Data, zone: to.Space.Zone);
                PimStore.Delete(from.Space.Db, src, uri);
                return to.PublicId + "/" + uri;
            }
            var target = to ?? from;
            return target.PublicId + "/" + uri;
        }));

        api.MapDelete("/tasks/{list:long}/{uri}", (long list, string uri, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, list).Writable();
            // Für „Rückgängig“ direkt nach dem Löschen (Kennung taskclip): der Stand von vorher
            var before = Features.On(Features.TaskClip) ? PimStore.Object(r.Space.Db, r.Collection, uri)?.Data : null;
            PimStore.Delete(r.Space.Db, PimStore.Find(r.Space.Db, r.Collection) ?? throw new KeyNotFoundException(), uri);
            return Features.On(Features.TaskClip) ? Results.Ok(new { undo = before }) : Results.Ok();
        });

        api.MapPost("/tasks/{list:long}/{uri}/restore", (long list, string uri, RestoreBody body, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, list).Writable();
            PimStore.Put(r.Space.Db, PimStore.Find(r.Space.Db, r.Collection) ?? throw new KeyNotFoundException(), uri, body.Data, zone: r.Space.Zone);
            return Results.Ok();
        });

        // ---------------- Einkaufs- und Checklisten ----------------

        api.MapGet("/lists/{id:long}", (long id, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id);
            if (PimStore.Find(r.Space.Db, r.Collection) == null) throw new KeyNotFoundException();
            var m = Lists.Get(r.Space.Db, r.Collection);
            return new { m.Type, m.Sections, m.Done, history = m.History.OrderByDescending(h => h.Count).ThenByDescending(h => h.Last) };
        });

        api.MapPut("/lists/{id:long}", (long id, ListBody body, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id).Writable();
            if (PimStore.Find(r.Space.Db, r.Collection) == null) throw new KeyNotFoundException();
            var m = Lists.Update(r.Space.Db, r.Collection, body.Type, body.Sections, body.Done);
            return Results.Ok(new { m.Type, m.Sections, m.Done });
        });

        api.MapPost("/lists/{id:long}/forget", (long id, ForgetBody body, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id).Writable();
            Lists.Forget(r.Space.Db, r.Collection, body.Item);
            return Results.Ok();
        });

        // Foto zu einem Artikel: welche Marke, welche Packung - erscheint neben dem Eintrag und im Vorschlag
        api.MapPost("/lists/{id:long}/photo", async (long id, string item, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            var r = SharedLists.Resolve(http, id).Writable();
            if (PimStore.Find(r.Space.Db, r.Collection) == null) throw new KeyNotFoundException();
            await foreach (var file in MultipartUpload.Files(request, ct))
            {
                using var buffer = new MemoryStream();
                await file.Body.CopyToAsync(buffer, ct);
                if (buffer.Length > 30_000_000) return Results.BadRequest(new { error = "Das Bild ist zu groß." });
                var photo = Lists.SetPhoto(r.Space.Db, r.Collection, item, buffer.ToArray());
                return photo == null ? Results.BadRequest(new { error = "Das ist kein Bild." }) : Results.Ok(new { photo });
            }
            return Results.BadRequest(new { error = "Kein Bild mitgeschickt." });
        }).DisableAntiforgery();

        api.MapDelete("/lists/{id:long}/photo", (long id, string item, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id).Writable();
            Lists.RemovePhoto(r.Space.Db, r.Collection, item);
            return Results.Ok();
        });

        api.MapGet("/lists/{id:long}/photos/{photo}", (long id, string photo, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id);
            if (Lists.Photo(r.Space.Db, r.Collection, photo) is not { } data) return Results.NotFound();
            // Neues Foto = neue Kennung: darf lange im Browser bleiben
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(data, "image/webp");
        });

        // Vorschlaege uebernehmen (AnyList-HTML oder ein Eintrag je Zeile); ohne Apply nur zaehlen
        api.MapPost("/lists/{id:long}/import", (long id, ImportBody body, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id);
            if (body.Apply) r.Writable();
            if (PimStore.Find(r.Space.Db, r.Collection) == null) throw new KeyNotFoundException();
            if (body.Text.Length > 5_000_000) return Results.BadRequest();
            var items = ListImport.Parse(body.Text);
            var fresh = body.Apply ? ListImport.Apply(r.Space.Db, r.Collection, items) : ListImport.CountNew(r.Space.Db, r.Collection, items);
            return Results.Ok(new { found = items.Count, added = fresh, sample = items.Take(8).Select(x => x.Name) });
        });

        // Checkliste: alle Haken weg, fuer das naechste Mal
        api.MapPost("/lists/{id:long}/reset", (long id, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id).Writable();
            var space = r.Space;
            int n = 0;
            foreach (var task in CalendarModel.Tasks(space, new[] { r.Collection }).Where(x => x.Completed))
            {
                CalendarModel.SaveTask(space, Input(task, completed: false));
                n++;
            }
            return Results.Ok(new { count = n });
        });

        // Einkaufsliste: Erledigtes weg
        api.MapPost("/lists/{id:long}/clear", (long id, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id).Writable();
            var space = r.Space;
            var coll = PimStore.Find(space.Db, r.Collection) ?? throw new KeyNotFoundException();
            int n = 0;
            foreach (var task in CalendarModel.Tasks(space, new[] { r.Collection }).Where(x => x.Completed))
            {
                PimStore.Delete(space.Db, coll, task.Uri);
                n++;
            }
            return Results.Ok(new { count = n });
        });

        // ---------------- Kontakte ----------------

        api.MapGet("/contacts", (string? books, HttpContext http) =>
        {
            var space = http.Space();
            PimStore.EnsureDefaults(space.Db);
            var ids = Ids(books) ?? PimStore.Collections(space.Db, PimStore.AddressBook).Select(c => c.Id).ToList();
            return ContactModel.All(space, ids);
        });

        api.MapGet("/contacts/{book:long}/{uri}", (long book, string uri, HttpContext http) =>
        {
            var obj = PimStore.Object(http.Space().Db, book, uri) ?? throw new KeyNotFoundException();
            return ContactModel.Read(obj, withPhoto: true);
        });

        api.MapGet("/contacts/{book:long}/{uri}/vcf", (long book, string uri, HttpContext http) =>
        {
            var (data, name) = ContactModel.Export(http.Space().Db, book, uri);
            return Results.File(data, "text/vcard", name);
        });

        // Adressen der VIP-Kontakte (App: Benachrichtigungen "nur VIPs")
        api.MapGet("/vips", (HttpContext http) => ContactModel.VipAddresses(http.Space()));

        api.MapPost("/contacts", (ContactDto body, HttpContext http) =>
            Saved(() => ContactModel.Save(http.Space(), body)));

        api.MapDelete("/contacts/{book:long}/{uri}", (long book, string uri, HttpContext http) =>
        {
            var db = http.Space().Db;
            var before = PimStore.Object(db, book, uri)?.Data;
            PimStore.Delete(db, PimStore.Find(db, book) ?? throw new KeyNotFoundException(), uri);
            return Results.Ok(new { undo = before });
        });

        api.MapPost("/contacts/{book:long}/{uri}/restore", (long book, string uri, RestoreBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            PimStore.Put(db, PimStore.Find(db, book) ?? throw new KeyNotFoundException(), uri, body.Data, zone: http.Space().Zone);
            return Results.Ok();
        });

        api.MapGet("/suggest", (string q, string? people, HttpContext http) =>
        {
            var list = ContactStore.Suggest(http.Space(), q);
            // Termine (Version 2): Benutzer dieses dmOffice zuerst - sie lädt Pukki ohne Mail ein
            if (people == "1") list = [.. InternalInvites.Suggest(http.RequestServices, http.Space().User.Id, q), .. list];
            return list.DistinctBy(s => s.Address.ToLowerInvariant()).Take(12).ToList();
        });

        // Zuletzt benutzte Adressen (Adressvorschlaege) verwalten
        api.MapGet("/recent", (HttpContext http) =>
        {
            var space = http.Space();
            var known = ContactStore.KnownAddresses(space);
            return ContactStore.Recents(space).OrderByDescending(r => r.Last)
                .Select(r => new { r.Name, r.Address, r.Count, r.Last, inContacts = known.Contains(r.Address.ToLowerInvariant()) });
        });
        api.MapPost("/recent/delete", (AddressesBody body, HttpContext http) =>
            Results.Ok(new { removed = ContactStore.Forget(http.Space(), body.Addresses) }));
        api.MapPost("/recent/add", (AddressesBody body, HttpContext http) =>
        {
            var space = http.Space();
            PimStore.EnsureDefaults(space.Db);
            var book = body.Book ?? PimStore.Collections(space.Db, PimStore.AddressBook).First().Id;
            return Results.Ok(new { added = ContactStore.AddToBook(space, body.Addresses, book) });
        });

        // ---------------- Ein- und Ausfuhr ----------------

        api.MapPost("/import/{id:long}", async (long id, HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            var r = SharedLists.Resolve(http, id).Writable();
            var space = r.Space;
            var coll = PimStore.Find(space.Db, r.Collection) ?? throw new KeyNotFoundException();
            var form = await request.ReadFormAsync(ct);
            int added = 0, updated = 0, existing = 0, failed = 0, found = 0;
            // Kontakte ohne gleiche UID, aber mit gleichem Namen, gleichen E-Mail-Adressen und Telefonnummern: schon vorhanden
            var known = coll.Kind == PimStore.AddressBook
                ? PimStore.Objects(space.Db, coll.Id).Select(o => CardKey(o.Data)).Where(k => k != null).ToHashSet()
                : new HashSet<string?>();
            foreach (var file in form.Files)
            {
                using var reader = new StreamReader(file.OpenReadStream());
                var text = await reader.ReadToEndAsync(ct);
                foreach (var (uri, data) in Split(coll, text))
                {
                    found++;
                    // Unlesbare Zeiten: der Termin erschiene in keiner Ansicht - nicht uebernehmen, sondern mitzaehlen (ebenso ueberlange Titel)
                    if (coll.Kind == PimStore.Calendar && !Importable(data)) { failed++; continue; }
                    try
                    {
                        // Gleiche UID: derselbe Eintrag noch einmal - aktualisieren statt doppelt anlegen
                        var uid = Ics.Parse(data).SelectMany(c => c.Name == "VCALENDAR" ? c.Children : [c]).Select(c => c.Text("UID")).FirstOrDefault(u => u != null);
                        if (uid != null && PimStore.ByUid(space.Db, coll.Id, uid) is { } same)
                        {
                            PimStore.Put(space.Db, coll, same.Uri, data, zone: space.Zone); updated++;
                            continue;
                        }
                        if (coll.Kind == PimStore.AddressBook && CardKey(data) is { } key && !known.Add(key)) { existing++; continue; }
                        PimStore.Put(space.Db, coll, uri, data, zone: space.Zone); added++;
                    }
                    catch { failed++; }
                }
            }
            if (found == 0)
                return Results.BadRequest(new { error = coll.Kind == PimStore.AddressBook ? "Die Datei enthält keine Kontakte (vCard, .vcf)." : "Die Datei enthält keine Termine oder Aufgaben (iCalendar, .ics)." });
            return Results.Ok(new { added, updated, existing, failed });
        }).DisableAntiforgery();

        api.MapGet("/export/{id:long}", (long id, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id).NotShowOnly();
            var db = r.Space.Db;
            var coll = PimStore.Find(db, r.Collection) ?? throw new KeyNotFoundException();
            var objects = PimStore.Objects(db, r.Collection);
            if (coll.Kind == PimStore.AddressBook)
                return Results.File(Encoding.UTF8.GetBytes(string.Join("", objects.Select(o => o.Data))), "text/vcard", coll.Name + ".vcf");
            var all = CalendarModel.NewCalendar();
            var zones = new HashSet<string>();
            foreach (var o in objects)
                foreach (var c in Ics.Parse(o.Data).Where(r => r.Name == "VCALENDAR").SelectMany(r => r.Children))
                    if (c.Name != "VTIMEZONE" || zones.Add(c.Text("TZID") ?? "")) all.Children.Add(c);
            return Results.File(Encoding.UTF8.GetBytes(all.ToString()), "text/calendar", coll.Name + ".ics");
        });
    }

    /// <summary>
    /// Eigener beschreibbarer Kalender: Aufgaben-Regeln merken (nur der Besitzer legt Aufgaben an). „Keine Erinnerung“ steht
    /// als X-DM-NOREMIND im Termin, damit auch die App und geteilte Personen es sehen. Null: Eintrag weg.
    /// </summary>
    private static void RememberOwn(UserDb db, long coll, string uri, List<RemindRule>? rules)
    {
        var tasks = rules == null ? [] : CalendarReminders.Clean(rules).Where(r => r.List != null).ToList();
        CalendarReminders.SetOverlay(db, coll, uri, tasks.Count > 0 ? tasks : null);
    }

    /// <summary>Eigene Erinnerungen in einem geteilten, für mich nur lesbaren Kalender (calremshared); sonst null.</summary>
    private static Dictionary<string, List<RemindRule>>? Mine(UserSpace me, ListRef r) =>
        r.Share == null || !CalendarReminders.Active ? null : CalendarReminders.MineFor(me, r.Share, r.Space, r.Collection);

    /// <summary>Ein Termin mit den Ids, wie der angemeldete Benutzer den Kalender kennt (bei geteilten negativ).</summary>
    private static EventDto Public(EventDto e, ListRef r) =>
        r.Share == null ? e : e with { Calendar = r.PublicId, Id = r.PublicId + e.Id[e.Id.IndexOf('/')..], CanWrite = r.CanWrite };

    /// <summary>Speichern aus der Oberflaeche; passt der ETag des geladenen Stands nicht mehr: 409.</summary>
    public sealed record SavedId(string Id);

    private static IResult Saved(Func<string> save)
    {
        try { return TypedResults.Ok(new SavedId(save())); }
        catch (PreconditionFailedException)
        {
            return Results.Conflict(new { error = "Wurde inzwischen auf einem anderen Gerät geändert – bitte neu laden." });
        }
    }

    private static string Kind(string k) => k switch
    {
        "calendars" or "calendar" => PimStore.Calendar,
        "addressbooks" or "addressbook" => PimStore.AddressBook,
        _ => throw new ArgumentException("Unbekannte Art.")
    };

    /// <summary>Eine gelesene Aufgabe unveraendert zurueckschreiben - bis auf "erledigt".</summary>
    internal static TaskInput Input(TaskDto t, bool completed) => new()
    {
        List = t.List, Uri = t.Uri, Etag = t.Etag, Title = t.Title, Description = t.Description, Due = t.Due, DueAllDay = t.DueAllDay,
        Priority = t.Priority, Completed = completed, Percent = completed ? 100 : 0, Categories = t.Categories,
    };

    private static List<long>? Ids(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? null : csv.Split(',').Select(long.Parse).ToList();

    /// <summary>Lassen sich alle Zeiten der Termine und Aufgaben lesen (ein Termin braucht einen Beginn), und bleibt der
    /// Titel in der Grenze?</summary>
    public static bool Importable(string data)
    {
        foreach (var c in Ics.Parse(data).Where(r => r.Name == "VCALENDAR").SelectMany(r => r.Children))
        {
            if (c.Name is not ("VEVENT" or "VTODO")) continue;
            if (c.Name == "VEVENT" && c.Get("DTSTART") == null) return false;
            if (c.Text("SUMMARY")?.Length > CalendarModel.MaxTitle || c.Text("LOCATION")?.Length > CalendarModel.MaxTitle) return false;
            foreach (var name in new[] { "DTSTART", "DTEND", "DUE", "RECURRENCE-ID" })
                if (c.Get(name) is { } p && Ics.Time(p, TimeZoneInfo.Utc, out _) == null) return false;
        }
        return true;
    }

    /// <summary>Name, E-Mail-Adressen und Telefonnummern einer vCard als Vergleichsschlüssel; null bei einer Karte ohne all das.</summary>
    internal static string? CardKey(string data)
    {
        var card = Ics.Parse(data).FirstOrDefault(c => c.Name == "VCARD");
        if (card == null) return null;
        var name = (card.Text("FN") ?? "").Trim().ToLowerInvariant();
        var mails = card.All("EMAIL").Select(p => p.Text.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Order(StringComparer.Ordinal);
        var tels = card.All("TEL").Select(p => new string(p.Text.Where(char.IsDigit).ToArray())).Where(x => x.Length > 0).Order(StringComparer.Ordinal);
        var key = name + "|" + string.Join(",", mails) + "|" + string.Join(",", tels);
        return key == "||" ? null : key;
    }

    /// <summary>
    /// Eine Datei aus einer anderen Anwendung (Export aus Nextcloud, Outlook,
    /// Google) in Objekte teilen: je vCard eines, je UID eines - Serien mit
    /// ihren Ausnahmen bleiben zusammen, Zeitzonen kommen zu jedem mit.
    /// </summary>
    public static IEnumerable<(string Uri, string Data)> Split(Collection coll, string text)
    {
        var roots = Ics.Parse(text);
        if (coll.Kind == PimStore.AddressBook)
        {
            foreach (var card in roots.Where(r => r.Name == "VCARD"))
            {
                if (card.Get("UID") == null) card.Set("UID", Guid.NewGuid().ToString("D"));
                yield return (PimStore.NewUri(".vcf"), card.ToString());
            }
            yield break;
        }
        foreach (var cal in roots.Where(r => r.Name == "VCALENDAR"))
        {
            var zones = cal.Of("VTIMEZONE").ToList();
            var groups = cal.Children.Where(c => c.Name is "VEVENT" or "VTODO" or "VJOURNAL")
                .GroupBy(c => c.Text("UID") ?? Guid.NewGuid().ToString("D"));
            foreach (var g in groups)
            {
                var one = CalendarModel.NewCalendar();
                one.Children.AddRange(zones);
                foreach (var item in g)
                {
                    if (item.Get("UID") == null) item.Set("UID", g.Key);
                    one.Children.Add(item);
                }
                yield return (PimStore.NewUri(".ics"), one.ToString());
            }
        }
    }
}
