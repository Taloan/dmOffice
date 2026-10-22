package at.dimes.dmoffice.data

import android.content.Context
import android.util.LruCache
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.api.NoteDetail
import at.dimes.dmoffice.api.NoteFolder
import at.dimes.dmoffice.api.NoteItem
import at.dimes.dmoffice.api.NoteMeta
import at.dimes.dmoffice.api.NoteSaveResult
import at.dimes.dmoffice.api.NotesList
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Notizen (notizi, Ende-zu-Ende verschluesselt im Abgleichordner): die Liste, die Ordner und
 * geoeffnete Notizen werden gecacht. Bearbeiten blockweise: unveraenderte Bloecke als Nummer,
 * neue als HTML, mit Versionspruefung (409 bei Aenderung auf einem anderen Geraet).
 */
class NotesRepo(private val context: Context, private val db: AppDb) {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
    private fun api(): ApiClient = App.get(context).api ?: throw ApiException(401, "no account")

    /** Bilder der Notizen: nur im Speicher (die Datenbank ist verschluesselt, der Cache-Ordner nicht). */
    private val blobs = object : LruCache<String, ByteArray>(24 * 1024 * 1024) {
        override fun sizeOf(key: String, value: ByteArray) = value.size
    }

    fun all(): Flow<List<NoteItem>> = db.notes().all().map { l -> l.map { json.decodeFromString<NoteItem>(it.json) } }
    fun search(q: String): Flow<List<NoteItem>> = (if (q.isBlank()) db.notes().all() else db.notes().search(q.trim())).map { l -> l.map { json.decodeFromString<NoteItem>(it.json) } }
    fun observe(id: Long): Flow<NoteDetail?> = db.notes().observe(id).map { e -> e?.detailJson?.let { json.decodeFromString<NoteDetail>(it) } }
    fun folders(): Flow<List<NoteFolder>> = db.noteFolders().all().map { l -> l.map { NoteFolder(it.id, it.name, it.parent, it.sortOrder) } }

    suspend fun refresh() = withContext(Dispatchers.IO) {
        val list: NotesList = api().get(api().url("/api/notes", "view" to "all"))
        val existing = db.notes()
        // Geoeffnete Notizen behalten ihren Inhalt, auch wenn er inzwischen aelter ist: ohne Netz (Einkaufen im Funkloch)
        // ist der letzte Stand besser als keiner; load holt mit Netz ohnehin den neuen
        val entities = list.items.map { n ->
            val old = existing.byId(n.id)
            NoteEntity(n.id, n.folder, n.title, n.snippet, n.modified, n.important, json.encodeToString(n), old?.detailJson)
        }
        existing.upsert(entities)
        existing.deleteMissing(entities.map { it.id }.ifEmpty { listOf(-1L) })
        try {
            val folders: List<NoteFolder> = api().get(api().url("/api/notes/folders"))
            db.noteFolders().replace(folders.map { NoteFolderEntity(it.syncId, it.parent, it.name, it.sortOrder) })
        } catch (_: Exception) { /* Ordner beim naechsten Mal */ }
    }

    /** Volltextsuche auf dem Server (auch im Text, nicht nur in Titel und Vorschau). */
    suspend fun searchServer(q: String): List<NoteItem> = withContext(Dispatchers.IO) {
        api().get<NotesList>(api().url("/api/notes", "q" to q.trim())).items
    }

    /** Archiv oder Papierkorb - nicht gecacht, kommt jedes Mal vom Server. */
    suspend fun view(view: String): List<NoteItem> = withContext(Dispatchers.IO) {
        api().get<NotesList>(api().url("/api/notes", "view" to view)).items
    }

    suspend fun load(id: Long, force: Boolean = false): NoteDetail = withContext(Dispatchers.IO) {
        val cached = db.notes().byId(id)
        val stale = cached?.detailJson?.let { json.decodeFromString<NoteDetail>(it) }
        // Ohne force: der gemerkte Inhalt, solange er zum Stand der Liste passt (oder offline gespeichert ist)
        if (!force && stale != null && (stale.modified == cached.modified || db.notePending().byId(id) != null)) return@withContext stale
        val d: NoteDetail = try { api().get(api().url("/api/notes/$id")) } catch (e: Exception) {
            // Ohne Netz den letzten Stand - aber nur, wenn ihn niemand ausdruecklich frisch verlangt hat
            if (!force && stale != null && isOffline(e)) return@withContext stale else throw e
        }
        db.notes().setDetail(id, json.encodeToString(d))
        d
    }

    /**
     * Bloecke aus dem Editor speichern, so wie getContent() sie liefert: {i, swaps, align} fuer
     * einen Block der geladenen Version, {html} fuer neuen Text.
     */
    suspend fun save(id: Long, version: String, blocks: JsonArray, plain: String): NoteSaveResult = withContext(Dispatchers.IO) {
        val body = buildJsonObject {
            put("version", version)
            put("plain", plain)
            put("blocks", blocks)
        }
        json.decodeFromString<NoteSaveResult>(api().putText(api().url("/api/notes/$id/content"), body.toString()))
    }

    /** Kein Netz (im Unterschied zu einer Antwort des Servers mit Fehler): dann offline speichern. */
    fun isOffline(e: Throwable) = e is java.io.IOException && e !is ApiException

    val pendingCount: Flow<Int> = db.notePending().count()
    suspend fun hasPending(id: Long) = db.notePending().byId(id) != null

    /**
     * Ohne Netz speichern, Bloecke wie beim normalen Speichern ({i} = Block der angezeigten Fassung, {html} = neu).
     * Merkt den Serverstand, auf dem die Aenderung beruht, und zeigt ab jetzt die eigene Fassung - auch nach einem Neustart.
     * null: geht offline nicht (bearbeitete Bilder in gesperrten Bloecken), sonst die neue lokale Fassung.
     */
    suspend fun saveOffline(shown: NoteDetail, blocks: JsonArray, plain: String): NoteDetail? = withContext(Dispatchers.IO) {
        val mine = ArrayList<String>()
        val prot = ArrayList<Boolean>()
        for (b in blocks) {
            val o = b.jsonObject
            val i = o["i"]?.jsonPrimitive?.intOrNull
            if (i != null) {
                if (!(o["swaps"] as? JsonArray).isNullOrEmpty() || (o["align"] ?: JsonNull) != JsonNull) return@withContext null
                mine += shown.blocks.getOrNull(i) ?: return@withContext null
                prot += shown.protected.getOrNull(i) ?: true
            } else {
                mine += o["html"]?.jsonPrimitive?.contentOrNull ?: ""
                prot += false
            }
        }
        val old = db.notePending().byId(shown.id)
        db.notePending().upsert(NotePendingEntity(shown.id, old?.version ?: shown.version,
            old?.baseJson ?: json.encodeToString(shown.blocks), json.encodeToString(mine), plain))
        val local = shown.copy(blocks = mine, protected = prot)
        db.notes().setDetail(shown.id, json.encodeToString(local))
        local
    }

    /**
     * Offline Gespeichertes hochladen: unveraendert auf dem Server - so wie es ist; inzwischen anderswo geaendert -
     * dreiseitig zusammengefuehrt; dieselbe Stelle verschieden geaendert - die eigene Fassung als neue Notiz.
     * Ergebnis: Titel der dabei angelegten Kopien. Ohne Netz bleibt alles fuer das naechste Mal liegen.
     */
    suspend fun flushPending(): List<String> = withContext(Dispatchers.IO) {
        val copies = ArrayList<String>()
        for (p in db.notePending().all()) {
            try { flushOne(p)?.let { copies += it } }
            catch (e: Exception) {
                if (isOffline(e)) break
                // Notiz gibt es nicht mehr oder sie ist nur noch lesbar: die Aenderung laesst sich nicht mehr anbringen
                if (e is ApiException && (e.code == 404 || e.code == 403)) db.notePending().delete(p.id)
            }
        }
        copies
    }

    private suspend fun flushOne(p: NotePendingEntity): String? {
        val base: List<String> = json.decodeFromString(p.baseJson)
        val mine: List<String> = json.decodeFromString(p.mineJson)
        val local = db.notes().byId(p.id)?.detailJson
        repeat(3) {
            val theirs = load(p.id, true)
            val result = if (theirs.version == p.version) mine else NoteMerge.diff3(base, mine, theirs.blocks)
            if (result == null) {
                val title = context.getString(at.dimes.dmoffice.R.string.note_conflict_title,
                    theirs.title.ifBlank { context.getString(at.dimes.dmoffice.R.string.note_untitled) })
                val copy = create(theirs.folder)
                val cd = load(copy, true)
                save(copy, cd.version, buildJsonArray { mine.forEach { add(buildJsonObject { put("html", it) }) } }, p.plain)
                setMeta(copy, NoteMeta(customTitle = title))
                db.notePending().delete(p.id)
                return title
            }
            // Was es in der Serverfassung schon gibt, geht als deren Nummer (gesperrte Bloecke bleiben so unberuehrt)
            val used = HashSet<Int>()
            val send = buildJsonArray {
                for (s in result) {
                    val k = theirs.blocks.indices.firstOrNull { it !in used && theirs.blocks[it] == s }
                    if (k != null) { used += k; add(buildJsonObject { put("i", k) }) } else add(buildJsonObject { put("html", s) })
                }
            }
            try {
                save(p.id, theirs.version, send, p.plain)
                db.notePending().delete(p.id)
                load(p.id, true)
                return null
            } catch (e: ApiException) { if (e.code != 409) throw e }
        }
        // Dreimal dazwischen geaendert: beim naechsten Mal wieder, bis dahin bleibt die eigene Fassung zu sehen
        db.notes().setDetail(p.id, local)
        return null
    }

    suspend fun create(folder: String?): Long = withContext(Dispatchers.IO) {
        val r = api().postText(api().url("/api/notes"), buildJsonObject { if (folder != null) put("folder", folder) }.toString())
        val id = (json.parseToJsonElement(r) as JsonObject)["id"]!!.let { (it as JsonPrimitive).content.toLong() }
        try { refresh() } catch (_: Exception) { }
        id
    }

    /** Titel, Farbe, Wichtig, Ordner ("" = oberste Ebene). */
    suspend fun setMeta(id: Long, meta: NoteMeta) = withContext(Dispatchers.IO) {
        api().putText(api().url("/api/notes/$id/meta"), json.encodeToString(meta))
        try { refresh() } catch (_: Exception) { }
    }

    /** "archive", "restore" oder "trash" - laesst sich zurueckholen. */
    suspend fun act(id: Long, action: String) = withContext(Dispatchers.IO) {
        api().postText(api().url("/api/notes/$id/$action"), null)
        try { refresh() } catch (_: Exception) { }
    }

    /** Endgueltig loeschen (nur aus dem Papierkorb). */
    suspend fun deleteForever(id: Long) = withContext(Dispatchers.IO) {
        api().delete(api().url("/api/notes/$id"))
        try { refresh() } catch (_: Exception) { }
    }

    /** Ein Bild oder Anhang der Notizen (Blobname wie im Abgleichordner). Blockiert - nicht im Hauptthread. */
    fun blob(name: String): ByteArray {
        blobs.get(name)?.let { return it }
        val (bytes, _) = api().getBytes(api().url("/api/notes/blob/" + java.net.URLEncoder.encode(name, "UTF-8").replace("+", "%20")))
        blobs.put(name, bytes)
        return bytes
    }

    suspend fun blobAsync(name: String): ByteArray = withContext(Dispatchers.IO) { blob(name) }

    /** Ein Bild fuer den Editor ablegen (Foto, Zeichnung); der Server vergibt den Blobnamen wie der Abgleich. */
    suspend fun uploadImage(bytes: ByteArray, mime: String): String = withContext(Dispatchers.IO) {
        val ext = if (mime.contains("jpeg")) "jpg" else mime.substringAfter('/', "png")
        val r = api().postMultipart(api().url("/api/notes/images"), "{}", listOf(Triple("bild.$ext", mime, bytes)))
        val name = ((json.parseToJsonElement(r) as JsonObject)["name"] as JsonPrimitive).content
        blobs.put(name, bytes)
        name
    }
}
