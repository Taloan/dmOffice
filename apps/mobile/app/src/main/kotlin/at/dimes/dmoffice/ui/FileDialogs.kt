package at.dimes.dmoffice.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.FileInfo
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.FileLink
import at.dimes.dmoffice.files.FileVersion
import at.dimes.dmoffice.files.parseTime
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.LocalDate
import java.time.ZoneId

internal fun copyText(ctx: Context, text: String) {
    ctx.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("dmOffice", text))
    android.widget.Toast.makeText(ctx, ctx.getString(R.string.files_link_copied), android.widget.Toast.LENGTH_SHORT).show()
}

internal fun shareText(ctx: Context, text: String, title: String) {
    ctx.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, text), title))
}

/** Lesbare Art eines Links. */
@Composable
internal fun linkModeText(mode: String) = stringResource(when (mode) { "upload" -> R.string.files_link_upload; "drop" -> R.string.files_link_drop; else -> R.string.files_link_read })

/**
 * Link erstellen oder aendern: Art (Ordner auch Hochladen/Nur hochladen), Ablaufdatum, Passwort, Download-Grenze
 * (Vorgabe 10 wie im Web, 0 = unbegrenzt). Nach dem Erstellen: Adresse kopieren oder teilen.
 */
@Composable
internal fun LinkDialog(item: FileItem, existing: FileLink? = null, onDismiss: () -> Unit, onDone: () -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).files
    val scope = rememberCoroutineScope()
    val zone = ZoneId.systemDefault()
    var mode by remember { mutableStateOf(existing?.mode ?: "read") }
    var expires by remember { mutableStateOf(existing?.expires?.let { parseTime(it) }?.takeIf { it > 0 }?.let { java.time.Instant.ofEpochMilli(it).atZone(zone).toLocalDate() }) }
    var password by remember { mutableStateOf("") }
    var removePassword by remember { mutableStateOf(false) }
    var maxDl by remember { mutableStateOf((existing?.maxDownloads ?: if (existing == null) 10 else 0).toString()) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var created by remember { mutableStateOf<FileLink?>(null) }

    val made = created
    if (made != null) {
        val url = repo.publicUrl(made)
        AlertDialog(onDismissRequest = onDone, title = { Text(stringResource(R.string.files_link_created)) },
            text = { Column { Text(url, style = MaterialTheme.typography.bodyMedium) } },
            confirmButton = { TextButton(onClick = { shareText(ctx, url, item.name); onDone() }) { Text(stringResource(R.string.files_share)) } },
            dismissButton = { TextButton(onClick = { copyText(ctx, url); onDone() }) { Text(stringResource(R.string.files_link_copy)) } })
        return
    }

    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(stringResource(if (existing == null) R.string.files_link_new else R.string.files_link_edit)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(item.name, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (item.isDir) Picker(stringResource(R.string.files_link_mode), mode, listOf("read", "upload", "drop"), { m ->
                    ctx.getString(when (m) { "upload" -> R.string.files_link_upload; "drop" -> R.string.files_link_drop; else -> R.string.files_link_read })
                }, { mode = it })
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(checked = expires != null, onCheckedChange = { expires = if (it) LocalDate.now().plusDays(7) else null })
                    Text(stringResource(R.string.files_link_expires), modifier = Modifier.weight(1f))
                    expires?.let { d -> DateButton(d, { expires = it }) }
                }
                OutlinedTextField(value = password, onValueChange = { password = it; removePassword = false }, singleLine = true,
                    label = { Text(stringResource(if (existing?.hasPassword == true) R.string.files_link_password_new else R.string.files_link_password)) },
                    visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
                if (existing?.hasPassword == true) Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(checked = removePassword, onCheckedChange = { removePassword = it; if (it) password = "" })
                    Text(stringResource(R.string.files_link_password_remove))
                }
                OutlinedTextField(value = maxDl, onValueChange = { maxDl = it.filter { c -> c.isDigit() }.take(6) }, singleLine = true,
                    label = { Text(stringResource(R.string.files_link_max)) }, supportingText = { Text(stringResource(R.string.files_link_max_hint)) },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number))
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy, onClick = {
                busy = true; error = null
                // Ablauf: Ende des gewaehlten Tages in Ortszeit (wie im Web 23:59:59)
                val exp = expires?.atTime(23, 59, 59)?.atZone(zone)?.toInstant()?.toString()
                val max = maxDl.toIntOrNull() ?: 0
                scope.launch {
                    try {
                        withContext(Dispatchers.IO) {
                            if (existing == null) created = repo.createLink(item.path, if (item.isDir) mode else "read", exp, password.ifEmpty { null }, max)
                            else repo.updateLink(existing, if (item.isDir) mode else "read", exp, if (removePassword) "" else password.ifEmpty { null }, max)
                        }
                        if (existing != null) onDone()
                    } catch (e: Exception) { error = e.message ?: e.toString() } finally { busy = false }
                }
            }) { Text(stringResource(if (existing == null) R.string.files_link_create else R.string.save)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Ein Link in der Liste "Freigegeben": Datei, Art, Ablauf, Abrufe; kopieren, teilen, aendern, loeschen. */
@Composable
internal fun LinkRow(l: FileLink, onEdit: () -> Unit, onDelete: () -> Unit) {
    val ctx = LocalContext.current
    val url = App.get(ctx).files.publicUrl(l)
    val sub = buildList {
        add(linkModeText(l.mode))
        l.expires?.let { parseTime(it) }?.takeIf { it > 0 }?.let { add(stringResource(R.string.files_link_until, Fmt.dateTime(ctx, it, true))) }
        if (l.hasPassword) add(stringResource(R.string.files_link_protected))
        add(if ((l.maxDownloads ?: 0) > 0) stringResource(R.string.files_link_downloads_of, l.downloads, l.maxDownloads ?: 0) else stringResource(R.string.files_link_downloads, l.downloads))
        if (l.expired) add(stringResource(R.string.files_link_expired))
    }.joinToString(" · ")
    ListItem(
        headlineContent = { Text(l.item?.name ?: stringResource(R.string.files_link_in_trash), maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = { Column {
            Text("/" + (l.item?.path ?: ""), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(sub, style = MaterialTheme.typography.bodySmall, color = if (l.expired) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
        } },
        trailingContent = { Row {
            IconButton(onClick = { copyText(ctx, url) }) { Icon(Icons.Default.ContentCopy, contentDescription = stringResource(R.string.files_link_copy)) }
            IconButton(onClick = { shareText(ctx, url, l.item?.name ?: "") }) { Icon(Icons.Default.Share, contentDescription = stringResource(R.string.files_share)) }
            if (l.item != null) IconButton(onClick = onEdit) { Icon(Icons.Default.Edit, contentDescription = stringResource(R.string.files_link_edit)) }
            IconButton(onClick = onDelete) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.delete)) }
        } })
}

/** Fruehere Versionen einer Datei: ansehen (in einer App oeffnen) oder wiederherstellen. */
@Composable
internal fun VersionsDialog(item: FileItem, onDismiss: () -> Unit, onChanged: () -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).files
    val scope = rememberCoroutineScope()
    var list by remember { mutableStateOf<List<FileVersion>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(item.path) {
        try { list = withContext(Dispatchers.IO) { repo.versions(item.path) } } catch (e: Exception) { error = e.message ?: e.toString() }
    }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.files_versions)) },
        text = {
            Column {
                Text(item.name, style = MaterialTheme.typography.bodySmall)
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                val l = list
                when {
                    l == null && error == null -> CircularProgressIndicator(Modifier.padding(16.dp))
                    l != null && l.isEmpty() -> Text(stringResource(R.string.files_versions_none), modifier = Modifier.padding(vertical = 12.dp))
                    l != null -> LazyColumn(Modifier.heightIn(max = 360.dp)) {
                        items(l, key = { it.id }) { v ->
                            Column(Modifier.padding(vertical = 6.dp)) {
                                Text(Fmt.dateTime(ctx, parseTime(v.modified), false), style = MaterialTheme.typography.bodyLarge)
                                Text(sizeText(v.size) + (v.saved?.let { " · " + ctx.getString(R.string.files_versions_saved, Fmt.dateTime(ctx, parseTime(it), false)) } ?: ""), style = MaterialTheme.typography.bodySmall)
                                Row {
                                    TextButton(onClick = { scope.launch {
                                        try {
                                            val f = withContext(Dispatchers.IO) {
                                                val dir = java.io.File(ctx.cacheDir, "files/versions").apply { mkdirs() }
                                                val dot = item.name.lastIndexOf('.').takeIf { it > 0 } ?: item.name.length
                                                java.io.File(dir, item.name.substring(0, dot) + " (" + v.id + ")" + item.name.substring(dot)).also { repo.downloadVersion(item.path, v.id, it) }
                                            }
                                            openLocalFile(ctx, f, item, share = false)
                                        } catch (e: Exception) { error = e.message ?: e.toString() }
                                    } }) { Text(stringResource(R.string.files_open)) }
                                    TextButton(onClick = { scope.launch {
                                        try { withContext(Dispatchers.IO) { repo.restoreVersion(item.path, v.id) }; onChanged(); onDismiss() } catch (e: Exception) { error = e.message ?: e.toString() }
                                    } }) { Text(stringResource(R.string.files_versions_restore)) }
                                }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.close)) } })
}

/** Eigenschaften: Groesse (bei Ordnern samt Inhalt), Anzahl, Daten, Versionen, Links. */
@Composable
internal fun DetailsDialog(item: FileItem, onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    var info by remember { mutableStateOf<FileInfo?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(item.path) { try { info = withContext(Dispatchers.IO) { App.get(ctx).files.info(item.path) } } catch (e: Exception) { error = e.message ?: e.toString() } }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(item.name, maxLines = 2, overflow = TextOverflow.Ellipsis) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                fun row(label: String, value: String) = label to value
                val i = info
                val rows = buildList {
                    add(row(ctx.getString(R.string.files_detail_path), "/" + item.path))
                    add(row(ctx.getString(R.string.files_detail_size), sizeText(i?.size ?: item.size)))
                    if (item.isDir && i != null) add(row(ctx.getString(R.string.files_detail_content), ctx.getString(R.string.files_detail_counts, i.files, i.folders)))
                    if (item.modifiedMs > 0) add(row(ctx.getString(R.string.files_detail_modified), Fmt.dateTime(ctx, item.modifiedMs, false)))
                    if (!item.isDir && i != null) add(row(ctx.getString(R.string.files_versions), i.versions.size.toString()))
                    if (i != null) add(row(ctx.getString(R.string.files_area_shared), i.links.size.toString()))
                }
                for ((k, v) in rows) Column { Text(k, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant); Text(v, style = MaterialTheme.typography.bodyMedium) }
                if (info == null && error == null) CircularProgressIndicator(Modifier.padding(8.dp))
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.close)) } })
}

@Composable
internal fun ConfirmDialog(title: String, text: String, confirm: String, onDismiss: () -> Unit, onConfirm: () -> Unit) {
    AlertDialog(onDismissRequest = onDismiss, title = { Text(title) }, text = { Text(text) },
        confirmButton = { TextButton(onClick = onConfirm) { Text(confirm, color = MaterialTheme.colorScheme.error) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

