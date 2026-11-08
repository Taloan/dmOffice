package at.dimes.dmoffice

import at.dimes.dmoffice.ui.dms.DmsEntry
import at.dimes.dmoffice.ui.dms.DmsNode
import at.dimes.dmoffice.ui.dms.filterOf
import at.dimes.dmoffice.ui.dms.fullName
import at.dimes.dmoffice.ui.dms.ordered
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** DMS in der App: Filter eines Baumordners und Namen der Einträge wie im Web (web/src/dms/model.ts). */
class DmsTreeTest {
    private fun n(level: String, key: String?) = DmsNode(level, key, key ?: "")

    @Test fun ordner_mit_den_ordnern_darueber() {
        val f = filterOf(listOf(n("correspondent", "c1"), n("doc_year", "2025"), n("doc_month", "2025-02")))
        assertEquals("c1", f.correspondent)
        assertEquals("2025-02-01", f.docFrom)
        assertEquals("2025-02-28", f.docTo)
    }

    @Test fun unbekannt_und_tags() {
        val f = filterOf(listOf(n("tag", "t1"), n("tag", "t2"), n("doctype", null), n("import_year", "2024")))
        assertEquals(listOf("t1", "t2"), f.tags)
        assertEquals(true, f.noDoctype)
        assertNull(f.doctype)
        assertEquals("2024-01-01", f.importedFrom)
        assertEquals("2024-12-31", f.importedTo)
    }

    @Test fun namen_mit_oberbegriffen_und_reihenfolge() {
        val all = listOf(
            DmsEntry("a", "correspondent", null, "20_Bürokratie"), DmsEntry("b", "correspondent", "a", "21_Finanzamt"),
            DmsEntry("c", "correspondent", null, "10_Ärzte"), DmsEntry("t", "tag", null, "steuer"),
        )
        assertEquals("20_Bürokratie › 21_Finanzamt", fullName(all, "b"))
        val o = ordered(all, "correspondent")
        assertEquals(listOf("c" to 0, "a" to 0, "b" to 1), o.map { it.first.id to it.second })
        assertTrue(ordered(all, "doctype").isEmpty())
    }
}
