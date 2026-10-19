package at.dimes.dmoffice.mail

import android.content.Context
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.api.Draft
import at.dimes.dmoffice.api.FlagsBody
import at.dimes.dmoffice.api.Folder
import at.dimes.dmoffice.api.MailAccount
import at.dimes.dmoffice.api.MailItem
import at.dimes.dmoffice.api.MailStatus
import at.dimes.dmoffice.api.Message
import at.dimes.dmoffice.api.MessagesPage
import at.dimes.dmoffice.api.MoveBody
import at.dimes.dmoffice.api.UidsBody
import at.dimes.dmoffice.data.AppDb
import at.dimes.dmoffice.data.MailBodyEntity
import at.dimes.dmoffice.data.MailFolderEntity
import at.dimes.dmoffice.data.MailItemEntity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import java.time.Instant

/**
 * Mail ueber die Web-API: Liste und gelesene Mails werden lokal gecacht (offline lesbar),
 * Aktionen (Flags, Loeschen, Senden) gehen direkt an den Server, der sie an IMAP/SMTP weitergibt.
 */
class MailRepo(private val context: Context, private val db: AppDb) {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
    private fun api(): ApiClient = App.get(context).api ?: throw ApiException(401, "no account")

    private val state = context.getSharedPreferences("mailstate", Context.MODE_PRIVATE)
    /** Ob ein Mailkonto eingerichtet ist - ohne Konto blendet die App den Mail-Reiter und die Mail-Optionen aus. */
    val hasAccounts = kotlinx.coroutines.flow.MutableStateFlow(state.getBoolean("has", true))

    suspend fun accounts(): List<MailAccount> = withContext(Dispatchers.IO) {
        val list: List<MailAccount> = api().get(api().url("/api/mail/accounts"))
        hasAccounts.value = list.isNotEmpty()
        state.edit().putBoolean("has", list.isNotEmpty()).apply()
        list
    }
    suspend fun status(): MailStatus = withContext(Dispatchers.IO) { api().get(api().url("/api/mail/status")) }

    fun folders(account: String): Flow<List<Folder>> = db.mail().folders(account).map { list ->
        list.map { Folder(it.path, it.name, "/", it.special, true, it.unread, it.total) }
    }

    suspend fun refreshFolders(account: String) = withContext(Dispatchers.IO) {
        val list: List<Folder> = api().get(api().url("/api/mail/$account/folders"))
        val order = listOf("inbox", "flagged", "drafts", "sent", "archive", "junk", "trash")
        val entities = list.filter { it.selectable }.mapIndexed { i, f ->
            val o = order.indexOf(f.special ?: "").let { if (it < 0) 100 + i else it }
            MailFolderEntity(account, f.path, f.name, f.special, f.unread, f.total, o)
        }
        db.mail().clearFolders(account)
        db.mail().upsertFolders(entities)
    }

    fun items(account: String, folder: String, limit: Int = 300): Flow<List<MailItem>> =
        db.mail().items(account, folder, limit).map { list -> list.map { decode(it) } }

    private fun decode(e: MailItemEntity): MailItem = json.decodeFromString<MailItem>(e.json).copy(seen = e.seen, flagged = e.flagged)

    /** Seite vom Server holen; Seite 0 ersetzt den Cache des Ordners. Gibt zurueck, ob es mehr gibt. */
    suspend fun refresh(account: String, folder: String, page: Int = 0, size: Int = 100): Boolean = withContext(Dispatchers.IO) {
        val p: MessagesPage = api().get(api().url("/api/mail/$account/messages", "folder" to folder, "page" to page, "size" to size))
        val entities = p.items.map { MailItemEntity(account, folder, it.uid, parseDate(it.date), it.seen, it.flagged, json.encodeToString(it)) }
        if (page == 0) db.mail().clearItems(account, folder)
        db.mail().upsertItems(entities)
        (page + 1) * size < p.total
    }

    private fun parseDate(s: String): Long = try { Instant.parse(s).toEpochMilli() } catch (_: Exception) {
        try { java.time.OffsetDateTime.parse(s).toInstant().toEpochMilli() } catch (_: Exception) { 0 }
    }

    fun observeItem(account: String, folder: String, uid: Long): Flow<MailItem?> = db.mail().item(account, folder, uid).map { it?.let { e -> decode(e) } }

    /** Mail laden - aus dem Cache, sonst vom Server (dort ggf. erst von IMAP geholt). */
    suspend fun message(account: String, folder: String, uid: Long, images: Boolean = false, force: Boolean = false): Message = withContext(Dispatchers.IO) {
        if (!force && !images) db.mail().body(account, folder, uid)?.let { return@withContext json.decodeFromString<Message>(it.json) }
        val m: Message = api().get(api().url("/api/mail/$account/message", "folder" to folder, "uid" to uid, "images" to if (images) "true" else null))
        if (!images) db.mail().upsertBody(MailBodyEntity(account, folder, uid, json.encodeToString(m), System.currentTimeMillis()))
        db.mail().pruneBodies(System.currentTimeMillis() - 30L * 86400_000)
        m
    }

    suspend fun setSeen(account: String, folder: String, uids: List<Long>, seen: Boolean) = withContext(Dispatchers.IO) {
        db.mail().setSeen(account, folder, uids, seen)
        try { api().post(api().url("/api/mail/$account/flags"), FlagsBody(folder, uids, seen = seen)) } catch (e: Exception) { db.mail().setSeen(account, folder, uids, !seen); throw e }
    }

    suspend fun setFlagged(account: String, folder: String, uids: List<Long>, flagged: Boolean) = withContext(Dispatchers.IO) {
        db.mail().setFlagged(account, folder, uids, flagged)
        try { api().post(api().url("/api/mail/$account/flags"), FlagsBody(folder, uids, flagged = flagged)) } catch (e: Exception) { db.mail().setFlagged(account, folder, uids, !flagged); throw e }
    }

    suspend fun delete(account: String, folder: String, uids: List<Long>) = withContext(Dispatchers.IO) {
        api().post(api().url("/api/mail/$account/delete"), UidsBody(folder, uids))
        db.mail().deleteItems(account, folder, uids)
    }

    suspend fun move(account: String, folder: String, uids: List<Long>, target: String) = withContext(Dispatchers.IO) {
        api().post(api().url("/api/mail/$account/move"), MoveBody(folder, uids, target))
        db.mail().deleteItems(account, folder, uids)
    }

    /** Senden ueber den Server (SMTP des Kontos, Kopie in Gesendet, Antwort-Markierung). */
    suspend fun send(account: String, draft: Draft, files: List<Triple<String, String, ByteArray>> = emptyList()): String? = withContext(Dispatchers.IO) {
        val r = api().postMultipart(api().url("/api/mail/$account/send"), json.encodeToString(draft), files)
        try { json.parseToJsonElement(r).let { (it as? kotlinx.serialization.json.JsonObject)?.get("warning")?.let { w -> (w as kotlinx.serialization.json.JsonPrimitive).content } } } catch (_: Exception) { null }
    }

    suspend fun attachment(account: String, folder: String, uid: Long, index: Int): Pair<ByteArray, String?> = withContext(Dispatchers.IO) {
        api().getBytes(api().url("/api/mail/$account/part", "folder" to folder, "uid" to uid, "index" to index, "download" to "true"))
    }

    private fun body(folder: String, uids: List<Long>, vararg extra: Pair<String, Any?>) = kotlinx.serialization.json.buildJsonObject {
        put("folder", kotlinx.serialization.json.JsonPrimitive(folder))
        put("uids", kotlinx.serialization.json.JsonArray(uids.map { kotlinx.serialization.json.JsonPrimitive(it) }))
        for ((k, v) in extra) when (v) {
            null -> put(k, kotlinx.serialization.json.JsonNull)
            is Boolean -> put(k, kotlinx.serialization.json.JsonPrimitive(v))
            else -> put(k, kotlinx.serialization.json.JsonPrimitive(v.toString()))
        }
    }.toString()

    /** Als Junk melden (lernt und verschiebt) bzw. "Kein Junk" (lernt als gut, aus Junk zurueck in den Posteingang). */
    suspend fun junk(account: String, folder: String, uids: List<Long>, spam: Boolean) = withContext(Dispatchers.IO) {
        api().postText(api().url("/api/mail/$account/" + if (spam) "junk" else "notjunk"), body(folder, uids))
        if (spam || db.mail().folders(account).first().firstOrNull { it.path == folder }?.special == "junk")
            db.mail().deleteItems(account, folder, uids)
    }

    /** In den Archiv-Ordner des Kontos; ohne Archiv-Ordner false. */
    suspend fun archive(account: String, folder: String, uids: List<Long>): Boolean = withContext(Dispatchers.IO) {
        val target = db.mail().folders(account).first().firstOrNull { it.special == "archive" }?.path ?: return@withContext false
        move(account, folder, uids, target)
        true
    }

    /** Kennzeichnen mit Faelligkeit (yyyy-MM-dd oder null = ohne Datum); done = erledigt (Kennzeichnung weg). */
    suspend fun followUp(account: String, folder: String, uids: List<Long>, due: String?, done: Boolean = false) = withContext(Dispatchers.IO) {
        api().postText(api().url("/api/mail/$account/followup"), body(folder, uids, "due" to due, "done" to done))
        db.mail().setFlagged(account, folder, uids, !done)
    }

    suspend fun followUps(): List<at.dimes.dmoffice.api.FollowUp> = withContext(Dispatchers.IO) { api().get(api().url("/api/mail/followups")) }

    /** Farbige Kategorien aus den Einstellungen (wie im Web). */
    val categories = kotlinx.coroutines.flow.MutableStateFlow<List<at.dimes.dmoffice.api.MailCategory>>(emptyList())
    suspend fun loadCategories() = withContext(Dispatchers.IO) {
        try { categories.value = api().get(api().url("/api/settings/categories")) } catch (_: Exception) { }
    }

    /** Kategorie setzen/entfernen; der lokale Stand der Mail bekommt das Schlagwort gleich mit. */
    suspend fun setCategory(account: String, folder: String, uids: List<Long>, key: String, on: Boolean) = withContext(Dispatchers.IO) {
        api().postText(api().url("/api/mail/$account/category"), body(folder, uids, "key" to key, "on" to on))
        for (uid in uids) {
            val e = db.mail().item(account, folder, uid).first() ?: continue
            val m = json.decodeFromString<MailItem>(e.json)
            val kw = if (on) (m.keywords + key).distinct() else m.keywords - key
            db.mail().upsertItems(listOf(e.copy(json = json.encodeToString(m.copy(keywords = kw)))))
        }
    }

    /** Wiedervorlage: Mail verschwindet bis [untilIso] und kommt dann zurueck in den Posteingang. */
    suspend fun snooze(account: String, folder: String, uids: List<Long>, untilIso: String) = withContext(Dispatchers.IO) {
        api().postText(api().url("/api/mail/$account/snooze"), body(folder, uids, "until" to untilIso))
        db.mail().deleteItems(account, folder, uids)
    }

    suspend fun unsnooze(account: String, folder: String, uids: List<Long>) = withContext(Dispatchers.IO) {
        api().postText(api().url("/api/mail/$account/unsnooze"), body(folder, uids))
        db.mail().deleteItems(account, folder, uids)
    }

    /** Vom Newsletter abmelden: One-Click oder Mail erledigt der Server (true, null); sonst kommt der Link zum Oeffnen zurueck. */
    suspend fun unsubscribe(account: String, folder: String, uid: Long): Pair<Boolean, String?> = withContext(Dispatchers.IO) {
        val r = json.parseToJsonElement(api().postText(api().url("/api/mail/$account/unsubscribe"),
            kotlinx.serialization.json.buildJsonObject { put("folder", kotlinx.serialization.json.JsonPrimitive(folder)); put("uid", kotlinx.serialization.json.JsonPrimitive(uid)) }.toString()))
            as kotlinx.serialization.json.JsonObject
        val done = (r["done"] as? kotlinx.serialization.json.JsonPrimitive)?.content == "true"
        done to (r["url"] as? kotlinx.serialization.json.JsonPrimitive)?.takeIf { it.isString }?.content
    }

    /** Prio-Posteingang: Stand und Ungelesene je Reiter; ordnet dabei noch nicht Eingeordnetes ein. */
    suspend fun focusInfo(account: String, folder: String): at.dimes.dmoffice.api.FocusInfo = withContext(Dispatchers.IO) {
        api().get(api().url("/api/mail/$account/focus", "folder" to folder))
    }

    /** Nach Relevant (true) bzw. Sonstige verschieben; always: Absender immer dorthin. Lernt auf dem Server. */
    suspend fun moveFocus(account: String, folder: String, uids: List<Long>, focused: Boolean, always: Boolean) = withContext(Dispatchers.IO) {
        api().postText(api().url("/api/mail/$account/focus"), body(folder, uids, "focused" to focused, "always" to always))
        for (uid in uids) {
            val e = db.mail().item(account, folder, uid).first() ?: continue
            val m = json.decodeFromString<MailItem>(e.json)
            db.mail().upsertItems(listOf(e.copy(json = json.encodeToString(m.copy(focus = focused)))))
        }
    }

    suspend fun sync() = withContext(Dispatchers.IO) { try { api().postText(api().url("/api/mail/sync"), null) } catch (_: Exception) { } }
}
