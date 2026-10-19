using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;

namespace DmOffice.Tests;

public class DavPimTests
{
    private static UserSpace NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmpim");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        PimStore.EnsureDefaults(db);
        // Zeitzone: Vorgabe Europe/Berlin
        return new UserSpace(new UserRecord { Login = "t" }, db);
    }

    private static Collection Calendar(UserSpace s) => PimStore.Collections(s.Db, PimStore.Calendar).First();
    private static Collection Book(UserSpace s) => PimStore.Collections(s.Db, PimStore.AddressBook).First();

    // ---------------- Sommerzeit-Luecke (#10) ----------------

    [Fact]
    public void Zeit_in_der_Sommerzeit_Luecke_rueckt_vor()
    {
        var p = new Prop("DTSTART", "20270328T023000").With("TZID", "Europe/Berlin");
        var t = Ics.Time(p, TimeZoneInfo.Utc, out var allDay);
        Assert.False(allDay);
        // 02:30 gibt es nicht - gedeutet mit dem Versatz davor (+1), also 03:30 Sommerzeit
        Assert.Equal(new DateTime(2027, 3, 28, 1, 30, 0, DateTimeKind.Utc), t);
        Assert.Equal(DateTimeKind.Utc, t!.Value.Kind);
        // Gewoehnliche Zeiten wie bisher
        Assert.Equal(new DateTime(2027, 3, 28, 1, 30, 0, DateTimeKind.Utc), Ics.Time(new Prop("DTSTART", "20270328T033000").With("TZID", "Europe/Berlin"), TimeZoneInfo.Utc, out _));
        Assert.Equal(new DateTime(2027, 3, 28, 0, 30, 0, DateTimeKind.Utc), Ics.Time(new Prop("DTSTART", "20270328T013000").With("TZID", "Europe/Berlin"), TimeZoneInfo.Utc, out _));
    }

    [Fact]
    public void Termin_und_Aufgabe_in_der_Luecke_lassen_sich_speichern_und_lesen()
    {
        var space = NewSpace();
        var cal = Calendar(space);
        PimStore.Put(space.Db, cal, "e.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:e1\r\nDTSTART;TZID=Europe/Berlin:20270328T023000\r\nDTEND;TZID=Europe/Berlin:20270328T040000\r\nSUMMARY:Luecke\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", zone: space.Zone);
        PimStore.Put(space.Db, cal, "t.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:t1\r\nDUE;TZID=Europe/Berlin:20270328T023000\r\nSUMMARY:Aufgabe\r\nEND:VTODO\r\nEND:VCALENDAR\r\n", zone: space.Zone);
        PimStore.Put(space.Db, cal, "t2.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:t2\r\nSUMMARY:Gut\r\nEND:VTODO\r\nEND:VCALENDAR\r\n", zone: space.Zone);

        var tasks = CalendarModel.Tasks(space, new[] { cal.Id });
        Assert.Contains(tasks, t => t.Title == "Aufgabe" && t.Due == "2027-03-28T01:30:00Z");
        Assert.Contains(tasks, t => t.Title == "Gut");
        var events = CalendarModel.Events(space, new DateTime(2027, 3, 27, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 3, 29, 0, 0, 0, DateTimeKind.Utc), new[] { cal.Id });
        Assert.Contains(events, e => e.Title == "Luecke" && e.Start == "2027-03-28T01:30:00Z");
    }

    // ---------------- FileStore MOVE/COPY (#7) ----------------

    [Fact]
    public void Ordner_auf_Vorfahren_verschieben_wird_abgewiesen()
    {
        var space = NewSpace();
        var db = space.Db;
        FileStore.EnsureDir(db, "a/b");
        FileStore.Put(db, "a/b/c.txt", Encoding.UTF8.GetBytes("hallo"), "text/plain");

        Assert.Throws<DavConflictException>(() => FileStore.MoveOrCopy(db, "a/b", "a", overwrite: true, copy: false));
        Assert.Throws<DavConflictException>(() => FileStore.MoveOrCopy(db, "a/b", "a", overwrite: true, copy: true));
        Assert.Throws<DavConflictException>(() => FileStore.MoveOrCopy(db, "a/b/c.txt", "a", overwrite: true, copy: false));
        Assert.Throws<DavForbiddenException>(() => FileStore.MoveOrCopy(db, "a/b", "a/b", overwrite: true, copy: false));
        Assert.Throws<ArgumentException>(() => FileStore.MoveOrCopy(db, "a", "a/b/x", overwrite: true, copy: false));

        // Nichts verloren
        Assert.Equal("hallo", Encoding.UTF8.GetString(FileStore.Data(db, "a/b/c.txt")!));
        Assert.NotNull(FileStore.Get(db, "a"));

        // Gewoehnliches Verschieben geht weiter
        Assert.True(FileStore.MoveOrCopy(db, "a/b", "d", overwrite: false, copy: false));
        Assert.NotNull(FileStore.Get(db, "d/c.txt"));
        Assert.Null(FileStore.Get(db, "a/b"));
    }

    // ---------------- PimStore.Move (#4) ----------------

    [Fact]
    public void Verschieben_scheitert_ohne_Verlust()
    {
        var space = NewSpace();
        var db = space.Db;
        var a = Calendar(space);
        var b = PimStore.CreateCollection(db, PimStore.Calendar, "zwei", "Zwei", null, "VEVENT,VTODO");
        string Ev(string uid) => $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART:20270101T100000Z\r\nSUMMARY:x\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        PimStore.Put(db, a, "1.ics", Ev("same"));
        PimStore.Put(db, b, "other.ics", Ev("same"));

        // Ziel hat dieselbe UID schon: Schreiben scheitert, die Quelle bleibt
        Assert.Throws<ArgumentException>(() => PimStore.Move(db, a, "1.ics", b, "1.ics"));
        Assert.NotNull(PimStore.Object(db, a.Id, "1.ics"));

        // Umbenennen in derselben Sammlung (gleiche UID) geht
        PimStore.Move(db, a, "1.ics", a, "2.ics");
        Assert.Null(PimStore.Object(db, a.Id, "1.ics"));
        Assert.Equal("same", PimStore.Object(db, a.Id, "2.ics")!.Uid);
    }

    // ---------------- Serie verschieben (#8), Erinnerung (#11), ETag (#6) ----------------

    [Fact]
    public void Serie_verschieben_zieht_Ausnahmen_mit()
    {
        var space = NewSpace();
        var cal = Calendar(space);
        // Montags 10:00 Berliner Zeit
        var id = CalendarModel.SaveEvent(space, new EventInput
        {
            Calendar = cal.Id, Title = "Serie", Start = "2027-01-04T09:00:00Z", End = "2027-01-04T10:00:00Z", RRule = "FREQ=WEEKLY;COUNT=6",
        });
        var uri = id.Split('/', 2)[1];
        CalendarModel.DeleteEvent(space, cal.Id, uri, "2027-01-11T09:00:00Z");
        CalendarModel.SaveEvent(space, new EventInput
        {
            Calendar = cal.Id, Uri = uri, Title = "Anders", Start = "2027-01-18T09:00:00Z", End = "2027-01-18T10:00:00Z",
            Scope = "instance", Instance = "2027-01-18T09:00:00Z",
        });

        // Ganze Serie eine Stunde spaeter
        CalendarModel.SaveEvent(space, new EventInput
        {
            Calendar = cal.Id, Uri = uri, Title = "Serie", Start = "2027-01-04T10:00:00Z", End = "2027-01-04T11:00:00Z",
        });

        var vcal = Ics.Parse(PimStore.Object(space.Db, cal.Id, uri)!.Data).First(r => r.Name == "VCALENDAR");
        var master = vcal.Of("VEVENT").First(e => e.Get("RECURRENCE-ID") == null);
        Assert.Equal("20270111T110000", master.Get("EXDATE")!.Value);
        Assert.Equal(space.Zone.Id, master.Get("EXDATE")!.Param("TZID"));
        var over = vcal.Of("VEVENT").Single(e => e.Get("RECURRENCE-ID") != null);
        Assert.Equal("20270118T110000", over.Get("RECURRENCE-ID")!.Value);

        // Der abgesagte Termin bleibt weg
        var events = CalendarModel.Events(space, new DateTime(2027, 1, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 13, 0, 0, 0, DateTimeKind.Utc), new[] { cal.Id });
        Assert.DoesNotContain(events, e => e.Start.StartsWith("2027-01-11"));
    }

    [Fact]
    public void Erinnerung_aendert_nur_den_einen_Alarm()
    {
        var space = NewSpace();
        var cal = Calendar(space);
        PimStore.Put(space.Db, cal, "a.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:a1\r\nDTSTART:20270105T100000Z\r\nDTEND:20270105T110000Z\r\nSUMMARY:A\r\n" +
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nDESCRIPTION:A\r\nEND:VALARM\r\n" +
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT1H\r\nDESCRIPTION:B\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", zone: space.Zone);
        CalendarModel.SaveEvent(space, new EventInput
        {
            Calendar = cal.Id, Uri = "a.ics", Title = "A", Start = "2027-01-05T10:00:00Z", End = "2027-01-05T11:00:00Z", Reminder = 30,
        });
        var e = Ics.Parse(PimStore.Object(space.Db, cal.Id, "a.ics")!.Data).First().Of("VEVENT").First();
        Assert.Equal(new[] { "-PT30M", "-PT1H" }, e.Of("VALARM").Select(a => a.Text("TRIGGER")).ToArray());
    }

    [Fact]
    public void Veralteter_ETag_wird_abgelehnt()
    {
        var space = NewSpace();
        var cal = Calendar(space);
        var id = CalendarModel.SaveEvent(space, new EventInput { Calendar = cal.Id, Title = "T", Start = "2027-01-05T10:00:00Z", End = "2027-01-05T11:00:00Z" });
        var uri = id.Split('/', 2)[1];
        var loaded = PimStore.Object(space.Db, cal.Id, uri)!.Etag;
        // Inzwischen vom Handy geaendert
        CalendarModel.SaveEvent(space, new EventInput { Calendar = cal.Id, Uri = uri, Title = "Handy", Start = "2027-01-05T10:00:00Z", End = "2027-01-05T11:00:00Z" });
        Assert.Throws<PreconditionFailedException>(() => CalendarModel.SaveEvent(space, new EventInput
        {
            Calendar = cal.Id, Uri = uri, Etag = loaded, Title = "Web", Start = "2027-01-05T10:00:00Z", End = "2027-01-05T11:00:00Z",
        }));
        Assert.Contains("SUMMARY:Handy", PimStore.Object(space.Db, cal.Id, uri)!.Data);
    }

    // ---------------- vCard (#9) ----------------

    private const string Card =
        "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:k1\r\nFN:Max Muster\r\nN:Muster;Max;;;\r\n" +
        "TEL;TYPE=CELL,PREF:+49 170 1\r\nTEL;TYPE=WORK,VOICE,FAX:030 1\r\n" +
        "EMAIL;TYPE=INTERNET,WORK;TYPE=PREF:max@acme.de\r\n" +
        "ADR;TYPE=HOME:Postfach 12;Hinterhaus;Hauptstr. 1;Berlin;;10115;DE\r\n" +
        "BDAY:--0501\r\nEND:VCARD\r\n";

    private static Component Saved(UserSpace space, Collection book) =>
        Ics.Parse(PimStore.Object(space.Db, book.Id, "k1.vcf")!.Data).First(r => r.Name == "VCARD");

    private static ContactDto Load(UserSpace space, Collection book) =>
        ContactModel.Read(PimStore.Object(space.Db, book.Id, "k1.vcf")!, withPhoto: true)!;

    [Fact]
    public void Kontakt_speichern_behaelt_Unveraendertes()
    {
        var space = NewSpace();
        var book = Book(space);
        PimStore.Put(space.Db, book, "k1.vcf", Card);

        var dto = Load(space, book);
        Assert.Null(dto.Birthday);   // ohne Jahr: nicht darstellbar
        dto.Note = "Neu";
        ContactModel.Save(space, dto);

        var card = Saved(space, book);
        Assert.Equal("Neu", card.Text("NOTE"));
        Assert.Equal("--0501", card.Text("BDAY"));
        var tels = card.All("TEL").ToList();
        Assert.Equal(new[] { "cell", "pref" }, tels[0].Types());
        Assert.Equal(new[] { "work", "voice", "fax" }, tels[1].Types());
        Assert.Contains("pref", card.Get("EMAIL")!.Types());
        var adr = Ics.Structured(card.Get("ADR")!.Value);
        Assert.Equal(new[] { "Postfach 12", "Hinterhaus", "Hauptstr. 1", "Berlin", "", "10115", "DE" }, adr);
    }

    [Fact]
    public void Kontakt_aendern_behaelt_Postfach_und_Parameter()
    {
        var space = NewSpace();
        var book = Book(space);
        PimStore.Put(space.Db, book, "k1.vcf", Card);

        var dto = Load(space, book);
        dto.Addresses[0].Street = "Neue Str. 2";
        dto.Phones[0] = new TypedValue("cell", "+49 170 2");
        ContactModel.Save(space, dto);

        var card = Saved(space, book);
        var adr = Ics.Structured(card.Get("ADR")!.Value);
        Assert.Equal(new[] { "Postfach 12", "Hinterhaus", "Neue Str. 2", "Berlin", "", "10115", "DE" }, adr);
        var tel = card.All("TEL").First();
        Assert.Equal("+49 170 2", tel.Text);
        Assert.Equal(new[] { "cell", "pref" }, tel.Types());
        Assert.Equal("--0501", card.Text("BDAY"));

        // Geburtstag mit Jahr laesst sich weiter setzen und loeschen
        dto = Load(space, book);
        dto.Birthday = "1980-05-01";
        ContactModel.Save(space, dto);
        Assert.Equal("1980-05-01", Load(space, book).Birthday);
        dto = Load(space, book);
        dto.Birthday = null;
        ContactModel.Save(space, dto);
        Assert.Null(Saved(space, book).Get("BDAY"));
    }

    [Fact]
    public void Kontakt_mit_veraltetem_ETag_wird_abgelehnt()
    {
        var space = NewSpace();
        var book = Book(space);
        PimStore.Put(space.Db, book, "k1.vcf", Card);
        var dto = Load(space, book);
        PimStore.Put(space.Db, book, "k1.vcf", Card.Replace("FN:Max Muster", "FN:Max vom Handy"));
        dto.Note = "Web";
        Assert.Throws<PreconditionFailedException>(() => ContactModel.Save(space, dto));
        Assert.Equal("Max vom Handy", Saved(space, book).Text("FN"));
    }
}
