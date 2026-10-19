package at.dimes.dmoffice.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.Account
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.api.E2e
import at.dimes.dmoffice.dav.DavClient
import at.dimes.dmoffice.dav.DavException
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Einrichtung: QR-Code aus dmOffice scannen oder Adresse, Anmeldename und App-Passwort eintippen. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SetupScreen(initial: Account?, onDone: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var url by remember { mutableStateOf(initial?.baseUrl ?: "") }
    var user by remember { mutableStateOf(initial?.login ?: "") }
    var token by remember { mutableStateOf(initial?.token ?: "") }
    var name by remember { mutableStateOf(initial?.displayName ?: "") }
    var manual by remember { mutableStateOf(initial != null) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    fun connect(a0: Account) {
        busy = true; error = null
        scope.launch {
            try {
                val a = withContext(Dispatchers.IO) { E2e.pair(a0) }
                val cals = withContext(Dispatchers.IO) { DavClient(a, app.prefs).listCalendars() }
                if (cals.isEmpty()) { error = ctx.getString(R.string.no_calendars); return@launch }
                app.prefs.setAccount(a)
                at.dimes.dmoffice.sync.Push.register(ctx, force = true)
                onDone()
            } catch (e: Exception) {
                error = ctx.getString(R.string.setup_failed, if (e is DavException && e.code == 401) ctx.getString(R.string.unauthorized) else (e.message ?: e.javaClass.simpleName))
            } finally { busy = false }
        }
    }

    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        val text = result.contents ?: return@rememberLauncherForActivityResult
        val a = try { Account.fromPairUri(android.net.Uri.parse(text)) } catch (_: Exception) { null }
        if (a == null) { error = ctx.getString(R.string.invalid_qr); return@rememberLauncherForActivityResult }
        url = a.baseUrl; user = a.login; token = a.token; name = a.displayName; manual = true
        connect(a)
    }

    // Per Deep-Link (Kamera-App) gekommen: direkt verbinden
    LaunchedEffect(initial) { if (initial != null && !busy) connect(initial) }

    Scaffold(topBar = { TopAppBar(title = { Text(stringResource(R.string.setup_title)) }) }) { pad ->
        Column(
            modifier = Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Icon(Icons.Default.QrCodeScanner, contentDescription = null, modifier = Modifier.size(72.dp), tint = MaterialTheme.colorScheme.primary)
            Text(stringResource(R.string.setup_intro), style = MaterialTheme.typography.bodyMedium)
            Button(
                onClick = {
                    val opts = ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setPrompt(ctx.getString(R.string.scan_prompt)).setBeepEnabled(false).setOrientationLocked(true)
                    scanner.launch(opts)
                },
                enabled = !busy, modifier = Modifier.fillMaxWidth()
            ) { Text(stringResource(R.string.scan_qr)) }
            if (!manual) TextButton(onClick = { manual = true }) { Text(stringResource(R.string.manual_setup)) }
            if (manual) {
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(value = url, onValueChange = { url = it }, label = { Text(stringResource(R.string.server_url)) },
                    placeholder = { Text(stringResource(R.string.server_url_hint)) }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
                OutlinedTextField(value = user, onValueChange = { user = it }, label = { Text(stringResource(R.string.username)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(value = token, onValueChange = { token = it }, label = { Text(stringResource(R.string.app_password)) }, singleLine = true,
                    visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
                OutlinedButton(
                    onClick = { connect(Account(Account.normalizeUrl(url), user.trim(), token.replace(" ", "").trim(), name.ifBlank { user.trim() })) },
                    enabled = !busy && url.isNotBlank() && user.isNotBlank() && token.isNotBlank(), modifier = Modifier.fillMaxWidth()
                ) { Text(stringResource(if (busy) R.string.connecting else R.string.connect)) }
            }
            if (busy) CircularProgressIndicator()
            error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium) }
        }
    }
}
