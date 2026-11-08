package at.dimes.dmoffice.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.LockOpen
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.Message
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable

// OpenPGP (Server Mail/OpenPgp*.cs, docs/openpgp-konzept.md): Signatur beim Lesen, Signieren beim Verfassen, Einstellungen

@Serializable data class PgpSignature(
    val state: String = "", val kind: String = "", val email: String? = null, val name: String? = null, val fingerprint: String? = null,
    val keyId: String? = null, val signed: String? = null, val source: String? = null, val partial: Boolean = false,
    val keyAdded: Boolean = false, val wkdTried: Boolean = false,
)
@Serializable data class PgpSignatureResult(val signature: PgpSignature? = null, val keyAdded: Boolean = false)
@Serializable data class PgpKey(val fingerprint: String = "", val userIds: List<String> = emptyList(), val algorithm: String = "",
    val created: String = "", val expires: String? = null, val matches: Boolean = true)
@Serializable data class PgpAccount(val id: String = "", val name: String = "", val email: String = "", val key: PgpKey? = null,
    val sign: Boolean = false, val attachKey: Boolean = false, val autocrypt: Boolean = false)
@Serializable data class PgpSettings(val wkd: Boolean = true, val accounts: List<PgpAccount> = emptyList())
@Serializable data class PgpKnownKey(val fingerprint: String = "", val emails: List<String> = emptyList(), val userIds: List<String> = emptyList(),
    val source: String = "", val confirmed: Boolean = false, val algorithm: String = "", val revoked: Boolean = false)
@Serializable data class PgpAccountBody(val id: String, val sign: Boolean? = null, val attachKey: Boolean? = null, val autocrypt: Boolean? = null)
@Serializable data class PgpSettingsBody(val wkd: Boolean? = null, val accounts: List<PgpAccountBody>? = null)
@Serializable data class PgpImportBody(val key: String, val passphrase: String? = null)
@Serializable data class PgpImportResult(val state: PgpSettings = PgpSettings(), val warning: String? = null)
@Serializable data class PgpKeyBody(val key: String)

/** Fingerabdruck in Vierergruppen, wie GnuPG ihn zeigt. */
fun fingerprintText(fp: String) = fp.chunked(4).joinToString(" ")

private fun enc(s: String) = Uri.encode(s)

object OpenPgp {
    /** Einstellungen je Sitzung zwischengespeichert (Verfassen fragt nicht jedes Mal); Einstellungen setzen sie neu. */
    @Volatile var cached: PgpSettings? = null

    private fun api(ctx: android.content.Context): ApiClient = App.get(ctx).api ?: throw IllegalStateException("no account")

    suspend fun settings(ctx: android.content.Context, fresh: Boolean = false): PgpSettings = withContext(Dispatchers.IO) {
        if (!fresh) cached?.let { return@withContext it }
        val a = api(ctx)
        a.get<PgpSettings>(a.url("/api/mail/pgp/settings")).also { cached = it }
    }
    suspend fun save(ctx: android.content.Context, body: PgpSettingsBody): PgpSettings = withContext(Dispatchers.IO) {
        val a = api(ctx)
        a.json.decodeFromString<PgpSettings>(a.putText(a.url("/api/mail/pgp/settings"), a.json.encodeToString(PgpSettingsBody.serializer(), body))).also { cached = it }
    }
    suspend fun signature(ctx: android.content.Context, m: Message, lookup: Boolean = false): PgpSignatureResult = withContext(Dispatchers.IO) {
        val a = api(ctx)
        a.get(a.url("/api/mail/${enc(m.account)}/signature", "folder" to m.folder, "uid" to m.uid, "lookup" to (if (lookup) "true" else null)))
    }
    suspend fun confirm(ctx: android.content.Context, fingerprint: String) = withContext(Dispatchers.IO) {
        val a = api(ctx); a.postText(a.url("/api/mail/pgp/keys/$fingerprint/confirm"), null)
    }
    suspend fun generate(ctx: android.content.Context, account: String): PgpSettings = withContext(Dispatchers.IO) {
        val a = api(ctx); a.json.decodeFromString<PgpSettings>(a.postText(a.url("/api/mail/pgp/${enc(account)}/generate"), null)).also { cached = it }
    }
    suspend fun importOwn(ctx: android.content.Context, account: String, key: String, pass: String?): PgpImportResult = withContext(Dispatchers.IO) {
        val a = api(ctx); a.postFor<PgpImportBody, PgpImportResult>(a.url("/api/mail/pgp/${enc(account)}/import"), PgpImportBody(key, pass)).also { cached = it.state }
    }
    suspend fun remove(ctx: android.content.Context, account: String) = withContext(Dispatchers.IO) {
        val a = api(ctx); a.delete(a.url("/api/mail/pgp/${enc(account)}/key")); cached = null
    }
    suspend fun publicKey(ctx: android.content.Context, account: String): String = withContext(Dispatchers.IO) {
        val a = api(ctx); a.getText(a.url("/api/mail/pgp/${enc(account)}/public"))
    }
    suspend fun keys(ctx: android.content.Context): List<PgpKnownKey> = withContext(Dispatchers.IO) {
        val a = api(ctx); a.get(a.url("/api/mail/pgp/keys"))
    }
    suspend fun importPublic(ctx: android.content.Context, key: String) = withContext(Dispatchers.IO) {
        val a = api(ctx); a.post(a.url("/api/mail/pgp/keys"), PgpKeyBody(key))
    }
    suspend fun deleteKey(ctx: android.content.Context, fingerprint: String) = withContext(Dispatchers.IO) {
        val a = api(ctx); a.delete(a.url("/api/mail/pgp/keys/$fingerprint"))
    }
}

/** Karte über dem Inhalt einer signierten Mail (wie der Balken im Web). */
@Composable
fun PgpCard(m: Message) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var result by remember(m.account, m.folder, m.uid) { mutableStateOf<PgpSignatureResult?>(null) }
    var ask by remember { mutableStateOf(false) }
    LaunchedEffect(m.account, m.folder, m.uid) { result = try { OpenPgp.signature(ctx, m) } catch (_: Exception) { null } }
    val r = result ?: return
    val sig = r.signature
    if (sig == null) {
        if (r.keyAdded) Text(stringResource(R.string.pgp_key_saved), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(vertical = 4.dp))
        return
    }
    val who = sig.name?.takeIf { it.isNotBlank() }?.let { "$it <${sig.email}>" } ?: sig.email ?: ""
    val (level, text) = when (sig.state) {
        "valid" -> 0 to stringResource(R.string.pgp_valid, who)
        "unconfirmed" -> 1 to stringResource(R.string.pgp_unconfirmed, who)
        "mismatch" -> 2 to stringResource(R.string.pgp_mismatch, who, m.from?.address ?: "")
        "bad" -> 3 to stringResource(R.string.pgp_bad)
        "expired" -> 2 to stringResource(R.string.pgp_expired, who)
        "revoked" -> 3 to stringResource(R.string.pgp_revoked, who)
        "unknown" -> 1 to stringResource(if (sig.wkdTried) R.string.pgp_unknown_wkd else R.string.pgp_unknown, sig.keyId ?: "?")
        else -> 2 to stringResource(R.string.pgp_error)
    }
    val colors = MaterialTheme.colorScheme
    val (bg, fg) = when (level) {
        0 -> colors.primaryContainer to colors.onPrimaryContainer
        1 -> colors.secondaryContainer to colors.onSecondaryContainer
        2 -> colors.tertiaryContainer to colors.onTertiaryContainer
        else -> colors.errorContainer to colors.onErrorContainer
    }
    Card(Modifier.fillMaxWidth().padding(vertical = 6.dp), colors = CardDefaults.cardColors(containerColor = bg, contentColor = fg)) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(if (level == 0) Icons.Outlined.Lock else Icons.Outlined.LockOpen, contentDescription = null)
                Text(text + if (sig.partial) " " + stringResource(R.string.pgp_partial) else "", style = MaterialTheme.typography.bodyMedium)
            }
            sig.fingerprint?.let { Text(fingerprintText(it), style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace) }
            if (sig.state == "unconfirmed") TextButton(onClick = { ask = true }) { Text(stringResource(R.string.pgp_confirm)) }
            if (sig.state == "unknown" && !sig.wkdTried) TextButton(onClick = {
                scope.launch { result = try { OpenPgp.signature(ctx, m, lookup = true) } catch (_: Exception) { result } }
            }) { Text(stringResource(R.string.pgp_lookup)) }
        }
    }
    if (ask && sig.fingerprint != null) AlertDialog(onDismissRequest = { ask = false },
        title = { Text(stringResource(R.string.pgp_confirm)) },
        text = { Text(stringResource(R.string.pgp_confirm_text, fingerprintText(sig.fingerprint))) },
        confirmButton = { TextButton(onClick = {
            ask = false
            scope.launch { try { OpenPgp.confirm(ctx, sig.fingerprint); result = OpenPgp.signature(ctx, m) } catch (_: Exception) { } }
        }) { Text(stringResource(R.string.pgp_matches)) } },
        dismissButton = { TextButton(onClick = { ask = false }) { Text(stringResource(R.string.cancel)) } })
}

/** Signieren beim Verfassen: Vorgabe des Kontos, bis man selbst umschaltet; ohne eigenen Schlüssel nicht da. */
class PgpSignState(val available: Boolean, val sign: Boolean, val toggle: () -> Unit)

@Composable
fun rememberPgpSign(account: String): PgpSignState {
    val ctx = LocalContext.current
    var settings by remember { mutableStateOf(OpenPgp.cached) }
    var choice by remember { mutableStateOf<Boolean?>(null) }
    LaunchedEffect(Unit) { settings = try { OpenPgp.settings(ctx) } catch (_: Exception) { settings } }
    val acc = settings?.accounts?.firstOrNull { it.id == account }
    val available = acc?.key != null
    val sign = available && (choice ?: acc!!.sign)
    return PgpSignState(available, sign) { choice = !sign }
}

@Composable
fun PgpSignButton(state: PgpSignState) {
    if (!state.available) return
    IconButton(onClick = state.toggle) {
        Icon(if (state.sign) Icons.Outlined.Lock else Icons.Outlined.LockOpen,
            contentDescription = stringResource(if (state.sign) R.string.pgp_signing_on else R.string.pgp_signing_off),
            tint = if (state.sign) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** Einstellungen → E-Mail → OpenPGP (wie im Web): eigene Schlüssel je Konto, Vorgaben, Schlüssel anderer, WKD. */
@Composable
fun OpenPgpSettingsContent() {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var st by remember { mutableStateOf<PgpSettings?>(null) }
    var keys by remember { mutableStateOf<List<PgpKnownKey>>(emptyList()) }
    var error by remember { mutableStateOf<String?>(null) }
    var importFor by remember { mutableStateOf<String?>(null) }   // Konto-Id oder "" für öffentliche Schlüssel
    var removeFor by remember { mutableStateOf<PgpAccount?>(null) }
    fun run(block: suspend () -> Unit) = scope.launch { try { error = null; block() } catch (e: Exception) { error = e.message } }
    fun reloadKeys() = run { keys = OpenPgp.keys(ctx) }
    LaunchedEffect(Unit) { run { st = OpenPgp.settings(ctx, fresh = true); keys = OpenPgp.keys(ctx) } }
    val s = st ?: return
    val hint = MaterialTheme.typography.bodySmall
    val hintColor = MaterialTheme.colorScheme.onSurfaceVariant

    error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
    SectionHeader(stringResource(R.string.pgp_own_keys))
    Text(stringResource(R.string.pgp_own_hint), style = hint, color = hintColor, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
    for (a in s.accounts) {
        Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(a.email.ifBlank { a.name }, style = MaterialTheme.typography.titleSmall)
            val key = a.key
            if (key == null) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { run { st = OpenPgp.generate(ctx, a.id); reloadKeys() } }) { Text(stringResource(R.string.pgp_generate)) }
                    OutlinedButton(onClick = { importFor = a.id }) { Text(stringResource(R.string.pgp_import)) }
                }
                Text(stringResource(R.string.pgp_generate_hint), style = hint, color = hintColor)
            } else {
                Text(fingerprintText(key.fingerprint), style = hint, fontFamily = FontFamily.Monospace)
                Text(key.algorithm + " · " + key.userIds.joinToString(", "), style = hint, color = hintColor)
                if (!key.matches) Text(stringResource(R.string.pgp_not_matching), style = hint, color = MaterialTheme.colorScheme.error)
                for ((label, value, body) in listOf(
                    Triple(R.string.pgp_sign_default, a.sign) { v: Boolean -> PgpAccountBody(a.id, sign = v) },
                    Triple(R.string.pgp_attach_key, a.attachKey) { v: Boolean -> PgpAccountBody(a.id, attachKey = v) },
                    Triple(R.string.pgp_autocrypt, a.autocrypt) { v: Boolean -> PgpAccountBody(a.id, autocrypt = v) })) {
                    ListItem(headlineContent = { Text(stringResource(label)) },
                        trailingContent = { Switch(checked = value, onCheckedChange = { v -> run { st = OpenPgp.save(ctx, PgpSettingsBody(accounts = listOf(body(v)))) } }) })
                }
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { run {
                        val text = OpenPgp.publicKey(ctx, a.id)
                        ctx.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, text)
                            .putExtra(Intent.EXTRA_SUBJECT, "OpenPGP " + a.email), null))
                    } }) { Text(stringResource(R.string.pgp_export)) }
                    TextButton(onClick = { removeFor = a }) { Text(stringResource(R.string.remove)) }
                }
            }
        }
    }
    Text(stringResource(R.string.pgp_publish_hint), style = hint, color = hintColor, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))

    HorizontalDivider(Modifier.padding(vertical = 12.dp))
    SectionHeader(stringResource(R.string.pgp_others))
    Text(stringResource(R.string.pgp_others_hint), style = hint, color = hintColor, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
    ListItem(headlineContent = { Text(stringResource(R.string.pgp_wkd)) }, supportingContent = { Text(stringResource(R.string.pgp_wkd_hint)) },
        trailingContent = { Switch(checked = s.wkd, onCheckedChange = { v -> run { st = OpenPgp.save(ctx, PgpSettingsBody(wkd = v)) } }) })
    TextButton(onClick = { importFor = "" }, modifier = Modifier.padding(horizontal = 8.dp)) { Text(stringResource(R.string.pgp_import_public)) }
    for (k in keys.filter { it.source != "own" }) {
        var menu by remember(k.fingerprint) { mutableStateOf(false) }
        ListItem(headlineContent = { Text(k.userIds.joinToString(", ").ifBlank { k.emails.joinToString(", ") }) },
            supportingContent = { Text(fingerprintText(k.fingerprint) + "\n" + stringResource(if (k.confirmed) R.string.pgp_confirmed else R.string.pgp_unconfirmed_short) + " · " + k.algorithm,
                fontFamily = FontFamily.Monospace, style = hint) },
            trailingContent = {
                Row {
                    if (!k.confirmed) TextButton(onClick = { menu = true }) { Text(stringResource(R.string.pgp_confirm_short)) }
                    TextButton(onClick = { run { OpenPgp.deleteKey(ctx, k.fingerprint); reloadKeys() } }) { Text(stringResource(R.string.remove)) }
                }
            })
        if (menu) AlertDialog(onDismissRequest = { menu = false },
            title = { Text(stringResource(R.string.pgp_confirm)) },
            text = { Text(stringResource(R.string.pgp_confirm_text, fingerprintText(k.fingerprint))) },
            confirmButton = { TextButton(onClick = { menu = false; run { OpenPgp.confirm(ctx, k.fingerprint); reloadKeys() } }) { Text(stringResource(R.string.pgp_matches)) } },
            dismissButton = { TextButton(onClick = { menu = false }) { Text(stringResource(R.string.cancel)) } })
    }

    removeFor?.let { a -> AlertDialog(onDismissRequest = { removeFor = null },
        title = { Text(stringResource(R.string.pgp_remove_title)) }, text = { Text(stringResource(R.string.pgp_remove_text)) },
        confirmButton = { TextButton(onClick = { removeFor = null; run { OpenPgp.remove(ctx, a.id); st = OpenPgp.settings(ctx, fresh = true); reloadKeys() } }) { Text(stringResource(R.string.remove)) } },
        dismissButton = { TextButton(onClick = { removeFor = null }) { Text(stringResource(R.string.cancel)) } }) }

    importFor?.let { target ->
        var key by remember { mutableStateOf("") }
        var pass by remember { mutableStateOf("") }
        val own = target.isNotEmpty()
        AlertDialog(onDismissRequest = { importFor = null },
            title = { Text(stringResource(if (own) R.string.pgp_import else R.string.pgp_import_public)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (own) Text(stringResource(R.string.pgp_import_own_hint), style = hint)
                    OutlinedTextField(key, { key = it }, label = { Text(stringResource(R.string.pgp_key_text)) }, minLines = 4, maxLines = 8,
                        textStyle = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace), modifier = Modifier.fillMaxWidth())
                    if (own) OutlinedTextField(pass, { pass = it }, label = { Text(stringResource(R.string.pgp_passphrase)) }, singleLine = true,
                        visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth())
                }
            },
            confirmButton = { TextButton(enabled = key.isNotBlank(), onClick = {
                importFor = null
                run {
                    if (own) {
                        val r = OpenPgp.importOwn(ctx, target, key, pass.ifBlank { null })
                        st = r.state
                        r.warning?.let { error = it }
                    } else OpenPgp.importPublic(ctx, key)
                    reloadKeys()
                }
            }) { Text(stringResource(R.string.pgp_import_short)) } },
            dismissButton = { TextButton(onClick = { importFor = null }) { Text(stringResource(R.string.cancel)) } })
    }
}
