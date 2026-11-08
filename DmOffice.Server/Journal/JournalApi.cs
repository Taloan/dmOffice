using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;

namespace DmOffice.Journal;

/// <summary>
/// Tagebuch (Version 2), /api/journal: eigene und mit mir geteilte Tagebücher (Freigaben wie bei Kalendern: zeigen, senden,
/// teilen; private Einträge sieht nur der Besitzer), Einträge (Zeitleiste, Kalender, Suche, Schlagwörter, Stimmung), „Heute vor …“,
/// Medien (Hochladen in Stücken, kleingerechnet in „sys-dmoffice-journal/Jahr/Monat“ beim Besitzer), Bilder → Einträge mit dem Datum der
/// Aufnahme, Wetter und Ortsname auf Wunsch, Termine des Tages, Vorschlag für den Anfang.
/// </summary>
public static class JournalApi
{
    /// <param name="Tresor">Neues Tagebuch im Tresor anlegen</param>
    public sealed record JournalBody(string Name, string? Color, bool? Tresor = null);
    /// <param name="To">„tresor“ oder „open“</param>
    /// <param name="Unshare">Geteiltes Tagebuch: Freigaben entfernen (nach dem Hinweis)</param>
    public sealed record MoveBody(string To, bool? Unshare = null);
    /// <param name="Ids">Einträge („Tagebuch/Name“ wie in <see cref="JournalEntry.Id"/>)</param>
    /// <param name="To">Ziel-Tagebuch (eigenes, auch im Tresor)</param>
    public sealed record MoveEntriesBody(List<string> Ids, long To);
    /// <param name="Ask">Uhrzeit, zu der Pukki abends nach dem Tag fragt (HH:mm); leer: aus</param>
    public sealed record SettingsBody(int? ImageSize, bool? Lookup, string? Ask = null);
    public sealed record FilesBody(List<string> Paths, long? Journal = null);
    public sealed record ExternalMediaBody(List<string> Ids, long? Journal = null);
    /// <summary>Ein Foto oder Video des Tages für den Vorschlag: aus der Ablage (Pfad) oder einem Galerie-Ordner („ext:…“).</summary>
    public sealed record DayPhoto(string Kind, string Path, string Name, string? Mime, string? Etag, long? Taken, double? Lat = null, double? Lon = null);
    public sealed record ImportBody(string Path, long? Journal);
    public sealed record FromMediaBody(long Journal, List<JournalMedia> Media, List<PlaceAt>? Places = null);
    /// <summary>Ort einer Aufnahme (aus dem Bild gelesen) - für neue Einträge ohne Ort.</summary>
    public sealed record PlaceAt(string Path, double Lat, double Lon);

    /// <summary>Eintrag in der Liste: das Wichtigste für Zeitleiste, Kalender und Medien.</summary>
    public sealed record EntryPreview(string Id, long Journal, string Uri, DateTime Date, string? Title, string Snippet, int? Mood,
        List<string> Tags, JournalPlace? Place, bool Private, List<JournalMedia> Media, int Refs, string? Weather, string? Owner, bool CanWrite);

    /// <summary>
    /// Ein Tagebuch, wie der Benutzer es sieht: eigenes, mit ihm geteiltes (PublicId negativ) oder eigenes im Tresor
    /// (PublicId ab <see cref="TresorJournals.Base"/>, <paramref name="InTresor"/> gesetzt).
    /// </summary>
    internal sealed record Scope(UserSpace Space, Collection Coll, long PublicId, bool Own, bool CanWrite, bool ShowOnly, TresorDb? InTresor = null)
    {
        /// <summary>Wo die Einträge liegen.</summary>
        public IJournalBox Box => InTresor is { } t ? new TresorJournals.Box(t, Coll.Id) : new PimJournal(Space.Db, Coll);
        /// <summary>Wohin Medien kommen und woher sie gelesen werden.</summary>
        public JournalFiles Files => new(Space, InTresor);

        public Scope Writable() => CanWrite ? this : throw new UnauthorizedAccessException("Nur lesbar – der Besitzer hat dir keine Bearbeitung erlaubt.");
        public string? Owner => Own ? null : Space.User.DisplayName;

        /// <summary>Einträge, die der Benutzer sieht: bei fremden Tagebüchern ohne die privaten - mit der Kennung aus seiner Sicht.</summary>
        public IEnumerable<JournalEntry> Entries() => JournalStore.Entries(Box).Where(e => Own || !e.Private).Select(Map);

        public JournalEntry Map(JournalEntry e) => e with { Journal = PublicId, Id = PublicId + "/" + e.Uri, Owner = Owner, CanWrite = CanWrite };

        /// <summary>Ein einzelner sichtbarer Eintrag.</summary>
        public JournalEntry Entry(string uri) =>
            Box.Object(uri) is { } o && JournalStore.Read(o) is { } e && (Own || !e.Private) ? Map(e) : throw new KeyNotFoundException("Diesen Eintrag gibt es nicht.");
    }

    internal static Scope Resolve(HttpContext http, long id)
    {
        // Im Tresor: nur, solange er auf diesem Gerät offen ist (sonst 423)
        if (TresorJournals.Is(id))
        {
            var tdb = Tresor.Db(http);
            // Kennung Base: „ein Tagebuch im Tresor“ ohne bestimmtes (neuer Eintrag bei geschlossenem Tresor) - das zuletzt beschriebene
            var tj = id == TresorJournals.Base ? TresorJournals.Default(tdb) : TresorJournals.Require(tdb, id - TresorJournals.Base);
            return new Scope(http.Space(), tj, TresorJournals.Base + tj.Id, true, true, false, tdb);
        }
        var r = SharedLists.Resolve(http, id, journals: true);
        var c = PimStore.Find(r.Space.Db, r.Collection);
        if (c == null || !JournalStore.IsJournal(c)) throw new KeyNotFoundException("Dieses Tagebuch gibt es nicht.");
        return new Scope(r.Space, c, r.PublicId, r.Share == null, r.CanWrite, r.ShowOnly);
    }

    /// <summary>
    /// Alle Tagebücher des Benutzers im offenen Bereich: eigene (ohne eins wird „Mein Tagebuch“ angelegt) und mit ihm geteilte.
    /// Die im Tresor nicht - „Alle Tagebücher“, Übersicht und Schlagwörter lesen sie nie.
    /// </summary>
    internal static List<Scope> Scopes(HttpContext http)
    {
        var me = http.Space();
        var list = JournalStore.Journals(me.Db).Select(c => new Scope(me, c, c.Id, true, true, false)).ToList();
        foreach (var (share, owner, coll) in SharedLists.WithMe(http).Where(x => JournalStore.IsJournal(x.Coll)))
            list.Add(new Scope(owner, coll, -(long)share.Id, false, !share.ReadOnly.Contains(me.User.Id), share.IsShowOnly(me.User.Id)));
        return list;
    }

    /// <summary>Tagebuch im Tresor, der auf diesem Gerät zu ist: Neues wird nur abgelegt und beim Öffnen übernommen.</summary>
    private static bool Dropping(HttpContext http, long journal) =>
        TresorJournals.Is(journal) && !Tresor.IsOpen(http) && Tresor.Exists(http.Space().User.Id);

    /// <summary>Wohin neue Medien kommen: eigene Dateiablage, Besitzer eines geteilten Tagebuchs, Tresor (offen oder zu).</summary>
    private static JournalFiles MediaTarget(HttpContext http, long? journal) =>
        journal is not long j ? http.Space()
        : Dropping(http, j) ? new JournalFiles(http.Space(), null, dropping: true)
        : Resolve(http, j).Writable().Files;

    /// <summary>
    /// In einem fremden Tagebuch anhängen lässt sich nur, was in dessen Medienordner liegt (dorthin kommt alles Hochgeladene und
    /// jede Kopie) oder was der Eintrag schon hat - nie eine andere Datei des Besitzers.
    /// </summary>
    private static void CheckMedia(Scope s, string? uri, IEnumerable<JournalMedia>? media)
    {
        if (s.Own || media == null) return;
        var known = string.IsNullOrEmpty(uri) ? [] : s.Entry(uri).Media.Select(m => m.Path).ToHashSet();
        foreach (var m in media)
        {
            var path = FileStore.Normalize(m.Path);
            if (!FileStore.InRoot(path, JournalMediaStore.Root) && !known.Contains(path))
                throw new UnauthorizedAccessException("Diese Datei lässt sich hier nicht anhängen.");
        }
    }

    private static IEnumerable<JournalEntry> All(HttpContext http, long? journal) =>
        (journal is long j ? [Resolve(http, j)] : Scopes(http)).SelectMany(s => s.Entries());

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/journal").RequireAuthorization();
        TresorJournals.Register();

        api.MapGet("/journals", (HttpContext http, ShareStore shares, UserStore users) =>
        {
            var me = http.Space().User.Id;
            var list = Scopes(http).Select(s => new
            {
                id = s.PublicId, s.Coll.Name, s.Coll.Color, count = s.Entries().Count(), owner = s.Owner, canWrite = s.CanWrite, showOnly = s.ShowOnly,
                components = new[] { JournalStore.Component },
                // Wie bei Kalendern: mit wem geteilt (eigene) bzw. wer es noch hat (geteilte)
                sharedWith = (s.Own ? shares.Of(me, s.Coll.Id) : shares.ById((int)-s.PublicId)) is { } sh
                    ? sh.Members.Where(m => m != me).Select(m => new { id = m, name = users.ById(m)?.DisplayName, readOnly = sh.ReadOnly.Contains(m), level = sh.LevelOf(m) })
                        .Where(m => m.name != null).ToList()
                    : null,
                tresor = false,
            }).ToList<object>();
            // Die im Tresor nur, solange er auf diesem Gerät offen ist
            if (Tresor.IsOpen(http, touch: false) && Tresor.DbOf(me) is { } tdb)
                list.AddRange(TresorJournals.List(tdb).Select(c => new
                {
                    id = TresorJournals.Base + c.Id, c.Name, c.Color, count = TresorJournals.Count(tdb, c.Id), owner = (string?)null, canWrite = true, showOnly = false,
                    components = new[] { JournalStore.Component }, sharedWith = (object?)null, tresor = true,
                }));
            return list;
        });

        api.MapPost("/journals", (JournalBody body, HttpContext http) =>
        {
            if (body.Tresor == true)
            {
                var t = TresorJournals.Create(Tresor.Db(http), body.Name, body.Color);
                return Results.Ok(new { id = TresorJournals.Base + t.Id, t.Name, t.Color });
            }
            var c = JournalStore.Create(http.Space().Db, body.Name, body.Color);
            return Results.Ok(new { c.Id, c.Name, c.Color });
        });

        // In den Tresor verschieben bzw. aus dem Tresor holen (Tresor offen auf diesem Gerät). Geteilt: erst nach dem Hinweis
        // (unshare) - dann sind alle Freigaben weg.
        api.MapPost("/journals/{id:long}/move", async (long id, MoveBody body, HttpContext http, ShareStore shares, CancellationToken ct) =>
        {
            var space = http.Space();
            var tdb = Tresor.Db(http);
            if (body.To == "tresor")
            {
                if (TresorJournals.Is(id)) throw new ArgumentException("Dieses Tagebuch liegt schon im Tresor.");
                var c = JournalStore.Require(space.Db, id);
                if (shares.Of(space.User.Id, id) is { Members.Count: > 0 } && body.Unshare != true)
                    return Results.Conflict(new { error = TresorJournals.SharedHint, shared = true });
                var moved = await TresorJournals.MoveInAsync(space, tdb, c, ct);
                shares.Set(space.User.Id, id, [], [], []);
                return Results.Ok(new { id = TresorJournals.Base + moved.Id });
            }
            if (!TresorJournals.Is(id)) throw new ArgumentException("Dieses Tagebuch liegt nicht im Tresor.");
            var back = await TresorJournals.MoveOutAsync(space, tdb, id - TresorJournals.Base, ct);
            return Results.Ok(new { id = back.Id });
        });

        api.MapPut("/journals/{id:long}", (long id, JournalBody body, HttpContext http) =>
        {
            if (TresorJournals.Is(id))
            {
                TresorJournals.Rename(Tresor.Db(http), id - TresorJournals.Base, body.Name, body.Color);
                return Results.Ok();
            }
            var db = http.Space().Db;
            JournalStore.Require(db, id);
            if (string.IsNullOrWhiteSpace(body.Name)) throw new ArgumentException("Bitte einen Namen eingeben.");
            PimStore.UpdateCollection(db, id, body.Name.Trim(), body.Color, null);
            return Results.Ok();
        });

        api.MapDelete("/journals/{id:long}", (long id, HttpContext http, ShareStore shares) =>
        {
            if (TresorJournals.Is(id))
            {
                var tdb = Tresor.Db(http);
                TresorJournals.Require(tdb, id - TresorJournals.Base);
                TresorJournals.Delete(tdb, id - TresorJournals.Base);
                return Results.Ok();
            }
            var db = http.Space().Db;
            JournalStore.Require(db, id);
            if (JournalStore.Journals(db).Count <= 1) throw new ArgumentException("Das letzte Tagebuch lässt sich nicht löschen.");
            shares.Set(http.Space().User.Id, id, [], [], []);
            PimStore.DeleteCollection(db, id);
            return Results.Ok();
        });

        // Zeitleiste: neueste zuerst; Filter nach Tagebuch, Zeitraum, Text, Schlagwort, Stimmung; „before“ blättert weiter.
        // full: ganze Einträge (Drucken, Ausgabe als PDF), älteste zuerst
        api.MapGet("/entries", (long? journal, DateTime? from, DateTime? to, string? q, string? tag, int? mood, DateTime? before, int? limit, bool? full, HttpContext http) =>
        {
            var list = Filter(All(http, journal), from, to, q, tag, mood);
            if (before is DateTime b) list = list.Where(e => e.Date < b.ToUniversalTime());
            if (full == true) return Results.Ok(list.OrderBy(e => e.Date).Take(Math.Clamp(limit ?? 1000, 1, 5000)).ToList());
            return Results.Ok(list.OrderByDescending(e => e.Date).Take(Math.Clamp(limit ?? 50, 1, 1000)).Select(Preview).ToList());
        });

        api.MapGet("/entries/{journal:long}/{uri}", (long journal, string uri, HttpContext http) => Resolve(http, journal).Entry(uri));

        api.MapPost("/entries", (JournalInput body, HttpContext http) =>
        {
            // Neuer Eintrag in ein Tagebuch im geschlossenen Tresor: verschlüsselt ablegen, beim Öffnen übernommen
            if (string.IsNullOrEmpty(body.Uri) && Dropping(http, body.Journal))
            {
                TresorJournals.Drop(http.Space(), body);
                return Results.Ok(new { dropped = true });
            }
            var s = Resolve(http, body.Journal).Writable();
            if (s.InTresor is { } used) TresorJournals.Used(used, s.Coll.Id);
            // Private Einträge gibt es nur im eigenen Tagebuch; fremde private sind ohnehin unsichtbar
            if (!s.Own)
            {
                body.Private = false;
                if (!string.IsNullOrEmpty(body.Uri)) s.Entry(body.Uri);
                CheckMedia(s, body.Uri, body.Media);
            }
            try { return Results.Ok(s.Map(JournalStore.Save(s.Box, body))); }
            catch (PreconditionFailedException) { return Results.Conflict(new { error = "Der Eintrag wurde inzwischen woanders geändert. Bitte neu laden." }); }
        });

        // Einträge in ein anderes eigenes Tagebuch verschieben, auch in den Tresor und heraus (Medien kommen mit).
        // Zurück: die Einträge an ihrem neuen Platz.
        api.MapPost("/entries/move", async (MoveEntriesBody body, HttpContext http, CancellationToken ct) =>
        {
            var to = Resolve(http, body.To);
            if (!to.Own) throw new UnauthorizedAccessException("Verschieben geht nur zwischen eigenen Tagebüchern.");
            var moved = new List<JournalEntry>();
            foreach (var id in body.Ids.Distinct().Take(1000))
            {
                var cut = id.IndexOf('/');
                if (cut <= 0 || !long.TryParse(id[..cut], out var journal)) throw new ArgumentException("Ungültiger Eintrag.");
                var uri = id[(cut + 1)..];
                var from = Resolve(http, journal);
                if (!from.Own) throw new UnauthorizedAccessException("Verschieben geht nur zwischen eigenen Tagebüchern.");
                if (from.PublicId == to.PublicId) continue;
                from.Entry(uri);
                var name = await TresorJournals.MoveEntryAsync(http.Space(), from.InTresor ?? to.InTresor, from.Box, from.InTresor != null, to.Box, to.InTresor != null, uri, ct);
                moved.Add(to.Entry(name));
            }
            if (to.InTresor is { } used && moved.Count > 0) TresorJournals.Used(used, to.Coll.Id);
            return Results.Ok(moved);
        });

        api.MapDelete("/entries/{journal:long}/{uri}", (long journal, string uri, HttpContext http) =>
        {
            var s = Resolve(http, journal).Writable();
            s.Entry(uri);
            s.Box.Delete(uri);
            return Results.Ok();
        });

        // Eintrag in den Chat: Text und Fotos als Verweise (nur eigene Tagebücher - die Fotos gehören einem selbst)
        api.MapGet("/entries/{journal:long}/{uri}/chat", (long journal, string uri, HttpContext http) =>
        {
            var s = Resolve(http, journal);
            if (!s.Own) throw new UnauthorizedAccessException("Einträge aus einem geteilten Tagebuch kann nur der Besitzer weitergeben.");
            if (s.InTresor != null) throw new UnauthorizedAccessException("Einträge aus dem Tresor lassen sich nicht weitergeben.");
            var e = s.Entry(uri);
            var local = TimeZoneInfo.ConvertTimeFromUtc(e.Date, s.Space.Zone);
            var text = $"📔 {local.ToString("dddd, d. MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))}"
                + (string.IsNullOrWhiteSpace(e.Title) ? "" : " – " + e.Title) + (e.Text.Length > 0 ? "\n" + (e.Text.Length > 1500 ? e.Text[..1500] + " …" : e.Text) : "");
            var refs = e.Media.Select(m => FileStore.Get(s.Space.Db, m.Path)).Where(f => f is { IsDir: false })
                .Select(f => new { type = "file", fileId = f!.FileId }).Take(20).ToList();
            return new { text, refs };
        });

        api.MapGet("/tags", (long? journal, HttpContext http) =>
            All(http, journal).SelectMany(e => e.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { tag = g.First(), count = g.Count() }).OrderByDescending(x => x.count).ThenBy(x => x.tag).ToList());

        // Heute vor einer Woche, einem Monat, einem Jahr, zwei Jahren … (Ortszeit des Benutzers)
        api.MapGet("/onthisday", (DateOnly? date, HttpContext http) =>
        {
            var space = http.Space();
            var today = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone));
            return OnThisDay(All(http, null), today, space.Zone).Select(x => new { x.Label, x.Day, entries = x.Entries.Select(Preview) });
        });

        // Stimmung über die Zeit: je Tag der Durchschnitt (für den Verlauf)
        api.MapGet("/moods", (DateTime? from, long? journal, HttpContext http) =>
        {
            var zone = http.Space().Zone;
            var since = (from ?? DateTime.UtcNow.AddDays(-90)).ToUniversalTime();
            return All(http, journal).Where(e => e.Mood != null && e.Date >= since)
                .GroupBy(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(e.Date, zone)))
                .OrderBy(g => g.Key).Select(g => new { day = g.Key, mood = Math.Round(g.Average(e => e.Mood!.Value), 1), count = g.Count() }).ToList();
        });

        api.MapGet("/settings", (HttpContext http) => new
        {
            imageSize = JournalMediaStore.ImageSize(http.Space().Db), sizes = JournalMediaStore.Sizes, lookup = JournalLookup.Enabled(http.Space().Db),
            ask = DmOffice.Chat.Pukki.JournalAskTime(http.Space().Db),
        });
        api.MapPut("/settings", (SettingsBody body, HttpContext http) =>
        {
            var db = http.Space().Db;
            if (body.ImageSize is int size) JournalMediaStore.SetImageSize(db, size);
            if (body.Lookup is bool on) JournalLookup.SetEnabled(db, on);
            if (body.Ask != null) DmOffice.Chat.Pukki.SetJournalAskTime(db, body.Ask);
            return Results.Ok(new { imageSize = JournalMediaStore.ImageSize(db), lookup = JournalLookup.Enabled(db), ask = DmOffice.Chat.Pukki.JournalAskTime(db) });
        });

        // Wetter und Ortsname zu Ort und Tag - nur eingeschaltet (Ort und Datum gehen an Open-Meteo bzw. OpenStreetMap)
        api.MapGet("/lookup", async (double lat, double lon, DateTime? date, HttpContext http, IHttpClientFactory factory, CancellationToken ct) =>
        {
            var space = http.Space();
            if (!JournalLookup.Enabled(space.Db)) return Results.BadRequest(new { error = "Wetter und Ortsnamen sind ausgeschaltet – einschalten im Tagebuch unter „…“ → „Einstellungen“." });
            // Uhrzeit des Eintrags: das Wetter dieser Stunde (Open-Meteo rechnet in der Ortszeit des Orts - bei uns dieselbe)
            var local = TimeZoneInfo.ConvertTimeFromUtc((date ?? DateTime.UtcNow).ToUniversalTime(), space.Zone);
            var day = DateOnly.FromDateTime(local);
            var client = factory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var weather = JournalLookup.Weather(client, lat, lon, day, ct, local.Hour);
            var place = JournalLookup.PlaceName(client, lat, lon, ct);
            return Results.Ok(new { weather = await weather, place = await place });
        });

        // Termine eines Tages (eigene und geteilte Kalender) - für „Termine dieses Tages zeigen“
        api.MapGet("/events", (DateOnly date, HttpContext http) => EventsOf(http, date));

        // Anfang für den Eintrag: Termine, erledigte Aufgaben und Fotos des Tages
        api.MapGet("/suggest", (DateOnly date, HttpContext http) =>
        {
            var space = http.Space();
            var events = EventsOf(http, date).ToList();
            var done = DoneTasks(space, date);
            var parts = new List<string>();
            if (events.Count > 0) parts.Add("<p><b>Termine:</b> " + string.Join(", ", events.Select(e => System.Net.WebUtility.HtmlEncode((e.AllDay ? "" : e.Time + " ") + e.Title))) + "</p>");
            if (done.Count > 0) parts.Add("<p><b>Erledigt:</b> " + string.Join(", ", done.Select(System.Net.WebUtility.HtmlEncode)) + "</p>");
            return new { html = string.Concat(parts), events = events.Count, tasks = done.Count };
        });

        // Vorschlag zum Auswählen: Termine, erledigte Aufgaben und die Fotos und Videos des Tages aus der Galerie
        // (eigene Ablage und Galerie-Ordner, nach Aufnahmedatum) - der Editor lässt anhaken, was übernommen wird
        api.MapGet("/suggest/day", (DateOnly date, HttpContext http, ServerConfig config) =>
            new { events = EventsOf(http, date).ToList(), tasks = DoneTasks(http.Space(), date), photos = PhotosOf(http.Space(), config, date) });

        // Fotos und Videos aus einem Galerie-Ordner (externer Speicher) anhängen - als Kopie im Tagebuch, Bilder kleingerechnet
        api.MapPost("/media/external", async (ExternalMediaBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            var target = MediaTarget(http, body.Journal);
            var found = new List<object>();
            foreach (var id in body.Ids.Take(100))
            {
                // Schon eine große Vorschau da (2048 px, sonst 1600 px): die nehmen statt das Original über die Freigabe zu holen
                if (DmOffice.Gallery.GalleryFolders.ReadyLarge(space, config, id) is { } ready
                    && JournalMediaStore.FromPreview(target, id[(id.LastIndexOf('/') + 1)..], ready.Image, ready.Size, ready.Taken, ready.Lat, ready.Lon) is { } quick)
                {
                    found.Add(new { media = quick.Media, lat = quick.Lat, lon = quick.Lon });
                    continue;
                }
                var (src, path) = DmOffice.Gallery.GalleryFolders.Resolve(space, config, id);
                var (driver, _) = DmOffice.Storage.ExternalFolders.Open(space, config, src.Mount);
                using (driver)
                {
                    var entry = await driver.StatAsync(path, ct) ?? throw new KeyNotFoundException("Diese Datei gibt es nicht mehr.");
                    await using var s = await driver.OpenReadAsync(path, ct);
                    var f = await JournalMediaStore.Store(target, entry.Name, DavServer.Mime(path), s, entry.Size, ct);
                    found.Add(new { media = f.Media, lat = f.Lat, lon = f.Lon });
                }
            }
            return found;
        });

        // Hochgeladen in Stücken (/api/files/uploads) - abschließen ins Tagebuch: Bild kleingerechnet, Datum und Ort ausgelesen.
        // Bei einem geteilten Tagebuch landet die Datei beim Besitzer, damit alle sie sehen.
        api.MapPost("/uploads/{id}/attach", async (string id, long? journal, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var target = MediaTarget(http, journal);
            var s = FilesApi.RequireComplete(space.Db, id);
            JournalMediaStore.Found found;
            await using (var body = FilesApi.OpenSession(space.Db, s))
                found = await JournalMediaStore.Store(target, s.Name, s.Mime, body, s.Size, ct);
            FilesApi.DropSession(space.Db, s);
            return Results.Ok(new { media = found.Media, lat = found.Lat, lon = found.Lon });
        });

        // Dateien aus der eigenen Dateiablage anhängen (in ein geteiltes Tagebuch als Kopie beim Besitzer)
        api.MapPost("/media/files", (FilesBody body, HttpContext http) =>
        {
            var space = http.Space();
            var target = MediaTarget(http, body.Journal);
            return body.Paths.Take(100).Select(p => JournalMediaStore.FromFiles(space, target, p)).Select(f => new { media = f.Media, lat = f.Lat, lon = f.Lon }).ToList();
        });

        // Foto, Video oder Datei eines Eintrags: Vorschaubild (size) oder Inhalt - nur, was ein sichtbarer Eintrag enthält
        api.MapGet("/file", async (long journal, string path, int? size, bool? download, HttpContext http, CancellationToken ct) =>
        {
            var s = Resolve(http, journal);
            path = FileStore.Normalize(path);
            if (!s.Own && (FileStore.IsVaultOrSync(s.Space.Db, path) || !s.Entries().Any(e => e.Media.Any(m => m.Path == path)))) throw new KeyNotFoundException("Nicht gefunden.");
            if (s.InTresor is { } tdb)
            {
                var m = TresorJournals.Media(tdb, path) ?? throw new KeyNotFoundException("Diese Datei gibt es nicht mehr.");
                if (size is int tpx)
                {
                    var thumb = TresorJournals.Thumb(tdb, m, tpx);
                    if (thumb == null) return Results.NotFound();
                    http.Response.Headers.CacheControl = "private, no-store";
                    return Results.Bytes(thumb, "image/webp");
                }
                http.Response.Headers.CacheControl = "private, no-store";
                var (tm, tdl) = FilesApi.Shown(http, m.Mime, Path.GetFileName(m.Path), download != true);
                return Results.File(TresorJournals.Open(tdb, m), tm, tdl, enableRangeProcessing: true);
            }
            var f = FileStore.Get(s.Space.Db, path) ?? throw new KeyNotFoundException("Diese Datei gibt es nicht mehr.");
            if (f.IsDir) throw new KeyNotFoundException("Nicht gefunden.");
            if (size is int px)
            {
                var thumb = await Thumbs.Get(s.Space.Db, f, px, ct);
                if (thumb == null) return Results.NotFound();
                http.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.Bytes(thumb, "image/webp");
            }
            // Nur gezeigt: ansehen ja, herunterladen nein
            if (download == true && s.ShowOnly) throw new UnauthorizedAccessException("Nur gezeigt – herunterladen ist nicht erlaubt.");
            var (mime, dl) = FilesApi.Shown(http, string.IsNullOrEmpty(f.Mime) ? JournalMediaStore.MimeOf(f.Name) : f.Mime, f.Name, download != true);
            return Results.File(FileStore.OpenRead(s.Space.Db, path), mime, dl, enableRangeProcessing: true);
        });

        // Alle Medien (Ansicht „Medien“): neueste zuerst, mit dem Eintrag dazu
        // Ansicht „Medien“: wie die Zeitleiste nach Schlagwort und Stimmung eingeschränkt
        api.MapGet("/media", (long? journal, int? limit, string? tag, int? mood, HttpContext http) =>
            All(http, journal).Where(e => string.IsNullOrEmpty(tag) || e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Where(e => mood == null || e.Mood == mood).OrderByDescending(e => e.Date)
                .SelectMany(e => e.Media.Where(m => m.Mime.StartsWith("image/") || m.Mime.StartsWith("video/")).Select(m => new { entry = e.Id, journal = e.Journal, date = e.Date, media = m }))
                .Take(Math.Clamp(limit ?? 3000, 1, 5000)).ToList());

        // Orte (Ansicht „Atlas“): Einträge mit Koordinaten
        api.MapGet("/places", (long? journal, HttpContext http) =>
            All(http, journal).Where(e => e.Place?.Lat != null && e.Place.Lon != null).OrderByDescending(e => e.Date)
                .Select(e => new { e.Id, e.Journal, e.Uri, e.Date, e.Title, place = e.Place, photo = e.Media.FirstOrDefault(m => m.Mime.StartsWith("image/")) }).ToList());

        // Geteilte Bilder → Einträge: je Aufnahmetag einer; gibt es an dem Tag schon einen, kommen sie dazu
        api.MapPost("/from-media", (FromMediaBody body, HttpContext http) =>
        {
            var s = Resolve(http, body.Journal).Writable();
            CheckMedia(s, null, body.Media);
            var zone = s.Space.Zone;
            var places = (body.Places ?? []).GroupBy(p => p.Path).ToDictionary(g => g.Key, g => g.First());
            var existing = s.Entries().ToList();
            var saved = new List<JournalEntry>();
            foreach (var day in body.Media.GroupBy(m => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(m.Taken ?? DateTime.UtcNow, zone))).OrderBy(g => g.Key))
            {
                var media = day.OrderBy(m => m.Taken).ToList();
                var place = media.Select(m => places.GetValueOrDefault(m.Path)).FirstOrDefault(p => p != null);
                var same = existing.Where(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(e.Date, zone)) == day.Key).OrderByDescending(e => e.Date).FirstOrDefault();
                var input = same != null ? Input(same) : new JournalInput { Journal = s.PublicId, Date = media[0].Taken ?? DateTime.UtcNow, Html = "", Media = [] };
                input.Media = [.. input.Media!, .. media.Where(m => input.Media!.All(x => x.Path != m.Path))];
                if (input.Place == null && place != null) input.Place = new JournalPlace(null, place.Lat, place.Lon);
                if (!s.Own) input.Private = false;
                saved.Add(s.Map(JournalStore.Save(s.Box, input)));
            }
            return saved;
        });

        // Import aus Diarium: ZIP aus der Dateiablage, läuft im Hintergrund (nur in eigene Tagebücher)
        api.MapPost("/import/diarium", (ImportBody body, HttpContext http) =>
        {
            var space = http.Space();
            var journal = body.Journal ?? JournalStore.Journals(space.Db).First().Id;
            if (TresorJournals.Is(journal)) throw new ArgumentException("In ein Tagebuch im Tresor lässt sich noch nicht importieren.");
            try { return Results.Ok(JournalImport.Start(space, journal, body.Path)); }
            catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
        });
        api.MapGet("/import", (HttpContext http) => JournalImport.Of(http.Space().User.Id));

        // Export als ZIP: je Eintrag Markdown, Fotos und Videos im Original, tagebuch.json zum Wiedereinlesen (Zeitraum in Tagen, beide einschließlich)
        api.MapGet("/export", async (long journal, DateOnly? from, DateOnly? to, bool? media, HttpContext http) =>
        {
            var s = Resolve(http, journal);
            if (s.ShowOnly) throw new UnauthorizedAccessException("Nur gezeigt – exportieren ist nicht erlaubt.");
            var zone = http.Space().Zone;
            await JournalExport.Write(http, s, zone, JournalExport.Select(s.Entries(), zone, from, to), media ?? true);
        });
    }

    /// <param name="Meal">Aus einem Essensplan-Kalender: die Mahlzeit (Frühstück, Mittagessen, Abendessen; sonst „Mahlzeit“)</param>
    public sealed record DayEvent(string Title, string? Time, bool AllDay, string? Location, string? Meal = null);

    private static readonly string[] MealNames = ["Frühstück", "Mittagessen", "Abendessen", "Snack"];

    internal static IEnumerable<DayEvent> EventsOf(HttpContext http, DateOnly date)
    {
        var space = http.Space();
        var from = Ics.ToUtc(date.ToDateTime(TimeOnly.MinValue), space.Zone);
        var to = Ics.ToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), space.Zone);
        var ids = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT")).Select(c => c.Id).ToList();
        // CalendarModel.Events greift für die Kalenderansicht einen Tag weiter (ganztägige Termine) - hier nur genau dieser Tag
        bool OnDay(EventDto e)
        {
            var start = CalendarModel.ParseIso(e.Start, out _);
            var end = CalendarModel.ParseIso(e.End, out _);
            if (e.AllDay) return DateOnly.FromDateTime(start) <= date && date < DateOnly.FromDateTime(end > start ? end : start.AddDays(1));
            return start < to && (end > start ? end : start.AddMinutes(1)) > from;
        }
        var meals = ids.Where(id => CalendarKinds.Of(space.Db, id) == CalendarKinds.Meal).ToHashSet();
        return CalendarModel.Events(space, from, to, ids).Where(OnDay).OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start)
            .Select(e => new DayEvent(string.IsNullOrWhiteSpace(e.Title) ? "Termin" : e.Title,
                e.AllDay ? null : TimeZoneInfo.ConvertTimeFromUtc(CalendarModel.ParseIso(e.Start, out _), space.Zone).ToString("HH:mm"), e.AllDay, e.Location,
                meals.Contains(e.Calendar) ? MealNames.FirstOrDefault(m => e.Categories.Contains(m, StringComparer.OrdinalIgnoreCase)) ?? "Mahlzeit" : null));
    }

    /// <summary>
    /// Fotos und Videos, die an diesem Tag aufgenommen wurden: eigene Ablage (ohne ausgeschlossene Ordner und ohne die
    /// Tagebuch-Medien selbst) und Galerie-Ordner. Höchstens 200, nach Aufnahmezeit.
    /// </summary>
    internal static List<DayPhoto> PhotosOf(UserSpace space, ServerConfig config, DateOnly date)
    {
        bool OnDay(long? ms) => ms is long m && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(m).UtcDateTime, space.Zone)) == date;
        var excluded = space.Settings.PhotoExcludes;
        var list = new List<(DayPhoto Photo, long Size, string? Sha)>();
        foreach (var r in FileMeta.List(space.Db, null).Where(r => OnDay(r.Taken)))
        {
            if (FileStore.InRoot(r.Path, JournalMediaStore.Root) || FileKinds.UnderAny(excluded, r.Path) || FileStore.Get(space.Db, r.Path) is not { IsDir: false } f) continue;
            list.Add((new DayPhoto("file", f.Path, f.Name, f.Mime, f.Etag, r.Taken, r.Lat, r.Lon), f.Size, FileHashes.Of(space.Db, f.Path)));
        }
        // Galerie-Ordner: der Ort steht in einer eigenen Tabelle (aus dem Kopf des Fotos, einer Sidecar-Datei oder vom Verorten)
        foreach (var e in DmOffice.Gallery.GalleryFolders.List(space, config).Where(e => OnDay(e.Meta?.Taken)))
        {
            var gps = DmOffice.Gallery.GalleryFolders.Gps(space.Db, e.Source, e.Path);
            list.Add((new DayPhoto("ext", "ext:" + e.Source + "/" + e.Path, e.Path[(e.Path.LastIndexOf('/') + 1)..], e.Mime,
                e.Size.ToString("x") + "-" + e.Modified.ToString("x"), e.Meta?.Taken, gps.Lat, gps.Lon), e.Size, null));
        }
        // Dasselbe Foto mehrfach (vom Handy in die Ablage und auf dem NAS im Galerie-Ordner, oder doppelt abgelegt): nur einmal,
        // die eigene Ablage zuerst. Gleich ist: gleiche Prüfsumme, oder gleiche Aufnahmesekunde mit gleicher Größe oder gleichem Namen.
        var kept = new List<(DayPhoto Photo, long Size, string? Sha)>();
        foreach (var x in list.OrderBy(x => x.Photo.Kind == "file" ? 0 : 1))
            if (!kept.Any(k => SamePhoto(k, x))) kept.Add(x);
        return kept.Select(x => x.Photo).OrderBy(p => p.Taken).Take(200).ToList();
    }

    private static string BaseName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(name), @" \(\d+\)$", "").ToLowerInvariant();

    private static bool SamePhoto((DayPhoto Photo, long Size, string? Sha) a, (DayPhoto Photo, long Size, string? Sha) b)
    {
        if (a.Sha != null && a.Sha == b.Sha) return true;
        if (a.Photo.Taken is not long ta || b.Photo.Taken is not long tb || ta / 1000 != tb / 1000) return false;
        return a.Size == b.Size || BaseName(a.Photo.Name) == BaseName(b.Photo.Name);
    }

    /// <summary>Aufgaben, die an diesem Tag erledigt wurden (eigene Listen, ohne Einkaufslisten - abgehakte Artikel sind kein Tagesereignis).</summary>
    internal static List<string> DoneTasks(UserSpace space, DateOnly date)
    {
        var list = new List<string>();
        foreach (var c in PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO") && Lists.TypeOf(space.Db, c.Id) != Lists.Shop))
            foreach (var o in PimStore.Objects(space.Db, c.Id))
            {
                Component? todo;
                try { todo = Ics.Parse(o.Data).FirstOrDefault(x => x.Name == "VCALENDAR")?.Of("VTODO").FirstOrDefault(); } catch (Exception) { continue; }
                if (todo?.Get("COMPLETED") is not { } done || Ics.Time(done, space.Zone, out _) is not DateTime when) continue;
                if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(when, space.Zone)) == date && todo.Text("SUMMARY") is { Length: > 0 } title) list.Add(title);
            }
        return list.Take(20).ToList();
    }

    /// <summary>Gruppen für „Heute vor …“.</summary>
    internal static List<(string Label, DateOnly Day, List<JournalEntry> Entries)> OnThisDay(IEnumerable<JournalEntry> entries, DateOnly today, TimeZoneInfo zone)
    {
        var byDay = entries.GroupBy(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(e.Date, zone))).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Date).ToList());
        var result = new List<(string, DateOnly, List<JournalEntry>)>();
        void Add(string label, DateOnly day) { if (byDay.TryGetValue(day, out var list)) result.Add((label, day, list)); }
        Add("vor einer Woche", today.AddDays(-7));
        Add("vor einem Monat", today.AddMonths(-1));
        for (var y = 1; y <= 30; y++) Add(y == 1 ? "vor einem Jahr" : $"vor {y} Jahren", today.AddYears(-y));
        return result;
    }

    private static IEnumerable<JournalEntry> Filter(IEnumerable<JournalEntry> list, DateTime? from, DateTime? to, string? q, string? tag, int? mood)
    {
        if (from is DateTime f) list = list.Where(e => e.Date >= f.ToUniversalTime());
        if (to is DateTime t) list = list.Where(e => e.Date < t.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(tag)) list = list.Where(e => e.Tags.Contains(tag.Trim().TrimStart('#'), StringComparer.OrdinalIgnoreCase));
        if (mood is int m) list = list.Where(e => e.Mood == m);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var words = q.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list = list.Where(e => words.All(w => (e.Title ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || e.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase) || (e.Place?.Name ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || e.Tags.Any(x => x.Contains(w.TrimStart('#'), StringComparison.CurrentCultureIgnoreCase))));
        }
        return list;
    }

    internal static EntryPreview Preview(JournalEntry e) => new(e.Id, e.Journal, e.Uri, e.Date, e.Title,
        e.Text.Length > 280 ? e.Text[..280].TrimEnd() + " …" : e.Text, e.Mood, e.Tags, e.Place, e.Private, e.Media, e.Refs.Count, e.Weather, e.Owner, e.CanWrite);

    private static JournalInput Input(JournalEntry e) => new()
    {
        Journal = e.Journal, Uri = e.Uri, Etag = e.Etag, Date = e.Date, Title = e.Title, Html = e.Html, Mood = e.Mood, Tags = e.Tags,
        Place = e.Place, Private = e.Private, ShowEvents = e.ShowEvents, Weather = e.Weather, Media = [.. e.Media], Refs = e.Refs,
    };
}
