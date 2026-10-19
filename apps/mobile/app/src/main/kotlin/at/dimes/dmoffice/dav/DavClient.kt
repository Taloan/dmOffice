package at.dimes.dmoffice.dav

import at.dimes.dmoffice.data.Account
import okhttp3.Credentials
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.net.URI
import java.util.concurrent.TimeUnit

class DavException(val code: Int, message: String) : IOException(message)
class PreconditionFailed : IOException("412 Precondition Failed")
class InvalidSyncToken : IOException("sync-token invalid")

data class RemoteCalendar(
    val href: String, val name: String, val color: Int?, val supportsEvents: Boolean, val supportsTasks: Boolean,
    val syncToken: String?, val ctag: String?, val order: Int,
)

data class RemoteObject(val href: String, val etag: String?, val ics: String?, val deleted: Boolean)

data class SyncResult(val objects: List<RemoteObject>, val syncToken: String?)

/** CalDAV gegen dmOffice: PROPFIND, sync-collection, multiget, PUT, DELETE. Basic Auth mit App-Passwort. */
class DavClient(private val account: Account, private val counters: at.dimes.dmoffice.api.E2e.Counters = at.dimes.dmoffice.api.E2e.MemoryCounters(account.counter)) {
    private val xml = "application/xml; charset=utf-8".toMediaType()
    private val ics = "text/calendar; charset=utf-8".toMediaType()
    private val auth = Credentials.basic(account.login, account.token, Charsets.UTF_8)

    private val http = OkHttpClient.Builder()
        .connectTimeout(20, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).writeTimeout(60, TimeUnit.SECONDS)
        .followRedirects(false)
        .apply { if (account.encrypted) addInterceptor(at.dimes.dmoffice.api.E2e.Interceptor(account, counters)) }
        .build()

    private fun url(path: String): String {
        if (path.startsWith("http://") || path.startsWith("https://")) return path
        return account.baseUrl + path
    }

    private fun request(method: String, path: String, body: String?, headers: Map<String, String> = emptyMap(), type: okhttp3.MediaType = xml): Request {
        val b = Request.Builder().url(url(path)).header("Authorization", auth).header("User-Agent", "dmOffice-Android/0.1")
        for ((k, v) in headers) b.header(k, v)
        b.method(method, body?.toRequestBody(type))
        return b.build()
    }

    private fun check(r: Response) {
        if (r.code == 412) throw PreconditionFailed()
        if (r.code == 401) throw DavException(401, "401")
        if (r.code !in 200..299) throw DavException(r.code, "HTTP ${r.code}")
    }

    /** Pfad aus einem href (absolut oder relativ), dekodiert nicht - der Server gibt kodierte Pfade zurueck. */
    private fun pathOf(href: String): String =
        if (href.startsWith("http")) URI(href).rawPath else href

    // ---------------- Kalender ----------------

    fun listCalendars(): List<RemoteCalendar> {
        val body = """<?xml version="1.0" encoding="utf-8"?>
<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/" xmlns:a="http://apple.com/ns/ical/">
  <d:prop>
    <d:displayname/><d:resourcetype/><c:supported-calendar-component-set/>
    <a:calendar-color/><a:calendar-order/><d:sync-token/><cs:getctag/>
  </d:prop>
</d:propfind>"""
        http.newCall(request("PROPFIND", account.calendarHome, body, mapOf("Depth" to "1"))).execute().use { r ->
            check(r)
            val ms = DavXml.parseMultistatus(r.body!!.charStream())
            val out = mutableListOf<RemoteCalendar>()
            for (resp in ms.responses) {
                val rt = resp.prop(Ns.DAV, "resourcetype") ?: continue
                if (rt.children.none { it.first == Ns.CALDAV && it.second == "calendar" }) continue
                val comps = resp.prop(Ns.CALDAV, "supported-calendar-component-set")?.children
                    ?.filter { it.first == Ns.CALDAV && it.second == "comp" }?.mapNotNull { it.third["name"]?.uppercase() }
                val events = comps == null || comps.isEmpty() || "VEVENT" in comps
                val tasks = comps == null || comps.isEmpty() || "VTODO" in comps
                val path = pathOf(resp.href).let { if (it.endsWith("/")) it else "$it/" }
                out.add(RemoteCalendar(
                    href = path,
                    name = resp.text(Ns.DAV, "displayname") ?: path.trimEnd('/').substringAfterLast('/'),
                    color = parseColor(resp.text(Ns.ICAL, "calendar-color")),
                    supportsEvents = events, supportsTasks = tasks,
                    syncToken = resp.text(Ns.DAV, "sync-token"),
                    ctag = resp.text(Ns.CS, "getctag"),
                    order = resp.text(Ns.ICAL, "calendar-order")?.toIntOrNull() ?: 0,
                ))
            }
            return out.sortedWith(compareBy({ it.order }, { it.name }))
        }
    }

    /** "#RRGGBB" oder "#RRGGBBAA" (Apple) nach ARGB. */
    private fun parseColor(s: String?): Int? {
        val h = s?.trim()?.removePrefix("#") ?: return null
        return try {
            when (h.length) {
                6 -> (0xFF000000L or h.toLong(16)).toInt()
                8 -> { val rgb = h.substring(0, 6).toLong(16); val a = h.substring(6, 8).toLong(16); ((a shl 24) or rgb).toInt() }
                else -> null
            }
        } catch (_: Exception) { null }
    }

    // ---------------- sync-collection ----------------

    /** Aenderungen seit [token] (leer: alles). Wirft [InvalidSyncToken], wenn der Server das Token nicht mehr kennt. */
    fun sync(calendarHref: String, token: String?): SyncResult {
        val body = """<?xml version="1.0" encoding="utf-8"?>
<d:sync-collection xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
  <d:sync-token>${DavXml.escape(token ?: "")}</d:sync-token>
  <d:sync-level>1</d:sync-level>
  <d:prop><d:getetag/><c:calendar-data/></d:prop>
</d:sync-collection>"""
        http.newCall(request("REPORT", calendarHref, body, mapOf("Depth" to "0"))).execute().use { r ->
            if (r.code == 403 || r.code == 409) {
                val text = r.body?.string() ?: ""
                if (token != null && (text.contains("valid-sync-token") || text.isEmpty())) throw InvalidSyncToken()
                throw DavException(r.code, "HTTP ${r.code}")
            }
            check(r)
            val ms = DavXml.parseMultistatus(r.body!!.charStream())
            val objs = ms.responses.mapNotNull { resp ->
                val path = pathOf(resp.href)
                if (path.endsWith("/")) return@mapNotNull null   // die Sammlung selbst
                if (resp.status == 404) RemoteObject(path, null, null, true)
                else RemoteObject(path, resp.etag, resp.text(Ns.CALDAV, "calendar-data"), false)
            }
            return SyncResult(objs, ms.syncToken)
        }
    }

    /** Einzelne Objekte nachladen (wenn calendar-data im Sync fehlte). */
    fun multiget(calendarHref: String, hrefs: List<String>): List<RemoteObject> {
        if (hrefs.isEmpty()) return emptyList()
        val body = buildString {
            append("""<?xml version="1.0" encoding="utf-8"?>""").append('\n')
            append("""<c:calendar-multiget xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/><c:calendar-data/></d:prop>""")
            for (h in hrefs) append("<d:href>").append(DavXml.escape(h)).append("</d:href>")
            append("</c:calendar-multiget>")
        }
        http.newCall(request("REPORT", calendarHref, body, mapOf("Depth" to "1"))).execute().use { r ->
            check(r)
            val ms = DavXml.parseMultistatus(r.body!!.charStream())
            return ms.responses.map { resp ->
                val path = pathOf(resp.href)
                if (resp.status == 404) RemoteObject(path, null, null, true) else RemoteObject(path, resp.etag, resp.text(Ns.CALDAV, "calendar-data"), false)
            }
        }
    }

    fun get(href: String): Pair<String, String?> {
        http.newCall(request("GET", href, null)).execute().use { r ->
            check(r)
            return (r.body?.string() ?: "") to r.header("ETag")
        }
    }

    // ---------------- Schreiben ----------------

    /** PUT mit If-Match (bestehend) bzw. If-None-Match: * (neu). Gibt den neuen ETag zurueck (ggf. null). */
    fun put(href: String, ics: String, etag: String?): String? {
        val headers = if (etag != null) mapOf("If-Match" to etag) else mapOf("If-None-Match" to "*")
        http.newCall(request("PUT", href, ics, headers, this.ics)).execute().use { r ->
            check(r)
            return r.header("ETag")
        }
    }

    /** Loeschen mit If-Match; 404 gilt als erledigt. */
    fun delete(href: String, etag: String?) {
        val headers = if (etag != null) mapOf("If-Match" to etag) else emptyMap()
        http.newCall(request("DELETE", href, null, headers)).execute().use { r ->
            if (r.code == 404) return
            check(r)
        }
    }
}
