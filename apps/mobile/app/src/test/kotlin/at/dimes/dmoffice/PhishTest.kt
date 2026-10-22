package at.dimes.dmoffice

import at.dimes.dmoffice.api.Message
import at.dimes.dmoffice.api.PhishFinding
import at.dimes.dmoffice.ui.phishWarns
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PhishTest {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }

    @Test fun merkmale_kommen_aus_dem_mail_json() {
        val m = json.decodeFromString<Message>("""{"uid":1,"subject":"x","phish":[
            {"kind":"link","strong":true,"domain":null,"shown":"www.sparkasse.de","target":"boese.example","like":null,"detail":null},
            {"kind":"newsender","strong":false,"domain":"a@neu.example","target":"neu.example"}]}""")
        assertEquals(2, m.phish!!.size)
        assertEquals(PhishFinding(kind = "link", strong = true, shown = "www.sparkasse.de", target = "boese.example"), m.phish!![0])
        // Aelterer Server ohne das Feld
        assertNull(json.decodeFromString<Message>("""{"uid":1}""").phish)
    }

    @Test fun warnung_bei_starkem_merkmal_oder_zwei_schwachen() {
        val weak = PhishFinding(kind = "newsender")
        val strong = PhishFinding(kind = "lookalike", strong = true)
        assertFalse(phishWarns(listOf(weak)))
        assertTrue(phishWarns(listOf(strong)))
        assertTrue(phishWarns(listOf(weak, PhishFinding(kind = "auth", detail = "spf"))))
    }
}
