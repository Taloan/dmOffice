package at.dimes.dmoffice.files

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import at.dimes.dmoffice.App
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

/**
 * Vorschaubilder kommen fertig vom Server (/api/files/thumb, WebP 256 bzw. 1600 px) - die App rechnet nichts
 * selbst. Hier nur zwischengespeichert: im Speicher (LRU) und im Cache-Ordner, Schluessel Pfad + ETag + Groesse,
 * so wird ein geaendertes Bild von selbst neu geholt. Der Cache-Ordner wird bei Platzmangel von Android geleert.
 */
object Thumbs {
    const val SMALL = 256
    const val LARGE = 1600
    private const val DISK_MAX = 150L * 1024 * 1024

    private val memory = object : LruCache<String, Bitmap>(((Runtime.getRuntime().maxMemory() / 8).coerceAtMost(64L * 1024 * 1024)).toInt()) {
        override fun sizeOf(key: String, value: Bitmap) = value.byteCount
    }
    /** Hat keines (404): in dieser Sitzung nicht wieder fragen. */
    private val missing = ConcurrentHashMap.newKeySet<String>()
    /** Nicht zu viele Abrufe gleichzeitig (Kachelansicht mit vielen Bildern). */
    private val gate = Semaphore(4)

    private fun key(item: FileItem, size: Int) = sha(item.path + "|" + (item.etag ?: item.modified ?: "") + "|" + size)

    private fun dir(context: Context) = File(context.cacheDir, "thumbs").apply { mkdirs() }

    /** Schon im Speicher (ohne Warten, fuer den ersten Frame). */
    fun cached(item: FileItem, size: Int): Bitmap? = memory.get(key(item, size))

    suspend fun load(context: Context, item: FileItem, size: Int): Bitmap? {
        val k = key(item, size)
        memory.get(k)?.let { return it }
        if (k in missing) return null
        return withContext(Dispatchers.IO) {
            gate.withPermit {
                memory.get(k)?.let { return@withPermit it }
                val f = File(dir(context), "$k.webp")
                val bytes = if (f.isFile) f.readBytes().also { f.setLastModified(System.currentTimeMillis()) }
                    else App.get(context).files.thumb(item, size)?.also { b -> f.writeBytes(b); trim(context) }
                if (bytes == null) { missing += k; return@withPermit null }
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.also { memory.put(k, it) }
            }
        }
    }

    /** Aelteste Dateien weg, wenn der Ordner zu gross wird. */
    private fun trim(context: Context) {
        val files = dir(context).listFiles() ?: return
        var total = files.sumOf { it.length() }
        if (total <= DISK_MAX) return
        for (f in files.sortedBy { it.lastModified() }) { total -= f.length(); f.delete(); if (total <= DISK_MAX * 3 / 4) break }
    }

    fun clear(context: Context) {
        memory.evictAll(); missing.clear()
        File(context.cacheDir, "thumbs").deleteRecursively()
    }

    private fun sha(s: String) = MessageDigest.getInstance("SHA-1").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
}
