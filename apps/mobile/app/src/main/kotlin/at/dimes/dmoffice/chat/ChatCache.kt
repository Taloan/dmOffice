package at.dimes.dmoffice.chat

import at.dimes.dmoffice.App
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import java.io.File

/**
 * Chat offline (Chat-Alltag): die Liste der Unterhaltungen und die letzten Nachrichten je Unterhaltung als JSON im
 * privaten Speicher der App - je Konto (Server + Login) ein eigener Ordner. Ohne Netz zeigt die App, was zuletzt da war;
 * sobald der Server antwortet, gilt wieder sein Stand. Eigene Dateien, keine Tabellen in der gemeinsamen Datenbank.
 */
class ChatCache(private val app: App) {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
    private val rooms = ListSerializer(ChatRoom.serializer())
    private val messages = ListSerializer(ChatMessage.serializer())

    /** Wie viele Nachrichten je Unterhaltung bleiben (die neuesten). */
    private val keep = 200

    private fun dir(): File? {
        val a = app.prefs.account.value ?: return null
        val key = Integer.toHexString((a.baseUrl.trimEnd('/') + "|" + a.login).hashCode())
        return File(app.noBackupFilesDir, "chat/$key").apply { mkdirs() }
    }

    suspend fun rooms(): List<ChatRoom>? = read("rooms.json") { json.decodeFromString(rooms, it) }
    suspend fun saveRooms(list: List<ChatRoom>) = write("rooms.json") { json.encodeToString(rooms, list) }

    suspend fun messages(room: Long): List<ChatMessage>? = read("room-$room.json") { json.decodeFromString(messages, it) }
    suspend fun saveMessages(room: Long, list: List<ChatMessage>) = write("room-$room.json") { json.encodeToString(messages, list.takeLast(keep)) }

    /** Abmelden oder Konto gewechselt: alles weg. */
    fun clear() { File(app.noBackupFilesDir, "chat").deleteRecursively() }

    private suspend fun <T> read(name: String, parse: (String) -> T): T? = withContext(Dispatchers.IO) {
        val f = dir()?.let { File(it, name) } ?: return@withContext null
        if (!f.exists()) null else try { parse(f.readText()) } catch (_: Exception) { null }
    }

    private suspend fun write(name: String, text: () -> String) = withContext(Dispatchers.IO) {
        val d = dir() ?: return@withContext
        try {
            // Erst daneben schreiben, dann tauschen - ein Abbruch laesst nie eine halbe Datei zurueck
            val tmp = File(d, "$name.tmp")
            tmp.writeText(text())
            tmp.renameTo(File(d, name))
        } catch (_: Exception) { }
    }
}
