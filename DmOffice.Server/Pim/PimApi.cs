using System.Text;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Pim;

public static class PimApi
{
    public record SubscribeBody(string Url, string? Name, string? Color, int? Interval);
    public record CollectionBody(string Name, string? Color, string? Description, string? Components, string? CalendarKind = null);
    public record AddressesBody(List<string> Addresses, long? Book);
    public record ListBody(string? Type, List<string>? Sections, string? Done);
    public record ForgetBody(string Item);
    public record ImportBody(string Text, bool Apply);
    public record ShareBody(List<string> Members, List<string>? ReadOnly);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/pim").RequireAuthorization();

        api.MapGet("/collections/{kind}", (string kind, HttpContext http, ShareStore shares, UserStore users) =>
        {
            var space = http.Space();
            var db = space.Db;
            PimStore.EnsureDefaults(db);
            var subs = Subscriptions.All(db);
            var k = Kind(kind);
            string? NameOf(string id) => users.ById(id)?.DisplayName;
            // Mitglieder einer Freigabe mit ihrem Recht (ohne "except")
            object Members(Share sh, string? except) => sh.Members.Where(m => m != except)
                .Select(m => new { id = m, name = NameOf(m), readOnly = sh.ReadOnly.Contains(m) }).Where(m => m.name != null).ToList();
            var own = PimStore.Collections(db, k).Select(c => new
            {
                c.Id, c.Uri, c.Name, c.Color, c.Description, components = c.Components.Split(',', StringSplitOptions.RemoveEmptyEntries),
                // Abonniert: schreibgeschuetzt, wird regelmaessig neu geholt
                subscription = subs.FirstOrDefault(s => s.Collection == c.Id) is { } sub
                    ? new { sub.Url, sub.Interval, sub.LastFetch, sub.LastError } : null,
                count = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM objects WHERE collection=$c", ("$c", c.Id))),
                // Getrennt gezaehlt: ein Kalender kann Termine und Aufgaben enthalten
                events = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM objects WHERE collection=$c AND component='VEVENT'", ("$c", c.Id))),
                tasks = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM objects WHERE collection=$c AND component='VTODO'", ("$c", c.Id))),
                // Aufgabenliste, Einkaufsliste oder Checkliste
                listType = c.Components.Contains("VTODO") ? Lists.TypeOf(db, c.Id) : null,
                // Art des Kalenders (CalendarKinds): normal, Essensplan, ...
                calendarKind = k == PimStore.Calendar && c.Components.Contains("VEVENT") ? CalendarKinds.Of(db, c.Id) : null,
                // Ich teile sie mit ... / sie gehoert ...
                sharedWith = shares.Of(space.User.Id, c.Id) is { } sh ? Members(sh, null) : null,
                owner = (string?)null,
                canWrite = true,
            }).ToList();
            if (k != PimStore.Calendar) return Results.Ok(own);
            // Mit mir geteilte Kalender und Aufgabenlisten (Id negativ)
            var withMe = SharedLists.WithMe(http).Select(x => new
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
                sharedWith = Members(x.Share, space.User.Id),
                owner = (string?)x.Owner.User.DisplayName,
                canWrite = !x.Share.ReadOnly.Contains(space.User.Id) && !Subscriptions.IsSubscribed(x.Owner.Db, x.Coll.Id),
            });
            return Results.Ok(own.Select(o => (object)o).Concat(withMe));
        });

        // Wer auf diesem Server ist - zum Teilen (nur Name, kein Admin-Wissen)
        api.MapGet("/people", (HttpContext http, UserStore users) =>
        {
            var me = http.Space().User.Id;
            return users.All().Where(u => u.Id != me).Select(u => new { u.Id, u.Login, u.DisplayName }).OrderBy(u => u.DisplayName);
        });

        // Einen eigenen Kalender oder eine Liste teilen (ReadOnly: wer davon nur lesen darf); leere Mitglieder: Freigabe aufheben
        api.MapPut("/shares/{id:long}", (long id, ShareBody body, HttpContext http, ShareStore shares, UserStore users) =>
        {
            var space = http.Space();
            var c = PimStore.Find(space.Db, id) ?? throw new KeyNotFoundException();
            var members = body.Members.Where(m => m != space.User.Id && users.ById(m) != null).ToList();
            shares.Set(space.User.Id, id, members, body.ReadOnly);
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
            PimStore.DeleteCollection(db, id);
            Subscriptions.Remove(db, id);
            Lists.Remove(db, id);
            CalendarKinds.Remove(db, id);
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
                result.AddRange(CalendarModel.Events(r.Space, start, end, new[] { r.Collection }).Select(e => Public(e, r)));
            }
            return result.OrderBy(e => e.Start).ToList();
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
                // Neue Teilnehmer oder etwas, das sie angeht (Zeit, Ort, Titel ...) - nicht bei eigener Erinnerung oder frei/beschaeftigt
                if (obj != null && now.Count > 0 && (now.Except(before).Any() || Mail.Invites.Changed(old, obj.Data)))
                    invited = Mail.Invites.Send(space, obj.Data, "REQUEST");
                var removed = before.Except(now).ToList();
                if (obj != null && removed.Count > 0) cancelled = Mail.Invites.Send(space, obj.Data, "CANCEL", removed);
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

        // ---------------- Kalender abonnieren ----------------
        api.MapPost("/subscriptions", async (SubscribeBody body, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var url = Subscriptions.Normalize(body.Url).ToString();
            var name = string.IsNullOrWhiteSpace(body.Name) ? new Uri(url).Host : body.Name.Trim();
            var coll = PimStore.CreateCollection(space.Db, PimStore.Calendar, Guid.NewGuid().ToString("N")[..12], name, body.Color, "VEVENT");
            var sub = new Subscription { Collection = coll.Id, Url = body.Url.Trim(), Interval = Math.Clamp(body.Interval ?? 360, 15, 10080) };
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
                if (body.Interval is int i) s.Interval = Math.Clamp(i, 15, 10080);
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
                    cancelled = Mail.Invites.Send(r.Space, vcal.ToString(), "CANCEL");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* Loeschen geht trotzdem */ }
            }
            CalendarModel.DeleteEvent(r.Space, r.Collection, uri, instance);
            return Results.Ok(new { cancelled });
        });

        api.MapGet("/events/{calendar:long}/{uri}/ics", (long calendar, string uri, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, calendar);
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

        api.MapPost("/tasks", (TaskInput body, HttpContext http) => Saved(() =>
        {
            var from = SharedLists.Resolve(http, body.List).Writable();
            var to = body.MoveTo is long m && m != body.List ? SharedLists.Resolve(http, m).Writable() : null;
            body.List = from.Collection;
            body.MoveTo = null;
            if (to != null && to.Space == from.Space) body.MoveTo = to.Collection;
            var id = CalendarModel.SaveTask(from.Space, body);
            var uri = id[(id.IndexOf('/') + 1)..];
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
            PimStore.Delete(r.Space.Db, PimStore.Find(r.Space.Db, r.Collection) ?? throw new KeyNotFoundException(), uri);
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
            PimStore.Delete(db, PimStore.Find(db, book) ?? throw new KeyNotFoundException(), uri);
            return Results.Ok();
        });

        api.MapGet("/suggest", (string q, HttpContext http) => ContactStore.Suggest(http.Space(), q));

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
            int added = 0, failed = 0;
            foreach (var file in form.Files)
            {
                using var reader = new StreamReader(file.OpenReadStream());
                var text = await reader.ReadToEndAsync(ct);
                foreach (var (uri, data) in Split(coll, text))
                {
                    try { PimStore.Put(space.Db, coll, uri, data, zone: space.Zone); added++; }
                    catch { failed++; }
                }
            }
            return Results.Ok(new { added, failed });
        }).DisableAntiforgery();

        api.MapGet("/export/{id:long}", (long id, HttpContext http) =>
        {
            var r = SharedLists.Resolve(http, id);
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
    private static TaskInput Input(TaskDto t, bool completed) => new()
    {
        List = t.List, Uri = t.Uri, Etag = t.Etag, Title = t.Title, Description = t.Description, Due = t.Due, DueAllDay = t.DueAllDay,
        Priority = t.Priority, Completed = completed, Percent = completed ? 100 : 0, Categories = t.Categories,
    };

    private static List<long>? Ids(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? null : csv.Split(',').Select(long.Parse).ToList();

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
