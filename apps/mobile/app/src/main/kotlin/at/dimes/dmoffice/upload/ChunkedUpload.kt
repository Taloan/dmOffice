package at.dimes.dmoffice.upload

import android.content.SharedPreferences
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import java.io.IOException
import java.io.InputStream

/**
 * Hochladen in Stuecken (Version 2): Teil fuer Teil (8 MB) an /api/files/uploads, ohne Groessengrenze je Datei.
 * Die Teile gehen am Umschlag vorbei (direkt, TLS) - wie grosse Dateien bisher. Die SHA-256 rechnet die App beim
 * Lesen mit. Mit [resumeKey] merkt sie sich die Sitzung: bricht die Verbindung ab, macht der naechste Versuch beim
 * Stand des Servers weiter statt von vorn. Abschliessen danach ueber [finishToFiles] bzw. [finishToChat].
 * Blockiert - nur aus einem Hintergrund-Thread aufrufen.
 */
object ChunkedUpload {
    class Result(val id: String, val sha256: String)

    /** Die Datei ist unterwegs kuerzer oder laenger geworden - ein neuer Versuch hilft nicht. */
    class ChangedException : IOException("Die Datei hat sich beim Hochladen geändert.")

    /** Der Server kennt das Hochladen in Stuecken nicht (vor Version 2.0). */
    class UnsupportedException : IOException("chunks")

    /** So gross darf eine Datei am Server vor Version 2.0 in einem Stueck sein (wie in der App 1.5). */
    const val SINGLE_MAX = 512L * 1024 * 1024

    /** Server (Adresse), die das Hochladen in Stuecken nicht kennen - nicht jedes Mal neu fragen. */
    private val unsupported = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()

    /**
     * Grosse Datei hochladen: in Stuecken ([chunked]); am Server vor Version 2.0 in einem Stueck ([single]) wie die
     * App 1.5 - bis 512 MB, darueber [tooBig] als Meldung.
     */
    fun <T> orSingle(api: ApiClient, size: Long, tooBig: String, chunked: () -> T, single: () -> T): T {
        val server = api.url("")
        fun fallback(): T = if (size <= SINGLE_MAX) single() else throw IOException(tooBig)
        if (server in unsupported) return fallback()
        return try { chunked() } catch (_: UnsupportedException) { unsupported.add(server); fallback() }
    }

    /**
     * [open] liefert den Inhalt jedes Mal von vorn (nach einem Fehler wird neu gelesen). [size] muss stimmen.
     * Kurze Unterbrechungen fangen bis zu fuenf neue Versuche ab; danach (oder bei Abbruch ueber [onCall]) fliegt
     * die IOException weiter - mit [resumeKey] geht es beim naechsten Aufruf an derselben Stelle weiter.
     */
    fun upload(
        api: ApiClient, open: () -> InputStream, name: String, size: Long, mime: String?,
        resumeKey: String? = null, prefs: SharedPreferences? = null,
        onProgress: (Long) -> Unit = {}, onCall: (okhttp3.Call) -> Unit = {},
    ): Result {
        var id = resumeKey?.let { prefs?.getString(it, null) }
        var received = 0L
        var chunk = 8 * 1024 * 1024
        if (id != null) {
            received = try { status(api, id) } catch (e: ApiException) { if (e.code == 404) { id = null; 0L } else throw e }
        }
        if (id == null) {
            val body = api.obj("name" to JsonPrimitive(name), "size" to JsonPrimitive(size), "mime" to JsonPrimitive(mime))
            // 404: Server vor Version 2.0 - der Aufrufer laedt dann in einem Stueck hoch (orSingle)
            val text = try { api.postText(api.url("/api/files/uploads"), body.toString()) }
                catch (e: ApiException) { if (e.code == 404 || e.code == 405) throw UnsupportedException() else throw e }
            val r = api.json.parseToJsonElement(text).jsonObject
            id = r["id"]!!.jsonPrimitive.content
            r["chunkSize"]?.jsonPrimitive?.long?.let { chunk = it.toInt() }
            if (resumeKey != null) prefs?.edit()?.putString(resumeKey, id)?.apply()
        }
        val sessionId = id!!
        var tries = 0
        while (true) {
            try {
                val md = java.security.MessageDigest.getInstance("SHA-256")
                open().use { input ->
                    // Was der Server schon hat: nur lesen (fuer die Pruefsumme), nicht noch einmal senden
                    var pos = 0L
                    val buf = ByteArray(chunk)
                    while (pos < received) {
                        val n = input.read(buf, 0, minOf(buf.size.toLong(), received - pos).toInt())
                        if (n < 0) throw ChangedException()
                        md.update(buf, 0, n); pos += n
                    }
                    onProgress(pos)
                    while (pos < size) {
                        var len = 0
                        while (len < buf.size && pos + len < size) {
                            val n = input.read(buf, len, minOf((buf.size - len).toLong(), size - pos - len).toInt())
                            if (n < 0) throw ChangedException()
                            len += n
                        }
                        val r = api.putRaw(api.url("/api/files/uploads/$sessionId", "offset" to pos), buf, len, onCall)
                        md.update(buf, 0, len)
                        pos += len
                        received = api.json.parseToJsonElement(r).jsonObject["received"]?.jsonPrimitive?.long ?: pos
                        onProgress(pos)
                        tries = 0
                    }
                    if (input.read() >= 0) throw ChangedException()
                }
                return Result(sessionId, hex(md.digest()))
            } catch (e: ChangedException) {
                drop(api, sessionId); forget(resumeKey, prefs)
                throw e
            } catch (e: ApiException) {
                // 409: der Server steht woanders (etwa nach einem Abbruch) - Stand holen und neu lesen.
                // 5xx: kurz warten und noch einmal. Alles andere (Kontingent, Sitzung weg ...) gibt auf.
                if (e.code == 404) forget(resumeKey, prefs)
                if ((e.code != 409 && e.code < 500) || ++tries > 5) throw e
                if (e.code >= 500) Thread.sleep(1500L * tries)
                received = status(api, sessionId)
            } catch (e: IOException) {
                if (e.message == "Canceled" || ++tries > 5) throw e
                Thread.sleep(1500L * tries)
                received = try { status(api, sessionId) } catch (_: ApiException) { throw e } catch (_: IOException) { received }
            }
        }
    }

    private fun status(api: ApiClient, id: String): Long =
        api.json.parseToJsonElement(api.getText(api.url("/api/files/uploads/$id"))).jsonObject["received"]!!.jsonPrimitive.long

    /** Gemerkte Sitzung vergessen (fertig oder verworfen). */
    fun forget(resumeKey: String?, prefs: SharedPreferences?) { if (resumeKey != null) prefs?.edit()?.remove(resumeKey)?.apply() }

    /** In die Dateiablage abschliessen - Antwort wie /api/files/upload ({ files: [{ path, skipped, sha256 }] }). */
    fun finishToFiles(api: ApiClient, id: String, dir: String, conflict: String? = null, mtime: Long? = null): String =
        api.postLong(api.url("/api/files/uploads/$id/finish", "path" to dir, "conflict" to conflict, "mtime" to mtime))

    /** Als Chat-Anhang abschliessen - Antwort: die neue Nachricht. */
    fun finishToChat(api: ApiClient, id: String, room: Long, text: String?): String =
        api.postLong(api.url("/api/chat/uploads/$id/send", "room" to room, "text" to text))

    /** Abbrechen: Teile auf dem Server wegraeumen. */
    fun drop(api: ApiClient, id: String) { try { api.delete(api.url("/api/files/uploads/$id")) } catch (_: Exception) { } }

    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }
}
