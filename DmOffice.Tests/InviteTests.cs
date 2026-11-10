using DmOffice.Core;
using DmOffice.Mail;
using DmOffice.Pim;
using MimeKit;

namespace DmOffice.Tests;

public class InviteTests
{
    private static UserSpace NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dminvite");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        space.UpdateSettings(s => s.MailAccounts.Add(new MailAccount { Name = "Privat", Email = "alex@beispiel.de", DisplayName = "Alex Berger" }));
        PimStore.EnsureDefaults(db);
        return space;
    }

    private static MimeMessage Invitation(string method = "REQUEST", int sequence = 0, string start = "20301015T100000", string summary = "Projekt-Review")
    {
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Test//Test//DE", "METHOD:" + method,
            "BEGIN:VEVENT", "UID:review-4711@firma.example", "SEQUENCE:" + sequence, "DTSTAMP:20301001T080000Z",
            "DTSTART;TZID=Europe/Berlin:" + start, "DTEND;TZID=Europe/Berlin:" + start[..9] + "113000",
            "SUMMARY:" + summary, "LOCATION:Raum 2.14",
            "ORGANIZER;CN=Laura Neumann:mailto:laura.neumann@firma.example",
            "ATTENDEE;CN=Alex Berger;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:alex@beispiel.de",
            "END:VEVENT", "END:VCALENDAR", "");
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Laura Neumann", "laura.neumann@firma.example"));
        m.To.Add(new MailboxAddress("Alex Berger", "alex@beispiel.de"));
        m.Subject = "Einladung: " + summary;
        var cal = new TextPart("calendar") { Text = ics };
        cal.ContentType.Parameters.Add("method", method);
        m.Body = new MultipartAlternative { new TextPart("plain") { Text = "Einladung" }, cal };
        return m;
    }

    [Fact]
    public void Einladung_wird_erkannt()
    {
        var dto = Invites.Read(NewSpace(), Invitation())!;
        Assert.Equal("REQUEST", dto.Method);
        Assert.Equal("Projekt-Review", dto.Title);
        Assert.Equal("Raum 2.14", dto.Location);
        Assert.Equal("laura.neumann@firma.example", dto.OrganizerEmail);
        Assert.Equal("NEEDS-ACTION", dto.MyStatus);
        Assert.Null(dto.CalendarId);
        Assert.Equal(new DateTime(2030, 10, 15, 8, 0, 0, DateTimeKind.Utc), dto.Start); // 10:00 MESZ
    }

    [Fact]
    public async Task Zusagen_traegt_ein_Absagen_entfernt()
    {
        var space = NewSpace();
        var account = space.Settings.MailAccounts[0];
        Assert.False(await Invites.RespondAsync(space, account, Invitation(), "accept", send: false, default));
        var dto = Invites.Read(space, Invitation())!;
        Assert.Equal("ACCEPTED", dto.MyStatus);
        Assert.Equal(Invites.DefaultCalendar(space).Id, dto.CalendarId);
        var stored = PimStore.ByUid(space.Db, dto.CalendarId!.Value, "review-4711@firma.example")!;
        Assert.DoesNotContain("METHOD", stored.Data);
        Assert.DoesNotContain("RSVP", stored.Data);

        // Aktualisierung mit neuer Zeit ersetzt den Termin, statt einen zweiten anzulegen
        await Invites.RespondAsync(space, account, Invitation(sequence: 1, start: "20301016T140000"), "tentative", false, default);
        Assert.Single(PimStore.Objects(space.Db, dto.CalendarId.Value));
        Assert.Equal("TENTATIVE", Invites.Read(space, Invitation(sequence: 1, start: "20301016T140000"))!.MyStatus);
        Assert.True(Invites.Read(space, Invitation())!.Outdated);

        // Absage des Organisators: entfernen
        await Invites.RespondAsync(space, account, Invitation("CANCEL", 2), "remove", false, default);
        Assert.Empty(PimStore.Objects(space.Db, dto.CalendarId.Value));
    }

    [Fact]
    public async Task Konflikt_wird_gemeldet()
    {
        var space = NewSpace();
        var cal = Invites.DefaultCalendar(space);
        PimStore.Put(space.Db, cal, "x.ics", string.Join("\r\n", "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//T//T//DE", "BEGIN:VEVENT",
            "UID:other@x", "DTSTAMP:20301001T080000Z", "DTSTART:20301015T083000Z", "DTEND:20301015T090000Z", "SUMMARY:Zahnarzt",
            "END:VEVENT", "END:VCALENDAR", ""), zone: space.Zone);
        Assert.Equal(new[] { "Zahnarzt" }, Invites.Read(space, Invitation())!.Conflicts);
        await Task.CompletedTask;
    }

    [Fact]
    public void Serien_nur_bei_echter_Ueberschneidung()
    {
        // Einladung: Dienstag, 15.10.2030, 10:00-11:30
        var space = NewSpace();
        var cal = Invites.DefaultCalendar(space);
        void Serie(string uid, string summary, string start, params string[] extra) =>
            PimStore.Put(space.Db, cal, uid + ".ics", string.Join("\r\n", new[] { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//T//T//DE", "BEGIN:VEVENT",
                "UID:" + uid, "DTSTAMP:20300101T080000Z", "DTSTART;TZID=Europe/Berlin:" + start, "DTEND;TZID=Europe/Berlin:" + start[..9] + "113000",
                "SUMMARY:" + summary }.Concat(extra).Concat(new[] { "END:VEVENT", "END:VCALENDAR", "" })), zone: space.Zone);

        Serie("mo", "Montagsrunde", "20300902T100000", "RRULE:FREQ=WEEKLY;BYDAY=MO");
        Serie("di-frei", "Jour fixe (ausgefallen)", "20300903T100000", "RRULE:FREQ=WEEKLY;BYDAY=TU", "EXDATE;TZID=Europe/Berlin:20301015T100000");
        Assert.Empty(Invites.Read(space, Invitation())!.Conflicts);

        Serie("di", "Teamrunde", "20300903T103000", "RRULE:FREQ=WEEKLY;BYDAY=TU");
        Assert.Equal(new[] { "Teamrunde" }, Invites.Read(space, Invitation())!.Conflicts);
    }
}
