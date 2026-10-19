package at.dimes.dmoffice

import at.dimes.dmoffice.data.Items
import at.dimes.dmoffice.dav.DavXml
import at.dimes.dmoffice.dav.Ns
import at.dimes.dmoffice.ical.Ical
import at.dimes.dmoffice.ical.RRule
import at.dimes.dmoffice.ical.Recurrence
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.StringReader
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneId

class IcalTest {
    private val zone = ZoneId.of("Europe/Vienna")

    @Test
    fun parsesAndRoundTrips() {
        val ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:a\r\nSUMMARY:Ein sehr langer Titel mit Umlauten äöü und noch mehr Text damit die Zeile ganz sicher gefaltet werden muss\r\nDTSTART;TZID=Europe/Vienna:20261019T090000\r\nDTEND;TZID=Europe/Vienna:20261019T100000\r\nX-FOO;BAR=\"a:b\":wert\\, mit Komma\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"
        val cal = Ical.parse(ics).single()
        val ev = cal.of("VEVENT").single()
        assertEquals("wert, mit Komma", ev.text("X-FOO"))
        assertEquals("a:b", ev.get("X-FOO")!!.param("BAR"))
        val again = Ical.parse(cal.toString()).single().of("VEVENT").single()
        assertEquals(ev.text("SUMMARY"), again.text("SUMMARY"))
        assertEquals("wert, mit Komma", again.text("X-FOO"))
        val t = Ical.time(ev.get("DTSTART"), zone)!!
        assertEquals(LocalDateTime.of(2026, 10, 19, 9, 0).atZone(zone).toInstant(), t.instant)
    }

    @Test
    fun weeklyRuleWithExdate() {
        val ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:w\r\nSUMMARY:Jour fixe\r\nDTSTART;TZID=Europe/Vienna:20261013T140000\r\nDTEND;TZID=Europe/Vienna:20261013T143000\r\nRRULE:FREQ=WEEKLY;BYDAY=TU,TH\r\nEXDATE;TZID=Europe/Vienna:20261022T140000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"
        val ev = Ical.parse(ics).single().of("VEVENT").single()
        val from = LocalDate.of(2026, 10, 18).atStartOfDay(zone).toInstant()
        val to = LocalDate.of(2026, 10, 31).atStartOfDay(zone).toInstant()
        val days = Recurrence.expand(ev, zone, from, to).map { it.atZone(zone).toLocalDate() }
        assertEquals(listOf(LocalDate.of(2026, 10, 20), LocalDate.of(2026, 10, 27), LocalDate.of(2026, 10, 29)), days)
    }

    @Test
    fun monthlyByDayAndCount() {
        val r = RRule("FREQ=MONTHLY;BYDAY=2MO;COUNT=3")
        val list = r.occurrences(LocalDateTime.of(2026, 1, 12, 10, 0), zone, Instant.EPOCH, Instant.parse("2027-01-01T00:00:00Z"))
        assertEquals(listOf(LocalDate.of(2026, 1, 12), LocalDate.of(2026, 2, 9), LocalDate.of(2026, 3, 9)), list.map { it.toLocalDate() })
    }

    @Test
    fun yearlyAllDay() {
        val ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:y\r\nDTSTART;VALUE=DATE:20201020\r\nDTEND;VALUE=DATE:20201021\r\nRRULE:FREQ=YEARLY\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"
        val ev = Ical.parse(ics).single().of("VEVENT").single()
        val from = LocalDate.of(2026, 10, 1).atStartOfDay(zone).toInstant()
        val to = LocalDate.of(2026, 11, 1).atStartOfDay(zone).toInstant()
        assertEquals(listOf(LocalDate.of(2026, 10, 20)), Recurrence.expand(ev, zone, from, to).map { it.atZone(zone).toLocalDate() })
    }

    @Test
    fun taskFieldsAndToggle() {
        val ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:t\r\nSUMMARY:Angebot\r\nDUE;VALUE=DATE:20261018\r\nPRIORITY:1\r\nBEGIN:VALARM\r\nTRIGGER:-P1D\r\nACTION:DISPLAY\r\nEND:VALARM\r\nEND:VTODO\r\nEND:VCALENDAR\r\n"
        val e = Items.fromIcs("/x/t.ics", "/x/", "\"1\"", ics, zone)!!
        assertEquals(1, e.kind)
        assertTrue(e.allDay)
        assertEquals(1, e.priority)
        assertEquals(86400L, e.reminderSeconds)
        assertEquals(LocalDate.of(2026, 10, 18).atStartOfDay(zone).toInstant().toEpochMilli(), e.due)
        val done = Items.toggleCompleted(ics, true)
        val e2 = Items.fromIcs("/x/t.ics", "/x/", null, done, zone)!!
        assertTrue(e2.completed)
        assertTrue(done.contains("PERCENT-COMPLETE:100"))
        val undone = Items.fromIcs("/x/t.ics", "/x/", null, Items.toggleCompleted(done, false), zone)!!
        assertTrue(!undone.completed)
    }

    @Test
    fun durations() {
        assertEquals(-900L, Ical.durationSeconds("-PT15M"))
        assertEquals(86400L, Ical.durationSeconds("P1D"))
        assertEquals("-PT15M", Ical.duration(-900))
        assertEquals("P1D", Ical.duration(86400))
        assertEquals("PT0M", Ical.duration(0))
    }

    @Test
    fun multistatusIsNamespaceAware() {
        // Praefixe absichtlich ungewoehnlich: Namespace zaehlt, nicht das Praefix
        val xml = """<?xml version="1.0"?>
<A:multistatus xmlns:A="DAV:" xmlns:B="urn:ietf:params:xml:ns:caldav" xmlns:x1="http://apple.com/ns/ical/">
<A:response><A:href>/dav/calendars/u/cal/</A:href><A:propstat><A:prop>
<A:displayname>Pers&#246;nlich</A:displayname><A:resourcetype><A:collection/><B:calendar/></A:resourcetype>
<B:supported-calendar-component-set><B:comp name="VEVENT"/><B:comp name="VTODO"/></B:supported-calendar-component-set>
<x1:calendar-color>#FF0000</x1:calendar-color><A:sync-token>http://sabre.io/ns/sync/7</A:sync-token>
</A:prop><A:status>HTTP/1.1 200 OK</A:status></A:propstat></A:response>
<A:response><A:href>/dav/calendars/u/cal/gone.ics</A:href><A:status>HTTP/1.1 404 Not Found</A:status></A:response>
<A:response><A:href>/dav/calendars/u/cal/a.ics</A:href><A:propstat><A:prop><A:getetag>"abc"</A:getetag>
<B:calendar-data>BEGIN:VCALENDAR
END:VCALENDAR</B:calendar-data></A:prop><A:status>HTTP/1.1 200 OK</A:status></A:propstat></A:response>
<A:sync-token>http://sabre.io/ns/sync/8</A:sync-token>
</A:multistatus>"""
        val ms = DavXml.parseMultistatus(StringReader(xml))
        assertEquals("http://sabre.io/ns/sync/8", ms.syncToken)
        assertEquals(3, ms.responses.size)
        val cal = ms.responses[0]
        assertEquals("Persönlich", cal.text(Ns.DAV, "displayname"))
        assertTrue(cal.prop(Ns.DAV, "resourcetype")!!.children.any { it.first == Ns.CALDAV && it.second == "calendar" })
        assertEquals(listOf("VEVENT", "VTODO"), cal.prop(Ns.CALDAV, "supported-calendar-component-set")!!.children.map { it.third["name"] })
        assertEquals("#FF0000", cal.text(Ns.ICAL, "calendar-color"))
        assertEquals(404, ms.responses[1].status)
        assertNull(ms.responses[1].etag)
        assertEquals("\"abc\"", ms.responses[2].etag)
        assertNotNull(ms.responses[2].text(Ns.CALDAV, "calendar-data"))
        assertTrue(ms.responses[2].text(Ns.CALDAV, "calendar-data")!!.startsWith("BEGIN:VCALENDAR"))
    }
}
