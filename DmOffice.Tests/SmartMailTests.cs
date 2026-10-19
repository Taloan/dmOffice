using DmOffice.Core;
using DmOffice.Mail;
using DmOffice.Pim;
using MimeKit;

namespace DmOffice.Tests;

public class SmartMailTests
{
    private static readonly TimeZoneInfo Berlin = DmOffice.Dav.Ics.Zone("Europe/Berlin")!;
    // Mittwoch, 21.10.2026, 9:00 Berlin
    private static readonly DateTime Now = new(2026, 10, 21, 7, 0, 0, DateTimeKind.Utc);

    private static MimeMessage Mail(string from, string fromName, string subject, string? html = null, string? text = null, DateTime? date = null)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(fromName, from));
        m.To.Add(new MailboxAddress("Alex", "alex@beispiel.de"));
        m.Subject = subject;
        m.Date = date ?? Now;
        var b = new BodyBuilder { HtmlBody = html, TextBody = text };
        m.Body = b.ToMessageBody();
        return m;
    }

    [Fact]
    public void Schema_Paket_Flug_Hotel()
    {
        var html = """
            <html><head><script type="application/ld+json">
            [{"@context":"http://schema.org","@type":"ParcelDelivery","expectedArrivalFrom":"2026-10-23T10:00:00+02:00","expectedArrivalUntil":"2026-10-23T14:00:00+02:00",
              "carrier":{"@type":"Organization","name":"DHL"},"trackingNumber":"JJD000390012345678","partOfOrder":{"@type":"Order","orderNumber":"302-1","merchant":{"@type":"Organization","name":"Elektro Sommer"}}},
             {"@context":"http://schema.org","@type":"FlightReservation","reservationNumber":"XB12CD","reservationFor":{"@type":"Flight","flightNumber":"123","airline":{"@type":"Airline","name":"Lufthansa","iataCode":"LH"},
              "departureAirport":{"@type":"Airport","name":"Frankfurt","iataCode":"FRA"},"departureTime":"2026-11-05T08:30:00+01:00","arrivalAirport":{"@type":"Airport","name":"Berlin","iataCode":"BER"},"arrivalTime":"2026-11-05T09:40:00+01:00"}},
             {"@context":"http://schema.org","@type":"LodgingReservation","reservationNumber":"H-77","reservationFor":{"@type":"LodgingBusiness","name":"Hotel Am See","address":{"streetAddress":"Seeweg 1","addressLocality":"Berlin"}},
              "checkinDate":"2026-11-05","checkoutDate":"2026-11-07"}]
            </script></head><body>Ihre Bestellung</body></html>
            """;
        var items = SmartMail.Extract(Mail("shop@sommer.example", "Shop", "Bestellung", html), Berlin, Now);
        Assert.Equal(new[] { "parcel", "flight", "hotel" }, items.Select(i => i.Kind));
        var p = items[0];
        Assert.Equal("📦 Paket von Elektro Sommer", p.Title);
        Assert.False(p.AllDay);
        Assert.Equal(new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc), p.Start);
        Assert.Equal("✈ Flug LH123 FRA → BER", items[1].Title);
        Assert.True(items[2].AllDay);
        Assert.Equal(new DateTime(2026, 11, 7), items[2].End);
    }

    [Fact]
    public void DHL_mit_Zeitfenster_heute()
    {
        var it = SmartMail.Extract(Mail("noreply@dhl.de", "DHL Paket", "Ihre Sendung kommt heute",
            text: "Hallo Alex, Ihre Sendung 00340434161234567890 kommt heute zwischen 10:30 und 13:30 Uhr."), Berlin, Now).Single();
        Assert.False(it.AllDay);
        Assert.Equal(new DateTime(2026, 10, 21, 8, 30, 0, DateTimeKind.Utc), it.Start);
        Assert.Equal(new DateTime(2026, 10, 21, 11, 30, 0, DateTimeKind.Utc), it.End);
        Assert.Equal("parcel:00340434161234567890", it.Key);
    }

    [Fact]
    public void Amazon_ueber_Alias_mit_Datum()
    {
        // Weitergeleitet ueber einen Wegwerf-Alias: Absenderadresse fremd, "DHL"/"Amazon" in Name und Betreff
        var it = SmartMail.Extract(Mail("amazon.de@alias.example", "DHL Zustell-Update - dhl.de", "Ihre Amazon Sendung kommt am Freitag",
            text: "Voraussichtliche Zustellung: Freitag, 23. Oktober"), Berlin, Now).Single();
        Assert.True(it.AllDay);
        Assert.Equal(new DateTime(2026, 10, 23), it.Start.Date);
        Assert.Contains("Amazon", it.Title);
    }

    [Fact]
    public void Zugestellt_und_normale_Mails_ignorieren()
    {
        Assert.Empty(SmartMail.Extract(Mail("noreply@dhl.de", "DHL", "Ihre Sendung wurde zugestellt", text: "Zugestellt am 20.10."), Berlin, Now));
        Assert.Empty(SmartMail.Extract(Mail("jana@example.org", "Jana", "Lieferung Getränke am Samstag", text: "Kommt morgen um 10"), Berlin, Now));
    }

    [Fact]
    public void Eintragen_ersetzt_gleiche_Sendung()
    {
        var dir = Directory.CreateTempSubdirectory("dmsmart");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        var a = new SmartItem("parcel", "parcel:123", "📦 Paket (DHL)", new DateTime(2026, 10, 22), new DateTime(2026, 10, 23), true, null, null);
        var cal = SmartMail.Upsert(space, a, null);
        SmartMail.Upsert(space, a with { Start = new DateTime(2026, 10, 24), End = new DateTime(2026, 10, 25) }, null);
        var objs = PimStore.Objects(db, cal);
        Assert.Single(objs);
        Assert.Contains("20261024", objs[0].Data);
        Assert.True(SmartMail.InCalendar(db, a));
    }

    [Fact]
    public void Alter_Kalendername_wird_umbenannt()
    {
        var dir = Directory.CreateTempSubdirectory("dmsmartname");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var cal = SmartMail.CalendarFor(db);
        Assert.Equal("E-Mail-Ankündigungen", cal.Name);
        PimStore.UpdateCollection(db, cal.Id, "Lieferungen & Reisen", null, null);
        Assert.Equal("E-Mail-Ankündigungen", SmartMail.CalendarFor(db).Name);
        // Selbst gewaehlte Namen bleiben
        PimStore.UpdateCollection(db, cal.Id, "Pakete", null, null);
        Assert.Equal("Pakete", SmartMail.CalendarFor(db).Name);
    }
}
