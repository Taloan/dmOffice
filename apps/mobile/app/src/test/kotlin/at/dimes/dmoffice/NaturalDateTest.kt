package at.dimes.dmoffice

import at.dimes.dmoffice.data.NaturalDate
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime

class NaturalDateTest {
    // Sonntag, 18.10.2026, 10:00
    private val now = LocalDateTime.of(2026, 10, 18, 10, 0)
    private fun p(s: String) = NaturalDate.parse(s, now)

    @Test fun morgenNachmittag() {
        val r = p("Zahnarzt anrufen morgen Nachmittag")
        assertEquals("Zahnarzt anrufen", r.title)
        assertEquals(LocalDate.of(2026, 10, 19), r.date)
        assertEquals(LocalTime.of(14, 0), r.time)
    }

    @Test fun naechstenMittwochViertelNach3() {
        val r = p("Bericht abgeben nächsten Mittwoch Viertel nach 3")
        assertEquals("Bericht abgeben", r.title)
        assertEquals(LocalDate.of(2026, 10, 21), r.date)
        assertEquals(LocalTime.of(15, 15), r.time)
    }

    @Test fun halbUndDreiviertel() {
        assertEquals(LocalTime.of(15, 30), p("Treffen um halb 4").time)
        assertEquals(LocalTime.of(15, 45), p("Treffen dreiviertel 4").time)
        assertEquals(LocalTime.of(16, 45), p("Treffen viertel vor 5").time)
        assertEquals(LocalTime.of(9, 30), p("Treffen morgens um halb 10").time)
    }

    @Test fun explizitesDatumMitUhr() {
        val r = p("Weihnachtsfeier 24.12. 14 Uhr")
        assertEquals("Weihnachtsfeier", r.title)
        assertEquals(LocalDate.of(2026, 12, 24), r.date)
        assertEquals(LocalTime.of(14, 0), r.time)
        val r2 = p("Steuer 15.3.2027 um 9:30")
        assertEquals(LocalDate.of(2027, 3, 15), r2.date)
        assertEquals(LocalTime.of(9, 30), r2.time)
        assertEquals("Steuer", r2.title)
    }

    @Test fun monatsnamen() {
        assertEquals(LocalDate.of(2026, 11, 3), p("Reifen am 3. November").date)
        assertEquals(LocalDate.of(2027, 1, 5), p("Neujahrsvorsatz 5. Jänner").date)
        assertEquals(LocalDate.of(2026, 12, 1), p("Call on Dec 1st").date)
        assertEquals("Call", p("Call on Dec 1st").title)
    }

    @Test fun relativ() {
        assertEquals(LocalDate.of(2026, 10, 21), p("Paket in 3 Tagen").date)
        assertEquals(LocalDate.of(2026, 11, 1), p("Miete in zwei Wochen").date)
        assertEquals(LocalDate.of(2026, 10, 20), p("Abholen übermorgen").date)
        assertEquals(LocalDate.of(2026, 10, 19), p("Wochenplanung nächste Woche").date)
        val r = p("Wasser in 2 Stunden")
        assertEquals(LocalTime.of(12, 0), r.time)
        assertEquals("Wasser", r.title)
    }

    @Test fun nurUhrzeitHeuteOderMorgen() {
        assertEquals(LocalDate.of(2026, 10, 18), p("Anruf um 15 Uhr").date)
        assertEquals(LocalDate.of(2026, 10, 19), p("Anruf um 8 Uhr").date)
        assertEquals(LocalTime.of(8, 0), p("Anruf um 8 Uhr").time)
    }

    @Test fun zeitspanne() {
        val r = p("Workshop Freitag von 9 bis 11 Uhr")
        assertEquals("Workshop", r.title)
        assertEquals(LocalDate.of(2026, 10, 23), r.date)
        assertEquals(LocalTime.of(9, 0), r.time)
        assertEquals(LocalTime.of(11, 0), r.endTime)
        val r2 = p("Kino morgen 20:15-22:30")
        assertEquals(LocalTime.of(20, 15), r2.time)
        assertEquals(LocalTime.of(22, 30), r2.endTime)
        assertEquals("Kino", r2.title)
    }

    @Test fun englisch() {
        val r = p("Dentist tomorrow 3pm")
        assertEquals("Dentist", r.title)
        assertEquals(LocalDate.of(2026, 10, 19), r.date)
        assertEquals(LocalTime.of(15, 0), r.time)
        val r2 = p("Lunch next friday at noon")
        assertEquals(LocalDate.of(2026, 10, 23), r2.date)
        assertEquals(LocalTime.of(12, 0), r2.time)
        assertEquals("Lunch", r2.title)
        assertEquals(LocalTime.of(15, 15), p("Standup at quarter past 3").time)
        assertEquals(LocalDate.of(2026, 11, 24), p("Thanksgiving prep 11/24").date)
    }

    @Test fun ohneDatumBleibtAlles() {
        val r = p("Milch kaufen")
        assertNull(r.date); assertNull(r.time)
        assertEquals("Milch kaufen", r.title)
        // "so" und "do" als Woerter, nicht als Wochentage
        assertNull(p("so schnell wie möglich").date)
        assertNull(p("do it").date)
        // Preis ist kein Datum
        assertNull(p("Budget 12.50 pruefen").time)
    }

    @Test fun wochentagKurz() {
        assertEquals(LocalDate.of(2026, 10, 21), p("Müll Mi.").date)
        assertEquals("Müll", p("Müll Mi.").title)
        assertEquals(LocalDate.of(2026, 10, 25), p("Brunch am Sonntag 11 Uhr").date)
        assertEquals("Brunch", p("Brunch am Sonntag 11 Uhr").title)
    }
}
