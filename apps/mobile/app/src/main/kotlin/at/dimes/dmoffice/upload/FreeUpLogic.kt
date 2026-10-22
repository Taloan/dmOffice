package at.dimes.dmoffice.upload

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put
import java.io.InputStream
import java.security.MessageDigest

/**
 * Reine Regeln fuer Pruefsummen und "Speicherplatz freigeben" (ohne Android, daher in Unit-Tests pruefbar).
 *
 * Gesichert heisst: der Server hat bestaetigt, dass er genau diesen Inhalt hat (gleiche SHA-256 und Groesse) -
 * egal unter welchem Namen und in welchem Ordner, auch wenn ein anderes Geraet ihn hochgeladen hat.
 * Freigegeben wird nur, was gesichert ist, auf dem Geraet noch unveraendert liegt, aelter als die Schonfrist ist
 * und direkt vor dem Loeschen noch einmal vom Server bestaetigt wird. Im Zweifel bleibt die Datei.
 */
object FreeUpLogic {
    /** Stand der Datei auf dem Geraet, bei jedem Durchsuchen neu bestimmt. */
    const val LOCAL_UNKNOWN = 0
    const val LOCAL_PRESENT = 1   // da, gleiche Groesse und Aenderungszeit wie beim Hochladen
    const val LOCAL_CHANGED = 2   // da, aber veraendert
    const val LOCAL_MISSING = 3   // beim Durchsuchen nicht gefunden

    /** Vorgabe der Schonfrist: Fotos und Videos der letzten 30 Tage bleiben auf dem Geraet. */
    const val DEFAULT_KEEP_DAYS = 30
    val KEEP_CHOICES = listOf(0, 7, 30, 90, 365)

    /** So viele Pruefsummen je Anfrage (der Server nimmt hoechstens 5000). */
    const val LOOKUP_CHUNK = 1000

    /** Nur was vor diesem Zeitpunkt aufgenommen und zuletzt geaendert wurde, darf weg. */
    fun cutoff(now: Long, keepDays: Int): Long = now - keepDays.coerceAtLeast(0) * 86_400_000L

    /** Aelter als die Schonfrist? Beide Zeiten muessen davor liegen - ein frisch bearbeitetes altes Foto bleibt. */
    fun oldEnough(taken: Long, modified: Long, cutoff: Long): Boolean = maxOf(taken, modified) < cutoff

    /** Stand gegen das, was beim Durchsuchen gefunden wurde: Groesse und Aenderungszeit, null = nicht gefunden. */
    fun localState(size: Long, modified: Long, found: Pair<Long, Long>?): Int = when {
        found == null -> LOCAL_MISSING
        found.first == size && found.second == modified -> LOCAL_PRESENT
        else -> LOCAL_CHANGED
    }

    fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }

    fun validHash(h: String?): Boolean = h != null && h.length == 64 && h.all { it in '0'..'9' || it in 'a'..'f' }

    /** SHA-256 eines Stroms (hex, klein) und die gelesene Laenge. */
    fun sha256(input: InputStream, onBytes: (Long) -> Unit = {}): Pair<String, Long> {
        val md = MessageDigest.getInstance("SHA-256")
        val buf = ByteArray(64 * 1024)
        var total = 0L
        while (true) {
            val n = input.read(buf)
            if (n < 0) break
            md.update(buf, 0, n)
            total += n
            onBytes(total)
        }
        return hex(md.digest()) to total
    }

    data class Key(val sha256: String, val size: Long)

    /** Antwort von POST /api/files/hashes: gefundene Inhalte mit einem Pfad als Beleg; [pending]: Dateien auf dem Server noch ohne Pruefsumme. */
    data class Answer(val found: Map<Key, String>, val pending: Long) {
        operator fun plus(o: Answer) = Answer(found + o.found, maxOf(pending, o.pending))
    }

    private val json = Json { ignoreUnknownKeys = true }

    /** [dir]: nur direkt in diesem Ordner suchen ("Duplikate ueberspringen" aus); null: ueberall. */
    fun requestBody(items: Collection<Key>, dir: String? = null): String = buildJsonObject {
        put("items", buildJsonArray { for (k in items) add(buildJsonObject { put("sha256", k.sha256); put("size", k.size) }) })
        if (dir != null) put("dir", dir)
    }.toString()

    fun parseAnswer(text: String): Answer {
        val o = json.parseToJsonElement(text).jsonObject
        val found = HashMap<Key, String>()
        for (e in (o["found"] as? JsonArray ?: JsonArray(emptyList()))) {
            val f = e as? JsonObject ?: continue
            val sha = (f["sha256"] as? JsonPrimitive)?.content?.lowercase() ?: continue
            val size = (f["size"] as? JsonPrimitive)?.longOrNull ?: continue
            val path = (f["path"] as? JsonPrimitive)?.content ?: continue
            if (validHash(sha)) found[Key(sha, size)] = path
        }
        return Answer(found, (o["pending"] as? JsonPrimitive)?.longOrNull ?: 0)
    }

    /** Wie "schon vorhanden" beim Hochladen bestimmt wird. */
    sealed class Already {
        /** Gleicher Inhalt liegt auf dem Server (Pfad als Beleg): nicht noch einmal hochladen. */
        data class ByHash(val path: String) : Already()
        /** Server ohne Pruefsummen oder noch nicht fertig nachgerechnet: wie bisher Name und Groesse im Zielordner. */
        data object ByName : Already()
        /** Inhalt nicht auf dem Server: hochladen. */
        data object No : Already()
    }

    /** [answer] null: der Server kennt keine Pruefsummen (aeltere Version). */
    fun already(answer: Answer?, sha256: String, size: Long): Already {
        if (answer == null) return Already.ByName
        answer.found[Key(sha256, size)]?.let { return Already.ByHash(it) }
        return if (answer.pending > 0) Already.ByName else Already.No
    }

    /** Ergebnis eines Uploads, gemessen an den Pruefsummen. */
    enum class Verdict { OK, CHANGED, SERVER_MISMATCH, SERVER_UNKNOWN }

    /**
     * [before]: vor dem Hochladen gerechnet, [sent]: beim Senden mitgerechnet, [server]: was der Server gespeichert hat.
     * Weicht [sent] ab, hat sich die Datei beim Hochladen geaendert; weicht [server] ab, ist unterwegs etwas kaputtgegangen.
     */
    fun verdict(before: String, sent: String?, server: String?): Verdict = when {
        sent != before -> Verdict.CHANGED
        server == null -> Verdict.SERVER_UNKNOWN
        !server.equals(sent, ignoreCase = true) -> Verdict.SERVER_MISMATCH
        else -> Verdict.OK
    }

    /**
     * Was nach der Gegenprobe wirklich geloescht werden darf: nur mit gueltiger Pruefsumme, die der Server mit gleicher
     * Groesse gerade eben bestaetigt hat.
     */
    fun <T> confirmed(candidates: List<T>, answer: Answer, key: (T) -> Key?): List<T> =
        candidates.filter { c -> key(c)?.let { k -> validHash(k.sha256) && k.size > 0 && answer.found.containsKey(k) } == true }

    /** "1,4 GB" - mit Komma nach Ortsgebrauch. */
    fun formatBytes(bytes: Long, locale: java.util.Locale = java.util.Locale.getDefault()): String {
        val units = listOf("B", "KB", "MB", "GB", "TB")
        var v = bytes.toDouble(); var i = 0
        while (v >= 1024 && i < units.lastIndex) { v /= 1024; i++ }
        return if (i == 0) "$bytes B" else String.format(locale, if (v >= 100) "%.0f %s" else "%.1f %s", v, units[i])
    }
}
