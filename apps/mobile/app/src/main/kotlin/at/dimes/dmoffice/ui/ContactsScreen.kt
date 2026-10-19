package at.dimes.dmoffice.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Call
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.outlined.StarBorder
import kotlinx.coroutines.launch
import androidx.compose.material.icons.filled.Email
import androidx.compose.material.icons.filled.Map
import androidx.compose.material.icons.filled.Message
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.Contact
import kotlinx.coroutines.launch

/** Kontaktliste mit Suche, gruppiert nach Anfangsbuchstaben des Nachnamens. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ContactsScreen(onOpen: (String) -> Unit, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var q by remember { mutableStateOf("") }
    val list by remember(q) { app.contacts.search(q) }.collectAsState(initial = emptyList())
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) { loading = true; try { app.contacts.refresh() } catch (e: Exception) { error = e.message } finally { loading = false } }

    Scaffold(topBar = {
        TopAppBar(title = { Text(stringResource(R.string.contacts)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = { IconButton(onClick = { scope.launch { loading = true; try { app.contacts.refresh() } catch (e: Exception) { error = e.message } finally { loading = false } } }) { Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.refresh)) } })
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            OutlinedTextField(value = q, onValueChange = { q = it }, placeholder = { Text(stringResource(R.string.search)) }, singleLine = true, modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp))
            error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp)) }
            if (loading && list.isEmpty()) CircularProgressIndicator(Modifier.padding(24.dp))
            if (list.isEmpty() && !loading) Text(stringResource(R.string.no_contacts), modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                var last = ""
                for (c in list) {
                    val letter = c.sortKey.firstOrNull()?.uppercaseChar()?.toString() ?: "#"
                    if (letter != last) { last = letter; item(key = "L$letter") { SectionHeader(letter, highlight = true) } }
                    item(key = c.id) {
                        ListItem(
                            headlineContent = { Text(c.name, fontWeight = FontWeight.Medium) },
                            supportingContent = { Text(listOfNotNull(c.org.ifBlank { null }, c.phones.firstOrNull()?.value, c.emails.firstOrNull()?.value).joinToString(" · ")) },
                            leadingContent = { Avatar(c, 40.dp) },
                            modifier = Modifier.clickable { onOpen(c.id) }
                        )
                    }
                }
            }
        }
    }
}

@Composable
fun Avatar(c: Contact, size: androidx.compose.ui.unit.Dp) {
    val bmp = remember(c.photo) {
        c.photo?.let { p -> try { val b64 = p.substringAfter("base64,"); val bytes = android.util.Base64.decode(b64, android.util.Base64.DEFAULT); android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size) } catch (_: Exception) { null } }
    }
    Box(Modifier.size(size).clip(CircleShape).background(MaterialTheme.colorScheme.secondaryContainer), contentAlignment = Alignment.Center) {
        if (bmp != null) Image(bmp.asImageBitmap(), contentDescription = null, modifier = Modifier.fillMaxSize())
        else Text(c.name.take(1).uppercase(), color = MaterialTheme.colorScheme.onSecondaryContainer, fontWeight = FontWeight.Bold)
    }
}

/** Kontaktkarte: Anrufen, Nachricht, Mail, Karte direkt ueber die Android-Apps. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ContactScreen(id: String, onClose: () -> Unit, onMail: (String) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    var c by remember { mutableStateOf<Contact?>(null) }
    LaunchedEffect(id) { c = app.contacts.byId(id); if (c == null) onClose() }
    val k = c ?: return
    fun open(i: Intent) { try { ctx.startActivity(i) } catch (_: Exception) { } }

    Scaffold(topBar = {
        TopAppBar(title = { Text(k.name) }, navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                // VIP wie im Web: Benachrichtigungen "nur VIPs", Prio-Posteingang. Foto bleibt unveraendert (null).
                val scope = androidx.compose.runtime.rememberCoroutineScope()
                IconButton(onClick = { scope.launch {
                    try { app.contacts.save(k.copy(vip = !k.vip, photo = null)); c = app.contacts.byId(id) }
                    catch (e: Exception) { android.widget.Toast.makeText(ctx, e.message ?: "", android.widget.Toast.LENGTH_LONG).show() }
                } }) {
                    Icon(if (k.vip) Icons.Filled.Star else Icons.Outlined.StarBorder, contentDescription = stringResource(R.string.vip),
                        tint = if (k.vip) androidx.compose.ui.graphics.Color(0xFFE8A317) else MaterialTheme.colorScheme.onSurfaceVariant)
                }
            })
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Avatar(k, 64.dp)
                Spacer(Modifier.width(16.dp))
                Column {
                    Text(k.name, style = MaterialTheme.typography.headlineSmall)
                    if (k.org.isNotBlank() || k.title.isNotBlank()) Text(listOf(k.title, k.org).filter { it.isNotBlank() }.joinToString(" · "), color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                k.phones.firstOrNull()?.let { p ->
                    FilledTonalIconButton(onClick = { open(Intent(Intent.ACTION_DIAL, Uri.parse("tel:" + p.value))) }) { Icon(Icons.Default.Call, contentDescription = stringResource(R.string.call)) }
                    FilledTonalIconButton(onClick = { open(Intent(Intent.ACTION_SENDTO, Uri.parse("smsto:" + p.value))) }) { Icon(Icons.Default.Message, contentDescription = stringResource(R.string.sms)) }
                }
                k.emails.firstOrNull()?.let { e -> FilledTonalIconButton(onClick = { onMail(e.value) }) { Icon(Icons.Default.Email, contentDescription = stringResource(R.string.email)) } }
                k.addresses.firstOrNull()?.let { a -> FilledTonalIconButton(onClick = { open(Intent(Intent.ACTION_VIEW, Uri.parse("geo:0,0?q=" + Uri.encode(a.text)))) }) { Icon(Icons.Default.Map, contentDescription = stringResource(R.string.map)) } }
            }
            for (p in k.phones) ListItem(headlineContent = { Text(p.value) }, supportingContent = { Text(typeLabel(p.type, stringResource(R.string.phone))) }, modifier = Modifier.clickable { open(Intent(Intent.ACTION_DIAL, Uri.parse("tel:" + p.value))) })
            for (e in k.emails) ListItem(headlineContent = { Text(e.value) }, supportingContent = { Text(typeLabel(e.type, stringResource(R.string.email))) }, modifier = Modifier.clickable { onMail(e.value) })
            for (a in k.addresses) ListItem(headlineContent = { Text(a.text) }, supportingContent = { Text(typeLabel(a.type, stringResource(R.string.address))) }, modifier = Modifier.clickable { open(Intent(Intent.ACTION_VIEW, Uri.parse("geo:0,0?q=" + Uri.encode(a.text)))) })
            for (u in k.urls) ListItem(headlineContent = { Text(u) }, supportingContent = { Text(stringResource(R.string.website)) }, modifier = Modifier.clickable { open(Intent(Intent.ACTION_VIEW, Uri.parse(if (u.startsWith("http")) u else "https://$u"))) })
            k.birthday?.let { ListItem(headlineContent = { Text(it) }, supportingContent = { Text(stringResource(R.string.birthday)) }) }
            if (k.note.isNotBlank()) ListItem(headlineContent = { Text(k.note) }, supportingContent = { Text(stringResource(R.string.notes)) })
        }
    }
}

private fun typeLabel(type: String, fallback: String): String = when (type.lowercase()) {
    "cell", "mobile" -> "Mobil"; "home" -> "Privat"; "work" -> "Arbeit"; "fax" -> "Fax"; "" -> fallback; else -> type
}
