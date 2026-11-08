package at.dimes.dmoffice.journal

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.files.FilesRepo
import at.dimes.dmoffice.upload.ChunkedUpload
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import java.io.File

@Serializable data class Journal(val id: Long, val name: String, val color: String? = null, val count: Int = 0, val owner: String? = null, val canWrite: Boolean = true,
                               /** Nur gezeigt: ansehen ja, herunterladen und exportieren nein */
                               val showOnly: Boolean = false)
@Serializable data class Place(val name: String? = null, val lat: Double? = null, val lon: Double? = null)
@Serializable data class Media(val path: String, val mime: String, val size: Long? = null, val width: Int? = null, val height: Int? = null, val taken: String? = null) {
    val isImage get() = mime.startsWith("image/")
    val isVideo get() = mime.startsWith("video/")
    val isAudio get() = mime.startsWith("audio/")
    val name get() = path.substringAfterLast('/')
}
@Serializable data class Ref(val type: String, val id: String, val name: String)

@Serializable data class Preview(
    val id: String, val journal: Long, val uri: String, val date: String, val title: String? = null, val snippet: String = "", val mood: Int? = null,
    val tags: List<String> = emptyList(), val place: Place? = null, val private: Boolean = false, val media: List<Media> = emptyList(), val refs: Int = 0,
    val weather: String? = null, val owner: String? = null, val canWrite: Boolean = true,
)

@Serializable data class Entry(
    val id: String, val journal: Long, val uri: String, val etag: String, val date: String, val title: String? = null, val html: String = "", val text: String = "",
    val mood: Int? = null, val tags: List<String> = emptyList(), val place: Place? = null, val private: Boolean = false, val media: List<Media> = emptyList(),
    val refs: List<Ref> = emptyList(), val showEvents: Boolean = false, val weather: String? = null, val owner: String? = null, val canWrite: Boolean = true,
)

@Serializable data class EntryInput(
    val journal: Long, val uri: String? = null, val etag: String? = null, val date: String, val title: String? = null, val html: String,
    val mood: Int? = null, val tags: List<String>, val place: Place? = null, val private: Boolean, val showEvents: Boolean, val media: List<Media>,
    val refs: List<Ref>, val weather: String? = null,
)

@Serializable data class Found(val media: Media, val lat: Double? = null, val lon: Double? = null)
@Serializable data class OnThisDay(val label: String, val day: String, val entries: List<Preview>)
@Serializable data class MediaHit(val entry: String, val journal: Long = 0, val date: String, val media: Media)
/** meal: aus einem Essensplan-Kalender die Mahlzeit (Frühstück, Mittagessen, Abendessen, sonst „Mahlzeit“) */
@Serializable data class DayEvent(val title: String, val time: String? = null, val allDay: Boolean = false, val location: String? = null, val meal: String? = null)
@Serializable data class Lookup(val weather: String? = null, val place: String? = null)
@Serializable data class Suggestion(val html: String = "", val events: Int = 0, val tasks: Int = 0)
/** Foto oder Video eines Tages für den Vorschlag: aus der Ablage (kind „file“, Pfad) oder einem Galerie-Ordner (kind „ext“, Pfad „ext:…“); taken: Unix-ms */
@Serializable data class DayPhoto(val kind: String, val path: String, val name: String, val mime: String? = null, val etag: String? = null, val taken: Long? = null)
@Serializable data class DaySuggestion(val events: List<DayEvent> = emptyList(), val tasks: List<String> = emptyList(), val photos: List<DayPhoto> = emptyList())
/** Mit KI schreiben (wie im Web): length short, medium, long; die letzte Wahl ist die Vorgabe. */
@Serializable data class AiSettings(val length: String = "medium", val fitness: Boolean = true, val events: Boolean = true, val place: Boolean = true,
                                    val weather: Boolean = true, val news: Boolean = false)
@Serializable data class AiState(val allowed: Boolean = false, val search: Boolean = false, val feeds: Boolean = false, val settings: AiSettings? = null)
@Serializable data class AiWrite(val date: String, val length: String, val fitness: Boolean, val events: Boolean, val place: Boolean, val weather: Boolean,
                                 val news: Boolean, val placeName: String?, val weatherText: String?, val mood: Int?, val title: String?, val notes: String?, val lang: String,
                                 val journal: Long? = null)
@Serializable data class AiResult(val html: String = "", val sent: List<String> = emptyList())
@Serializable data class JournalSettings(val imageSize: Int = 2048, val lookup: Boolean = false, val ask: String? = null)

/** Stimmung 1–5 wie im Web. */
val Moods = listOf(1 to "😢", 2 to "🙁", 3 to "😐", 4 to "🙂", 5 to "😄")
fun moodEmoji(m: Int?) = Moods.firstOrNull { it.first == m }?.second ?: ""

/** Tagebuch (Version 2) für die App: dieselbe Schnittstelle wie das Web (/api/journal). Blockiert - aus Coroutinen mit IO aufrufen. */
class JournalRepo(private val app: App) {
    /** Reihenfolge der zuletzt gezeigten Liste (Tagebuch, uri) - die Leseansicht blättert darin zum älteren/neueren Eintrag. */
    var order: List<Pair<Long, String>> = emptyList()

    private fun api(): ApiClient = app.api ?: throw IllegalStateException("Kein Konto")
    private suspend fun <T> io(block: () -> T): T = withContext(Dispatchers.IO) { block() }

    suspend fun journals(): List<Journal> = io { api().get<List<Journal>>(api().url("/api/journal/journals")) }

    /** Export als ZIP (je Eintrag Markdown, Fotos und Videos im Original, tagebuch.json) nach [dest] - am Umschlag vorbei, auch groß.
     *  from/to: Tage (yyyy-MM-dd), beide einschließlich; null = ohne Grenze. */
    suspend fun export(journal: Long, from: String?, to: String?, media: Boolean, dest: java.io.File) = io {
        val a = api()
        val q = StringBuilder("/api/journal/export?journal=$journal")
        if (from != null) q.append("&from=").append(from)
        if (to != null) q.append("&to=").append(to)
        if (!media) q.append("&media=false")
        a.downloadDirect(a.url(q.toString()), dest)
    }

    /**
     * Fotos aus der eigenen Ablage ins Tagebuch - ohne Umweg über Android-Teilen: je Aufnahmetag ein Eintrag (gibt es an dem Tag
     * schon einen, kommen sie dazu), Ort aus dem Bild. Gibt (Einträge, Fotos) zurück.
     */
    suspend fun fromFiles(journal: Long, paths: List<String>): Pair<Int, Int> = io {
        val a = api()
        val pathsJson = kotlinx.serialization.json.JsonArray(paths.map { kotlinx.serialization.json.JsonPrimitive(it) })
        val found = a.json.parseToJsonElement(a.postText(a.url("/api/journal/media/files"),
            kotlinx.serialization.json.buildJsonObject { put("paths", pathsJson); put("journal", kotlinx.serialization.json.JsonPrimitive(journal)) }.toString()))
            as kotlinx.serialization.json.JsonArray
        val media = found.map { (it as kotlinx.serialization.json.JsonObject)["media"]!! }
        val places = found.mapNotNull { f ->
            val o = f as kotlinx.serialization.json.JsonObject
            val lat = (o["lat"] as? kotlinx.serialization.json.JsonPrimitive)?.content?.toDoubleOrNull()
            val lon = (o["lon"] as? kotlinx.serialization.json.JsonPrimitive)?.content?.toDoubleOrNull()
            if (lat == null || lon == null) null else kotlinx.serialization.json.buildJsonObject {
                put("path", (o["media"] as kotlinx.serialization.json.JsonObject)["path"]!!)
                put("lat", kotlinx.serialization.json.JsonPrimitive(lat)); put("lon", kotlinx.serialization.json.JsonPrimitive(lon))
            }
        }
        val body = kotlinx.serialization.json.buildJsonObject {
            put("journal", kotlinx.serialization.json.JsonPrimitive(journal))
            put("media", kotlinx.serialization.json.JsonArray(media))
            put("places", kotlinx.serialization.json.JsonArray(places))
        }
        val entries = (a.json.parseToJsonElement(a.postText(a.url("/api/journal/from-media"), body.toString())) as? kotlinx.serialization.json.JsonArray)?.size ?: 0
        entries to media.size
    }

    /** Erste Seite aller Einträge vom letzten Laden (Vorladen, Reiter Tagebuch) - beim Öffnen sofort da, dann frisch vom Server */
    @Volatile var entriesCache: List<Preview>? = null; private set

    suspend fun entries(journal: Long? = null, q: String? = null, tag: String? = null, before: String? = null, limit: Int = 100, from: String? = null, to: String? = null): List<Preview> = io {
        api().get<List<Preview>>(api().url("/api/journal/entries", "journal" to journal, "q" to q?.takeIf { it.isNotBlank() }, "tag" to tag, "before" to before, "limit" to limit, "from" to from, "to" to to))
            .also { if (journal == null && q.isNullOrBlank() && tag == null && before == null && from == null && to == null && limit == 100) entriesCache = it }
    }

    suspend fun entry(journal: Long, uri: String): Entry = io { api().get<Entry>(api().url("/api/journal/entries/$journal/" + android.net.Uri.encode(uri))) }

    suspend fun save(e: EntryInput): Entry = io { api().postFor<EntryInput, Entry>(api().url("/api/journal/entries"), e) }

    suspend fun remove(journal: Long, uri: String) = io { api().delete(api().url("/api/journal/entries/$journal/" + android.net.Uri.encode(uri))) }

    suspend fun onThisDay(): List<OnThisDay> = io { api().get<List<OnThisDay>>(api().url("/api/journal/onthisday")) }

    suspend fun media(): List<MediaHit> = io { api().get<List<MediaHit>>(api().url("/api/journal/media", "limit" to 1000)) }

    /** Foto, Video oder Sprachaufnahme hochladen (in Stücken) und ins Tagebuch übernehmen - Datum und Ort aus dem Bild; bei einem geteilten Tagebuch beim Besitzer. */
    suspend fun upload(journal: Long, file: File, name: String, mime: String?): Found = io {
        val a = api()
        val up = ChunkedUpload.upload(a, { file.inputStream() }, name, file.length(), mime ?: FilesRepo.guessMime(name))
        a.json.decodeFromString(Found.serializer(), a.postText(a.url("/api/journal/uploads/${up.id}/attach", "journal" to journal), ""))
    }

    suspend fun settings(): JournalSettings = io { api().get<JournalSettings>(api().url("/api/journal/settings")) }

    /** Wetter und Ortsname zu Ort und Zeit (nur eingeschaltet). */
    suspend fun lookup(lat: Double, lon: Double, date: String): Lookup = io { api().get<Lookup>(api().url("/api/journal/lookup", "lat" to lat, "lon" to lon, "date" to date)) }

    /** Mit KI schreiben: freigegeben? Vorgabe der Auswahl. */
    suspend fun ai(): AiState = io { api().get<AiState>(api().url("/api/journal/ai")) }

    /** Den Eintrag von der KI ausformulieren lassen (HTML, Absätze). */
    suspend fun aiWrite(b: AiWrite): AiResult = io { api().postFor<AiWrite, AiResult>(api().url("/api/journal/ai/write"), b) }

    /** Termine eines Tages (yyyy-MM-dd). */
    suspend fun events(day: String): List<DayEvent> = io { api().get<List<DayEvent>>(api().url("/api/journal/events", "date" to day)) }

    /** Anfang für den Eintrag: Termine und erledigte Aufgaben des Tages (HTML). */
    suspend fun suggest(day: String): Suggestion = io { api().get<Suggestion>(api().url("/api/journal/suggest", "date" to day)) }

    /** Vorschlag zum Auswählen wie im Web: Termine, erledigte Aufgaben und die Fotos und Videos des Tages aus der Galerie. */
    suspend fun suggestDay(day: String): DaySuggestion = io { api().get<DaySuggestion>(api().url("/api/journal/suggest/day", "date" to day)) }

    /** Dateien aus der Dateiablage an den Eintrag hängen (Server kopiert; in ein geteiltes Tagebuch beim Besitzer). */
    suspend fun attachFiles(journal: Long, paths: List<String>): List<Found> = attach("/api/journal/media/files", "paths", journal, paths)

    /** Fotos und Videos aus Galerie-Ordnern (Kennung ohne „ext:“) an den Eintrag hängen - als Kopie, Bilder kleingerechnet. */
    suspend fun attachExternal(journal: Long, ids: List<String>): List<Found> = attach("/api/journal/media/external", "ids", journal, ids)

    private suspend fun attach(path: String, field: String, journal: Long, values: List<String>): List<Found> = io {
        if (values.isEmpty()) return@io emptyList()
        val a = api()
        val body = kotlinx.serialization.json.buildJsonObject {
            put(field, kotlinx.serialization.json.JsonArray(values.map { kotlinx.serialization.json.JsonPrimitive(it) }))
            put("journal", kotlinx.serialization.json.JsonPrimitive(journal))
        }
        a.json.decodeFromString(kotlinx.serialization.builtins.ListSerializer(Found.serializer()), a.postText(a.url(path), body.toString()))
    }

    private val thumbs = object : LruCache<String, Bitmap>(16 * 1024 * 1024) { override fun sizeOf(key: String, value: Bitmap) = value.byteCount }

    /** Vorschaubild einer Datei des Tagebuchs - auch in geteilten Tagebüchern (gezwischengespeichert); null: keines. */
    suspend fun thumb(journal: Long, path: String, size: Int = 256): Bitmap? {
        val key = "$journal|$size|$path"
        thumbs.get(key)?.let { return it }
        return try {
            io { api().getBytes(api().url("/api/journal/file", "journal" to journal, "path" to path, "size" to size)).first }
                .let { BitmapFactory.decodeByteArray(it, 0, it.size) }?.also { thumbs.put(key, it) }
        } catch (e: ApiException) { null } catch (e: java.io.IOException) { null }
    }
}
