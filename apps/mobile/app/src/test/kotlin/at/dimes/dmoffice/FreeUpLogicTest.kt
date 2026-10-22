package at.dimes.dmoffice

import at.dimes.dmoffice.upload.FreeUpLogic
import at.dimes.dmoffice.upload.FreeUpLogic.Already
import at.dimes.dmoffice.upload.FreeUpLogic.Key
import at.dimes.dmoffice.upload.FreeUpLogic.Verdict
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FreeUpLogicTest {
    private val a = "a".repeat(64)
    private val b = "b".repeat(64)

    @Test fun pruefsumme_wie_sha256sum() {
        val (h, n) = FreeUpLogic.sha256("abc".byteInputStream())
        assertEquals("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h)
        assertEquals(3, n)
        assertEquals("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", FreeUpLogic.sha256(ByteArray(0).inputStream()).first)
        // Ueber mehrere Puffer hinweg
        val big = ByteArray(200_000) { (it % 251).toByte() }
        val ref = FreeUpLogic.hex(java.security.MessageDigest.getInstance("SHA-256").digest(big))
        assertEquals(ref to 200_000L, FreeUpLogic.sha256(big.inputStream()))
        assertTrue(FreeUpLogic.validHash(h))
        assertFalse(FreeUpLogic.validHash(h.uppercase()))
        assertFalse(FreeUpLogic.validHash("abc"))
        assertFalse(FreeUpLogic.validHash(null))
    }

    @Test fun schon_vorhanden_ueber_den_inhalt() {
        val answer = FreeUpLogic.Answer(mapOf(Key(a, 10) to "Fotos/alt/IMG_1.jpg"), pending = 0)
        assertEquals(Already.ByHash("Fotos/alt/IMG_1.jpg"), FreeUpLogic.already(answer, a, 10))
        // Gleiche Pruefsumme, andere Groesse: nicht derselbe Inhalt
        assertEquals(Already.No, FreeUpLogic.already(answer, a, 11))
        assertEquals(Already.No, FreeUpLogic.already(answer, b, 10))
        // Server rechnet noch nach: wie frueher Name und Groesse pruefen
        assertEquals(Already.ByName, FreeUpLogic.already(answer.copy(pending = 3), b, 10))
        // Server ohne Pruefsummen
        assertEquals(Already.ByName, FreeUpLogic.already(null, a, 10))
    }

    @Test fun antwort_lesen_und_anfrage_bauen() {
        val text = """{"found":[{"sha256":"${a.uppercase()}","size":10,"path":"x.jpg"},{"sha256":"kaputt","size":1,"path":"y"},{"size":2}],"pending":4}"""
        val r = FreeUpLogic.parseAnswer(text)
        assertEquals(mapOf(Key(a, 10) to "x.jpg"), r.found)
        assertEquals(4, r.pending)
        assertEquals("""{"items":[{"sha256":"$a","size":10}]}""", FreeUpLogic.requestBody(listOf(Key(a, 10))))
        assertEquals("""{"items":[{"sha256":"$a","size":10}],"dir":"Fotos/2024"}""", FreeUpLogic.requestBody(listOf(Key(a, 10)), "Fotos/2024"))
        val merged = r + FreeUpLogic.Answer(mapOf(Key(b, 1) to "z"), 0)
        assertEquals(2, merged.found.size)
        assertEquals(4, merged.pending)
    }

    @Test fun hochladen_wird_an_den_pruefsummen_gemessen() {
        assertEquals(Verdict.OK, FreeUpLogic.verdict(a, a, a.uppercase()))
        assertEquals(Verdict.CHANGED, FreeUpLogic.verdict(a, b, b))
        assertEquals(Verdict.CHANGED, FreeUpLogic.verdict(a, null, a))
        assertEquals(Verdict.SERVER_MISMATCH, FreeUpLogic.verdict(a, a, b))
        assertEquals(Verdict.SERVER_UNKNOWN, FreeUpLogic.verdict(a, a, null))
    }

    @Test fun stand_auf_dem_geraet() {
        fun f(size: Long, mod: Long) = size to mod
        assertEquals(FreeUpLogic.LOCAL_PRESENT, FreeUpLogic.localState(10, 100, f(10, 100)))
        assertEquals(FreeUpLogic.LOCAL_CHANGED, FreeUpLogic.localState(10, 100, f(11, 100)))
        assertEquals(FreeUpLogic.LOCAL_CHANGED, FreeUpLogic.localState(10, 100, f(10, 101)))
        assertEquals(FreeUpLogic.LOCAL_MISSING, FreeUpLogic.localState(10, 100, null))
    }

    @Test fun schonfrist() {
        val day = 86_400_000L
        val now = 1_000 * day
        val cut = FreeUpLogic.cutoff(now, 30)
        assertEquals(now - 30 * day, cut)
        assertTrue(FreeUpLogic.oldEnough(now - 40 * day, now - 40 * day, cut))
        assertFalse(FreeUpLogic.oldEnough(now - 10 * day, now - 40 * day, cut))
        // Altes Foto, frisch bearbeitet: bleibt
        assertFalse(FreeUpLogic.oldEnough(now - 400 * day, now - day, cut))
        assertEquals(now, FreeUpLogic.cutoff(now, 0))
        assertEquals(now, FreeUpLogic.cutoff(now, -5))
        assertEquals(FreeUpLogic.DEFAULT_KEEP_DAYS, 30)
    }

    @Test fun geloescht_wird_nur_was_der_server_gerade_bestaetigt() {
        data class C(val id: Int, val sha: String?, val size: Long)
        val list = listOf(C(1, a, 10), C(2, b, 10), C(3, null, 10), C(4, a, 11), C(5, "kaputt", 10), C(6, a, 10), C(7, b.take(63) + "0", 0))
        val answer = FreeUpLogic.Answer(mapOf(Key(a, 10) to "p", Key(a, 11) to "q", Key("kaputt", 10) to "r"), 0)
        val ok = FreeUpLogic.confirmed(list, answer) { c -> c.sha?.let { Key(it, c.size) } }
        // Zwei Kopien desselben Fotos auf dem Geraet duerfen beide weg; ohne gueltige Pruefsumme nichts
        assertEquals(listOf(1, 4, 6), ok.map { it.id })
        assertTrue(FreeUpLogic.confirmed(list, FreeUpLogic.Answer(emptyMap(), 0)) { c -> c.sha?.let { Key(it, c.size) } }.isEmpty())
    }

    @Test fun groessen_lesbar() {
        val de = java.util.Locale.GERMANY
        assertEquals("512 B", FreeUpLogic.formatBytes(512, de))
        assertEquals("1,5 KB", FreeUpLogic.formatBytes(1536, de))
        assertEquals("1,4 GB", FreeUpLogic.formatBytes((1.4 * 1024 * 1024 * 1024).toLong(), de))
        assertEquals("150 MB", FreeUpLogic.formatBytes(150L * 1024 * 1024, de))
    }
}
