package at.dimes.dmoffice.sync

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import java.io.File
import java.util.UUID
import java.util.concurrent.TimeUnit

/** Chat-Nachricht, die noch raus muss: Text oder Datei (Foto, Sprachnachricht) aus dem Postausgang-Ordner. */
@Serializable
data class OutChat(
    val id: String, val room: Long, val text: String? = null, val replyTo: Long? = null,
    val file: String? = null, val name: String? = null, val mime: String? = null, val created: Long,
)

/** Mail, die noch raus muss: der Entwurf als JSON (wie für /send) und die Anhänge als Dateien im Postausgang-Ordner. */
@Serializable
data class OutMail(val id: String, val account: String, val draft: String, val subject: String = "", val files: List<OutFile> = emptyList(), val created: Long)

@Serializable
data class OutFile(val path: String, val name: String, val mime: String)

/**
 * Postausgang der App (Version 2): Was ohne Netz abgeschickt wird - Chat-Nachrichten und Mails - bleibt auf dem Gerät
 * (privater Speicher, je Konto) und geht raus, sobald der Server wieder erreichbar ist. Das übernimmt ein
 * Hintergrund-Job mit der Bedingung „Netz da“, auch wenn die App geschlossen ist. Gesendet wird der Reihe nach.
 */
object Outbox {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }
    private val lock = Mutex()
    private val _chat = MutableStateFlow<List<OutChat>>(emptyList())
    private val _mail = MutableStateFlow<List<OutMail>>(emptyList())
    val chat: StateFlow<List<OutChat>> = _chat
    val mail: StateFlow<List<OutMail>> = _mail
    @Volatile private var loadedFor: String? = null

    /** Keine Verbindung (kein Netz, Server nicht erreichbar, Proxy ohne Server) - dann vormerken statt Fehler. */
    fun offline(e: Throwable): Boolean = when (e) {
        is ApiException -> e.code == 502 || e.code == 503 || e.code == 504
        is java.io.IOException -> true
        else -> false
    }

    private fun dir(ctx: Context): File? {
        val a = App.get(ctx).prefs.account.value ?: return null
        val key = Integer.toHexString((a.baseUrl.trimEnd('/') + "|" + a.login).hashCode())
        return File(ctx.noBackupFilesDir, "outbox/$key").apply { mkdirs() }
    }

    /** Stand von der Platte (einmal je Konto). */
    fun load(ctx: Context) {
        val d = dir(ctx) ?: return
        if (loadedFor == d.path) return
        loadedFor = d.path
        _chat.value = read(File(d, "chat.json"), OutChat.serializer())
        _mail.value = read(File(d, "mail.json"), OutMail.serializer())
    }

    private fun <T> read(f: File, s: kotlinx.serialization.KSerializer<T>): List<T> =
        try { if (f.exists()) json.decodeFromString(ListSerializer(s), f.readText()) else emptyList() } catch (_: Exception) { emptyList() }

    private fun save(ctx: Context) {
        val d = dir(ctx) ?: return
        File(d, "chat.json").writeText(json.encodeToString(ListSerializer(OutChat.serializer()), _chat.value))
        File(d, "mail.json").writeText(json.encodeToString(ListSerializer(OutMail.serializer()), _mail.value))
    }

    /** Datei in den Postausgang kopieren (das Original im Zwischenspeicher darf weg). */
    fun keep(ctx: Context, source: File): File {
        val target = File(File(dir(ctx), "files").apply { mkdirs() }, UUID.randomUUID().toString() + "-" + source.name)
        source.copyTo(target, overwrite = true)
        return target
    }

    fun keep(ctx: Context, name: String, bytes: ByteArray): File {
        val target = File(File(dir(ctx), "files").apply { mkdirs() }, UUID.randomUUID().toString() + "-" + name.replace('/', '_'))
        target.writeBytes(bytes)
        return target
    }

    suspend fun addChat(ctx: Context, room: Long, text: String? = null, replyTo: Long? = null, file: File? = null, name: String? = null, mime: String? = null) =
        lock.withLock {
            load(ctx)
            _chat.value = _chat.value + OutChat(UUID.randomUUID().toString(), room, text, replyTo, file?.path, name, mime, System.currentTimeMillis())
            withContext(Dispatchers.IO) { save(ctx) }
            schedule(ctx)
        }

    suspend fun addMail(ctx: Context, account: String, draft: String, subject: String, files: List<OutFile>) = lock.withLock {
        load(ctx)
        _mail.value = _mail.value + OutMail(UUID.randomUUID().toString(), account, draft, subject, files, System.currentTimeMillis())
        withContext(Dispatchers.IO) { save(ctx) }
        schedule(ctx)
    }

    /** Vorgemerktes nicht mehr senden. */
    suspend fun cancelChat(ctx: Context, id: String) = lock.withLock {
        _chat.value.firstOrNull { it.id == id }?.file?.let { File(it).delete() }
        _chat.value = _chat.value.filter { it.id != id }
        withContext(Dispatchers.IO) { save(ctx) }
    }

    suspend fun cancelMail(ctx: Context, id: String) = lock.withLock {
        _mail.value.firstOrNull { it.id == id }?.files?.forEach { File(it.path).delete() }
        _mail.value = _mail.value.filter { it.id != id }
        withContext(Dispatchers.IO) { save(ctx) }
    }

    /** Hintergrund-Job: läuft, sobald Netz da ist; bei Fehlern später noch einmal. */
    fun schedule(ctx: Context) {
        val req = OneTimeWorkRequestBuilder<OutboxWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .setBackoffCriteria(androidx.work.BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
            .build()
        WorkManager.getInstance(ctx).enqueueUniqueWork("outbox", ExistingWorkPolicy.KEEP, req)
    }

    /**
     * Alles senden, was geht - der Reihe nach. Ohne Verbindung hört er auf (true: es bleibt etwas übrig).
     * Lehnt der Server eine Nachricht ab (etwa: Unterhaltung gibt es nicht mehr), fällt sie weg - sonst hinge alles fest.
     */
    suspend fun flush(ctx: Context): Boolean = lock.withLock {
        load(ctx)
        val app = App.get(ctx)
        withContext(Dispatchers.IO) {
            for (c in _chat.value.toList()) {
                try {
                    if (c.file != null) app.chat.upload(c.room, c.name ?: "Datei", c.mime, File(c.file), c.text)
                    else app.chat.send(c.room, c.text ?: "", c.replyTo)
                } catch (e: Exception) { if (offline(e)) { save(ctx); return@withContext } }
                c.file?.let { File(it).delete() }
                _chat.value = _chat.value.filter { it.id != c.id }
            }
            for (m in _mail.value.toList()) {
                try {
                    val files = m.files.map { Triple(it.name, it.mime, File(it.path).readBytes()) }
                    app.api?.postMultipart(app.api!!.url("/api/mail/${m.account}/send"), m.draft, files) ?: return@withContext
                } catch (e: Exception) { if (offline(e)) { save(ctx); return@withContext } }
                m.files.forEach { File(it.path).delete() }
                _mail.value = _mail.value.filter { it.id != m.id }
            }
            save(ctx)
        }
        _chat.value.isNotEmpty() || _mail.value.isNotEmpty()
    }
}

class OutboxWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result = if (Outbox.flush(applicationContext)) Result.retry() else Result.success()
}
