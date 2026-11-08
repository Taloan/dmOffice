package at.dimes.dmoffice.dashboard

import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.longOrNull

/** Ein Widget der Übersicht (Version 2) - dieselbe Zusammenstellung wie im Web, sie liegt auf dem Server. */
@Serializable data class DashWidget(
    val id: String, val type: String, val size: Int = 1, val title: String? = null, val opts: JsonObject? = null,
    val stack: List<DashWidget>? = null, val smart: Boolean = false,
)
@Serializable data class DashBackground(val kind: String = "none", val path: String? = null, val album: Long? = null, val dim: Int = 30)
@Serializable data class DashLayout(val widgets: List<DashWidget> = emptyList(), val background: DashBackground = DashBackground(), val saved: Boolean = false)
@Serializable private data class TaskDone(val list: Long, val uri: String, val done: Boolean)
@Serializable private data class TextBody(val text: String)

/** Übersicht (Version 2): Zusammenstellung und Daten der Widgets vom Server (/api/dashboard). Blockiert - aus Coroutinen mit IO aufrufen. */
class DashboardRepo(private val app: App) {
    private fun api(): ApiClient = app.api ?: throw IllegalStateException("Kein Konto")

    // Die App hat ihre eigene Zusammenstellung (Geräteart „phone“) - getrennt vom Web auf breitem Bildschirm
    fun layout(): DashLayout { val a = api(); return a.get(a.url("/api/dashboard/", "device" to "phone")) }
    fun save(l: DashLayout): DashLayout { val a = api(); return a.json.decodeFromString(a.putText(a.url("/api/dashboard/", "device" to "phone"), a.json.encodeToString(DashLayout.serializer(), l))) }
    /** Zurück auf die Vorgabe (wieder wie der Desktop). */
    fun reset(): DashLayout { val a = api(); a.delete(a.url("/api/dashboard/", "device" to "phone")); return layout() }

    /** Daten eines Widgets, roh als JSON - jedes Widget liest sich selbst heraus. */
    fun data(type: String, opts: JsonObject?): JsonObject {
        val a = api()
        val o = opts?.takeIf { it.isNotEmpty() }?.toString()
        return a.json.parseToJsonElement(a.getText(a.url("/api/dashboard/w/$type", "o" to o))) as JsonObject
    }

    fun taskDone(list: Long, uri: String, done: Boolean) { val a = api(); a.post(a.url("/api/dashboard/task"), TaskDone(list, uri, done)) }

    fun uploadBackground(bytes: ByteArray, mime: String): DashLayout { val a = api(); return a.json.decodeFromString(a.putBytes(a.url("/api/dashboard/background", "device" to "phone"), mime, bytes)) }
    fun removeBackground(): DashLayout { val a = api(); a.delete(a.url("/api/dashboard/background", "device" to "phone")); return layout() }
    fun backgroundBytes(): ByteArray? = try { val a = api(); a.getBytes(a.url("/api/dashboard/background", "device" to "phone")).first } catch (_: Exception) { null }

    /** Pukki fragen: Nachricht in seinen Raum, dann auf seine Antwort warten (er antwortet sofort). */
    fun askPukki(room: Long, text: String): String? {
        val a = api()
        val sent = a.json.parseToJsonElement(a.post(a.url("/api/chat/rooms/$room/messages"), TextBody(text))) as JsonObject
        val id = sent.long("id") ?: return null
        repeat(20) {
            Thread.sleep(400)
            val msgs = a.json.parseToJsonElement(a.getText(a.url("/api/chat/rooms/$room/messages", "limit" to 5))) as JsonArray
            msgs.map { it as JsonObject }.firstOrNull { (it.long("id") ?: 0) > id && it.str("sender") == "pukki" }?.let { return it.str("text") ?: "" }
        }
        return null
    }

    fun post(path: String, body: JsonObject) { val a = api(); a.postText(a.url(path), body.toString()) }
}

// Kleine Lesehilfen für die rohen Widget-Daten
fun JsonObject.str(k: String): String? = (this[k] as? JsonPrimitive)?.takeIf { it !is JsonNull }?.contentOrNull
fun JsonObject.int(k: String): Int? = (this[k] as? JsonPrimitive)?.intOrNull
fun JsonObject.long(k: String): Long? = (this[k] as? JsonPrimitive)?.longOrNull
fun JsonObject.dbl(k: String): Double? = (this[k] as? JsonPrimitive)?.doubleOrNull
fun JsonObject.bool(k: String): Boolean = (this[k] as? JsonPrimitive)?.booleanOrNull ?: false
fun JsonObject.obj(k: String): JsonObject? = this[k] as? JsonObject
fun JsonObject.list(k: String): List<JsonObject> = (this[k] as? JsonArray)?.mapNotNull { it as? JsonObject } ?: emptyList()
fun JsonObject.with(k: String, v: JsonElement?): JsonObject = JsonObject(if (v == null) this - k else this + (k to v))
