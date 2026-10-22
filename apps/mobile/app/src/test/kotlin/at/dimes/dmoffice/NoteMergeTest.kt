package at.dimes.dmoffice

import at.dimes.dmoffice.data.NoteMerge
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class NoteMergeTest {
    private val origin = listOf("<p>a</p>", "<p>b</p>", "<p>c</p>")

    @Test fun beideAnVerschiedenenStellen() {
        val mine = listOf("<p>a!</p>", "<p>b</p>", "<p>c</p>")
        val theirs = listOf("<p>a</p>", "<p>b</p>", "<p>c</p>", "<p>d</p>")
        assertEquals(listOf("<p>a!</p>", "<p>b</p>", "<p>c</p>", "<p>d</p>"), NoteMerge.diff3(origin, mine, theirs))
    }

    @Test fun nurDortGeaendert() {
        val theirs = listOf("<p>a</p>", "<p>x</p>", "<p>c</p>")
        assertEquals(theirs, NoteMerge.diff3(origin, origin, theirs))
    }

    @Test fun gleicheAenderung() {
        val both = listOf("<p>a</p>", "<p>y</p>", "<p>c</p>")
        assertEquals(both, NoteMerge.diff3(origin, both, both))
    }

    @Test fun dieselbeStelleVerschieden() {
        val mine = listOf("<p>a</p>", "<p>mine</p>", "<p>c</p>")
        val theirs = listOf("<p>a</p>", "<p>theirs</p>", "<p>c</p>")
        assertNull(NoteMerge.diff3(origin, mine, theirs))
    }

    @Test fun abgehaktUndDahinterAngefuegt() {
        // Offline das letzte abgehakt, daheim etwas angehaengt
        val mine = listOf("<p>a</p>", "<p>b</p>", "<p>c!</p>")
        val theirs = listOf("<p>a</p>", "<p>b</p>", "<p>c</p>", "<p>d</p>")
        assertEquals(listOf("<p>a</p>", "<p>b</p>", "<p>c!</p>", "<p>d</p>"), NoteMerge.diff3(origin, mine, theirs))
        assertEquals(listOf("<p>a</p>", "<p>b</p>", "<p>c!</p>", "<p>d</p>"), NoteMerge.diff3(origin, theirs, mine))
    }

    @Test fun abgehaktUndDavorEingefuegt() {
        val mine = listOf("<p>a</p>", "<p>b</p>", "<p>c!</p>")
        val theirs = listOf("<p>a</p>", "<p>b</p>", "<p>x</p>", "<p>c</p>")
        assertEquals(listOf("<p>a</p>", "<p>b</p>", "<p>x</p>", "<p>c!</p>"), NoteMerge.diff3(origin, mine, theirs))
    }
}
