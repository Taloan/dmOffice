package at.dimes.dmoffice

import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.FileViews
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalDate
import java.time.ZoneId

class FileViewsTest {
    private val zone = ZoneId.of("Europe/Vienna")
    private fun f(name: String, size: Long = 1, date: String = "2026-10-20T10:00:00Z", dir: Boolean = false, mime: String? = null) =
        FileItem(name, name, dir, size, mime, date)

    @Test fun arten_wie_am_server() {
        assertEquals(FileViews.IMAGE, FileViews.kindOf("a.HEIC", false, null))
        assertEquals(FileViews.PDF, FileViews.kindOf("a.pdf", false, null))
        assertEquals(FileViews.SHEET, FileViews.kindOf("a.xlsx", false, null))
        assertEquals(FileViews.TEXT, FileViews.kindOf("notiz.md", false, null))
        assertEquals(FileViews.VIDEO, FileViews.kindOf("clip", false, "video/mp4"))
        assertEquals(FileViews.OTHER, FileViews.kindOf("x.bin", false, "application/octet-stream"))
        assertEquals(FileViews.FOLDER, FileViews.kindOf("x.jpg", true, null))
        assertTrue(FileViews.hasThumb(f("a.jpg")))
        assertTrue(FileViews.hasThumb(f("a.pdf")))
        assertFalse(FileViews.hasThumb(f("a.svg")))
        assertFalse(FileViews.hasThumb(f("a.txt")))
        assertFalse(FileViews.hasThumb(f("bilder", dir = true)))
    }

    @Test fun sortieren_ordner_zuerst() {
        val list = listOf(f("b.txt", 5), f("A.txt", 9), f("ordner", dir = true), f("c.jpg", 1))
        assertEquals(listOf("ordner", "A.txt", "b.txt", "c.jpg"), FileViews.sort(list, FileViews.SORT_NAME, false).map { it.name })
        assertEquals(listOf("ordner", "c.jpg", "b.txt", "A.txt"), FileViews.sort(list, FileViews.SORT_NAME, true).map { it.name })
        assertEquals(listOf("ordner", "A.txt", "b.txt", "c.jpg"), FileViews.sort(list, FileViews.SORT_SIZE, true).map { it.name })
        assertEquals(listOf("ordner", "c.jpg", "A.txt", "b.txt"), FileViews.sort(list, FileViews.SORT_KIND, false).map { it.name })
        val dated = listOf(f("alt", date = "2020-01-01T00:00:00Z"), f("neu", date = "2026-01-01T00:00:00Z"))
        assertEquals(listOf("neu", "alt"), FileViews.sort(dated, FileViews.SORT_MODIFIED, true).map { it.name })
    }

    @Test fun datumsgruppen() {
        val today = LocalDate.of(2026, 10, 22)   // Donnerstag
        fun g(d: String) = FileViews.dateGroup(java.time.ZonedDateTime.parse(d + "T12:00:00+02:00").toInstant().toEpochMilli(), today, zone)
        assertEquals(0, g("2026-10-22"))
        assertEquals(1, g("2026-10-21"))
        assertEquals(2, g("2026-10-19"))   // Montag dieser Woche
        assertEquals(3, g("2026-10-18"))   // Sonntag davor
        assertEquals(3, g("2026-10-12"))
        assertEquals(4, g("2026-10-02"))
        assertEquals(5, g("2026-03-01"))
        assertEquals(6, g("2025-12-31"))
    }

    @Test fun gruppieren_nach_art_ordner_zuerst() {
        val list = listOf(f("b.pdf"), f("a.jpg"), f("dir", dir = true), f("c.jpg"))
        val groups = FileViews.group(list, FileViews.GROUP_KIND)
        assertEquals(listOf(FileViews.FOLDER, FileViews.IMAGE, FileViews.PDF), groups.map { it.key })
        assertEquals(listOf("a.jpg", "c.jpg"), groups[1].items.map { it.name })
        assertEquals(1, FileViews.group(list, FileViews.GROUP_NONE).size)
    }

    @Test fun galerie_nach_monat() {
        val list = listOf(f("a", date = "2026-09-03T10:00:00Z"), f("b", date = "2026-10-01T10:00:00Z"), f("c", date = "2026-10-15T10:00:00Z"))
        val g = FileViews.byMonth(list, zone)
        assertEquals(listOf("2026-10", "2026-09"), g.map { it.key })
        assertEquals(listOf("c", "b"), g[0].items.map { it.name })
    }

    @Test fun ausschnitt_der_inhaltssuche() {
        val (text, marks) = FileViews.snippet("Die Rechnung vom März.")
        assertEquals("Die Rechnung vom März.", text)
        assertEquals(listOf(4..11, 17..20), marks)
        assertEquals("ohne" to emptyList<IntRange>(), FileViews.snippet("ohne"))
    }
}
