package at.dimes.dmoffice.update

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull

/** Neueste Version auf GitHub: Tag, versionCode aus dem Tag, Release-Notizen und die APK. */
data class Release(
    val tag: String,
    val versionName: String,
    val versionCode: Int,
    val notes: String,
    val apkUrl: String,
    val apkSize: Long,
    val pageUrl: String,
)

/**
 * Reine Logik ohne Android (Unit-Tests): Tag lesen, Versionen vergleichen, Adressen pruefen, Signaturen vergleichen.
 * Der versionCode entsteht wie in app/build.gradle.kts: a*10000 + b*100 + c.
 */
object UpdateLogic {
    const val RELEASES_LATEST = "https://api.github.com/repos/Taloan/dmOffice/releases/latest"
    const val APK_NAME = "dmOffice.apk"
    /** Obergrenze fuer die APK; was groesser ist, ist sicher nicht unsere. */
    const val MAX_APK_BYTES = 200L * 1024 * 1024

    /** Nur von GitHub laden. release-assets.githubusercontent.com ist das neuere Ziel der Download-Weiterleitung. */
    val ALLOWED_HOSTS = setOf("github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com")

    private val TAG = Regex("^v?(\\d{1,2})\\.(\\d{1,2})\\.(\\d{1,2})$")

    /** "v1.2.3" oder "1.2.3" -> "1.2.3"; sonst null (Vorabversionen wie v1.2.0-rc1 zaehlen nicht). */
    fun versionName(tag: String): String? = TAG.matchEntire(tag.trim())?.let { m -> m.groupValues.drop(1).joinToString(".") { it.toInt().toString() } }

    /** versionCode wie im Build: a*10000 + b*100 + c. */
    fun versionCode(tag: String): Int? = TAG.matchEntire(tag.trim())?.groupValues?.drop(1)?.map { it.toInt() }?.let { (a, b, c) -> a * 10000 + b * 100 + c }

    /** Neuer als die installierte App? Massgeblich ist der versionCode, nur den nimmt Android als Update an. */
    fun isNewer(release: Release, installedCode: Int): Boolean = release.versionCode > installedCode

    /** Nur HTTPS und nur GitHub-Hosts (genau, keine Subdomains, kein Port, keine Zugangsdaten in der Adresse). */
    fun isAllowedUrl(url: String): Boolean {
        val u = try { java.net.URI(url) } catch (_: Exception) { return false }
        if (u.scheme?.lowercase() != "https") return false
        if (u.rawUserInfo != null) return false
        if (u.port != -1 && u.port != 443) return false
        val host = u.host?.lowercase()?.trimEnd('.') ?: return false
        return host in ALLOWED_HOSTS
    }

    private val json = Json { ignoreUnknownKeys = true }

    /** Antwort von /releases/latest lesen. null, wenn Tag oder APK fehlen, es ein Entwurf/Vorabversion ist oder die Adresse nicht passt. */
    fun parseRelease(body: String): Release? {
        val o = try { json.parseToJsonElement(body).jsonObject } catch (_: Exception) { return null }
        if (o.bool("draft") || o.bool("prerelease")) return null
        val tag = o.str("tag_name") ?: return null
        val name = versionName(tag) ?: return null
        val code = versionCode(tag) ?: return null
        val assets = try { o["assets"]?.jsonArray } catch (_: Exception) { null } ?: return null
        val apk = assets.mapNotNull { try { it.jsonObject } catch (_: Exception) { null } }.firstOrNull { it.str("name") == APK_NAME } ?: return null
        val url = apk.str("browser_download_url") ?: return null
        if (!isAllowedUrl(url)) return null
        val size = apk["size"]?.jsonPrimitive?.longOrNull ?: 0L
        if (size > MAX_APK_BYTES) return null
        return Release(tag, name, code, shortNotes(o.str("body") ?: ""), url, size, o.str("html_url") ?: "https://github.com/Taloan/dmOffice/releases/latest")
    }

    /** Release-Notizen kurz: Markdown-Zeichen weg, Leerzeilen zusammen, hoechstens [max] Zeichen. */
    fun shortNotes(body: String, max: Int = 600): String {
        val lines = body.replace("\r\n", "\n").lines().map { line ->
            line.trim()
                .replace(Regex("^#{1,6}\\s*"), "")
                .replace(Regex("^[*-]\\s+"), "• ")
                .replace(Regex("\\*\\*(.+?)\\*\\*"), "$1")
                .replace(Regex("`([^`]*)`"), "$1")
                .replace(Regex("\\[([^\\]]+)]\\([^)]*\\)"), "$1")
        }
        val text = lines.joinToString("\n").replace(Regex("\n{3,}"), "\n\n").trim()
        return if (text.length <= max) text else text.take(max).substringBeforeLast(' ', text.take(max)).trimEnd() + " …"
    }

    /**
     * Darf die geladene APK installiert werden? Ihre Signatur (SHA-256 der Zertifikate) muss der installierten gleichen.
     * Bei Schluesselrotation genuegt, dass die Signaturgeschichte der APK den heutigen Schluessel der App enthaelt.
     */
    fun signaturesMatch(installed: Set<String>, apk: Set<String>, apkHistory: Set<String> = emptySet()): Boolean {
        if (installed.isEmpty() || apk.isEmpty()) return false
        if (apk == installed) return true
        return installed.size == 1 && installed.first() in apkHistory
    }

    /** SHA-256 als Hex, klein geschrieben. */
    fun sha256(bytes: ByteArray): String =
        java.security.MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    private fun JsonObject.str(key: String): String? = try { this[key]?.jsonPrimitive?.contentOrNull } catch (_: Exception) { null }
    private fun JsonObject.bool(key: String): Boolean = str(key) == "true"
}
