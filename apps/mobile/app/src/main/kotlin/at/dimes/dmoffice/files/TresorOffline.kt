package at.dimes.dmoffice.files

import android.content.Context
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.io.IOException

/**
 * Offline verfügbare Ordner im Tresor (docs/tresor-cryptomator-konzept.md, 4 - Stufe 7b). Mit Netz entschlüsselt der Server wie
 * sonst auch. Die App hält markierte Ordner zusätzlich verschlüsselt im Cryptomator-Format vor: welche Dateien dazugehören, sagt der
 * offene Tresor (`/api/tresor/vault/offline`), abgeglichen über ETags. Ohne Netz öffnet die App diese Kopie mit dem Tresor-Passwort
 * (masterkey.cryptomator liegt mit dabei) und entschlüsselt selbst, mit cryptolib. Auf dem Gerät liegt kein Klartext; den
 * Schlüssel hält die App nur im Arbeitsspeicher, bis der Tresor geschlossen wird. Vorschaubilder gibt es ohne Netz nicht.
 */
object TresorOffline {
    @Serializable private data class State(val roots: List<String> = emptyList(), val etags: Map<String, String> = emptyMap())
    @Serializable private data class Item(val path: String, val size: Long = 0, val etag: String? = null)
    @Serializable private data class Items(val items: List<Item> = emptyList())

    /** Ohne Netz, Kopie noch nicht geöffnet: erst mit dem Tresor-Passwort öffnen. */
    class NeedPassword : IOException("Tresor ohne Netz: mit dem Tresor-Passwort öffnen")

    private val json = Json { ignoreUnknownKeys = true }
    /** Markierte Ordner („tresor:/A/B“). */
    val roots = MutableStateFlow<Set<String>>(emptySet())
    @Volatile private var reader: VaultReader? = null
    @Volatile private var loaded = false

    private fun dir(ctx: Context) = File(ctx.filesDir, "tresor-vault")
    private fun stateFile(ctx: Context) = File(ctx.filesDir, "tresor-vault.json")

    @Synchronized private fun load(ctx: Context): State {
        val s = try { json.decodeFromString<State>(stateFile(ctx).readText()) } catch (_: Exception) { State() }
        if (!loaded) { roots.value = s.roots.toSet(); loaded = true }
        return s
    }
    @Synchronized private fun save(ctx: Context, s: State) {
        val tmp = File(ctx.filesDir, "tresor-vault.json.tmp")
        tmp.writeText(json.encodeToString(State.serializer(), s))
        if (!tmp.renameTo(stateFile(ctx))) { tmp.copyTo(stateFile(ctx), true); tmp.delete() }
        roots.value = s.roots.toSet()
    }

    fun init(ctx: Context) { load(ctx) }
    /** Liegt [path] in einem offline verfügbaren Ordner oder auf dem Weg dorthin? */
    fun covers(path: String) = path == FilesRepo.TRESOR || roots.value.any { path == it || path.startsWith("$it/") || it.startsWith("$path/") }
    private fun has(ctx: Context) = load(ctx).roots.isNotEmpty() && File(dir(ctx), "masterkey.cryptomator").isFile

    /** Ordner markieren und gleich holen (der Tresor muss offen sein). */
    fun mark(ctx: Context, api: ApiClient, path: String) {
        val s = load(ctx)
        save(ctx, s.copy(roots = (s.roots + path).distinct()))
        try { sync(ctx, api) } catch (e: Exception) { save(ctx, load(ctx).let { it.copy(roots = it.roots - path) }); throw e }
    }

    /** Markierung weg; die Kopie wird aus den übrigen Ordnern neu gebildet (oder ganz gelöscht). */
    fun unmark(ctx: Context, api: ApiClient?, path: String) {
        val s = load(ctx)
        val rest = s.roots - path
        save(ctx, s.copy(roots = rest))
        if (rest.isEmpty()) { dir(ctx).deleteRecursively(); save(ctx, State()); forgetKey() }
        else if (api != null) try { sync(ctx, api) } catch (_: Exception) { /* beim nächsten Abgleich */ }
    }

    /** Kopie abgleichen. Geschlossener Tresor (423): nichts tun, beim nächsten Mal. */
    @Synchronized fun sync(ctx: Context, api: ApiClient) {
        val s = load(ctx)
        if (s.roots.isEmpty()) return
        val want = HashMap<String, String?>()
        for (root in s.roots) {
            val items = try { json.decodeFromString<Items>(api.getText(api.url("/api/tresor/vault/offline", "path" to root))).items }
            catch (e: ApiException) { if (e.code == 423) return; if (e.code == 404) continue; throw e }
            for (i in items) want[i.path] = i.etag
        }
        val base = dir(ctx).apply { mkdirs() }
        val etags = s.etags.toMutableMap()
        for ((path, etag) in want) {
            val f = File(base, path)
            if (!f.canonicalPath.startsWith(base.canonicalPath + File.separator)) continue
            if (f.isFile && etag != null && etags[path] == etag) continue
            f.parentFile?.mkdirs()
            val tmp = File(f.parentFile, f.name + ".part")
            try {
                api.downloadDirect(api.url("/api/tresor/vault/file", "path" to path), tmp)
                if (!tmp.renameTo(f)) { tmp.copyTo(f, true); tmp.delete() }
            } finally { tmp.delete() }
            if (etag != null) etags[path] = etag
        }
        // Was nicht mehr dazugehört, weg
        base.walkBottomUp().forEach { f ->
            if (f == base) return@forEach
            val r = f.relativeTo(base).invariantSeparatorsPath
            if (f.isFile && r !in want) f.delete()
            else if (f.isDirectory && f.list()?.isEmpty() == true) f.delete()
        }
        save(ctx, load(ctx).copy(etags = etags.filterKeys { it in want }))
        // Der Leser merkt sich Ordner-Kennungen: nach dem Abgleich frisch ansetzen, mit demselben Schlüssel
        reader?.let { r -> reader = r.reopen(LocalSource(base)); r.close() }
    }

    private class LocalSource(val base: File) : VaultReader.Source {
        override fun list(dir: String) = File(base, dir).takeIf { it.isDirectory }?.listFiles()
            ?.filter { !it.name.endsWith(".part") }
            ?.map { VaultReader.Raw(it.name, it.isDirectory, if (it.isFile) it.length() else 0, null, java.time.Instant.ofEpochMilli(it.lastModified()).toString()) }
        override fun open(path: String) = File(base, path).inputStream()
    }

    /** Ohne Netz öffnen: masterkey.cryptomator der Kopie mit dem Tresor-Passwort (scrypt). Falsches Passwort wirft. */
    @Synchronized fun unlock(ctx: Context, password: String) {
        val k = File(dir(ctx), "masterkey.cryptomator").inputStream().use { VaultReader.keyFromFile(it, password) }
        reader?.close()
        reader = VaultReader(LocalSource(dir(ctx)), k).also { k.fill(0) }
    }

    /** Tresor geschlossen: Schlüssel vergessen. */
    @Synchronized fun forgetKey() { reader?.close(); reader = null }

    @Synchronized private fun reader(ctx: Context): VaultReader = reader ?: throw NeedPassword()

    private fun rel(path: String) = path.removePrefix(FilesRepo.TRESOR)

    /** Inhalt eines Ordners aus der Kopie; null, wenn er nicht dazugehört. */
    fun list(ctx: Context, path: String): Listing? {
        if (!has(ctx) || !covers(path)) return null
        val l = reader(ctx).list(rel(path)) ?: return null
        val base = if (path == FilesRepo.TRESOR) FilesRepo.TRESOR else path
        val items = l.filter { !(rel(path).trim('/').isEmpty() && it.name == ".dmoffice") }
            .map { FileItem("$base/${it.name}", it.name, it.isDir, it.size, if (it.isDir) null else FilesRepo.guessMime(it.name), it.modified) }
            .sortedWith(compareBy<FileItem>({ !it.isDir }, { it.name.lowercase() }))
        return Listing(path, items = items, cached = true)
    }

    /** Datei aus der Kopie entschlüsseln nach [dest]; false, wenn sie nicht dazugehört. */
    fun decryptTo(ctx: Context, path: String, dest: File): Boolean {
        if (!has(ctx) || !covers(path)) return false
        reader(ctx).open(rel(path)).use { i -> dest.outputStream().use { o -> i.copyTo(o) } }
        return true
    }
}
