package at.dimes.dmoffice.upload

import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import android.text.format.Formatter
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.FilesRepo
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import java.io.File

/**
 * Geteilte Dateien hochladen (Version 2): die Teilen-Seite kopiert sie in den Cache (die Leserechte der fremden App gelten nur
 * kurz), dieser Auftrag lädt sie im Hintergrund hoch - in einen Ordner der Dateiablage oder ins Tagebuch (je Aufnahmetag ein
 * Eintrag, Fotos kleingerechnet, Datum und Ort aus dem Bild). Fortschritt und Ergebnis als Benachrichtigung.
 */
class ShareUploadWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {

    companion object {
        private const val NOTIF_PROGRESS = 7301
        private const val NOTIF_RESULT = 7302

        /** Ordner im Cache für einen Auftrag. */
        fun batchDir(context: Context, batch: String) = File(context.cacheDir, "share/$batch")

        /** Was gerade aus dem Teilen-Menü hochgeht - auch unter „Automatisch hochladen“ zu sehen (läuft im selben Prozess). */
        data class Progress(val name: String, val sent: Long, val size: Long, val index: Int, val count: Int)
        private val _progress = kotlinx.coroutines.flow.MutableStateFlow<Progress?>(null)
        val progress: kotlinx.coroutines.flow.StateFlow<Progress?> = _progress

        /** In die Dateiablage (dir) bzw. ins Tagebuch (journal). */
        fun start(context: Context, batch: String, dir: String?, journal: Long?) {
            val req = OneTimeWorkRequestBuilder<ShareUploadWorker>()
                .setInputData(workDataOf("batch" to batch, "dir" to dir, "journal" to (journal ?: -1L)))
                .build()
            WorkManager.getInstance(context).enqueue(req)
        }
    }

    override suspend fun getForegroundInfo(): ForegroundInfo = foreground(0, 0)

    /** Bytes aller Dateien und der schon fertigen - bei mehreren Dateien zeigt der Balken den Gesamtfortschritt. */
    private var allBytes = 0L
    private var doneBytes = 0L

    /** Fortschritt: Dateien ([done] von [total]); bei einer großen Datei zusätzlich ihr Name und die gesendeten Bytes. */
    private fun foreground(done: Int, total: Int, name: String? = null, sent: Long = 0, size: Long = 0): ForegroundInfo {
        AutoUpload.ensureChannel(applicationContext)
        if (name != null) _progress.value = Progress(name, sent, size, done + 1, total)
        val ctx = applicationContext
        val bytes = size > 0 && name != null
        val text = when {
            bytes -> ctx.getString(R.string.share_progress_bytes, name, Formatter.formatShortFileSize(ctx, sent), Formatter.formatShortFileSize(ctx, size)) +
                (if (total > 1) " · " + ctx.getString(R.string.share_progress, done + 1, total) else "")
            total > 0 -> ctx.getString(R.string.share_progress, done, total)
            else -> null
        }
        val b = NotificationCompat.Builder(ctx, AutoUpload.CHANNEL).setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(ctx.getString(R.string.share_uploading)).setContentText(text).setOngoing(true).setSilent(true)
        when {
            total > 1 && allBytes > 0 -> {
                val now = (doneBytes + (if (name != null) sent else 0)).coerceAtMost(allBytes)
                b.setProgress(1000, (now * 1000 / allBytes).toInt(), false)
                b.setContentText(ctx.getString(R.string.share_progress, (done + if (name != null) 1 else 0).coerceAtMost(total), total) + " · " +
                    ctx.getString(R.string.au_notif_bytes, Formatter.formatShortFileSize(ctx, now), Formatter.formatShortFileSize(ctx, allBytes)))
            }
            bytes -> b.setProgress(1000, (sent * 1000 / size).toInt(), false)
            else -> b.setProgress(total, done, total == 0)
        }
        val n = b.build()
        return if (Build.VERSION.SDK_INT >= 29) ForegroundInfo(NOTIF_PROGRESS, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC) else ForegroundInfo(NOTIF_PROGRESS, n)
    }

    /** Bytes einer Datei melden - nur bei jedem vollen Prozent, sonst flackert die Benachrichtigung. */
    private fun bytes(i: Int, total: Int, f: java.io.File): (Long) -> Unit {
        var last = -1L
        return { sent ->
            val pct = if (f.length() > 0) sent * 100 / f.length() else 0
            if (pct != last) { last = pct; setForegroundAsync(foreground(i, total, f.name, sent, f.length())) }
        }
    }

    override suspend fun doWork(): Result {
        val ctx = applicationContext
        val app = App.get(ctx)
        val batch = inputData.getString("batch") ?: return Result.failure()
        val folder = batchDir(ctx, batch)
        val files = folder.listFiles()?.filter { it.isFile }?.sortedBy { it.name } ?: emptyList()
        val dir = inputData.getString("dir")
        val journal = inputData.getLong("journal", -1L).takeIf { it >= 0 }
        var failed = 0
        var message: String
        allBytes = files.sumOf { it.length() }; doneBytes = 0
        try {
            setForeground(foreground(0, files.size))
            val api = app.api ?: throw IllegalStateException(ctx.getString(R.string.share_no_account))
            if (journal == null) {
                val target = dir ?: ""
                files.forEachIndexed { i, f ->
                    setForeground(foreground(i, files.size, f.name, 0, f.length()))
                    try {
                        val name = FilesRepo.uniqueName(app.files.listCached(target, 0), f.name)
                        app.files.uploadServer(target, name, null, f, bytes(i, files.size, f))
                    } catch (e: Exception) { failed++ }
                    doneBytes += f.length()
                    setForeground(foreground(i + 1, files.size))
                }
                val shown = target.ifEmpty { ctx.getString(R.string.files) }
                message = if (failed == 0) ctx.resources.getQuantityString(R.plurals.share_done_files, files.size, files.size, shown)
                    else ctx.getString(R.string.share_failed, failed, files.size)
            } else {
                val media = mutableListOf<JsonObject>()
                val places = mutableListOf<JsonObject>()
                files.forEachIndexed { i, f ->
                    setForeground(foreground(i, files.size, f.name, 0, f.length()))
                    try {
                        val mime = FilesRepo.guessMime(f.name)
                        val up = ChunkedUpload.upload(api, { f.inputStream() }, f.name, f.length(), mime, onProgress = bytes(i, files.size, f))
                        val r = api.json.parseToJsonElement(api.postText(api.url("/api/journal/uploads/${up.id}/attach"), "")).jsonObject
                        val m = r["media"]!!.jsonObject
                        media += m
                        val lat = (r["lat"] as? JsonPrimitive)?.doubleOrNull
                        val lon = (r["lon"] as? JsonPrimitive)?.doubleOrNull
                        if (lat != null && lon != null) places += buildJsonObject { put("path", m["path"]!!); put("lat", lat); put("lon", lon) }
                    } catch (e: Exception) { failed++ }
                    doneBytes += f.length()
                    setForeground(foreground(i + 1, files.size))
                }
                var entries = 0
                if (media.isNotEmpty()) {
                    val body = buildJsonObject {
                        put("journal", journal)
                        put("media", buildJsonArray { media.forEach { add(it) } })
                        put("places", buildJsonArray { places.forEach { add(it) } })
                    }
                    entries = (api.json.parseToJsonElement(api.postText(api.url("/api/journal/from-media"), body.toString())) as? JsonArray)?.size ?: 0
                }
                message = if (failed == 0) ctx.resources.getQuantityString(R.plurals.share_done_journal, entries, entries, media.size)
                    else ctx.getString(R.string.share_failed, failed, files.size)
            }
        } catch (e: Exception) {
            failed = files.size
            message = e.message ?: e.toString()
        } finally {
            _progress.value = null
            folder.deleteRecursively()
        }
        AutoUpload.ensureChannel(ctx)
        val nm = ctx.getSystemService(android.app.NotificationManager::class.java)
        nm.notify(NOTIF_RESULT, NotificationCompat.Builder(ctx, AutoUpload.CHANNEL).setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(ctx.getString(if (failed == 0) R.string.share_done_title else R.string.share_failed_title))
            .setContentText(message).setStyle(NotificationCompat.BigTextStyle().bigText(message)).setAutoCancel(true).build())
        return Result.success()
    }
}
