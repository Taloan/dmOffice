package at.dimes.dmoffice.api

import at.dimes.dmoffice.data.Account
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import okhttp3.Credentials
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.util.concurrent.TimeUnit

class ApiException(val code: Int, message: String) : IOException(message)

/**
 * Web-API von dmOffice mit App-Passwort (Basic). Der Server laesst damit Mail, Kalender,
 * Kontakte, Notizen und Dateien zu, keine Verwaltung (Auth.AppPasswordAllowed).
 */
class ApiClient(private val account: Account, private val counters: E2e.Counters = E2e.MemoryCounters(account.counter)) {
    val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
    private val auth = Credentials.basic(account.login, account.token, Charsets.UTF_8)
    private val jsonType = "application/json; charset=utf-8".toMediaType()

    val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(20, TimeUnit.SECONDS).readTimeout(90, TimeUnit.SECONDS).writeTimeout(90, TimeUnit.SECONDS)
        .apply { if (account.encrypted) addInterceptor(E2e.Interceptor(account, counters)) }
        .build()

    /**
     * Ohne Umschlag (nur TLS), fuer grosse Dateiinhalte: der Umschlag haelt alles im Speicher (Base64 in JSON).
     * Mit Umschlag geht hier nie das App-Passwort mit, sondern nur ein Ticket fuer genau diese Datei (siehe [ticket]).
     */
    private val direct: OkHttpClient by lazy { OkHttpClient.Builder()
        .connectTimeout(20, TimeUnit.SECONDS).readTimeout(5, TimeUnit.MINUTES).writeTimeout(5, TimeUnit.MINUTES).build() }

    fun url(path: String, vararg query: Pair<String, Any?>): String {
        val q = query.filter { it.second != null && it.second.toString().isNotEmpty() }
            .joinToString("&") { enc(it.first) + "=" + enc(it.second.toString()) }
        return account.baseUrl + path + (if (q.isEmpty()) "" else (if (path.contains('?')) "&" else "?") + q)
    }

    private fun enc(s: String) = java.net.URLEncoder.encode(s, "UTF-8").replace("+", "%20")

    private fun build(method: String, url: String, body: RequestBody?): Request =
        Request.Builder().url(url).header("Authorization", auth).header("User-Agent", "dmOffice-Android/0.1").header("Accept", "application/json")
            .method(method, body).build()

    /** Anfrage am Umschlag vorbei: mit Umschlag nur mit Ticket, ohne Umschlag (nur TLS) wie bisher mit dem App-Passwort. */
    private fun buildDirect(method: String, url: String, body: RequestBody?, ticket: String?): Request {
        if (ticket == null && account.encrypted) throw IllegalStateException("Mit Umschlag nie ohne Ticket am Umschlag vorbei")
        return Request.Builder().url(url).header("User-Agent", "dmOffice-Android/0.1").header("Accept", "application/json")
            .apply { if (ticket != null) header(TICKET_HEADER, ticket) else header("Authorization", auth) }
            .method(method, body).build()
    }

    private fun check(r: Response): Response {
        if (r.code in 200..299) return r
        val text = try { r.body?.string() ?: "" } catch (_: Exception) { "" }
        val msg = try { json.parseToJsonElement(text).jsonObject["error"]?.jsonPrimitive?.content } catch (_: Exception) { null }
        throw ApiException(r.code, msg ?: (if (r.code == 401) "401" else "HTTP ${r.code}"))
    }

    fun getText(url: String): String = http.newCall(build("GET", url, null)).execute().use { check(it).body?.string() ?: "" }
    fun getBytes(url: String): Pair<ByteArray, String?> = http.newCall(build("GET", url, null)).execute().use { r -> check(r); (r.body?.bytes() ?: ByteArray(0)) to r.header("Content-Type") }

    inline fun <reified T> get(url: String): T = json.decodeFromString(getText(url))

    fun postText(url: String, body: String?): String =
        http.newCall(build("POST", url, (body ?: "").toRequestBody(jsonType))).execute().use { check(it).body?.string() ?: "" }
    fun putText(url: String, body: String): String =
        http.newCall(build("PUT", url, body.toRequestBody(jsonType))).execute().use { check(it).body?.string() ?: "" }
    fun delete(url: String) { http.newCall(build("DELETE", url, null)).execute().use { check(it) } }

    inline fun <reified B> post(url: String, body: B): String = postText(url, json.encodeToString(body))
    inline fun <reified B, reified T> postFor(url: String, body: B): T = json.decodeFromString(post(url, body))

    /** Multipart wie das Web: Feld "json" plus Dateien "files". */
    fun postMultipart(url: String, jsonField: String, files: List<Triple<String, String, ByteArray>>): String {
        val b = MultipartBody.Builder().setType(MultipartBody.FORM).addFormDataPart("json", jsonField)
        for ((name, mime, bytes) in files) b.addFormDataPart("files", name, bytes.toRequestBody(mime.toMediaType()))
        return http.newCall(build("POST", url, b.build())).execute().use { check(it).body?.string() ?: "" }
    }

    /** Antwort in eine Datei schreiben (im Umschlag, also ganz im Speicher - nur fuer kleine Inhalte). */
    fun download(url: String, dest: java.io.File) {
        http.newCall(build("GET", url, null)).execute().use { r ->
            check(r)
            dest.outputStream().use { o -> r.body?.byteStream()?.copyTo(o) }
        }
    }

    // ---------------- Am Umschlag vorbei: grosse Inhalte und Streams, fuer jeden Bereich (Dateien, Chat, ...) ----------------

    /**
     * Ticket fuer genau eine Anfrage am Umschlag vorbei (POST /api/tickets, im Umschlag geholt): gebunden an Methode,
     * Pfad und alle Query-Werte, kurz gueltig, schreibend nur einmal. So sieht ein mitlesender Proxy nie das App-Passwort,
     * hoechstens diese eine Anfrage. null: Konto ohne Umschlag (dann reicht TLS mit dem App-Passwort). Wirft
     * [TicketsUnsupported], wenn der Server noch keine Tickets kennt (aelter als die App). [stream]: langlebig fuer Player.
     */
    private fun ticket(method: String, url: String, stream: Boolean = false): String? {
        if (!account.encrypted) return null
        require(url.startsWith(account.baseUrl + "/")) { "Ticket nur fuer den eigenen Server" }
        val body = buildJsonObject { put("method", method); put("url", url.removePrefix(account.baseUrl)); if (stream) put("stream", true) }
        val text = try { postText(url("/api/tickets"), body.toString()) } catch (e: ApiException) {
            // Unbekannte Route: 404/405 ohne Fehlertext
            if ((e.code == 404 || e.code == 405) && e.message == "HTTP ${e.code}") throw TicketsUnsupported() else throw e
        }
        return json.parseToJsonElement(text).jsonObject["ticket"]?.jsonPrimitive?.content ?: throw ApiException(500, "ticket")
    }

    /**
     * Wie [check]; mit Ticket ist 401 aber kein Anmeldefehler, sondern ein abgelaufenes oder schon benutztes Ticket
     * (etwa nach einer stillen Wiederholung durch OkHttp) - also ein Netzfehler, den der naechste Versuch mit neuem
     * Ticket behebt. Ein echtes Anmeldeproblem zeigt sich schon beim Holen des Tickets.
     */
    private fun checkDirect(r: Response, ticket: String?): Response = try { check(r) } catch (e: ApiException) {
        if (ticket != null && e.code == 401) throw IOException("Ticket abgelehnt") else throw e
    }

    /**
     * Beliebige Anfrage am Umschlag vorbei, gestreamt (nichts liegt ganz im Speicher) - fuer grosse Up- und Downloads
     * jedes Bereichs. Mit Umschlag geht nur ein Ticket fuer genau diese Anfrage mit, ohne Umschlag das App-Passwort.
     * [url] wie von [url] gebaut; genau diese Adresse (Pfad und Query) muss danach gesendet werden. [onCall] bekommt den
     * Aufruf, damit er sich abbrechen laesst. Liefert die Antwort als Text.
     */
    fun sendDirect(method: String, url: String, body: RequestBody?, onCall: (okhttp3.Call) -> Unit = {}): String {
        val t = ticket(method, url)
        val call = direct.newCall(buildDirect(method, url, body, t))
        onCall(call)
        return call.execute().use { checkDirect(it, t).body?.string() ?: "" }
    }

    /** GET am Umschlag vorbei (wie [sendDirect]), Antwort gestreamt nach [dest]. */
    fun downloadDirect(url: String, dest: java.io.File, onCall: (okhttp3.Call) -> Unit = {}) {
        val t = ticket("GET", url)
        val call = direct.newCall(buildDirect("GET", url, null, t))
        onCall(call)
        call.execute().use { r ->
            checkDirect(r, t)
            dest.outputStream().use { o -> r.body?.byteStream()?.copyTo(o) }
        }
    }

    /**
     * Adresse und Kopfzeilen, mit denen ein Player [url] selbst abruft (Bereichsanfragen beim Spulen, am Umschlag vorbei):
     * mit Umschlag ein langlebiges Lese-Ticket, sonst das App-Passwort. Geht ins Netz, also nicht im Hauptthread.
     */
    fun streamRequest(url: String): Pair<String, Map<String, String>> {
        val t = ticket("GET", url, stream = true)
        return url to (if (t != null) mapOf(TICKET_HEADER to t) else mapOf("Authorization" to auth))
    }

    // ---------------- Dateiablage ----------------

    /**
     * Datei der Ablage nach [dest]. Klein: im Umschlag. Gross ([big]): mit [downloadDirect]. Kennt der Server keine
     * Tickets, geht es im Umschlag, wenn [envelopeOk] (Groesse unbekannt, etwa Versionen); sonst [TicketsUnsupported].
     */
    fun downloadFile(path: String, dest: java.io.File, big: Boolean, version: Long? = null, envelopeOk: Boolean = false) {
        val url = url("/api/files/download", "path" to path, "version" to version)
        if (!big) return download(url, dest)
        try { downloadDirect(url, dest) } catch (e: TicketsUnsupported) { if (envelopeOk) download(url, dest) else throw e }
    }

    /** Eine Datei als Multipart in den Ordner [dir] hochladen (Feld "files", Dateiname = Name am Ziel); gross: wie [uploadStream]. */
    fun uploadFile(dir: String, name: String, mime: String?, file: java.io.File, big: Boolean): String {
        val type = mime?.toMediaTypeOrNull() ?: "application/octet-stream".toMediaType()
        return uploadStream(dir, name, file.asRequestBody(type), big)
    }

    /**
     * Multipart-Upload in die Ablage aus einem Strom (automatisches Hochladen): der Inhalt kommt erst beim Senden aus
     * [body]. Gross ([big]): mit [sendDirect] (je Versuch ein neues Ticket). [onCall] zum Abbrechen.
     */
    fun uploadStream(dir: String, name: String, body: RequestBody, big: Boolean, conflict: String? = null, mtime: Long? = null,
                     onCall: (okhttp3.Call) -> Unit = {}): String {
        val url = url("/api/files/upload", "path" to dir, "conflict" to conflict, "mtime" to mtime)
        val b = MultipartBody.Builder().setType(MultipartBody.FORM).addFormDataPart("files", name, body).build()
        if (big) return sendDirect("POST", url, b, onCall)
        val call = http.newCall(build("POST", url, b))
        onCall(call)
        return call.execute().use { check(it).body?.string() ?: "" }
    }

    fun obj(vararg pairs: Pair<String, JsonElement>): JsonObject = buildJsonObject { for ((k, v) in pairs) put(k, v) }

    /** Der Server kennt noch keine Datei-Tickets: grosse Dateien gehen dann nicht (das App-Passwort bleibt im Umschlag). */
    class TicketsUnsupported : IOException("tickets")

    companion object {
        const val TICKET_HEADER = "X-DM-Ticket"
    }
}
