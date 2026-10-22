package at.dimes.dmoffice.files

import android.content.Context
import android.database.Cursor
import android.database.MatrixCursor
import android.os.Bundle
import android.os.CancellationSignal
import android.os.Handler
import android.os.HandlerThread
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract
import android.provider.DocumentsContract.Document
import android.provider.DocumentsContract.Root
import android.provider.DocumentsProvider
import android.util.Log
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import java.io.File
import java.io.FileNotFoundException

/**
 * Die Dateiablage als Speicherort in Android (Storage Access Framework): Wurzel "dmOffice" in der
 * App "Dateien" und in jedem Oeffnen-/Speichern-Dialog. Dokument-IDs sind "/" + Pfad in der Ablage.
 * Lesen laedt die Datei in den Cache; Schreiben geht in eine temporaere Datei, die beim Schliessen
 * hochgeladen wird. Ohne gekoppeltes Konto gibt es keine Wurzel.
 */
class FilesProvider : DocumentsProvider() {
    private val app: App get() = App.get(context!!)
    private val repo: FilesRepo get() = app.files

    override fun onCreate(): Boolean = true

    override fun queryRoots(projection: Array<out String>?): Cursor {
        val c = MatrixCursor(projection ?: ROOT_COLS)
        val ctx = context!!
        c.setNotificationUri(ctx.contentResolver, DocumentsContract.buildRootsUri(authority(ctx)))
        val a = app.prefs.account.value ?: return c
        val host = try { android.net.Uri.parse(a.baseUrl).host } catch (_: Exception) { null }
        val free = repo.cached("")?.second?.takeIf { it.quota > 0 }?.let { (it.quota - it.usage).coerceAtLeast(0) }
        c.newRow().apply {
            add(Root.COLUMN_ROOT_ID, ROOT_ID)
            add(Root.COLUMN_DOCUMENT_ID, ROOT_DOC)
            add(Root.COLUMN_TITLE, ctx.getString(R.string.app_name))
            add(Root.COLUMN_SUMMARY, if (host != null) "${a.login}@$host" else a.login)
            add(Root.COLUMN_ICON, R.mipmap.ic_launcher)
            add(Root.COLUMN_FLAGS, Root.FLAG_SUPPORTS_CREATE or Root.FLAG_SUPPORTS_IS_CHILD)
            if (free != null) add(Root.COLUMN_AVAILABLE_BYTES, free)
        }
        return c
    }

    override fun queryDocument(documentId: String, projection: Array<out String>?): Cursor {
        val c = MatrixCursor(projection ?: DOC_COLS)
        val path = pathOf(documentId)
        val item = try { repo.find(path) } catch (e: Exception) { throw FileNotFoundException(e.message) } ?: throw FileNotFoundException(documentId)
        addRow(c, item, if (path.isEmpty()) context!!.getString(R.string.app_name) else item.name)
        return c
    }

    override fun queryChildDocuments(parentDocumentId: String, projection: Array<out String>?, sortOrder: String?): Cursor {
        val ctx = context!!
        val path = pathOf(parentDocumentId)
        val uri = DocumentsContract.buildChildDocumentsUri(authority(ctx), parentDocumentId)
        val cached = repo.cached(path)
        var loading = false
        var error: String? = null
        val listing = when {
            cached != null && cached.first < FRESH_MS -> cached.second
            cached != null -> {
                // Gleich das Bekannte zeigen, im Hintergrund neu laden und dann melden
                loading = true
                scope.launch {
                    // Fehlschlag (kein Netz): das Bekannte als frisch werten, sonst laedt die Ansicht endlos neu
                    try { repo.list(path) } catch (e: Exception) { Log.w(TAG, "list $path", e); repo.touch(path) }
                    ctx.contentResolver.notifyChange(uri, null)
                }
                cached.second
            }
            else -> try { repo.list(path) } catch (e: Exception) { error = e.message ?: e.toString(); null }
        }
        val extras = Bundle().apply {
            if (loading) putBoolean(DocumentsContract.EXTRA_LOADING, true)
            if (error != null) putString(DocumentsContract.EXTRA_ERROR, error)
        }
        val c = object : MatrixCursor(projection ?: DOC_COLS) { override fun getExtras(): Bundle = extras }
        listing?.items?.forEach { addRow(c, it, it.name) }
        c.setNotificationUri(ctx.contentResolver, uri)
        return c
    }

    override fun openDocument(documentId: String, mode: String, signal: CancellationSignal?): ParcelFileDescriptor {
        val ctx = context!!
        val path = pathOf(documentId)
        val item = try { repo.find(path) } catch (e: Exception) { throw FileNotFoundException(e.message) }
        if (item == null || item.isDir) throw FileNotFoundException(documentId)
        val pfdMode = ParcelFileDescriptor.parseMode(mode)
        if (!mode.contains('w') && !mode.contains('a')) {
            val f = try { repo.cacheFile(item) } catch (e: Exception) { throw FileNotFoundException(e.message) }
            return ParcelFileDescriptor.open(f, ParcelFileDescriptor.MODE_READ_ONLY)
        }
        // Schreiben: in eine eigene Datei, beim Schliessen als Ganzes hochladen (die Ablage kennt kein Teil-Schreiben)
        val tmp = File.createTempFile("write", ".tmp", File(ctx.cacheDir, "docs").apply { mkdirs() })
        if (!mode.contains('t') && (mode.contains('r') || mode.contains('a')) && item.size > 0) {
            try { repo.cacheFile(item).copyTo(tmp, overwrite = true) } catch (e: Exception) { tmp.delete(); throw FileNotFoundException(e.message) }
        }
        return ParcelFileDescriptor.open(tmp, pfdMode, handler) { e ->
            scope.launch {
                try {
                    if (e == null && repo.offline.ready(path)) {
                        // Offline-Ordner: in die lokale Kopie (geht auch ohne Netz), hochladen macht der Abgleich
                        repo.offline.writeLocal(path, tmp)
                        OfflineWorker.now(ctx)
                        notifyChildren(ctx, FilesRepo.parent(path))
                    } else if (e == null) {
                        repo.upload(FilesRepo.parent(path), item.name, item.type, tmp)
                        notifyChildren(ctx, FilesRepo.parent(path))
                    } else Log.w(TAG, "Schreiben abgebrochen: $path", e)
                } catch (ex: Exception) {
                    Log.w(TAG, "Hochladen $path", ex)
                    toast(ctx, ctx.getString(R.string.files_upload_failed, item.name, ex.message ?: ex.toString()))
                } finally { tmp.delete() }
            }
        }
    }

    override fun createDocument(parentDocumentId: String, mimeType: String, displayName: String): String {
        val ctx = context!!
        val dir = pathOf(parentDocumentId)
        try {
            val name = FilesRepo.uniqueName(repo.list(dir), FilesRepo.safeName(displayName))
            val path = FilesRepo.join(dir, name)
            if (repo.offline.ready(dir)) {
                // Offline-Ordner: lokal anlegen, der Abgleich bringt es auf den Server
                repo.offline.createLocal(path, mimeType == Document.MIME_TYPE_DIR)
                OfflineWorker.now(ctx)
            } else if (mimeType == Document.MIME_TYPE_DIR) repo.mkdir(path)
            else {
                val empty = File.createTempFile("create", ".tmp", File(ctx.cacheDir, "docs").apply { mkdirs() })
                try { repo.upload(dir, name, mimeType, empty) } finally { empty.delete() }
            }
            notifyChildren(ctx, dir)
            return docId(path)
        } catch (e: Exception) { throw IllegalStateException(e.message, e) }
    }

    override fun deleteDocument(documentId: String) {
        val path = pathOf(documentId)
        try { repo.delete(path) } catch (e: Exception) { throw IllegalStateException(e.message, e) }
        notifyChildren(context!!, FilesRepo.parent(path))
    }

    override fun removeDocument(documentId: String, parentDocumentId: String) = deleteDocument(documentId)

    override fun renameDocument(documentId: String, displayName: String): String {
        val path = pathOf(documentId)
        val dir = FilesRepo.parent(path)
        val target = FilesRepo.join(dir, FilesRepo.safeName(displayName))
        if (target == path) return documentId
        try { repo.move(path, target) } catch (e: Exception) { throw IllegalStateException(e.message, e) }
        notifyChildren(context!!, dir)
        return docId(target)
    }

    override fun moveDocument(sourceDocumentId: String, sourceParentDocumentId: String, targetParentDocumentId: String): String {
        val path = pathOf(sourceDocumentId)
        val target = FilesRepo.join(pathOf(targetParentDocumentId), FilesRepo.nameOf(path))
        try { repo.move(path, target) } catch (e: Exception) { throw IllegalStateException(e.message, e) }
        notifyChildren(context!!, FilesRepo.parent(path)); notifyChildren(context!!, FilesRepo.parent(target))
        return docId(target)
    }

    override fun copyDocument(sourceDocumentId: String, targetParentDocumentId: String): String {
        val path = pathOf(sourceDocumentId)
        val dir = pathOf(targetParentDocumentId)
        try {
            val target = FilesRepo.join(dir, FilesRepo.uniqueName(repo.list(dir), FilesRepo.nameOf(path)))
            repo.move(path, target, copy = true)
            notifyChildren(context!!, dir)
            return docId(target)
        } catch (e: Exception) { throw IllegalStateException(e.message, e) }
    }

    override fun isChildDocument(parentDocumentId: String, documentId: String): Boolean {
        // pathOf weist ".." ab (wirft) - eine ungueltige Id ist dann kein Kind
        val parent = try { pathOf(parentDocumentId) } catch (_: FileNotFoundException) { return false }
        val child = try { pathOf(documentId) } catch (_: FileNotFoundException) { return false }
        return parent.isEmpty() || child.startsWith("$parent/")
    }

    private fun addRow(c: MatrixCursor, item: FileItem, name: String) {
        val flags = if (item.isDir) Document.FLAG_DIR_SUPPORTS_CREATE or (if (item.path.isEmpty()) 0 else EDIT_FLAGS)
        else Document.FLAG_SUPPORTS_WRITE or EDIT_FLAGS
        c.newRow().apply {
            add(Document.COLUMN_DOCUMENT_ID, docId(item.path))
            add(Document.COLUMN_DISPLAY_NAME, name)
            add(Document.COLUMN_MIME_TYPE, if (item.isDir) Document.MIME_TYPE_DIR else item.type)
            add(Document.COLUMN_SIZE, if (item.isDir) null else item.size)
            add(Document.COLUMN_LAST_MODIFIED, item.modifiedMs.takeIf { it > 0 })
            add(Document.COLUMN_FLAGS, flags)
        }
    }

    companion object {
        private const val TAG = "FilesProvider"
        const val ROOT_ID = "dmoffice"
        private const val ROOT_DOC = "/"
        /** So lange gilt ein geladener Ordnerinhalt ohne Nachladen. */
        private const val FRESH_MS = 5_000L
        private const val EDIT_FLAGS = Document.FLAG_SUPPORTS_DELETE or Document.FLAG_SUPPORTS_RENAME or Document.FLAG_SUPPORTS_MOVE or
            Document.FLAG_SUPPORTS_COPY or Document.FLAG_SUPPORTS_REMOVE

        private val ROOT_COLS = arrayOf(Root.COLUMN_ROOT_ID, Root.COLUMN_DOCUMENT_ID, Root.COLUMN_TITLE, Root.COLUMN_SUMMARY, Root.COLUMN_ICON, Root.COLUMN_FLAGS, Root.COLUMN_AVAILABLE_BYTES)
        private val DOC_COLS = arrayOf(Document.COLUMN_DOCUMENT_ID, Document.COLUMN_DISPLAY_NAME, Document.COLUMN_MIME_TYPE, Document.COLUMN_SIZE, Document.COLUMN_LAST_MODIFIED, Document.COLUMN_FLAGS)

        private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        private val handler: Handler by lazy { Handler(HandlerThread("dmoffice-docs").apply { start() }.looper) }

        fun authority(ctx: Context) = ctx.packageName + ".documents"
        fun docId(path: String) = "/$path"
        fun pathOf(documentId: String): String {
            if (!documentId.startsWith("/")) throw FileNotFoundException(documentId)
            val path = documentId.removePrefix("/")
            // Kein Ausbrechen ueber ".." oder Backslash: die Dokument-Id kommt teils aus fremden Apps
            // (buildDocumentUriUsingTree). Jedes Segment muss ein echter Name sein.
            if (path.split('/').any { it == "." || it == ".." || it.contains('\\') || it.contains('\u0000') })
                throw FileNotFoundException(documentId)
            return path
        }

        /** Ordnerinhalt hat sich geaendert (auch aus der App heraus): offene Ansichten neu laden lassen. */
        fun notifyChildren(ctx: Context, dir: String) {
            ctx.contentResolver.notifyChange(DocumentsContract.buildChildDocumentsUri(authority(ctx), docId(dir)), null)
        }

        /** Wurzel ein- bzw. ausblenden, wenn das Konto kommt oder geht; der Cache des alten Kontos wird geleert. */
        fun watchAccount(app: App) {
            scope.launch {
                app.prefs.account.map { it?.let { a -> a.baseUrl + "|" + a.login } }.distinctUntilChanged().drop(1).collect { key ->
                    app.files.clear()
                    app.contentResolver.notifyChange(DocumentsContract.buildRootsUri(authority(app)), null)
                }
            }
        }

        private fun toast(ctx: Context, msg: String) {
            Handler(android.os.Looper.getMainLooper()).post { android.widget.Toast.makeText(ctx, msg, android.widget.Toast.LENGTH_LONG).show() }
        }
    }
}
