using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Abgleich mit Microsoft Graph: Umwandeln in beide Richtungen, Delta, Warteschlange, kein Echo, Konflikte.
/// Microsoft ist ein Fake (HttpMessageHandler) - kein echtes Konto.</summary>
public class GraphTests
{
    /// <summary>Antwortet nach einer Regel je Anfrage und merkt sich die Anfragen (Methode, Pfad samt Abfrage, Inhalt).</summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Requests = new();
        public Func<string, string, string, (HttpStatusCode Status, string Json)> Answer = (_, _, _) => (HttpStatusCode.NotFound, "{}");
        public Func<string, string, string, HttpResponseMessage?>? Raw;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Replace("/v1.0", "");
            Requests.Add((request.Method.Method, path, body));
            Assert.Equal("Bearer TOKEN", request.Headers.Authorization?.ToString());
            if (Raw?.Invoke(request.Method.Method, path, body) is { } raw) return raw;
            var (status, json) = Answer(request.Method.Method, path, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static UserSpace NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmgraph");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        PimStore.EnsureDefaults(db);
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        space.UpdateSettings(s => s.TimeZone = "Europe/Berlin");
        return space;
    }

    private static MailAccount Account(bool tasks = false, bool contacts = false, bool calendars = false) => new()
    {
        Id = "acc", Email = "anna@outlook.com", Auth = MailAuth.Microsoft, GraphRefreshToken = "RT",
        GraphTasks = tasks, GraphContacts = contacts, GraphCalendars = calendars,
    };

    private static (GraphEngine Engine, FakeGraph Fake, List<TimeSpan> Waits) Engine(UserSpace space, MailAccount account)
    {
        var fake = new FakeGraph();
        var waits = new List<TimeSpan>();
        var client = new GraphClient(new HttpClient(fake), _ => Task.FromResult("TOKEN"))
        {
            Wait = (t, _) => { waits.Add(t); return Task.CompletedTask; },
        };
        return (new GraphEngine(space, account, client), fake, waits);
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static Component Vcal(string ics) => Ics.Parse(ics).First(r => r.Name == "VCALENDAR");
    private static readonly TimeZoneInfo Berlin = Ics.Zone("Europe/Berlin")!;

    private static long SyncToken(UserDb db, long coll) => (long)db.Scalar("SELECT synctoken FROM collections WHERE id=$c", ("$c", coll))!;
    private static long Count(UserDb db, string table) => (long)db.Scalar($"SELECT COUNT(*) FROM {table}")!;

    // ---------------- Umwandeln ----------------

    [Fact]
    public void Erledigte_Aufgabe_hin_und_zurueck()
    {
        var task = J("""
        {"id":"T1","@odata.etag":"W/\"e1\"","title":"Steuer, Belege","status":"completed","importance":"high",
         "body":{"content":"Ordner\r\nim Keller","contentType":"text"},
         "dueDateTime":{"dateTime":"2026-11-01T23:00:00.0000000","timeZone":"UTC"},
         "completedDateTime":{"dateTime":"2026-10-25T08:30:00.0000000","timeZone":"UTC"},
         "categories":["Büro"],"isReminderOn":true,"reminderDateTime":{"dateTime":"2026-11-01T07:00:00.0000000","timeZone":"UTC"},
         "lastModifiedDateTime":"2026-10-25T08:30:00Z"}
        """);
        var ics = GraphConvert.TaskToIcs(task, null, Berlin);
        var todo = Vcal(ics).Of("VTODO").Single();
        Assert.Equal("Steuer, Belege", todo.Text("SUMMARY"));
        Assert.Equal("Ordner\nim Keller", todo.Text("DESCRIPTION"));
        Assert.Equal("20261102", todo.Get("DUE")!.Value);   // 23:00 UTC ist in Berlin schon der 2.
        Assert.Equal("COMPLETED", todo.Text("STATUS"));
        Assert.Equal("20261025T083000Z", todo.Text("COMPLETED"));
        Assert.Equal("1", todo.Text("PRIORITY"));
        Assert.Equal("Büro", todo.Text("CATEGORIES"));
        Assert.Equal("20261101T070000Z", todo.Of("VALARM").Single().Text("TRIGGER"));

        var back = GraphConvert.TaskToGraph(ics, Berlin);
        Assert.Equal("Steuer, Belege", (string?)back["title"]);
        Assert.Equal("completed", (string?)back["status"]);
        Assert.Equal("high", (string?)back["importance"]);
        Assert.Equal("2026-11-02T00:00:00", (string?)back["dueDateTime"]!["dateTime"]);
        Assert.Equal("W. Europe Standard Time", (string?)back["dueDateTime"]!["timeZone"]);
        Assert.True((bool)back["isReminderOn"]!);
        Assert.Equal("2026-11-01T08:00:00", (string?)back["reminderDateTime"]!["dateTime"]);
        Assert.Equal("Büro", (string?)back["categories"]![0]);
    }

    [Fact]
    public void Kontakt_mit_allen_Feldern_hin_und_zurueck()
    {
        var c = J("""
        {"id":"C1","changeKey":"k1","displayName":"Dr. Anna Maria Beispiel","givenName":"Anna","middleName":"Maria","surname":"Beispiel",
         "title":"Dr.","nickName":"Anni","companyName":"Muster GmbH","department":"Einkauf","jobTitle":"Leiterin",
         "emailAddresses":[{"name":"Anna","address":"anna@example.org"},{"address":"a.b@muster.de"}],
         "businessPhones":["+49 30 1234"],"homePhones":["+49 30 9876"],"mobilePhone":"+49 170 5555",
         "homeAddress":{"street":"Hauptstr. 1","city":"Berlin","state":"BE","postalCode":"10115","countryOrRegion":"Deutschland"},
         "businessAddress":{"street":"Am Markt 2","city":"Potsdam","postalCode":"14467"},
         "otherAddress":{},
         "birthday":"1980-04-12T11:59:00Z","personalNotes":"Mag Tee; kein Kaffee","categories":["Kunden","VIP"]}
        """);
        var vcf = GraphConvert.ContactToVcf(c, null);
        var d = ContactModel.Read(new DavObject(1, 1, "a.vcf", null, "VCARD", "", vcf, ""), false)!;
        Assert.Equal("Dr. Anna Maria Beispiel", d.DisplayName);
        Assert.Equal(("Anna", "Maria", "Beispiel", "Dr."), (d.Given, d.Additional, d.Family, d.Prefix));
        Assert.Equal(("Muster GmbH", "Einkauf", "Leiterin", "Anni"), (d.Org, d.Department, d.Title, d.Nickname));
        Assert.Equal(new[] { "anna@example.org", "a.b@muster.de" }, d.Emails.Select(e => e.Value));
        Assert.Contains(d.Phones, p => p is { Type: "cell", Value: "+49 170 5555" });
        Assert.Contains(d.Phones, p => p is { Type: "work", Value: "+49 30 1234" });
        Assert.Contains(d.Phones, p => p is { Type: "home", Value: "+49 30 9876" });
        Assert.Equal(2, d.Addresses.Count);   // leere Adresse faellt weg
        Assert.Contains(d.Addresses, a => a is { Type: "home", Street: "Hauptstr. 1", City: "Berlin", Region: "BE", Zip: "10115", Country: "Deutschland" });
        Assert.Contains(d.Addresses, a => a is { Type: "work", City: "Potsdam" });
        Assert.Equal("1980-04-12", d.Birthday);
        Assert.Equal("Mag Tee; kein Kaffee", d.Note);
        Assert.Equal(new[] { "Kunden", "VIP" }, d.Categories);

        var back = GraphConvert.ContactToGraph(vcf);
        Assert.Equal("Anna", (string?)back["givenName"]);
        Assert.Equal("Beispiel", (string?)back["surname"]);
        Assert.Equal("Maria", (string?)back["middleName"]);
        Assert.Equal("Dr.", (string?)back["title"]);
        Assert.Equal("Leiterin", (string?)back["jobTitle"]);
        Assert.Equal("+49 170 5555", (string?)back["mobilePhone"]);
        Assert.Equal("+49 30 1234", (string?)back["businessPhones"]![0]);
        Assert.Equal("+49 30 9876", (string?)back["homePhones"]![0]);
        Assert.Equal("Berlin", (string?)back["homeAddress"]!["city"]);
        Assert.Equal("Potsdam", (string?)back["businessAddress"]!["city"]);
        Assert.Equal("a.b@muster.de", (string?)back["emailAddresses"]![1]!["address"]);
        Assert.Equal("1980-04-12T00:00:00Z", (string?)back["birthday"]);
        Assert.Equal("Mag Tee; kein Kaffee", (string?)back["personalNotes"]);
    }

    private const string SeriesMaster = """
        {"id":"E1","changeKey":"k1","type":"seriesMaster","iCalUId":"UID-SERIE","subject":"Teamrunde","isAllDay":false,
         "start":{"dateTime":"2026-10-05T07:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-10-05T08:00:00.0000000","timeZone":"UTC"},
         "originalStartTimeZone":"W. Europe Standard Time","originalEndTimeZone":"W. Europe Standard Time",
         "location":{"displayName":"Raum 3"},"showAs":"busy","categories":["Arbeit"],"isReminderOn":true,"reminderMinutesBeforeStart":15,
         "body":{"contentType":"text","content":"Wochenplan"},
         "organizer":{"emailAddress":{"name":"Anna","address":"anna@outlook.com"}},
         "attendees":[{"type":"required","status":{"response":"accepted"},"emailAddress":{"name":"Bert","address":"bert@example.org"}}],
         "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["monday"],"firstDayOfWeek":"monday"},
                       "range":{"type":"numbered","startDate":"2026-10-05","numberOfOccurrences":10,"recurrenceTimeZone":"W. Europe Standard Time"}},
         "lastModifiedDateTime":"2026-10-01T10:00:00Z"}
        """;

    private const string SeriesException = """
        {"id":"E1-x","type":"exception","originalStart":"2026-10-12T07:00:00Z","subject":"Teamrunde (verschoben)","isAllDay":false,
         "start":{"dateTime":"2026-10-12T09:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-10-12T10:00:00.0000000","timeZone":"UTC"},
         "location":{"displayName":"Raum 5"},"showAs":"busy","categories":[]}
        """;

    [Fact]
    public void Serie_mit_Ausnahme_und_Absage_in_Windows_Zeitzone()
    {
        var ics = GraphConvert.EventToIcs(J(SeriesMaster), [J(SeriesException)], [new DateTime(2026, 10, 19, 7, 0, 0, DateTimeKind.Utc)], null);
        var cal = Vcal(ics);
        Assert.Equal("Europe/Berlin", cal.Of("VTIMEZONE").Single().Text("TZID"));
        var events = cal.Of("VEVENT").ToList();
        Assert.Equal(2, events.Count);
        var master = events[0];
        Assert.Equal("UID-SERIE", master.Text("UID"));
        Assert.Equal("20261005T090000", master.Get("DTSTART")!.Value);   // 07:00 UTC = 09:00 Sommerzeit
        Assert.Equal("Europe/Berlin", master.Get("DTSTART")!.Param("TZID"));
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO;COUNT=10", master.Text("RRULE"));
        Assert.Equal("20261019T090000", master.Get("EXDATE")!.Value);
        Assert.Equal("Raum 3", master.Text("LOCATION"));
        Assert.Equal("-PT15M", master.Of("VALARM").Single().Text("TRIGGER"));
        Assert.Equal("mailto:bert@example.org", master.Get("ATTENDEE")!.Value);
        Assert.Equal("ACCEPTED", master.Get("ATTENDEE")!.Param("PARTSTAT"));
        var ex = events[1];
        Assert.Equal("UID-SERIE", ex.Text("UID"));
        Assert.Equal("20261012T090000", ex.Get("RECURRENCE-ID")!.Value);
        Assert.Equal("20261012T110000", ex.Get("DTSTART")!.Value);
        Assert.Equal("Raum 5", ex.Text("LOCATION"));

        // Gespeichert und ausgerechnet: neun Termine (zehn, einer abgesagt), einer verschoben
        var space = NewSpace();
        var coll = PimStore.Collections(space.Db, PimStore.Calendar).First();
        PimStore.Put(space.Db, coll, "s.ics", ics, zone: space.Zone);
        var list = CalendarModel.Events(space, new DateTime(2026, 10, 1), new DateTime(2027, 1, 1), [coll.Id]);
        Assert.Equal(9, list.Count);
        Assert.Contains(list, e => e.Title == "Teamrunde (verschoben)");

        // Zurueck: Serie mit Muster in Windows-Zeitzone
        var back = GraphConvert.EventToGraph(master, Berlin);
        Assert.Equal("2026-10-05T09:00:00", (string?)back["start"]!["dateTime"]);
        Assert.Equal("W. Europe Standard Time", (string?)back["start"]!["timeZone"]);
        Assert.Equal("weekly", (string?)back["recurrence"]!["pattern"]!["type"]);
        Assert.Equal("monday", (string?)back["recurrence"]!["pattern"]!["daysOfWeek"]![0]);
        Assert.Equal("numbered", (string?)back["recurrence"]!["range"]!["type"]);
        Assert.Equal(10, (int)back["recurrence"]!["range"]!["numberOfOccurrences"]!);
        Assert.Equal("2026-10-05", (string?)back["recurrence"]!["range"]!["startDate"]);
        Assert.Equal(15, (int)back["reminderMinutesBeforeStart"]!);
        Assert.Null(back["attendees"]);   // Teilnehmer gehen nicht zurueck (keine Einladungen)
    }

    [Fact]
    public void Ganztaegiger_Termin_hin_und_zurueck()
    {
        var e = J("""
        {"id":"E2","changeKey":"k","type":"singleInstance","iCalUId":"U2","subject":"Urlaub","isAllDay":true,"showAs":"free",
         "start":{"dateTime":"2026-12-24T00:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-12-27T00:00:00.0000000","timeZone":"UTC"},
         "originalStartTimeZone":"W. Europe Standard Time"}
        """);
        var ev = Vcal(GraphConvert.EventToIcs(e, [], [], null)).Of("VEVENT").Single();
        Assert.Equal("20261224", ev.Get("DTSTART")!.Value);
        Assert.Equal("DATE", ev.Get("DTSTART")!.Param("VALUE"));
        Assert.Equal("20261227", ev.Get("DTEND")!.Value);
        Assert.Equal("TRANSPARENT", ev.Text("TRANSP"));

        var back = GraphConvert.EventToGraph(ev, Berlin);
        Assert.True((bool)back["isAllDay"]!);
        Assert.Equal("2026-12-24T00:00:00", (string?)back["start"]!["dateTime"]);
        Assert.Equal("2026-12-27T00:00:00", (string?)back["end"]!["dateTime"]);
        Assert.Equal("free", (string?)back["showAs"]);
        Assert.Null(back["recurrence"]);
    }

    [Theory]
    [InlineData("""{"pattern":{"type":"daily","interval":2},"range":{"type":"noEnd","startDate":"2026-10-05"}}""", "FREQ=DAILY;INTERVAL=2")]
    [InlineData("""{"pattern":{"type":"relativeMonthly","interval":1,"daysOfWeek":["friday"],"index":"last"},"range":{"type":"noEnd","startDate":"2026-10-30"}}""", "FREQ=MONTHLY;BYDAY=-1FR")]
    [InlineData("""{"pattern":{"type":"absoluteYearly","interval":1,"month":4,"dayOfMonth":12},"range":{"type":"endDate","startDate":"2026-04-12","endDate":"2030-04-12"}}""", "FREQ=YEARLY;BYMONTH=4;BYMONTHDAY=12;UNTIL=20300412T215959Z")]
    public void Wiederholung_hin_und_zurueck(string recurrence, string rrule)
    {
        Assert.Equal(rrule, GraphConvert.RRule(J(recurrence), Berlin, false));
        var start = DateTime.ParseExact(J(recurrence).GetProperty("range").GetProperty("startDate").GetString()!, "yyyy-MM-dd", null);
        var back = GraphConvert.Recurrence(rrule, start, Berlin)!;
        Assert.Equal(GraphConvert.RRule(J(back.ToJsonString()), Berlin, false), rrule);
    }

    // ---------------- Abholen ----------------

    private static (UserSpace Space, Collection Coll, GraphEngine Engine, FakeGraph Fake, List<TimeSpan> Waits) TaskSetup()
    {
        var space = NewSpace();
        var (engine, fake, waits) = Engine(space, Account(tasks: true));
        fake.Answer = (m, p, _) => (m, p) switch
        {
            ("GET", "/me/todo/lists") => (HttpStatusCode.OK, """{"value":[{"id":"L1","displayName":"Aufgaben","wellknownListName":"defaultList"},{"id":"L2","displayName":"Gekennzeichnete E-Mail","wellknownListName":"flaggedEmails"}]}"""),
            _ => (HttpStatusCode.NotFound, """{"error":{"code":"ErrorItemNotFound","message":"nicht da"}}"""),
        };
        engine.AssignAsync(default).GetAwaiter().GetResult();
        var link = Assert.Single(Graph.All(space.Db));
        return (space, PimStore.Find(space.Db, link.Collection)!, engine, fake, waits);
    }

    private static string TaskJson(string id, string etag, string title, string? modified = null) =>
        $$"""{"id":"{{id}}","@odata.etag":"{{etag}}","title":"{{title}}","status":"notStarted","importance":"normal","lastModifiedDateTime":"{{modified ?? "2026-10-20T10:00:00Z"}}"}""";

    [Fact]
    public async Task Zuordnung_legt_je_Liste_eine_Sammlung_an_ohne_gekennzeichnete_Mails()
    {
        var (space, coll, engine, _, _) = TaskSetup();
        Assert.Equal("Aufgaben", coll.Name);   // ohne Zusatz - kein anderer Kalender heisst so
        Assert.Equal("VTODO", coll.Components);
        Assert.Equal(Graph.Tasks, Graph.Of(space.Db, coll.Id)!.Kind);

        // Abschalten: lokale Kopie weg
        var off = Account(tasks: false);
        var (engine2, fake2, _) = Engine(space, off);
        await engine2.AssignAsync(default);
        Assert.Empty(Graph.All(space.Db));
        Assert.Null(PimStore.Find(space.Db, coll.Id));
        Assert.Empty(fake2.Requests);
    }

    [Fact]
    public async Task Delta_ueber_zwei_Seiten_dann_geloescht()
    {
        var (space, coll, engine, fake, _) = TaskSetup();
        var round = 0;
        fake.Answer = (m, p, _) => (m, p) switch
        {
            ("GET", "/me/todo/lists/L1/tasks/delta") => (HttpStatusCode.OK,
                $$"""{"value":[{{TaskJson("T1", "e1", "Eins")}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$skiptoken=S2"}"""),
            ("GET", "/me/todo/lists/L1/tasks/delta?$skiptoken=S2") => (HttpStatusCode.OK,
                $$"""{"value":[{{TaskJson("T2", "e1", "Zwei")}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""),
            ("GET", "/me/todo/lists/L1/tasks/delta?$deltatoken=D1") => (HttpStatusCode.OK, round++ == 0
                ? """{"value":[{"id":"T1","@removed":{"reason":"deleted"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D2"}"""
                : """{"value":[]}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        };
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Equal(new[] { "Eins", "Zwei" }, CalendarModel.Tasks(space, [coll.Id]).Select(t => t.Title).Order());
        Assert.EndsWith("D1", Graph.Of(space.Db, coll.Id)!.DeltaLink);
        Assert.Equal(0, Count(space.Db, "graph_outbox"));   // Abgeholtes geht nicht zurueck

        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Equal("Zwei", Assert.Single(CalendarModel.Tasks(space, [coll.Id])).Title);
        Assert.EndsWith("D2", Graph.Of(space.Db, coll.Id)!.DeltaLink);
        Assert.Equal(1, Count(space.Db, "graph_map"));
        Assert.Equal(0, Count(space.Db, "graph_outbox"));
    }

    [Fact]
    public async Task Verfallener_Delta_Stand_holt_von_vorn_und_raeumt_auf()
    {
        var (space, coll, engine, fake, _) = TaskSetup();
        var full = 0;
        fake.Answer = (m, p, _) => (m, p) switch
        {
            ("GET", "/me/todo/lists/L1/tasks/delta") => (HttpStatusCode.OK, full++ == 0
                ? $$"""{"value":[{{TaskJson("T1", "e1", "Eins")}},{{TaskJson("T2", "e1", "Zwei")}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""
                : $$"""{"value":[{{TaskJson("T2", "e1", "Zwei")}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D9"}"""),
            ("GET", "/me/todo/lists/L1/tasks/delta?$deltatoken=D1") => (HttpStatusCode.Gone, """{"error":{"code":"syncStateNotFound","message":"weg"}}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        };
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Equal(2, CalendarModel.Tasks(space, [coll.Id]).Count);
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Equal("Zwei", Assert.Single(CalendarModel.Tasks(space, [coll.Id])).Title);
        Assert.EndsWith("D9", Graph.Of(space.Db, coll.Id)!.DeltaLink);
    }

    [Fact]
    public async Task Bei_429_wird_nach_Retry_After_wiederholt()
    {
        var (space, coll, engine, fake, waits) = TaskSetup();
        var calls = 0;
        fake.Raw = (m, p, _) =>
        {
            if (p != "/me/todo/lists/L1/tasks/delta" || calls++ > 0) return null;
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"error":{"code":"TooManyRequests"}}""") };
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        };
        fake.Answer = (_, p, _) => p == "/me/todo/lists/L1/tasks/delta"
            ? (HttpStatusCode.OK, $$"""{"value":[{{TaskJson("T1", "e1", "Eins")}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}""")
            : (HttpStatusCode.NotFound, "{}");
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, waits);
        Assert.Single(CalendarModel.Tasks(space, [coll.Id]));
    }

    // ---------------- Zurueckschreiben ----------------

    [Fact]
    public async Task Rundlauf_lokal_aendern_PATCH_kein_Echo()
    {
        var (space, coll, engine, fake, _) = TaskSetup();
        var etag = "e1";
        var title = "Eins";
        fake.Answer = (m, p, body) => (m, p) switch
        {
            ("GET", "/me/todo/lists/L1/tasks/delta") => (HttpStatusCode.OK,
                $$"""{"value":[{{TaskJson("T1", etag, title)}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""),
            // Microsoft meldet die eigene Aenderung beim naechsten Delta noch einmal
            ("GET", "/me/todo/lists/L1/tasks/delta?$deltatoken=D1") => (HttpStatusCode.OK,
                $$"""{"value":[{{TaskJson("T1", etag, title)}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""),
            ("PATCH", "/me/todo/lists/L1/tasks/T1") => Patch(body),
            ("POST", "/me/todo/lists/L1/tasks") => (HttpStatusCode.Created, TaskJson("T9", "n1", (string)JsonNode.Parse(body)!["title"]!)),
            _ => (HttpStatusCode.NotFound, "{}"),
        };
        (HttpStatusCode, string) Patch(string body)
        {
            title = (string)JsonNode.Parse(body)!["title"]!;
            etag = "e2";
            return (HttpStatusCode.OK, TaskJson("T1", etag, title));
        }

        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        var task = Assert.Single(CalendarModel.Tasks(space, [coll.Id]));

        // Lokal geaendert (wie aus der Weboberflaeche) - landet in der Warteschlange
        CalendarModel.SaveTask(space, new TaskInput { List = coll.Id, Uri = task.Uri, Title = "Eins geändert" });
        Assert.Equal("put", Assert.Single(Graph.Outbox(space.Db, coll.Id)).Op);
        // Neu angelegt - ebenso
        CalendarModel.SaveTask(space, new TaskInput { List = coll.Id, Title = "Ganz neu" });
        Assert.Equal(2, Graph.Outbox(space.Db, coll.Id).Count);

        await engine.PushAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Empty(Graph.Outbox(space.Db, coll.Id));
        Assert.Equal("Eins geändert", title);
        Assert.Contains(fake.Requests, r => r.Method == "POST" && r.Body.Contains("Ganz neu"));
        Assert.Equal(2, Count(space.Db, "graph_map"));

        // Abholen danach aendert nichts mehr
        var before = SyncToken(space.Db, coll.Id);
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Equal(before, SyncToken(space.Db, coll.Id));
        Assert.Empty(Graph.Outbox(space.Db, coll.Id));
        Assert.Equal(1, fake.Requests.Count(r => r.Method == "PATCH"));
    }

    [Fact]
    public async Task Lokal_geloescht_geht_als_DELETE_und_nur_verbundene_Sammlungen_kommen_in_die_Warteschlange()
    {
        var (space, coll, engine, fake, _) = TaskSetup();
        fake.Answer = (m, p, _) => (m, p) switch
        {
            ("GET", "/me/todo/lists/L1/tasks/delta") => (HttpStatusCode.OK, $$"""{"value":[{{TaskJson("T1", "e1", "Eins")}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""),
            ("DELETE", "/me/todo/lists/L1/tasks/T1") => (HttpStatusCode.NoContent, ""),
            _ => (HttpStatusCode.NotFound, "{}"),
        };
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        var task = Assert.Single(CalendarModel.Tasks(space, [coll.Id]));

        // Eigener Kalender: keine Warteschlange
        var own = PimStore.Collections(space.Db, PimStore.Calendar).First(c => !Graph.IsLinked(space.Db, c.Id));
        CalendarModel.SaveTask(space, new TaskInput { List = own.Id, Title = "Privat" });
        Assert.Equal(0, Count(space.Db, "graph_outbox"));

        PimStore.Delete(space.Db, coll, task.Uri);
        Assert.Equal("delete", Assert.Single(Graph.Outbox(space.Db, coll.Id)).Op);
        await engine.PushAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Contains(fake.Requests, r => r is { Method: "DELETE", Path: "/me/todo/lists/L1/tasks/T1" });
        Assert.Equal(0, Count(space.Db, "graph_outbox"));
        Assert.Equal(0, Count(space.Db, "graph_map"));
    }

    [Fact]
    public async Task Fehler_bleiben_in_der_Warteschlange_und_zaehlen()
    {
        var (space, coll, engine, fake, _) = TaskSetup();
        fake.Answer = (_, _, _) => (HttpStatusCode.BadRequest, """{"error":{"code":"invalidRequest","message":"Titel zu lang"}}""");
        CalendarModel.SaveTask(space, new TaskInput { List = coll.Id, Title = "X" });
        for (int i = 0; i < 3; i++) await engine.PushAsync(Graph.Of(space.Db, coll.Id)!, default);
        var o = Assert.Single(Graph.Outbox(space.Db, coll.Id));
        Assert.Equal(3, o.Tries);
        Assert.Contains("Titel zu lang", o.Error);
        Assert.Equal((1, 1), Graph.Waiting(space.Db, coll.Id));
    }

    // ---------------- Konflikte ----------------

    [Theory]
    [InlineData("2026-10-20T10:00:00Z", "2099-01-01T00:00:00Z", "Von Microsoft")]   // Microsoft neuer
    [InlineData("2026-10-20T10:00:00Z", "2000-01-01T00:00:00Z", "Lokal")]           // lokal neuer
    public async Task Beide_geaendert_die_neuere_Aenderung_gewinnt(string first, string remoteModified, string winner)
    {
        var (space, coll, engine, fake, _) = TaskSetup();
        var round = 0;
        fake.Answer = (m, p, _) => (m, p) switch
        {
            ("GET", "/me/todo/lists/L1/tasks/delta") => (HttpStatusCode.OK, $$"""{"value":[{{TaskJson("T1", "e1", "Eins", first)}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""),
            ("GET", "/me/todo/lists/L1/tasks/delta?$deltatoken=D1") => (HttpStatusCode.OK, round++ == 0
                ? $$"""{"value":[{{TaskJson("T1", "e2", "Von Microsoft", remoteModified)}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""
                : """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""),
            ("PATCH", "/me/todo/lists/L1/tasks/T1") => (HttpStatusCode.OK, TaskJson("T1", "e3", "Lokal")),
            _ => (HttpStatusCode.NotFound, "{}"),
        };
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        var task = Assert.Single(CalendarModel.Tasks(space, [coll.Id]));
        CalendarModel.SaveTask(space, new TaskInput { List = coll.Id, Uri = task.Uri, Title = "Lokal" });

        await engine.RunAsync(pull: true, default);
        Assert.Equal(winner, Assert.Single(CalendarModel.Tasks(space, [coll.Id])).Title);
        Assert.Empty(Graph.Outbox(space.Db, coll.Id));
        Assert.Single(Graph.Of(space.Db, coll.Id)!.Conflicts);
        Assert.Equal(winner == "Lokal" ? 1 : 0, fake.Requests.Count(r => r.Method == "PATCH"));
    }

    [Fact]
    public async Task Bei_Microsoft_geloescht_lokal_geaendert_wird_neu_angelegt()
    {
        var (space, coll, engine, fake, _) = TaskSetup();
        var round = 0;
        fake.Answer = (m, p, _) => (m, p) switch
        {
            ("GET", "/me/todo/lists/L1/tasks/delta") => (HttpStatusCode.OK, $$"""{"value":[{{TaskJson("T1", "e1", "Eins")}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""),
            ("GET", "/me/todo/lists/L1/tasks/delta?$deltatoken=D1") => (HttpStatusCode.OK, round++ == 0
                ? """{"value":[{"id":"T1","@removed":{"reason":"deleted"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""
                : """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks/delta?$deltatoken=D1"}"""),
            ("POST", "/me/todo/lists/L1/tasks") => (HttpStatusCode.Created, TaskJson("T7", "n1", "Lokal")),
            _ => (HttpStatusCode.NotFound, "{}"),
        };
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        var task = Assert.Single(CalendarModel.Tasks(space, [coll.Id]));
        CalendarModel.SaveTask(space, new TaskInput { List = coll.Id, Uri = task.Uri, Title = "Lokal" });
        await engine.RunAsync(pull: true, default);
        Assert.Equal("Lokal", Assert.Single(CalendarModel.Tasks(space, [coll.Id])).Title);
        Assert.Equal("T7", Assert.Single(Graph.Map(space.Db, coll.Id)).RemoteId);
    }

    // ---------------- Kalender ----------------

    [Fact]
    public async Task Kalender_holt_nur_Geaendertes_und_erkennt_Absagen()
    {
        var space = NewSpace();
        var (engine, fake, _) = Engine(space, Account(calendars: true));
        var listed = "{\"value\":[" + SeriesMaster + "]}";
        fake.Answer = (m, p, _) =>
        {
            if (p == "/me/calendars?$select=id,name,hexColor,canEdit") return (HttpStatusCode.OK, """{"value":[{"id":"CAL1","name":"Kalender","hexColor":"#e3008c"}]}""");
            if (p.StartsWith("/me/calendars/CAL1/events?")) return (HttpStatusCode.OK, listed);
            if (p == "/me/events/E1") return (HttpStatusCode.OK, SeriesMaster);
            if (p.StartsWith("/me/events/E1/instances?"))
            {
                // Zehn Montage ab 5.10., der 12. verschoben (Ausnahme), der 19. fehlt (abgesagt)
                var items = Enumerable.Range(0, 10).Where(i => i != 2).Select(i =>
                {
                    var s = new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc).AddDays(7 * i);
                    if (s >= new DateTime(2026, 10, 25)) s = s.AddHours(1);   // Winterzeit: 09:00 Ortszeit = 08:00 UTC
                    return i == 1 ? SeriesException : $$"""{"id":"E1-{{i}}","type":"occurrence","originalStart":"{{s:yyyy-MM-ddTHH:mm:ssZ}}"}""";
                });
                return (HttpStatusCode.OK, "{\"value\":[" + string.Join(",", items) + "]}");
            }
            return (HttpStatusCode.NotFound, "{}");
        };
        await engine.RunAsync(pull: true, default);
        var coll = PimStore.Find(space.Db, Assert.Single(Graph.All(space.Db)).Collection)!;
        Assert.Equal("Kalender", coll.Name.Replace(" (Outlook)", ""));
        Assert.Equal("#e3008c", coll.Color);
        var obj = Assert.Single(PimStore.Objects(space.Db, coll.Id));
        var master = Vcal(obj.Data).Of("VEVENT").First();
        Assert.Equal("20261019T090000", master.Get("EXDATE")!.Value);
        Assert.Equal(2, Vcal(obj.Data).Of("VEVENT").Count());
        Assert.Equal(0, Count(space.Db, "graph_outbox"));

        // Unveraenderter changeKey: kein vollstaendiges Holen mehr
        var requests = fake.Requests.Count;
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Equal(requests + 1, fake.Requests.Count);

        // Bei Microsoft geloescht: lokal weg
        listed = """{"value":[]}""";
        await engine.PullAsync(Graph.Of(space.Db, coll.Id)!, default);
        Assert.Empty(PimStore.Objects(space.Db, coll.Id));
        Assert.Equal(0, Count(space.Db, "graph_outbox"));
    }

    [Fact]
    public async Task Termin_lokal_angelegt_geht_als_POST_in_den_Kalender()
    {
        var space = NewSpace();
        var (engine, fake, _) = Engine(space, Account(calendars: true));
        var posted = false;
        fake.Answer = (m, p, body) => (m, p) switch
        {
            ("GET", "/me/calendars?$select=id,name,hexColor,canEdit") => (HttpStatusCode.OK, """{"value":[{"id":"CAL1","name":"Kalender"}]}"""),
            ("GET", _) when p.StartsWith("/me/calendars/CAL1/events?") => (HttpStatusCode.OK, posted ? """{"value":[{"id":"N1","changeKey":"c1","type":"singleInstance"}]}""" : """{"value":[]}"""),
            ("POST", "/me/calendars/CAL1/events") => Post(),
            _ => (HttpStatusCode.NotFound, "{}"),
        };
        (HttpStatusCode, string) Post() { posted = true; return (HttpStatusCode.Created, """{"id":"N1","changeKey":"c1"}"""); }
        await engine.AssignAsync(default);
        var coll = PimStore.Find(space.Db, Assert.Single(Graph.All(space.Db)).Collection)!;
        CalendarModel.SaveEvent(space, new EventInput { Calendar = coll.Id, Title = "Zahnarzt", Start = "2026-11-03T08:00:00Z", End = "2026-11-03T09:00:00Z", Notify = false });
        await engine.RunAsync(pull: true, default);
        await engine.RunAsync(pull: true, default);

        var post = Assert.Single(fake.Requests, r => r.Method == "POST");
        var body = JsonNode.Parse(post.Body)!;
        Assert.Equal("Zahnarzt", (string?)body["subject"]);
        Assert.Equal("2026-11-03T09:00:00", (string?)body["start"]!["dateTime"]);   // 08:00 UTC = 09:00 Berlin
        Assert.Equal("W. Europe Standard Time", (string?)body["start"]!["timeZone"]);
        Assert.Equal("N1", Assert.Single(Graph.Map(space.Db, coll.Id)).RemoteId);
        // Die Liste danach zeigt denselben changeKey - nicht noch einmal holen
        Assert.DoesNotContain(fake.Requests, r => r.Path == "/me/events/N1");
        Assert.Empty(Graph.Outbox(space.Db, coll.Id));
    }
}
