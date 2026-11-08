package at.dimes.dmoffice.files

import com.google.common.io.BaseEncoding
import org.cryptomator.cryptolib.api.Cryptor
import org.cryptomator.cryptolib.api.CryptorProvider
import org.cryptomator.cryptolib.api.Masterkey
import org.cryptomator.cryptolib.common.DecryptingReadableByteChannel
import org.cryptomator.cryptolib.common.MasterkeyFileAccess
import java.io.InputStream
import java.nio.channels.Channels
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap

/**
 * Cryptomator-Tresor (Vault Format 8) auf dem Gerät lesen und entschlüsseln (docs/tresor-cryptomator-konzept.md, 4 - Stufe 7b),
 * mit cryptolib. Woher die verschlüsselten Dateien kommen, sagt [Source]: vom Server oder aus der Offline-Kopie.
 * Pfade im Klartext wie „/A/B“, „“ ist die Wurzel. Schreiben geht weiter über den Server.
 */
class VaultReader(private val src: Source, rawKey: ByteArray) : AutoCloseable {
    private val raw = rawKey.copyOf()
    /** Verschlüsselte Dateien relativ zum Tresor („d/AB/CD…/x.c9r“). */
    interface Source {
        /** Inhalt eines Ordners; null = gibt es nicht. */
        fun list(dir: String): List<Raw>?
        fun open(path: String): InputStream
    }
    data class Raw(val name: String, val isDir: Boolean, val size: Long, val etag: String? = null, val modified: String? = null)
    /** Eintrag im Klartext; [cipher] ist die verschlüsselte Datei bzw. der Ordner der Liste, [dirId] bei Ordnern. */
    data class Entry(val name: String, val isDir: Boolean, val size: Long, val modified: String?, val cipher: String, val dirId: String?)

    private val cryptor: Cryptor = CryptorProvider.forScheme(CryptorProvider.Scheme.SIV_GCM).provide(Masterkey(rawKey.copyOf()), SecureRandom())
    private val names = cryptor.fileNameCryptor()
    private val content = cryptor.fileContentCryptor()
    private val header = cryptor.fileHeaderCryptor()
    private val dirIds = ConcurrentHashMap<String, String>().apply { put("", "") }

    /** Ordner der verschlüsselten Einträge zu einer Ordner-Kennung. */
    fun cipherDir(dirId: String): String { val h = names.hashDirectoryId(dirId); return "d/${h.substring(0, 2)}/${h.substring(2)}" }

    private fun parts(path: String) = path.split('/').filter { it.isNotEmpty() }
    private fun norm(path: String) = parts(path).joinToString("/")

    /** Kennung eines Ordners (null = gibt es nicht). */
    fun dirIdOf(path: String): String? {
        val p = norm(path)
        dirIds[p]?.let { return it }
        val parent = p.substringBeforeLast('/', "")
        val leaf = p.substringAfterLast('/')
        val e = list(parent)?.firstOrNull { it.isDir && it.name == leaf } ?: return null
        return e.dirId?.also { dirIds[p] = it }
    }

    /** Inhalt eines Ordners im Klartext; null = gibt es nicht. */
    fun list(path: String): List<Entry>? {
        val p = norm(path)
        val id = dirIdOf(p) ?: return null
        val dir = cipherDir(id)
        val raw = src.list(dir) ?: return null
        val ad = id.toByteArray(Charsets.UTF_8)
        return raw.mapNotNull { r ->
            try {
                when {
                    r.name.endsWith(".c9r") -> {
                        val name = names.decryptFilename(BaseEncoding.base64Url(), r.name.removeSuffix(".c9r"), ad)
                        val at = "$dir/${r.name}"
                        if (r.isDir) Entry(name, true, 0, r.modified, at, readText("$at/dir.c9r"))
                        else Entry(name, false, cleartextSize(r.size), r.modified, at, null)
                    }
                    // Lange Namen: Ordner mit name.c9s, darin dir.c9r (Ordner) oder contents.c9r (Datei)
                    r.name.endsWith(".c9s") && r.isDir -> {
                        val at = "$dir/${r.name}"
                        val inner = src.list(at) ?: return@mapNotNull null
                        val full = readText("$at/name.c9s")
                        val name = names.decryptFilename(BaseEncoding.base64Url(), full.removeSuffix(".c9r"), ad)
                        val file = inner.firstOrNull { it.name == "contents.c9r" }
                        if (file != null) Entry(name, false, cleartextSize(file.size), file.modified ?: r.modified, "$at/contents.c9r", null)
                        else if (inner.any { it.name == "dir.c9r" }) Entry(name, true, 0, r.modified, at, readText("$at/dir.c9r"))
                        else null
                    }
                    else -> null
                }
            } catch (_: org.cryptomator.cryptolib.api.AuthenticationFailedException) { null }
        }.also { l -> l.filter { it.isDir && it.dirId != null }.forEach { dirIds[if (p.isEmpty()) it.name else "$p/${it.name}"] = it.dirId!! } }
    }

    /** Eintrag zu einem Pfad (Wurzel: Ordner ohne Datei). */
    fun find(path: String): Entry? {
        val p = norm(path)
        if (p.isEmpty()) return Entry("", true, 0, null, cipherDir(""), "")
        return list(p.substringBeforeLast('/', ""))?.firstOrNull { it.name == p.substringAfterLast('/') }
    }

    /** Inhalt einer Datei, entschlüsselt beim Lesen (Blöcke werden geprüft). */
    fun open(path: String): InputStream {
        val e = find(path)?.takeIf { !it.isDir } ?: throw java.io.FileNotFoundException(path)
        return openCipher(e.cipher)
    }

    fun openCipher(cipher: String): InputStream =
        Channels.newInputStream(DecryptingReadableByteChannel(Channels.newChannel(src.open(cipher)), cryptor, true))

    private fun cleartextSize(cipherSize: Long): Long {
        val body = cipherSize - header.headerSize()
        return if (body <= 0) 0 else content.cleartextSize(body)
    }

    private fun readText(path: String) = src.open(path).use { String(it.readBytes(), Charsets.UTF_8) }

    /** Derselbe Schlüssel über eine andere Quelle (ohne gemerkte Ordner-Kennungen). */
    fun reopen(other: Source) = VaultReader(other, raw)

    override fun close() { cryptor.destroy(); raw.fill(0) }

    companion object {
        /** Masterkey aus masterkey.cryptomator mit dem Tresor-Passwort (scrypt) - für den Tresor ohne Netz. */
        fun keyFromFile(masterkeyFile: InputStream, password: CharSequence): ByteArray {
            val key = MasterkeyFileAccess(ByteArray(0), SecureRandom()).load(masterkeyFile, password)
            // getEncoded gibt das innere Feld heraus, destroy() nullt es - daher kopieren
            try { return key.encoded.copyOf() } finally { key.destroy() }
        }
    }
}
