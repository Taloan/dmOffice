package at.dimes.dmoffice

import at.dimes.dmoffice.upload.Scanner
import at.dimes.dmoffice.upload.UploadLogic
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.ZoneId
import java.time.ZonedDateTime

class UploadLogicTest {
    private val vienna = ZoneId.of("Europe/Vienna")
    private fun ms(y: Int, m: Int, d: Int, h: Int = 12) = ZonedDateTime.of(y, m, d, h, 0, 0, 0, vienna).toInstant().toEpochMilli()

    @Test fun datumsordner() {
        val t = ms(2024, 5, 4)
        assertEquals("", UploadLogic.dateFolder(UploadLogic.SUB_NONE, t, vienna))
        assertEquals("2024", UploadLogic.dateFolder(UploadLogic.SUB_YEAR, t, vienna))
        assertEquals("2024/05", UploadLogic.dateFolder(UploadLogic.SUB_MONTH, t, vienna))
        assertEquals("2024/05/04", UploadLogic.dateFolder(UploadLogic.SUB_DAY, t, vienna))
        // Ortszeit zaehlt: 0:30 Uhr in Wien ist noch der Vortag in UTC
        assertEquals("2024/01/01", UploadLogic.dateFolder(UploadLogic.SUB_DAY, ZonedDateTime.of(2024, 1, 1, 0, 30, 0, 0, vienna).toInstant().toEpochMilli(), vienna))
        assertEquals("", UploadLogic.dateFolder(UploadLogic.SUB_MONTH, 0, vienna))
    }

    @Test fun zielpfad() {
        val t = ms(2023, 12, 31)
        assertEquals("Fotos/Kamera/2023/12", UploadLogic.targetDir("/Fotos//Kamera/", "", false, UploadLogic.SUB_MONTH, t, vienna))
        assertEquals("WhatsApp/WhatsApp Images/Sent/2023", UploadLogic.targetDir("WhatsApp", "WhatsApp Images/Sent", true, UploadLogic.SUB_YEAR, t, vienna))
        assertEquals("WhatsApp", UploadLogic.targetDir("WhatsApp", "WhatsApp Images", false, UploadLogic.SUB_NONE, t, vienna))
        assertEquals("2023/12/31", UploadLogic.targetDir("", "", false, UploadLogic.SUB_DAY, t, vienna))
        assertEquals("a/b", UploadLogic.normalize("\\a/./../b/"))
    }

    @Test fun notizi_ordner_ist_gesperrt() {
        assertTrue(UploadLogic.forbiddenTarget("notizisync"))
        assertTrue(UploadLogic.forbiddenTarget("/NotiziSync/Bilder"))
        assertFalse(UploadLogic.forbiddenTarget("notizisync2"))
        assertFalse(UploadLogic.forbiddenTarget("Fotos/notizisync"))
        assertFalse(UploadLogic.forbiddenTarget(""))
    }

    @Test fun dateiarten() {
        assertTrue(UploadLogic.typeMatches(UploadLogic.TYPES_PHOTOS, "image/jpeg", "a.jpg"))
        assertFalse(UploadLogic.typeMatches(UploadLogic.TYPES_PHOTOS, "video/mp4", "a.mp4"))
        assertTrue(UploadLogic.typeMatches(UploadLogic.TYPES_VIDEOS, null, "clip.MOV"))
        assertTrue(UploadLogic.typeMatches(UploadLogic.TYPES_PHOTOS, "application/octet-stream", "IMG_1.HEIC"))
        assertTrue(UploadLogic.typeMatches(UploadLogic.TYPES_ALL, "application/pdf", "x.pdf"))
        assertFalse(UploadLogic.typeMatches(UploadLogic.TYPES_VIDEOS, "application/pdf", "x.pdf"))
        assertTrue(UploadLogic.ignoredName(".pending-123-IMG.jpg"))
        assertTrue(UploadLogic.ignoredName(".nomedia"))
        assertFalse(UploadLogic.ignoredName("IMG.jpg"))
    }

    @Test fun wartezeit_waechst_bis_sechs_stunden() {
        assertEquals(30_000L, UploadLogic.backoffMs(1))
        assertEquals(60_000L, UploadLogic.backoffMs(2))
        assertEquals(120_000L, UploadLogic.backoffMs(3))
        assertEquals(6 * 3600_000L, UploadLogic.backoffMs(12))
        assertEquals(6 * 3600_000L, UploadLogic.backoffMs(100))
        var last = 0L
        for (i in 1..UploadLogic.MAX_ATTEMPTS) { val b = UploadLogic.backoffMs(i); assertTrue(b >= last); last = b }
    }

    @Test fun fehlerarten() {
        assertTrue(UploadLogic.permanent(404))
        assertTrue(UploadLogic.permanent(413))
        assertFalse(UploadLogic.permanent(500))
        assertFalse(UploadLogic.permanent(503))
        assertFalse(UploadLogic.permanent(429))
        assertFalse(UploadLogic.permanent(507))
        assertFalse(UploadLogic.permanent(401))
    }

    @Test fun schon_am_ziel() {
        assertTrue(UploadLogic.sameRemote("IMG_1.jpg", 100, false, "img_1.JPG", 100))
        assertFalse(UploadLogic.sameRemote("IMG_1.jpg", 101, false, "IMG_1.jpg", 100))
        assertFalse(UploadLogic.sameRemote("IMG_1.jpg", 100, true, "IMG_1.jpg", 100))
        assertFalse(UploadLogic.sameRemote("IMG_2.jpg", 100, false, "IMG_1.jpg", 100))
    }

    @Test fun unterordner_der_quelle() {
        assertEquals("", UploadLogic.relDir("DCIM/Camera/", "DCIM/Camera/"))
        assertEquals("Sub/Tief", UploadLogic.relDir("DCIM/Camera/", "DCIM/Camera/Sub/Tief/"))
        assertEquals("WhatsApp Images/Sent", UploadLogic.relDir("Android/media/com.whatsapp/WhatsApp/Media/", "android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent"))
        assertEquals("", UploadLogic.relDir("DCIM/Camera/", "Pictures/"))
    }

    @Test fun startdatum() {
        // (since, taken, modified, added, ruleCreated)
        assertTrue(UploadLogic.newEnough(0, 0, 1, 1, 5000))                // kein Startdatum: alles
        assertTrue(UploadLogic.newEnough(1000, 1500, 10, 10, 5000))        // Aufnahme nach dem Startdatum
        assertTrue(UploadLogic.newEnough(1000, 0, 1000, 10, 5000))         // ohne Aufnahmedatum zaehlt die Aenderung
        assertFalse(UploadLogic.newEnough(1000, 500, 9000, 4000, 5000))    // Geraetewechsel: vor dem Anlegen der Regel neu hinzugefuegt/geaendert, aber alt aufgenommen
        assertFalse(UploadLogic.newEnough(1000, 0, 999, 999, 5000))
        assertTrue(UploadLogic.newEnough(1000, 500, 500, 6000, 5000))      // nach dem Anlegen der Regel aufs Geraet gekommen: immer neu
    }

    @Test fun aufnahmedatum_lesen() {
        assertEquals(ms(2024, 5, 4, 13), Scanner.parseExif("2024:05:04 13:00:00", vienna))
        assertNull(Scanner.parseExif("0000:00:00 00:00:00", vienna))
        assertNull(Scanner.parseExif(null, vienna))
        assertNull(Scanner.parseExif("kaputt", vienna))
        assertEquals(ZonedDateTime.of(2024, 5, 4, 11, 2, 1, 0, ZoneId.of("UTC")).toInstant().toEpochMilli(), Scanner.parseVideoDate("20240504T110201.000Z"))
        assertNull(Scanner.parseVideoDate("19040101T000000.000Z"))
        assertNull(Scanner.parseVideoDate(""))
    }

    @Test fun medienordner_auswahl() {
        assertEquals(0, UploadLogic.folderRank("DCIM/Camera/"))
        assertEquals(0, UploadLogic.folderRank("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/"))
        assertEquals(1, UploadLogic.folderRank("Pictures/Urlaub/"))
        assertEquals(2, UploadLogic.folderRank("Android/media/com.nextcloud.client/nextcloud/admin@nc.dimes.at/SofortUpload/Camera/2026/"))
        assertEquals("2026", UploadLogic.folderName("Android/media/com.nextcloud.client/x/SofortUpload/Camera/2026/"))
        assertEquals("…/SofortUpload/Camera", UploadLogic.folderParent("Android/media/com.nextcloud.client/x/SofortUpload/Camera/2026/"))
        assertEquals("DCIM", UploadLogic.folderParent("DCIM/Camera/"))
        assertEquals("", UploadLogic.folderParent("Download/"))
        val sorted = UploadLogic.sortFolders(listOf("Android/media/app/x/" to 500, "Pictures/A/" to 3, "Download/" to 40, "DCIM/Camera/" to 10), { it.first }, { it.second })
        assertEquals(listOf("DCIM/Camera/", "Download/", "Pictures/A/", "Android/media/app/x/"), sorted.map { it.first })
    }
}
