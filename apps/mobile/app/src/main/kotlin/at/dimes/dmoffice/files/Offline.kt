package at.dimes.dmoffice.files

import android.content.Context
import android.util.Log
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.ApiException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/**
 * Offline verfuegbare Ordner: eine lokale Kopie unter filesDir/offline/data, im Hintergrund in beide
 * Richtungen abgeglichen. Fuer jeden Pfad merkt sich [Entry] den Stand beim letzten Abgleich (Zeit und
 * Groesse auf dem Server, Zeit der lokalen Datei); daran erkennt der Abgleich, welche Seite sich geaendert
 * hat. Haben sich beide geaendert, bleibt die Serverfassung unter dem Namen, die lokale kommt als
 * Konfliktkopie daneben - es wird nie etwas still ueberschrieben.
 */
class Offline(private val context: Context, private val repo: FilesRepo) {
    @Serializable data class Entry(val dir: Boolean, val serverMs: Long = 0, val size: Long = 0, val localMs: Long = 0)
    @Serializable data class Root(val lastSync: Long = 0, val error: String? = null)
    @Serializable data class State(val roots: Map<String, Root> = emptyMap(), val entries: Map<String, Entry> = emptyMap(), val usage: Long = 0, val quota: Long = 0)

    private val json = Json { ignoreUnknownKeys = true }
    private val base = File(context.filesDir, "offline")
    private val dataDir = File(base, "data")
    private val stateFile = File(base, "state.json")
    private val lock = ReentrantLock()

    private val _state = MutableStateFlow(load())
    val state: StateFlow<State> = _state
    private val _syncing = MutableStateFlow<Set<String>>(emptySet())
    val syncing: StateFlow<Set<String>> = _syncing

    private fun load(): State = try { if (stateFile.exists()) json.decodeFromString(State.serializer(), stateFile.readText()) else State() } catch (_: Exception) { State() }

    private fun save(s: State) {
        base.mkdirs()
        val tmp = File(base, "state.json.tmp")
        tmp.writeText(json.encodeToString(State.serializer(), s))
        if (!tmp.renameTo(stateFile)) { stateFile.delete(); tmp.renameTo(stateFile) }
        _state.value = s
    }

    private fun update(block: (State) -> State) = lock.withLock { save(block(_state.value)) }

    /** Markierter Ordner, in dem [path] liegt (oder der [path] ist), sonst null. */
    fun rootOf(path: String): String? = _state.value.roots.keys.firstOrNull { inside(path, it) }

    /** Liegt eine lokale Kopie vor (mindestens einmal abgeglichen)? Vorher kommt alles vom Server. */
    fun ready(path: String): Boolean = rootOf(path)?.let { (_state.value.roots[it]?.lastSync ?: 0) > 0 } == true

    fun local(path: String): File {
        val f = if (path.isEmpty()) dataDir else File(dataDir, path)
        // Nie aus dem Datenordner ausbrechen (".." in path): sonst erreichten fremde Apps ueber den
        // DocumentsProvider oder ein boesartiger Server App-interne Dateien.
        val root = dataDir.canonicalPath
        val canon = f.canonicalPath
        require(canon == root || canon.startsWith(root + File.separator)) { "Pfad außerhalb des Datenordners" }
        return f
    }

    /** Letzte bekannte Belegung (fuer Ordnerlisten aus der lokalen Kopie). */
    fun rememberUsage(usage: Long, quota: Long) {
        val s = _state.value
        if (s.usage != usage || s.quota != quota) update { it.copy(usage = usage, quota = quota) }
    }

    /** Ordnerinhalt aus der lokalen Kopie, im Format des Servers. */
    fun listLocal(path: String): Listing {
        val s = _state.value
        val items = (local(path).listFiles() ?: emptyArray()).map { f ->
            val p = FilesRepo.join(path, f.name)
            FileItem(p, f.name, f.isDirectory, if (f.isDirectory) 0 else f.length(), if (f.isDirectory) null else FilesRepo.guessMime(f.name),
                java.time.Instant.ofEpochMilli(s.entries[p]?.takeIf { it.localMs == f.lastModified() }?.serverMs ?: f.lastModified()).toString())
        }.sortedWith(compareBy<FileItem>({ !it.isDir }, { it.name.lowercase() }))
        return Listing(path, s.usage, s.quota, items)
    }

    /** Ohne Netz: Ordnerinhalt mit den Unterordnern, die zu markierten Ordnern fuehren (null, wenn keiner darunter liegt). */
    fun pathTo(path: String): Listing? {
        val s = _state.value
        val next = s.roots.keys.filter { it != path && inside(it, path) }
            .map { r -> FilesRepo.join(path, r.removePrefix(if (path.isEmpty()) "" else "$path/").substringBefore('/')) }.distinct()
        if (next.isEmpty()) return null
        return Listing(path, s.usage, s.quota, next.sortedBy { it.lowercase() }.map { FileItem(it, FilesRepo.nameOf(it), true) })
    }

    /** Ordner offline verfuegbar machen; darin liegende markierte Ordner gehen im neuen auf. */
    fun mark(path: String) {
        if (rootOf(path) != null) return
        update { s -> s.copy(roots = s.roots.filterKeys { !inside(it, path) } + (path to Root())) }
        OfflineWorker.now(context)
    }

    /** Nicht mehr offline: erst abgleichen, damit lokale Aenderungen nicht verloren gehen, dann die Kopie loeschen. */
    fun unmark(path: String) {
        if (_state.value.roots[path] == null) return
        val pending = pendingChanges(path)
        if (pending) sync(path)
        drop(path)
    }

    /** Markierung und lokale Kopie unterhalb von [path] verwerfen (ohne Abgleich). */
    fun drop(path: String) = lock.withLock {
        local(path).deleteRecursively()
        update { s -> s.copy(roots = s.roots.filterKeys { !inside(it, path) }, entries = s.entries.filterKeys { !inside(it, path) }) }
        repo.invalidate(path, FilesRepo.parent(path))
    }

    /** Ein markierter Ordner wurde auf dem Server umbenannt/verschoben: Markierung und Kopie mitnehmen. */
    fun moved(from: String, to: String) = lock.withLock {
        val s = _state.value
        if (s.roots.keys.none { inside(it, from) } && s.entries.keys.none { inside(it, from) }) return@withLock
        val src = local(from)
        if (src.exists()) { local(FilesRepo.parent(to)).mkdirs(); if (!src.renameTo(local(to))) src.deleteRecursively() }
        fun re(p: String) = if (inside(p, from)) to + p.substring(from.length) else p
        // Unter einem anderen markierten Ordner gilt dessen Markierung; eigene Markierung dann nicht noetig
        val roots = s.roots.mapKeys { re(it.key) }.filterKeys { k -> s.roots.keys.map(::re).none { it != k && inside(k, it) } }
        save(s.copy(roots = roots, entries = s.entries.mapKeys { re(it.key) }))
    }

    /** Hat sich lokal unter [path] etwas geaendert, das noch nicht auf dem Server ist? */
    fun pendingChanges(path: String): Boolean {
        val s = _state.value
        val root = local(path)
        if (!root.exists()) return false
        val files = root.walkTopDown().filter { it != root }.toList()
        for (f in files) {
            val p = f.relativeTo(dataDir).invariantSeparatorsPath
            val e = s.entries[p] ?: return true
            if (!f.isDirectory && (e.localMs != f.lastModified() || e.size != f.length())) return true
        }
        // Lokal geloescht, aber noch auf dem Server
        return s.entries.keys.any { inside(it, path) && it != path && !local(it).exists() }
    }

    /** Geschriebene Datei (DocumentsProvider) in die lokale Kopie uebernehmen; der Abgleich laedt sie hoch. */
    fun writeLocal(path: String, content: File) = lock.withLock {
        val dest = local(path)
        dest.parentFile?.mkdirs()
        if (!content.renameTo(dest)) { content.copyTo(dest, overwrite = true); content.delete() }
        repo.invalidate(FilesRepo.parent(path))
    }

    /** Leere Datei bzw. Ordner lokal anlegen (offline moeglich); der Abgleich bringt sie auf den Server. */
    fun createLocal(path: String, dir: Boolean) = lock.withLock {
        val f = local(path)
        f.parentFile?.mkdirs()
        if (dir) f.mkdirs() else f.createNewFile()
        repo.invalidate(FilesRepo.parent(path))
    }

    /** Alle markierten Ordner abgleichen; true, wenn alles geklappt hat. */
    fun syncAll(): Boolean {
        var ok = true
        for (r in _state.value.roots.keys.toList()) try { sync(r) } catch (e: Exception) { ok = false; Log.w(TAG, "Abgleich $r", e) }
        return ok
    }

    /** Einen markierten Ordner (oder den, in dem [path] liegt) jetzt abgleichen; wirft bei Netzfehlern. */
    fun sync(path: String) {
        val root = rootOf(path) ?: return
        _syncing.value = _syncing.value + root
        val touched = mutableSetOf<String>()
        try {
            lock.withLock {
                try {
                    syncDir(root, touched)
                    update { s -> s.copy(roots = s.roots + (root to Root(System.currentTimeMillis(), null))) }
                } catch (e: ApiException) {
                    if (e.code == 404 && !local(root).let { it.exists() && pendingChanges(root) }) {
                        // Ordner auf dem Server geloescht und lokal nichts Neues: Markierung aufgeben
                        drop(root)
                    } else { fail(root, e); throw e }
                } catch (e: Exception) { fail(root, e); throw e }
            }
        } finally {
            _syncing.value = _syncing.value - root
            for (d in touched) { repo.invalidate(d); FilesProvider.notifyChildren(context, d) }
        }
    }

    private fun fail(root: String, e: Exception) {
        update { s -> s.copy(roots = s.roots + (root to (s.roots[root] ?: Root()).copy(error = e.message ?: e.toString()))) }
    }

    /** Ein Ordner: Server und lokale Kopie vergleichen, Unterschiede ausgleichen, dann die Unterordner. */
    private fun syncDir(dir: String, touched: MutableSet<String>) {
        val server = repo.listServer(dir).items.associateBy { it.path }
        val localDir = local(dir).apply { mkdirs() }
        val locals = (localDir.listFiles() ?: emptyArray()).associateBy { FilesRepo.join(dir, it.name) }
        val entries = _state.value.entries.toMutableMap()
        val names = (server.keys + locals.keys + entries.keys.filter { FilesRepo.parent(it) == dir && it != dir }).toSortedSet()
        val uploaded = mutableListOf<String>()
        val subdirs = mutableListOf<String>()
        var serverChanged = false
        var localChanged = false

        if (dir.isNotEmpty() && entries[dir] == null) entries[dir] = Entry(true)
        for (p in names) {
            val s = server[p]; val l = locals[p]; val e = entries[p]
            val name = FilesRepo.nameOf(p)
            if (s?.isDir == true || l?.isDirectory == true || (s == null && l == null && e?.dir == true)) {
                when {
                    s != null && l != null && s.isDir && l.isDirectory -> { entries[p] = Entry(true); subdirs += p }
                    s != null && l != null -> {
                        // Datei und Ordner gleichen Namens: lokale Seite zur Seite legen, im naechsten Durchgang hochladen
                        val c = conflictName(dir, name, server.keys + locals.keys)
                        l.renameTo(local(c)); localChanged = true
                        entries.remove(p)
                    }
                    s != null -> if (e != null) {
                        // Lokal geloescht -> auf dem Server loeschen
                        repo.deleteServer(p); serverChanged = true
                        entries.keys.removeAll { inside(it, p) }
                    } else { local(p).mkdirs(); entries[p] = Entry(true); subdirs += p; localChanged = true }
                    l != null -> if (e != null) {
                        // Auf dem Server geloescht -> lokal loeschen, aber nur, wenn darin nichts Neues liegt
                        if (pendingChangesIn(l, entries)) { repo.mkdirServer(p); serverChanged = true; entries[p] = Entry(true); subdirs += p }
                        else { l.deleteRecursively(); entries.keys.removeAll { inside(it, p) }; localChanged = true }
                    } else { repo.mkdirServer(p); serverChanged = true; entries[p] = Entry(true); subdirs += p }
                    else -> entries.keys.removeAll { inside(it, p) }
                }
                continue
            }
            val serverNew = s != null && (e == null || s.modifiedMs != e.serverMs || s.size != e.size)
            val localNew = l != null && (e == null || l.lastModified() != e.localMs || l.length() != e.size)
            when {
                s != null && l != null -> when {
                    e == null && s.size == l.length() -> entries[p] = Entry(false, s.modifiedMs, s.size, l.lastModified())
                    serverNew && localNew -> {
                        val c = conflictName(dir, name, server.keys + locals.keys)
                        l.renameTo(local(c))
                        repo.uploadServer(dir, FilesRepo.nameOf(c), FilesRepo.guessMime(c), local(c)); uploaded += c; serverChanged = true
                        download(s, entries); localChanged = true
                    }
                    serverNew -> { download(s, entries); localChanged = true }
                    localNew -> { repo.uploadServer(dir, name, FilesRepo.guessMime(name), l); uploaded += p; serverChanged = true }
                }
                s != null -> if (e != null && !serverNew) { repo.deleteServer(p); entries.remove(p); serverChanged = true }
                    else { download(s, entries); localChanged = true }
                l != null -> if (e != null && !localNew) { l.delete(); entries.remove(p); localChanged = true }
                    else { repo.uploadServer(dir, name, FilesRepo.guessMime(name), l); uploaded += p; serverChanged = true }
                else -> entries.remove(p)
            }
        }
        if (uploaded.isNotEmpty()) {
            // Zeit und Groesse vergibt der Server beim Hochladen: frisch holen
            val fresh = repo.listServer(dir).items.associateBy { it.path }
            for (p in uploaded) fresh[p]?.let { entries[p] = Entry(false, it.modifiedMs, it.size, local(p).lastModified()) }
        }
        if (serverChanged || localChanged) touched += dir
        lock.withLock { save(_state.value.copy(entries = entries)) }
        for (d in subdirs) syncDir(d, touched)
    }

    private fun pendingChangesIn(dir: File, entries: Map<String, Entry>): Boolean =
        dir.walkTopDown().filter { it.isFile }.any { f ->
            val e = entries[f.relativeTo(dataDir).invariantSeparatorsPath]
            e == null || e.localMs != f.lastModified() || e.size != f.length()
        }

    private fun download(s: FileItem, entries: MutableMap<String, Entry>) {
        val dest = local(s.path)
        // Temporaer im Cache (gleiches Dateisystem), damit ein abgebrochener Download nicht in der Kopie liegen bleibt
        val tmp = File.createTempFile("offline", ".part", File(context.cacheDir, "offline-tmp").apply { mkdirs() })
        try {
            repo.downloadServer(s, tmp)
            dest.delete()
            if (!tmp.renameTo(dest)) throw IOException("Umbenennen fehlgeschlagen: ${dest.name}")
            if (s.modifiedMs > 0) dest.setLastModified(s.modifiedMs)
            entries[s.path] = Entry(false, s.modifiedMs, s.size, dest.lastModified())
        } finally { tmp.delete() }
    }

    /** "Bericht.docx" -> "Bericht (Konflikt 2026-10-20 10.15).docx", frei im Ordner. */
    private fun conflictName(dir: String, name: String, taken: Set<String>): String {
        val stamp = java.time.LocalDateTime.now().format(java.time.format.DateTimeFormatter.ofPattern("yyyy-MM-dd HH.mm"))
        val dot = name.lastIndexOf('.').takeIf { it > 0 } ?: name.length
        val base = name.substring(0, dot) + " (" + context.getString(R.string.files_conflict) + " " + stamp
        val ext = name.substring(dot)
        val lower = taken.map { it.lowercase() }.toSet()
        var i = 1
        var c = FilesRepo.join(dir, "$base)$ext")
        while (c.lowercase() in lower || local(c).exists()) { i++; c = FilesRepo.join(dir, "$base $i)$ext") }
        return c
    }

    /** Alles weg (nach "Konto entfernen"). */
    fun clear() = lock.withLock {
        OfflineWorker.cancel(context)
        base.deleteRecursively()
        _state.value = State()
    }

    companion object {
        private const val TAG = "Offline"
        fun inside(path: String, root: String) = root.isEmpty() || path == root || path.startsWith("$root/")
    }
}

/** Abgleich der Offline-Ordner: stuendlich mit Netz und gleich nach Aenderungen (Markieren, Schreiben). */
class OfflineWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val app = App.get(applicationContext)
        if (app.prefs.account.value == null) return Result.success()
        val ok = app.files.offline.syncAll()
        return if (ok) Result.success() else if (runAttemptCount < 5) Result.retry() else Result.failure()
    }

    companion object {
        private const val PERIODIC = "files-offline-periodic"
        private const val NOW = "files-offline-now"
        private val net get() = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()

        fun schedule(context: Context) {
            val req = PeriodicWorkRequestBuilder<OfflineWorker>(1, TimeUnit.HOURS).setConstraints(net).build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(PERIODIC, ExistingPeriodicWorkPolicy.KEEP, req)
        }

        fun now(context: Context) {
            schedule(context)
            val req = OneTimeWorkRequestBuilder<OfflineWorker>().setConstraints(net).build()
            WorkManager.getInstance(context).enqueueUniqueWork(NOW, ExistingWorkPolicy.APPEND_OR_REPLACE, req)
        }

        fun cancel(context: Context) {
            WorkManager.getInstance(context).cancelUniqueWork(PERIODIC)
            WorkManager.getInstance(context).cancelUniqueWork(NOW)
        }
    }
}
