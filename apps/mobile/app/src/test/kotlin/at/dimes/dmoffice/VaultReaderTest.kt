package at.dimes.dmoffice

import at.dimes.dmoffice.files.VaultReader
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.security.MessageDigest
import java.text.Normalizer

/**
 * Tresor auf dem Gerät lesen (Stufe 7b): der mit der Cryptomator-App angelegte Referenztresor aus den Server-Tests,
 * mit cryptolib wie in der App - Namen (lang, Umlaute, NFD, Emoji), Ordnerebenen, leere und große Dateien.
 */
class VaultReaderTest {
    private val root = File("../../../DmOffice.Tests/Fixtures/cryptomator/reference")
    private val expected = File("../../../DmOffice.Tests/Fixtures/cryptomator/reference.txt")

    private class DirSource(val root: File) : VaultReader.Source {
        override fun list(dir: String) = File(root, dir).listFiles()?.map { VaultReader.Raw(it.name, it.isDirectory, if (it.isFile) it.length() else 0) }
        override fun open(path: String) = File(root, path).inputStream()
    }

    private fun nfc(s: String) = Normalizer.normalize(s, Normalizer.Form.NFC)

    @Test fun referenztresor_vollstaendig_lesen() {
        val key = File(root, "masterkey.cryptomator").inputStream().use { VaultReader.keyFromFile(it, "Pässwört ü 1") }
        VaultReader(DirSource(root), key).use { v ->
            val lines = mutableListOf<String>()
            fun walk(dir: String) {
                for (e in v.list(dir)!!) {
                    val p = "$dir/${e.name}"
                    if (e.isDir) { lines += "D\t${nfc(p)}\t0\t-"; walk(p) }
                    else {
                        val bytes = v.open(p).use { it.readBytes() }
                        assertEquals(p, e.size, bytes.size.toLong())
                        val sha = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
                        lines += "F\t${nfc(p)}\t${bytes.size}\t$sha"
                    }
                }
            }
            walk("")
            val want = expected.readLines(Charsets.UTF_8).filter { it.isNotBlank() }.map { nfc(it) }.sorted()
            assertEquals(want, lines.sorted())
            assertNull(v.list("/gibt es nicht"))
        }
    }

    @Test fun falsches_passwort_scheitert() {
        val failed = try { File(root, "masterkey.cryptomator").inputStream().use { VaultReader.keyFromFile(it, "falsch") }; false } catch (_: Exception) { true }
        assertTrue(failed)
    }
}
