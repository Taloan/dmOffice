package at.dimes.dmoffice

import at.dimes.dmoffice.data.MonthGrid
import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.YearMonth

class MonthGridTest {
    @Test fun oktober2026MontagsStart() {
        // 1.10.2026 ist ein Donnerstag; die Woche beginnt am Montag, 28.9.
        val cells = MonthGrid.cells(YearMonth.of(2026, 10), DayOfWeek.MONDAY)
        assertEquals(42, cells.size)
        assertEquals(LocalDate.of(2026, 9, 28), cells.first())
        assertEquals(LocalDate.of(2026, 11, 8), cells.last())
        assertEquals(DayOfWeek.MONDAY, cells[7].dayOfWeek)
    }

    @Test fun sonntagsStartUndMonatsersterAmWochenanfang() {
        // 1.11.2026 ist ein Sonntag: bei Sonntagsstart beginnt das Raster am Monatsersten
        val cells = MonthGrid.cells(YearMonth.of(2026, 11), DayOfWeek.SUNDAY)
        assertEquals(LocalDate.of(2026, 11, 1), cells.first())
        assertEquals(DayOfWeek.SUNDAY, cells.first().dayOfWeek)
    }
}
