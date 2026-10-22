package at.dimes.dmoffice

import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.Office
import at.dimes.dmoffice.files.OfficeConfig
import at.dimes.dmoffice.files.OfficeSession
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class OfficeTest {
    private fun f(name: String, dir: Boolean = false) = FileItem(name, name, dir)
    private val cfg = OfficeConfig(enabled = true, edit = listOf("csv", "docx", "odt", "pptx", "xlsx"), view = listOf("doc", "docx", "pdf", "xlsx"), create = listOf("docx", "xlsx", "pptx"))

    @Test fun art_nach_endung() {
        assertEquals(Office.EDIT, Office.mode(f("Brief.DOCX"), cfg))
        assertEquals(Office.EDIT, Office.mode(f("a.odt"), cfg))
        assertEquals(Office.VIEW, Office.mode(f("alt.doc"), cfg))
        assertNull(Office.mode(f("x.pdf"), cfg))            // PDF bleibt im eigenen Betrachter
        assertNull(Office.mode(f("bild.jpg"), cfg))
        assertNull(Office.mode(f("ohne"), cfg))
        assertNull(Office.mode(f("ordner.docx", dir = true), cfg))
    }

    @Test fun ohne_office_server_wie_bisher() {
        assertNull(Office.mode(f("a.docx"), null))
        assertNull(Office.mode(f("a.docx"), OfficeConfig(enabled = false, edit = listOf("docx"))))
        assertFalse(Office.opensOnTap(f("a.docx"), OfficeConfig()))
    }

    @Test fun csv_nicht_beim_antippen() {
        assertTrue(Office.opensOnTap(f("a.xlsx"), cfg))
        assertFalse(Office.opensOnTap(f("a.csv"), cfg))
        assertEquals(Office.EDIT, Office.mode(f("a.csv"), cfg))   // im Menue schon
    }

    @Test fun config_vom_server() {
        val c = Json { ignoreUnknownKeys = true }.decodeFromString(OfficeConfig.serializer(),
            """{"enabled":true,"product":"Collabora Online","edit":["docx"],"view":["docx","pdf"],"create":["docx"]}""")
        assertTrue(c.enabled); assertEquals(listOf("docx"), c.create)
        assertFalse(Json.decodeFromString(OfficeConfig.serializer(), """{"enabled":false}""").enabled)
    }

    @Test fun oeffnen_fragt_nach_handy_oberflaeche() {
        // Wie ApiClient: Vorgaben werden nicht geschrieben - mobile muss trotzdem ankommen
        val json = Json { ignoreUnknownKeys = true; explicitNulls = false }
        assertEquals("""{"path":"a.docx","mobile":true}""", json.encodeToString(at.dimes.dmoffice.files.OfficeOpenBody.serializer(), at.dimes.dmoffice.files.OfficeOpenBody("a.docx", false, mobile = true)))
    }

    @Test fun wopi_token_im_formular() {
        val body = Office.wopiForm(OfficeSession(url = "https://co/x", token = "a+b/c=", ttl = 123)).toString(Charsets.UTF_8)
        assertEquals("access_token=a%2Bb%2Fc%3D&access_token_ttl=123", body)
    }

    @Test fun docs_seite_bettet_konfiguration_sicher_ein() {
        val config = buildJsonObject { put("document", buildJsonObject { put("title", "</script><b>x") }); put("token", JsonPrimitive("abc")) }
        val html = Office.docsHtml(OfficeSession(api = "https://oo/web-apps/apps/api/documents/api.js?a=1&b=\"2", config = config), null)
        assertFalse(html.contains("</script><b>"))
        assertTrue(html.contains("<\\/script><b>x"))
        assertTrue(html.contains("src=\"https://oo/web-apps/apps/api/documents/api.js?a=1&amp;b=&quot;2\""))
        assertFalse(html.contains("cfg.type"))
        assertTrue(Office.docsHtml(OfficeSession(api = "x", config = config), "mobile").contains("cfg.type = 'mobile';"))
    }

    @Test fun name_fuer_neues_dokument() {
        assertEquals("Neues Dokument", Office.newName("Neues Dokument", "docx", listOf("a.txt")))
        assertEquals("Neues Dokument (2)", Office.newName("Neues Dokument", "docx", listOf("neues dokument.docx")))
        assertEquals("Neues Dokument (3)", Office.newName("Neues Dokument", "xlsx", listOf("Neues Dokument.xlsx", "Neues Dokument (2).xlsx")))
    }
}
