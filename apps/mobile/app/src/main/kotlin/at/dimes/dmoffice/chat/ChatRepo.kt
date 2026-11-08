package at.dimes.dmoffice.chat

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.files.FilesRepo
import at.dimes.dmoffice.upload.ChunkedUpload
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import java.io.File
import java.time.Instant

// Chat (Version 2) ueber die Web-API /api/chat - dieselben Daten wie im Browser. Neues kommt per Long-Poll
// (/api/chat/wait), solange ein Chat-Bildschirm offen ist, und per FCM-Signal t=chat im Hintergrund.

/** mode: show (Zeigen) | send (Senden); state: ok | expired | revoked; forwarded: weitergeleitet */
@Serializable data class ChatRef(val type: String, val name: String, val mime: String? = null, val size: Long? = null, val owner: String = "",
                              val mode: String = "send", val expires: String? = null, val state: String = "ok", val forwarded: Boolean = false,
                              /** Standort: Breite, Laenge, Genauigkeit (Meter) - nur solange er gilt */
                              val lat: Double? = null, val lon: Double? = null, val accuracy: Double? = null,
                              /** Live-Teilen: bis wann (danach letzter Standort), Zeit der letzten Position */
                              val liveUntil: String? = null, val at: String? = null) {
    /** Kurzform fuer Listen und Benachrichtigungen */
    val preview: String get() = if (type == "location") "📍 $name" else name
    val isImage: Boolean get() = type == "file" && (mime ?: "").startsWith("image/") && mime != "image/svg+xml"
    val shown: Boolean get() = mode == "show"
    val usable: Boolean get() = state == "ok"
}
@Serializable data class ChatAction(val key: String, val label: String = "")
@Serializable data class ChatReaction(val emoji: String, val users: List<String> = emptyList())
@Serializable data class ChatMessage(
    val id: Long, val room: Long, val sender: String, val senderName: String = "", val created: String, val edited: String? = null,
    val deleted: Boolean = false, val kind: String = "text", val text: String? = null, val replyTo: Long? = null,
    val refs: List<ChatRef> = emptyList(), val reactions: List<ChatReaction> = emptyList(), val seq: Long = 0,
    /** @Erwaehnungen: wer gemeint ist (Benutzer-Kennungen) */
    val mentions: List<String> = emptyList(),
    /** Knoepfe unter Pukkis Antworten (Rueckgaengig, Auswahl) */
    val actions: List<ChatAction> = emptyList(),
    /** Pukkis Stimmung: Pose neben der Nachricht (frage, daumen, checkliste …), null: er erklaert ("zeigen") */
    val mood: String? = null,
) {
    val createdMs: Long get() = try { Instant.parse(created).toEpochMilli() } catch (_: Exception) { 0L }
    val isSystem: Boolean get() = kind == "system"
}
/** lastRead: gelesen bis (0, wenn das Mitglied keine Lesebestaetigungen sendet); delivered: zugestellt bis; receipts: sendet Lesebestaetigungen */
@Serializable data class ChatMember(val id: String, val name: String = "", val login: String? = null, val admin: Boolean = false, val lastRead: Long = 0,
    val delivered: Long = 0, val receipts: Boolean = true,
    /** Profilbild (Version, null: keins) und Akzentfarbe (#rrggbb) */
    val avatar: String? = null, val accent: String? = null)
@Serializable data class ChatRoom(
    val id: Long, val kind: String, val title: String = "", val created: String = "", val seq: Long = 0, val unread: Int = 0,
    val muted: Boolean = false, val admin: Boolean = false, val lastRead: Long = 0, val members: List<ChatMember> = emptyList(), val last: ChatMessage? = null,
    /** persoenliche Farbe (#RRGGBB), null: Akzentfarbe */
    val color: String? = null,
    /** ungelesene Nachrichten, die mich erwaehnen */
    val mentioned: Int = 0,
    /** Standort-Anfrage: darf ich den anderen fragen? (Recht + seine Einstellung, nur zu zweit) */
    val askLocation: Boolean = false,
) {
    val isGroup: Boolean get() = kind == "group"
    /** Pukki (Stufe 5): die Unterhaltung mit dem Assistenten */
    val isPukki: Boolean get() = kind == "direct" && members.any { it.id == "pukki" }
}
@Serializable data class ChatAlbumPhoto(val key: String, val name: String = "", val etag: String? = null, val ownerName: String? = null)
@Serializable data class ChatAlbum(val name: String = "", val items: List<ChatAlbumPhoto> = emptyList())
@Serializable data class ChatPerson(val id: String, val login: String = "", val displayName: String = "", val avatar: String? = null, val accent: String? = null)
/** quiet: Ruhezeit - null (alles melden), "mentions" (nur Erwaehnungen), "all" (nichts) */
@Serializable data class ChatStatus(val seq: Long = 0, val unread: Int = 0, val me: String? = null, val quiet: String? = null,
    /** eigenes Profilbild (Version) und eigene Akzentfarbe */
    val avatar: String? = null, val accent: String? = null,
    /** Figur, als die Pukki erscheint: panda, walross, baer, zauberer */
    val pukkiFigure: String = "panda")
@Serializable data class PukkiFigureBody(val figure: String)
/** Ergebnis des Wartens: neuer Stand und „denkt nach“ (Version und Unterhaltungen, in denen Pukki an einer Antwort arbeitet) */
@Serializable data class ChatWait(val seq: Long = 0, val busy: Long = 0, val busyRooms: List<Long> = emptyList())
@Serializable data class ChatChanges(val seq: Long, val rooms: List<ChatRoom> = emptyList(), val messages: List<ChatMessage> = emptyList(), val more: Boolean = false)
@Serializable data class ChatNote(val title: String = "", val html: String = "", val ownerName: String = "")
@Serializable data class ChatSaved(val type: String = "", val path: String? = null, val noteId: Long? = null)
/** Einstellungen -> Chat (wie im Web); Arten show|send, showFor forever|1y|6m|1m|1w|24h */
@Serializable data class ChatSettings(
    val deviceMode: String = "send", val filesMode: String = "show", val itemsMode: String = "show", val saveFolder: String = "Chat",
    val noteFolder: String? = null, val reshow: Boolean = false, val showFor: String = "forever", val locationRequests: Boolean = false,
    /** Pukki (Stufe 5): Tagesueberblick um pukkiTime, Erinnerungen an Termine */
    val pukkiDaily: Boolean = true, val pukkiTime: String = "07:00", val pukkiReminders: Boolean = true,
    /** Ruhezeiten (Chat-Komfort): von/bis Ortszeit, Erwaehnungen trotzdem melden */
    val quiet: Boolean = false, val quietFrom: String = "22:00", val quietTo: String = "07:00", val quietMentions: Boolean = false,
    /** Privatsphaere: anderen zeigen, ob ich gelesen habe */
    val readReceipts: Boolean = true,
)
@Serializable data class ChatRights(val expiring: Boolean = true, val locationRequests: Boolean = false)
@Serializable data class ChatSettingsResponse(val settings: ChatSettings = ChatSettings(), val rights: ChatRights = ChatRights())
/** Eintrag der Dateiablage (Auswahl im Chat) */
@Serializable data class ChatFileItem(val path: String, val name: String, val isDir: Boolean = false, val size: Long = 0, val mime: String? = null, val fileId: Long = 0,
                                   val shared: Boolean = false, val owner: String? = null)
@Serializable data class ChatFileList(val items: List<ChatFileItem> = emptyList())
@Serializable data class ChatNoteItem(val id: Long, val title: String = "", val snippet: String = "", val owner: String? = null)
@Serializable data class ChatNoteList(val items: List<ChatNoteItem> = emptyList())
@Serializable data class ChatNoteFolder(val syncId: String, val parent: String? = null, val name: String = "")
/** Eintrag in einem Ordner, der im Chat gezeigt/gesendet wurde (path relativ zum Ordner). */
@Serializable data class ChatFolderEntry(val path: String, val name: String, val isDir: Boolean = false, val size: Long = 0, val mime: String? = null, val etag: String? = null)
/** Eigene Sammlung (Kalender/Liste/Adressbuch) als Ziel fuer "Bei mir speichern". */
@Serializable data class ChatCollection(val id: Long, val name: String = "", val color: String? = null, val components: List<String> = emptyList(),
                                       val owner: String? = null, val subscription: kotlinx.serialization.json.JsonElement? = null, val uri: String = "")

@Serializable private data class SeqOnly(val seq: Long = 0)
@Serializable private data class NewRoom(val members: List<String>, val title: String? = null)
@Serializable private data class PostBody(val text: String?, val replyTo: Long? = null)
@Serializable private data class ComposedBody(val text: String?, val replyTo: Long? = null, val refs: List<RefInput> = emptyList())
/** Vorab hochgeladener Anhang: Datei in „Chat“ der eigenen Ablage, Art als Vorgabe „vom Gerät“. */
@Serializable data class ChatStaged(val fileId: Long, val mode: String = "send")

/** Ein gesammelter Anhang, bevor die Nachricht rausgeht (Eingabe: erst zusammenstellen, dann senden). */
sealed interface ChatPending {
    val name: String
    val image: Boolean
    /** Vom Handy (Kamera, Galerie des Handys, Datei): wird beim Senden hochgeladen. */
    data class Local(val file: File, override val name: String, val mime: String?) : ChatPending {
        override val image get() = (mime ?: "").startsWith("image/")
    }
    /** Liegt schon in dmOffice (Datei, Notiz, Foto der Galerie): die Nachricht zeigt nur darauf. */
    data class Ref(val ref: ChatOutRef, val mode: String, override val name: String, val item: at.dimes.dmoffice.files.FileItem? = null) : ChatPending {
        override val image get() = (item?.mime ?: "").startsWith("image/")
    }
    /** Foto aus einem Galerie-Ordner (externer Speicher): wird beim Senden in „Chat“ kopiert. */
    data class Gallery(val item: at.dimes.dmoffice.files.FileItem, val mode: String) : ChatPending {
        override val name get() = item.name
        override val image get() = true
    }
}
@Serializable private data class TextBody(val text: String)
@Serializable private data class EmojiBody(val emoji: String)
@Serializable private data class RateBody(val message: Long, val value: Int)
@Serializable private data class ReadBody(val upTo: Long)
@Serializable private data class MuteBody(val muted: Boolean)
@Serializable private data class ColorBody(val color: String)
@Serializable private data class RefPatch(val mode: String? = null, val revoke: Boolean? = null)
@Serializable private data class ForwardBody(val rooms: List<Long>, val mode: String? = null, val withText: Boolean = true)
@Serializable private data class SaveBody(val folder: String? = null)
@Serializable private data class FolderSaveBody(val path: String, val folder: String? = null)
@Serializable private data class RefInput(val type: String, val fileId: Long? = null, val noteId: Long? = null, val mode: String? = null,
                                         val collection: Long? = null, val item: String? = null)
/** Ein Verweis zum Schicken: Datei/Ordner (fileId), Notiz (noteId), Termin/Aufgabe/Kontakt (collection + item = uri). */
/** Gruppenraum (Stufe 4): Kalender, Liste, Ordner oder Notizordner; target = Sprungziel (Sammlung, Pfad, Notizordner). */
/** Suche im Chat (Chat-Komfort): Treffer mit Unterhaltung */
@Serializable data class ChatSearchHit(val room: Long, val roomTitle: String = "", val message: ChatMessage)
@Serializable data class ChatSpace(val kind: String, val name: String = "", val owner: String = "", val ownerName: String = "", val mine: Boolean = false, val target: String? = null)
@Serializable private data class NewSpaceBody(val kind: String)
@Serializable data class ChatOutRef(val type: String, val fileId: Long? = null, val noteId: Long? = null, val collection: Long? = null, val item: String? = null)
@Serializable private data class RefsBody(val refs: List<RefInput>)
/** Anmeldestand (/api/auth/state). */
@Serializable data class AuthUser(val id: String = "", val login: String = "", val displayName: String = "", val files: Boolean = true)
/** featuresOff: Release-Liste - in der Version des Servers nicht freigeschaltete Funktionen (data/Features.kt). */
@Serializable data class AuthState(val loggedIn: Boolean = false, val user: AuthUser? = null, val featuresOff: List<String> = emptyList())
@Serializable data class ListPerson(val id: String, val login: String = "", val displayName: String = "")
@Serializable private class Empty
@Serializable private data class LocationBody(val lat: Double, val lon: Double, val accuracy: Double? = null, val request: Long? = null, val minutes: Int? = null)
@Serializable private data class LocationUpdate(val lat: Double, val lon: Double, val accuracy: Double? = null)

class ChatRepo(private val app: App) {
    private fun api(): ApiClient = app.api ?: throw IllegalStateException("Kein Konto")
    private suspend fun <T> io(block: () -> T): T = withContext(Dispatchers.IO) { block() }

    /** Chat offline: zuletzt Geladenes je Konto */
    val cache = ChatCache(app)

    private val _unread = MutableStateFlow(0)
    /** Ungelesen ueber alle nicht stummen Unterhaltungen - fuer die Zahl am Reiter. */
    val unread: StateFlow<Int> = _unread

    /** Eigene Kennung (fuer "meine" Nachrichten); aus der Mitgliederliste einer Unterhaltung zu zweit nicht ablesbar. */
    @Volatile var me: String? = null
        private set

    private val _pukkiFigure = MutableStateFlow("panda")
    /** Figur, als die Pukki erscheint - kommt mit dem Status. */
    val pukkiFigure: StateFlow<String> = _pukkiFigure
    /** Status schon einmal geholt - dann ist auch pukkiFigure bekannt. */
    @Volatile var statusKnown = false
        private set

    suspend fun status(): ChatStatus = io { api().get<ChatStatus>(api().url("/api/chat/status")) }
        .also { _unread.value = it.unread; if (it.me != null) me = it.me; _pukkiFigure.value = it.pukkiFigure; statusKnown = true }

    /** Wartet bis zu 25 Sekunden auf Neues; zurueck: der neue Stand. */
    suspend fun wait(since: Long): Long = io { api().get<SeqOnly>(api().url("/api/chat/wait", "since" to since, "timeout" to 25)).seq }

    /** Wie wait, wacht aber auch auf, wenn sich „denkt nach“ ändert (busy: zuletzt gesehene Version). */
    suspend fun waitBusy(since: Long, busy: Long): ChatWait =
        io { api().get<ChatWait>(api().url("/api/chat/wait", "since" to since, "timeout" to 25, "busy" to busy)) }

    suspend fun changes(since: Long): ChatChanges = io { api().get<ChatChanges>(api().url("/api/chat/changes", "since" to since)) }

    suspend fun rooms(): List<ChatRoom> = io { api().get<List<ChatRoom>>(api().url("/api/chat/rooms")) }.also { rooms ->
        _unread.value = rooms.filter { !it.muted }.sumOf { it.unread }
        cache.saveRooms(rooms)
    }

    suspend fun room(id: Long): ChatRoom = io { api().get<ChatRoom>(api().url("/api/chat/rooms/$id")) }

    /** Suche im Chat: alle sichtbaren Nachrichten, alle Woerter muessen passen */
    suspend fun search(q: String): List<ChatSearchHit> = io { api().get<List<ChatSearchHit>>(api().url("/api/chat/search", "q" to q)) }

    suspend fun people(): List<ChatPerson> = io { api().get<List<ChatPerson>>(api().url("/api/chat/people")) }

    /** Eigene Kennung - liefert der Server mit dem Chat-Stand. */
    // Rueckfall fuer Server ohne me: der Anmeldename des Kontos steht in der Mitgliederliste jeder Unterhaltung
    suspend fun whoAmI(): String? = me ?: try { status().me } catch (_: Exception) { null } ?: try {
        val login = app.prefs.account.value?.login
        rooms().flatMap { it.members }.firstOrNull { it.login != null && it.login.equals(login, ignoreCase = true) }?.id?.also { me = it }
    } catch (_: Exception) { null }

    suspend fun open(members: List<String>, title: String?): ChatRoom =
        io { api().postFor<NewRoom, ChatRoom>(api().url("/api/chat/rooms"), NewRoom(members, title)) }

    suspend fun messages(room: Long, before: Long? = null, limit: Int = 50): List<ChatMessage> =
        io { api().get<List<ChatMessage>>(api().url("/api/chat/rooms/$room/messages", "before" to before, "limit" to limit)) }

    suspend fun send(room: Long, text: String, replyTo: Long?): ChatMessage =
        io { api().postFor<PostBody, ChatMessage>(api().url("/api/chat/rooms/$room/messages"), PostBody(text, replyTo)) }

    /** Foto, Video oder Datei vom Handy: liegt danach beim Absender in der Ablage unter "Chat" (gleiche Datei nur einmal).
     *  Gestreamt ohne Umschlag (nur TLS) - ein Video laege im Umschlag ganz im Speicher. */
    suspend fun upload(room: Long, name: String, mime: String?, file: File, text: String? = null): ChatMessage = io {
        val a = api()
        val answer = if (file.length() <= FilesRepo.ENVELOPE_MAX) a.postFile(a.url("/api/chat/rooms/$room/upload", "name" to name, "text" to text), mime, file)
        else {
            // Gross (Video): in Stuecken, ohne Groessengrenze
            val up = ChunkedUpload.upload(a, { file.inputStream() }, name, file.length(), mime)
            ChunkedUpload.finishToChat(a, up.id, room, text)
        }
        a.json.decodeFromString<ChatMessage>(answer)
    }

    /** Anhang vom Handy vorab hochladen (noch keine Nachricht) - gestreamt, große Dateien in Stücken. */
    suspend fun stage(room: Long, name: String, mime: String?, file: File): ChatStaged = io {
        val a = api()
        val answer = if (file.length() <= FilesRepo.ENVELOPE_MAX) a.postFile(a.url("/api/chat/rooms/$room/stage", "name" to name), mime, file)
        else {
            val up = ChunkedUpload.upload(a, { file.inputStream() }, name, file.length(), mime)
            a.postLong(a.url("/api/chat/uploads/${up.id}/stage", "room" to room))
        }
        a.json.decodeFromString<ChatStaged>(answer)
    }

    /** Eine Nachricht mit Text und allen gesammelten Verweisen (je mit eigener Art). */
    suspend fun sendComposed(room: Long, text: String?, replyTo: Long?, refs: List<Pair<ChatOutRef, String>>): ChatMessage = io {
        val body = ComposedBody(text, replyTo, refs.map { (r, mode) -> RefInput(r.type, fileId = r.fileId, noteId = r.noteId, mode = mode, collection = r.collection, item = r.item) })
        api().postFor<ComposedBody, ChatMessage>(api().url("/api/chat/rooms/$room/messages"), body)
    }

    suspend fun edit(id: Long, text: String): ChatMessage = io {
        val a = api(); a.json.decodeFromString<ChatMessage>(a.putText(a.url("/api/chat/messages/$id"), a.json.encodeToString(TextBody.serializer(), TextBody(text))))
    }

    suspend fun delete(id: Long) = io { api().delete(api().url("/api/chat/messages/$id")) }

    suspend fun react(id: Long, emoji: String): ChatMessage = io { api().postFor<EmojiBody, ChatMessage>(api().url("/api/chat/messages/$id/reactions"), EmojiBody(emoji)) }

    suspend fun markRead(room: Long, upTo: Long) {
        io { api().post(api().url("/api/chat/rooms/$room/read"), ReadBody(upTo)) }
        try { status() } catch (_: Exception) { }
    }

    suspend fun mute(room: Long, muted: Boolean) = io { api().putText(api().url("/api/chat/rooms/$room"), api().json.encodeToString(MuteBody.serializer(), MuteBody(muted))) }

    suspend fun leave(room: Long) = io { api().post(api().url("/api/chat/rooms/$room/leave"), Empty()) }

    /** Unterhaltung exportieren: ZIP (Verlauf als Text, erlaubte Anhaenge) in den Zwischenspeicher. */
    suspend fun export(room: Long): File = io {
        val f = File(app.cacheDir, "export/chat-$room.zip").apply { parentFile?.mkdirs(); delete() }
        api().download(api().url("/api/chat/rooms/$room/export"), f)
        f
    }

    /** Knopf unter Pukkis Antwort: der Server fuehrt aus, Pukkis Antwort kommt mit den Aenderungen. */
    /** Standort-Anfrage stellen (der Server prueft Recht und Einstellung des anderen). */
    suspend fun requestLocation(room: Long): ChatMessage = io { api().postFor<Empty, ChatMessage>(api().url("/api/chat/rooms/$room/location-request"), Empty()) }

    /** Eigenen Standort schicken, als Antwort auf [request]. */
    suspend fun sendLocation(room: Long, lat: Double, lon: Double, accuracy: Double?, request: Long?, minutes: Int? = null): ChatMessage = io {
        api().postFor<LocationBody, ChatMessage>(api().url("/api/chat/rooms/$room/location"), LocationBody(lat, lon, accuracy, request, minutes))
    }

    /** Live-Teilen: neue Position in derselben Nachricht (der Server lehnt nach dem Ende ab). */
    suspend fun updateLocation(room: Long, message: Long, lat: Double, lon: Double, accuracy: Double?) = io {
        api().putText(api().url("/api/chat/rooms/$room/location/$message"), api().json.encodeToString(LocationUpdate.serializer(), LocationUpdate(lat, lon, accuracy)))
    }

    /** Live-Teilen vorzeitig beenden - die letzte Position bleibt stehen. */
    suspend fun stopLocation(room: Long, message: Long): ChatMessage = io {
        api().postFor<Empty, ChatMessage>(api().url("/api/chat/rooms/$room/location/$message/stop"), Empty())
    }

    // ---------------- Aufgaben zuweisen (Version 2) ----------------

    /** An wen man Aufgaben dieser Liste geben kann - [calendarHref] ist die CalDAV-Adresse der Liste. */
    suspend fun listPeople(calendarHref: String): List<ListPerson> = io {
        val uri = calendarHref.trimEnd('/').substringAfterLast('/')
        api().get<List<ListPerson>>(api().url("/api/pim/list-people", "uri" to uri))
    }

    suspend fun authState(): AuthState = io { api().get<AuthState>(api().url("/api/auth/state")) }

    suspend fun act(message: Long, key: String) = io { api().post(api().url("/api/chat/messages/$message/actions/$key"), Empty()) }

    /** Pukkis Antworten bewerten (Daumen wie im Web): 1 richtig verstanden, -1 falsch, 0 zurücknehmen. */
    suspend fun pukkiRatings(): Map<Long, Int> = io { api().get<Map<String, Int>>(api().url("/api/chat/pukki/ratings")).mapKeys { it.key.toLong() } }
    suspend fun ratePukki(message: Long, value: Int) = io { api().post(api().url("/api/chat/pukki/rate"), RateBody(message, value)) }

    // ---------------- Verweise ----------------

    private val thumbs = object : LruCache<String, Bitmap>(24 * 1024 * 1024) { override fun sizeOf(key: String, value: Bitmap) = value.byteCount }

    // ---------------- Profilbilder (Version 2) ----------------
    private val avatars = object : LruCache<String, Bitmap>(8 * 1024 * 1024) { override fun sizeOf(key: String, value: Bitmap) = value.byteCount }

    /** Profilbild eines Benutzers in dieser Version (die Version steht in Mitgliedern und Status). */
    suspend fun avatar(user: String, version: String): Bitmap? {
        val key = "$user/$version"
        avatars.get(key)?.let { return it }
        return try {
            io { api().getBytes(api().url("/api/chat/people/$user/avatar", "v" to version)).first }
                .let { android.graphics.BitmapFactory.decodeByteArray(it, 0, it.size) }?.also { avatars.put(key, it) }
        } catch (_: Exception) { null }
    }

    /** Eigenes Profilbild setzen (JPEG/PNG/WebP); zurück: neue Version. */
    suspend fun setAvatar(bytes: ByteArray, mime: String): String? = io {
        val r = api().putBytes(api().url("/api/chat/people/me/avatar"), mime, bytes)
        try { (api().json.parseToJsonElement(r) as? kotlinx.serialization.json.JsonObject)?.get("avatar")?.let { (it as kotlinx.serialization.json.JsonPrimitive).content } } catch (_: Exception) { null }
    }

    suspend fun removeAvatar() = io { api().delete(api().url("/api/chat/people/me/avatar")) }

    /** Pukki darf meine Mails lesen (eigener Schalter auf dem Server). */
    suspend fun pukkiMail(): Boolean = io {
        (api().json.parseToJsonElement(api().getText(api().url("/api/chat/pukki/mail"))) as kotlinx.serialization.json.JsonObject)["allowed"]
            ?.let { (it as kotlinx.serialization.json.JsonPrimitive).content == "true" } ?: false
    }

    suspend fun setPukkiMail(allowed: Boolean) = io { api().putText(api().url("/api/chat/pukki/mail"), "{\"allowed\":$allowed}") }

    /** Andere Figur für Pukki - gilt nur für einen selbst. */
    suspend fun setPukkiFigure(figure: String) {
        val before = _pukkiFigure.value
        _pukkiFigure.value = figure
        try { io { api().putText(api().url("/api/chat/pukki/figure"), api().json.encodeToString(PukkiFigureBody.serializer(), PukkiFigureBody(figure))) } }
        catch (e: Exception) { _pukkiFigure.value = before; throw e }
    }

    /** Vorschaubild eines Bildes in einer Nachricht (gezwischengespeichert); null: nicht mehr verfuegbar. */
    suspend fun thumb(message: Long, index: Int, size: Int = 640): Bitmap? {
        val key = "$message/$index/$size"
        thumbs.get(key)?.let { return it }
        return try {
            io {
                val a = api()
                val (bytes, _) = a.getBytes(a.url("/api/chat/messages/$message/refs/$index/thumb", "size" to size))
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            }?.also { thumbs.put(key, it) }
        } catch (_: Exception) { null }
    }

    /** Datei einer Nachricht in den Zwischenspeicher laden (zum Oeffnen mit einer anderen App). */
    suspend fun download(message: Long, index: Int, name: String): File = io {
        val dir = File(app.cacheDir, "attachments/chat/$message-$index").apply { mkdirs() }
        val f = File(dir, name.replace('/', '_'))
        if (!f.exists() || f.length() == 0L) api().download(api().url("/api/chat/messages/$message/refs/$index/file", "inline" to true), f)
        f
    }

    suspend fun note(message: Long, index: Int): ChatNote = io { api().get<ChatNote>(api().url("/api/chat/messages/$message/refs/$index/note")) }

    /** Bei mir speichern: Datei in die eigene Ablage (Ordner "Chat"), Notiz in die eigenen Notizen. */
    suspend fun save(message: Long, index: Int): ChatSaved = io { api().postFor<Empty, ChatSaved>(api().url("/api/chat/messages/$message/refs/$index/save"), Empty()) }
    // ---------------- Stufe 2: Arten, Weiterleiten, Einstellungen, Farbe, Auswahl ----------------

    /** Eigenen Verweis aendern: Art (show/send) oder zurueckziehen/wieder freigeben. */
    suspend fun changeRef(message: Long, index: Int, mode: String? = null, revoke: Boolean? = null): ChatMessage = io {
        val a = api(); a.json.decodeFromString<ChatMessage>(a.putText(a.url("/api/chat/messages/$message/refs/$index"), a.json.encodeToString(RefPatch.serializer(), RefPatch(mode, revoke))))
    }

    suspend fun forward(message: Long, rooms: List<Long>, mode: String?, withText: Boolean = true): List<ChatMessage> =
        io { api().postFor<ForwardBody, List<ChatMessage>>(api().url("/api/chat/messages/$message/forward"), ForwardBody(rooms, mode, withText)) }

    suspend fun settings(): ChatSettingsResponse = io { api().get<ChatSettingsResponse>(api().url("/api/chat/settings")) }

    suspend fun saveSettings(s: ChatSettings): ChatSettings = io {
        val a = api(); a.json.decodeFromString<ChatSettings>(a.putText(a.url("/api/chat/settings"), a.json.encodeToString(ChatSettings.serializer(), s)))
    }

    /** Persoenliche Farbe der Unterhaltung, null: Akzentfarbe. */
    suspend fun setColor(room: Long, color: String?): ChatRoom = io {
        val a = api(); a.json.decodeFromString<ChatRoom>(a.putText(a.url("/api/chat/rooms/$room"), a.json.encodeToString(ColorBody.serializer(), ColorBody(color ?: ""))))
    }

    /** Bei mir speichern mit Ziel (Ordner der Ablage bzw. Notizordner; null: Vorgabe aus den Einstellungen, "": ohne Ordner). */
    suspend fun saveTo(message: Long, index: Int, folder: String?): ChatSaved =
        io { api().postFor<SaveBody, ChatSaved>(api().url("/api/chat/messages/$message/refs/$index/save"), SaveBody(folder)) }

    /** Eigene Dateien und Ordner der Ablage (ohne mit mir geteilte). */
    suspend fun listFiles(path: String): List<ChatFileItem> =
        io { api().get<ChatFileList>(api().url("/api/files/", "path" to path)).items.filter { !it.shared && it.owner == null } }

    /** Eigene Notizen (Suche oder alle). */
    suspend fun listNotes(query: String): List<ChatNoteItem> = io {
        val a = api()
        a.get<ChatNoteList>(if (query.isBlank()) a.url("/api/notes", "view" to "all") else a.url("/api/notes", "q" to query)).items.filter { it.id > 0 && it.owner == null }
    }

    /** Mini-Vorschau einer eigenen Datei der Ablage (Auswahl im Chat); null: keine. */
    suspend fun fileThumb(item: ChatFileItem, size: Int = 128): Bitmap? {
        val key = "f/" + item.path + "/" + size
        thumbs.get(key)?.let { return it }
        return try {
            io { val a = api(); val (bytes, _) = a.getBytes(a.url("/api/files/thumb", "path" to item.path, "size" to size)); BitmapFactory.decodeByteArray(bytes, 0, bytes.size) }
                ?.also { thumbs.put(key, it) }
        } catch (_: Exception) { null }
    }

    suspend fun noteFolders(): List<ChatNoteFolder> = io { api().get<List<ChatNoteFolder>>(api().url("/api/notes/folders")) }

    // ---------------- Stufe 3: Termin, Aufgabe, Kontakt, Ordner ----------------

    /** Termin, Aufgabe oder Kontakt aus einer Nachricht (aktueller Stand beim Besitzer) - als JSON wie vom Server. */
    suspend fun item(message: Long, index: Int): kotlinx.serialization.json.JsonObject =
        io { api().get<kotlinx.serialization.json.JsonObject>(api().url("/api/chat/messages/$message/refs/$index/item")) }

    /** Gezeigtes Album (Version 2): seine Fotos. */
    suspend fun album(message: Long, index: Int): List<ChatAlbumPhoto> =
        io { api().get<ChatAlbum>(api().url("/api/chat/messages/$message/refs/$index/album")).items }

    /** Vorschau eines Albumfotos; size 1600 zum Ansehen. */
    suspend fun albumThumb(message: Long, index: Int, key: String, size: Int = 256): Bitmap? {
        val cacheKey = "al/$message/$index/$key/$size"
        thumbs.get(cacheKey)?.let { return it }
        return try {
            io { val a = api(); val (bytes, _) = a.getBytes(a.url("/api/chat/messages/$message/refs/$index/album/thumb", "key" to key, "size" to size)); BitmapFactory.decodeByteArray(bytes, 0, bytes.size) }
                ?.also { thumbs.put(cacheKey, it) }
        } catch (_: Exception) { null }
    }

    /** Inhalt eines gezeigten/gesendeten Ordners (eine Ebene). */
    suspend fun folder(message: Long, index: Int, path: String): List<ChatFolderEntry> =
        io { api().get<List<ChatFolderEntry>>(api().url("/api/chat/messages/$message/refs/$index/folder", "path" to path)) }

    suspend fun folderThumb(message: Long, index: Int, entry: ChatFolderEntry, size: Int = 128): Bitmap? {
        val key = "fo/$message/$index/${entry.path}/$size"
        thumbs.get(key)?.let { return it }
        return try {
            io { val a = api(); val (bytes, _) = a.getBytes(a.url("/api/chat/messages/$message/refs/$index/folder/thumb", "path" to entry.path, "size" to size)); BitmapFactory.decodeByteArray(bytes, 0, bytes.size) }
                ?.also { thumbs.put(key, it) }
        } catch (_: Exception) { null }
    }

    /** Datei aus dem Ordner in den Zwischenspeicher (zum Oeffnen - nur bei Senden oder eigenem). */
    suspend fun folderDownload(message: Long, index: Int, entry: ChatFolderEntry): File = io {
        val dir = File(app.cacheDir, "attachments/chat/$message-$index-folder").apply { mkdirs() }
        val f = File(dir, entry.path.replace('/', '_'))
        if (!f.exists() || f.length() == 0L) api().download(api().url("/api/chat/messages/$message/refs/$index/folder/file", "path" to entry.path, "inline" to true), f)
        f
    }

    suspend fun folderSave(message: Long, index: Int, path: String, folder: String? = null): ChatSaved =
        io { api().postFor<FolderSaveBody, ChatSaved>(api().url("/api/chat/messages/$message/refs/$index/folder/save"), FolderSaveBody(path, folder)) }

    /** Eigene Sammlungen fuer Termin/Aufgabe (calendars) oder Kontakt (addressbooks), ohne Abos und geteilte. */
    suspend fun ownCollections(contacts: Boolean, component: String?): List<ChatCollection> = io {
        api().get<List<ChatCollection>>(api().url("/api/pim/collections/" + if (contacts) "addressbooks" else "calendars"))
            .filter { it.owner == null && (it.subscription == null || it.subscription is kotlinx.serialization.json.JsonNull) && it.id > 0 && (component == null || component in it.components) }
    }

    suspend fun spaces(room: Long): List<ChatSpace> = io { api().get<List<ChatSpace>>(api().url("/api/chat/rooms/$room/spaces")) }

    suspend fun createSpace(room: Long, kind: String): List<ChatSpace> = io { api().postFor<NewSpaceBody, List<ChatSpace>>(api().url("/api/chat/rooms/$room/spaces"), NewSpaceBody(kind)) }

    suspend fun removeSpace(room: Long, kind: String) = io { api().delete(api().url("/api/chat/rooms/$room/spaces/$kind")) }

    /** „Im Chat …“ aus den Bereichen: dieselben Verweise in mehrere Unterhaltungen (Art: show/send). */
    suspend fun sendTo(rooms: List<Long>, refs: List<ChatOutRef>, mode: String) = io {
        val body = RefsBody(refs.map { RefInput(it.type, it.fileId, it.noteId, mode, it.collection, it.item) })
        for (r in rooms) api().postFor<RefsBody, ChatMessage>(api().url("/api/chat/rooms/$r/messages"), body)
    }

    /**
     * Termin oder Aufgabe aus dem CalDAV-Abgleich als Verweis: der Kalender ist der letzte Teil seiner Adresse
     * (gleich der Uri beim Server), der Eintrag der letzte Teil der Adresse des Eintrags. Null: kein eigener Kalender.
     */
    suspend fun itemRef(type: String, calendarHref: String, href: String): ChatOutRef? {
        fun last(s: String) = java.net.URLDecoder.decode(s.trimEnd('/').substringAfterLast('/'), "UTF-8")
        val uri = last(calendarHref)
        val coll = ownCollections(contacts = false, component = null).firstOrNull { it.uri == uri } ?: return null
        return ChatOutRef(type, collection = coll.id, item = last(href))
    }

    /** Verweise auf eigene Dateien oder Notizen schicken (Art: show/send). */
    suspend fun sendRefs(room: Long, files: List<Long> = emptyList(), notes: List<Long> = emptyList(), mode: String): ChatMessage = io {
        val refs = files.map { RefInput("file", fileId = it, mode = mode) } + notes.map { RefInput("note", noteId = it, mode = mode) }
        api().postFor<RefsBody, ChatMessage>(api().url("/api/chat/rooms/$room/messages"), RefsBody(refs))
    }
}
