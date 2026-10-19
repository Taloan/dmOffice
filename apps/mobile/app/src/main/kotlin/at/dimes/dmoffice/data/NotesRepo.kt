package at.dimes.dmoffice.data

import android.content.Context
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.api.NoteDetail
import at.dimes.dmoffice.api.NoteItem
import at.dimes.dmoffice.api.NoteSaveResult
import at.dimes.dmoffice.api.NotesList
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Notizen (notizi, Ende-zu-Ende verschluesselt im Abgleichordner): die Liste und geoeffnete
 * Notizen werden gecacht. Bearbeiten blockweise: unveraenderte Bloecke als Nummer, neue als HTML,
 * mit Versionspruefung (409 bei Aenderung auf einem anderen Geraet).
 */
class NotesRepo(private val context: Context, private val db: AppDb) {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
    private fun api(): ApiClient = App.get(context).api ?: throw ApiException(401, "no account")

    fun all(): Flow<List<NoteItem>> = db.notes().all().map { l -> l.map { json.decodeFromString<NoteItem>(it.json) } }
    fun search(q: String): Flow<List<NoteItem>> = (if (q.isBlank()) db.notes().all() else db.notes().search(q.trim())).map { l -> l.map { json.decodeFromString<NoteItem>(it.json) } }
    fun observe(id: Long): Flow<NoteDetail?> = db.notes().observe(id).map { e -> e?.detailJson?.let { json.decodeFromString<NoteDetail>(it) } }

    suspend fun refresh() = withContext(Dispatchers.IO) {
        val list: NotesList = api().get(api().url("/api/notes", "view" to "all"))
        val existing = db.notes()
        val entities = list.items.map { n ->
            val old = existing.byId(n.id)
            NoteEntity(n.id, n.folder, n.title, n.snippet, n.modified, n.important, json.encodeToString(n), if (old?.modified == n.modified) old.detailJson else null)
        }
        existing.upsert(entities)
        existing.deleteMissing(entities.map { it.id }.ifEmpty { listOf(-1L) })
    }

    suspend fun load(id: Long, force: Boolean = false): NoteDetail = withContext(Dispatchers.IO) {
        if (!force) db.notes().byId(id)?.detailJson?.let { return@withContext json.decodeFromString<NoteDetail>(it) }
        val d: NoteDetail = api().get(api().url("/api/notes/$id"))
        db.notes().setDetail(id, json.encodeToString(d))
        d
    }

    /** Bloecke speichern: Liste aus (Index der unveraenderten) oder (neues HTML). */
    suspend fun save(id: Long, version: String, blocks: List<Pair<Int?, String?>>, plain: String): NoteSaveResult = withContext(Dispatchers.IO) {
        val body = buildJsonObject {
            put("version", version)
            put("plain", plain)
            put("blocks", buildJsonArray { for ((i, html) in blocks) add(buildJsonObject { if (i != null) put("i", i) else put("html", html ?: "") }) })
        }
        val r: NoteSaveResult = json.decodeFromString(api().putText(api().url("/api/notes/$id/content"), body.toString()))
        load(id, force = true)
        r
    }

    suspend fun create(folder: String?): Long = withContext(Dispatchers.IO) {
        val r = api().postText(api().url("/api/notes"), buildJsonObject { if (folder != null) put("folder", folder) }.toString())
        val id = (json.parseToJsonElement(r) as kotlinx.serialization.json.JsonObject)["id"]!!.let { (it as JsonPrimitive).content.toLong() }
        refresh()
        id
    }

    @Suppress("unused")
    private val keep: JsonArray = buildJsonArray { }
}
