package at.dimes.dmoffice.ui.dms

import android.content.Context
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody
import okhttp3.RequestBody.Companion.asRequestBody

/*
 * DMS in der App (docs/dms-konzept.md, Stufe 4): Typen und Aufrufe von /api/dms. Wie im Web greift DMS nur über die
 * eigene API zu; aus dem Rest der App kommen nur der API-Client und allgemeine Bausteine.
 */

@Serializable data class DmsTag(val id: String, val auto: Boolean = false)

@Serializable data class DmsDoc(
    val id: Long, val fileId: Long = 0, val path: String? = null, val state: String = "done", val title: String = "", val docDate: String? = null,
    val imported: String = "", val correspondent: String? = null, val doctype: String? = null, val tags: List<DmsTag> = emptyList(),
    val inbox: Boolean = false, val byAi: Boolean = false, val error: String? = null, val originalName: String = "", val mime: String? = null,
    val size: Long = 0, val hasOriginal: Boolean = false, val archive: Boolean = false, val profile: String? = null, val source: String = "",
    val sender: String? = null, val autoCorrespondent: Boolean = false, val autoDoctype: Boolean = false,
    /** Externer Ordner als Speicherort; path ist dann der Pfad darin. */
    val mount: String? = null,
    /** Betrag des Dokuments in Cent (von Hand eingetragen). */
    val amount: Long? = null,
    val fixed: Boolean = false,
)

/** Buchung in Finanzen zu einem Dokument (GET/PUT /api/finance/dms/{id}). Der Betrag gehört der Buchung, nicht dem Dokument. */
@Serializable data class DmsBooking(val entry: Long, val kind: String, val amountCents: Long, val date: String = "", val unassigned: Boolean = false,
    val closed: Boolean = false, val invoice: Boolean = false)
@Serializable data class DmsBookingResult(val booking: DmsBooking? = null)
@Serializable data class DmsBookingBody(val kind: String?, val amountCents: Long?)

@Serializable data class DmsEntry(val id: String, val kind: String, val parent: String? = null, val name: String, val color: String? = null, val count: Int = 0)

@Serializable data class DmsProfile(val id: String, val name: String, val target: String = "files", val folder: String = "", val default: Boolean = false)

@Serializable data class DmsState(
    val ocr: Boolean = false, val inbox: Int = 0, val queued: Int = 0, val tresor: Boolean = false, val tresorOpen: Boolean = false, val tresorInbox: Int = 0,
    val paused: Boolean = false, val tresorQueued: Int = 0,
)

@Serializable data class DmsNode(val level: String, val key: String? = null, val name: String, val count: Int = 0, val children: List<DmsNode> = emptyList())

/** Ein im Text gefundenes Datum (GET /api/dms/docs/{id}/dates). */
@Serializable data class DmsDateCandidate(val date: String, val count: Int = 1, val context: String = "", val labeled: Boolean = false)

@Serializable private data class DocsPage(val items: List<DmsDoc> = emptyList(), val total: Int = 0)

/** Filter der Dokumentliste (wie /api/dms/docs). */
data class DmsFilter(
    val q: String? = null, val inbox: Boolean? = null, val correspondent: String? = null, val doctype: String? = null, val tags: List<String> = emptyList(),
    val docFrom: String? = null, val docTo: String? = null, val importedFrom: String? = null, val importedTo: String? = null, val profile: String? = null,
    val noCorrespondent: Boolean? = null, val noDoctype: Boolean? = null, val noTags: Boolean? = null,
    /** Warteschlange: noch nicht verarbeitet. */
    val queued: Boolean? = null,
)

/** Angaben ändern (PUT /api/dms/docs/{id}); "" bei Korrespondent und Dokumenttyp entfernt die Angabe. */
@Serializable data class DmsChange(
    val title: String? = null, val docDate: String? = null, val correspondent: String? = null, val doctype: String? = null,
    val tags: List<String>? = null, val confirm: Boolean? = null,
    /** Betrag in Cent als Text; "" entfernt ihn. */
    val amount: String? = null,
)

/** Ein virtueller Baum: Name und Ebenen (Vorgaben wie im Web). */
data class DmsTree(val name: Int, val levels: List<String>)

val dmsTrees = listOf(
    DmsTree(at.dimes.dmoffice.R.string.dms_correspondent, listOf("correspondent", "doc_year")),
    DmsTree(at.dimes.dmoffice.R.string.dms_doctype, listOf("doctype", "doc_year")),
    DmsTree(at.dimes.dmoffice.R.string.dms_tags, listOf("tag")),
    DmsTree(at.dimes.dmoffice.R.string.dms_doc_date, listOf("doc_year", "doc_month")),
    DmsTree(at.dimes.dmoffice.R.string.dms_import_date, listOf("import_year", "import_month")),
)

/** Filter für einen Ordner eines virtuellen Baums samt der Ordner darüber (wie filterOf im Web). */
fun filterOf(path: List<DmsNode>): DmsFilter {
    var f = DmsFilter()
    fun range(key: String): Pair<String, String> =
        if (key.length == 4) "$key-01-01" to "$key-12-31"
        else java.time.YearMonth.parse(key).let { "$key-01" to it.atEndOfMonth().toString() }
    for (n in path) {
        val k = n.key
        f = when (n.level) {
            "correspondent" -> if (k != null) f.copy(correspondent = k) else f.copy(noCorrespondent = true)
            "doctype" -> if (k != null) f.copy(doctype = k) else f.copy(noDoctype = true)
            "tag" -> if (k != null) f.copy(tags = f.tags + k) else f.copy(noTags = true)
            "profile" -> if (k != null) f.copy(profile = k) else f
            "doc_year", "doc_month" -> if (k != null) range(k).let { (a, b) -> f.copy(docFrom = a, docTo = b) } else f
            "import_year", "import_month" -> if (k != null) range(k).let { (a, b) -> f.copy(importedFrom = a, importedTo = b) } else f
            else -> f
        }
    }
    return f
}

/** Name mit Oberbegriffen, etwa „20_Bürokratie › 21_Finanzamt“. */
fun fullName(all: List<DmsEntry>, id: String?): String {
    if (id == null) return ""
    val byId = all.associateBy { it.id }
    val parts = mutableListOf<String>()
    var e = byId[id]
    while (e != null && parts.size < 20) { parts.add(0, e.name); e = e.parent?.let { byId[it] } }
    return parts.joinToString(" › ")
}

/** Einträge einer Liste in Baumreihenfolge mit Tiefe (für Auswahllisten). */
fun ordered(all: List<DmsEntry>, kind: String): List<Pair<DmsEntry, Int>> {
    val list = all.filter { it.kind == kind }
    val out = mutableListOf<Pair<DmsEntry, Int>>()
    fun walk(parent: String?, depth: Int) {
        for (e in list.filter { it.parent == parent }.sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.name })) {
            out.add(e to depth)
            if (depth < 20) walk(e.id, depth + 1)
        }
    }
    walk(null, 0)
    return out
}

class DmsRepo(private val ctx: Context) {
    private companion object {
        /** Vorschaubilder der Kacheln (etwa 40 Seiten zu 360 px) */
        val thumbs = android.util.LruCache<String, android.graphics.Bitmap>(40)
    }

    private fun api(): ApiClient = App.get(ctx).api ?: throw ApiException(401, "no account")
    private fun area(tresor: Boolean): String? = if (tresor) "tresor" else null

    suspend fun state(): DmsState = withContext(Dispatchers.IO) { api().let { it.get<DmsState>(it.url("/api/dms/state")) } }

    /** Warteschlange anhalten bzw. fortsetzen. */
    suspend fun pause(paused: Boolean) = withContext(Dispatchers.IO) {
        val a = api()
        a.postText(a.url("/api/dms/queue/pause"), a.json.encodeToString(JsonObject.serializer(), a.obj("paused" to kotlinx.serialization.json.JsonPrimitive(paused))))
    }

    /** Eintrag anlegen (parent: übergeordneter Eintrag oder null). */
    suspend fun createEntry(kind: String, name: String, parent: String?) = withContext(Dispatchers.IO) {
        val a = api()
        a.postText(a.url("/api/dms/lists"), a.json.encodeToString(JsonObject.serializer(), a.obj("kind" to JsonPrimitive(kind), "name" to JsonPrimitive(name),
            "parent" to JsonPrimitive(parent ?: ""))))
    }

    /** Eintrag umbenennen bzw. verschieben (parent "" = oberste Ebene); null bleibt, wie es ist. */
    suspend fun updateEntry(id: String, name: String? = null, parent: String? = null) = withContext(Dispatchers.IO) {
        val a = api()
        val body = listOfNotNull(name?.let { "name" to JsonPrimitive(it) }, parent?.let { "parent" to JsonPrimitive(it) }).toTypedArray()
        a.putText(a.url("/api/dms/lists/$id"), a.json.encodeToString(JsonObject.serializer(), a.obj(*body)))
    }

    suspend fun lists(): List<DmsEntry> = withContext(Dispatchers.IO) { api().let { it.get<List<DmsEntry>>(it.url("/api/dms/lists")) } }

    suspend fun profiles(): List<DmsProfile> = withContext(Dispatchers.IO) { api().let { it.get<List<DmsProfile>>(it.url("/api/dms/profiles")) } }

    suspend fun docs(f: DmsFilter, tresor: Boolean, limit: Int = 200): Pair<List<DmsDoc>, Int> = withContext(Dispatchers.IO) {
        val a = api()
        val p = a.get<DocsPage>(a.url("/api/dms/docs", "q" to f.q?.takeIf { it.isNotBlank() }, "inbox" to f.inbox, "correspondent" to f.correspondent,
            "doctype" to f.doctype, "tags" to f.tags.takeIf { it.isNotEmpty() }?.joinToString(","), "docFrom" to f.docFrom, "docTo" to f.docTo,
            "importedFrom" to f.importedFrom, "importedTo" to f.importedTo, "profile" to f.profile, "noCorrespondent" to f.noCorrespondent,
            "noDoctype" to f.noDoctype, "noTags" to f.noTags, "queued" to f.queued, "area" to area(tresor), "limit" to limit))
        p.items to p.total
    }

    /** Erste Seite einer PDF als Bild (vom Server gerendert), für die Kacheln; null, wenn es keins gibt. Kurz im Speicher gehalten. */
    suspend fun thumb(d: DmsDoc, tresor: Boolean, width: Int): android.graphics.Bitmap? = withContext(Dispatchers.IO) {
        val key = "${d.id}:${d.path}:$width:$tresor"
        thumbs.get(key)?.let { return@withContext it }
        val bmp = try {
            val a = api()
            val (bytes, _) = a.getBytes(a.url("/api/dms/docs/${d.id}/page/0", "width" to width, "area" to area(tresor)))
            android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        } catch (_: Exception) { null }
        bmp?.also { thumbs.put(key, it) }
    }

    suspend fun doc(id: Long, tresor: Boolean): DmsDoc = withContext(Dispatchers.IO) { api().let { it.get<DmsDoc>(it.url("/api/dms/docs/$id", "area" to area(tresor))) } }

    /** Plausible Daten aus dem Text, beste zuerst. */
    suspend fun dates(id: Long, tresor: Boolean): List<DmsDateCandidate> = withContext(Dispatchers.IO) {
        api().let { it.get<List<DmsDateCandidate>>(it.url("/api/dms/docs/$id/dates", "area" to area(tresor))) }
    }

    suspend fun text(id: Long, tresor: Boolean): String = withContext(Dispatchers.IO) {
        val a = api()
        a.json.parseToJsonElement(a.getText(a.url("/api/dms/docs/$id/text", "area" to area(tresor)))).jsonObject["text"]?.jsonPrimitive?.content ?: ""
    }

    suspend fun change(id: Long, tresor: Boolean, c: DmsChange): DmsDoc = withContext(Dispatchers.IO) {
        val a = api()
        a.json.decodeFromString(a.putText(a.url("/api/dms/docs/$id", "area" to area(tresor)), a.json.encodeToString(DmsChange.serializer(), c)))
    }

    suspend fun booking(id: Long, tresor: Boolean): DmsBooking? = withContext(Dispatchers.IO) {
        val a = api()
        a.json.decodeFromString(DmsBookingResult.serializer(), a.getText(a.url("/api/finance/dms/$id", "area" to area(tresor)))).booking
    }

    /** Als Einnahme oder Ausgabe buchen (kind null: nicht mehr); amountCents null: Betrag des Dokuments bzw. bisheriger. */
    suspend fun book(id: Long, tresor: Boolean, kind: String?, amountCents: Long?): DmsBooking? = withContext(Dispatchers.IO) {
        val a = api()
        a.json.decodeFromString(DmsBookingResult.serializer(), a.putText(a.url("/api/finance/dms/$id", "area" to area(tresor)),
            a.json.encodeToString(DmsBookingBody.serializer(), DmsBookingBody(kind, amountCents)))).booking
    }

    suspend fun delete(ids: List<Long>, tresor: Boolean) = withContext(Dispatchers.IO) {
        val a = api()
        a.postText(a.url("/api/dms/docs/delete", "area" to area(tresor)), a.json.encodeToString(JsonObject.serializer(), a.obj("ids" to ids.toJson())))
    }

    /** In den Tresor verschieben (to = "tresor") bzw. aus dem Tresor holen (to = "files"). */
    suspend fun moveArea(ids: List<Long>, to: String): Int = withContext(Dispatchers.IO) {
        val a = api()
        val r = a.postText(a.url("/api/dms/docs/move-area"), a.json.encodeToString(JsonObject.serializer(),
            a.obj("ids" to ids.toJson(), "to" to kotlinx.serialization.json.JsonPrimitive(to))))
        a.json.parseToJsonElement(r).jsonObject["moved"]?.jsonPrimitive?.content?.toIntOrNull() ?: 0
    }

    suspend fun tree(levels: List<String>, tresor: Boolean): List<DmsNode> = withContext(Dispatchers.IO) {
        api().let { it.get<List<DmsNode>>(it.url("/api/dms/tree", "levels" to levels.joinToString(","), "area" to area(tresor))) }
    }

    /**
     * Datei hochladen (POST /api/dms/upload): [profile] null = Standardprofil, im Tresor das Profil mit Ziel Tresor.
     * Gestreamt am Umschlag vorbei (wie große Dateien der Ablage); Antwort: je Datei Kennung, doppelt, im Tresor abgelegt.
     */
    suspend fun upload(file: java.io.File, name: String, mime: String?, profile: String?, tresor: Boolean): UploadResult = withContext(Dispatchers.IO) {
        val a = api()
        val body = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("file", name, file.asRequestBody((mime ?: "application/octet-stream").toMediaTypeOrNull())).build()
        val url = a.url("/api/dms/upload", "profile" to profile, "area" to area(tresor).takeIf { profile == null })
        // Server ohne Tickets: im Umschlag (dann ganz im Speicher)
        val r = try { a.sendDirect("POST", url, body) } catch (_: ApiClient.TicketsUnsupported) {
            a.postMultipart(url, "{}", listOf(Triple(name, mime ?: "application/octet-stream", file.readBytes())))
        }
        val f = a.json.parseToJsonElement(r).jsonObject["files"]?.jsonArray?.firstOrNull()?.jsonObject
        UploadResult(f?.get("duplicate")?.jsonPrimitive?.content == "true", f?.get("dropped")?.jsonPrimitive?.content == "true")
    }

    /** Dateien aus der eigenen Ablage aufnehmen (POST /api/dms/import-files, wie „Ins DMS aufnehmen“ im Web). Anzahl aufgenommen. */
    suspend fun importFiles(paths: List<String>, profile: String?): Int = withContext(Dispatchers.IO) {
        val a = api()
        val body = a.obj("paths" to kotlinx.serialization.json.JsonArray(paths.map { JsonPrimitive(it) }), "profile" to (profile?.let { JsonPrimitive(it) } ?: kotlinx.serialization.json.JsonNull))
        val r = a.postText(a.url("/api/dms/import-files"), a.json.encodeToString(JsonObject.serializer(), body))
        a.json.parseToJsonElement(r).let { (it as? JsonObject)?.get("files") ?: it }.let { (it as? kotlinx.serialization.json.JsonArray)?.count { x -> (x as? JsonObject)?.get("duplicate") == null } ?: 0 }
    }

    /** Inhalt zum Ansehen in [dest]: Datei der Ablage, im Tresor bzw. im externen Ordner. */
    suspend fun download(d: DmsDoc, tresor: Boolean, dest: java.io.File) = withContext(Dispatchers.IO) {
        val a = api()
        if (d.mount != null) a.download(a.url("/api/dms/docs/${d.id}/content"), dest)
        else if (tresor) a.download(a.url("/api/tresor/files/${d.fileId}/content", "inline" to true), dest)
        else a.downloadFile(d.path ?: throw ApiException(404, "no file"), dest, big = d.size > 4_000_000, envelopeOk = true, inline = true)
    }

    /** „Ins DMS ablegen“ aus einer Mail (POST /api/mail/{konto}/to-dms). */
    suspend fun fromMail(account: String, folder: String, uid: Long, whole: Boolean, attachments: Boolean, parts: List<Int>?, profile: String?): Int =
        withContext(Dispatchers.IO) {
            val a = api()
            val body = buildMap<String, kotlinx.serialization.json.JsonElement> {
                put("folder", kotlinx.serialization.json.JsonPrimitive(folder)); put("uid", kotlinx.serialization.json.JsonPrimitive(uid))
                put("whole", kotlinx.serialization.json.JsonPrimitive(whole)); put("attachments", kotlinx.serialization.json.JsonPrimitive(attachments))
                if (parts != null) put("parts", parts.toJson())
                if (profile != null) put("profile", kotlinx.serialization.json.JsonPrimitive(profile))
            }
            val r = a.postText(a.url("/api/mail/" + at.dimes.dmoffice.data.Account.encode(account) + "/to-dms"),
                a.json.encodeToString(JsonObject.serializer(), JsonObject(body)))
            try { a.json.parseToJsonElement(r).jsonObject["files"]?.jsonArray?.size ?: 0 } catch (_: Exception) { 0 }
        }

    data class UploadResult(val duplicate: Boolean, val dropped: Boolean)

    private fun List<Number>.toJson() = kotlinx.serialization.json.JsonArray(map { kotlinx.serialization.json.JsonPrimitive(it) })
}
