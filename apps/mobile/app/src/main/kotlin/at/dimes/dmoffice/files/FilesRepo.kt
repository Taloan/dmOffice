package at.dimes.dmoffice.files

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import kotlinx.serialization.Serializable
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

@Serializable
data class FileItem(val path: String, val name: String, val isDir: Boolean, val size: Long = 0, val mime: String? = null, val modified: String? = null,
                    val etag: String? = null, val fileId: Long = 0, val favorite: Boolean = false, val shared: Boolean = false) {
    /** Aenderungszeit in ms (0 = unbekannt). Der Server liefert UTC, teils ohne Zonenangabe. */
    val modifiedMs: Long get() = parseTime(modified)

    /** MIME-Typ fuer Android: vom Server, sonst aus der Endung. */
    val type: String get() = mime?.takeIf { it.isNotBlank() && it != "application/octet-stream" } ?: FilesRepo.guessMime(name)
}

@Serializable
data class Listing(val path: String = "", val usage: Long = 0, val quota: Long = 0, val items: List<FileItem> = emptyList(),
                   /** Alles, was der Benutzer belegt (Mails, Kalender, Kontakte, Notizen, Dateien) - dafuer gilt das Kontingent. */
                   val used: Long = 0,
                   val trash: Long = 0, val versions: Long = 0, val favorite: Boolean = false,
                   /** Aus den gespeicherten Metadaten (ohne Verbindung), nicht frisch vom Server. */
                   @kotlinx.serialization.Transient val cached: Boolean = false)

@Serializable data class Items(val items: List<FileItem> = emptyList())
@Serializable data class RecentItem(val item: FileItem, val used: String? = null)
@Serializable data class RecentList(val items: List<RecentItem> = emptyList())
@Serializable data class TrashItem(val id: Long, val path: String, val name: String, val isDir: Boolean = false, val size: Long = 0, val deleted: String? = null, val mime: String? = null)
@Serializable data class TrashList(val days: Int = 30, val usage: Long = 0, val items: List<TrashItem> = emptyList())
@Serializable data class FileVersion(val id: Long, val size: Long = 0, val etag: String? = null, val modified: String? = null, val saved: String? = null)
@Serializable data class VersionList(val items: List<FileVersion> = emptyList())
@Serializable data class FileLink(val id: String, val token: String, val mode: String = "read", val expires: String? = null, val hasPassword: Boolean = false,
                                  val created: String? = null, val downloads: Long = 0, val maxDownloads: Long? = null, val expired: Boolean = false, val item: FileItem? = null)
@Serializable data class LinkList(val items: List<FileLink> = emptyList())
@Serializable data class ContentHit(val path: String, val name: String, val size: Long = 0, val mime: String? = null, val modified: String? = null, val method: String? = null, val snippet: String = "")
@Serializable data class ContentResult(val enabled: Boolean = false, val items: List<ContentHit> = emptyList())
@Serializable data class FileInfo(val item: FileItem, val size: Long = 0, val files: Int = 0, val folders: Int = 0, val versions: List<FileVersion> = emptyList(), val links: List<FileLink> = emptyList())

fun parseTime(s: String?): Long {
    if (s.isNullOrBlank()) return 0
    return try { java.time.OffsetDateTime.parse(s).toInstant().toEpochMilli() } catch (_: Exception) {
        try { java.time.LocalDateTime.parse(s).toInstant(java.time.ZoneOffset.UTC).toEpochMilli() } catch (_: Exception) { 0 }
    }
}

/**
 * Dateiablage von dmOffice ueber /api/files (Web-API mit App-Passwort, im Umschlag wie alles andere).
 * Haelt die zuletzt geladenen Ordnerinhalte kurz im Speicher, damit der DocumentsProvider nicht fuer
 * jede Einzelabfrage zum Server muss. Geoeffnete Dateien liegen im Cache unter files/.
 */
class FilesRepo(private val context: Context) {
    private val listings = ConcurrentHashMap<String, Pair<Long, Listing>>()

    /** Offline verfuegbare Ordner (lokale Kopie mit Abgleich). */
    val offline by lazy { Offline(context, this) }

    private fun api(): ApiClient = App.get(context).api ?: throw ApiException(401, "dmOffice ist nicht gekoppelt")

    /** Server ohne Datei-Tickets: verstaendliche Meldung statt "tickets". */
    private fun <T> tooOld(block: () -> T): T = try { block() } catch (_: ApiClient.TicketsUnsupported) {
        throw java.io.IOException(context.getString(at.dimes.dmoffice.R.string.files_err_server_old))
    }

    /** Ordnerinhalt: in offline verfuegbaren Ordnern aus der lokalen Kopie, sonst vom Server. */
    fun list(path: String): Listing {
        val l = if (offline.ready(path)) offline.listLocal(path) else try { listServer(path) } catch (e: java.io.IOException) {
            // Ohne Netz: zuletzt gesehene Metadaten, sonst wenigstens den Weg zu den Offline-Ordnern (HTTP-Fehler bleiben Fehler)
            if (e is ApiException) throw e
            metaListing(path) ?: offline.pathTo(path) ?: throw e
        }
        listings[path] = System.currentTimeMillis() to l
        return l
    }

    /** Ordnerinhalt vom Server, Ordner zuerst, dann nach Namen. */
    fun listServer(path: String): Listing {
        val a = api()
        val l: Listing = a.get(a.url("/api/files", "path" to path))
        offline.rememberUsage(l.usage, l.quota)
        rememberMeta(path, l.items)
        return l.copy(items = l.items.sortedWith(compareBy<FileItem>({ !it.isDir }, { it.name.lowercase() })))
    }

    // ---------------- Metadaten in Room ----------------

    private val meta get() = App.get(context).db.fileMeta()

    private fun rememberMeta(dir: String, items: List<FileItem>) {
        try {
            meta.replaceChildren(dir, items.map { at.dimes.dmoffice.data.FileMetaEntity(it.path, dir, it.name, it.isDir, it.size, it.mime, it.modified, it.etag, it.fileId, it.favorite, it.shared) })
        } catch (e: Exception) { android.util.Log.w("FilesRepo", "Metadaten: " + e.message) }
    }

    /** Zuletzt gesehener Inhalt eines Ordners aus Room (null = nie gesehen). */
    fun metaListing(path: String): Listing? = try {
        val rows = meta.childrenBlocking(path)
        if (rows.isEmpty() && (path.isEmpty() || meta.isKnownDir(path) == 0)) null
        else Listing(path, offline.state.value.usage, offline.state.value.quota, rows.map { FileItem(it.path, it.name, it.isDir, it.size, it.mime, it.modified, it.etag, it.fileId, it.favorite, it.shared) }
            .sortedWith(compareBy<FileItem>({ !it.isDir }, { it.name.lowercase() })), cached = true)
    } catch (_: Exception) { null }

    private fun forgetMeta(path: String) {
        try { meta.deleteTree(path, path.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "/%") } catch (_: Exception) { }
    }

    // ---------------- Bereiche wie im Web ----------------

    fun recent(): List<RecentItem> { val a = api(); return a.get<RecentList>(a.url("/api/files/recent")).items }
    fun favorites(): List<FileItem> { val a = api(); return a.get<Items>(a.url("/api/files/favorites")).items }
    /** Alle Bilder (Galerie), neueste zuerst. */
    fun photos(): List<FileItem> = search("", FileViews.IMAGE, "").sortedByDescending { it.modifiedMs }
    /** Suche nach Namen unter [under] ("" = ueberall), optional nur eine Art. */
    fun search(q: String, type: String?, under: String): List<FileItem> {
        val a = api(); return a.get<Items>(a.url("/api/files/search", "q" to q, "path" to under, "type" to type)).items
    }
    /** Inhaltssuche (Volltextindex des Servers). */
    fun searchContent(q: String, under: String): ContentResult { val a = api(); return a.get(a.url("/api/files/search/content", "q" to q, "path" to under, "limit" to 200)) }
    fun info(path: String): FileInfo { val a = api(); return a.get(a.url("/api/files/info", "path" to path)) }

    // ---------------- Office (Editor des Office-Servers) ----------------

    @Volatile private var officeCfg: Pair<Long, OfficeConfig>? = null

    /** Office-Server da und welche Endungen? Fuenf Minuten gemerkt; ohne Verbindung bzw. bei Fehler: aus. */
    fun officeConfig(maxAgeMs: Long = 5 * 60_000): OfficeConfig {
        officeCfg?.let { (t, c) -> if (System.currentTimeMillis() - t < maxAgeMs) return c }
        val c = try { val a = api(); a.get<OfficeConfig>(a.url("/api/office/config")) } catch (e: Exception) {
            android.util.Log.w("FilesRepo", "Office: " + e.message); return officeCfg?.second ?: OfficeConfig()
        }
        officeCfg = System.currentTimeMillis() to c
        return c
    }

    /** Sitzung fuer den Editor (Adresse und Token bzw. api.js und Konfiguration). */
    fun officeOpen(path: String, view: Boolean): OfficeSession { val a = api(); return a.postFor(a.url("/api/office/open"), OfficeOpenBody(path, view, mobile = true)) }

    /** Leeres Dokument (docx, xlsx, pptx) anlegen; liefert den Pfad. */
    fun officeNew(dir: String, kind: String, name: String): String {
        val a = api()
        val r: OfficeNewResult = a.postFor(a.url("/api/office/new"), OfficeNewBody(dir, kind, name))
        listings.remove(dir)
        return r.path
    }

    fun setFavorite(path: String, on: Boolean) {
        val a = api()
        a.postText(a.url("/api/files/favorite"), a.json.encodeToString(FavBody.serializer(), FavBody(path, on)))
        try { meta.setFavorite(path, on) } catch (_: Exception) { }
        invalidate(parent(path))
    }

    /** Mehrere in den Papierkorb; liefert die Eintraege dort (fuer Rueckgaengig). */
    fun deleteMany(paths: List<String>): List<Long> {
        val a = api()
        val r = a.postText(a.url("/api/files/delete"), a.json.encodeToString(PathsBody.serializer(), PathsBody(paths)))
        for (p in paths) {
            invalidate(parent(p), p); forgetMeta(p)
            if (offline.state.value.roots.keys.any { Offline.inside(it, p) }) offline.drop(p)
            afterChange(p)
        }
        return try { a.json.decodeFromString(TrashIds.serializer(), r).trash } catch (_: Exception) { emptyList() }
    }

    /** Verschieben/Kopieren mit Konfliktregel (fail, rename, replace, skip); liefert den Pfad am Ziel (null = uebersprungen). */
    fun transfer(from: String, to: String, copy: Boolean, conflict: String = "fail"): String? {
        val a = api()
        val r = a.postText(a.url("/api/files/move"), a.json.encodeToString(MoveBody.serializer(), MoveBody(from, to, copy, conflict)))
        invalidate(parent(from), parent(to), from)
        if (!copy) forgetMeta(from)
        if (!copy && offline.state.value.roots.keys.any { Offline.inside(it, from) }) offline.moved(from, to)
        if (!copy) afterChange(from)
        afterChange(to)
        return try { a.json.decodeFromString(MoveResult.serializer(), r).let { if (it.skipped) null else it.path ?: to } } catch (_: Exception) { to }
    }

    // Papierkorb
    fun trash(): TrashList { val a = api(); return a.get(a.url("/api/files/trash")) }
    fun restore(ids: List<Long>) { val a = api(); a.postText(a.url("/api/files/trash/restore"), a.json.encodeToString(IdsBody.serializer(), IdsBody(ids))); listings.clear() }
    fun purge(ids: List<Long>) { val a = api(); a.postText(a.url("/api/files/trash/delete"), a.json.encodeToString(IdsBody.serializer(), IdsBody(ids))) }
    fun emptyTrash() { val a = api(); a.postText(a.url("/api/files/trash/empty"), null) }

    // Fruehere Versionen
    fun versions(path: String): List<FileVersion> { val a = api(); return a.get<VersionList>(a.url("/api/files/versions", "path" to path)).items }
    fun restoreVersion(path: String, id: Long) {
        val a = api(); a.postText(a.url("/api/files/versions/restore"), a.json.encodeToString(VersionBody.serializer(), VersionBody(path, id))); invalidate(parent(path))
    }
    /** Groesse der Version unbekannt: immer am Umschlag vorbei (mit Ticket); ein Server ohne Tickets liefert sie im Umschlag. */
    fun downloadVersion(path: String, id: Long, dest: File) = tooOld { api().downloadFile(path, dest, big = true, version = id, envelopeOk = true) }

    // Oeffentliche Links
    fun links(path: String? = null): List<FileLink> { val a = api(); return a.get<LinkList>(a.url("/api/files/links", "path" to path)).items }
    fun createLink(path: String, mode: String, expires: String?, password: String?, maxDownloads: Int): FileLink {
        val a = api()
        val l: FileLink = a.json.decodeFromString(a.postText(a.url("/api/files/links"), a.json.encodeToString(LinkBody.serializer(), LinkBody(path, mode, expires, password, maxDownloads))))
        invalidate(parent(path))
        return l
    }
    fun updateLink(l: FileLink, mode: String, expires: String?, password: String?, maxDownloads: Int) {
        val a = api(); a.putText(a.url("/api/files/links/" + l.id), a.json.encodeToString(LinkBody.serializer(), LinkBody(null, mode, expires, password, maxDownloads)))
    }
    fun deleteLink(id: String) { val a = api(); a.delete(a.url("/api/files/links/$id")) }
    /** Adresse fuer andere: /s/<token> auf dem Server. */
    fun publicUrl(l: FileLink): String = (App.get(context).prefs.account.value?.baseUrl ?: "") + "/s/" + l.token

    // Vorschaubilder und Streamen
    /** Vorschaubild vom Server (WebP), [size] 256 oder 1600; null = keines. */
    fun thumb(item: FileItem, size: Int): ByteArray? = try {
        val a = api(); a.getBytes(a.url("/api/files/thumb", "path" to item.path, "size" to size, "v" to item.etag)).first
    } catch (e: ApiException) { if (e.code == 404 || e.code == 400 || e.code == 415) null else throw e }

    /**
     * Adresse und Kopfzeilen zum Streamen (Audio/Video, direkt ueber TLS mit Bereichsanfragen): mit Umschlag ein Ticket
     * nur fuer diese Datei, nie das App-Passwort. Geht ins Netz, also nicht im Hauptthread.
     */
    fun streamUrl(item: FileItem): Pair<String, Map<String, String>> = tooOld {
        val a = api(); a.streamRequest(a.url("/api/files/download", "path" to item.path, "inline" to true))
    }

    /** Zuletzt geladener Inhalt eines Ordners samt Alter in ms, ohne Netz. */
    fun cached(path: String): Pair<Long, Listing>? = listings[path]?.let { (t, l) -> (System.currentTimeMillis() - t) to l }

    /** Inhalt aus dem Speicher, wenn juenger als [maxAgeMs], sonst vom Server. */
    fun listCached(path: String, maxAgeMs: Long): Listing =
        cached(path)?.takeIf { it.first <= maxAgeMs }?.second ?: list(path)

    /** Eintrag zu einem Pfad (ueber den Elternordner); "" ist die Wurzel. */
    fun find(path: String, maxAgeMs: Long = 10_000): FileItem? {
        if (path.isEmpty()) return FileItem("", "", true)
        val parent = parent(path)
        listCached(parent, maxAgeMs).items.firstOrNull { it.path == path }?.let { return it }
        // Eben angelegt oder umbenannt: einmal frisch nachsehen
        return list(parent).items.firstOrNull { it.path == path }
    }

    fun invalidate(vararg dirs: String) { for (d in dirs) listings.remove(d) }

    /** Gespeicherten Ordnerinhalt als frisch werten (Nachladen fehlgeschlagen: nicht sofort wieder versuchen). */
    fun touch(path: String) { listings[path]?.let { listings[path] = System.currentTimeMillis() to it.second } }

    /** Datei zum Lesen: die lokale Kopie eines Offline-Ordners, sonst in den Cache geladen (gleiche Groesse und Zeit wird wiederverwendet). */
    fun cacheFile(item: FileItem): File {
        if (offline.ready(item.path)) offline.local(item.path).takeIf { it.isFile }?.let { return it }
        val dir = File(context.cacheDir, "files/" + hash(item.path)).apply { mkdirs() }
        val f = File(dir, safeName(item.name))
        val ms = item.modifiedMs
        if (f.exists() && f.length() == item.size && ms > 0 && f.lastModified() == ms) return f
        val tmp = File(dir, f.name + ".part")
        try {
            downloadServer(item, tmp)
            if (!tmp.renameTo(f)) { tmp.copyTo(f, overwrite = true); tmp.delete() }
            if (ms > 0) f.setLastModified(ms)
        } finally { tmp.delete() }
        return f
    }

    fun downloadServer(item: FileItem, dest: File) {
        tooOld { api().downloadFile(item.path, dest, big = item.size > ENVELOPE_MAX) }
    }

    /**
     * Lokale Datei als [name] in den Ordner [dir] hochladen (ersetzt eine gleichnamige Datei). In einem
     * Offline-Ordner kommt sie in die lokale Kopie und der Abgleich laedt sie hoch - so nur einmal ueber das Netz.
     */
    fun upload(dir: String, name: String, mime: String?, file: File) {
        if (offline.ready(dir)) {
            val tmp = File.createTempFile("upload", ".tmp", File(context.cacheDir, "upload").apply { mkdirs() })
            file.copyTo(tmp, overwrite = true)
            offline.writeLocal(join(dir, name), tmp)
            afterChange(dir)
        } else uploadServer(dir, name, mime, file)
        invalidate(dir)
    }

    fun uploadServer(dir: String, name: String, mime: String?, file: File) {
        tooOld { api().uploadFile(dir, name, mime ?: guessMime(name), file, big = file.length() > ENVELOPE_MAX) }
        invalidate(dir)
    }

    /** Inhalt einer content://-Uri (Dateiauswahl, Kamera) hochladen; liefert den Namen am Ziel. */
    fun uploadUri(dir: String, uri: Uri, fallbackName: String? = null): String {
        val cr = context.contentResolver
        var name = fallbackName
        cr.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c -> if (c.moveToFirst() && !c.isNull(0)) name = c.getString(0) }
        val target = uniqueName(listCached(dir, 5_000), name?.takeIf { it.isNotBlank() } ?: "upload")
        val tmp = File.createTempFile("upload", ".tmp", File(context.cacheDir, "upload").apply { mkdirs() })
        try {
            cr.openInputStream(uri)?.use { i -> tmp.outputStream().use { o -> i.copyTo(o) } } ?: throw java.io.FileNotFoundException(uri.toString())
            upload(dir, target, cr.getType(uri), tmp)
        } finally { tmp.delete() }
        return target
    }

    // Aenderungen gehen zuerst an den Server; in Offline-Ordnern zieht der Abgleich danach die lokale Kopie nach

    fun mkdir(path: String) {
        mkdirServer(path)
        afterChange(path)
    }

    fun delete(path: String) {
        deleteServer(path)
        forgetMeta(path)
        // Markierte Ordner darunter (oder der Ordner selbst) sind weg
        if (offline.state.value.roots.keys.any { Offline.inside(it, path) }) offline.drop(path)
        afterChange(path)
    }

    fun move(from: String, to: String, copy: Boolean = false) {
        val a = api()
        a.postText(a.url("/api/files/move"), a.json.encodeToString(MoveBody.serializer(), MoveBody(from, to, copy)))
        invalidate(parent(from), parent(to), from)
        // Markierter Ordner umbenannt/verschoben: Markierung und lokale Kopie wandern mit
        if (!copy && offline.state.value.roots.keys.any { Offline.inside(it, from) }) offline.moved(from, to)
        if (!copy) afterChange(from)
        afterChange(to)
    }

    fun mkdirServer(path: String) {
        val a = api()
        a.post(a.url("/api/files/mkdir"), mapOf("path" to path))
        invalidate(parent(path))
    }

    fun deleteServer(path: String) {
        val a = api()
        a.post(a.url("/api/files/delete"), mapOf("path" to path))
        invalidate(parent(path), path)
    }

    /** Nach einer Server-Aenderung: den betroffenen Offline-Ordner gleich abgleichen (Fehler holt der Worker nach). */
    private fun afterChange(path: String) {
        if (offline.rootOf(path) == null) return
        try { offline.sync(path) } catch (e: Exception) { OfflineWorker.now(context) }
    }

    /** Alles Zwischengespeicherte weg (nach "Konto entfernen"). */
    fun clear() {
        listings.clear()
        File(context.cacheDir, "files").deleteRecursively()
        File(context.cacheDir, "docs").deleteRecursively()
        File(context.cacheDir, "upload").deleteRecursively()
        File(context.cacheDir, "camera").deleteRecursively()
        File(context.cacheDir, "offline-tmp").deleteRecursively()
        Thumbs.clear(context)
        offline.clear()
    }

    @Serializable private data class MoveBody(val from: String, val to: String, val copy: Boolean, val conflict: String? = null)
    @Serializable private data class MoveResult(val path: String? = null, val skipped: Boolean = false)
    @Serializable private data class FavBody(val path: String, val on: Boolean)
    @Serializable private data class PathsBody(val paths: List<String>)
    @Serializable private data class TrashIds(val trash: List<Long> = emptyList())
    @Serializable private data class IdsBody(val ids: List<Long>)
    @Serializable private data class VersionBody(val path: String, val id: Long)
    @Serializable private data class LinkBody(val path: String?, val mode: String, val expires: String?, val password: String?, val maxDownloads: Int)

    companion object {
        /**
         * Bis zu dieser Groesse gehen Dateiinhalte im verschluesselten Umschlag; groessere direkt ueber TLS
         * (wie die Download-Links im Web), weil der Umschlag den ganzen Inhalt mehrfach im Speicher haelt
         * und die App bei einigen zehn MB abstuerzt.
         */
        const val ENVELOPE_MAX = 8L * 1024 * 1024

        fun parent(path: String): String = path.substringBeforeLast('/', "")
        fun join(dir: String, name: String): String = if (dir.isEmpty()) name else "$dir/$name"
        fun nameOf(path: String): String = path.substringAfterLast('/')

        fun guessMime(name: String): String =
            MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"

        /** Freier Name im Ordner: "Bild.jpg" -> "Bild (1).jpg", wenn es "Bild.jpg" schon gibt. */
        fun uniqueName(l: Listing, name: String): String {
            val taken = l.items.map { it.name.lowercase() }.toSet()
            if (name.lowercase() !in taken) return name
            val dot = name.lastIndexOf('.').takeIf { it > 0 } ?: name.length
            val base = name.substring(0, dot); val ext = name.substring(dot)
            var i = 1
            while ("$base ($i)$ext".lowercase() in taken) i++
            return "$base ($i)$ext"
        }

        fun safeName(name: String): String = name.ifBlank { "datei" }.replace(Regex("[\\\\/:*?\"<>|\\u0000-\\u001f]"), "_")

        private fun hash(s: String): String = MessageDigest.getInstance("SHA-1").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }.take(16)
    }
}
