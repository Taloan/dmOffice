package at.dimes.dmoffice.upload

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.util.Log
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** Eine Datei auf dem Geraet, die zu einer Regel passt. Zeiten in ms. */
data class LocalFile(
    val uri: Uri,
    val sourcePath: String,
    val relDir: String,
    val name: String,
    val mime: String?,
    val size: Long,
    val modified: Long,
    val added: Long,
    val taken: Long,
)

/**
 * Sucht die Dateien einer Regel: Medienordner ueber den MediaStore (Fotos und Videos, mit Aufnahmedatum),
 * frei gewaehlte Ordner ueber das Storage Access Framework (alle Dateiarten, Datum aus EXIF bzw. den Video-Metadaten).
 */
object Scanner {
    private const val TAG = "AutoUpload"

    fun scan(context: Context, rule: UploadRule): List<LocalFile> =
        if (rule.source == UploadLogic.SRC_TREE) scanTree(context, rule) else scanMedia(context, rule)

    /** Ordner (RELATIVE_PATH) einer Regel aus dem MediaStore. */
    fun mediaPaths(rule: UploadRule): List<String> =
        rule.sourcePaths.lines().map { it.trim() }.filter { it.isNotEmpty() }.map { UploadLogic.normalize(it) + "/" }

    private fun tables(types: String): List<Uri> {
        val vol = if (Build.VERSION.SDK_INT >= 29) MediaStore.VOLUME_EXTERNAL else "external"
        return buildList {
            if (types != UploadLogic.TYPES_VIDEOS) add(MediaStore.Images.Media.getContentUri(vol))
            if (types != UploadLogic.TYPES_PHOTOS) add(MediaStore.Video.Media.getContentUri(vol))
        }
    }

    private val storageRoot: String by lazy { @Suppress("DEPRECATION") Environment.getExternalStorageDirectory().absolutePath.trimEnd('/') + "/" }

    private fun scanMedia(context: Context, rule: UploadRule): List<LocalFile> {
        val paths = mediaPaths(rule)
        if (paths.isEmpty()) return emptyList()
        val out = ArrayList<LocalFile>()
        val q29 = Build.VERSION.SDK_INT >= 29
        for (table in tables(rule.types)) {
            val proj = buildList {
                add(MediaStore.MediaColumns._ID); add(MediaStore.MediaColumns.DISPLAY_NAME); add(MediaStore.MediaColumns.SIZE)
                add(MediaStore.MediaColumns.DATE_MODIFIED); add(MediaStore.MediaColumns.DATE_ADDED); add("datetaken"); add(MediaStore.MediaColumns.MIME_TYPE)
                if (q29) { add(MediaStore.MediaColumns.RELATIVE_PATH); add(MediaStore.MediaColumns.VOLUME_NAME) } else add("_data")
            }.toTypedArray()
            val sel: String; val args: Array<String>
            if (q29) {
                sel = "(" + paths.joinToString(" OR ") { "${MediaStore.MediaColumns.RELATIVE_PATH} LIKE ?" } + ") AND ${MediaStore.MediaColumns.IS_PENDING} = 0"
                args = paths.map { "$it%" }.toTypedArray()
            } else {
                sel = paths.joinToString(" OR ") { "_data LIKE ?" }
                args = paths.map { "%/$it%" }.toTypedArray()
            }
            try {
                context.contentResolver.query(table, proj, sel, args, null)?.use { c ->
                    while (c.moveToNext()) {
                        val id = c.getLong(0)
                        val name = c.getString(1) ?: continue
                        val size = c.getLong(2)
                        val modified = c.getLong(3) * 1000
                        val added = c.getLong(4) * 1000
                        val taken = if (c.isNull(5)) 0 else c.getLong(5)
                        val mime = c.getString(6)
                        val dir: String; val uri: Uri
                        if (q29) {
                            dir = c.getString(7) ?: continue
                            uri = ContentUris.withAppendedId(tableFor(table, c.getString(8)), id)
                        } else {
                            val data = c.getString(7) ?: continue
                            dir = if (data.startsWith(storageRoot)) data.removePrefix(storageRoot).substringBeforeLast('/', "") + "/" else continue
                            uri = ContentUris.withAppendedId(table, id)
                        }
                        // LIKE kennt "_" als Platzhalter: hier genau pruefen
                        val root = paths.firstOrNull { dir.startsWith(it, ignoreCase = true) } ?: continue
                        if (UploadLogic.ignoredName(name) || size <= 0) continue
                        val rel = UploadLogic.relDir(root, dir)
                        // Ohne Zeitzone im EXIF (kopierte Fotos) laesst der MediaStore "datetaken" leer: dann liest scanRule das EXIF selbst
                        out += LocalFile(uri, UploadLogic.normalize(dir) + "/" + name, rel, name, mime, size, modified, added, taken)
                    }
                }
            } catch (e: SecurityException) {
                Log.w(TAG, "Keine Berechtigung fuer Medien: " + e.message)
                throw e
            }
        }
        return out
    }

    /**
     * Groesse und Aenderungszeit (ms, wie beim Durchsuchen) einer Datei im MediaStore; null, wenn es sie nicht mehr gibt
     * oder sie gerade im Papierkorb des Systems liegt. Fuer die Pruefung direkt vor dem Freigeben. Fehler (Berechtigung)
     * gehen weiter - "weiss nicht" ist nicht "weg".
     */
    fun mediaStat(context: Context, uri: Uri): Pair<Long, Long>? {
        val sel = if (Build.VERSION.SDK_INT >= 30) "${MediaStore.MediaColumns.IS_TRASHED} = 0" else null
        val c = context.contentResolver.query(uri, arrayOf(MediaStore.MediaColumns.SIZE, MediaStore.MediaColumns.DATE_MODIFIED), sel, null, null)
            ?: throw java.io.IOException("MediaStore antwortet nicht")
        return c.use { if (it.moveToFirst()) it.getLong(0) to it.getLong(1) * 1000 else null }
    }

    /** Uri der Einzeldatei in derselben Tabelle (Bilder/Videos), aber auf ihrem Speicher (Volume). */
    private fun tableFor(table: Uri, volume: String?): Uri {
        val vol = volume ?: if (Build.VERSION.SDK_INT >= 29) MediaStore.VOLUME_EXTERNAL_PRIMARY else "external"
        return if (table.toString().contains("/video/")) MediaStore.Video.Media.getContentUri(vol) else MediaStore.Images.Media.getContentUri(vol)
    }

    private fun scanTree(context: Context, rule: UploadRule): List<LocalFile> {
        val tree = Uri.parse(rule.sourcePaths.trim())
        val rootId = DocumentsContract.getTreeDocumentId(tree)
        val out = ArrayList<LocalFile>()
        val proj = arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE, DocumentsContract.Document.COLUMN_SIZE, DocumentsContract.Document.COLUMN_LAST_MODIFIED)
        fun walk(docId: String, rel: String, depth: Int) {
            if (depth > 30) return
            val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, docId)
            context.contentResolver.query(children, proj, null, null, null)?.use { c ->
                while (c.moveToNext()) {
                    val id = c.getString(0) ?: continue
                    val name = c.getString(1) ?: continue
                    val mime = c.getString(2)
                    if (UploadLogic.ignoredName(name)) continue
                    if (mime == DocumentsContract.Document.MIME_TYPE_DIR) { walk(id, if (rel.isEmpty()) name else "$rel/$name", depth + 1); continue }
                    if (!UploadLogic.typeMatches(rule.types, mime, name)) continue
                    val size = if (c.isNull(3)) 0 else c.getLong(3)
                    if (size <= 0) continue
                    val modified = if (c.isNull(4)) 0 else c.getLong(4)
                    out += LocalFile(DocumentsContract.buildDocumentUriUsingTree(tree, id), if (rel.isEmpty()) name else "$rel/$name", rel, name, mime, size, modified, modified, 0)
                }
            }
        }
        walk(rootId, "", 0)
        return out
    }

    /**
     * Aufnahmedatum fuer Dateien aus einem frei gewaehlten Ordner: EXIF bei Fotos, Metadaten bei Videos,
     * sonst die Aenderungszeit. Nur fuer neue Dateien, deren Datumsordner gebraucht wird.
     */
    fun takenDate(context: Context, f: LocalFile): Long {
        try {
            when (UploadLogic.kindOf(f.mime, f.name)) {
                1 -> context.contentResolver.openInputStream(f.uri)?.use { s ->
                    val exif = android.media.ExifInterface(s)
                    val v = exif.getAttribute(android.media.ExifInterface.TAG_DATETIME_ORIGINAL) ?: exif.getAttribute(android.media.ExifInterface.TAG_DATETIME)
                    parseExif(v)?.let { return it }
                }
                2 -> {
                    val r = android.media.MediaMetadataRetriever()
                    try {
                        r.setDataSource(context, f.uri)
                        parseVideoDate(r.extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_DATE))?.let { return it }
                    } finally { r.release() }
                }
            }
        } catch (e: Exception) { Log.d(TAG, "Kein Aufnahmedatum fuer ${f.name}: ${e.message}") }
        return f.modified
    }

    /** EXIF "2024:05:04 13:02:01" in Ortszeit. */
    fun parseExif(v: String?, zone: ZoneId = ZoneId.systemDefault()): Long? {
        if (v.isNullOrBlank() || v.startsWith("0000")) return null
        return try { LocalDateTime.parse(v.trim().take(19), DateTimeFormatter.ofPattern("yyyy:MM:dd HH:mm:ss")).atZone(zone).toInstant().toEpochMilli() } catch (_: Exception) { null }
    }

    /** Video-Metadaten "20240504T130201.000Z" (UTC). */
    fun parseVideoDate(v: String?): Long? {
        if (v.isNullOrBlank() || v.startsWith("1904") || v.startsWith("1970")) return null
        return try { LocalDateTime.parse(v.trim().take(15), DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss")).atZone(ZoneId.of("UTC")).toInstant().toEpochMilli() } catch (_: Exception) { null }
    }

    /** Ein Medienordner fuer die Auswahl: Anzahl Fotos/Videos und das neueste (fuer das Vorschaubild). */
    data class MediaFolder(val path: String, val count: Int, val latest: Uri?, val latestMs: Long)

    /** Medienordner auf dem Geraet (Auswahl "anderer Medienordner"), bekannte zuerst, fremde App-Kopien zuletzt. */
    fun mediaFolders(context: Context): List<MediaFolder> {
        val counts = HashMap<String, Int>()
        val latest = HashMap<String, Pair<Uri, Long>>()
        for (table in tables(UploadLogic.TYPES_ALL)) {
            val col = if (Build.VERSION.SDK_INT >= 29) MediaStore.MediaColumns.RELATIVE_PATH else "_data"
            try {
                context.contentResolver.query(table, arrayOf(col, MediaStore.MediaColumns._ID, MediaStore.MediaColumns.DATE_ADDED), null, null, null)?.use { c ->
                    while (c.moveToNext()) {
                        val v = c.getString(0) ?: continue
                        val dir = if (Build.VERSION.SDK_INT >= 29) v else if (v.startsWith(storageRoot)) v.removePrefix(storageRoot).substringBeforeLast('/', "") + "/" else continue
                        counts[dir] = (counts[dir] ?: 0) + 1
                        val added = c.getLong(2)
                        if ((latest[dir]?.second ?: -1) < added) latest[dir] = ContentUris.withAppendedId(table, c.getLong(1)) to added
                    }
                }
            } catch (e: SecurityException) { Log.w(TAG, "Medienordner: " + e.message) }
        }
        val list = counts.map { (dir, n) -> MediaFolder(dir, n, latest[dir]?.first, (latest[dir]?.second ?: 0) * 1000) }
        return UploadLogic.sortFolders(list, { it.path }, { it.count })
    }

    /** Kleines Vorschaubild eines Fotos/Videos (vom System erzeugt), null = keines. */
    fun thumbnail(context: Context, uri: Uri, px: Int = 144): android.graphics.Bitmap? = try {
        if (Build.VERSION.SDK_INT >= 29) context.contentResolver.loadThumbnail(uri, android.util.Size(px, px), null) else null
    } catch (_: Exception) { null }

    /** Anzeigename eines frei gewaehlten Ordners. */
    fun treeLabel(context: Context, tree: Uri): String {
        val docId = DocumentsContract.getTreeDocumentId(tree)
        try {
            context.contentResolver.query(DocumentsContract.buildDocumentUriUsingTree(tree, docId), arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)?.use { c ->
                if (c.moveToFirst() && !c.isNull(0)) return docId.substringAfter(':', "").ifEmpty { c.getString(0) }
            }
        } catch (_: Exception) { }
        return docId.substringAfter(':', docId)
    }
}
