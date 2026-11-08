package at.dimes.dmoffice.ui

import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.Uri
import android.provider.DocumentsContract
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import android.widget.Toast
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.LockOpen
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.security.KeyStore
import java.text.DateFormat
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import kotlin.coroutines.resume

/** Grün für „offen“ - überall gleich (Web und App). */
val TresorOpenColor = Color(0xFF13A10E)

// ---------------- Daten vom Server ----------------

@Serializable data class TresorSettingsDto(val autoClose: String = "never", val closeOnAdminReset: Boolean = true, val closeOnScreenLock: Boolean = false)
@Serializable data class TresorDevice(val id: String, val name: String, val at: String, val used: String, val app: Boolean = false, val mine: Boolean = false)
@Serializable data class TresorRemembered(val id: String, val kind: String, val name: String? = null, val created: String)
@Serializable data class TresorState(val exists: Boolean = false, val user: String = "", val open: Boolean = false, val openAnywhere: Boolean = false,
    val settings: TresorSettingsDto = TresorSettingsDto(), val codesLeft: Int = 0, val size: Long = 0, val pending: Int = 0,
    val devices: List<TresorDevice> = emptyList(), val remembered: List<TresorRemembered> = emptyList())
@Serializable private data class TresorOpened(val codesLeft: Int = 0, val usedCode: Boolean = false, val device: TresorSecret? = null)
@Serializable data class TresorSecret(val id: String, val secret: String)
@Serializable private data class TresorCreated(val recoveryCodes: List<String> = emptyList(), val recoveryKey: String = "")
@Serializable private data class TresorCodes(val recoveryCodes: List<String> = emptyList())
@Serializable private data class TresorKey(val recoveryKey: String = "")

/** Gerätename für die Liste der Geräte mit offenem Tresor. */
private fun deviceName() = (Build.MANUFACTURER.replaceFirstChar { it.uppercase() } + " " + Build.MODEL).trim()

object TresorApi {
    private fun api(ctx: Context): ApiClient = App.get(ctx).api ?: throw IllegalStateException("Kein Konto")
    private val json = Json { ignoreUnknownKeys = true }
    private fun body(vararg pairs: Pair<String, Any?>) = buildJsonObject {
        for ((k, v) in pairs) when (v) { is String -> put(k, v); is Boolean -> put(k, v); is Number -> put(k, v); null -> {} }
    }.toString()

    fun state(ctx: Context): TresorState = api(ctx).let { json.decodeFromString(it.getText(it.url("/api/tresor"))) }
    fun setup(ctx: Context, password: String): Pair<List<String>, String> {
        val a = api(ctx)
        val r = json.decodeFromString<TresorCreated>(a.postText(a.url("/api/tresor/setup"), body("password" to password, "name" to deviceName())))
        return r.recoveryCodes to r.recoveryKey
    }
    /** Öffnen; gibt (Codes übrig, mit Code geöffnet, gemerktes Gerät) zurück. */
    fun open(ctx: Context, password: String? = null, code: String? = null, device: TresorSecret? = null, remember: Boolean = false): Triple<Int, Boolean, TresorSecret?> {
        val a = api(ctx)
        val r = json.decodeFromString<TresorOpened>(a.postText(a.url("/api/tresor/open"), body("password" to password, "code" to code,
            "deviceId" to device?.id, "secret" to device?.secret, "remember" to remember, "name" to deviceName())))
        return Triple(r.codesLeft, r.usedCode, r.device)
    }
    fun close(ctx: Context) { val a = api(ctx); a.postText(a.url("/api/tresor/close"), null); App.get(ctx).files.clearTresorCache() }
    fun closeDevice(ctx: Context, id: String) { val a = api(ctx); a.postText(a.url("/api/tresor/close/$id"), null) }
    fun closeAll(ctx: Context) { val a = api(ctx); a.postText(a.url("/api/tresor/close-all"), null); App.get(ctx).files.clearTresorCache() }

    // „Tresor herunterladen“ (docs/tresor-cryptomator-konzept.md, 9.1): verschlüsselt als Ordner auf dem Gerät
    @Serializable data class DownloadInfo(val available: Boolean = false, val size: Long = 0, val limit: Long = 0)
    @Serializable private data class DownloadToken(val token: String)
    fun downloadInfo(ctx: Context): DownloadInfo = api(ctx).let { json.decodeFromString(it.getText(it.url("/api/tresor/download"))) }
    /** Passwort im Umschlag prüfen, dann das ZIP am Umschlag vorbei (Ticket) lesen und als Ordner „vault“ in [tree] auspacken. */
    fun downloadTo(ctx: Context, password: String, tree: Uri, onProgress: (Long) -> Unit): String {
        val a = api(ctx)
        val token = json.decodeFromString<DownloadToken>(a.postText(a.url("/api/tresor/download/check"), body("password" to password))).token
        val cr = ctx.contentResolver
        val root = DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
        // Freien Namen wählen: vault, vault (2), …
        val taken = buildSet {
            cr.query(DocumentsContract.buildChildDocumentsUriUsingTree(tree, DocumentsContract.getDocumentId(root)), arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)
                ?.use { c -> while (c.moveToNext()) add(c.getString(0)) }
        }
        val name = generateSequence(1) { it + 1 }.map { if (it == 1) "vault" else "vault ($it)" }.first { it !in taken }
        val dirs = HashMap<String, Uri>()
        fun dirOf(path: String): Uri {
            if (path.isEmpty()) return root
            dirs[path]?.let { return it }
            val parent = dirOf(path.substringBeforeLast('/', ""))
            val leaf = path.substringAfterLast('/')
            return (DocumentsContract.createDocument(cr, parent, DocumentsContract.Document.MIME_TYPE_DIR, if (path == "vault") name else leaf)
                ?: throw java.io.IOException(path)).also { dirs[path] = it }
        }
        var done = 0L
        // Als TAR (PAX): gestreamt lesbar; erst die Endmarke zeigt, dass alles da ist
        a.streamDirect(a.url("/api/tresor/download/zip", "token" to token, "format" to "tar")) { input ->
            val tar = TarReader(java.io.BufferedInputStream(input, 256 * 1024))
            while (true) {
                val e = tar.next() ?: break
                val path = e.name.trimEnd('/')
                if (path.isEmpty() || path.split('/').any { it == ".." } || !(path == "vault" || path.startsWith("vault/"))) { tar.skip(e); continue }
                if (e.dir) { dirOf(path); continue }
                val file = DocumentsContract.createDocument(cr, dirOf(path.substringBeforeLast('/', "")), "application/octet-stream", path.substringAfterLast('/'))
                    ?: throw java.io.IOException(path)
                cr.openOutputStream(file)?.use { o -> tar.copy(e, o) { n -> done += n; onProgress(done) } } ?: throw java.io.IOException(path)
            }
        }
        return name
    }
    fun biometric(ctx: Context): TresorSecret { val a = api(ctx); return json.decodeFromString(a.postText(a.url("/api/tresor/biometric"), body("name" to deviceName()))) }
    fun remember(ctx: Context): TresorSecret { val a = api(ctx); return json.decodeFromString(a.postText(a.url("/api/tresor/remember"), body("name" to deviceName()))) }
    fun forget(ctx: Context, id: String) { val a = api(ctx); a.delete(a.url("/api/tresor/remembered/$id")) }
    fun password(ctx: Context, current: String, password: String) { val a = api(ctx); a.postText(a.url("/api/tresor/password"), body("current" to current, "password" to password)) }
    fun codes(ctx: Context): List<String> { val a = api(ctx); return json.decodeFromString<TresorCodes>(a.postText(a.url("/api/tresor/codes"), null)).recoveryCodes }
    fun recoveryKey(ctx: Context): String { val a = api(ctx); return json.decodeFromString<TresorKey>(a.getText(a.url("/api/tresor/recovery-key"))).recoveryKey }
    fun settings(ctx: Context, autoClose: String? = null, closeOnAdminReset: Boolean? = null, closeOnScreenLock: Boolean? = null) {
        val a = api(ctx)
        a.putText(a.url("/api/tresor/settings"), body("autoClose" to autoClose, "closeOnAdminReset" to closeOnAdminReset, "closeOnScreenLock" to closeOnScreenLock))
    }
    fun delete(ctx: Context, password: String) { val a = api(ctx); a.deleteWithBody(a.url("/api/tresor"), body("password" to password)) }

}

// ---------------- Schlüssel auf dem Gerät ----------------

/**
 * Geheimnisse des Tresors auf dem Handy, je Konto: „gemerkt“ (öffnet nach einem Neustart des Servers von selbst, Schlüssel im
 * Android-Schlüsselspeicher ohne Abfrage) und „Fingerabdruck“ (Schlüssel nur nach Fingerabdruck oder Gesicht nutzbar).
 * Auf dem Server liegt nur die Hülle - erst mit dem Geheimnis von hier öffnet sie sich.
 */
object TresorSecrets {
    private fun login(ctx: Context) = App.get(ctx).prefs.account.value?.login ?: ""
    private fun alias(ctx: Context, bio: Boolean) = "dmoffice-tresor-" + (if (bio) "bio-" else "keep-") + login(ctx)
    private fun prefs(ctx: Context) = ctx.getSharedPreferences("tresor", Context.MODE_PRIVATE)
    private fun prefKey(ctx: Context, bio: Boolean) = (if (bio) "bio:" else "keep:") + login(ctx)
    private fun store() = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    fun possibleBiometric(ctx: Context): Boolean =
        BiometricManager.from(ctx).canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG) == BiometricManager.BIOMETRIC_SUCCESS

    fun has(ctx: Context, bio: Boolean): Boolean = prefs(ctx).getString(prefKey(ctx, bio), null) != null
    fun id(ctx: Context, bio: Boolean): String? = prefs(ctx).getString(prefKey(ctx, bio), null)?.substringBefore(':')

    private fun key(ctx: Context, bio: Boolean): SecretKey {
        (store().getKey(alias(ctx, bio), null) as? SecretKey)?.let { return it }
        val spec = KeyGenParameterSpec.Builder(alias(ctx, bio), KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256)
            .apply {
                if (bio) {
                    setUserAuthenticationRequired(true)
                    setInvalidatedByBiometricEnrollment(true)
                    if (Build.VERSION.SDK_INT >= 30) setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)
                    else @Suppress("DEPRECATION") setUserAuthenticationValidityDurationSeconds(-1)
                }
            }.build()
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply { init(spec) }.generateKey()
    }

    /** Verschlüsseln vorbereiten (beim Fingerabdruck erst nach der Abfrage nutzbar). */
    fun encryptCipher(ctx: Context, bio: Boolean): Cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key(ctx, bio)) }

    /** Entschlüsseln vorbereiten; null, wenn nichts gemerkt ist oder der Schlüssel ungültig wurde (neuer Fingerabdruck). */
    fun decryptCipher(ctx: Context, bio: Boolean): Cipher? = try {
        val iv = Base64.decode(prefs(ctx).getString(prefKey(ctx, bio), null)!!.split(':')[1], Base64.NO_WRAP)
        Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(ctx, bio), GCMParameterSpec(128, iv)) }
    } catch (_: Exception) { clear(ctx, bio); null }

    fun save(ctx: Context, bio: Boolean, s: TresorSecret, cipher: Cipher) {
        val ct = cipher.doFinal(s.secret.toByteArray())
        prefs(ctx).edit().putString(prefKey(ctx, bio), s.id + ":" + Base64.encodeToString(cipher.iv, Base64.NO_WRAP) + ":" + Base64.encodeToString(ct, Base64.NO_WRAP)).apply()
    }

    fun read(ctx: Context, bio: Boolean, cipher: Cipher): TresorSecret? = try {
        val parts = prefs(ctx).getString(prefKey(ctx, bio), null)!!.split(':')
        TresorSecret(parts[0], String(cipher.doFinal(Base64.decode(parts[2], Base64.NO_WRAP))))
    } catch (_: Exception) { null }

    fun clear(ctx: Context, bio: Boolean) {
        prefs(ctx).edit().remove(prefKey(ctx, bio)).apply()
        try { store().deleteEntry(alias(ctx, bio)) } catch (_: Exception) { }
    }

    /** „Handy gesperrt“: gemerkt, damit der Empfänger ohne Netz entscheiden kann. */
    var closeOnScreenLock: Boolean
        get() = App.lastContext?.let { prefs(it).getBoolean("screenlock", false) } ?: false
        set(v) { App.lastContext?.let { prefs(it).edit().putBoolean("screenlock", v).apply() } }
}

/** Fingerabdruck oder Gesicht fragen und damit den Schlüssel freigeben. Null: abgebrochen. */
private suspend fun biometricCipher(activity: FragmentActivity, cipher: Cipher, title: String): Cipher? = suspendCancellableCoroutine { cont ->
    val info = BiometricPrompt.PromptInfo.Builder().setTitle(title)
        .setNegativeButtonText(activity.getString(R.string.cancel))
        .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG).build()
    BiometricPrompt(activity, ContextCompat.getMainExecutor(activity), object : BiometricPrompt.AuthenticationCallback() {
        override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) { if (cont.isActive) cont.resume(result.cryptoObject?.cipher) }
        override fun onAuthenticationError(errorCode: Int, errString: CharSequence) { if (cont.isActive) cont.resume(null) }
    }).authenticate(info, BiometricPrompt.CryptoObject(cipher))
}

/** Mit gemerktem Gerät öffnen (ohne Abfrage). true: offen. Passt das Geheimnis nicht mehr, wird es vergessen. */
private suspend fun openKept(ctx: Context, st: TresorState): Boolean {
    if (st.settings.autoClose != "never" || !TresorSecrets.has(ctx, false)) return false
    val cipher = TresorSecrets.decryptCipher(ctx, false) ?: return false
    val s = TresorSecrets.read(ctx, false, cipher) ?: return false
    return try { withContext(Dispatchers.IO) { TresorApi.open(ctx, device = s) }; true }
    catch (e: ApiException) { if (e.code == 400) TresorSecrets.clear(ctx, false); false }
    catch (_: Exception) { false }
}

/** Mit Fingerabdruck öffnen. true: offen. */
private suspend fun openBiometric(activity: FragmentActivity): Boolean {
    val ctx = activity.applicationContext
    if (!TresorSecrets.has(ctx, true) || !TresorSecrets.possibleBiometric(ctx)) return false
    val cipher = TresorSecrets.decryptCipher(ctx, true) ?: return false
    val ready = biometricCipher(activity, cipher, activity.getString(R.string.tresor_open)) ?: return false
    val s = TresorSecrets.read(ctx, true, ready) ?: return false
    return try { withContext(Dispatchers.IO) { TresorApi.open(ctx, device = s) }; true }
    catch (e: ApiException) { if (e.code == 400) TresorSecrets.clear(ctx, true); false }
    catch (_: Exception) { false }
}

/** Fingerabdruck hinterlegen (Tresor muss offen sein). */
private suspend fun setupBiometric(activity: FragmentActivity) {
    val ctx = activity.applicationContext
    val cipher = biometricCipher(activity, TresorSecrets.encryptCipher(ctx, true), activity.getString(R.string.tresor_bio_setup)) ?: return
    val s = withContext(Dispatchers.IO) { TresorApi.biometric(ctx) }
    TresorSecrets.save(ctx, true, s, cipher)
}

// ---------------- Öffnen-Dialog ----------------

/**
 * Öffnen-Dialog des Tresors - aus [TresorHost] für jede Antwort 423 { locked: "tresor" }. Versucht zuerst ein gemerktes Gerät,
 * dann den Fingerabdruck, sonst Tresor-Passwort oder Wiederherstellungscode.
 */
@Composable
fun TresorOpenDialog(done: (Boolean) -> Unit) {
    val ctx = LocalContext.current
    val activity = ctx as? FragmentActivity
    val scope = rememberCoroutineScope()
    var st by remember { mutableStateOf<TresorState?>(null) }
    var password by remember { mutableStateOf("") }
    var useCode by remember { mutableStateOf(false) }
    var keep by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(true) }
    var error by remember { mutableStateOf<String?>(null) }
    var afterCode by remember { mutableStateOf<Int?>(null) }
    val bio = activity != null && TresorSecrets.has(ctx, true) && TresorSecrets.possibleBiometric(ctx)
    LaunchedEffect(Unit) {
        val s = try { withContext(Dispatchers.IO) { TresorApi.state(ctx) } } catch (_: Exception) { null }
        st = s
        when {
            s == null || !s.exists -> done(false)
            s.open -> done(true)
            openKept(ctx, s) -> done(true)
            bio && openBiometric(activity!!) -> done(true)
            else -> busy = false
        }
    }
    val s = st ?: return
    if (!s.exists) return
    afterCode?.let { left -> NewTresorPasswordDialog(left) { done(true) }; return }
    AlertDialog(onDismissRequest = { done(false) },
        icon = { Icon(Icons.Outlined.Lock, contentDescription = null) },
        title = { Text(stringResource(R.string.tresor_open)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(password, { password = it }, singleLine = true,
                    label = { Text(stringResource(if (useCode) R.string.tresor_code else R.string.tresor_password)) },
                    visualTransformation = if (useCode) androidx.compose.ui.text.input.VisualTransformation.None else PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = if (useCode) KeyboardType.Ascii else KeyboardType.Password), modifier = Modifier.fillMaxWidth())
                if (!useCode && s.settings.autoClose == "never") Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(keep, { keep = it })
                    Text(stringResource(R.string.tresor_keep), style = MaterialTheme.typography.bodySmall)
                }
                TextButton(onClick = { useCode = !useCode; password = "" }) { Text(stringResource(if (useCode) R.string.tresor_use_password else R.string.tresor_use_code)) }
                if (bio) OutlinedButton(onClick = { scope.launch { busy = true; if (openBiometric(activity!!)) done(true); busy = false } }, enabled = !busy) {
                    Text(stringResource(R.string.tresor_biometric))
                }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            Button(enabled = password.isNotEmpty() && !busy, onClick = {
                busy = true; error = null
                scope.launch {
                    try {
                        val (left, usedCode, device) = withContext(Dispatchers.IO) {
                            TresorApi.open(ctx, password = if (useCode) null else password, code = if (useCode) password else null, remember = keep && !useCode)
                        }
                        if (device != null) TresorSecrets.save(ctx, false, device, TresorSecrets.encryptCipher(ctx, false))
                        if (usedCode) afterCode = left else done(true)
                    } catch (e: Exception) { error = e.message ?: e.toString() }
                    busy = false
                }
            }) { Text(stringResource(R.string.tresor_open_button)) }
        },
        dismissButton = { TextButton(onClick = { done(false) }) { Text(stringResource(R.string.cancel)) } })
}

/** Nach einem Wiederherstellungscode: gleich ein neues Tresor-Passwort festlegen (der Tresor ist offen, das alte braucht es nicht). */
@Composable
private fun NewTresorPasswordDialog(codesLeft: Int, done: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var pw by remember { mutableStateOf("") }
    var pw2 by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    AlertDialog(onDismissRequest = done,
        title = { Text(stringResource(R.string.tresor_new_password_ask)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.tresor_code_used, codesLeft), style = MaterialTheme.typography.bodySmall)
                PasswordField(pw, { pw = it }, stringResource(R.string.tresor_new_password))
                PasswordField(pw2, { pw2 = it }, stringResource(R.string.tresor_new_password_again))
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            Button(enabled = pw.isNotEmpty(), onClick = {
                if (pw != pw2) { error = ctx.getString(R.string.tresor_mismatch); return@Button }
                scope.launch {
                    try { withContext(Dispatchers.IO) { TresorApi.password(ctx, "", pw) }; done() } catch (e: Exception) { error = e.message ?: e.toString() }
                }
            }) { Text(stringResource(R.string.save)) }
        },
        dismissButton = { TextButton(onClick = done) { Text(stringResource(R.string.tresor_later)) } })
}

@Composable
private fun PasswordField(value: String, onChange: (String) -> Unit, label: String) =
    OutlinedTextField(value, onChange, singleLine = true, label = { Text(label) }, visualTransformation = PasswordVisualTransformation(),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), modifier = Modifier.fillMaxWidth())

/** Öffnen aus der Oberfläche (Eintrag „Tresor“, Einstellungen). */
suspend fun requestTresorOpen(): Boolean = TresorGate.prompt("tresor")

// ---------------- Eintrag in den Dateien ----------------

/** Eintrag „Tresor“ in der Bereichsleiste der Dateien: Schloss zu, geöffnet Schloss offen in Grün. */
@Composable
fun TresorChip(onOpen: () -> Unit) {
    val ctx = LocalContext.current
    val changed by TresorGate.changed.collectAsState()
    val st by produceState<TresorState?>(null, changed) { value = try { withContext(Dispatchers.IO) { TresorApi.state(ctx) } } catch (_: Exception) { null } }
    val open = st?.open == true
    androidx.compose.material3.FilterChip(selected = false, onClick = onOpen, label = { Text(stringResource(R.string.tresor)) },
        leadingIcon = { Icon(if (open) Icons.Outlined.LockOpen else Icons.Outlined.Lock, contentDescription = null, modifier = Modifier.size(18.dp), tint = if (open) TresorOpenColor else androidx.compose.material3.LocalContentColor.current) })
}

// ---------------- Einstellungen ----------------

/** Einstellungen → Tresor (wie im Web): einrichten, öffnen per Fingerabdruck, Passwort, Wiederherstellung, Schließen, Geräte, Belegung, löschen. */
@Composable
fun TresorSettingsScreen() {
    val ctx = LocalContext.current
    val activity = ctx as? FragmentActivity
    val scope = rememberCoroutineScope()
    val changed by TresorGate.changed.collectAsState()
    var tick by remember { mutableIntStateOf(0) }
    var st by remember { mutableStateOf<TresorState?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var shown by remember { mutableStateOf<Pair<List<String>?, String?>?>(null) }
    var askDelete by remember { mutableStateOf(0) }
    LaunchedEffect(changed, tick) { st = try { withContext(Dispatchers.IO) { TresorApi.state(ctx) } } catch (e: Exception) { error = e.message; null } }
    fun run(block: suspend () -> Unit) = scope.launch { try { block(); error = null } catch (e: Exception) { error = e.message ?: e.toString() }; tick++ }
    val s = st ?: return

    Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(stringResource(R.string.tresor_intro), style = MaterialTheme.typography.bodyMedium)
        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        shown?.let { (codes, key) ->
            Text(stringResource(R.string.tresor_print_now), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            RecoveryBlock(codes, key)
            Button(onClick = { shown = null }) { Text(stringResource(R.string.tresor_done)) }
            return@Column
        }
        if (!s.exists) { SetupTresor { codes, key -> shown = codes to key; tick++; TresorGate.changed.value++ }; return@Column }

        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Icon(if (s.open) Icons.Outlined.LockOpen else Icons.Outlined.Lock, contentDescription = null, tint = if (s.open) TresorOpenColor else MaterialTheme.colorScheme.onSurface)
            Text(stringResource(if (s.open) R.string.tresor_is_open else R.string.tresor_is_closed), modifier = Modifier.weight(1f))
            if (s.open) OutlinedButton(onClick = { run { withContext(Dispatchers.IO) { TresorApi.close(ctx) }; TresorGate.changed.value++ } }) { Text(stringResource(R.string.tresor_close)) }
            else Button(onClick = { scope.launch { requestTresorOpen() } }) { Text(stringResource(R.string.tresor_open)) }
        }
        HorizontalDivider()

        // Öffnen mit Fingerabdruck oder Gesicht
        Text(stringResource(R.string.tresor_section_open), style = MaterialTheme.typography.titleSmall)
        val bioMine = TresorSecrets.has(ctx, true) && s.remembered.any { it.id == TresorSecrets.id(ctx, true) }
        SwitchRow(stringResource(R.string.tresor_bio_switch), bioMine, enabled = activity != null && TresorSecrets.possibleBiometric(ctx) && (s.open || bioMine)) { on ->
            run {
                if (on) setupBiometric(activity!!)
                else { TresorSecrets.id(ctx, true)?.let { id -> withContext(Dispatchers.IO) { TresorApi.forget(ctx, id) } }; TresorSecrets.clear(ctx, true) }
            }
        }
        if (s.settings.autoClose == "never") {
            val keptMine = TresorSecrets.has(ctx, false) && s.remembered.any { it.id == TresorSecrets.id(ctx, false) }
            SwitchRow(stringResource(R.string.tresor_keep_switch), keptMine, enabled = s.open || keptMine) { on ->
                run {
                    if (on) { val x = withContext(Dispatchers.IO) { TresorApi.remember(ctx) }; TresorSecrets.save(ctx, false, x, TresorSecrets.encryptCipher(ctx, false)) }
                    else { TresorSecrets.id(ctx, false)?.let { id -> withContext(Dispatchers.IO) { TresorApi.forget(ctx, id) } }; TresorSecrets.clear(ctx, false) }
                }
            }
        }
        HorizontalDivider()

        Text(stringResource(R.string.tresor_change_password), style = MaterialTheme.typography.titleSmall)
        ChangeTresorPassword { cur, new -> run { withContext(Dispatchers.IO) { TresorApi.password(ctx, cur, new) }; Toast.makeText(ctx, ctx.getString(R.string.tresor_password_changed), Toast.LENGTH_SHORT).show() } }
        HorizontalDivider()

        Text(stringResource(R.string.tresor_recovery), style = MaterialTheme.typography.titleSmall)
        Text(stringResource(R.string.tresor_codes_left, s.codesLeft), style = MaterialTheme.typography.bodySmall)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(enabled = s.open, onClick = { run { shown = withContext(Dispatchers.IO) { TresorApi.codes(ctx) } to null } }) { Text(stringResource(R.string.tresor_new_codes)) }
            OutlinedButton(enabled = s.open, onClick = { run { shown = null to withContext(Dispatchers.IO) { TresorApi.recoveryKey(ctx) } } }) { Text(stringResource(R.string.tresor_show_key)) }
        }
        HorizontalDivider()

        Text(stringResource(R.string.tresor_auto_close), style = MaterialTheme.typography.titleSmall)
        for ((value, label) in listOf("1h" to R.string.tresor_auto_1h, "8h" to R.string.tresor_auto_8h, "1d" to R.string.tresor_auto_1d, "never" to R.string.tresor_auto_never))
            Row(verticalAlignment = Alignment.CenterVertically) {
                RadioButton(s.settings.autoClose == value, enabled = s.open, onClick = {
                    run {
                        if (value != "never") TresorSecrets.clear(ctx, false)
                        withContext(Dispatchers.IO) { TresorApi.settings(ctx, autoClose = value) }
                    }
                })
                Text(stringResource(label))
            }
        Text(stringResource(R.string.tresor_close_on_reset), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        SwitchRow(stringResource(R.string.tresor_close_on_screen_lock), s.settings.closeOnScreenLock, enabled = s.open) { on ->
            run { withContext(Dispatchers.IO) { TresorApi.settings(ctx, closeOnScreenLock = on) }; TresorSecrets.closeOnScreenLock = on }
        }
        HorizontalDivider()

        Text(stringResource(R.string.tresor_devices), style = MaterialTheme.typography.titleSmall)
        if (s.devices.isEmpty()) Text(stringResource(R.string.tresor_no_devices), style = MaterialTheme.typography.bodySmall)
        s.devices.forEach { d ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(d.name + if (d.mine) " · " + stringResource(R.string.tresor_this_device) else "")
                    Text(stringResource(R.string.tresor_device_used, isoShort(d.used)), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                TextButton(onClick = { run { withContext(Dispatchers.IO) { if (d.mine) TresorApi.close(ctx) else TresorApi.closeDevice(ctx, d.id) }; TresorGate.changed.value++ } }) { Text(stringResource(R.string.close)) }
            }
        }
        if (s.devices.size > 1) OutlinedButton(onClick = { run { withContext(Dispatchers.IO) { TresorApi.closeAll(ctx) }; TresorGate.changed.value++ } }) { Text(stringResource(R.string.tresor_close_all)) }
        HorizontalDivider()

        Text(stringResource(R.string.tresor_size, android.text.format.Formatter.formatShortFileSize(ctx, s.size)), style = MaterialTheme.typography.bodySmall)
        HorizontalDivider()
        OutlinedButton(onClick = { askDelete = 1 }) { Text(stringResource(R.string.tresor_delete), color = MaterialTheme.colorScheme.error) }
    }

    if (askDelete == 1) AlertDialog(onDismissRequest = { askDelete = 0 }, title = { Text(stringResource(R.string.tresor_delete)) },
        text = { Text(stringResource(R.string.tresor_delete_ask_all)) },
        confirmButton = { Button(onClick = { askDelete = 2 }) { Text(stringResource(R.string.tresor_continue)) } },
        dismissButton = { TextButton(onClick = { askDelete = 0 }) { Text(stringResource(R.string.cancel)) } })
    if (askDelete == 2) {
        var pw by remember { mutableStateOf("") }
        AlertDialog(onDismissRequest = { askDelete = 0 }, title = { Text(stringResource(R.string.tresor_delete_final)) },
            text = { PasswordField(pw, { pw = it }, stringResource(R.string.tresor_confirm_password)) },
            confirmButton = {
                Button(enabled = pw.isNotEmpty(), onClick = {
                    askDelete = 0
                    run { withContext(Dispatchers.IO) { TresorApi.delete(ctx, pw) }; TresorSecrets.clear(ctx, true); TresorSecrets.clear(ctx, false); TresorGate.changed.value++ }
                }) { Text(stringResource(R.string.tresor_delete_final)) }
            },
            dismissButton = { TextButton(onClick = { askDelete = 0 }) { Text(stringResource(R.string.cancel)) } })
    }
}

private fun isoShort(iso: String): String = try {
    DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(java.util.Date.from(java.time.Instant.parse(if (iso.endsWith("Z")) iso else iso + "Z")))
} catch (_: Exception) { iso }

@Composable
private fun SwitchRow(label: String, checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
        Switch(checked, onChange, enabled = enabled)
    }
}

@Composable
private fun SetupTresor(onDone: (List<String>, String) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var pw by remember { mutableStateOf("") }
    var pw2 by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    Text(stringResource(R.string.tresor_setup_warning), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
    PasswordField(pw, { pw = it }, stringResource(R.string.tresor_password))
    PasswordField(pw2, { pw2 = it }, stringResource(R.string.tresor_password_again))
    Text(stringResource(R.string.tresor_password_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    Button(enabled = pw.isNotEmpty() && !busy, onClick = {
        if (pw != pw2) { error = ctx.getString(R.string.tresor_mismatch); return@Button }
        busy = true
        scope.launch {
            try { val (codes, key) = withContext(Dispatchers.IO) { TresorApi.setup(ctx, pw) }; onDone(codes, key) } catch (e: Exception) { error = e.message ?: e.toString() }
            busy = false
        }
    }) { Text(stringResource(R.string.tresor_setup)) }
}

@Composable
private fun ChangeTresorPassword(onSave: (String, String) -> Unit) {
    val ctx = LocalContext.current
    var cur by remember { mutableStateOf("") }
    var pw by remember { mutableStateOf("") }
    var pw2 by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    PasswordField(cur, { cur = it }, stringResource(R.string.tresor_current_password))
    PasswordField(pw, { pw = it }, stringResource(R.string.tresor_new_password))
    PasswordField(pw2, { pw2 = it }, stringResource(R.string.tresor_new_password_again))
    error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    OutlinedButton(enabled = cur.isNotEmpty() && pw.isNotEmpty(), onClick = {
        if (pw != pw2) { error = ctx.getString(R.string.tresor_mismatch); return@OutlinedButton }
        error = null; onSave(cur, pw); cur = ""; pw = ""; pw2 = ""
    }) { Text(stringResource(R.string.tresor_change_password)) }
}

/** Codes und Wiederherstellungsschlüssel: anzeigen, kopieren, teilen (z. B. zum Drucken oder in den Passwortmanager). */
@Composable
private fun RecoveryBlock(codes: List<String>?, key: String?) {
    val ctx = LocalContext.current
    val text = buildString {
        appendLine(ctx.getString(R.string.tresor_sheet_title))
        key?.let { appendLine(); appendLine(ctx.getString(R.string.tresor_sheet_key)); appendLine(it) }
        codes?.let { appendLine(); appendLine(ctx.getString(R.string.tresor_sheet_codes)); it.forEach { c -> appendLine(c) } }
        appendLine(); append(ctx.getString(R.string.tresor_sheet_keep))
    }
    key?.let {
        Text(stringResource(R.string.tresor_recovery_key), style = MaterialTheme.typography.titleSmall)
        Text(it, fontFamily = FontFamily.Monospace, modifier = Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(8.dp)).padding(12.dp))
    }
    codes?.let {
        Text(stringResource(R.string.tresor_recovery_codes), style = MaterialTheme.typography.titleSmall)
        Column(Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(8.dp)).padding(12.dp)) {
            it.forEach { c -> Text(c, fontFamily = FontFamily.Monospace) }
        }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = {
            (ctx.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager).setPrimaryClip(ClipData.newPlainText("dmOffice", text))
        }) { Text(stringResource(R.string.tresor_copy)) }
        OutlinedButton(onClick = {
            ctx.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, text), null))
        }) { Text(stringResource(R.string.tresor_share)) }
    }
}

// ---------------- Handy gesperrt ----------------

/** Schließt den Tresor, wenn das Handy gesperrt wird (Einstellung „Handy gesperrt“, Vorgabe aus). Einmal beim Start der App. */
object TresorScreenLock {
    fun register(app: App) {
        App.lastContext = app
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                if (i.action != Intent.ACTION_SCREEN_OFF || !TresorSecrets.closeOnScreenLock || app.api == null) return
                Thread { try { TresorApi.close(app); TresorGate.changed.value++ } catch (_: Exception) { } }.start()
            }
        }
        ContextCompat.registerReceiver(app, receiver, IntentFilter(Intent.ACTION_SCREEN_OFF), ContextCompat.RECEIVER_NOT_EXPORTED)
    }
}

/** Liest ein TAR (ustar mit PAX-Kopf für lange Namen), wie der Server es für „Tresor herunterladen“ schreibt. */
private class TarReader(private val input: java.io.InputStream) {
    class Entry(val name: String, val size: Long, val dir: Boolean)
    private val block = ByteArray(512)

    private fun readBlock(): Boolean {
        var off = 0
        while (off < 512) { val n = input.read(block, off, 512 - off); if (n < 0) { if (off == 0) return false; throw java.io.EOFException() }; off += n }
        return true
    }
    private fun str(from: Int, len: Int) = String(block, from, len, Charsets.UTF_8).substringBefore('\u0000')
    private fun octal(from: Int, len: Int) = str(from, len).trim().ifEmpty { "0" }.toLong(8)

    fun next(): Entry? {
        var paxName: String? = null
        var paxSize: Long? = null
        while (true) {
            if (!readBlock()) throw java.io.EOFException("TAR ohne Ende")
            if (block.all { it == 0.toByte() }) return null
            val type = block[156].toInt().toChar()
            val size = octal(124, 12)
            if (type == 'x') {
                // PAX-Kopf: Zeilen „<Länge> <Schlüssel>=<Wert>“ und Zeilenende
                val data = ByteArray(size.toInt()).also { readFully(it) }
                skipPadding(size)
                var i = 0
                while (i < data.size) {
                    val sp = data.indexOfFirst(i) { it == ' '.code.toByte() }
                    val len = String(data, i, sp - i).toInt()
                    val kv = String(data, sp + 1, len - (sp - i) - 2, Charsets.UTF_8)
                    when (kv.substringBefore('=')) { "path" -> paxName = kv.substringAfter('='); "size" -> paxSize = kv.substringAfter('=').toLong() }
                    i += len
                }
                continue
            }
            if (type == 'g') { readFully(ByteArray(size.toInt())); skipPadding(size); continue }
            val prefix = str(345, 155)
            val name = paxName ?: if (prefix.isNotEmpty()) prefix + "/" + str(0, 100) else str(0, 100)
            return Entry(name, paxSize ?: size, type == '5')
        }
    }

    fun copy(e: Entry, out: java.io.OutputStream, onBytes: (Int) -> Unit) {
        val buf = ByteArray(64 * 1024)
        var left = e.size
        while (left > 0) {
            val n = input.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
            if (n < 0) throw java.io.EOFException()
            out.write(buf, 0, n); left -= n; onBytes(n)
        }
        skipPadding(e.size)
    }

    fun skip(e: Entry) = copy(e, object : java.io.OutputStream() { override fun write(b: Int) {}; override fun write(b: ByteArray, off: Int, len: Int) {} }) { }

    private fun readFully(b: ByteArray) { var off = 0; while (off < b.size) { val n = input.read(b, off, b.size - off); if (n < 0) throw java.io.EOFException(); off += n } }
    private fun skipPadding(size: Long) { val pad = ((512 - size % 512) % 512).toInt(); if (pad > 0) readFully(ByteArray(pad)) }
}

private inline fun ByteArray.indexOfFirst(from: Int, pred: (Byte) -> Boolean): Int { for (i in from until size) if (pred(this[i])) return i; return -1 }
