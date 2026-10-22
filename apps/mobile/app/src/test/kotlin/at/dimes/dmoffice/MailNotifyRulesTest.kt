package at.dimes.dmoffice

import at.dimes.dmoffice.api.Addr
import at.dimes.dmoffice.api.MailItem
import at.dimes.dmoffice.notify.MailNotifyConfig
import at.dimes.dmoffice.notify.MailNotifyOverride
import at.dimes.dmoffice.notify.MailNotifyRules
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class MailNotifyRulesTest {
    private fun mail(uid: Long, seen: Boolean = false, focus: Boolean? = null, from: String = "a@x.de") =
        MailItem(uid = uid, seen = seen, focus = focus, from = Addr(address = from))
    private val def = MailNotifyConfig("all", true, false)

    @Test fun vorgabe_gilt_ohne_eigene_werte() {
        assertEquals(def, MailNotifyRules.resolve(null, def))
        assertEquals(def, MailNotifyRules.resolve(MailNotifyOverride(), def))
    }

    @Test fun eigene_werte_ueberschreiben_einzeln() {
        val c = MailNotifyRules.resolve(MailNotifyOverride(mode = "off", hideOnLock = true), def)
        assertEquals(MailNotifyConfig("off", true, true), c)
        // Unbekannter Modus faellt auf die Vorgabe zurueck
        assertEquals("all", MailNotifyRules.resolve(MailNotifyOverride(mode = "kaputt"), def).mode)
    }

    @Test fun nur_neue_ungelesene_aelteste_zuerst() {
        val items = listOf(mail(5), mail(9), mail(7, seen = true), mail(3), mail(8))
        assertEquals(listOf(5L, 8L, 9L), MailNotifyRules.fresh(items, 4, "all", emptySet()).map { it.uid })
    }

    @Test fun erster_lauf_und_aus_melden_nichts() {
        assertTrue(MailNotifyRules.fresh(listOf(mail(1)), -1, "all", emptySet()).isEmpty())
        assertTrue(MailNotifyRules.fresh(listOf(mail(1)), 0, "off", emptySet()).isEmpty())
    }

    @Test fun nur_relevante() {
        val items = listOf(mail(1, focus = true), mail(2, focus = false), mail(3, focus = null))
        assertEquals(listOf(1L, 3L), MailNotifyRules.fresh(items, 0, "relevant", emptySet()).map { it.uid })
    }

    @Test fun nur_vips() {
        val items = listOf(mail(1, from = "Chef@Firma.de"), mail(2, from = "spam@x.de"))
        assertEquals(listOf(1L), MailNotifyRules.fresh(items, 0, "vip", setOf("chef@firma.de")).map { it.uid })
    }

    @Test fun begrenzung_nach_dem_filtern_behaelt_die_neuesten() {
        val items = (1L..10L).map { mail(it, focus = it % 2 == 0L) }
        assertEquals(listOf(6L, 8L, 10L), MailNotifyRules.fresh(items, 0, "relevant", emptySet(), limit = 3).map { it.uid })
    }

    @Test fun kanal_und_name_je_konto() {
        assertEquals("mail.abc", MailNotifyRules.channelId("abc"))
        assertEquals("Privat", MailNotifyRules.label("Privat", "p@x.de", "1"))
        assertEquals("p@x.de", MailNotifyRules.label("", "p@x.de", "1"))
        assertEquals("1", MailNotifyRules.label(" ", "", "1"))
    }
}
