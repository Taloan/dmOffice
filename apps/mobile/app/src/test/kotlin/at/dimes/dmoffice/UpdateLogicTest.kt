package at.dimes.dmoffice

import at.dimes.dmoffice.update.Release
import at.dimes.dmoffice.update.UpdateLogic
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdateLogicTest {
    private fun release(tag: String) = Release(tag, UpdateLogic.versionName(tag)!!, UpdateLogic.versionCode(tag)!!, "", "https://github.com/x", 0, "")

    @Test fun versionCodeWieImBuild() {
        assertEquals(10100, UpdateLogic.versionCode("v1.1.0"))
        assertEquals(10203, UpdateLogic.versionCode("1.2.3"))
        assertEquals(1, UpdateLogic.versionCode("v0.0.1"))
        assertEquals(100, UpdateLogic.versionCode("v0.1.0"))
        assertEquals("1.2.0", UpdateLogic.versionName("v1.2.0"))
    }

    @Test fun ungueltigeTags() {
        assertNull(UpdateLogic.versionCode("v1.2"))
        assertNull(UpdateLogic.versionCode("v1.2.0-rc1"))
        assertNull(UpdateLogic.versionCode("latest"))
        assertNull(UpdateLogic.versionCode("v100.0.0"))
        assertNull(UpdateLogic.versionName(""))
    }

    @Test fun neuerNachVersionCode() {
        assertTrue(UpdateLogic.isNewer(release("v1.2.0"), 10100))
        assertTrue(UpdateLogic.isNewer(release("v1.10.0"), 10900))
        assertTrue(UpdateLogic.isNewer(release("v2.0.0"), 19999))
        assertFalse(UpdateLogic.isNewer(release("v1.1.0"), 10100))
        assertFalse(UpdateLogic.isNewer(release("v1.0.9"), 10100))
        // Lokaler Build ohne -PappVersion hat 0.1.0 = 100
        assertTrue(UpdateLogic.isNewer(release("v1.1.0"), 100))
    }

    @Test fun nurHttpsUndGithub() {
        assertTrue(UpdateLogic.isAllowedUrl("https://github.com/Taloan/dmOffice/releases/download/v1.1.0/dmOffice.apk"))
        assertTrue(UpdateLogic.isAllowedUrl("https://api.github.com/repos/Taloan/dmOffice/releases/latest"))
        assertTrue(UpdateLogic.isAllowedUrl("https://objects.githubusercontent.com/github-production-release-asset/1?x=y"))
        assertTrue(UpdateLogic.isAllowedUrl("https://release-assets.githubusercontent.com/github-production-release-asset/1"))
        assertTrue(UpdateLogic.isAllowedUrl("https://GitHub.com:443/x"))
        assertFalse(UpdateLogic.isAllowedUrl("http://github.com/Taloan/dmOffice/releases/download/v1.1.0/dmOffice.apk"))
        assertFalse(UpdateLogic.isAllowedUrl("https://github.com.evil.example/dmOffice.apk"))
        assertFalse(UpdateLogic.isAllowedUrl("https://evilgithub.com/dmOffice.apk"))
        assertFalse(UpdateLogic.isAllowedUrl("https://raw.githubusercontent.com/x/dmOffice.apk"))
        assertFalse(UpdateLogic.isAllowedUrl("https://github.com@evil.example/dmOffice.apk"))
        assertFalse(UpdateLogic.isAllowedUrl("https://user@github.com/x"))
        assertFalse(UpdateLogic.isAllowedUrl("https://github.com:8443/x"))
        assertFalse(UpdateLogic.isAllowedUrl("file:///sdcard/dmOffice.apk"))
        assertFalse(UpdateLogic.isAllowedUrl("nicht eine adresse"))
    }

    private val sample = """
        {"tag_name":"v1.2.0","draft":false,"prerelease":false,"html_url":"https://github.com/Taloan/dmOffice/releases/tag/v1.2.0",
         "body":"## Neu\r\n- **Updates** direkt in der App\r\n- Fehler in `Mail` behoben\r\n\r\n\r\n\r\nSiehe [Blog](https://dmoffice.de)",
         "assets":[
           {"name":"dmOffice-1.2.0.apk","size":10,"browser_download_url":"https://github.com/Taloan/dmOffice/releases/download/v1.2.0/dmOffice-1.2.0.apk"},
           {"name":"dmOffice.apk","size":12345678,"browser_download_url":"https://github.com/Taloan/dmOffice/releases/download/v1.2.0/dmOffice.apk"}
         ]}
    """.trimIndent()

    @Test fun releaseLesen() {
        val r = UpdateLogic.parseRelease(sample)
        assertNotNull(r)
        r!!
        assertEquals("v1.2.0", r.tag)
        assertEquals("1.2.0", r.versionName)
        assertEquals(10200, r.versionCode)
        assertEquals("https://github.com/Taloan/dmOffice/releases/download/v1.2.0/dmOffice.apk", r.apkUrl)
        assertEquals(12345678L, r.apkSize)
        assertEquals("Neu\n• Updates direkt in der App\n• Fehler in Mail behoben\n\nSiehe Blog", r.notes)
    }

    @Test fun releaseOhneApkOderFremdeAdresse() {
        assertNull(UpdateLogic.parseRelease(sample.replace("\"dmOffice.apk\"", "\"other.apk\"")))
        assertNull(UpdateLogic.parseRelease(sample.replace("https://github.com/Taloan/dmOffice/releases/download/v1.2.0/dmOffice.apk", "http://evil.example/dmOffice.apk")))
        assertNull(UpdateLogic.parseRelease(sample.replace("\"prerelease\":false", "\"prerelease\":true")))
        assertNull(UpdateLogic.parseRelease(sample.replace("v1.2.0\",\"draft", "nightly\",\"draft")))
        assertNull(UpdateLogic.parseRelease(sample.replace("12345678", "999999999999")))
        assertNull(UpdateLogic.parseRelease("{\"message\":\"Not Found\"}"))
        assertNull(UpdateLogic.parseRelease("kein json"))
    }

    @Test fun notizenGekuerzt() {
        val long = "Wort ".repeat(300)
        val n = UpdateLogic.shortNotes(long, 50)
        assertTrue(n.length <= 52)
        assertTrue(n.endsWith(" …"))
        assertEquals("", UpdateLogic.shortNotes(""))
    }

    @Test fun signaturVergleich() {
        val a = UpdateLogic.sha256("schluessel-a".toByteArray())
        val b = UpdateLogic.sha256("schluessel-b".toByteArray())
        assertEquals(64, a.length)
        assertTrue(UpdateLogic.signaturesMatch(setOf(a), setOf(a)))
        assertFalse(UpdateLogic.signaturesMatch(setOf(a), setOf(b)))
        assertFalse(UpdateLogic.signaturesMatch(emptySet(), emptySet()))
        assertFalse(UpdateLogic.signaturesMatch(setOf(a), emptySet()))
        assertFalse(UpdateLogic.signaturesMatch(emptySet(), setOf(a)))
        // Mehrere Signierer: alle muessen gleich sein
        assertFalse(UpdateLogic.signaturesMatch(setOf(a, b), setOf(a)))
        assertTrue(UpdateLogic.signaturesMatch(setOf(a, b), setOf(b, a)))
        // Schluesselrotation: neue APK mit b signiert, ihre Geschichte enthaelt a
        assertTrue(UpdateLogic.signaturesMatch(setOf(a), setOf(b), setOf(a, b)))
        assertFalse(UpdateLogic.signaturesMatch(setOf(a), setOf(b), setOf(b)))
    }
}
